# AI 平台接入与免费政策

核对日期：**2026-10-20**。v0.4.0 内置 **25 个云平台预设**，另有自定义兼容服务、Ollama 与无需密钥的 MyMemory。

这些是接入配置，不是公共 API Key，也不代表每个平台都提供永久免费 API。用户自己的 API Key 在本机填写，按 Windows 当前账号加密保存。软件不代替平台注册、实名、开通模型或领取额度。

## 第一次使用

1. 启动后打开翻译服务向导。想直接体验就保留「直接试用」，点击「测试并保存」。
2. 已有智谱等额度时，选择「使用自己的 AI 平台额度」，选择平台。
3. 点击「获取密钥」打开官方页面，复制自己的标准 API Key，粘贴到软件。
4. 选择内置候选模型，或点击「读取账号模型列表」。读取到模型不等于免费或已有调用权限。
5. 点击「仅测试连接」查看译文和耗时，或「测试并保存」保存生效。

默认显示全部平台，并提供 DeepSeek、豆包、通义千问、ChatGPT/OpenAI、Claude、Gemini、Grok 快捷按钮。取消「显示全部平台」才只看免费档候选。试用/付费平台、手动添加的模型和修改后的地址需要确认额度后才能发送翻译请求。

## 常用平台直接配置

选择平台后，软件自动填写官方接口和候选模型，通常只需粘贴 Key → 确认额度 → 测试并保存。模型列表可读取，也可手动输入账号实际开通的 ID。

| 平台 | 默认候选 | 接入方式及注意点 |
| --- | --- | --- |
| DeepSeek | `deepseek-flash`；可选 `deepseek-v4-pro` | 官方 Chat Completions，关闭思考；免费网页聊天不代表 API 余额。保留 v0.2 官方地址对应的已存密钥 |
| 火山方舟 / 豆包 | `doubao-seed-2-1-lite-260915`；可选 pro/turbo | 方舟 Chat API；需先开通模型，也可输入 `ep-...` 推理接入点。已知 Seed 2 候选关闭思考，自填接入点采用服务端默认参数 |
| 通义千问 / 百炼 | `qwen-flash`；可选 `qwen-plus` | 先选择 Key 对应的北京、新加坡、美国弗吉尼亚或香港地区；不同地区密钥分别保存。工作空间专属根地址填高级地址 |
| ChatGPT / OpenAI API | `gpt-4.1-mini` | 原生 Responses API，`store: false`；使用标准 API Key 和 API 额度。仅支持 Chat Completions 的第三方代理应选择「其他兼容平台」 |
| Anthropic Claude | `claude-haiku-5-5` | 原生 Messages API，Haiku 5.5 使用低 effort；网页订阅不直接通用，附赠 API 积分按控制台条件使用 |
| xAI / Grok | `grok-4.3`；可选 `grok-4.7` | 原生 Responses API，`store: false`；4.3 关闭思考，4.7 使用 low。需要 API 余额，网页订阅不直接通用 |
| MiniMax | `MiniMax-M3`；可选 `MiniMax-M3.1-Flash` | 选择国内/国际地区与对应 Key；M3 关闭思考，Flash 使用 low 并分离思考输出 |

接口与参数依据：[DeepSeek](https://api-docs.deepseek.com/api/create-chat-completion/)、[豆包模型与思考参数](https://docs.volcengine.com/docs/ark/deep-thinking)、[百炼地区地址](https://help.aliyun.com/en/model-studio/base-url)、[Qwen Flash](https://help.aliyun.com/en/model-studio/qwen-flash)、[OpenAI Responses](https://developers.openai.com/api/reference/resources/responses/methods/create)、[Claude Haiku 5.5](https://platform.claude.com/docs/en/models/haiku-5-5/migration-guide)、[Grok](https://docs.x.ai/developers/model-capabilities/text/generate-text)、[MiniMax](https://platform.minimax.io/docs/api-reference/text-openai-api)。预填 ID 并不保证账号有权限，报 404/403 时请读取列表并检查控制台。

## 有免费档的候选

| 平台 | 内置候选模型 | 条件与官方政策 |
| --- | --- | --- |
| 智谱 GLM | `glm-4.5-flash` | [官方列为免费](https://docs.bigmodel.cn/cn/guide/models/free/glm-4.5-flash)，有权限与频率限制；使用标准 API Key，关闭思考 |
| 硅基流动 | `Qwen/Qwen3-8B` | [部分模型有免费档](https://docs.siliconflow.cn/docs/userguide/quickstart)，以当前模型价格为准，Pro 等模型可能计费 |
| 魔搭 ModelScope | `Qwen/Qwen3-8B` | [API-Inference 体验](https://modelscope.cn/docs/model-service/API-Inference/intro)有额度和模型限制，不等同于 API-Provider 付费转接 |
| Google Gemini | `gemini-3.5-flash-lite`、`gemini-3.8-flash` | [部分模型有 Free Tier](https://ai.google.dev/gemini-api/docs/pricing)，地区、项目计费档位、限额和数据政策不同 |
| Groq | `qwen/qwen3.8-27b`、`openai/gpt-oss-20b` | [Free Plan 限额](https://console.groq.com/docs/rate-limits)，Developer Plan 计费 |
| OpenRouter | `openrouter/free` | [免费路由/免费变体](https://openrouter.ai/docs/guides/routing/model-variants/free)，有频率、日限额及供应商数据策略 |
| Cloudflare Workers AI | `@cf/meta/llama-3.1-8b-instruct` | [每日免费 Neurons](https://developers.cloudflare.com/workers-ai/platform/pricing/)，需要 Account ID 和相应 API Token，付费计划超额可能计费 |

**“有免费档”不等于软件能保证账单为零。** 例如已开启计费的 Gemini 项目、Workers AI 付费账号，可能使用同名模型但按付费规则结算。软件无法读取并保证所有平台的剩余额度。若要求不产生 API 费用，应在平台控制台关闭自动付费/开启用完即停，或使用本机模型。

## 赠送额度或需要自备额度的预设

下面全部支持接入，但不承诺长期免费；没有确认免费资格的模型不会伪装成免费模型。

| 平台 | 入口/政策说明 | 需要注意 |
| --- | --- | --- |
| 讯飞星火 | [HTTP 接口](https://www.xfyun.cn/doc/spark/HTTP调用文档.html) | 候选 `lite`；使用 HTTP APIpassword；活动和模型免费资格以控制台为准 |
| Mistral | [计划说明](https://docs.mistral.ai/admin/billing-usage/subscriptions) | Free/试验资格可能变化；API Key 与 Vibe 套餐不同 |
| Cerebras | [试用与限额](https://inference-docs.cerebras.ai/support/rate-limits) | 当前为限时 Free Trial，文档说明没有自动续期的永久免费档 |
| Hugging Face | [推理计费](https://huggingface.co/docs/inference-providers/pricing) | 使用有推理权限的 Token，赠送额度按账号类型和供应商变化 |
| 阿里云百炼 | [新人额度与用完即停](https://help.aliyun.com/zh/model-studio/new-free-quota) | 部分模型有时效赠送，选对地域；开启免费用完即停 |
| 火山方舟/豆包 | [Chat API](https://docs.volcengine.com/docs/ark/chat-api) | 需要已开通模型 ID 或接入点 ID，核对安心体验模式 |
| 百度千帆 | [调用文档](https://cloud.baidu.com/doc/qianfan-docs/s/Fm9l6ocai) | 使用新版 API Key，不是旧版 AK/SK；模型/活动额度以控制台为准 |
| 腾讯混元 | [兼容接口](https://cloud.tencent.com/document/product/1729/111007) | 使用兼容接口 API Key，不是云账号 SecretId/SecretKey |
| Kimi/月之暗面 | [快速开始](https://platform.kimi.com/docs/get-api-key) | 标准 API 额度与聊天/编程套餐不同 |
| MiniMax | [文本接口](https://platform.minimaxi.com/docs/api-reference/text-openai-api) | 国内标准 API 与国外地址、套餐资格不同 |
| 阶跃星辰 | [开放平台](https://platform.stepfun.com/) | 赠送额度和可调用模型随账号变化 |
| DeepSeek | [API 文档](https://api-docs.deepseek.com/) | 不把免费网页聊天当成免费 API；读取实际模型 ID |
| OpenAI | [官方模型说明](https://developers.openai.com/api/docs/models/gpt-4.1-mini) | 需可用 API 额度，ChatGPT 订阅不通用；预设不是免费承诺 |
| xAI / Grok | [Responses API](https://docs.x.ai/developers/model-capabilities/text/generate-text) | 需可用 API 余额；网页会员与 API 权限不同 |
| Anthropic Claude | [Messages API](https://docs.anthropic.com/en/api/messages) | 需要标准 API Key 与可用余额，读取账号可用模型 |
| SambaNova | [开发文档](https://docs.sambanova.ai/docs/en/get-started/overview) | 试用资格、限额与模型随账号变化 |
| Together AI | [快速开始](https://docs.together.ai/docs/quickstart) | 不保证新账号免费，需有赠送或充值额度 |
| NVIDIA NIM/Build | [托管接口](https://docs.api.nvidia.com/nim/reference/llm-apis) | 核对试用资格与模型 Key；托管体验不等于自部署服务免费 |

部分平台不提供标准模型列表，或列表请求需要额外权限。此时向导会保留手动填写模型 ID 的方式，不猜测已经下线的模型。可用模型会变化，旧候选报错时请读取列表并在控制台确认。

## 本地模型与其他平台

选择「本地离线」，先从向导里的官方链接安装 Ollama，点击「检测并读取本机模型」。没有模型时可点击「下载推荐 3B 模型」，下载 `qwen2.5:3b`（约 1.9 GB）；需要网络、磁盘空间和运行资源。关闭向导可取消客户端请求，实际下载由 Ollama 管理，可再次检测或继续下载。

LM Studio 或其他兼容 Chat Completions 的服务可选择「其他兼容平台」，高级设置填写根地址（例如 `http://localhost:1234/v1`）和模型 ID。本机可不填 Key，远程必须 HTTPS；如服务有认证则填写其密钥。不支持所有厂商的特殊签名、自定义业务接口或任意代理协议。

## 数据、费用与验证范围

- 仅在用户点击测试或启动翻译后发送文字；切换平台不会自动发出测试请求。
- 不自动注册、不捆绑公共 Key、不自动切换其他平台或付费模型。平台错误、限流、余额不足会显示明确提示。
- 401/403/402/400/404 会阻止当前翻译客户端继续发请求，修改服务配置后可重试；429 遵守 Retry-After 并有冷却期。
- 冷却、缓存和模型分类不能替代服务端账单限制。只有服务端明确返回余额/配额错误时，客户端才知道已超额。
- 密钥保存为 `%LOCALAPPDATA%\LexiLens\credentials.dat`，用 Windows DPAPI CurrentUser 加密，并按平台及服务地址隔离。改地址不会自动携带另一个地址的密钥。
- `settings.json` 只保存平台、模型、地址等普通配置，不含明文密钥；换电脑或 Windows 账号后需重新填写密钥。
- 已通过协议模拟测试、向导交互与密钥加密诊断。未持有上述平台的用户 Key，因此**没有逐个平台完成真实账号连通性或免费资格验证**；以向导的真实连接测试和平台控制台为准。
