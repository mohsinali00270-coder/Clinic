using System.Text;
using Clinic.Client.Core.Local;

namespace Clinic.Api.Tests;

public sealed class LocalDatabaseTests
{
    /// <summary>FR-OFF-01 / FR-SEC-02: with a key the file is SQLCipher-encrypted, not plain SQLite.</summary>
    [Fact]
    public async Task Database_file_is_encrypted_when_a_key_is_given()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clinic-enc-{Guid.NewGuid():N}.db3");
        var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        var db = await LocalDatabase.OpenAsync(path, key);
        await db.Connection.InsertAsync(new LocalBranchNote { Id = Guid.NewGuid(), Title = "Confidential handover" });
        await db.Connection.ExecuteScalarAsync<string>("PRAGMA wal_checkpoint(TRUNCATE)");
        await db.DisposeAsync();

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(Encoding.ASCII.GetString(bytes, 0, 15) == "SQLite format 3", "file header is plain SQLite");
        Assert.DoesNotContain("Confidential handover", Encoding.UTF8.GetString(bytes));

        // Re-opening with the right key works.
        var reopened = await LocalDatabase.OpenAsync(path, key);
        Assert.Equal(1, await reopened.Connection.Table<LocalBranchNote>().CountAsync());
        await reopened.WipeAsync();
        Assert.False(File.Exists(path));
    }
}
