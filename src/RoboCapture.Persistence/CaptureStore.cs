using Microsoft.Data.Sqlite;
using RoboCapture.Core;

namespace RoboCapture.Persistence;

public sealed record IncompleteSession(string SessionId, string SubjectId, DateTimeOffset StartedUtc, int CaptureCount);

public sealed class CaptureStore(string databasePath) : ICaptureRecorder
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Sessions (Id TEXT PRIMARY KEY, SubjectId TEXT NOT NULL, StartedUtc TEXT NOT NULL, CompletedUtc TEXT NULL);
            CREATE TABLE IF NOT EXISTS Captures (Id INTEGER PRIMARY KEY AUTOINCREMENT, SessionId TEXT NOT NULL, SubjectId TEXT NOT NULL, PoseId TEXT NOT NULL, ShotNumber INTEGER NOT NULL, State TEXT NOT NULL, CameraFileName TEXT NULL, LocalPath TEXT NULL, Error TEXT NULL, TimestampUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ErrorLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, TimestampUtc TEXT NOT NULL, Operation TEXT NOT NULL, Error TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Events (Id TEXT PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Subjects (Id TEXT PRIMARY KEY, EventId TEXT NULL, StudentId TEXT NULL, FirstName TEXT NULL, LastName TEXT NULL, CustomFieldsJson TEXT NULL);
            CREATE TABLE IF NOT EXISTS Stations (Id TEXT PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Cameras (Id TEXT PRIMARY KEY, Manufacturer TEXT NOT NULL, Model TEXT NOT NULL, SerialNumber TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS PosePrograms (Id TEXT PRIMARY KEY, Name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Poses (Id TEXT PRIMARY KEY, PoseProgramId TEXT NOT NULL, Name TEXT NOT NULL, Instruction TEXT NOT NULL, ShotCount INTEGER NOT NULL, ShotIntervalMs INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS FileRecords (Id INTEGER PRIMARY KEY AUTOINCREMENT, CaptureId INTEGER NOT NULL, Path TEXT NOT NULL, VerifiedUtc TEXT NULL);
            CREATE TABLE IF NOT EXISTS AuditLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, TimestampUtc TEXT NOT NULL, Operation TEXT NOT NULL, Details TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task StartSessionAsync(string sessionId, string subjectId, CancellationToken ct = default) =>
        await ExecuteAsync("INSERT INTO Sessions (Id, SubjectId, StartedUtc) VALUES ($id, $subject, $started)", ct,
            ("$id", sessionId), ("$subject", subjectId), ("$started", DateTimeOffset.UtcNow.ToString("O")));

    public async Task RecordCaptureAsync(string sessionId, CaptureRequest request, CaptureResult result, CancellationToken ct = default) =>
        await ExecuteAsync("INSERT INTO Captures (SessionId, SubjectId, PoseId, ShotNumber, State, CameraFileName, LocalPath, Error, TimestampUtc) VALUES ($session, $subject, $pose, $shot, $state, $camera, $path, $error, $timestamp)", ct,
            ("$session", sessionId), ("$subject", request.SubjectId), ("$pose", request.PoseId), ("$shot", request.ShotNumber),
            ("$state", result.State.ToString()), ("$camera", result.CameraFileName), ("$path", result.LocalPath),
            ("$error", result.Error), ("$timestamp", result.Timestamp.ToString("O")));

    public async Task CompleteSessionAsync(string sessionId, CancellationToken ct = default) =>
        await ExecuteAsync("UPDATE Sessions SET CompletedUtc = $completed WHERE Id = $id", ct,
            ("$id", sessionId), ("$completed", DateTimeOffset.UtcNow.ToString("O")));

    public async Task<IReadOnlyList<string>> GetIncompleteSessionIdsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Sessions WHERE CompletedUtc IS NULL ORDER BY StartedUtc";
        var sessions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sessions.Add(reader.GetString(0));
        return sessions;
    }

    public async Task<int> GetCaptureCountAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Captures WHERE SessionId = $session";
        command.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<string>> GetCaptureStatesAsync(string sessionId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM Captures WHERE SessionId = $session ORDER BY Id";
        command.Parameters.AddWithValue("$session", sessionId);
        var states = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) states.Add(reader.GetString(0));
        return states;
    }

    public async Task<IReadOnlyList<IncompleteSession>> GetIncompleteSessionsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.SubjectId, s.StartedUtc, (SELECT COUNT(*) FROM Captures c WHERE c.SessionId = s.Id)
            FROM Sessions s WHERE s.CompletedUtc IS NULL ORDER BY s.StartedUtc
            """;
        var sessions = new List<IncompleteSession>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sessions.Add(new IncompleteSession(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)), reader.GetInt32(3)));
        return sessions;
    }

    public async Task RecordCameraEventAsync(CameraEvent cameraEvent, string? sessionId = null, string? subjectId = null, CancellationToken ct = default)
    {
        await ExecuteAsync("INSERT INTO AuditLog (TimestampUtc, Operation, Details) VALUES ($timestamp, $operation, $details)", ct,
            ("$timestamp", cameraEvent.Timestamp.ToString("O")),
            ("$operation", cameraEvent.Operation),
            ("$details", $"driver={cameraEvent.DriverId};state={cameraEvent.State};result={cameraEvent.Result};duration={cameraEvent.Duration};error={cameraEvent.Error};session={sessionId};subject={subjectId}"));
    }

    public async Task<int> GetAuditEventCountAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM AuditLog";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}