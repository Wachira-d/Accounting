using System.Text.RegularExpressions;

namespace Accounting.Helpers;

public static partial class CmsSecurityHelper
{
    private static readonly HashSet<string> DangerousTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "object", "embed", "applet", "form", "input",
        "meta", "link", "base", "svg"
    };

    private static readonly HashSet<string> DangerousAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "onload", "onerror", "onclick", "onmouseover", "onfocus", "onblur",
        "onsubmit", "onchange", "onkeydown", "onkeyup", "onkeypress",
        "onmousedown", "onmouseup", "onmousemove", "oncontextmenu"
    };

    public static string SanitizeCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return "";

        var sanitized = css;

        sanitized = ExpressionRegex().Replace(sanitized, "/* blocked */");
        sanitized = ImportRegex().Replace(sanitized, "/* blocked */");
        sanitized = UrlJavascriptRegex().Replace(sanitized, "url(/* blocked */)");
        sanitized = BehaviorRegex().Replace(sanitized, "/* blocked */");
        sanitized = BindingRegex().Replace(sanitized, "/* blocked */");

        return sanitized;
    }

    public static string SanitizeHtmlBlock(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var sanitized = html;

        foreach (var tag in DangerousTags)
        {
            sanitized = Regex.Replace(sanitized, $@"<{tag}[^>]*>[\s\S]*?</{tag}>", "<!-- blocked -->", RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, $@"<{tag}[^>]*/?>", "<!-- blocked -->", RegexOptions.IgnoreCase);
        }

        foreach (var attr in DangerousAttributes)
        {
            sanitized = Regex.Replace(sanitized, $@"\s{attr}\s*=\s*""[^""]*""", "", RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, $@"\s{attr}\s*=\s*'[^']*'", "", RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, $@"\s{attr}\s*=\s*\S+", "", RegexOptions.IgnoreCase);
        }

        sanitized = Regex.Replace(sanitized, @"javascript\s*:", "blocked:", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"vbscript\s*:", "blocked:", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"data\s*:", "blocked:", RegexOptions.IgnoreCase);

        return sanitized;
    }

    public static bool IsValidSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        return SlugValidationRegex().IsMatch(slug);
    }

    public static string SanitizeJsonConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement);
        }
        catch
        {
            return "{}";
        }
    }

    public static bool IsValidDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        return DomainValidationRegex().IsMatch(domain);
    }

    public static bool IsValidHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return false;
        return HexColorRegex().IsMatch(color);
    }

    [GeneratedRegex(@"expression\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ExpressionRegex();

    [GeneratedRegex(@"@import\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"url\s*\(\s*['""]?\s*javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex UrlJavascriptRegex();

    [GeneratedRegex(@"behavior\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex BehaviorRegex();

    [GeneratedRegex(@"-moz-binding\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex BindingRegex();

    [GeneratedRegex(@"^[a-z0-9฀-๿]([a-z0-9฀-๿-]*[a-z0-9฀-๿])?$")]
    private static partial Regex SlugValidationRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}$")]
    private static partial Regex DomainValidationRegex();

    [GeneratedRegex(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColorRegex();
}
