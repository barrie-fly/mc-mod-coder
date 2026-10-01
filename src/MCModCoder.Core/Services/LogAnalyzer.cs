using System.Text.RegularExpressions;

namespace MCModCoder.Core.Services;

public class LogEntry
{
    public string Time { get; set; } = "";
    public string Thread { get; set; } = "";
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = "";
    public bool IsError => Level is "ERROR" or "FATAL";
    public bool IsWarn => Level == "WARN";
}

public class LogFinding
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Advice { get; set; } = "";
    public int Count { get; set; } = 1;
}

/// <summary>游戏日志捕获 / 解析 / 分析</summary>
public static partial class LogAnalyzer
{
    [GeneratedRegex(@"^\[(?<time>[^\]]+)\]\s*\[(?<thread>[^\]]+)/(?<level>[A-Z]+)\]:?\s*(?<msg>.*)$")]
    private static partial Regex McLogRegex();

    [GeneratedRegex(@"(?<ex>\w+(?:\.\w+)+(?::\s*.+)?)$")]
    private static partial Regex ExceptionRegex();

    public static LogEntry ParseLine(string line)
    {
        var m = McLogRegex().Match(line);
        if (m.Success)
        {
            return new LogEntry
            {
                Time = m.Groups["time"].Value,
                Thread = m.Groups["thread"].Value,
                Level = m.Groups["level"].Value,
                Message = m.Groups["msg"].Value
            };
        }
        return new LogEntry { Time = "", Thread = "", Level = line.Contains("Exception") ? "ERROR" : "INFO", Message = line };
    }

    /// <summary>分析日志，提取崩溃原因与常见问题</summary>
    public static List<LogFinding> Analyze(IEnumerable<LogEntry> entries)
    {
        var findings = new List<LogFinding>();
        var msgs = entries.Select(e => e.Message).ToList();

        // 崩溃报告
        if (msgs.Any(m => m.Contains("Crash report") || m.Contains("The game crashed")))
            findings.Add(new LogFinding
            {
                Title = "检测到游戏崩溃",
                Detail = "日志中包含崩溃报告引用。",
                Advice = "打开 crash-reports 目录中的最新报告，定位 \"Caused by\" 行；通常是模组冲突或注册缺失。"
            });

        // 常见异常模式
        var patterns = new (string Key, string Title, string Advice)[]
        {
            ("NullPointerException", "空指针异常", "检查事件回调中的对象是否可能为 null；Forge/Fabric 事件里的 world、player、stack 都可能为空。"),
            ("RegistryObject", "注册对象未初始化", "确认在模组构造函数中完成了 DeferredRegister 注册，且 registryName 与引用一致。"),
            ("Missing or unsupported mandatory dependencies", "缺少必需的模组依赖", "在 mods.toml / fabric.mod.json 中声明的依赖版本未满足，请调整版本或安装依赖。"),
            ("Mismatched mod list", "客户端与服务端模组不一致", "同步两侧 mods 目录，或把仅客户端模组标记 displayTest/displayTest=IGNORE。"),
            ("Mixin apply failed", "Mixin 注入失败", "检查 mixin 目标类名/方法签名是否与当前 MC 版本匹配。"),
            ("OutOfMemoryError", "内存不足", "在启动参数中增大 -Xmx（建议 ≥ 4G）。"),
            ("GL_INVALID", "OpenGL 错误", "显卡驱动或光影包问题，尝试更新驱动或禁用着色器。"),
            ("AbstractMethodError", "抽象方法未实现", "API 版本不匹配：模组编译所用的 API 与运行时版本不一致。"),
            ("Could not execute entrypoint", "入口点执行失败", "Fabric：检查 fabric.mod.json 的 entrypoints 配置与实现类。"),
            ("Duplicate mods found", "模组重复加载", "同一模组的不同版本同时存在，删除多余版本。"),
        };
        foreach (var (key, title, advice) in patterns)
        {
            int n = msgs.Count(m => m.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
                findings.Add(new LogFinding { Title = title, Detail = $"出现 {n} 次（关键词 {key}）", Advice = advice, Count = n });
        }

        // 首个异常堆栈
        var ex = msgs.Select(m => ExceptionRegex().Match(m)).FirstOrDefault(m => m.Success);
        if (ex != null)
            findings.Add(new LogFinding
            {
                Title = "首个异常堆栈",
                Detail = ex.Groups["ex"].Value,
                Advice = "结合上方建议排查；将异常类名在依赖库中搜索可快速定位来源模组。"
            });

        // 统计
        int errors = entries.Count(e => e.IsError);
        int warns = entries.Count(e => e.IsWarn);
        findings.Add(new LogFinding
        {
            Title = "日志统计",
            Detail = $"ERROR {errors} 条 / WARN {warns} 条 / 共 {msgs.Count} 行",
            Advice = errors > 0 ? "优先处理 ERROR 级别日志。" : "日志无错误，可关注 WARN 是否影响功能。"
        });
        return findings;
    }
}
