using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Deployer;

public sealed record SiteMeta(string Name, string SecretHash, DateTimeOffset Created, DateTimeOffset Updated, long Visits);

public sealed record SiteInfo(string Name, DateTimeOffset? Created, DateTimeOffset Updated, long Visits, long Bytes, int Files, bool HasPhp);

/// <summary>Owns /sites (served content) and /meta (secrets, counters). Not served by web.</summary>
public sealed class SiteStore(string sitesDir, string metaDir, TimeProvider time)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string SitesDir { get; } = sitesDir;

    private string SitePath(string name) => Path.Combine(SitesDir, name);
    private string MetaPath(string name) => Path.Combine(metaDir, name + ".json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SitesDir);
        Directory.CreateDirectory(metaDir);
    }

    public bool Exists(string name) => Directory.Exists(SitePath(name));

    public SiteMeta? ReadMeta(string name)
    {
        var p = MetaPath(name);
        return File.Exists(p) ? JsonSerializer.Deserialize<SiteMeta>(File.ReadAllText(p)) : null;
    }

    private void WriteMeta(SiteMeta meta)
    {
        var p = MetaPath(meta.Name);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, Json));
        File.Move(tmp, p, overwrite: true);
    }

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool SecretMatches(SiteMeta meta, string? secret) =>
        !string.IsNullOrEmpty(secret) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(HashSecret(secret.Trim())),
            Encoding.ASCII.GetBytes(meta.SecretHash));

    /// <summary>Deploys (or redeploys) a site. Returns the new secret on first deploy, else null.</summary>
    public async Task<string?> DeployAsync(string name, Stream zipStream, string? secret, ZipLimits limits, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var meta = ReadMeta(name);
            string? newSecret = null;
            if (meta is null)
            {
                if (Exists(name))
                    throw new DeployException($"Jméno '{name}' je obsazené, zvol jiné.", 409);
                newSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
                    .Replace('+', '-').Replace('/', '_');
            }
            else if (!SecretMatches(meta, secret))
            {
                throw new DeployException(
                    $"Jméno '{name}' už patří jinému webu. Zvol jiné jméno (nebo nasazuj ze složky, kde je soubor .deploy-secret).", 403);
            }

            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = ZipValidator.Validate(zip, limits);

            var staging = Path.Combine(SitesDir, $".tmp-{name}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var entry in entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(staging, entry.RelativePath));
                    if (!dest.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new DeployException($"Neplatná cesta v zipu: {entry.RelativePath}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await using var src = entry.Source.Open();
                    await using var dst = File.Create(dest);
                    // Copy at most the declared size + 1 so a lying header can't blow up the disk.
                    await CopyLimitedAsync(src, dst, entry.Source.Length, ct);
                }

                var current = SitePath(name);
                var dataDir = Path.Combine(staging, "data");
                var oldData = Path.Combine(current, "data");
                if (Directory.Exists(oldData)) Directory.Move(oldData, dataDir); // keep counters etc.
                else Directory.CreateDirectory(dataDir);
                MakeWritableForPhp(dataDir);

                if (Directory.Exists(current))
                {
                    var trash = Path.Combine(SitesDir, $".old-{name}-{Guid.NewGuid():N}");
                    Directory.Move(current, trash);
                    Directory.Move(staging, current);
                    TryDelete(trash);
                }
                else
                {
                    Directory.Move(staging, current);
                }
            }
            catch
            {
                TryDelete(staging);
                throw;
            }

            var now = time.GetUtcNow();
            WriteMeta(meta is null
                ? new SiteMeta(name, HashSecret(newSecret!), now, now, 0)
                : meta with { Updated = now });
            return newSecret;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task CopyLimitedAsync(Stream src, Stream dst, long declared, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            written += read;
            if (written > declared)
                throw new DeployException("Poškozený zip (velikost souboru nesedí).");
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static void MakeWritableForPhp(string dir)
    {
        // PHP runs as www-data; everything else stays root-owned and read-only for it.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111 | UnixFileMode.StickyBit);
    }

    public async Task<long?> IncrementCounterAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var meta = ReadMeta(name);
            if (meta is null) return null;
            meta = meta with { Visits = meta.Visits + 1 };
            WriteMeta(meta);
            return meta.Visits;
        }
        finally
        {
            _lock.Release();
        }
    }

    public IReadOnlyList<SiteInfo> List()
    {
        if (!Directory.Exists(SitesDir)) return [];
        var list = new List<SiteInfo>();
        foreach (var dir in new DirectoryInfo(SitesDir).EnumerateDirectories())
        {
            if (dir.Name.StartsWith('.')) continue;
            var files = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            var meta = ReadMeta(dir.Name);
            list.Add(new SiteInfo(
                dir.Name,
                meta?.Created,
                meta?.Updated ?? dir.LastWriteTimeUtc,
                meta?.Visits ?? 0,
                files.Sum(f => f.Length),
                files.Count,
                files.Any(f => f.Extension.Equals(".php", StringComparison.OrdinalIgnoreCase))));
        }
        return list.OrderByDescending(s => s.Updated).ToList();
    }

    public async Task<bool> DeleteAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var existed = Exists(name);
            if (existed)
            {
                var trash = Path.Combine(SitesDir, $".old-{name}-{Guid.NewGuid():N}");
                Directory.Move(SitePath(name), trash);
                TryDelete(trash);
            }
            if (File.Exists(MetaPath(name))) File.Delete(MetaPath(name));
            return existed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes sites not redeployed within <paramref name="ttl"/> and leftover temp folders.</summary>
    public async Task<int> CleanupAsync(TimeSpan ttl)
    {
        var cutoff = time.GetUtcNow() - ttl;
        var removed = 0;
        foreach (var site in List())
        {
            if (site.Created is not null && site.Updated < cutoff && await DeleteAsync(site.Name))
                removed++;
        }
        foreach (var dir in new DirectoryInfo(SitesDir).EnumerateDirectories(".*"))
            if (dir.LastWriteTimeUtc < time.GetUtcNow().UtcDateTime.AddHours(-1))
                TryDelete(dir.FullName);
        return removed;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
