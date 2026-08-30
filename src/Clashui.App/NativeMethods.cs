using System.Runtime.InteropServices;

namespace Clashui.App;

internal static partial class NativeMethods
{
    /// 允许目标进程把窗口提到前台。Windows 限制只有满足特定条件的进程才能设置前台窗口，
    /// 再启动的第二实例由前台启动（有该权限），退出前转授给第一实例。
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint dwProcessId);
}
