using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MCModCoder.Core;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Services;
using Microsoft.Win32;

namespace MCModCoder.Modules;

public class LogLineVm
{
    public string Line { get; set; } = "";
    public Brush Brush { get; set; } = Brushes.Gray;
}

public partial class TestView : UserControl
{
    private List<LogEntry> _entries = new();
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Process? _monitored;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSample;
    private readonly Queue<double> _cpuHistory = new();
    private double _previewFps;
    private long _frames;
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TestView()
    {
        InitializeComponent();
        RefreshProcs_Click(null, null!);
        _perfTimer.Tick += SamplePerf;
        _fpsTimer.Tick += (_, _) => { _previewFps = _frames; _frames = 0; };
        _fpsTimer.Start();
    }

    // ================= 部署 / 热重载 =================
    private string ModsDir => Environment.ExpandEnvironmentVariables(ModsDirBox.Text.Trim());

    private void BrowseMods_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择游戏 mods 目录" };
        if (dlg.ShowDialog() != true) return;
        ModsDirBox.Text = dlg.FolderName;
    }

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        string jar = Path.Combine(p.ProjectDir, p.Build.OutputDir, $"{p.ModId}-1.0.0.jar");
        if (!File.Exists(jar))
        {
            MessageBox.Show($"未找到构建产物：\n{jar}\n请先在「开发环境 → 构建与打包」中执行构建。", "部署");
            return;
        }
        try
        {
            Directory.CreateDirectory(ModsDir);
            var dest = Path.Combine(ModsDir, Path.GetFileName(jar));
            File.Copy(jar, dest, overwrite: true);
            AppContext.SetStatus($"模组已部署：{dest}");
            MessageBox.Show($"部署完成：\n{dest}", "部署", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("部署失败：" + ex.Message); }
    }

    private bool _syncingHotReload;   // 程序化改 IsChecked 时抑制事件回环

    private void HotReload_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingHotReload) return;
        var p = AppContext.CurrentProject;
        if (HotReloadToggle.IsChecked != true)
        {
            _watcher?.Dispose(); _watcher = null;
            HotReloadState.Text = "热重载未启用";
            return;
        }
        if (p == null)
        {
            HotReloadState.Text = "⚠ 热重载需要先打开项目——请先创建或打开一个项目。";
            _syncingHotReload = true;
            HotReloadToggle.IsChecked = false;   // 复位开关，避免"看着开了实际没开"
            _syncingHotReload = false;
            return;
        }
        string outDir = Path.Combine(p.ProjectDir, p.Build.OutputDir);
        Directory.CreateDirectory(outDir);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(outDir, "*.jar")
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        DateTime last = DateTime.MinValue;
        _watcher.Changed += (_, ev) =>
        {
            if ((DateTime.Now - last).TotalSeconds < 2) return;   // 防抖
            last = DateTime.Now;
            Dispatcher.BeginInvoke(async () =>
            {
                string dest = Path.Combine(ModsDir, Path.GetFileName(ev.FullPath));
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(ModsDir);
                        File.Copy(ev.FullPath, dest, true);
                        HotReloadState.Text = $"热重载 @ {DateTime.Now:HH:mm:ss}：{Path.GetFileName(ev.FullPath)} 已重新部署";
                        AppContext.SetStatus("热重载：构建产物已自动部署到 mods 目录。");
                        return;
                    }
                    catch when (attempt < 3)
                    {
                        await Task.Delay(1000);   // 产物刚生成常被游戏/杀软占用，稍候重试
                    }
                    catch (Exception ex)
                    {
                        // 失败不再静默：状态行给出原因与兜底操作指引
                        HotReloadState.Text = $"⚠ 热重载部署失败：{ex.Message}（可稍后手动点击「部署模组」补一次）";
                        AppContext.SetStatus("热重载部署失败：" + ex.Message);
                        return;
                    }
                }
            });
        };
        HotReloadState.Text = "热重载已启用（监控 " + outDir + "）";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string logs = Path.Combine(ModsDir, "..", "logs");
        try { Process.Start("explorer.exe", Path.GetFullPath(logs)); }
        catch { MessageBox.Show("目录不存在：" + logs); }
    }

    // ================= 内置测试套件（单元 + 集成） =================
    private int _passed, _failed;
    private readonly List<string> _failDetails = new();

    private void Check(string name, bool cond, string detail = "")
    {
        if (cond) { _passed++; TestOutput.AppendText($"  ✔ {name}\n"); }
        else { _failed++; _failDetails.Add($"{name}: {detail}"); TestOutput.AppendText($"  ✖ {name}  —  {detail}\n"); }
    }

    private void RunTests_Click(object sender, RoutedEventArgs e)
    {
        if (_testsRunning) return;   // 防重复点击
        _testsRunning = true;
        BtnRunTests.IsEnabled = false;
        BtnRunTests.Content = "⏳ 测试运行中…";
        try
        {
            RunAllTests();
        }
        catch (Exception ex)
        {
            TestOutput.AppendText($"\n✖ 测试套件异常中断：{ex.Message}\n");
            AppContext.SetStatus("测试套件异常：" + ex.Message);
        }
        finally
        {
            _testsRunning = false;
            BtnRunTests.IsEnabled = true;
            BtnRunTests.Content = "▶ 运行内置测试套件";
        }
    }

    private bool _testsRunning;

    private void RunAllTests()
    {
        _passed = 0; _failed = 0; _failDetails.Clear();
        TestOutput.Clear();
        TestOutput.AppendText("═══ MC模组代码工作台 内置测试套件 ═══\n\n");
        TestOutput.AppendText("[单元测试] 配方序列化 / 验证\n");

        // 1. 有序合成 往返
        var shaped = new Core.Models.RecipeDefinition
        {
            Id = "test:pickaxe", Type = Core.Models.RecipeType.CraftingShaped,
            Pattern = new() { "XX ", " # ", " # " },
            Key = new() { ["X"] = Core.Models.IngredientSpec.Parse("minecraft:iron_ingot"), ["#"] = Core.Models.IngredientSpec.Parse("minecraft:stick") },
            Result = new() { Item = "minecraft:iron_pickaxe" }
        };
        var json = RecipeJsonSerializer.ToDatapackJson(shaped);
        var back = RecipeJsonSerializer.FromJson("test:pickaxe", json);
        Check("有序合成 JSON 往返保持图案", back?.Pattern.SequenceEqual(shaped.Pattern) == true);
        Check("有序合成 JSON 往返保持产物", back?.Result.Item == "minecraft:iron_pickaxe");
        Check("数据包类型字段正确", json.Contains("minecraft:crafting_shaped"));

        // 2. 熔炼
        var smelt = new Core.Models.RecipeDefinition
        {
            Id = "test:iron", Type = Core.Models.RecipeType.Smelting,
            Ingredient = Core.Models.IngredientSpec.Parse("minecraft:iron_ore"),
            Result = new() { Item = "minecraft:iron_ingot" }, CookingTime = 200, Experience = 0.7
        };
        var back2 = RecipeJsonSerializer.FromJson("test:iron", RecipeJsonSerializer.ToDatapackJson(smelt));
        Check("熔炼配方往返保持输入/时间", back2?.Ingredient.Item == "minecraft:iron_ore" && back2.CookingTime == 200);

        // 3. 验证器
        var bad = new Core.Models.RecipeDefinition { Id = "", Type = Core.Models.RecipeType.CraftingShaped };
        var issues = Core.Validation.RecipeValidator.Validate(bad);
        Check("验证器可发现空配方问题", issues.Any(i => i.Severity == Core.Validation.IssueSeverity.Error));
        var good = new Core.Models.RecipeDefinition
        {
            Id = "test:ok", Type = Core.Models.RecipeType.CraftingShapeless,
            ShapelessIngredients = { Core.Models.IngredientSpec.Parse("minecraft:stick") },
            Result = new() { Item = "minecraft:torch" }
        };
        Check("合法配方零错误", !Core.Validation.RecipeValidator.Validate(good).Any(i => i.Severity == Core.Validation.IssueSeverity.Error));

        TestOutput.AppendText("\n[单元测试] 结构变换\n");
        var s = new Core.Models.StructureData { Name = "t", SizeX = 4, SizeY = 4, SizeZ = 4 };
        s.Set(0, 1, 0, 2);
        s.RotateY();
        Check("Y 轴旋转保持方块数量", s.BlockCount == 1);
        s.RotateY(); s.RotateY(); s.RotateY();
        Check("Y 轴旋转 4 次还原", s.Get(0, 1, 0) == 2);
        s.MirrorX();
        Check("镜像保持方块数量", s.BlockCount == 1);

        var sjson = System.Text.Json.JsonSerializer.Serialize(s);
        var sback = System.Text.Json.JsonSerializer.Deserialize<Core.Models.StructureData>(sjson);
        Check("结构 JSON 往返", sback?.BlockCount == 1);

        TestOutput.AppendText("\n[单元测试] 红石模拟（集成）\n");
        var grid = new Core.Simulation.RedstoneGrid(7, 3);
        grid.Place(0, 1, Core.Simulation.SimBlock.Lever);
        grid.Place(1, 1, Core.Simulation.SimBlock.Wire);
        grid.Place(2, 1, Core.Simulation.SimBlock.Wire);
        grid.Place(3, 1, Core.Simulation.SimBlock.Lamp);
        grid.Interact(0, 1);
        grid.Tick();
        Check("拉杆开启后灯点亮", grid.OnState[3, 1]);
        grid.Interact(0, 1);
        grid.Tick();
        Check("拉杆关闭后灯熄灭", !grid.OnState[3, 1]);

        var inv = new Core.Simulation.RedstoneGrid(5, 3);
        inv.Place(0, 1, Core.Simulation.SimBlock.Torch);
        inv.Place(1, 1, Core.Simulation.SimBlock.Wire);
        inv.Place(2, 1, Core.Simulation.SimBlock.Lamp);
        inv.Tick();
        Check("火把默认点亮灯", inv.OnState[2, 1]);

        TestOutput.AppendText("\n[单元测试] 日志分析\n");
        var logEntries = new List<LogEntry>
        {
            LogAnalyzer.ParseLine("[12:00:01] [Server thread/ERROR]: NullPointerException at com.example.Test"),
            LogAnalyzer.ParseLine("[12:00:02] [main/INFO]: Done (2.1s)!"),
        };
        var findings = LogAnalyzer.Analyze(logEntries);
        Check("日志解析识别 ERROR 级别", logEntries[0].IsError);
        Check("日志分析发现空指针问题", findings.Any(f => f.Title.Contains("空指针")));

        TestOutput.AppendText("\n[单元测试] AI 输出审计（AiOutputAudit）\n");
        var goodPng = Core.Services.MiniPng.CreateRgba(16, 16, (x, y) => ((byte)(x * 16), (byte)(y * 16), 128, 255));
        var goodReport = Core.Services.AiOutputAudit.AuditImage(goodPng);
        Check("有效 PNG 审计通过", !goodReport.HasErrors && !goodReport.HasWarnings);
        Check("审计摘要可读", goodReport.Summary.Contains("✔"));

        var solidPng = Core.Services.MiniPng.CreateRgba(16, 16, (_, _) => (250, 250, 250, (byte)255));
        var solidReport = Core.Services.AiOutputAudit.AuditImage(solidPng);
        Check("纯色图触发警告且无错误", !solidReport.HasErrors && solidReport.Findings.Any(f => f.Code == "img.solid"));

        var transparentPng = Core.Services.MiniPng.CreateRgba(16, 16, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)0));
        Check("全透明图触发警告", Core.Services.AiOutputAudit.AuditImage(transparentPng).Findings.Any(f => f.Code == "img.transparent"));

        var truncated = goodPng.Take(goodPng.Length * 2 / 3).ToArray();
        Check("截断 PNG 报错误", Core.Services.AiOutputAudit.AuditImage(truncated).HasErrors);
        Check("垃圾字节报错误", Core.Services.AiOutputAudit.AuditImage(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }).HasErrors);
        Check("空数据报错误", Core.Services.AiOutputAudit.AuditImage(Array.Empty<byte>()).HasErrors);
        Check("null 数据报错误", Core.Services.AiOutputAudit.AuditImage(null).HasErrors);

        var sizeMismatch = Core.Services.AiOutputAudit.AuditImage(goodPng, 32, 32);
        Check("尺寸不符触发警告", !sizeMismatch.HasErrors && sizeMismatch.Findings.Any(f => f.Code == "img.size"));
        Check("审计结论确定性（重复执行结果一致）",
            Core.Services.AiOutputAudit.AuditImage(solidPng).Findings.Count == solidReport.Findings.Count);

        Check("空提示词报错误", Core.Services.AiOutputAudit.AuditPrompt("  \n ").HasErrors);
        Check("超长提示词触发警告", Core.Services.AiOutputAudit.AuditPrompt(new string('a', 4200)).Findings.Any(f => f.Code == "prompt.long"));
        Check("占位符提示词触发警告", Core.Services.AiOutputAudit.AuditPrompt("画一个 {{modid}} 物品").Findings.Any(f => f.Code == "prompt.placeholder"));
        var okPrompt = Core.Services.AiOutputAudit.AuditPrompt("一把发光的蓝色魔法剑");
        Check("正常提示词完全通过", okPrompt.Passed);

        Check("括号不平衡触发警告", Core.Services.AiOutputAudit.AuditCode("void f() {", "java").Findings.Any(f => f.Code == "code.balance"));
        Check("字符串内括号不误报", !Core.Services.AiOutputAudit.AuditCode("String s = \"(){\"; // )", "java").HasWarnings);
        Check("占位符残留触发警告", Core.Services.AiOutputAudit.AuditCode("register(\"{{modid}}\");", "java").Findings.Any(f => f.Code == "code.placeholder"));
        Check("空代码报错误", Core.Services.AiOutputAudit.AuditCode("", "java").HasErrors);

        TestOutput.AppendText("\n[单元测试] 提示词组装（AiImageAssist.BuildPrompt）\n");
        Check("像素约束含防文字水印指令",
            AiImageAssist.BuildPrompt("ruby", null, true).Contains("no watermark"));
        var tilePrompt = AiImageAssist.BuildPrompt("stone", AiImageAssist.StylePresets[1].Hint, true);
        Check("无缝平铺风格省略纯色背景指令", !tilePrompt.Contains("plain solid background"));
        Check("无缝平铺风格保留核心像素约束", tilePrompt.Contains("pixel art game texture"));
        Check("图标风格保留纯色背景指令",
            AiImageAssist.BuildPrompt("ruby", AiImageAssist.StylePresets[0].Hint, true).Contains("plain solid background"));
        Check("重复片段被去重", AiImageAssist.BuildPrompt("ruby gem", "ruby gem, shiny", false) == "ruby gem, shiny");
        Check("负面提示词可融入正向提示词",
            AiImageAssist.MergeNegativeIntoPrompt("a ruby", "text, watermark").Contains("Avoid the following: text, watermark"));

        TestOutput.AppendText("\n[单元测试] AI 开发引擎（技能系统 + 规划执行）\n");
        // 端到端最小链路：需求 → 规划 → 代码 → 资源 → 构建 → 打包 → 验证（临时目录，随跑随清）
        var aiRoot = Path.Combine(Path.GetTempPath(), "mcmodcoder-test-ai");
        try { if (Directory.Exists(aiRoot)) Directory.Delete(aiRoot, true); } catch { }
        var aiProject = new Core.Models.McProject
        {
            Name = "TestMod", ModId = "tmod", McVersion = "1.20.1", ModLoader = "Forge",
            Package = "com.example.tmod", Description = "内置测试", ProjectDir = aiRoot
        };
        var aiEngine = new Core.Ai.AiDevEngine();
        Check("内置技能全部注册（13 个）", aiEngine.Skills.Count == 13, $"实际 {aiEngine.Skills.Count}");
        Check("技能精确发现 file.write", aiEngine.Skills.Get("file.write") != null);
        Check("技能模糊发现「验证」→ pack.verify", aiEngine.Skills.Discover("验证").FirstOrDefault()?.Name == "pack.verify");
        var customReg = new Core.Ai.SkillRegistry();
        customReg.Register(new Core.Ai.WriteFileSkill());
        Check("注册表可注入自定义技能集", customReg.Count == 1 && customReg.Get("file.write") != null);
        var aiPlan = aiEngine.Run("注册 ruby 物品与 ruby_pickaxe 工具，ruby_pickaxe 用铁锭和木棍合成，构建并打包整合包", aiProject);
        Check("AI 端到端：需求解析出 2 个功能点", aiPlan.Tasks.FirstOrDefault(t => t.SkillName == "plan.analyze")?.Result?.Outputs.GetValueOrDefault("featureCount") == "2");
        Check("AI 端到端：全部任务完成", !aiPlan.AnyFailed,
            string.Join("；", aiPlan.Tasks.Where(t => t.Status == Core.Ai.DevTaskStatus.Failed).Select(t => $"{t.Title}: {t.Result?.Message}")));
        Check("AI 端到端：jar 产物存在", File.Exists(Path.Combine(aiRoot, "build", "libs", "tmod-1.0.0.jar")));
        Check("AI 端到端：配方 JSON 生成", File.Exists(Path.Combine(aiRoot, "src", "main", "resources", "data", "tmod", "recipes", "ruby_pickaxe.json")));
        Check("AI 端到端：整合包 zip 生成", File.Exists(Path.Combine(aiRoot, "modpack", "modpack.zip")));
        Check("AI 端到端：验证技能结论 pass",
            aiPlan.Tasks.FirstOrDefault(t => t.SkillName == "pack.verify")?.Result?.Outputs.GetValueOrDefault("verdict") == "pass");

        TestOutput.AppendText("\n══════════════════════════\n");
        TestOutput.AppendText($"结果：{_passed} 通过，{_failed} 失败\n");
        if (_failed == 0) TestOutput.AppendText("全部测试通过 ✔\n");
        AppContext.SetStatus($"测试套件完成：{_passed} 通过 / {_failed} 失败。");
    }

    // ================= 日志 =================
    private void LoadLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        { Filter = "日志文件|latest.log;*.log;*.txt|全部文件|*.*", Title = "导入游戏日志" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _entries = File.ReadAllLines(dlg.FileName).Select(LogAnalyzer.ParseLine).ToList();
            ApplyLogFilter();
            AppContext.SetStatus($"已加载日志 {dlg.FileName}（{_entries.Count} 行）。点击「智能分析」查看诊断。");
        }
        catch (Exception ex) { MessageBox.Show("读取失败：" + ex.Message); }
    }

    private void ApplyLogFilter(object? sender = null, EventArgs? e = null)
    {
        if (LevelFilter == null || LogSearch == null || _entries == null) return;   // XAML 初始化期间事件先于控件就绪
        string level = (LevelFilter.SelectedItem as ComboBoxItem)?.Content as string ?? "全部";
        string kw = LogSearch.Text ?? "";
        var vms = _entries
            .Where(en => level == "全部" || en.Level == level)
            .Where(en => kw.Length == 0 || en.Message.Contains(kw, StringComparison.OrdinalIgnoreCase))
            .Take(4000)
            .Select(en => new LogLineVm
            {
                Line = $"[{en.Time}] [{en.Level}] {en.Message}",
                Brush = en.IsError ? FindResource("RedBrush") as Brush : en.IsWarn ? FindResource("YellowBrush") as Brush : Brushes.LightGray
            }).ToList();
        LogList.ItemsSource = new ObservableCollection<LogLineVm>(vms);
        LogCountText.Text = $"显示 {vms.Count} / {_entries.Count} 行";
    }

    private void AnalyzeLog_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) { MessageBox.Show("请先导入日志文件。"); return; }
        FindingList.ItemsSource = LogAnalyzer.Analyze(_entries);
    }

    // ================= 性能监控 =================
    private void RefreshProcs_Click(object sender, RoutedEventArgs e)
    {
        var cur = ProcessCombo?.SelectedItem as string;
        ProcessCombo?.Items.Clear();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    string name = proc.ProcessName;
                    if (name.Equals("javaw", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("java", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("minecraft", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("MCModCoder", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("PrismLauncher", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("MultiMC", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("PCL", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("HMCL", StringComparison.OrdinalIgnoreCase))
                        ProcessCombo?.Items.Add($"{name} (PID {proc.Id})");
                }
                catch
                {
                    // 个别进程已退出或无权限访问，跳过即可，不影响其余进程枚举
                }
            }
        }
        catch { }
        if (ProcessCombo?.Items.Count == 0) ProcessCombo?.Items.Add("（未检测到游戏进程）");
        ProcessCombo!.SelectedIndex = 0;
        if (cur != null && ProcessCombo.Items.Contains(cur)) ProcessCombo.SelectedItem = cur;
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_perfTimer.IsEnabled)
        {
            _perfTimer.Stop();
            _monitored?.Dispose(); _monitored = null;
            BtnMonitor.Content = "▶ 开始监控";
            AppContext.SetStatus("性能监控已停止。");
            return;
        }
        var sel = ProcessCombo.SelectedItem as string ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(sel, @"PID (\d+)");
        if (!m.Success) { MessageBox.Show("未检测到可监控的进程。"); return; }
        try
        {
            _monitored = Process.GetProcessById(int.Parse(m.Groups[1].Value));
            _lastCpuTime = _monitored.TotalProcessorTime;
            _lastSample = DateTime.Now;
            _cpuHistory.Clear();
            _perfTimer.Start();
            BtnMonitor.Content = "⏸ 停止监控";   // 运行态可视化：按钮即状态
            AppContext.SetStatus($"正在监控进程 {sel}…（再次点击按钮停止）");
        }
        catch (Exception ex) { MessageBox.Show("监控失败：" + ex.Message); }
    }

    private void SamplePerf(object? sender, EventArgs e)
    {
        if (_monitored == null || _monitored.HasExited)
        {
            _perfTimer.Stop();
            _monitored?.Dispose(); _monitored = null;
            BtnMonitor.Content = "▶ 开始监控";   // 进程退出后自动复位按钮状态
            PerfText.Text = "进程已退出。";
            return;
        }
        try
        {
            _monitored.Refresh();
            var now = DateTime.Now;
            double cpu = (_monitored.TotalProcessorTime - _lastCpuTime).TotalMilliseconds /
                         ((now - _lastSample).TotalMilliseconds * Environment.ProcessorCount) * 100;
            _lastCpuTime = _monitored.TotalProcessorTime;
            _lastSample = now;
            double memMb = _monitored.WorkingSet64 / 1024.0 / 1024.0;
            _cpuHistory.Enqueue(cpu);
            while (_cpuHistory.Count > 80) _cpuHistory.Dequeue();

            PerfText.Text = $"CPU: {cpu,5:F1}%  |  内存: {memMb,7:F0} MB  |  预览帧率: {_previewFps} FPS";
            DrawPerfChart();
            _frames++;
        }
        catch { }
    }

    private void DrawPerfChart()
    {
        PerfChart.Children.Clear();
        double w = PerfChart.ActualWidth > 0 ? PerfChart.ActualWidth : 300;
        double h = PerfChart.ActualHeight;
        var pts = _cpuHistory.ToList();
        var poly = new Polyline
        {
            Stroke = FindResource("AccentBrush") as Brush,
            StrokeThickness = 1.6
        };
        for (int i = 0; i < pts.Count; i++)
            poly.Points.Add(new Point(w * i / 80.0, h - Math.Min(1, pts[i] / 100.0) * (h - 6) - 3));
        PerfChart.Children.Add(poly);
        var line = new Line { X1 = 0, X2 = w, Y1 = h / 2, Y2 = h / 2, Stroke = Brushes.DimGray, StrokeDashArray = new DoubleCollection { 2, 3 } };
        PerfChart.Children.Add(line);
        var lbl = new TextBlock { Text = "CPU% (50% 基线)", Foreground = Brushes.DimGray, FontSize = 9 };
        Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, 2);
        PerfChart.Children.Add(lbl);
    }
}
