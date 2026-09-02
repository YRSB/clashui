using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClashUI.Core;

/// 系统代理：写 HKCU Internet Settings 并广播刷新，浏览器立即生效，无需管理员权限。
public static partial class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    // wininet 导出的是带 A/W 后缀的名字；LibraryImport 不会自动探测后缀，必须显式指定 EntryPoint，
    // 否则运行时 EntryPointNotFoundException（曾导致点系统代理闪退）
    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static void Set(int mixedPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Internet Settings 注册表项");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{mixedPort}");
        key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>");
        NotifyShell();
    }

    /// 系统代理当前是否指向本应用的 127.0.0.1:<paramref name="mixedPort"/>（用于识别崩溃残留）。
    public static bool IsSetTo(int mixedPort)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key is null) return false;
        if (key.GetValue("ProxyEnable") is not 1) return false;
        return key.GetValue("ProxyServer") as string == $"127.0.0.1:{mixedPort}";
    }

    public static void Clear()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyShell();
    }

    private static void NotifyShell()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
