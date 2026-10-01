using System.IO.Compression;
using System.Text.Json;

namespace MCModCoder.Core.Remote;

/// <summary>整合包内单个待下载文件</summary>
public class ModpackFileEntry
{
    public string RelativePath { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>来源平台："Modrinth" / "CurseForge"</summary>
    public string Source { get; set; } = "";
}

/// <summary>整合包解析结果（下载计划）</summary>
public class ModpackPlan
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public RemotePlatform Platform { get; set; }
    public string PackPath { get; set; } = "";
    public List<ModpackFileEntry> Files { get; set; } = new();
}

/// <summary>整合包解析器（支持 Modrinth .mrpack 与 CurseForge zip，两种格式均用 overrides/ 约定）</summary>
public static class ModpackParser
{
    /// <summary>解析 Modrinth 整合包（modrinth.index.json），格式不符或损坏返回 null</summary>
    public static ModpackPlan? ParseMrpack(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("modrinth.index.json");
            if (entry == null) return null;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var plan = new ModpackPlan
            {
                Platform = RemotePlatform.Modrinth,
                PackPath = zipPath,
                Name = RemoteJson.Str(root, "name", Path.GetFileNameWithoutExtension(zipPath))
            };

            // dependencies 对象：minecraft 键 → MC 版本；加载器键存在即确定加载器
            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in deps.EnumerateObject())
                {
                    if (p.NameEquals("minecraft")) plan.McVersion = RemoteJson.Str(deps, "minecraft");
                    else if (p.NameEquals("forge")) plan.Loader = "Forge";
                    else if (p.NameEquals("neoforge")) plan.Loader = "NeoForge";
                    else if (p.NameEquals("fabric-loader")) plan.Loader = "Fabric";
                    else if (p.NameEquals("quilt-loader")) plan.Loader = "Quilt";
                }
            }

            // files 数组：path / hashes.sha1 / size / downloads[0]
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    var e = new ModpackFileEntry
                    {
                        Source = "Modrinth",
                        RelativePath = RemoteJson.Str(f, "path"),
                        Sha1 = RemoteJson.Str(RemoteJson.Obj(f, "hashes"), "sha1"),
                        SizeBytes = RemoteJson.Long(f, "size")
                    };
                    if (f.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Array
                        && dl.GetArrayLength() > 0 && dl[0].ValueKind == JsonValueKind.String)
                        e.Url = dl[0].GetString() ?? "";
                    if (e.Url.Length > 0 && e.RelativePath.Length > 0)
                        plan.Files.Add(e);
                }
            }
            return plan;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 CurseForge 整合包（manifest.json），格式不符或损坏返回 null。
    /// 文件下载信息需逐个联网获取，网络错误以 RemoteRequestException 向上抛出。
    /// </summary>
    public static async Task<ModpackPlan?> ParseCurseForgeAsync(string zipPath, CancellationToken ct = default)
    {
        var plan = new ModpackPlan();
        var wanted = new List<(long ProjectId, long FileId)>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("manifest.json");
            if (entry == null) return null;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            plan.Platform = RemotePlatform.CurseForge;
            plan.PackPath = zipPath;
            plan.Name = RemoteJson.Str(root, "name", Path.GetFileNameWithoutExtension(zipPath));

            // minecraft.version 与 minecraft.modLoaders[].id（"forge-47.2.0" → Forge 等）
            if (root.TryGetProperty("minecraft", out var mc))
            {
                plan.McVersion = RemoteJson.Str(mc, "version");
                if (mc.TryGetProperty("modLoaders", out var mls) && mls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ml in mls.EnumerateArray())
                    {
                        var id = RemoteJson.Str(ml, "id").ToLowerInvariant();
                        if (id.StartsWith("neoforge-", StringComparison.Ordinal)) { plan.Loader = "NeoForge"; break; }
                        if (id.StartsWith("forge-", StringComparison.Ordinal)) { plan.Loader = "Forge"; break; }
                        if (id.StartsWith("fabric-", StringComparison.Ordinal)) { plan.Loader = "Fabric"; break; }
                    }
                }
            }

            // files 数组：projectID / fileID / required
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    var projectId = RemoteJson.Long(f, "projectID");
                    var fileId = RemoteJson.Long(f, "fileID");
                    var required = RemoteJson.Bool(f, "required", true);
                    if (required && projectId > 0 && fileId > 0)
                        wanted.Add((projectId, fileId));
                }
            }
        }
        catch
        {
            return null;
        }

        // 逐个获取文件下载信息
        foreach (var (projectId, fileId) in wanted)
        {
            ct.ThrowIfCancellationRequested();
            var info = await CurseForgeClient.GetFileAsync(projectId.ToString(), fileId.ToString(), ct);
            plan.Files.Add(new ModpackFileEntry
            {
                Source = "CurseForge",
                RelativePath = "mods/" + info.FileName,
                Url = info.Url,
                Sha1 = info.Sha1,
                SizeBytes = info.SizeBytes
            });
        }
        return plan;
    }

    /// <summary>解压整合包内 overrides/ 前缀条目到目标目录（去前缀、覆盖已存在文件、防 Zip-Slip）</summary>
    public static void ExtractOverrides(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var targetRoot = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetRoot);

        foreach (var entry in zip.Entries)
        {
            var fullName = entry.FullName;
            if (string.IsNullOrEmpty(fullName)) continue;
            if (!fullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) continue;
            if (fullName.EndsWith("/", StringComparison.Ordinal)) continue; // 跳过目录条目

            var relative = fullName["overrides/".Length..].Replace('/', Path.DirectorySeparatorChar);
            if (relative.Length == 0) continue;

            var dest = Path.GetFullPath(Path.Combine(targetRoot, relative));
            // 防 Zip-Slip：解压目标必须位于 targetDir 内
            if (!dest.StartsWith(targetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            entry.ExtractToFile(dest, true); // 已存在文件覆盖
        }
    }
}
