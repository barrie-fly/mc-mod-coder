using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MCModCoder.Core.Models;
using MCModCoder.Core.Services;
using Microsoft.Win32;

namespace MCModCoder.Modules;

public partial class CodeEditorView : UserControl
{
    private string? _filePath;
    private bool _highlighting;
    private bool _dirty;            // 有未保存更改时为 true（文件标签加 * 提示，切换/退出前确认）
    private bool _gitBusy;          // Git 命令执行中（防并发）
    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _aiTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _reviewTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private List<CodeSnippet> _userSnippets = new();
    private string? _lastOp;   // 最近开发操作（供 AI 助手做上下文感知）

    private static readonly string[] Keywords =
    {
        "public","private","protected","static","final","void","return","if","else","for","while","new","class",
        "extends","implements","import","package","boolean","int","float","double","long","short","byte","char",
        "true","false","null","this","super","abstract","interface","enum","try","catch","finally","throw","throws",
        "instanceof","switch","case","break","continue","default","synchronized","volatile","transient","native","var","record"
    };

    private static readonly string[] McTypes =
    {
        "Item","Block","BlockState","Level","Player","ServerPlayer","BlockPos","Component","ItemStack",
        "Mod","ModInitializer","MinecraftForge","DeferredRegister","RegistryObject","ForgeRegistries",
        "ItemProperties","BlockBehaviour","EquipmentSlot","MobEffects","MobEffectInstance","SoundType",
        "EventHandler","SubscribeEvent","TagKey","ResourceLocation","Identifier","Registries","Registry",
        "CreativeModeTab","FoodProperties","InteractionResult","Direction","CompoundTag","RandomSource",
        "Entity","LivingEntity","World","NBTTagCompound","GameRegistry","ObjectHolder","EventBusSubscriber"
    };

    public CodeEditorView()
    {
        InitializeComponent();
        _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); Highlight(); };
        _aiTimer.Tick += (_, _) => { _aiTimer.Stop(); ShowAiSuggestions(); };
        _reviewTimer.Tick += (_, _) => { _reviewTimer.Stop(); RefreshAssistant(); };
        LoadSnippets();
        SetStarterCode();
        RefreshAssistant();
        AppContext.OpenCodeFileRequested += OnOpenCodeFileRequested;
        Unloaded += (_, _) => AppContext.OpenCodeFileRequested -= OnOpenCodeFileRequested;
    }

    /// <summary>全局搜索跳转：在编辑器中打开文件并定位到指定行</summary>
    private void OnOpenCodeFileRequested(string path, int line)
    {
        if (!ConfirmDiscardIfDirty()) return;   // 跳转前确认，避免静默丢弃未保存内容
        try
        {
            SetText(File.ReadAllText(path));
            _filePath = path;
            _dirty = false;
            UpdateFileLabel();
            CodeStatus.Text = "已打开：" + path + (line > 0 ? $"（第 {line} 行）" : "");
            TrackOp("打开文件");
            if (line > 0) JumpToLine(line);
            Editor.Focus();
        }
        catch (Exception ex)
        {
            CodeStatus.Text = "打开文件失败：" + ex.Message;
        }
    }

    /// <summary>存在未保存更改时弹出三态确认；返回 false 表示用户选择取消操作</summary>
    private bool ConfirmDiscardIfDirty()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show("当前代码有未保存的更改，继续将丢弃这些更改。\n\n是否继续？",
            "未保存的更改", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    /// <summary>文件标签显示（脏状态加 * 前缀），完整路径放 ToolTip</summary>
    private void UpdateFileLabel()
    {
        FileLabel.Text = (_dirty ? "* " : "") + (_filePath ?? "（未命名）");
    }

    /// <summary>是否存在未保存更改（主窗口关闭前确认用）</summary>
    public bool HasUnsavedChanges => _dirty;

    /// <summary>保存当前文件（供外部如主窗口退出前调用）；返回是否保存成功（取消/失败视为未保存）</summary>
    public bool SaveFromExternal()
    {
        SaveFile_Click(this, new RoutedEventArgs());
        return !_dirty;
    }

    /// <summary>把光标移动到指定行并滚动到可视区（换行符按 1 个符号位计算）</summary>
    private void JumpToLine(int line)
    {
        try
        {
            string text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text;
            string norm = text.Replace("\r\n", "\n");
            var lines = norm.Split('\n');
            if (line > lines.Length) line = lines.Length;
            if (line < 1) return;

            int offset = 0;
            for (int i = 0; i < line - 1; i++) offset += lines[i].Length + 1;

            var tp = Editor.Document.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
            if (tp == null) return;
            var lineEnd = tp.GetPositionAtOffset(lines[line - 1].Length, LogicalDirection.Forward) ?? tp;
            Editor.Selection.Select(tp, lineEnd);
            Editor.CaretPosition = tp;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var rect = tp.GetCharacterRect(LogicalDirection.Forward);
                    if (rect.Y > 0) Editor.ScrollToVerticalOffset(Math.Max(0, rect.Y - 80));
                }
                catch { }
            }), DispatcherPriority.Loaded);
        }
        catch { /* 定位失败不影响打开 */ }
    }

    private void LoadSnippets()
    {
        _userSnippets = SnippetLibrary.LoadUser();
        SnippetList.ItemsSource = SnippetLibrary.Builtin.Concat(_userSnippets).ToList();
    }

    private void SetStarterCode()
    {
        string modid = AppContext.CurrentProject?.ModId.ToLowerInvariant() ?? "mymod";
        string pkg = AppContext.CurrentProject?.Package ?? "com.example.mymod";
        SetText($$"""
// {{(AppContext.CurrentProject?.Name ?? "MyMod")}} — 主逻辑
// 快捷键：Ctrl+S 保存 · 片段库双击插入 · AI 辅助可在右上角关闭
package {{pkg}};

import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.eventbus.api.SubscribeEvent;
import net.minecraftforge.event.entity.player.PlayerInteractEvent;
import net.minecraft.network.chat.Component;
import net.minecraft.world.level.Level;
import net.minecraft.world.entity.player.Player;

@Mod("{{modid}}")
public class ExampleEvents {

    @SubscribeEvent
    public void onRightClickBlock(PlayerInteractEvent.RightClickBlock event) {
        Level level = event.getLevel();
        Player player = event.getEntity();
        if (!level.isClientSide()) {
            player.sendSystemMessage(Component.literal("Hello {{modid}}!"));
        }
    }
}
""");
        _filePath = null;
        _dirty = false;
        UpdateFileLabel();
    }

    // ================= 文本操作 =================
    private string GetText()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        return range.Text.Replace("\r\n", "\n").TrimEnd('\n');
    }

    private void SetText(string text)
    {
        _highlighting = true;
        Editor.Document.Blocks.Clear();
        Editor.Document.Blocks.Add(new Paragraph(new Run(text)));
        _highlighting = false;
        ScheduleHighlight();
    }

    private void InsertAtCaret(string text)
    {
        Editor.CaretPosition.InsertTextInRun(text);
        Editor.Focus();
        ScheduleHighlight();
    }

    // ================= 语法高亮 =================
    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_highlighting) return;
        _dirty = true;
        UpdateFileLabel();
        ScheduleHighlight();
        _aiTimer.Stop();
        _reviewTimer.Stop();
        if (AiToggle.IsChecked == true) _aiTimer.Start();
        if (InsightToggle.IsChecked == true) _reviewTimer.Start();
        CheckErrors();
    }

    private void ScheduleHighlight()
    {
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private void Highlight()
    {
        if (_highlighting) return;
        _highlighting = true;
        try
        {
            var doc = Editor.Document;
            string text = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
            if (text.Length > 300_000) { _highlighting = false; return; }   // 超大文件不高亮
            string norm = text.Replace("\r\n", "\n");
            ApplyTokens(doc, norm);
        }
        catch { /* 高亮失败不影响编辑 */ }
        finally { _highlighting = false; }
    }

    private void ApplyTokens(FlowDocument doc, string norm)
    {
        var pattern = new Regex(
            @"(?<comment>//[^\n]*|/\*.*?\*/)" +
            @"|(?<string>@?""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')" +
            @"|(?<annot>@\w+)" +
            @"|(?<mctype>\b(?:" + string.Join("|", McTypes) + @")\b)" +
            @"|(?<kw>\b(?:" + string.Join("|", Keywords) + @")\b)" +
            @"|(?<num>\b\d+(?:\.\d+)?[fFdDlL]?\b)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var tokens = new List<(int start, int len, Brush brush)>();
        foreach (Match m in pattern.Matches(norm))
        {
            string? group = m.Groups["comment"].Success ? "comment"
                : m.Groups["string"].Success ? "string"
                : m.Groups["annot"].Success ? "annot"
                : m.Groups["mctype"].Success ? "mctype"
                : m.Groups["kw"].Success ? "kw"
                : "num";
            var brush = group switch
            {
                "comment" => Brushes.DarkSeaGreen,
                "string" => Brushes.MediumSeaGreen,
                "annot" => Brushes.Goldenrod,
                "mctype" => Brushes.SkyBlue,
                "kw" => Brushes.Orchid,
                _ => Brushes.SandyBrown
            };
            tokens.Add((m.Index, m.Length, brush));
        }

        Editor.BeginChange();
        var tp = doc.ContentStart;
        int cur = 0;
        foreach (var (start, len, brush) in tokens)
        {
            // 推进到 token 起点
            while (cur < start)
            {
                var next = tp.GetNextInsertionPosition(LogicalDirection.Forward);
                if (next == null) break;
                tp = next; cur++;
            }
            if (cur < start) break;
            var startTp = tp;
            int end = Math.Min(norm.Length, start + len);
            while (cur < end)
            {
                var next = tp.GetNextInsertionPosition(LogicalDirection.Forward);
                if (next == null) break;
                tp = next; cur++;
            }
            if (startTp != null && tp != null && startTp.CompareTo(tp) < 0)
                new TextRange(startTp, tp).ApplyPropertyValue(TextElement.ForegroundProperty, brush);
        }
        Editor.EndChange();
    }

    // ================= 错误提示（轻量静态检查） =================
    private void CheckErrors()
    {
        var errors = new List<string>();
        string text = GetText();
        int braces = 0, parens = 0, brackets = 0;
        foreach (var c in text)
        {
            if (c == '{') braces++; else if (c == '}') braces--;
            if (c == '(') parens++; else if (c == ')') parens--;
            if (c == '[') brackets++; else if (c == ']') brackets--;
        }
        if (braces != 0) errors.Add($"大括号不匹配：'{{' 与 '}}' 数量差 {braces}。");
        if (parens != 0) errors.Add($"圆括号不匹配：'(' 与 ')' 数量差 {parens}。");
        if (brackets != 0) errors.Add($"方括号不匹配：'[' 与 ']' 数量差 {brackets}。");
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("@")) continue;
            if (line.StartsWith("package ") && !line.EndsWith(";")) errors.Add($"第 {i + 1} 行：package 语句缺少分号。");
            if (Regex.IsMatch(line, @"\bclass\s+\w+\s*$")) errors.Add($"第 {i + 1} 行：class 声明后应有 '{{'。");
            if (line.EndsWith(");") == false && line.EndsWith("(") && line.Length > 3 && !line.Contains("comment"))
            { /* 忽略换行续行 */ }
        }
        ErrorList.ItemsSource = errors;
        ErrorSummary.Text = errors.Count == 0 ? "✔ 无静态检查问题" : $"⚠ {errors.Count} 个提示";
        ErrorSummary.Foreground = errors.Count == 0 ? FindResource("GreenBrush") as Brush : FindResource("YellowBrush") as Brush;
    }

    // ================= AI 辅助补全 =================
    private void ShowAiSuggestions()
    {
        if (AiToggle.IsChecked != true) return;
        var caret = Editor.CaretPosition;
        var textBefore = new TextRange(Editor.Document.ContentStart, caret).Text;
        if (textBefore.Length == 0) { AiPopup.IsOpen = false; return; }
        int lineStart = textBefore.LastIndexOf('\n');
        var curLine = textBefore[(lineStart + 1)..];
        var wordMatch = Regex.Match(curLine, @"[\w$]+$");
        string word = wordMatch.Success ? wordMatch.Value : "";

        var items = CodeAssist.GetCompletions(textBefore.Replace("\r\n", "\n"), word);

        // 文档词库补全：扫描全文标识符（前缀优先 + 频次排序），与上下文规则合并去重
        if (word.Length >= 2)
        {
            var seen = new HashSet<string>(items.Select(i => i.Label), StringComparer.Ordinal);
            items.AddRange(CodeAssist.GetWordCompletions(GetText(), word, 6)
                .Where(w => seen.Add(w.Label)));
            // 前缀匹配优先展示
            items = items.OrderByDescending(i => i.Label.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                         .ToList();
        }
        else
        {
            // 新行/无前缀 → 语句级"下一句预测"（与词级补全互补）
            var seen = new HashSet<string>(items.Select(i => i.Label), StringComparer.Ordinal);
            items.AddRange(DevAssistant.PredictNext(textBefore)
                .Where(p => seen.Add(p.Label)));
        }

        if (items.Count == 0) { AiPopup.IsOpen = false; return; }

        AiList.ItemsSource = items;
        AiList.SelectedIndex = 0;
        var rect = caret.GetCharacterRect(LogicalDirection.Backward);
        AiPopup.PlacementTarget = Editor;
        AiPopup.HorizontalOffset = rect.X + 14;
        AiPopup.VerticalOffset = rect.Y + 26;
        AiPopup.IsOpen = true;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (AiPopup.IsOpen)
        {
            if (e.Key == Key.Escape) { AiPopup.IsOpen = false; e.Handled = true; return; }
            if (e.Key == Key.Down) { MoveAiSelection(1); e.Handled = true; return; }
            if (e.Key == Key.Up) { MoveAiSelection(-1); e.Handled = true; return; }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                AcceptAi();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SaveFile_Click(sender, e); e.Handled = true; }
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OpenFind(); e.Handled = true; }
        if (e.Key == Key.F3) { if (_findOffsets.Count > 0) FindStep(1); else OpenFind(); e.Handled = true; }
    }

    // ================= 查找（Ctrl+F） =================
    private readonly List<int> _findOffsets = new();
    private int _findIdx = -1;

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _findIdx = -1;
        RefreshMatches(advance: false);
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        if (e.Key == Key.Escape) { FindClose_Click(sender, e); e.Handled = true; }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindStep(-1);

    /// <summary>重建匹配列表（换行符按 1 个符号位计算，与 RichTextBox 偏移一致）</summary>
    private void RefreshMatches(bool advance)
    {
        _findOffsets.Clear();
        string kw = FindBox.Text ?? "";
        if (kw.Length > 0)
        {
            string text = GetText();
            int i = 0;
            while ((i = text.IndexOf(kw, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                _findOffsets.Add(i);
                i += kw.Length;
            }
        }
        FindCount.Text = _findOffsets.Count == 0 ? "无结果" : $"{_findOffsets.Count} 个结果";
        if (_findOffsets.Count == 0) { _findIdx = -1; return; }
        _findIdx = advance ? (_findIdx < 0 ? 0 : _findIdx) : 0;
        SelectFindOffset(_findOffsets[_findIdx], kw.Length);
    }

    private void FindStep(int dir)
    {
        if (_findOffsets.Count == 0) { RefreshMatches(advance: false); if (_findOffsets.Count == 0) return; }
        _findIdx = ((_findIdx + dir) % _findOffsets.Count + _findOffsets.Count) % _findOffsets.Count;
        SelectFindOffset(_findOffsets[_findIdx], (FindBox.Text ?? "").Length);
    }

    private void SelectFindOffset(int offset, int len)
    {
        try
        {
            var start = Editor.Document.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
            var end = start?.GetPositionAtOffset(Math.Max(1, len), LogicalDirection.Forward);
            if (start != null && end != null)
            {
                Editor.Selection.Select(start, end);
                Editor.Focus();
                var rect = start.GetCharacterRect(LogicalDirection.Forward);
                if (rect.Y > 0) Editor.ScrollToVerticalOffset(Math.Max(0, rect.Y - 80));
            }
        }
        catch { /* 偏移越界时忽略 */ }
    }

    private void MoveAiSelection(int delta)
    {
        int i = AiList.SelectedIndex + delta;
        if (i >= 0 && i < AiList.Items.Count) AiList.SelectedIndex = i;
    }

    private void AcceptAi()
    {
        if (AiList.SelectedItem is not CompletionItem item) { AiPopup.IsOpen = false; return; }
        // 移除光标前已输入的前缀词
        var caret = Editor.CaretPosition;
        var textBefore = new TextRange(Editor.Document.ContentStart, caret).Text;
        var curLine = textBefore[(textBefore.LastIndexOf('\n') + 1)..];
        var wordMatch = Regex.Match(curLine, @"[\w$]+$");
        if (wordMatch.Success && wordMatch.Length > 0)
        {
            var start = caret.GetPositionAtOffset(-wordMatch.Length, LogicalDirection.Backward);
            if (start != null) new TextRange(start, caret).Text = "";
        }
        InsertAtCaret(item.Insert);
        AiPopup.IsOpen = false;
        CodeStatus.Text = $"AI 已插入：{item.Label}（{item.Detail}）";
    }

    private void AiList_DoubleClick(object sender, MouseButtonEventArgs e) => AcceptAi();

    private void AiToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AiPopup == null) return;
        if (AiToggle.IsChecked != true) AiPopup.IsOpen = false;
        CodeStatus.Text = AiToggle.IsChecked == true
            ? "AI 辅助已开启：输入代码时将给出上下文补全。"
            : "AI 辅助已关闭：完全自主编写模式。";
    }

    // ================= AI 开发助手（实时建议 + 内容点评） =================
    private void InsightToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SuggestionList == null) return;   // XAML 初始化期间先于控件就绪
        RefreshAssistant();
        AppContext.SetStatus(InsightToggle.IsChecked == true
            ? "AI 开发助手已开启：实时建议与内容点评将随输入更新。"
            : "AI 开发助手已关闭。");
    }

    /// <summary>刷新右侧 AI 助手面板：实时建议（含操作上下文）+ 内容点评（亮点/改进）</summary>
    private void RefreshAssistant()
    {
        if (SuggestionList == null || ScoreText == null) return;
        if (InsightToggle.IsChecked != true)
        {
            SuggestionList.ItemsSource = null;
            SuggestionEmpty.Text = "AI 助手已关闭，可随时在右上角重新开启。";
            SuggestionEmpty.Visibility = Visibility.Visible;
            HighlightList.ItemsSource = null;
            ImproveList.ItemsSource = null;
            ScoreText.Text = "—";
            ReviewSummary.Text = "";
            return;
        }

        try
        {
            string code = GetText();

            // ① 实时建议（操作上下文感知）
            var suggestions = DevAssistant.GetSuggestions(code, _filePath, _lastOp);
            SuggestionList.ItemsSource = suggestions;
            SuggestionEmpty.Text = suggestions.Count == 0
                ? "当前没有需要处理的问题，继续保持！"
                : "";
            SuggestionEmpty.Visibility = suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ② 内容点评
            var review = DevAssistant.Review(code);
            ScoreText.Text = $"综合 {review.Score} 分";
            string scoreKey = review.Score >= 85 ? "GreenBrush" : review.Score >= 55 ? "YellowBrush" : "RedBrush";
            ScoreBadge.SetResourceReference(Border.BackgroundProperty, scoreKey);   // 跟随主题热切换
            ScoreText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");   // 字色随主题，固定黑白在部分主题下不可读
            ReviewSummary.Text = review.Summary;
            HighlightList.ItemsSource = review.Highlights;
            ImproveList.ItemsSource = review.Improvements;
        }
        catch (Exception ex)
        {
            SuggestionEmpty.Text = "助手分析暂时不可用：" + ex.Message;
            SuggestionEmpty.Visibility = Visibility.Visible;
        }
    }

    /// <summary>记录最近操作（驱动上下文建议）</summary>
    private void TrackOp(string op)
    {
        _lastOp = op;
        if (InsightToggle.IsChecked == true)
        {
            _reviewTimer.Stop();
            _reviewTimer.Start();   // 操作后立即给出衔接建议
        }
    }

    // ================= 片段 =================
    private void SnippetList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SnippetList.SelectedItem is not CodeSnippet s) return;
        string code = s.Code
            .Replace("{{package}}", AppContext.CurrentProject?.Package ?? "com.example.mymod")
            .Replace("{{modid}}", AppContext.CurrentProject?.ModId.ToLowerInvariant() ?? "mymod")
            .Replace("{{MODID_UPPER}}", (AppContext.CurrentProject?.ModId ?? "MYMOD").ToUpperInvariant())
            .Replace("{{MainClass}}", McProject.MakeClassName(AppContext.CurrentProject?.Name ?? "MyMod"));
        InsertAtCaret(code);
        CodeStatus.Text = $"已插入片段：{s.Name}";
        TrackOp("插入片段:" + s.Name);
    }

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        var sel = new TextRange(Editor.Selection.Start, Editor.Selection.End).Text;
        if (string.IsNullOrWhiteSpace(sel)) { MessageBox.Show("请先在编辑器中选中要保存的代码。"); return; }
        // 自动命名：依据代码内容（类名/方法名/关键调用）生成规范片段名
        var suggested = AutoNaming.ForSnippet(sel);
        var dlg = new InputDialog("片段名称（已按内容自动生成，可修改）", suggested) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        _userSnippets.Add(new CodeSnippet { Name = dlg.Value, Description = "用户自定义", Code = sel });
        try
        {
            SnippetLibrary.SaveUser(_userSnippets);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存片段失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        LoadSnippets();
        AppContext.SetStatus($"片段「{dlg.Value}」已保存。");
    }

    // ================= 格式化 =================
    private void Format_Click(object sender, RoutedEventArgs e)
    {
        string formatted = CodeAssist.FormatCode(GetText());
        SetText(formatted);
        CodeStatus.Text = "已格式化（统一 4 空格缩进）。";
        TrackOp("格式化");
    }

    // ================= 文件 =================
    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;
        SetStarterCode();
        CodeStatus.Text = "已新建。";
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardIfDirty()) return;
        var dlg = new OpenFileDialog { Filter = "源代码|*.java;*.json;*.glsl;*.frag;*.vert;*.txt|全部文件|*.*" };
        string? projectCodeDir = AppContext.CurrentProject is { } op ? AppContext.DirCode(op) : null;
        if (projectCodeDir != null && Directory.Exists(projectCodeDir))
            dlg.InitialDirectory = projectCodeDir;   // 默认定位到项目源码目录，省去逐层寻找
        if (dlg.ShowDialog() != true) return;
        try
        {
            SetText(File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _filePath = dlg.FileName;
        _dirty = false;
        UpdateFileLabel();
        CodeStatus.Text = "已打开：" + dlg.FileName;
        TrackOp("打开文件");
    }

    private void SaveFile_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null)
        {
            var dlg = new SaveFileDialog { Filter = "Java 源文件|*.java|JSON|*.json|GLSL|*.glsl|全部文件|*.*" };
            // 新文件默认定位到项目源码目录（按包路径逐级创建），而不是上次的任意位置
            if (AppContext.CurrentProject is { } p)
            {
                try
                {
                    string pkgDir = Path.Combine(new[] { AppContext.DirCode(p) }
                        .Concat(p.Package.Split('.')).ToArray());
                    Directory.CreateDirectory(pkgDir);
                    dlg.InitialDirectory = pkgDir;
                    dlg.FileName = McProject.MakeClassName(p.Name) + ".java";
                }
                catch { /* 目录创建失败时退回默认位置 */ }
            }
            if (dlg.ShowDialog() != true) return;
            _filePath = dlg.FileName;
        }
        try
        {
            File.WriteAllText(_filePath, GetText());
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败（文件可能被占用或路径不可写）：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _dirty = false;
        UpdateFileLabel();
        CodeStatus.Text = "已保存：" + _filePath;
        AppContext.SetStatus("代码已保存。");
        TrackOp("保存");
    }

    // ================= Git 集成 =================
    private async Task RunGitAsync(string args)
    {
        if (_gitBusy) { CodeStatus.Text = "Git 命令执行中，请稍候…"; return; }
        _gitBusy = true;
        CodeStatus.Text = $"git {args} 执行中…";
        var p = AppContext.CurrentProject;
        string dir = p?.ProjectDir ?? Directory.GetCurrentDirectory();
        try
        {
            // 后台执行：git 偶发挂起（网络/凭据弹窗）时不再冻结整个界面
            string output = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("无法启动 git");
                string outText = proc.StandardOutput.ReadToEnd();
                string errText = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(true); } catch { }
                    throw new TimeoutException("git 10 秒内无响应，已终止（请检查网络或凭据配置）");
                }
                return outText + errText;
            });
            CodeStatus.Text = string.IsNullOrWhiteSpace(output) ? $"git {args} 完成（无输出）。" : output.Trim();
            AppContext.SetStatus($"git {args}：见代码编辑器状态行。");
        }
        catch (Exception ex)
        {
            CodeStatus.Text = "Git 不可用：" + ex.Message + "（请安装 Git 或使用外部工具）";
        }
        finally
        {
            _gitBusy = false;
        }
    }

    private async void GitStatus_Click(object sender, RoutedEventArgs e) => await RunGitAsync("status --short");

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("提交说明（git commit）", "由工作台提交") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        string msg = dlg.Value.Replace("\"", "'");
        await RunGitAsync("add -A");
        await RunGitAsync($"commit -m \"{msg}\"");
    }
}
