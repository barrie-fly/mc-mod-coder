using System.Text.Json;

namespace MCModCoder.Core.Remote;

/// <summary>Modrinth API 客户端（v2 接口，JSON 松散解析，字段缺失容错）</summary>
public static class ModrinthClient
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    /// <summary>搜索项目。projectType: "mod"|"modpack"；sort: relevance|downloads|newest|updated</summary>
    public static async Task<List<RemoteSearchHit>> SearchAsync(string query, string? mcVersion, string? loader,
        string projectType, string sort, int limit = 20, int offset = 0, CancellationToken ct = default)
    {
        // 分页参数钳制，防止异常入参触发接口限流
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        // facets 为 JSON 数组的数组，整体 URL 编码后拼进 query string
        var facets = new List<List<string>> { new() { $"project_type:{projectType}" } };
        if (!string.IsNullOrEmpty(mcVersion)) facets.Add(new() { $"versions:{mcVersion}" });
        if (!string.IsNullOrEmpty(loader)) facets.Add(new() { $"categories:{loader.ToLowerInvariant()}" });
        var facetsJson = JsonSerializer.Serialize(facets);

        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query ?? "")}" +
                  $"&limit={limit}&offset={offset}&index={Uri.EscapeDataString(sort)}" +
                  $"&facets={Uri.EscapeDataString(facetsJson)}";

        using var doc = await RemoteHttp.GetJsonAsync(url, null, ct);
        var hits = new List<RemoteSearchHit>();
        if (!doc.RootElement.TryGetProperty("hits", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return hits;
        foreach (var h in arr.EnumerateArray())
        {
            hits.Add(new RemoteSearchHit
            {
                Platform = RemotePlatform.Modrinth,
                Id = RemoteJson.Str(h, "project_id"),
                ProjectType = RemoteJson.Str(h, "project_type", projectType),
                Slug = RemoteJson.Str(h, "slug"),
                Author = RemoteJson.Str(h, "author"),
                Name = RemoteJson.Str(h, "title"),
                Summary = RemoteJson.Str(h, "description"),
                Categories = RemoteJson.StrList(h, "categories"),
                Downloads = RemoteJson.Long(h, "downloads"),
                IconUrl = RemoteJson.Str(h, "icon_url"),
                DateModified = RemoteJson.Str(h, "date_modified")
            });
        }
        return hits;
    }

    /// <summary>获取项目全部版本（每版本取 primary 文件，无则取第一个文件）</summary>
    public static async Task<List<RemoteFileInfo>> GetVersionsAsync(string projectIdOrSlug, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        using var doc = await RemoteHttp.GetJsonAsync(url, null, ct);
        var list = new List<RemoteFileInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var info = FileInfoFromVersion(v, projectIdOrSlug);
            if (info != null) list.Add(info);
        }
        return list;
    }

    /// <summary>取匹配 MC 版本与加载器的最新版本文件（按 date_published 降序取第一个），无匹配返回 null</summary>
    public static async Task<RemoteFileInfo?> GetBestVersionAsync(string projectIdOrSlug, string mcVersion, string loader,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        using var doc = await RemoteHttp.GetJsonAsync(url, null, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        RemoteFileInfo? best = null;
        var bestDate = DateTime.MinValue;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var gameVersions = RemoteJson.StrList(v, "game_versions");
            if (!gameVersions.Contains(mcVersion, StringComparer.Ordinal)) continue;
            var loaders = RemoteJson.StrList(v, "loaders");
            if (!loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;

            var date = RemoteJson.ParseDate(RemoteJson.Str(v, "date_published"));
            if (best != null && date <= bestDate) continue;
            var info = FileInfoFromVersion(v, projectIdOrSlug);
            if (info == null) continue;
            best = info;
            bestDate = date;
        }
        return best;
    }

    /// <summary>解析项目的全部 required 依赖（递归，深度上限 3，按 project id 去重防环），不含根项目自身的文件</summary>
    public static async Task<List<RemoteFileInfo>> ResolveDependenciesAsync(string projectId, string mcVersion, string loader,
        CancellationToken ct = default)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RemoteFileInfo>();
        await ResolveDependenciesRecursiveAsync(projectId, mcVersion, loader, 3, visited, result, ct);
        return result;
    }

    private static async Task ResolveDependenciesRecursiveAsync(string projectId, string mcVersion, string loader,
        int depth, HashSet<string> visited, List<RemoteFileInfo> result, CancellationToken ct)
    {
        if (depth <= 0) return;
        if (!visited.Add(projectId)) return; // 去重防环

        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/dependencies";
        using var doc = await RemoteHttp.GetJsonAsync(url, null, ct);
        var root = doc.RootElement;
        var projects = RemoteJson.Obj(root, "projects");
        var versions = RemoteJson.Obj(root, "versions");
        if (projects.ValueKind != JsonValueKind.Array) return;

        // 从依赖版本对象的 dependencies 中收集 required 依赖的 project id
        var requiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (versions.ValueKind == JsonValueKind.Array)
            foreach (var ver in versions.EnumerateArray())
                if (ver.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
                    foreach (var dep in deps.EnumerateArray())
                        if (string.Equals(RemoteJson.Str(dep, "dependency_type"), "required", StringComparison.OrdinalIgnoreCase))
                        {
                            var pid = RemoteJson.Str(dep, "project_id");
                            if (pid.Length > 0) requiredIds.Add(pid);
                        }
        // 字段缺失容错：识别不出 required 标记时，视全部项目为 required
        var treatAllAsRequired = requiredIds.Count == 0;

        foreach (var proj in projects.EnumerateArray())
        {
            var pid = RemoteJson.Str(proj, "id");
            if (pid.Length == 0) continue;
            if (!treatAllAsRequired && !requiredIds.Contains(pid)) continue;
            if (RemoteJson.Str(proj, "project_type") != "mod") continue;
            if (visited.Contains(pid)) continue;

            var file = await GetBestVersionAsync(pid, mcVersion, loader, ct);
            if (file != null) result.Add(file);

            // 递归解析该依赖项目自身的依赖（深度上限 3）
            await ResolveDependenciesRecursiveAsync(pid, mcVersion, loader, depth - 1, visited, result, ct);
        }
    }

    /// <summary>把 Modrinth 版本对象转换为文件信息（取 primary 文件，无则取第一个）</summary>
    private static RemoteFileInfo? FileInfoFromVersion(JsonElement v, string projectId)
    {
        JsonElement file = default;
        bool found = false;
        if (v.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (!found) { file = f; found = true; }
                if (RemoteJson.Bool(f, "primary")) { file = f; break; }
            }
        }
        if (!found) return null;

        return new RemoteFileInfo
        {
            Platform = RemotePlatform.Modrinth,
            ProjectId = projectId,
            FileId = RemoteJson.Str(v, "id"),
            FileName = RemoteJson.Str(file, "filename"),
            DisplayName = RemoteJson.Str(file, "filename"),
            Url = RemoteJson.Str(file, "url"),
            Sha1 = RemoteJson.Str(RemoteJson.Obj(file, "hashes"), "sha1"),
            SizeBytes = RemoteJson.Long(file, "size"),
            VersionNumber = RemoteJson.Str(v, "version_number"),
            GameVersions = RemoteJson.StrList(v, "game_versions"),
            Loaders = RemoteJson.StrList(v, "loaders"),
            ReleaseDate = RemoteJson.Str(v, "date_published"),
            IsPrimary = RemoteJson.Bool(file, "primary"),
            RequiredDependencyIds = new()
        };
    }
}
