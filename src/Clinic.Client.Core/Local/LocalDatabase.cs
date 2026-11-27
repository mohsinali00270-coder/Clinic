using Clinic.Client.Core.Sync;
using SQLite;

namespace Clinic.Client.Core.Local;

/// <summary>
/// FR-OFF-01: the device database. SQLite via sqlite-net, encrypted with SQLCipher when a key is given
/// (the app keeps the key in platform secure storage). FR-OFF-03: schema changes are additive only —
/// CreateTable adds missing columns and never drops data.
/// </summary>
public sealed class LocalDatabase : IAsyncDisposable
{
    public const int SchemaVersion = 9;

    private LocalDatabase(string path, SQLiteAsyncConnection connection)
    {
        Path = path;
        Connection = connection;
    }

    public string Path { get; }

    /// <summary>False when this SQLite build has no FTS5; patient search then falls back to LIKE.</summary>
    public bool SearchIndexAvailable { get; private set; }
    public SQLiteAsyncConnection Connection { get; }

    public static async Task<LocalDatabase> OpenAsync(string path, string? encryptionKey)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        var options = new SQLiteConnectionString(
            path,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex,
            storeDateTimeAsTicks: true,
            key: string.IsNullOrEmpty(encryptionKey) ? null : encryptionKey);

        var connection = new SQLiteAsyncConnection(options);

        if (!string.IsNullOrEmpty(encryptionKey))
        {
            // If another package's plain SQLite provider won at load time, the key would be silently ignored
            // and patient data written in clear text. Refuse to run instead.
            var cipher = await connection.ExecuteScalarAsync<string>("PRAGMA cipher_version");
            if (string.IsNullOrEmpty(cipher))
            {
                await connection.CloseAsync();
                throw new InvalidOperationException(
                    "SQLCipher is not the active SQLite provider, so the local database cannot be encrypted. " +
                    "Check that only SQLitePCLRaw.bundle_e_sqlcipher is referenced.");
            }
        }

        var db = new LocalDatabase(path, connection);
        await db.MigrateAsync();
        return db;
    }

    private async Task MigrateAsync()
    {
        await Connection.EnableWriteAheadLoggingAsync();

        foreach (var entry in LocalSyncRegistry.All)
            await Connection.CreateTableAsync(entry.LocalType);

        await Connection.CreateTableAsync<OutboxEntry>();
        await Connection.CreateTableAsync<PendingConflictResolution>();
        await Connection.CreateTableAsync<SyncCheckpoint>();
        await Connection.CreateTableAsync<LeasedRange>();
        await Connection.CreateTableAsync<LocalMeta>();
        await Connection.CreateTableAsync<LocalFormDraft>();
        SearchIndexAvailable = await Clinical.PatientSearchIndex.CreateAsync(Connection);

        await Connection.InsertOrReplaceAsync(new LocalMeta { Key = "schema_version", Value = SchemaVersion.ToString() });
    }

    public async Task<string?> GetMetaAsync(string key) =>
        (await Connection.FindAsync<LocalMeta>(key))?.Value;

    public Task SetMetaAsync(string key, string? value) =>
        Connection.InsertOrReplaceAsync(new LocalMeta { Key = key, Value = value });

    /// <summary>FR-OFF-31 remote wipe: close and delete the database files.</summary>
    public async Task WipeAsync()
    {
        await Connection.CloseAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;
            if (File.Exists(file)) File.Delete(file);
        }
    }

    public async ValueTask DisposeAsync() => await Connection.CloseAsync();
}
