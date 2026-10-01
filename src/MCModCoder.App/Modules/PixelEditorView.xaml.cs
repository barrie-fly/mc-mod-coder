using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Services;

namespace MCModCoder.Modules;

/// <summary>图层模型</summary>
public class PixelLayer
{
    public string Name { get; set; } = "图层";
    public WriteableBitmap Bitmap { get; set; } = null!;
    public bool Visible { get; set; } = true;
    public double Opacity { get; set; } = 1.0;

    [System.Text.Json.Serialization.JsonIgnore]
    public string SizeText => $"{Bitmap.PixelWidth}×{Bitmap.PixelHeight}";
}

public partial class PixelEditorView : UserControl
{
    private readonly ObservableCollection<PixelLayer> _layers = new();
    private WriteableBitmap? _composite;                 // 合成结果
    private int _w = 16, _h = 16;
    private Color _color = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private bool _drawing;
    private readonly Stack<(int layer, int[] px)> _undo = new();
    private readonly Stack<(int layer, int[] px)> _redo = new();

    public PixelEditorView()
    {
        InitializeComponent();
        LayerList.ItemsSource = _layers;
        BuildPalette();
        NewDocument(16, 16);
        ZoomText.Text = "16×";
        BrushSize.ValueChanged += (_, _) => BrushSizeText.Text = $"{(int)BrushSize.Value} px";
        AppContext.ProjectChanged += RefreshProjectUi;
        PreviewKeyDown += PixelEditor_PreviewKeyDown;   // Ctrl+Z / Ctrl+Y 撤销重做
    }

    /// <summary>画布编辑支持通用撤销快捷键（Ctrl+Z / Ctrl+Y），与按钮行为一致</summary>
    private void PixelEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { BtnUndo_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { BtnRedo_Click(sender, e); e.Handled = true; }
    }

    // ================= 文档管理 =================
    private void NewDocument(int w, int h)
    {
        _w = w; _h = h;
        _layers.Clear();
        _undo.Clear(); _redo.Clear();
        _layers.Add(new PixelLayer { Name = "图层 1", Bitmap = MakeBitmap(w, h) });
        LayerList.SelectedIndex = 0;
        RebuildComposite();
        SizeText.Text = $"{w} × {h}";
        ResCombo.SelectedItem = ResCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Content as string) == w.ToString());
    }

    private static WriteableBitmap MakeBitmap(int w, int h) => new(w, h, 96, 96, PixelFormats.Bgra32, null);

    private static int[] ReadPixels(WriteableBitmap bmp)
    {
        var px = new int[bmp.PixelWidth * bmp.PixelHeight];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return px;
    }

    private PixelLayer? Active => LayerList.SelectedItem as PixelLayer;
    private int ActiveIndex => LayerList.SelectedIndex;

    // ================= 合成 =================
    private void RebuildComposite() => ComposeRegion(new(0, 0, _w, _h));

    private void ComposeRegion(Int32Rect r)
    {
        r = ClampRect(r);
        if (r.Width <= 0 || r.Height <= 0) return;
        if (_composite == null || _composite.PixelWidth != _w || _composite.PixelHeight != _h)
            _composite = MakeBitmap(_w, _h);

        int stride = _w * 4;
        var dst = new int[r.Width * r.Height];
        // 透明底
        for (int i = 0; i < dst.Length; i++) dst[i] = 0;

        foreach (var layer in _layers)
        {
            if (!layer.Visible || layer.Opacity <= 0) continue;
            var srcFull = ReadPixels(layer.Bitmap);
            int oa = (int)(layer.Opacity * 255);
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int sx = (r.X + x) + (r.Y + y) * _w;
                    int a = (byte)(srcFull[sx] >> 24) * oa / 255;
                    if (a == 0) continue;
                    int sb = srcFull[sx] & 0xFF, sg = (srcFull[sx] >> 8) & 0xFF, sr = (srcFull[sx] >> 16) & 0xFF;
                    int di = x + y * r.Width;
                    int db = dst[di] & 0xFF, dg = (dst[di] >> 8) & 0xFF, dr = (dst[di] >> 16) & 0xFF, da = dst[di] >> 24;
                    int outA = a + da * (255 - a) / 255;
                    int outR = outA == 0 ? 0 : (sr * a + dr * da * (255 - a) / 255) / outA;
                    int outG = outA == 0 ? 0 : (sg * a + dg * da * (255 - a) / 255) / outA;
                    int outB = outA == 0 ? 0 : (sb * a + db * da * (255 - a) / 255) / outA;
                    dst[di] = (outA << 24) | (outR << 16) | (outG << 8) | outB;
                }
        }
        _composite.WritePixels(new Int32Rect(r.X, r.Y, r.Width, r.Height), dst, r.Width * 4, 0);

        CanvasImage.Source = _composite;
        CanvasImage.Width = _w * ZoomSlider.Value;
        CanvasImage.Height = _h * ZoomSlider.Value;
        DrawGridOverlay();
        RenderPreview();
    }

    private Int32Rect ClampRect(Int32Rect r)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(_w, r.X + r.Width), y1 = Math.Min(_h, r.Y + r.Height);
        return x1 <= x0 || y1 <= y0 ? default : new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // ================= 网格 =================
    private void DrawGridOverlay()
    {
        if (GridOverlay == null || ShowGrid == null || ZoomSlider == null) return;   // XAML 初始化期间事件先于控件就绪
        GridOverlay.Children.Clear();
        if (ShowGrid.IsChecked != true || ZoomSlider.Value < 6 || _w > 64) return;
        double z = ZoomSlider.Value;
        var brush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        for (int x = 1; x < _w; x++)
        {
            var line = new System.Windows.Shapes.Line { X1 = x * z, X2 = x * z, Y1 = 0, Y2 = _h * z, Stroke = brush, StrokeThickness = 1 };
            GridOverlay.Children.Add(line);
        }
        for (int y = 1; y < _h; y++)
        {
            var line = new System.Windows.Shapes.Line { X1 = 0, X2 = _w * z, Y1 = y * z, Y2 = y * z, Stroke = brush, StrokeThickness = 1 };
            GridOverlay.Children.Add(line);
        }
    }

    private void Grid_Changed(object sender, RoutedEventArgs e) => DrawGridOverlay();

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CanvasImage == null) return;
        CanvasImage.Width = _w * ZoomSlider.Value;
        CanvasImage.Height = _h * ZoomSlider.Value;
        ZoomText.Text = ZoomSlider.Value >= 1 ? $"{ZoomSlider.Value:0.#}×" : $"{ZoomSlider.Value:0.##}×";
        DrawGridOverlay();
    }

    // ================= 绘制 =================
    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = GetPixelPos(e);
        if (pos == null) return;
        if (ToolPipette.IsChecked == true)
        {
            var c = PickColor(pos.Value.x, pos.Value.y);
            if (c.HasValue) SetColor(c.Value);
            return;
        }
        _drawing = true;
        CanvasImage.CaptureMouse();
        if (ToolFill.IsChecked == true)
        {
            PushUndo();
            FloodFill(pos.Value.x, pos.Value.y);
            RebuildComposite();
        }
        else
        {
            PushUndo();
            PaintAt(pos.Value.x, pos.Value.y, e.ChangedButton == MouseButton.Right || ToolEraser.IsChecked == true);
            RebuildComposite();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = GetPixelPos(e);
        CoordText.Text = pos == null ? "坐标：-, -" : $"坐标：{pos.Value.x}, {pos.Value.y}";
        if (!_drawing || pos == null) return;
        PaintAt(pos.Value.x, pos.Value.y, e.RightButton == MouseButtonState.Pressed || ToolEraser.IsChecked == true);
        RebuildComposite();
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _drawing = false;
        CanvasImage.ReleaseMouseCapture();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e) => CoordText.Text = "坐标：-, -";

    private (int x, int y)? GetPixelPos(MouseEventArgs e)
    {
        var p = e.GetPosition(CanvasImage);
        int x = (int)(p.X / ZoomSlider.Value);
        int y = (int)(p.Y / ZoomSlider.Value);
        if (x < 0 || y < 0 || x >= _w || y >= _h) return null;
        return (x, y);
    }

    private void PaintAt(int cx, int cy, bool erase)
    {
        var layer = Active;
        if (layer == null) return;
        int size = (int)BrushSize.Value;
        int half = size / 2;
        int colorInt = erase ? 0 : (ColorToInt(_color));
        var rect = new Int32Rect(cx - half, cy - half, size, size);
        int doMirror = SymmetryX.IsChecked == true ? _w - 1 - cx - half : -1;

        ApplyBrush(layer, rect, colorInt);
        if (doMirror >= 0)
            ApplyBrush(layer, new Int32Rect(doMirror, cy - half, size, size), colorInt);
    }

    private void ApplyBrush(PixelLayer layer, Int32Rect rect, int colorInt)
    {
        int[] px = ReadPixels(layer.Bitmap);
        for (int y = Math.Max(0, rect.Y); y < Math.Min(_h, rect.Y + rect.Height); y++)
            for (int x = Math.Max(0, rect.X); x < Math.Min(_w, rect.X + rect.Width); x++)
                px[y * _w + x] = colorInt;
        layer.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), px, _w * 4, 0);
    }

    private void FloodFill(int sx, int sy)
    {
        var layer = Active;
        if (layer == null) return;
        int[] px = ReadPixels(layer.Bitmap);
        int target = px[sy * _w + sx];
        int fill = ColorToInt(_color);
        if (target == fill) return;
        var stack = new Stack<(int, int)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if (x < 0 || y < 0 || x >= _w || y >= _h) continue;
            if (px[y * _w + x] != target) continue;
            px[y * _w + x] = fill;
            stack.Push((x + 1, y)); stack.Push((x - 1, y));
            stack.Push((x, y + 1)); stack.Push((x, y - 1));
        }
        layer.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), px, _w * 4, 0);
    }

    private Color? PickColor(int x, int y)
    {
        if (_composite == null) return null;
        var px = new int[1];
        _composite.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        int v = px[0];
        if ((v >> 24) == 0) return null;
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static int ColorToInt(Color c) => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;

    // ================= 撤销/重做 =================
    private void PushUndo()
    {
        var layer = Active;
        if (layer == null) return;
        _undo.Push((ActiveIndex, ReadPixels(layer.Bitmap)));
        // 撤销步数上限 40：超出时移除最旧记录，避免长时间绘制内存无限增长
        while (_undo.Count > 40)
        {
            var kept = new (int layer, int[] px)[_undo.Count - 1];
            for (int i = kept.Length - 1; i >= 0; i--) kept[i] = _undo.Pop();
            _undo.Clear();
            foreach (var item in kept) _undo.Push(item);
        }
        _redo.Clear();
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var (idx, px) = _undo.Pop();
        var layer = _layers[idx];
        _redo.Push((idx, ReadPixels(layer.Bitmap)));
        layer.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), px, _w * 4, 0);
        RebuildComposite();
        AppContext.SetStatus("已撤销。");
    }

    private void BtnRedo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        var (idx, px) = _redo.Pop();
        var layer = _layers[idx];
        _undo.Push((idx, ReadPixels(layer.Bitmap)));
        layer.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), px, _w * 4, 0);
        RebuildComposite();
        AppContext.SetStatus("已重做。");
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var layer = Active;
        if (layer == null) return;
        // 破坏性操作前确认（清空后画面立即变化，误触代价高）
        var r = MessageBox.Show($"确定清空图层「{layer.Name}」的全部像素吗？\n（清空本身可撤销：按 Ctrl+Z 恢复）",
            "清空图层", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        PushUndo();
        layer.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), new int[_w * _h], _w * 4, 0);
        RebuildComposite();
    }

    // ================= 颜色 =================
    private void BuildPalette()
    {
        string[] colors =
        {
            "#000000","#FFFFFF","#9D9D97","#474F52","#835432","#F9FFFE","#F38BAA","#CDB5A7",
            "#A06E4C","#394B1D","#89AC47","#1639A2","#3C44AA","#8932B8","#8146C4","#EA339F",
            "#D8AF93","#F9801D","#FCFCFC","#FF6B6B","#FFD166","#06D6A0","#118AB2","#073B4C",
        };
        foreach (var hex in colors)
        {
            var b = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x4D)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand, Tag = hex
            };
            b.MouseLeftButtonDown += (_, _) => SetColor((Color)ColorConverter.ConvertFromString(hex));
            PalettePanel.Children.Add(b);
        }
    }

    private void SetColor(Color c)
    {
        _color = c;
        ColorSwatch.Background = new SolidColorBrush(c);
        ColorHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void ColorHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(ColorHex.Text);
            _color = Color.FromRgb(c.R, c.G, c.B);
            ColorSwatch.Background = new SolidColorBrush(_color);
            ColorHex.ToolTip = "十六进制颜色，格式 #RRGGBB（如 #FF6B6B）";
        }
        catch
        {
            // 非法输入即时提示：色样不动 + 悬停可见原因（原来完全静默，用户不知道为何没生效）
            ColorHex.ToolTip = "⚠ 格式无效：请输入 #RRGGBB 形式的十六进制颜色（如 #FF6B6B）";
        }
    }

    // ================= 分辨率转换 =================
    private void BtnApplyRes_Click(object sender, RoutedEventArgs e)
    {
        if (ResCombo.SelectedItem is not ComboBoxItem it || !int.TryParse(it.Content as string, out int nw))
        { MessageBox.Show("请选择目标分辨率。"); return; }
        if (nw == _w && nw == _h) return;

        var newLayers = new List<PixelLayer>();
        foreach (var layer in _layers)
            newLayers.Add(new PixelLayer
            {
                Name = layer.Name,
                Visible = layer.Visible,
                Opacity = layer.Opacity,
                Bitmap = ScaleBitmap(layer.Bitmap, nw, nw)
            });
        _w = nw; _h = nw;
        _layers.Clear();
        foreach (var l in newLayers) _layers.Add(l);
        LayerList.SelectedIndex = Math.Min(LayerList.SelectedIndex, _layers.Count - 1);
        RebuildComposite();
        SizeText.Text = $"{_w} × {_h}";
        AppContext.SetStatus($"分辨率已转换为 {_w}×{_h}（最近邻插值，像素风保持）。");
    }

    private static WriteableBitmap ScaleBitmap(WriteableBitmap src, int nw, int nh)
    {
        var srcPx = ReadPixels(src);
        int sw = src.PixelWidth, sh = src.PixelHeight;
        var dst = MakeBitmap(nw, nh);
        var dstPx = new int[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                int sx = Math.Min(sw - 1, x * sw / nw);
                int sy = Math.Min(sh - 1, y * sh / nh);
                dstPx[y * nw + x] = srcPx[sy * sw + sx];
            }
        dst.WritePixels(new Int32Rect(0, 0, nw, nh), dstPx, nw * 4, 0);
        return dst;
    }

    // ================= 图层 =================
    private void LayerAdd_Click(object sender, RoutedEventArgs e)
    {
        _layers.Add(new PixelLayer { Name = $"图层 {_layers.Count + 1}", Bitmap = MakeBitmap(_w, _h) });
        LayerList.SelectedIndex = _layers.Count - 1;
    }

    private void LayerDel_Click(object sender, RoutedEventArgs e)
    {
        if (_layers.Count <= 1) { MessageBox.Show("至少保留一个图层。"); return; }
        int idx = ActiveIndex;
        _layers.RemoveAt(idx);
        LayerList.SelectedIndex = Math.Max(0, idx - 1);
        RebuildComposite();
    }

    private void LayerRename_Click(object sender, RoutedEventArgs e)
    {
        var layer = Active;
        if (layer == null) return;
        var dlg = new InputDialog("重命名图层", layer.Name) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            layer.Name = dlg.Value;
            LayerList.Items.Refresh();
        }
    }

    private void LayerUp_Click(object sender, RoutedEventArgs e)
    {
        int i = ActiveIndex;
        if (i <= 0) return;
        _layers.Move(i, i - 1);
        LayerList.SelectedIndex = i - 1;
        RebuildComposite();
    }

    private void LayerDown_Click(object sender, RoutedEventArgs e)
    {
        int i = ActiveIndex;
        if (i < 0 || i >= _layers.Count - 1) return;
        _layers.Move(i, i + 1);
        LayerList.SelectedIndex = i + 1;
        RebuildComposite();
    }

    private void LayerMergeDown_Click(object sender, RoutedEventArgs e)
    {
        int i = ActiveIndex;
        if (i <= 0) { MessageBox.Show("没有下方图层可合并。"); return; }
        var top = _layers[i]; var bottom = _layers[i - 1];
        var bpx = ReadPixels(bottom.Bitmap);
        var tpx = ReadPixels(top.Bitmap);
        int oa = (int)(top.Opacity * 255);
        for (int p = 0; p < bpx.Length; p++)
        {
            int a = (byte)(tpx[p] >> 24) * oa / 255;
            if (a == 0) continue;
            int sb = tpx[p] & 0xFF, sg = (tpx[p] >> 8) & 0xFF, sr = (tpx[p] >> 16) & 0xFF;
            int db = bpx[p] & 0xFF, dg = (bpx[p] >> 8) & 0xFF, dr = (bpx[p] >> 16) & 0xFF, da = bpx[p] >> 24;
            int outA = a + da * (255 - a) / 255;
            int outR = outA == 0 ? 0 : (sr * a + dr * da * (255 - a) / 255) / outA;
            int outG = outA == 0 ? 0 : (sg * a + dg * da * (255 - a) / 255) / outA;
            int outB = outA == 0 ? 0 : (sb * a + db * da * (255 - a) / 255) / outA;
            bpx[p] = (outA << 24) | (outR << 16) | (outG << 8) | outB;
        }
        bottom.Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), bpx, _w * 4, 0);
        _layers.RemoveAt(i);
        LayerList.SelectedIndex = i - 1;
        RebuildComposite();
    }

    private void LayerSelected_Changed(object sender, SelectionChangedEventArgs e)
    {
        var layer = Active;
        if (layer == null) return;
        LayerOpacity.Value = layer.Opacity * 100;
        LayerOpacityText.Text = $"{(int)(layer.Opacity * 100)}%";
    }

    private void LayerOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var layer = Active;
        if (layer == null || LayerOpacityText == null) return;
        layer.Opacity = LayerOpacity.Value / 100;
        LayerOpacityText.Text = $"{(int)LayerOpacity.Value}%";
        RebuildComposite();
    }

    private void LayerVis_Changed(object sender, RoutedEventArgs e) => RebuildComposite();

    // ================= 预览 =================
    private void RenderPreview()
    {
        if (PreviewCanvas == null) return;
        PreviewCanvas.Children.Clear();
        if (_composite == null) return;
        var src = _composite;
        double cx = PreviewCanvas.ActualWidth > 0 ? PreviewCanvas.ActualWidth / 2 : 200;
        double cy = 66;

        if (PrevHand.IsChecked == true)
        {
            var img = new Image { Source = src, Width = 88, Height = 88 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            img.RenderTransformOrigin = new Point(0.5, 0.5);
            img.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new RotateTransform(-28),
                    new SkewTransform(0, -14, 0, 0)
                }
            };
            Canvas.SetLeft(img, cx - 44); Canvas.SetTop(img, 24);
            PreviewCanvas.Children.Add(img);
            var lbl = new TextBlock { Text = "手持状态（倾斜 -28°）", FontSize = 11 };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");   // 随主题，固定 Gray 在浅色主题下对比不足
            Canvas.SetLeft(lbl, cx + 64); Canvas.SetTop(lbl, 96);
            PreviewCanvas.Children.Add(lbl);
        }
        else if (PrevPlace.IsChecked == true)
        {
            double s = 52;   // 面尺寸
            var brush = new ImageBrush(src) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.None, Stretch = Stretch.Fill };
            // 顶面（菱形）
            var top = new System.Windows.Shapes.Polygon
            {
                Fill = brush, StrokeThickness = 0, Opacity = 1,
                Points = new PointCollection { new(0, s * 0.25), new(s * 0.5, 0), new(s, s * 0.25), new(s * 0.5, s * 0.5) }
            };
            Canvas.SetLeft(top, cx - s / 2); Canvas.SetTop(top, 22);
            PreviewCanvas.Children.Add(top);
            // 左面
            var left = new System.Windows.Shapes.Polygon
            {
                Fill = brush, Opacity = 0.72, StrokeThickness = 0,
                Points = new PointCollection { new(0, s * 0.25), new(s * 0.5, s * 0.5), new(s * 0.5, s), new(0, s * 0.75) }
            };
            Canvas.SetLeft(left, cx - s / 2); Canvas.SetTop(left, 22);
            PreviewCanvas.Children.Add(left);
            // 右面
            var right = new System.Windows.Shapes.Polygon
            {
                Fill = brush, Opacity = 0.5, StrokeThickness = 0,
                Points = new PointCollection { new(s * 0.5, s * 0.5), new(s, s * 0.25), new(s, s * 0.75), new(s * 0.5, s) }
            };
            Canvas.SetLeft(right, cx - s / 2); Canvas.SetTop(right, 22);
            PreviewCanvas.Children.Add(right);
            var lbl = new TextBlock { Text = "放置状态（等距方块）", FontSize = 11 };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
            Canvas.SetLeft(lbl, cx + 64); Canvas.SetTop(lbl, 96);
            PreviewCanvas.Children.Add(lbl);
        }
        else
        {
            var img = new Image
            {
                Source = src, Width = 96, Height = 96,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 4, Opacity = 0.5, BlurRadius = 8 }
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, cx - 48); Canvas.SetTop(img, 14);
            PreviewCanvas.Children.Add(img);
        }
    }

    private void Preview_Changed(object sender, RoutedEventArgs e) => RenderPreview();

    // ================= 导出 =================
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_composite == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "导出纹理图像",
            Filter = "PNG 图像 (*.png)|*.png|JPEG 图像 (*.jpg)|*.jpg|TIFF 图像 (*.tif)|*.tif",
            FileName = "texture.png"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            BitmapEncoder enc = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            enc.Frames.Add(BitmapFrame.Create(_composite));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
            AppContext.SetStatus($"已导出：{dlg.FileName}");
        }
        catch (Exception ex) { MessageBox.Show("导出失败：" + ex.Message); }
    }

    private void BtnSaveTexture_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) { MessageBox.Show("请先打开项目。"); return; }
        var dir = AppContext.DirTextures(p);
        try { Directory.CreateDirectory(dir); } catch { }
        // 自动命名：依据内容/元数据（尺寸、图层名、主色）生成规范名称并保证目录内唯一
        var suggested = AutoNaming.EnsureUnique(dir,
            AutoNaming.ForTexture(_w, _h, TextureSignature(), p.ModId), ".png");
        var dlg = new InputDialog("纹理名称（已按内容自动生成，可修改）", suggested) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            Directory.CreateDirectory(dir);
            // 文件名消毒：过滤路径非法字符，避免含 ? * 等字符时保存抛异常
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeName = new string(dlg.Value.Trim().Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            var file = Path.Combine(dir, safeName + ".png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_composite!));
            using var fs = File.Create(file);
            enc.Save(fs);
            AppContext.PushTexture(dlg.Value.Trim());
            AppContext.SetStatus($"纹理已保存：{file}（已推送至渲染编辑器）");
        }
        catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message); }
    }

    private void BtnExportPack_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) { MessageBox.Show("请先打开项目。"); return; }
        // 自动命名：与"保存到纹理库"使用同一内容签名，保证命名风格统一
        var suggested = AutoNaming.ForTexture(_w, _h, TextureSignature(), p.ModId);
        var dlg = new InputDialog("纹理名称（已按内容自动生成，将生成 item 模型与资源包结构）", suggested) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            var name = dlg.Value.Trim();
            var ver = Core.Models.McVersions.Find(p.McVersion);
            var outRoot = Path.Combine(p.ProjectDir, "resourcepack");
            var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(_composite!));
            enc.Save(ms);

            var written = ResourcePackExporter.Generate(outRoot, p.ModId.ToLowerInvariant(), ver?.PackFormat ?? 15,
                new[] { ($"item/{name}", ms.ToArray()) });

            var zip = ResourcePackExporter.PackAsZip(outRoot, Path.Combine(p.ProjectDir, $"resourcepack_{p.ModId}.zip"));
            AppContext.SetStatus($"资源包已生成：{outRoot}（{written.Count} 个文件），压缩包：{zip}");
            MessageBox.Show($"资源包结构已生成：\n{outRoot}\n\n可直接放入 .minecraft/resourcepacks 使用。",
                "生成完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("生成失败：" + ex.Message); }
    }

    private void RefreshProjectUi()
    {
        if (AppContext.CurrentProject == null) return;
        AppContext.SetStatus("像素编辑器就绪。");
    }

    /// <summary>纹理内容签名：尺寸 + 图层名 + 当前主色，供自动命名生成稳定哈希</summary>
    private string TextureSignature()
    {
        var layerNames = string.Join("|", _layers.Select(l => l.Name));
        return $"{_w}x{_h}|{layerNames}|{_color}";
    }
}
