using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using TraceWeave.Core.Capture;

namespace TraceWeave.Infrastructure.Windows;

/// <summary>
/// Coordinates a visible, explicit recording session. Hooks are installed only between
/// StartAsync and StopAsync. Raw events are serialized through one local journal writer.
/// </summary>
public sealed class WindowsCaptureCoordinator : IAsyncDisposable
{
    private readonly ICaptureEventJournal _journal;
    private readonly Dispatcher _dispatcher;
    private readonly ITextRedactor _redactor;

    private IWindowExclusionPolicy? _exclusionPolicy;
    private CaptureOptions? _options;
    private Guid _recordingId;
    private long _sequence;
    private bool _isRecording;
    private bool _isPaused;
    private string? _lastWindowKey;

    private Channel<CaptureEvent>? _eventChannel;
    private Task? _writerTask;
    private WindowsInputHook? _inputHook;
    private ForegroundWindowObserver? _windowObserver;
    private WindowsUiAutomationObserver? _uiAutomationObserver;
    private ClipboardMessageWindow? _clipboardWindow;

    public WindowsCaptureCoordinator(
        ICaptureEventJournal journal,
        Dispatcher dispatcher,
        ITextRedactor redactor)
    {
        _journal = journal;
        _dispatcher = dispatcher;
        _redactor = redactor;
    }

    public bool IsRecording => _isRecording;
    public bool IsPaused => _isPaused;
    public long DroppedEventCount { get; private set; }

    public event Action<Exception>? PersistenceError;
    public event Action<bool, bool>? StateChanged;    public event Action<CaptureEvent>? EventObserved;

    public Task StartAsync(
        Guid recordingId,
        CaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Recording must be started on the WPF dispatcher thread.");
        }

        if (_isRecording)
        {
            throw new InvalidOperationException("A recording session is already active.");
        }

        _recordingId = recordingId;
        _options = options;
        _sequence = 0;
        DroppedEventCount = 0;
        _isPaused = false;
        _lastWindowKey = null;
        _exclusionPolicy = new DefaultWindowExclusionPolicy(options.ExcludedProcessNames);

        _eventChannel = Channel.CreateUnbounded<CaptureEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(() => WriteEventsAsync(_eventChannel.Reader));

        try
        {
            if (options.CaptureUiAutomation)
            {
                _uiAutomationObserver = new WindowsUiAutomationObserver(
                    _dispatcher,
                    _exclusionPolicy,
                    _redactor,
                    options.CaptureCommittedText);
                _uiAutomationObserver.Observed += OnUiAutomationObserved;
            }

            if (options.CaptureMouse || options.CaptureKeyboardShortcuts)
            {
                _inputHook = new WindowsInputHook(options.CaptureMouse, options.CaptureKeyboardShortcuts);
                _inputHook.Observed += OnInputObserved;
            }

            if (options.CaptureWindowChanges)
            {
                _windowObserver = new ForegroundWindowObserver();
                _windowObserver.ForegroundChanged += OnForegroundChanged;
                OnForegroundChanged(WindowContextReader.ReadForeground());
            }

            if (options.CaptureClipboardMetadata)
            {
                _clipboardWindow = new ClipboardMessageWindow();
                _clipboardWindow.ClipboardUpdated += OnClipboardUpdated;
            }

            _isRecording = true;
            StateChanged?.Invoke(_isRecording, _isPaused);
            return Task.CompletedTask;
        }
        catch
        {
            _isRecording = true;
            _ = StopAsync();
            throw;
        }
    }

    public void Pause()
    {
        if (!_isRecording || _isPaused) return;
        _isPaused = true;
        StateChanged?.Invoke(_isRecording, _isPaused);
    }

    public void Resume()
    {
        if (!_isRecording || !_isPaused) return;
        _isPaused = false;
        StateChanged?.Invoke(_isRecording, _isPaused);
    }

    public async Task StopAsync()
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(StopAsync).Task.Unwrap();
            return;
        }

        if (!_isRecording && _eventChannel is null) return;

        _isRecording = false;
        _isPaused = false;

        if (_inputHook is not null)
        {
            _inputHook.Observed -= OnInputObserved;
            _inputHook.Dispose();
            _inputHook = null;
        }

        if (_windowObserver is not null)
        {
            _windowObserver.ForegroundChanged -= OnForegroundChanged;
            _windowObserver.Dispose();
            _windowObserver = null;
        }

        if (_uiAutomationObserver is not null)
        {
            _uiAutomationObserver.Observed -= OnUiAutomationObserved;
            _uiAutomationObserver.Dispose();
            _uiAutomationObserver = null;
        }

        if (_clipboardWindow is not null)
        {
            _clipboardWindow.ClipboardUpdated -= OnClipboardUpdated;
            _clipboardWindow.Dispose();
            _clipboardWindow = null;
        }

        _eventChannel?.Writer.TryComplete();
        if (_writerTask is not null)
        {
            await _writerTask;
        }

        _eventChannel = null;
        _writerTask = null;
        _options = null;
        _exclusionPolicy = null;
        _lastWindowKey = null;
        StateChanged?.Invoke(false, false);
    }

    private void OnInputObserved(RawInputObservation observation)
    {
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => HandleInputObservation(observation)));
    }

    private void HandleInputObservation(RawInputObservation observation)
    {
        if (!CanCapture()) return;

        var foreground = WindowContextReader.ReadForeground();
        if (IsExcluded(foreground.ProcessName, foreground.Title)) return;

        var target = observation.Source == CaptureSource.MouseHook
            ? _uiAutomationObserver?.ResolveAtPoint(observation.X, observation.Y)
            : _uiAutomationObserver?.CurrentTarget;

        if (target?.IsPassword == true ||
            (target is not null && IsExcluded(target.Window.ProcessName, target.Window.Title)))
        {
            return;
        }

        if (observation.Source == CaptureSource.KeyboardHook &&
            _uiAutomationObserver?.IsCaptureExcluded == true)
        {
            return;
        }

        var window = target?.Window ?? foreground;
        var metadata = ImmutableDictionary.CreateBuilder<string, string>();

        if (!string.IsNullOrWhiteSpace(observation.Shortcut))
        {
            metadata["shortcut"] = observation.Shortcut;
        }

        if (!string.IsNullOrWhiteSpace(observation.Button))
        {
            metadata["button"] = observation.Button;
        }

        if (observation.Kind is CaptureEventKind.MouseClick or CaptureEventKind.MouseDoubleClick or CaptureEventKind.MouseScroll)
        {
            metadata["x"] = observation.X.ToString(CultureInfo.InvariantCulture);
            metadata["y"] = observation.Y.ToString(CultureInfo.InvariantCulture);
        }

        if (observation.Kind == CaptureEventKind.MouseDrag)
        {
            metadata["startX"] = observation.StartX.ToString(CultureInfo.InvariantCulture);
            metadata["startY"] = observation.StartY.ToString(CultureInfo.InvariantCulture);
            metadata["endX"] = observation.X.ToString(CultureInfo.InvariantCulture);
            metadata["endY"] = observation.Y.ToString(CultureInfo.InvariantCulture);
        }

        if (observation.Kind == CaptureEventKind.MouseScroll)
        {
            metadata["delta"] = observation.Delta.ToString(CultureInfo.InvariantCulture);
        }

        Enqueue(new CaptureEvent(
            Guid.NewGuid(),
            _recordingId,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            observation.Kind,
            observation.Source,
            window.ProcessName,
            window.Title,
            target?.ElementName,
            target?.AutomationId,
            target?.ControlType,
            Metadata: metadata.ToImmutable()));

        if (observation.Shortcut is "CTRL+V" or "CONTROL+V")
        {
            EmitClipboardPaste(window, target);
        }
    }

    private void EmitClipboardPaste(WindowContext window, UiAutomationTargetSnapshot? target)
    {
        if (_options?.CaptureClipboardMetadata != true ||
            target?.IsPassword == true ||
            _uiAutomationObserver?.IsCaptureExcluded == true)
        {
            return;
        }

        var sequence = GetClipboardSequenceNumber().ToString(CultureInfo.InvariantCulture);
        Enqueue(new CaptureEvent(
            Guid.NewGuid(),
            _recordingId,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            CaptureEventKind.ClipboardPasted,
            CaptureSource.ClipboardListener,
            window.ProcessName,
            window.Title,
            target?.ElementName,
            target?.AutomationId,
            target?.ControlType,
            TextDisposition: TextCaptureDisposition.Suppressed,
            ClipboardCorrelationId: sequence,
            Metadata: ImmutableDictionary<string, string>.Empty.Add("captureMode", "paste-shortcut")));
    }

    private void OnClipboardUpdated(object? sender, EventArgs args)
    {
        if (!CanCapture() || _options?.CaptureClipboardMetadata != true) return;

        var window = WindowContextReader.ReadForeground();
        if (IsExcluded(window.ProcessName, window.Title) ||
            _uiAutomationObserver?.IsCaptureExcluded == true)
        {
            return;
        }

        string? text = null;
        var disposition = TextCaptureDisposition.Suppressed;
        var sensitivity = SensitivityLevel.Public;

        if (_options.CaptureClipboardText)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    var rawText = Clipboard.GetText(TextDataFormat.UnicodeText);
                    var redaction = _redactor.Inspect(rawText, window.ProcessName, window.Title);
                    text = redaction.Value;
                    disposition = redaction.Disposition;
                    sensitivity = redaction.Sensitivity;
                }
                else
                {
                    disposition = TextCaptureDisposition.Unavailable;
                }
            }
            catch
            {
                disposition = TextCaptureDisposition.Unavailable;
            }
        }

        var sequence = GetClipboardSequenceNumber().ToString(CultureInfo.InvariantCulture);
        var target = _uiAutomationObserver?.CurrentTarget;

        Enqueue(new CaptureEvent(
            Guid.NewGuid(),
            _recordingId,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            CaptureEventKind.ClipboardCopied,
            CaptureSource.ClipboardListener,
            window.ProcessName,
            window.Title,
            target?.ElementName,
            target?.AutomationId,
            target?.ControlType,
            text,
            disposition,
            sequence,
            Metadata: ImmutableDictionary<string, string>.Empty.Add(
                "captureMode",
                _options.CaptureClipboardText ? "text-opt-in" : "metadata-only"),
            Sensitivity: sensitivity));
    }

    private void OnForegroundChanged(WindowContext window)
    {
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!CanCapture() || IsExcluded(window.ProcessName, window.Title)) return;

                var key = $"{window.ProcessId}|{window.Handle}|{window.Title}";
                if (string.Equals(key, _lastWindowKey, StringComparison.Ordinal)) return;
                _lastWindowKey = key;

                Enqueue(new CaptureEvent(
                    Guid.NewGuid(),
                    _recordingId,
                    Interlocked.Increment(ref _sequence),
                    DateTimeOffset.UtcNow,
                    CaptureEventKind.WindowActivated,
                    CaptureSource.WindowWatcher,
                    window.ProcessName,
                    window.Title,
                    Metadata: ImmutableDictionary<string, string>.Empty
                        .Add("windowHandle", window.Handle.ToInt64().ToString(CultureInfo.InvariantCulture))
                        .Add("processId", window.ProcessId.ToString(CultureInfo.InvariantCulture))));
            }));
    }

    private void OnUiAutomationObserved(UiAutomationObservation observation)
    {
        if (!CanCapture()) return;

        var target = observation.Target;
        if (target.IsPassword || IsExcluded(target.Window.ProcessName, target.Window.Title)) return;

        Enqueue(new CaptureEvent(
            Guid.NewGuid(),
            _recordingId,
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            observation.Kind,
            CaptureSource.UiAutomation,
            target.Window.ProcessName,
            target.Window.Title,
            target.ElementName,
            target.AutomationId,
            target.ControlType,
            observation.TextValue,
            observation.TextDisposition,
            Metadata: observation.RedactionReason is null
                ? null
                : ImmutableDictionary<string, string>.Empty.Add("redactionReason", observation.RedactionReason),
            Sensitivity: observation.Sensitivity));
    }

    private bool IsExcluded(string? processName, string? title) =>
        _exclusionPolicy?.IsExcluded(processName, title) == true;

    private bool CanCapture() => _isRecording && !_isPaused && _eventChannel is not null;

    private void Enqueue(CaptureEvent captureEvent)
    {
        if (!CanCapture()) return;

        if (!_eventChannel!.Writer.TryWrite(captureEvent))
        {
            DroppedEventCount++;
            return;
        }

        EventObserved?.Invoke(captureEvent);
    }

    private async Task WriteEventsAsync(ChannelReader<CaptureEvent> reader)
    {
        await foreach (var captureEvent in reader.ReadAllAsync())
        {
            try
            {
                await _journal.AppendAsync(captureEvent);
            }
            catch (Exception exception)
            {
                PersistenceError?.Invoke(exception);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
