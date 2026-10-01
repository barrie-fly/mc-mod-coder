using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using MCModCoder.Core;
using MCModCoder.Core.Models;
using MCModCoder.Core.Services;

namespace MCModCoder.Modules;

public partial class ShaderView : UserControl
{
    private DirectionalLight? _sun;
    private AmbientLight? _ambient;
    private readonly List<Model3D> _blocks = new();
    private readonly Random _rng = new(42);

    public ShaderView()
    {
        InitializeComponent();
        BuildScene();
        ApplyParams();
    }

    // ================= 场景 =================
    private void BuildScene()
    {
        var scene = new Model3DGroup();
        _ambient = new AmbientLight(Color.FromRgb(0x8F, 0xA6, 0xC8)) { };
        _sun = new DirectionalLight(Colors.White, new Vector3D(-0.6, -1, -0.5));
        scene.Children.Add(_ambient);
        scene.Children.Add(_sun);

        // 示例地形：起伏小山 + 一个发光方块
        for (int x = 0; x < 18; x++)
            for (int z = 0; z < 18; z++)
            {
                double h = 3 * Math.Sin(x * 0.4) * Math.Cos(z * 0.5) + 3 + _rng.NextDouble() * 0.6;
                int levels = Math.Max(1, (int)h);
                for (int y = 0; y < levels; y++)
                {
                    var color = y == levels - 1
                        ? Color.FromRgb((byte)(0x50 + x * 6), (byte)(0x8A + z * 4), 0x50)   // 草
                        : Color.FromRgb(0x6B, 0x54, 0x3B);                                   // 土
                    if (x == 9 && z == 9 && y == levels - 1) color = Color.FromRgb(0xFF, 0xD2, 0x60); // 发光方块
                    var mesh = CubeMesh();
                    var model = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color)))
                    { Transform = new TranslateTransform3D(x, y + 0.5, z) };
                    scene.Children.Add(model);
                    _blocks.Add(model);
                }
            }

        Viewport.Camera = new PerspectiveCamera
        { Position = new Point3D(24, 16, 24), LookDirection = new Vector3D(-14, -9, -14), FieldOfView = 50 };
        Viewport.Children.Add(new ModelVisual3D { Content = scene });
    }

    private static MeshGeometry3D CubeMesh()
    {
        var m = new MeshGeometry3D();
        double h = 0.5;
        m.Positions = new Point3DCollection
        {
            new(-h,h,-h), new(-h,h,h), new(h,h,h), new(h,h,-h),          // top
            new(-h,-h,-h), new(h,-h,-h), new(h,-h,h), new(-h,-h,h),      // bottom
            new(-h,-h,h), new(h,-h,h), new(h,h,h), new(-h,h,h),          // front
            new(h,-h,-h), new(-h,-h,-h), new(-h,h,-h), new(h,h,-h),      // back
            new(h,-h,h), new(h,-h,-h), new(h,h,-h), new(h,h,h),          // right
            new(-h,-h,-h), new(-h,-h,h), new(-h,h,h), new(-h,h,-h),      // left
        };
        var I = new Int32Collection();
        for (int f = 0; f < 6; f++) { int s = f * 4; I.Add(s); I.Add(s + 1); I.Add(s + 2); I.Add(s); I.Add(s + 2); I.Add(s + 3); }
        m.TriangleIndices = I;
        return m;
    }

    // ================= 参数应用 =================
    private Color HexColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private void ApplyParams()
    {
        if (_sun == null || _ambient == null) return;
        double az = SunAzimuth.Value * Math.PI / 180, el = SunAngle.Value * Math.PI / 180;
        var dir = new Vector3D(-Math.Cos(el) * Math.Sin(az), -Math.Sin(el), -Math.Cos(el) * Math.Cos(az));
        _sun.Direction = dir;
        // 光强：DirectionalLight 无 Intensity，用强度值缩放太阳光颜色表示
        double sunF = Math.Clamp(SunIntensity.Value * (Volumetric.IsChecked == true ? 1.08 : 1), 0, 1);
        var sunC = HexColor(SunColor.Text, Colors.White);
        _sun.Color = Color.FromRgb((byte)(sunC.R * sunF), (byte)(sunC.G * sunF), (byte)(sunC.B * sunF));
        // 阴影：WPF 无实时阴影，用阴影强度调制环境光亮度模拟暗部
        double shadowDim = 1.0 - ShadowStrength.Value * 0.55;
        var ambC = HexColor(AmbientColor.Text, Color.FromRgb(0x8F, 0xA6, 0xC8));
        _ambient.Color = Color.FromRgb(
            (byte)(ambC.R * AmbientIntensity.Value * shadowDim),
            (byte)(ambC.G * AmbientIntensity.Value * shadowDim),
            (byte)(ambC.B * AmbientIntensity.Value * shadowDim));

        // 雾效：雾密度驱动雾色叠加层（Viewport3D 无背景属性，底色由外层 Border 提供）
        var fog = HexColor(FogColor.Text, Color.FromRgb(0xA8, 0xC4, 0xE0));
        double f = Math.Clamp(FogDensity.Value * 14, 0, 1);
        FogOverlay.Background = new SolidColorBrush(Color.FromArgb((byte)(f * 60), fog.R, fog.G, fog.B));
    }

    private void Param_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyParams();
    private void Color_Changed(object sender, TextChangedEventArgs e)
    {
        if (_sun != null) ApplyParams();
    }

    // ================= 快照对比 =================
    private void SnapA_Click(object sender, RoutedEventArgs e) => TakeSnapshot(SnapA);
    private void SnapB_Click(object sender, RoutedEventArgs e) => TakeSnapshot(SnapB);

    private void TakeSnapshot(Image target)
    {
        try
        {
            var rtb = new RenderTargetBitmap(800, 520, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Viewport);
            target.Source = rtb;
        }
        catch (Exception ex) { MessageBox.Show("快照失败：" + ex.Message); }
    }

    // ================= 导出 =================
    private ShaderConfig Collect()
    {
        return new ShaderConfig
        {
            SunAngle = SunAngle.Value, SunAzimuth = SunAzimuth.Value, SunIntensity = SunIntensity.Value,
            SunColor = SunColor.Text, ShadowStrength = ShadowStrength.Value, ShadowSoftness = ShadowSoftness.Value,
            AmbientIntensity = AmbientIntensity.Value, AmbientColor = AmbientColor.Text,
            FogDensity = FogDensity.Value, FogColor = FogColor.Text, EmissiveBoost = Emissive.Value,
            VolumetricLight = Volumetric.IsChecked == true, TonemappingACES = Tonemap.IsChecked == true
        };
    }

    private void Apply(ShaderConfig c)
    {
        SunAngle.Value = c.SunAngle; SunAzimuth.Value = c.SunAzimuth; SunIntensity.Value = c.SunIntensity;
        SunColor.Text = c.SunColor; ShadowStrength.Value = c.ShadowStrength; ShadowSoftness.Value = c.ShadowSoftness;
        AmbientIntensity.Value = c.AmbientIntensity; AmbientColor.Text = c.AmbientColor;
        FogDensity.Value = c.FogDensity; FogColor.Text = c.FogColor; Emissive.Value = c.EmissiveBoost;
        Volumetric.IsChecked = c.VolumetricLight; Tonemap.IsChecked = c.TonemappingACES;
    }

    private void ExportGlsl_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "GLSL|*.glsl", FileName = "shader_config.glsl" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Collect().ToGlsl());
            AppContext.SetStatus($"GLSL 光影配置已导出：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCfg_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        var dir = AppContext.DirShaders(p);
        // 自动命名：按项目 ModID 与日期生成规范配置名，目录内自动去重
        var suggested = AutoNaming.EnsureUnique(dir, AutoNaming.ForDated("shader", p.ModId), ".mcshader.json");
        var dlg = new InputDialog("光影配置名称（已自动生成，可修改）", suggested) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(dlg.Value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (safe.Length == 0) safe = "MyShader";
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, safe + ".mcshader.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(Collect(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            AppContext.SetStatus($"光影配置已保存：{file}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadCfg_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        var dir = p != null ? AppContext.DirShaders(p) : null;
        var dlg = new OpenFileDialog
        { Filter = "光影配置|*.mcshader.json|JSON|*.json", InitialDirectory = dir != null && Directory.Exists(dir) ? dir : "" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var c = System.Text.Json.JsonSerializer.Deserialize<ShaderConfig>(File.ReadAllText(dlg.FileName));
            if (c != null) { Apply(c); AppContext.SetStatus("光影配置已加载。"); }
        }
        catch (Exception ex) { MessageBox.Show("加载失败：" + ex.Message); }
    }
}
