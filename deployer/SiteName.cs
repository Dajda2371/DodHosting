using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Deployer;

public static partial class SiteName
{
    public static readonly HashSet<string> Reserved =
        ["deploy", "www", "admin", "api", "mail", "qr", "static", "test"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,28})[a-z0-9]$")]
    private static partial Regex Valid();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex Invalid();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();

    /// <summary>"Jiří Novák" -> "jiri-novak". Does not validate.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var decomposed = input.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        var s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = Invalid().Replace(s, "-");
        s = Dashes().Replace(s, "-");
        return s.Trim('-');
    }

    /// <summary>Returns the normalized name, or an error message.</summary>
    public static (string? Name, string? Error) Parse(string? input)
    {
        var name = Normalize(input);
        if (name.Length < 3 || name.Length > 30)
            return (null, "Jméno musí mít 3 až 30 znaků (a-z, 0-9, pomlčka).");
        if (!Valid().IsMatch(name))
            return (null, "Jméno smí obsahovat jen a-z, 0-9 a pomlčku.");
        if (Reserved.Contains(name))
            return (null, $"Jméno '{name}' je rezervované, zvol jiné.");
        return (name, null);
    }

    /// <summary>Strict check for names coming from URLs (no normalization).</summary>
    public static bool IsValid(string? name) =>
        name is not null && Valid().IsMatch(name) && !Reserved.Contains(name);
}
