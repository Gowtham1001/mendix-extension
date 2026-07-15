using System.Runtime.InteropServices;

namespace MendixVibeCoder;

public static class ProjectSyncHelper
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    private const byte VK_F4 = 0x73;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static async Task TriggerSyncAsync(int delayMs = 800)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await Task.Delay(delayMs);
            return;
        }

        await Task.Delay(Math.Max(300, delayMs));

        try
        {
            var foregroundWindow = GetForegroundWindow();
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(foregroundWindow, sb, 256);
            var windowTitle = sb.ToString();

            if (windowTitle.Contains("Mendix") || windowTitle.Contains("Studio Pro"))
            {
                keybd_event(VK_F4, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F4, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
        catch
        {
            // silent fail on non-Windows or if window not found
        }
    }

    public static bool IsStudioProForeground()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            var foregroundWindow = GetForegroundWindow();
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(foregroundWindow, sb, 256);
            var windowTitle = sb.ToString();
            return windowTitle.Contains("Mendix") || windowTitle.Contains("Studio Pro");
        }
        catch
        {
            return false;
        }
    }
}
