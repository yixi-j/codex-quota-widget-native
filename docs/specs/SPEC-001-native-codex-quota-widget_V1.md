# SPEC-001 Native Codex 额度小窗 V1

## 目标

实现一个 Windows 11 优先的原生桌面小挂件，用于显示 Codex 共享额度剩余百分比。新版从零开始实现，不沿用 Electron、Chromium、WebView、Node 常驻进程或前端框架。

## 技术栈

- C# / .NET 8
- WPF
- Windows 原生托盘图标
- Win32 扩展窗口样式实现点击穿透
- 单文件 self-contained portable 发布

## 窗口

- 窗口正文标题只显示 `Codex`。
- `Codex 额度小窗` 只允许用于 exe 名称、托盘 tooltip、README、进程描述和不可见窗口 title。
- 窗口无系统标题栏、无白色标题条、无任务栏按钮、置顶显示。
- 默认点击穿透，不抢焦点。
- 编辑位置模式只关闭点击穿透、显示淡边框和 `编辑` 标记，不创建新窗口，不切换系统 frame。
- 拖动通过 WPF `DragMove()` 实现，锁定后保存位置并恢复穿透。

## 布局

小窗使用三列 Grid：

- 左列右对齐。
- 中间分隔符列固定宽度 18。
- 右列左对齐。
- 三行分隔符分别为 `｜`、`｜`、`·`，纵向对齐。

默认文案：

```text
Codex
5小时 89% ｜ 本周 98%
重置 14:09 ｜ 6月11日
刷新 09:52 · 下次 10:02
```

无数据时不显示假额度：

```text
Codex
未获取到额度 ｜ --
重置 -- ｜ --
下次 -- · --
```

## 数据文件

数据目录：

```text
%APPDATA%\codex-quota-widget-native\
```

文件：

- `config.json`
- `usage.json`
- `logs\app.log`

portable 版本仍把用户数据写入 AppData，避免写入程序目录造成权限问题。

## 额度来源

主数据源为本机 `codex app-server`，通过 stdio JSON-RPC 调用：

```text
account/rateLimits/read
```

禁止行为：

- 不抓网页。
- 不读浏览器 Cookie。
- 不读 `auth.json`。
- 不读 token。
- 不模拟键盘。
- 不读取终端文字作为主数据源。

## 额度解析

`app-server` 返回 `usedPercent`，小窗显示：

```text
remainingPercent = 100 - usedPercent
```

映射：

- `windowDurationMins = 300` -> `5小时`
- `windowDurationMins = 10080` -> `本周`
- `resetsAt` / `resetAt` -> 重置时间

## Bucket 选择

小窗主显示必须选择 Codex 共享额度。模型专项额度可进入“自动获取详情”，但不能进入主显示。

识别为模型专项的关键词：

```text
GPT
GPT-5
GPT-5.3
Spark
Codex-Spark
bengalfox
model
model-specific
模型
```

如果同时存在共享 `87%/64%` 和 Spark `100%/100%`，小窗必须显示共享 `87%/64%`。

## 自动获取

- 默认每 10 分钟获取一次。
- 启动时若距离上次获取不足间隔，不立即请求。
- 手动“立即获取额度”也遵守冷却保护。
- 不允许并发请求。
- 冷却期间不启动 app-server，不调用 `rateLimits/read`。

429 冷却：

- 第一次连续 429：30 分钟。
- 第二次连续 429：60 分钟。
- 第三次及以上：120 分钟。

## 托盘菜单

一级菜单：

- 显示小窗 / 隐藏小窗
- 编辑位置 / 锁定穿透
- 立即获取额度
- 打开 Codex 用量页
- 重新读取数据
- 设置
- 重置位置
- 退出

设置子菜单：

- 自动获取额度
- 自动获取频率：10 分钟、30 分钟、60 分钟
- 窗口置顶
- 透明度：60%、75%、85%、100%
- 字号：小、标准、大
- 清空额度数据
- 查看自动获取详情

主菜单不暴露 debug、raw JSON、diagnostics、token、cookie、app-server 原始日志。

## 日志

日志路径：

```text
%APPDATA%\codex-quota-widget-native\logs\app.log
```

记录启动、退出、编辑模式、保存位置、获取成功/失败、429 冷却、app-server 启动/退出。日志限量轮转，不写 token、cookie、auth、完整原始 JSON 或敏感账号信息。
