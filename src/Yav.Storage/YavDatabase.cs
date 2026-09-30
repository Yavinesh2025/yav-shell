using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yav.Storage;

public sealed class DatabaseVersionException(int foundVersion, int supportedVersion)
    : InvalidOperationException(
        $"The YAV database was written by a newer version (schema {foundVersion}; this version supports up to {supportedVersion}). "
        + "It was not opened or changed. Install the newer YAV Shell to use it.")
{
    public int FoundVersion { get; } = foundVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// YAV's durable state in one SQLite file outside every project. Each public method is a single
/// transaction, so a crash leaves either the old state or the new state.
/// </summary>
public sealed partial class YavDatabase : IDisposable
{
    public const int LatestSchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);
    private bool _disposed;

    private YavDatabase(SqliteConnection connection, TimeProvider clock, int schemaVersion)
    {
        _connection = connection;
        _clock = clock;
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }

    /// <summary>Upper bound on stored events per run. The oldest events are removed first.</summary>
    public int MaxEventsPerRun { get; set; } = 5000;

    public static YavDatabase Open(string path, TimeProvider clock)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 15,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA busy_timeout=15000;");
            Execute(connection, "PRAGMA foreign_keys=ON;");
            var version = Migrate(connection, clock);
            return new YavDatabase(connection, clock, version);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                // Fold the write-ahead log back into the main file so a copy of yav.db alone is complete.
                Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException)
            {
            }

            _connection.Dispose();
        }
    }

    /// <summary>A key under which a project is found regardless of letter case or a trailing separator.</summary>
    public static string ProjectKey(string projectPath)
    {
        var full = Path.GetFullPath(projectPath);
        var root = Path.GetPathRoot(full);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return full.ToLowerInvariant();
    }

    private static int Migrate(SqliteConnection connection, TimeProvider clock)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);");

        var current = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            current = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (current > LatestSchemaVersion)
        {
            throw new DatabaseVersionException(current, LatestSchemaVersion);
        }

        foreach (var migration in Migrations.All.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $at);";
                command.Parameters.AddWithValue("$version", migration.Version);
                command.Parameters.AddWithValue("$name", migration.Name);
                command.Parameters.AddWithValue("$at", Stamp(clock.GetUtcNow()));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private int Write(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }

    private void InTransaction(Action<SqliteTransaction> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var transaction = _connection.BeginTransaction();
            work(transaction);
            transaction.Commit();
        }
    }

    private T InTransaction<T>(Func<SqliteTransaction, T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var transaction = _connection.BeginTransaction();
            var result = work(transaction);
            transaction.Commit();
            return result;
        }
    }

    private T Read<T>(Func<T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return work();
        }
    }

    internal static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Moment(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? Text(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string Required(SqliteDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    private static int Integer(SqliteDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    private static long Long(SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    private static int? NullableInteger(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? NullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static bool Flag(SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column)) != 0;

    private static bool? NullableFlag(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    }

    private static decimal? Money(SqliteDataReader reader, string column)
    {
        var text = Text(reader, column);
        return text is null ? null : decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static string? Money(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static TEnum Enumeration<TEnum>(SqliteDataReader reader, string column)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(Required(reader, column));
}
