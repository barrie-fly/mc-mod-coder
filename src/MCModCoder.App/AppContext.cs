using MCModCoder.Core.Models;
using System.IO;

namespace MCModCoder;

/// <summary>
/// 全局应用上下文：项目状态、跨模块数据流转中心。
/// 所有模块共享同一项目与数据目录约定，实现"设计→编码→测试"闭环。
/// </summary>
public static class AppContext
{
    public static McProject? CurrentProject { get; private set; }

    /// <summary>状态栏消息事件（所有模块共用）</summary>
    public static event Action<string>? StatusMessage;
    public static void SetStatus(string msg) => StatusMessage?.Invoke(msg);

    /// <summary>项目打开事件（模块可刷新自身数据）</summary>
    public static event Action? ProjectChanged;

    public static void OpenProject(McProject p)
    {
        // 旧格式/手工编辑的项目文件可能出现 "Build": null / "Dependencies": null，
        // 反序列化会覆盖属性默认值，后续 p.Build.JavaVersion 等访问将空引用——此处统一归一化
        p.Build ??= new BuildOptions();
        p.Dependencies ??= new List<DependencyEntry>();

        // 关键字段同样可能为 null（手工编辑/旧版本文件），归一化避免各模块访问时 NRE
        p.Name = string.IsNullOrWhiteSpace(p.Name) ? "未命名项目" : p.Name.Trim();
        p.ModId = string.IsNullOrWhiteSpace(p.ModId) ? "mymod" : p.ModId.Trim();
        p.McVersion = string.IsNullOrWhiteSpace(p.McVersion) ? "1.20.1" : p.McVersion.Trim();
        p.ModLoader = string.IsNullOrWhiteSpace(p.ModLoader) ? "Forge" : p.ModLoader.Trim();
        p.Package = string.IsNullOrWhiteSpace(p.Package) ? "com.example." + p.ModId : p.Package.Trim();
        if (string.IsNullOrWhiteSpace(p.ProjectDir))
            throw new InvalidDataException(
                "项目文件缺少项目目录信息（ProjectDir 为空），无法打开。\n请使用「新建项目」重新创建。");

        CurrentProject = p;
        EnsureProjectDirs(p);
        PushRecent(p.ProjectFilePath, p.Name);
        ProjectChanged?.Invoke();
        SetStatus($"已打开项目：{p.Name}（{p.McVersion} / {p.ModLoader}）");
    }

    public static void CloseProject()
    {
        CurrentProject = null;
        ProjectChanged?.Invoke();
        SetStatus("已关闭项目。");
    }

    // ---------- 目录约定（项目内工作区） ----------
    public static string DirTextures(McProject p)   => Path.Combine(p.ProjectDir, "workspace", "textures");
    public static string DirRecipes(McProject p)    => Path.Combine(p.ProjectDir, "workspace", "recipes");
    public static string DirStructures(McProject p) => Path.Combine(p.ProjectDir, "workspace", "structures");
    public static string DirMaps(McProject p)       => Path.Combine(p.ProjectDir, "workspace", "maps");
    public static string DirDungeons(McProject p)   => Path.Combine(p.ProjectDir, "workspace", "dungeons");
    public static string DirShaders(McProject p)    => Path.Combine(p.ProjectDir, "workspace", "shaders");
    public static string DirEffects(McProject p)    => Path.Combine(p.ProjectDir, "workspace", "effects");
    public static string DirRemote(McProject p)     => Path.Combine(p.ProjectDir, "workspace", "remote");
    public static string DirAiImages(McProject p)   => Path.Combine(p.ProjectDir, "workspace", "ai-images");
    public static string DirCode(McProject p)       => Path.Combine(p.ProjectDir, "src", "main", "java");

    private static void EnsureProjectDirs(McProject p)
    {
        foreach (var d in new[] { DirTextures(p), DirRecipes(p), DirStructures(p), DirMaps(p), DirDungeons(p), DirShaders(p), DirEffects(p), DirRemote(p), DirAiImages(p) })
            Directory.CreateDirectory(d);
    }

    // ---------- 最近项目 ----------
    public static string RecentFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCModCoder", "recent.json");

    public static List<(string Name, string Path)> RecentProjects { get; } = new();

    public static void LoadRecent()
    {
        try
        {
            if (File.Exists(RecentFile))
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(RecentFile));
                if (arr != null)
                    foreach (var r in arr)
                        RecentProjects.Add((r.Name, r.Path));
            }
        }
        catch { }
    }

    public static void PushRecent(string path, string name)
    {
        RecentProjects.RemoveAll(r => r.Path == path);
        RecentProjects.Insert(0, (name, path));
        while (RecentProjects.Count > 8) RecentProjects.RemoveAt(RecentProjects.Count - 1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)!);
            File.WriteAllText(RecentFile, System.Text.Json.JsonSerializer.Serialize(
                RecentProjects.Select(r => new RecentEntry { Name = r.Name, Path = r.Path }).ToList(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private class RecentEntry { public string Name { get; set; } = ""; public string Path { get; set; } = ""; }

    // ---------- 跨模块数据流转 ----------
    /// <summary>结构设计器接收外部结构（如从地牢编辑器推送）</summary>
    public static event Action<StructureData>? StructurePushed;
    public static void PushStructure(StructureData s) => StructurePushed?.Invoke(s);

    /// <summary>像素编辑器导出的纹理 → 渲染编辑器/3D预览共享</summary>
    public static event Action<string>? TexturePushed;   // 参数：纹理显示名
    public static void PushTexture(string textureName) => TexturePushed?.Invoke(textureName);

    /// <summary>
    /// 请求在「代码编写」编辑器中打开指定文件并跳转到行（全局快速搜索的代码定位入口）。
    /// 参数：文件路径、行号（0 表示不跳转）。
    /// </summary>
    public static event Action<string, int>? OpenCodeFileRequested;
    public static void RequestOpenCodeFile(string path, int line = 0) => OpenCodeFileRequested?.Invoke(path, line);
}
