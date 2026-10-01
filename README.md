# MC模组代码工作台 (MCModCoder)

> 一站式 Minecraft 模组与整合包集成开发环境（IDE）· 纯 C# / WPF 桌面应用

**MC模组代码工作台**覆盖模组开发从设计、编码到测试的完整闭环：

> 像素级纹理设计 → 可视化配方编辑 → 3D 多方块结构 → 代码编写 → 一键构建 → 游戏内测试

支持 **Forge / Fabric / NeoForge**，覆盖 Minecraft **1.12.2 ~ 1.20.6** 共 7 个版本。内置地图编辑、地牢生成、红石/实体模拟、光影与粒子特效编辑等可视化工具，让不熟悉 Gradle 与 JSON 的创作者也能产出规范的资源包、数据包与整合包。

## ✨ 功能特性

应用共提供 **14 个功能模块**（另有「项目总览」首页），按五组组织：

| 分组 | 模块 | 亮点 |
|------|------|------|
| 设计 | 物品图像设计 | 像素画布、图层、对称绘制、分辨率互转、三种实时预览、一键生成资源包 |
| | AI 图像生成 | 外接 AI 图像模型（OpenAI 兼容 / SD WebUI）文字出图、像素风格约束、六种风格预设、生成历史、输出审计门禁（拦截损坏/空白图）、一键导入纹理库 |
| | 配方设置 | 拖拽式 3×3 槽位编辑、9 种配方类型、实时校验与修正建议、与原版数据包 JSON 双向兼容 |
| | 多方块结构设计 | 3D 体素视口、全景/俯视/侧视/正视平滑过渡动画 + 自由视角（旋转/平移/缩放）、旋转/镜像/复制、生成 `/place template` 指令 |
| | 地图编辑 | 种子地形生成、地形笔刷、矿物分布配置、高度图导出 |
| | 地牢编辑 | 随机/手动两种生成模式、连通性压力测试（并查集）、一键推送至 3D 结构设计器 |
| 编码 | 代码编写 | Java/JSON/GLSL 语法高亮、本地 AI 补全（不联网）与语句级预测、AI 开发助手（实时建议 + 内容点评）、片段库、Git 集成、实时静态检查 |
| | 开发环境 | 新建项目向导（生成完整 Gradle 脚手架）、依赖管理、一键构建、打包整合包 |
| 资源 | 资源导入 | Modrinth / CurseForge 在线搜索下载、依赖递归解析、`.mrpack`/`.zip` 整合包导入、SHA1 校验 |
| 调试与模拟 | 测试工具 | 内置测试套件、构建产物部署与热重载、游戏日志智能分析、进程性能监控 |
| | 模拟运行 | 红石电路模拟（15 级信号衰减）、实体行为与游戏规则模拟 |
| 视觉 | 光影编辑 | 实时 3D 预览、快照 A/B 对比、导出 GLSL uniforms 配置 |
| | 渲染编辑 | 粒子发射器、材质球（PBR 参数）、生命周期动画曲线、性能建议 |

支持的 Minecraft 版本矩阵（加载器随版本级联）：

| MC 版本 | 加载器 | Java |
|---------|--------|------|
| 1.12.2 | Forge、Rift | 8 |
| 1.16.5 ~ 1.19.4 | Forge、Fabric | 8 / 17 |
| 1.20.1 / 1.20.4 | Forge、Fabric、NeoForge | 17 |
| 1.20.6 | NeoForge、Fabric | 21 |

## 🏗️ 项目结构

```
mc-mod-coder/
├── MCModCoder.sln                 # 解决方案（.NET 8）
├── src/
│   ├── MCModCoder.Core/           # 领域类库：模型、序列化、校验、服务、模拟器
│   │                              # （net8.0，零 NuGet 依赖、零 UI 依赖，可独立复用）
│   └── MCModCoder.App/            # WPF 宿主（net8.0-windows）：主窗口、14 个模块视图、AppContext
├── docs/
│   ├── 用户手册.md                # 全部模块的详细操作指南与 FAQ
│   ├── 教程.md                    # 新手六篇教程（画纹理→配配方→搭结构→写代码→构建→测试发布）
│   └── API文档.md                 # Core 类库 API 参考与扩展指南
├── pack.ps1                       # 一键打包发布脚本（dotnet publish + 冒烟测试 + zip）
├── mcmodcoder-brand.miora         # 品牌资源源文件
├── mcmodcoder-brand_assets/       # 品牌 PNG 资源
└── publish/                       # 打包产物输出目录（打包脚本生成）
```

架构分层：`MCModCoder.App`（呈现与交互）仅通过 `ProjectReference` 依赖 `MCModCoder.Core`（是什么/怎么算/怎么读写），Core 可被控制台、单元测试等任何 .NET 宿主独立复用。

## 📦 安装步骤

### 环境要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 及以上（64 位） |
| .NET SDK | **.NET 8 SDK**（构建源码必需） |
| 内存 / 磁盘 | 8 GB 内存；500 MB 磁盘空间 |
| 可选 | JDK 8/17/21（真实 Gradle 构建时，随 MC 版本而定）、Git（代码编写模块的版本控制集成） |

### 方式一：从源码构建运行

```powershell
# 1. 克隆仓库
git clone <仓库地址> mc-mod-coder
cd mc-mod-coder

# 2. 构建解决方案（若 dotnet 已在 PATH 中）
dotnet build MCModCoder.sln

# 若 .NET SDK 安装在非默认路径（如 D:\dotnet），使用完整路径：
D:\dotnet\dotnet.exe build MCModCoder.sln

# 3. 启动应用
# .NET 运行时在自定义目录时，需先设置 DOTNET_ROOT，否则启动报找不到 hostfxr.dll：
$env:DOTNET_ROOT = "D:\dotnet"
& "src\MCModCoder.App\bin\Debug\net8.0-windows\MCModCoder.exe"
```

### 方式二：一键打包发布（产出可分发的 zip）

使用仓库根目录的 `pack.ps1`，自动完成环境检查 → `dotnet publish`（自包含版 + 框架依赖版双包）→ 启动冒烟测试 → zip 封包，全程写日志到 `publish\logs\`：

```powershell
# 默认：v1.0.0 双包 + 冒烟测试 + zip
.\pack.ps1

# 指定版本号、无人值守模式（CI 适用）
.\pack.ps1 -Version 1.1.0 -Force

# 仅发布自包含版、跳过 zip
.\pack.ps1 -Packages SelfContained -SkipZip

# 若执行策略禁止运行脚本：
powershell -ExecutionPolicy Bypass -File .\pack.ps1
```

产物位置：

- `publish\self-contained\` — 自包含版，目标机**双击即用**，无需安装运行时；
- `publish\framework-dependent\` — 框架依赖版，目标机需安装 .NET 8 Desktop Runtime；
- 对应 zip 包：`publish\MCModCoder-<版本>-win-x64[-selfcontained].zip`。

> 💡 提示：自包含版可直接分发给普通用户；开发调试时用源码构建更快。

## 🚀 使用示例

以下是一个最小工作流示例：**新建模组项目 → 画纹理 → 配配方 → 写代码 → 构建 → 部署到游戏**。完整教程见 [docs/教程.md](docs/教程.md)。

### 1. 新建模组项目

1. 启动应用，点击右上角 **「新建项目」**（自动跳转「开发环境 → 新建项目向导」）；
2. 填写表单：项目名称 `RubyMod`（Mod ID 自动同步为 `rubymod`）、MC 版本 `1.20.1`、加载器 `Forge`；
3. 点击 **「创建项目并生成脚手架」**——自动生成 `build.gradle`、`mods.toml`、主类、`pack.mcmeta` 等完整可构建工程，并创建项目文件 `RubyMod.mcmproj.json`：

```
RubyMod/
├── RubyMod.mcmproj.json      # 项目元数据
├── workspace/                # 各模块产物自动落位：textures/ recipes/ structures/ ...
└── src/main/java/...         # 生成的 Java 源码
```

### 2. 画一个物品纹理并生成资源包

进入 **「物品图像设计」**，用铅笔在 16×16 画布上绘制（勾选「左右对称绘制」更省事）→ 点击 **「保存到项目纹理库」** 输入名称 `ruby` → 点击 **「生成资源包结构…」**，得到可直接放入 `.minecraft/resourcepacks` 的资源包：

```
resourcepack/
├─ pack.mcmeta
└─ assets/rubymod/
   ├─ textures/item/ruby.png
   └─ models/item/ruby.json   # 自动生成物品模型
```

### 3. 可视化配置一个合成配方

进入 **「配方设置」** → 新建配方 → 从物品面板把 `iron_ingot` 拖进 3×3 网格、`stick` 拖进中列（镐子形状，key 字母自动推导）→ 产物填 `rubymod:ruby_pickaxe` → 点击 **「验证当前配方」** 排查问题 → **「批量导出数据包…」** 生成标准数据包（与原版 JSON 完全同构，可直接放入存档 `datapacks/`）。

### 4. 写代码（片段 + 本地 AI 补全）

进入 **「代码编写」**，打开生成的主类，双击片段库中的 **「Forge 注册物品」** 即插入模板代码（`DeferredRegister` + `RegistryObject`），占位符 `{{modid}}`、`{{MainClass}}` 已按项目自动替换。输入停顿时弹出本地 AI 补全建议（不联网），`Enter` 确认、`Esc` 关闭。

### 5. 一键构建与部署

- 「开发环境 → 构建与打包」点击 **「🔨 一键构建」**：项目含 `gradlew.bat` 时执行真实 Gradle 构建，否则降级为内置模拟构建。产物位于 `<项目目录>\build\libs\rubymod-1.0.0.jar`；
- 「测试工具」模块设置游戏 mods 目录（默认 `%APPDATA%\.minecraft\mods`），点击 **「部署模组（复制到 mods）」**；勾选 **「热重载」** 后，每次构建产物变化都会自动重新部署——推荐循环：写代码 → 构建 → 热重载 → 重启游戏验证；
- 发布前点击 **「📦 打包整合包」**，在 `modpack/` 生成资源包 + 数据包 + `modlist.txt` 清单，压缩后即可分发。

### 6. 更多示例

- **地牢流水线**：「地牢编辑」随机生成 → 压力测试连通性 → 「推送到 3D 结构设计器」精修 → 「生成放置指令」得到 `/place template rubymod:xxx ~ ~ ~`；
- **光影导出**：「光影编辑」调太阳角度/雾效 → 「导出 GLSL 配置…」得到可直接粘贴进光影包的 uniforms；
- **在线导入**：「资源导入」搜索 Modrinth/CurseForge 模组 → 「⬇ 下载并解析依赖」自动递归拉齐全部依赖 → 或导入 `.mrpack` 整合包生成实例目录。

## 🤝 贡献指南

欢迎提交 Issue 与 Pull Request！以下是参与开发的快速指引。

### 搭建开发环境

1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）；
2. 克隆仓库并用 Visual Studio 2022 / Rider / VS Code 打开 `MCModCoder.sln`，或直接命令行 `dotnet build MCModCoder.sln`；
3. 启动应用确认可运行：`src\MCModCoder.App\bin\Debug\net8.0-windows\MCModCoder.exe`。

### 代码结构约定

| 目录 | 职责 | 约束 |
|------|------|------|
| `src/MCModCoder.Core` | 领域模型、序列化、校验、服务、模拟器 | **保持零 NuGet 依赖、零 UI 依赖**；新增业务能力一律放 Core |
| `src/MCModCoder.App/Modules` | 模块视图（XAML + code-behind） | 仅做呈现与交互，通过 `AppContext` 访问 Core |
| `src/MCModCoder.App/AppContext.cs` | 全局上下文 | 跨模块数据流（如 纹理→渲染编辑）通过事件接入，参考 [API文档.md](docs/API文档.md) §4.3 |

### 提交变更前请检查

- [ ] `dotnet build MCModCoder.sln` 无警告无错误；
- [ ] 涉及 Core 的改动：启动应用，在 **「测试工具」→「▶ 运行内置测试套件」** 跑一遍回归（覆盖配方序列化、校验器、结构变换、红石模拟、日志解析等）；
- [ ] UI 改动已在实际界面验证，按钮文案与 [docs/用户手册.md](docs/用户手册.md) 保持一致（如有出入请同步更新文档）；
- [ ] 新增 JSON 数据格式时，在 [docs/API文档.md](docs/API文档.md) §5 补充格式说明。

### Pull Request 流程

1. 从 `main` 拉出功能分支，命名建议 `feat/xxx`、`fix/xxx`、`docs/xxx`；
2. 保持提交原子性，提交信息使用祈使句（如 `fix: 修复配方导入缺少命名空间时的崩溃`）；
3. PR 描述请说明：改动动机、修改要点、验证方式（附截图更佳）；
4. 等待 Review 通过后合并。

### 反馈问题

提交 Issue 时请附上：应用版本、复现步骤、期望/实际行为；涉及崩溃时请附相关日志（可用应用内「测试工具 → 导入日志」先行排查）。

## 📚 更多文档

| 文档 | 内容 |
|------|------|
| [docs/教程.md](docs/教程.md) | 新手六篇教程，完整走通 开发→构建→发布 闭环 |
| [docs/用户手册.md](docs/用户手册.md) | 14 个模块的详细操作指南、FAQ、快捷键速查 |
| [docs/API文档.md](docs/API文档.md) | Core 类库 API 参考、扩展指南、JSON 数据格式说明 |

## 📄 许可证

本项目许可证待定（TBD）。在许可证确定之前，未经授权请勿分发本仓库代码。
