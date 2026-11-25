namespace Deployer;

public sealed record DeployerOptions(
    string EventToken,
    string AdminPassword,
    string SiteDomain,
    string SiteScheme,
    int TtlDays,
    string SitesDir,
    string MetaDir,
    string ScriptsDir)
{
    public string SiteUrl(string name) => $"{SiteScheme}://{name}.{SiteDomain}/";

    public static DeployerOptions FromEnvironment(IConfiguration c) => new(
        Required(c, "EVENT_TOKEN"),
        Required(c, "ADMIN_PASSWORD"),
        c["SITE_DOMAIN"] ?? "ai-dod-spse.davidbenes.cz",
        c["SITE_SCHEME"] ?? "https",
        int.TryParse(c["SITE_TTL_DAYS"], out var d) && d > 0 ? d : 14,
        c["SITES_DIR"] ?? "/sites",
        c["META_DIR"] ?? "/meta",
        c["SCRIPTS_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "scripts"));

    private static string Required(IConfiguration c, string key) =>
        c[key] is { Length: >= 8 } v ? v : throw new InvalidOperationException($"{key} must be set (at least 8 characters).");
}
