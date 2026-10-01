using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MCModCoder.Core.Models;
using MCModCoder.Core.Serialization;
using MCModCoder.Core.Validation;

namespace MCModCoder.Modules;

public class RecipeListItem
{
    public RecipeDefinition Recipe { get; init; } = null!;
    public string Id => string.IsNullOrEmpty(Recipe.Id) ? "(未命名)" : Recipe.Id;
    public string TypeDisplay => Recipe.Type.DisplayName();
    public string CategoryDisplay => Recipe.Category;
}

public class ValidationItemVm
{
    public string Badge { get; set; } = "";
    public string Message { get; set; } = "";
    public string Suggestion { get; set; } = "";
}

public partial class RecipeEditorView : UserControl
{
    private readonly ObservableCollection<RecipeListItem> _all = new();
    private RecipeDefinition? _current;
    private bool _building;

    public RecipeEditorView()
    {
        InitializeComponent();
        RecipeList.ItemsSource = _all;
        foreach (var c in new[] { "全部", "常规", "工具与武器", "食物", "矿物冶炼", "建筑方块", "魔法物品" })
            CategoryFilter.Items.Add(c);
        CategoryFilter.SelectedIndex = 0;
        AppContext.ProjectChanged += LoadFromProject;
        Loaded += (_, _) => LoadFromProject();
        BuildEditor();
    }

    // ============ 数据 ============
    private void LoadFromProject()
    {
        _all.Clear();
        var p = AppContext.CurrentProject;
        if (p == null) { ApplyFilter(); return; }
        var dir = AppContext.DirRecipes(p);
        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); }
        catch { files = Array.Empty<string>(); }   // 目录不可访问时按空处理
        int broken = 0;
        foreach (var file in files)
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(file));
                if (node is not JsonObject root) { broken++; continue; }
                string cat = "常规", desc = "";
                JsonNode? data = root;
                if (root["recipe"] is JsonObject wrapped)
                {
                    cat = root["app"]?["category"]?.GetValue<string>() ?? "常规";
                    desc = root["app"]?["description"]?.GetValue<string>() ?? "";
                    data = wrapped;
                }
                var r = RecipeJsonSerializer.FromJson(Path.GetFileNameWithoutExtension(file), data!.ToJsonString());
                if (r != null) { r.Category = cat; r.Description = desc; _all.Add(new RecipeListItem { Recipe = r }); }
                else broken++;
            }
            catch { broken++; /* 跳过坏文件 */ }
        }
        if (broken > 0)
            AppContext.SetStatus($"⚠ 配方库中有 {broken} 个 JSON 文件无法解析（格式损坏），已跳过。文件位于：{dir}");
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string cat = CategoryFilter.SelectedItem as string ?? "全部";
        string kw = SearchBox.Text?.Trim() ?? "";
        var filtered = _all.Where(it =>
            (cat == "全部" || it.Recipe.Category == cat) &&
            (kw.Length == 0 ||
             it.Recipe.Id.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
             it.Recipe.Category.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
             it.Recipe.Result.Item.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        RecipeList.ItemsSource = new ObservableCollection<RecipeListItem>(filtered);
        ListCountText.Text = $"{filtered.Count} / {_all.Count} 个配方";
    }

    private void Filter_Changed(object sender, EventArgs e) { if (SearchBox != null) ApplyFilter(); }

    private void NewRecipe_Click(object sender, RoutedEventArgs e)
    {
        string modid = AppContext.CurrentProject?.ModId.ToLowerInvariant() ?? "mymod";
        var r = new RecipeDefinition { Id = $"{modid}:new_recipe_{_all.Count + 1}" };
        _all.Add(new RecipeListItem { Recipe = r });
        RecipeList.ItemsSource = _all;
        RecipeList.SelectedItem = _all[^1];
        ApplyFilter();
    }

    private void DelRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (RecipeList.SelectedItem is not RecipeListItem it) return;
        // 删除前确认 + 同步清理已保存到项目的 JSON 文件（否则重进模块配方会"复活"）
        bool fileDeleted = false;
        var p = AppContext.CurrentProject;
        if (p != null && !string.IsNullOrWhiteSpace(it.Recipe.Id))
        {
            var raw = it.Recipe.Id.Contains(':') ? it.Recipe.Id.Split(':', 2)[1] : it.Recipe.Id;
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeName = new string(raw.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
            var file = Path.Combine(AppContext.DirRecipes(p), safeName + ".json");
            if (File.Exists(file))
            {
                var r = MessageBox.Show($"配方「{it.Id}」已保存到项目配方库，删除将同时移除文件：\n{file}\n\n确定删除吗？",
                    "删除配方", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
                try { File.Delete(file); fileDeleted = true; } catch { /* 文件占用时仅从列表移除 */ }
            }
            else
            {
                var r = MessageBox.Show($"确定删除配方「{it.Id}」吗？", "删除配方",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
        }
        _all.Remove(it);
        if (_current == it.Recipe) { _current = null; BuildEditor(); }
        ApplyFilter();
        if (fileDeleted) AppContext.SetStatus($"配方「{it.Id}」已删除（含项目文件）。");
    }

    private void RecipeSelected_Changed(object sender, SelectionChangedEventArgs e)
    {
        _current = (RecipeList.SelectedItem as RecipeListItem)?.Recipe;
        BuildEditor();
        Validate(false);
    }

    private void RefreshListText()
    {
        if (RecipeList.SelectedItem is RecipeListItem it)
            RecipeList.Items.Refresh();
    }

    // ============ 编辑器构建（按类型动态生成） ============
    private void BuildEditor()
    {
        _building = true;
        EditorPanel.Children.Clear();
        if (_current == null)
        {
            EditorPanel.Children.Add(new TextBlock
            {
                Text = "← 在左侧新建或选择一个配方开始编辑。\n支持：有序/无序合成、熔炉/高炉/烟熏/篝火、切石机、锻造台、酿造台。",
                Foreground = (Brush)FindResource("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4)
            });
            _building = false;
            return;
        }
        var r = _current;

        // 基本：类型 / ID / 分类
        var typeCombo = new ComboBox { Width = 240 };
        foreach (RecipeType t in Enum.GetValues<RecipeType>()) typeCombo.Items.Add(t.DisplayName());
        typeCombo.SelectedIndex = (int)r.Type;
        typeCombo.SelectionChanged += (_, _) =>
        {
            if (_building || typeCombo.SelectedIndex < 0) return;
            r.Type = (RecipeType)typeCombo.SelectedIndex;
            RefreshListText();
            BuildEditor();
        };

        var idBox = new TextBox { Text = r.Id, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        idBox.TextChanged += (_, _) => { if (!_building) { r.Id = idBox.Text.Trim(); RefreshListText(); } };

        var catBox = new TextBox { Text = r.Category, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        catBox.TextChanged += (_, _) => { if (!_building) { r.Category = catBox.Text.Trim(); RefreshListText(); } };

        EditorPanel.Children.Add(Wrap("配方类型", typeCombo));
        EditorPanel.Children.Add(Wrap("配方 ID（命名空间:路径）", idBox));
        EditorPanel.Children.Add(Wrap("分类", catBox));

        // 分区：输入区
        var inputPanel = new StackPanel();
        EditorPanel.Children.Add(new TextBlock { Text = "材料", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });

        if (r.Type == RecipeType.CraftingShaped)
        {
            var grid = BuildSlotGrid(3, 3, r, shaped: true);
            inputPanel.Children.Add(grid);
        }
        else if (r.Type == RecipeType.CraftingShapeless)
        {
            var grid = BuildSlotGrid(3, 3, r, shaped: false);
            inputPanel.Children.Add(grid);
            inputPanel.Children.Add(new TextBlock
            {
                Text = "提示：从下方物品面板拖拽到格子；无序合成忽略摆放顺序（最多 9 项）。",
                Foreground = (Brush)FindResource("TextDimBrush"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0)
            });
        }
        else if (r.Type.IsCooking() || r.Type == RecipeType.Stonecutting)
        {
            inputPanel.Children.Add(IngRow("输入材料 ingredient", r, nameof(r.Ingredient)));
            if (r.Type.IsCooking())
            {
                var timeBox = NumBox(r.CookingTime, v => r.CookingTime = (int)v, 20, 20000);
                var xpBox = NumBox((decimal)r.Experience, v => r.Experience = (double)v, 0, 1000);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(timeBox);
                sp.Children.Add(new TextBlock { Text = " 刻（cookingtime）   经验 ", VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(xpBox);
                inputPanel.Children.Add(Wrap("烧炼时间与经验", sp));
            }
        }
        else if (r.Type == RecipeType.Smithing)
        {
            inputPanel.Children.Add(IngRow("锻造模板 template（可选）", r, "Template"));
            inputPanel.Children.Add(IngRow("基础物品 base", r, "Base"));
            inputPanel.Children.Add(IngRow("附加材料 addition", r, "Addition"));
        }
        else if (r.Type == RecipeType.Brewing)
        {
            inputPanel.Children.Add(IngRow("酿造材料 ingredient", r, nameof(r.Ingredient)));
            inputPanel.Children.Add(IngRow("底酒 base（如水瓶）", r, "BrewingBase"));
            var timeBox = NumBox(r.CookingTime, v => r.CookingTime = (int)v, 20, 800);
            inputPanel.Children.Add(Wrap("酿造时间（刻）", timeBox));
        }
        EditorPanel.Children.Add(inputPanel);

        // 产物区
        EditorPanel.Children.Add(new TextBlock { Text = "产物", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        var resultRow = new StackPanel { Orientation = Orientation.Horizontal };
        var resBox = new TextBox { Text = r.Result.Item, Width = 230 };
        resBox.TextChanged += (_, _) => { if (!_building) { r.Result.Item = resBox.Text.Trim(); RefreshListText(); } };
        var cntBox = new TextBox { Text = r.Result.Count.ToString(), Width = 54, Margin = new Thickness(8, 0, 0, 0) };
        cntBox.TextChanged += (_, _) => { if (!_building && int.TryParse(cntBox.Text, out var c)) r.Result.Count = c; };
        resultRow.Children.Add(resBox);
        resultRow.Children.Add(new TextBlock { Text = " ×数量", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextDimBrush") });
        resultRow.Children.Add(cntBox);
        EditorPanel.Children.Add(resultRow);

        // 物品拖拽面板
        EditorPanel.Children.Add(new TextBlock { Text = "物品面板（拖拽到格子 / 点击后点击格子）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        var palette = BuildPalette();
        EditorPanel.Children.Add(palette);

        EditorPanel.Children.Add(new TextBlock
        {
            Text = "常用原版物品：air, stone, stick, iron_ingot, gold_ingot, diamond, coal, iron_ore…\n也可在物品框中输入任意 ID（如 mymod:ruby）或标签（如 #minecraft:planks）。",
            Foreground = (Brush)FindResource("TextDimBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        _building = false;
    }

    private UIElement Wrap(string label, UIElement content)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("TextDimBrush"), FontSize = 12 });
        sp.Children.Add(content);
        return sp;
    }

    private TextBox NumBox(decimal value, Action<decimal> set, decimal min, decimal max)
    {
        var b = new TextBox { Text = value.ToString(), Width = 80, TextAlignment = TextAlignment.Right };
        b.TextChanged += (_, _) => { if (!_building && decimal.TryParse(b.Text, out var v)) set(Math.Clamp(v, min, max)); };
        return b;
    }

    // ---- 槽位系统 ----
    private readonly Dictionary<(int row, int col), string> _shapedSlots = new();
    private readonly List<string?> _shapelessSlots = new(Enumerable.Repeat<string?>(null, 9));
    private Border? _pendingSlotTarget;

    private Grid BuildSlotGrid(int rows, int cols, RecipeDefinition r, bool shaped)
    {
        if (shaped)
        {
            _shapedSlots.Clear();
            for (int row = 0; row < 3; row++)
            {
                if (row >= r.Pattern.Count) break;   // 图案行数不足时防御（外部导入数据）
                for (int col = 0; col < 3 && col < r.Pattern[row].Length; col++)
                {
                    char ch = r.Pattern[row][col];
                    if (ch != ' ' && r.Key.TryGetValue(ch.ToString(), out var ing))
                        _shapedSlots[(row, col)] = ing.Display;
                }
            }
        }
        else
        {
            _shapelessSlots.Clear();
            for (int i = 0; i < 9; i++) _shapelessSlots.Add(i < r.ShapelessIngredients.Count ? r.ShapelessIngredients[i].Display : null);
        }

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < cols; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        for (int i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });

        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                var slot = MakeSlot();
                string? val = shaped ? _shapedSlots.GetValueOrDefault((row, col)) : _shapelessSlots[row * cols + col];
                SetSlotVisual(slot, val);
                slot.Tag = (row, col, shaped);
                slot.AllowDrop = true;
                slot.Drop += (_, e) =>
                {
                    var s = e.Data.GetData(DataFormats.StringFormat) as string;
                    if (string.IsNullOrWhiteSpace(s)) return;
                    var (rr, cc, isShaped) = ((int, int, bool))slot.Tag!;
                    AssignSlot(rr, cc, isShaped, s, slot, r);
                };
                slot.MouseLeftButtonDown += (_, _) =>
                {
                    // 点击已有内容 → 清除；点击空格且剪贴板有待放置 → 放置
                    var (rr, cc, isShaped) = ((int, int, bool))slot.Tag!;
                    var cur = isShaped ? _shapedSlots.GetValueOrDefault((rr, cc)) : _shapelessSlots[rr * cols + cc];
                    if (!string.IsNullOrEmpty(cur))
                        AssignSlot(rr, cc, isShaped, null, slot, r);
                    else if (Clipboard.ContainsText())
                        AssignSlot(rr, cc, isShaped, Clipboard.GetText(), slot, r);
                };
                Grid.SetRow(slot, row);
                Grid.SetColumn(slot, col);
                grid.Children.Add(slot);
            }
        return grid;
    }

    private Border MakeSlot()
    {
        return new Border
        {
            Width = 52, Height = 52, Margin = new Thickness(2),
            Background = (Brush)FindResource("SlotDarkBrush"),
            BorderBrush = (Brush)FindResource("SlotBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand
        };
    }

    private void SetSlotVisual(Border slot, string? val)
    {
        slot.Child = string.IsNullOrEmpty(val) ? null : new TextBlock
        {
            Text = ShortName(val), FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        slot.ToolTip = val;
    }

    private static string ShortName(string s) => s.Replace("minecraft:", "");

    private void AssignSlot(int row, int col, bool shaped, string? val, Border slot, RecipeDefinition r)
    {
        val = string.IsNullOrWhiteSpace(val) ? null : val.Trim();
        SetSlotVisual(slot, val);
        if (shaped)
        {
            if (val == null) _shapedSlots.Remove((row, col));
            else _shapedSlots[(row, col)] = val;
            // 重建 pattern/key
            var letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ#";
            var itemToChar = new Dictionary<string, char>();
            r.Key.Clear();
            var pattern = new List<string>();
            for (int rr = 0; rr < 3; rr++)
            {
                var line = new System.Text.StringBuilder();
                for (int cc = 0; cc < 3; cc++)
                {
                    if (!_shapedSlots.TryGetValue((rr, cc), out var item)) { line.Append(' '); continue; }
                    if (!itemToChar.TryGetValue(item, out var ch))
                    {
                        ch = letters[itemToChar.Count % letters.Length];
                        itemToChar[item] = ch;
                        r.Key[ch.ToString()] = IngredientSpec.Parse(item);
                    }
                    line.Append(ch);
                }
                pattern.Add(line.ToString());
            }
            r.Pattern = pattern;
        }
        else
        {
            _shapelessSlots[row * 3 + col] = val;
            r.ShapelessIngredients = _shapelessSlots.Where(v => !string.IsNullOrEmpty(v))
                .Select(v => IngredientSpec.Parse(v!)).ToList();
        }
        Validate(false);
    }

    private UIElement IngRow(string label, RecipeDefinition r, string field)
    {
        var box = new TextBox { Width = 240, Text = field switch
        {
            "Template" => r.Template?.Display ?? "",
            "Base" => r.Base?.Display ?? "",
            "Addition" => r.Addition?.Display ?? "",
            "BrewingBase" => r.BrewingBase?.Display ?? "",
            _ => r.Ingredient.Display
        } };
        box.TextChanged += (_, _) =>
        {
            if (_building) return;
            var ing = string.IsNullOrWhiteSpace(box.Text) ? null : IngredientSpec.Parse(box.Text);
            switch (field)
            {
                case "Template": r.Template = ing; break;
                case "Base": r.Base = ing; break;
                case "Addition": r.Addition = ing; break;
                case "BrewingBase": r.BrewingBase = ing; break;
                default: r.Ingredient = ing ?? new IngredientSpec(); break;
            }
            Validate(false);
        };
        return Wrap(label, box);
    }

    private UIElement BuildPalette()
    {
        string[] items =
        {
            "minecraft:stick", "minecraft:iron_ingot", "minecraft:gold_ingot", "minecraft:copper_ingot",
            "minecraft:diamond", "minecraft:coal", "minecraft:iron_ore", "minecraft:diamond_sword",
            "#minecraft:planks", "#minecraft:logs", "minecraft:glass", "minecraft:string",
            "minecraft:nether_wart", "minecraft:potion", "minecraft:netherite_ingot",
            "minecraft:blaze_rod", "minecraft:ender_pearl", "minecraft:leather"
        };
        var panel = new WrapPanel();
        foreach (var it in items)
        {
            var b = new Border
            {
                Width = 92, Margin = new Thickness(3), Padding = new Thickness(6, 4, 6, 4),
                Background = (Brush)FindResource("Panel2Brush"),
                BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Tag = it
            };
            b.Child = new TextBlock { Text = ShortName(it), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            b.MouseLeftButtonDown += (sender, e) =>
            {
                try { Clipboard.SetText((string)((Border)sender).Tag); } catch { }
                DragDrop.DoDragDrop(b, new DataObject(DataFormats.StringFormat, (string)b.Tag), DragDropEffects.Copy);
            };
            panel.Children.Add(b);
        }
        return panel;
    }

    // ============ 验证 ============
    private void Validate(bool popup)
    {
        var items = new List<ValidationItemVm>();
        if (_current != null)
        {
            foreach (var issue in RecipeValidator.Validate(_current))
                items.Add(new ValidationItemVm
                {
                    Badge = issue.Severity switch { IssueSeverity.Error => "✖错误", IssueSeverity.Warning => "⚠警告", _ => "ⓘ提示" },
                    Message = issue.Message, Suggestion = issue.Suggestion
                });
        }
        ValidationList.ItemsSource = items;
        if (popup && items.Count == 0)
            MessageBox.Show("当前配方验证通过，没有发现问题。", "验证结果", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Validate_Click(object sender, RoutedEventArgs e) => Validate(true);

    private void ValidateAll_Click(object sender, RoutedEventArgs e)
    {
        int bad = 0, warn = 0;
        foreach (var it in _all)
        {
            var issues = RecipeValidator.Validate(it.Recipe);
            bad += issues.Count(i => i.Severity == IssueSeverity.Error);
            warn += issues.Count(i => i.Severity == IssueSeverity.Warning);
        }
        MessageBox.Show($"全部 {_all.Count} 个配方验证完成：\n错误 {bad} 个，警告 {warn} 个。", "批量验证",
            MessageBoxButton.OK, bad > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ============ JSON 导入 / 导出 ============
    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) { MessageBox.Show("请先选择配方。"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        { Filter = "JSON 配方 (*.json)|*.json", FileName = _current.Id.Split(':').LastOrDefault() + ".json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, RecipeJsonSerializer.ToDatapackJson(_current));
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        AppContext.SetStatus($"配方已导出：{dlg.FileName}");
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON 配方 (*.json)|*.json|全部文件 (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        int ok = 0, failed = 0;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var r = RecipeJsonSerializer.FromJson(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
                if (r == null) { failed++; continue; }
                r.Category = "导入";
                _all.Add(new RecipeListItem { Recipe = r });
                ok++;
            }
            catch { failed++; }   // 单个文件损坏不影响其余导入
        }
        ApplyFilter();
        MessageBox.Show(failed > 0
            ? $"成功导入 {ok} 个配方，{failed} 个文件解析失败（JSON 格式损坏或不受支持）。"
            : $"成功导入 {ok} 个配方。", "导入完成",
            MessageBoxButton.OK, failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ExportPack_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null || _all.Count == 0) { MessageBox.Show("请先打开项目并确保有配方。"); return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择数据包输出目录" };
        if (dlg.ShowDialog() != true) return;
        var ver = Core.Models.McVersions.Find(p.McVersion);
        var files = RecipeJsonSerializer.ExportToDatapack(_all.Select(i => i.Recipe), dlg.FolderName,
            p.ModId.ToLowerInvariant(), ver?.DataPackFormat ?? 15);
        MessageBox.Show($"已导出 {files.Count} 个文件到：\n{dlg.FolderName}\n\n该目录即为完整数据包，可直接放入存档 datapacks 使用。",
            "数据包导出", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var p = AppContext.CurrentProject;
        if (p == null) { MessageBox.Show("请先打开项目。"); return; }
        var dir = AppContext.DirRecipes(p);
        var invalidChars = Path.GetInvalidFileNameChars();
        int saved = 0, failed = 0;
        foreach (var it in _all)
        {
            var raw = it.Recipe.Id.Contains(':') ? it.Recipe.Id.Split(':', 2)[1] : it.Recipe.Id;
            // 文件名净化：替换非法字符，避免保存时抛异常
            var path = new string(raw.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var wrapper = new JsonObject
                {
                    ["app"] = new JsonObject { ["category"] = it.Recipe.Category, ["description"] = it.Recipe.Description },
                    ["recipe"] = JsonNode.Parse(RecipeJsonSerializer.ToDatapackJson(it.Recipe))
                };
                File.WriteAllText(Path.Combine(dir, path + ".json"), wrapper.ToJsonString(new() { WriteIndented = true }));
                saved++;
            }
            catch { failed++; }
        }
        var summary = $"已保存 {saved} / {_all.Count} 个配方到 {dir}" + (failed > 0 ? $"（{failed} 个失败）" : "");
        AppContext.SetStatus(summary);
        MessageBox.Show(failed > 0 ? summary : $"已保存 {_all.Count} 个配方到项目配方库。",
            failed > 0 ? "保存完成（部分失败）" : "保存完成", MessageBoxButton.OK,
            failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
