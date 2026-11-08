using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CWTrainer.Desktop;

public partial class App : Application
{
    private static bool _isDark;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _isDark = IsSystemDarkTheme();
        ApplyTheme(_isDark);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 进程退出（包括被任务管理器强制结束）前的最后兜底：
        // 尽量把 BYE 发出去，让硬件交还实体蜂鸣器。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeHandoff();

        // 系统里切换「应用模式」时会触发这个事件
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
                RefreshTheme();
        };
    }

    /// <summary>重新读一次系统主题，变了就换色板。窗口收到 WM_SETTINGCHANGE 时也会调这里。</summary>
    public static void RefreshTheme()
    {
        bool dark = IsSystemDarkTheme();

        if (dark == _isDark)
            return;

        _isDark = dark;

        if (Current is null)
            return;

        Current.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(dark)));
    }

    /// <summary>
    /// 整页的颜色都通过 DynamicResource 引用主题字典里的键，
    /// 所以只要替换 MergedDictionaries[0]，界面会自动刷新，不需要重建控件。
    /// </summary>
    private static void ApplyTheme(bool dark)
    {
        var theme = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        Collection<ResourceDictionary> merged = Current.Resources.MergedDictionaries;

        if (merged.Count > 0)
            merged[0] = theme;
        else
            merged.Add(theme);
    }

    /// <summary>
    /// 读注册表判断系统当前是深色还是浅色。
    /// AppsUseLightTheme = 0 表示深色，Win10 1809 以后与 Win11 都是这个位置。
    /// </summary>
    private static bool IsSystemDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
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
