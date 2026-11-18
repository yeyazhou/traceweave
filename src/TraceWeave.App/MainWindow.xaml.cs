using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Review;
using TraceWeave.Core.Workflows;
using TraceWeave.Infrastructure.Windows;

namespace TraceWeave.App;

public partial class MainWindow : Window
{
    private readonly TraceWeaveRuntime _runtime;
    private readonly ObservableCollection<string> _timeline = [];
    private readonly ObservableCollection<string> _findings = [];
    private readonly ObservableCollection<ProposalView> _proposals = [];
    private readonly ObservableCollection<WorkflowStep> _steps = [];
    private readonly ObservableCollection<string> _revisions = [];
    private readonly ConcurrentQueue<string> _pendingTimeline = new();
    private readonly DispatcherTimer _timelineDrainTimer;
    private long _observedEventCount;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        TimelineList.ItemsSource = _timeline;
        FindingList.ItemsSource = _findings;
        ProposalList.ItemsSource = _proposals;
        StepList.ItemsSource = _steps;
        RevisionList.ItemsSource = _revisions;

        _timelineDrainTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timelineDrainTimer.Tick += (_, _) => DrainTimeline();
        _timelineDrainTimer.Start();

        _runtime = new TraceWeaveRuntime(Dispatcher);
        _runtime.EventObserved += OnEventObserved;
        _runtime.StateChanged += OnStateChanged;
        _runtime.PersistenceError += OnPersistenceError;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.InitializeAsync();
            UpdateUi();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "TraceWeave 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;

        e.Cancel = true;
        try
        {
            _timelineDrainTimer.Stop();
            await _runtime.DisposeAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            int.TryParse(button.Tag?.ToString(), out var index) &&
            index >= 0 &&
            index < MainTabs.Items.Count)
        {
            MainTabs.SelectedIndex = index;
        }
    }

    private void StartFromDashboard_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        IntentBox.Focus();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            var options = new CaptureOptions
            {
                CaptureMouse = MouseCheck.IsChecked == true,
                CaptureKeyboardShortcuts = ShortcutCheck.IsChecked == true,
                CaptureClipboardMetadata = ClipboardMetaCheck.IsChecked == true,
                CaptureClipboardText = ClipboardTextCheck.IsChecked == true,
                CaptureWindowChanges = true,
                CaptureUiAutomation = UiAutomationCheck.IsChecked == true,
                CaptureCommittedText = CommittedTextCheck.IsChecked == true,
                ExcludedProcessNames = ParseExcludedProcesses()
            };

            _pendingTimeline.Clear();
            _timeline.Clear();
            Interlocked.Exchange(ref _observedEventCount, 0);
            _steps.Clear();
            _revisions.Clear();
            _findings.Clear();
            _proposals.Clear();
            RecordingCountText.Text = "0";
            PersistenceText.Text = "无";
            CurrentWorkflowReset();

            await _runtime.StartRecordingAsync(options);

            RecordingIdText.Text = _runtime.CurrentRecordingId?.ToString() ?? "—";
            CaptureHint.Text = "正在录制。建议现在切换到要记录的应用完成一次完整任务。";
            UpdateUi();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法开始录制", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "正在停止采集并生成 Draft v1…");
            var result = await _runtime.StopRecordingAndGenerateAsync(IntentBox.Text);

            if (result is null)
            {
                MessageBox.Show(this, "没有活动的录制。", "TraceWeave", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyWorkflowToUi();
            MainTabs.SelectedIndex = 2;

            MessageBox.Show(
                this,
                $"首次优化完成。\n\n原始事件：{result.Report.InputEventCount}\n候选步骤：{result.Report.CandidateStepCount}\n最终步骤：{result.Report.OutputStepCount}\n去掉噪声：{result.Report.NoiseEventsRemoved}\n合并重复：{result.Report.DuplicateStepsMerged}\n剪贴板关联：{result.Report.ClipboardTransfersCorrelated}",
                "Draft v1 已生成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.ToString(), "生成工作流失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _runtime.PauseRecording();
        CaptureHint.Text = "已暂停。暂停期间不会追加原始事件。";
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        _runtime.ResumeRecording();
        CaptureHint.Text = "已继续录制。";
    }

    private void OnEventObserved(CaptureEvent captureEvent)
    {
        var target = captureEvent.AutomationId ??
                     captureEvent.ElementName ??
                     captureEvent.ControlType;

        var context = string.Join(
            " · ",
            new[]
            {
                captureEvent.ProcessName,
                captureEvent.WindowTitle,
                target
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var text = captureEvent.TextDisposition == TextCaptureDisposition.Captured &&
                   !string.IsNullOrWhiteSpace(captureEvent.TextValue)
            ? $" text={TrimForUi(captureEvent.TextValue, 60)}"
            : captureEvent.TextDisposition is TextCaptureDisposition.Redacted or
                TextCaptureDisposition.Suppressed
                ? $" text={captureEvent.TextDisposition}"
                : string.Empty;

        _pendingTimeline.Enqueue(
            $"{captureEvent.Sequence:0000}  {captureEvent.OccurredAtUtc:HH:mm:ss.fff}  " +
            $"{captureEvent.Kind}  {context}{text}");

        Interlocked.Increment(ref _observedEventCount);
    }

    private void DrainTimeline()
    {
        const int maxItemsPerTick = 80;
        var drained = 0;

        while (drained < maxItemsPerTick &&
               _pendingTimeline.TryDequeue(out var item))
        {
            _timeline.Add(item);
            drained++;
        }

        while (_timeline.Count > 500)
        {
            _timeline.RemoveAt(0);
        }

        var count = Interlocked.Read(ref _observedEventCount).ToString();
        RecordingCountText.Text = count;
        DashboardEventCount.Text = count;

        if (drained > 0 && TimelineList.Items.Count > 0)
        {
            TimelineList.ScrollIntoView(TimelineList.Items[^1]);
        }
    }
    private void OnStateChanged(bool recording, bool paused)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StartButton.IsEnabled = !recording;
            PauseButton.IsEnabled = recording && !paused;
            ResumeButton.IsEnabled = recording && paused;
            StopButton.IsEnabled = recording;

            if (!recording)
            {
                StatusText.Text = "  采集未启动";
                HeaderStatus.Text = "● 本地优先";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(49, 196, 141));
            }
            else if (paused)
            {
                StatusText.Text = "  采集中 · 已暂停";
                HeaderStatus.Text = "● 已暂停";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }
            else
            {
                StatusText.Text = "  正在采集";
                HeaderStatus.Text = "● 正在采集";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        }));
    }

    private void OnPersistenceError(Exception exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PersistenceText.Text = exception.Message;
            PersistenceText.Foreground = new SolidColorBrush(Color.FromRgb(190, 40, 40));
        }));
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepList.SelectedItem is not WorkflowStep step) return;

        StepTitleBox.Text = step.Title;
        StepIntentBox.Text = step.Intent;
        StepInputBox.Text = step.InputTemplate ?? string.Empty;
        StepShortcutBox.Text = step.Shortcut ?? string.Empty;
    }

    private async void SaveStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not WorkflowStep step)
        {
            MessageBox.Show(this, "先选择一个步骤。", "TraceWeave", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, "保存步骤版本…");
            await _runtime.EditStepAsync(
                step.Id,
                StepTitleBox.Text,
                StepIntentBox.Text,
                StepInputBox.Text,
                StepShortcutBox.Text);
            ApplyWorkflowToUi(step.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "保存步骤失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not WorkflowStep step) return;

        if (MessageBox.Show(
                this,
                $"确定删除步骤 {step.Order}「{step.Title}」吗？历史版本不会被修改。",
                "删除步骤",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "创建删除版本…");
            await _runtime.RemoveStepAsync(step.Id);
            ApplyWorkflowToUi();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "删除步骤失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        await MoveSelectedAsync(-1);
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        await MoveSelectedAsync(1);
    }

    private async Task MoveSelectedAsync(int delta)
    {
        if (StepList.SelectedItem is not WorkflowStep step) return;
        var targetOrder = step.Order + delta;

        if (targetOrder < 1 || _runtime.CurrentWorkflow is null ||
            targetOrder > _runtime.CurrentWorkflow.CurrentRevision.Steps.Length)
        {
            return;
        }

        try
        {
            SetBusy(true, "创建重排版本…");
            await _runtime.MoveStepAsync(step.Id, targetOrder);
            ApplyWorkflowToUi(step.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "重排步骤失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RevisionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // RevisionList displays a stable textual snapshot; restore uses the selected index.
    }

    private async void RestoreRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.CurrentWorkflow is null || RevisionList.SelectedIndex < 0) return;
        if (RevisionList.SelectedIndex >= _runtime.CurrentWorkflow.Revisions.Length) return;

        var revision = _runtime.CurrentWorkflow.Revisions[RevisionList.SelectedIndex];
        if (MessageBox.Show(
                this,
                $"恢复到 v{revision.Version}？这会创建新的 Restore 版本，不会覆盖历史。",
                "恢复历史版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "创建恢复版本…");
            await _runtime.RestoreRevisionAsync(revision.Id);
            ApplyWorkflowToUi();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.CurrentWorkflow is null)
        {
            MessageBox.Show(this, "请先完成一次录制并生成 Draft v1。", "TraceWeave", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, "正在进行二次审查…");
            var result = await _runtime.ReviewAsync(
                RemoteReviewRadio.IsChecked == true,
                AiEndpointBox.Text,
                AiModelBox.Text,
                AiKeyBox.Password,
                ReviewInstructionBox.Text);

            _findings.Clear();
            foreach (var finding in result.Findings)
            {
                _findings.Add($"[{finding.Severity}] {finding.Title} · 置信度 {finding.Confidence:P0}\n{finding.Explanation}");
            }

            _proposals.Clear();
            foreach (var proposal in result.Proposals)
            {
                _proposals.Add(new ProposalView(
                    proposal.Id,
                    DescribeProposal(proposal),
                    true));
            }

            ReviewSummaryText.Text = $"{result.Provider} / {result.ModelId}：{result.Summary}";
            MainTabs.SelectedIndex = 3;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "AI 二次审查失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ApplyProposals_Click(object sender, RoutedEventArgs e)
    {
        var selected = _proposals.Where(item => item.Selected).Select(item => item.Id).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "没有勾选任何提案。", "TraceWeave", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetBusy(true, "应用 AI 提案并创建新版本…");
            await _runtime.ApplyReviewAsync(selected);
            ApplyWorkflowToUi();
            MessageBox.Show(this, $"已应用 {selected.Length} 个提案。历史版本仍保留。", "AI 校正完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "应用 AI 提案失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        _ = ExportWorkflowAsync();
    }

    private async Task ExportWorkflowAsync()
    {
        if (_runtime.CurrentWorkflow is null)
        {
            MessageBox.Show(this, "还没有工作流可以导出。", "TraceWeave", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 TraceWeave 导出目录",
                Multiselect = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (dialog.ShowDialog(this) != true) return;

            SetBusy(true, "导出 workflow.json 和 SKILL.md…");
            var result = await _runtime.ExportAsync(dialog.FolderName);

            MessageBox.Show(
                this,
                $"导出完成：\n\n{result.WorkflowJsonPath}\n{result.SkillMarkdownPath}",
                "TraceWeave 导出完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyWorkflowToUi(Guid? selectedStepId = null)
    {
        _steps.Clear();
        _revisions.Clear();

        var workflow = _runtime.CurrentWorkflow;
        if (workflow is null)
        {
            WorkflowSummaryText.Text = "完成一次录制后，这里会出现 Draft v1。";
            DashboardStepCount.Text = "0";
            DashboardVersion.Text = "—";
            return;
        }

        foreach (var step in workflow.CurrentRevision.Steps)
        {
            _steps.Add(step);
        }

        foreach (var revision in workflow.Revisions)
        {
            _revisions.Add($"v{revision.Version} · {revision.Kind} · {revision.Author} · {revision.CreatedAtUtc:MM-dd HH:mm} · {revision.Summary}");
        }

        WorkflowSummaryText.Text = $"{workflow.Name} · 当前 v{workflow.CurrentRevision.Version} · {workflow.CurrentRevision.Steps.Length} 个步骤 · 置信度 {(workflow.CurrentRevision.Confidence ?? 0):P0}";
        DashboardStepCount.Text = workflow.CurrentRevision.Steps.Length.ToString();
        DashboardVersion.Text = $"v{workflow.CurrentRevision.Version}";

        if (selectedStepId is { } id)
        {
            var item = _steps.FirstOrDefault(step => step.Id == id);
            if (item is not null)
            {
                StepList.SelectedItem = item;
            }
        }
        else if (_steps.Count > 0)
        {
            StepList.SelectedIndex = 0;
        }
    }

    private void CurrentWorkflowReset()
    {
        WorkflowSummaryText.Text = "录制完成后自动生成 Draft v1。";
        DashboardStepCount.Text = "0";
        DashboardVersion.Text = "—";
    }

    private void UpdateUi()
    {
        var recording = _runtime.IsRecording;
        var paused = _runtime.IsPaused;

        StartButton.IsEnabled = !recording;
        PauseButton.IsEnabled = recording && !paused;
        ResumeButton.IsEnabled = recording && paused;
        StopButton.IsEnabled = recording;

        DashboardEventCount.Text = _timeline.Count.ToString();
        DashboardStepCount.Text = _runtime.CurrentWorkflow?.CurrentRevision.Steps.Length.ToString() ?? "0";
        DashboardVersion.Text = _runtime.CurrentWorkflow is null ? "—" : $"v{_runtime.CurrentWorkflow.CurrentRevision.Version}";
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            HeaderStatus.Text = $"● {message}";
        }

        if (busy)
        {
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            ResumeButton.IsEnabled = false;
            StopButton.IsEnabled = false;
        }
    }

    private HashSet<string> ParseExcludedProcesses()
    {
        return ExcludedProcessesBox.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Path.GetFileNameWithoutExtension(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string TrimForUi(string value, int maxLength)
    {
        var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
    }

    private static string DescribeProposal(WorkflowChangeProposal proposal)
    {
        var target = proposal.TargetStepId is null ? "" : $" target={proposal.TargetStepId}";
        var order = proposal.NewOrder is null ? "" : $" order={proposal.NewOrder}";
        return $"{proposal.Kind}{target}{order} · 置信度 {proposal.Confidence:P0}\n{proposal.Reason}";
    }

    private sealed class ProposalView
    {
        public ProposalView(Guid id, string text, bool selected)
        {
            Id = id;
            Text = text;
            Selected = selected;
        }

        public Guid Id { get; }
        public string Text { get; }
        public bool Selected { get; set; }
    }
}
