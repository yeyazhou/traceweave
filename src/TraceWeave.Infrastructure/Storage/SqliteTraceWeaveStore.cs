using System.IO;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TraceWeave.Core.Capture;
using TraceWeave.Core.Workflows;

namespace TraceWeave.Infrastructure.Storage;

/// <summary>
/// Local SQLite persistence for immutable capture events and versioned workflow snapshots.
/// Call InitializeAsync once during application startup; other operations also ensure initialization.
/// </summary>
public sealed class SqliteTraceWeaveStore : ICaptureEventJournal, IWorkflowRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private bool _initialized;

    public SqliteTraceWeaveStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task AppendAsync(CaptureEvent captureEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureEvent);
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var recordingCommand = connection.CreateCommand())
        {
            recordingCommand.Transaction = (SqliteTransaction)transaction;
            recordingCommand.CommandText = """
                INSERT INTO recordings (recording_id, first_event_at_utc)
                VALUES ($recordingId, $occurredAt)
                ON CONFLICT(recording_id) DO NOTHING;
                """;
            recordingCommand.Parameters.AddWithValue("$recordingId", captureEvent.RecordingId.ToString("N"));
            recordingCommand.Parameters.AddWithValue("$occurredAt", captureEvent.OccurredAtUtc.ToString("O"));
            await recordingCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO capture_events
                    (event_id, recording_id, sequence, occurred_at_utc, kind, source, sensitivity, payload_json)
                VALUES
                    ($eventId, $recordingId, $sequence, $occurredAt, $kind, $source, $sensitivity, $payload);
                """;
            command.Parameters.AddWithValue("$eventId", captureEvent.Id.ToString("N"));
            command.Parameters.AddWithValue("$recordingId", captureEvent.RecordingId.ToString("N"));
            command.Parameters.AddWithValue("$sequence", captureEvent.Sequence);
            command.Parameters.AddWithValue("$occurredAt", captureEvent.OccurredAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$kind", captureEvent.Kind.ToString());
            command.Parameters.AddWithValue("$source", captureEvent.Source.ToString());
            command.Parameters.AddWithValue("$sensitivity", captureEvent.Sensitivity.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(captureEvent, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CaptureEvent>> ReadRecordingAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json
            FROM capture_events
            WHERE recording_id = $recordingId
            ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("$recordingId", recordingId.ToString("N"));

        var events = new List<CaptureEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(0);
            var captureEvent = JsonSerializer.Deserialize<CaptureEvent>(payload, _jsonOptions);
            if (captureEvent is not null) events.Add(captureEvent);
        }

        return events;
    }

    public async Task SaveAsync(WorkflowDocument workflow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = (SqliteTransaction)transaction;
            metadata.CommandText = """
                INSERT INTO workflows (workflow_id, name, goal, created_at_utc, parameters_json)
                VALUES ($id, $name, $goal, $createdAt, $parameters)
                ON CONFLICT(workflow_id) DO UPDATE SET
                    name = excluded.name,
                    goal = excluded.goal,
                    parameters_json = excluded.parameters_json;
                """;
            metadata.Parameters.AddWithValue("$id", workflow.Id.ToString("N"));
            metadata.Parameters.AddWithValue("$name", workflow.Name);
            metadata.Parameters.AddWithValue("$goal", workflow.Goal);
            metadata.Parameters.AddWithValue("$createdAt", workflow.CreatedAtUtc.ToString("O"));
            metadata.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(workflow.Parameters, _jsonOptions));
            await metadata.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var revision in workflow.Revisions)
        {
            await using var revisionCommand = connection.CreateCommand();
            revisionCommand.Transaction = (SqliteTransaction)transaction;
            revisionCommand.CommandText = """
                INSERT INTO workflow_revisions
                    (revision_id, workflow_id, version, parent_revision_id, created_at_utc, kind, payload_json)
                VALUES
                    ($revisionId, $workflowId, $version, $parentRevisionId, $createdAt, $kind, $payload)
                ON CONFLICT(revision_id) DO NOTHING;
                """;
            revisionCommand.Parameters.AddWithValue("$revisionId", revision.Id.ToString("N"));
            revisionCommand.Parameters.AddWithValue("$workflowId", workflow.Id.ToString("N"));
            revisionCommand.Parameters.AddWithValue("$version", revision.Version);
            revisionCommand.Parameters.AddWithValue("$parentRevisionId", revision.ParentRevisionId?.ToString("N") ?? (object)DBNull.Value);
            revisionCommand.Parameters.AddWithValue("$createdAt", revision.CreatedAtUtc.ToString("O"));
            revisionCommand.Parameters.AddWithValue("$kind", revision.Kind.ToString());
            revisionCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(revision, _jsonOptions));
            await revisionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<WorkflowDocument?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        string name;
        string goal;
        DateTimeOffset createdAt;
        ImmutableArray<WorkflowParameter> parameters;

        await using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT name, goal, created_at_utc, parameters_json
                FROM workflows
                WHERE workflow_id = $id;
                """;
            metadata.Parameters.AddWithValue("$id", workflowId.ToString("N"));

            await using var reader = await metadata.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            name = reader.GetString(0);
            goal = reader.GetString(1);
            createdAt = DateTimeOffset.Parse(reader.GetString(2));
            parameters = JsonSerializer.Deserialize<ImmutableArray<WorkflowParameter>>(reader.GetString(3), _jsonOptions);
        }

        var revisions = ImmutableArray.CreateBuilder<WorkflowRevision>();
        await using (var revisionCommand = connection.CreateCommand())
        {
            revisionCommand.CommandText = """
                SELECT payload_json
                FROM workflow_revisions
                WHERE workflow_id = $id
                ORDER BY version ASC;
                """;
            revisionCommand.Parameters.AddWithValue("$id", workflowId.ToString("N"));

            await using var reader = await revisionCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var revision = JsonSerializer.Deserialize<WorkflowRevision>(reader.GetString(0), _jsonOptions);
                if (revision is not null) revisions.Add(revision);
            }
        }

        if (revisions.Count == 0) return null;
        return new WorkflowDocument(workflowId, name, goal, createdAt, parameters, revisions.ToImmutable());
    }

    /// <summary>
    /// Permanently removes one recording after an explicit user confirmation.
    /// A tombstone remains in purge_audit; capture events cannot otherwise be deleted.
    /// </summary>
    public async Task PurgeRecordingAsync(
        Guid recordingId,
        string reason,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmed)
        {
            throw new InvalidOperationException("Recording purge requires explicit user confirmation.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A purge reason is required.", nameof(reason));
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = (SqliteTransaction)transaction;
            audit.CommandText = """
                INSERT INTO purge_audit (recording_id, purged_at_utc, reason)
                VALUES ($id, $at, $reason);
                """;
            audit.Parameters.AddWithValue("$id", recordingId.ToString("N"));
            audit.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$reason", reason.Trim());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var authorization = connection.CreateCommand())
        {
            authorization.Transaction = (SqliteTransaction)transaction;
            authorization.CommandText = """
                INSERT INTO purge_authorizations (recording_id)
                VALUES ($id);
                """;
            authorization.Parameters.AddWithValue("$id", recordingId.ToString("N"));
            await authorization.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteEvents = connection.CreateCommand())
        {
            deleteEvents.Transaction = (SqliteTransaction)transaction;
            deleteEvents.CommandText = "DELETE FROM capture_events WHERE recording_id = $id;";
            deleteEvents.Parameters.AddWithValue("$id", recordingId.ToString("N"));
            await deleteEvents.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteRecording = connection.CreateCommand())
        {
            deleteRecording.Transaction = (SqliteTransaction)transaction;
            deleteRecording.CommandText = "DELETE FROM recordings WHERE recording_id = $id;";
            deleteRecording.Parameters.AddWithValue("$id", recordingId.ToString("N"));
            await deleteRecording.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearAuthorization = connection.CreateCommand())
        {
            clearAuthorization.Transaction = (SqliteTransaction)transaction;
            clearAuthorization.CommandText = "DELETE FROM purge_authorizations WHERE recording_id = $id;";
            clearAuthorization.Parameters.AddWithValue("$id", recordingId.ToString("N"));
            await clearAuthorization.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = FULL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS recordings (
            recording_id TEXT PRIMARY KEY,
            first_event_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS capture_events (
            event_id TEXT PRIMARY KEY,
            recording_id TEXT NOT NULL REFERENCES recordings(recording_id) ON DELETE RESTRICT,
            sequence INTEGER NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            kind TEXT NOT NULL,
            source TEXT NOT NULL,
            sensitivity TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            UNIQUE(recording_id, sequence)
        );

        CREATE INDEX IF NOT EXISTS ix_capture_events_recording_time
            ON capture_events(recording_id, occurred_at_utc);

        CREATE TRIGGER IF NOT EXISTS capture_events_no_update
        BEFORE UPDATE ON capture_events
        BEGIN
            SELECT RAISE(ABORT, 'capture_events is append-only');
        END;

        CREATE TABLE IF NOT EXISTS purge_audit (
            purge_id INTEGER PRIMARY KEY AUTOINCREMENT,
            recording_id TEXT NOT NULL,
            purged_at_utc TEXT NOT NULL,
            reason TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS purge_authorizations (
            recording_id TEXT PRIMARY KEY
        );

        CREATE TRIGGER IF NOT EXISTS capture_events_guard_delete
        BEFORE DELETE ON capture_events
        WHEN NOT EXISTS (
            SELECT 1 FROM purge_authorizations WHERE recording_id = OLD.recording_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'capture_events can only be deleted by an authorized recording purge');
        END;

        CREATE TABLE IF NOT EXISTS workflows (
            workflow_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            goal TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            parameters_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS workflow_revisions (
            revision_id TEXT PRIMARY KEY,
            workflow_id TEXT NOT NULL REFERENCES workflows(workflow_id) ON DELETE CASCADE,
            version INTEGER NOT NULL,
            parent_revision_id TEXT NULL,
            created_at_utc TEXT NOT NULL,
            kind TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            UNIQUE(workflow_id, version)
        );

        CREATE INDEX IF NOT EXISTS ix_workflow_revisions_workflow
            ON workflow_revisions(workflow_id, version);
        """;
}
