using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

/// <summary>
/// Captures discrete mouse actions and keyboard shortcuts only.
/// Ordinary character key-down events are deliberately not recorded.
/// </summary>
public sealed class WindowsInputHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMiddleButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;

    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly HookProcedure _keyboardProcedure;
    private readonly HookProcedure _mouseProcedure;
    private readonly HashSet<int> _reportedKeys = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private NativePoint? _mouseDownPoint;
    private string? _mouseButton;
    private bool _mouseDragging;
    private bool _doubleClickPending;
    private bool _disposed;

    public WindowsInputHook(bool captureMouse, bool captureKeyboardShortcuts)
    {
        _keyboardProcedure = KeyboardHookCallback;
        _mouseProcedure = MouseHookCallback;

        var module = GetModuleHandle(null);
        if (captureKeyboardShortcuts)
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProcedure, module, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the low-level keyboard hook.");
            }
        }

        if (captureMouse)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProcedure, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error, "Unable to install the low-level mouse hook.");
            }
        }
    }

    public event Action<RawInputObservation>? Observed;

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            var key = (int)data.VirtualKey;

            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (_reportedKeys.Add(key))
                {
                    var shortcut = BuildShortcut(key);
                    if (shortcut is not null)
                    {
                        Observed?.Invoke(new RawInputObservation(
                            CaptureEventKind.KeyChord,
                            CaptureSource.KeyboardHook,
                            Shortcut: shortcut));
                    }
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _reportedKeys.Remove(key);
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MouseHookData>(lParam);
            var point = data.Point;

            switch (message)
            {
                case WmLeftButtonDown:
                    BeginMouseAction(point, "Left", false);
                    break;
                case WmLeftButtonDoubleClick:
                    BeginMouseAction(point, "Left", true);
                    break;
                case WmRightButtonDown:
                    BeginMouseAction(point, "Right", false);
                    break;
                case WmMiddleButtonDown:
                    BeginMouseAction(point, "Middle", false);
                    break;
                case WmMouseMove:
                    UpdateDragState(point);
                    break;
                case WmLeftButtonUp:
                case WmRightButtonUp:
                case WmMiddleButtonUp:
                    EndMouseAction(point);
                    break;
                case WmMouseWheel:
                    var delta = unchecked((short)(data.MouseData >> 16));
                    Observed?.Invoke(new RawInputObservation(
                        CaptureEventKind.MouseScroll,
                        CaptureSource.MouseHook,
                        X: point.X,
                        Y: point.Y,
                        Delta: delta));
                    break;
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void BeginMouseAction(NativePoint point, string button, bool isDoubleClick)
    {
        _mouseDownPoint = point;
        _mouseButton = button;
        _mouseDragging = false;
        _doubleClickPending = isDoubleClick;
    }

    private void UpdateDragState(NativePoint current)
    {
        if (_mouseDownPoint is not { } start || _mouseDragging) return;

        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if ((dx * dx) + (dy * dy) >= 64)
        {
            _mouseDragging = true;
        }
    }

    private void EndMouseAction(NativePoint end)
    {
        if (_mouseDownPoint is not { } start || _mouseButton is null) return;

        if (_mouseDragging)
        {
            Observed?.Invoke(new RawInputObservation(
                CaptureEventKind.MouseDrag,
                CaptureSource.MouseHook,
                Button: _mouseButton,
                X: end.X,
                Y: end.Y,
                StartX: start.X,
                StartY: start.Y));
        }
        else
        {
            Observed?.Invoke(new RawInputObservation(
                _doubleClickPending ? CaptureEventKind.MouseDoubleClick : CaptureEventKind.MouseClick,
                CaptureSource.MouseHook,
                Button: _mouseButton,
                X: end.X,
                Y: end.Y));
        }

        _mouseDownPoint = null;
        _mouseButton = null;
        _mouseDragging = false;
        _doubleClickPending = false;
    }

    private string? BuildShortcut(int virtualKey)
    {
        if (virtualKey is VkControl or VkShift or VkMenu or VkLWin or VkRWin)
        {
            return null;
        }

        var control = _reportedKeys.Contains(VkControl);
        var shift = _reportedKeys.Contains(VkShift);
        var alt = _reportedKeys.Contains(VkMenu);
        var windows = _reportedKeys.Contains(VkLWin) || _reportedKeys.Contains(VkRWin);

        var keyName = KeyInterop.KeyFromVirtualKey(virtualKey).ToString().ToUpperInvariant();
        var hasModifier = control || shift || alt || windows;

        if (!hasModifier && virtualKey is not (0x0D or 0x09 or 0x1B or 0x74 or 0x08 or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x2D))
        {
            return null;
        }

        var parts = new List<string>(5);
        if (control) parts.Add("CTRL");
        if (alt) parts.Add("ALT");
        if (shift) parts.Add("SHIFT");
        if (windows) parts.Add("WIN");
        parts.Add(keyName);
        return string.Join("+", parts);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _reportedKeys.Clear();
    }

    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

}
