using System.Text.Json;

namespace CodexQuotaWidget.Core;

public sealed class JsonStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public void EnsureFiles()
    {
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.Logs);
        if (!File.Exists(paths.ConfigFile))
        {
            WriteConfig(new WidgetConfig());
        }
        if (!File.Exists(paths.UsageFile))
        {
            WriteUsage(new CodexUsage
            {
                Diagnostics = ["暂无真实数据"]
            });
        }
    }

    public WidgetConfig ReadConfig()
    {
        try
        {
            var config = JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(paths.ConfigFile), JsonOptions) ?? new WidgetConfig();
            return NormalizeConfig(config);
        }
        catch
        {
            return new WidgetConfig();
        }
    }

    public void WriteConfig(WidgetConfig config)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(NormalizeConfig(config), JsonOptions));
    }

    public CodexUsage ReadUsage()
    {
        try
        {
            return JsonSerializer.Deserialize<CodexUsage>(File.ReadAllText(paths.UsageFile), JsonOptions) ?? new CodexUsage();
        }
        catch
        {
            return new CodexUsage
            {
                Error = "数据文件格式无效",
                Diagnostics = ["无法解析 usage.json"]
            };
        }
    }

    public void WriteUsage(CodexUsage usage)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.UsageFile, JsonSerializer.Serialize(usage, JsonOptions));
    }

    public static WidgetConfig NormalizeConfig(WidgetConfig config)
    {
        var window = config.Window;
        var autoFetch = config.AutoFetch;
        return config with
        {
            Window = window with
            {
                Width = Clamp(window.Width, 420, 1000, 620),
                Height = Clamp(window.Height, 44, 160, 48),
                Opacity = Clamp(window.Opacity, 0.45, 1.0, 0.78),
                FontSize = Clamp(window.FontSize, 11, 24, 12)
            },
            AutoFetch = autoFetch with
            {
                IntervalMinutes = Math.Max(10, autoFetch.IntervalMinutes)
            }
        };
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }
        return Math.Min(max, Math.Max(min, value));
    }
}
