# v0.3.0 “等待识别游戏文字”故障分析

记录日期：2026-10-20；修复版本：v0.4.0。

用户反馈：v0.2 可以使用，v0.3 接入智谱免费模型后，连接测试成功，但框选游戏区域并开始后一直等待识别。

## 已确认的代码缺陷

### 1. 完全相同的两次 OCR 才会请求翻译

[v0.3.0 LiveSession](https://github.com/observer233/LexiLens/blob/v0.3.0/src/LexiLens.Core/LiveSession.cs) 使用严格字符串相等判断。识别原文每次变化一点，即使句子基本一致，也会反复把计数重置为 1，永远无法进入翻译队列。

独立实验取出 v0.3.0 原始 `LiveSession.cs`，与修复版使用相同来源、翻译计数器、50ms 间隔和 700ms 运行时长。OCR 交替返回：

```text
Find the ancient relic before dawn.
Find the ancient relic before davvn.
```

| 版本 | API 调用次数 |
| --- | --- |
| v0.3.0 原逻辑 | 0 |
| v0.4.0 修复逻辑 | 1 |

[实验输出](benchmarks/wait-jitter-v030.json)。现有回归测试 `PipelineTests.AlternatingMinorOcrErrorsStillTranslateAndReportEveryStage` 覆盖这一触发条件。

修复：容忍少量编辑差异，数字序列必须一致，短文本保守判断；连续两次近似结果即可提交，持续变化三次则提交当前结果。新增“翻译一次”直接提交当次结果，绕过稳定等待。

### 2. 空 OCR 与实际识别状态不可见

旧版在文字稳定前不发布原文；初始空结果也不会触发更新。因此 OCR 已工作但不稳定、OCR 返回空、截图为空等情况，都可能表现成同一句“等待识别”。

修复：每次识别立即更新原文，并分别记录截图、OCR、文字判断、翻译和悬浮显示。空结果明确显示“未识别到文字”，记录 `OCR_EMPTY`；近乎全黑的截图记为 `CAPTURE_DARK`。

### 3. 原生 OCR 缺少超时隔离

v0.3 在应用进程里调用 PaddleOCR。原生调用不能靠普通 .NET CancellationToken 强制打断；如果卡住，停止/关闭也可能等不到返回。此前只验证了正常模型运行。

修复：增强识别移到同一 EXE 的独立子进程，通过当前用户专用命名管道传送选区。首次 15 秒、后续 8 秒为限，超时终止该子进程，有 Windows OCR 时继续识别；空结果也尝试系统 OCR。

故障注入验证故意让子进程连接后无限等待，确认父进程到时终止它、记录 `OCR_TIMEOUT_FALLBACK` 并从 Windows OCR 得到练习句。另验证取消请求能终止子进程。停止时不会留下仍显示“处理中”的步骤。

## 与智谱 API 的关系

连接测试翻译固定英文短句，不经过屏幕截图/OCR。该测试成功说明那次请求的密钥、模型和网络可用；不能证明实际选区已经产出文字。此前的等待文案也不能证明已经向 API 发出了请求。

以上已证明代码存在会阻止请求的触发条件。用户设备上的具体分支仍需要新版日志确认：可能是空识别、严格稳定判断或引擎异常。没有取得用户的实际游戏帧和旧版运行日志，不能把其中某个分支当成已确定的现场根因。

## 日志代码

| 代码 | 含义 | 对应操作 |
| --- | --- | --- |
| CAPTURE_START / CAPTURE_OK | 开始截图 / 截图完成 | 核对选区大小 |
| CAPTURE_DARK | 截图近乎全黑 | 检查遮挡、显示模式和受保护内容 |
| CAPTURE_FAILED | 截图失败 | 重新选择区域 |
| OCR_START / OCR_OK | OCR 处理中 / 有识别文字 | 查看字符数、耗时和原文 |
| OCR_EMPTY | 没有识别到文字 | 检查选区，尝试其他 OCR 或图像处理 |
| OCR_FRAME_CACHE | 图像相同，复用识别结果 | 没有重复运行 OCR |
| OCR_EMPTY_FALLBACK | 增强为空，系统 OCR 成功 | 当前结果来自 Windows OCR |
| OCR_TIMEOUT_FALLBACK | 增强进程超时被终止，改用系统 OCR | 查看系统 OCR 的后续结果 |
| OCR_ENGINE_FALLBACK | 增强进程异常，改用系统 OCR | 核对解压完整性与运行库 |
| OCR_FAILED | OCR 没能恢复 | 查看当前引擎和错误说明 |
| TEXT_PENDING | 已识别，在确认文字 | API 尚未请求；也可单次翻译 |
| TEXT_READY / TEXT_MANUAL | 已确认 / 手动单次提交 | 即将进入翻译 |
| TEXT_DEBOUNCE_LIMIT | 持续变化达到三次，提交当前文字 | 避免无限等待 |
| TEXT_UNCHANGED | 文字未变化 | 跳过重复翻译 |
| API_REQUEST_SENT | 已实际发出服务请求 | 等待 HTTP 响应 |
| HTTP_200 | HTTP 成功 | 后续仍需解析出有效译文 |
| HTTP_401 / HTTP_403 | 密钥、权限或地区问题 | 检查标准 API Key、模型权限和地区 |
| HTTP_402 / HTTP_429 | 余额不足 / 限流或配额 | 查看平台控制台 |
| HTTP_404 / HTTP_400 | 模型、地址或参数错误 | 读取模型列表并检查配置 |
| API_TIMEOUT | 翻译请求超时 | 检查服务连接 |
| TRANSLATION_CACHE | 复用已有译文 | 没有重复请求 API |
| TRANSLATION_OK / OVERLAY_OK | 译文返回 / 已显示 | 流程完成 |
| CANCELLED | 用户停止 | 不会当作服务故障 |

## 如何提交问题

复现后打开“诊断与日志”→“导出诊断报告”，附上报告、游戏名和显示模式即可。报告为白名单元数据：事件、耗时、代码、模型、语言、选区尺寸、系统版本；不序列化密钥、服务地址、窗口标题、原文或截图。日志保存在本机并轮换，重启后仍能读取最近记录。
