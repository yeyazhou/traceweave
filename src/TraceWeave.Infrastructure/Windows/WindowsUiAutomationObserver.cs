using System.Threading;
using System.Windows;
using System.Windows.Automation;
using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

/// <summary>
/// Observes desktop UI Automation from a dedicated MTA thread.
///
/// The observer is intentionally polling-based at a low rate: UIA providers from
/// browsers, IDEs and remote desktop clients can be expensive or unstable. The
/// WPF UI thread is never used for UIA queries.
/// </summary>
public sealed class WindowsUiAutomationObserver : IDisposable
{
    private const int PollIntervalMilliseconds = 220;
    private const int StartupTimeoutMilliseconds = 3000;

    private readonly IWindowExclusionPolicy _exclusionPolicy;
    private readonly ITextRedactor _redactor;
    private readonly bool _captureCommittedText;
    private readonly AutomationFocusChangedEventHandler _focusChangedHandler;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _workerThread;

    private volatile bool _disposed;
    private Exception? _startupException;
    private UiAutomationTargetSnapshot? _currentTarget;
    private bool _captureExcluded;

    private AutomationElement? _focusedElement;
    private ValuePattern? _valuePattern;
    private TextPattern? _textPattern;
    private string _lastObservedValue = string.Empty;
    private string? _elementIdentity;
    private DateTime _nextPatternRetryUtc = DateTime.MinValue;

    public WindowsUiAutomationObserver(
        IWindowExclusionPolicy exclusionPolicy,
        ITextRedactor redactor,
        bool captureCommittedText = true)
    {
        _exclusionPolicy = exclusionPolicy;
        _redactor = redactor;
        _captureCommittedText = captureCommittedText;
        _focusChangedHandler = (_, _) => _wakeEvent.Set();

        _workerThread = new Thread(UiAutomationWorker)
        {
            IsBackground = true,
            Name = "TraceWeave.UIAutomation.MTA"
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();

        if (!_ready.Wait(StartupTimeoutMilliseconds))
        {
            _disposed = true;
            _wakeEvent.Set();
            throw new TimeoutException(
                "TraceWeave UI Automation worker did not start within 3 seconds.");
        }

        if (_startupException is not null)
        {
            _disposed = true;
            _wakeEvent.Set();
            throw new InvalidOperationException(
                "TraceWeave could not initialize the UI Automation worker.",
                _startupException);
        }
    }

    public event Action<UiAutomationObservation>? Observed;

    public UiAutomationTargetSnapshot? CurrentTarget =>
        Volatile.Read(ref _currentTarget);

    public bool IsCaptureExcluded =>
        Volatile.Read(ref _captureExcluded);

    public UiAutomationTargetSnapshot? ResolveAtPoint(int x, int y)
    {
        if (_disposed) return null;

        try
        {
            var element = AutomationElement.FromPoint(new Point(x, y));
            return ReadTarget(element);
        }
        catch
        {
            return null;
        }
    }

    private void UiAutomationWorker()
    {
        try
        {
            Automation.AddAutomationFocusChangedEventHandler(_focusChangedHandler);
            _ready.Set();

            ProcessFocusedElement();

            while (!_disposed)
            {
                _wakeEvent.WaitOne(PollIntervalMilliseconds);
                if (_disposed) break;

                try
                {
                    ProcessFocusedElement();
                }
                catch
                {
                    // UIA providers can disappear or become temporarily unavailable.
                }
            }

            Automation.RemoveAutomationFocusChangedEventHandler(
                _focusChangedHandler);
        }
        catch (Exception exception)
        {
            _startupException = exception;
            _ready.Set();
        }
        finally
        {
            ClearWorkerState();
        }
    }

    private void ProcessFocusedElement()
    {
        if (_disposed) return;

        AutomationElement? element;
        try
        {
            element = AutomationElement.FocusedElement;
        }
        catch
        {
            return;
        }

        if (element is null)
        {
            ClearFocus();
            return;
        }

        var identity = TryGetRuntimeIdentity(element);
        if (identity is null)
        {
            ClearFocus();
            return;
        }

        // The focused element has not changed. Do not repeatedly walk its UIA
        // properties. Only sample text when this element actually exposes a
        // text/value pattern.
        if (string.Equals(identity, _elementIdentity, StringComparison.Ordinal))
        {
            if (_captureExcluded || !_captureCommittedText)
            {
                return;
            }

            CaptureTextDelta();
            return;
        }

        var target = ReadTarget(element);
        if (target is null)
        {
            ClearFocus();
            return;
        }

        var excluded = target.IsPassword ||
                       _exclusionPolicy.IsExcluded(
                           target.Window.ProcessName,
                           target.Window.Title);

        _elementIdentity = identity;
        _focusedElement = element;
        _currentTarget = target;
        _captureExcluded = excluded;
        _lastObservedValue = string.Empty;
        _valuePattern = null;
        _textPattern = null;
        _nextPatternRetryUtc = DateTime.UtcNow;

        if (!excluded && _captureCommittedText)
        {
            InitializeTextPatterns();
        }

        if (!excluded)
        {
            Observed?.Invoke(
                new UiAutomationObservation(
                    CaptureEventKind.UiElementFocused,
                    target));
        }
    }

    private void InitializeTextPatterns()
    {
        if (_focusedElement is null) return;

        try
        {
            _valuePattern = TryGetValuePattern(_focusedElement);
            _textPattern = TryGetTextPattern(_focusedElement);

            if (_valuePattern is not null || _textPattern is not null)
            {
                _lastObservedValue = ReadCurrentText();
            }
            else
            {
                // Do not hammer providers that do not expose text patterns.
                _nextPatternRetryUtc = DateTime.UtcNow.AddSeconds(2);
            }
        }
        catch
        {
            _valuePattern = null;
            _textPattern = null;
            _nextPatternRetryUtc = DateTime.UtcNow.AddSeconds(2);
        }
    }

    private void CaptureTextDelta()
    {
        if (_focusedElement is null ||
            (_valuePattern is null && _textPattern is null))
        {
            if (_focusedElement is not null &&
                DateTime.UtcNow >= _nextPatternRetryUtc)
            {
                InitializeTextPatterns();
            }

            return;
        }

        try
        {
            var target = _currentTarget;
            if (target is null || target.IsPassword) return;

            var currentValue = ReadCurrentText();
            var insertedText = TextDiff.ExtractInsertedText(
                _lastObservedValue,
                currentValue);
            _lastObservedValue = currentValue;

            if (string.IsNullOrEmpty(insertedText)) return;

            var redaction = _redactor.Inspect(
                insertedText,
                target.Window.ProcessName,
                target.Window.Title);

            Observed?.Invoke(
                new UiAutomationObservation(
                    CaptureEventKind.TextCommitted,
                    target,
                    redaction.Value,
                    redaction.Disposition,
                    redaction.Sensitivity,
                    redaction.Reason));
        }
        catch
        {
            // A control can disappear or invalidate its provider.
            _valuePattern = null;
            _textPattern = null;
            _nextPatternRetryUtc = DateTime.UtcNow.AddSeconds(2);
        }
    }

    private string ReadCurrentText()
    {
        try
        {
            if (_valuePattern is not null)
            {
                return _valuePattern.Current.Value ?? string.Empty;
            }

            if (_textPattern is not null)
            {
                return _textPattern.DocumentRange.GetText(4096) ??
                       string.Empty;
            }
        }
        catch
        {
            // Provider became unavailable.
        }

        return string.Empty;
    }

    private static string? TryGetRuntimeIdentity(AutomationElement element)
    {
        try
        {
            return string.Join(".", element.GetRuntimeId());
        }
        catch
        {
            return null;
        }
    }

    private static ValuePattern? TryGetValuePattern(
        AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(
                ValuePattern.Pattern,
                out var pattern)
                ? (ValuePattern)pattern
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static TextPattern? TryGetTextPattern(
        AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(
                TextPattern.Pattern,
                out var pattern)
                ? (TextPattern)pattern
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static UiAutomationTargetSnapshot? ReadTarget(
        AutomationElement element)
    {
        try
        {
            var isPassword = ReadBool(
                element,
                AutomationElement.IsPasswordProperty);

            var nativeHandle = ReadInt(
                element,
                AutomationElement.NativeWindowHandleProperty);

            var window = nativeHandle != 0
                ? WindowContextReader.Read(new IntPtr(nativeHandle))
                : WindowContextReader.ReadForeground();

            var name = ReadString(
                element,
                AutomationElement.NameProperty);

            var automationId = ReadString(
                element,
                AutomationElement.AutomationIdProperty);

            var controlType =
                element.Current.ControlType?.ProgrammaticName;

            return new UiAutomationTargetSnapshot(
                window,
                name,
                automationId,
                controlType,
                isPassword);
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBool(
        AutomationElement element,
        AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) is bool value &&
        value;

    private static int ReadInt(
        AutomationElement element,
        AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) is int value
            ? value
            : 0;

    private static string? ReadString(
        AutomationElement element,
        AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) as string;

    private void ClearFocus()
    {
        _focusedElement = null;
        _valuePattern = null;
        _textPattern = null;
        _lastObservedValue = string.Empty;
        _elementIdentity = null;
        _currentTarget = null;
        _captureExcluded = true;
        _nextPatternRetryUtc = DateTime.MinValue;
    }

    private void ClearWorkerState() => ClearFocus();

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _wakeEvent.Set();

        if (Thread.CurrentThread != _workerThread &&
            _workerThread.IsAlive)
        {
            _workerThread.Join(250);
        }

        _wakeEvent.Dispose();
        _ready.Dispose();
    }
}
