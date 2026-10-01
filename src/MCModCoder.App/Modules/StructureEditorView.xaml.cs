using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using MCModCoder.Core.Models;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Services;
using Microsoft.Win32;

namespace MCModCoder.Modules;

public partial class StructureEditorView : UserControl
{
    private readonly StructureData _struct = new() { Name = "NewStructure", SizeX = 16, SizeY = 16, SizeZ = 16 };
    private StructureData? _clipboard;
    private CameraRig? _rig;
    private string _preset = "overview";
    private bool _syncingPreset;
    private Point _lastPos;
    private bool _orbiting, _painting, _panning;
    private bool _paintErase;

    public StructureEditorView()
    {
        InitializeComponent();
        BuildDefaultScene();
        RefreshPalette();
        UpdateInfo();
        AppContext.StructurePushed += s =>
        {
            CopyInto(s);
            AppContext.SetStatus($"已接收结构「{s.Name}」（来自地牢编辑器）。");
        };
    }

    private void CopyInto(StructureData s)
    {
        _struct.Name = s.Name; _struct.Palette = new List<string>(s.Palette);
        _struct.Blocks = new Dictionary<string, int>(s.Blocks);
        _struct.SizeX = s.SizeX; _struct.SizeY = s.SizeY; _struct.SizeZ = s.SizeZ;
        SizeXBox.Text = s.SizeX.ToString(); SizeYBox.Text = s.SizeY.ToString(); SizeZBox.Text = s.SizeZ.ToString();
        RefreshPalette(); RebuildBlocks(); UpdateInfo();
        // 结构尺寸/内容变化 → 平滑重新取景（保持当前角度）
        _rig?.AnimateTo(_rig.Theta, _rig.Phi, FitDistance(), StructCenter(), 600);
    }

    // ================= 场景 =================
    private void BuildDefaultScene()
    {
        var scene = new Model3DGroup();

        // 灯光
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x64, 0x6C)));
        var sun = new DirectionalLight(Color.FromRgb(0xFF, 0xF2, 0xD9), new Vector3D(-0.6, -1, -0.4));
        scene.Children.Add(sun);

        // 地面（棋盘格）
        var ground = new GeometryModel3D(MakePlaneMesh(_struct.SizeX * 2), MakeCheckerMaterial())
        { Transform = new TranslateTransform3D(_struct.SizeX, -0.02, _struct.SizeZ) };
        scene.Children.Add(ground);

        // 坐标轴
        scene.Children.Add(MakeAxis(new Point3D(0, 0, 0), new Point3D(_struct.SizeX + 2, 0, 0), Colors.IndianRed));
        scene.Children.Add(MakeAxis(new Point3D(0, 0, 0), new Point3D(0, 0, _struct.SizeZ + 2), Colors.LightGreen));
        scene.Children.Add(MakeAxis(new Point3D(0, 0, 0), new Point3D(0, _struct.SizeY + 2, 0), Colors.CornflowerBlue));

        // 相机装置（含预设动画与自由视角）
        InitCamera();
        Viewport.Children.Add(new ModelVisual3D { Content = scene });
        var blocksRoot = new ModelVisual3D();
        Viewport.Children.Add(blocksRoot);

        // 半透明边界框
        AddBoundaryBox(scene);
        RebuildBlocks();
    }

    private static MeshGeometry3D MakePlaneMesh(double size)
    {
        var m = new MeshGeometry3D();
        double half = size / 2;
        m.Positions = new Point3DCollection
        {
            new(-half,0,-half), new(half,0,-half), new(half,0,half), new(-half,0,half)
        };
        m.TriangleIndices = new Int32Collection { 0, 2, 1, 0, 3, 2 };
        m.TextureCoordinates = new PointCollection { new(0,0), new(1,0), new(1,1), new(0,1) };
        m.Normals = new Vector3DCollection { new(0,1,0), new(0,1,0), new(0,1,0), new(0,1,0) };
        return m;
    }

    private static Material MakeCheckerMaterial()
    {
        int n = 64;
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(n, n, 96, 96, PixelFormats.Bgr24, null);
        int stride = n * 3;
        var px = new byte[stride * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool dark = (x / 8 + y / 8) % 2 == 0;
                int i = y * stride + x * 3;
                px[i] = dark ? (byte)0x3C : (byte)0x2A;
                px[i + 1] = dark ? (byte)0x40 : (byte)0x2D;
                px[i + 2] = dark ? (byte)0x46 : (byte)0x31;
            }
        bmp.WritePixels(new Int32Rect(0, 0, n, n), px, stride, 0);
        return new DiffuseMaterial(new ImageBrush(bmp) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.FlipY });
    }

    private static Model3D MakeAxis(Point3D a, Point3D b, Color color)
    {
        var mesh = new MeshGeometry3D();
        var dir = b - a;
        double len = dir.Length; dir.Normalize();
        var perp = Vector3D.CrossProduct(dir, new Vector3D(0, 1, 0));
        if (perp.LengthSquared < 0.01) perp = new Vector3D(1, 0, 0);
        perp.Normalize();
        double w = 0.04;
        var p1 = a + perp * w; var p2 = a - perp * w;
        var p3 = b - perp * w; var p4 = b + perp * w;
        mesh.Positions = new Point3DCollection { p1, p2, p3, p4 };
        mesh.TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 };
        return new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color)));
    }

    private void AddBoundaryBox(Model3DGroup scene)
    {
        var c = new Color { A = 40, R = 0x4C, G = 0x8D, B = 0xFF };
        var mat = new DiffuseMaterial(new SolidColorBrush(c));
        double sx = _struct.SizeX, sy = _struct.SizeY, sz = _struct.SizeZ;
        scene.Children.Add(new GeometryModel3D(BoxMesh(sx, 0.05, sz), mat) { Transform = new TranslateTransform3D(sx / 2, -0.05, sz / 2) });
    }

    // ---- 方块网格 ----
    private static MeshGeometry3D BoxMesh(double sx, double sy, double sz)
    {
        double hx = sx / 2, hy = sy / 2, hz = sz / 2;
        var m = new MeshGeometry3D();
        var P = new Point3DCollection();
        var N = new Vector3DCollection();
        var I = new Int32Collection();
        void Face(Vector3D n, Point3D a, Point3D b, Point3D c, Point3D d)
        {
            int s = P.Count;
            P.Add(a); P.Add(b); P.Add(c); P.Add(d);
            N.Add(n); N.Add(n); N.Add(n); N.Add(n);
            I.Add(s); I.Add(s + 1); I.Add(s + 2); I.Add(s); I.Add(s + 2); I.Add(s + 3);
        }
        Face(new(0, 1, 0),  new(-hx,hy,-hz), new(-hx,hy,hz), new(hx,hy,hz), new(hx,hy,-hz));
        Face(new(0,-1,0),  new(-hx,-hy,-hz), new(hx,-hy,-hz), new(hx,-hy,hz), new(-hx,-hy,hz));
        Face(new(0,0,1),   new(-hx,-hy,hz), new(hx,-hy,hz), new(hx,hy,hz), new(-hx,hy,hz));
        Face(new(0,0,-1),  new(-hx,-hy,-hz), new(-hx,hy,-hz), new(hx,hy,-hz), new(hx,-hy,-hz));
        Face(new(1,0,0),   new(hx,-hy,-hz), new(hx,hy,-hz), new(hx,hy,hz), new(hx,-hy,hz));
        Face(new(-1,0,0),  new(-hx,-hy,-hz), new(-hx,-hy,hz), new(-hx,hy,hz), new(-hx,hy,-hz));
        m.Positions = P; m.Normals = N; m.TriangleIndices = I;
        return m;
    }

    private static Color BlockColor(string blockId)
    {
        uint h = 2166136261;
        foreach (var c in blockId) { h ^= c; h *= 16777619; }
        byte r = (byte)((h >> 16) & 0x7F), g = (byte)((h >> 8) & 0x7F), b = (byte)(h & 0x7F);
        return Color.FromRgb((byte)(r + 0x55), (byte)(g + 0x55), (byte)(b + 0x55));
    }

    private void RebuildBlocks()
    {
        var root = Viewport.Children.OfType<ModelVisual3D>().ElementAtOrDefault(1);
        if (root == null) return;
        var group = new Model3DGroup();
        foreach (var (key, idx) in _struct.Blocks)
        {
            if (idx <= 0 || idx >= _struct.Palette.Count) continue;
            if (!TryParseKey(key, out int x, out int y, out int z)) continue;   // 跳过损坏数据，不中断渲染
            var color = BlockColor(_struct.Palette[idx]);
            var mesh = BoxMesh(1, 1, 1);
            var model = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(color)))
            {
                Transform = new TranslateTransform3D(x + 0.5, y + 0.5, z + 0.5)
            };
            group.Children.Add(model);
        }
        root.Content = group;
    }

    // ================= 相机 =================
    private Point3D StructCenter() => new(_struct.SizeX / 2.0, _struct.SizeY / 2.0, _struct.SizeZ / 2.0);

    /// <summary>按结构尺寸计算合适的观察距离</summary>
    private double FitDistance() =>
        Math.Clamp(Math.Max(_struct.SizeX, Math.Max(_struct.SizeY, _struct.SizeZ)) * 1.7 + 6,
            10, CameraRig.MaxDistance);

    private void InitCamera()
    {
        _rig = new CameraRig(Viewport, StructCenter());
        _rig.JumpTo(Math.PI / 4, Math.PI / 4.4, FitDistance(), StructCenter());
        // 用户手动旋转/平移/缩放 → 自动切到"自由"视角单选
        _rig.UserInteracted += () =>
        {
            if (_preset == "free" || _syncingPreset) return;
            _syncingPreset = true;
            PresetFree.IsChecked = true;
            _syncingPreset = false;
            _preset = "free";
        };
    }

    private void GotoOverview() => _rig!.AnimateTo(Math.PI / 4, Math.PI / 4.4, FitDistance(), StructCenter());

    /// <summary>视角预设切换（全景/俯视/侧视/正视/自由）——统一缓动动画过渡，避免生硬跳变</summary>
    private void Preset_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingPreset || _rig == null) return;   // XAML 初始化期间事件先于相机装置就绪
        if (sender is not RadioButton rb) return;
        _preset = rb.Tag as string ?? "overview";
        switch (_preset)
        {
            case "free":
                return;   // 自由模式：相机保持现状，交由用户操控
            case "overview":
                GotoOverview();
                break;
            case "top":     // 俯视：自上而下，方位角不变以保持画面稳定
                _rig.AnimateTo(_rig.Theta, 0.10, FitDistance(), StructCenter());
                break;
            case "side":    // 侧视：自 +X 方向
                _rig.AnimateTo(Math.PI / 2, 0.30, FitDistance(), StructCenter());
                break;
            case "front":   // 正视：自 +Z 方向
                _rig.AnimateTo(0, 0.30, FitDistance(), StructCenter());
                break;
        }
        AppContext.SetStatus($"视角：{rb.Content}（平滑过渡）。");
    }

    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        if (_rig == null) return;
        if (PresetOverview.IsChecked == true)
        {
            _preset = "overview";
            GotoOverview();
        }
        else
        {
            PresetOverview.IsChecked = true;   // 触发 Preset_Changed 完成动画
        }
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _rig?.Zoom(e.Delta / 60.0);
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastPos = e.GetPosition(Viewport);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            if (shift) _panning = true;   // Shift+中键 平移
            else _orbiting = true;
            Viewport.CaptureMouse();
            return;
        }
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            _panning = true;              // Alt+左键 平移
            Viewport.CaptureMouse();
            return;
        }
        if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
        {
            _painting = true;
            _paintErase = e.ChangedButton == MouseButton.Right;
            HandlePaint(e);
        }
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Viewport);
        if (_orbiting)
        {
            _rig?.Orbit(-(pos.X - _lastPos.X) * 0.01, (pos.Y - _lastPos.Y) * 0.01);
            _lastPos = pos;
            return;
        }
        if (_panning)
        {
            _rig?.Pan(pos.X - _lastPos.X, pos.Y - _lastPos.Y);
            _lastPos = pos;
            return;
        }
        if (_painting && e.LeftButton == MouseButtonState.Pressed)
        {
            HandlePaint(e);
        }
        _lastPos = pos;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _orbiting = false; _painting = false; _panning = false;
        Viewport.ReleaseMouseCapture();
    }

    // ================= 放置 / 删除 =================
    private void HandlePaint(MouseEventArgs e)
    {
        var hit = HitTestCell(e.GetPosition(Viewport));
        if (hit == null) return;
        var (cell, place) = hit.Value;
        var (x, y, z) = cell;
        if (x < 0 || y < 0 || z < 0 || x >= _struct.SizeX || y >= _struct.SizeY || z >= _struct.SizeZ) return;
        if (_paintErase)
        {
            if (_struct.Get(x, y, z) != 0) { _struct.Set(x, y, z, 0); RebuildBlocks(); UpdateInfo(); }
        }
        else
        {
            int idx = PaletteCombo.SelectedIndex + 1;
            if (idx <= 0) return;
            if (_struct.Get(x, y, z) != idx) { _struct.Set(x, y, z, idx); RebuildBlocks(); UpdateInfo(); }
        }
    }

    /// <summary>命中测试：返回目标格（放置）或命中格（删除）</summary>
    private ((int x, int y, int z), bool place)? HitTestCell(Point pos)
    {
        var result = VisualTreeHelper.HitTest(Viewport, pos) as RayHitTestResult;
        if (result is not RayMeshGeometry3DHitTestResult meshHit) return null;

        // 地面命中 → 放置在地面上方一格
        if (result.VisualHit is ModelVisual3D && !(meshHit.MeshHit is MeshGeometry3D mg && mg.Positions.Count > 20))
        {
            var p = result.PointHit;
            int gx = (int)Math.Floor(p.X), gz = (int)Math.Floor(p.Z);
            return ((gx, 0, gz), true);
        }

        // 方块命中：由模型 Tag 找到中心
        var model = meshHit.ModelHit as GeometryModel3D;
        if (model?.Transform is TranslateTransform3D t)
        {
            int cx = (int)Math.Round(t.OffsetX - 0.5);
            int cy = (int)Math.Round(t.OffsetY - 0.5);
            int cz = (int)Math.Round(t.OffsetZ - 0.5);
            if (_paintErase) return ((cx, cy, cz), false);

            // 放置：选偏移最大的面
            var d = result.PointHit - new Point3D(cx + 0.5, cy + 0.5, cz + 0.5);
            var dir = Math.Abs(d.X) > Math.Abs(d.Y) && Math.Abs(d.X) > Math.Abs(d.Z) ? (Math.Sign(d.X), 0, 0)
                    : Math.Abs(d.Y) > Math.Abs(d.Z) ? (0, Math.Sign(d.Y), 0)
                    : (0, 0, Math.Sign(d.Z));
            return ((cx + dir.Item1, cy + dir.Item2, cz + dir.Item3), true);
        }
        return null;
    }

    // ================= 调色板 =================
    private void RefreshPalette()
    {
        PaletteCombo.Items.Clear();
        for (int i = 1; i < _struct.Palette.Count; i++)
            PaletteCombo.Items.Add(Short(_struct.Palette[i]));
        if (PaletteCombo.SelectedIndex < 0 && PaletteCombo.Items.Count > 0)
            PaletteCombo.SelectedIndex = 0;
    }

    private static string Short(string id) => id.Replace("minecraft:", "");

    /// <summary>解析 "x,y,z" 键；损坏数据返回 false</summary>
    private static bool TryParseKey(string key, out int x, out int y, out int z)
    {
        x = y = z = 0;
        if (string.IsNullOrEmpty(key)) return false;
        var p = key.Split(',');
        if (p.Length != 3) return false;
        return int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && int.TryParse(p[2], out z);
    }

    /// <summary>净化文件名（替换非法字符）</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string((name ?? "structure").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "structure" : s;
    }

    private void AddPalette_Click(object sender, RoutedEventArgs e)
    {
        var id = CustomBlockBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!id.Contains(':')) id = "minecraft:" + id;
        if (_struct.Palette.Contains(id)) { PaletteCombo.SelectedItem = _struct.Palette.IndexOf(id) - 1; return; }
        _struct.Palette.Add(id);
        RefreshPalette();
        PaletteCombo.SelectedIndex = Math.Max(0, Math.Min(PaletteCombo.Items.Count - 1, _struct.Palette.Count - 2));
        UpdateInfo();
    }

    // ================= 操作 =================
    private void RotY_Click(object sender, RoutedEventArgs e) { _struct.RotateY(); RebuildBlocks(); UpdateInfo(); }
    private void RotX_Click(object sender, RoutedEventArgs e) { _struct.RotateX(); RebuildBlocks(); UpdateInfo(); }
    private void RotZ_Click(object sender, RoutedEventArgs e) { _struct.RotateZ(); RebuildBlocks(); UpdateInfo(); }
    private void MirrorX_Click(object sender, RoutedEventArgs e) { _struct.MirrorX(); RebuildBlocks(); UpdateInfo(); }
    private void MirrorZ_Click(object sender, RoutedEventArgs e) { _struct.MirrorZ(); RebuildBlocks(); UpdateInfo(); }

    private void CopyStruct_Click(object sender, RoutedEventArgs e)
    {
        _clipboard = _struct.Clone();
        AppContext.SetStatus("结构已复制到剪贴板。");
    }

    private void PasteStruct_Click(object sender, RoutedEventArgs e)
    {
        if (_clipboard == null) { MessageBox.Show("剪贴板中没有结构。"); return; }
        _struct.Blocks = new Dictionary<string, int>(_clipboard.Blocks);
        _struct.SizeX = Math.Max(_struct.SizeX, _clipboard.SizeX);
        _struct.SizeY = Math.Max(_struct.SizeY, _clipboard.SizeY);
        _struct.SizeZ = Math.Max(_struct.SizeZ, _clipboard.SizeZ);
        foreach (var id in _clipboard.Palette.Where(id => !_struct.Palette.Contains(id)))
            _struct.Palette.Add(id);
        RebuildBlocks(); UpdateInfo();
        AppContext.SetStatus("结构已粘贴（合并）。");
    }

    private void ClearStruct_Click(object sender, RoutedEventArgs e)
    {
        if (_struct.Blocks.Count == 0) return;   // 空结构无需确认
        var r = MessageBox.Show($"确定清空当前结构的全部 {_struct.Blocks.Count} 个方块吗？\n（清空后可撤销不了，建议先「复制结构」备份）",
            "清空结构", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        _struct.Blocks.Clear();
        RebuildBlocks(); UpdateInfo();
        AppContext.SetStatus("结构已清空。");
    }

    private void ApplySize_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SizeXBox.Text, out int sx)) _struct.SizeX = Math.Clamp(sx, 4, 64);
        if (int.TryParse(SizeYBox.Text, out int sy)) _struct.SizeY = Math.Clamp(sy, 4, 64);
        if (int.TryParse(SizeZBox.Text, out int sz)) _struct.SizeZ = Math.Clamp(sz, 4, 64);
        // 裁剪越界方块（键格式损坏的也一并清除）
        foreach (var k in _struct.Blocks.Keys.ToList())
        {
            if (!TryParseKey(k, out int x, out int y, out int z) ||
                x >= _struct.SizeX || y >= _struct.SizeY || z >= _struct.SizeZ)
                _struct.Blocks.Remove(k);
        }
        RebuildBlocks(); UpdateInfo();
        _rig?.AnimateTo(_rig.Theta, _rig.Phi, FitDistance(), StructCenter(), 600);   // 尺寸变化后重新取景
    }

    private void UpdateInfo()
    {
        StructInfoText.Text = $"尺寸 {_struct.SizeX}×{_struct.SizeY}×{_struct.SizeZ} · 方块 {_struct.BlockCount} 个";
        var used = _struct.Blocks.Values.Where(v => v > 0 && v < _struct.Palette.Count).Distinct()
            .OrderBy(v => v).Select(v => _struct.Palette[v]).ToList();
        var list = new ObservableCollection<TextBlock>(used.Select(id =>
            new TextBlock { Text = Short(id), Margin = new Thickness(4, 2, 0, 2) }));
        UsedBlocksList.ItemsSource = list;
    }

    // ================= 存取 =================
    private void NewStruct_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("新结构名称", "MyStructure") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        _struct.Name = dlg.Value;
        _struct.Blocks.Clear();
        RebuildBlocks(); UpdateInfo();
    }

    private void SaveStruct_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        try
        {
            var dir = AppContext.DirStructures(p);
            Directory.CreateDirectory(dir);
            // 自动命名：同名结构自动追加序号，避免静默覆盖
            var baseName = SanitizeFileName(_struct.Name);
            var uniqueName = AutoNaming.EnsureUnique(dir, baseName, ".mcstructure.json");
            var file = Path.Combine(dir, uniqueName + ".mcstructure.json");
            StructureSerializer.Save(_struct, file);
            AppContext.SetStatus(uniqueName == baseName
                ? $"结构已保存：{file}"
                : $"结构已保存（同名文件存在，自动命名为 {uniqueName}）：{file}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadStruct_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        var dir = p != null ? AppContext.DirStructures(p) : null;
        var dlg = new OpenFileDialog
        {
            Filter = "工作台结构 (*.mcstructure.json)|*.mcstructure.json|JSON (*.json)|*.json",
            InitialDirectory = dir != null && Directory.Exists(dir) ? dir : ""
        };
        if (dlg.ShowDialog() != true) return;
        var s = StructureSerializer.Load(dlg.FileName);
        if (s == null) { MessageBox.Show("无法解析结构文件。"); return; }
        CopyInto(s);
        AppContext.SetStatus($"结构已加载：{dlg.FileName}");
    }

    private void GenCommand_Click(object sender, RoutedEventArgs e)
    {
        var cmd = StructureSerializer.GeneratePlaceCommand(_struct);
        try { Clipboard.SetText(cmd); } catch { }
        MessageBox.Show($"放置指令（已复制到剪贴板）：\n\n{cmd}\n\n在游戏中使用结构方块保存同名结构后即可使用。",
            "放置指令", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
