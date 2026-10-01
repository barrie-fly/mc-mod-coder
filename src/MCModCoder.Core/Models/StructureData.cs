namespace MCModCoder.Core.Models;

/// <summary>多方块结构数据</summary>
public class StructureData
{
    public string Name { get; set; } = "NewStructure";
    public int SizeX { get; set; } = 8;
    public int SizeY { get; set; } = 8;
    public int SizeZ { get; set; } = 8;
    /// <summary>调色板：索引 → 方块 ID</summary>
    public List<string> Palette { get; set; } = new() { "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:oak_planks", "minecraft:glass", "minecraft:oak_log", "minecraft:bricks", "minecraft:iron_block", "minecraft:gold_block", "minecraft:diamond_block", "minecraft:glowstone", "minecraft:water" };
    /// <summary>坐标 "x,y,z" → 调色板索引</summary>
    public Dictionary<string, int> Blocks { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public int BlockCount => Blocks.Values.Count(v => v != 0);

    public static string Key(int x, int y, int z) => $"{x},{y},{z}";

    public int Get(int x, int y, int z) =>
        Blocks.TryGetValue(Key(x, y, z), out var v) ? v : 0;

    public void Set(int x, int y, int z, int paletteIndex)
    {
        var k = Key(x, y, z);
        if (paletteIndex <= 0) Blocks.Remove(k);
        else Blocks[k] = paletteIndex;
    }

    public void EnsureSize(int x, int y, int z)
    {
        SizeX = Math.Max(SizeX, x + 1);
        SizeY = Math.Max(SizeY, y + 1);
        SizeZ = Math.Max(SizeZ, z + 1);
    }

    /// <summary>解析 "x,y,z" 键；格式损坏返回 false（容错跳过，不抛异常）</summary>
    private static bool TryParseKey(string key, out int x, out int y, out int z)
    {
        x = y = z = 0;
        if (string.IsNullOrEmpty(key)) return false;
        var p = key.Split(',');
        if (p.Length != 3) return false;
        return int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out z);
    }

    /// <summary>绕 Y 轴顺时针旋转 90°</summary>
    public void RotateY()
    {
        var old = new Dictionary<string, int>(Blocks);
        Blocks.Clear();
        foreach (var (k, v) in old)
        {
            if (!TryParseKey(k, out int x, out int y, out int z)) continue;
            int nx = SizeZ - 1 - z, nz = x;
            Blocks[Key(nx, y, nz)] = v;
        }
        (SizeX, SizeZ) = (SizeZ, SizeX);
    }

    /// <summary>绕 X 轴俯仰旋转 90°</summary>
    public void RotateX()
    {
        var old = new Dictionary<string, int>(Blocks);
        Blocks.Clear();
        foreach (var (k, v) in old)
        {
            if (!TryParseKey(k, out int x, out int y, out int z)) continue;
            int ny = z, nz = SizeY - 1 - y;
            Blocks[Key(x, ny, nz)] = v;
        }
        (SizeY, SizeZ) = (SizeZ, SizeY);
    }

    /// <summary>绕 Z 轴滚转旋转 90°</summary>
    public void RotateZ()
    {
        var old = new Dictionary<string, int>(Blocks);
        Blocks.Clear();
        foreach (var (k, v) in old)
        {
            if (!TryParseKey(k, out int x, out int y, out int z)) continue;
            int nx = SizeY - 1 - y, ny = x;
            Blocks[Key(nx, ny, z)] = v;
        }
        (SizeX, SizeY) = (SizeY, SizeX);
    }

    /// <summary>沿 X 轴镜像</summary>
    public void MirrorX()
    {
        var old = new Dictionary<string, int>(Blocks);
        Blocks.Clear();
        foreach (var (k, v) in old)
        {
            if (!TryParseKey(k, out int x, out int y, out int z)) continue;
            Blocks[Key(SizeX - 1 - x, y, z)] = v;
        }
    }

    /// <summary>沿 Z 轴镜像</summary>
    public void MirrorZ()
    {
        var old = new Dictionary<string, int>(Blocks);
        Blocks.Clear();
        foreach (var (k, v) in old)
        {
            if (!TryParseKey(k, out int x, out int y, out int z)) continue;
            Blocks[Key(x, y, SizeZ - 1 - z)] = v;
        }
    }

    public StructureData Clone() => new()
    {
        Name = Name + "_副本",
        SizeX = SizeX, SizeY = SizeY, SizeZ = SizeZ,
        Palette = new List<string>(Palette),
        Blocks = new Dictionary<string, int>(Blocks)
    };
}
