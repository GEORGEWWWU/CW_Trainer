using System.Windows;
using System.Windows.Threading;

namespace CWTrainer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 进程退出（包括被任务管理器强制结束）前的最后兜底：
        // 尽量把 BYE 发出去，让硬件交还实体蜂鸣器。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeHandoff();
    }

    /// <summary>
    /// Windows 关机 / 注销。系统只留很短的退出时间，这里同步发送 BYE。
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        SafeHandoff();
        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        // 异常退出前也把蜂鸣器交还回去，避免硬件停在静音状态。
        SafeHandoff();

        MessageBox.Show(
            $"程序遇到未处理的异常：\n\n{e.Exception.Message}",
            "CW Trainer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // 不吞掉异常：状态已经不可信，让进程退出；
        // 即使这里失败，固件的心跳超时也会兜底恢复实体蜂鸣器。
        e.Handled = false;
    }

    private void SafeHandoff()
    {
        try
        {
            (MainWindow as CWTrainer.Desktop.MainWindow)?.EmergencyHandoff();
        }
        catch
        {
        }
    }
}
