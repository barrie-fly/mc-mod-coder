using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MCModCoder.Core;
using MCModCoder.Core.Simulation;

namespace MCModCoder.Modules;

public partial class SimulatorView : UserControl
{
    // ---- 红石 ----
    private readonly RedstoneGrid _grid = new(28, 16);
    private const double Cell = 30;
    private readonly DispatcherTimer _rsTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };

    // ---- 实体 ----
    private readonly EntitySimulator _ents = new();
    private readonly DispatcherTimer _entTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _playerY = 0, _playerVy = 0;    // 玩家跳跃模拟
    private readonly List<(double x, double y)> _platforms = new();

    public SimulatorView()
    {
        InitializeComponent();
        _rsTimer.Tick += (_, _) => { _grid.Tick(); DrawRedstone(); };
        _entTimer.Tick += (_, _) => { EntTick(); };
        LoadExampleCircuit();
        DrawRedstone();
        DrawEntities();
    }

    // ================= 红石 =================
    private SimBlock SelectedBlock() =>
        RsTorch.IsChecked == true ? SimBlock.Torch :
        RsLever.IsChecked == true ? SimBlock.Lever :
        RsButton.IsChecked == true ? SimBlock.Button :
        RsLamp.IsChecked == true ? SimBlock.Lamp :
        RsRepeater.IsChecked == true ? SimBlock.RepeaterEast :
        RsSolid.IsChecked == true ? SimBlock.SolidBlock : SimBlock.Wire;

    private void Rs_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(RsCanvas);
        int x = (int)(p.X / Cell), y = (int)(p.Y / Cell);
        if (!_grid.In(x, y)) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (e.ChangedButton == MouseButton.Right)
        {
            _grid.Remove(x, y);
        }
        else if (shift && (e.OriginalSource is Shape))
        {
            _grid.Interact(x, y);   // Shift+点击 与拉杆/按钮交互
        }
        else
        {
            var cur = _grid.Type[x, y];
            if ((cur == SimBlock.Lever || cur == SimBlock.Button) && e.ChangedButton == MouseButton.Left && cur == SelectedBlock())
                _grid.Interact(x, y);
            else
                _grid.Place(x, y, SelectedBlock());
        }
        DrawRedstone();
    }

    private void Rs_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(RsCanvas);
        int x = (int)(p.X / Cell), y = (int)(p.Y / Cell);
        if (!_grid.In(x, y)) return;
        if (e.RightButton == MouseButtonState.Pressed) _grid.Remove(x, y);
        else if (_grid.Type[x, y] == SimBlock.Air) _grid.Place(x, y, SelectedBlock());
        DrawRedstone();
    }

    private void RsRun_Click(object sender, RoutedEventArgs e)
    {
        if (_rsTimer.IsEnabled) { _rsTimer.Stop(); BtnRsRun.Content = "▶ 自动运行"; }
        else
        {
            _rsTimer.Interval = TimeSpan.FromMilliseconds(600 / RsSpeed.Value);
            _rsTimer.Start();
            BtnRsRun.Content = "⏸ 暂停";
        }
    }

    private void RsStep_Click(object sender, RoutedEventArgs e) { _grid.Tick(); DrawRedstone(); }
    private void RsClear_Click(object sender, RoutedEventArgs e)
    {
        for (int x = 0; x < _grid.W; x++) for (int y = 0; y < _grid.H; y++) _grid.Remove(x, y);
        DrawRedstone();
    }

    private void RsExample_Click(object sender, RoutedEventArgs e) { LoadExampleCircuit(); DrawRedstone(); }

    private void LoadExampleCircuit()
    {
        for (int x = 0; x < _grid.W; x++) for (int y = 0; y < _grid.H; y++) _grid.Remove(x, y);
        // 非门示例：拉杆 → 火把反相 → 灯
        _grid.Place(1, 2, SimBlock.Lever);
        _grid.Place(2, 2, SimBlock.Wire);
        _grid.Place(3, 2, SimBlock.Torch);
        _grid.Place(4, 2, SimBlock.Wire);
        _grid.Place(5, 2, SimBlock.Lamp);
        // 时钟示例：火把 → 导线 → 火把（自振荡）
        _grid.Place(2, 8, SimBlock.Torch);
        _grid.Place(3, 8, SimBlock.Wire);
        _grid.Place(4, 8, SimBlock.Torch);
        _grid.Place(5, 8, SimBlock.Wire);
        _grid.Place(6, 8, SimBlock.Lamp);
    }

    private void DrawRedstone()
    {
        RsCanvas.Children.Clear();
        RsCanvas.Width = _grid.W * Cell;
        RsCanvas.Height = _grid.H * Cell;
        var gridBrush = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255));
        for (int x = 0; x <= _grid.W; x++)
            RsCanvas.Children.Add(new Line { X1 = x * Cell, X2 = x * Cell, Y1 = 0, Y2 = _grid.H * Cell, Stroke = gridBrush });
        for (int y = 0; y <= _grid.H; y++)
            RsCanvas.Children.Add(new Line { X1 = 0, X2 = _grid.W * Cell, Y1 = y * Cell, Y2 = y * Cell, Stroke = gridBrush });

        for (int x = 0; x < _grid.W; x++)
            for (int y = 0; y < _grid.H; y++)
            {
                var t = _grid.Type[x, y];
                if (t == SimBlock.Air) continue;
                int str = _grid.Strength[x, y];
                Shape shape = t switch
                {
                    SimBlock.Wire => new Ellipse { Width = Cell * 0.42, Height = Cell * 0.42 },
                    SimBlock.Torch => new Ellipse { Width = Cell * 0.5, Height = Cell * 0.5 },
                    SimBlock.Lever or SimBlock.Button => new Rectangle { Width = Cell * 0.5, Height = Cell * 0.5, RadiusX = 4, RadiusY = 4 },
                    SimBlock.Lamp => new Rectangle { Width = Cell * 0.86, Height = Cell * 0.86, RadiusX = 3, RadiusY = 3 },
                    SimBlock.RepeaterEast or SimBlock.RepeaterWest or SimBlock.RepeaterSouth or SimBlock.RepeaterNorth
                        => new Rectangle { Width = Cell * 0.8, Height = Cell * 0.36, RadiusX = 2, RadiusY = 2 },
                    _ => new Rectangle { Width = Cell * 0.86, Height = Cell * 0.86, RadiusX = 2, RadiusY = 2 },
                };
                Color c = t switch
                {
                    SimBlock.Wire => str > 0 ? Color.FromRgb(0xFF, 0x40, 0x30) : Color.FromRgb(0x64, 0x20, 0x18),
                    SimBlock.Torch => _grid.OnState[x, y] || str > 0 ? Color.FromRgb(0xFF, 0x70, 0x30) : Color.FromRgb(0x8A, 0x44, 0x22),
                    SimBlock.Lever => _grid.OnState[x, y] ? Colors.Orange : Colors.SlateGray,
                    SimBlock.Button => _grid.Timer[x, y] > 0 ? Colors.Orange : Colors.SlateGray,
                    SimBlock.Lamp => _grid.OnState[x, y] ? Color.FromRgb(0xFF, 0xE0, 0x70) : Color.FromRgb(0x5A, 0x4E, 0x30),
                    SimBlock.SolidBlock => Colors.Gray,
                    _ => str > 0 ? Colors.Orange : Colors.SlateGray,
                };
                shape.Fill = new SolidColorBrush(c);
                Canvas.SetLeft(shape, x * Cell + (Cell - shape.Width) / 2);
                Canvas.SetTop(shape, y * Cell + (Cell - shape.Height) / 2);
                RsCanvas.Children.Add(shape);

                if (ShowStrength.IsChecked == true && str > 0)
                {
                    var lbl = new TextBlock { Text = str.ToString(), Foreground = Brushes.White, FontSize = 10 };
                    Canvas.SetLeft(lbl, x * Cell + Cell / 2 - 4);
                    Canvas.SetTop(lbl, y * Cell + 1);
                    RsCanvas.Children.Add(lbl);
                }
            }
    }

    // ================= 实体 =================
    private void SpawnZombie_Click(object sender, RoutedEventArgs e) { _ents.Spawn("zombie", 5, 30, 16); DrawEntities(); }
    private void SpawnCow_Click(object sender, RoutedEventArgs e) { _ents.Spawn("cow", 5, 30, 16); DrawEntities(); }
    private void SpawnSheep_Click(object sender, RoutedEventArgs e) { _ents.Spawn("sheep", 5, 30, 16); DrawEntities(); }
    private void ClearEntities_Click(object sender, RoutedEventArgs e) { _ents.Clear(); DrawEntities(); }
    private void EntRun_Click(object sender, RoutedEventArgs e)
    {
        if (_entTimer.IsEnabled) { _entTimer.Stop(); BtnEntRun.Content = "▶ 运行模拟"; }
        else { _entTimer.Start(); BtnEntRun.Content = "⏸ 暂停"; }
    }
    private void EntReset_Click(object sender, RoutedEventArgs e)
    {
        _ents.Clear(); _platforms.Clear(); _playerY = 0; _playerVy = 0;
        DrawEntities();
    }

    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (_playerY <= 0.01) _playerVy = 0.35 * GravitySlider.Value;
    }

    private void EntCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(EntCanvas);
        _platforms.Add((p.X / 24, p.Y / 24));
        DrawEntities();
    }

    private void EntTick()
    {
        _ents.Tick(30, 16);
        // 玩家跳跃物理
        if (_playerY > 0 || _playerVy > 0)
        {
            _playerVy -= 0.02 * GravitySlider.Value;
            _playerY += _playerVy;
            if (_playerY <= 0) { _playerY = 0; _playerVy = 0; }
        }
        DrawEntities();
    }

    private void DrawEntities()
    {
        double cw = EntCanvas.ActualWidth > 0 ? EntCanvas.ActualWidth : 900;
        double ch = EntCanvas.ActualHeight > 0 ? EntCanvas.ActualHeight : 480;
        EntCanvas.Children.Clear();
        EntCanvas.Width = cw; EntCanvas.Height = ch;

        // 天空色（随时间变化）
        double t = TimeSlider.Value;
        bool day = t < 12000 || t > 23000;
        var sky = day ? Color.FromRgb(0x4A, 0x7A, 0xC0) : Color.FromRgb(0x0A, 0x0F, 0x22);
        if (RuleDaylight.IsChecked != true) sky = Color.FromRgb(0x4A, 0x7A, 0xC0);
        EntCanvas.Background = new SolidColorBrush(sky);
        if (!day && RuleDaylight.IsChecked == true)
        {
            var moon = new Ellipse { Width = 26, Height = 26, Fill = Brushes.LightGray, Opacity = 0.8 };
            Canvas.SetLeft(moon, cw - 90); Canvas.SetTop(moon, 40);
            EntCanvas.Children.Add(moon);
        }

        // 地面
        var ground = new Rectangle { Width = cw, Height = 60, Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x46, 0x32)) };
        Canvas.SetLeft(ground, 0); Canvas.SetTop(ground, ch - 60);
        EntCanvas.Children.Add(ground);

        // 玩家方块交互平台
        foreach (var (x, y) in _platforms)
        {
            var b = new Rectangle { Width = 24, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(0x8B, 0x6D, 0x4C)) };
            Canvas.SetLeft(b, x * 24); Canvas.SetTop(b, y * 24);
            EntCanvas.Children.Add(b);
        }

        // 玩家（跳跃模拟）
        double py = ch - 60 - 34 - _playerY * 60;
        var player = new Rectangle { Width = 18, Height = 34, Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), RadiusX = 3, RadiusY = 3 };
        Canvas.SetLeft(player, 60); Canvas.SetTop(player, py);
        EntCanvas.Children.Add(player);
        var pLbl = new TextBlock { Text = $"Player  y={_playerY:F2}", Foreground = Brushes.White, FontSize = 10 };
        Canvas.SetLeft(pLbl, 40); Canvas.SetTop(pLbl, py - 16);
        EntCanvas.Children.Add(pLbl);

        // 实体
        foreach (var en in _ents.Entities)
        {
            double x = en.X / 30.0 * cw, y = ch - 60 - 14 - (en.Y / 16.0) * (ch - 120);
            Color c = en.Kind switch
            {
                "zombie" => Color.FromRgb(0x44, 0x8A, 0x44),
                "cow" => Color.FromRgb(0xD2, 0xB4, 0x8C),
                _ => Color.FromRgb(0xEE, 0xEE, 0xEE),
            };
            var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(dot, x - 7); Canvas.SetTop(dot, y - 7);
            ToolTipService.SetToolTip(dot, $"{en.Kind} (存活 {en.Age} 刻)");
            EntCanvas.Children.Add(dot);
        }

        // 游戏规则信息
        var info = new TextBlock
        {
            Text = $"时间 {TimeSlider.Value:00000} · doDaylightCycle={RuleDaylight.IsChecked} · keepInventory={RuleKeepInv.IsChecked} · mobGriefing={RuleMobGrief.IsChecked}",
            Foreground = Brushes.White, FontSize = 11, Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0))
        };
        Canvas.SetLeft(info, 8); Canvas.SetTop(info, 6);
        EntCanvas.Children.Add(info);
    }
}
