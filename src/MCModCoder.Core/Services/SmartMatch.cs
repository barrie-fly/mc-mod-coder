using System.Text.RegularExpressions;

namespace MCModCoder.Core.Services;

/// <summary>智能匹配推荐条目</summary>
public class SmartSuggestion
{
    /// <summary>推荐内容（可直接作为搜索关键词）</summary>
    public string Text { get; set; } = "";
    /// <summary>推荐理由（展示给用户）</summary>
    public string Reason { get; set; } = "";
    public int Score { get; set; }
}

/// <summary>
/// AI 智能匹配引擎（本地知识库版）：
/// 1. 模糊评分 —— 前缀 &gt; 子序列 &gt; 词元重叠，用于候选排序；
/// 2. 关联推荐 —— 内置 Minecraft 模组生态知识语料，结合项目上下文
///    （名称 / ModID / 描述 / 版本 / 加载器）自动推荐关联内容。
/// </summary>
public static class SmartMatch
{
    // ---------- 内置知识语料：触发词 → 推荐（热门/常用模组生态） ----------
    private static readonly (string[] Triggers, string Text, string Reason)[] Corpus =
    {
        (new[]{ "food","食品","食物","农业","farm","种植" }, "Farmer's Delight", "热门食物/农业模组，烹饪系统丰富，常作为美食类整合包核心"),
        (new[]{ "tech","科技","能源","电力","机器","energy","pipe" }, "Mekanism", "科技能源类头部模组，提供机器、管道与多方块结构"),
        (new[]{ "storage","存储","仓库","箱子","背包" }, "Applied Energistics 2", "数字化存储方案，ME 网络与自动合成"),
        (new[]{ "build","建筑","装饰","家具","decoration" }, "Macaw's Bridges", "建筑装饰系列，轻量且几乎无前置"),
        (new[]{ "magic","魔法","法术","附魔","咒语" }, "Ars Nouveau", "魔法类热门模组，自带文档化的法术系统"),
        (new[]{ "mob","生物","怪物","entity","boss" }, "Alex's Mobs", "热门生物扩展模组，90+ 新生物与生态互动"),
        (new[]{ "fps","性能","优化","帧数","卡顿","performance" }, "Embeddium (Sodium)", "客户端渲染优化，显著提升帧数"),
        (new[]{ "jei","配方","合成表","查看","物品" }, "JEI (Just Enough Items)", "配方查看器，几乎所有整合包的标配前置"),
        (new[]{ "worldgen","世界生成","地图","群系","biome","地形" }, "TerraBlender", "生物群系 API，自定义世界生成的常用前置"),
        (new[]{ "dungeon","地牢","副本","冒险","结构","探索" }, "YUNG's Better Dungeons", "结构系列模组，与地牢设计思路相通"),
        (new[]{ "tool","工具","武器","装备"," armor","盔甲" }, "Silent Gear", "模块化工具与装备，自定义材料体系"),
        (new[]{ "create","机械","动能","传动","齿轮" }, "Create", "机械动力模组，旋转传动系统与自动化产线"),
        (new[]{ "shader","光影","渲染","画质" }, "Iris Shaders", "光影加载器，配合自研光影配置调试"),
        (new[]{ "map","小地图","导航","路径" }, "JourneyMap", "实时小地图与路径点，测试探索阶段实用"),
        (new[]{ "library","前置","api","lib" }, "Fabric API / Kotlin for Forge", "按项目加载器选择对应的前置库"),
    };

    // ---------- 默认推荐（无上下文时的兜底） ----------
    private static readonly (string Text, string Reason)[] DefaultPicks =
    {
        ("JEI (Just Enough Items)", "物品与配方查看器，模组开发调试期的必备工具"),
        ("Create", "机械动力：可参考其注册与数据生成范式"),
        ("Embeddium (Sodium)", "客户端渲染优化，测试运行更流畅"),
        ("TerraBlender", "若涉及自定义群系/地形（地图编辑产物），常用此前置"),
        ("YUNG's Better Dungeons", "结构设计参考：高质量地牢生成范式"),
    };

    /// <summary>结合上下文给出关联内容推荐（≤ max 条）</summary>
    public static List<SmartSuggestion> Recommend(string context, int max = 5)
    {
        var tokens = Tokenize(context);
        var result = new List<SmartSuggestion>();

        if (tokens.Count > 0)
        {
            foreach (var (triggers, text, reason) in Corpus)
            {
                int score = triggers.Sum(t => CountTokenHits(tokens, t));
                if (score > 0)
                    result.Add(new SmartSuggestion { Text = text, Reason = reason, Score = score });
            }
        }

        if (result.Count == 0)
            result.AddRange(DefaultPicks.Select((p, i) => new SmartSuggestion
            {
                Text = p.Text, Reason = p.Reason, Score = DefaultPicks.Length - i
            }));

        return result.OrderByDescending(s => s.Score).Take(max).ToList();
    }

    /// <summary>资源导入页的推荐词条：基于项目上下文生成</summary>
    public static List<SmartSuggestion> GetSearchSuggestions(string? projectName, string? modId,
        string? description, string? mcVersion, string? loader)
        => Recommend($"{projectName} {modId} {description} {mcVersion} {loader}", 5);

    /// <summary>模糊匹配评分：前缀(100) &gt; 包含(70) &gt; 子序列(40)，用于候选排序</summary>
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(candidate)) return 0;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase)) return 70;
        // 子序列匹配
        int qi = 0;
        foreach (var ch in candidate)
        {
            if (char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi])) qi++;
            if (qi >= query.Length) return 40;
        }
        return 0;
    }

    // ---------- 内部辅助 ----------

    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (Match m in Regex.Matches(text, @"[A-Za-z\u4e00-\u9fff]{2,}"))
        {
            set.Add(m.Value.ToLowerInvariant());
            // 驼峰/下划线拆词（如 "BetterDungeons" → better / dungeons）
            foreach (var part in Regex.Split(m.Value, @"(?<!^)(?=[A-Z])|[_\-]"))
                if (part.Length >= 3) set.Add(part.ToLowerInvariant());
        }
        return set;
    }

    private static int CountTokenHits(HashSet<string> tokens, string trigger)
    {
        trigger = trigger.Trim().ToLowerInvariant();
        if (trigger.Length == 0) return 0;
        int n = 0;
        foreach (var t in tokens)
        {
            if (t == trigger) n += 2;                       // 完全命中
            else if (t.Contains(trigger) || trigger.Contains(t)) n += 1; // 部分命中
        }
        return n;
    }
}
