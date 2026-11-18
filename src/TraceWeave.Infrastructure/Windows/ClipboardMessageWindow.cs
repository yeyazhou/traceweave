using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TraceWeave.Infrastructure.Windows;

/// <summary>
/// Receives WM_CLIPBOARDUPDATE on the owning WPF dispatcher thread.
/// The caller decides whether clipboard text content may be read.
/// </summary>
public sealed class ClipboardMessageWindow : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private bool _disposed;

    public ClipboardMessageWindow()
    {
        var parameters = new HwndSourceParameters("TraceWeave.ClipboardListener")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowProcedure);

        if (!AddClipboardFormatListener(_source.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            _source.RemoveHook(WindowProcedure);
            _source.Dispose();
            throw new Win32Exception(error, "Unable to register the Windows clipboard listener.");
        }
    }

    public event EventHandler? ClipboardUpdated;

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmClipboardUpdate)
        {
            ClipboardUpdated?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ = RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr windowHandle);
}
