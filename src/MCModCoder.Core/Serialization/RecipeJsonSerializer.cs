using System.Text.Json;
using System.Text.Json.Nodes;
using MCModCoder.Core.Models;

namespace MCModCoder.Core.Serialization;

/// <summary>配方 ↔ Minecraft 数据包 JSON 双向序列化</summary>
public static class RecipeJsonSerializer
{
    private static readonly JsonSerializerOptions Indent = new() { WriteIndented = true };

    /// <summary>导出为 Minecraft 数据包格式 JSON</summary>
    public static string ToDatapackJson(RecipeDefinition r)
    {
        var root = new JsonObject { ["type"] = r.Type.ToJsonType() };

        switch (r.Type)
        {
            case RecipeType.CraftingShaped:
            {
                var pattern = new JsonArray();
                foreach (var row in r.Pattern) pattern.Add(row);
                root["pattern"] = pattern;
                var key = new JsonObject();
                foreach (var (ch, ing) in r.Key)
                    key[ch] = IngJson(ing);
                root["key"] = key;
                break;
            }
            case RecipeType.CraftingShapeless:
            {
                var arr = new JsonArray();
                foreach (var ing in r.ShapelessIngredients.Where(i => !i.IsEmpty))
                    arr.Add(IngJson(ing));
                root["ingredients"] = arr;
                break;
            }
            case RecipeType.Smelting or RecipeType.Blasting or RecipeType.Smoking or RecipeType.CampfireCooking:
            {
                root["ingredient"] = IngJson(r.Ingredient);
                root["cookingtime"] = r.CookingTime;
                root["experience"] = r.Experience;
                break;
            }
            case RecipeType.Stonecutting:
            {
                root["ingredient"] = IngJson(r.Ingredient);
                root["count"] = Math.Max(1, r.Result.Count);
                root["result"] = r.Result.Item;
                goto End;
            }
            case RecipeType.Smithing:
            {
                if (r.Template != null) root["template"] = IngJson(r.Template);
                root["base"] = IngJson(r.Base ?? new IngredientSpec());
                root["addition"] = IngJson(r.Addition ?? new IngredientSpec());
                break;
            }
            case RecipeType.Brewing:
            {
                root["ingredient"] = IngJson(r.Ingredient);
                if (r.BrewingBase != null) root["base"] = IngJson(r.BrewingBase);
                root["brewing_time"] = r.CookingTime;
                break;
            }
        }

        if (r.Type != RecipeType.Stonecutting)
        {
            var result = new JsonObject { ["item"] = r.Result.Item };
            if (r.Result.Count != 1) result["count"] = Math.Max(1, r.Result.Count);
            if (!string.IsNullOrEmpty(r.Result.Nbt)) result["nbt"] = r.Result.Nbt;
            root["result"] = result;
        }

        End:
        return root.ToJsonString(Indent);
    }

    private static JsonNode IngJson(IngredientSpec ing)
    {
        if (!string.IsNullOrEmpty(ing.Tag)) return ing.Tag;
        return ing.Item ?? "minecraft:air";
    }

    /// <summary>从 JSON（数据包或工作台格式）解析配方</summary>
    public static RecipeDefinition? FromJson(string id, string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root) return null;
            var r = new RecipeDefinition { Id = id };
            r.Type = RecipeTypeMap.FromJsonType(GetStr(root, "type") ?? "minecraft:crafting_shaped");

            if (root["pattern"] is JsonArray pat)
            {
                r.Pattern = pat.Select(p =>
                {
                    if (p is JsonValue v && v.TryGetValue<string>(out var s)) return s;
                    return "   ";
                }).ToList();
                while (r.Pattern.Count < 3) r.Pattern.Add("   ");
                r.Pattern = r.Pattern.Take(3).ToList();
                r.Pattern = r.Pattern.Select(s => s.PadRight(3)).Select(s => s.Length > 3 ? s[..3] : s).ToList();
            }
            if (root["key"] is JsonObject keyObj)
            {
                r.Key.Clear();
                foreach (var (ch, v) in keyObj)
                    if (!string.IsNullOrEmpty(ch) && ch.Length == 1)   // 忽略多字符键（无法对应图案字符）
                        r.Key[ch] = ParseIng(v);
            }
            if (root["ingredients"] is JsonArray ingArr)
            {
                r.ShapelessIngredients.Clear();
                foreach (var it in ingArr)
                {
                    var ing = ParseIng(it);
                    if (!ing.IsEmpty) r.ShapelessIngredients.Add(ing);
                }
            }
            if (root["ingredient"] != null) r.Ingredient = ParseIng(root["ingredient"]);
            if (root["base"] != null)
            {
                if (r.Type == RecipeType.Brewing) r.BrewingBase = ParseIng(root["base"]);
                else r.Base = ParseIng(root["base"]);
            }
            if (root["addition"] != null) r.Addition = ParseIng(root["addition"]);
            if (root["template"] != null) r.Template = ParseIng(root["template"]);

            var res = root["result"];
            if (res is JsonObject resObj)
            {
                r.Result.Item = GetStr(resObj, "item") ?? GetStr(resObj, "id") ?? "minecraft:stone";
                r.Result.Count = GetInt(resObj, "count", 1);
                r.Result.Nbt = GetStr(resObj, "nbt");
            }
            else if (res != null && r.Type == RecipeType.Stonecutting)
            {
                r.Result.Item = GetStr(root, "result") ?? "minecraft:stone";
                r.Result.Count = GetInt(root, "count", 1);
            }

            r.CookingTime = root["cookingtime"] != null ? GetInt(root, "cookingtime", 200)
                          : root["brewing_time"] != null ? GetInt(root, "brewing_time", 200) : 200;
            r.Experience = GetDouble(root, "experience", 0.1);
            return r;
        }
        catch
        {
            return null;
        }
    }

    // ---- 容错取值辅助：字段类型异常（字符串数字 / 浮点整数）时不再导致整体解析失败 ----
    private static string? GetStr(JsonObject o, string name)
    {
        if (o[name] is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private static int GetInt(JsonObject o, string name, int def)
    {
        if (o[name] is not JsonValue v) return def;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return def;
    }

    private static double GetDouble(JsonObject o, string name, double def)
    {
        if (o[name] is not JsonValue v) return def;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return def;
    }

    private static IngredientSpec ParseIng(JsonNode? n)
    {
        if (n == null) return new IngredientSpec();
        if (n is JsonArray arr)  // 数组配料取第一个
            return ParseIng(arr.FirstOrDefault());
        var s = n.GetValue<string>();
        return IngredientSpec.Parse(s);
    }

    /// <summary>批量导出到数据包目录结构 data/&lt;ns&gt;/recipe(s)/&lt;path&gt;.json（路径净化防穿越 + 重名自动编号）</summary>
    public static List<string> ExportToDatapack(IEnumerable<RecipeDefinition> recipes, string datapackRoot, string nameSpace, int packFormat = 15)
    {
        var written = new List<string>();
        // 命名空间作为目录名，净化非法字符
        nameSpace = SanitizeSegment(string.IsNullOrWhiteSpace(nameSpace) ? "mymod" : nameSpace);
        var recipeDir = Path.Combine(datapackRoot, "data", nameSpace, "recipe");
        Directory.CreateDirectory(recipeDir);
        var recipeDirFull = Path.GetFullPath(recipeDir);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recipes)
        {
            var raw = r.Id.Contains(':') ? r.Id.Split(':', 2)[1] : r.Id;
            var path = SanitizeRelPath(raw);
            if (path.Length == 0) path = "unnamed";
            var baseName = path;
            int n = 2;
            while (!usedNames.Add(path)) path = $"{baseName}_{n++}";
            var file = Path.GetFullPath(Path.Combine(recipeDir, path + ".json"));
            // 防路径穿越：目标必须位于配方目录内
            if (!file.StartsWith(recipeDirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            File.WriteAllText(file, ToDatapackJson(r));
            written.Add(file);
        }
        var meta = Path.Combine(datapackRoot, "pack.mcmeta");
        File.WriteAllText(meta,
            $$"""{ "pack": { "pack_format": {{packFormat}}, "description": "由MC模组代码工作台生成的配方数据包" } }""");
        written.Add(meta);
        return written;
    }

    private static string SanitizeSegment(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>净化相对路径：按 / 分段，剔除 . / .. 与非法字符，防止写出目标目录之外</summary>
    private static string SanitizeRelPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = (s ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (p is "." or "..") continue;
            if (sb.Length > 0) sb.Append('/');
            foreach (var c in p) sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
