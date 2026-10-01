using MCModCoder.Core.Models;

namespace MCModCoder.Core.Ai;

/// <summary>技能分类（用于注册与发现时的筛选）</summary>
public enum SkillCategory
{
    /// <summary>分析与规划</summary>
    Planning,
    /// <summary>文件读写</summary>
    File,
    /// <summary>代码生成</summary>
    Code,
    /// <summary>资源配置（模型 / 配方 / 纹理）</summary>
    Resource,
    /// <summary>项目构建</summary>
    Build,
    /// <summary>打包与验证</summary>
    Package,
}

/// <summary>技能参数描述（供 AI 引擎发现阶段了解如何组装参数）</summary>
public class SkillParam
{
    public string Name { get; set; } = "";
    /// <summary>参数说明（自然语言，供规划器理解语义）</summary>
    public string Description { get; set; } = "";
    public bool Required { get; set; }

    public SkillParam() { }
    public SkillParam(string name, string description, bool required = false)
    { Name = name; Description = description; Required = required; }
}

/// <summary>技能执行结果（引擎据此解析并推进下一步）</summary>
public class SkillResult
{
    public bool Success { get; set; }
    /// <summary>人类可读的执行摘要（已写入引擎日志）</summary>
    public string Message { get; set; } = "";
    /// <summary>结构化输出（如 jar 路径、生成文件数），供后续技能或引擎读取</summary>
    public Dictionary<string, string> Outputs { get; set; } = new();
    /// <summary>失败时的详细原因（异常信息等）</summary>
    public string? Error { get; set; }

    public static SkillResult Ok(string message, Dictionary<string, string>? outputs = null)
        => new() { Success = true, Message = message, Outputs = outputs ?? new() };
    public static SkillResult Fail(string message, string? error = null)
        => new() { Success = false, Message = message, Error = error };
}

/// <summary>技能执行上下文：项目、根目录、共享草稿与日志回调</summary>
public class SkillContext
{
    /// <summary>当前模组项目（可为 null，此时依赖项目的技能应返回失败并说明原因）</summary>
    public McProject? Project { get; init; }
    /// <summary>技能间共享的草稿区（如需求分析结论），键为字符串</summary>
    public Dictionary<string, string> Scratch { get; } = new();
    /// <summary>日志回调（引擎传入，写执行轨迹）</summary>
    public Action<string>? Log { get; init; }

    public void WriteLog(string message) => Log?.Invoke(message);
}

/// <summary>
/// 可注册、可调用的技能接口。AI 引擎通过注册表发现技能、按参数说明组装参数并调用，
/// 再解析 <see cref="SkillResult"/> 决定推进方向。所有内置能力（文件读写、代码生成、
/// 构建打包等）均以此形式封装，外部宿主也可注册自定义技能扩展引擎。
/// </summary>
public interface ISkill
{
    /// <summary>技能唯一名（点分小写，如 "file.write"），注册与调用的主键</summary>
    string Name { get; }
    /// <summary>显示名（中文，用于日志与发现结果展示）</summary>
    string DisplayName { get; }
    /// <summary>能力描述（自然语言，供规划器按需匹配）</summary>
    string Description { get; }
    public SkillCategory Category { get; }
    /// <summary>参数清单（发现阶段的"说明书"）</summary>
    IReadOnlyList<SkillParam> Params { get; }

    /// <summary>执行技能。实现须自行捕获异常并转为失败结果，不得向引擎抛出。</summary>
    SkillResult Execute(SkillContext context, IReadOnlyDictionary<string, string> args);
}

/// <summary>
/// 技能注册表：技能的注册、发现与调用中枢。
/// - 注册：Register / Unregister（同名覆盖，幂等）；
/// - 发现：Get 精确取名，Discover 按关键词 + 分类模糊检索（评分排序）；
/// - 调用：Invoke 统一入口（参数校验 → 执行 → 异常兜底）。
/// 线程安全。
/// </summary>
public class SkillRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ISkill> _skills = new(StringComparer.Ordinal);

    /// <summary>注册技能（同名覆盖）。返回注册后的技能实例。</summary>
    public ISkill Register(ISkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        lock (_gate) _skills[skill.Name] = skill;
        return skill;
    }

    /// <summary>注销技能。返回是否确实移除了一个。</summary>
    public bool Unregister(string name)
    {
        lock (_gate) return _skills.Remove(name);
    }

    /// <summary>全部已注册技能快照。</summary>
    public List<ISkill> All
    {
        get { lock (_gate) return _skills.Values.ToList(); }
    }

    public int Count { get { lock (_gate) return _skills.Count; } }

    /// <summary>精确发现：按技能名取技能（不存在返回 null）。</summary>
    public ISkill? Get(string name)
    {
        lock (_gate) return _skills.TryGetValue(name, out var s) ? s : null;
    }

    /// <summary>
    /// 模糊发现：按关键词（匹配 名称/显示名/描述/分类）与可选分类过滤，评分降序。
    /// 关键词为空时返回该分类下全部技能（分类也为空则返回全部）。
    /// </summary>
    public List<ISkill> Discover(string? keyword = null, SkillCategory? category = null)
    {
        var all = All;
        if (category is { } cat) all = all.Where(s => s.Category == cat).ToList();
        if (string.IsNullOrWhiteSpace(keyword)) return all;

        var kw = keyword.Trim();
        return all
            .Select(s => (Skill: s, Score: ScoreMatch(s, kw)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Name, StringComparer.Ordinal)
            .Select(x => x.Skill)
            .ToList();
    }

    /// <summary>统一调用入口：缺参校验 → 执行 → 异常兜底为失败结果。技能不存在返回失败结果。</summary>
    public SkillResult Invoke(string name, SkillContext context, IReadOnlyDictionary<string, string>? args = null)
    {
        var skill = Get(name);
        if (skill == null)
            return SkillResult.Fail($"技能不存在：{name}", $"已注册技能：{string.Join(", ", All.Select(s => s.Name))}");
        var safeArgs = args ?? new Dictionary<string, string>();
        var missing = skill.Params.Where(p => p.Required && !safeArgs.ContainsKey(p.Name)).Select(p => p.Name).ToList();
        if (missing.Count > 0)
            return SkillResult.Fail($"调用 {name} 缺少必需参数：{string.Join(", ", missing)}");

        context.WriteLog($"→ 调用技能 {name}");
        try
        {
            var r = skill.Execute(context, safeArgs);
            context.WriteLog($"  {(r.Success ? "✔" : "✖")} {r.Message}");
            return r;
        }
        catch (Exception ex)
        {
            context.WriteLog($"  ✖ {name} 执行异常：{ex.Message}");
            return SkillResult.Fail($"{name} 执行异常", ex.ToString());
        }
    }

    /// <summary>关键词匹配评分：名称前缀 100 / 名称包含 70 / 显示名或分类包含 50 / 描述包含 30，其余 0</summary>
    private static int ScoreMatch(ISkill s, string kw)
    {
        var k = kw.ToLowerInvariant();
        if (s.Name.StartsWith(kw, StringComparison.Ordinal)) return 100;
        if (s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)) return 70;
        if (s.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)) return 50;
        if (s.Category.ToString().Contains(k, StringComparison.OrdinalIgnoreCase)) return 50;
        if (s.Description.Contains(kw, StringComparison.OrdinalIgnoreCase)) return 30;
        return 0;
    }
}
