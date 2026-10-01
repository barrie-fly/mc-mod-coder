# MC 模组代码工作台 — API 文档

> 版本：1.0（2026-10-01）｜ 源码基线：`MCModCoder.sln` 当前工作区
> 本文档全部内容以 `src/MCModCoder.Core/` 与 `src/MCModCoder.App/` 真实源码为准。

## 目录

- [1. 总览](#1-总览)
  - [1.1 项目组成与职责划分](#11-项目组成与职责划分)
  - [1.2 依赖关系图（文字描述）](#12-依赖关系图文字描述)
  - [1.3 设计原则](#13-设计原则)
- [2. Core 类库 API 参考](#2-core-类库-api-reference)
  - [2.1 命名空间 `MCModCoder.Core`](#21-命名空间-mcmodcodercore)
  - [2.2 命名空间 `MCModCoder.Core.Models`](#22-命名空间-mcmodcodercoremodels)
  - [2.3 命名空间 `MCModCoder.Core.Serialization`](#23-命名空间-mcmodcoderserialization)
  - [2.4 命名空间 `MCModCoder.Core.Validation`](#24-命名空间-mcmodcodervalidation)
  - [2.5 命名空间 `MCModCoder.Core.Services`](#25-命名空间-mcmodcoderservices)
  - [2.6 命名空间 `MCModCoder.Core.Simulation`](#26-命名空间-mcmodcodercoresimulation)
  - [2.7 命名空间 `MCModCoder.Core.Remote`（远程资源层）](#27-命名空间-mcmodcodercoreremote远程资源层)
  - [2.8 命名空间 `MCModCoder.Core.Ai`（AI 开发引擎）](#28-命名空间-mcmodcodercoreai-ai-开发引擎)
- [3. App 层集成 API（AppContext）](#3-app-层集成-apiappcontext)
- [4. 扩展指南](#4-扩展指南)
  - [4.1 新增一个模块视图（XAML + code-behind）](#41-新增一个模块视图xaml--code-behind)
  - [4.2 新增一个 Core 服务类](#42-新增一个-core-服务类)
  - [4.3 通过事件接入跨模块数据流](#43-通过事件接入跨模块数据流)
- [5. JSON 数据格式说明](#5-json-数据格式说明)

---

# 1. 总览

## 1.1 项目组成与职责划分

解决方案 `MCModCoder.sln` 包含两个项目：

| 项目 | 类型 | 目标框架 | 职责 |
|---|---|---|---|
| `MCModCoder.Core` | 类库 | `net8.0` | 领域模型、序列化、校验、服务、模拟器等全部业务能力。**零 NuGet 依赖、零 UI 依赖**，可被任何 .NET 宿主（控制台、单元测试、其他 GUI）独立复用。 |
| `MCModCoder.App` | WPF 宿主 | `net8.0-windows`（`UseWPF=true`） | 桌面工作台外壳：主窗口、导航、12 个功能模块视图，以及全局上下文 `AppContext`。仅通过 `ProjectReference` 引用 Core。 |

两者的分工边界：

- **Core 负责"是什么 / 怎么算 / 怎么读写"**：`McProject` 等模型定义、配方与 MC 数据包 JSON 的双向转换、资源包/脚手架文件生成、配方校验、代码补全、日志诊断、红石/实体模拟。
- **App 负责"怎么呈现 / 怎么交互"**：XAML 视图、WPF 画布渲染（2D/3D）、文件对话框、导航与状态栏，并通过 `AppContext` 把各模块串成"设计 → 编码 → 测试"闭环。

## 1.2 依赖关系图（文字描述）

```
MCModCoder.App (net8.0-windows, WPF)
 │   ── MainWindow（导航外壳）
 │   ── Modules/*View（12 个模块视图，命名空间 MCModCoder.Modules）
 │   ── AppContext（全局上下文，命名空间 MCModCoder）
 │
 └──► MCModCoder.Core (net8.0, 无第三方依赖)
          ├── Models/        领域模型（版本矩阵、项目、配方、结构、地形、地牢、光影、渲染效果）
          ├── Serialization/ 配方 JSON、结构文件、资源包导出
          ├── Validation/    配方校验器
          ├── Services/      脚手架生成、代码补全、片段库、日志分析
          ├── Simulation/    红石网格、实体模拟
          ├── Remote/        远程资源搜索、下载校验、整合包解析与导入队列
          └── ColorUtil      颜色工具（根命名空间）
```

- 依赖方向**单向**：`App → Core`。Core 不引用 App，也不引用任何 WPF 类型。
- App 内部各模块视图之间**不直接互相引用**，跨模块数据一律经由 `AppContext` 的静态事件传递（见第 3、4.3 节）。

## 1.3 设计原则

1. **纯 C#，无第三方依赖**：Core 与 App 的 `.csproj` 均未声明任何 `PackageReference`，序列化使用内置 `System.Text.Json`，压缩使用 `System.IO.Compression`。
2. **Core 不引用 WPF 类型**：Core 只依赖 BCL；颜色处理等逻辑放在 `ColorUtil`（基于 `(byte R, byte G, byte B)` 元组）而非 WPF 的 `Color`，保证可移植。
3. **静态服务 + POCO 模型**：无状态能力（序列化、校验、补全、导出）以 `static` 类提供；数据以带自动属性的 POCO 表达，便于 JSON 直序列化。
4. **默认值即合法值**：所有模型属性的默认值均为可直接运行的合理配置（如 `McProject.ModId = "mymod"`、`OreConfig.MinY = -64`），`new` 出来即可用。
5. **JSON 与 Minecraft 格式双向兼容**：`RecipeJsonSerializer` 可直接读写原版数据包 recipe JSON；工作台私有扩展（酿造台、app 包装）采用独立命名空间/包装字段，不污染原版格式。
6. **解析永不抛出**：所有 `Load/FromJson` 方法在格式非法时返回 `null`（或空集合），由调用方决定提示方式。

---

# 2. Core 类库 API 参考

## 2.1 命名空间 `MCModCoder.Core`

### 2.1.1 `ColorUtil`（静态类）

十六进制颜色解析与插值工具，无任何 UI 依赖。文件：`src/MCModCoder.Core/ColorUtil.cs`。

```csharp
public static class ColorUtil
{
    public static (byte R, byte G, byte B) ParseHex(string hex);
    public static (byte R, byte G, byte B) Lerp(string from, string to, double t);
    public static string ToHex(byte r, byte g, byte b);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `ParseHex` | `hex`：形如 `"#RRGGBB"`、`"RRGGBB"` 或 3 位缩写 `"#RGB"` | `(byte R, byte G, byte B)` | 解析失败或传入 `null` 时返回白色 `(255,255,255)`。 |
| `Lerp` | `from`/`to`：起始/结束颜色；`t`：插值系数，自动 `Clamp` 到 `[0,1]` | `(byte R, byte G, byte B)` | 两色之间逐通道线性插值，常用于粒子颜色渐变。 |
| `ToHex` | `r`、`g`、`b`：0–255 通道值 | `string` | 输出 `"#RRGGBB"`（大写十六进制）。 |

```csharp
// 示例：粒子颜色随生命周期渐变
var (r, g, b) = ColorUtil.Lerp("#FFE066", "#FF5722", 0.5);
string mid = ColorUtil.ToHex(r, g, b);   // "#FF9C44"
```

---

## 2.2 命名空间 `MCModCoder.Core.Models`

文件：`src/MCModCoder.Core/Models/`。共 23 个类型：`McVersionInfo`、`McVersions`、`McProject`、`DependencyEntry`、`BuildOptions`、`RecipeType`、`RecipeTypeMap`、`IngredientSpec`、`ResultSpec`、`RecipeDefinition`、`StructureData`、`Biomes`、`OreConfig`、`TerrainMap`、`DungeonRoom`、`DungeonCorridor`、`DungeonGenRules`、`DungeonGraph`、`ShaderConfig`、`ParticleConfig`、`MaterialConfig`、`AnimKeyframe`、`AnimationConfig`。

### 2.2.1 `McVersionInfo`

Minecraft 单个版本的信息条目。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Version` | `string` | 版本号，如 `"1.20.1"` | `""` |
| `Loaders` | `string[]` | 该版本可用的模组加载器（Forge / Fabric / NeoForge / Rift） | `Array.Empty<string>()` |
| `JavaVersion` | `string` | 所需 Java 主版本 | `"8"` |
| `PackFormat` | `int` | 数据包/资源包 `pack_format` | `15` |
| `DataPackDir` | `string` | 数据包内容目录名 | `"data"` |
| `AssetsDir` | `string` | 资源包内容目录名 | `"assets"` |
| `RecipeNamespace` | `string`（只读） | 配方命名空间，恒为 `"minecraft"` | — |

### 2.2.2 `McVersions`（静态类）

版本与加载器兼容矩阵。

```csharp
public static class McVersions
{
    public static readonly McVersionInfo[] Supported;
    public static McVersionInfo? Find(string version);
    public static string[] LoadersFor(string version);
}
```

| 成员 | 说明 |
|---|---|
| `Supported` | 内置版本矩阵：1.12.2（Forge/Rift, Java 8, pack 3）、1.16.5（Forge/Fabric, Java 8, pack 6）、1.18.2（pack 8）、1.19.4（pack 13）、1.20.1（Forge/Fabric/NeoForge, Java 17, pack 15）、1.20.4（pack 26）、1.20.6（NeoForge/Fabric, Java 21, pack 41）。 |
| `Find(version)` | 按版本号查找，未找到返回 `null`。 |
| `LoadersFor(version)` | 返回该版本可用加载器；未知版本回退 `["Forge"]`。 |

```csharp
var v = McVersions.Find("1.20.1");
Console.WriteLine(v?.PackFormat);        // 15
Console.WriteLine(string.Join(",", McVersions.LoadersFor("1.20.6"))); // NeoForge,Fabric
```

### 2.2.3 `McProject`

模组项目模型，也是项目文件 `.mcmproj.json` 的序列化载体。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 模组显示名 | `"MyMod"` |
| `ModId` | `string` | 模组 ID（小写） | `"mymod"` |
| `McVersion` | `string` | 目标 MC 版本 | `"1.20.1"` |
| `ModLoader` | `string` | 加载器：Forge / Fabric / NeoForge / Rift | `"Forge"` |
| `Package` | `string` | Java 包名 | `"com.example.mymod"` |
| `Author` | `string` | 作者 | `""` |
| `Description` | `string` | 描述 | `""` |
| `ProjectDir` | `string` | 项目磁盘根目录 | `""` |
| `CreatedAt` | `DateTime` | 创建时间 | `DateTime.Now` |
| `ModifiedAt` | `DateTime` | 修改时间 | `DateTime.Now` |
| `Dependencies` | `List<DependencyEntry>` | 依赖列表 | 空 |
| `Build` | `BuildOptions` | 构建选项 | `new()` |
| `ProjectFilePath` | `string`（只读） | `Path.Combine(ProjectDir, Name + ".mcmproj.json")` | — |
| `MainClass` | `string`（只读） | `MakeClassName(Name)` 的结果 | — |
| `JavaPackagePath` | `string`（只读） | `Package.Replace('.', '/')`，如 `com/example/mymod` | — |

| 方法 | 签名 | 说明 |
|---|---|---|
| `MakeClassName` | `public static string MakeClassName(string name)` | 把任意名称转为合法 Java 类名：非字母数字替换为 `_`；空串或以数字开头则补前缀 `_`。 |

```csharp
var p = new McProject { Name = "Ruby Mod", ModId = "ruby", ProjectDir = @"D:\mods\ruby" };
p.MainClass;                    // "Ruby_Mod"
p.ProjectFilePath;              // D:\mods\ruby\Ruby Mod.mcmproj.json
TemplateGenerator.Generate(p);  // 生成完整脚手架
```

### 2.2.4 `DependencyEntry`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `GroupId` | `string` | Maven 组 ID | `""` |
| `Artifact` | `string` | 构件名 | `""` |
| `Version` | `string` | 版本 | `""` |
| `Scope` | `string` | Gradle 依赖范围：`implementation` / `compileOnly` / `runtimeOnly` | `"implementation"` |
| `Coordinate` | `string`（只读） | `"GroupId:Artifact:Version"` | — |

### 2.2.5 `BuildOptions`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `BuildTool` | `string` | 构建工具 | `"Gradle"` |
| `JavaVersion` | `string` | 构建 Java 版本 | `"17"` |
| `OutputDir` | `string` | 构建产物目录 | `"build/libs"` |
| `ParallelBuild` | `bool` | 并行构建 | `true` |
| `RunClientAfterBuild` | `bool` | 构建后启动客户端 | `false` |
| `ExtraGradleArgs` | `string` | 附加 Gradle 参数 | `""` |

### 2.2.6 `RecipeType`（枚举）

```csharp
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
    Brewing              // 工作台自定义扩展，导出为 mcmcoder:brewing
}
```

### 2.2.7 `RecipeTypeMap`（静态类，扩展方法）

`RecipeType` 与 MC JSON 字符串、中文显示名的映射。

```csharp
public static class RecipeTypeMap
{
    public static string ToJsonType(this RecipeType t);
    public static RecipeType FromJsonType(string s);
    public static string DisplayName(this RecipeType t);
    public static bool IsCooking(this RecipeType t);
}
```

| 方法 | 说明 |
|---|---|
| `ToJsonType` | 映射为 JSON `type` 字段；`Brewing → "mcmcoder:brewing"`，其余为对应 `minecraft:*`；未知回退 `minecraft:crafting_shaped`。 |
| `FromJsonType` | 逆向映射；同时兼容 `"minecraft:smithing"` 旧写法；未知回退 `CraftingShaped`。 |
| `DisplayName` | 中文名，如 `"有序合成（合成台 3×3）"`、`"熔炉冶炼"`。 |
| `IsCooking` | 是否熔炼类（Smelting / Blasting / Smoking / CampfireCooking）。 |

### 2.2.8 `IngredientSpec`

配料：物品 ID 或标签（二选一）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Item` | `string?` | 物品 ID，如 `minecraft:iron_ingot` | `null` |
| `Tag` | `string?` | 标签，如 `#minecraft:planks` | `null` |
| `Display` | `string`（只读，`[JsonIgnore]`） | 显示文本：优先 Tag，否则 Item，否则 `"air"` | — |
| `IsEmpty` | `bool`（只读） | Item 与 Tag 均为空 | — |

| 方法 | 签名 | 说明 |
|---|---|---|
| `Parse` | `public static IngredientSpec Parse(string text)` | 以 `#` 开头解析为 Tag；空文本或 `"air"` 解析为 `Item="minecraft:air"`；其余为 Item。 |
| `ToString` | 重写 | 返回 `Display`。 |

```csharp
IngredientSpec.Parse("#minecraft:planks").Tag;      // "#minecraft:planks"
IngredientSpec.Parse("minecraft:iron_ingot").Item;  // "minecraft:iron_ingot"
new IngredientSpec().IsEmpty;                       // true
```

### 2.2.9 `ResultSpec`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Item` | `string` | 产物物品 ID | `"minecraft:stone"` |
| `Count` | `int` | 数量 | `1` |
| `Nbt` | `string?` | 产物 NBT（可选，常用于药水） | `null` |

### 2.2.10 `RecipeDefinition`

配方定义，与 Minecraft 数据包 JSON 双向兼容（见 2.3.1 与第 5 章）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Id` | `string` | 配方 ID，如 `mymod:iron_pickaxe` | `""` |
| `Category` | `string` | 工作台内分类名（便于管理/搜索） | `"常规"` |
| `Type` | `RecipeType` | 配方类型 | `CraftingShaped` |
| `Description` | `string` | 备注 | `""` |
| `Pattern` | `List<string>` | 有序合成图案，每行一个字符串，空格为空位 | `["   ","   ","   "]` |
| `Key` | `Dictionary<string, IngredientSpec>` | 图案字符 → 配料 | 空 |
| `ShapelessIngredients` | `List<IngredientSpec>` | 无序合成材料（最多 9 项） | 空 |
| `Ingredient` | `IngredientSpec` | 熔炼类 / 切石机 / 酿造台输入 | `new()` |
| `Template` | `IngredientSpec?` | 锻造台模板（1.20+） | `null` |
| `Base` | `IngredientSpec?` | 锻造台基础物品 | `null` |
| `Addition` | `IngredientSpec?` | 锻造台附加材料 | `null` |
| `BrewingBase` | `IngredientSpec?` | 酿造台底酒（仅 Brewing 使用） | `null` |
| `Result` | `ResultSpec` | 产物 | `new()` |
| `CookingTime` | `int` | 烹饪耗时（刻；酿造时对应 `brewing_time`） | `200` |
| `Experience` | `double` | 经验值 | `0.1` |
| `UsesGrid` | `bool`（只读） | 是否合成台类（Shaped/Shapeless） | — |
| `UsesSingleIngredient` | `bool`（只读） | 是否单输入（熔炼类或切石机） | — |

### 2.2.11 `StructureData`

多方块结构数据：调色板 + 稀疏方块字典。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 结构名 | `"NewStructure"` |
| `SizeX` / `SizeY` / `SizeZ` | `int` | 尺寸 | `8` / `8` / `8` |
| `Palette` | `List<string>` | 调色板：索引 → 方块 ID；索引 0 恒为 `minecraft:air` | 12 种常用方块（air/stone/dirt/oak_planks/glass/oak_log/bricks/iron_block/gold_block/diamond_block/glowstone/water） |
| `Blocks` | `Dictionary<string,int>` | 坐标键 `"x,y,z"` → 调色板索引（0 不落盘） | 空 |
| `BlockCount` | `int`（只读，`[JsonIgnore]`） | 非空气方块数 | — |

| 方法 | 签名 | 说明 |
|---|---|---|
| `Key` | `public static string Key(int x, int y, int z)` | 生成坐标键 `"x,y,z"`。 |
| `Get` | `public int Get(int x, int y, int z)` | 取调色板索引，未放置返回 `0`（空气）。 |
| `Set` | `public void Set(int x, int y, int z, int paletteIndex)` | 写入方块；`paletteIndex <= 0` 时移除该坐标。 |
| `EnsureSize` | `public void EnsureSize(int x, int y, int z)` | 必要时扩展三向尺寸以容纳该坐标。 |
| `RotateY` | `public void RotateY()` | 绕 Y 轴顺时针旋转 90°（SizeX/SizeZ 互换）。 |
| `RotateX` | `public void RotateX()` | 绕 X 轴俯仰旋转 90°（SizeY/SizeZ 互换）。 |
| `RotateZ` | `public void RotateZ()` | 绕 Z 轴滚转旋转 90°（SizeX/SizeY 互换）。 |
| `MirrorX` / `MirrorZ` | `public void MirrorX()` / `public void MirrorZ()` | 沿 X / Z 轴镜像。 |
| `Clone` | `public StructureData Clone()` | 深拷贝，名称自动加 `"_副本"` 后缀。 |

```csharp
var s = new StructureData { Name = "Altar" };
s.EnsureSize(2, 0, 0);
s.Set(0, 0, 0, 1);   // minecraft:stone
s.Set(1, 0, 0, 11);  // minecraft:glowstone
s.RotateY();
Console.WriteLine(s.BlockCount);  // 2
```

### 2.2.12 `Biomes`（静态类）

常用生物群系预设。

```csharp
public static class Biomes
{
    public static readonly (string Id, string Cn, string Color)[] All;
    public static string ColorOf(int index);
    public static string NameOf(int index);
    public static string IdOf(int index);
}
```

| 成员 | 说明 |
|---|---|
| `All` | 14 个群系元组：平原、森林、沙漠、雪原、针叶林、丛林、热带草原、沼泽、海洋、沙滩、山地、恶地、蘑菇岛、樱花树林，含 MC ID、中文名与展示色。 |
| `ColorOf(index)` | 返回十六进制展示色；越界返回 `"#888888"`。 |
| `NameOf(index)` | 返回中文名；越界返回 `"未知"`。 |
| `IdOf(index)` | 返回 MC ID；越界回退 `"minecraft:plains"`。 |

### 2.2.13 `OreConfig`

矿物分布配置。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Block` | `string` | 矿物方块 ID | `"minecraft:iron_ore"` |
| `CnName` | `string` | 中文名 | `"铁矿石"` |
| `VeinsPerChunk` | `int` | 每区块矿脉数 | `8` |
| `VeinSize` | `int` | 单矿脉方块数 | `6` |
| `MinY` / `MaxY` | `int` | 生成高度区间 | `-64` / `64` |

### 2.2.14 `TerrainMap`

地形地图：高度图 + 群系图 + 矿物分布。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 地图名 | `"NewWorld"` |
| `Size` | `int` | 区域边长（方块） | `128` |
| `SeaLevel` | `int` | 海平面高度 | `63` |
| `Seed` | `string` | 种子（字符串哈希后驱动噪声） | `"minecraft"` |
| `Height` | `int[]` | 高度数组，长度 `Size*Size`，值 0–255 | 空 |
| `Biome` | `int[]` | 群系索引数组（对应 `Biomes.All` 下标） | 空 |
| `Ores` | `List<OreConfig>` | 矿物配置 | 内置煤/铁/金/钻石/红石/青金石/绿宝石 7 种 |

| 方法 | 签名 | 说明 |
|---|---|---|
| `InitArrays` | `public void InitArrays()` | 按当前 `Size` 分配数组，高度置为 `SeaLevel`，群系置 0。 |
| `H` / `SetH` | `public int H(int x, int y)` / `public void SetH(int x, int y, int v)` | 读写高度；`SetH` 自动 Clamp 到 0–255。 |
| `B` / `SetB` | `public int B(int x, int y)` / `public void SetB(int x, int y, int v)` | 读写群系索引。 |
| `Generate` | `public void Generate(int baseHeight = 64, int amplitude = 20, int biomeMode = 0)` | 基于种子生成 4 层 Perlin 风格梯度噪声地形。`biomeMode=0` 按高度自动选群系（深海/沙滩/山地/丛林/平原），`=1` 生成东西向温度带状群系。 |

```csharp
var map = new TerrainMap { Size = 64, Seed = "demo" };
map.Generate(baseHeight: 64, amplitude: 24, biomeMode: 0);
int h = map.H(10, 10);
int biomeName = Biomes.NameOf(map.B(10, 10));   // 如 "平原"
```

### 2.2.15 `DungeonRoom`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Id` | `string` | 房间唯一 ID | 8 位 GUID 片段 |
| `Kind` | `string` | 类型：`normal` / `entrance` / `treasure` / `spawner` / `boss` / `corridor-hub` | `"normal"` |
| `CnKind` | `string`（只读） | 中文类型名（入口/宝物房/刷怪房/BOSS房/普通房间） | — |
| `X` / `Y` | `int` | 中心平面坐标 | `0` |
| `W` / `H` | `int` | 宽 / 高 | `9` / `9` |
| `Material` | `string` | 墙体材质 | `"minecraft:bricks"` |

### 2.2.16 `DungeonCorridor`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `FromRoom` / `ToRoom` | `string` | 起止房间 ID | `""` |
| `Style` | `string` | 走廊样式：`straight`（直线）/ `L-shaped`（L 形） | `"straight"` |
| `Width` | `int` | 走廊宽度 | `3` |

### 2.2.17 `DungeonGenRules`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Mode` | `string` | `random`（随机生成）/ `fixed`（使用手动布局） | `"random"` |
| `Seed` | `int` | 随机种子 | `12345` |
| `RoomCount` | `int` | 房间数 | `10` |
| `MinRoomSize` / `MaxRoomSize` | `int` | 房间尺寸区间 | `7` / `15` |
| `CorridorWidth` | `int` | 走廊宽度 | `3` |
| `IncludeTreasure` | `bool` | 是否含宝物房 | `true` |
| `IncludeSpawner` | `bool` | 是否含刷怪房（最多 2 个） | `true` |
| `IncludeBoss` | `bool` | 是否含 BOSS 房（距入口最远者，≥15×15） | `true` |
| `LoopConnectionRate` | `double` | 额外环路连接概率 | `0.15` |
| `WallMaterial` / `FloorMaterial` | `string` | 墙体 / 地面材质 | `minecraft:bricks` / `minecraft:stone_bricks` |

### 2.2.18 `DungeonGraph`

地牢结构图（房间 + 走廊）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 名称 | `"NewDungeon"` |
| `Description` | `string` | 描述 | `""` |
| `Rules` | `DungeonGenRules` | 生成规则 | `new()` |
| `Rooms` | `List<DungeonRoom>` | 房间列表 | 空 |
| `Corridors` | `List<DungeonCorridor>` | 走廊列表 | 空 |

| 方法 | 签名 | 说明 |
|---|---|---|
| `RoomById` | `public DungeonRoom? RoomById(string id)` | 按 ID 查房间。 |
| `Generate` | `public void Generate()` | 按规则随机生成：入口房 → 随机游走布置房间并连线（防重叠）→ 指派宝物/刷怪/BOSS 房 → 按 `LoopConnectionRate` 添加环路。`Mode=="fixed"` 时直接清空返回（保留手动布局）。 |

```csharp
var g = new DungeonGraph();
g.Rules.RoomCount = 12;
g.Rules.Seed = 42;
g.Generate();
Console.WriteLine($"{g.Rooms.Count} 房间 / {g.Corridors.Count} 走廊");
```

### 2.2.19 `ShaderConfig`

光影（着色器）配置：光源 / 阴影 / 环境光 / 雾效。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 光影名 | `"MyShader"` |
| `SunAngle` | `double` | 太阳高度角 0–90 | `45` |
| `SunAzimuth` | `double` | 方位角 0–360 | `135` |
| `SunIntensity` | `double` | 日光强度 | `1.0` |
| `SunColor` | `string` | 日光颜色（十六进制） | `"#FFF3D6"` |
| `ShadowStrength` | `double` | 阴影强度 0–1 | `0.6` |
| `ShadowSoftness` | `double` | 阴影柔和度 | `0.5` |
| `AmbientIntensity` | `double` | 环境光强度 | `0.35` |
| `AmbientColor` | `string` | 环境光颜色 | `"#8FA6C8"` |
| `FogDensity` | `double` | 雾密度 | `0.015` |
| `FogColor` | `string` | 雾颜色 | `"#A8C4E0"` |
| `FogHeightFalloff` | `double` | 雾高度衰减系数 | `0.5` |
| `EmissiveBoost` | `double` | 自发光增益 | `1.0` |
| `WaterTint` | `string` | 水体染色 | `"#3F76E4"` |
| `VolumetricLight` | `bool` | 体积光 | `false` |
| `TonemappingACES` | `bool` | ACES 色调映射 | `true` |

| 方法 | 签名 | 说明 |
|---|---|---|
| `ToGlsl` | `public string ToGlsl()` | 导出 GLSL 风格 uniforms 与 `applyFog` / `applyLighting` 两个着色函数；太阳方向由角度/方位角换算为单位向量。 |

```csharp
string glsl = new ShaderConfig { SunAngle = 30 }.ToGlsl();
File.WriteAllText("shader_config.glsl", glsl);
```

### 2.2.20 `ParticleConfig`

粒子发射器配置（渲染编辑器 / 效果文件）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 效果名 | `"MyEffect"` |
| `RatePerSecond` | `double` | 每秒发射数 | `40` |
| `Lifetime` | `double` | 存活时间（秒） | `1.2` |
| `Speed` | `double` | 初速度 | `1.0` |
| `Gravity` | `double` | 重力 | `0.4` |
| `Spread` | `double` | 发射锥半角（0–1） | `0.6` |
| `ColorFrom` / `ColorTo` | `string` | 颜色渐变起止（十六进制） | `"#FFE066"` / `"#FF5722"` |
| `SizeStart` / `SizeEnd` | `double` | 粒子尺寸起止（像素，预览用） | `4.0` / `0.5` |
| `Additive` | `bool` | 叠加混合 | `true` |
| `MaxCount` | `int` | 最大粒子数 | `600` |
| `Shape` | `string` | 发射形状：`point` / `cone` / `ring` | `"cone"` |

### 2.2.21 `MaterialConfig`

材质渲染参数。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 材质名 | `"MyMaterial"` |
| `Metalness` | `double` | 金属度 | `0.0` |
| `Roughness` | `double` | 粗糙度 | `0.8` |
| `Emission` | `double` | 自发光 | `0.0` |
| `BaseColor` | `string` | 基础色（十六进制） | `"#C8C8C8"` |
| `UseNormalMap` | `bool` | 使用法线贴图 | `true` |
| `UseSpecularMap` | `bool` | 使用高光贴图 | `false` |
| `RenderLayer` | `string` | 渲染层：`solid` / `cutout` / `translucent` | `"solid"` |

### 2.2.22 `AnimKeyframe`

动画曲线关键帧。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Time` | `double` | 时间归一化 0–1 | `0` |
| `Value` | `double` | 数值归一化 0–1 | `0` |

### 2.2.23 `AnimationConfig`

动画配置（粒子尺寸 / 透明度随生命周期的曲线）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 曲线名 | `"SizeOverLife"` |
| `Keys` | `List<AnimKeyframe>` | 关键帧列表 | `(0,1)`、`(0.4,0.8)`、`(1,0)` |

| 方法 | 签名 | 说明 |
|---|---|---|
| `Sample` | `public double Sample(double t)` | 按 `t ∈ [0,1]` 在关键帧之间线性插值；无关键帧返回 `1`；超出首尾取边界值。 |

---

## 2.3 命名空间 `MCModCoder.Core.Serialization`

### 2.3.1 `RecipeJsonSerializer`（静态类）

配方 ↔ Minecraft 数据包 JSON 双向序列化。文件：`src/MCModCoder.Core/Serialization/RecipeJsonSerializer.cs`。

```csharp
public static class RecipeJsonSerializer
{
    public static string ToDatapackJson(RecipeDefinition r);
    public static RecipeDefinition? FromJson(string id, string json);
    public static List<string> ExportToDatapack(IEnumerable<RecipeDefinition> recipes,
        string datapackRoot, string nameSpace, int packFormat = 15);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `ToDatapackJson` | `r`：配方定义 | `string`（缩进 JSON） | 按类型输出原版数据包字段（见第 5.1 节字段表）。切石机产物输出为字符串形式；Brewing 输出 `type: "mcmcoder:brewing"` 与 `brewing_time`。 |
| `FromJson` | `id`：配方 ID；`json`：JSON 文本 | `RecipeDefinition?` | 兼容数据包格式与旧 `smithing` 写法；数组配料取第一项；pattern 自动补齐/截断为 3 行、行宽 3；解析失败返回 `null`，不抛异常。 |
| `ExportToDatapack` | `recipes`：配方集合；`datapackRoot`：数据包根目录；`nameSpace`：命名空间（模组 ID）；`packFormat`：包格式 | `List<string>`（写出文件全路径） | 生成 `data/<ns>/recipe/<path>.json`（每配方一个文件）与 `pack.mcmeta`；`path` 取自 `Id` 冒号后半段。 |

```csharp
var recipe = new RecipeDefinition
{
    Id = "mymod:ruby_pickaxe",
    Type = RecipeType.CraftingShaped,
    Pattern = new() { "XXX", " # ", " # " },
    Key = new() { ["X"] = IngredientSpec.Parse("mymod:ruby"), ["#"] = IngredientSpec.Parse("minecraft:stick") },
    Result = new ResultSpec { Item = "mymod:ruby_pickaxe" }
};
string json = RecipeJsonSerializer.ToDatapackJson(recipe);
var back = RecipeJsonSerializer.FromJson(recipe.Id, json);   // 往返无损
RecipeJsonSerializer.ExportToDatapack(new[] { recipe }, @"D:\packs\demo", "mymod", 15);
```

### 2.3.2 `StructureSerializer`（静态类）

结构序列化 / 反序列化（工作台 `.mcstructure.json` 格式，即 `StructureData` 的直接 JSON 投影）。

```csharp
public static class StructureSerializer
{
    public static void Save(StructureData s, string file);
    public static StructureData? Load(string file);
    public static string ExportPaletteCsv(StructureData s);
    public static string GeneratePlaceCommand(StructureData s, string nameSpace = "mymod");
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `Save` | `s`：结构；`file`：目标路径 | `void` | 缩进 JSON 写入；允许从字符串读取数字。 |
| `Load` | `file`：结构文件路径 | `StructureData?` | 解析失败返回 `null`。 |
| `ExportPaletteCsv` | `s`：结构 | `string` | 调色板 CSV（表头 `index,block`），便于人工核对。 |
| `GeneratePlaceCommand` | `s`：结构；`nameSpace`：命名空间 | `string` | 生成 `/place template <ns>:<名称> ~ ~ ~` 指令；名称转小写、空格换下划线。 |

```csharp
StructureSerializer.Save(_struct, @"D:\mods\demo\workspace\structures\altar.mcstructure.json");
var loaded = StructureSerializer.Load(@"D:\mods\demo\workspace\structures\altar.mcstructure.json");
string cmd = StructureSerializer.GeneratePlaceCommand(_struct, "mymod");
// /place template mymod:altar ~ ~ ~
```

### 2.3.3 `ResourcePackExporter`（静态类）

资源包目录结构自动生成与打包导出。

```csharp
public static class ResourcePackExporter
{
    public static List<string> Generate(string outputRoot, string nameSpace, int packFormat,
        IEnumerable<(string RelPath, byte[] Png)> textures,
        IEnumerable<(string RelPath, string Json)>? blockModels = null);
    public static string PackAsZip(string outputRoot, string zipPath);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `Generate` | `outputRoot`：输出根目录；`nameSpace`：命名空间（模组 ID）；`packFormat`：资源包格式；`textures`：纹理列表（相对路径如 `item/ruby`，PNG 字节）；`blockModels`：可选的方块/自定义模型（路径 → 模型 JSON） | `List<string>`（写出文件全路径） | 写入 `pack.mcmeta`、内置 1×1 占位 `pack.png`、`assets/<ns>/textures/<rel>.png`；对 `item/*` 纹理自动生成 `item/generated` 模型 JSON（若提供 `blockModels` 则 `block/*` 不自动生成），写入 `assets/<ns>/models/<rel>.json`；`blockModels` 原样写入 models 目录。 |
| `PackAsZip` | `outputRoot`：资源包目录；`zipPath`：zip 输出路径 | `string` | 目录整体压缩为可直接放入 `resourcepacks` 的 zip；同名旧文件先删除。 |

```csharp
byte[] png = File.ReadAllBytes("ruby.png");
ResourcePackExporter.Generate(@"D:\packs\out", "mymod", 15,
    new[] { (RelPath: "item/ruby", Png: png) });
ResourcePackExporter.PackAsZip(@"D:\packs\out", @"D:\packs\out.zip");
```

---

## 2.4 命名空间 `MCModCoder.Core.Validation`

### 2.4.1 `IssueSeverity`（枚举）

```csharp
public enum IssueSeverity { Error, Warning, Info }
```

### 2.4.2 `ValidationIssue`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Severity` | `IssueSeverity` | 严重级别 | `Error` |
| `Message` | `string` | 问题描述（中文） | `""` |
| `Suggestion` | `string` | 修正建议（中文） | `""` |

### 2.4.3 `RecipeValidator`（静态类）

配方验证器：检测无效配置并给出修正建议。

```csharp
public static partial class RecipeValidator
{
    public static List<ValidationIssue> Validate(RecipeDefinition r);
    public static Dictionary<string, List<ValidationIssue>> ValidateAll(IEnumerable<RecipeDefinition> recipes);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `Validate` | `r`：配方 | `List<ValidationIssue>` | 检查项：通用 ID 合法性（正则 `^[a-z0-9_.\-]+:[a-z0-9_/.\-]+$`，缺命名空间给 Warning）；产物非空 / 格式 / 数量 1–64；再按类型分支——有序合成（pattern/key 对应、行宽一致、≤3 行、非空）、无序合成（1–9 项）、熔炼类（输入非空、耗时提示）、切石机、锻造台（base/addition 必填、template 建议）、酿造台（ingredient 必填、base 建议）。 |
| `ValidateAll` | `recipes`：配方集合 | `Dictionary<string, List<ValidationIssue>>` | 批量验证，键为配方 `Id`。 |

```csharp
foreach (var issue in RecipeValidator.Validate(recipe))
    Console.WriteLine($"[{issue.Severity}] {issue.Message} → {issue.Suggestion}");
```

---

## 2.5 命名空间 `MCModCoder.Core.Services`

### 2.5.1 `TemplateGenerator`（静态类）

项目脚手架生成器：按 MC 版本与加载器生成完整可构建工程。

```csharp
public static class TemplateGenerator
{
    public static List<string> Generate(McProject p);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `Generate` | `p`：项目（`ProjectDir` 必须已指定） | `List<string>`（相对路径列表） | 按 `p.ModLoader` 分支生成：**Fabric**（fabric-loom 工程 + `fabric.mod.json`）、**Forge 1.12.2**（ForgeGradle 5 + `mcmod.info`）、**Forge 现代版**（ForgeGradle 6 + `mods.toml`）、**NeoForge**（同现代版，依赖 `net.neoforged:neoforge`）、**Rift**（Fabric 风格轻量模板）。额外统一写出 `src/main/resources/pack.mcmeta`（按版本 `PackFormat`）、`.mcmproj.json` 项目元数据、`.gitignore`。依赖列表按 `DependencyEntry.Scope` 展开进 `build.gradle`。 |

```csharp
var p = new McProject { Name = "Ruby", ModLoader = "Fabric", ProjectDir = @"D:\mods\ruby" };
foreach (var rel in TemplateGenerator.Generate(p)) Console.WriteLine(rel);
// build.gradle / settings.gradle / gradle.properties / src/main/java/com/example/ruby/Ruby.java / ...
```

### 2.5.2 `CompletionItem`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Label` | `string` | 补全列表显示文本 | `""` |
| `Insert` | `string` | 插入到编辑器的文本 | `""` |
| `Detail` | `string` | 中文说明 | `""` |

### 2.5.3 `CodeAssist`（静态类）

AI 辅助编写引擎（本地知识库版）：基于上下文正则规则匹配光标前代码，给出 Forge / Fabric 常用 API 补全。

```csharp
public static class CodeAssist
{
    public static List<CompletionItem> GetCompletions(string codeBeforeCursor, string currentWord, int max = 8);
    public static string FormatCode(string code);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `GetCompletions` | `codeBeforeCursor`：光标前代码（自动截取末 400 字符）；`currentWord`：光标处正在输入的词；`max`：返回上限 | `List<CompletionItem>` | 遍历内置规则（`DeferredRegister` 注册、`new Item/Block`、事件类型、`Item.Properties()`/`BlockBehaviour.Properties.of()` 链、`stack.`/`level.`/`player.` 成员、Fabric 注册、1.12.2 旧 API、JSON 配方 `type` 字段等）；无命中时按 `currentWord` 兜底 Java 关键字。忽略大小写、去重。 |
| `FormatCode` | `code`：源码文本 | `string` | 简易缩进规范化：按 `{` `[` 增进、`}` `]` `)` 退进，4 空格缩进，行内去首尾空白。 |

```csharp
var items = CodeAssist.GetCompletions("public static final DeferredRegister<", "I");
foreach (var it in items) Console.WriteLine($"{it.Label} — {it.Detail}");
```

### 2.5.4 `CodeSnippet`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Name` | `string` | 片段名 | `""` |
| `Language` | `string` | 语言：`java` / `json` / `glsl` | `"java"` |
| `Description` | `string` | 描述 | `""` |
| `Code` | `string` | 片段内容 | `""` |

### 2.5.5 `SnippetLibrary`（静态类）

代码片段库（内置 + 用户自定义持久化）。

```csharp
public static class SnippetLibrary
{
    public static List<CodeSnippet> Builtin { get; }
    public static string UserFile { get; }
    public static List<CodeSnippet> LoadUser();
    public static void SaveUser(List<CodeSnippet> snippets);
}
```

| 成员 | 说明 |
|---|---|
| `Builtin` | 8 个内置片段：Forge 主类、Forge 注册物品、Forge 注册方块、Fabric 入口、事件监听、合成配方 JSON、物品模型 JSON、光源方块 GLSL。 |
| `UserFile` | 用户片段文件路径：`%APPDATA%\MCModCoder\snippets.json`。 |
| `LoadUser` | 读取用户片段；文件不存在或解析失败返回空列表。 |
| `SaveUser` | 保存用户片段（自动创建目录）。 |

```csharp
var mine = SnippetLibrary.LoadUser();
mine.Add(new CodeSnippet { Name = "我的片段", Language = "java", Code = "..." });
SnippetLibrary.SaveUser(mine);
```

### 2.5.6 `LogEntry`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Time` | `string` | 日志时间戳原文 | `""` |
| `Thread` | `string` | 线程名 | `""` |
| `Level` | `string` | 级别（INFO / WARN / ERROR / FATAL） | `"INFO"` |
| `Message` | `string` | 消息正文 | `""` |
| `IsError` | `bool`（只读） | Level 为 `ERROR` 或 `FATAL` | — |
| `IsWarn` | `bool`（只读） | Level 为 `WARN` | — |

### 2.5.7 `LogFinding`

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Title` | `string` | 发现标题（如"空指针异常"） | `""` |
| `Detail` | `string` | 细节描述 | `""` |
| `Advice` | `string` | 处置建议 | `""` |
| `Count` | `int` | 命中次数 | `1` |

### 2.5.8 `LogAnalyzer`（静态类）

游戏日志捕获 / 解析 / 分析。

```csharp
public static partial class LogAnalyzer
{
    public static LogEntry ParseLine(string line);
    public static List<LogFinding> Analyze(IEnumerable<LogEntry> entries);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `ParseLine` | `line`：单行日志 | `LogEntry` | 按 MC 标准格式 `[时间] [线程/级别]: 消息` 正则解析；不匹配时整行归入 Message，含 "Exception" 判为 ERROR 级。 |
| `Analyze` | `entries`：日志条目集合 | `List<LogFinding>` | 诊断崩溃报告引用 + 10 类常见模式（NullPointerException、RegistryObject 未初始化、缺依赖、客户端/服务端不一致、Mixin 失败、OutOfMemoryError、GL_INVALID、AbstractMethodError、entrypoint 失败、模组重复）+ 首个异常堆栈 + ERROR/WARN/总行数统计，全部附中文建议。 |

```csharp
var entries = File.ReadLines("latest.log").Select(LogAnalyzer.ParseLine);
foreach (var f in LogAnalyzer.Analyze(entries))
    Console.WriteLine($"{f.Title}（{f.Count}）：{f.Advice}");
```

### 2.5.9 `AiImageAssist`（静态类）

外接 AI 图像辅助引擎：把 OpenAI 兼容接口（`/v1/images/generations`）或 Stable Diffusion WebUI（`/sdapi/v1/txt2img`）接入工作台，文字出图并落盘。设置类 `AiImageSettings` 持久化于 `%APPDATA%\MCModCoder\ai-image.json`。

```csharp
public static class AiImageAssist
{
    public const string PixelStyleCore;          // 通用像素风约束（含 no text/no watermark）
    public const string PixelStyleBackgroundHint;// 纯色背景提示（与无缝平铺互斥）
    public const string PixelStyleSuffix;        // Core + BackgroundHint（历史兼容）
    public static readonly (string Name, string Hint)[] StylePresets;   // 6 种风格预设

    public static string BuildPrompt(string prompt, string? styleHint, bool pixelStyle);
    public static string MergeNegativeIntoPrompt(string prompt, string? negative);
    public static Task<AiImageResult> GenerateAsync(AiImageSettings settings, string prompt,
        string? negativePrompt = null, CancellationToken ct = default);
    public static string SavePng(string dir, byte[] pngBytes, string prompt);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `BuildPrompt` | `prompt`：用户描述；`styleHint`：风格预设片段；`pixelStyle`：是否附加像素风约束 | `string` | 按逗号拆分短语后全局去重拼接；风格含 `seamless`/`tileable` 时自动省略「纯色背景」指令，避免与无缝平铺矛盾导致输出不稳定。 |
| `MergeNegativeIntoPrompt` | `prompt`、`negative` | `string` | 把负面提示词以「Avoid the following: …」融入正向提示词，供无独立负面字段的 OpenAI 兼容接口使用（SD WebUI 走独立字段）。 |
| `GenerateAsync` | 见上 | `AiImageResult` | 流程：提示词审计（空提示词直接拒绝）→ 按服务分支调用（OpenAI 分支对 dall-e-2/3 做尺寸预校验、提示词超 4000 字符截断）→ **自动执行图像审计**写入 `AiImageResult.Audit`。失败抛 `InvalidOperationException`（中文原因）。 |
| `SavePng` | `dir`：目标目录；`pngBytes`；`prompt` | `string`（文件路径） | 落盘前做 PNG 有效性防御校验，坏数据抛异常不产生脏文件；文件名 `ai_yyyyMMdd_HHmmss_<提示词摘要>.png`。 |

```csharp
var result = await AiImageAssist.GenerateAsync(settings, "一把发光的蓝色魔法剑", styleHint, negative);
if (result.Audit is { HasErrors: true }) { /* 审计未通过，阻止使用 */ }
else AiImageAssist.SavePng(projectDir, result.PngBytes, prompt);
```

### 2.5.10 `AiAuditSeverity`（枚举）与审计结果类型

| 类型 | 成员 | 说明 |
|---|---|---|
| `AiAuditSeverity` | `Info` / `Warning` / `Error` | 与 `IssueSeverity` 语义对齐：Error 应阻止进入后续流程（落盘 / 导入），Warning 需人工确认。 |
| `AiAuditFinding` | `Severity`、`Code`、`Message`、`Suggestion` | 单条结论；`Code` 为机器可读问题码（如 `img.solid`、`prompt.long`、`code.balance`），便于程序化处理与测试断言。 |
| `AiAuditReport` | `Target`、`Findings` | 另有 `HasErrors` / `HasWarnings` / `Passed` 属性与 `Summary`（单行中文摘要）、`FirstErrorMessage`。 |

### 2.5.11 `PngInfo` 与 `PngInspector`（静态类）

`PngInspector`：纯 BCL 的 PNG 检查器——遍历 chunk、校验关键块 CRC、zlib/Deflate 解压 IDAT、逐行 unfilter（Sub/Up/Average/Paeth）后做像素统计。**任何内部失败都返回 null 或部分信息，绝不抛异常**；像素统计仅处理 8/16 位、非隔行的常规色型，超大图（原始数据 > 96 MB）自动跳过。

`PngInfo` 关键属性：`Width` / `Height` / `BitDepth` / `ColorType` / `Interlaced`、`FileBytes`、`ChunkCount`、`CrcMismatch`（关键块 CRC 不一致）、`Truncated`（缺 IEND，数据被截断）、`HasIdat`、`HasAlphaChannel`、`PixelStatsAvailable`、`ColorCount`（唯一颜色数）、`TopColorRatio`（主色占比）、`TransparentRatio`（近全透明像素占比）、`NearWhiteRatio`（近纯白像素占比）。

### 2.5.12 `AiOutputAudit`（静态类）

AI 输出审计引擎：对外接 AI 的返回结果做**确定性、可复现**的质量门禁——全部结论由固定阈值规则推出，相同输入必得相同报告；审计过程自身不抛异常。

```csharp
public static class AiOutputAudit
{
    public const int MaxPromptLength;   // 4000（DALL-E 3 上限）
    public static readonly IReadOnlyDictionary<string, string[]> KnownModelSizes; // dall-e-2/3 合法尺寸

    public static AiAuditReport AuditImage(byte[]? png,
        int? expectedWidth = null, int? expectedHeight = null);
    public static AiAuditReport AuditPrompt(string? prompt, int maxLength = MaxPromptLength);
    public static AiAuditReport AuditCode(string? code, string language = "java");
    public static bool IsValidPng(byte[]? png);
}
```

| 方法 | 返回值 | 检查规则（问题码） |
|---|---|---|
| `AuditImage` | `AiAuditReport` | **Error**：`img.empty` 空数据、`img.signature` 非 PNG、`img.parse` 结构解析失败、`img.truncated` 缺 IEND（传输截断）、`img.noidat` 缺图像数据、`img.dims` 尺寸非法。**Warning**：`img.crc` 关键块 CRC 不一致、`img.size` 返回尺寸与请求不符、`img.solid` 纯色图（≤3 色 且主色 ≥99%，疑似渲染失败）、`img.blank` 近纯白、`img.transparent` 近全透明、`img.large` 体积 >25 MB。**Info**：`img.nonsquare` 非方形、`img.stats` 统计跳过说明、`img.summary` 尺寸/颜色数摘要。 |
| `AuditPrompt` | `AiAuditReport` | **Error**：`prompt.empty` 空提示词。**Warning**：`prompt.long` 超 4000 字符、`prompt.placeholder` 含未替换 `{{占位符}}`、`prompt.ctrl` 含控制字符、`prompt.repeat` 超长重复字符序列、`prompt.weak` 无有效文字。 |
| `AuditCode` | `AiAuditReport` | **Error**：`code.empty`。**Warning**：`code.placeholder` 占位符残留、`code.balance` 括号不配对（花/圆/方，扫描时跳过字符串字面量与注释——json 无注释，java/glsl 同规则，显著降低误报）。 |
| `IsValidPng` | `bool` | `AuditImage` 无 Error 即有效；用于 `SavePng` 落盘防御与导入门禁。 |

```csharp
var report = AiOutputAudit.AuditImage(pngBytes, 1024, 1024);
foreach (var f in report.Findings)
    Console.WriteLine($"[{f.Severity}] {f.Code}: {f.Message} → {f.Suggestion}");
```

### 2.5.13 `MiniPng`（静态类，测试辅助）

最小 PNG 编码器（仅 8-bit RGBA、非隔行），用于内置测试套件构造确定性样本；产物符合 PNG 规范（经第三方解码器验证），也可用于审计器对拍。

```csharp
public static byte[] CreateRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> px);
```

---

## 2.6 命名空间 `MCModCoder.Core.Simulation`

### 2.6.1 `SimBlock`（枚举）

红石元件类型。

```csharp
public enum SimBlock
{
    Air, Wire, Torch, Lever, Lamp,
    RepeaterEast, RepeaterWest, RepeaterSouth, RepeaterNorth,   // 中继器朝向
    SolidBlock, Button
}
```

### 2.6.2 `RedstoneGrid`

红石平面网格模拟器（信号强度 15 级衰减）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `W` / `H` | `int`（只读） | 网格宽 / 高 | 构造参数 |
| `Type` | `SimBlock[,]`（只读） | 元件类型网格 | 全 `Air` |
| `Strength` | `int[,]`（只读） | 信号强度 0–15 | 全 0 |
| `OnState` | `bool[,]`（只读） | 持久状态：拉杆开关 / 灯是否点亮 | 全 false |
| `Timer` | `int[,]`（只读） | 按钮剩余计时（刻） | 全 0 |

| 方法 | 签名 | 说明 |
|---|---|---|
| `Place` | `public void Place(int x, int y, SimBlock t)` | 放置元件（越界忽略）；放置拉杆默认断开，按钮计时清零。 |
| `Remove` | `public void Remove(int x, int y)` | 移除元件并清状态。 |
| `Interact` | `public bool Interact(int x, int y)` | 点击交互：拉杆翻转开关，按钮置 10 刻计时；其余返回 `false`。 |
| `Tick` | `public bool Tick()` | 推进 1 刻：按钮倒计时 → 拉杆/按钮供能 15 → 迭代传播（导线每格衰减 1，火把反相输出，中继器满强度隔离）→ 灯依据相邻信号点亮。返回本轮是否有变化。 |
| `In` | `public bool In(int x, int y)` | 坐标是否在网格内。 |

```csharp
var g = new RedstoneGrid(8, 8);
g.Place(1, 1, SimBlock.Lever);
g.Place(2, 1, SimBlock.Wire);
g.Place(3, 1, SimBlock.Wire);
g.Place(4, 1, SimBlock.Lamp);
g.Interact(1, 1);          // 拉下拉杆
g.Tick();                  // 导线 15→14→13，灯点亮
Console.WriteLine(g.OnState[4, 1]);   // True
```

### 2.6.3 `EntitySimulator`

实体行为模拟（游走 AI + 边界反弹 + 同类聚集倾向）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Entities` | `List<Entity>`（只读） | 实体列表 | 空 |

| 方法 | 签名 | 说明 |
|---|---|---|
| `Spawn` | `public void Spawn(string kind, int count, int w, int h)` | 在 `w×h` 区域内随机位置生成 `count` 个指定类型实体，随机朝向。 |
| `Tick` | `public void Tick(int w, int h)` | 步进：5% 概率随机转向 → 按 0.15 格/刻移动 → 边界反弹并 Clamp。 |
| `Clear` | `public void Clear()` | 清空全部实体。 |

嵌套类 `EntitySimulator.Entity`：

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `X` / `Y` | `double` | 当前坐标（公共字段） | `0` |
| `DX` / `DY` | `double` | 方向向量（公共字段） | `0` |
| `Kind` | `string` | 实体类型：`zombie` / `cow` / `sheep` | `"zombie"` |
| `Age` | `int` | 存活刻数 | `0` |

```csharp
var sim = new EntitySimulator();
sim.Spawn("sheep", 10, 40, 30);
for (int i = 0; i < 100; i++) sim.Tick(40, 30);
Console.WriteLine(sim.Entities[0].Age);   // 100
```

## 2.7 命名空间 `MCModCoder.Core.Remote`（远程资源层）

文件：`src/MCModCoder.Core/Remote/`。远程资源层：从 **Modrinth** 与 **CurseForge** 搜索模组/整合包、下载并做 SHA1 校验、解析导入整合包，以及可跨重启恢复的下载队列管理。共 14 个公共类型：`RemotePlatform`、`RemoteSearchHit`、`RemoteFileInfo`、`RemoteRequestException`、`ModrinthClient`、`CurseForgeClient`、`DownloadService`、`ModpackFileEntry`、`ModpackPlan`、`ModpackParser`、`DownloadState`、`DownloadTask`、`ImportedPack`、`ImportManager`。仍遵循零第三方依赖（`HttpClient` + `System.Text.Json` + `System.IO.Compression`）。

数据源约定：

- **Modrinth**：始终走官方 API `https://api.modrinth.com/v2`；
- **CurseForge**：`ApiKey` 为空（默认）时走社区公益镜像 `https://api.curse.tools/v1/cf`（无需密钥）；配置官方密钥后切换 `https://api.curseforge.com/v1`，并在每个请求附加 `x-api-key` 头；
- 所有网络方法均接收 `CancellationToken`；网络层失败（超时 / 请求异常 / JSON 解析失败）一律抛 `RemoteRequestException`（中文提示）。

两个内部辅助类（不对外暴露）：

| 内部类型 | 说明 |
|---|---|
| `RemoteHttp`（静态类） | 共享 `HttpClient`（统一 `User-Agent: MCModCoder/1.0`）与统一入口 `GetJsonAsync(url, configure, ct)`：发起 GET 并解析 JSON，统一把网络相关错误转换为 `RemoteRequestException`。 |
| `RemoteJson`（静态类） | 平台 JSON 的**松散解析辅助**，字段缺失/类型异常时容错返回默认值而非抛异常：`Str`（取字符串，数字/布尔自动转文本）、`Long`（取整数，字符串数字亦可）、`Bool`、`Obj`（取嵌套对象，缺失返回 `default`）、`StrList`（取字符串数组）、`ParseDate`（解析 ISO 8601 时间，失败返回 `DateTime.MinValue`）。 |

### 2.7.1 `RemotePlatform` 与 `DownloadState`（枚举）

```csharp
public enum RemotePlatform { Modrinth, CurseForge }
public enum DownloadState { Queued, Downloading, Verifying, Done, Failed, Cancelled }
```

`DownloadState` 驱动下载管理页的状态显示：`Queued` 排队中 → `Downloading` 下载中 → `Verifying` 校验中 → `Done` 完成；异常路径为 `Failed` / `Cancelled`。

### 2.7.2 `RemoteSearchHit`

搜索结果条目（两平台通用投影）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Platform` | `RemotePlatform` | 来源平台 | `Modrinth` |
| `Id` | `string` | 平台项目 ID（Modrinth 为 GUID；CurseForge 为数字字符串） | `""` |
| `Slug` | `string` | URL 别名 | `""` |
| `Name` | `string` | 项目名 | `""` |
| `Author` | `string` | 作者（CurseForge 取 `authors[0].name`） | `""` |
| `Summary` | `string` | 简介 | `""` |
| `Downloads` | `long` | 累计下载量 | `0` |
| `IconUrl` | `string` | 图标 URL（CurseForge 取 `logo.thumbnailUrl`） | `""` |
| `Categories` | `List<string>` | 分类标签 | 空 |
| `ProjectType` | `string` | 项目类型：`"mod"` / `"modpack"` | `"mod"` |
| `DateModified` | `string` | 最后更新时间（ISO 8601 原文） | `""` |

### 2.7.3 `RemoteFileInfo`

可下载文件信息（版本 + 文件两级信息打平）。

| 属性 | 类型 | 说明 | 默认值 |
|---|---|---|---|
| `Platform` | `RemotePlatform` | 来源平台 | `Modrinth` |
| `ProjectId` | `string` | 所属项目 ID | `""` |
| `FileId` | `string` | 文件 ID（Modrinth 为版本 ID） | `""` |
| `FileName` | `string` | 文件名 | `""` |
| `DisplayName` | `string` | 显示名（Modrinth 与 `FileName` 相同；CurseForge 优先 `displayName`） | `""` |
| `Url` | `string` | 下载直链；CurseForge `downloadUrl` 为空时回退构造官网直链 `https://www.curseforge.com/api/v1/mods/{modId}/files/{fileId}/download` | `""` |
| `Sha1` | `string` | SHA1 十六进制（CurseForge 取 `hashes` 数组中 `algo == 1` 项） | `""` |
| `SizeBytes` | `long` | 文件大小（字节） | `0` |
| `VersionNumber` | `string` | 版本号 | `""` |
| `GameVersions` | `List<string>` | 适配 MC 版本（CurseForge 数组中亦含 `"Forge"` 等加载器名） | 空 |
| `Loaders` | `List<string>` | 加载器（仅 Modrinth 提供） | 空 |
| `ReleaseDate` | `string` | 发布时间（ISO 8601 原文） | `""` |
| `IsPrimary` | `bool` | 是否主文件（仅 Modrinth） | `false` |
| `RequiredDependencyIds` | `List<string>` | required 依赖项目 ID（CurseForge 由 `dependencies[].relationType == 1` 收集；Modrinth 恒为空，依赖经 `ResolveDependenciesAsync` 单独解析） | 空 |

### 2.7.4 `RemoteRequestException`

继承 `Exception`，远程访问层的统一异常类型。`Message` 为可直接展示的中文提示，如「网络请求超时，请检查网络连接」「网络请求失败：…」「远程数据解析失败，请稍后重试」「SHA1 校验失败：xxx.jar（来源文件可能已更新）」「远程文件信息不存在或已被删除」。

### 2.7.5 `ModrinthClient`（静态类）

Modrinth v2 API 客户端（`BaseUrl = "https://api.modrinth.com/v2"`，JSON 松散解析、字段缺失容错）。

```csharp
public static class ModrinthClient
{
    public static Task<List<RemoteSearchHit>> SearchAsync(string query, string? mcVersion, string? loader,
        string projectType, string sort, int limit = 20, int offset = 0, CancellationToken ct = default);
    public static Task<List<RemoteFileInfo>> GetVersionsAsync(string projectIdOrSlug, CancellationToken ct = default);
    public static Task<RemoteFileInfo?> GetBestVersionAsync(string projectIdOrSlug, string mcVersion, string loader,
        CancellationToken ct = default);
    public static Task<List<RemoteFileInfo>> ResolveDependenciesAsync(string projectId, string mcVersion, string loader,
        CancellationToken ct = default);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `SearchAsync` | `query` 关键词；`mcVersion` / `loader` 可空过滤；`projectType`：`"mod"` / `"modpack"`；`sort`：`relevance` / `downloads` / `newest` / `updated`；`limit` / `offset` 分页 | `List<RemoteSearchHit>` | `GET /search`。过滤条件以 **facets** 表达（`project_type:*`、`versions:*`、`categories:*`——加载器转小写），facets 整体 JSON 序列化后 URL 编码拼入 query string。 |
| `GetVersionsAsync` | `projectIdOrSlug` | `List<RemoteFileInfo>` | `GET /project/{id}/version`。每个版本取 `primary` 文件（无则取第一个文件）转换为 `RemoteFileInfo`。 |
| `GetBestVersionAsync` | `projectIdOrSlug`、`mcVersion`、`loader` | `RemoteFileInfo?` | 在全部版本中筛出 `game_versions` 精确包含 MC 版本、`loaders`（忽略大小写）包含加载器的最新版本（按 `date_published` 降序取第一个）；无匹配返回 `null`。 |
| `ResolveDependenciesAsync` | `projectId`、`mcVersion`、`loader` | `List<RemoteFileInfo>` | 解析全部 required 依赖：`GET /project/{id}/dependencies`，从 `versions[].dependencies[]` 收集 `dependency_type == "required"` 的 project id（识别不出 required 标记时视返回的全部项目为 required）；逐个调 `GetBestVersionAsync` 取文件，并**递归**解析依赖项目自身的依赖——**深度上限 3**、按 project id **去重防环**，仅收集 `project_type == "mod"`；结果不含根项目自身的文件。 |

### 2.7.6 `CurseForgeClient`（静态类）

CurseForge API 客户端（镜像 / 官方可切换，路由一致）。

```csharp
public static class CurseForgeClient
{
    public static string? ApiKey { get; set; }
    public static string BaseUrl { get; }

    public static Task<List<RemoteSearchHit>> SearchAsync(string query, string? mcVersion, string? loader,
        string projectType, string sort, int limit = 20, int offset = 0, CancellationToken ct = default);
    public static Task<List<RemoteFileInfo>> GetFilesAsync(string modId, CancellationToken ct = default);
    public static Task<RemoteFileInfo?> GetBestFileAsync(string modId, string mcVersion, string? loader,
        CancellationToken ct = default);
    public static Task<RemoteFileInfo> GetFileAsync(string modId, string fileId, CancellationToken ct = default);
}
```

| 成员 | 说明 |
|---|---|
| `ApiKey` | 官方 API 密钥。**为空（默认）时请求社区公益镜像 `https://api.curse.tools/v1/cf`（无需密钥）；配置后切换官方 `https://api.curseforge.com/v1`，并在每个请求附加 `x-api-key` 头。** |
| `BaseUrl` | 按 `ApiKey` 是否为空返回镜像或官方基地址。 |
| 常量 `GameId = 432` | Minecraft 的 CurseForge 游戏 ID，随搜索请求提交。 |

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `SearchAsync` | 同 `ModrinthClient.SearchAsync`；`sort`：`popularity` / `downloads` / `updated` / `name` | `List<RemoteSearchHit>` | `GET {base}/mods/search?gameId=432`。参数映射：`sort` → `sortField`（popularity→2、downloads→6、updated→3、name→4，默认 2，`sortOrder=desc`）；`projectType` → `classId`（mod→4、modpack→4471）；`loader` → `modLoaderType`（forge→1、fabric→4、neoforge→6，其余忽略）；`mcVersion` → `gameVersion`；`limit` / `offset` → `pageSize` / `index`。 |
| `GetFilesAsync` | `modId` | `List<RemoteFileInfo>` | `GET {base}/mods/{id}/files`，返回模组全部文件。文件转换时 `hashes` 数组取 `algo == 1`（SHA1）项；`downloadUrl` 为空时回退构造官网直链。 |
| `GetBestFileAsync` | `modId`、`mcVersion`、`loader`（可空） | `RemoteFileInfo?` | 基于 `GetFilesAsync` 结果筛选：`gameVersions` 需包含 MC 版本，指定加载器时还需包含加载器名（CF 的 `gameVersions` 同时含 `"1.20.1"` 与 `"Forge"` 等）；按 `fileDate` 降序取最新；无匹配返回 `null`。 |
| `GetFileAsync` | `modId`、`fileId` | `RemoteFileInfo` | `GET {base}/mods/{id}/file/{fileId}`，整合包 manifest 解析用；文件不存在时抛 `RemoteRequestException("远程文件信息不存在或已被删除")`。 |

文件对象转换规则（`GetFilesAsync` / `GetFileAsync` 共用）：`hashes[]` 取 `algo == 1`（SHA1）项为 `Sha1`；`downloadUrl` 为空时回退构造官网直链为 `Url`；`dependencies[]` 中 `relationType == 1`（required）的 `modId` 记入 `RequiredDependencyIds`。

### 2.7.7 `DownloadService`（静态类）

```csharp
public static class DownloadService
{
    public static Task DownloadFileAsync(string url, string destPath, string? expectedSha1,
        IProgress<double>? progress, CancellationToken ct = default);
    public static Task<string> ComputeSha1Async(string filePath, CancellationToken ct = default);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `DownloadFileAsync` | `url` 直链；`destPath` 目标路径；`expectedSha1` 期望哈希（可空）；`progress` 0~1 进度回调（可空） | `Task` | 先下载到 `destPath + ".part"` 临时文件，成功后改名覆盖目标。**失败自动重试 3 次（指数退避 1s / 2s / 4s）**；`progress` 上报 0~1（未知长度按已收字节 / 16MB 估算，封顶 0.99）。`expectedSha1` 非空时下载完成后校验，不匹配则删除临时文件并抛 `RemoteRequestException`（「SHA1 校验失败：…」）。取消与网络异常时同样清理临时文件。 |
| `ComputeSha1Async` | `filePath` | `string`（十六进制大写） | 流式计算文件 SHA1。 |

### 2.7.8 `ModpackParser` 与 `ModpackPlan` / `ModpackFileEntry`

```csharp
public class ModpackFileEntry
{
    public string RelativePath { get; set; }   // 相对导入目录的落盘路径，如 "mods/jei-1.20.1-forge.jar"
    public string Url { get; set; }            // 下载直链
    public string Sha1 { get; set; }           // 来源哈希（可空）
    public long SizeBytes { get; set; }
    public string Source { get; set; }         // "Modrinth" / "CurseForge"
}

public class ModpackPlan
{
    public string Name { get; set; }           // 整合包名（缺省取文件名）
    public string McVersion { get; set; }
    public string Loader { get; set; }         // "Forge"/"NeoForge"/"Fabric"/"Quilt"，可能为空
    public RemotePlatform Platform { get; set; }
    public string PackPath { get; set; }       // .mrpack/.zip 原始路径
    public List<ModpackFileEntry> Files { get; set; }
}

public static class ModpackParser
{
    public static ModpackPlan? ParseMrpack(string zipPath);
    public static Task<ModpackPlan?> ParseCurseForgeAsync(string zipPath, CancellationToken ct = default);
    public static void ExtractOverrides(string zipPath, string targetDir);
}
```

| 方法 | 参数 | 返回值 | 说明 |
|---|---|---|---|
| `ParseMrpack` | `zipPath` | `ModpackPlan?` | 解析 Modrinth 整合包（zip 内 `modrinth.index.json`）：`dependencies.minecraft` → MC 版本，`forge` / `neoforge` / `fabric-loader` / `quilt-loader` 键存在即确定加载器；`files[].path / hashes.sha1 / size / downloads[0]` → 下载计划。**纯本地解析不联网**；格式不符或损坏返回 `null`。 |
| `ParseCurseForgeAsync` | `zipPath` | `ModpackPlan?` | 解析 CurseForge 整合包（zip 内 `manifest.json`）：`minecraft.version` → MC 版本，`modLoaders[].id` 前缀（`neoforge-` / `forge-` / `fabric-`）→ 加载器；`files[].projectID / fileID`（`required` 缺省视为 true）需**逐个联网**调用 `CurseForgeClient.GetFileAsync` 换取下载信息（网络错误以 `RemoteRequestException` 上抛），落盘路径固定为 `mods/<fileName>`。格式不符返回 `null`。 |
| `ExtractOverrides` | `zipPath`、`targetDir` | `void` | 把 zip 内 `overrides/` 前缀条目解压到 `targetDir`（去前缀、覆盖同名文件、跳过目录条目）。**Zip-Slip 防护**：解压目标经 `Path.GetFullPath` 规范化后必须仍位于 `targetDir` 内，越界条目直接跳过。 |

### 2.7.9 `ImportManager` 与 `DownloadTask` / `ImportedPack`

```csharp
public class DownloadTask
{
    public string Name { get; set; }           // 队列显示名
    public string Url { get; set; }
    public string DestPath { get; set; }
    public string? Sha1 { get; set; }          // 非空时下载后自动校验
    public long SizeBytes { get; set; }
    public RemotePlatform Platform { get; set; }
    public DownloadState State { get; set; }   // 默认 Queued
    public double Progress { get; set; }       // 0~1
    public string Error { get; set; }          // 失败/取消原因（"已取消" 或异常消息）
}

public class ImportedPack
{
    public string Name { get; set; }
    public string Dir { get; set; }            // 导入目录
    public string McVersion { get; set; }
    public string Loader { get; set; }
    public string Source { get; set; }         // "Modrinth" / "CurseForge"
    public DateTime ImportTime { get; set; }   // 默认 DateTime.Now
}

public static class ImportManager
{
    public static event Action? Changed;
    public static IReadOnlyList<DownloadTask> Tasks { get; }          // 快照
    public static IReadOnlyList<ImportedPack> ImportedPacks { get; }  // 快照

    public static DownloadTask Enqueue(string name, string url, string destPath,
        string? sha1, long size, RemotePlatform platform);
    public static void Start();
    public static void Cancel(DownloadTask t);
    public static void Retry(DownloadTask t);
    public static void Remove(DownloadTask t);
    public static void RegisterImport(ModpackPlan plan, string importDir);
    public static void SaveHistory(string dir);
    public static void LoadHistory(string dir);
}
```

全部成员线程安全（内部锁保护）。

| 成员 | 说明 |
|---|---|
| `Changed` | 队列任何变化（入队、状态切换、进度更新、登记、移除）后触发，供 UI 绑定刷新；进度上报按 **≥2% 步长节流**；处理器异常被吞掉，不影响队列运行。 |
| `Tasks` / `ImportedPacks` | 当前任务 / 已导入整合包的快照列表（每次返回拷贝）。 |
| `Enqueue` | 新建 `DownloadTask`（`State = Queued`）入队并自动 `Start()`，返回任务实例。 |
| `Start` | 启动后台泵循环（幂等）：每 500ms 扫描队列取 `Queued` 任务，`SemaphoreSlim(3, 3)` 控制**最多 3 并发**下载。 |
| `Cancel` | `Queued` 任务直接置 `Cancelled`；`Downloading` / `Verifying` 任务通过内部 `CancellationTokenSource` 中断下载。 |
| `Retry` | 仅 `Failed` / `Cancelled` 可重试：状态复位 `Queued`、清空 `Error` 与 `Progress`、换新取消源并重新排队。 |
| `Remove` | 从队列移除任务；若在下载中同时中断。 |
| `RegisterImport` | 由 `ModpackPlan` + 导入目录生成一条 `ImportedPack` 记录（来源取平台名）。 |
| `SaveHistory` | 持久化到 `dir` 下 `downloads.json`（任务）与 `imports.json`（导入记录），枚举序列化为字符串，失败静默忽略。 |
| `LoadHistory` | 从 `dir` 恢复（文件损坏容错）。恢复时 `Queued` / `Downloading` / `Verifying` 等中间态一律视为 `Failed`（应用重启所致，可手动重试），`Done` 任务进度置 1。 |

任务执行细节：下载进度达 1.0 且任务带 `Sha1` 时先进入 `Verifying` 再落盘定态；失败时 `Error` 记录异常消息且进度归零；取消置 `Error = "已取消"`。

```csharp
// 示例：搜索 Modrinth 模组 → 取最佳版本 → 入队下载（自动 SHA1 校验，3 并发）
var hits = await ModrinthClient.SearchAsync("jei", "1.20.1", "forge", "mod", "relevance", limit: 20);
var file = await ModrinthClient.GetBestVersionAsync(hits[0].Id, "1.20.1", "forge");
ImportManager.Enqueue(file!.DisplayName, file.Url,
    @"D:\mods\demo\workspace\remote\mods\" + file.FileName,
    file.Sha1, file.SizeBytes, file.Platform);
ImportManager.Changed += () =>
    Console.WriteLine(string.Join("\n", ImportManager.Tasks.Select(t => $"{t.Name}: {t.State} {t.Progress:P0}")));
```

### 2.7.10 远程 API 端点一览

| 平台 | 端点 | 用途（调用方） |
|---|---|---|
| Modrinth | `GET https://api.modrinth.com/v2/search` | 项目搜索（facets 过滤，`SearchAsync`） |
| Modrinth | `GET https://api.modrinth.com/v2/project/{id}/version` | 版本列表 / 最佳版本（`GetVersionsAsync` / `GetBestVersionAsync`） |
| Modrinth | `GET https://api.modrinth.com/v2/project/{id}/dependencies` | required 依赖解析（`ResolveDependenciesAsync`） |
| CurseForge | `GET {base}/mods/search`（`gameId=432`） | 项目搜索（`CurseForgeClient.SearchAsync`） |
| CurseForge | `GET {base}/mods/{id}/files` | 文件列表 / 最佳文件（`GetFilesAsync` / `GetBestFileAsync`） |
| CurseForge | `GET {base}/mods/{id}/file/{fileId}` | 单文件信息（`GetFileAsync`，整合包 manifest 解析） |

> `{base}` 为 `https://api.curse.tools/v1/cf`（社区镜像，默认）或 `https://api.curseforge.com/v1`（官方，配置 `CurseForgeClient.ApiKey` 后）。公共请求头 `User-Agent: MCModCoder/1.0`；官方源另需 `x-api-key`。

---

## 2.8 命名空间 `MCModCoder.Core.Ai`（AI 开发引擎）

面向「AI 独立完成模组与整合包开发」的技能化引擎：所有能力（文件读写、代码生成、构建打包等）以 `ISkill` 接口形式封装，`SkillRegistry` 负责技能的注册、发现与调用，`AiDevEngine` 负责需求分析 → 任务规划 → 逐技能执行 → 结果解析推进的完整闭环。

### 2.8.1 技能接口与注册表（SkillSystem.cs）

| 类型 | 说明 |
|---|---|
| `ISkill` | 技能接口：`Name`（点分唯一名，如 `file.write`）、`DisplayName`、`Description`、`Category`（Planning/File/Code/Resource/Build/Package）、`Params`（参数说明书）、`Execute(context, args)` |
| `SkillParam` | 参数描述：名称、说明、是否必需（供发现阶段组装参数） |
| `SkillResult` | 执行结果：`Success`、`Message`（摘要）、`Outputs`（结构化输出字典）、`Error`；静态 `Ok` / `Fail` 工厂 |
| `SkillContext` | 执行上下文：`Project`（当前项目）、`Scratch`（跨技能共享草稿）、`Log`（日志回调） |
| `SkillRegistry` | 注册/发现/调用中枢：`Register`（同名覆盖，幂等）、`Unregister`、`Get`（精确）、`Discover(keyword, category)`（模糊评分：名称前缀 100 / 包含 70 / 显示名或分类 50 / 描述 30）、`Invoke`（缺参校验 → 执行 → 异常兜底） |

### 2.8.2 内置技能（BuiltinSkills.cs，`AiSkills.RegisterAll` 一键注册）

| 技能名 | 类别 | 功能 |
|---|---|---|
| `file.read` / `file.write` / `file.list` | File | 项目内文件读写与清单（路径穿越防御：拒绝绝对路径与 `..`） |
| `plan.analyze` | Planning | 需求分析：解析模组名 / MC 版本 / 加载器 / 功能要素 / 配方与整合包意图 |
| `project.scaffold` | Code | 生成完整 Gradle 工程脚手架（包装 `TemplateGenerator`，已存在时跳过） |
| `code.generate` | Code | 按要素生成 Java 注册类（Forge / Forge 1.12.2 / NeoForge / Fabric 模板自选） |
| `code.snippet` | Code | 从片段库取模板代码并按项目替换占位符（包装 `SnippetLibrary`） |
| `resource.generate` | Resource | 生成物品/方块模型 JSON 与占位纹理到 `src/main/resources/assets/<modid>/` |
| `resource.recipe` | Resource | 生成合成/熔炼配方 JSON 到 `data/<modid>/recipes/`（写入前过校验器） |
| `resource.export_pack` | Resource | 汇出标准资源包目录 + zip（包装 `ResourcePackExporter`） |
| `build.project` | Build | 构建 jar：有 `gradlew.bat` 走真实 Gradle，否则内置模拟构建（产出含加载器描述文件与资源的真实 zip） |
| `pack.modpack` | Package | 打包整合包：资源包 + 数据包 + `modlist.txt` → `modpack/` + `modpack.zip` |
| `pack.verify` | Package | 产物验证：jar 内加载器描述文件 / 整合包结构（pack.mcmeta、modlist） |

### 2.8.3 引擎与规划（AiDevEngine.cs）

- `RequirementAnalyzer.Analyze(text)` → `RequirementFacts`：本地规则解析（离线、确定性），提取模组名、版本、加载器、功能要素（中文需求回退 `kind_哈希` 注册 ID）、配方原料（「用铁锭和木棍合成」→ `minecraft:iron_ingot` + `minecraft:stick`）与整合包意图。
- `AiDevEngine.Plan(requirement, project)` → `DevPlan`：自主规划任务序列（需求分析 → 脚手架 → 代码 ×N → 资源 ×N → 配方 → 构建 → 打包 → 验证）；每个 `DevTask` 携带首选技能名、参数与模糊发现关键词。
- `AiDevEngine.ExecutePlan(plan, project)`：逐任务执行 —— 技能精确匹配，未注册时按关键词模糊发现补位 → `Invoke` → 解析 `SkillResult` 推进状态（Done / Failed / Skipped）；前置失败自动跳过后续（验证任务始终执行以输出报告）。执行轨迹写入 `Log`。
- `Run(requirement, project)`：一步规划 + 执行。
- 自定义扩展：`new AiDevEngine(myRegistry)` 注入宿主注册表，或向默认注册表 `Register` 自定义 `ISkill`。

### 2.8.4 最小示例

```csharp
var project = new McProject { Name = "RubyMod", ModId = "rubymod",
    McVersion = "1.20.1", ModLoader = "Forge", Package = "com.example.rubymod",
    ProjectDir = @"D:\projects\RubyMod" };

var engine = new AiDevEngine();                       // 注册全部内置技能
var plan = engine.Plan("开发 RubyMod 模组：注册 ruby 物品与 ruby_pickaxe 工具，" +
                       "ruby_pickaxe 用铁锭和木棍合成，构建并打包整合包", project);
engine.ExecutePlan(plan, project);
// plan.Tasks 全部 Done；产物：src/main/java/.../Ruby.java、assets 模型与纹理、
// data/.../ruby_pickaxe.json、build/libs/rubymod-1.0.0.jar、modpack/modpack.zip
```

回归覆盖：内置测试套件「AI 开发引擎」段（TestView → ▶ 运行内置测试套件）。

---

# 3. App 层集成 API（AppContext）

命名空间 `MCModCoder`。文件：`src/MCModCoder.App/AppContext.cs`。
全局应用上下文：项目状态、目录约定与跨模块数据流转中心。所有成员均为 `static`，各模块共享同一项目实例。

## 3.1 属性

| 成员 | 类型 | 说明 |
|---|---|---|
| `CurrentProject` | `McProject?`（只读） | 当前打开的项目；未打开为 `null`。由 `OpenProject` / `CloseProject` 维护。 |
| `RecentFile` | `string`（只读） | 最近项目记录文件：`%APPDATA%\MCModCoder\recent.json`。 |
| `RecentProjects` | `List<(string Name, string Path)>`（只读） | 最近项目列表（内存态，最多 8 条，最新在前）。 |

## 3.2 方法

```csharp
public static class AppContext
{
    // 项目生命周期
    public static void OpenProject(McProject p);
    public static void CloseProject();

    // 状态栏
    public static void SetStatus(string msg);

    // 最近项目
    public static void LoadRecent();
    public static void PushRecent(string path, string name);

    // 目录约定（项目内工作区）
    public static string DirTextures(McProject p);
    public static string DirRecipes(McProject p);
    public static string DirStructures(McProject p);
    public static string DirMaps(McProject p);
    public static string DirDungeons(McProject p);
    public static string DirShaders(McProject p);
    public static string DirEffects(McProject p);
    public static string DirRemote(McProject p);
    public static string DirCode(McProject p);

    // 跨模块数据推送
    public static void PushStructure(StructureData s);
    public static void PushTexture(string textureName);
}
```

| 方法 | 参数 | 说明 |
|---|---|---|
| `OpenProject` | `p`：项目实例 | 设为当前项目 → 创建全部工作区目录（见下表）→ 写入最近项目 → 触发 `ProjectChanged` → 状态栏提示"已打开项目"。 |
| `CloseProject` | — | 置空 `CurrentProject` → 触发 `ProjectChanged` → 状态栏提示"已关闭项目"。 |
| `SetStatus` | `msg`：消息文本 | 触发 `StatusMessage` 事件（MainWindow 将其显示在状态栏，任何模块亦可订阅）。 |
| `LoadRecent` | — | 启动时从 `RecentFile` 读取最近项目到 `RecentProjects`；解析失败静默忽略。 |
| `PushRecent` | `path`：项目文件路径；`name`：项目名 | 去重后插入列表头，裁剪到 8 条，持久化到 `RecentFile`。 |
| `Dir*` 系列 | `p`：项目 | 返回项目内约定子目录：`workspace/textures`、`workspace/recipes`、`workspace/structures`、`workspace/maps`、`workspace/dungeons`、`workspace/shaders`、`workspace/effects`、`workspace/remote`（远程资源根目录，含 `mods/`、`packs/`、`modpacks/` 与 `downloads.json` / `imports.json` 下载历史）；`DirCode` 返回 `src/main/java`。**打开项目时前 8 个目录自动创建。** |
| `PushStructure` | `s`：结构数据 | 触发 `StructurePushed`。典型用法：地牢编辑器 `DungeonView` 把生成的房间推给结构设计器。 |
| `PushTexture` | `textureName`：纹理显示名 | 触发 `TexturePushed`。典型用法：像素编辑器导出纹理后通知渲染/3D 预览刷新。 |

## 3.3 事件

| 事件 | 签名 | 触发时机 |
|---|---|---|
| `StatusMessage` | `Action<string>?` | 任何模块调用 `SetStatus` 时（含 `OpenProject` / `CloseProject` 内部调用）。 |
| `ProjectChanged` | `Action?` | 项目打开或关闭后。订阅者应重读 `CurrentProject` 刷新自身。 |
| `StructurePushed` | `Action<StructureData>?` | `PushStructure` 被调用时。 |
| `TexturePushed` | `Action<string>?` | `PushTexture` 被调用时（参数为纹理显示名）。 |

## 3.4 集成示例

```csharp
// 1) 打开项目（MainWindow 读取 .mcmproj.json 后调用）
var p = JsonSerializer.Deserialize<McProject>(File.ReadAllText(file),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
AppContext.OpenProject(p!);

// 2) 模块内使用目录约定
var dir = AppContext.DirStructures(AppContext.CurrentProject!);
Directory.CreateDirectory(dir);

// 3) 订阅事件：刷新 UI / 接收跨模块数据
AppContext.StatusMessage += msg => StatusText.Text = msg;
AppContext.ProjectChanged += () => RefreshMine();
AppContext.StructurePushed += s =>
{
    CopyInto(s);                                   // 拷贝到本地编辑状态
    AppContext.SetStatus($"已接收结构「{s.Name}」"); // 再向状态栏回执
};

// 4) 推送数据给其他模块
AppContext.PushTexture(dlg.Value.Trim());
```

---

# 4. 扩展指南

## 4.1 新增一个模块视图（XAML + code-behind）

现有 12 个模块视图（overview / pixel / recipe / structure / map / dungeon / code / devenv / test / sim / shader / render）遵循统一约定，新增步骤：

**① 创建视图文件**：在 `src/MCModCoder.App/Modules/` 新建 `MyFeatureView.xaml` + `MyFeatureView.xaml.cs`。

- 命名空间统一为 `MCModCoder.Modules`，类为 `public partial class MyFeatureView : UserControl`；
- 构造函数第一行 `InitializeComponent()`，随后可初始化本地状态并**订阅 `AppContext` 事件**（如 `StructureEditorView` 在构造函数中订阅 `StructurePushed`）；
- 在 XAML 根节点声明 `x:Class="MCModCoder.Modules.MyFeatureView"`，样式复用 `Themes/Dark.xaml` 主题资源。

```xml
<!-- MyFeatureView.xaml（要点） -->
<UserControl x:Class="MCModCoder.Modules.MyFeatureView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <!-- 模块内容 -->
    </Grid>
</UserControl>
```

```csharp
// MyFeatureView.xaml.cs（要点）
namespace MCModCoder.Modules;

public partial class MyFeatureView : UserControl
{
    public MyFeatureView()
    {
        InitializeComponent();
        AppContext.ProjectChanged += () => Refresh();   // 响应项目切换
    }

    private void Refresh() { /* 读取 AppContext.CurrentProject 刷新自身 */ }
}
```

**② 注册导航**（`MainWindow.xaml` / `MainWindow.xaml.cs`）：

1. 在 `MainWindow.xaml` 左侧 `NavPanel` 中，于对应分组位置添加单选按钮（加入 `GroupName="nav"`，套用 `NavButton` 样式）：

```xml
<RadioButton x:Name="NavMyFeature" Content="  我的功能" Style="{StaticResource NavButton}" GroupName="nav"/>
```

2. 在 `MainWindow` 构造函数中挂接 `Checked` 事件：

```csharp
NavMyFeature.Checked += (_, _) => Navigate("myfeature");
```

3. 在 `GetView(string key)` 的 switch 中添加懒加载分支（视图实例缓存在 `_views` 字典中，切换页签不重建）：

```csharp
"myfeature" => _views.TryGetValue(key, out var v) ? v : _views[key] = new MyFeatureView(),
```

4. 在 `Navigate(string key)` 内的 `names` 字典中补充中文标题：

```csharp
["myfeature"] = "我的功能",
```

无需其他注册。未打开项目时 `Navigate` 会自动拦截非 overview 页签并提示"请先创建或打开一个项目"，新视图天然受此保护。

## 4.2 新增一个 Core 服务类

以新增"战利品表生成器"为例：

1. **落位**：在 `src/MCModCoder.Core/Services/`（或更贴切的子目录，如 `Serialization/`）新建 `LootTableGenerator.cs`，命名空间 `MCModCoder.Core.Services`。
2. **形态**：无状态能力用 `static class` + 静态方法（对齐 `TemplateGenerator` / `RecipeJsonSerializer`）；需要配置则配一个 POCO（对齐 `DungeonGenRules`）。遵循既有惯例：
   - 返回值用 `List<string>` 表示写出的文件路径集合；
   - 所有 JSON 读写用 `System.Text.Json`（`WriteIndented = true`）；
   - `Load/Parse` 类方法失败返回 `null` / 空集合，**不要抛异常**；
   - 属性默认值必须是可直接使用的合理配置；
   - 不引用任何 WPF / App 类型；不新增 NuGet 依赖。
3. **App 接入**：在对应模块视图 code-behind 中直接调用静态方法，完成后调用 `AppContext.SetStatus("...")` 反馈状态栏；产物按目录约定写入 `AppContext.Dir*(AppContext.CurrentProject!)` 对应子目录。

```csharp
namespace MCModCoder.Core.Services;

public static class LootTableGenerator          // 示例骨架
{
    public static string Generate(string blockId) => $$"""
        { "type": "minecraft:block", "pools": [ { "rolls": 1,
          "entries": [ { "type": "minecraft:item", "name": "{{blockId}}" } ] } ] }
        """;
}
```

## 4.3 通过事件接入跨模块数据流

`AppContext` 是唯一的跨模块总线（模块间不互相引用）。现有两条数据流 + 两条通知流：

| 数据流 | 方向 | 流程 |
|---|---|---|
| 结构 | 地牢编辑器 → 结构设计器 | `DungeonView` 调 `AppContext.PushStructure(s)` → `StructureEditorView` 构造时订阅的 `StructurePushed` 回调把数据拷入本地 `_struct` 并 `SetStatus` 回执 |
| 纹理 | 像素编辑器 → 渲染 / 3D 预览 | `PixelEditorView` 调 `AppContext.PushTexture(name)` → 订阅 `TexturePushed` 的模块刷新对应纹理 |
| 项目切换 | MainWindow / 模块 → 全体 | `OpenProject` / `CloseProject` 内部触发 `ProjectChanged` → 各视图刷新 |
| 状态消息 | 任意模块 → 状态栏 | `SetStatus` → `StatusMessage` → MainWindow `StatusText` |

新增一条数据流的步骤：

1. 在 `AppContext` 中成对添加"事件 + 推送方法"（保持与现有 4 组相同的形态）：

```csharp
/// <summary>地图编辑器向外部推送地形图</summary>
public static event Action<TerrainMap>? MapPushed;
public static void PushMap(TerrainMap m) => MapPushed?.Invoke(m);
```

2. 生产方模块在操作完成时调用 `PushMap(...)`；
3. 消费方模块在**构造函数**中订阅（视图是懒加载单例，订阅一次即长期有效）；若需双向同步，可参考 `PixelEditorView` 与 `StructureEditorView` 的做法——消费后在状态栏回执，便于用户确认数据已流转。

---

# 5. JSON 数据格式说明

## 5.1 配方 JSON

工作台配方库文件（`workspace/recipes/<配方路径>.json`）采用 **app 包装格式**——外层 `app` 存放工作台私有元数据，内层 `recipe` 为 Minecraft 原版数据包格式，二者可独立消费：

```json
{
  "app": {
    "category": "常规",
    "description": "备注说明"
  },
  "recipe": {
    "type": "minecraft:crafting_shaped",
    "pattern": [ "XXX", " # ", " # " ],
    "key": {
      "X": "mymod:ruby",
      "#": "minecraft:stick"
    },
    "result": { "item": "mymod:ruby_pickaxe" }
  }
}
```

`recipe` 内层字段随 `type` 变化（与 `RecipeJsonSerializer.ToDatapackJson` 输出一一对应）：

| `type` | 字段 | 说明 |
|---|---|---|
| `minecraft:crafting_shaped` | `pattern`（字符串数组，空格为空位，≤3 行）、`key`（字符 → 配料）、`result` | 配料可为 `"命名空间:路径"` 字符串或 `"#命名空间:标签"`；`result.count` 仅在 ≠1 时写出；`result.nbt` 可选。 |
| `minecraft:crafting_shapeless` | `ingredients`（配料数组，≤9 项）、`result` | 空配料在导出时被剔除。 |
| `minecraft:smelting` / `blasting` / `smoking` / `campfire_cooking` | `ingredient`、`cookingtime`、`experience`、`result` | `cookingtime` 默认 200；`experience` 默认 0.1。 |
| `minecraft:stonecutting` | `ingredient`、`count`、`result` | **特例**：`result` 为物品 ID 字符串（非对象）；`count` 取自 `Result.Count`（≥1）。 |
| `minecraft:smithing_transform` | `template`（可选）、`base`、`addition`、`result` | 兼容读取旧 `minecraft:smithing`。 |
| `mcmcoder:brewing`（工作台扩展） | `ingredient`（酿造材料）、`base`（底酒，可选）、`brewing_time`、`result` | 非原版类型；读取时 `brewing_time` 回填到 `CookingTime`。 |

> `result` 对象通用字段：`item`（必填）、`count`（默认 1，≠1 才写出）、`nbt`（可选）。从文件加载时兼容 `id` 作为物品键名、`result` 为字符串等原版变体写法。

## 5.2 效果文件 `.mceffect.json`

渲染编辑器保存的三合一效果包（默认目录 `workspace/effects/`），序列化自 `EffectBundle { Particle, Material, Animation }`：

```json
{
  "Particle": {
    "Name": "MyEffect", "RatePerSecond": 40, "Lifetime": 1.2, "Speed": 1.0,
    "Gravity": 0.4, "Spread": 0.6, "ColorFrom": "#FFE066", "ColorTo": "#FF5722",
    "SizeStart": 4.0, "SizeEnd": 0.5, "Additive": true, "MaxCount": 600, "Shape": "cone"
  },
  "Material": {
    "Name": "MyMaterial", "Metalness": 0.0, "Roughness": 0.8, "Emission": 0.0,
    "BaseColor": "#C8C8C8", "UseNormalMap": true, "UseSpecularMap": false,
    "RenderLayer": "solid"
  },
  "Animation": {
    "Name": "SizeOverLife",
    "Keys": [ { "Time": 0, "Value": 1 }, { "Time": 0.4, "Value": 0.8 }, { "Time": 1, "Value": 0 } ]
  }
}
```

| 分组 | 字段要点 |
|---|---|
| `Particle` | 见 2.2.20 `ParticleConfig`；`Shape` 取 `point` / `cone` / `ring`；颜色为 `#RRGGBB`。 |
| `Material` | 见 2.2.21 `MaterialConfig`；`RenderLayer` 取 `solid` / `cutout` / `translucent`。 |
| `Animation` | 见 2.2.23 `AnimationConfig`；`Keys` 为 0–1 归一化关键帧曲线。 |

## 5.3 结构文件 `.mcstructure.json`

`StructureData` 的直接 JSON 投影（`StructureSerializer.Save/Load`，默认目录 `workspace/structures/`）：

```json
{
  "Name": "Altar",
  "SizeX": 3, "SizeY": 2, "SizeZ": 3,
  "Palette": [
    "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:oak_planks",
    "minecraft:glass", "minecraft:oak_log", "minecraft:bricks", "minecraft:iron_block",
    "minecraft:gold_block", "minecraft:diamond_block", "minecraft:glowstone", "minecraft:water"
  ],
  "Blocks": { "0,0,0": 1, "2,0,0": 10, "1,1,1": 1 }
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `Name` | `string` | 结构名；`GeneratePlaceCommand` 会转为小写并以 `_` 替换空格。 |
| `SizeX` / `SizeY` / `SizeZ` | `int` | 三向尺寸。 |
| `Palette` | `string[]` | 调色板，下标 0 恒为 `minecraft:air`。 |
| `Blocks` | `map<"x,y,z", int>` | 稀疏方块表；值为调色板下标，空气（0）不写入。配套指令：`/place template <命名空间>:<名称> ~ ~ ~`。 |

## 5.4 光影配置 `.mcshader.json`

`ShaderConfig` 的直接 JSON 投影（`ShaderView` 保存/加载，默认目录 `workspace/shaders/`）：

```json
{
  "Name": "MyShader",
  "SunAngle": 45, "SunAzimuth": 135, "SunIntensity": 1.0, "SunColor": "#FFF3D6",
  "ShadowStrength": 0.6, "ShadowSoftness": 0.5,
  "AmbientIntensity": 0.35, "AmbientColor": "#8FA6C8",
  "FogDensity": 0.015, "FogColor": "#A8C4E0", "FogHeightFalloff": 0.5,
  "EmissiveBoost": 1.0, "WaterTint": "#3F76E4",
  "VolumetricLight": false, "TonemappingACES": true
}
```

| 字段组 | 说明 |
|---|---|
| 光源 | `SunAngle`（0–90 高度角）、`SunAzimuth`（0–360 方位角）、`SunIntensity`、`SunColor`。 |
| 阴影 | `ShadowStrength`（0–1）、`ShadowSoftness`。 |
| 环境光 | `AmbientIntensity`、`AmbientColor`。 |
| 雾效 | `FogDensity`、`FogColor`、`FogHeightFalloff`。 |
| 其他 | `EmissiveBoost`、`WaterTint`、`VolumetricLight`、`TonemappingACES`。 |

配套导出：`ShaderConfig.ToGlsl()` 生成 `.glsl` 文件（uniforms 声明 + `applyFog` / `applyLighting` 函数），供光影包直接引用。

## 5.5 项目文件 `.mcmproj.json`

`McProject` 的直接 JSON 投影（`MainWindow` 打开时按 `PropertyNameCaseInsensitive` 反序列化；`TemplateGenerator.Generate` 在创建脚手架时写出）：

```json
{
  "Name": "MyMod", "ModId": "mymod", "McVersion": "1.20.1", "ModLoader": "Forge",
  "Package": "com.example.mymod", "Author": "", "Description": "",
  "ProjectDir": "D:\\mods\\mymod",
  "CreatedAt": "2026-10-01T10:00:00", "ModifiedAt": "2026-10-01T10:00:00",
  "Dependencies": [ { "GroupId": "", "Artifact": "", "Version": "", "Scope": "implementation" } ],
  "Build": {
    "BuildTool": "Gradle", "JavaVersion": "17", "OutputDir": "build/libs",
    "ParallelBuild": true, "RunClientAfterBuild": false, "ExtraGradleArgs": ""
  }
}
```

> 只读计算属性（`ProjectFilePath` / `MainClass` / `JavaPackagePath`）不参与序列化。

## 5.6 其他 JSON 文件

| 文件 | 位置 | 内容 |
|---|---|---|
| `recent.json` | `%APPDATA%\MCModCoder\recent.json` | 最近项目：`[{ "Name": "...", "Path": "..." }]`，最多 8 条（`AppContext.LoadRecent/PushRecent`）。 |
| `snippets.json` | `%APPDATA%\MCModCoder\snippets.json` | 用户代码片段：`[{ "Name", "Language", "Description", "Code" }]`（`SnippetLibrary.LoadUser/SaveUser`）。 |
| `pack.mcmeta` | 数据包 / 资源包根目录 | `{ "pack": { "pack_format": <PackFormat>, "description": "..." } }`（`RecipeJsonSerializer.ExportToDatapack` / `ResourcePackExporter.Generate` / `TemplateGenerator`）。 |

---

*文档结束 — 共覆盖 Core 7 个命名空间 55 个公共类型与 App 层 `AppContext`。*
