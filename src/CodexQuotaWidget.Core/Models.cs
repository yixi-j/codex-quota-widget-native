namespace CodexQuotaWidget.Core;

public sealed record WidgetConfig
{
    public WindowConfig Window { get; init; } = new();
    public AutoFetchConfig AutoFetch { get; init; } = new();
}

public sealed record WindowConfig
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double Width { get; init; } = 620;
    public double Height { get; init; } = 48;
    public double Opacity { get; init; } = 0.78;
    public double FontSize { get; init; } = 12;
    public bool ClickThrough { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
}

public sealed record AutoFetchConfig
{
    public bool Enabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 10;
    public string LastAttemptAt { get; init; } = "";
    public string LastSuccessAt { get; init; } = "";
    public string LastFailureAt { get; init; } = "";
    public string CooldownUntil { get; init; } = "";
    public int Consecutive429 { get; init; }
}

public sealed record CodexUsage
{
    public string Source { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public UsageWindow? FiveHour { get; init; }
    public UsageWindow? Weekly { get; init; }
    public IReadOnlyList<UsageWindow> OtherWindows { get; init; } = [];
    public IReadOnlyList<UsageWindow> ModelSpecificWindows { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
    public string Error { get; init; } = "";
}

public sealed record UsageWindow
{
    public string Label { get; init; } = "";
    public double RemainingPercent { get; init; }
    public double? UsedPercent { get; init; }
    public int WindowDurationMins { get; init; }
    public string ResetAt { get; init; } = "";
    public long? ResetsAt { get; init; }
    public string BucketKind { get; init; } = "shared";
    public string LimitId { get; init; } = "";
    public string LimitName { get; init; } = "";
    public string ModelName { get; init; } = "";
}

public sealed record DisplayModel
{
    public string Title { get; init; } = "";
    public string QuotaLeft { get; init; } = "未获取到额度";
    public string QuotaRight { get; init; } = "--";
    public string QuotaSeparator { get; init; } = "｜";
    public string ResetLeft { get; init; } = "重置 --";
    public string ResetRight { get; init; } = "--";
    public string ResetSeparator { get; init; } = "｜";
    public string RefreshLeft { get; init; } = "下次 --";
    public string RefreshRight { get; init; } = "--";
    public string RefreshSeparator { get; init; } = "·";
    public bool IsWarning { get; init; }
    public string BodyText => string.Join("  ", new[]
    {
        Title,
        $"{QuotaLeft} {QuotaSeparator} {QuotaRight}",
        $"{ResetLeft} {ResetSeparator} {ResetRight}",
        $"{RefreshLeft} {RefreshSeparator} {RefreshRight}"
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record AutoFetchState
{
    public string Status { get; init; } = "等待中";
    public string Message { get; init; } = "每 10 分钟自动获取一次";
    public string LastAttemptAt { get; init; } = "";
    public string LastSuccessAt { get; init; } = "";
    public string LastFailureAt { get; init; } = "";
    public string CooldownUntil { get; init; } = "";
    public string NextRunAt { get; init; } = "";
    public string FailureStage { get; init; } = "";
    public string FailureReason { get; init; } = "";
    public string RequestId { get; init; } = "";
    public bool InFlight { get; init; }
}

public sealed record FetchResult
{
    public bool Success { get; init; }
    public bool RateLimited { get; init; }
    public CodexUsage? Usage { get; init; }
    public string Stage { get; init; } = "";
    public string Message { get; init; } = "";
    public string RequestId { get; init; } = "";
    public IReadOnlyList<string> RawShapeSummary { get; init; } = [];
}
