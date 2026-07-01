namespace CodexQuotaWidget.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Logs { get; }
    public string ConfigFile { get; }
    public string UsageFile { get; }
    public string AppLog { get; }

    public AppPaths(string? appDataRoot = null)
    {
        var appData = appDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = Path.Combine(appData, "codex-quota-widget-native");
        Logs = Path.Combine(Root, "logs");
        ConfigFile = Path.Combine(Root, "config.json");
        UsageFile = Path.Combine(Root, "usage.json");
        AppLog = Path.Combine(Logs, "app.log");
    }
}

public sealed class AppLogger(AppPaths paths)
{
    private const long MaxBytes = 512 * 1024;

    public void Info(string message, object? details = null)
    {
        Directory.CreateDirectory(paths.Logs);
        RotateIfNeeded();
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now.ToString("O"),
            message,
            details
        });
        File.AppendAllText(paths.AppLog, line + Environment.NewLine);
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(paths.AppLog);
        if (!file.Exists || file.Length <= MaxBytes)
        {
            return;
        }

        var backup = Path.Combine(paths.Logs, "app.log.1");
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }
        File.Move(paths.AppLog, backup);
    }
}
