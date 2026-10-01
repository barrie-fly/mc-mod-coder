using MCModCoder.Core;
using MCModCoder.Core.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MCModCoder.Modules;

/// <summary>
/// 渲染编辑系统：粒子发射器实时预览 + 材质球预览 + 动画曲线编辑 + 性能建议。
/// 粒子/材质/动画配置以 JSON 打包保存到项目 workspace/effects 目录。
/// </summary>
public partial class RenderView : UserControl
{
    private readonly ParticleConfig _pcfg = new();
    private readonly MaterialConfig _mcfg = new();
    private readonly AnimationConfig _acfg = new();
    private bool _useCurve;

    private sealed class P { public double X, Y, VX, VY, Age, Life; public Ellipse? El; }
    private readonly List<P> _parts = new();
    private readonly Random _rng = new();
    private double _emitAcc;
    private bool _playing = true;
    private double _markerT = -1;
    private double _fps;
    private int _frames;
    private DateTime _fpsT = DateTime.UtcNow;
    private readonly Dictionary<string, SolidColorBrush> _brushCache = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public RenderView()
    {
        InitializeComponent();
        ApplyParticle();
        ApplyMaterial();
        ApplyAnim();
        RefreshKeys();
        DrawCurve();
        SampleVal.Text = _acfg.Sample(SampleSlider.Value).ToString("0.00");
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Loaded += (_, _) => { if (_playing) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    // ---------- 主循环 ----------
    private void Tick()
    {
        if (_playing) Simulate(1.0 / 60.0);
        _frames++;
        var now = DateTime.UtcNow;
        if ((now - _fpsT).TotalMilliseconds >= 500)
        {
            _fps = _frames / (now - _fpsT).TotalSeconds;
            _frames = 0;
            _fpsT = now;
            FpsText.Text = $"FPS：{_fps:0}";
            MemText.Text = $"内存：{GC.GetTotalMemory(false) / 1048576.0:0} MB";
            if (PerfPanel.Visibility == Visibility.Visible) PerfText.Text = BuildPerf();
        }
    }

    private void Simulate(double dt)
    {
        double w = SimCanvas.ActualWidth, h = SimCanvas.ActualHeight;
        if (w < 10 || h < 10) return;

        _emitAcc += _pcfg.RatePerSecond * dt;
        int budget = 240; // 单帧发射上限，防止速率突增卡顿
        while (_emitAcc >= 1 && _parts.Count < _pcfg.MaxCount && budget-- > 0)
        {
            _emitAcc -= 1;
            Spawn(w, h);
        }
        if (_parts.Count >= _pcfg.MaxCount) _emitAcc = Math.Min(_emitAcc, 1.0);

        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                if (p.El != null) SimCanvas.Children.Remove(p.El);
                _parts.RemoveAt(i);
                continue;
            }
            p.VY += _pcfg.Gravity * 700 * dt;
            p.X += p.VX * dt;
            p.Y += p.VY * dt;

            double t = p.Age / p.Life;
            double sz = Math.Max(0.6,
                (_pcfg.SizeStart + (_pcfg.SizeEnd - _pcfg.SizeStart) * t) * (_useCurve ? _acfg.Sample(t) : 1.0));
            var el = p.El ??= NewEllipse();
            el.Width = el.Height = sz;
            Canvas.SetLeft(el, p.X - sz / 2);
            Canvas.SetTop(el, p.Y - sz / 2);
            var (r, g, b) = ColorUtil.Lerp(_pcfg.ColorFrom, _pcfg.ColorTo, t);
            double a = (_pcfg.Additive ? 0.9 : 0.95) * (1 - t * 0.85);
            el.Fill = BrushFor(r, g, b, a);
        }
        CountText.Text = $"粒子数：{_parts.Count}";
    }

    private void Spawn(double w, double h)
    {
        double ox = w / 2, oy = h * 0.72;
        var p = new P { Life = _pcfg.Lifetime * (0.7 + _rng.NextDouble() * 0.6) };
        double sp = _pcfg.Speed * 150 * (0.6 + _rng.NextDouble() * 0.8);
        switch (_pcfg.Shape)
        {
            case "cone":
            {
                double ang = -Math.PI / 2 + (_rng.NextDouble() - 0.5) * _pcfg.Spread * Math.PI;
                p.X = ox; p.Y = oy;
                p.VX = Math.Cos(ang) * sp; p.VY = Math.Sin(ang) * sp;
                break;
            }
            case "ring":
            {
                double ang = _rng.NextDouble() * Math.PI * 2;
                p.X = ox + Math.Cos(ang) * 46; p.Y = oy + Math.Sin(ang) * 14;
                p.VX = Math.Cos(ang) * sp; p.VY = Math.Sin(ang) * sp * 0.5;
                break;
            }
            default: // point
            {
                double ang = _rng.NextDouble() * Math.PI * 2;
                p.X = ox; p.Y = oy;
                p.VX = Math.Cos(ang) * sp; p.VY = Math.Sin(ang) * sp;
                break;
            }
        }
        _parts.Add(p);
    }

    private Ellipse NewEllipse()
    {
        var el = new Ellipse { IsHitTestVisible = false };
        SimCanvas.Children.Add(el);
        return el;
    }

    private Brush BrushFor(byte r, byte g, byte b, double alpha)
    {
        byte a = (byte)(Math.Clamp(alpha, 0, 1) * 255);
        string key = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        if (_brushCache.TryGetValue(key, out var br)) return br;
        if (_brushCache.Count > 8192) _brushCache.Clear();
        var s = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        s.Freeze();
        _brushCache[key] = s;
        return s;
    }

    // ---------- 参数同步 ----------
    private void CollectParticle()
    {
        // XAML 初始化期间事件先于控件就绪：任一关联控件未加载完则跳过
        if (PName == null || Rate == null || Life == null || SpeedS == null || Grav == null
            || SpreadS == null || CFrom == null || CTo == null || SzA == null || SzB == null
            || Additive == null || MaxC == null) return;
        _pcfg.Name = string.IsNullOrWhiteSpace(PName.Text) ? "MyEffect" : PName.Text.Trim();
        _pcfg.RatePerSecond = Rate.Value;
        _pcfg.Lifetime = Life.Value;
        _pcfg.Speed = SpeedS.Value;
        _pcfg.Gravity = Grav.Value;
        _pcfg.Spread = SpreadS.Value;
        _pcfg.ColorFrom = CFrom.Text;
        _pcfg.ColorTo = CTo.Text;
        _pcfg.SizeStart = SzA.Value;
        _pcfg.SizeEnd = SzB.Value;
        _pcfg.Additive = Additive.IsChecked == true;
        _pcfg.MaxCount = (int)MaxC.Value;
        _pcfg.Shape = (shapeCone.IsChecked == true) ? "cone" : (shapePoint.IsChecked == true) ? "point" : "ring";
    }

    private void CollectMaterial()
    {
        // XAML 初始化期间事件先于控件就绪：任一关联控件未加载完则跳过
        if (MName == null || Metal == null || Rough == null || Emis == null || BaseCol == null
            || NMap == null || SMap == null || layerSolid == null || layerCutout == null) return;
        _mcfg.Name = string.IsNullOrWhiteSpace(MName.Text) ? "MyMaterial" : MName.Text.Trim();
        _mcfg.Metalness = Metal.Value;
        _mcfg.Roughness = Rough.Value;
        _mcfg.Emission = Emis.Value;
        _mcfg.BaseColor = BaseCol.Text;
        _mcfg.UseNormalMap = NMap.IsChecked == true;
        _mcfg.UseSpecularMap = SMap.IsChecked == true;
        _mcfg.RenderLayer = (layerSolid.IsChecked == true) ? "solid"
                          : (layerCutout.IsChecked == true) ? "cutout" : "translucent";
    }

    private void CollectAnim()
    {
        if (AName != null)
            _acfg.Name = string.IsNullOrWhiteSpace(AName.Text) ? "SizeOverLife" : AName.Text.Trim();
        if (UseSizeCurve != null)
            _useCurve = UseSizeCurve.IsChecked == true;
    }

    private void ApplyParticle()
    {
        PName.Text = _pcfg.Name;
        Rate.Value = _pcfg.RatePerSecond;
        Life.Value = _pcfg.Lifetime;
        SpeedS.Value = _pcfg.Speed;
        Grav.Value = _pcfg.Gravity;
        SpreadS.Value = _pcfg.Spread;
        CFrom.Text = _pcfg.ColorFrom;
        CTo.Text = _pcfg.ColorTo;
        SzA.Value = _pcfg.SizeStart;
        SzB.Value = _pcfg.SizeEnd;
        Additive.IsChecked = _pcfg.Additive;
        MaxC.Value = _pcfg.MaxCount;
        SelectRadio(_pcfg.Shape, shapeCone, shapePoint, shapeRing);
    }

    private void ApplyMaterial()
    {
        MName.Text = _mcfg.Name;
        Metal.Value = _mcfg.Metalness;
        Rough.Value = _mcfg.Roughness;
        Emis.Value = _mcfg.Emission;
        BaseCol.Text = _mcfg.BaseColor;
        NMap.IsChecked = _mcfg.UseNormalMap;
        SMap.IsChecked = _mcfg.UseSpecularMap;
        SelectRadio(_mcfg.RenderLayer, layerSolid, layerCutout, layerTrans);
        UpdateSphere();
    }

    private void ApplyAnim()
    {
        AName.Text = _acfg.Name;
        UseSizeCurve.IsChecked = _useCurve;
    }

    private static void SelectRadio(string tag, params RadioButton[] rs)
    {
        foreach (var r in rs)
            if ((r.Tag as string) == tag) { r.IsChecked = true; return; }
    }

    // ---------- 材质球 ----------
    private void UpdateSphere()
    {
        if (SphMain == null || SphGlow == null || MatLabel == null) return;   // XAML 初始化期间事件先于控件就绪
        var (r, g, b) = ColorUtil.ParseHex(_mcfg.BaseColor);
        double spec = Math.Clamp((1 - _mcfg.Roughness) * (0.55 + 0.45 * _mcfg.Metalness) + _mcfg.Emission * 0.3, 0, 1);
        static byte Scale(byte c, double f) => (byte)Math.Clamp(c * f, 0, 255);
        static byte MixWhite(byte c, double f) => (byte)Math.Clamp(c + (255 - c) * f, 0, 255);

        var stops = new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(MixWhite(r, spec), MixWhite(g, spec), MixWhite(b, spec)), 0.0),
            new GradientStop(Color.FromRgb(Scale(r, 0.95), Scale(g, 0.95), Scale(b, 0.95)), 0.35),
            new GradientStop(Color.FromRgb(Scale(r, 0.45), Scale(g, 0.45), Scale(b, 0.45)), 0.7),
            new GradientStop(Color.FromRgb(Scale(r, 0.22), Scale(g, 0.22), Scale(b, 0.22)), 1.0),
        };
        SphMain.Fill = new RadialGradientBrush(stops)
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        var glow = new SolidColorBrush(Color.FromRgb(r, g, b));
        glow.Freeze();
        SphGlow.Fill = glow;
        SphGlow.Opacity = Math.Clamp(_mcfg.Emission * 0.6, 0, 1);
        MatLabel.Text = $"{_mcfg.Name} · 金属 {_mcfg.Metalness:0.00} / 粗糙 {_mcfg.Roughness:0.00} / 自发光 {_mcfg.Emission:0.00}";
    }

    // ---------- 动画曲线 ----------
    private void RefreshKeys()
    {
        _acfg.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        KeyList.ItemsSource = _acfg.Keys
            .Select((k, i) => $"#{i}   t = {k.Time:0.00}    v = {k.Value:0.00}").ToList();
    }

    private void DrawCurve()
    {
        var c = CurveCanvas;
        if (c == null) return;
        c.Children.Clear();
        double w = c.Width, h = c.Height;
        for (int i = 0; i <= 4; i++)
        {
            double x = w * i / 4.0, y = h * i / 4.0;
            c.Children.Add(MakeLine(0, y, w, y));
            c.Children.Add(MakeLine(x, 0, x, h));
        }
        var pts = _acfg.Keys.OrderBy(k => k.Time)
            .Select(k => new Point(k.Time * w, (1 - Math.Clamp(k.Value, 0, 1)) * h)).ToList();
        if (pts.Count >= 2)
        {
            var pl = new Polyline
            {
                StrokeThickness = 2,
                Stroke = (TryFindResource("AccentBrush") as Brush) ?? Brushes.CornflowerBlue,
            };
            foreach (var pt in pts) pl.Points.Add(pt);
            c.Children.Add(pl);
        }
        foreach (var pt in pts)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
            Canvas.SetLeft(dot, pt.X - 3.5);
            Canvas.SetTop(dot, pt.Y - 3.5);
            c.Children.Add(dot);
        }
        if (_markerT >= 0)
        {
            double mx = _markerT * w;
            c.Children.Add(MakeLine(mx, 0, mx, h, stroke: true));
            double v = Math.Clamp(_acfg.Sample(_markerT), 0, 1);
            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = (TryFindResource("YellowBrush") as Brush) ?? Brushes.Orange,
            };
            Canvas.SetLeft(dot, mx - 4.5);
            Canvas.SetTop(dot, (1 - v) * h - 4.5);
            c.Children.Add(dot);
        }
    }

    private static System.Windows.Shapes.Line MakeLine(double x1, double y1, double x2, double y2, bool stroke = false)
    {
        var (r, g, b) = stroke ? ((byte)0xE8, (byte)0xC4, (byte)0x68) : ((byte)0x2A, (byte)0x2D, (byte)0x33);
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return new System.Windows.Shapes.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = br, StrokeThickness = 1 };
    }

    // ---------- 性能建议 ----------
    private string BuildPerf()
    {
        var tips = new List<string>();
        if (_pcfg.MaxCount > 800)
            tips.Add("• 最大粒子数 > 800：中端 GPU 上可能掉帧，建议 ≤ 600。");
        if (_pcfg.RatePerSecond * _pcfg.Lifetime > _pcfg.MaxCount)
            tips.Add("• 发射速率 × 生命周期超过最大数量，实际粒子会被截断：调大 MaxCount 或降低速率。");
        if (_mcfg.RenderLayer == "translucent" && !_pcfg.Additive)
            tips.Add("• 半透明层 + 非加法混合需要逐粒子排序，建议改用 cutout 或开启加法混合。");
        if (_mcfg.UseNormalMap && _mcfg.UseSpecularMap)
            tips.Add("• 法线 + 高光双贴图采样开销较高，低端显卡建议关闭高光图。");
        if (_fps > 0 && _fps < 45)
            tips.Add($"• 当前预览帧率 {_fps:0} FPS 偏低：优先降低 MaxCount / 发射速率。");
        if (tips.Count == 0)
            tips.Add("• 当前配置性能开销较低，无明显优化点。");
        tips.Add("");
        tips.Add("通用建议：粒子尺寸越小越省填充率；加法混合适合火焰/魔力，烟尘用 alpha 混合。");
        return string.Join("\n", tips);
    }

    // ---------- 保存 / 加载 ----------
    private sealed class EffectBundle
    {
        public ParticleConfig Particle { get; set; } = new();
        public MaterialConfig Material { get; set; } = new();
        public AnimationConfig Animation { get; set; } = new();
    }

    private string BundleJson() => JsonSerializer.Serialize(new EffectBundle
    {
        Particle = _pcfg,
        Material = _mcfg,
        Animation = _acfg,
    }, new JsonSerializerOptions { WriteIndented = true });

    private static void CopyProps<T>(T src, T dst)
    {
        foreach (var p in typeof(T).GetProperties())
            if (p.CanRead && p.CanWrite) p.SetValue(dst, p.GetValue(src));
    }

    // ---------- 事件 ----------
    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (ParticlePanel == null || TabParticles == null) return;
        ParticlePanel.Visibility = TabParticles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MaterialPanel.Visibility = TabMaterial.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AnimPanel.Visibility = TabAnim.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void P_Changed(object sender, RoutedEventArgs e) => CollectParticle();
    private void M_Changed(object sender, RoutedEventArgs e) { CollectMaterial(); UpdateSphere(); }
    private void A_Changed(object sender, RoutedEventArgs e) => CollectAnim();

    private void Sample_Changed(object sender, RoutedEventArgs e)
    {
        if (SampleSlider == null || SampleVal == null) return;   // XAML 初始化期间事件先于控件就绪
        _markerT = SampleSlider.Value;
        SampleVal.Text = _acfg.Sample(_markerT).ToString("0.00");
        DrawCurve();
    }

    private void KeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = KeyList.SelectedIndex;
        if (i >= 0 && i < _acfg.Keys.Count)
        {
            KTime.Text = _acfg.Keys[i].Time.ToString("0.##", CultureInfo.InvariantCulture);
            KValue.Text = _acfg.Keys[i].Value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(KTime.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
            !double.TryParse(KValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            MessageBox.Show("请输入 0~1 之间的小数（时间 t / 取值 v）。", "添加关键帧",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _acfg.Keys.Add(new AnimKeyframe { Time = Math.Clamp(t, 0, 1), Value = Math.Clamp(v, 0, 1) });
        RefreshKeys();
        DrawCurve();
    }

    private void DelKey_Click(object sender, RoutedEventArgs e)
    {
        int i = KeyList.SelectedIndex;
        if (i < 0 || _acfg.Keys.Count <= 2)
        {
            AppContext.SetStatus("至少保留两个关键帧，且需先在列表中选中一项。");
            return;
        }
        _acfg.Keys.RemoveAt(i);
        RefreshKeys();
        DrawCurve();
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        _playing = !_playing;
        PlayBtn.Content = _playing ? "⏸ 暂停" : "▶ 播放";
        if (_playing) _timer.Start(); else _timer.Stop();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SimCanvas.Children.Clear();
        _parts.Clear();
        _emitAcc = 0;
    }

    private void Perf_Click(object sender, RoutedEventArgs e)
    {
        bool show = PerfPanel.Visibility != Visibility.Visible;
        PerfPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) PerfText.Text = BuildPerf();
    }

    private void SaveEff_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "保存效果",
            Filter = "工作台效果 (*.mceffect.json)|*.mceffect.json|JSON (*.json)|*.json",
            FileName = _pcfg.Name + ".mceffect.json",
        };
        var proj = AppContext.CurrentProject;
        if (proj != null)
        {
            Directory.CreateDirectory(AppContext.DirEffects(proj));
            dlg.InitialDirectory = AppContext.DirEffects(proj);
        }
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, BundleJson());
            AppContext.SetStatus($"效果已保存：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "渲染编辑", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadEff_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "加载效果",
            Filter = "工作台效果 (*.mceffect.json)|*.mceffect.json|JSON (*.json)|*.json",
        };
        var proj = AppContext.CurrentProject;
        if (proj != null)
        {
            Directory.CreateDirectory(AppContext.DirEffects(proj));
            dlg.InitialDirectory = AppContext.DirEffects(proj);
        }
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bundle = JsonSerializer.Deserialize<EffectBundle>(File.ReadAllText(dlg.FileName));
            if (bundle == null) throw new InvalidDataException("文件内容为空或格式不正确。");
            CopyProps(bundle.Particle, _pcfg);
            CopyProps(bundle.Material, _mcfg);
            _acfg.Name = bundle.Animation.Name;
            _acfg.Keys = bundle.Animation.Keys;
            ApplyParticle();
            ApplyMaterial();
            ApplyAnim();
            RefreshKeys();
            DrawCurve();
            AppContext.SetStatus($"效果已加载：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载失败：{ex.Message}", "渲染编辑", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BundleJson());
            AppContext.SetStatus("已复制效果 JSON（粒子 + 材质 + 动画）到剪贴板。");
        }
        catch { }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        var proj = AppContext.CurrentProject;
        string dir = proj != null
            ? AppContext.DirEffects(proj)
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MCModCoder");
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开目录失败：" + ex.Message + $"\n目录：{dir}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
