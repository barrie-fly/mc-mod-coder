using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using MCModCoder.Core.Models;
using MCModCoder.Core.Services;

namespace MCModCoder.Modules;

public class DungeonRoomVm
{
    public DungeonRoom Room { get; init; } = null!;
    public string Label => $"({Room.X},{Room.Y}) {Room.W}×{Room.H}";
    public string KindCn => Room.CnKind;
}

public partial class DungeonView : UserControl
{
    private readonly DungeonGraph _graph = new();
    private DungeonRoom? _selected;
    private DungeonRoom? _linkFrom;       // 走廊连接模式的起点
    private bool _dragging;
    private Point _dragOffset;
    private bool _linkMode;               // 走廊连接模式（BtnLink 是 Button 无 IsChecked，用字段代替）
    private double _scale = 1.0;

    private static readonly Dictionary<string, Color> KindColors = new()
    {
        ["entrance"] = Color.FromRgb(0x4C, 0xC3, 0x8A),
        ["treasure"] = Color.FromRgb(0xE8, 0xC4, 0x68),
        ["spawner"] = Color.FromRgb(0xF2, 0x6D, 0x6D),
        ["boss"] = Color.FromRgb(0xB4, 0x6E, 0xF2),
        ["normal"] = Color.FromRgb(0x4C, 0x8D, 0xFF),
    };

    public DungeonView()
    {
        InitializeComponent();
        _graph.Rules.Seed = 12345;
        _graph.Generate();
        RefreshAll();
    }

    // ================= 渲染 =================
    private void RefreshAll()
    {
        Redraw();
        RoomList.ItemsSource = new ObservableCollection<DungeonRoomVm>(
            _graph.Rooms.Select(r => new DungeonRoomVm { Room = r }));
        DunInfoText.Text = $"房间 {_graph.Rooms.Count} · 走廊 {_graph.Corridors.Count} · 模式：{(_graph.Rules.Mode == "fixed" ? "固定" : "随机")}";
    }

    private void Redraw()
    {
        DungeonCanvas.Children.Clear();
        // 网格
        var gridBrush = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
        for (int x = 0; x <= DungeonCanvas.Width; x += 50)
            DungeonCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = DungeonCanvas.Height, Stroke = gridBrush });
        for (int y = 0; y <= DungeonCanvas.Height; y += 50)
            DungeonCanvas.Children.Add(new Line { X1 = 0, X2 = DungeonCanvas.Width, Y1 = y, Y2 = y, Stroke = gridBrush });

        // 走廊
        foreach (var c in _graph.Corridors)
        {
            var a = _graph.RoomById(c.FromRoom);
            var b = _graph.RoomById(c.ToRoom);
            if (a == null || b == null) continue;
            var line = new Line
            {
                X1 = a.X * _scale, Y1 = a.Y * _scale,
                X2 = b.X * _scale, Y2 = b.Y * _scale,
                Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x7A)),
                StrokeThickness = Math.Max(1.5, c.Width * _scale),
                StrokeDashArray = c.Style == "L-shaped" ? new DoubleCollection { 3, 2 } : null,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            };
            DungeonCanvas.Children.Add(line);
        }

        // 房间
        foreach (var room in _graph.Rooms)
        {
            var color = KindColors.GetValueOrDefault(room.Kind, Colors.Gray);
            bool selected = room == _selected || room == _linkFrom;
            double w = room.W * _scale, h = room.H * _scale;
            var rect = new Rectangle
            {
                Width = w, Height = h,
                Stroke = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                StrokeThickness = selected ? 2.5 : 1,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(selected ? 90 : 55), color.R, color.G, color.B)),
                RadiusX = 3, RadiusY = 3,
                Tag = room
            };
            Canvas.SetLeft(rect, (room.X - room.W / 2.0) * _scale);
            Canvas.SetTop(rect, (room.Y - room.H / 2.0) * _scale);
            ToolTipService.SetToolTip(rect, $"{room.CnKind} · {room.W}×{room.H} · 材质 {room.Material}");
            DungeonCanvas.Children.Add(rect);

            var label = new TextBlock
            {
                Text = room.CnKind,
                Foreground = new SolidColorBrush(color),
                FontSize = 11 * Math.Max(0.6, _scale)
            };
            Canvas.SetLeft(label, (room.X - room.W / 2.0) * _scale + 4);
            Canvas.SetTop(label, (room.Y - room.H / 2.0) * _scale + 4);
            DungeonCanvas.Children.Add(label);
        }
    }

    // ================= 生成 =================
    private void ApplyRulesFromUi()
    {
        _graph.Rules.Mode = ModeFixed.IsChecked == true ? "fixed" : "random";
        if (int.TryParse(SeedBox.Text, out int seed)) _graph.Rules.Seed = seed;
        _graph.Rules.RoomCount = (int)RoomCount.Value;
        if (int.TryParse(MinSizeBox.Text, out int mn)) _graph.Rules.MinRoomSize = Math.Max(4, mn);
        if (int.TryParse(MaxSizeBox.Text, out int mx)) _graph.Rules.MaxRoomSize = Math.Max(_graph.Rules.MinRoomSize, mx);
        _graph.Rules.CorridorWidth = (int)CorrWidth.Value;
        _graph.Rules.IncludeTreasure = IncTreasure.IsChecked == true;
        _graph.Rules.IncludeSpawner = IncSpawner.IsChecked == true;
        _graph.Rules.IncludeBoss = IncBoss.IsChecked == true;
        _graph.Rules.LoopConnectionRate = LoopRate.Value;
        _graph.Rules.WallMaterial = WallBox.Text.Trim();
        _graph.Rules.FloorMaterial = FloorBox.Text.Trim();
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        ApplyRulesFromUi();
        if (_graph.Rules.Mode == "fixed")
        {
            MessageBox.Show("固定结构模式：请手动布置房间（添加房间、拖动位置、走廊连接模式连线）。", "固定结构");
            return;
        }
        _graph.Generate();
        RefreshAll();
        AppContext.SetStatus($"地牢已生成：{_graph.Rooms.Count} 个房间，{_graph.Corridors.Count} 条走廊（种子 {_graph.Rules.Seed}）。");
    }

    private void RandomSeed_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = new Random().Next(1, 999999).ToString();
    }

    // ================= 手动编辑 =================
    private DungeonRoom? HitRoom(Point p)
    {
        foreach (var room in _graph.Rooms)
        {
            double x = (room.X - room.W / 2.0) * _scale, y = (room.Y - room.H / 2.0) * _scale;
            if (p.X >= x && p.X <= x + room.W * _scale && p.Y >= y && p.Y <= y + room.H * _scale)
                return room;
        }
        return null;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(DungeonCanvas);
        var hit = HitRoom(p);
        if (_linkMode && hit != null)
        {
            if (_linkFrom == null)
            {
                _linkFrom = hit;
                AppContext.SetStatus($"走廊连接：起点 {hit.CnKind}，请点击目标房间。");
            }
            else if (_linkFrom != hit)
            {
                if (!_graph.Corridors.Any(c => (c.FromRoom == _linkFrom.Id && c.ToRoom == hit.Id) ||
                                               (c.FromRoom == hit.Id && c.ToRoom == _linkFrom.Id)))
                {
                    _graph.Corridors.Add(new DungeonCorridor
                    { FromRoom = _linkFrom.Id, ToRoom = hit.Id, Width = _graph.Rules.CorridorWidth, Style = "straight" });
                }
                _linkFrom = null;
                _linkMode = false;
            }
            Redraw();
            return;
        }

        if (hit == null) return;
        _selected = hit;
        _dragging = true;
        _dragOffset = new Point(hit.X - p.X / _scale, hit.Y - p.Y / _scale);
        DungeonCanvas.CaptureMouse();
        Redraw();
        RoomList.ItemsSource = new ObservableCollection<DungeonRoomVm>(_graph.Rooms.Select(r => new DungeonRoomVm { Room = r }));
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _selected == null) return;
        var p = e.GetPosition(DungeonCanvas);
        _selected.X = (int)((p.X / _scale) + _dragOffset.X);
        _selected.Y = (int)((p.Y / _scale) + _dragOffset.Y);
        Redraw();
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        DungeonCanvas.ReleaseMouseCapture();
        RefreshAll();
    }

    private void AddRoom_Click(object sender, RoutedEventArgs e)
    {
        var rng = new Random();
        var room = new DungeonRoom
        {
            X = rng.Next(60, (int)(DungeonCanvas.Width / _scale) - 60),
            Y = rng.Next(60, (int)(DungeonCanvas.Height / _scale) - 60),
            W = _graph.Rules.MinRoomSize, H = _graph.Rules.MinRoomSize,
            Material = _graph.Rules.WallMaterial
        };
        _graph.Rooms.Add(room);
        _selected = room;
        RefreshAll();
    }

    private void DelSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _graph.Corridors.RemoveAll(c => c.FromRoom == _selected!.Id || c.ToRoom == _selected!.Id);
        _graph.Rooms.Remove(_selected);
        _selected = null;
        RefreshAll();
    }

    private void LinkMode_Click(object sender, RoutedEventArgs e)
    {
        _linkMode = !_linkMode;
        if (_linkMode) AppContext.SetStatus("走廊连接模式：点击起点房间 → 点击终点房间。");
        else _linkFrom = null;
        Redraw();
    }

    private void RoomList_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RoomList.SelectedItem is DungeonRoomVm vm) { _selected = vm.Room; Redraw(); }
    }

    // ================= 测试生成 =================
    private void TestGenerate_Click(object sender, RoutedEventArgs e)
    {
        // 次数限制在 1~1000：手滑输入 99999 会让界面卡死几分钟且无任何提示
        if (!int.TryParse(TestCount.Text, out int n) || n <= 0) return;
        n = Math.Clamp(n, 1, 1000);
        int originalSeed = _graph.Rules.Seed;
        int totalRooms = 0, minRooms = int.MaxValue, maxRooms = 0, connected = 0;
        var rng = new Random();
        for (int i = 0; i < n; i++)
        {
            _graph.Rules.Seed = rng.Next(1, int.MaxValue);
            _graph.Generate();
            totalRooms += _graph.Rooms.Count;
            minRooms = Math.Min(minRooms, _graph.Rooms.Count);
            maxRooms = Math.Max(maxRooms, _graph.Rooms.Count);
            if (IsConnected()) connected++;
        }
        _graph.Rules.Seed = originalSeed;
        _graph.Generate();
        RefreshAll();

        TestResultBox.Text =
            $"""
            压力测试结果（{n} 次）：
            ─────────────────────
            平均房间数：{(double)totalRooms / n:F1}
            最少 / 最多：{minRooms} / {maxRooms}
            全连通率：{connected * 100.0 / n:F0}%
            ─────────────────────
            {(connected * 2 >= n ? "✔ 生成器稳定。" : "⚠ 部分种子存在孤立房间，建议提高环路连接概率。")}
            """;
    }

    /// <summary>并查集检查连通性</summary>
    private bool IsConnected()
    {
        var idx = _graph.Rooms.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var parent = Enumerable.Range(0, _graph.Rooms.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        foreach (var c in _graph.Corridors)
        {
            if (!idx.TryGetValue(c.FromRoom, out int a) || !idx.TryGetValue(c.ToRoom, out int b)) continue;
            parent[Find(a)] = Find(b);
        }
        return parent.Select(Find).Distinct().Count() == 1;
    }

    // ================= 推送到结构设计器 =================
    private void PushToStructure_Click(object sender, RoutedEventArgs e)
    {
        if (_graph.Rooms.Count == 0) { MessageBox.Show("请先生成或布置地牢。"); return; }
        var s = DungeonToStructure(_graph);
        AppContext.PushStructure(s);
        AppContext.SetStatus($"地牢「{_graph.Name}」已转换为结构（{s.SizeX}×{s.SizeY}×{s.SizeZ}）并推送到 3D 结构设计器。");
    }

    /// <summary>地牢 → 多层方块结构</summary>
    public static StructureData DungeonToStructure(DungeonGraph g)
    {
        int minX = g.Rooms.Min(r => r.X - r.W / 2) - 4, maxX = g.Rooms.Max(r => r.X + r.W / 2) + 4;
        int minY = g.Rooms.Min(r => r.Y - r.H / 2) - 4, maxY = g.Rooms.Max(r => r.Y + r.H / 2) + 4;
        int w = maxX - minX, d = maxY - minY;
        var palette = new List<string>
        {
            "minecraft:air",
            g.Rules.WallMaterial,
            g.Rules.FloorMaterial,
            "minecraft:spawner",
            "minecraft:chest",
            "minecraft:torch"
        };
        int wall = 1, floor = 2, spawner = 3, chest = 4, torch = 5;
        int height = 6;
        var s = new StructureData { Name = g.Name, SizeX = w, SizeY = height, SizeZ = d, Palette = palette };

        // 房间：地板 + 墙 + 火把 + 特殊物
        foreach (var room in g.Rooms)
        {
            int x0 = room.X - room.W / 2 - minX, z0 = room.Y - room.H / 2 - minY;
            for (int x = x0; x < x0 + room.W; x++)
                for (int z = z0; z < z0 + room.H; z++)
                {
                    s.Set(x, 0, z, floor);
                    bool border = x == x0 || z == z0 || x == x0 + room.W - 1 || z == z0 + room.H - 1;
                    if (border)
                        for (int y = 1; y <= 4; y++) s.Set(x, y, z, wall);
                    else if ((x + z) % 6 == 0) s.Set(x, 4, z, torch);
                }
            if (room.Kind == "spawner") s.Set(room.X - minX, 1, room.Y - minY, spawner);
            if (room.Kind == "treasure") s.Set(room.X - minX, 1, room.Y - minY, chest);
        }

        // 走廊：3 宽镂空 + 地板 + 顶
        foreach (var c in g.Corridors)
        {
            var a = g.RoomById(c.FromRoom); var b = g.RoomById(c.ToRoom);
            if (a == null || b == null) continue;
            int ax = a.X - minX, az = a.Y - minY, bx = b.X - minX, bz = b.Y - minY;
            int hw = Math.Max(1, c.Width / 2);
            int x = ax, z = az;
            while (x != bx || z != bz)
            {
                if (x != bx) x += Math.Sign(bx - x);
                if (z != bz) z += Math.Sign(bz - z);
                for (int dx = -hw; dx <= hw; dx++)
                    for (int dz = -hw; dz <= hw; dz++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= w || zz >= d) continue;
                        if (s.Get(xx, 1, zz) == 0 || s.Get(xx, 1, zz) == wall)
                        {
                            s.Set(xx, 0, zz, floor);
                            for (int y = 1; y <= 4; y++) s.Set(xx, y, zz, 0);
                            s.Set(xx, 5, zz, wall);
                        }
                    }
            }
        }
        return s;
    }

    // ================= 存取 =================
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) return;
        var dir = AppContext.DirDungeons(p);
        Directory.CreateDirectory(dir);
        ApplyRulesFromUi();
        try
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string(_graph.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (safe.Length == 0) safe = "dungeon";
            // 自动命名：同名地牢自动追加序号，避免静默覆盖
            var uniqueName = AutoNaming.EnsureUnique(dir, safe, ".mcdungeon.json");
            var file = System.IO.Path.Combine(dir, uniqueName + ".mcdungeon.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(_graph,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            AppContext.SetStatus(uniqueName == safe
                ? $"地牢已保存：{file}"
                : $"地牢已保存（同名文件存在，自动命名为 {uniqueName}）：{file}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        var dir = p != null ? AppContext.DirDungeons(p) : null;
        var dlg = new OpenFileDialog
        {
            Filter = "工作台地牢 (*.mcdungeon.json)|*.mcdungeon.json|JSON (*.json)|*.json",
            InitialDirectory = dir != null && Directory.Exists(dir) ? dir : ""
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var g = System.Text.Json.JsonSerializer.Deserialize<DungeonGraph>(File.ReadAllText(dlg.FileName),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (g == null) return;
            _graph.Name = g.Name; _graph.Rules = g.Rules; _graph.Rooms = g.Rooms; _graph.Corridors = g.Corridors;
            ModeFixed.IsChecked = g.Rules.Mode == "fixed"; ModeRandom.IsChecked = g.Rules.Mode != "fixed";
            SeedBox.Text = g.Rules.Seed.ToString();
            RefreshAll();
            AppContext.SetStatus($"地牢已加载：{dlg.FileName}");
        }
        catch (Exception ex) { MessageBox.Show("加载失败：" + ex.Message); }
    }
}
