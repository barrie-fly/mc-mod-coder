using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCModCoder.Core.Models;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Services;
using Microsoft.Win32;

namespace MCModCoder.Modules;

public partial class DevEnvView : UserControl
{
    private readonly DispatcherTimer _wizardSync = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public DevEnvView()
    {
        InitializeComponent();
        foreach (var v in McVersions.Supported)
            WizVersion.Items.Add(v.Version);
        WizVersion.SelectedItem = "1.20.1";   // 默认 1.20.1（版本列表现为全版本，不能用固定索引）
        _wizardSync.Tick += (_, _) => SyncModId();
        _wizardSync.Start();
        AppContext.ProjectChanged += RefreshSettings;
        Loaded += (_, _) => RefreshSettings();
    }

    /// <summary>模组 ID 跟随项目名（用户未手改时）</summary>
    private bool _modIdTouched;
    private void SyncModId()
    {
        if (_modIdTouched || WizName == null) return;
        WizModId.Text = WizName.Text.ToLowerInvariant().Replace(" ", "_");
        if (string.IsNullOrEmpty(WizPackage.Text) || WizPackage.Text.StartsWith("com.example."))
            WizPackage.Text = "com.example." + WizModId.Text;
    }

    public void ShowWizardTab()
    {
        // 由主窗口「新建项目」按钮调用
        try { WizName.Focus(); } catch { }
    }

    private void WizDir_TextChanged(object sender, TextChangedEventArgs e) { }

    private void WizVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WizVersion.SelectedItem is not string ver) return;
        var info = McVersions.Find(ver);
        WizLoader.Items.Clear();
        foreach (var l in McVersions.LoadersFor(ver))
            WizLoader.Items.Add(l);
        WizLoader.SelectedIndex = 0;
        WizJavaLabel.Text = "Java: " + (info?.JavaVersion ?? "17");
    }

    private void WizBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择项目目录（将在此生成工程）" };
        if (dlg.ShowDialog() != true) return;
        string name = string.IsNullOrWhiteSpace(WizName.Text) ? "MyMod" : WizName.Text;
        WizDir.Text = Path.Combine(dlg.FolderName, name);
    }

    private void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        string name = WizName.Text.Trim();
        string dir = WizDir.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("请输入项目名称。"); return; }
        if (dir.Length == 0)
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MCModCoder", name);
            WizDir.Text = dir;
        }
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            var r = MessageBox.Show($"目录 {dir}\n已存在文件，仍要在此创建项目吗？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }

        // 输入校验：模组 ID / Java 包名格式（避免生成无法构建的工程）
        string modid = WizModId.Text.Trim().ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(modid, @"^[a-z][a-z0-9_]{1,63}$"))
        {
            MessageBox.Show("模组 ID 无效：需以小写字母开头，仅含小写字母、数字、下划线，长度 2-64。\n例如：mymod",
                "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string package = WizPackage.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)*$"))
        {
            MessageBox.Show("Java 包名无效：应形如 com.example.mymod（每段以字母或下划线开头，仅含字母/数字/下划线）。",
                "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var p = new McProject
        {
            Name = name,
            ModId = modid,
            McVersion = (string?)WizVersion.SelectedItem ?? "1.20.1",
            ModLoader = (string?)WizLoader.SelectedItem ?? "Forge",
            Package = package,
            Author = WizAuthor.Text.Trim(),
            Description = WizDesc.Text.Trim(),
            ProjectDir = dir,
            Build = { JavaVersion = McVersions.Find((string?)WizVersion.SelectedItem ?? "1.20.1")?.JavaVersion ?? "17" }
        };
        try
        {
            SetLog($"正在生成项目脚手架：{dir} …");
            var files = TemplateGenerator.Generate(p);
            AppContext.OpenProject(p);
            SetLog($"✔ 已生成 {files.Count} 个文件：\n  " + string.Join("\n  ", files.Take(12)) +
                   (files.Count > 12 ? $"\n  … 共 {files.Count} 个" : ""));
            SetLog("\n下一步建议：\n  1. 在「物品图像设计」中绘制纹理 → 生成资源包\n  2. 在「配方设置」中创建配方 → 导出数据包\n  3. 在「代码编写」中编写逻辑 → 一键构建\n  4. 在「测试工具」中部署运行并分析日志");
            MessageBox.Show($"项目「{p.Name}」创建成功！\n共生成 {files.Count} 个文件。", "创建完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("创建失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================= 项目设置 =================
    private TextBox? _nameBox, _pkgBox, _authorBox, _descBox, _javaBox;
    private ComboBox? _verBox, _loaderBox;

    private void RefreshSettings()
    {
        SettingsPanel.Children.Clear();
        var p = AppContext.CurrentProject;
        if (p == null)
        {
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "尚未打开项目。请先创建或打开项目。",
                Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush")
            });
            return;
        }

        void AddRow(string label, FrameworkElement editor)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var lbl = new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(lbl);
            sp.Children.Add(editor);
            SettingsPanel.Children.Add(sp);
        }

        _nameBox = new TextBox { Width = 260, Text = p.Name };
        _pkgBox = new TextBox { Width = 260, Text = p.Package };
        _authorBox = new TextBox { Width = 260, Text = p.Author };
        _descBox = new TextBox { Width = 420, Text = p.Description };
        var dirBox = new TextBox { Width = 420, Text = p.ProjectDir, IsReadOnly = true };
        _verBox = new ComboBox { Width = 140 };
        foreach (var v in McVersions.Supported) _verBox.Items.Add(v.Version);
        _verBox.SelectedItem = p.McVersion;
        _loaderBox = new ComboBox { Width = 140 };
        _javaBox = new TextBox { Width = 80, Text = p.Build.JavaVersion, TextAlignment = TextAlignment.Center };

        void SyncLoaders()
        {
            string? v = _verBox!.SelectedItem as string;
            _loaderBox!.Items.Clear();
            foreach (var l in McVersions.LoadersFor(v ?? "1.20.1")) _loaderBox.Items.Add(l);
            _loaderBox.SelectedItem = p.ModLoader;
            _javaBox!.Text = McVersions.Find(v ?? "1.20.1")?.JavaVersion ?? "17";
        }
        _verBox.SelectionChanged += (_, _) => SyncLoaders();
        SyncLoaders();

        AddRow("项目名称", _nameBox);
        AddRow("Java 包名", _pkgBox);
        AddRow("作者", _authorBox);
        AddRow("项目目录", dirBox);
        AddRow("Minecraft 版本", _verBox);
        AddRow("模组加载器", _loaderBox);
        AddRow("Java 版本", _javaBox);

        SettingsPanel.Children.Add(new TextBlock { Text = "依赖管理", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        var depGrid = new DataGrid { Height = 180, ItemsSource = p.Dependencies };
        depGrid.Columns.Add(new DataGridTextColumn { Header = "Group:Artifact:Version", Binding = new System.Windows.Data.Binding("Coordinate"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        depGrid.Columns.Add(new DataGridTextColumn { Header = "Scope", Binding = new System.Windows.Data.Binding("Scope"), Width = 120 });
        SettingsPanel.Children.Add(depGrid);

        var depBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var coordBox = new TextBox { Width = 330, Text = "net.example:lib:1.0.0" };
        var scopeBox = new ComboBox { Width = 130, ItemsSource = new[] { "implementation", "compileOnly", "runtimeOnly" }, SelectedIndex = 0 };
        depBtns.Children.Add(coordBox);
        depBtns.Children.Add(scopeBox);
        var addBtn = new Button { Content = "添加依赖", Margin = new Thickness(8, 0, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            var parts = coordBox.Text.Split(':');
            if (parts.Length != 3) { MessageBox.Show("坐标格式：group:artifact:version"); return; }
            p.Dependencies.Add(new DependencyEntry
            { GroupId = parts[0].Trim(), Artifact = parts[1].Trim(), Version = parts[2].Trim(), Scope = (string?)scopeBox.SelectedItem ?? "implementation" });
            depGrid.Items.Refresh();
        };
        var delBtn = new Button { Content = "删除选中", Margin = new Thickness(8, 0, 0, 0) };
        delBtn.Click += (_, _) =>
        {
            if (depGrid.SelectedItem is DependencyEntry d) p.Dependencies.Remove(d);
            depGrid.Items.Refresh();
        };
        depBtns.Children.Add(addBtn);
        depBtns.Children.Add(delBtn);
        SettingsPanel.Children.Add(depBtns);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null || _nameBox == null) { MessageBox.Show("请先打开项目。"); return; }
        p.Name = _nameBox.Text.Trim();
        p.Package = _pkgBox!.Text.Trim();
        p.Author = _authorBox!.Text.Trim();
        p.Description = _descBox!.Text;
        p.McVersion = (string?)_verBox!.SelectedItem ?? p.McVersion;
        p.ModLoader = (string?)_loaderBox!.SelectedItem ?? p.ModLoader;
        p.Build.JavaVersion = _javaBox!.Text;
        p.ModifiedAt = DateTime.Now;
        try
        {
            TemplateGenerator.Generate(p);   // 重新生成配置文件（保留用户源码）
            AppContext.SetStatus("✔ 项目设置已保存，build.gradle 等配置已按新设置重新生成。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message);
        }
    }

    // ================= 构建 =================
    private bool _building;   // 构建进行中（防重复点击）
    private void SetLog(string text) => BuildLog.AppendText(text + "\n");

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        var p = AppContext.CurrentProject;
        if (p == null) { MessageBox.Show("请先打开项目。"); return; }
        _building = true;
        BtnBuild.IsEnabled = false;
        BtnBuild.Content = "⏳ 构建中…";
        try
        {
            BuildLog.Clear();
            SetLog($"[{DateTime.Now:HH:mm:ss}] 开始构建 {p.Name}（{p.McVersion} / {p.ModLoader}）…");

            string gradlew = Path.Combine(p.ProjectDir, "gradlew.bat");
            if (File.Exists(gradlew))
            {
                await RunGradleAsync(p, gradlew);
            }
            else
            {
                SetLog("未检测到 gradlew.bat，执行内置模拟构建流程（生成 Gradle Wrapper 后可进行真实构建）：");
                await SimulateBuildAsync(p);
            }

            if (RunClientCheck.IsChecked == true)
            {
                SetLog("\n[启动] runClient（如已配置 Forge/Fabric 运行环境将拉起游戏客户端）…");
                await SimulateBuildAsync(p, runClient: true);
            }
            SetLog($"[{DateTime.Now:HH:mm:ss}] 构建流程结束。");
        }
        catch (Exception ex)
        {
            SetLog($"✖ 构建过程出现异常：{ex.Message}\n（详细排查可查看「测试工具」模块的日志分析）");
            AppContext.SetStatus("构建失败：" + ex.Message);
        }
        finally
        {
            _building = false;
            BtnBuild.IsEnabled = true;
            BtnBuild.Content = "🔨 一键构建";
        }
    }

    private async Task RunGradleAsync(McProject p, string gradlew)
    {
        var tcs = new TaskCompletionSource<bool>();
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{gradlew}\" build --console=plain {(p.Build.ParallelBuild ? "--parallel" : "")} {p.Build.ExtraGradleArgs}".Trim(),
            WorkingDirectory = p.ProjectDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) { SetLog("✖ 无法启动 Gradle 进程。"); return; }
        proc.OutputDataReceived += (_, ev) => { if (ev.Data != null) Dispatcher.BeginInvoke(() => SetLog(ev.Data)); };
        proc.ErrorDataReceived += (_, ev) => { if (ev.Data != null) Dispatcher.BeginInvoke(() => SetLog("[stderr] " + ev.Data)); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await Task.Run(() => { proc.WaitForExit(); tcs.TrySetResult(true); });
        SetLog(proc.ExitCode == 0 ? "✔ Gradle 构建成功。" : $"✖ Gradle 构建失败（退出码 {proc.ExitCode}）。请检查上方日志。");
    }

    private async Task SimulateBuildAsync(McProject p, bool runClient = false)
    {
        SetLog("> configure build ……");
        await Task.Delay(300);
        SetLog($"> Java {p.Build.JavaVersion} · Loader {p.ModLoader} · MC {p.McVersion}");
        await Task.Delay(300);

        int javaFiles = 0;
        try
        {
            javaFiles = Directory.EnumerateFiles(Path.Combine(p.ProjectDir, "src"), "*.java", SearchOption.AllDirectories).Count();
        }
        catch { }
        SetLog($"> compileJava：编译 {javaFiles} 个 Java 源文件");
        for (int i = 0; i <= 4; i++)
        {
            await Task.Delay(160);
            SetLog($"  ▏编译进度 {i * 25}%");
        }
        SetLog("> processResources：打包资源（纹理 / 模型 / 配方数据包）");
        await Task.Delay(300);
        string jar = Path.Combine(p.ProjectDir, p.Build.OutputDir, $"{p.ModId}-1.0.0.jar");
        SetLog($"> 将生成：{jar}（模拟输出，磁盘上不会产生真实 jar 文件）");
        SetLog(runClient ? "✔ 模拟构建完成（未产出真实 jar——安装 JDK 与 Gradle Wrapper 后执行「一键构建」即可进行真实构建）。"
                        : "✔ 模拟构建完成（未产出真实 jar——安装 JDK 与 Gradle Wrapper 后执行「一键构建」即可进行真实构建）。");
    }

    private void PackModpack_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) { MessageBox.Show("请先打开项目。"); return; }
        try
        {
            string outDir = Path.Combine(p.ProjectDir, "modpack");
            Directory.CreateDirectory(outDir);
            int n = 0;
            // 资源包
            var texDir = AppContext.DirTextures(p);
            if (Directory.Exists(texDir))
            {
                var textures = Directory.GetFiles(texDir, "*.png")
                    .Select(f => ("item/" + Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f))).ToList();
                if (textures.Count > 0)
                {
                    var ver = McVersions.Find(p.McVersion);
                    n += ResourcePackExporter.Generate(Path.Combine(outDir, "resourcepack"),
                        p.ModId.ToLowerInvariant(), ver?.PackFormat ?? 15, textures).Count;
                }
            }
            // 数据包
            var recipeDir = AppContext.DirRecipes(p);
            if (Directory.Exists(recipeDir) && Directory.GetFiles(recipeDir, "*.json").Length > 0)
            {
                var recipes = Directory.GetFiles(recipeDir, "*.json")
                    .Select(f => RecipeJsonSerializer.FromJson(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
                    .Where(r => r != null).Cast<Core.Models.RecipeDefinition>().ToList();
                if (recipes.Count() > 0)
                {
                    var ver = McVersions.Find(p.McVersion);
                    n += RecipeJsonSerializer.ExportToDatapack(recipes, Path.Combine(outDir, "datapack"),
                        p.ModId.ToLowerInvariant(), ver?.DataPackFormat ?? 15).Count;
                }
            }
            // 模组清单
            File.WriteAllText(Path.Combine(outDir, "modlist.txt"),
                $"# {p.Name} 整合包清单\n# MC {p.McVersion} / {p.ModLoader}\n模组本体: {p.ModId}-1.0.0.jar\n\n依赖:\n" +
                string.Join("\n", p.Dependencies.Select(d => d.Coordinate)));
            n++;
            SetLog($"✔ 整合包内容已生成到 {outDir}（{n} 个文件）。可直接压缩分发。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("打包失败：" + ex.Message);
        }
    }

    private void DeployTest_Click(object sender, RoutedEventArgs e)
    {
        // 直接跳转到「测试工具」，不再让按钮停留在"告诉你去别处"的死胡同
        if (Window.GetWindow(this) is MCModCoder.MainWindow main)
        {
            main.NavigateTo("test");
            AppContext.SetStatus("已进入「测试工具」：设置游戏 mods 目录后点击「部署模组（复制到 mods）」。");
        }
    }
}
