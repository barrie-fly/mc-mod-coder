using System.Text.Json.Serialization;

namespace MCModCoder.Core.Models;

public enum RecipeType
{
    CraftingShaped,      // minecraft:crafting_shaped
    CraftingShapeless,   // minecraft:crafting_shapeless
    Smelting,            // minecraft:smelting
    Blasting,            // minecraft:blasting
    Smoking,             // minecraft:smoking
    CampfireCooking,     // minecraft:campfire_cooking
    Stonecutting,        // minecraft:stonecutting
    Smithing,            // minecraft:smithing_transform
    Brewing              // 自定义扩展（工作台导出为 mcmcoder:brewing）
}

public static class RecipeTypeMap
{
    public static string ToJsonType(this RecipeType t) => t switch
    {
        RecipeType.CraftingShaped => "minecraft:crafting_shaped",
        RecipeType.CraftingShapeless => "minecraft:crafting_shapeless",
        RecipeType.Smelting => "minecraft:smelting",
        RecipeType.Blasting => "minecraft:blasting",
        RecipeType.Smoking => "minecraft:smoking",
        RecipeType.CampfireCooking => "minecraft:campfire_cooking",
        RecipeType.Stonecutting => "minecraft:stonecutting",
        RecipeType.Smithing => "minecraft:smithing_transform",
        RecipeType.Brewing => "mcmcoder:brewing",
        _ => "minecraft:crafting_shaped"
    };

    public static RecipeType FromJsonType(string s) => s switch
    {
        "minecraft:crafting_shaped" => RecipeType.CraftingShaped,
        "minecraft:crafting_shapeless" => RecipeType.CraftingShapeless,
        "minecraft:smelting" => RecipeType.Smelting,
        "minecraft:blasting" => RecipeType.Blasting,
        "minecraft:smoking" => RecipeType.Smoking,
        "minecraft:campfire_cooking" => RecipeType.CampfireCooking,
        "minecraft:stonecutting" => RecipeType.Stonecutting,
        "minecraft:smithing_transform" or "minecraft:smithing" => RecipeType.Smithing,
        "mcmcoder:brewing" => RecipeType.Brewing,
        _ => RecipeType.CraftingShaped
    };

    public static string DisplayName(this RecipeType t) => t switch
    {
        RecipeType.CraftingShaped => "有序合成（合成台 3×3）",
        RecipeType.CraftingShapeless => "无序合成",
        RecipeType.Smelting => "熔炉冶炼",
        RecipeType.Blasting => "高炉冶炼",
        RecipeType.Smoking => "烟熏炉",
        RecipeType.CampfireCooking => "篝火烹饪",
        RecipeType.Stonecutting => "切石机",
        RecipeType.Smithing => "锻造台升级",
        RecipeType.Brewing => "酿造台",
        _ => t.ToString()
    };

    public static bool IsCooking(this RecipeType t) =>
        t is RecipeType.Smelting or RecipeType.Blasting or RecipeType.Smoking or RecipeType.CampfireCooking;
}

/// <summary>配料（物品 ID 或标签）</summary>
public class IngredientSpec
{
    /// <summary>物品 ID，如 minecraft:iron_ingot</summary>
    public string? Item { get; set; }
    /// <summary>标签，如 #minecraft:planks（与 Item 二选一）</summary>
    public string? Tag { get; set; }

    [JsonIgnore]
    public string Display => Tag != null ? Tag : (Item ?? "air");

    public bool IsEmpty => string.IsNullOrEmpty(Item) && string.IsNullOrEmpty(Tag);

    public static IngredientSpec Parse(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith("#"))
            return new IngredientSpec { Tag = text };
        return new IngredientSpec { Item = string.IsNullOrWhiteSpace(text) || text == "air" ? "minecraft:air" : text };
    }

    public override string ToString() => Display;
}

/// <summary>产物</summary>
public class ResultSpec
{
    public string Item { get; set; } = "minecraft:stone";
    public int Count { get; set; } = 1;
    /// <summary>酿造台/药水产物的 NBT（可选）</summary>
    public string? Nbt { get; set; }
}

/// <summary>配方定义（与 Minecraft 数据包 JSON 双向兼容）</summary>
public class RecipeDefinition
{
    /// <summary>配方 ID，如 mymod:iron_pickaxe</summary>
    public string Id { get; set; } = "";
    /// <summary>工作台内的分类名（便于管理/搜索）</summary>
    public string Category { get; set; } = "常规";
    public RecipeType Type { get; set; } = RecipeType.CraftingShaped;
    public string Description { get; set; } = "";

    // ---- 有序合成 ----
    public List<string> Pattern { get; set; } = new() { "   ", "   ", "   " };
    public Dictionary<string, IngredientSpec> Key { get; set; } = new();

    // ---- 无序合成（最多 9 项）----
    public List<IngredientSpec> ShapelessIngredients { get; set; } = new();

    // ---- 熔炼类 / 切石机 ----
    public IngredientSpec Ingredient { get; set; } = new();

    // ---- 锻造台 ----
    public IngredientSpec? Template { get; set; }
    public IngredientSpec? Base { get; set; }
    public IngredientSpec? Addition { get; set; }

    // ---- 酿造台 ----
    public IngredientSpec? BrewingBase { get; set; }

    // ---- 产物 ----
    public ResultSpec Result { get; set; } = new();

    // ---- 烹饪参数 ----
    public int CookingTime { get; set; } = 200;   // 刻
    public double Experience { get; set; } = 0.1;

    public bool UsesGrid => Type is RecipeType.CraftingShaped or RecipeType.CraftingShapeless;
    public bool UsesSingleIngredient => Type.IsCooking() || Type == RecipeType.Stonecutting;
}
