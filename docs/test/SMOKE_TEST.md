# Smoke Test

## 构建验收

```powershell
cd D:\work\tools\codex-quota-widget-native
dotnet build
dotnet run --project tests\CodexQuotaWidget.Tests\CodexQuotaWidget.Tests.csproj
.\scripts\publish-portable.ps1
```

确认生成：

```text
D:\work\tools\codex-quota-widget-native\release\Codex额度小窗-1.0.0-portable.exe
```

## Windows 11 手工验收

1. 双击 `Codex额度小窗-1.0.0-portable.exe`。
2. 确认桌面出现半透明深色小窗。
3. 确认小窗无系统标题栏、无白色标题条、无任务栏按钮。
4. 确认小窗正文标题是 `Codex`，不是 `Codex 额度小窗`。
5. 确认首次无数据时显示 `未获取到额度`，不显示假额度。
6. 右键托盘图标，确认菜单为中文。
7. 点击 `立即获取额度`，确认可获取共享额度或显示中文失败原因。
8. 点击 `编辑位置`，确认出现淡边框和 `编辑` 标记。
9. 拖动小窗，确认没有出现系统标题栏。
10. 点击 `锁定穿透`，确认位置保存并恢复点击穿透。
11. 退出应用，确认没有残留 `codex app-server` 子进程。

## 点击穿透验证

锁定状态下，把小窗放在其他应用按钮上方，点击小窗区域，应命中下面的应用。进入 `编辑位置` 后，点击不再穿透，可拖动小窗。

## 数据文件验证

编辑：

```text
%APPDATA%\codex-quota-widget-native\usage.json
```

写入 shared 测试数据后点击托盘 `重新读取数据`，确认小窗更新。不要把模型专项 100% 写入 `fiveHour` / `weekly` 主字段。

## 自动获取详情验证

打开托盘 `设置` -> `查看自动获取详情`，确认显示：

```text
主额度：
- 5小时：xx%，重置 xx
- 本周：yy%，重置 xx

其他额度：
- GPT-5.3-Codex-Spark 5小时：100%
- GPT-5.3-Codex-Spark 本周：100%
```

## 冷却验证

如果出现 429，应用应进入冷却。冷却期间再次点击 `立即获取额度` 不应启动 app-server，也不应调用 `rateLimits/read`。

## 已知限制

- 不默认抓网页。
- 不读浏览器 Cookie。
- 不读未授权凭据。
- 不调用未公开接口。
- 不读取 `auth.json` 或 token。
- WPF 透明窗和点击穿透依赖 Windows Win32 扩展样式。
- 当前 portable 构建未代码签名，Windows SmartScreen 或安全软件可能提示未知发布者。
- 当前图标是 Windows 内置占位图标，后续可替换。
