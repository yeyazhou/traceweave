using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

/// <summary>
/// Observes focus and accessible value/text changes. Password controls are never read.
/// Text values are debounced so IME composition and rapid keystrokes settle before capture.
/// </summary>
public sealed class WindowsUiAutomationObserver : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IWindowExclusionPolicy _exclusionPolicy;
    private readonly ITextRedactor _redactor;
    private readonly DispatcherTimer _textDebounceTimer;
    private readonly AutomationFocusChangedEventHandler _focusChangedHandler;
    private readonly AutomationPropertyChangedEventHandler _propertyChangedHandler;
    private readonly AutomationEventHandler _textChangedHandler;

    private AutomationElement? _focusedElement;
    private UiAutomationTargetSnapshot? _currentTarget;
    private string _lastObservedValue = string.Empty;
    private bool _hasValuePattern;
    private bool _hasTextPattern;
    private bool _disposed;

    private readonly bool _captureCommittedText;

    public WindowsUiAutomationObserver(
        Dispatcher dispatcher,
        IWindowExclusionPolicy exclusionPolicy,
        ITextRedactor redactor,
        bool captureCommittedText = true)
    {
        _dispatcher = dispatcher;
        _exclusionPolicy = exclusionPolicy;
        _redactor = redactor;
        _captureCommittedText = captureCommittedText;

        _focusChangedHandler = OnFocusChanged;
        _propertyChangedHandler = OnPropertyChanged;
        _textChangedHandler = OnTextChanged;

        _textDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _textDebounceTimer.Tick += OnTextDebounceTick;

        Automation.AddAutomationFocusChangedEventHandler(_focusChangedHandler);
    }

    public event Action<UiAutomationObservation>? Observed;

    public UiAutomationTargetSnapshot? CurrentTarget => _currentTarget;

    public bool IsCaptureExcluded { get; private set; }

    public UiAutomationTargetSnapshot? ResolveAtPoint(int x, int y)
    {
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

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs args)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null) return;

            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => AttachFocusedElement(element)));
        }
        catch
        {
            // Focus can move while the provider is being queried.
        }
    }

    private void AttachFocusedElement(AutomationElement element)
    {
        if (_disposed) return;

        DetachCurrentElement();

        var target = ReadTarget(element);
        if (target is null)
        {
            _currentTarget = null;
            IsCaptureExcluded = true;
            return;
        }

        _currentTarget = target;
        IsCaptureExcluded = target.IsPassword ||
            _exclusionPolicy.IsExcluded(target.Window.ProcessName, target.Window.Title);
        if (IsCaptureExcluded) return;

        _focusedElement = element;
        if (!_captureCommittedText)
        {
            Observed?.Invoke(new UiAutomationObservation(CaptureEventKind.UiElementFocused, target));
            return;
        }

        _lastObservedValue = ReadCurrentText(element);
        _hasValuePattern = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
        _hasTextPattern = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        try
        {
            if (_hasValuePattern)
            {
                Automation.AddAutomationPropertyChangedEventHandler(
                    element,
                    TreeScope.Element,
                    _propertyChangedHandler,
                    ValuePattern.ValueProperty);
            }

            if (_hasTextPattern)
            {
                Automation.AddAutomationEventHandler(
                    TextPattern.TextChangedEvent,
                    element,
                    TreeScope.Element,
                    _textChangedHandler);
            }
        }
        catch
        {
            // Some providers expose patterns but do not support event subscriptions.
        }

        Observed?.Invoke(new UiAutomationObservation(CaptureEventKind.UiElementFocused, target));
    }

    private void OnPropertyChanged(object sender, AutomationPropertyChangedEventArgs args)
    {
        if (args.Property == ValuePattern.ValueProperty)
        {
            ScheduleTextRead();
        }
    }

    private void OnTextChanged(object sender, AutomationEventArgs args) => ScheduleTextRead();

    private void ScheduleTextRead()
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (_disposed || _currentTarget is null || _currentTarget.IsPassword) return;
                _textDebounceTimer.Stop();
                _textDebounceTimer.Start();
            }));
    }

    private void OnTextDebounceTick(object? sender, EventArgs args)
    {
        _textDebounceTimer.Stop();

        if (_disposed || _focusedElement is null || _currentTarget is null || _currentTarget.IsPassword)
        {
            return;
        }

        try
        {
            var currentValue = ReadCurrentText(_focusedElement);
            var insertedText = TextDiff.ExtractInsertedText(_lastObservedValue, currentValue);
            _lastObservedValue = currentValue;

            if (string.IsNullOrEmpty(insertedText)) return;

            var redaction = _redactor.Inspect(
                insertedText,
                _currentTarget.Window.ProcessName,
                _currentTarget.Window.Title);

            Observed?.Invoke(new UiAutomationObservation(
                CaptureEventKind.TextCommitted,
                _currentTarget,
                redaction.Value,
                redaction.Disposition,
                redaction.Sensitivity,
                redaction.Reason));
        }
        catch
        {
            // A control can disappear or lose its UIA provider during a text-change notification.
        }
    }

    private static UiAutomationTargetSnapshot? ReadTarget(AutomationElement element)
    {
        try
        {
            var isPassword = ReadBool(element, AutomationElement.IsPasswordProperty);
            var nativeHandle = ReadInt(element, AutomationElement.NativeWindowHandleProperty);
            var window = nativeHandle != 0
                ? WindowContextReader.Read(new IntPtr(nativeHandle))
                : WindowContextReader.ReadForeground();

            var name = ReadString(element, AutomationElement.NameProperty);
            var automationId = ReadString(element, AutomationElement.AutomationIdProperty);
            var controlType = element.Current.ControlType?.ProgrammaticName;

            return new UiAutomationTargetSnapshot(window, name, automationId, controlType, isPassword);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadCurrentText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value ?? string.Empty;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                return ((TextPattern)textPattern).DocumentRange.GetText(4096) ?? string.Empty;
            }
        }
        catch
        {
            // Unsupported or unavailable UIA pattern.
        }

        return string.Empty;
    }

    private static bool ReadBool(AutomationElement element, AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) is bool value && value;

    private static int ReadInt(AutomationElement element, AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) is int value ? value : 0;

    private static string? ReadString(AutomationElement element, AutomationProperty property) =>
        element.GetCurrentPropertyValue(property, true) as string;

    private void DetachCurrentElement()
    {
        _textDebounceTimer.Stop();

        if (_focusedElement is not null)
        {
            try
            {
                if (_hasValuePattern)
                {
                    Automation.RemoveAutomationPropertyChangedEventHandler(
                        _focusedElement,
                        _propertyChangedHandler);
                }

                if (_hasTextPattern)
                {
                    Automation.RemoveAutomationEventHandler(
                        TextPattern.TextChangedEvent,
                        _focusedElement,
                        _textChangedHandler);
                }
            }
            catch
            {
                // The provider may already have been disposed by the target application.
            }
        }

        _focusedElement = null;
        _currentTarget = null;
        IsCaptureExcluded = false;
        _lastObservedValue = string.Empty;
        _hasValuePattern = false;
        _hasTextPattern = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Automation.RemoveAutomationFocusChangedEventHandler(_focusChangedHandler);
        DetachCurrentElement();
        _textDebounceTimer.Tick -= OnTextDebounceTick;
    }
}
