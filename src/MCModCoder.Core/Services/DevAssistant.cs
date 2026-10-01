using System.IO;
using System.Text.RegularExpressions;

namespace MCModCoder.Core.Services;

/// <summary>实时建议条目。Severity：warn（建议修复）/ tip（优化提示）/ info（上下文提醒）</summary>
public sealed record DevSuggestion(string Severity, string Title, string Detail);

/// <summary>内容点评结果：评分 + 一句话总结 + 亮点 + 可改进项</summary>
public sealed record CodeReview(
    int Score, string Summary,
    IReadOnlyList<string> Highlights, IReadOnlyList<string> Improvements);

/// <summary>
/// AI 开发助手（本地启发式引擎，不联网）：
/// ① 根据代码内容与最近操作上下文实时给出优化建议；
/// ② 对开发内容做出简要评价（亮点与可改进之处，附评分）；
/// ③ 预测用户接下来可能编写的语句，给出补全建议。
/// </summary>
public static class DevAssistant
{
    private const int MaxAnalyzeLength = 200_000;   // 超长文件跳过分析，保障响应及时

    // ================= ① 实时智能建议 =================

    /// <param name="code">当前编辑器全文</param>
    /// <param name="filePath">当前文件路径（可空）</param>
    /// <param name="lastOperation">最近的开发操作描述（如"插入片段:xxx"、"格式化"、"保存"），用于上下文感知</param>
    public static List<DevSuggestion> GetSuggestions(string code, string? filePath, string? lastOperation)
    {
        var list = new List<DevSuggestion>();
        if (string.IsNullOrEmpty(code) || code.Length > MaxAnalyzeLength) return list;

        bool isJava = (filePath?.EndsWith(".java", StringComparison.OrdinalIgnoreCase) ?? false) || code.Contains("class ");
        bool isJson = (filePath?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ?? false) || code.TrimStart().StartsWith("{");

        // --- 上下文感知：与操作衔接 ---
        if (lastOperation != null && lastOperation.StartsWith("插入片段", StringComparison.Ordinal) && code.Contains("{{"))
            list.Add(new DevSuggestion("warn", "片段占位符未替换",
                "刚插入的片段中仍有 {{...}} 占位符（会导致编译失败），请替换为项目实际的 modid / 类名。"));

        if (lastOperation != null && lastOperation.StartsWith("格式化", StringComparison.Ordinal) && ContainsUnbalancedBraces(code))
            list.Add(new DevSuggestion("warn", "括号不匹配",
                "格式化后检测到括号数量不匹配，建议先修复配对问题再继续编码。"));

        // --- Java / 模组特有启发式 ---
        if (isJava)
        {
            // 服务端逻辑保护：改世界却没区分客户端/服务端
            bool worldWrite = Regex.IsMatch(code, @"\.(setBlock|addFreshEntity|destroyBlock|setBlockAndUpdate)\s*\(");
            if (worldWrite && !code.Contains("isClientSide"))
                list.Add(new DevSuggestion("warn", "建议区分客户端/服务端逻辑",
                    "检测到世界写入调用（setBlock 等），请用 if (!level.isClientSide()) 包裹，否则客户端执行会导致不同步或崩溃。"));

            // 空 catch
            if (Regex.IsMatch(code, @"catch\s*(\([^)]*\))?\s*\{\s*\}"))
                list.Add(new DevSuggestion("warn", "存在空 catch 块",
                    "异常被静默吞掉会掩盖真实问题，建议至少记录日志或添加注释说明忽略原因。"));

            // @SubscribeEvent 方法可见性
            if (Regex.IsMatch(code, @"@SubscribeEvent\s+(?:protected|private)"))
                list.Add(new DevSuggestion("warn", "事件方法应使用 public",
                    "Forge 事件订阅方法必须为 public，否则事件不会触发。"));

            // System.out 调试输出
            if (code.Contains("System.out.println"))
                list.Add(new DevSuggestion("tip", "用日志替代 System.out",
                    "模组环境建议使用 LOGGER.info(...)（LogUtils.getLogger()），便于玩家在日志中定位问题。"));

            // TODO 未完成项
            int todos = Regex.Matches(code, @"\b(TODO|FIXME|HACK)\b").Count;
            if (todos > 0)
                list.Add(new DevSuggestion("info", $"有 {todos} 处 TODO/FIXME 待处理",
                    "发布前记得清理由程碑标记，可在「测试工具」构建前检查。"));

            // 魔法 modid 重复硬编码
            var modMatch = Regex.Match(code, "@Mod\\s*\\(\\s*\"([^\"]+)\"");
            if (modMatch.Success)
            {
                string modid = modMatch.Groups[1].Value;
                if (modid.Length > 0 && Regex.Matches(code, Regex.Escape("\"" + modid + "\"")).Count > 2)
                    list.Add(new DevSuggestion("tip", "modid 建议提取为常量",
                        $"\"{modid}\" 出现多次，建议定义 public static final String MODID 后统一引用，避免拼写不一致。"));
            }

            // 文件名与公共类名不一致
            if (filePath != null && filePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            {
                string fileClass = Path.GetFileNameWithoutExtension(filePath);
                var m = Regex.Match(code, @"\bpublic\s+(?:final\s+|abstract\s+)?class\s+(\w+)");
                if (m.Success && !string.Equals(m.Groups[1].Value, fileClass, StringComparison.Ordinal))
                    list.Add(new DevSuggestion("warn", "公共类名与文件名不一致",
                        $"文件 {Path.GetFileName(filePath)} 中的公共类是 {m.Groups[1].Value}，Java 要求两者一致。"));
            }
            else if (filePath == null && !Regex.IsMatch(code, @"^\s*package\b", RegexOptions.Multiline))
            {
                list.Add(new DevSuggestion("tip", "补充 package 声明",
                    "新文件建议先写 package 语句，与项目结构保持一致，便于后续构建。"));
            }
        }

        if (isJson)
        {
            // JSON 注释/尾逗号（数据包 JSON 不允许）
            if (Regex.IsMatch(code, @",\s*[}\]]"))
                list.Add(new DevSuggestion("warn", "JSON 存在尾随逗号",
                    "数据包 JSON 不允许尾随逗号，游戏加载时会解析失败。"));
            if (code.Contains("//"))
                list.Add(new DevSuggestion("warn", "JSON 不支持注释",
                    "数据包/资源包 JSON 中不能使用 // 注释，请移除。"));
        }

        return list;
    }

    private static bool ContainsUnbalancedBraces(string code)
    {
        int braces = 0;
        foreach (var c in code)
        {
            if (c == '{') braces++;
            else if (c == '}') braces--;
        }
        return braces != 0;
    }

    // ================= ② 内容点评 =================

    /// <summary>对当前开发内容给出简要评价：评分（0-100）、一句话总结、亮点与可改进之处。</summary>
    public static CodeReview Review(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new CodeReview(0, "暂无内容可评价。",
                Array.Empty<string>(), Array.Empty<string>());
        if (code.Length > MaxAnalyzeLength)
            return new CodeReview(0, "文件过大，已跳过点评分析。",
                Array.Empty<string>(), Array.Empty<string>());

        int score = 40;
        var highlights = new List<string>();
        var improvements = new List<string>();

        bool isJava = code.Contains("class ") || Regex.IsMatch(code, @"^\s*package\b", RegexOptions.Multiline);

        // --- 基础结构 ---
        bool balanced = !ContainsUnbalancedBraces(code);
        if (balanced) { score += 12; highlights.Add("括号配对完整，结构闭合良好"); }
        else { score -= 8; improvements.Add("存在未配对的大括号，先修复结构闭合问题"); }

        if (isJava)
        {
            if (Regex.IsMatch(code, @"^\s*package\s+[\w.]+\s*;", RegexOptions.Multiline)) score += 8;
            else improvements.Add("缺少 package 声明，建议与项目包结构保持一致");

            if (Regex.IsMatch(code, @"^\s*import\s+", RegexOptions.Multiline)) score += 4;

            if (Regex.IsMatch(code, @"@Mod\s*\(|@SubscribeEvent|DeferredRegister|ModInitializer"))
            {
                score += 6;
                highlights.Add("使用了 Forge/Fabric 标准扩展点（注册/事件订阅）");
            }

            bool worldWrite = Regex.IsMatch(code, @"\.(setBlock|addFreshEntity|destroyBlock)\s*\(");
            if (worldWrite && code.Contains("isClientSide"))
            {
                score += 6;
                highlights.Add("正确区分了客户端/服务端逻辑（isClientSide 保护）");
            }

            if (Regex.IsMatch(code, @"\bclass\s+\w+")) score += 6;
        }

        // --- 代码卫生 ---
        if (Regex.IsMatch(code, @"(//|/\*)")) { score += 6; highlights.Add("包含注释，可读性较好"); }
        else if (code.Length > 400) improvements.Add("关键逻辑缺少注释，建议为公开方法与复杂分支补充说明");

        if (Regex.IsMatch(code, @"catch\s*(\([^)]*\))?\s*\{\s*\}"))
        { score -= 6; improvements.Add("存在空 catch 块——至少记录日志或说明忽略原因"); }

        if (Regex.IsMatch(code, @"System\.out\.print"))
        { score -= 4; improvements.Add("用 LOGGER 替代 System.out 输出"); }

        int todos = Regex.Matches(code, @"\b(TODO|FIXME|HACK)\b").Count;
        if (todos > 0) { score -= 2; improvements.Add($"还有 {todos} 处 TODO/FIXME 待完成"); }
        else score += 4;

        // --- 行长与缩进 ---
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        double avgLen = nonEmpty.Count > 0 ? nonEmpty.Average(l => l.Length) : 0;
        if (avgLen > 0 && avgLen <= 100) { score += 6; highlights.Add("平均行长适中，阅读舒适"); }
        else if (avgLen > 120) improvements.Add("平均行长偏长，考虑拆分长表达式或提取方法");

        bool mixedIndent = nonEmpty.Any(l => l.StartsWith('\t')) && nonEmpty.Any(l => l.StartsWith("    "));
        if (mixedIndent) { score -= 4; improvements.Add("缩进混用了 Tab 与空格，建议统一（可用「格式化」一键修正）"); }
        else if (nonEmpty.Count > 0) score += 4;

        if (Regex.IsMatch(code, @"\bfinal\s+\w+\s+\w+\s*=") || Regex.IsMatch(code, @"\bstatic\s+final\b"))
            highlights.Add("使用了常量/final 修饰，语义清晰");

        int n = Math.Clamp(score, 0, 100);
        string grade = n >= 85 ? "优秀" : n >= 70 ? "良好" : n >= 55 ? "合格" : "待改进";
        string summary = $"综合 {n} 分（{grade}）——" + (n >= 85
            ? "代码质量很高，可直接进入构建验证。"
            : n >= 70 ? "整体不错，处理改进项后即可构建。"
            : "建议先解决列出的改进项，再进行构建与测试。");

        return new CodeReview(n, summary, highlights.Take(4).ToList(), improvements.Take(4).ToList());
    }

    // ================= ③ 下一句预测 =================

    /// <summary>
    /// 预测用户接下来可能编写的完整语句（行首/新行场景）。
    /// 与 <see cref="CodeAssist.GetCompletions"/> 的"成员/类型补全"互补：这里给出"下一句"级别建议。
    /// </summary>
    public static List<CompletionItem> PredictNext(string codeBeforeCursor)
    {
        var result = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(codeBeforeCursor) || codeBeforeCursor.Length > MaxAnalyzeLength) return result;

        var tail = codeBeforeCursor[^500..].Replace("\r\n", "\n");
        // 光标所在行已输入的部分（新行场景应为空或仅缩进）
        int lastLine = tail.LastIndexOf('\n');
        string curLine = lastLine >= 0 ? tail[(lastLine + 1)..] : tail;
        string tailBefore = lastLine >= 0 ? tail[..lastLine] : "";
        if (curLine.Trim().Length > 0) return result;   // 非新行场景交给词级补全

        void Add(string label, string insert, string detail)
        {
            if (seen.Add(label))
                result.Add(new CompletionItem { Label = label, Insert = insert, Detail = "预测下一句 · " + detail });
        }

        // 事件方法体：取实体/世界引用
        if (Regex.IsMatch(tailBefore, @"@SubscribeEvent[\s\S]{0,200}(?:\{)\s*$") ||
            Regex.IsMatch(tailBefore, @"(?:RightClickBlock|LeftClickBlock|LivingDeathEvent|BreakEvent|PlayerTickEvent)[\s\S]{0,80}$"))
        {
            Add("Player player = event.getEntity();", "Player player = event.getEntity();", "事件处理器标准起手：获取玩家实体");
            Add("Level level = event.getLevel();", "Level level = event.getLevel();", "获取世界实例");
            Add("if (!level.isClientSide()) {", "if (!level.isClientSide()) {\n    \n}", "服务端逻辑保护（重要）");
            Add("event.setCanceled(true);", "event.setCanceled(true);", "取消该事件");
        }
        // 取到 level 之后 → 建议 isClientSide 保护
        if (Regex.IsMatch(tailBefore, @"\bLevel\s+\w+\s*=\s*event\.getLevel\(\)\s*;\s*$"))
            Add("if (!level.isClientSide()) {", "if (!level.isClientSide()) {\n    \n}", "取到 Level 后紧接服务端保护");
        // 注册上下文：DeferredRegister 字段后 → 建议注册条目
        var dr = Regex.Match(tailBefore, @"DeferredRegister<(\w+)>\s+(\w+)\s*=\s*[^;]+;\s*$");
        if (dr.Success)
            Add($"public static final RegistryObject<{dr.Groups[1].Value}> NAME = {dr.Groups[2].Value}.register(\"name\", () -> new {dr.Groups[1].Value}(new Item.Properties()));",
                $"public static final RegistryObject<{dr.Groups[1].Value}> NAME = {dr.Groups[2].Value}.register(\"name\", () -> new {dr.Groups[1].Value}(new Item.Properties()));",
                "紧接注册一个条目（替换 NAME 与 name）");
        // 类体开头（@Mod 类）→ 建议注册器
        if (Regex.IsMatch(tailBefore, @"@Mod\s*\([^)]*\)\s*[\s\S]{0,120}class\s+\w+\s*\{[\s\S]{0,80}$"))
        {
            Add("public static final String MODID = \"mymod\";", "public static final String MODID = \"mymod\";", "先定义 modid 常量");
            Add("private static final Logger LOGGER = LogUtils.getLogger();", "private static final Logger LOGGER = LogUtils.getLogger();", "标准日志器（Forge 提供 LogUtils）");
        }
        // setBlock 调用习惯
        if (Regex.IsMatch(tailBefore, @"if\s*\(\s*!\s*\w+\.isClientSide\(\)\s*\)\s*\{\s*$"))
        {
            Add("level.setBlock(pos, state, 3);", "level.setBlock(pos, state, 3);", "服务端内放置方块（3=同步客户端）");
            Add("player.sendSystemMessage(Component.literal(\"...\"));", "player.sendSystemMessage(Component.literal(\"\"));", "给玩家反馈消息");
        }
        // JSON 配方上下文
        if (Regex.IsMatch(tailBefore, @"""type""\s*:\s*""minecraft:crafting_shaped""\s*,?\s*$"))
        {
            Add("\"pattern\": [", "\"pattern\": [\n    \"###\",\n    \"# #\"\n  ],", "有序合成的图案行");
            Add("\"key\": {", "\"key\": {\n    \"#\": { \"item\": \"minecraft:iron_ingot\" }\n  },", "图案字母映射");
            Add("\"result\": {", "\"result\": { \"item\": \"mymod:item\", \"count\": 1 },", "合成产物");
        }
        if (Regex.IsMatch(tailBefore, @"""pattern""\s*:\s*\[[^\]]*$"))
            Add("\"key\": {", "\"key\": {\n    \"#\": { \"item\": \"minecraft:iron_ingot\" }\n  },", "图案写完接 key 映射");

        return result.Take(5).ToList();
    }
}
