namespace MCModCoder.Core.Models;

/// <summary>常用生物群系预设</summary>
public static class Biomes
{
    public static readonly (string Id, string Cn, string Color)[] All =
    {
        ("minecraft:plains",             "平原",       "#91BD59"),
        ("minecraft:forest",             "森林",       "#79C05A"),
        ("minecraft:desert",             "沙漠",       "#FBF5A2"),
        ("minecraft:snowy_plains",       "雪原",       "#FFFFFF"),
        ("minecraft:taiga",              "针叶林",     "#86B783"),
        ("minecraft:jungle",             "丛林",       "#59C93C"),
        ("minecraft:savanna",            "热带草原",   "#BFB755"),
        ("minecraft:swamp",              "沼泽",       "#6A7039"),
        ("minecraft:ocean",              "海洋",       "#3F76E4"),
        ("minecraft:beach",              "沙滩",       "#FAF0BE"),
        ("minecraft:mountains",          "山地",       "#8F8F8F"),
        ("minecraft:badlands",           "恶地",       "#D9A45C"),
        ("minecraft:mushroom_fields",    "蘑菇岛",     "#55C1AA"),
        ("minecraft:cherry_grove",       "樱花树林",   "#F7B4CE"),
    };

    public static string ColorOf(int index) => index >= 0 && index < All.Length ? All[index].Color : "#888888";
    public static string NameOf(int index) => index >= 0 && index < All.Length ? All[index].Cn : "未知";
    public static string IdOf(int index) => index >= 0 && index < All.Length ? All[index].Id : "minecraft:plains";
}

/// <summary>矿物分布配置</summary>
public class OreConfig
{
    public string Block { get; set; } = "minecraft:iron_ore";
    public string CnName { get; set; } = "铁矿石";
    public int VeinsPerChunk { get; set; } = 8;
    public int VeinSize { get; set; } = 6;
    public int MinY { get; set; } = -64;
    public int MaxY { get; set; } = 64;
}

/// <summary>地形地图（高度图 + 群系图 + 矿物分布）</summary>
public class TerrainMap
{
    public string Name { get; set; } = "NewWorld";
    public int Size { get; set; } = 128;              // 区域尺寸（方块）
    public int SeaLevel { get; set; } = 63;
    public string Seed { get; set; } = "minecraft";
    public int[] Height { get; set; } = Array.Empty<int>();   // 0..255
    public int[] Biome { get; set; } = Array.Empty<int>();    // 群系索引
    public List<OreConfig> Ores { get; set; } = new()
    {
        new() { Block = "minecraft:coal_ore",    CnName = "煤矿石",   VeinsPerChunk = 20, VeinSize = 12, MinY = 0,  MaxY = 128 },
        new() { Block = "minecraft:iron_ore",    CnName = "铁矿石",   VeinsPerChunk = 12, VeinSize = 8,  MinY = -24, MaxY = 72 },
        new() { Block = "minecraft:gold_ore",    CnName = "金矿石",   VeinsPerChunk = 4,  VeinSize = 6,  MinY = -64, MaxY = 30 },
        new() { Block = "minecraft:diamond_ore", CnName = "钻石矿石", VeinsPerChunk = 2,  VeinSize = 5,  MinY = -64, MaxY = 16 },
        new() { Block = "minecraft:redstone_ore",CnName = "红石矿石", VeinsPerChunk = 6,  VeinSize = 7,  MinY = -64, MaxY = 15 },
        new() { Block = "minecraft:lapis_ore",   CnName = "青金石矿石", VeinsPerChunk = 3, VeinSize = 6, MinY = -64, MaxY = 30 },
        new() { Block = "minecraft:emerald_ore", CnName = "绿宝石矿石", VeinsPerChunk = 1, VeinSize = 3, MinY = 4,  MaxY = 100 },
    };

    public void InitArrays()
    {
        Size = Math.Clamp(Size, 16, 1024);   // 防御非法尺寸（含手工编辑的存档文件）
        Height = new int[Size * Size];
        Biome = new int[Size * Size];
        for (int i = 0; i < Height.Length; i++) { Height[i] = SeaLevel; Biome[i] = 0; }
    }

    private bool InBounds(int x, int y) =>
        Height != null && Biome != null && Height.Length >= Size * Size &&
        x >= 0 && y >= 0 && x < Size && y < Size;

    public int H(int x, int y) => InBounds(x, y) ? Height[y * Size + x] : 0;
    public void SetH(int x, int y, int v) { if (InBounds(x, y)) Height[y * Size + x] = Math.Clamp(v, 0, 255); }
    public int B(int x, int y) => InBounds(x, y) ? Biome[y * Size + x] : 0;
    public void SetB(int x, int y, int v) { if (InBounds(x, y)) Biome[y * Size + x] = Math.Clamp(v, 0, Biomes.All.Length - 1); }

    /// <summary>基于种子与群系的简易地形生成（Perlin 风格多层噪声）</summary>
    public void Generate(int baseHeight = 64, int amplitude = 20, int biomeMode = 0)
    {
        InitArrays();
        var rng = new Random(Seed.GetHashCode());
        // 生成 4 层梯度噪声
        var offsets = new (double fx, double fy)[4];
        for (int i = 0; i < 4; i++) offsets[i] = (rng.NextDouble() * 1024, rng.NextDouble() * 1024);

        if (biomeMode == 1) // 温度带状群系（东西向）
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    SetB(x, y, (int)(x / (double)Size * 13 + rng.NextDouble() * 0.6) % 13);
        }

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double h = 0;
                double[] freqs = { 0.008, 0.016, 0.032, 0.064 };
                double[] amps  = { 1.0, 0.5, 0.25, 0.12 };
                for (int i = 0; i < 4; i++)
                    h += Noise2(x * freqs[i] + offsets[i].fx, y * freqs[i] + offsets[i].fy) * amps[i];
                int height = (int)(baseHeight + h * amplitude);
                SetH(x, y, height);
                if (biomeMode == 0)
                {
                    // 依据高度自动选群系
                    SetB(x, y, height < SeaLevel - 4 ? 8 : height < SeaLevel - 1 ? 9
                        : height > 92 ? 10 : height > 84 ? 5 : (x + y) % 17 == 0 ? 1 : 0);
                }
            }
        }
    }

    /// <summary>简易二维值噪声（平滑插值）</summary>
    private static double Noise2(double x, double y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        double a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v; // 双线性 → 映射到 [-1,1]
    }

    private static double Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (double)uint.MaxValue * 2 - 1;
    }
}
