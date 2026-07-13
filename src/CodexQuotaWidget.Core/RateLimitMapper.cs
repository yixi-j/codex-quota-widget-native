using System.Text.Json;

namespace CodexQuotaWidget.Core;

public sealed record RateLimitMapResult(
    CodexUsage? Usage,
    int BucketsFound,
    IReadOnlyList<ProbeBucket> ProbeBuckets,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> RawShapeSummary);

public sealed record ProbeBucket(
    string SourcePath,
    string SelectedFor,
    string BucketKind,
    int? WindowDurationMins,
    double? UsedPercent,
    double? RemainingPercent,
    string LimitId,
    string LimitName);

public static class RateLimitMapper
{
    private const int FiveHourMins = 300;
    private const int WeeklyMins = 10080;

    public static RateLimitMapResult Map(JsonElement root, DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.Now).ToString("O");
        var candidates = CollectBuckets(root);
        var normalized = candidates.Select(Normalize).ToList();
        var valid = normalized.Where(x => x.Window is not null).ToList();
        var mainBuckets = SelectMainWindows(valid);
        var mainWindows = mainBuckets
            .Select(x => CompleteResetAt(x.Window!))
            .ToArray();
        var five = mainBuckets.FirstOrDefault(x => x.Target == FiveHourMins) ?? SelectMain(valid, FiveHourMins);
        var weekly = mainBuckets.FirstOrDefault(x => x.Target == WeeklyMins) ?? SelectMain(valid, WeeklyMins);
        var other = valid
            .Where(x => x.Window is not null && x.Window.WindowDurationMins is not FiveHourMins and not WeeklyMins && x.Kind != "modelSpecific")
            .Select(x => CompleteResetAt(x.Window!))
            .ToArray();
        var modelSpecific = valid
            .Where(x => x.Window is not null && x.Kind == "modelSpecific")
            .Select(x => CompleteResetAt(x.Window!) with
            {
                Label = LabelWithModel(x.Window!, x.ModelName)
            })
            .ToArray();
        var diagnostics = normalized.Where(x => !string.IsNullOrWhiteSpace(x.Diagnostic)).Select(x => x.Diagnostic!).ToList();

        foreach (var ignored in valid.Where(x => x.Kind == "modelSpecific"))
        {
            diagnostics.Add($"{ignored.SourcePath}: 模型专项额度未进入主显示");
        }

        CodexUsage? usage = null;
        if (mainWindows.Length > 0 || modelSpecific.Length > 0)
        {
            usage = new CodexUsage
            {
                Source = "app-server",
                UpdatedAt = timestamp,
                FiveHour = five?.Window is null ? null : CompleteResetAt(five.Window),
                Weekly = weekly?.Window is null ? null : CompleteResetAt(weekly.Window),
                MainWindows = mainWindows,
                OtherWindows = other,
                ModelSpecificWindows = modelSpecific,
                Diagnostics = diagnostics
            };
        }

        var probes = normalized.Select(x => new ProbeBucket(
            x.SourcePath,
            ReferenceEquals(x, five) ? "fiveHour" : ReferenceEquals(x, weekly) ? "weekly" : mainBuckets.Any(main => ReferenceEquals(main, x)) ? "main" : "ignored",
            x.Kind,
            x.Target,
            x.UsedPercent,
            x.Window?.RemainingPercent,
            x.LimitId,
            x.LimitName)).ToArray();

        return new RateLimitMapResult(usage, candidates.Count, probes, diagnostics, SummarizeShape(root));
    }

    private static IReadOnlyList<NormalizedBucket> SelectMainWindows(List<NormalizedBucket> buckets)
    {
        return buckets
            .Where(x => x.Window is not null && x.Kind != "modelSpecific")
            .GroupBy(x => x.Target)
            .Select(PreferredBucket)
            .OrderBy(x => x.Target ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .ToArray();
    }

    private static NormalizedBucket? SelectMain(List<NormalizedBucket> buckets, int duration)
    {
        var candidates = buckets.Where(x => x.Target == duration && x.Kind != "modelSpecific").ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return PreferredBucket(candidates);
    }

    private static NormalizedBucket PreferredBucket(IEnumerable<NormalizedBucket> candidates)
    {
        return candidates
            .OrderByDescending(x => x.Kind == "shared" ? 100 : 10)
            .ThenByDescending(x => x.SourcePath.StartsWith("rateLimits.", StringComparison.Ordinal) ? 10 : 0)
            .ThenBy(x => x.Index)
            .First();
    }

    private static UsageWindow CompleteResetAt(UsageWindow window)
    {
        return window with
        {
            ResetAt = string.IsNullOrWhiteSpace(window.ResetAt) ? TimeFormatters.ToIsoFromUnixSeconds(window.ResetsAt) ?? "" : window.ResetAt
        };
    }

    private static string LabelWithModel(UsageWindow window, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || window.Label.StartsWith(modelName, StringComparison.OrdinalIgnoreCase))
        {
            return window.Label;
        }
        return $"{modelName} {window.Label}";
    }

    private static List<CandidateBucket> CollectBuckets(JsonElement root)
    {
        var output = new List<CandidateBucket>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return output;
        }

        if (root.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            CollectGroup(output, single, "rateLimits", null);
        }

        if (root.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in byId.EnumerateObject())
            {
                if (group.Value.ValueKind == JsonValueKind.Object)
                {
                    CollectGroup(output, group.Value, $"rateLimitsByLimitId.{group.Name}", group.Name);
                }
            }
        }

        return output;
    }

    private static void CollectGroup(List<CandidateBucket> output, JsonElement group, string sourcePath, string? fallbackLimitId)
    {
        var limitId = StringProperty(group, "limitId") ?? fallbackLimitId ?? "";
        var limitName = StringProperty(group, "limitName") ?? "";
        if (group.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
        {
            output.Add(new CandidateBucket(output.Count, $"{sourcePath}.primary", limitId, limitName, primary));
        }
        if (group.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
        {
            output.Add(new CandidateBucket(output.Count, $"{sourcePath}.secondary", limitId, limitName, secondary));
        }
    }

    private static NormalizedBucket Normalize(CandidateBucket candidate)
    {
        var usedPercent = NumberProperty(candidate.Bucket, "usedPercent");
        var duration = IntProperty(candidate.Bucket, "windowDurationMins");
        var resetsAt = LongProperty(candidate.Bucket, "resetsAt");
        var resetAt = StringProperty(candidate.Bucket, "resetAt") ?? "";
        var metadata = Classify(candidate);
        if (usedPercent is null || usedPercent < 0 || usedPercent > 100)
        {
            return new NormalizedBucket(candidate, metadata.Kind, metadata.ModelName, duration, usedPercent, null, "usedPercent 无效");
        }
        if (duration is null or <= 0)
        {
            return new NormalizedBucket(candidate, metadata.Kind, metadata.ModelName, duration, usedPercent, null, "windowDurationMins 无效");
        }

        var remaining = Math.Round(Math.Clamp(100 - usedPercent.Value, 0, 100), 2);
        var label = LabelForDuration(duration.Value);

        var window = new UsageWindow
        {
            Label = label,
            RemainingPercent = remaining,
            UsedPercent = usedPercent,
            WindowDurationMins = duration.Value,
            ResetAt = resetAt,
            ResetsAt = resetsAt,
            BucketKind = metadata.Kind,
            LimitId = candidate.LimitId,
            LimitName = candidate.LimitName,
            ModelName = metadata.ModelName
        };
        return new NormalizedBucket(candidate, metadata.Kind, metadata.ModelName, duration, usedPercent, window, "");
    }

    private static string LabelForDuration(int durationMins)
    {
        if (durationMins == FiveHourMins)
        {
            return "5小时";
        }
        if (durationMins == WeeklyMins)
        {
            return "本周";
        }
        if (durationMins % WeeklyMins == 0)
        {
            var weeks = durationMins / WeeklyMins;
            return $"{weeks}周";
        }
        if (durationMins % 1440 == 0)
        {
            var days = durationMins / 1440;
            return $"{days}天";
        }
        if (durationMins % 60 == 0)
        {
            var hours = durationMins / 60;
            return $"{hours}小时";
        }
        return $"{durationMins}分钟";
    }

    private static (string Kind, string ModelName) Classify(CandidateBucket candidate)
    {
        var combined = $"{candidate.LimitId} {candidate.LimitName} {candidate.SourcePath}";
        if (ContainsAny(combined, ["spark", "bengalfox", "model", "model-specific", "gpt", "gpt-5", "gpt-5.3", "模型"]))
        {
            var name = string.IsNullOrWhiteSpace(candidate.LimitName) ? "模型专项" : candidate.LimitName;
            return ("modelSpecific", name);
        }
        if (candidate.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
            candidate.SourcePath.StartsWith("rateLimits.", StringComparison.Ordinal))
        {
            return ("shared", "");
        }
        return ("unknown", "");
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> keywords)
    {
        return keywords.Any(keyword => value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SummarizeShape(JsonElement root)
    {
        var lines = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["root: not-object"];
        }
        foreach (var property in root.EnumerateObject().Take(8))
        {
            lines.Add($"{property.Name}: {property.Value.ValueKind}");
        }
        return lines;
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double? NumberProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    }

    private static int? IntProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static long? LongProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }

    private sealed record CandidateBucket(int Index, string SourcePath, string LimitId, string LimitName, JsonElement Bucket);

    private sealed record NormalizedBucket(
        CandidateBucket Candidate,
        string Kind,
        string ModelName,
        int? Target,
        double? UsedPercent,
        UsageWindow? Window,
        string? Diagnostic)
    {
        public int Index => Candidate.Index;
        public string SourcePath => Candidate.SourcePath;
        public string LimitId => Candidate.LimitId;
        public string LimitName => Candidate.LimitName;
    }
}
