using System.IO;
using System.IO.Compression;
using System.Text;

namespace MCModCoder.Core.Services;

/// <summary>审计结论严重级别（与 RecipeValidator 的 IssueSeverity 语义对齐）</summary>
public enum AiAuditSeverity
{
    /// <summary>提示性信息（如尺寸、颜色统计），无需处理</summary>
    Info = 0,
    /// <summary>警告：结果可能不符合预期，建议人工确认</summary>
    Warning = 1,
    /// <summary>错误：输出无效或已损坏，应阻止其进入后续流程（落盘 / 导入）</summary>
    Error = 2,
}

/// <summary>一条审计结论</summary>
public class AiAuditFinding
{
    public AiAuditSeverity Severity { get; init; }
    /// <summary>机器可读的问题码（如 img.solid / prompt.long / code.balance），便于程序化处理与测试断言</summary>
    public string Code { get; init; } = "";
    /// <summary>人类可读的问题描述（中文）</summary>
    public string Message { get; init; } = "";
    /// <summary>处理建议（中文，可为空）</summary>
    public string Suggestion { get; init; } = "";

    public override string ToString() => $"{Severity} [{Code}] {Message}";
}

/// <summary>一次审计的完整报告</summary>
public class AiAuditReport
{
    /// <summary>审计对象标识（如 "image"、"prompt"、"code:java"）</summary>
    public string Target { get; init; } = "";
    public List<AiAuditFinding> Findings { get; init; } = new();

    public bool HasErrors => Findings.Any(f => f.Severity == AiAuditSeverity.Error);
    public bool HasWarnings => Findings.Any(f => f.Severity == AiAuditSeverity.Warning);
    /// <summary>既无错误也无警告才算完全通过</summary>
    public bool Passed => !HasErrors && !HasWarnings;

    public string Summary
    {
        get
        {
            int e = Findings.Count(f => f.Severity == AiAuditSeverity.Error);
            int w = Findings.Count(f => f.Severity == AiAuditSeverity.Warning);
            int i = Findings.Count(f => f.Severity == AiAuditSeverity.Info);
            if (e > 0) return $"✘ 审计未通过：{e} 条错误、{w} 条警告";
            if (w > 0) return $"⚠ 审计有警告：{w} 条警告、{i} 条提示";
            return $"✔ 审计通过：无问题（{i} 条提示）";
        }
    }

    /// <summary>第一条 Error 的描述；无错误返回空串</summary>
    public string FirstErrorMessage =>
        Findings.FirstOrDefault(f => f.Severity == AiAuditSeverity.Error)?.Message ?? "";
}

/// <summary>PNG 结构与像素统计信息（由 PngInspector 解析得出）</summary>
public class PngInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    /// <summary>PNG 色彩类型（0 灰度 / 2 RGB / 3 调色板 / 4 灰度+Alpha / 6 RGBA）</summary>
    public int ColorType { get; init; }
    public bool Interlaced { get; init; }
    public long FileBytes { get; init; }
    public int ChunkCount { get; init; }
    /// <summary>任一关键数据块（IHDR/PLTE/IDAT/IEND）CRC 校验失败</summary>
    public bool CrcMismatch { get; init; }
    /// <summary>数据流不完整（未读到 IEND 结束块）</summary>
    public bool Truncated { get; init; }
    public bool HasIdat { get; init; }
    public bool HasAlphaChannel { get; init; }
    /// <summary>像素级统计是否可用（编码格式特殊或图过大时跳过）</summary>
    public bool PixelStatsAvailable { get; init; }
    /// <summary>唯一颜色数（抽样统计）</summary>
    public int ColorCount { get; init; }
    /// <summary>出现最多的颜色占全部抽样像素的比例（0~1）</summary>
    public double TopColorRatio { get; init; }
    /// <summary>近全透明像素（alpha &lt; 8）占比</summary>
    public double TransparentRatio { get; init; }
    /// <summary>近纯白像素（R/G/B ≥ 250）占比</summary>
    public double NearWhiteRatio { get; init; }
}

/// <summary>
/// 纯 BCL 的 PNG 检查器：解析 chunk 结构、校验 CRC、解压 IDAT 并做轻量像素统计。
/// 设计约束：<b>绝不抛异常</b>——任何内部失败都返回 null 或尽力而为的部分信息，
/// 由上层审计规则把失败翻译为稳定的 Error/Warning 结论。
/// </summary>
public static class PngInspector
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>像素统计的原始数据量上限（约 96MB，防止超大图占爆内存）</summary>
    private const long MaxRawBytesForStats = 96L * 1024 * 1024;

    /// <summary>签名是否为 PNG（输入为空/过短返回 false，不抛异常）</summary>
    public static bool HasPngSignature(byte[]? data)
    {
        if (data is not { Length: >= 8 }) return false;
        for (int i = 0; i < 8; i++)
            if (data[i] != Signature[i]) return false;
        return true;
    }

    /// <summary>解析 PNG；数据不是 PNG 或结构无法解析时返回 null（不抛异常）</summary>
    public static PngInfo? Inspect(byte[]? data)
    {
        try
        {
            if (!HasPngSignature(data)) return null;

            // ---- 遍历 chunk ----
            int pos = 8, chunkCount = 0;
            bool crcMismatch = false, truncated = false, hasIdat = false;
            int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
            bool hasIhdr = false;
            byte[]? plte = null, trns = null;
            var idatParts = new List<byte[]>();

            while (pos + 8 <= data!.Length)
            {
                int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                int dataStart = pos + 8;
                if (len < 0 || dataStart + (long)len + 4 > data.Length)
                {
                    truncated = true;   // 长度字段指向流外 → 文件被截断
                    break;
                }

                uint expect = (uint)((data[dataStart + len] << 24) | (data[dataStart + len + 1] << 16)
                                   | (data[dataStart + len + 2] << 8) | data[dataStart + len + 3]);
                uint actual = Crc32(data, pos + 4, len + 4);
                bool isCritical = (data[pos + 4] & 0x20) == 0;   // 关键块首字母大写
                if (isCritical && expect != actual) crcMismatch = true;

                switch (type)
                {
                    case "IHDR" when len >= 13:
                        width = ReadInt32(data, dataStart);
                        height = ReadInt32(data, dataStart + 4);
                        bitDepth = data[dataStart + 8];
                        colorType = data[dataStart + 9];
                        interlace = data[dataStart + 12];
                        hasIhdr = true;
                        break;
                    case "PLTE": plte = data[dataStart..(dataStart + len)]; break;
                    case "tRNS": trns = data[dataStart..(dataStart + len)]; break;
                    case "IDAT": hasIdat = true; idatParts.Add(data[dataStart..(dataStart + len)]); break;
                    case "IEND":
                        chunkCount++;
                        pos = data.Length;   // 正常结束
                        goto done;
                }
                chunkCount++;
                pos = dataStart + len + 4;
            }
            // 循环自然耗尽说明从未读到 IEND（IEND 分支会直接 goto done）→ 文件被截断
            truncated = true;

        done:
            if (!hasIhdr) return null;

            bool statsOk = false, hasAlpha = false;
            int colorCount = 0;
            double topRatio = 0, transparentRatio = 0, nearWhiteRatio = 0;

            if (hasIdat && width > 0 && height > 0 && !truncated)
            {
                try
                {
                    var stats = ComputePixelStats(width, height, bitDepth, colorType, interlace,
                        idatParts, plte, trns);
                    if (stats != null)
                    {
                        statsOk = true;
                        hasAlpha = stats.Value.HasAlpha;
                        colorCount = stats.Value.ColorCount;
                        topRatio = stats.Value.TopRatio;
                        transparentRatio = stats.Value.TransparentRatio;
                        nearWhiteRatio = stats.Value.NearWhiteRatio;
                    }
                }
                catch { /* 解压/解码失败：统计不可用，审计层依据其它结论处理 */ }
            }

            return new PngInfo
            {
                Width = width, Height = height,
                BitDepth = bitDepth, ColorType = colorType,
                Interlaced = interlace != 0,
                FileBytes = data.Length, ChunkCount = chunkCount,
                CrcMismatch = crcMismatch, Truncated = truncated, HasIdat = hasIdat,
                HasAlphaChannel = hasAlpha || colorType is 4 or 6 || (colorType == 3 && trns != null),
                PixelStatsAvailable = statsOk,
                ColorCount = colorCount,
                TopColorRatio = topRatio,
                TransparentRatio = transparentRatio,
                NearWhiteRatio = nearWhiteRatio,
            };
        }
        catch { return null; }
    }

    // ================= 像素统计 =================

    private record struct PixelStats(bool HasAlpha, int ColorCount, double TopRatio,
                                     double TransparentRatio, double NearWhiteRatio);

    private static PixelStats? ComputePixelStats(
        int w, int h, int bitDepth, int colorType, int interlace,
        List<byte[]> idatParts, byte[]? plte, byte[]? trns)
    {
        if (interlace != 0) return null;                    // Adam7 隔行：跳过统计（不影响结构审计）
        if (bitDepth != 8 && bitDepth != 16) return null;   // 位打包格式：跳过统计
        if (colorType is not (0 or 2 or 3 or 4 or 6)) return null;

        int channels = colorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
        int bytesPerSample = bitDepth / 8;
        long stride = 1L + (long)w * channels * bytesPerSample;
        if (stride * h > MaxRawBytesForStats) return null;  // 过大：跳过统计

        // ---- zlib 解压 IDAT ----
        var packed = idatParts.SelectMany(p => p).ToArray();
        if (packed.Length < 2 || (packed[0] & 0x0F) != 8) return null;   // zlib 头 CMF 检查
        using var src = new MemoryStream(packed, 2, packed.Length - 2);
        using var def = new DeflateStream(src, CompressionMode.Decompress);
        var raw = new byte[stride * h];
        int total = 0, read;
        while ((read = def.Read(raw, total, raw.Length - total)) > 0)
        {
            total += read;
            if (total >= raw.Length) break;
        }
        if (total < raw.Length) throw new InvalidDataException("IDAT 解压后数据不足");

        // ---- 逐行 unfilter 并统计 ----
        int bpp = channels * bytesPerSample;   // filter 引用的前导字节数
        var counts = new Dictionary<long, int>(4096);
        long sampled = 0, transparent = 0, nearWhite = 0;
        var row = new byte[stride - 1];
        var prev = new byte[stride - 1];

        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * stride];
            if (filter > 4) throw new InvalidDataException($"未知滤镜类型 {filter}");
            Buffer.BlockCopy(raw, (int)(y * stride + 1), row, 0, row.Length);
            UnfilterRow(row, prev, filter, bpp);
            prev = (byte[])row.Clone();

            // 采样统计（行内按步进抽样，控制大图耗时；行序遍历保证覆盖整图）
            int stepX = w > 1024 ? w / 1024 + 1 : 1;
            int stepY = h > 1024 ? h / 1024 + 1 : 1;
            if (y % stepY != 0) continue;

            for (int x = 0; x < w; x += stepX)
            {
                (long key, int alpha, bool white) = ReadPixel(row, x, colorType, channels, bytesPerSample, plte, trns);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                sampled++;
                if (alpha < 8) transparent++;
                if (white) nearWhite++;
            }
        }

        if (sampled == 0) return null;
        int top = counts.Values.Max();
        bool hasAlpha = colorType is 4 or 6 || (colorType == 3 && trns is { Length: > 0 });
        return new PixelStats(hasAlpha, counts.Count, (double)top / sampled,
            (double)transparent / sampled, (double)nearWhite / sampled);
    }

    private static void UnfilterRow(byte[] row, byte[] prev, int filter, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: // Sub
                for (int i = bpp; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - bpp]);
                break;
            case 2: // Up
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + prev[i]);
                break;
            case 3: // Average
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] = (byte)(row[i] + (left + prev[i]) / 2);
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = prev[i];
                    int ul = i >= bpp ? prev[i - bpp] : 0;
                    int p = left + up - ul;
                    int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - ul);
                    int pred = pa <= pb && pa <= pc ? left : pb <= pc ? up : ul;
                    row[i] = (byte)(row[i] + pred);
                }
                break;
        }
    }

    private static (long Key, int Alpha, bool NearWhite) ReadPixel(
        byte[] row, int x, int colorType, int channels, int bps, byte[]? plte, byte[]? trns)
    {
        switch (colorType)
        {
            case 0: // 灰度
            {
                int g = bps == 2 ? row[x * 2] : row[x];
                return ((long)g << 16 | g << 8 | g, 255, g >= 250);
            }
            case 2: // RGB
            {
                int o = x * channels * bps;
                int r = row[o], g = row[o + bps], b = row[o + 2 * bps];   // 16 位取高字节
                return ((long)r << 16 | g << 8 | b, 255, r >= 250 && g >= 250 && b >= 250);
            }
            case 3: // 调色板
            {
                int idx = row[x];
                byte r = 0, g = 0, b = 0;
                if (plte != null && plte.Length >= (idx + 1) * 3)
                {
                    r = plte[idx * 3]; g = plte[idx * 3 + 1]; b = plte[idx * 3 + 2];
                }
                int alpha = trns != null && trns.Length > idx ? trns[idx] : 255;
                return ((long)r << 16 | g << 8 | b, alpha, r >= 250 && g >= 250 && b >= 250);
            }
            case 4: // 灰度+Alpha
            {
                int o = x * channels * bps;
                int g = row[o], a = row[o + bps];
                return ((long)g << 16 | g << 8 | g, a, g >= 250);
            }
            default: // 6 = RGBA
            {
                int o = x * channels * bps;
                int r = row[o], g = row[o + bps], b = row[o + 2 * bps], a = row[o + 3 * bps];
                return ((long)r << 16 | g << 8 | b, a, r >= 250 && g >= 250 && b >= 250);
            }
        }
    }

    // ================= 基础工具 =================

    private static int ReadInt32(byte[] d, int o)
        => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    private static uint Crc32(byte[] data, int offset, int count)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>
/// 最小 PNG 编码器（仅 8-bit RGBA、非隔行），用于内置测试套件构造确定性的合法/异常样本。
/// 产物符合 PNG 规范，可被 WPF/标准解码器正常读取。
/// </summary>
public static class MiniPng
{
    /// <summary>按像素函数生成一张 RGBA PNG；px 返回 (R, G, B, A)</summary>
    public static byte[] CreateRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> px)
    {
        var raw = new byte[height * (1 + width * 4)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;   // filter: None
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = px(x, y);
                raw[o++] = r; raw[o++] = g; raw[o++] = b; raw[o++] = a;
            }
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteInt32(ihdr, 0, width); WriteInt32(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", ZlibCompress(raw));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var buf = new byte[4 + 4 + data.Length + 4];
        WriteInt32(buf, 0, data.Length);
        Buffer.BlockCopy(t, 0, buf, 4, 4);
        Buffer.BlockCopy(data, 0, buf, 8, data.Length);
        uint crc = Crc32(buf, 4, 4 + data.Length);
        WriteInt32(buf, 8 + data.Length, unchecked((int)crc));
        s.Write(buf);
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x01);   // zlib 头（CMF=8, 快速模式）
        using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            def.Write(raw, 0, raw.Length);
        // Adler-32：a 从 1 开始（初始值不可省，否则标准解码器会判定数据流损坏）
        const uint MOD = 65521;
        uint a = 1, b = 0;
        foreach (var by in raw) { a = (a + by) % MOD; b = (b + a) % MOD; }
        var tail = new byte[4];
        WriteInt32(tail, 0, unchecked((int)((b << 16) | a)));
        ms.Write(tail);
        return ms.ToArray();
    }

    private static void WriteInt32(byte[] d, int o, int v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>
/// AI 输出审计引擎：对外接 AI 服务的返回结果做<b>确定性、可复现</b>的质量门禁。
/// 三类审计：
/// - <see cref="AuditImage"/>：PNG 完整性 / 可解码性 / 尺寸 / 空白纯色 / 全透明等；
/// - <see cref="AuditPrompt"/>：提示词长度 / 占位符残留 / 控制字符等；
/// - <see cref="AuditCode"/>：括号配对 / 占位符残留等。
/// 全部结论由固定阈值规则推出，相同输入必得相同报告；审计过程本身不抛异常。
/// </summary>
public static class AiOutputAudit
{
    /// <summary>DALL-E 系列已知合法尺寸（用于友好校验）</summary>
    public static readonly IReadOnlyDictionary<string, string[]> KnownModelSizes = new Dictionary<string, string[]>
    {
        ["dall-e-3"] = new[] { "1024x1024", "1792x1024", "1024x1792" },
        ["dall-e-2"] = new[] { "256x256", "512x512", "1024x1024" },
    };

    /// <summary>OpenAI 兼容接口的提示词长度上限（DALL-E 3 为 4000 字符）</summary>
    public const int MaxPromptLength = 4000;

    // ================= 图像审计 =================

    /// <summary>审计一段 AI 返回的 PNG 字节</summary>
    /// <param name="png">待审计字节</param>
    /// <param name="expectedWidth">期望宽度（发起请求时的尺寸，可空）</param>
    /// <param name="expectedHeight">期望高度（可空）</param>
    public static AiAuditReport AuditImage(byte[]? png, int? expectedWidth = null, int? expectedHeight = null)
    {
        var findings = new List<AiAuditFinding>();

        if (png is not { Length: > 0 })
        {
            findings.Add(Err("img.empty", "AI 返回的图像数据为空。", "重新生成；若持续出现请检查服务端日志。"));
            return Finish("image", findings);
        }

        if (!PngInspector.HasPngSignature(png))
        {
            findings.Add(Err("img.signature",
                $"返回数据不是有效的 PNG（{png.Length} 字节，签名不符）。",
                "多为网关返回了错误页 / JSON 错误体。请核对 Endpoint 与模型名后重试。"));
            return Finish("image", findings);
        }

        var info = PngInspector.Inspect(png);
        if (info == null)
        {
            findings.Add(Err("img.parse", "PNG 结构解析失败（签名正确但内容无法解读）。",
                "文件可能已损坏，请重新生成。"));
            return Finish("image", findings);
        }

        if (info.Truncated)
            findings.Add(Err("img.truncated", "图像数据不完整（缺少 IEND 结束块），疑似传输被截断。",
                "重新生成；若持续出现多为网络不稳定。"));
        if (!info.HasIdat)
            findings.Add(Err("img.noidat", "PNG 缺少图像数据块（IDAT），无法显示。", "重新生成。"));
        if (info.Width <= 0 || info.Height <= 0)
            findings.Add(Err("img.dims", $"图像尺寸异常（{info.Width}×{info.Height}）。", "重新生成。"));
        if (info.CrcMismatch)
            findings.Add(Warn("img.crc", "关键数据块校验和（CRC）不一致，文件可能已损坏或被篡改。",
                "建议重新生成后再导入纹理库。"));

        if (expectedWidth is > 0 && expectedHeight is > 0 &&
            (info.Width != expectedWidth || info.Height != expectedHeight))
            findings.Add(Warn("img.size",
                $"返回尺寸 {info.Width}×{info.Height} 与请求的 {expectedWidth}×{expectedHeight} 不一致。",
                "部分服务会自行调整尺寸，可继续使用；如需精确尺寸请在生成后缩放。"));

        if (info.PixelStatsAvailable)
        {
            if (info.ColorCount <= 3 && info.TopColorRatio >= 0.99)
                findings.Add(Warn("img.solid",
                    $"画面几乎为纯色（仅 {info.ColorCount} 种颜色，主色占 {info.TopColorRatio:P1}），疑似模型未成功渲染。",
                    "尝试丰富提示词描述、更换风格预设后重新生成。"));
            else if (info.NearWhiteRatio >= 0.995 && info.ColorCount <= 8)
                findings.Add(Warn("img.blank", "画面接近纯白，疑似空白图。", "检查提示词是否过于抽象，重新生成。"));

            if (info.HasAlphaChannel && info.TransparentRatio >= 0.999)
                findings.Add(Warn("img.transparent",
                    $"图像接近全透明（透明像素占 {info.TransparentRatio:P1}），导入后可能不可见。",
                    "在提示词中要求纯色背景，或使用 SD WebUI 的负面提示词排除透明背景。"));
        }
        else if (!info.Truncated && !info.CrcMismatch)
        {
            findings.Add(new AiAuditFinding
            {
                Severity = AiAuditSeverity.Info, Code = "img.stats",
                Message = "编码格式特殊（隔行 / 位打包 / 超大图），已跳过像素级统计，仅做结构审计。",
            });
        }

        if (png.Length > 25_000_000)
            findings.Add(Warn("img.large", $"图像体积 {png.Length / 1024 / 1024} MB，异常偏大。",
                "确认尺寸设置是否过大，导入纹理库前建议先缩放。"));
        if (info.Width != info.Height)
            findings.Add(new AiAuditFinding
            {
                Severity = AiAuditSeverity.Info, Code = "img.nonsquare",
                Message = $"图像非正方形（{info.Width}×{info.Height}）。物品 / 方块纹理通常为方形。",
                Suggestion = "用作图标 / 纹理时建议方形出图。",
            });

        findings.Add(new AiAuditFinding
        {
            Severity = AiAuditSeverity.Info, Code = "img.summary",
            Message = info.PixelStatsAvailable
                ? $"{info.Width}×{info.Height} · {info.BitDepth}bit · 颜色数 {info.ColorCount} · {info.FileBytes / 1024.0:F0} KB"
                : $"{info.Width}×{info.Height} · {info.BitDepth}bit · {info.FileBytes / 1024.0:F0} KB",
        });

        return Finish("image", findings);
    }

    // ================= 提示词审计 =================

    /// <summary>审计发送给 AI 服务的提示词</summary>
    public static AiAuditReport AuditPrompt(string? prompt, int maxLength = MaxPromptLength)
    {
        var findings = new List<AiAuditFinding>();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            findings.Add(Err("prompt.empty", "提示词为空。", "请输入图像描述后再生成。"));
            return Finish("prompt", findings);
        }

        int len = prompt.Length;
        if (len > maxLength)
            findings.Add(Warn("prompt.long",
                $"提示词 {len} 字符，超过常见上限（DALL-E 3 为 {maxLength}），可能被服务端拒绝或截断。",
                "精简描述，保留主体、风格与关键约束。"));

        if (prompt.Contains("{{") && prompt.Contains("}}"))
            findings.Add(Warn("prompt.placeholder",
                "提示词包含未替换的 {{占位符}}，原样发送会让模型困惑。",
                "先替换占位符（如 {{modid}} → 实际 Mod ID）。"));

        if (prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            findings.Add(Warn("prompt.ctrl", "提示词包含控制字符，可能干扰模型理解。", "清理特殊不可见字符。"));

        if (HasLongRun(prompt, 50))
            findings.Add(Warn("prompt.repeat", "提示词存在超长重复字符序列，疑似误粘贴。",
                "检查并清理异常重复内容。"));

        if (!prompt.Any(char.IsLetterOrDigit))
            findings.Add(Warn("prompt.weak", "提示词几乎没有有效文字，模型难以理解意图。", "用自然语言描述主体与风格。"));

        if (findings.Count == 0)
            findings.Add(new AiAuditFinding
            {
                Severity = AiAuditSeverity.Info, Code = "prompt.ok",
                Message = $"提示词 {len} 字符，未发现问题。",
            });

        return Finish("prompt", findings);
    }

    private static bool HasLongRun(string s, int minRun)
    {
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] == s[i - 1] ? run + 1 : 1;
            if (run >= minRun) return true;
        }
        return false;
    }

    // ================= 代码审计 =================

    /// <summary>审计一段 AI 生成的代码 / 片段内容（java / json / glsl）</summary>
    public static AiAuditReport AuditCode(string? code, string language = "java")
    {
        var findings = new List<AiAuditFinding>();

        if (string.IsNullOrWhiteSpace(code))
        {
            findings.Add(Err("code.empty", "代码内容为空。", "检查输入或重新生成。"));
            return Finish("code", findings);
        }

        if (code.Contains("{{") && code.Contains("}}"))
            findings.Add(Warn("code.placeholder",
                "代码包含未替换的 {{占位符}}，直接编译会失败。",
                "插入片段后请确认占位符已按项目信息替换。"));

        // 括号配对检查（跳过字符串字面量与注释，降低误报）
        var counts = CountBrackets(code, language);
        foreach (var (open, close, name) in new[] { ('{', '}', "花括号"), ('(', ')', "圆括号"), ('[', ']', "方括号") })
        {
            int d = counts(open) - counts(close);
            if (d != 0)
                findings.Add(Warn("code.balance",
                    $"{name}不配对：{open} 有 {counts(open)} 个、{close} 有 {counts(close)} 个。",
                    d > 0 ? $"疑似缺少 {d} 个 {close}" : $"疑似缺少 {-d} 个 {open}"));
        }

        if (findings.Count == 0)
            findings.Add(new AiAuditFinding
            {
                Severity = AiAuditSeverity.Info, Code = "code.ok",
                Message = $"代码 {code.Length} 字符，括号配对正常。",
            });

        return Finish($"code:{language}", findings);
    }

    /// <summary>统计括号数量，跳过字符串字面量与注释（json 无注释；glsl/java 同规则）</summary>
    private static Func<char, int> CountBrackets(string code, string language)
    {
        var tally = new Dictionary<char, int> { ['{'] = 0, ['}'] = 0, ['('] = 0, [')'] = 0, ['['] = 0, [']'] = 0 };
        bool inStr = false, inChar = false, inLine = false, inBlock = false;
        bool allowComments = !string.Equals(language, "json", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            char next = i + 1 < code.Length ? code[i + 1] : '\0';

            if (inLine) { if (c == '\n') inLine = false; continue; }
            if (inBlock) { if (c == '*' && next == '/') { inBlock = false; i++; } continue; }
            if (inStr)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inStr = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            if (allowComments && c == '/' && next == '/') { inLine = true; i++; continue; }
            if (allowComments && c == '/' && next == '*') { inBlock = true; i++; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '\'') { inChar = true; continue; }
            if (tally.ContainsKey(c)) tally[c]++;
        }

        return ch => tally.TryGetValue(ch, out var n) ? n : 0;
    }

    // ================= 公共小工具 =================

    /// <summary>数据是否为可被解码器接受的 PNG（签名 + 基本结构完整），用于落盘前防御</summary>
    public static bool IsValidPng(byte[]? png)
    {
        var r = AuditImage(png);
        return !r.HasErrors;
    }

    private static AiAuditFinding Err(string code, string msg, string? tip = null) =>
        new() { Severity = AiAuditSeverity.Error, Code = code, Message = msg, Suggestion = tip ?? "" };
    private static AiAuditFinding Warn(string code, string msg, string? tip = null) =>
        new() { Severity = AiAuditSeverity.Warning, Code = code, Message = msg, Suggestion = tip ?? "" };

    private static AiAuditReport Finish(string target, List<AiAuditFinding> findings)
    {
        findings.Sort((a, b) => ((int)b.Severity).CompareTo((int)a.Severity));
        return new AiAuditReport { Target = target, Findings = findings };
    }
}
