using System.IO.Compression;
using System.Text;
using Deployer;

public class ZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dod-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SiteStore _store;

    public ZipTests()
    {
        _store = new SiteStore(Path.Combine(_root, "sites"), Path.Combine(_root, "meta"), TimeProvider.System);
        _store.EnsureDirectories();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static MemoryStream MakeZip(params (string path, string content)[] files) =>
        MakeZip(files.Select(f => (f.path, f.content, 0)).ToArray());

    internal static MemoryStream MakeZip(params (string path, string content, int unixMode)[] files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content, mode) in files)
            {
                var e = zip.CreateEntry(path);
                if (mode != 0) e.ExternalAttributes = mode << 16;
                using var w = new StreamWriter(e.Open(), Encoding.UTF8);
                w.Write(content);
            }
        ms.Position = 0;
        return ms;
    }

    private static List<ZipValidator.Entry> Validate(MemoryStream ms, ZipLimits? limits = null) =>
        ZipValidator.Validate(new ZipArchive(ms), limits ?? new ZipLimits());

    [Fact]
    public void AcceptsSimpleSite()
    {
        var entries = Validate(MakeZip(("index.html", "<h1>hi</h1>"), ("css/style.css", "body{}")));
        Assert.Equal(["index.html", "css/style.css"], entries.Select(e => e.RelativePath));
    }

    [Fact]
    public void UnwrapsSingleTopFolder()
    {
        var entries = Validate(MakeZip(("muj-web/index.php", "<?php echo 1;"), ("muj-web/a.js", "")));
        Assert.Contains(entries, e => e.RelativePath == "index.php");
    }

    [Fact]
    public void SkipsDotfilesAndMacJunk()
    {
        var entries = Validate(MakeZip(("index.html", ""), (".deploy-secret", "x"), ("__MACOSX/._index.html", ""), (".DS_Store", "")));
        Assert.Single(entries);
    }

    [Theory]
    [InlineData("../evil.html")]
    [InlineData("a/../../evil.html")]
    [InlineData("/etc/evil.html")]
    [InlineData("C:/evil.html")]
    public void RejectsTraversal(string path) =>
        Assert.Throws<DeployException>(() => Validate(MakeZip(("index.html", ""), (path, ""))));

    [Fact]
    public void RejectsSymlink() =>
        Assert.Throws<DeployException>(() => Validate(MakeZip(("index.html", "", 0), ("link.html", "/etc/passwd", 0xA1FF))));

    [Fact]
    public void RejectsDisallowedExtension() =>
        Assert.Throws<DeployException>(() => Validate(MakeZip(("index.html", ""), ("run.sh", "rm -rf /"))));

    [Fact]
    public void RejectsHtaccessLikeAndUserIniViaExtensionOrDotfile()
    {
        // .user.ini would let PHP-FPM override open_basedir; dotfiles are dropped, never extracted.
        var entries = Validate(MakeZip(("index.php", ""), (".user.ini", "open_basedir=/")));
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("user.ini"));
    }

    [Fact]
    public void RequiresIndex() =>
        Assert.Throws<DeployException>(() => Validate(MakeZip(("about.html", ""))));

    [Fact]
    public void RejectsReservedDataFolder() =>
        Assert.Throws<DeployException>(() => Validate(MakeZip(("index.html", ""), ("data/x.json", "{}"))));

    [Fact]
    public void RejectsTooManyFiles() =>
        Assert.Throws<DeployException>(() => Validate(
            MakeZip(Enumerable.Range(0, 5).Select(i => ($"f{i}.html", "")).Prepend(("index.html", "")).ToArray()),
            new ZipLimits(MaxEntries: 3)));

    [Fact]
    public void RejectsTooLargeUncompressed() =>
        Assert.Throws<DeployException>(() => Validate(
            MakeZip(("index.html", new string('a', 5000))), new ZipLimits(MaxUncompressedBytes: 1000)));

    [Fact]
    public async Task FirstDeployReturnsSecretAndRedeployNeedsIt()
    {
        var secret = await _store.DeployAsync("jiri", MakeZip(("index.html", "v1")), null, new(), default);
        Assert.NotNull(secret);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(_store.SitesDir, "jiri", "index.html")));
        Assert.True(Directory.Exists(Path.Combine(_store.SitesDir, "jiri", "data")));

        var ex = await Assert.ThrowsAsync<DeployException>(() =>
            _store.DeployAsync("jiri", MakeZip(("index.html", "evil")), null, new(), default));
        Assert.Equal(403, ex.Status);
        await Assert.ThrowsAsync<DeployException>(() =>
            _store.DeployAsync("jiri", MakeZip(("index.html", "evil")), "wrong", new(), default));

        File.WriteAllText(Path.Combine(_store.SitesDir, "jiri", "data", "count.txt"), "5");
        Assert.Null(await _store.DeployAsync("jiri", MakeZip(("index.html", "v2")), secret, new(), default));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(_store.SitesDir, "jiri", "index.html")));
        Assert.Equal("5", File.ReadAllText(Path.Combine(_store.SitesDir, "jiri", "data", "count.txt")));
        Assert.DoesNotContain(Directory.GetDirectories(_store.SitesDir), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public async Task FailedDeployLeavesOldVersion()
    {
        var secret = await _store.DeployAsync("anna", MakeZip(("index.html", "ok")), null, new(), default);
        await Assert.ThrowsAsync<DeployException>(() =>
            _store.DeployAsync("anna", MakeZip(("nope.html", "")), secret, new(), default));
        Assert.Equal("ok", File.ReadAllText(Path.Combine(_store.SitesDir, "anna", "index.html")));
    }

    [Fact]
    public async Task HandMadeFolderCannotBeTaken()
    {
        Directory.CreateDirectory(Path.Combine(_store.SitesDir, "stul1"));
        var ex = await Assert.ThrowsAsync<DeployException>(() =>
            _store.DeployAsync("stul1", MakeZip(("index.html", "")), null, new(), default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task CounterAndCleanup()
    {
        await _store.DeployAsync("bob-1", MakeZip(("index.html", "")), null, new(), default);
        Assert.Equal(1, await _store.IncrementCounterAsync("bob-1"));
        Assert.Equal(2, await _store.IncrementCounterAsync("bob-1"));
        Assert.Null(await _store.IncrementCounterAsync("nobody"));

        Assert.Equal(0, await _store.CleanupAsync(TimeSpan.FromDays(1)));
        Assert.Equal(1, await _store.CleanupAsync(TimeSpan.FromSeconds(-1)));
        Assert.False(_store.Exists("bob-1"));
    }
}
