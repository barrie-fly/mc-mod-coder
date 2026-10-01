using System.Text.Json;

namespace MCModCoder.Core.Remote;

/// <summary>远程资源平台</summary>
public enum RemotePlatform { Modrinth, CurseForge }

/// <summary>搜索结果条目</summary>
public class RemoteSearchHit
{
    public RemotePlatform Platform { get; set; }
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Summary { get; set; } = "";
    public long Downloads { get; set; }
    public string IconUrl { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    public string ProjectType { get; set; } = "mod";
    public string DateModified { get; set; } = "";
}

/// <summary>可下载文件信息</summary>
public class RemoteFileInfo
{
    public RemotePlatform Platform { get; set; }
    public string ProjectId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string VersionNumber { get; set; } = "";
    public List<string> GameVersions { get; set; } = new();
    public List<string> Loaders { get; set; } = new();
    public string ReleaseDate { get; set; } = "";
    public bool IsPrimary { get; set; }
    public List<string> RequiredDependencyIds { get; set; } = new();
}

/// <summary>远程请求失败异常（Message 为可直接展示的中文提示，StatusCode 为 HTTP 状态码，0 表示非 HTTP 错误）</summary>
public class RemoteRequestException : Exception
{
    public int StatusCode { get; }
    public RemoteRequestException(string message, int statusCode = 0) : base(message) { StatusCode = statusCode; }
}

/// <summary>远程访问共享 HttpClient（静态共享实例，统一 User-Agent）与统一网络错误转换</summary>
internal static class RemoteHttp
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCModCoder/1.0");
        return client;
    }

    /// <summary>发起 GET 请求并解析 JSON，统一把网络相关错误转换为 RemoteRequestException（中文提示）</summary>
    public static async Task<JsonDocument> GetJsonAsync(string url, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            configure?.Invoke(req);
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new RemoteRequestException($"请求失败（HTTP {(int)resp.StatusCode}）", (int)resp.StatusCode);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new RemoteRequestException("网络请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { throw new RemoteRequestException($"网络请求失败：{ex.Message}"); }
        catch (JsonException) { throw new RemoteRequestException("远程数据解析失败，请稍后重试"); }
    }
}

/// <summary>JSON 松散解析辅助（字段缺失 / 类型异常时容错返回默认值）</summary>
internal static class RemoteJson
{
    /// <summary>取字符串字段（数字/布尔自动转文本），缺失返回 def</summary>
    public static string Str(JsonElement e, string name, string def = "")
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? def,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => def
        };
    }

    /// <summary>取整数字段（字符串数字亦可），缺失返回 def</summary>
    public static long Long(JsonElement e, string name, long def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : def;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed)) return parsed;
        return def;
    }

    /// <summary>取布尔字段，缺失返回 def</summary>
    public static bool Bool(JsonElement e, string name, bool def = false)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : def,
            _ => def
        };
    }

    /// <summary>取嵌套对象，缺失或类型不符返回 default（ValueKind 为 Undefined）</summary>
    public static JsonElement Obj(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            return v;
        return default;
    }

    /// <summary>取字符串数组字段，缺失返回空列表</summary>
    public static List<string> StrList(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Number) list.Add(item.GetRawText());
        }
        return list;
    }

    /// <summary>解析 ISO 8601 时间字符串（Modrinth/CurseForge 通用），失败返回 MinValue</summary>
    public static DateTime ParseDate(string s)
        => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var d) ? d : DateTime.MinValue;
}
