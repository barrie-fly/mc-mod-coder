using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MCModCoder.Core.Models;
using MCModCoder.Core.Remote;
using MCModCoder.Core.Services;
using Microsoft.Win32;

namespace MCModCoder.Modules;

// ============ 绑定模型 ============

/// <summary>搜索结果条目（含图标与格式化字段）</summary>
public class SearchHitVm
{
    /// <summary>图标后台加载完成后通知（UI 线程刷新列表用）</summary>
    public static event Action? IconLoaded;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> IconCache = new();
    private static readonly System.Net.Http.HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public RemoteSearchHit Hit { get; }
    private ImageSource? _icon;
    private int _loading;   // 0=未开始 1=进行中

    public SearchHitVm(RemoteSearchHit hit)
    {
        Hit = hit;
        var url = hit.IconUrl;
        if (string.IsNullOrEmpty(url)) return;
        if (IconCache.TryGetValue(url, out var cached)) { _icon = cached; return; }
        BeginLoadIcon(url);
    }

    /// <summary>后台下载并解码图标（冻结后跨线程使用），不阻塞搜索结果展示</summary>
    private async void BeginLoadIcon(string url)
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var bytes = await IconHttp.GetByteArrayAsync(url);
            var bmp = await Task.Run(() =>
            {
                try
                {
                    var image = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 40;
                    image.StreamSource = ms;
                    image.EndInit();
                    image.Freeze();   // 冻结后可在 UI 线程安全使用
                    return (ImageSource?)image;
                }
                catch { return null; }
            });
            IconCache[url] = bmp;   // 失败也缓存 null，避免反复重试
            _icon = bmp;
            if (bmp != null) IconLoaded?.Invoke();
        }
        catch
        {
            IconCache[url] = null;
        }
    }

    public ImageSource? IconSource => _icon;
    public string Name => Hit.Name;
    public string AuthorDisplay => string.IsNullOrEmpty(Hit.Author) ? "未知作者" : Hit.Author;
    public string PlatformBadge => Hit.Platform == RemotePlatform.Modrinth ? "Modrinth" : "CurseForge";
    public string CategoriesDisplay => string.Join(" / ", Hit.Categories.Take(4));
    public string DownloadsDisplay => RemoteView.FormatDownloads(Hit.Downloads);
}

/// <summary>详情文件列表条目</summary>
public class RemoteFileVm
{
    public RemoteFileInfo File { get; }
    public string ActionLabel { get; }

    public RemoteFileVm(RemoteFileInfo file, string actionLabel)
    {
        File = file;
        ActionLabel = actionLabel;
    }

    public string VersionDisplay => string.IsNullOrEmpty(File.VersionNumber) ? "(无版本号)" : File.VersionNumber;
    public string GameVersionsDisplay => File.GameVersions.Count == 0 ? "--" : string.Join(" ", File.GameVersions);
    public string LoadersDisplay => File.Loaders.Count == 0 ? "--" : string.Join(" ", File.Loaders);
    public string SizeDisplay => RemoteView.FormatSize(File.SizeBytes);
    public string DateDisplay => RemoteView.ShortDate(File.ReleaseDate);
}

/// <summary>下载任务条目（属性实时读取 Task，配合 Items.Refresh() 刷新）</summary>
public class TaskVm
{
    public DownloadTask Task { get; }
    public TaskVm(DownloadTask task) => Task = task;

    public string Name => string.IsNullOrEmpty(Task.Name) ? "(未命名)" : Task.Name;
    public string DestName => System.IO.Path.GetFileName(Task.DestPath);
    public string ErrorText => Task.Error;

    public string StateText => Task.State switch
    {
        DownloadState.Queued => "排队中",
        DownloadState.Downloading => "下载中",
        DownloadState.Verifying => "校验中",
        DownloadState.Done => "完成",
        DownloadState.Failed => "失败",
        _ => "已取消"
    };

    public Brush StateBrush => Application.Current.TryFindResource(Task.State switch
    {
        DownloadState.Done => "GreenBrush",
        DownloadState.Failed => "RedBrush",
        DownloadState.Downloading or DownloadState.Verifying => "YellowBrush",
        _ => "TextDimBrush"
    }) as Brush ?? Brushes.Gray;

    public double ProgressPct => Math.Clamp(Task.Progress * 100, 0, 100);
    public string SizeDisplay => RemoteView.FormatSize(Task.SizeBytes);

    public Visibility RetryVisibility => Task.State is DownloadState.Failed or DownloadState.Cancelled
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => Task.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => Task.State is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled
        ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>本地 packs 目录下的整合包文件</summary>
public class PackFileVm
{
    public string Path { get; }
    public PackFileVm(string path) => Path = path;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeDisplay
    {
        get
        {
            try { return RemoteView.FormatSize(new FileInfo(Path).Length); }
            catch { return "--"; }
        }
    }
}

/// <summary>已导入的整合包记录</summary>
public class ImportedPackVm
{
    public ImportedPack Pack { get; }
    public ImportedPackVm(ImportedPack pack) => Pack = pack;

    public string Name => Pack.Name;
    public string InfoLine => $"MC {Pack.McVersion} · {Pack.Loader} · 来源 {Pack.Source}";
    public string Dir => Pack.Dir;
}

/// <summary>AI 智能推荐词条（点击即作为关键词搜索）</summary>
public class SuggestionVm
{
    public string Label { get; set; } = "";
    public string Reason { get; set; } = "";
}

// ============ 视图 ============

public partial class RemoteView : UserControl
{
    private const int PageSize = 20;

    // 搜索状态
    private readonly ObservableCollection<SearchHitVm> _resultVms = new();
    private CancellationTokenSource? _searchCts;
    private int _offset;
    private bool _hasMore;

    // 详情状态
    private RemoteSearchHit? _selectedHit;
    private CancellationTokenSource? _detailCts;
    private bool _currentIsPack;

    // 整合包导入状态
    private ModpackPlan? _pendingPlan;
    private bool _batchImporting;

    // 下载管理刷新
    private readonly DispatcherTimer _uiTimer;
    private string? _historyLoadedFor;

    public RemoteView()
    {
        InitializeComponent();

        CmbVersion.Items.Add("全部版本");
        foreach (var v in McVersions.Supported) CmbVersion.Items.Add(v.Version);
        foreach (var l in new[] { "全部", "Forge", "Fabric", "NeoForge", "Quilt" }) CmbLoader.Items.Add(l);
        foreach (var s in new[] { "按相关性", "按下载量", "按更新时间" }) CmbSort.Items.Add(s);
        CmbVersion.SelectedIndex = 0;
        CmbLoader.SelectedIndex = 0;
        CmbSort.SelectedIndex = 0;
        UpdateVersionCombo();

        ResultList.ItemsSource = _resultVms;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += UiTimer_Tick;

        Loaded += RemoteView_Loaded;
        Unloaded += RemoteView_Unloaded;
    }

    // ============ 生命周期 ============

    private void RemoteView_Loaded(object sender, RoutedEventArgs e)
    {
        ImportManager.Changed += OnImportManagerChanged;
        AppContext.ProjectChanged += OnProjectChanged;
        SearchHitVm.IconLoaded += OnIconLoaded;
        UpdateGuard();
        EnsureHistoryLoaded();
        UpdateGuardDependentLists();
        RefreshSuggestions();
    }

    private void RemoteView_Unloaded(object sender, RoutedEventArgs e)
    {
        ImportManager.Changed -= OnImportManagerChanged;
        AppContext.ProjectChanged -= OnProjectChanged;
        SearchHitVm.IconLoaded -= OnIconLoaded;
        _uiTimer.Stop();
    }

    /// <summary>后台图标加载完成 → 低优先级刷新列表（仅外观，不改变选中项）</summary>
    private void OnIconLoaded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_resultVms.Count > 0) ResultList.Items.Refresh();
        }, DispatcherPriority.Background);
    }

    private void EnsureHistoryLoaded()
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        var dir = AppContext.DirRemote(p);
        if (string.Equals(_historyLoadedFor, dir, StringComparison.OrdinalIgnoreCase)) return;
        _historyLoadedFor = dir;
        ImportManager.LoadHistory(dir); // 内部会触发 Changed → 刷新列表
    }

    private void OnProjectChanged()
    {
        UpdateGuard();
        EnsureHistoryLoaded();
        UpdateVersionCombo();
        UpdateGuardDependentLists();
        RefreshSuggestions();
        ResetSearchState();
    }

    private void UpdateGuardDependentLists()
    {
        RefreshLocalPacks();
        UpdateImportedPacks();
        UpdateTasksList();
        UpdateTimerState();
    }

    private void UpdateGuard()
    {
        GuardMask.Visibility = AppContext.CurrentProject == null ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>切换项目后清空搜索结果（结果与项目无关，但详情面板需要重置）</summary>
    private void ResetSearchState()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        ResultList.SelectedIndex = -1;
        _resultVms.Clear();
        _offset = 0;
        _hasMore = false;
        BtnMore.IsEnabled = false;
        HideDetail();
        ResultEmptyText.Visibility = Visibility.Visible;
    }

    // ============ 页签切换 ============

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // XAML 初始化期间的 Checked 不处理
        SearchPanel.Visibility = TabSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PackPanel.Visibility = TabPack.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPanel.Visibility = TabDownloads.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabPack.IsChecked == true)
        {
            RefreshLocalPacks();
            UpdateImportedPacks();
        }
        UpdateTimerState();
    }

    // ============ 格式化辅助 ============

    /// <summary>下载量友好格式：≥1e8 → x.x亿；≥1e4 → x.x万；否则原值</summary>
    public static string FormatDownloads(long n)
    {
        if (n >= 100_000_000) return (n / 1e8).ToString("0.0") + "亿";
        if (n >= 10_000) return (n / 1e4).ToString("0.0") + "万";
        return n.ToString();
    }

    /// <summary>大小友好格式：B / KB / MB / GB，0 显示 "--"</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "--";
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        if (bytes >= 1L << 10) return (bytes / 1024.0).ToString("0.0") + " KB";
        return bytes + " B";
    }

    /// <summary>ISO 时间字符串截取 yyyy-MM-dd，空显示 "--"</summary>
    public static string ShortDate(string iso)
        => string.IsNullOrEmpty(iso) ? "--" : (iso.Length >= 10 ? iso[..10] : iso);

    private static string SafeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
            sb.Append(System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "modpack" : s;
    }

    /// <summary>安全拼接相对路径（防路径穿越），非法返回 null</summary>
    private static string? SafeJoin(string root, string relative)
    {
        try
        {
            var rel = relative.Replace('/', System.IO.Path.DirectorySeparatorChar)
                              .Replace('\\', System.IO.Path.DirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
            var rootFull = System.IO.Path.GetFullPath(root);
            if (!full.StartsWith(rootFull + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch { return null; }
    }

    // ============ 页签1：资源搜索 ============

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = DoSearchAsync(reset: true);
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await DoSearchAsync(reset: true);

    private async void More_Click(object sender, RoutedEventArgs e) => await DoSearchAsync(reset: false);

    private void PlatformType_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_resultVms.Count > 0) _ = DoSearchAsync(reset: true);
    }

    private async Task DoSearchAsync(bool reset)
    {
        if (AppContext.CurrentProject == null) return;

        int offset = reset ? 0 : _offset;
        var cts = new CancellationTokenSource();
        var old = _searchCts;
        _searchCts = cts;
        old?.Cancel();

        try
        {
            AppContext.SetStatus(reset ? "正在搜索…" : "正在加载更多…");
            var hits = await SearchCoreAsync(offset, PageSize, cts.Token);
            if (cts.IsCancellationRequested) return;

            if (reset)
            {
                ResultList.SelectedIndex = -1;
                _resultVms.Clear();
                HideDetail();
            }
            _offset = offset + hits.Count;
            _hasMore = hits.Count >= PageSize;
            foreach (var h in hits) _resultVms.Add(new SearchHitVm(h));
            BtnMore.IsEnabled = _hasMore;
            ResultEmptyText.Visibility = _resultVms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppContext.SetStatus($"搜索完成，当前共 {_resultVms.Count} 条结果。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "搜索失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppContext.SetStatus("搜索失败。");
        }
        finally
        {
            if (_searchCts == cts) _searchCts = null;
        }
    }

    private async Task<List<RemoteSearchHit>> SearchCoreAsync(int offset, int limit, CancellationToken ct)
    {
        var platform = RbModrinth.IsChecked == true ? RemotePlatform.Modrinth : RemotePlatform.CurseForge;
        var projectType = RbTypeMod.IsChecked == true ? "mod" : "modpack";
        var version = CmbVersion.SelectedIndex > 0 ? CmbVersion.SelectedItem as string : null;
        var loader = CmbLoader.SelectedIndex > 0 ? CmbLoader.SelectedItem as string : null;
        var sort = CmbSort.SelectedIndex switch
        {
            1 => "downloads",
            2 => platform == RemotePlatform.Modrinth ? "newest" : "updated",
            _ => platform == RemotePlatform.Modrinth ? "relevance" : "popularity"
        };
        var query = SearchBox.Text?.Trim() ?? "";

        return platform == RemotePlatform.Modrinth
            ? await ModrinthClient.SearchAsync(query, version, loader, projectType, sort, limit, offset, ct)
            : await CurseForgeClient.SearchAsync(query, version, loader, projectType, sort, limit, offset, ct);
    }

    // ---- 详情 ----

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ResultList.SelectedItem as SearchHitVm;
        if (vm == null) { HideDetail(); return; }
        _selectedHit = vm.Hit;
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        ShowDetailHeader(vm.Hit);
        _ = LoadFilesAsync(vm.Hit, _detailCts.Token);
    }

    private void ShowDetailHeader(RemoteSearchHit hit)
    {
        _currentIsPack = string.Equals(hit.ProjectType, "modpack", StringComparison.OrdinalIgnoreCase);
        DetailTitle.Text = hit.Name;
        DetailMeta.Text = $"{(string.IsNullOrEmpty(hit.Author) ? "未知作者" : hit.Author)} · " +
                          $"{FormatDownloads(hit.Downloads)} 次下载 · 更新于 {ShortDate(hit.DateModified)} · {hit.Platform}";
        DetailSummary.Text = string.IsNullOrEmpty(hit.Summary) ? "（无简介）" : hit.Summary;
        BtnResolveDeps.Visibility = _currentIsPack ? Visibility.Collapsed : Visibility.Visible;
        DetailLoading.Text = "正在加载文件列表…";
        DetailLoading.Visibility = Visibility.Visible;
        FileList.ItemsSource = null;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void HideDetail()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        _selectedHit = null;
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        ResultList.SelectedIndex = -1;
        HideDetail();
    }

    private async Task LoadFilesAsync(RemoteSearchHit hit, CancellationToken ct)
    {
        try
        {
            var files = hit.Platform == RemotePlatform.Modrinth
                ? await ModrinthClient.GetVersionsAsync(hit.Id, ct)
                : await CurseForgeClient.GetFilesAsync(hit.Id, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_selectedHit, hit)) return;

            files = files.Where(f => f.FileName.Length > 0 && f.Url.Length > 0)
                         .OrderByDescending(f => f.ReleaseDate, StringComparer.Ordinal)
                         .ToList();
            var label = string.Equals(hit.ProjectType, "modpack", StringComparison.OrdinalIgnoreCase) ? "下载整合包" : "下载";
            FileList.ItemsSource = files.Select(f => new RemoteFileVm(f, label)).ToList();

            DetailLoading.Text = files.Count == 0 ? "未找到任何文件版本。" : "";
            DetailLoading.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_selectedHit, hit) || ct.IsCancellationRequested) return;
            DetailLoading.Visibility = Visibility.Collapsed;
            MessageBox.Show(ex.Message, "加载文件失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResolveDeps_Click(object sender, RoutedEventArgs e)
    {
        var hit = _selectedHit;
        var p = AppContext.CurrentProject;
        if (hit == null || p == null) return;
        if (_currentIsPack) return;

        BtnResolveDeps.IsEnabled = false;
        AppContext.SetStatus("正在解析依赖…");
        try
        {
            // 依赖解析需要具体加载器，使用项目加载器（"全部加载器"时无法匹配远程版本）
            var ver = p.McVersion;
            var loader = p.ModLoader;
            var all = new List<RemoteFileInfo>();

            if (hit.Platform == RemotePlatform.Modrinth)
            {
                var main = await ModrinthClient.GetBestVersionAsync(hit.Id, ver, loader);
                if (main != null) all.Add(main);
                all.AddRange(await ModrinthClient.ResolveDependenciesAsync(hit.Id, ver, loader));
            }
            else
            {
                var main = await CurseForgeClient.GetBestFileAsync(hit.Id, ver, loader);
                if (main != null)
                {
                    all.Add(main);
                    foreach (var depId in main.RequiredDependencyIds)
                    {
                        var dep = await CurseForgeClient.GetBestFileAsync(depId, ver, loader);
                        if (dep != null) all.Add(dep);
                    }
                }
            }

            all = all.Where(f => f.FileName.Length > 0 && f.Url.Length > 0)
                     .GroupBy(f => f.FileId)
                     .Select(g => g.First())
                     .ToList();

            if (all.Count == 0)
            {
                MessageBox.Show($"未找到匹配 {ver} / {loader} 的文件版本。", "解析依赖", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var f in all)
            {
                var dest = System.IO.Path.Combine(AppContext.DirRemote(p), "mods", f.FileName);
                ImportManager.Enqueue(string.IsNullOrEmpty(f.DisplayName) ? hit.Name : f.DisplayName,
                    f.Url, dest, string.IsNullOrEmpty(f.Sha1) ? null : f.Sha1, f.SizeBytes, f.Platform);
            }
            ImportManager.Start();
            AppContext.SetStatus($"依赖解析完成：共 {all.Count} 个文件（含主文件）已加入下载队列。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "解析依赖失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppContext.SetStatus("解析依赖失败。");
        }
        finally
        {
            BtnResolveDeps.IsEnabled = true;
        }
    }

    private async void FileAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RemoteFileVm vm) return;
        var p = AppContext.CurrentProject;
        if (p == null || _selectedHit == null) return;

        var chosen = vm.File;

        // ===== 下载时自动匹配项目版本 =====
        if (ChkAutoMatch.IsChecked == true && !_currentIsPack && !FileMatches(chosen, p.McVersion, p.ModLoader))
        {
            try
            {
                AppContext.SetStatus($"所选文件与项目（MC {p.McVersion} · {p.ModLoader}）不匹配，正在自动匹配兼容版本…");
                var best = chosen.Platform == RemotePlatform.Modrinth
                    ? await ModrinthClient.GetBestVersionAsync(chosen.ProjectId, p.McVersion, p.ModLoader)
                    : await CurseForgeClient.GetBestFileAsync(chosen.ProjectId, p.McVersion, p.ModLoader);

                if (best != null && best.FileId != chosen.FileId)
                {
                    var ans = MessageBox.Show(
                        $"所选文件「{chosen.FileName}」与当前项目不兼容（MC {p.McVersion} · {p.ModLoader}）。\n\n" +
                        $"已自动匹配到兼容版本：\n{best.FileName}（{best.VersionNumber}）\n\n是否改为下载兼容版本？",
                        "自动匹配项目版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ans == MessageBoxResult.Yes) chosen = best;
                }
                else if (best == null)
                {
                    var ans = MessageBox.Show(
                        $"未找到匹配当前项目（MC {p.McVersion} · {p.ModLoader}）的文件版本。\n\n" +
                        $"所选文件「{chosen.FileName}」可能与项目不兼容（游戏内可能报错或无法加载）。\n\n仍要继续下载吗？",
                        "未找到兼容版本", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ans != MessageBoxResult.Yes) return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // 匹配失败不阻断下载：回退为按用户所选文件下载
                AppContext.SetStatus("自动版本匹配失败，将按所选文件下载：" + ex.Message);
            }
        }

        var subdir = _currentIsPack ? "packs" : "mods";
        var dest = System.IO.Path.Combine(AppContext.DirRemote(p), subdir, chosen.FileName);
        ImportManager.Enqueue(string.IsNullOrEmpty(chosen.DisplayName) ? _selectedHit.Name : chosen.DisplayName,
            chosen.Url, dest, string.IsNullOrEmpty(chosen.Sha1) ? null : chosen.Sha1, chosen.SizeBytes, chosen.Platform);
        ImportManager.Start();

        if (!ReferenceEquals(chosen, vm.File))
            AppContext.SetStatus($"已自动匹配项目版本（MC {p.McVersion} · {p.ModLoader}）并加入下载队列：{chosen.FileName}");
        else if (_currentIsPack)
            AppContext.SetStatus($"整合包 {chosen.FileName} 已加入下载队列，完成后请到【整合包导入】页选择该文件完成导入。");
        else
            AppContext.SetStatus($"已加入下载队列：{chosen.FileName}");
    }

    /// <summary>判断文件是否与项目的 MC 版本和加载器兼容</summary>
    private static bool FileMatches(RemoteFileInfo f, string mcVersion, string loader)
    {
        bool verOk = f.GameVersions.Contains(mcVersion, StringComparer.Ordinal);
        bool loaderOk = string.IsNullOrEmpty(loader) || loader == "全部"
            || f.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)
            || f.GameVersions.Contains(loader, StringComparer.OrdinalIgnoreCase);
        return verOk && loaderOk;
    }

    // ============ AI 智能推荐 ============

    /// <summary>依据项目上下文（名称 / ModID / 描述 / 版本 / 加载器）刷新推荐词条</summary>
    private void RefreshSuggestions()
    {
        var p = AppContext.CurrentProject;
        List<SuggestionVm> items;
        if (p == null)
        {
            items = new List<SuggestionVm>();
        }
        else
        {
            items = SmartMatch.GetSearchSuggestions(p.Name, p.ModId, p.Description, p.McVersion, p.ModLoader)
                .Select(s => new SuggestionVm { Label = s.Text, Reason = s.Reason })
                .ToList();
        }
        AiChips.ItemsSource = items;
        AiChipsPanel.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AiChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SuggestionVm vm) return;
        SearchBox.Text = vm.Label;
        SearchBox.Focus();
        _ = DoSearchAsync(reset: true);
    }

    private void UpdateVersionCombo()
    {
        var want = AppContext.CurrentProject?.McVersion;
        int idx = 0;
        for (int i = 1; i < CmbVersion.Items.Count; i++)
            if (string.Equals(CmbVersion.Items[i] as string, want, StringComparison.Ordinal)) { idx = i; break; }
        CmbVersion.SelectedIndex = idx;
    }

    // ============ 页签2：整合包导入 ============

    private async void PickPack_Click(object sender, RoutedEventArgs e)
    {
        if (AppContext.CurrentProject == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "选择整合包文件",
            Filter = "整合包文件 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await ParseAndShowPlanAsync(dlg.FileName);
    }

    private async void ImportPackFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackFileVm vm) return;
        await ParseAndShowPlanAsync(vm.Path);
    }

    /// <summary>后台解析整合包文件并展示解析结果（.mrpack → 本地解析，zip → CurseForge 联网解析）</summary>
    private async Task ParseAndShowPlanAsync(string path)
    {
        if (_batchImporting) { MessageBox.Show("已有导入任务在进行中，请等待完成。"); return; }
        AppContext.SetStatus("正在解析整合包（CurseForge 包需联网获取文件信息）…");
        try
        {
            var plan = path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                ? await Task.Run(() => ModpackParser.ParseMrpack(path))
                : await Task.Run(() => ModpackParser.ParseCurseForgeAsync(path, System.Threading.CancellationToken.None));
            if (plan == null)
            {
                MessageBox.Show("无法识别该整合包格式：需要 Modrinth .mrpack（含 modrinth.index.json）或 CurseForge zip（含 manifest.json）。",
                    "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                AppContext.SetStatus("整合包解析失败。");
                return;
            }
            SetPendingPlan(plan);
            AppContext.SetStatus($"整合包解析完成：{plan.Name}，共 {plan.Files.Count} 个待下载文件。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "解析失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            AppContext.SetStatus("整合包解析失败。");
        }
    }

    private void SetPendingPlan(ModpackPlan plan)
    {
        _pendingPlan = plan;
        ResetProgressCard();
        PackInfoPanel.Visibility = Visibility.Visible;
        PackInfoName.Text = plan.Name;
        PackInfoMeta.Text = $"MC {plan.McVersion} · {(plan.Loader.Length > 0 ? plan.Loader : "无加载器")} · 来源 {plan.Platform}";
        PackInfoCount.Text = plan.Files.Count == 0
            ? "无待下载文件（仅解压覆盖文件）"
            : $"需下载 {plan.Files.Count} 个文件";
        BtnStartImport.IsEnabled = true;
    }

    private void ResetProgressCard()
    {
        PackProgress.Value = 0;
        PackCurrentFile.Text = "";
        PackStatText.Text = "";
        ImportProgressCard.Visibility = Visibility.Collapsed;
    }

    private async void StartImport_Click(object sender, RoutedEventArgs e)
    {
        var plan = _pendingPlan;
        var p = AppContext.CurrentProject;
        if (plan == null || p == null) return;
        if (_batchImporting) { MessageBox.Show("已有导入任务在进行中，请等待完成。"); return; }

        _batchImporting = true;
        BtnStartImport.IsEnabled = false;
        ImportProgressCard.Visibility = Visibility.Visible;
        PackProgress.Value = 0;

        int total = plan.Files.Count;
        int done = 0;
        var failures = new List<string>();
        var importDir = System.IO.Path.Combine(AppContext.DirRemote(p), "modpacks", SafeName(plan.Name));

        try
        {
            Directory.CreateDirectory(importDir);

            // 批量顺序下载：dest = importDir/<RelativePath>，聚合进度 = (i + fileProgress) / total
            for (int i = 0; i < total; i++)
            {
                var entry = plan.Files[i];
                PackCurrentFile.Text = $"({i + 1}/{total})  {entry.RelativePath}";
                var dest = SafeJoin(importDir, entry.RelativePath);
                if (dest == null)
                {
                    failures.Add($"{entry.RelativePath}：非法路径");
                }
                else
                {
                    try
                    {
                        var idx = i;
                        var prog = new Progress<double>(v =>
                            PackProgress.Value = Math.Clamp((idx + Math.Clamp(v, 0, 1)) / total * 100, 0, 100));
                        await DownloadService.DownloadFileAsync(entry.Url, dest,
                            string.IsNullOrEmpty(entry.Sha1) ? null : entry.Sha1, prog,
                            System.Threading.CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{entry.RelativePath}：{ex.Message}");
                    }
                }
                done++;
                PackProgress.Value = Math.Clamp((double)done / total * 100, 0, 100);
                PackStatText.Text = $"已完成 {done}/{total}，失败 {failures.Count}";
            }

            // 全部完成后解压覆盖文件并登记（解压放后台线程，避免大包解压冻结界面）
            PackCurrentFile.Text = "正在解压整合包覆盖文件（overrides）…";
            await Task.Run(() => ModpackParser.ExtractOverrides(plan.PackPath, importDir));
            ImportManager.RegisterImport(plan, importDir);
            ImportManager.SaveHistory(AppContext.DirRemote(p));
            RefreshLocalPacks();

            if (failures.Count > 0)
            {
                var detail = string.Join("\n", failures.Take(12)) + (failures.Count > 12 ? $"\n… 等共 {failures.Count} 个" : "");
                MessageBox.Show($"整合包「{plan.Name}」导入完成，但有 {failures.Count} 个文件下载失败" +
                                "（文件 URL 可能已失效，其余成功文件已保留）：\n\n" + detail,
                    "部分文件失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                AppContext.SetStatus($"整合包「{plan.Name}」导入完成：成功 {done - failures.Count}/{total}，失败 {failures.Count}。");
            }
            else
            {
                MessageBox.Show($"整合包「{plan.Name}」导入完成！\n\n共处理 {total} 个文件。\n导入目录：\n{importDir}",
                    "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
                AppContext.SetStatus($"整合包「{plan.Name}」导入完成（{total} 个文件）。");
            }

            _pendingPlan = null;
            PackInfoPanel.Visibility = Visibility.Collapsed;
            BtnStartImport.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            AppContext.SetStatus("整合包导入失败。");
        }
        finally
        {
            _batchImporting = false;
            BtnStartImport.IsEnabled = _pendingPlan != null;
        }
    }

    private void RefreshLocalPacks()
    {
        var items = new List<PackFileVm>();
        var p = AppContext.CurrentProject;
        if (p != null)
        {
            var dir = System.IO.Path.Combine(AppContext.DirRemote(p), "packs");
            try
            {
                if (Directory.Exists(dir))
                    items = Directory.GetFiles(dir)
                        .Where(f => f.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTime)
                        .Select(f => new PackFileVm(f))
                        .ToList();
            }
            catch { /* 目录扫描失败按空处理 */ }
        }
        LocalPacksList.ItemsSource = items;
        bool empty = items.Count == 0;
        LocalPacksEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LocalPacksList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateImportedPacks()
    {
        var items = ImportManager.ImportedPacks.Select(p => new ImportedPackVm(p)).ToList();
        ImportedPacksList.ItemsSource = items;
        ImportedEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportedPacksList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenPackDir_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImportedPackVm vm) return;
        OpenInExplorer(vm.Dir);
    }

    private void OpenInExplorer(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ============ 页签3：下载管理 ============

    private void RetryAllFailed_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in ImportManager.Tasks.Where(t => t.State == DownloadState.Failed).ToList())
            ImportManager.Retry(t);
        UpdateTasksList();
        UpdateTimerState();
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in ImportManager.Tasks.Where(t => t.State == DownloadState.Done).ToList())
            ImportManager.Remove(t);
        UpdateTasksList();
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        OpenInExplorer(System.IO.Path.Combine(AppContext.DirRemote(p), "mods"));
    }

    private void TaskAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskVm vm) return;
        switch (vm.Task.State)
        {
            case DownloadState.Failed or DownloadState.Cancelled:
                ImportManager.Retry(vm.Task);
                break;
            case DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying:
                ImportManager.Cancel(vm.Task);
                break;
            default:
                ImportManager.Remove(vm.Task);
                break;
        }
        UpdateTasksList();
        UpdateTimerState();
    }

    private void UpdateTasksList()
    {
        var tasks = ImportManager.Tasks;
        TasksList.ItemsSource = tasks.Select(t => new TaskVm(t)).ToList();
        TasksEmpty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksList.Visibility = tasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============ 导入管理器事件与刷新定时 ============

    private void OnImportManagerChanged()
    {
        // 低优先级调度，合并高频进度事件
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshLocalPacks();   // 整合包下载完成后出现在 packs 列表
            UpdateImportedPacks();
            UpdateTasksList();
            UpdateTimerState();
        }), DispatcherPriority.Background);
    }

    /// <summary>仅在页签3可见且有活动任务时启动 200ms 刷新定时器（DownloadTask 无 INPC）</summary>
    private void UpdateTimerState()
    {
        bool tabVisible = DownloadsPanel.Visibility == Visibility.Visible;
        bool hasActive = ImportManager.Tasks.Any(t =>
            t.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying);
        if (tabVisible && hasActive) _uiTimer.Start();
        else _uiTimer.Stop();
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (DownloadsPanel.Visibility != Visibility.Visible) { _uiTimer.Stop(); return; }
        TasksList.Items.Refresh(); // TaskVm 属性实时读取 Task，Refresh 重新求值
    }
}
