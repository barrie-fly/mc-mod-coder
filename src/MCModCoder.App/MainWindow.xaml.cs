using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MCModCoder.Core.Models;
using MCModCoder.Core.Services;
using MCModCoder.Modules;
using Microsoft.Win32;

namespace MCModCoder;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };

    /// <summary>导航按钮 → 完整文案（精简模式图标化后恢复用）</summary>
    private readonly Dictionary<RadioButton, string> _navButtons = new();
    /// <summary>主题 id → 色卡 / 排版 id → 示意卡（外观弹层同步用）</summary>
    private readonly Dictionary<string, RadioButton> _themeCards = new();
    private readonly Dictionary<string, RadioButton> _layoutCards = new();
    /// <summary>程序化同步卡片选中态期间抑制 Checked 事件回环</summary>
    private bool _syncingCards;

    public MainWindow()
    {
        InitializeComponent();
        WindowIcons.Apply(this); // 图标代码加载：XAML 静态 Icon 在资产异常时会炸掉启动（见 WindowIcons 注释）
        AppContext.LoadRecent();
        AppContext.StatusMessage += m => StatusText.Text = m;
        AppContext.ProjectChanged += RefreshProjectInfo;
        Closing += MainWindow_Closing;   // 有未保存代码时先确认，避免一键关闭丢失工作成果
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clock.Start();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunGlobalSearch(); };

        NavOverview.Checked += (_, _) => Navigate("overview");
        NavPixel.Checked += (_, _) => Navigate("pixel");
        NavRecipe.Checked += (_, _) => Navigate("recipe");
        NavStructure.Checked += (_, _) => Navigate("structure");
        NavMap.Checked += (_, _) => Navigate("map");
        NavDungeon.Checked += (_, _) => Navigate("dungeon");
        NavCode.Checked += (_, _) => Navigate("code");
        NavDevEnv.Checked += (_, _) => Navigate("devenv");
        NavImport.Checked += (_, _) => Navigate("import");
        NavTest.Checked += (_, _) => Navigate("test");
        NavSim.Checked += (_, _) => Navigate("sim");
        NavShader.Checked += (_, _) => Navigate("shader");
        NavRender.Checked += (_, _) => Navigate("render");

        // 记录导航按钮完整文案（精简侧栏模式只显示图标，恢复时用）
        TrackNav(NavOverview); TrackNav(NavPixel); TrackNav(NavRecipe); TrackNav(NavStructure);
        TrackNav(NavMap); TrackNav(NavDungeon); TrackNav(NavCode); TrackNav(NavDevEnv);
        TrackNav(NavImport); TrackNav(NavTest); TrackNav(NavSim); TrackNav(NavShader); TrackNav(NavRender);

        // 外观弹层卡片 → ThemeManager
        _themeCards["dark"] = ThemeDark; _themeCards["light"] = ThemeLight;
        _themeCards["midnight"] = ThemeMidnight; _themeCards["grass"] = ThemeGrass;
        _themeCards["lava"] = ThemeLava;
        foreach (var kv in _themeCards)
            kv.Value.Checked += (_, _) => { if (!_syncingCards) ThemeManager.ApplyTheme(kv.Key); };

        _layoutCards["left"] = LayoutLeft; _layoutCards["right"] = LayoutRight;
        _layoutCards["top"] = LayoutTop; _layoutCards["mini"] = LayoutMini;
        foreach (var kv in _layoutCards)
            kv.Value.Checked += (_, _) => { if (!_syncingCards) ThemeManager.ApplyLayout(kv.Key); };

        // ThemeManager → 窗口（重排布局 + 回同步卡片选中态）
        ThemeManager.ThemeChanged += _ => SyncAppearanceCards();
        ThemeManager.LayoutChanged += id => { ApplyWindowLayout(id); SyncAppearanceCards(); };

        // 应用持久化的主题与排版（首次启动回退默认，事件早退时由下方 Sync 补选中态）
        ThemeManager.Load();
        SyncAppearanceCards();

        Navigate("overview");
        if (AppContext.RecentProjects.Count > 0)
            StatusText.Text = $"就绪 — 检测到 {AppContext.RecentProjects.Count} 个最近项目，可在项目总览中快速打开。";
    }

    private void TrackNav(RadioButton rb) => _navButtons[rb] = (string)rb.Content;

    // ================= 退出保护 =================

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ContentHost.Content is CodeEditorView { HasUnsavedChanges: true })
        {
            var r = MessageBox.Show(this, "代码编辑器中有未保存的更改，退出前要保存吗？\n\n「是」保存并退出 ·「否」不保存直接退出 ·「取消」留在工作台",
                "未保存的更改", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes && !(ContentHost.Content as CodeEditorView)!.SaveFromExternal())
            {
                // 保存失败（如文件被占用）时留在应用内，让用户自行处理
                MessageBox.Show(this, "保存未成功，已取消退出。请检查文件是否被占用后重试。",
                    "退出中止", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Cancel = true;
                return;
            }
        }
    }

    // ================= 外观：主题 / 排版 =================

    private void BtnAppearance_Click(object sender, RoutedEventArgs e) => AppearancePopup.IsOpen = true;

    /// <summary>回写外观弹层卡片的选中态（程序化，不触发事件）</summary>
    private void SyncAppearanceCards()
    {
        _syncingCards = true;
        if (_themeCards.TryGetValue(ThemeManager.CurrentTheme, out var tc)) tc.IsChecked = true;
        if (_layoutCards.TryGetValue(ThemeManager.CurrentLayout, out var lc)) lc.IsChecked = true;
        _syncingCards = false;
    }

    /// <summary>
    /// 按排版 id 重排主体：left 左侧栏 / right 右侧栏 / top 顶部导航 / mini 精简侧栏。
    /// 导航面板在侧栏宿主（纵向）与顶栏宿主（横向）之间搬运，DockPanel 子元素重新入列。
    /// </summary>
    private void ApplyWindowLayout(string id)
    {
        bool top = id == "top";
        bool mini = id == "mini";

        // 1) NavPanel 宿主搬运与方向
        if (top)
        {
            if (!ReferenceEquals(TopNavHost.Content, NavPanel))
            {
                NavScrollHost.Content = null;
                TopNavHost.Content = NavPanel;
                NavPanel.Orientation = Orientation.Horizontal;
            }
        }
        else if (!ReferenceEquals(NavScrollHost.Content, NavPanel))
        {
            TopNavHost.Content = null;
            NavScrollHost.Content = NavPanel;
            NavPanel.Orientation = Orientation.Vertical;
        }

        // 2) DockPanel 子元素重排（LastChildFill：ContentHost 始终最后入列占满剩余空间）
        BodyRoot.Children.Remove(SidebarBorder);
        BodyRoot.Children.Remove(TopNavBorder);
        BodyRoot.Children.Remove(ContentHost);

        TopNavBorder.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        SidebarBorder.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        SidebarHeader.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        SidebarBorder.Width = mini ? 52 : 216;
        SidebarBorder.SetValue(DockPanel.DockProperty, id == "right" ? Dock.Right : Dock.Left);

        if (top) BodyRoot.Children.Add(TopNavBorder);
        else BodyRoot.Children.Add(SidebarBorder);
        BodyRoot.Children.Add(ContentHost);

        // 3) 分组标题 / 分组分隔符可见性（顶栏横向显示分隔条，纵向显示标题）
        foreach (var child in NavPanel.Children)
        {
            if (child is FrameworkElement fe)
            {
                switch (fe.Tag as string)
                {
                    case "sec": fe.Visibility = top || mini ? Visibility.Collapsed : Visibility.Visible; break;
                    case "sep": fe.Visibility = top ? Visibility.Visible : Visibility.Collapsed; break;
                }
            }
        }

        // 4) 精简模式：按钮只保留图标（emoji），居中 + ToolTip 提示全名
        foreach (var (rb, full) in _navButtons)
        {
            var parts = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (mini)
            {
                rb.Content = parts.Length > 0 ? parts[0] : full;
                rb.Padding = new Thickness(2, 9, 2, 9);
                rb.HorizontalContentAlignment = HorizontalAlignment.Center;
                rb.ToolTip = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : full;
            }
            else
            {
                rb.Content = full;
                rb.ClearValue(RadioButton.PaddingProperty);
                rb.ClearValue(RadioButton.HorizontalContentAlignmentProperty);
                rb.ToolTip = null;
            }
        }
    }

    private UserControl GetView(string key) => key switch
    {
        "overview"  => _views.TryGetValue(key, out var v0) ? v0 : _views[key] = new OverviewView(),
        "pixel"     => _views.TryGetValue(key, out var v1) ? v1 : _views[key] = new PixelEditorView(),
        "recipe"    => _views.TryGetValue(key, out var v2) ? v2 : _views[key] = new RecipeEditorView(),
        "structure" => _views.TryGetValue(key, out var v3) ? v3 : _views[key] = new StructureEditorView(),
        "map"       => _views.TryGetValue(key, out var v4) ? v4 : _views[key] = new MapView(),
        "dungeon"   => _views.TryGetValue(key, out var v5) ? v5 : _views[key] = new DungeonView(),
        "code"      => _views.TryGetValue(key, out var v6) ? v6 : _views[key] = new CodeEditorView(),
        "devenv"    => _views.TryGetValue(key, out var v7) ? v7 : _views[key] = new DevEnvView(),
        "import"    => _views.TryGetValue(key, out var vc) ? vc : _views[key] = new RemoteView(),
        "test"      => _views.TryGetValue(key, out var v8) ? v8 : _views[key] = new TestView(),
        "sim"       => _views.TryGetValue(key, out var v9) ? v9 : _views[key] = new SimulatorView(),
        "shader"    => _views.TryGetValue(key, out var va) ? va : _views[key] = new ShaderView(),
        "render"    => _views.TryGetValue(key, out var vb) ? vb : _views[key] = new RenderView(),
        _ => new OverviewView()
    };

    private void Navigate(string key)
    {
        // devenv（开发环境）承载项目创建向导与项目设置，无项目时也必须可访问，
        // 否则"新建项目"入口会被"请先打开项目"守卫死锁
        if (key != "overview" && key != "devenv" && AppContext.CurrentProject == null)
        {
            StatusText.Text = "请先创建或打开一个项目（右上角「新建项目」）。";
            NavOverview.IsChecked = true;
            return;
        }
        ContentHost.Content = GetView(key);
        var names = new Dictionary<string, string>
        {
            ["overview"] = "项目总览", ["pixel"] = "物品图像设计", ["recipe"] = "配方设置",
            ["structure"] = "多方块结构设计", ["map"] = "地图编辑", ["dungeon"] = "地牢编辑",
            ["code"] = "代码编写", ["devenv"] = "开发环境", ["import"] = "资源导入", ["test"] = "测试工具",
            ["sim"] = "模拟运行", ["shader"] = "光影编辑", ["render"] = "渲染编辑"
        };
        Title = $"{names.GetValueOrDefault(key, key)} — MC模组代码工作台";
    }

    private void RefreshProjectInfo()
    {
        var p = AppContext.CurrentProject;
        ProjectInfoText.Text = p == null ? "未打开项目"
            : $"{p.Name}  ·  {p.McVersion}  ·  {p.ModLoader}";
    }

    private void BtnNewProject_Click(object sender, RoutedEventArgs e)
    {
        if (ContentHost.Content is not DevEnvView)
        {
            NavDevEnv.IsChecked = true;   // Navigate("devenv") → 向导默认在"新建项目"页
        }
        else
        {
            (ContentHost.Content as DevEnvView)?.ShowWizardTab();
        }
    }

    private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "打开项目（.mcmproj.json）",
            Filter = "工作台项目 (*.mcmproj.json)|*.mcmproj.json|全部文件 (*.*)|*.*",
            InitialDirectory = AppContext.RecentProjects.Count > 0
                ? System.IO.Path.GetDirectoryName(AppContext.RecentProjects[0].Path) ?? ""
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<McProject>(
                System.IO.File.ReadAllText(dlg.FileName),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (p == null) { MessageBox.Show(this, "无法解析项目文件。"); return; }
            if (string.IsNullOrWhiteSpace(p.ProjectDir))
            {
                MessageBox.Show(this, "项目文件缺少项目目录信息（ProjectDir 为空），无法打开。\n请使用「新建项目」重新创建。",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            AppContext.OpenProject(p);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开项目失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCloseProject_Click(object sender, RoutedEventArgs e) => AppContext.CloseProject();

    // ================= 全局快速搜索（Ctrl+K） =================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(GlobalSearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void GlobalSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchHit();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            MoveSearchSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveSearchSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void MoveSearchSelection(int delta)
    {
        int i = SearchResultsList.SelectedIndex + delta;
        if (SearchResultsList.Items.Count == 0) return;
        if (i < 0) i = SearchResultsList.Items.Count - 1;
        if (i >= SearchResultsList.Items.Count) i = 0;
        SearchResultsList.SelectedIndex = i;
        SearchResultsList.ScrollIntoView(SearchResultsList.SelectedItem);
    }

    private void SearchResults_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenSelectedSearchHit();

    /// <summary>执行全局搜索：模块索引 + 项目资产 + 代码行 + AI 推荐（异步去抖后调用）</summary>
    private void RunGlobalSearch()
    {
        var kw = GlobalSearchBox.Text?.Trim() ?? "";
        if (kw.Length == 0)
        {
            SearchPopup.IsOpen = false;
            SearchResultsList.ItemsSource = null;
            return;
        }

        var hits = WorkspaceSearch.Search(AppContext.CurrentProject, kw, 28);

        // AI 智能匹配：无结果或结果较少时追加推荐词条（点击即按推荐词搜索）
        if (hits.Count < 3)
            foreach (var s in SmartMatch.Recommend(kw, 3))
                hits.Add(new WorkspaceSearchHit { Kind = "AI 推荐", Title = s.Text, Detail = s.Reason });

        SearchResultsList.ItemsSource = hits;
        if (hits.Count > 0) SearchResultsList.SelectedIndex = 0;
        SearchPopup.IsOpen = true;
    }

    private void OpenSelectedSearchHit()
    {
        if (SearchResultsList.SelectedItem is not WorkspaceSearchHit hit) return;
        SearchPopup.IsOpen = false;

        // AI 推荐项：把推荐词填入搜索框并重新检索
        if (hit.Kind == "AI 推荐")
        {
            GlobalSearchBox.Text = hit.Title;
            GlobalSearchBox.CaretIndex = GlobalSearchBox.Text.Length;
            return;
        }

        if (!string.IsNullOrEmpty(hit.Module))
        {
            NavigateTo(hit.Module);
            // 代码结果：导航完成后在编辑器中打开并跳转到行
            if (hit.Module == "code" && hit.FilePath != null && ContentHost.Content is CodeEditorView)
                AppContext.RequestOpenCodeFile(hit.FilePath, hit.Line);
        }

        StatusText.Text = hit.Line > 0
            ? $"已定位：{hit.Title}（{hit.Kind} · 第 {hit.Line} 行）"
            : $"已定位：{hit.Title}（{hit.Kind}）";
    }

    /// <summary>供搜索结果等外部入口导航到指定模块（设置导航单选按钮触发 Navigate）</summary>
    public void NavigateTo(string key)
    {
        var rb = key switch
        {
            "overview" => NavOverview, "pixel" => NavPixel, "recipe" => NavRecipe,
            "structure" => NavStructure, "map" => NavMap, "dungeon" => NavDungeon,
            "code" => NavCode, "devenv" => NavDevEnv, "import" => NavImport,
            "test" => NavTest, "sim" => NavSim, "shader" => NavShader, "render" => NavRender,
            _ => null
        };
        if (rb != null) rb.IsChecked = true;
        else Navigate(key);
    }
}
