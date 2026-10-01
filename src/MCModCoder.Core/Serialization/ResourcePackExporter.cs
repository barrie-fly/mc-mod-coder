using System.IO.Compression;
using System.Text.Json.Nodes;
using MCModCoder.Core.Models;

namespace MCModCoder.Core.Serialization;

/// <summary>资源包结构自动生成 / 导出</summary>
public static class ResourcePackExporter
{
    /// <summary>
    /// 生成资源包目录结构。
    /// </summary>
    /// <param name="outputRoot">输出根目录</param>
    /// <param name="nameSpace">命名空间（模组 ID）</param>
    /// <param name="packFormat">资源包格式</param>
    /// <param name="textures">纹理列表（相对路径如 item/ruby，PNG 字节）</param>
    /// <param name="blockModels">方块模型（路径→模型 JSON）</param>
    public static List<string> Generate(string outputRoot, string nameSpace, int packFormat,
        IEnumerable<(string RelPath, byte[] Png)> textures,
        IEnumerable<(string RelPath, string Json)>? blockModels = null)
    {
        var written = new List<string>();
        Directory.CreateDirectory(outputRoot);

        // pack.mcmeta
        var meta = Path.Combine(outputRoot, "pack.mcmeta");
        File.WriteAllText(meta, $$"""{ "pack": { "pack_format": {{packFormat}}, "description": "由MC模组代码工作台生成的资源包" } }""");
        written.Add(meta);
        File.WriteAllBytes(Path.Combine(outputRoot, "pack.png"), WriteablePngPlaceholder());

        string assets = Path.Combine(outputRoot, "assets", nameSpace);
        foreach (var (relRaw, png) in textures)
        {
            // 边界防御：空路径 / 含 ".." 的路径直接跳过，防止写出输出目录
            var rel = (relRaw ?? "").Replace('\\', '/').Trim('/');
            if (rel.Length == 0 || rel.Split('/').Contains("..")) continue;
            var pngPath = Path.Combine(assets, "textures", rel.Replace('/', Path.DirectorySeparatorChar) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
            File.WriteAllBytes(pngPath, png);
            written.Add(pngPath);

            // 物品自动生成 item 模型（16×16 视为物品；其余不生成，由 blockModels 提供）
            // 需要存在目录分隔符（item/xxx），避免 LastIndexOf('/') 越界
            if ((rel.StartsWith("item/") || rel.StartsWith("block/") && blockModels == null) && rel.LastIndexOf('/') > 0)
            {
                var name = Path.GetFileNameWithoutExtension(rel);
                var modelPath = Path.Combine(assets, "models", rel.Replace('/', Path.DirectorySeparatorChar) + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                if (!File.Exists(modelPath))
                {
                    var parent = rel.StartsWith("item/") ? "item/generated" : "block/cube_all";
                    var layer0 = $"{nameSpace}:textures/{rel}";
                    var json = parent == "block/cube_all"
                        ? $$"""{ "parent": "block/cube_all", "textures": { "all": "{{layer0}}" } }"""
                        : $$"""{ "parent": "item/generated", "textures": { "layer0": "{{layer0}}" } }""";
                    File.WriteAllText(modelPath, json);
                    written.Add(modelPath);
                }
            }
        }

        if (blockModels != null)
            foreach (var (rel, json) in blockModels)
            {
                var file = Path.Combine(assets, "models", rel.Replace('/', Path.DirectorySeparatorChar) + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json);
                written.Add(file);
            }

        return written;
    }

    /// <summary>将资源包目录打包为 .zip（可直接放入 resourcepacks）</summary>
    public static string PackAsZip(string outputRoot, string zipPath)
    {
        if (!Directory.Exists(outputRoot))
            throw new DirectoryNotFoundException($"资源包目录不存在：{outputRoot}");
        var zipFull = Path.GetFullPath(zipPath);
        // 防止把输出目录自身打包进自身（zip 会落在 outputRoot 内时出问题）
        if (zipFull.StartsWith(Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("zip 输出路径不能位于资源包目录内部。");
        if (File.Exists(zipFull)) File.Delete(zipFull);
        ZipFile.CreateFromDirectory(outputRoot, zipFull);
        return zipPath;
    }

    private static byte[] WriteablePngPlaceholder()
    {
        // 1×1 半透明灰 PNG（内置 base64）
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABh6FO1AAAAABJRU5ErkJggg==");
    }
}
