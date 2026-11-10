using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Win32;

namespace CWTrainer.Desktop;

public partial class MainWindow
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmSystemBackdropType = 38;
    private const int DwmBackdropNone = 1;
    private const int DwmBackdropAcrylic = 3;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentAcrylic = 4;
    private const uint AccentUseTint = 2;

    internal void RefreshWindowBackdrop(bool dark)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _allowClose)
            return;

        bool enabled = DwmIsCompositionEnabled(out bool compositionEnabled) >= 0
            && compositionEnabled
            && !SystemParameters.HighContrast
            && TransparencyEffectsEnabled();
        bool acrylicApplied = TryApplyAcrylic(handle, dark, enabled);

        // WindowChrome 延展整个客户区，并让 WPF 的 HWND 底层露出系统材质。
        WindowChrome.GetWindowChrome(this).GlassFrameThickness = acrylicApplied
            ? WindowChrome.GlassFrameCompleteThickness
            : new Thickness(0);
        SetResourceReference(BackgroundProperty, acrylicApplied ? "WindowAcrylicTint" : "Surface");
    }

    private static bool TryApplyAcrylic(IntPtr handle, bool dark, bool enabled)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));

            int backdrop = enabled ? DwmBackdropAcrylic : DwmBackdropNone;
            return DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int)) >= 0
                && enabled;
        }

        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            && TryApplyLegacyAccent(handle, dark, enabled)
            && enabled;
    }

    private static bool TryApplyLegacyAccent(IntPtr handle, bool dark, bool enabled)
    {
        // Win10 和 Win11 21H2 使用旧版 DWM 合成策略。TintColor 的字节顺序为 AABBGGRR。
        var accent = new AccentPolicy
        {
            State = enabled ? AccentAcrylic : AccentDisabled,
            Flags = AccentUseTint,
            TintColor = dark ? 0x88181818u : 0x77F4F4F4u
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = data,
                Size = (uint)size
            };
            return SetWindowCompositionAttribute(handle, ref attribute);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static bool TransparencyEffectsEnabled()
    {
        const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        try
        {
            return Registry.GetValue(key, "EnableTransparency", 1) is not 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public uint Flags;
        public uint TintColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public uint Size;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
