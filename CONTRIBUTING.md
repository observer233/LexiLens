# 参与开发

请先阅读 [README](README.md) 的功能边界和 [验证记录](docs/TESTING.md)。

使用 Windows 和 .NET 10 SDK。开始改动前运行：

```powershell
dotnet build LexiLens.sln -c Release
dotnet test tests/LexiLens.Core.Tests -c Release
```

保持截图/OCR 的平台代码位于 `LexiLens.App`，将可以独立测试的文本、翻译、并发和存储逻辑放到 `LexiLens.Core`。翻译服务通过 `ITranslator` 接口接入，截图来源通过 `ITextSource` 接入。

涉及字幕管线时，请验证取消、空白画面、重复文字和旧请求晚返回的情况。涉及 UI 时，请在可交互 Windows 桌面检查布局、键盘操作、穿透解锁、退出流程和相应缩放比例。

请勿提交 API key、个人生词、游戏截图或本机设置。Issue 和 PR 中使用自编英文测试句即可。
