# LexiLens v0.3.0 · 字体增强识别与主流平台快捷接入

下载 `LexiLens-v0.3.0-win-x64.zip`，完整解压后运行 `LexiLens.exe`。自带 .NET、PaddleOCR v5 模型和 CPU 推理库，无需 Python 或手动下载 OCR 模型。Windows x64，建议 Windows 11。

## 字体识别

- 默认启用本地 PaddleOCR v5 增强英文/中文识别，保留 Windows OCR 选项。
- 自动放大小字、增强局部对比度、暗底反色；弱识别时用原图重试。可手动选原图、暗底亮字、低对比度。
- 「检查识别」查看框选截图、原始 OCR、引擎和耗时，不调用翻译 API。
- 同行碎片自动合并，相同截图复用结果。CPU 推理限制为 2 个数学线程，截图留在本机。

## AI 平台

- 默认展示全部 25 个云平台，另有自定义兼容服务与 Ollama。
- DeepSeek、豆包、通义千问、ChatGPT/OpenAI、Claude、Gemini、Grok 快捷入口，补充候选模型。
- 千问可选北京、新加坡、美国弗吉尼亚和香港；Kimi、MiniMax 可选国内或国际地址，各地址隔离密钥。
- OpenAI 与新增的 Grok 使用原生 Responses API，`store: false`；Claude 使用 Messages，Gemini 使用原生接口，其他平台使用兼容 Chat Completions。
- 已知 DeepSeek、豆包 Seed 2、MiniMax M3 关闭思考以降低延迟；其他参数按模型分别处理。保留 DeepSeek v0.2 官方地址的已存密钥。

## 升级与使用

退出旧版，解压并运行新版；本机设置、生词和密钥库沿用。偏好设置选择「本地增强」→「自动」→保存，重新框选并点击「检查识别」。已有平台额度时：选择平台 → 粘贴自己的标准 API Key → 选模型/地区 → 确认额度 → 测试并使用。

OCR 模型已内置；可选的 Ollama **翻译模型**仍需另行下载。英文/中文增强无需系统 OCR 语言包；其他语言和 Windows 引擎需要。增强加载失败时会明确提示，有 Windows OCR 则切换；少数缺少 VC++ x64 运行库的系统可从偏好设置打开微软安装说明。

## 验证和限制

64 项自动测试通过。自包含发布 EXE 完成真实截图、PaddleOCR、MyMemory 翻译和悬浮窗诊断。8 组合成字体样张的字符错误率从 Windows OCR 22.7% 降至增强 0.8%，但仍有相似字母误识别；这不是实际游戏通用正确率。样张中模型加载后单次约 0.6–0.8 秒，首次约 2.3 秒，实际延迟另含扫描与翻译。

API 适配经过官方文档核对和协议模拟，未使用 25 家真实账号逐一验证。需要自己的 API Key、权限及额度，不保证所有模型免费；ChatGPT/豆包等网页订阅不直接等同 API 余额。详见 [平台配置](https://github.com/observer233/LexiLens/blob/main/docs/AI_PLATFORMS.md) 与 [字体实测](https://github.com/observer233/LexiLens/blob/main/docs/OCR.md)。

特殊花体、像素字、特效遮挡、HDR、独占全屏和各游戏兼容性仍有边界。保留英中对照、鼠标穿透、生词本和 CSV 导出；发布包尚未签名。
