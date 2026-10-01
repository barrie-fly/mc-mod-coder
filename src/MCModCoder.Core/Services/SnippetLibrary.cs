namespace MCModCoder.Core.Services;

public class CodeSnippet
{
    public string Name { get; set; } = "";
    public string Language { get; set; } = "java";   // java / json / glsl
    public string Description { get; set; } = "";
    public string Code { get; set; } = "";
}

/// <summary>代码片段库（内置 + 用户自定义持久化）</summary>
public static class SnippetLibrary
{
    public static List<CodeSnippet> Builtin { get; } = new()
    {
        new()
        {
            Name = "Forge 主类", Language = "java", Description = "Forge 1.16+ 模组主类骨架",
            Code = """
package {{package}};

import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.eventbus.api.IEventBus;
import net.minecraftforge.fml.javafmlmod.FMLJavaModLoadingContext;

@Mod({{MODID_UPPER}})
public class {{MainClass}} {
    public static final String MODID = "{{modid}}";

    public {{MainClass}}() {
        IEventBus bus = FMLJavaModLoadingContext.get().getModEventBus();
        // ModItems.REGISTRY.register(bus);
        // ModBlocks.REGISTRY.register(bus);
        MinecraftForge.EVENT_BUS.register(this);
    }
}
"""
        },
        new()
        {
            Name = "Forge 注册物品", Language = "java", Description = "DeferredRegister 注册物品（1.16+）",
            Code = """
public static final DeferredRegister<Item> ITEMS =
        DeferredRegister.create(ForgeRegistries.ITEMS, MODID);

public static final RegistryObject<Item> RUBY =
        ITEMS.register("ruby",
                () -> new Item(new Item.Properties().stacksTo(64)));
"""
        },
        new()
        {
            Name = "Forge 注册方块", Language = "java", Description = "方块 + 对应方块物品注册",
            Code = """
public static final DeferredRegister<Block> BLOCKS =
        DeferredRegister.create(ForgeRegistries.BLOCKS, MODID);

public static final DeferredRegister<Item> ITEMS =
        DeferredRegister.create(ForgeRegistries.ITEMS, MODID);

public static final RegistryObject<Block> RUBY_BLOCK =
        BLOCKS.register("ruby_block",
                () -> new Block(BlockBehaviour.Properties.of().strength(3.0f).requiresCorrectToolForDrops()));

public static final RegistryObject<Item> RUBY_BLOCK_ITEM =
        ITEMS.register("ruby_block",
                () -> new BlockItem(RUBY_BLOCK.get(), new Item.Properties()));
"""
        },
        new()
        {
            Name = "Fabric 入口", Language = "java", Description = "Fabric ModInitializer 骨架",
            Code = """
public class {{MainClass}} implements ModInitializer {
    public static final String MODID = "{{modid}}";

    @Override
    public void onInitialize() {
        // Registry.register(Registries.ITEM, new Identifier(MODID, "ruby"), RUBY);
    }
}
"""
        },
        new()
        {
            Name = "事件监听", Language = "java", Description = "Forge 事件订阅示例",
            Code = """
@SubscribeEvent
public void onRightClickBlock(PlayerInteractEvent.RightClickBlock event) {
    Level level = event.getLevel();
    BlockPos pos = event.getPos();
    Player player = event.getEntity();
    if (!level.isClientSide()) {
        player.sendSystemMessage(Component.literal("右键了 " + pos.toShortString()));
    }
}
"""
        },
        new()
        {
            Name = "合成配方 JSON", Language = "json", Description = "有序合成数据包格式",
            Code = """
{
  "type": "minecraft:crafting_shaped",
  "pattern": [
    "XX ",
    " # ",
    " # "
  ],
  "key": {
    "X": { "item": "minecraft:iron_ingot" },
    "#": { "item": "minecraft:stick" }
  },
  "result": {
    "item": "minecraft:iron_pickaxe",
    "count": 1
  }
}
"""
        },
        new()
        {
            Name = "物品模型 JSON", Language = "json", Description = "标准物品模型",
            Code = """
{
  "parent": "item/generated",
  "textures": {
    "layer0": "mymod:item/ruby"
  }
}
"""
        },
        new()
        {
            Name = "光源方块 GLSL", Language = "glsl", Description = "自发光材质片段",
            Code = """
// 自发光：不参与光照衰减
vec3 emissive = albedo * emissiveBoost;
vec3 lit = applyLighting(albedo, ndl, shadow);
outColor = mix(lit, emissive, step(0.5, emissiveMask));
"""
        },
    };

    /// <summary>加载用户自定义片段（工作台数据目录）</summary>
    public static string UserFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MCModCoder", "snippets.json");

    public static List<CodeSnippet> LoadUser()
    {
        try
        {
            if (File.Exists(UserFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<CodeSnippet>>(File.ReadAllText(UserFile)) ?? new();
        }
        catch
        {
            // 损坏的片段库不直接丢弃：留底为 .corrupt 待用户自行恢复，避免下次保存覆盖掉原始数据
            try
            {
                var backup = UserFile + ".corrupt";
                if (File.Exists(UserFile)) File.Copy(UserFile, backup, overwrite: true);
            }
            catch { /* 留底失败不影响使用 */ }
        }
        return new();
    }

    public static void SaveUser(List<CodeSnippet> snippets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        // 原子写：先写临时文件再替换，写盘中断（断电/崩溃）不会留下损坏的半截 JSON
        var tmp = UserFile + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(snippets,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, UserFile, overwrite: true);
    }
}
