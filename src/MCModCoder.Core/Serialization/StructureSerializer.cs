using System.Text.Json;
using System.Text.Json.Serialization;
using MCModCoder.Core.Models;

namespace MCModCoder.Core.Serialization;

/// <summary>结构序列化 / 反序列化（工作台 .mcstructure.json 格式）</summary>
public static class StructureSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static void Save(StructureData s, string file)
    {
        var json = JsonSerializer.Serialize(s, Opts);
        // 临时文件 + 替换：写入中断不会损坏已有存档
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, file, true);
    }

    public static StructureData? Load(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<StructureData>(File.ReadAllText(file), Opts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导出为 Minecraft 原版结构方块兼容的文本描述（调色板 + 索引），便于核对</summary>
    public static string ExportPaletteCsv(StructureData s)
    {
        var lines = new List<string> { "index,block" };
        for (int i = 0; i < s.Palette.Count; i++)
            lines.Add($"{i},{s.Palette[i]}");
        return string.Join('\n', lines);
    }

    /// <summary>生成结构放置指令（/place template），名称净化为合法资源路径</summary>
    public static string GeneratePlaceCommand(StructureData s, string nameSpace = "mymod")
    {
        var path = SanitizePath(s.Name);
        return $"/place template {nameSpace}:{path} ~ ~ ~";
    }

    private static string SanitizePath(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (string.IsNullOrWhiteSpace(s) ? "structure" : s).ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_' ? c : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "structure" : r;
    }
}
