namespace MCModCoder.Core.Models;

/// <summary>模组项目模型</summary>
public class McProject
{
    public string Name { get; set; } = "MyMod";
    public string ModId { get; set; } = "mymod";
    public string McVersion { get; set; } = "1.20.1";
    public string ModLoader { get; set; } = "Forge";
    public string Package { get; set; } = "com.example.mymod";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public List<DependencyEntry> Dependencies { get; set; } = new();
    public BuildOptions Build { get; set; } = new();

    /// <summary>项目文件路径</summary>
    public string ProjectFilePath => Path.Combine(ProjectDir, Name + ".mcmproj.json");

    public string MainClass => MakeClassName(Name);

    public static string MakeClassName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    public string JavaPackagePath => Package.Replace('.', '/');
}

/// <summary>依赖条目</summary>
public class DependencyEntry
{
    public string GroupId { get; set; } = "";
    public string Artifact { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>implementation / compileOnly / runtimeOnly</summary>
    public string Scope { get; set; } = "implementation";

    public string Coordinate => $"{GroupId}:{Artifact}:{Version}";
}

/// <summary>构建选项</summary>
public class BuildOptions
{
    public string BuildTool { get; set; } = "Gradle";
    public string JavaVersion { get; set; } = "17";
    public string OutputDir { get; set; } = "build/libs";
    public bool ParallelBuild { get; set; } = true;
    public bool RunClientAfterBuild { get; set; } = false;
    public string ExtraGradleArgs { get; set; } = "";
}
