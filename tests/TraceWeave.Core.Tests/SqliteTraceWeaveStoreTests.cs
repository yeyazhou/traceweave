using System.IO;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Editing;
using TraceWeave.Core.Generation;
using TraceWeave.Core.Optimization;
using TraceWeave.Core.Workflows;
using TraceWeave.Infrastructure.Storage;

namespace TraceWeave.Core.Tests;

public sealed class SqliteTraceWeaveStoreTests
{
    [Fact]
    public async Task CaptureEventsArePersistedInSequence_AndDirectDeleteIsBlocked()
    {
        var databasePath = CreateDatabasePath();
        var store = new SqliteTraceWeaveStore(databasePath);
        var recordingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var first = NewEvent(recordingId, 1, now);
        var second = NewEvent(recordingId, 2, now.AddSeconds(1));

        await store.InitializeAsync();
        await store.AppendAsync(second);
        await store.AppendAsync(first);

        var events = await store.ReadRecordingAsync(recordingId);
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].Sequence);
        Assert.Equal(2, events[1].Sequence);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM capture_events;";
        await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task RecordingPurgeRequiresConfirmation_AndLeavesAuditTombstone()
    {
        var databasePath = CreateDatabasePath();
        var store = new SqliteTraceWeaveStore(databasePath);
        var recordingId = Guid.NewGuid();

        await store.AppendAsync(NewEvent(recordingId, 1, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PurgeRecordingAsync(recordingId, "User requested deletion", false));

        await store.PurgeRecordingAsync(recordingId, "User requested deletion", true);
        Assert.Empty(await store.ReadRecordingAsync(recordingId));

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM purge_audit WHERE recording_id = $id;";
        audit.Parameters.AddWithValue("$id", recordingId.ToString("N"));
        Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WorkflowRevisionsRoundTripWithoutReplacingEarlierSnapshots()
    {
        var databasePath = CreateDatabasePath();
        var store = new SqliteTraceWeaveStore(databasePath);
        var workflow = await CreateWorkflowAsync();
        var originalTitle = workflow.CurrentRevision.Steps[0].Title;

        await store.SaveAsync(workflow);

        var editor = new WorkflowRevisionEditor();
        var current = workflow.CurrentRevision;
        var changed = editor.UpdateStep(
            workflow,
            current.Steps[0].Id,
            current.Steps[0] with { Title = "Edited title" },
            "Manual test edit.");
        await store.SaveAsync(changed);

        var loaded = await store.GetAsync(workflow.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Revisions.Length);
        Assert.Equal(originalTitle, loaded.Revisions[0].Steps[0].Title);
        Assert.Equal("Edited title", loaded.CurrentRevision.Steps[0].Title);
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TraceWeave.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "traceweave.db");
    }

    private static CaptureEvent NewEvent(Guid recordingId, long sequence, DateTimeOffset time) =>
        new(
            Guid.NewGuid(),
            recordingId,
            sequence,
            time,
            CaptureEventKind.MouseClick,
            CaptureSource.MouseHook,
            "test.exe",
            "Test window",
            "Save",
            "save-button",
            "Button");

    private static async Task<WorkflowDocument> CreateWorkflowAsync()
    {
        var recordingId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var events = ImmutableArray.Create(
            NewEvent(recordingId, 1, now),
            NewEvent(recordingId, 2, now.AddMilliseconds(200)) with { AutomationId = "open-button", ElementName = "Open" });

        var generator = new FirstPassWorkflowGenerator(
            new RuleBasedWorkflowSynthesisProvider(),
            new FirstPassOptimizer());

        return (await generator.GenerateFirstPassAsync(
            new WorkflowSynthesisRequest(recordingId, "Test workflow", events))).Workflow;
    }
}
