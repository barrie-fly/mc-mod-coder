using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MCModCoder;

/// <summary>
/// 窗口图标安全加载器。
///
/// 背景（2026-10-01 启动崩溃修复）：app.ico 的内嵌 PNG 帧曾为 RGB（色彩类型 2）格式，
/// WPF/WIC 的 ICO 解码器无法解码 ICO 内嵌的 RGB 格式 PNG 帧，抛出
/// "图像无法识别" (0x88982F60)。异常发生在 XAML 静态 Icon 属性上时，会在应用启动阶段
/// 包装为 XamlParseException，直接阻断 MainWindow 加载（全局兜底弹窗"程序已恢复运行"）。
///
/// 因此图标统一改为代码后台 try/catch 加载：失败仅降级为系统默认图标并记录诊断日志，
/// 不影响启动。
///
/// 资产约定：Assets/app.ico 内嵌 PNG 帧必须为 RGBA（色彩类型 6，带 Alpha 通道），
/// 建议 16/24/32/48/64/128/256 全尺寸。RGB / 索引色帧 WPF 解码必失败。
/// </summary>
public static class WindowIcons
{
    /// <summary>为窗口安全加载应用图标，失败时降级为默认图标并记录诊断日志</summary>
    public static void Apply(Window window)
    {
        try
        {
            window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch (Exception ex)
        {
            try
            {
                var log = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MCModCoder", "logs", "icon-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log,
                    $"[{DateTime.Now:HH:mm:ss}] 窗口图标加载失败（已降级为默认图标）[{window.GetType().Name}]: {ex.Message}\n",
                    Encoding.UTF8);
            }
            catch { /* 诊断日志失败不影响运行 */ }
        }
    }
}
