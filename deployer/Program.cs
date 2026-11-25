using System.Security.Cryptography;
using System.Text;
using Deployer;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

var options = DeployerOptions.FromEnvironment(builder.Configuration);
var limits = new ZipLimits();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = limits.MaxZipBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = limits.MaxZipBytes);
builder.Services.ConfigureHttpJsonOptions(j =>
    j.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping); // Czech text stays readable
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SiteStore(options.SitesDir, options.MetaDir, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<CleanupService>();

var app = builder.Build();
app.Services.GetRequiredService<SiteStore>().EnsureDirectories();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    await next();
});

static bool SameSecret(string? given, string expected) =>
    given is not null && CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

app.MapGet("/", () => Results.Content(Pages.Home(options), "text/html; charset=utf-8"));
app.MapGet("/health", () => Results.Ok("ok"));

// Deploy scripts, so workshop PCs can fetch them with one curl.
foreach (var script in new[] { "deploy.sh", "deploy.ps1" })
    app.MapGet("/" + script, () =>
    {
        var path = Path.Combine(options.ScriptsDir, script);
        return File.Exists(path)
            ? Results.File(path, "text/plain; charset=utf-8", script)
            : Results.NotFound();
    });

app.MapPost("/deploy", async (HttpRequest req, SiteStore store, ILogger<Program> log, CancellationToken ct) =>
{
    if (!SameSecret(req.Headers["X-Event-Token"], options.EventToken))
        return Error("Neplatný token akce (X-Event-Token).", 401);
    if (!req.HasFormContentType)
        return Error("Očekávám multipart/form-data s poli 'name' a 'zip'.", 400);

    IFormCollection form;
    try { form = await req.ReadFormAsync(ct); }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
    {
        return Error($"Zip je příliš velký (max {limits.MaxZipBytes / 1024 / 1024} MB).", 413);
    }

    var (name, nameError) = SiteName.Parse(form["name"]);
    if (name is null) return Error(nameError!, 400);
    var file = form.Files["zip"];
    if (file is null || file.Length == 0) return Error("Chybí soubor 'zip'.", 400);
    if (file.Length > limits.MaxZipBytes)
        return Error($"Zip je příliš velký (max {limits.MaxZipBytes / 1024 / 1024} MB).", 413);

    string? secret;
    try
    {
        await using var buffered = new MemoryStream();
        await file.CopyToAsync(buffered, ct);
        buffered.Position = 0;
        secret = await store.DeployAsync(name, buffered, req.Headers["X-Site-Secret"], limits, ct);
    }
    catch (DeployException ex)
    {
        return Error(ex.Message, ex.Status);
    }
    catch (InvalidDataException)
    {
        return Error("Soubor není platný zip.", 400);
    }

    var url = options.SiteUrl(name);
    var qrUrl = $"{req.Scheme}://{req.Host}/qr/{name}";
    log.LogInformation("Deployed {Name} ({Bytes} bytes, new={New})", name, file.Length, secret is not null);
    return Results.Json(new { name, url, qrUrl, secret, qrAscii = Qr.Ascii(url) });
});

app.MapGet("/qr/{name}", (string name, SiteStore store) =>
    SiteName.IsValid(name) && store.Exists(name)
        ? Results.Content(Pages.QrPage(options.SiteUrl(name)), "text/html; charset=utf-8")
        : Results.NotFound("Web neexistuje."));

app.MapGet("/qr/{name}/text", (string name, SiteStore store) =>
    SiteName.IsValid(name) && store.Exists(name)
        ? Results.Text(Qr.Ascii(options.SiteUrl(name)), "text/plain; charset=utf-8")
        : Results.NotFound());

// Visit counter for static pages: fetch("https://deploy.../api/counter/jiri").then(r => r.json())
app.MapGet("/api/counter/{name}", async (string name, HttpResponse res, SiteStore store) =>
{
    res.Headers.AccessControlAllowOrigin = "*";
    res.Headers.CacheControl = "no-store";
    if (!SiteName.IsValid(name)) return Results.NotFound();
    var count = await store.IncrementCounterAsync(name);
    return count is null ? Results.NotFound() : Results.Json(new { name, count });
});

// --- admin (HTTP basic auth, any username) ---
var admin = app.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;
    var header = http.Request.Headers.Authorization.ToString();
    string? password = null;
    if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            password = decoded[(decoded.IndexOf(':') + 1)..];
        }
        catch (FormatException) { }
    }
    if (!SameSecret(password, options.AdminPassword))
    {
        http.Response.Headers.WWWAuthenticate = "Basic realm=\"DOD admin\", charset=\"UTF-8\"";
        return Results.Unauthorized();
    }
    // Basic auth is sent automatically by the browser, so block cross-site form posts (CSRF).
    if (HttpMethods.IsPost(http.Request.Method) &&
        http.Request.Headers["Sec-Fetch-Site"] is { Count: > 0 } site && site != "same-origin" && site != "none")
        return Results.StatusCode(403);
    return await next(ctx);
});

admin.MapGet("", (SiteStore store) => Results.Content(Pages.Admin(store.List(), options), "text/html; charset=utf-8"));

admin.MapPost("/delete/{name}", async (string name, SiteStore store) =>
{
    if (SiteName.IsValid(name)) await store.DeleteAsync(name);
    return Results.Redirect("/admin");
}).DisableAntiforgery();

admin.MapPost("/delete-all", async (SiteStore store) =>
{
    foreach (var site in store.List()) await store.DeleteAsync(site.Name);
    return Results.Redirect("/admin");
}).DisableAntiforgery();

app.Run();

public partial class Program;
