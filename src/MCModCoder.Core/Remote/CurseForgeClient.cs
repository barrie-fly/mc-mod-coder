using System.Text.Json;

namespace MCModCoder.Core.Remote;

/// <summary>CurseForge API 客户端（未配置 ApiKey 时走社区公益镜像 api.curse.tools，官方路由一致）</summary>
public static class CurseForgeClient
{
    /// <summary>官方 API Key（为空时使用社区镜像，配置后才请求官方接口）</summary>
    public static string? ApiKey { get; set; }

    public static string BaseUrl => string.IsNullOrEmpty(ApiKey)
        ? "https://api.curse.tools/v1/cf"
        : "https://api.curseforge.com/v1";

    private const int GameId = 432; // Minecraft

    /// <summary>附加请求头（Accept 已在共享方法中设置，这里补 x-api-key）</summary>
    private static void Configure(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(ApiKey))
            req.Headers.Add("x-api-key", ApiKey);
    }

    /// <summary>统一请求入口：把 401/403 转换为可操作的中文提示（镜像失效或需要官方密钥）</summary>
    private static async Task<JsonDocument> CfJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            return await RemoteHttp.GetJsonAsync(url, Configure, ct);
        }
        catch (RemoteRequestException ex) when (ex.StatusCode is 401 or 403)
        {
            throw new RemoteRequestException(
                "CurseForge 接口拒绝访问（HTTP " + ex.StatusCode + "）：社区镜像可能暂不可用。" +
                "可设置 CurseForgeClient.ApiKey 为官方 API Key（console.curseforge.com 申请）以切换官方接口；Modrinth 源不受影响。",
                ex.StatusCode);
        }
    }

    /// <summary>搜索项目。projectType: "mod"|"modpack"；sort: popularity|downloads|updated|name</summary>
    public static async Task<List<RemoteSearchHit>> SearchAsync(string query, string? mcVersion, string? loader,
        string projectType, string sort, int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        // 分页参数钳制（CurseForge pageSize 上限 50）
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);
        var sortField = sort?.ToLowerInvariant() switch
        {
            "popularity" => 2,
            "downloads" => 6,
            "updated" => 3,
            "name" => 4,
            _ => 2
        };
        var classId = projectType switch
        {
            "mod" => 4,
            "modpack" => 4471,
            _ => 0
        };
        var url = $"{BaseUrl}/mods/search?gameId={GameId}" +
                  $"&searchFilter={Uri.EscapeDataString(query ?? "")}" +
                  $"&sortField={sortField}&sortOrder=desc&pageSize={limit}&index={offset}";
        if (classId > 0) url += $"&classId={classId}";
        if (!string.IsNullOrEmpty(mcVersion)) url += $"&gameVersion={Uri.EscapeDataString(mcVersion)}";
        if (!string.IsNullOrEmpty(loader))
        {
            var loaderType = loader.ToLowerInvariant() switch
            {
                "forge" => 1,
                "fabric" => 4,
                "neoforge" => 6,
                _ => 0
            };
            if (loaderType > 0) url += $"&modLoaderType={loaderType}";
        }

        using var doc = await CfJsonAsync(url, ct);
        var hits = new List<RemoteSearchHit>();
        if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return hits;
        foreach (var d in arr.EnumerateArray())
        {
            // authors 为对象数组，取第一个作者名
            var author = "";
            if (d.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array && authors.GetArrayLength() > 0)
                author = RemoteJson.Str(authors[0], "name");

            hits.Add(new RemoteSearchHit
            {
                Platform = RemotePlatform.CurseForge,
                Id = RemoteJson.Long(d, "id").ToString(),
                Slug = RemoteJson.Str(d, "slug"),
                Name = RemoteJson.Str(d, "name"),
                Author = author,
                Summary = RemoteJson.Str(d, "summary"),
                Downloads = RemoteJson.Long(d, "downloadCount"),
                IconUrl = RemoteJson.Str(RemoteJson.Obj(d, "logo"), "thumbnailUrl"),
                DateModified = RemoteJson.Str(d, "dateModified"),
                ProjectType = projectType
            });
        }
        return hits;
    }

    /// <summary>获取模组全部文件</summary>
    public static async Task<List<RemoteFileInfo>> GetFilesAsync(string modId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/mods/{Uri.EscapeDataString(modId)}/files";
        using var doc = await CfJsonAsync(url, ct);
        var list = new List<RemoteFileInfo>();
        if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var f in arr.EnumerateArray())
        {
            var info = FileInfoFromFile(f, modId);
            if (info != null) list.Add(info);
        }
        return list;
    }

    /// <summary>取匹配 MC 版本（指定加载器时还需匹配加载器）的最新文件（按 fileDate 降序），无匹配返回 null</summary>
    public static async Task<RemoteFileInfo?> GetBestFileAsync(string modId, string mcVersion, string? loader,
        CancellationToken ct = default)
    {
        var files = await GetFilesAsync(modId, ct);
        RemoteFileInfo? best = null;
        var bestDate = DateTime.MinValue;
        foreach (var f in files)
        {
            if (!f.GameVersions.Contains(mcVersion, StringComparer.Ordinal)) continue;
            // CF 的 gameVersions 同时含 "1.20.1"/"Forge" 等，直接要求包含加载器名
            if (!string.IsNullOrEmpty(loader) && !f.GameVersions.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;

            var date = RemoteJson.ParseDate(f.ReleaseDate);
            if (best == null || date > bestDate) { best = f; bestDate = date; }
        }
        return best;
    }

    /// <summary>获取单个文件信息（整合包 manifest 解析用）</summary>
    public static async Task<RemoteFileInfo> GetFileAsync(string modId, string fileId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/mods/{Uri.EscapeDataString(modId)}/file/{Uri.EscapeDataString(fileId)}";
        using var doc = await CfJsonAsync(url, ct);
        if (!doc.RootElement.TryGetProperty("data", out var f))
            throw new RemoteRequestException("远程文件信息不存在或已被删除");
        var info = FileInfoFromFile(f, modId);
        if (info == null)
            throw new RemoteRequestException("远程文件信息不存在或已被删除");
        return info;
    }

    /// <summary>把 CurseForge 文件对象转换为文件信息</summary>
    private static RemoteFileInfo? FileInfoFromFile(JsonElement f, string modId)
    {
        var fileId = RemoteJson.Long(f, "id");
        if (fileId <= 0) return null;

        // downloadUrl 为空时回退构造官网直链
        var url = RemoteJson.Str(f, "downloadUrl");
        if (string.IsNullOrEmpty(url))
            url = $"https://www.curseforge.com/api/v1/mods/{modId}/files/{fileId}/download";

        // hashes 数组：algo 1 = SHA1
        var sha1 = "";
        if (f.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
            foreach (var h in hashes.EnumerateArray())
                if (RemoteJson.Long(h, "algo") == 1) { sha1 = RemoteJson.Str(h, "value"); break; }

        var fileName = RemoteJson.Str(f, "fileName");
        var info = new RemoteFileInfo
        {
            Platform = RemotePlatform.CurseForge,
            ProjectId = modId,
            FileId = fileId.ToString(),
            FileName = fileName,
            DisplayName = RemoteJson.Str(f, "displayName", fileName),
            Url = url,
            Sha1 = sha1,
            SizeBytes = RemoteJson.Long(f, "fileLength"),
            VersionNumber = RemoteJson.Str(f, "displayName", fileName),
            GameVersions = RemoteJson.StrList(f, "gameVersions"),
            ReleaseDate = RemoteJson.Str(f, "fileDate")
        };

        // dependencies 数组：relationType 1 = required
        if (f.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            foreach (var dep in deps.EnumerateArray())
                if (RemoteJson.Long(dep, "relationType") == 1)
                {
                    var depId = RemoteJson.Long(dep, "modId");
                    if (depId > 0) info.RequiredDependencyIds.Add(depId.ToString());
                }
        return info;
    }
}
