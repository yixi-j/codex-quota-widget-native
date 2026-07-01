using System.Text.Json;
using CodexQuotaWidget.Core;

var tests = new (string Name, Action Body)[]
{
    ("usedPercent 转换为 remainingPercent", UsedPercentConvertsToRemaining),
    ("shared 87/64 优先于 Spark 100/100", SharedBucketsWinOverSpark),
    ("只有模型专项时不进入主额度", ModelSpecificOnlyDoesNotBecomeMainQuota),
    ("小窗正文不显示 Codex 标题", OverlayBodyDoesNotShowCodexTitle),
    ("重置时间格式化", ResetTimeFormatting),
    ("429 后进入 30 分钟冷却", RateLimitCreatesCooldown),
    ("冷却期间不请求 app-server", CooldownSkipsClient)
};

var passed = 0;
foreach (var test in tests)
{
    test.Body();
    passed++;
    Console.WriteLine($"PASS {test.Name}");
}

Console.WriteLine($"通过 {passed}/{tests.Length} 项测试");

static void UsedPercentConvertsToRemaining()
{
    using var document = JsonDocument.Parse("""
    {
      "rateLimits": {
        "limitId": "codex",
        "primary": { "usedPercent": 11, "windowDurationMins": 300 },
        "secondary": { "usedPercent": 36, "windowDurationMins": 10080 }
      }
    }
    """);
    var result = RateLimitMapper.Map(document.RootElement);
    Equal(89, result.Usage!.FiveHour!.RemainingPercent);
    Equal(64, result.Usage!.Weekly!.RemainingPercent);
}

static void SharedBucketsWinOverSpark()
{
    using var document = JsonDocument.Parse("""
    {
      "rateLimits": {
        "limitId": "codex",
        "primary": { "usedPercent": 13, "windowDurationMins": 300 },
        "secondary": { "usedPercent": 36, "windowDurationMins": 10080 }
      },
      "rateLimitsByLimitId": {
        "gpt-5.3-codex-spark": {
          "limitId": "gpt-5.3-codex-spark",
          "limitName": "GPT-5.3-Codex-Spark",
          "primary": { "usedPercent": 0, "windowDurationMins": 300 },
          "secondary": { "usedPercent": 0, "windowDurationMins": 10080 }
        }
      }
    }
    """);
    var result = RateLimitMapper.Map(document.RootElement);
    Equal(87, result.Usage!.FiveHour!.RemainingPercent);
    Equal(64, result.Usage!.Weekly!.RemainingPercent);
    Equal(2, result.Usage!.ModelSpecificWindows.Count);
    True(result.ProbeBuckets.Any(x => x.BucketKind == "modelSpecific" && x.SelectedFor == "ignored"));
}

static void ModelSpecificOnlyDoesNotBecomeMainQuota()
{
    using var document = JsonDocument.Parse("""
    {
      "rateLimitsByLimitId": {
        "gpt-5.3-codex-spark": {
          "limitId": "gpt-5.3-codex-spark",
          "limitName": "GPT-5.3-Codex-Spark",
          "primary": { "usedPercent": 0, "windowDurationMins": 300 },
          "secondary": { "usedPercent": 0, "windowDurationMins": 10080 }
        }
      }
    }
    """);
    var result = RateLimitMapper.Map(document.RootElement);
    True(result.Usage is not null);
    True(result.Usage!.FiveHour is null);
    True(result.Usage!.Weekly is null);
    Equal(2, result.Usage!.ModelSpecificWindows.Count);
}

static void OverlayBodyDoesNotShowCodexTitle()
{
    var usage = new CodexUsage
    {
        Source = "app-server",
        UpdatedAt = DateTimeOffset.Now.ToString("O"),
        FiveHour = new UsageWindow { RemainingPercent = 87, WindowDurationMins = 300, ResetAt = DateTimeOffset.Now.AddHours(2).ToString("O") },
        Weekly = new UsageWindow { RemainingPercent = 64, WindowDurationMins = 10080, ResetAt = DateTimeOffset.Now.AddDays(3).ToString("O") }
    };
    var model = DisplayModelBuilder.Build(usage, new WidgetConfig(), new AutoFetchState());
    False(model.BodyText.Contains("Codex 额度小窗", StringComparison.Ordinal));
    False(model.BodyText.Contains("Codex", StringComparison.Ordinal));
    True(model.BodyText.Contains("5小时 87%", StringComparison.Ordinal));
    True(model.BodyText.Contains("本周 64%", StringComparison.Ordinal));
    True(model.BodyText.Contains("｜", StringComparison.Ordinal));
}

static void ResetTimeFormatting()
{
    var now = new DateTimeOffset(2026, 6, 4, 9, 52, 0, TimeSpan.FromHours(8));
    Equal("14:09", TimeFormatters.ResetTime("2026-06-04T14:09:00+08:00", now: now));
    Equal("明天 14:09", TimeFormatters.ResetTime("2026-06-05T14:09:00+08:00", now: now));
    Equal("6月11日", TimeFormatters.ResetTime("2026-06-11T00:00:00+08:00", now: now));
}

static void RateLimitCreatesCooldown()
{
    using var fixture = TestFixture.Create(new FakeClient(new FetchResult
    {
        Success = false,
        RateLimited = true,
        Stage = "account/rateLimits/read",
        Message = "429 Too Many Requests"
    }));
    var controller = new AutoFetchController(fixture.Store, fixture.Client, fixture.Logger);
    var result = controller.TryFetchAsync(manual: true).GetAwaiter().GetResult();
    True(result.RateLimited);
    var config = fixture.Store.ReadConfig();
    Equal(1, config.AutoFetch.Consecutive429);
    True(DateTimeOffset.TryParse(config.AutoFetch.CooldownUntil, out var cooldown));
    True(cooldown > DateTimeOffset.Now.AddMinutes(25));
}

static void CooldownSkipsClient()
{
    using var fixture = TestFixture.Create(new FakeClient(new FetchResult { Success = true, Usage = new CodexUsage() }));
    fixture.Store.WriteConfig(new WidgetConfig
    {
        AutoFetch = new AutoFetchConfig
        {
            CooldownUntil = DateTimeOffset.Now.AddMinutes(20).ToString("O"),
            Consecutive429 = 1
        }
    });
    var controller = new AutoFetchController(fixture.Store, fixture.Client, fixture.Logger);
    var result = controller.TryFetchAsync(manual: true).GetAwaiter().GetResult();
    True(result.RateLimited);
    Equal(0, fixture.Client.Calls);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true");
    }
}

static void False(bool value)
{
    if (value)
    {
        throw new InvalidOperationException("Expected false");
    }
}

internal sealed class FakeClient(FetchResult result) : IRateLimitClient
{
    public int Calls { get; private set; }

    public Task<FetchResult> FetchRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

internal sealed class TestFixture : IDisposable
{
    private TestFixture(string root, FakeClient client)
    {
        Root = root;
        Client = client;
        Paths = new AppPaths(root);
        Store = new JsonStore(Paths);
        Store.EnsureFiles();
        Logger = new AppLogger(Paths);
    }

    public string Root { get; }
    public FakeClient Client { get; }
    public AppPaths Paths { get; }
    public JsonStore Store { get; }
    public AppLogger Logger { get; }

    public static TestFixture Create(FakeClient client)
    {
        return new TestFixture(Path.Combine(Path.GetTempPath(), "codex-quota-widget-native-tests", Guid.NewGuid().ToString("N")), client);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
