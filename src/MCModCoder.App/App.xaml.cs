using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MCModCoder;

public partial class App : Application
{
    /// <summary>崩溃日志文件路径（%APPDATA%\MCModCoder\logs）</summary>
    public static string ErrorLogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCModCoder", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        // UI 冒烟模式：MCModCoder.exe --smoke-ui <结果文件路径>
        // 逐个实例化全部页面，把 BAML 加载结果（含完整异常链）写入结果文件后退出。
        // 用于在无法交互操作 GUI 的环境里自动定位"页面崩溃"。
        if (e.Args.Length > 0 && e.Args[0] == "--smoke-ui")
        {
            RunUiSmoke(e.Args.Length > 1 ? e.Args[1] : "ui-smoke-result.txt");
            Shutdown(0);
            return;
        }

        // 全局异常兜底：未处理异常记录完整异常链并弹出根因提示，恢复运行而不是崩溃退出
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                args.Handled = true;
                var detail = Describe(args.Exception);
                WriteErrorLog(detail);
                var root = args.Exception.GetBaseException();
                MessageBox.Show(
                    "发生未预期的错误，程序已恢复运行。若反复出现，请重启应用或联系开发者。\n\n" +
                    "错误信息：" + (root.Message.Length > 0 ? root.Message : args.Exception.Message) + "\n\n" +
                    "详细日志：" + Path.Combine(ErrorLogDir,
                        "error-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 兜底处理本身不能再抛异常 */ }
        };

        // 后台任务未观察异常：记录并吞掉，避免进程终止
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { WriteErrorLog("未观察的后台任务异常：\n" + Describe(args.Exception)); }
            catch { }
            args.SetObserved();
        };

        // 非 UI 线程兜底：线程池/手动线程的未处理异常会直接终止进程（不走 Dispatcher 事件），
        // 这里尽力留一份完整日志，便于事后定位（进程通常已无法挽救）
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    WriteErrorLog("致命异常（非 UI 线程，进程即将退出，IsTerminating=" + args.IsTerminating + "）：\n" + Describe(ex));
                else
                    WriteErrorLog("致命异常（非 Exception 对象）：" + args.ExceptionObject);
            }
            catch { /* 兜底处理本身不能再抛异常 */ }
        };
    }

    /// <summary>展开异常链（含内部异常与关键堆栈）为可读文本</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var cur = (Exception?)ex;
        int level = 0;
        while (cur != null)
        {
            sb.AppendLine($"[{level}] {cur.GetType().FullName}: {cur.Message}");
            if (cur.StackTrace is { } st)
            {
                var lines = st.Split('\n').Take(10);
                foreach (var line in lines) sb.AppendLine("    " + line.Trim());
            }
            cur = cur.InnerException;
            level++;
        }
        return sb.ToString();
    }

    /// <summary>追加写入错误日志（按天分文件），失败时静默</summary>
    private static void WriteErrorLog(string content)
    {
        try
        {
            Directory.CreateDirectory(ErrorLogDir);
            File.AppendAllText(Path.Combine(ErrorLogDir, "error-" + DateTime.Now.ToString("yyyyMMdd") + ".log"),
                "========\n" + content + "\n", Encoding.UTF8);
        }
        catch { }
    }

    // ================= UI 冒烟测试（--smoke-ui） =================

    /// <summary>在真实主题资源下逐个实例化全部页面，输出每页加载结果与完整异常链</summary>
    private static void RunUiSmoke(string resultPath)
    {
        var sb = new StringBuilder();
        try
        {
            int failed = 0;
            var asm = typeof(App).Assembly;
            var viewTypes = asm.GetTypes()
                .Where(t => typeof(System.Windows.Controls.UserControl).IsAssignableFrom(t) && !t.IsAbstract)
                .OrderBy(t => t.Name);

            foreach (var t in viewTypes)
            {
                try
                {
                    Activator.CreateInstance(t);
                    sb.AppendLine($"OK    {t.Name}");
                }
                catch (Exception ex)
                {
                    failed++;
                    sb.AppendLine($"FAIL  {t.Name}");
                    AppendException(sb, ex);
                }
            }

            try
            {
                Activator.CreateInstance(asm.GetType("MCModCoder.MainWindow")!);
                sb.AppendLine("OK    MainWindow（构造）");
            }
            catch (Exception ex)
            {
                failed++;
                sb.AppendLine("FAIL  MainWindow（构造）");
                AppendException(sb, ex);
            }

            sb.AppendLine(failed == 0 ? "== 全部通过 ==" : $"== {failed} 个失败 ==");
        }
        catch (Exception ex)
        {
            sb.AppendLine("冒烟流程自身异常：");
            AppendException(sb, ex);
        }

        try
        {
            File.WriteAllText(resultPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>展开异常链（含内部异常与关键堆栈）</summary>
    private static void AppendException(StringBuilder sb, Exception ex)
    {
        var cur = (Exception?)ex;
        int level = 0;
        while (cur != null)
        {
            sb.AppendLine($"  [{level}] {cur.GetType().FullName}: {cur.Message}");
            if (cur.StackTrace is { } st)
                foreach (var line in st.Split('\n').Take(8))
                    sb.AppendLine("        " + line.Trim());
            cur = cur.InnerException;
            level++;
        }
    }
}
