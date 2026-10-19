# LexiLens

Windows 屏幕翻译工具。选择游戏窗口或字幕区域，在悬浮窗中查看中文译文，点击英文单词收藏到生词本。

[下载 Windows 版](https://github.com/observer233/LexiLens/releases/latest) · [使用指南](docs/USER_GUIDE.md) · [卡住问题分析](docs/BUG_ANALYSIS.md) · [AI 平台配置](docs/AI_PLATFORMS.md) · [English](README.en.md)

![LexiLens v0.5.1 多区域界面（固定练习样例，模拟译文）](docs/images/main.png)

## v0.5.1

- **快速字幕**：默认每 150ms 采样，系统 OCR 优先，首次识别即提交；清晰优先模式保留增强字体识别。扫描间隔包含识别耗时，不再识别完额外等一轮。
- **多个选区**：最多 4 个区域。点击“添加框选”继续添加，可命名、重选、移除；每个区域有独立悬浮框，默认放在选区旁，尽量避开其他译文框，位置与宽度保存到本机。
- **对白回看**：已捕获的短对白继续翻译，保留最近 100 条原文/译文。新字幕不会取消上一句，晚返回的旧结果不会覆盖新译文。
- **并行与限流**：多个区域共享缓存和最多 2 个并行请求。快速模式 API 最小间隔默认 400ms，可按账号限额调整；收到 429 后遵守冷却时间。
- **区域日志**：分步状态、耗时、错误代码带区域编号，仍不记录游戏文字、截图或密钥。

## 开始使用

1. 下载 `LexiLens-v0.5.1-win-x64.zip`，完整解压运行 `LexiLens.exe`。自带 .NET 和 OCR 模型，Windows x64，建议 Windows 11。
2. 首次引导中点击“选择服务”。无密钥可用 MyMemory；已有智谱等额度时选择 AI 平台，粘贴标准 API Key，测试并保存。
3. 点击“选择窗口”，移到游戏上，出现边框后单击；或点击“手动框选”选择任务/字幕区域。Esc 取消。
4. 先点击“翻译一次”，确认原文与译文；再点击“开始连续翻译”。
5. 需要分别翻译任务和对白时，点击“添加框选”。使用下拉框选择当前区域；每个区域分别显示译文。
6. 过场对白可勾选“快速字幕”，从左侧“对白回看”补看已捕获的内容；无译文时查看“诊断与日志”。

只选字幕通常更快；整个窗口会识别更多文字。窗口移动或调整大小后需要重新选择。跨屏窗口当前选取其在单一显示器上的最大可见部分，不自动跟踪窗口。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl+Alt+W | 吸附选择窗口 |
| Ctrl+Alt+R | 重选当前区域 |
| Ctrl+Alt+A | 添加一个框选区域 |
| Ctrl+Alt+F | 翻译一次 |
| Ctrl+Alt+T | 开始 / 停止连续翻译 |
| Ctrl+Alt+H | 显示 / 隐藏悬浮窗，会话继续 |
| Ctrl+Alt+L | 切换鼠标穿透，解锁后可查词 |

## 识别和翻译服务

快速模式优先使用 Windows OCR，空结果尝试增强引擎；特殊字体可取消“快速字幕”，按所选引擎识别。英文/中文增强模式使用内置 PaddleOCR v5 CPU，支持自动、原图、暗底亮字和低对比度处理；其他语言使用系统 OCR。可手动选择 Windows OCR。置信度是模型分数，特殊花体仍可能认错；点击“检查识别”查看选区和原文。[字体测试](docs/OCR.md)

支持 25 个云平台，以及自定义兼容服务和 Ollama。智谱、DeepSeek、豆包、千问、OpenAI、Claude、Gemini、Grok 等均可使用自己的标准 API Key；平台默认全部显示。免费档、赠送额度、付费 API 分别标注。[配置与验证范围](docs/AI_PLATFORMS.md)

OCR 模型已内置；Ollama **翻译模型**需另行下载。云平台需要自己的权限和额度，不附带公共密钥。截图留在本机，识别文字按用户选择发送至翻译服务。

## 对白回看

![对白回看（固定练习样例）](docs/images/replay.png)

## 诊断

![诊断与日志（固定界面样例）](docs/images/diagnostics.png)

服务连接测试成功，只能说明固定测试句可以翻译。实际游戏必须先完成截图和 OCR，才会请求 API。v0.3.0 的严格稳定判断已复现并修复；用户具体设备上的空识别、黑屏或引擎故障需看分步日志确认。[分析记录与错误代码](docs/BUG_ANALYSIS.md)

日志保存在 `%LOCALAPPDATA%\LexiLens\logs`，轮换限制约 4 MB。应用内可导出最近 500 条记录和配置摘要，不包含密钥、服务地址、窗口标题、游戏文字或截图。设置、生词和加密密钥继续保存在 `%LOCALAPPDATA%\LexiLens`。

## 验证与范围

80 项核心测试通过。新增验证首次识别立即提交、短对白保留、旧结果保护、队列满提示、多区并行与缓存去重。桌面诊断验证两个独立区域的原文/译文、悬浮框、区域切换、位置保存及回看。已在桌面练习窗口验证实际截图、独立进程 OCR、MyMemory 翻译、窗口选择、穿透、故障注入和报告导出。未逐个平台使用真实 API Key，也未完成商业游戏、HDR、混合 DPI、长期资源占用的兼容矩阵。窗口捕获受显示模式和系统保护影响；不是逐帧或零延迟翻译。

多框采用手动分区；整窗内按语义自动寻找/聚合同类文本尚未实现。快速模式有识别准确度取舍；未采样到的画面无法补译，云端延迟也无法由本机消除。[速度与队列边界](docs/PERFORMANCE.md)

下载包尚未签名。增强识别需要 VC++ x64 运行库；缺失时可通过设置中的微软入口查看安装说明，有系统 OCR 时会明确提示并切换。

## 开发

Windows、[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

```powershell
dotnet build LexiLens.sln -c Release
dotnet test tests/LexiLens.Core.Tests -c Release
dotnet run --project src/LexiLens.App
./scripts/publish.ps1
```

[GitHub 调研与设计参考](docs/RESEARCH.md) · [验证记录](docs/TESTING.md) · [MIT License](LICENSE) · [第三方组件](docs/THIRD_PARTY_NOTICES.md)
