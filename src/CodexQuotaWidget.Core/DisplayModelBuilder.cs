namespace CodexQuotaWidget.Core;

public static class DisplayModelBuilder
{
    public static DisplayModel Build(CodexUsage usage, WidgetConfig config, AutoFetchState state, DateTimeOffset? now = null)
    {
        var fiveHour = usage.FiveHour;
        var weekly = usage.Weekly;
        var hasShared = fiveHour is not null || weekly is not null;
        var resetLeft = $"重置 {TimeFormatters.ResetTime(fiveHour?.ResetAt, fiveHour?.ResetsAt, now)}";
        var resetRight = TimeFormatters.ResetTime(weekly?.ResetAt, weekly?.ResetsAt, now);
        var refresh = BuildRefreshCells(usage, config, state);

        if (!hasShared)
        {
            return new DisplayModel
            {
                QuotaLeft = state.InFlight ? "正在获取" : FailureOrEmpty(usage, state),
                QuotaRight = "--",
                ResetLeft = "重置 --",
                ResetRight = "--",
                RefreshLeft = refresh.Left,
                RefreshRight = refresh.Right
            };
        }

        var quotaLeft = fiveHour is null ? "5小时 --" : $"5小时 {Math.Round(fiveHour.RemainingPercent)}%";
        var quotaRight = weekly is null ? "本周 --" : $"本周 {Math.Round(weekly.RemainingPercent)}%";
        return new DisplayModel
        {
            QuotaLeft = quotaLeft,
            QuotaRight = quotaRight,
            ResetLeft = resetLeft,
            ResetRight = resetRight,
            RefreshLeft = refresh.Left,
            RefreshRight = refresh.Right,
            IsWarning = (fiveHour?.RemainingPercent < 20) || (weekly?.RemainingPercent < 20)
        };
    }

    public static string BuildDetails(CodexUsage usage, WidgetConfig config, AutoFetchState state)
    {
        var lines = new List<string>
        {
            "自动获取状态：",
            "",
            $"状态：{state.Status}",
            $"频率：每 {config.AutoFetch.IntervalMinutes} 分钟自动获取一次"
        };
        if (!string.IsNullOrWhiteSpace(state.LastAttemptAt))
        {
            lines.Add($"上次尝试：{FormatClock(state.LastAttemptAt)}");
        }
        if (!string.IsNullOrWhiteSpace(state.LastSuccessAt))
        {
            lines.Add($"上次成功：{FormatClock(state.LastSuccessAt)}");
        }
        if (!string.IsNullOrWhiteSpace(state.NextRunAt))
        {
            lines.Add($"下次计划：{FormatClock(state.NextRunAt)}");
        }
        if (!string.IsNullOrWhiteSpace(state.CooldownUntil))
        {
            lines.Add($"下次可试：{FormatClock(state.CooldownUntil)}");
        }
        if (!string.IsNullOrWhiteSpace(state.FailureStage))
        {
            lines.Add($"阶段：{state.FailureStage}");
        }
        if (!string.IsNullOrWhiteSpace(state.FailureReason))
        {
            lines.Add($"原因：{state.FailureReason}");
        }
        if (!string.IsNullOrWhiteSpace(state.RequestId))
        {
            lines.Add($"请求 ID：{state.RequestId}");
        }

        lines.Add("");
        lines.Add("主额度：");
        if (usage.FiveHour is not null)
        {
            lines.Add($"- 5小时：{Math.Round(usage.FiveHour.RemainingPercent)}%，重置 {TimeFormatters.ResetTime(usage.FiveHour.ResetAt, usage.FiveHour.ResetsAt)}");
        }
        if (usage.Weekly is not null)
        {
            lines.Add($"- 本周：{Math.Round(usage.Weekly.RemainingPercent)}%，重置 {TimeFormatters.ResetTime(usage.Weekly.ResetAt, usage.Weekly.ResetsAt)}");
        }
        if (usage.FiveHour is null && usage.Weekly is null)
        {
            lines.Add("- 未获取到共享额度");
        }

        if (usage.ModelSpecificWindows.Count > 0)
        {
            lines.Add("");
            lines.Add("其他额度：");
            foreach (var item in usage.ModelSpecificWindows.Take(8))
            {
                lines.Add($"- {item.Label}：{Math.Round(item.RemainingPercent)}%");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatClock(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed) ? TimeFormatters.Clock(parsed) : "--";
    }

    private static string FailureOrEmpty(CodexUsage usage, AutoFetchState state)
    {
        if (!string.IsNullOrWhiteSpace(usage.Error))
        {
            return usage.Error.Contains("限流", StringComparison.Ordinal) ? "限流冷却中" : "自动获取失败";
        }
        if (state.Status == "暂停")
        {
            return "限流冷却中";
        }
        return "未获取到额度";
    }

    private static (string Left, string Right) BuildRefreshCells(CodexUsage usage, WidgetConfig config, AutoFetchState state)
    {
        if (state.InFlight)
        {
            return ("请稍候", "--");
        }
        if (!string.IsNullOrWhiteSpace(state.CooldownUntil) && DateTimeOffset.TryParse(state.CooldownUntil, out var cooldown))
        {
            return ($"冷却至 {TimeFormatters.Clock(cooldown)}", "--");
        }
        if (!config.AutoFetch.Enabled)
        {
            return ("自动刷新已关闭", "--");
        }
        if (!string.IsNullOrWhiteSpace(usage.UpdatedAt) && DateTimeOffset.TryParse(usage.UpdatedAt, out var refreshed))
        {
            var right = DateTimeOffset.TryParse(state.NextRunAt, out var next) ? $"下次 {TimeFormatters.Clock(next)}" : "下次 --";
            return ($"刷新 {TimeFormatters.Clock(refreshed)}", right);
        }
        if (DateTimeOffset.TryParse(state.NextRunAt, out var nextOnly))
        {
            return ($"下次 {TimeFormatters.Clock(nextOnly)}", "--");
        }
        return ("下次 --", "--");
    }
}
