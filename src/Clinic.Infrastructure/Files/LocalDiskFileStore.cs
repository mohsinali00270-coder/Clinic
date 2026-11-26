using System.Security.Cryptography;
using Clinic.Application.Clinical;
using Microsoft.Extensions.Configuration;

namespace Clinic.Infrastructure.Files;

/// <summary>
/// IFileStore on the local disk (on-premise installs and development). Layout:
/// {root}/{tenant}/chunks/{file}/{index}.part and {root}/{tenant}/files/{file}.
/// Azure Blob / S3 implementations plug in behind the same interface.
/// </summary>
public sealed class LocalDiskFileStore(IConfiguration config) : IFileStore
{
    private readonly string _root = Path.GetFullPath(config["Files:Root"] ?? Path.Combine(AppContext.BaseDirectory, "files"));

    private string ChunkDir(Guid tenant, Guid file) => Path.Combine(_root, tenant.ToString("N"), "chunks", file.ToString("N"));

    public async Task WriteChunkAsync(Guid tenantId, Guid fileId, int index, Stream data, CancellationToken ct)
    {
        var dir = ChunkDir(tenantId, fileId);
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"{index}.tmp-{Guid.NewGuid():N}");
        await using (var file = File.Create(temp))
            await data.CopyToAsync(file, ct);
        File.Move(temp, Path.Combine(dir, $"{index}.part"), overwrite: true); // atomic: a retried chunk just replaces it
    }

    public Task<IReadOnlyList<int>> ListChunksAsync(Guid tenantId, Guid fileId, CancellationToken ct)
    {
        var dir = ChunkDir(tenantId, fileId);
        IReadOnlyList<int> result = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.part").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f))).Order().ToList()
            : [];
        return Task.FromResult(result);
    }

    public async Task<(string Key, long Size, string Sha256)> AssembleAsync(Guid tenantId, Guid fileId, int totalChunks, CancellationToken ct)
    {
        var key = Path.Combine(tenantId.ToString("N"), "files", fileId.ToString("N"));
        var target = Path.Combine(_root, key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (var output = File.Create(target))
        {
            for (var i = 0; i < totalChunks; i++)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(ChunkDir(tenantId, fileId), $"{i}.part"), ct);
                sha.AppendData(bytes);
                size += bytes.Length;
                await output.WriteAsync(bytes, ct);
            }
        }
        await DeleteChunksAsync(tenantId, fileId, ct);
        return (key, size, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    public Task DeleteChunksAsync(Guid tenantId, Guid fileId, CancellationToken ct)
    {
        var dir = ChunkDir(tenantId, fileId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Path.GetFullPath(Path.Combine(_root, key));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Task.FromResult<Stream?>(null);
        return Task.FromResult<Stream?>(File.OpenRead(path));
    }
}
