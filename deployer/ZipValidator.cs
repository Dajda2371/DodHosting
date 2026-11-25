using System.IO.Compression;

namespace Deployer;

public sealed record ZipLimits(
    long MaxZipBytes = 10 * 1024 * 1024,
    long MaxUncompressedBytes = 40 * 1024 * 1024,
    int MaxEntries = 500);

public static class ZipValidator
{
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".txt", ".md", ".csv", ".xml",
        ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico",
        ".woff", ".woff2", ".ttf", ".otf", ".mp3", ".wav", ".ogg", ".mp4", ".webm",
        ".php",
    };

    // Junk that editors and OSes add; skipped silently instead of rejecting the upload.
    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.Ordinal) ||
        path.Split('/').Any(p => p.StartsWith('.') || p is "Thumbs.db" or "desktop.ini" or "node_modules");

    public sealed record Entry(ZipArchiveEntry Source, string RelativePath);

    /// <summary>
    /// Validates the archive and returns the entries to extract with their final relative paths
    /// (a single top-level folder is unwrapped). Throws <see cref="DeployException"/> on problems.
    /// </summary>
    public static List<Entry> Validate(ZipArchive zip, ZipLimits limits)
    {
        if (zip.Entries.Count > limits.MaxEntries)
            throw new DeployException($"Příliš mnoho souborů (max {limits.MaxEntries}).");

        var files = new List<(ZipArchiveEntry e, string path)>();
        long total = 0;
        foreach (var e in zip.Entries)
        {
            var path = e.FullName.Replace('\\', '/');
            if (path.EndsWith('/')) continue; // directory entry
            if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or ""))
                throw new DeployException($"Neplatná cesta v zipu: {path}");
            if (IsJunk(path)) continue; // dotfiles (.deploy-secret, .git, .DS_Store), OS junk

            // Unix mode lives in the high 16 bits of ExternalAttributes; 0xA000 = symlink.
            var unixMode = (e.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new DeployException($"Symbolické odkazy nejsou povolené: {path}");

            var ext = Path.GetExtension(path);
            if (!AllowedExtensions.Contains(ext))
                throw new DeployException($"Typ souboru není povolený: {path}");

            total += e.Length;
            if (total > limits.MaxUncompressedBytes)
                throw new DeployException("Rozbalený web je příliš velký.");

            files.Add((e, path));
        }

        if (files.Count == 0)
            throw new DeployException("Zip je prázdný.");

        // Unwrap a single top-level folder ("muj-web/index.html" -> "index.html").
        var prefix = "";
        if (!files.Any(f => !f.path.Contains('/')))
        {
            var tops = files.Select(f => f.path[..f.path.IndexOf('/')]).Distinct().ToList();
            if (tops.Count == 1) prefix = tops[0] + "/";
        }

        var result = files.Select(f => new Entry(f.e, f.path[prefix.Length..])).ToList();

        if (!result.Any(r => r.RelativePath is "index.html" or "index.php" or "index.htm"))
            throw new DeployException("Chybí index.html (nebo index.php) v kořeni složky.");

        if (result.Any(r => r.RelativePath == "data" || r.RelativePath.StartsWith("data/")))
            throw new DeployException("Složka 'data' je rezervovaná pro server, přejmenuj ji.");

        return result;
    }
}

public sealed class DeployException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
