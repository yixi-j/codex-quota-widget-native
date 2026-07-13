using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexQuotaWidget.Core;

public interface IRateLimitClient
{
    Task<FetchResult> FetchRateLimitsAsync(CancellationToken cancellationToken = default);
}

public sealed class CodexAppServerClient(AppLogger logger) : IRateLimitClient, IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private Process? _process;
    private int _nextId;

    public async Task<FetchResult> FetchRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await StartAsync(cancellationToken);
            var result = await RequestAsync("account/rateLimits/read", new { }, cancellationToken);
            var mapped = RateLimitMapper.Map(result);
            if (mapped.Usage is null || mapped.Usage.MainWindows.Count == 0)
            {
                return new FetchResult
                {
                    Success = false,
                    Usage = mapped.Usage,
                    Stage = "account/rateLimits/read",
                    Message = mapped.Usage is null ? "未返回有效额度窗口" : "未返回共享额度窗口",
                    RawShapeSummary = mapped.RawShapeSummary
                };
            }

            return new FetchResult
            {
                Success = true,
                Usage = mapped.Usage,
                Message = "获取成功",
                RawShapeSummary = mapped.RawShapeSummary
            };
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            return new FetchResult
            {
                Success = false,
                RateLimited = IsRateLimited(message),
                Stage = "account/rateLimits/read",
                Message = SanitizeError(message),
                RequestId = ExtractRequestId(message)
            };
        }
        finally
        {
            DisposeProcess();
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        DisposeProcess();
        _disposeCts.Dispose();
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var codexExe = ResolveCodexExecutable();
        logger.Info("app-server 启动", new { source = CodexPathLabel(codexExe) });
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = codexExe,
                Arguments = "app-server",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        _process.Exited += (_, _) => RejectAll(new InvalidOperationException("app-server exited"));
        _process.Start();
        _ = Task.Run(() => ReadStdoutLoopAsync(_process, _disposeCts.Token));
        _ = Task.Run(() => DrainStderrAsync(_process, _disposeCts.Token));

        await RequestAsync("initialize", new
        {
            clientInfo = new
            {
                name = "codex-quota-widget-native",
                title = "Codex 额度小窗",
                version = "1.0.0"
            },
            capabilities = new { }
        }, cancellationToken);
        Notify("initialized", new { });
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
        {
            throw new InvalidOperationException("app-server not running");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });
        await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var registration = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"{method} timeout"));
            }
        });
        return await tcs.Task;
    }

    private void Notify(string method, object parameters)
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
        _process.StandardInput.WriteLine(json);
        _process.StandardInput.Flush();
    }

    private async Task ReadStdoutLoopAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            HandleMessage(line);
        }
    }

    private static async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
        }
    }

    private void HandleMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                return;
            }
            if (!_pending.TryRemove(id, out var pending))
            {
                return;
            }
            if (root.TryGetProperty("error", out var error))
            {
                pending.TrySetException(new InvalidOperationException(error.GetRawText()));
                return;
            }
            pending.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : root);
        }
        catch
        {
            // app-server 的非 JSON 输出直接忽略，不写入日志。
        }
    }

    private void RejectAll(Exception error)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.TrySetException(error);
            }
        }
    }

    private void DisposeProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            process.Dispose();
            logger.Info("app-server 退出");
        }
        catch (Exception ex)
        {
            logger.Info("app-server 清理失败", new { error = SanitizeError(ex.Message) });
        }
    }

    private static bool IsRateLimited(string value)
    {
        return value.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("exceeded retry limit", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeError(string value)
    {
        return value.Length <= 500 ? value : value[..500];
    }

    private static string ExtractRequestId(string value)
    {
        const string marker = "request";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "" : value[index..Math.Min(value.Length, index + 80)];
    }

    private static string ResolveCodexExecutable()
    {
        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var bin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            candidates.Add(Path.Combine(bin, "codex.exe"));
            if (Directory.Exists(bin))
            {
                candidates.AddRange(Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(part.Trim(), "codex.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }
            if (candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return candidate;
        }

        return "codex";
    }

    private static string CodexPathLabel(string path)
    {
        if (path.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "PATH";
        }
        if (path.Contains(@"\OpenAI\Codex\bin\", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI Codex bin";
        }
        return Path.GetFileName(path);
    }
}
