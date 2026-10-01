namespace MCModCoder.Core;

/// <summary>十六进制颜色解析（无 UI 依赖）</summary>
public static class ColorUtil
{
    public static (byte R, byte G, byte B) ParseHex(string hex)
    {
        hex = (hex ?? "#FFFFFF").TrimStart('#');
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        if (hex.Length >= 6 && byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return (r, g, b);
        return (255, 255, 255);
    }

    /// <summary>在两个颜色间线性插值，t ∈ [0,1]</summary>
    public static (byte R, byte G, byte B) Lerp(string from, string to, double t)
    {
        var a = ParseHex(from); var b = ParseHex(to);
        t = Math.Clamp(t, 0, 1);
        return ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
