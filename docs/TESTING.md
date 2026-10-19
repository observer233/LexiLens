# 验证记录与后续测试

v0.1.0 的记录日期：2026-10-19。

## 已执行

- Windows 上使用 .NET 10 SDK 编译整个解决方案，零编译错误、零警告。
- 15 项核心测试通过，覆盖 UTF-8/emoji 字节拆分、缓存去重、MyMemory 额度失败、Ollama 请求结构、服务地址校验、取消和过时译文保护、空白字幕清除、JSON 损坏恢复及 CSV 转义。
- 本机 Windows OCR 枚举到 `en-US`、`ja` 和 `zh-Hans-CN`；本次真实识别验证使用 `en-US`。
- 生成图像中 `Find the ancient relic before dawn.` 被准确识别。
- 通过真实 GDI 桌面截图识别练习场，读到 `Find the ancient relic before the enemy arrives.`。
- 在主窗口启动真实识别会话，将练习场文字发送 MyMemory 并收到中文译文；随后停止会话。
- 验证悬浮窗截图排除成功、锁定后存在鼠标穿透样式、解锁后清除此样式。
- 渲染并人工检查主界面、设置页和悬浮窗。

本地诊断的 JSON 和 PNG 放在被 Git 忽略的 `artifacts/diagnostics*` 下，不公开用户的桌面图像。README 使用程序自身渲染的固定示例界面。

核心自动测试使用假的 HTTP 响应，保证 CI 不依赖外部翻译服务。Windows 交互式桌面诊断另行运行，确实使用了真实 OCR、桌面截图和一次真实联网翻译。Ollama 接口已进行协议测试；没有在本机安装或下载模型，因此不能宣称实际模型端到端验证通过。

## 运行核心测试

```powershell
dotnet test tests/LexiLens.Core.Tests -c Release
```

## 运行桌面诊断

```powershell
dotnet run --project src/LexiLens.App -- --diagnostics artifacts/diagnostics
```

诊断只截取自己创建的练习窗口，只向 MyMemory 发送练习文本，使用单独的临时配置目录，不加载现有生词本或设置。会输出 `report.json`，其中 `success` 汇总 OCR、截图、穿透和在线会话检查。需要可交互桌面、英语 OCR 和网络；不适合无桌面 CI。

## 尚未验证的环境

| 场景 | 状态 |
| --- | --- |
| 具体商业游戏的任务界面和连续对话字幕 | 待测 |
| 独占全屏、反作弊或受保护画面 | 不保证支持，待逐个游戏验证 |
| HDR、不同显卡、远程桌面 | 待测 |
| 多屏负坐标及 100% / 150% / 200% 混合 DPI 框选 | 已实现物理坐标换算，尚待真实配置矩阵验证 |
| 英语之外的 OCR 质量及长段落翻译效果 | 待测 |
| Ollama 实际模型的质量、首次加载耗时、持续字幕延迟 | 协议测试通过，实际模型待测 |
| 持续 1 小时运行时的 CPU / 内存 / 翻译额度 | 待测 |
| 自定义快捷键、多个识别区域 | 尚未实现 |

欢迎附上系统版本、缩放比例、游戏显示模式、可复现步骤，以及不包含私人信息的截图，帮助扩展测试矩阵。
