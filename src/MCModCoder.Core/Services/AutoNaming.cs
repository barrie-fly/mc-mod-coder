using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MCModCoder.Core.Services;

/// <summary>
/// 上传/保存自动命名引擎：依据文件内容或元数据生成规范统一的名称，
/// 并保证在同一目录下唯一（冲突时自动追加序号/时间后缀）。
/// </summary>
public static class AutoNaming
{
    /// <summary>规范化为安全的标识符风格（小写、下划线分隔、去除非法字符）</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
        {
            if (char.IsWhiteSpace(ch)) sb.Append('_');
            else if (Path.GetInvalidFileNameChars().Contains(ch)) sb.Append('_');
            else sb.Append(ch);
        }
        var s = Regex.Replace(sb.ToString(), @"_{2,}", "_").Trim('_');
        return s.Length == 0 ? "" : s.ToLowerInvariant();
    }

    /// <summary>内容签名哈希（SHA1 前 6 位十六进制）</summary>
    public static string Hash6(string content)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(content ?? ""));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString()[..6];
    }

    /// <summary>
    /// 纹理命名：依据元数据（尺寸）与内容签名（图层名 / 主色等）生成，
    /// 形如 <c>mymod_tex_16x_a1b2c3</c>。
    /// </summary>
    public static string ForTexture(int width, int height, string contentSignature, string? modid = null)
    {
        string prefix = Sanitize(modid);
        if (prefix.Length == 0) prefix = "tex";
        return $"{prefix}_tex_{width}x_{Hash6(contentSignature)}";
    }

    /// <summary>
    /// 代码片段命名：分析代码内容提取类名 / 方法名，
    /// 形如 <c>ExampleEvents_onRightClickBlock</c>；无法识别时回退 <c>snippet_哈希</c>。
    /// </summary>
    public static string ForSnippet(string code)
    {
        var cls = Regex.Match(code ?? "", @"\b(?:class|interface|enum|record)\s+(\w+)");
        var method = Regex.Match(code ?? "", @"\b(?:void|public|private|protected|static|final)\s+[\w<>\[\],\s]*?(\w+)\s*\(");
        if (cls.Success)
        {
            var clsName = cls.Groups[1].Value;
            var m = method.Success ? method.Groups[1].Value : "";
            if (m.Length > 0 && !string.Equals(m, clsName, StringComparison.Ordinal))
                return $"{clsName}_{Sanitize(m)}";
            return clsName;
        }

        // 无类声明：取第一行有效代码的关键调用
        var firstLine = (code ?? "").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("@") && !l.StartsWith("*") && !l.StartsWith("/*"));
        if (!string.IsNullOrEmpty(firstLine))
        {
            var call = Regex.Match(firstLine, @"(\w+)\s*\(");
            if (call.Success && !IsKeyword(call.Groups[1].Value))
                return Sanitize(call.Groups[1].Value);
            var ident = Regex.Match(firstLine, @"[A-Za-z_]\w{3,}");
            if (ident.Success) return Sanitize(ident.Value);
        }
        return $"snippet_{Hash6(code ?? "")}";
    }

    /// <summary>带日期的通用命名（光影配置 / 特效等），形如 <c>shader_mymod_20261001</c></summary>
    public static string ForDated(string kind, string? hint = null)
    {
        var parts = new List<string> { Sanitize(kind) };
        var h = Sanitize(hint);
        if (h.Length > 0) parts.Add(h);
        parts.Add(DateTime.Now.ToString("yyyyMMdd"));
        return string.Join("_", parts.Where(p => p.Length > 0));
    }

    /// <summary>保证名称在目录内唯一：冲突时追加 <c>_2</c>、<c>_3</c>…，超限追加时间后缀</summary>
    public static string EnsureUnique(string dir, string baseName, string ext)
    {
        baseName = Sanitize(baseName);
        if (baseName.Length == 0) baseName = "item";
        var candidate = baseName;
        for (int i = 2; i < 100; i++)
        {
            if (!File.Exists(Path.Combine(dir, candidate + ext))) return candidate;
            candidate = $"{baseName}_{i}";
        }
        return $"{baseName}_{DateTime.Now:HHmmss}";
    }

    private static bool IsKeyword(string w) =>
        w is "if" or "for" or "while" or "switch" or "catch" or "return" or "new" or "super" or "this" or "synchronized";
}
