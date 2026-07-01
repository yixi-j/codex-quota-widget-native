namespace CodexQuotaWidget.Core;

public static class TimeFormatters
{
    public static string Clock(DateTimeOffset? value)
    {
        return value is null ? "--" : value.Value.LocalDateTime.ToString("HH:mm");
    }

    public static string ResetTime(string? resetAt, long? resetsAt = null, DateTimeOffset? now = null)
    {
        var parsed = ParseResetTime(resetAt, resetsAt);
        if (parsed is null)
        {
            return "--";
        }

        var localNow = (now ?? DateTimeOffset.Now).LocalDateTime;
        var localReset = parsed.Value.LocalDateTime;
        if (localReset <= localNow)
        {
            return "--";
        }

        if (localReset.Date == localNow.Date)
        {
            return localReset.ToString("HH:mm");
        }

        if (localReset.Date == localNow.Date.AddDays(1))
        {
            return $"明天 {localReset:HH:mm}";
        }

        if (localReset.Year == localNow.Year)
        {
            return $"{localReset.Month}月{localReset.Day}日";
        }

        return $"{localReset.Year}-{localReset.Month}-{localReset.Day}";
    }

    public static DateTimeOffset? ParseResetTime(string? resetAt, long? resetsAt = null)
    {
        if (!string.IsNullOrWhiteSpace(resetAt) && DateTimeOffset.TryParse(resetAt, out var parsed))
        {
            return parsed;
        }

        if (resetsAt is > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(resetsAt.Value);
        }

        return null;
    }

    public static string? ToIsoFromUnixSeconds(long? value)
    {
        return value is > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value).ToString("O") : null;
    }
}
