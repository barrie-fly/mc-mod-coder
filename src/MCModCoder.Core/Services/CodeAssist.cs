using System.Text.RegularExpressions;

namespace MCModCoder.Core.Services;

public class CompletionItem
{
    public string Label { get; set; } = "";
    public string Insert { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// AI 辅助编写引擎（本地知识库版）：基于上下文感知的智能补全。
/// 内置 Forge / Fabric 常用 API 知识，按光标前代码上下文给出建议；
/// 可通过上下文关键词触发，也可完全关闭（纯手动编写模式）。
/// </summary>
public static class CodeAssist
{
    private static readonly (string Pattern, CompletionItem[] Items)[] Rules =
    {
        // 注册上下文：DeferredRegister / RegistryObject
        (@"\.\s*register\s*\(\s*""?$", new[]
        {
            new CompletionItem{ Label="register(\"...\")", Insert="register(", Detail="DeferredRegister.register — 注册对象到注册表" },
        }),
        (@"DeferredRegister\s*<\s*$", new[]
        {
            new CompletionItem{ Label="<Item>", Insert="Item>", Detail="物品注册表" },
            new CompletionItem{ Label="<Block>", Insert="Block>", Detail="方块注册表" },
            new CompletionItem{ Label="<BlockEntityType<?>>", Insert="BlockEntityType<?>>", Detail="方块实体注册表" },
        }),
        (@"new\s+Item\s*\(\s*$", new[]
        {
            new CompletionItem{ Label="new Item(new Item.Properties())", Insert="new Item(new Item.Properties())", Detail="基础物品" },
            new CompletionItem{ Label="new Item(new Item.Properties().stacksTo(1))", Insert="new Item(new Item.Properties().stacksTo(1))", Detail="不可堆叠物品" },
        }),
        (@"new\s+Block\s*\(\s*$", new[]
        {
            new CompletionItem{ Label="new Block(BlockBehaviour.Properties.of())", Insert="new Block(BlockBehaviour.Properties.of())", Detail="基础方块" },
            new CompletionItem{ Label="new Block(BlockBehaviour.Properties.of().strength(3.0f).requiresCorrectToolForDrops())", Insert="new Block(BlockBehaviour.Properties.of().strength(3.0f).requiresCorrectToolForDrops())", Detail="需要镐挖掘的方块" },
        }),
        // 事件上下文
        (@"@SubscribeEvent|onEvent\s*\(", new[]
        {
            new CompletionItem{ Label="PlayerInteractEvent.RightClickBlock", Insert="PlayerInteractEvent.RightClickBlock", Detail="玩家右键方块事件" },
            new CompletionItem{ Label="TickEvent.PlayerTickEvent", Insert="TickEvent.PlayerTickEvent", Detail="玩家每刻事件" },
            new CompletionItem{ Label="LivingDeathEvent", Insert="LivingDeathEvent", Detail="生物死亡事件" },
            new CompletionItem{ Label="BlockEvent.BreakEvent", Insert="BlockEvent.BreakEvent", Detail="方块破坏事件" },
            new CompletionItem{ Label="EntityJoinLevelEvent", Insert="EntityJoinLevelEvent", Detail="实体加入世界事件" },
        }),
        // 物品属性链
        (@"Item\.Properties\s*\(\s*\)\s*\.\s*$", new[]
        {
            new CompletionItem{ Label="stacksTo(64)", Insert="stacksTo(64)", Detail="最大堆叠数" },
            new CompletionItem{ Label="durability(250)", Insert="durability(250)", Detail="耐久" },
            new CompletionItem{ Label="fireproof()", Insert="fireproof()", Detail="防火（岩浆不烧毁）" },
            new CompletionItem{ Label="rarity(Rarity.EPIC)", Insert="rarity(Rarity.EPIC)", Detail="稀有度（紫色名称）" },
            new CompletionItem{ Label="food(foodProperties)", Insert="food(foodProperties)", Detail="食物属性" },
        }),
        // 方块属性链
        (@"BlockBehaviour\.Properties\.of\s*\(\s*\)\s*\.\s*$", new[]
        {
            new CompletionItem{ Label="strength(3.0f)", Insert="strength(3.0f)", Detail="硬度/抗爆性" },
            new CompletionItem{ Label="requiresCorrectToolForDrops()", Insert="requiresCorrectToolForDrops()", Detail="需要正确工具掉落" },
            new CompletionItem{ Label="lightLevel(state -> 15)", Insert="lightLevel(state -> 15)", Detail="发光等级" },
            new CompletionItem{ Label="sound(SoundType.STONE)", Insert="sound(SoundType.STONE)", Detail="挖掘音效" },
            new CompletionItem{ Label="noOcclusion()", Insert="noOcclusion()", Detail="非完整方块（不遮挡）" },
            new CompletionItem{ Label="randomTicks()", Insert="randomTicks()", Detail="随机刻" },
        }),
        // NBT / 物品栈
        (@"stack\s*\.\s*$", new[]
        {
            new CompletionItem{ Label="getTag()", Insert="getTag()", Detail="获取 NBT（1.16.5）" },
            new CompletionItem{ Label="getOrCreateTag()", Insert="getOrCreateTag()", Detail="获取或创建 NBT" },
            new CompletionItem{ Label="getItem()", Insert="getItem()", Detail="获取物品" },
            new CompletionItem{ Label="shrink(1)", Insert="shrink(1)", Detail="数量减 1" },
            new CompletionItem{ Label="grow(1)", Insert="grow(1)", Detail="数量加 1" },
            new CompletionItem{ Label="isDamageableItem()", Insert="isDamageableItem()", Detail="是否可损耗" },
        }),
        // 世界/玩家
        (@"level\s*\.\s*$|world\s*\.\s*$", new[]
        {
            new CompletionItem{ Label="setBlock(pos, state, 3)", Insert="setBlock(pos, state, 3)", Detail="放置方块（3=发送给客户端+更新）" },
            new CompletionItem{ Label="getBlockState(pos)", Insert="getBlockState(pos)", Detail="读取方块状态" },
            new CompletionItem{ Label="addFreshEntity(entity)", Insert="addFreshEntity(entity)", Detail="生成实体" },
            new CompletionItem{ Label="isClientSide()", Insert="isClientSide()", Detail="是否客户端（逻辑应在服务端执行）" },
            new CompletionItem{ Label="playSound(null, pos, sound, source, vol, pitch)", Insert="playSound(null, pos, sound, source, vol, pitch)", Detail="播放音效" },
        }),
        (@"player\s*\.\s*$", new[]
        {
            new CompletionItem{ Label="sendSystemMessage(Component.literal(\"...\"))", Insert="sendSystemMessage(Component.literal(\"\"))", Detail="发送聊天消息" },
            new CompletionItem{ Label="getMainHandItem()", Insert="getMainHandItem()", Detail="主手物品" },
            new CompletionItem{ Label="setItemSlot(EquipmentSlot.MAINHAND, stack)", Insert="setItemSlot(EquipmentSlot.MAINHAND, stack)", Detail="设置装备" },
            new CompletionItem{ Label="addEffect(new MobEffectInstance(MobEffects.MOVEMENT_SPEED, 200, 0))", Insert="addEffect(new MobEffectInstance(MobEffects.MOVEMENT_SPEED, 200, 0))", Detail="给予药水效果（速度I，10秒）" },
            new CompletionItem{ Label="teleportTo(x, y, z)", Insert="teleportTo(x, y, z)", Detail="传送" },
        }),
        // Fabric
        (@"ModInitializer|fabric", new[]
        {
            new CompletionItem{ Label="Registry.register(Registries.ITEM, new Identifier(modid, \"name\"), item)", Insert="Registry.register(Registries.ITEM, new Identifier(modid, \"name\"), item);", Detail="Fabric 注册物品（1.19.4+ 用 Registries）" },
            new CompletionItem{ Label="RegistryKey.ofRegistry(new Identifier(modid, \"name\"))", Insert="RegistryKey.ofRegistry(new Identifier(modid, \"name\"))", Detail="注册表键" },
        }),
        // 1.12.2 老版本
        (@"@Mod\s*\(|GameRegistry", new[]
        {
            new CompletionItem{ Label="@Mod.EventBusSubscriber(modid = MODID)", Insert="@Mod.EventBusSubscriber(modid = MODID)", Detail="1.12.2 事件订阅" },
            new CompletionItem{ Label="GameRegistry.register(...)", Insert="GameRegistry.register(", Detail="1.12.2 注册" },
            new CompletionItem{ Label="@ObjectHolder(MODID)", Insert="@ObjectHolder(MODID)", Detail="1.12.2 静态注入引用" },
        }),
        // JSON 配方文件上下文
        (@"""\s*type\s*""\s*:\s*""?$", new[]
        {
            new CompletionItem{ Label="minecraft:crafting_shaped", Insert="minecraft:crafting_shaped", Detail="有序合成" },
            new CompletionItem{ Label="minecraft:crafting_shapeless", Insert="minecraft:crafting_shapeless", Detail="无序合成" },
            new CompletionItem{ Label="minecraft:smelting", Insert="minecraft:smelting", Detail="熔炉" },
            new CompletionItem{ Label="minecraft:blasting", Insert="minecraft:blasting", Detail="高炉" },
            new CompletionItem{ Label="minecraft:stonecutting", Insert="minecraft:stonecutting", Detail="切石机" },
            new CompletionItem{ Label="minecraft:smithing_transform", Insert="minecraft:smithing_transform", Detail="锻造台" },
        }),
    };

    /// <summary>Java 关键字集合（词库补全与代码片段命名时排除）</summary>
    private static readonly HashSet<string> JavaKeywords = new(StringComparer.Ordinal)
    {
        "public","private","protected","static","final","void","return","if","else","for","while","new","class",
        "extends","implements","override","import","package","boolean","int","float","double","String","true",
        "false","null","this","super","abstract","interface","enum","try","catch","finally","throw","throws",
        "instanceof","switch","case","break","continue","default","synchronized","volatile","transient","native",
        "var","record","long","short","byte","char"
    };

    /// <summary>
    /// 文档词库补全：扫描当前代码中的全部标识符，按"前缀匹配优先 + 出现频次"排序，
    /// 让补全能感知你正在编写的类名、变量名与方法名。
    /// </summary>
    public static List<CompletionItem> GetWordCompletions(string code, string currentWord, int max = 6)
    {
        var result = new List<CompletionItem>();
        if (string.IsNullOrEmpty(currentWord) || currentWord.Length < 2 || string.IsNullOrEmpty(code))
            return result;

        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(code, @"[\w$]{3,64}"))
        {
            var w = m.Value;
            if (JavaKeywords.Contains(w)) continue;
            freq[w] = freq.TryGetValue(w, out var n) ? n + 1 : 1;
        }

        return freq.Where(kv => !string.Equals(kv.Key, currentWord, StringComparison.Ordinal)
                             && (kv.Key.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)
                              || kv.Key.Contains(currentWord, StringComparison.OrdinalIgnoreCase)))
                   .OrderByDescending(kv => kv.Key.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .ThenByDescending(kv => kv.Value)
                   .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                   .Take(max)
                   .Select(kv => new CompletionItem
                   {
                       Label = kv.Key,
                       Insert = kv.Key,
                       Detail = $"文档标识符 · 出现 {kv.Value} 次"
                   })
                   .ToList();
    }

    /// <summary>按上下文获取补全建议（返回 ≤ N 条）</summary>
    public static List<CompletionItem> GetCompletions(string codeBeforeCursor, string currentWord, int max = 8)
    {
        var result = new List<CompletionItem>();
        var seen = new HashSet<string>();
        var tail = codeBeforeCursor.Length > 400 ? codeBeforeCursor[^400..] : codeBeforeCursor;

        foreach (var (pattern, items) in Rules)
        {
            try
            {
                if (Regex.IsMatch(tail, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    foreach (var it in items)
                        if (seen.Add(it.Label) && (string.IsNullOrEmpty(currentWord) ||
                            it.Label.Contains(currentWord, StringComparison.OrdinalIgnoreCase)))
                            result.Add(it);
                }
            }
            catch { /* 忽略非法模式 */ }
        }

        // 关键字兜底
        if (result.Count == 0 && !string.IsNullOrEmpty(currentWord))
        {
            string[] kws = { "public", "private", "protected", "static", "final", "void", "return", "if", "else", "for", "while", "new", "class", "extends", "implements", "override", "import", "package", "boolean", "int", "float", "double", "String", "true", "false", "null" };
            foreach (var kw in kws)
                if (kw.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase) && seen.Add(kw))
                    result.Add(new CompletionItem { Label = kw, Insert = kw, Detail = "Java 关键字" });
        }

        return result.Take(max).ToList();
    }

    /// <summary>简易代码格式化（缩进规范）</summary>
    public static string FormatCode(string code)
    {
        var sb = new System.Text.StringBuilder();
        int indent = 0;
        bool inString = false, inComment = false, inLineComment = false;
        char prev = '\0';
        foreach (var raw in code.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { sb.Append('\n'); continue; }

            bool endsBlock = line.StartsWith("}") || line.StartsWith("]") || line.StartsWith(")");
            if (endsBlock) indent = Math.Max(0, indent - 1);

            bool isLabelComment = line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*");
            if (!isLabelComment && prev != ';' && prev != '{' && prev != '\0' && !endsBlock && !inString && !inComment && !inLineComment)
            { /* 连续语句独立成行即可 */ }

            sb.Append(new string(' ', indent * 4)).Append(line).Append('\n');
            if (!isLabelComment && (line.EndsWith("{") || line.EndsWith("["))) indent++;
            if (line.EndsWith("}")) { /* 已处理 */ }

            prev = line.Length > 0 ? line[^1] : '\0';
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
