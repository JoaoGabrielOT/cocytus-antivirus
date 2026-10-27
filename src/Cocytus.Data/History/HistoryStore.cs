using System.Globalization;
using System.Text.Json;
using Cocytus.Core;
using Microsoft.Data.Sqlite;

namespace Cocytus.Data.History;

public sealed record ScanRecord(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    ScanStatus Status,
    string? Error,
    IReadOnlyList<string> Targets,
    int NoThreat,
    int Threats,
    int Suspicious,
    int NotScanned,
    int ReadErrors,
    long BytesScanned,
    string? EngineName,
    string? EngineVersion,
    string? DatabaseVersion,
    DateTimeOffset? DatabaseDate,
    string? Limitations,
    bool ItemsTruncated);

public sealed record HistoryEvent(long Id, DateTimeOffset At, string Kind, string Message, string? Path);

/// <summary>Histórico local (SQLite): varreduras, itens não limpos e eventos (quarentena, atualização, erros).</summary>
public sealed class HistoryStore
{
    public const string EventQuarantine = "Quarentena";
    public const string EventRestore = "Restauração";
    public const string EventDelete = "Exclusão";
    public const string EventUpdate = "Atualização";
    public const string EventError = "Erro";
    public const string EventSettings = "Configurações";
    public const string EventRealTime = "Tempo real";
    public const string EventOptimization = "Otimização";
    public const string EventUninstall = "Desinstalação";

    private readonly string _connectionString;
    private readonly Lock _lock = new();

    public HistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS scans (
                    id TEXT PRIMARY KEY,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error TEXT,
                    targets TEXT NOT NULL,
                    no_threat INTEGER NOT NULL,
                    threats INTEGER NOT NULL,
                    suspicious INTEGER NOT NULL,
                    not_scanned INTEGER NOT NULL,
                    read_errors INTEGER NOT NULL,
                    bytes_scanned INTEGER NOT NULL,
                    engine_name TEXT,
                    engine_version TEXT,
                    database_version TEXT,
                    database_date TEXT,
                    limitations TEXT,
                    items_truncated INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS scan_items (
                    scan_id TEXT NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
                    path TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    threat_name TEXT,
                    detail TEXT,
                    size INTEGER,
                    sha256 TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_scan_items_scan ON scan_items(scan_id);
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    at TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    message TEXT NOT NULL,
                    path TEXT
                );
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }
    }

    public void SaveScan(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_lock)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO scans (id, started_at, finished_at, status, error, targets, no_threat, threats, suspicious,
                        not_scanned, read_errors, bytes_scanned, engine_name, engine_version, database_version, database_date,
                        limitations, items_truncated)
                    VALUES ($id, $started, $finished, $status, $error, $targets, $noThreat, $threats, $suspicious,
                        $notScanned, $readErrors, $bytes, $engineName, $engineVersion, $dbVersion, $dbDate, $limitations, $truncated);
                    """;
                command.Parameters.AddWithValue("$id", report.Id.ToString("N"));
                command.Parameters.AddWithValue("$started", report.StartedAt.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$finished", report.FinishedAt.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$status", report.Status.ToString());
                command.Parameters.AddWithValue("$error", (object?)report.Error ?? DBNull.Value);
                command.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(report.Targets));
                command.Parameters.AddWithValue("$noThreat", report.NoThreat);
                command.Parameters.AddWithValue("$threats", report.Threats);
                command.Parameters.AddWithValue("$suspicious", report.Suspicious);
                command.Parameters.AddWithValue("$notScanned", report.NotScanned);
                command.Parameters.AddWithValue("$readErrors", report.ReadErrors);
                command.Parameters.AddWithValue("$bytes", report.BytesScanned);
                command.Parameters.AddWithValue("$engineName", (object?)report.Engine?.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("$engineVersion", (object?)report.Engine?.Version ?? DBNull.Value);
                command.Parameters.AddWithValue("$dbVersion", (object?)report.Engine?.DatabaseVersion ?? DBNull.Value);
                command.Parameters.AddWithValue("$dbDate", (object?)report.Engine?.DatabaseDate?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);
                command.Parameters.AddWithValue("$limitations", (object?)report.Engine?.Limitations ?? DBNull.Value);
                command.Parameters.AddWithValue("$truncated", report.ItemsTruncated ? 1 : 0);
                command.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO scan_items (scan_id, path, outcome, reason, threat_name, detail, size, sha256)
                    VALUES ($scan, $path, $outcome, $reason, $threat, $detail, $size, $sha);
                    """;
                var pScan = insert.Parameters.Add("$scan", SqliteType.Text);
                var pPath = insert.Parameters.Add("$path", SqliteType.Text);
                var pOutcome = insert.Parameters.Add("$outcome", SqliteType.Text);
                var pReason = insert.Parameters.Add("$reason", SqliteType.Text);
                var pThreat = insert.Parameters.Add("$threat", SqliteType.Text);
                var pDetail = insert.Parameters.Add("$detail", SqliteType.Text);
                var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
                var pSha = insert.Parameters.Add("$sha", SqliteType.Text);
                foreach (var item in report.Items)
                {
                    pScan.Value = report.Id.ToString("N");
                    pPath.Value = item.Path;
                    pOutcome.Value = item.Outcome.ToString();
                    pReason.Value = item.Reason.ToString();
                    pThreat.Value = (object?)item.ThreatName ?? DBNull.Value;
                    pDetail.Value = (object?)item.Detail ?? DBNull.Value;
                    pSize.Value = (object?)item.Size ?? DBNull.Value;
                    pSha.Value = (object?)item.Sha256 ?? DBNull.Value;
                    insert.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<ScanRecord> ListScans(int limit = 200)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, started_at, finished_at, status, error, targets, no_threat, threats, suspicious, not_scanned,
                    read_errors, bytes_scanned, engine_name, engine_version, database_version, database_date, limitations, items_truncated
                FROM scans ORDER BY started_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<ScanRecord>();
            while (reader.Read())
            {
                list.Add(new ScanRecord(
                    reader.GetString(0),
                    ParseDate(reader.GetString(1)),
                    ParseDate(reader.GetString(2)),
                    Enum.TryParse<ScanStatus>(reader.GetString(3), out var status) ? status : ScanStatus.Failed,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [],
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : ParseDate(reader.GetString(15)),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.GetInt32(17) != 0));
            }

            return list;
        }
    }

    public IReadOnlyList<FileScanResult> GetScanItems(string scanId)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT path, outcome, reason, threat_name, detail, size, sha256
                FROM scan_items WHERE scan_id = $scan ORDER BY rowid;
                """;
            command.Parameters.AddWithValue("$scan", scanId);
            using var reader = command.ExecuteReader();
            var list = new List<FileScanResult>();
            while (reader.Read())
            {
                list.Add(new FileScanResult(
                    reader.GetString(0),
                    Enum.TryParse<ScanOutcome>(reader.GetString(1), out var outcome) ? outcome : ScanOutcome.ReadError,
                    Enum.TryParse<NotScannedReason>(reader.GetString(2), out var reason) ? reason : NotScannedReason.None,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return list;
        }
    }

    public void AddEvent(string kind, string message, string? path = null)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO events (at, kind, message, path) VALUES ($at, $kind, $message, $path);";
            command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HistoryEvent> ListEvents(int limit = 500)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, at, kind, message, path FROM events ORDER BY id DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<HistoryEvent>();
            while (reader.Read())
            {
                list.Add(new HistoryEvent(reader.GetInt64(0), ParseDate(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return list;
        }
    }

    /// <summary>Apaga varreduras e eventos (a quarentena não é afetada).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM scan_items; DELETE FROM scans; DELETE FROM events;";
            command.ExecuteNonQuery();
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
