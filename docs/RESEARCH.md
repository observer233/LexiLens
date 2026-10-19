# GitHub 调研与设计参考

2026-10-20 检索屏幕 OCR、游戏翻译、悬浮字幕、Windows 设置界面等关键词，并阅读以下项目的说明与相关源码。这里记录选取的参考，未把对整个 GitHub 的检索理解为已经审查所有仓库。

| 项目 | 实际阅读的内容 | 本次采用的思路 |
| --- | --- | --- |
| [Translumo](https://github.com/ramjke/Translumo) | [翻译处理流程](https://github.com/ramjke/Translumo/blob/dc7fe322d68ec5d7d2664a0fa044770bdfe0141d/src/Translumo.Processing/TranslationProcessingService.cs)：多 OCR 结果、有效性与缓存、截图/识别异常分类 | 把识别与翻译的状态分开，主引擎异常或空结果时尝试系统 OCR |
| [LunaTranslator](https://github.com/HIllya51/LunaTranslator) | [OCR 参数说明](https://github.com/HIllya51/LunaTranslator/blob/51884b6c88cba8c42e98ce3e43c4f408017082a1/docs/en/ocrparam.md)：图像稳定度、文本相似度、定时与触发方式 | 文本判断容忍微小 OCR 抖动，提供单次触发 |
| [SubLens](https://github.com/bnsware/SubLens) | [扫描循环](https://github.com/bnsware/SubLens/blob/2b3a0fafa13d890e6c18ace3dd6f6d9336757fde/sublens.py)：归一化、相似度、单次/连续和日志面板 | 避免完全相等成为唯一门槛，显示识别与翻译进度 |
| [Game2Text](https://github.com/mathewthe2/Game2Text) | [OCR 入口](https://github.com/mathewthe2/Game2Text/blob/93088879f0fb9f3c7f558ceeb475ce53475d47de/ocr.py)：识别结果与失败返回、记录入口 | 提供明确的识别检查和错误事件；本项目日志采用不含原文/图像的元数据形式 |
| [Pot](https://github.com/pot-app/pot-desktop) | [设置侧栏](https://github.com/pot-app/pot-desktop/blob/594d32ede96acd106b0256deaa8bb440ffcdff40/src/window/Config/components/SideBar/index.jsx)：通用、翻译、识别、热键、服务等分类 | 将翻译、生词、设置、诊断放在固定导航中，减少主流程文案 |
| [WPF UI](https://github.com/lepoco/wpfui) | [设置页](https://github.com/lepoco/wpfui/blob/ffebacd61058170cf63864b7d5aa730cffff848a/src/Wpf.Ui.Gallery/Views/Pages/SettingsPage.xaml)：分组设置、Fluent 控件和层级 | 采用克制的 Windows 浅色布局，使用 .NET 内置 Fluent 主题和命名控件样式 |

实现与界面由本仓库独立编写。参考项目分别使用 Apache-2.0、GPL-3.0 或 MIT，链接保留原始来源和所读版本；本次没有引入这些应用作为依赖。实际 Fluent 控件来自 .NET 的 PresentationFramework.Fluent，随运行时分发，见第三方说明。

Fluent 主题依据：[微软 WPF Fluent 文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net90)、[微软 Button 样式源](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/Themes/PresentationFramework.Fluent/Styles/Button.xaml)。使用原生控件样式保留键盘焦点、禁用和悬停状态。

## 本次取舍

- 首先暴露截图、OCR、文本判断和网络请求的边界，避免用户只能看到一个等待提示。
- 默认保留增强字体识别，但将可能阻塞的原生推理隔离到子进程，设置超时和回退。
- 新增窗口边界选择和单次翻译，让用户用最短路径确认是否能识别。
- 首次引导只保留服务、区域、单次测试三步，平台政策和高级地址收进展开区。
- 保留元数据日志与诊断导出，不自动把游戏内容写入磁盘。

尚未引入窗口后台捕获、自动跟随、多区域和游戏术语表。窗口选择只是保存当前可见区域；相似度过滤也不是通用 OCR 纠错。[当前故障分析](BUG_ANALYSIS.md)
