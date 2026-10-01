using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MCModCoder.Core.Services;

/// <summary>外接 AI 图像服务类型</summary>
public enum AiImageProviderKind
{
    /// <summary>OpenAI 兼容接口（/v1/images/generations），适用于 OpenAI、Azure OpenAI 及各类兼容网关</summary>
    OpenAiCompatible = 0,
    /// <summary>Stable Diffusion WebUI 本地/远程接口（/sdapi/v1/txt2img）</summary>
    SdWebUi = 1,
}

/// <summary>
/// 外接 AI 图像生成设置：端点、密钥、模型与出图尺寸。
/// 持久化于 %APPDATA%\MCModCoder\ai-image.json（全局共享，不属于单个项目）。
/// </summary>
public class AiImageSettings
{
    public AiImageProviderKind Provider { get; set; } = AiImageProviderKind.OpenAiCompatible;
    /// <summary>服务基础地址，如 https://api.openai.com/v1 或 http://127.0.0.1:7860</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>API 密钥（OpenAI 兼容接口需要；SD WebUI 可留空）</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>模型名，如 dall-e-3、dall-e-2、sd3、cogview-3 等（SD WebUI 忽略）</summary>
    public string Model { get; set; } = "dall-e-3";
    /// <summary>出图尺寸，如 1024x1024</summary>
    public string Size { get; set; } = "1024x1024";
    /// <summary>负面提示词（SD WebUI 使用独立字段；OpenAI 兼容接口以「Avoid…」融入正向提示词）</summary>
    public string NegativePrompt { get; set; } = "blurry, low quality, text, watermark, realistic photo";
    /// <summary>是否把「像素游戏纹理」风格约束自动附加到提示词</summary>
    public bool PixelStyle { get; set; } = true;

    // ---------- 持久化 ----------
    public static string SettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCModCoder", "ai-image.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AiImageSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AiImageSettings>(File.ReadAllText(SettingsFile));
                if (s != null) return s;
            }
        }
        catch { }
        return new AiImageSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 配置写失败不致命 */ }
    }
}

/// <summary>一次 AI 图像生成的结果</summary>
public class AiImageResult
{
    public byte[] PngBytes { get; init; } = Array.Empty<byte>();
    /// <summary>实际使用的完整提示词</summary>
    public string PromptUsed { get; init; } = "";
    /// <summary>服务端返回的修改后提示词（DALL-E 3 会改写；无则与 PromptUsed 相同）</summary>
    public string RevisedPrompt { get; init; } = "";
    /// <summary>输出审计报告（生成后自动执行，见 AiOutputAudit）</summary>
    public AiAuditReport? Audit { get; set; }
}

/// <summary>
/// 外接 AI 图像辅助引擎：把外部 AI 图像模型（OpenAI 兼容 / Stable Diffusion WebUI）
/// 接入工作台，用文字描述直接生成物品图标、方块纹理等像素素材。
/// 与「物品图像设计」像素画布互补——AI 出图后可导入纹理库继续手工修改。
/// </summary>
public static class AiImageAssist
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>像素风风格约束（通用部分）：不与「无缝平铺」类指令冲突，且内嵌负面约束补偿 OpenAI 接口不支持负面提示词的问题</summary>
    public const string PixelStyleCore =
        "pixel art game texture, clean flat shading, simple shape, no text, no watermark, no signature";

    /// <summary>像素风风格约束（背景提示）：仅用于图标 / 立绘类出图；与「无缝平铺」指令互斥</summary>
    public const string PixelStyleBackgroundHint = "plain solid background";

    /// <summary>像素风风格约束完整版（历史兼容；「方块纹理」等无缝平铺场景请用 PixelStyleCore）</summary>
    public const string PixelStyleSuffix = PixelStyleCore + ", " + PixelStyleBackgroundHint;

    /// <summary>风格预设：名称 → 附加提示词片段（措辞已消除相互冲突的指令）</summary>
    public static readonly (string Name, string Hint)[] StylePresets =
    {
        ("物品图标", "game item icon, centered single object, sprite sheet style"),
        ("方块纹理", "seamless tileable block texture, top-down surface pattern, full-bleed, uniform lighting"),
        ("生物/怪物", "fantasy creature character, front view, full body, plain solid background"),
        ("工具/武器", "fantasy weapon or tool icon, diagonal orientation, metallic highlights"),
        ("食物", "fantasy food item icon, appetizing, cartoon style"),
        ("附魔符文", "magic rune symbol, glowing enchantment, symmetrical, centered"),
    };

    /// <summary>
    /// 组装完整提示词：用户描述 → 风格预设 → 像素风格约束（可选）。
    /// 「像素风格约束」在无缝平铺类风格下自动省略纯色背景提示，避免自相矛盾导致输出不稳定；
    /// 片段按逗号拆分为短语后全局去重（不区分大小写），避免重复指令干扰模型。
    /// </summary>
    public static string BuildPrompt(string prompt, string? styleHint, bool pixelStyle)
    {
        var tokens = new List<string>();
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            foreach (var raw in s.Split(',', '，'))
            {
                var t = raw.Trim().TrimEnd('.').Trim();
                if (t.Length > 0 && !tokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(t);
            }
        }

        Add(prompt);
        Add(styleHint);
        if (pixelStyle)
        {
            bool tileable = styleHint is not null &&
                (styleHint.Contains("seamless", StringComparison.OrdinalIgnoreCase) ||
                 styleHint.Contains("tileable", StringComparison.OrdinalIgnoreCase));
            Add(tileable ? PixelStyleCore : PixelStyleSuffix);
        }
        return string.Join(", ", tokens);
    }

    /// <summary>把负面提示词融入正向提示词（供不支持独立负面提示词字段的 OpenAI 兼容接口使用）</summary>
    public static string MergeNegativeIntoPrompt(string prompt, string? negative)
    {
        if (string.IsNullOrWhiteSpace(negative)) return prompt;
        var items = negative.Split(',', ';', '，', '；')
            .Select(t => t.Trim()).Where(t => t.Length > 0).Take(12);
        return $"{prompt}. Avoid the following: {string.Join(", ", items)}.";
    }

    /// <summary>
    /// 调用外接 AI 模型生成一张 PNG 图片。
    /// 生成前会审计提示词（空提示词直接拒绝）；返回结果自带 <see cref="AiImageResult.Audit"/> 审计报告。
    /// </summary>
    /// <param name="settings">服务配置</param>
    /// <param name="prompt">用户提示词（可为空字符串，与 negativePrompt 组合后仍需非空）</param>
    /// <param name="negativePrompt">负面提示词（SD WebUI 使用独立字段；OpenAI 兼容接口融入正向提示词）</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<AiImageResult> GenerateAsync(
        AiImageSettings settings, string prompt, string? negativePrompt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            throw new InvalidOperationException("请先在「AI 图像生成」模块中配置外接服务地址（Endpoint）。");

        // 提示词门禁：错误级问题（空提示词）直接拒绝，避免无意义的服务调用
        var promptReport = AiOutputAudit.AuditPrompt(prompt);
        if (promptReport.HasErrors)
            throw new InvalidOperationException("提示词审计未通过：" + promptReport.FirstErrorMessage);

        var result = settings.Provider switch
        {
            AiImageProviderKind.SdWebUi => await GenerateSdWebUiAsync(settings, prompt, negativePrompt, cancellationToken).ConfigureAwait(false),
            _ => await GenerateOpenAiAsync(settings, prompt, negativePrompt, cancellationToken).ConfigureAwait(false),
        };

        // 输出审计：确定性规则，绝不抛异常
        var (ew, eh) = ParseSize(settings.Size, 0);
        result.Audit = AiOutputAudit.AuditImage(result.PngBytes,
            ew > 0 ? ew : null, eh > 0 ? eh : null);
        return result;
    }

    // ================= OpenAI 兼容接口 =================

    private static async Task<AiImageResult> GenerateOpenAiAsync(
        AiImageSettings s, string prompt, string? negative, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("提示词不能为空。");

        // OpenAI 兼容接口无独立负面提示词字段：融入正向提示词（DALL-E 3 对自然语言约束有响应）
        var effective = MergeNegativeIntoPrompt(prompt.Trim(), negative);
        // 长度钳制：超上限先截断，避免整次调用被服务端 400 拒绝
        if (effective.Length > AiOutputAudit.MaxPromptLength)
            effective = effective[..AiOutputAudit.MaxPromptLength];

        var model = string.IsNullOrWhiteSpace(s.Model) ? "dall-e-3" : s.Model.Trim();
        var size = string.IsNullOrWhiteSpace(s.Size) ? "1024x1024" : s.Size.Trim();
        ValidateModelSize(model, size);

        // 端点拼接：/v1 → /images/generations；已带 /images → 只补 /generations；已完整则不动
        var baseUri = s.Endpoint.TrimEnd('/');
        if (!baseUri.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            !baseUri.EndsWith("/images", StringComparison.OrdinalIgnoreCase) &&
            !baseUri.EndsWith("/generations", StringComparison.OrdinalIgnoreCase))
            baseUri += "/v1";
        if (baseUri.EndsWith("/images", StringComparison.OrdinalIgnoreCase))
            baseUri += "/generations";
        else if (!baseUri.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            baseUri += "/images/generations";

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = effective,
            ["n"] = 1,
            ["size"] = size,
            ["response_format"] = "b64_json",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUri)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(s.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey.Trim());

        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;

        // 兼容 b64_json 与 url 两种返回
        var first = json.GetProperty("data")[0];
        byte[] bytes;
        if (first.TryGetProperty("b64_json", out var b64) && b64.GetString() is { Length: > 0 } b64Str)
            bytes = Convert.FromBase64String(b64Str);
        else if (first.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } urlStr)
            bytes = await Http.GetByteArrayAsync(urlStr, ct).ConfigureAwait(false);
        else
            throw new InvalidOperationException("服务返回中没有图片数据（b64_json / url 均缺失）。");

        var revised = first.TryGetProperty("revised_prompt", out var rp) ? rp.GetString() ?? "" : "";
        return new AiImageResult { PngBytes = bytes, PromptUsed = effective, RevisedPrompt = revised };
    }

    /// <summary>对已知模型（dall-e-2 / dall-e-3）做尺寸预校验，把服务端 400 转为可读的本地提示</summary>
    private static void ValidateModelSize(string model, string size)
    {
        if (!AiOutputAudit.KnownModelSizes.TryGetValue(model.ToLowerInvariant(), out var allowed)) return;
        if (allowed.Any(a => string.Equals(a, size, StringComparison.OrdinalIgnoreCase))) return;
        throw new InvalidOperationException(
            $"模型 {model} 仅支持尺寸：{string.Join(" / ", allowed)}，当前设置为 {size}。请在「出图尺寸」中调整。");
    }

    // ================= Stable Diffusion WebUI =================

    private static async Task<AiImageResult> GenerateSdWebUiAsync(
        AiImageSettings s, string prompt, string? negative, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(negative))
            throw new InvalidOperationException("提示词不能为空。");

        var (w, h) = ParseSize(s.Size, 512);
        var body = new Dictionary<string, object?>
        {
            ["prompt"] = string.IsNullOrWhiteSpace(prompt) ? "pixel art game texture" : prompt,
            ["negative_prompt"] = string.IsNullOrWhiteSpace(negative) ? s.NegativePrompt : negative,
            ["width"] = w,
            ["height"] = h,
            ["steps"] = 24,
            ["cfg_scale"] = 7,
            ["sampler_name"] = "DPM++ 2M Karras",
            ["batch_size"] = 1,
            ["n_iter"] = 1,
            ["seed"] = -1,
        };

        var baseUri = s.Endpoint.TrimEnd('/');
        if (!baseUri.EndsWith("/sdapi/v1/txt2img", StringComparison.OrdinalIgnoreCase))
            baseUri += "/sdapi/v1/txt2img";

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUri)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).RootElement;

        if (!json.TryGetProperty("images", out var images) || images.GetArrayLength() == 0)
            throw new InvalidOperationException("SD WebUI 返回中没有图片数据，请确认服务已启动且 API 可访问。");

        var b64 = images[0].GetString() ?? "";
        // WebUI 可能附带 info 前缀头（如 "data:image/png;base64,"），去掉
        int comma = b64.IndexOf(',');
        if (b64.StartsWith("data:") && comma >= 0) b64 = b64[(comma + 1)..];

        return new AiImageResult { PngBytes = Convert.FromBase64String(b64), PromptUsed = prompt };
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 400) text = text[..400];
            throw new InvalidOperationException($"外接 AI 服务返回 {(int)resp.StatusCode}：{text}");
        }
        return resp;
    }

    private static (int W, int H) ParseSize(string? size, int fallback)
    {
        var m = System.Text.RegularExpressions.Regex.Match(size ?? "", @"(\d{2,4})\s*[x×]\s*(\d{2,4})");
        if (!m.Success) return (fallback, fallback);
        int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value);
        return (Math.Clamp(w, 64, 4096), Math.Clamp(h, 64, 4096));
    }

    // ---------- 落盘 ----------

    /// <summary>把 PNG 字节写入指定目录，文件名按时间戳 + 提示词摘要生成，返回完整路径。
    /// 落盘前做 PNG 有效性防御校验，坏数据直接拒绝（不产生脏文件）。</summary>
    public static string SavePng(string dir, byte[] pngBytes, string prompt)
    {
        if (!AiOutputAudit.IsValidPng(pngBytes))
            throw new InvalidOperationException("AI 返回的不是有效 PNG 数据，已阻止保存。请重新生成（详情见审计面板）。");

        Directory.CreateDirectory(dir);
        var slug = new string((prompt ?? "ai").Where(char.IsLetterOrDigit)
            .Take(16).ToArray());
        if (slug.Length == 0) slug = "ai";
        var name = $"ai_{DateTime.Now:yyyyMMdd_HHmmss}_{slug}.png";
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, pngBytes);
        return path;
    }
}
