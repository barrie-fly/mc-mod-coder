using System.Text.RegularExpressions;
using MCModCoder.Core.Models;

namespace MCModCoder.Core.Validation;

public enum IssueSeverity { Error, Warning, Info }

public class ValidationIssue
{
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public string Suggestion { get; set; } = "";
}

/// <summary>配方验证器：检测无效配置并给出修正建议</summary>
public static partial class RecipeValidator
{
    [GeneratedRegex(@"^[a-z0-9_.\-]+:[a-z0-9_/.\-]+$")]
    private static partial Regex IdRegex();

    public static List<ValidationIssue> Validate(RecipeDefinition r)
    {
        var issues = new List<ValidationIssue>();
        void Err(string m, string s) => issues.Add(new ValidationIssue { Severity = IssueSeverity.Error, Message = m, Suggestion = s });
        void Warn(string m, string s) => issues.Add(new ValidationIssue { Severity = IssueSeverity.Warning, Message = m, Suggestion = s });
        void Info(string m, string s) => issues.Add(new ValidationIssue { Severity = IssueSeverity.Info, Message = m, Suggestion = s });

        // 通用：ID 检查
        if (string.IsNullOrWhiteSpace(r.Id))
            Err("配方 ID 为空。", "请填写形如 mymod:my_recipe 的命名空间 ID。");
        else if (!r.Id.Contains(':'))
            Warn($"配方 ID \"{r.Id}\" 缺少命名空间前缀。", "建议改为 mymod:" + r.Id.ToLowerInvariant() + "，避免与原版配方冲突。");
        else if (!IdRegex().IsMatch(r.Id))
            Err($"配方 ID \"{r.Id}\" 含非法字符。", "ID 只能使用小写字母、数字、下划线、点、横线和斜杠。");

        // 通用：产物检查
        if (string.IsNullOrEmpty(r.Result.Item) || r.Result.Item == "minecraft:air")
            Err("产物（result）为空。", "请在产物栏选择或输入有效物品 ID，如 minecraft:diamond_sword。");
        else if (!IdRegex().IsMatch(r.Result.Item))
            Warn($"产物 ID \"{r.Result.Item}\" 格式异常。", "产物 ID 应形如 命名空间:路径（全小写）。");
        if (r.Result.Count < 1)
            Err("产物数量小于 1。", "产物数量至少为 1。");
        else if (r.Result.Count > 64 && r.Type != RecipeType.Stonecutting)
            Warn($"产物数量 {r.Result.Count} 超过一组（64）。", "超过 64 的数量在游戏中可能出现异常，建议 ≤ 64。");

        switch (r.Type)
        {
            case RecipeType.CraftingShaped:
            {
                if (r.Pattern.Count == 0 || r.Pattern.All(p => string.IsNullOrWhiteSpace(p)))
                {
                    Err("有序合成的图案（pattern）为空。", "请在 3×3 网格中放置材料，或改用无序合成。");
                    break;
                }
                var usedChars = r.Pattern.SelectMany(p => p).Where(c => c != ' ').Select(c => c.ToString()).ToHashSet();
                foreach (var ch in usedChars)
                    if (!r.Key.ContainsKey(ch))
                        Err($"图案中使用的字符 '{ch}' 未在 key 中定义。", $"请在键位表中为 '{ch}' 指定对应材料。");
                foreach (var ch in r.Key.Keys)
                    if (!usedChars.Contains(ch))
                        Warn($"key 中的字符 '{ch}' 未被图案使用。", $"删除 '{ch}' 的键位定义，或修改图案使用它。");
                if (!r.Pattern.Any(p => p.Trim().Length > 0))
                    Info("图案全部为空格。", "这等同于空配方，请至少放置一个材料。");
                int rowLen = r.Pattern[0].Length;
                if (r.Pattern.Any(p => p.Length != rowLen))
                    Err("图案各行长度不一致。", "每行（含尾部空格）长度必须相同，可用空格补齐。");
                if (r.Pattern.Count > 3)
                    Err("图案超过 3 行。", "合成台配方最多 3×3。");
                if (!usedChars.Any())
                    Err("未使用任何材料。", "请在网格中放置至少一个材料。");
                break;
            }
            case RecipeType.CraftingShapeless:
            {
                var list = r.ShapelessIngredients.Where(i => !i.IsEmpty).ToList();
                if (list.Count == 0)
                    Err("无序合成未放置任何材料。", "请添加 1-9 个材料。");
                else if (list.Count > 9)
                    Err($"无序合成材料数量 {list.Count} 超过上限 9。", "无序合成最多支持 9 个材料。");
                if (list.Count == 1)
                    Info("只有一个材料。", "单材料合成同样合法，但请确认这不是你想要的切石/熔炼配方。");
                break;
            }
            case RecipeType.Smelting or RecipeType.Blasting or RecipeType.Smoking or RecipeType.CampfireCooking:
            {
                if (r.Ingredient.IsEmpty)
                    Err("未设置输入材料（ingredient）。", "请在输入栏选择被冶炼的物品，如 minecraft:iron_ore。");
                if (r.Type == RecipeType.Smelting && r.CookingTime != 200)
                    Info($"熔炉 cookingtime={r.CookingTime}（原版默认 200）。", "修改烧炼时间可能影响燃料效率平衡。");
                if (r.Type == RecipeType.Blasting && r.CookingTime == 200)
                    Info("高炉默认耗时为 100 刻。", "当前仍为熔炉默认值 200，若想保留高炉快速冶炼特性建议设为 100。");
                break;
            }
            case RecipeType.Stonecutting:
            {
                if (r.Ingredient.IsEmpty)
                    Err("切石机配方缺少输入材料。", "请在输入栏放置石类材料。");
                if (r.Result.Count > 1 && !IdRegex().IsMatch(r.Result.Item))
                    Info("切石机产物数量可 >1，但产物 ID 仍需合法。", "");
                break;
            }
            case RecipeType.Smithing:
            {
                if (r.Base == null || r.Base.IsEmpty)
                    Err("锻造台配方缺少基础物品（base）。", "例如 minecraft:diamond_pickaxe。");
                if (r.Addition == null || r.Addition.IsEmpty)
                    Err("锻造台配方缺少附加材料（addition）。", "例如 minecraft:netherite_ingot。");
                if (r.Template == null || r.Template.IsEmpty)
                    Warn("未设置锻造模板（template）。", "1.20+ 的 smithing_transform 配方建议提供 template，如 minecraft:netherite_upgrade_smithing_template。");
                break;
            }
            case RecipeType.Brewing:
            {
                if (r.Ingredient.IsEmpty)
                    Err("酿造台配方缺少酿造材料（ingredient）。", "例如 minecraft:nether_wart。");
                if (r.BrewingBase == null || r.BrewingBase.IsEmpty)
                    Warn("未设置底酒（base）。", "酿造台通常以水瓶/粗制药水为基底，如 minecraft:potion。");
                break;
            }
        }

        return issues;
    }

    /// <summary>批量验证并汇总（ID 为空或重复时自动去重并编号，不再抛出异常）</summary>
    public static Dictionary<string, List<ValidationIssue>> ValidateAll(IEnumerable<RecipeDefinition> recipes)
    {
        var result = new Dictionary<string, List<ValidationIssue>>(StringComparer.Ordinal);
        foreach (var r in recipes)
        {
            var issues = Validate(r);
            var key = string.IsNullOrWhiteSpace(r.Id) ? "(未命名)" : r.Id;
            if (result.TryAdd(key, issues)) continue;
            int n = 2;
            while (!result.TryAdd($"{key}#{n}", issues)) n++;
        }
        return result;
    }
}
