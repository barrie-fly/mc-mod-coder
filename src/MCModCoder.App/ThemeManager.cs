using System.IO;
using System.Windows;

namespace MCModCoder;

/// <summary>
/// 外观管理：主题色板与界面排版的热切换 + 设置持久化。
/// 色板文件（Themes/Brushes.*.xaml）位于 Application.Resources 合并字典 [0]，
/// 切换主题 = 整体替换该字典；控件样式层（Controls.xaml）通过 DynamicResource 引用画刷，
/// 替换后全局颜色即时生效，无需重启或重建窗口。
/// </summary>
public static class ThemeManager
{
    /// <summary>可选项（主题 / 排版）的描述元组。</summary>
    public record Option(string Id, string Name);

    /// <summary>全部可用主题（Id 对应 Themes/Brushes.{Id}.xaml）。</summary>
    public static readonly Option[] Themes =
    {
        new("dark",     "深空黑"),
        new("light",    "极昼白"),
        new("midnight", "午夜蓝"),
        new("grass",    "草原绿"),
        new("lava",     "熔岩橙"),
    };

    /// <summary>全部可用排版（由 MainWindow 响应 LayoutChanged 实现重排）。</summary>
    public static readonly Option[] Layouts =
    {
        new("left",  "左侧栏"),
        new("right", "右侧栏"),
        new("top",   "顶部导航"),
        new("mini",  "精简侧栏"),
    };

    public static string CurrentTheme { get; private set; } = "dark";
    public static string CurrentLayout { get; private set; } = "left";

    /// <summary>主题切换事件（参数：主题 id）。</summary>
    public static event Action<string>? ThemeChanged;

    /// <summary>排版切换事件（参数：排版 id）。</summary>
    public static event Action<string>? LayoutChanged;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCModCoder", "settings.json");

    /// <summary>启动时调用：读取持久化设置并应用（触发对应事件）。</summary>
    public static void Load()
    {
        var theme = "dark";
        var layout = "left";
        try
        {
            if (File.Exists(SettingsFile))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile));
                if (s != null)
                {
                    if (!string.IsNullOrWhiteSpace(s.Theme)) theme = s.Theme;
                    if (!string.IsNullOrWhiteSpace(s.Layout)) layout = s.Layout;
                }
            }
        }
        catch { /* 设置文件损坏时回退默认外观 */ }

        ApplyTheme(Themes.Any(t => t.Id == theme) ? theme : "dark");
        ApplyLayout(Layouts.Any(l => l.Id == layout) ? layout : "left");
    }

    /// <summary>切换主题：替换合并字典 [0] 的色板并广播事件。</summary>
    public static void ApplyTheme(string id)
    {
        if (id == CurrentTheme || !Themes.Any(t => t.Id == id)) return;
        try
        {
            // 文件名按色板文件实际大小写（id 首字母大写："dark" → Brushes.Dark.xaml）
            var file = char.ToUpperInvariant(id[0]) + id[1..];
            var dict = new ResourceDictionary { Source = new Uri($"Themes/Brushes.{file}.xaml", UriKind.Relative) };
            Application.Current.Resources.MergedDictionaries[0] = dict;
            CurrentTheme = id;
            ThemeChanged?.Invoke(id);
            Save();
        }
        catch
        {
            // 色板加载失败时保持当前主题，不影响运行
        }
    }

    /// <summary>切换排版：广播事件，由 MainWindow 完成实际重排。</summary>
    public static void ApplyLayout(string id)
    {
        if (id == CurrentLayout || !Layouts.Any(l => l.Id == id)) return;
        CurrentLayout = id;
        LayoutChanged?.Invoke(id);
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(
                new Settings { Theme = CurrentTheme, Layout = CurrentLayout },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 持久化失败不影响运行，下次启动回退默认 */ }
    }

    private class Settings
    {
        public string? Theme { get; set; }
        public string? Layout { get; set; }
    }
}
