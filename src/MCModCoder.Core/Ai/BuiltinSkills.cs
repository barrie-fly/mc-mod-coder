using System.IO.Compression;
using System.Text.RegularExpressions;
using MCModCoder.Core.Models;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Services;

namespace MCModCoder.Core.Ai;

/// <summary>
/// 内置技能集：把工作台既有能力（文件读写、脚手架、代码生成、资源配置、构建、打包验证）
/// 以 <see cref="ISkill"/> 接口形式封装，供 AI 引擎注册、发现与调用。
/// 调用 <see cref="AiSkills.RegisterAll"/> 一键注册全部内置技能。
/// </summary>
public static class AiSkills
{
    /// <summary>16×16 占位纹理（纯色 PNG，内置 base64，供 AI 生成资源时的兜底贴图）</summary>
    internal static byte[] PlaceholderPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAFklEQVR4nGPYpGRCEmIY1TCqYfhqAADYvAgQtZH+hQAAAABJRU5ErkJggg==");

    /// <summary>注册全部内置技能，返回注册数量（幂等，同名覆盖）</summary>
    public static int RegisterAll(SkillRegistry registry)
    {
        ISkill[] all =
        {
            new ReadFileSkill(), new WriteFileSkill(), new ListFilesSkill(),
            new AnalyzeRequirementSkill(), new ScaffoldProjectSkill(),
            new GenerateCodeSkill(), new SnippetSkill(),
            new GenerateResourceSkill(), new GenerateRecipeSkill(), new ExportResourcePackSkill(),
            new BuildProjectSkill(), new PackageModpackSkill(), new VerifyArtifactSkill(),
        };
        foreach (var s in all) registry.Register(s);
        return all.Length;
    }

    // ================= 共享工具 =================

    /// <summary>
    /// 将相对路径解析为项目目录内的绝对路径（路径穿越防御：拒绝 ".."、盘符、根目录转义）。
    /// 无项目上下文时返回 false。
    /// </summary>
    internal static bool TryResolveInProject(SkillContext ctx, string rel, out string full, out string error)
    {
        full = ""; error = "";
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) { error = "缺少项目上下文，请先创建或打开项目"; return false; }
        if (string.IsNullOrWhiteSpace(rel)) { error = "路径为空"; return false; }
        rel = rel.Replace('\\', '/').Trim();
        if (Path.IsPathRooted(rel) || rel.Contains(':') || rel.Split('/').Contains(".."))
        { error = $"非法路径（禁止绝对路径与 .. 穿越）：{rel}"; return false; }
        full = Path.GetFullPath(Path.Combine(p.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(p.ProjectDir), StringComparison.OrdinalIgnoreCase))
        { error = $"路径越出项目目录：{rel}"; return false; }
        return true;
    }
}

// ================= 文件读写 =================

/// <summary>读取项目内文本文件</summary>
public class ReadFileSkill : ISkill
{
    public string Name => "file.read";
    public string DisplayName => "读取文件";
    public string Description => "读取项目内文本文件内容（JSON / Java / 配置等），路径相对项目根目录";
    public SkillCategory Category => SkillCategory.File;
    public IReadOnlyList<SkillParam> Params { get; } = new[] { new SkillParam("path", "相对项目根目录的文件路径", true) };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        if (!AiSkills.TryResolveInProject(ctx, args.GetValueOrDefault("path", ""), out var full, out var err))
            return SkillResult.Fail(err);
        if (!File.Exists(full)) return SkillResult.Fail($"文件不存在：{args["path"]}");
        var content = File.ReadAllText(full);
        return SkillResult.Ok($"已读取 {args["path"]}（{content.Length} 字符）",
            new() { ["path"] = args["path"], ["content"] = content });
    }
}

/// <summary>写入项目内文本文件（自动创建目录）</summary>
public class WriteFileSkill : ISkill
{
    public string Name => "file.write";
    public string DisplayName => "写入文件";
    public string Description => "把文本内容写入项目内文件（自动建目录，Java 源码 / JSON 资源 / 配置），路径相对项目根目录";
    public SkillCategory Category => SkillCategory.File;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("path", "相对项目根目录的文件路径", true),
        new SkillParam("content", "要写入的文本内容", true),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        if (!AiSkills.TryResolveInProject(ctx, args.GetValueOrDefault("path", ""), out var full, out var err))
            return SkillResult.Fail(err);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, args.GetValueOrDefault("content", ""));
        return SkillResult.Ok($"已写入 {args["path"]}（{args.GetValueOrDefault("content", "").Length} 字符）",
            new() { ["path"] = args["path"], ["fullPath"] = full });
    }
}

/// <summary>列出项目内文件</summary>
public class ListFilesSkill : ISkill
{
    public string Name => "file.list";
    public string DisplayName => "列出文件";
    public string Description => "列出项目目录（默认根目录）下的文件清单，支持通配符过滤，用于了解项目现状";
    public SkillCategory Category => SkillCategory.File;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("dir", "相对项目根目录的子目录（可空）"),
        new SkillParam("pattern", "通配符（如 *.java，默认 *）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var dir = p.ProjectDir;
        var sub = args.GetValueOrDefault("dir", "").Trim();
        if (sub.Length > 0)
        {
            if (!AiSkills.TryResolveInProject(ctx, sub, out var subFull, out var err)) return SkillResult.Fail(err);
            if (!Directory.Exists(subFull)) return SkillResult.Fail($"目录不存在：{sub}");
            dir = subFull;
        }
        var pattern = string.IsNullOrWhiteSpace(args.GetValueOrDefault("pattern")) ? "*" : args["pattern"];
        var files = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(p.ProjectDir, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).Take(200).ToList();
        return SkillResult.Ok($"项目{(string.IsNullOrEmpty(sub) ? "根目录" : sub)}共列出 {files.Count} 个文件",
            new() { ["count"] = files.Count.ToString(), ["files"] = string.Join("\n", files) });
    }
}

// ================= 分析与脚手架 =================

/// <summary>需求分析：把自然语言需求解析为结构化要素（供规划器与其他技能使用）</summary>
public class AnalyzeRequirementSkill : ISkill
{
    public string Name => "plan.analyze";
    public string DisplayName => "需求分析";
    public string Description => "解析自然语言开发需求：提取模组名 / MC 版本 / 加载器 / 物品方块等功能要素 / 配方与整合包意图";
    public SkillCategory Category => SkillCategory.Planning;
    public IReadOnlyList<SkillParam> Params { get; } = new[] { new SkillParam("requirement", "自然语言需求描述", true) };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var req = args.GetValueOrDefault("requirement", "").Trim();
        if (req.Length == 0) return SkillResult.Fail("需求描述为空");
        var facts = RequirementAnalyzer.Analyze(req);
        ctx.Scratch["facts"] = System.Text.Json.JsonSerializer.Serialize(facts);

        var summary = $"需求要素：模组「{facts.ModName ?? "(未指明)"}」 MC {facts.McVersion ?? "(默认)"} / {facts.Loader ?? "(默认)"}；" +
                      $"功能点 {facts.Features.Count} 个{string.Join("", facts.Features.Select(f => $"、{f.Kind}:{f.Id}"))}" +
                      $"；{(facts.WantsRecipe ? "含配方，" : "")}{(facts.WantsModpack ? "需整合包" : "模组 jar")}";
        return SkillResult.Ok(summary, new()
        {
            ["modName"] = facts.ModName ?? "",
            ["mcVersion"] = facts.McVersion ?? "",
            ["loader"] = facts.Loader ?? "",
            ["featureCount"] = facts.Features.Count.ToString(),
            ["wantsRecipe"] = facts.WantsRecipe ? "1" : "0",
            ["wantsModpack"] = facts.WantsModpack ? "1" : "0",
        });
    }
}

/// <summary>项目脚手架：为当前项目生成完整可构建的 Gradle 工程（包装 TemplateGenerator）</summary>
public class ScaffoldProjectSkill : ISkill
{
    public string Name => "project.scaffold";
    public string DisplayName => "生成项目脚手架";
    public string Description => "按项目设置的 MC 版本与加载器生成 build.gradle、mods.toml、主类、pack.mcmeta 等完整工程（已存在时跳过）";
    public SkillCategory Category => SkillCategory.Code;
    public IReadOnlyList<SkillParam> Params { get; } = Array.Empty<SkillParam>();

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        if (File.Exists(Path.Combine(p.ProjectDir, "build.gradle")))
            return SkillResult.Ok("检测到 build.gradle，工程已存在，跳过脚手架生成", new() { ["skipped"] = "1" });
        var written = TemplateGenerator.Generate(p);
        return SkillResult.Ok($"脚手架生成完成，共 {written.Count} 个文件", new() { ["files"] = written.Count.ToString() });
    }
}

// ================= 代码生成 =================

/// <summary>代码生成：按功能要素生成对应加载器的 Java 注册代码</summary>
public class GenerateCodeSkill : ISkill
{
    public string Name => "code.generate";
    public string DisplayName => "生成 Java 代码";
    public string Description => "生成物品/工具/食物/方块的 Java 注册类（按加载器选用 Forge/Fabric/NeoForge 模板）并写入源码目录";
    public SkillCategory Category => SkillCategory.Code;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("id", "注册 ID（小写字母数字下划线，如 ruby_pickaxe）", true),
        new SkillParam("kind", "类别：item / tool / food / block（默认 item）"),
        new SkillParam("name", "显示名（如 红宝石，可选）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var id = SanitizeId(args.GetValueOrDefault("id", ""));
        if (id.Length == 0) return SkillResult.Fail("注册 ID 非法（需为小写字母数字下划线）");
        var kind = (args.GetValueOrDefault("kind", "item") ?? "item").ToLowerInvariant() switch
        {
            "tool" => "tool", "food" => "food", "block" => "block", _ => "item"
        };
        var display = args.GetValueOrDefault("name", "") is { Length: > 0 } n ? n : id;

        string code = BuildJava(p, id, kind, display);
        var rel = $"src/main/java/{p.JavaPackagePath}/{Pascal(id)}.java";
        Directory.CreateDirectory(Path.Combine(p.ProjectDir, "src", "main", "java", p.JavaPackagePath));
        File.WriteAllText(Path.Combine(p.ProjectDir, rel.Replace('/', Path.DirectorySeparatorChar)), code);
        return SkillResult.Ok($"已生成 {kind} 注册类 {Pascal(id)}.java（{p.ModLoader} 模板）",
            new() { ["path"] = rel, ["className"] = Pascal(id), ["id"] = id, ["kind"] = kind });
    }

    internal static string SanitizeId(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (s ?? "").ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        var r = sb.ToString().Trim('_');
        if (r.Length == 0 || char.IsDigit(r[0])) return "";
        return r.Length > 64 ? r[..64] : r;
    }

    internal static string Pascal(string id)
    {
        return string.Concat(id.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? "" : char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string BuildJava(McProject p, string id, string kind, string display)
    {
        string cls = Pascal(id);
        string pkg = p.Package;
        string main = p.MainClass;
        string upper = id.ToUpperInvariant();
        return p.ModLoader switch
        {
            "Fabric" or "Rift" => $$"""
            package {{pkg}};

            import net.minecraft.item.Item;
            import net.minecraft.registry.Registries;
            import net.minecraft.registry.Registry;
            import net.minecraft.util.Identifier;

            /** AI 生成：{{display}}（{{kind}}）注册 — Fabric/Rift */
            public class {{cls}} {
                public static final Item {{upper}} = Registry.register(Registries.ITEM,
                        new Identifier({{main}}.MODID, "{{id}}"), new Item(new Item.Settings()));
            }
            """,
            "NeoForge" => $$"""
            package {{pkg}};

            import net.minecraft.world.item.Item;
            import net.neoforged.neoforge.registries.DeferredRegister;
            import net.neoforged.neoforge.registries.DeferredHolder;
            import net.minecraft.core.registries.Registries;

            /** AI 生成：{{display}}（{{kind}}）注册 — NeoForge */
            public class {{cls}} {
                public static final DeferredRegister<Item> ITEMS =
                        DeferredRegister.create(Registries.ITEM, {{main}}.MODID);
                public static final DeferredHolder<Item, Item> {{upper}} =
                        ITEMS.register("{{id}}", () -> new Item(new Item.Properties()));
            }
            """,
            // Forge（含 1.12.2 差异由分支处理）
            _ => $$"""
            package {{pkg}};

            import net.minecraft.world.item.Item;
            import net.minecraftforge.registries.DeferredRegister;
            import net.minecraftforge.registries.ForgeRegistries;
            import net.minecraftforge.registries.RegistryObject;

            /** AI 生成：{{display}}（{{kind}}）注册 — Forge */
            public class {{cls}} {
                public static final DeferredRegister<Item> ITEMS =
                        DeferredRegister.create(ForgeRegistries.ITEMS, {{main}}.MODID);
                public static final RegistryObject<Item> {{upper}} =
                        ITEMS.register("{{id}}", () -> new Item(new Item.Properties()));
            }
            """,
        };
    }
}

/// <summary>片段插入：从片段库取模板代码（包装 SnippetLibrary）</summary>
public class SnippetSkill : ISkill
{
    public string Name => "code.snippet";
    public string DisplayName => "获取代码片段";
    public string Description => "从内置片段库按名称取模板代码（占位符 {{modid}}/{{MainClass}} 按项目替换），返回代码文本供写文件";
    public SkillCategory Category => SkillCategory.Code;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("name", "片段名（如 Forge 主类 / Forge 注册物品）", true),
        new SkillParam("file", "写入的目标文件相对路径（可选；提供则直接写入）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var name = args.GetValueOrDefault("name", "").Trim();
        var snip = SnippetLibrary.Builtin.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(name));
        if (snip == null)
            return SkillResult.Fail($"未找到片段「{name}」",
                "可用片段：" + string.Join("、", SnippetLibrary.Builtin.Select(s => s.Name)));
        var p = ctx.Project;
        var code = snip.Code
            .Replace("{{modid}}", p?.ModId ?? "mymod")
            .Replace("{{MODID_UPPER}}", (p?.ModId ?? "mymod").ToUpperInvariant())
            .Replace("{{MainClass}}", p?.MainClass ?? "MyMod")
            .Replace("{{package}}", p?.Package ?? "com.example.mymod");

        var outputs = new Dictionary<string, string> { ["code"] = code, ["snippet"] = snip.Name };
        var file = args.GetValueOrDefault("file", "").Trim();
        if (file.Length > 0)
        {
            if (!AiSkills.TryResolveInProject(ctx, file, out var full, out var err)) return SkillResult.Fail(err);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, code);
            outputs["path"] = file;
            return SkillResult.Ok($"片段「{snip.Name}」已写入 {file}", outputs);
        }
        return SkillResult.Ok($"已获取片段「{snip.Name}」（{code.Length} 字符）", outputs);
    }
}

// ================= 资源配置 =================

/// <summary>资源生成：为注册 ID 生成物品模型 JSON 与占位纹理，写入 src/main/resources</summary>
public class GenerateResourceSkill : ISkill
{
    public string Name => "resource.generate";
    public string DisplayName => "生成物品资源";
    public string Description => "按注册 ID 生成模型 JSON（item/generated）与占位纹理 PNG 到 src/main/resources/assets/<modid>/，进入模组 jar";
    public SkillCategory Category => SkillCategory.Resource;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("ids", "注册 ID，多个用英文逗号分隔（如 ruby,ruby_pickaxe）", true),
        new SkillParam("kind", "item / block（默认 item）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var ids = (args.GetValueOrDefault("ids", "") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GenerateCodeSkill.SanitizeId).Where(s => s.Length > 0).Distinct().ToList();
        if (ids.Count == 0) return SkillResult.Fail("没有合法的注册 ID");
        var isBlock = string.Equals(args.GetValueOrDefault("kind", "item"), "block", StringComparison.OrdinalIgnoreCase);
        var modid = p.ModId.ToLowerInvariant();

        int written = 0;
        foreach (var id in ids)
        {
            var rel = $"assets/{modid}/{(isBlock ? "block" : "item")}/{id}";
            var modelDir = $"src/main/resources/assets/{modid}/models/{(isBlock ? "block" : "item")}";
            var modelJson = isBlock
                ? $$"""{ "parent": "block/cube_all", "textures": { "all": "{{modid}}:block/{{id}}" } }"""
                : $$"""{ "parent": "item/generated", "textures": { "layer0": "{{modid}}:item/{{id}}" } }""";
            var modelFull = Path.Combine(p.ProjectDir, modelDir.Replace('/', Path.DirectorySeparatorChar), id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(modelFull)!);
            File.WriteAllText(modelFull, modelJson);
            written++;

            var pngDir = $"src/main/resources/assets/{modid}/textures/{(isBlock ? "block" : "item")}";
            var pngFull = Path.Combine(p.ProjectDir, pngDir.Replace('/', Path.DirectorySeparatorChar), id + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(pngFull)!);
            if (!File.Exists(pngFull)) File.WriteAllBytes(pngFull, AiSkills.PlaceholderPng);
            written++;
        }
        return SkillResult.Ok($"已生成 {ids.Count} 个 ID 的模型与占位纹理（共写 {written} 个文件）",
            new() { ["ids"] = string.Join(",", ids), ["files"] = written.ToString() });
    }
}

/// <summary>配方生成：构造 RecipeDefinition 并写入数据包目录 src/main/resources/data/&lt;modid&gt;/recipes</summary>
public class GenerateRecipeSkill : ISkill
{
    public string Name => "resource.recipe";
    public string DisplayName => "生成配方";
    public string Description => "生成合成/熔炼配方 JSON 到 src/main/resources/data/<modid>/recipes/，格式 pattern=A/B/C，key=X=minecraft:iron_ingot";
    public SkillCategory Category => SkillCategory.Resource;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("result", "产物注册 ID（如 ruby_pickaxe）", true),
        new SkillParam("type", "shaped / shapeless / smelting（默认 shaped）"),
        new SkillParam("pattern", "有序图案，分号分行，空格表示空槽并决定列对齐（如 XX; #; #）"),
        new SkillParam("key", "图案键映射，分号分隔（如 X=minecraft:iron_ingot;#=minecraft:stick）"),
        new SkillParam("ingredients", "无序/熔炼原料，逗号分隔（如 minecraft:iron_ingot,minecraft:stick）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var result = GenerateCodeSkill.SanitizeId(args.GetValueOrDefault("result", ""));
        if (result.Length == 0) return SkillResult.Fail("产物 ID 非法");
        var typeStr = (args.GetValueOrDefault("type", "shaped") ?? "shaped").ToLowerInvariant();
        var type = typeStr switch
        {
            "shapeless" => RecipeType.CraftingShapeless,
            "smelting" => RecipeType.Smelting,
            "blasting" => RecipeType.Blasting,
            "stonecutting" => RecipeType.Stonecutting,
            _ => RecipeType.CraftingShaped,
        };

        var recipe = new RecipeDefinition
        {
            Id = $"{p.ModId.ToLowerInvariant()}:{result}",
            Type = type,
            Result = new ResultSpec { Item = $"{p.ModId.ToLowerInvariant()}:{result}", Count = 1 },
            Description = "AI 引擎生成",
        };

        if (type == RecipeType.CraftingShaped)
        {
            // 保留行内空格（前导空格决定列对齐），仅去掉行尾换行残留
            var rows = (args.GetValueOrDefault("pattern", "") ?? "")
                .Split(';', StringSplitOptions.None)
                .Select(r => r.TrimEnd('\r')).ToList();
            while (rows.Count < 3) rows.Add(rows.Count > 0 ? new string(' ', rows[0].Length) : "   ");
            recipe.Pattern = rows.Take(3).ToList();
            foreach (var pair in (args.GetValueOrDefault("key", "") ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2 && kv[0].Length == 1)
                    recipe.Key[kv[0][0].ToString()] = IngredientSpec.Parse(kv[1]);
            }
            var issues = Validation.RecipeValidator.Validate(recipe)
                .Where(i => i.Severity == Validation.IssueSeverity.Error).ToList();
            if (issues.Count > 0)
                return SkillResult.Fail("配方校验失败：" + string.Join("；", issues.Take(3).Select(i => i.Message)));
        }
        else if (type == RecipeType.CraftingShapeless)
        {
            foreach (var ing in (args.GetValueOrDefault("ingredients", "") ?? "")
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                recipe.ShapelessIngredients.Add(IngredientSpec.Parse(ing));
            if (recipe.ShapelessIngredients.Count == 0) return SkillResult.Fail("无序配方缺少原料（ingredients）");
        }
        else
        {
            var first = (args.GetValueOrDefault("ingredients", "") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first)) return SkillResult.Fail("熔炼配方缺少原料（ingredients）");
            recipe.Ingredient = IngredientSpec.Parse(first);
        }

        var dir = Path.Combine(p.ProjectDir, "src", "main", "resources", "data",
            p.ModId.ToLowerInvariant(), "recipes");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, result + ".json");
        File.WriteAllText(file, RecipeJsonSerializer.ToDatapackJson(recipe));
        return SkillResult.Ok($"配方已生成：{result}.json（{type}）",
            new() { ["path"] = $"src/main/resources/data/{p.ModId.ToLowerInvariant()}/recipes/{result}.json", ["id"] = recipe.Id });
    }
}

/// <summary>资源包导出：把项目资源整理为可分发资源包目录 + zip（包装 ResourcePackExporter）</summary>
public class ExportResourcePackSkill : ISkill
{
    public string Name => "resource.export_pack";
    public string DisplayName => "导出资源包";
    public string Description => "收集项目纹理与模型生成标准资源包（modpack/resourcepack/ + resourcepack.zip）";
    public SkillCategory Category => SkillCategory.Resource;
    public IReadOnlyList<SkillParam> Params { get; } = Array.Empty<SkillParam>();

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var assets = Path.Combine(p.ProjectDir, "src", "main", "resources", "assets", p.ModId.ToLowerInvariant(), "textures");
        var textures = new List<(string RelPath, byte[] Png)>();
        if (Directory.Exists(assets))
            foreach (var png in Directory.EnumerateFiles(assets, "*.png", SearchOption.AllDirectories))
                textures.Add((Path.GetRelativePath(assets, png)[..^4].Replace('\\', '/'), File.ReadAllBytes(png)));

        var outRoot = Path.Combine(p.ProjectDir, "modpack", "resourcepack");
        var ver = McVersions.Find(p.McVersion);
        var written = ResourcePackExporter.Generate(outRoot, p.ModId.ToLowerInvariant(), ver?.PackFormat ?? 15, textures);
        var zip = ResourcePackExporter.PackAsZip(outRoot, Path.Combine(p.ProjectDir, "modpack", "resourcepack.zip"));
        return SkillResult.Ok($"资源包已导出：{written.Count} 个文件 → {zip}",
            new() { ["dir"] = "modpack/resourcepack", ["zip"] = "modpack/resourcepack.zip", ["files"] = written.Count.ToString() });
    }
}

// ================= 构建与打包 =================

/// <summary>项目构建：存在 gradlew.bat 时执行真实 Gradle 构建，否则生成内置模拟 jar（含资源与加载器描述文件）</summary>
public class BuildProjectSkill : ISkill
{
    public string Name => "build.project";
    public string DisplayName => "构建项目";
    public string Description => "构建当前项目产出 jar：有 gradlew.bat 走真实 Gradle，否则执行内置模拟构建（打包资源到 build/libs/<modid>-1.0.0.jar）";
    public SkillCategory Category => SkillCategory.Build;
    public IReadOnlyList<SkillParam> Params { get; } = Array.Empty<SkillParam>();

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var gradlew = Path.Combine(p.ProjectDir, "gradlew.bat");
        return File.Exists(gradlew) ? RunRealGradle(ctx, p, gradlew) : RunSimulatedBuild(ctx, p);
    }

    private static SkillResult RunRealGradle(SkillContext ctx, McProject p, string gradlew)
    {
        ctx.WriteLog("  检测到 gradlew.bat，执行真实 Gradle 构建…");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{gradlew}\" build --console=plain {(p.Build.ParallelBuild ? "--parallel" : "")} {p.Build.ExtraGradleArgs}".Trim(),
            WorkingDirectory = p.ProjectDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return SkillResult.Fail("无法启动 Gradle 进程");
        var tail = new List<string>();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) { tail.Add(e.Data); ctx.WriteLog("  | " + e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) ctx.WriteLog("  | [stderr] " + e.Data); };
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        proc.WaitForExit(10 * 60 * 1000);
        var libs = Path.Combine(p.ProjectDir, p.Build.OutputDir.Replace('/', Path.DirectorySeparatorChar));
        var jar = Directory.Exists(libs)
            ? Directory.GetFiles(libs, "*.jar").OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault()
            : null;
        if (proc.ExitCode != 0 || jar == null)
            return SkillResult.Fail($"Gradle 构建失败（退出码 {proc.ExitCode}）",
                string.Join("\n", tail.TakeLast(20)));
        return SkillResult.Ok($"Gradle 构建成功：{Path.GetFileName(jar)}",
            new() { ["jar"] = Path.GetRelativePath(p.ProjectDir, jar), ["mode"] = "real" });
    }

    private static SkillResult RunSimulatedBuild(SkillContext ctx, McProject p)
    {
        var resDir = Path.Combine(p.ProjectDir, "src", "main", "resources");
        int javaFiles = 0, resFiles = 0;
        try { javaFiles = Directory.EnumerateFiles(Path.Combine(p.ProjectDir, "src"), "*.java", SearchOption.AllDirectories).Count(); } catch { }
        try { resFiles = Directory.EnumerateFiles(resDir, "*", SearchOption.AllDirectories).Count(); } catch { }
        ctx.WriteLog($"  编译 {javaFiles} 个 Java 源文件，打包 {resFiles} 个资源文件（模拟构建）");

        var libsDir = Path.Combine(p.ProjectDir, p.Build.OutputDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(libsDir);
        var jarPath = Path.Combine(libsDir, $"{p.ModId.ToLowerInvariant()}-1.0.0.jar");
        using (var fs = new FileStream(jarPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var modid = p.ModId.ToLowerInvariant();
            WriteEntry(zip, "META-INF/MANIFEST.MF",
                "Manifest-Version: 1.0\r\n" +
                $"Implementation-Title: {p.Name}\r\nBuilt-By: MCModCoder-AI-Engine\r\n\r\n");
            // 加载器描述文件（进 jar 的关键元数据）
            switch (p.ModLoader)
            {
                case "NeoForge":
                    WriteEntry(zip, "META-INF/neoforge.mods.toml", LoaderToml(p, "neoforge"));
                    break;
                case "Fabric" or "Rift":
                    WriteEntry(zip, "fabric.mod.json",
                        $$"""{ "schemaVersion": 1, "id": "{{modid}}", "version": "1.0.0", "name": "{{p.Name}}" }""");
                    break;
                default:
                    WriteEntry(zip, "META-INF/mods.toml", LoaderToml(p, "forge"));
                    if (p.McVersion == "1.12.2")
                        WriteEntry(zip, "mcmod.info",
                            $$"""[{ "modid": "{{modid}}", "name": "{{p.Name}}", "version": "1.0.0", "mcversion": "{{p.McVersion}}" }]""");
                    break;
            }
            WriteEntry(zip, "pack.mcmeta",
                $$"""{ "pack": { "pack_format": {{McVersions.Find(p.McVersion)?.PackFormat ?? 15}}, "description": "{{p.Name}} 资源" } }""");
            // 收录项目资源文件
            if (Directory.Exists(resDir))
                foreach (var f in Directory.EnumerateFiles(resDir, "*", SearchOption.AllDirectories))
                    WriteEntry(zip, Path.GetRelativePath(resDir, f).Replace('\\', '/'), File.ReadAllBytes(f));
        }
        return SkillResult.Ok($"模拟构建完成：{p.ModId.ToLowerInvariant()}-1.0.0.jar（{javaFiles} 源码 / {resFiles} 资源）",
            new() { ["jar"] = $"{p.Build.OutputDir}/{p.ModId.ToLowerInvariant()}-1.0.0.jar", ["mode"] = "simulated" });
    }

    private static string LoaderToml(McProject p, string loader) =>
        $$"""
        modLoader = "javafml"
        loaderVersion = "[1,)"
        license = "All Rights Reserved"

        [[mods]]
        modId = "{{p.ModId.ToLowerInvariant()}}"
        version = "1.0.0"
        displayName = "{{p.Name}}"
        authors = "{{p.Author}}"
        description = "{{p.Description}}"

        [[dependencies.{{p.ModId.ToLowerInvariant()}}]]
        modId = "{{loader}}"
        mandatory = true
        versionRange = "[1,)"
        ordering = "NONE"
        """;

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var e = zip.CreateEntry(entryName);
        using var w = new StreamWriter(e.Open()) { NewLine = "\n" };
        w.Write(content);
    }

    private static void WriteEntry(ZipArchive zip, string entryName, byte[] bytes)
    {
        var e = zip.CreateEntry(entryName);
        using var s = e.Open();
        s.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>整合包打包：资源包 + 数据包 + 模组清单 → modpack/ 目录并压缩</summary>
public class PackageModpackSkill : ISkill
{
    public string Name => "pack.modpack";
    public string DisplayName => "打包整合包";
    public string Description => "汇总资源包、数据包（src/main/resources/data 下的配方）与模组清单到 modpack/ 目录并压缩为 modpack.zip";
    public SkillCategory Category => SkillCategory.Package;
    public IReadOnlyList<SkillParam> Params { get; } = Array.Empty<SkillParam>();

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var modid = p.ModId.ToLowerInvariant();
        var outDir = Path.Combine(p.ProjectDir, "modpack");
        Directory.CreateDirectory(outDir);
        int n = 0;

        // 1. 资源包（复用 ExportResourcePackSkill 的产物；缺失则现场生成）
        var rp = Path.Combine(outDir, "resourcepack");
        if (!File.Exists(Path.Combine(rp, "pack.mcmeta")))
        {
            var r = new ExportResourcePackSkill().Execute(ctx, args);
            if (!r.Success) return r;
        }
        if (File.Exists(Path.Combine(rp, "pack.mcmeta"))) n++;

        // 2. 数据包：项目内 data/<modid>/recipes → modpack/datapack（含 pack.mcmeta）
        var dataDir = Path.Combine(p.ProjectDir, "src", "main", "resources", "data");
        if (Directory.Exists(dataDir) && Directory.EnumerateFiles(dataDir, "*.json", SearchOption.AllDirectories).Any())
        {
            var dpRoot = Path.Combine(outDir, "datapack");
            Directory.CreateDirectory(dpRoot);
            var ver = McVersions.Find(p.McVersion);
            File.WriteAllText(Path.Combine(dpRoot, "pack.mcmeta"),
                $$"""{ "pack": { "pack_format": {{ver?.DataPackFormat ?? 15}}, "description": "{{p.Name}} 数据包" } }""");
            n++;
            foreach (var f in Directory.EnumerateFiles(dataDir, "*.json", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dataDir, f);
                var dest = Path.Combine(dpRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, true);
                n++;
            }
        }

        // 3. 模组清单
        File.WriteAllText(Path.Combine(outDir, "modlist.txt"),
            $"# {p.Name} 整合包清单\n# MC {p.McVersion} / {p.ModLoader}\n模组本体: {modid}-1.0.0.jar\n\n依赖:\n" +
            string.Join("\n", p.Dependencies.Select(d => d.Coordinate)));
        n++;

        // 4. 压缩（先写到项目根再移入 outDir，避免把正在压缩的 zip 打包进自身）
        var zipPath = Path.Combine(outDir, "modpack.zip");
        var tmpZip = Path.Combine(p.ProjectDir, $"{p.ModId.ToLowerInvariant()}-modpack.tmp.zip");
        if (File.Exists(tmpZip)) File.Delete(tmpZip);
        ZipFile.CreateFromDirectory(outDir, tmpZip);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(tmpZip, zipPath);
        return SkillResult.Ok($"整合包已生成：modpack/（{n} 项）→ modpack.zip",
            new() { ["dir"] = "modpack", ["zip"] = "modpack/modpack.zip", ["items"] = n.ToString() });
    }
}

/// <summary>产物验证：校验 jar / 整合包的关键内容（zip 完整性、加载器描述文件、清单）</summary>
public class VerifyArtifactSkill : ISkill
{
    public string Name => "pack.verify";
    public string DisplayName => "验证产物";
    public string Description => "校验构建产物：jar 内加载器描述文件（mods.toml / fabric.mod.json 等）与整合包结构（pack.mcmeta、modlist）";
    public SkillCategory Category => SkillCategory.Package;
    public IReadOnlyList<SkillParam> Params { get; } = new[]
    {
        new SkillParam("target", "jar / modpack / all（默认 all）"),
    };

    public SkillResult Execute(SkillContext ctx, IReadOnlyDictionary<string, string> args)
    {
        var p = ctx.Project;
        if (p == null || string.IsNullOrWhiteSpace(p.ProjectDir)) return SkillResult.Fail("缺少项目上下文，请先创建或打开项目");
        var target = (args.GetValueOrDefault("target", "all") ?? "all").ToLowerInvariant();
        var report = new List<string>();
        bool ok = true;

        if (target is "jar" or "all")
        {
            var libs = Path.Combine(p.ProjectDir, p.Build.OutputDir.Replace('/', Path.DirectorySeparatorChar));
            var jar = Directory.Exists(libs)
                ? Directory.GetFiles(libs, "*.jar").OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault()
                : null;
            if (jar == null) { report.Add("✖ 未找到 jar 产物"); ok = false; }
            else
            {
                using var fs = File.OpenRead(jar);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string[] descriptors = p.ModLoader switch
                {
                    "NeoForge" => new[] { "META-INF/neoforge.mods.toml" },
                    "Fabric" or "Rift" => new[] { "fabric.mod.json" },
                    _ => new[] { "META-INF/mods.toml", "mcmod.info" },
                };
                var hit = descriptors.FirstOrDefault(names.Contains);
                if (hit != null) report.Add($"✔ {Path.GetFileName(jar)}：加载器描述文件 {hit} 存在，共 {names.Count} 个条目");
                else { report.Add($"✖ {Path.GetFileName(jar)}：缺少加载器描述文件（{string.Join(" / ", descriptors)}）"); ok = false; }
                if (!names.Contains("META-INF/MANIFEST.MF")) { report.Add("✖ 缺少 META-INF/MANIFEST.MF"); ok = false; }
            }
        }
        if (target is "modpack" or "all")
        {
            var mp = Path.Combine(p.ProjectDir, "modpack");
            if (!Directory.Exists(mp)) { report.Add("✖ 整合包目录 modpack/ 不存在"); ok = false; }
            else
            {
                report.Add(File.Exists(Path.Combine(mp, "modlist.txt")) ? "✔ modlist.txt 存在" : "✖ 缺少 modlist.txt");
                if (File.Exists(Path.Combine(mp, "modlist.txt")) == false) ok = false;
                var rpMeta = File.Exists(Path.Combine(mp, "resourcepack", "pack.mcmeta"));
                report.Add(rpMeta ? "✔ 资源包 pack.mcmeta 存在" : "⚠ 未生成资源包（可选）");
                var dpMeta = File.Exists(Path.Combine(mp, "datapack", "pack.mcmeta"));
                report.Add(dpMeta ? "✔ 数据包 pack.mcmeta 存在" : "⚠ 未生成数据包（可选）");
                if (File.Exists(Path.Combine(mp, "modpack.zip"))) report.Add("✔ modpack.zip 已压缩");
            }
        }
        var verdict = ok ? "验证通过 ✔" : "验证未通过 ✖";
        return new SkillResult
        {
            Success = ok,
            Message = $"{verdict}（{target}）\n    " + string.Join("\n    ", report),
            Outputs = new() { ["verdict"] = ok ? "pass" : "fail", ["report"] = string.Join("\n", report) },
        };
    }
}
