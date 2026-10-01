using System.IO;
using MCModCoder.Core.Models;

namespace MCModCoder.Core.Services;

/// <summary>全局快速搜索结果条目（模块 / 工作区资产 / 代码行）</summary>
public class WorkspaceSearchHit
{
    /// <summary>可导航的模块 key（空表示无跳转目标）</summary>
    public string Module { get; set; } = "";
    /// <summary>分类徽标：模块 / 纹理 / 配方 / 结构 / 地图 / 地牢 / 光影 / 特效 / 模组 / 整合包 / 代码</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>路径或匹配行摘要</summary>
    public string Detail { get; set; } = "";
    /// <summary>关联文件（代码类结果支持在代码编辑器中打开并跳转到行）</summary>
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public int Score { get; set; }
}

/// <summary>
/// 全局快速搜索引擎：关键词即时检索全部功能模块、项目工作区资产与源码，
/// 按匹配度评分排序，支持定位到具体模块或代码行。
/// </summary>
public static class WorkspaceSearch
{
    private static readonly (string Key, string Name, string[] Aliases)[] Modules =
    {
        ("overview",  "项目总览",       new[] { "首页", "总览", "最近项目", "home" }),
        ("pixel",     "物品图像设计",   new[] { "像素", "画布", "纹理", "贴图", "图层", "texture", "paint", "item" }),
        ("recipe",    "配方设置",       new[] { "合成", "熔炉", "高炉", "烟熏", "锻造", "配方", "recipe", "crafting" }),
        ("structure", "多方块结构设计", new[] { "结构", "体素", "放置指令", "structure", "3d" }),
        ("map",       "地图编辑",       new[] { "地形", "群系", "矿物", "高度图", "map", "terrain", "worldgen" }),
        ("dungeon",   "地牢编辑",       new[] { "地牢", "房间", "走廊", "宝物房", "dungeon" }),
        ("code",      "代码编写",       new[] { "代码", "编辑器", "补全", "片段", "java", "glsl", "json", "git" }),
        ("devenv",    "开发环境",       new[] { "新建项目", "构建", "打包", "部署", "设置", "依赖", "gradle", "向导" }),
        ("import",    "资源导入",       new[] { "下载", "搜索模组", "整合包", "模组", "modrinth", "curseforge", "remote", "mod" }),
        ("test",      "测试工具",       new[] { "测试", "日志", "分析", "性能", "监控", "热重载", "test", "log" }),
        ("sim",       "模拟运行",       new[] { "模拟", "红石", "电路", "实体", "信号", "simulate", "redstone" }),
        ("shader",    "光影编辑",       new[] { "光影", "阴影", "雾", "太阳", "光照", "shader" }),
        ("render",    "渲染编辑",       new[] { "渲染", "粒子", "材质", "动画", "特效", "render", "effect", "particle" }),
    };

    /// <summary>工作区子目录约定（与 AppContext 的目录约定保持一致）</summary>
    public static string WsDir(string projectDir, string sub) => Path.Combine(projectDir, "workspace", sub);

    /// <summary>执行全局搜索（关键词即时检索，返回按匹配度排序的 ≤ max 条结果）</summary>
    public static List<WorkspaceSearchHit> Search(McProject? project, string keyword, int max = 30)
    {
        var kw = (keyword ?? "").Trim();
        var hits = new List<WorkspaceSearchHit>();
        if (kw.Length == 0) return hits;

        SearchModules(kw, hits);
        if (project != null && !string.IsNullOrEmpty(project.ProjectDir))
        {
            SearchAssets(project.ProjectDir, kw, hits);
            SearchCode(Path.Combine(project.ProjectDir, "src"), kw, hits);
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
                   .Take(max).ToList();
    }

    // ---------- 模块索引 ----------
    private static void SearchModules(string kw, List<WorkspaceSearchHit> hits)
    {
        foreach (var (key, name, aliases) in Modules)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new WorkspaceSearchHit
                {
                    Module = key, Kind = "模块", Title = name,
                    Detail = "打开功能模块", Score = name.StartsWith(kw, StringComparison.OrdinalIgnoreCase) ? 120 : 100
                });
                continue;
            }
            var alias = aliases.FirstOrDefault(a => a.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (alias != null)
                hits.Add(new WorkspaceSearchHit
                {
                    Module = key, Kind = "模块", Title = name,
                    Detail = $"匹配关键词「{alias}」", Score = 85
                });
        }
    }

    // ---------- 工作区资产 ----------
    private static void SearchAssets(string projectDir, string kw, List<WorkspaceSearchHit> hits)
    {
        // (子目录, 分类徽标, 模块, 是否扫描文件内容, 扩展名过滤)
        var catalog = new (string Sub, string Kind, string Module, bool ScanContent, string Ext)[]
        {
            ("textures",   "纹理",   "pixel",     false, ".png"),
            ("recipes",    "配方",   "recipe",    true,  ".json"),
            ("structures", "结构",   "structure", true,  ".json"),
            ("maps",       "地图",   "map",       true,  ".json"),
            ("dungeons",   "地牢",   "dungeon",   true,  ".json"),
            ("shaders",    "光影",   "shader",    true,  ".json"),
            ("effects",    "特效",   "render",    true,  ".json"),
        };

        foreach (var (sub, kind, module, scanContent, ext) in catalog)
        {
            var dir = WsDir(projectDir, sub);
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + ext); } catch { continue; }

            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new WorkspaceSearchHit
                    {
                        Module = module, Kind = kind, Title = name, FilePath = file,
                        Detail = file, Score = name.StartsWith(kw, StringComparison.OrdinalIgnoreCase) ? 95 : 75
                    });
                }
                if (scanContent)
                    ScanTextFile(file, kw, kind, module, hits, perFileLimit: 2);
            }
        }

        // 已下载的远程资源文件名
        var remote = WsDir(projectDir, Path.Combine("remote", "mods"));
        if (Directory.Exists(remote))
        {
            try
            {
                foreach (var file in Directory.GetFiles(remote))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new WorkspaceSearchHit
                        {
                            Module = "import", Kind = "模组", Title = name, FilePath = file,
                            Detail = file, Score = 70
                        });
                }
            }
            catch { }
        }
    }

    // ---------- 源码行级检索 ----------
    private static void SearchCode(string codeDir, string kw, List<WorkspaceSearchHit> hits)
    {
        if (!Directory.Exists(codeDir)) return;

        string[] files;
        try { files = Directory.GetFiles(codeDir, "*.*", SearchOption.AllDirectories)
                               .Where(f => f.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
                                        || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                        || f.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase))
                               .ToArray(); }
        catch { return; }

        foreach (var file in files)
            ScanTextFile(file, kw, "代码", "code", hits, perFileLimit: 3);
    }

    /// <summary>扫描单个文本文件的内容匹配行（限制每个文件的命中数与文件大小）</summary>
    private static void ScanTextFile(string file, string kw, string kind, string module,
        List<WorkspaceSearchHit> hits, int perFileLimit)
    {
        try
        {
            var fi = new FileInfo(file);
            if (fi.Length > 512 * 1024) return; // 跳过超大文件

            string[] lines;
            lock (IoLock)
            {
                // 可能与保存操作并发，读取失败按跳过处理
                lines = File.ReadAllLines(file);
            }

            string name = Path.GetFileNameWithoutExtension(file);
            int found = 0;
            for (int i = 0; i < lines.Length && found < perFileLimit; i++)
            {
                var line = lines[i];
                if (!line.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;

                var snippet = line.Trim();
                if (snippet.Length > 110) snippet = snippet[..110] + "…";
                hits.Add(new WorkspaceSearchHit
                {
                    Module = module, Kind = kind, Title = name, FilePath = file, Line = i + 1,
                    Detail = $"{Relative(file)}:{i + 1}  {snippet}",
                    Score = 55 - found * 2
                });
                found++;
            }
        }
        catch { /* 文件占用或读取失败，忽略 */ }
    }

    private static readonly object IoLock = new();

    private static string Relative(string fullPath)
    {
        try
        {
            int idx = fullPath.IndexOf("workspace", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return fullPath[idx..].Replace('\\', '/');
            idx = fullPath.IndexOf("src", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return fullPath[idx..].Replace('\\', '/');
        }
        catch { }
        return Path.GetFileName(fullPath);
    }
}
