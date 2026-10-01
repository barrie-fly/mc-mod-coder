namespace MCModCoder.Core.Models;

/// <summary>Minecraft 版本信息</summary>
public class McVersionInfo
{
    public string Version { get; init; } = "";
    /// <summary>该版本可用的模组加载器</summary>
    public string[] Loaders { get; init; } = Array.Empty<string>();
    /// <summary>所需的 Java 主版本</summary>
    public string JavaVersion { get; init; } = "8";
    /// <summary>资源包格式（pack.mcmeta 的 pack_format，资源包侧）</summary>
    public int PackFormat { get; init; } = 15;
    /// <summary>数据包格式（数据包侧 pack_format）</summary>
    public int DataPackFormat { get; init; } = 15;
    /// <summary>推荐的 Forge 构建版本（空 = 不支持 Forge）</summary>
    public string ForgeVersion { get; init; } = "";
    /// <summary>推荐的 NeoForge 构建版本（空 = 不支持 NeoForge）</summary>
    public string NeoForgeVersion { get; init; } = "";
    /// <summary>推荐的 Fabric API 版本（空 = 不支持 Fabric）</summary>
    public string FabricApiVersion { get; init; } = "";
    /// <summary>推荐的 Fabric Loader 版本（空时生成器使用默认值）</summary>
    public string FabricLoaderVersion { get; init; } = "";
    public string DataPackDir { get; init; } = "data";       // 1.19.4 之后为 "data"
    public string AssetsDir { get; init; } = "assets";
    public string RecipeNamespace => "minecraft";

    /// <summary>2026 起的年份版本号（26.1 / 26.2 / 26.3 …）</summary>
    public bool IsCalendarVersion => Version.StartsWith("26.");
}

/// <summary>
/// Minecraft 版本与加载器兼容矩阵：覆盖 1.7.10 至当前最新 26.3 的全部可模组化版本。
/// 说明：
///  - 2026 起 Mojang 改用年份版本号（26.1 Tiny Takeover、26.2 Chaos Cubed、26.3 Wilderness Bound），
///    旧 "1.x" 线止于 1.21.11（2025-12）；26.1+ 需要 Java 25。
///  - Forge 无 26.3 构建，且 1.20.5/1.20.6 未发布 Forge，相应版本不含 Forge。
///  - 各加载器推荐构建版本号与 26.x 的 pack_format 为社区公开资料整理的近似值，随官方更新可在此校准。
/// </summary>
public static class McVersions
{
    public static readonly McVersionInfo[] Supported =
    {
        // ---------- 1.x 早期（Java 8） ----------
        new() { Version = "1.7.10", Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 1,  DataPackFormat = 1,  ForgeVersion = "10.13.4.1614" },
        new() { Version = "1.8.9",  Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 1,  DataPackFormat = 1,  ForgeVersion = "11.15.1.2318" },
        new() { Version = "1.9.4",  Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 2,  DataPackFormat = 2,  ForgeVersion = "12.17.0.2317" },
        new() { Version = "1.10.2", Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 2,  DataPackFormat = 2,  ForgeVersion = "12.18.3.2511" },
        new() { Version = "1.11.2", Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 3,  DataPackFormat = 3,  ForgeVersion = "13.20.1.2588" },
        new() { Version = "1.12.2", Loaders = new[] { "Forge", "Rift" }, JavaVersion = "8",  PackFormat = 3,  DataPackFormat = 3,  ForgeVersion = "14.23.5.2859" },
        new() { Version = "1.13.2", Loaders = new[] { "Forge" }, JavaVersion = "8",  PackFormat = 4,  DataPackFormat = 4,  ForgeVersion = "25.0.223" },
        new() { Version = "1.14.4", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "8",  PackFormat = 4,  DataPackFormat = 4,  ForgeVersion = "28.2.26",     FabricApiVersion = "0.28.5+1.14" },
        new() { Version = "1.15.2", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "8",  PackFormat = 5,  DataPackFormat = 5,  ForgeVersion = "31.2.57",     FabricApiVersion = "0.34.9+1.15" },
        new() { Version = "1.16.1", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "8",  PackFormat = 5,  DataPackFormat = 5,  ForgeVersion = "32.0.108",    FabricApiVersion = "0.34.1+1.16" },
        new() { Version = "1.16.5", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "8",  PackFormat = 6,  DataPackFormat = 6,  ForgeVersion = "36.2.42",     FabricApiVersion = "0.42.0+1.16" },
        // ---------- 1.17 – 1.20.4（Java 16/17） ----------
        new() { Version = "1.17.1", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "16", PackFormat = 7,  DataPackFormat = 7,  ForgeVersion = "37.1.1",      FabricApiVersion = "0.46.1+1.17" },
        new() { Version = "1.18.1", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 8,  DataPackFormat = 8,  ForgeVersion = "39.1.2",      FabricApiVersion = "0.75.1+1.18.1" },
        new() { Version = "1.18.2", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 8,  DataPackFormat = 9,  ForgeVersion = "40.2.21",     FabricApiVersion = "0.77.0+1.18.2" },
        new() { Version = "1.19",   Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 9,  DataPackFormat = 10, ForgeVersion = "41.1.0",      FabricApiVersion = "0.81.0+1.19" },
        new() { Version = "1.19.2", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 9,  DataPackFormat = 10, ForgeVersion = "43.3.13",     FabricApiVersion = "0.77.0+1.19.2" },
        new() { Version = "1.19.3", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 12, DataPackFormat = 10, ForgeVersion = "44.1.0",      FabricApiVersion = "0.76.0+1.19.3" },
        new() { Version = "1.19.4", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 13, DataPackFormat = 12, ForgeVersion = "45.3.0",      FabricApiVersion = "0.87.2+1.19.4" },
        new() { Version = "1.20",   Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 15, DataPackFormat = 15, ForgeVersion = "46.0.14",     FabricApiVersion = "0.86.1+1.20" },
        new() { Version = "1.20.1", Loaders = new[] { "Forge", "Fabric", "NeoForge" }, JavaVersion = "17", PackFormat = 15, DataPackFormat = 15, ForgeVersion = "47.3.0", NeoForgeVersion = "47.1.106", FabricApiVersion = "0.97.8+1.20.1" },
        new() { Version = "1.20.2", Loaders = new[] { "Forge", "Fabric", "NeoForge" }, JavaVersion = "17", PackFormat = 18, DataPackFormat = 18, ForgeVersion = "48.1.0", NeoForgeVersion = "20.2.88",  FabricApiVersion = "0.91.6+1.20.2" },
        new() { Version = "1.20.3", Loaders = new[] { "Forge", "Fabric" }, JavaVersion = "17", PackFormat = 22, DataPackFormat = 26, ForgeVersion = "49.0.16",     FabricApiVersion = "0.91.1+1.20.3" },
        new() { Version = "1.20.4", Loaders = new[] { "Forge", "Fabric", "NeoForge" }, JavaVersion = "17", PackFormat = 22, DataPackFormat = 26, ForgeVersion = "49.0.50", NeoForgeVersion = "20.4.237", FabricApiVersion = "0.97.2+1.20.4" },
        // ---------- 1.20.5 – 1.21.11（Java 21；1.20.5 起 Forge 停更，NeoForge 接棒） ----------
        new() { Version = "1.20.5", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 32, DataPackFormat = 41, NeoForgeVersion = "20.5.19-beta", FabricApiVersion = "0.97.8+1.20.5" },
        new() { Version = "1.20.6", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 32, DataPackFormat = 41, NeoForgeVersion = "20.6.119", FabricApiVersion = "0.100.8+1.20.6" },
        new() { Version = "1.21",   Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 34, DataPackFormat = 48, NeoForgeVersion = "21.0.167", FabricApiVersion = "0.102.0+1.21" },
        new() { Version = "1.21.1", Loaders = new[] { "Forge", "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 34, DataPackFormat = 48, ForgeVersion = "52.0.63", NeoForgeVersion = "21.1.77", FabricApiVersion = "0.115.6+1.21.1" },
        new() { Version = "1.21.2", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 42, DataPackFormat = 57, NeoForgeVersion = "21.2.3",   FabricApiVersion = "0.106.1+1.21.2" },
        new() { Version = "1.21.3", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 42, DataPackFormat = 57, NeoForgeVersion = "21.3.56",  FabricApiVersion = "0.106.1+1.21.3" },
        new() { Version = "1.21.4", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 46, DataPackFormat = 61, NeoForgeVersion = "21.4.141", FabricApiVersion = "0.119.2+1.21.4" },
        new() { Version = "1.21.5", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 55, DataPackFormat = 71, NeoForgeVersion = "21.5.95",  FabricApiVersion = "0.126.0+1.21.5" },
        new() { Version = "1.21.6", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 63, DataPackFormat = 80, NeoForgeVersion = "21.6.24",  FabricApiVersion = "0.129.0+1.21.6" },
        new() { Version = "1.21.7", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 64, DataPackFormat = 81, NeoForgeVersion = "21.7.23",  FabricApiVersion = "0.131.0+1.21.7" },
        new() { Version = "1.21.8", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 64, DataPackFormat = 81, NeoForgeVersion = "21.8.47",  FabricApiVersion = "0.133.0+1.21.8" },
        new() { Version = "1.21.9", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 69, DataPackFormat = 88, NeoForgeVersion = "21.9.53",  FabricApiVersion = "0.134.0+1.21.9" },
        new() { Version = "1.21.10", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 69, DataPackFormat = 88, NeoForgeVersion = "21.10.5", FabricApiVersion = "0.135.0+1.21.10" },
        new() { Version = "1.21.11", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "21", PackFormat = 69, DataPackFormat = 88, NeoForgeVersion = "21.11.5", FabricApiVersion = "0.136.0+1.21.11" },
        // ---------- 26.x（2026 年份版本号，Java 25；Forge 尚无构建） ----------
        new() { Version = "26.1", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "25", PackFormat = 101, DataPackFormat = 90, NeoForgeVersion = "26.1.0",      FabricApiVersion = "0.140.0+26.1", FabricLoaderVersion = "0.19.3" },
        new() { Version = "26.2", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "25", PackFormat = 111, DataPackFormat = 94, NeoForgeVersion = "26.2.4",      FabricApiVersion = "0.141.0+26.2", FabricLoaderVersion = "0.19.3" },
        new() { Version = "26.3", Loaders = new[] { "Fabric", "NeoForge" }, JavaVersion = "25", PackFormat = 121, DataPackFormat = 97, NeoForgeVersion = "26.3.0-beta", FabricApiVersion = "0.142.0+26.3", FabricLoaderVersion = "0.19.3" },
    };

    public static McVersionInfo? Find(string version) =>
        Supported.FirstOrDefault(v => v.Version == version);

    public static string[] LoadersFor(string version) =>
        Find(version)?.Loaders ?? new[] { "Forge" };
}
