using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CodexQuotaWidget.Core;
using Forms = System.Windows.Forms;

namespace CodexQuotaWidget.App;

public partial class App : System.Windows.Application
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private AppPaths? _paths;
    private JsonStore? _store;
    private AppLogger? _logger;
    private CodexAppServerClient? _client;
    private AutoFetchController? _autoFetch;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private DispatcherTimer? _uiTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _paths = new AppPaths();
        _store = new JsonStore(_paths);
        _store.EnsureFiles();
        _logger = new AppLogger(_paths);
        _client = new CodexAppServerClient(_logger);
        _autoFetch = new AutoFetchController(_store, _client, _logger);
        _logger.Info("启动");

        if (e.Args.Contains("--fetch-once", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunFetchOnceAsync(e.Args);
            return;
        }

        var config = _store.ReadConfig();
        _window = new MainWindow(config, SaveWindowConfig, _logger);
        RefreshWindow();
        _window.Show();
        CreateTray();

        if (e.Args.Contains("--verify-window", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunWindowVerificationAsync(e.Args);
            return;
        }

        StartTimers();
        if (_autoFetch.ShouldAutoFetch(config))
        {
            _ = FetchAndRefreshAsync(manual: false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("退出");
        _tray?.Dispose();
        _client?.Dispose();
        base.OnExit(e);
    }

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "Codex 额度小窗",
            Icon = LoadTrayIcon(),
            Visible = true
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ToggleWindowVisible();
            }
        };
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        var config = Store.ReadConfig();

        menu.Items.Add(MenuItem(_window?.IsVisible == true ? "隐藏小窗" : "显示小窗", (_, _) => ToggleWindowVisible()));
        menu.Items.Add(MenuItem(_window?.IsEditMode == true ? "锁定位置和大小" : "编辑位置和大小", (_, _) =>
        {
            Window.ToggleEditMode();
            RebuildTrayMenu();
        }));
        menu.Items.Add(MenuItem("吸附到任务栏最左边", (_, _) => SnapToTaskbarLeft()));
        menu.Items.Add(MenuItem("立即获取额度", async (_, _) => await FetchAndRefreshAsync(manual: true)));
        menu.Items.Add(MenuItem("打开 Codex 用量页", (_, _) => OpenUrl("https://chatgpt.com/codex/settings")));
        menu.Items.Add(MenuItem("重新读取数据", (_, _) => RefreshWindow()));
        menu.Items.Add(BuildSettingsMenu(config));
        menu.Items.Add(MenuItem("重置位置和大小", (_, _) => ResetPositionAndSize()));
        menu.Items.Add(MenuItem("退出", (_, _) => Shutdown()));
        return menu;
    }

    private Forms.ToolStripMenuItem BuildSettingsMenu(WidgetConfig config)
    {
        var settings = new Forms.ToolStripMenuItem("设置");
        settings.DropDownItems.Add(CheckItem("自动获取额度", config.AutoFetch.Enabled, (_, _) =>
        {
            SaveConfig(config with { AutoFetch = config.AutoFetch with { Enabled = !config.AutoFetch.Enabled } });
        }));

        var frequency = new Forms.ToolStripMenuItem("自动获取频率");
        foreach (var minutes in new[] { 10, 30, 60 })
        {
            frequency.DropDownItems.Add(CheckItem($"{minutes} 分钟", config.AutoFetch.IntervalMinutes == minutes, (_, _) =>
            {
                SaveConfig(Store.ReadConfig() with { AutoFetch = Store.ReadConfig().AutoFetch with { IntervalMinutes = minutes } });
            }));
        }
        settings.DropDownItems.Add(frequency);

        settings.DropDownItems.Add(CheckItem("窗口置顶", config.Window.AlwaysOnTop, (_, _) =>
        {
            var current = Store.ReadConfig();
            SaveConfig(current with { Window = current.Window with { AlwaysOnTop = !current.Window.AlwaysOnTop } });
        }));

        var opacity = new Forms.ToolStripMenuItem("透明度");
        foreach (var pair in new[] { ("60%", 0.60), ("75%", 0.75), ("85%", 0.85), ("100%", 1.0) })
        {
            opacity.DropDownItems.Add(CheckItem(pair.Item1, Math.Abs(config.Window.Opacity - pair.Item2) < 0.01, (_, _) =>
            {
                var current = Store.ReadConfig();
                SaveConfig(current with { Window = current.Window with { Opacity = pair.Item2 } });
            }));
        }
        settings.DropDownItems.Add(opacity);

        var width = new Forms.ToolStripMenuItem("窗口宽度");
        foreach (var value in new[] { 520.0, 620.0, 720.0, 840.0, 960.0 })
        {
            width.DropDownItems.Add(CheckItem($"{value:0}px", Math.Abs(config.Window.Width - value) < 0.1, (_, _) =>
            {
                var current = Store.ReadConfig();
                SaveConfig(current with { Window = current.Window with { Width = value } });
            }));
        }
        settings.DropDownItems.Add(width);

        var font = new Forms.ToolStripMenuItem("字号");
        foreach (var pair in new[] { ("小", 11.0), ("标准", 12.0), ("大", 14.0) })
        {
            font.DropDownItems.Add(CheckItem(pair.Item1, Math.Abs(config.Window.FontSize - pair.Item2) < 0.01, (_, _) =>
            {
                var current = Store.ReadConfig();
                SaveConfig(current with { Window = current.Window with { FontSize = pair.Item2 } });
            }));
        }
        settings.DropDownItems.Add(font);

        settings.DropDownItems.Add(MenuItem("清空额度数据", (_, _) =>
        {
            Store.WriteUsage(new CodexUsage { Diagnostics = ["用户已清空额度数据"] });
            RefreshWindow();
        }));
        settings.DropDownItems.Add(MenuItem("查看自动获取详情", (_, _) =>
        {
            var text = DisplayModelBuilder.BuildDetails(Store.ReadUsage(), Store.ReadConfig(), AutoFetch.State);
            Forms.MessageBox.Show(text, "自动获取详情", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
        }));
        return settings;
    }

    private void StartTimers()
    {
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _uiTimer.Tick += async (_, _) =>
        {
            RefreshWindow();
            if (AutoFetch.ShouldAutoFetch(Store.ReadConfig()))
            {
                await FetchAndRefreshAsync(manual: false);
            }
        };
        _uiTimer.Start();
    }

    private async Task FetchAndRefreshAsync(bool manual)
    {
        RefreshWindow(fetching: true);
        var result = await AutoFetch.TryFetchAsync(manual);
        RefreshWindow();
        if (manual && !result.Success)
        {
            Forms.MessageBox.Show(result.Message, result.RateLimited ? "限流冷却" : "获取失败", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
        }
    }

    private void RefreshWindow(bool fetching = false)
    {
        var config = Store.ReadConfig();
        AutoFetch.RefreshState();
        var state = fetching ? AutoFetch.State with { InFlight = true, Status = "获取中" } : AutoFetch.State;
        Window.ApplyWindowConfig(config.Window);
        Window.ApplyDisplay(DisplayModelBuilder.Build(Store.ReadUsage(), config, state));
        RebuildTrayMenu();
    }

    private void ToggleWindowVisible()
    {
        if (Window.IsVisible)
        {
            Window.Hide();
        }
        else
        {
            Window.Show();
            Window.EnforceTopmost();
        }
        RebuildTrayMenu();
    }

    private void SnapToTaskbarLeft()
    {
        if (!Window.IsVisible)
        {
            Window.Show();
        }

        var config = Store.ReadConfig();
        if (!config.Window.AlwaysOnTop)
        {
            SaveConfig(config with { Window = config.Window with { AlwaysOnTop = true } });
        }

        Window.SnapToTaskbarLeft();
        Window.EnforceTopmost();
        RebuildTrayMenu();
    }

    private void ResetPositionAndSize()
    {
        var config = Store.ReadConfig();
        var defaults = new WindowConfig();
        SaveConfig(config with
        {
            Window = config.Window with
            {
                X = null,
                Y = null,
                Width = defaults.Width,
                Height = defaults.Height
            }
        });
        Window.Hide();
        Window.Show();
        RefreshWindow();
    }

    private void SaveWindowConfig(WindowConfig windowConfig)
    {
        var config = Store.ReadConfig();
        SaveConfig(config with { Window = windowConfig });
    }

    private void SaveConfig(WidgetConfig config)
    {
        Store.WriteConfig(config);
        Window.ApplyWindowConfig(Store.ReadConfig().Window);
        RefreshWindow();
    }

    private void RebuildTrayMenu()
    {
        if (_tray is null)
        {
            return;
        }

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildTrayMenu();
        old?.Dispose();
    }

    private async Task RunWindowVerificationAsync(string[] args)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.SetEditMode(true);
        var beforeMove = Window.VerifyWindow();
        Window.SnapToTaskbarLeft();
        var afterSnap = Window.VerifyWindow();
        Window.Left += 6;
        Window.Top += 4;
        Window.ResizeTo(beforeMove.Width + 120, beforeMove.Height + 12);
        var afterResize = Window.VerifyWindow();
        Window.SetEditMode(false);
        var afterLock = Window.VerifyWindow();
        Window.Hide();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var afterShow = Window.VerifyWindow();
        var output = ArgValue(args, "--verify-output") ?? Path.Combine(AppContext.BaseDirectory, "verify-native.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            ok = !afterLock.HasCaption &&
                 !afterShow.HasCaption &&
                 afterLock.HasToolWindow &&
                 afterShow.HasToolWindow &&
                 afterLock.HasLayered &&
                 afterShow.HasLayered &&
                 afterLock.HasTransparent &&
                 afterShow.HasTransparent &&
                 !afterLock.ShowInTaskbar &&
                 !afterShow.ShowInTaskbar &&
                 afterLock.Topmost &&
                 afterShow.Topmost &&
                 !afterLock.BodyText.Contains("Codex 额度小窗", StringComparison.Ordinal) &&
                 !afterShow.BodyText.Contains("Codex 额度小窗", StringComparison.Ordinal) &&
                 !afterLock.BodyText.Contains("Codex", StringComparison.Ordinal) &&
                 !afterShow.BodyText.Contains("Codex", StringComparison.Ordinal) &&
                 beforeMove.IsResizeHandleVisible &&
                 afterSnap.IsOnTaskbarLeft &&
                 !afterLock.IsResizeHandleVisible &&
                 afterResize.Width > beforeMove.Width &&
                 afterLock.Width > beforeMove.Width &&
                 afterLock.Height >= beforeMove.Height &&
                 Math.Abs(afterShow.Width - afterLock.Width) < 0.1 &&
                 Math.Abs(afterShow.Height - afterLock.Height) < 0.1,
            beforeMove,
            afterSnap,
            afterResize,
            afterLock,
            afterShow,
            appData = Paths.Root,
            usageFile = Paths.UsageFile,
            configFile = Paths.ConfigFile
        }, _jsonOptions));
        Shutdown();
    }

    private async Task RunFetchOnceAsync(string[] args)
    {
        var output = ArgValue(args, "--verify-output") ?? Path.Combine(AppContext.BaseDirectory, "fetch-once.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = await AutoFetch.TryFetchAsync(manual: true);
        var usage = Store.ReadUsage();
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            result.Success,
            result.RateLimited,
            result.Stage,
            result.Message,
            result.RequestId,
            usage = new
            {
                usage.Source,
                usage.UpdatedAt,
                fiveHour = usage.FiveHour is null ? null : new
                {
                    usage.FiveHour.RemainingPercent,
                    usage.FiveHour.UsedPercent,
                    usage.FiveHour.WindowDurationMins,
                    usage.FiveHour.BucketKind,
                    reset = TimeFormatters.ResetTime(usage.FiveHour.ResetAt, usage.FiveHour.ResetsAt)
                },
                weekly = usage.Weekly is null ? null : new
                {
                    usage.Weekly.RemainingPercent,
                    usage.Weekly.UsedPercent,
                    usage.Weekly.WindowDurationMins,
                    usage.Weekly.BucketKind,
                    reset = TimeFormatters.ResetTime(usage.Weekly.ResetAt, usage.Weekly.ResetsAt)
                },
                modelSpecific = usage.ModelSpecificWindows.Select(x => new
                {
                    x.Label,
                    x.RemainingPercent,
                    x.WindowDurationMins,
                    x.BucketKind
                }).ToArray()
            },
            details = DisplayModelBuilder.BuildDetails(usage, Store.ReadConfig(), AutoFetch.State)
        }, _jsonOptions));
        Shutdown();
    }

    private static Forms.ToolStripMenuItem MenuItem(string text, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    private static Forms.ToolStripMenuItem CheckItem(string text, bool isChecked, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Checked = isChecked
        };
        item.Click += handler;
        return item;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var baseDirIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(baseDirIcon))
        {
            return new System.Drawing.Icon(baseDirIcon);
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            var associated = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (associated is not null)
            {
                return associated;
            }
        }

        return System.Drawing.SystemIcons.Application;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private AppPaths Paths => _paths ?? throw new InvalidOperationException("paths not initialized");
    private JsonStore Store => _store ?? throw new InvalidOperationException("store not initialized");
    private AutoFetchController AutoFetch => _autoFetch ?? throw new InvalidOperationException("auto fetch not initialized");
    private MainWindow Window => _window ?? throw new InvalidOperationException("window not initialized");
}
