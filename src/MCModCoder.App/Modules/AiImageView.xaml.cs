using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MCModCoder.Core.Services;

namespace MCModCoder.Modules;

/// <summary>风格预设条目（供 XAML 绑定）</summary>
public record StylePreset(string Name, string Hint);

/// <summary>审计结论行（供 XAML 绑定）</summary>
public record AuditFindingVm(string Text, System.Windows.Media.Brush Brush);

/// <summary>
/// 「AI 图像生成」模块：接入外接 AI 图像模型（OpenAI 兼容 / Stable Diffusion WebUI），
/// 用文字描述生成物品图标、方块纹理等素材，并可一键导入纹理库继续手工编辑。
/// </summary>
public partial class AiImageView : UserControl
{
    private readonly AiImageSettings _settings = AiImageSettings.Load();
    private bool _busy;
    private string? _currentFile;   // 当前预览的本地文件路径

    public ObservableCollection<HistoryItem> History { get; } = new();

    public AiImageView()
    {
        InitializeComponent();
        StyleChips.ItemsSource = AiImageAssist.StylePresets
            .Select(t => new StylePreset(t.Name, t.Hint)).ToList();
        HistoryList.ItemsSource = History;
        LoadSettingsToUi();
        // 订阅/退订成对放在 Loaded/Unloaded：只放 ctor 订阅 + Unloaded 退订会导致
        // 视图二次进入后不再收到项目切换事件（遮罩、历史列表失效）
        Loaded += (_, _) =>
        {
            AppContext.ProjectChanged += OnProjectChanged;
            UpdateGuard();
        };
        Unloaded += (_, _) => AppContext.ProjectChanged -= OnProjectChanged;
    }

    // ================= 设置 =================

    private void LoadSettingsToUi()
    {
        CmbProvider.SelectedIndex = _settings.Provider switch
        {
            AiImageProviderKind.SdWebUi => 1,
            _ => 0,
        };
        TxtEndpoint.Text = _settings.Endpoint;
        PswApiKey.Password = _settings.ApiKey;
        TxtModel.Text = _settings.Model;
        TxtNegative.Text = _settings.NegativePrompt;
        ChkPixelStyle.IsChecked = _settings.PixelStyle;
        // 尺寸：命中既有项则选中，否则直接填入可编辑框
        var sizeItem = CmbSize.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content as string, _settings.Size, StringComparison.OrdinalIgnoreCase));
        if (sizeItem != null) CmbSize.SelectedItem = sizeItem;
        else CmbSize.Text = _settings.Size;
        UpdateProviderFields();
    }

    private void SaveSettingsToModel()
    {
        _settings.Provider = CmbProvider.SelectedIndex == 1
            ? AiImageProviderKind.SdWebUi : AiImageProviderKind.OpenAiCompatible;
        _settings.Endpoint = TxtEndpoint.Text.Trim();
        _settings.ApiKey = PswApiKey.Password.Trim();
        _settings.Model = TxtModel.Text.Trim();
        _settings.Size = string.IsNullOrWhiteSpace(CmbSize.Text) ? "1024x1024" : CmbSize.Text.Trim();
        _settings.NegativePrompt = TxtNegative.Text.Trim();
        _settings.PixelStyle = ChkPixelStyle.IsChecked == true;
        _settings.Save();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsToModel();
        GenStatus.Text = "✔ 设置已保存。";
        AppContext.SetStatus("AI 图像生成：外接服务设置已保存。");
    }

    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderFields();

    /// <summary>按服务类型启停字段：SD WebUI 不需要密钥与模型名，但更依赖负面提示词</summary>
    private void UpdateProviderFields()
    {
        bool isSd = CmbProvider.SelectedIndex == 1;
        PswApiKey.IsEnabled = !isSd;
        TxtModel.IsEnabled = !isSd;
        TxtNegative.IsEnabled = isSd;
        TxtEndpoint.ToolTip = isSd
            ? "SD WebUI 需以 --api 启动，地址形如 http://127.0.0.1:7860"
            : "OpenAI 兼容服务地址，如 https://api.openai.com/v1";
    }

    // ================= 生成 =================

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SaveSettingsToModel();
        var prompt = TxtPrompt.Text.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            GenStatus.Text = "请先输入图像描述（提示词）。";
            return;
        }

        _busy = true;
        BtnGenerate.IsEnabled = false;
        BtnGenerate.Content = "⏳ 生成中…";
        GenStatus.Text = "正在请求外接 AI 模型，请稍候…";
        AppContext.SetStatus("AI 图像生成：正在调用外接模型…");

        try
        {
            var styleHint = (StyleChips.Tag as string) ?? "";
            var fullPrompt = AiImageAssist.BuildPrompt(prompt, styleHint, _settings.PixelStyle);
            var negative = TxtNegative.Text.Trim();
            if (string.IsNullOrEmpty(negative)) negative = null;

            var result = await Task.Run(() =>
                AiImageAssist.GenerateAsync(_settings, fullPrompt, negative)).ConfigureAwait(true);

            var audit = result.Audit;
            ShowAudit(audit);

            // 审计门禁：错误级问题（数据无效/损坏）不落盘、不进历史，避免污染项目目录
            if (audit is { HasErrors: true })
            {
                _currentFile = null;
                ImportBtn().IsEnabled = false;
                OpenFolderBtn().IsEnabled = false;
                TryShowPng(result.PngBytes);
                GenStatus.Text = $"✘ 生成结果未通过审计，已阻止保存：{audit.FirstErrorMessage}";
                AppContext.SetStatus("AI 图像生成：审计未通过 — " + audit.FirstErrorMessage);
            }
            // 自动保存到项目目录（未打开项目时仅预览，提示用户手动另存）
            else if (AppContext.CurrentProject is { } p)
            {
                _currentFile = AiImageAssist.SavePng(AppContext.DirAiImages(p), result.PngBytes, prompt);
                TryShowPng(result.PngBytes);
                RefreshHistory();
                ImportBtn().IsEnabled = true;
                OpenFolderBtn().IsEnabled = true;
                GenStatus.Text = audit is { HasWarnings: true }
                    ? $"⚠ 已生成并保存：{Path.GetFileName(_currentFile)}（存在 {audit.Findings.Count(f => f.Severity == AiAuditSeverity.Warning)} 条警告，见审计面板）"
                    : $"✔ 已生成并保存：{Path.GetFileName(_currentFile)}";
            }
            else
            {
                _currentFile = null;
                TryShowPng(result.PngBytes);
                ImportBtn().IsEnabled = false;
                OpenFolderBtn().IsEnabled = false;
                GenStatus.Text = "✔ 已生成（未打开项目，仅预览；请尽快打开项目以便自动归档）。";
            }

            if (!string.IsNullOrEmpty(result.RevisedPrompt) && result.RevisedPrompt != result.PromptUsed)
            {
                RevisedText.Text = "模型改写提示词：" + result.RevisedPrompt;
                RevisedText.Visibility = Visibility.Visible;
            }
            else RevisedText.Visibility = Visibility.Collapsed;

            AppContext.SetStatus("AI 图像生成：完成。");
        }
        catch (OperationCanceledException)
        {
            GenStatus.Text = "已取消。";
        }
        catch (Exception ex)
        {
            GenStatus.Text = "✘ 生成失败：" + ex.Message;
            AppContext.SetStatus("AI 图像生成：失败 — " + ex.Message);
        }
        finally
        {
            _busy = false;
            BtnGenerate.IsEnabled = true;
            BtnGenerate.Content = "🎨 生成图像";
        }
    }

    private void StyleChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StylePreset tag })
        {
            StyleChips.Tag = tag.Hint;   // 选中风格暂存，生成时附加
            GenStatus.Text = $"已选风格「{tag.Name}」，点击「生成图像」开始。";
        }
    }

    // ================= 预览 / 历史 / 导入 =================

    private void ShowPng(byte[] png)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        using var ms = new MemoryStream(png);
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        PreviewImage.Source = img;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>容错预览：字节无法解码时给出占位提示，而不是让异常冒泡</summary>
    private void TryShowPng(byte[] png)
    {
        try { ShowPng(png); }
        catch
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Text = "⚠&#x0a;数据无法解码预览（详见审计面板）";
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ShowFile(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        PreviewImage.Source = img;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    // ================= 输出审计展示 =================

    /// <summary>渲染审计报告到面板；报告为空时隐藏面板</summary>
    private void ShowAudit(AiAuditReport? report)
    {
        if (report == null) { AuditPanel.Visibility = Visibility.Collapsed; return; }
        AuditPanel.Visibility = Visibility.Visible;
        AuditSummary.Text = report.Summary;
        AuditSummary.Foreground = report.HasErrors ? ResBrush("RedBrush")
            : report.HasWarnings ? ResBrush("YellowBrush") : ResBrush("GreenBrush");

        var rows = new List<AuditFindingVm>();
        foreach (var f in report.Findings)
        {
            var icon = f.Severity switch
            {
                AiAuditSeverity.Error => "✘",
                AiAuditSeverity.Warning => "⚠",
                _ => "ℹ",
            };
            var brush = f.Severity switch
            {
                AiAuditSeverity.Error => ResBrush("RedBrush"),
                AiAuditSeverity.Warning => ResBrush("YellowBrush"),
                _ => ResBrush("TextDimBrush"),
            };
            var text = f.Severity == AiAuditSeverity.Info
                ? $"{icon} {f.Message}"
                : $"{icon} [{f.Code}] {f.Message}" + (string.IsNullOrEmpty(f.Suggestion) ? "" : $"　→　{f.Suggestion}");
            rows.Add(new AuditFindingVm(text, brush));
        }
        AuditList.ItemsSource = rows;
    }

    private static System.Windows.Media.Brush ResBrush(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    private void OnProjectChanged()
    {
        UpdateGuard();
        History.Clear();
        if (AppContext.CurrentProject != null) RefreshHistory();
    }

    private void UpdateGuard()
        => GuardMask.Visibility = AppContext.CurrentProject == null ? Visibility.Visible : Visibility.Hidden;

    private void RefreshHistory_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void RefreshHistory()
    {
        History.Clear();
        if (AppContext.CurrentProject is not { } p) return;
        var dir = AppContext.DirAiImages(p);
        if (!Directory.Exists(dir)) return;

        foreach (var f in Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTime).Take(60))
            History.Add(new HistoryItem(f));
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem item && File.Exists(item.Path))
        {
            _currentFile = item.Path;
            try { ShowFile(item.Path); }
            catch { PreviewImage.Source = null; }
            ImportBtn().IsEnabled = true;
            OpenFolderBtn().IsEnabled = true;
            RevisedText.Visibility = Visibility.Collapsed;

            // 历史图同样过审计：坏文件在预览阶段即暴露，而不是等到导入后
            try
            {
                ShowAudit(AiOutputAudit.AuditImage(File.ReadAllBytes(item.Path)));
            }
            catch { AuditPanel.Visibility = Visibility.Collapsed; }
        }
    }

    private void ImportToTextures_Click(object sender, RoutedEventArgs e)
    {
        if (AppContext.CurrentProject is not { } p || _currentFile == null || !File.Exists(_currentFile)) return;
        try
        {
            // 导入门禁：错误级阻止；警告级需人工确认（防止空白图 / 纯色图混入纹理库）
            var report = AiOutputAudit.AuditImage(File.ReadAllBytes(_currentFile));
            if (report.HasErrors)
            {
                ShowAudit(report);
                MessageBox.Show("该图像未通过输出审计，无法导入：\n\n" +
                                string.Join("\n", report.Findings
                                    .Where(f => f.Severity == AiAuditSeverity.Error)
                                    .Select(f => "• " + f.Message)),
                                "导入被阻止", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (report.HasWarnings)
            {
                ShowAudit(report);
                var warn = string.Join("\n", report.Findings
                    .Where(f => f.Severity == AiAuditSeverity.Warning)
                    .Select(f => "• " + f.Message));
                if (MessageBox.Show("该图像存在以下审计警告：\n\n" + warn +
                                    "\n\n仍要导入到纹理库吗？",
                                    "审计警告", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            // 导入不覆盖同名纹理：自动追加序号（-2、-3），避免悄悄覆盖掉手工调整过的成果
            var baseName = Path.GetFileNameWithoutExtension(_currentFile);
            var unique = AutoNaming.EnsureUnique(AppContext.DirTextures(p), baseName, ".png");
            var name = unique + ".png";
            var dest = Path.Combine(AppContext.DirTextures(p), name);
            File.Copy(_currentFile, dest, overwrite: false);
            AppContext.PushTexture(unique);
            AppContext.SetStatus($"AI 图像已导入纹理库：{name}（可在「物品图像设计」中继续编辑）");
            GenStatus.Text = $"✔ 已导入纹理库：{name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入纹理库失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _currentFile != null ? Path.GetDirectoryName(_currentFile)
            : AppContext.CurrentProject is { } p ? AppContext.DirAiImages(p) : null;
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void PreviewImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_currentFile == null || !File.Exists(_currentFile)) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存 AI 生成图像",
            Filter = "PNG 图像 (*.png)|*.png",
            FileName = Path.GetFileName(_currentFile),
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                File.Copy(_currentFile, dlg.FileName, overwrite: true);
                GenStatus.Text = $"✔ 已另存：{dlg.FileName}";
            }
            catch (Exception ex)
            {
                // 另存失败（路径只读/被占用）不再崩溃，给出明确提示
                MessageBox.Show("另存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private Button ImportBtn() => BtnImportTextures;

    private Button OpenFolderBtn() => BtnOpenFolder;

    public class HistoryItem
    {
        public string Path { get; }
        public string Name { get; }
        public string TimeText { get; }
        public BitmapImage? Thumb { get; }

        public HistoryItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            TimeText = File.GetLastWriteTime(path).ToString("MM-dd HH:mm");
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 96;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                Thumb = img;
            }
            catch
            {
                Thumb = null;   // 损坏的历史文件：缩略图留空，不让整个历史列表加载失败
            }
        }
    }
}
