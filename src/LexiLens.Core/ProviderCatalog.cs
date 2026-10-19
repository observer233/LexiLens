namespace LexiLens.Core;

public enum ApiProtocol { ChatCompletions, Gemini, Anthropic, Responses }
public enum AccessCategory { FreeTier, TrialOrCredits, PaidOrUnknown, Custom }
public sealed record ServiceEndpoint(string Name, string Url);

public sealed record CloudPreset(
    string Id, string Name, ApiProtocol Protocol, string Endpoint,
    AccessCategory Access, string[] Models, string Notice, string KeyUrl, string DocsUrl,
    string KeyLabel = "API Key", bool NeedsAccountId = false)
{
    public string CategoryLabel => Access switch
    {
        AccessCategory.FreeTier => "有免费档 · 有限制",
        AccessCategory.TrialOrCredits => "试用/赠送额度 · 查有效期",
        AccessCategory.Custom => "自定义 · 费用未核验",
        _ => "自备额度 · 不保证免费"
    };
    public string DisplayName => $"{Name}  ·  {CategoryLabel}";
    public CloudProfile DefaultProfile => new() { Model = Models.FirstOrDefault() ?? "" };
    public IReadOnlyList<ServiceEndpoint> Endpoints => Id switch
    {
        "dashscope" => [new("中国 · 北京", Endpoint), new("新加坡", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"),
            new("美国 · 弗吉尼亚", "https://dashscope-us.aliyuncs.com/compatible-mode/v1"), new("中国 · 香港", "https://cn-hongkong.dashscope.aliyuncs.com/compatible-mode/v1")],
        "moonshot" => [new("中国", Endpoint), new("国际", "https://api.moonshot.ai/v1")],
        "minimax" => [new("中国", Endpoint), new("国际", "https://api.minimax.io/v1")],
        _ => [new("官方默认", Endpoint)]
    };
}

// Reviewed 2026-10-20. These are connection presets, never shared credentials or
// a guarantee about the user's account billing. All policy links are visible in UI.
public static class ProviderCatalog
{
    public const string ReviewedOn = "2026-10-20";
    public static IReadOnlyList<CloudPreset> All { get; } =
    [
        new("zhipu", "智谱 GLM", ApiProtocol.ChatCompletions, "https://open.bigmodel.cn/api/paas/v4", AccessCategory.FreeTier,
            ["glm-4.5-flash"], "官方列有免费 Flash 模型。使用模型标准 API Key；Coding Plan Key 不通用。限流和权限以控制台为准。",
            "https://bigmodel.cn/usercenter/proj-mgmt/apikeys", "https://docs.bigmodel.cn/cn/guide/models/free/glm-4.5-flash"),
        new("siliconflow", "硅基流动", ApiProtocol.ChatCompletions, "https://api.siliconflow.cn/v1", AccessCategory.FreeTier,
            ["Qwen/Qwen3-8B"], "平台部分模型有免费档，Pro/ 前缀等模型可能收费。选择前核对模型当前价格与账号权限。",
            "https://cloud.siliconflow.cn/account/ak", "https://docs.siliconflow.cn/docs/userguide/quickstart"),
        new("modelscope", "魔搭 ModelScope", ApiProtocol.ChatCompletions, "https://api-inference.modelscope.cn/v1", AccessCategory.FreeTier,
            ["Qwen/Qwen3-8B"], "API-Inference 有限额体验；模型可用性、每日额度、实名要求以账号页面为准。API-Provider 转接不等于免费。",
            "https://modelscope.cn/my/myaccesstoken", "https://modelscope.cn/docs/model-service/API-Inference/intro", "访问令牌 Access Token"),
        new("gemini", "Google Gemini", ApiProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta", AccessCategory.FreeTier,
            ["gemini-3.5-flash-lite", "gemini-3.8-flash"], "部分模型提供免费档，地区与限额受限。已开通计费的项目可能按付费档计费；免费档数据政策也不同，请先核对项目。",
            "https://aistudio.google.com/apikey", "https://ai.google.dev/gemini-api/docs/pricing"),
        new("groq", "Groq", ApiProtocol.ChatCompletions, "https://api.groq.com/openai/v1", AccessCategory.FreeTier,
            ["qwen/qwen3.8-27b", "openai/gpt-oss-20b"], "Free Plan 有按模型的请求与 Token 限额，Developer Plan 则计费。使用免费计划并查看账号 Limits。",
            "https://console.groq.com/keys", "https://console.groq.com/docs/rate-limits"),
        new("openrouter", "OpenRouter 免费路由", ApiProtocol.ChatCompletions, "https://openrouter.ai/api/v1", AccessCategory.FreeTier,
            ["openrouter/free"], "默认路由只选择免费模型，但有日限额、数据策略和供应商差异。手动更换非 :free 模型可能收费。",
            "https://openrouter.ai/settings/keys", "https://openrouter.ai/docs/guides/routing/model-variants/free"),
        new("cloudflare", "Cloudflare Workers AI", ApiProtocol.ChatCompletions, "https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/v1", AccessCategory.FreeTier,
            ["@cf/meta/llama-3.1-8b-instruct"], "有每日免费 Neurons 额度。需要 Account ID 与 Workers AI 权限的 API Token；付费计划超额可能计费。",
            "https://dash.cloudflare.com/profile/api-tokens", "https://developers.cloudflare.com/workers-ai/platform/pricing/", "API Token", true),
        new("spark", "讯飞星火", ApiProtocol.ChatCompletions, "https://spark-api-open.xf-yun.com/v1", AccessCategory.TrialOrCredits,
            ["lite"], "Lite/活动模型的免费或赠送额度以控制台为准。填写 HTTP APIpassword，不能直接使用 WebSocket 的 APIKey。",
            "https://console.xfyun.cn/services/cbm", "https://www.xfyun.cn/doc/spark/HTTP调用文档.html", "HTTP APIpassword"),
        new("mistral", "Mistral", ApiProtocol.ChatCompletions, "https://api.mistral.ai/v1", AccessCategory.PaidOrUnknown,
            ["mistral-small-latest"], "Free/试验计划的 API 资格可能调整，不能保证所有账号可调用。请确认 API 额度，网页/Vibe 套餐不等于标准 API。",
            "https://console.mistral.ai/api-keys", "https://docs.mistral.ai/admin/billing-usage/subscriptions"),
        new("cerebras", "Cerebras", ApiProtocol.ChatCompletions, "https://api.cerebras.ai/v1", AccessCategory.TrialOrCredits,
            ["qwen-3.8-27b", "gpt-oss-120b"], "当前文档是有期限的 Free Trial，非永久免费档；可能要求验证付款方式，额度与有效期以账号为准。",
            "https://cloud.cerebras.ai/platform/api-keys", "https://inference-docs.cerebras.ai/support/rate-limits"),
        new("huggingface", "Hugging Face", ApiProtocol.ChatCompletions, "https://router.huggingface.co/v1", AccessCategory.PaidOrUnknown,
            [], "Inference Providers 权限、赠送额度和计费按账号及供应商变化；网页免费聊天不代表 API 免费。先读取模型列表再选择。",
            "https://huggingface.co/settings/tokens", "https://huggingface.co/docs/inference-providers/pricing", "具有推理权限的 HF Token"),
        new("dashscope", "阿里云百炼 / 通义千问", ApiProtocol.ChatCompletions, "https://dashscope.aliyuncs.com/compatible-mode/v1", AccessCategory.TrialOrCredits,
            ["qwen-flash", "qwen-plus"], "API Key 与地域绑定，请先选择与 Key 相同的地区。新用户赠送额度有期限，用完可能计费；Coding Plan Key 不通用。工作空间专属域名可填在高级地址中。",
            "https://bailian.console.aliyun.com/", "https://help.aliyun.com/zh/model-studio/new-free-quota"),
        new("doubao", "火山方舟 / 豆包", ApiProtocol.ChatCompletions, "https://ark.cn-beijing.volces.com/api/v3", AccessCategory.TrialOrCredits,
            ["doubao-seed-2-1-lite-260915", "doubao-seed-2-1-pro-260915", "doubao-seed-2-1-turbo-260628"], "先在火山方舟开通模型。可选预填模型 ID，或直接粘贴 ep- 开头的推理接入点 ID。使用方舟 API Key；豆包 App 登录和 Coding Plan Key 不通用。请核对体验额度。",
            "https://ark.volcengine.com/region:cn-beijing/apikey", "https://docs.volcengine.com/docs/ark/chat-api"),
        new("baidu", "百度智能云千帆", ApiProtocol.ChatCompletions, "https://qianfan.baidubce.com/v2", AccessCategory.TrialOrCredits,
            [], "需标准 API Key 和已开通的模型。免费模型或活动额度以控制台当前政策为准；不使用旧版 AK/SK 鉴权。",
            "https://console.bce.baidu.com/iam/#/iam/apikey/list", "https://cloud.baidu.com/doc/qianfan-docs/s/Fm9l6ocai"),
        new("tencent", "腾讯混元", ApiProtocol.ChatCompletions, "https://api.hunyuan.cloud.tencent.com/v1", AccessCategory.TrialOrCredits,
            ["hunyuan-lite"], "Lite 和赠送资源政策可能调整。需 OpenAI 兼容接口 API Key，不使用云账号 SecretId/SecretKey。",
            "https://console.cloud.tencent.com/hunyuan/", "https://cloud.tencent.com/document/product/1729/111007"),
        new("moonshot", "Kimi / 月之暗面", ApiProtocol.ChatCompletions, "https://api.moonshot.cn/v1", AccessCategory.PaidOrUnknown,
            ["kimi-k2.6"], "标准 API 按账号额度计费。聊天会员和编程套餐不等于本工具可用的通用 API 余额。",
            "https://platform.kimi.com/console/api-keys", "https://platform.kimi.com/docs/get-api-key"),
        new("minimax", "MiniMax", ApiProtocol.ChatCompletions, "https://api.minimaxi.com/v1", AccessCategory.PaidOrUnknown,
            ["MiniMax-M3", "MiniMax-M3.1-Flash"], "按地区选择标准 API Key；网页订阅不等于 API 余额。M3 默认关闭思考；M3.1-Flash 必须思考，使用 low 深度，仍可能比普通文本模型慢。",
            "https://platform.minimaxi.com/user-center/basic-information/interface-key", "https://platform.minimaxi.com/docs/api-reference/text-openai-api"),
        new("stepfun", "阶跃星辰", ApiProtocol.ChatCompletions, "https://api.stepfun.com/v1", AccessCategory.TrialOrCredits,
            [], "部分账号可能有活动赠送额度，不能保证免费。使用已开通模型 ID；剩余额度及有效期以控制台为准。",
            "https://platform.stepfun.com/", "https://platform.stepfun.com/docs"),
        new("deepseek", "DeepSeek", ApiProtocol.ChatCompletions, "https://api.deepseek.com", AccessCategory.PaidOrUnknown,
            ["deepseek-flash", "deepseek-v4-pro"], "使用 DeepSeek 开放平台 API Key 与 API 余额。默认关闭思考以降低字幕延迟；免费网页聊天账号不代表有 API 额度。可读取最新模型列表。",
            "https://platform.deepseek.com/api_keys", "https://api-docs.deepseek.com/"),
        new("openai", "ChatGPT / OpenAI API", ApiProtocol.Responses, "https://api.openai.com/v1", AccessCategory.PaidOrUnknown,
            ["gpt-4.1-mini"], "标准 API 需可用 API 余额；ChatGPT 免费版、Plus/Pro 订阅不等于 API 额度。本预设不承诺赠送额度。",
            "https://platform.openai.com/api-keys", "https://developers.openai.com/api/docs/models/gpt-4.1-mini"),
        new("xai", "xAI / Grok", ApiProtocol.Responses, "https://api.x.ai/v1", AccessCategory.PaidOrUnknown,
            ["grok-4.3", "grok-4.7"], "使用 xAI API Key 与可用 API 余额。Grok 网页订阅不等于 API 权限。4.3 默认关闭思考；4.7 使用低思考深度，字幕可能较慢。",
            "https://console.x.ai/", "https://docs.x.ai/developers/model-capabilities/text/generate-text"),
        new("anthropic", "Anthropic Claude", ApiProtocol.Anthropic, "https://api.anthropic.com/v1", AccessCategory.PaidOrUnknown,
            ["claude-haiku-5-5"], "标准 Messages API 需可用余额和 API Key，Claude 网页订阅不直接通用。可读取账号可用模型；若账号有订阅附赠 API 积分，请在平台核对适用模型和有效期。",
            "https://console.anthropic.com/settings/keys", "https://docs.anthropic.com/en/api/messages"),
        new("sambanova", "SambaNova", ApiProtocol.ChatCompletions, "https://api.sambanova.ai/v1", AccessCategory.TrialOrCredits,
            [], "试用额度、模型和请求限额随账号变化，请先在控制台核对并读取可用模型。",
            "https://cloud.sambanova.ai/", "https://docs.sambanova.ai/docs/en/get-started/overview"),
        new("together", "Together AI", ApiProtocol.ChatCompletions, "https://api.together.xyz/v1", AccessCategory.PaidOrUnknown,
            [], "不承诺注册即有免费 API。需确认账号赠送或充值额度，并选择当前 Serverless 模型。",
            "https://api.together.ai/settings/api-keys", "https://docs.together.ai/docs/quickstart"),
        new("nvidia", "NVIDIA NIM / Build", ApiProtocol.ChatCompletions, "https://integrate.api.nvidia.com/v1", AccessCategory.TrialOrCredits,
            [], "托管体验 API 有账号资格、模型和调用限制；免费体验不等于自部署 NIM 免费。先在 Build 获取支持该模型的 Key。",
            "https://build.nvidia.com/", "https://docs.api.nvidia.com/nim/reference/llm-apis"),
        new("custom", "其他兼容平台 / LM Studio", ApiProtocol.ChatCompletions, "", AccessCategory.Custom,
            [], "手动填写兼容 Chat Completions 的服务地址与模型 ID。本地服务可不填 Key；云服务的数据和费用由该服务决定。",
            "https://lmstudio.ai/docs/developer/core/server", "https://lmstudio.ai/docs/developer/core/server")
    ];

    public static CloudPreset? Find(string? id) => All.FirstOrDefault(x => x.Id == id);
    public static CloudPreset Get(string id) => Find(id) ?? throw new ArgumentException("未识别的平台，请重新选择。");
    public static CloudProfile Profile(AppSettings settings) => settings.CloudProfiles.GetValueOrDefault(settings.CloudServiceId) ?? Get(settings.CloudServiceId).DefaultProfile;
    public static bool NeedsCreditAcknowledgement(CloudPreset preset, CloudProfile profile) =>
        preset.Access != AccessCategory.FreeTier || !preset.Models.Contains(profile.Model, StringComparer.Ordinal)
        || !string.IsNullOrWhiteSpace(profile.Endpoint);

    public static Uri Endpoint(CloudPreset preset, CloudProfile profile)
    {
        var value = string.IsNullOrWhiteSpace(profile.Endpoint) ? preset.Endpoint : profile.Endpoint.Trim();
        if (preset.NeedsAccountId && value.Contains("{accountId}", StringComparison.Ordinal))
        {
            if (profile.AccountId.Length != 32 || !profile.AccountId.All(Uri.IsHexDigit))
                throw new ArgumentException("请从 Cloudflare 控制台复制 32 位 Account ID。");
            value = value.Replace("{accountId}", profile.AccountId, StringComparison.Ordinal);
        }
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("服务地址格式无效，请填写平台文档中的 API 根地址，不包含密钥或查询参数。");
        if (uri.Scheme == "http" && !uri.IsLoopback) throw new ArgumentException("远程服务需要 HTTPS，本机服务可使用 HTTP。");
        if (!string.IsNullOrWhiteSpace(profile.Endpoint) && new[] { "/chat/completions", "/responses", "/messages" }.Any(x => uri.AbsolutePath.TrimEnd('/').EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请填写 API 根地址（通常以 /v1 结尾），去掉 /chat/completions、/responses 或 /messages。");
        return uri;
    }
}
