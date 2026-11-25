using System.Net;
using System.Text;

namespace Deployer;

public static class Pages
{
    private static string H(string s) => WebUtility.HtmlEncode(s);

    private const string Style = """
        <style>
        :root { color-scheme: light dark; --bg:#fff; --fg:#111; --muted:#666; --line:#ddd; --accent:#0b6bcb; }
        @media (prefers-color-scheme: dark) { :root { --bg:#111; --fg:#eee; --muted:#999; --line:#333; --accent:#5aa9ff; } }
        * { box-sizing: border-box; }
        body { margin:0; font: 16px/1.5 system-ui, sans-serif; background: var(--bg); color: var(--fg); }
        main { max-width: 960px; margin: 0 auto; padding: 24px 16px; }
        a { color: var(--accent); }
        table { width:100%; border-collapse: collapse; }
        th, td { text-align:left; padding: 6px 8px; border-bottom: 1px solid var(--line); }
        td.num, th.num { text-align:right; font-variant-numeric: tabular-nums; }
        .muted { color: var(--muted); }
        button { font: inherit; cursor: pointer; }
        code, pre { background: color-mix(in srgb, var(--fg) 8%, transparent); padding: 2px 6px; border-radius: 4px; }
        pre { padding: 12px; overflow-x: auto; }
        </style>
        """;

    private static string Layout(string title, string body) => $"""
        <!doctype html><html lang="cs"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex"><title>{H(title)}</title>{Style}</head>
        <body><main>{body}</main></body></html>
        """;

    public static string QrPage(string url) => $$"""
        <!doctype html><html lang="cs"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex"><title>QR – {{H(url)}}</title>
        <style>
        html,body { margin:0; height:100%; background:#fff; color:#000; font-family: system-ui, sans-serif; }
        body { display:flex; flex-direction:column; align-items:center; justify-content:center; gap:3vh; }
        .qr { width: min(80vw, 75vh); height: min(80vw, 75vh); }
        .qr svg { width:100%; height:100%; display:block; }
        .url { font-size: clamp(18px, 4vw, 48px); font-weight: 600; word-break: break-all; text-align:center; padding: 0 16px; }
        </style></head>
        <body><div class="qr">{{Qr.Svg(url)}}</div><div class="url">{{H(url.TrimEnd('/'))}}</div></body></html>
        """;

    public static string Home(DeployerOptions o) => Layout("DOD hosting", $"""
        <h1>DOD hosting</h1>
        <p>Nahraje složku s webem na <code>https://jmeno.{H(o.SiteDomain)}</code>.</p>
        <h2>macOS / Linux</h2>
        <pre>curl -fsSLO https://deploy.{H(o.SiteDomain)}/deploy.sh
        bash deploy.sh ./muj-web jmeno</pre>
        <h2>Windows (PowerShell)</h2>
        <pre>curl.exe -fsSLO https://deploy.{H(o.SiteDomain)}/deploy.ps1
        powershell -ExecutionPolicy Bypass -File deploy.ps1 .\muj-web jmeno</pre>
        <p>Token akce můžeš dát do souboru <code>.env</code> ve složce s webem: <code>DOD_TOKEN=…</code>
        (na server se nenahrává).</p>
        <p class="muted">Ve složce musí být <code>index.html</code> nebo <code>index.php</code>. Žádné fotky ani příjmení.</p>
        """);

    public static string Admin(IReadOnlyList<SiteInfo> sites, DeployerOptions o)
    {
        var rows = new StringBuilder();
        foreach (var s in sites)
        {
            var url = o.SiteUrl(s.Name);
            rows.Append($"""
                <tr><td><a href="{H(url)}" target="_blank">{H(s.Name)}</a>{(s.HasPhp ? " <span class=muted>php</span>" : "")}</td>
                <td>{s.Updated.ToLocalTime():d.M. HH:mm}</td>
                <td class="num">{s.Files}</td><td class="num">{s.Bytes / 1024.0:0} kB</td><td class="num">{s.Visits}</td>
                <td><a href="/qr/{H(s.Name)}" target="_blank">QR</a></td>
                <td><form method="post" action="/admin/delete/{H(s.Name)}" onsubmit="return confirm('Smazat {H(s.Name)}?')">
                <button>Smazat</button></form></td></tr>
                """);
        }
        return Layout("DOD hosting – admin", $"""
            <h1>Weby ({sites.Count})</h1>
            <p class="muted">Weby se mažou {o.TtlDays} dní po posledním nasazení.</p>
            <div style="overflow-x:auto"><table>
            <tr><th>Jméno</th><th>Nasazeno</th><th class="num">Soubory</th><th class="num">Velikost</th><th class="num">Návštěvy</th><th></th><th></th></tr>
            {rows}
            </table></div>
            <form method="post" action="/admin/delete-all" style="margin-top:24px"
              onsubmit="return prompt('Smaže všechny weby. Napiš SMAZAT:') === 'SMAZAT'">
            <button>Smazat všechny weby</button></form>
            """);
    }
}
