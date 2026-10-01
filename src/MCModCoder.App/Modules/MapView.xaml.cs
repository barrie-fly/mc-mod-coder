using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using MCModCoder.Core;
using MCModCoder.Core.Models;
using MCModCoder.Core.Serialization;

namespace MCModCoder.Modules;

public partial class MapView : UserControl
{
    private readonly TerrainMap _map = new() { Size = 128 };
    private WriteableBitmap? _bitmap;
    private bool _painting;
    private double _zoom = 4;

    public MapView()
    {
        InitializeComponent();
        foreach (var b in Biomes.All) BiomeCombo.Items.Add(b.Cn);
        BiomeCombo.SelectedIndex = 0;
        OreGrid.DataContext = _map.Ores;
        _map.Generate(64, 20, 0);
        Loaded += (_, _) => { Render(); };
        UpdateInfo();
    }

    // ================= 渲染 =================
    private void Render()
    {
        int n = _map.Size;
        if (_bitmap == null || _bitmap.PixelWidth != n)
            _bitmap = new WriteableBitmap(n, n, 96, 96, PixelFormats.Bgra32, null);
        var px = new int[n * n];
        var rng = new Random(0x51EED);

        // 矿物点（确定性散布，仅显示用）
        bool showOres = ShowOres.IsChecked == true;
        var orePixels = new Dictionary<int, Color>();
        if (showOres)
        {
            foreach (var ore in _map.Ores)
            {
                var oc = Color.FromRgb((byte)(0x30 + ore.VeinSize * 3), (byte)(0x30 + ore.VeinsPerChunk * 2), (byte)(0x80 + ore.MinY / 2));
                int veins = Math.Max(1, ore.VeinsPerChunk * n / 256);
                for (int v = 0; v < veins; v++)
                {
                    int cx = rng.Next(n), cy = rng.Next(n);
                    for (int s = 0; s < ore.VeinSize; s++)
                    {
                        int px_ = Math.Clamp(cx + rng.Next(-2, 3), 0, n - 1);
                        int py = Math.Clamp(cy + rng.Next(-2, 3), 0, n - 1);
                        orePixels[py * n + px_] = oc;
                    }
                }
            }
        }

        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int h = _map.H(x, y);
                var baseC = ColorUtil.ParseHex(Biomes.ColorOf(_map.B(x, y)));
                double shade = 0.75 + (h - _map.SeaLevel + 20) / 60.0;
                shade = Math.Clamp(shade, 0.45, 1.35);
                byte r = (byte)Math.Clamp(baseC.R * shade, 0, 255);
                byte g = (byte)Math.Clamp(baseC.G * shade, 0, 255);
                byte bl = (byte)Math.Clamp(baseC.B * shade, 0, 255);
                if (h < _map.SeaLevel)   // 水体
                {
                    double depth = Math.Clamp((_map.SeaLevel - h) / 24.0, 0, 1);
                    r = (byte)(r * (1 - depth) + 0x2A * depth);
                    g = (byte)(g * (1 - depth) + 0x64 * depth);
                    bl = (byte)(bl * (1 - depth) + 0xD8 * depth);
                }
                var c = Color.FromRgb(r, g, bl);
                int idx = y * n + x;
                if (showOres && orePixels.TryGetValue(idx, out var oc2) && h >= _map.SeaLevel)
                    c = oc2;
                px[idx] = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
        _bitmap.WritePixels(new Int32Rect(0, 0, n, n), px, n * 4, 0);
        MapImage.Source = _bitmap;
        MapImage.Width = n * _zoom;
        MapImage.Height = n * _zoom;
    }

    // 供 XAML Checked/Unchecked 事件绑定（签名匹配 RoutedEventHandler）
    private void Render(object sender, RoutedEventArgs e) => Render();

    private void UpdateInfo()
    {
        MapInfoText.Text = $"尺寸 {_map.Size}×{_map.Size} · 海平面 {_map.SeaLevel} · 种子「{_map.Seed}」";
    }

    // ================= 生成 =================
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        _map.Seed = SeedBox.Text;
        _map.Generate((int)BaseHeight.Value, (int)Amplitude.Value, BioBand.IsChecked == true ? 1 : 0);
        Render(); UpdateInfo();
        AppContext.SetStatus($"地形已生成（种子 {_map.Seed}）。");
    }

    // ================= 绘制 =================
    private (int x, int y)? GetCell(MouseEventArgs e)
    {
        var p = e.GetPosition(MapImage);
        int x = (int)(p.X / _zoom), y = (int)(p.Y / _zoom);
        if (x < 0 || y < 0 || x >= _map.Size || y >= _map.Size) return null;
        return (x, y);
    }

    private void Map_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _painting = true;
        MapImage.CaptureMouse();
        ApplyBrush(e);
    }

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        if (_painting && e.LeftButton == MouseButtonState.Pressed) ApplyBrush(e);
    }

    private void Map_MouseUp(object sender, MouseButtonEventArgs e) { _painting = false; MapImage.ReleaseMouseCapture(); }
    private void Map_MouseLeave(object sender, MouseEventArgs e) { _painting = false; }

    private void ApplyBrush(MouseEventArgs e)
    {
        if (GetCell(e) is not (int cx, int cy)) return;
        int r = (int)BrushSize.Value, str = (int)BrushStrength.Value;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= _map.Size || y >= _map.Size) continue;
                if (BrushRaise.IsChecked == true) _map.SetH(x, y, _map.H(x, y) + str);
                else if (BrushLower.IsChecked == true) _map.SetH(x, y, _map.H(x, y) - str);
                else if (BrushSmooth.IsChecked == true)
                {
                    int sum = 0, cnt = 0;
                    for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++)
                    {
                        int nx = x + i, ny = y + j;
                        if (nx >= 0 && ny >= 0 && nx < _map.Size && ny < _map.Size) { sum += _map.H(nx, ny); cnt++; }
                    }
                    _map.SetH(x, y, (int)(_map.H(x, y) * 0.6 + sum / (double)cnt * 0.4));
                }
                else if (BrushBiome.IsChecked == true) _map.SetB(x, y, BiomeCombo.SelectedIndex);
            }
        Render();
    }

    private void RerollOres_Click(object sender, RoutedEventArgs e)
    {
        OreGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Render();
        AppContext.SetStatus("矿物分布已按新配置重绘。");
    }

    // ================= 导入导出 =================
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "工作台地图 (*.mcmap.json)|*.mcmap.json", FileName = _map.Name + ".mcmap.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            MapSerializer.Save(_map, dlg.FileName);
            var pngPath = System.IO.Path.ChangeExtension(dlg.FileName, ".png");
            MapSerializer.SaveHeightmapPng(_map, pngPath);
            AppContext.SetStatus($"地图已导出：{dlg.FileName} + 高度图 {pngPath}");
        }
        catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "工作台地图 (*.mcmap.json)|*.mcmap.json|JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        var m = MapSerializer.Load(dlg.FileName);
        if (m == null) { MessageBox.Show("无法解析地图文件。"); return; }
        var ores = _map.Ores;
        _map.Name = m.Name; _map.Size = m.Size; _map.SeaLevel = m.SeaLevel; _map.Seed = m.Seed;
        _map.Height = m.Height; _map.Biome = m.Biome; _map.Ores = ores;
        OreGrid.DataContext = _map.Ores;
        _zoom = Math.Max(2, 512 / _map.Size);
        Render(); UpdateInfo();
        AppContext.SetStatus($"地图已导入：{dlg.FileName}");
    }
}

/// <summary>地图序列化</summary>
public static class MapSerializer
{
    private static readonly System.Text.Json.JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(TerrainMap m, string file) =>
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(m, Opts));

    public static TerrainMap? Load(string file)
    {
        try
        {
            var m = System.Text.Json.JsonSerializer.Deserialize<TerrainMap>(File.ReadAllText(file),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (m == null) return null;
            // 数据长度与尺寸不符（手工编辑 / 损坏文件）时重建数组，避免后续访问越界崩溃
            if (m.Height == null || m.Biome == null || m.Height.Length != m.Size * m.Size || m.Biome.Length != m.Size * m.Size)
                m.InitArrays();
            return m;
        }
        catch { return null; }
    }

    /// <summary>灰度高度图 PNG（供 WorldGen 工具链使用）</summary>
    public static void SaveHeightmapPng(TerrainMap m, string file)
    {
        int n = m.Size;
        if (m.Height == null || m.Height.Length < n * n)
            throw new InvalidOperationException("高度数据不完整，无法导出高度图。");
        var bmp = new WriteableBitmap(n, n, 96, 96, PixelFormats.Gray8, null);
        var px = new byte[n * n];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)m.Height[i];
        bmp.WritePixels(new Int32Rect(0, 0, n, n), px, n, 0);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
