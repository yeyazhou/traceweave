using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TraceWeave.Infrastructure.Windows;

public sealed record WindowContext(
    IntPtr Handle,
    uint ProcessId,
    string? ProcessName,
    string? Title);

public static class WindowContextReader
{
    public static WindowContext ReadForeground()
    {
        var handle = GetForegroundWindow();
        return Read(handle);
    }

    public static WindowContext Read(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return new WindowContext(IntPtr.Zero, 0, null, null);

        _ = GetWindowThreadProcessId(handle, out var processId);
        var titleBuilder = new StringBuilder(1024);
        _ = GetWindowText(handle, titleBuilder, titleBuilder.Capacity);

        string? processName = null;
        try
        {
            if (processId > 0)
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
        }
        catch (ArgumentException)
        {
            // The process may exit between the window event and this lookup.
        }
        catch (InvalidOperationException)
        {
            // The process may exit between the window event and this lookup.
        }

        return new WindowContext(handle, processId, processName, titleBuilder.ToString());
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
