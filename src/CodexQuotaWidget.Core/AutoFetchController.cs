namespace CodexQuotaWidget.Core;

public sealed class AutoFetchController(JsonStore store, IRateLimitClient client, AppLogger logger)
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private AutoFetchState _state = BuildInitialState(store.ReadConfig());

    public AutoFetchState State => _state;

    public DateTimeOffset? NextRunAt(WidgetConfig config, DateTimeOffset? now = null)
    {
        if (!config.AutoFetch.Enabled)
        {
            return null;
        }

        var reference = Parse(config.AutoFetch.LastSuccessAt) ?? Parse(config.AutoFetch.LastAttemptAt);
        if (reference is null)
        {
            return now ?? DateTimeOffset.Now;
        }

        return reference.Value.AddMinutes(config.AutoFetch.IntervalMinutes);
    }

    public bool IsInCooldown(WidgetConfig config, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var cooldown = Parse(config.AutoFetch.CooldownUntil);
        return cooldown is not null && cooldown.Value > current;
    }

    public bool ShouldAutoFetch(WidgetConfig config, DateTimeOffset? now = null)
    {
        if (!config.AutoFetch.Enabled || IsInCooldown(config, now))
        {
            return false;
        }

        var next = NextRunAt(config, now);
        return next is not null && next.Value <= (now ?? DateTimeOffset.Now);
    }

    public async Task<FetchResult> TryFetchAsync(bool manual, CancellationToken cancellationToken = default)
    {
        var config = store.ReadConfig();
        var now = DateTimeOffset.Now;

        if (IsInCooldown(config, now))
        {
            var cooldown = Parse(config.AutoFetch.CooldownUntil)!.Value;
            _state = StateFromConfig(config) with
            {
                Status = "暂停",
                Message = "请求过于频繁，已进入冷却",
                CooldownUntil = cooldown.ToString("O"),
                NextRunAt = cooldown.ToString("O"),
                FailureReason = "请求过于频繁，已触发限流"
            };
            return new FetchResult
            {
                Success = false,
                RateLimited = true,
                Stage = "cooldown",
                Message = "限流冷却中"
            };
        }

        if (!manual && !ShouldAutoFetch(config, now))
        {
            _state = StateFromConfig(config) with
            {
                Status = "等待中",
                Message = "未到自动获取时间"
            };
            return new FetchResult
            {
                Success = false,
                Stage = "schedule",
                Message = "未到自动获取时间"
            };
        }

        if (!await _singleFlight.WaitAsync(0, cancellationToken))
        {
            _state = _state with { InFlight = true, Status = "获取中", Message = "已有请求正在进行" };
            return new FetchResult
            {
                Success = false,
                Stage = "single-flight",
                Message = "已有请求正在进行"
            };
        }

        try
        {
            var attemptConfig = config with
            {
                AutoFetch = config.AutoFetch with
                {
                    LastAttemptAt = now.ToString("O")
                }
            };
            store.WriteConfig(attemptConfig);
            _state = StateFromConfig(attemptConfig) with
            {
                Status = "获取中",
                Message = "正在获取",
                InFlight = true
            };
            logger.Info(manual ? "手动获取开始" : "自动获取开始");

            var result = await client.FetchRateLimitsAsync(cancellationToken);
            var finishedAt = DateTimeOffset.Now;
            if (result.Success && result.Usage is not null)
            {
                var usage = result.Usage with
                {
                    Source = "app-server",
                    UpdatedAt = finishedAt.ToString("O")
                };
                store.WriteUsage(usage);
                var successConfig = store.ReadConfig() with
                {
                    AutoFetch = store.ReadConfig().AutoFetch with
                    {
                        LastSuccessAt = finishedAt.ToString("O"),
                        LastFailureAt = "",
                        CooldownUntil = "",
                        Consecutive429 = 0
                    }
                };
                store.WriteConfig(successConfig);
                _state = StateFromConfig(successConfig) with
                {
                    Status = "成功",
                    Message = "获取成功"
                };
                logger.Info("获取成功");
                return result;
            }

            ApplyFailure(result, finishedAt);
            return result;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public void RefreshState()
    {
        _state = StateFromConfig(store.ReadConfig());
    }

    private void ApplyFailure(FetchResult result, DateTimeOffset finishedAt)
    {
        var config = store.ReadConfig();
        if (result.RateLimited)
        {
            var consecutive = config.AutoFetch.Consecutive429 + 1;
            var cooldownMinutes = consecutive switch
            {
                1 => 30,
                2 => 60,
                _ => 120
            };
            var cooldownUntil = finishedAt.AddMinutes(cooldownMinutes).ToString("O");
            var rateConfig = config with
            {
                AutoFetch = config.AutoFetch with
                {
                    LastFailureAt = finishedAt.ToString("O"),
                    CooldownUntil = cooldownUntil,
                    Consecutive429 = consecutive
                }
            };
            store.WriteConfig(rateConfig);
            _state = StateFromConfig(rateConfig) with
            {
                Status = "暂停",
                Message = "请求过于频繁，已进入冷却",
                FailureStage = result.Stage,
                FailureReason = result.Message,
                RequestId = result.RequestId
            };
            logger.Info("429 冷却", new { cooldownMinutes, requestId = result.RequestId });
            return;
        }

        var failureConfig = config with
        {
            AutoFetch = config.AutoFetch with
            {
                LastFailureAt = finishedAt.ToString("O")
            }
        };
        store.WriteConfig(failureConfig);
        _state = StateFromConfig(failureConfig) with
        {
            Status = "失败",
            Message = "未更新显示数据",
            FailureStage = result.Stage,
            FailureReason = result.Message,
            RequestId = result.RequestId
        };
        logger.Info("获取失败", new { stage = result.Stage, reason = result.Message });
    }

    private static AutoFetchState BuildInitialState(WidgetConfig config)
    {
        return StateFromConfig(config);
    }

    private static AutoFetchState StateFromConfig(WidgetConfig config)
    {
        var now = DateTimeOffset.Now;
        var cooldown = Parse(config.AutoFetch.CooldownUntil);
        var nextRun = cooldown is not null && cooldown > now
            ? cooldown
            : (Parse(config.AutoFetch.LastSuccessAt) ?? Parse(config.AutoFetch.LastAttemptAt))?.AddMinutes(config.AutoFetch.IntervalMinutes);
        return new AutoFetchState
        {
            Status = cooldown is not null && cooldown > now ? "暂停" : "等待中",
            Message = config.AutoFetch.Enabled ? $"每 {config.AutoFetch.IntervalMinutes} 分钟自动获取一次" : "自动获取已关闭",
            LastAttemptAt = config.AutoFetch.LastAttemptAt,
            LastSuccessAt = config.AutoFetch.LastSuccessAt,
            LastFailureAt = config.AutoFetch.LastFailureAt,
            CooldownUntil = cooldown is not null && cooldown > now ? cooldown.Value.ToString("O") : "",
            NextRunAt = nextRun?.ToString("O") ?? ""
        };
    }

    private static DateTimeOffset? Parse(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
