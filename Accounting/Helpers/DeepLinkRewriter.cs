namespace Accounting.Helpers;

/// <summary>
/// Rewrites pretty deep-link URLs (used by external systems calling our API)
/// into the in-app page URL with the right query param so the existing
/// per-page deep-link handler opens the record.
///
/// Pattern: <c>/{companyId-guid}/{entity}/{recordId-guid}</c>
/// Example: <c>/3c98...0df9/journals/5a95...a873</c>
///       → <c>/pages/journals.html?company=3c98...0df9&amp;entryId=5a95...a873</c>
///
/// The mapping list mirrors what the page-level JS already supports (param
/// names like <c>entryId</c> / <c>editDoc</c> / <c>id</c> are intentionally
/// per-page so the existing handlers don't need to change).
/// </summary>
public static class DeepLinkRewriter
{
    /// <summary>
    /// (entity-segment, target page, query-param name).
    /// Add a row here when a new page wants to accept deep-link callbacks.
    /// </summary>
    private static readonly (string Entity, string Page, string Param)[] _routes = new[]
    {
        ("journals",        "journals.html",        "entryId"),
        ("journal-entries", "journals.html",        "entryId"),
        ("documents",       "documents.html",       "editDoc"),
        ("invoices",        "documents.html",       "editDoc"),
        ("tax-invoices",    "documents.html",       "editDoc"),
        ("receipts",        "documents.html",       "editDoc"),
        ("quotations",      "documents.html",       "editDoc"),
        ("payment-vouchers","documents.html",       "editDoc"),
        // Expense / purchase docs are also just Documents (?side=expense in
        // the UI but documents.html opens any doc id via editDoc). External
        // systems were linking /{company}/expenses/{id} and hitting a 404
        // because the segment wasn't mapped.
        ("expenses",        "documents.html",       "editDoc"),
        ("expense",         "documents.html",       "editDoc"),
        ("purchase-invoices","documents.html",      "editDoc"),
        ("purchases",       "documents.html",       "editDoc"),
        ("bills",           "documents.html",       "editDoc"),
        ("billing-notes",   "documents.html",       "editDoc"),
        ("credit-notes",    "documents.html",       "editDoc"),
        ("debit-notes",     "documents.html",       "editDoc"),
        ("contacts",        "contacts.html",        "id"),
        ("products",        "products.html",        "id"),
        ("supplies",        "supplies.html",        "id"),
        ("fixed-assets",    "fixed-assets.html",    "id"),
        ("payments",        "payments.html",        "id"),
        ("payroll-runs",    "payroll.html",         "runId"),
        ("projects",        "projects.html",        "id"),
        ("reservations",    "pos-reservations.html","id"),
        ("pos-orders",      "cms-orders.html",      "id"),
    };

    /// <summary>Try to rewrite the path. Returns the redirect target, or null
    /// when the path doesn't look like a deep-link pattern.</summary>
    public static string? TryMap(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return null;

        // Split into segments — expect either 2 (companyId / entity) or 3 (companyId / entity / id).
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Length > 3) return null;

        // First segment must look like a GUID — otherwise it's some other route.
        if (!Guid.TryParse(segments[0], out var companyId)) return null;

        var entity = segments[1].ToLowerInvariant();
        var match = _routes.FirstOrDefault(r => r.Entity == entity);
        if (match.Page == null) return null;

        var basePath = $"/pages/{match.Page}?company={companyId}";
        if (segments.Length == 3)
        {
            if (!Guid.TryParse(segments[2], out var recordId)) return null;
            basePath += $"&{match.Param}={recordId}";
        }
        // Tag the redirect so the page can show a "🔗 มาจากระบบที่เชื่อมต่อ"
        // badge if it wants to. The query param is harmless on pages that
        // don't read it.
        basePath += "&fromDeepLink=1";
        return basePath;
    }

    /// <summary>Pretty-link generator for our own API responses — keep the
    /// response shape consistent with what TryMap parses so we never ship
    /// a link our own server can't resolve.</summary>
    public static string BuildLink(string baseUrl, Guid companyId, string entity, Guid recordId)
        => $"{baseUrl.TrimEnd('/')}/{companyId}/{entity}/{recordId}";
}
