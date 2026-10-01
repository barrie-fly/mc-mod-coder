using System.Text.RegularExpressions;
using MCModCoder.Core.Models;
using MCModCoder.Core.Services;

namespace MCModCoder.Core.Ai;

/// <summary>需求解析出的功能要素</summary>
public class FeatureSpec
{
    /// <summary>类别：item / tool / food / block</summary>
    public string Kind { get; set; } = "item";
    /// <summary>合法注册 ID（中文需求回退为 kind_哈希，保证小写字母数字下划线）</summary>
    public string Id { get; set; } = "";
    /// <summary>显示名（原文）</summary>
    public string Display { get; set; } = "";
}

/// <summary>需求分析的结构化结果</summary>
public class RequirementFacts
{
    public string? ModName { get; set; }
    public string? McVersion { get; set; }
    public string? Loader { get; set; }
    public List<FeatureSpec> Features { get; set; } = new();
    public bool WantsRecipe { get; set; }
    /// <summary>解析出的配方原料（如 minecraft:iron_ingot），最多两个</summary>
    public List<string> RecipeIngredients { get; set; } = new();
    public bool WantsModpack { get; set; }
    public string Raw { get; set; } = "";
}

/// <summary>
/// 本地规则需求解析器：离线、确定性（与工作台「本地 AI」风格一致）。
/// 从自然语言需求中提取 模组名 / MC 版本 / 加载器 / 功能要素 / 配方与整合包意图。
/// </summary>
public static class RequirementAnalyzer
{
    /// <summary>类别词表：后缀词 → (kind, 英文能力名)</summary>
    private static readonly (string Suffix, string Kind)[] KindSuffixes =
    {
        ("镐", "tool"), ("斧", "tool"), ("剑", "tool"), ("锹", "tool"), ("锄", "tool"), ("工具", "tool"),
        ("食物", "food"), ("食品", "food"), ("方块", "block"), ("矿石", "block"), ("矿", "block"),
        ("物品", "item"), ("item", "item"), ("block", "block"),
    };

    /// <summary>需要从要素名头部剥离的动词/量词噪音</summary>
    private static readonly Regex NoisePrefix = new(
        @"^(?:帮我|请|我|想|要|需要|打算|准备|尝试|开始|进行|完成|支持|实现|新增|添加|增加|注册|开发|制作|生成|创建|设计|写|做|一个|一款|一种|一套|新的|新|简单|基本|基础)+",
        RegexOptions.Compiled);

    public static RequirementFacts Analyze(string text)
    {
        var facts = new RequirementFacts { Raw = text };

        // MC 版本：取「1.x / 1.x.y / 26.x」中能命中版本矩阵的
        foreach (Match m in Regex.Matches(text, @"\b\d{1,2}\.\d{1,2}(?:\.\d{1,2})?\b"))
            if (McVersions.Find(m.Value) != null) { facts.McVersion = m.Value; break; }

        // 加载器
        if (Regex.IsMatch(text, "neoforge", RegexOptions.IgnoreCase)) facts.Loader = "NeoForge";
        else if (Regex.IsMatch(text, "forge", RegexOptions.IgnoreCase)) facts.Loader = "Forge";
        else if (Regex.IsMatch(text, "fabric", RegexOptions.IgnoreCase)) facts.Loader = "Fabric";
        else if (Regex.IsMatch(text, "rift", RegexOptions.IgnoreCase)) facts.Loader = "Rift";

        // 模组名：「XX模组」
        var modName = Regex.Match(text, @"([\u4e00-\u9fa5A-Za-z0-9_]+?)\s*模组");
        if (modName.Success) facts.ModName = NoisePrefix.Replace(modName.Groups[1].Value, "").Trim();

        // 功能要素：「XX物品 / XX镐 / XX方块 / XX食物」
        foreach (Match m in Regex.Matches(text, @"([\u4e00-\u9fa5A-Za-z0-9_]{1,12}?)\s*(物品|方块|矿石|矿|食物|食品|镐|斧|剑|锹|锄|工具|item|block)"))
        {
            var display = NoisePrefix.Replace(m.Groups[1].Value, "").Trim();
            var suffix = m.Groups[2].Value;
            var kind = KindSuffixes.FirstOrDefault(k => k.Suffix == suffix).Kind;
            if (display.Length == 0) continue;
            if (facts.Features.Any(f => f.Display == display)) continue;

            var id = GenerateCodeSkill.SanitizeId(display);
            if (id.Length == 0) id = $"{kind}_{AutoNaming.Hash6(display).ToLowerInvariant()}";  // 中文显示名 → 稳定哈希 ID
            facts.Features.Add(new FeatureSpec { Kind = kind, Id = id, Display = display });
        }
        // 中英混排（如 "ruby 物品"）里 ASCII 词直接作 ID
        foreach (Match m in Regex.Matches(text, @"([a-z][a-z0-9_]{1,31})\s*(物品|方块|item|block)", RegexOptions.IgnoreCase))
        {
            var display = m.Groups[1].Value.ToLowerInvariant();
            if (facts.Features.Any(f => f.Id == display)) continue;
            var kind = m.Groups[2].Value is "方块" or "block" ? "block" : "item";
            facts.Features.Add(new FeatureSpec { Kind = kind, Id = display, Display = display });
        }

        // 配方意图与原料：「用X和Y合成」
        facts.WantsRecipe = Regex.IsMatch(text, "合成|配方|craft");
        var ing = Regex.Match(text, @"用\s*([\u4e00-\u9fa5A-Za-z0-9_:]+?)\s*(?:和|与|及|\+)\s*([\u4e00-\u9fa5A-Za-z0-9_:]+?)\s*(?:合成|制作|打造)");
        if (ing.Success)
            foreach (var g in new[] { ing.Groups[1].Value, ing.Groups[2].Value })
            {
                var item = NormalizeIngredient(g);
                if (item.Length > 0 && !facts.RecipeIngredients.Contains(item)) facts.RecipeIngredients.Add(item);
            }

        // 整合包意图
        facts.WantsModpack = Regex.IsMatch(text, "整合包|发布|分发|modpack");
        return facts;
    }

    /// <summary>原料词规范化：已含命名空间原样小写；常见材料中文映射；否则按 minecraft: 处理</summary>
    private static string NormalizeIngredient(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.Contains(':')) return GenerateCodeSkill.SanitizeId(s.Replace(':', '_')).Replace('_', ':') is var r && r.Length > 0 ? s : "";
        var table = new (string Word, string Item)[]
        {
            ("铁锭", "minecraft:iron_ingot"), ("木棍", "minecraft:stick"), ("棍子", "minecraft:stick"),
            ("金锭", "minecraft:gold_ingot"), ("钻石", "minecraft:diamond"), ("线", "minecraft:string"),
            ("木板", "minecraft:planks"), ("石头", "minecraft:stone"), ("煤炭", "minecraft:coal"),
            ("红石", "minecraft:redstone"), ("皮革", "minecraft:leather"), ("铁矿", "minecraft:raw_iron"),
        };
        return table.FirstOrDefault(t => t.Word == s).Item is { Length: > 0 } hit ? hit
             : s.All(char.IsAsciiLetterOrDigit) ? $"minecraft:{s}" : "";
    }
}

/// <summary>开发任务状态</summary>
public enum DevTaskStatus { Pending, Running, Done, Failed, Skipped }

/// <summary>开发任务：一项待执行工作，绑定首选技能与参数（技能可由引擎模糊发现补位）</summary>
public class DevTask
{
    public string Title { get; set; } = "";
    /// <summary>首选技能名（点分 ID）；引擎优先精确匹配，失败时按 FallbackKeywords 发现补位</summary>
    public string SkillName { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
    /// <summary>技能缺失时的发现关键词（空格分隔）</summary>
    public string FallbackKeywords { get; set; } = "";
    public DevTaskStatus Status { get; set; } = DevTaskStatus.Pending;
    /// <summary>执行结果（引擎据此解析并推进）</summary>
    public SkillResult? Result { get; set; }
    /// <summary>实际使用的技能名（可能与首选不同 — 模糊发现时）</summary>
    public string? ResolvedSkill { get; set; }

    public override string ToString() => $"[{Status}] {Title}";
}

/// <summary>开发计划：一次自主开发的目标与任务序列</summary>
public class DevPlan
{
    public string Requirement { get; set; } = "";
    public string Goal { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<DevTask> Tasks { get; set; } = new();

    public bool AllDone => Tasks.All(t => t.Status is DevTaskStatus.Done or DevTaskStatus.Skipped);
    public bool AnyFailed => Tasks.Any(t => t.Status == DevTaskStatus.Failed);
}

/// <summary>
/// AI 开发引擎：面向「模组与整合包独立开发」的技能编排中枢。
/// 流程：Plan（按需求自主规划任务序列）→ ExecutePlan（逐任务 技能发现→调用→解析结果→推进）。
/// 全部能力通过 <see cref="SkillRegistry"/> 中注册的 <see cref="ISkill"/> 完成，
/// 自定义宿主可注入自己的注册表并扩展技能。
/// </summary>
public class AiDevEngine
{
    /// <summary>技能注册表（构造时默认注册全部内置技能）</summary>
    public SkillRegistry Skills { get; }

    /// <summary>执行轨迹（时间戳日志，可直接展示到 UI）</summary>
    public List<string> Log { get; } = new();

    public AiDevEngine(SkillRegistry? registry = null)
    {
        Skills = registry ?? new SkillRegistry();
        if (Skills.Count == 0) AiSkills.RegisterAll(Skills);
    }

    private void WriteLog(string message) => Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

    // ================= 规划 =================

    /// <summary>按自然语言需求自主规划开发任务序列（需求分析 → 脚手架 → 代码 → 资源 → 构建打包 → 验证）</summary>
    public DevPlan Plan(string requirement, McProject? project)
    {
        var facts = RequirementAnalyzer.Analyze(requirement);
        var plan = new DevPlan
        {
            Requirement = requirement,
            Goal = $"开发{(facts.ModName is { Length: > 0 } mn ? $"「{mn}」" : "模组")}" +
                   $"（MC {facts.McVersion ?? project?.McVersion ?? "1.20.1"} / {facts.Loader ?? project?.ModLoader ?? "Forge"}）" +
                   $"，功能点 {facts.Features.Count} 个",
        };
        WriteLog($"■ 规划：{plan.Goal}");

        // 1. 需求分析（结构化事实进入上下文草稿区）
        plan.Tasks.Add(new DevTask
        {
            Title = "需求分析",
            SkillName = "plan.analyze",
            Args = new() { ["requirement"] = requirement },
            FallbackKeywords = "analyze 需求 分析",
        });

        if (project == null)
        {
            WriteLog("⚠ 无项目上下文：仅执行需求分析。请先创建项目再完整规划。");
            return plan;
        }

        // 2. 项目脚手架（无 build.gradle 时）
        if (!File.Exists(Path.Combine(project.ProjectDir, "build.gradle")))
            plan.Tasks.Add(new DevTask { Title = "生成项目脚手架", SkillName = "project.scaffold", FallbackKeywords = "脚手架 scaffold 项目" });

        // 3. 代码生成（每个功能要素一个任务）
        foreach (var f in facts.Features)
            plan.Tasks.Add(new DevTask
            {
                Title = $"编写 {f.Display}（{f.Kind}）注册代码",
                SkillName = "code.generate",
                Args = new() { ["id"] = f.Id, ["kind"] = f.Kind, ["name"] = f.Display },
                FallbackKeywords = "生成 代码 code",
            });

        // 4. 资源配置：模型 + 纹理（按类别分批）
        foreach (var grp in facts.Features.GroupBy(f => f.Kind))
            plan.Tasks.Add(new DevTask
            {
                Title = $"生成 {string.Join("、", grp.Select(f => f.Id))} 的模型与纹理",
                SkillName = "resource.generate",
                Args = new() { ["ids"] = string.Join(",", grp.Select(f => f.Id)), ["kind"] = grp.Key },
                FallbackKeywords = "资源 模型 resource",
            });

        // 5. 配方（含意图且要素非空）
        if (facts.WantsRecipe && facts.Features.Count > 0)
        {
            var target = facts.Features.FirstOrDefault(f => f.Kind == "tool") ?? facts.Features[0];
            var type = facts.RecipeIngredients.Count >= 2 && target.Kind == "tool" ? "shaped" : "shapeless";
            var args = new Dictionary<string, string> { ["result"] = target.Id, ["type"] = type };
            if (type == "shaped")
            {
                // 镐形图案：三行等宽（首行双原料，其余行首空格对齐），避免行宽不一致校验失败
                args["pattern"] = facts.RecipeIngredients.Count >= 2 ? "XX; #; #" : "XX;XX;XX";
                if (facts.RecipeIngredients.Count >= 2)
                    args["key"] = $"X={facts.RecipeIngredients[0]};#={facts.RecipeIngredients[1]}";
                else if (facts.RecipeIngredients.Count == 1)
                    args["key"] = $"X={facts.RecipeIngredients[0]}";
            }
            if (facts.RecipeIngredients.Count > 0)
                args["ingredients"] = string.Join(",", facts.RecipeIngredients);
            plan.Tasks.Add(new DevTask
            {
                Title = $"生成 {target.Id} 的{type}配方",
                SkillName = "resource.recipe",
                Args = args,
                FallbackKeywords = "配方 recipe",
            });
        }

        // 6. 构建（模组 jar）
        plan.Tasks.Add(new DevTask { Title = "构建项目（产出模组 jar）", SkillName = "build.project", FallbackKeywords = "构建 build" });

        // 7. 打包整合包 + 产物验证
        plan.Tasks.Add(new DevTask { Title = "打包整合包（资源包+数据包+清单）", SkillName = "pack.modpack", FallbackKeywords = "打包 整合包 modpack" });
        plan.Tasks.Add(new DevTask { Title = "验证产物（jar 与整合包）", SkillName = "pack.verify", Args = new() { ["target"] = "all" }, FallbackKeywords = "验证 verify 产物" });

        WriteLog($"  计划 {plan.Tasks.Count} 个任务：{string.Join(" → ", plan.Tasks.Select(t => t.SkillName))}");
        return plan;
    }

    // ================= 执行 =================

    /// <summary>
    /// 执行计划：逐任务进行 技能发现（精确 → 模糊补位）→ 调用 → 解析结果 → 推进。
    /// 任务失败时中止后续链路（验证任务除外，始终执行以给出完整报告）。
    /// </summary>
    /// <returns>执行完毕的计划（含每任务状态与结果）</returns>
    public DevPlan ExecutePlan(DevPlan plan, McProject? project)
    {
        var ctx = new SkillContext { Project = project, Log = WriteLog };
        WriteLog($"■ 开始执行：{plan.Goal}");

        foreach (var task in plan.Tasks)
        {
            // 前置失败 → 跳过（验证任务始终执行）
            if (plan.AnyFailed && task.SkillName != "pack.verify")
            {
                task.Status = DevTaskStatus.Skipped;
                WriteLog($"  ⏭ 跳过「{task.Title}」（前置任务失败）");
                continue;
            }

            var skill = ResolveSkill(task);
            if (skill == null)
            {
                task.Status = DevTaskStatus.Failed;
                task.Result = SkillResult.Fail($"未找到可用技能（首选 {task.SkillName}，关键词 {task.FallbackKeywords}）");
                continue;
            }

            task.Status = DevTaskStatus.Running;
            task.Result = Skills.Invoke(skill.Name, ctx, task.Args);
            task.Status = task.Result.Success switch
            {
                true => task.Result.Outputs.ContainsKey("skipped") ? DevTaskStatus.Skipped : DevTaskStatus.Done,
                false => DevTaskStatus.Failed,
            };
        }

        var done = plan.Tasks.Count(t => t.Status == DevTaskStatus.Done);
        var skipped = plan.Tasks.Count(t => t.Status == DevTaskStatus.Skipped);
        var failed = plan.Tasks.Count(t => t.Status == DevTaskStatus.Failed);
        WriteLog($"■ 执行结束：{done} 完成 / {skipped} 跳过 / {failed} 失败");
        return plan;
    }

    /// <summary>技能发现：首选名精确匹配；未命中时按任务关键词模糊发现取最优。</summary>
    private ISkill? ResolveSkill(DevTask task)
    {
        var exact = Skills.Get(task.SkillName);
        if (exact != null) { task.ResolvedSkill = exact.Name; return exact; }

        var candidates = Skills.Discover(task.FallbackKeywords);
        var chosen = candidates.FirstOrDefault(s => s.Category != SkillCategory.Planning) ?? candidates.FirstOrDefault();
        if (chosen != null)
        {
            task.ResolvedSkill = chosen.Name;
            WriteLog($"  ⤷ 技能「{task.SkillName}」未注册，模糊发现补位 → {chosen.Name}（{chosen.DisplayName}）");
        }
        return chosen;
    }

    /// <summary>便捷入口：一步完成 规划 + 执行，返回计划。</summary>
    public DevPlan Run(string requirement, McProject? project)
        => ExecutePlan(Plan(requirement, project), project);
}
