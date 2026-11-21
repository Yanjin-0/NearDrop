using System.Security.Cryptography;
using System.Text.Json;

namespace Relay.Core;

public sealed record ManifestEntry(string Path, long Length, string Sha256, bool Directory);
public sealed record SnapshotManifest(int Version, string TransferId, List<ManifestEntry> Entries);
public sealed record SnapshotProgress(string FileName, int FilesCopied, long BytesCopied, long FileBytesCopied, long FileLength);

public static class Snapshots
{
    public const string ManifestName = "neardrop-manifest.json";
    public const int MaxEntries = 100000;
    public const int MaxManifestBytes = 24 * 1024 * 1024;
    public static async Task<long> CreateAsync(string[] sources, string destination, string transferId, CancellationToken ct = default,
        IProgress<SnapshotProgress>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        var payload = Path.Combine(destination, "Files"); Directory.CreateDirectory(payload);
        var entries = new List<ManifestEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        int files = 0;
        var progressClock = progress == null ? null : System.Diagnostics.Stopwatch.StartNew();
        void Report(string file, long copied, long length)
        {
            if (progressClock == null || (files != 0 || copied != 0) && progressClock.ElapsedMilliseconds < 200) return;
            progress!.Report(new(file, files, bytes + copied, copied, length));
            progressClock.Restart();
        }
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SafePaths.NoLinks(source);
            if (SafePaths.Overlap(source, destination)) throw new IOException("傳送來源不能包含暫存目錄。");
            var top = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (!names.Add(top)) throw new IOException("所選項目含相同名稱，請分開傳送。");
            var items = Directory.Exists(source) ? new[] { source }.Concat(SafePaths.Walk(source)) : new[] { source };
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries) throw new IOException("單次傳送最多 100,000 個檔案與目錄，請分批傳送。");
                var relative = item == source ? top : Path.Combine(top, Path.GetRelativePath(source, item));
                var target = SafePaths.Relative(payload, relative);
                SafePaths.NoLinks(item);
                if (Directory.Exists(item))
                {
                    Directory.CreateDirectory(target); entries.Add(new(relative, 0, "", true)); continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Deny concurrent writes/deletes during the copy; never share the original source.
                await using var input = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024]; long size = 0; int read;
                var fileLength = input.Length;
                Report(relative, size, fileLength);
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); hasher.AppendData(buffer, 0, read); size += read;
                    Report(relative, size, fileLength);
                }
                await output.FlushAsync(ct); output.Flush(true);
                entries.Add(new(relative, size, Convert.ToHexString(hasher.GetHashAndReset()), false)); bytes += size; files++;
            }
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(new("", files, bytes, 0, 0));
        var json = JsonSerializer.SerializeToUtf8Bytes(new SnapshotManifest(1, transferId, entries));
        if (json.Length > MaxManifestBytes) throw new IOException("傳送清單過大，請分批傳送。");
        await File.WriteAllBytesAsync(Path.Combine(destination, ManifestName), json, ct);
        return bytes;
    }

    // Caller MUST stop the engine first; hashes alone cannot prevent concurrent remote mutation.
    public static async Task<SnapshotManifest> VerifyAsync(string root, string id, CancellationToken ct = default)
    {
        SafePaths.NoLinks(root);
        var manifestPath = SafePaths.Relative(root, ManifestName);
        if (new FileInfo(manifestPath).Length > MaxManifestBytes) throw new IOException("收件清單超過安全上限。");
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(await File.ReadAllTextAsync(manifestPath, ct)) ?? throw new IOException("收件清單損壞。");
        if (manifest.Version != 1 || manifest.TransferId != id || manifest.Entries == null || manifest.Entries.Count > MaxEntries)
            throw new IOException("收件清單不相符。");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payload = SafePaths.Relative(root, "Files");
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            SafePaths.NoLinks(item);
            if (Path.GetFileName(item) is not ("Files" or ManifestName or ".stfolder"))
                throw new IOException("收件根目錄包含清單以外的內容。");
            if (Path.GetFileName(item) == ".stfolder" && Directory.EnumerateFileSystemEntries(item).Any())
                throw new IOException("收件標記目錄包含未預期內容。");
        }
        foreach (var entry in manifest.Entries)
        {
            var path = SafePaths.Relative(payload, entry.Path);
            if (!expected.Add(path)) throw new IOException("收件清單出現重複路徑。");
            if (entry.Directory) { if (!Directory.Exists(path)) throw new IOException("缺少目錄。"); continue; }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != entry.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != entry.Sha256)
                throw new IOException("檔案完整性驗證失敗：" + entry.Path);
        }
        foreach (var item in SafePaths.Walk(payload))
            if (!expected.Contains(item)) throw new IOException("收到不在清單內的內容，已停止保存。");
        // No incoming metadata or executable is opened; add Windows Internet Zone marking.
        foreach (var entry in manifest.Entries.Where(e => !e.Directory))
        {
            var path = SafePaths.Relative(payload, entry.Path);
            var attributes = File.GetAttributes(path);
            try
            {
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", ct);
            }
            finally { if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes); }
        }
        return manifest;
    }
}
