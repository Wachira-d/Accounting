namespace Accounting.Models.Entities;

/// <summary>
/// Alternate / historical names for a Product — fed by OCR receipts where
/// the same physical item shows up under many wordings:
///   • "น้ำมันพืช ตรา A 1 ลิตร" (vendor A's invoice)
///   • "น้ำมัน A 1L"            (vendor B's invoice, abbreviated)
///   • "A OIL 1L"                (export label)
///   • "น้ำมัน 1 ลิตร A"        (token-shuffled)
/// When the user confirms one of these as the same Product, we persist the
/// raw OCR'd description here together with a normalized form. Future OCR
/// scans look this table up first — direct alias hit beats any fuzzy
/// scoring, so the system "learns" each vendor's naming conventions.
///
/// ContactId is optional:
///   • Non-null → alias is supplier-specific (typical case — Vendor A
///     consistently calls it X; Vendor B calls it Y).
///   • Null     → alias matches regardless of vendor (after the same name
///     has surfaced from several different suppliers).
/// </summary>
public class ProductAlias : TenantEntity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    // Optional: bind alias to a specific vendor so we don't cross-pollinate
    // "Cola 325 ml" matches between unrelated suppliers in unrelated industries.
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>Verbatim OCR'd description as it appeared on the source receipt.</summary>
    public string AliasName { get; set; } = null!;

    /// <summary>Lower-cased, whitespace/punctuation-stripped, unit-tokenized
    /// form used for fast equality lookups + pg_trgm indexing.</summary>
    public string NormalizedName { get; set; } = null!;

    /// <summary>Number of times this alias has been confirmed by a user.
    /// Promotes confident aliases when ranking candidates.</summary>
    public int TimesUsed { get; set; } = 1;

    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>How the alias was created: "user" (manual pick in import-
    /// stock modal), "auto" (high-confidence fuzzy hit accepted by user),
    /// "import" (back-fill from existing PurchaseInvoiceLines).</summary>
    public string Source { get; set; } = "user";
}
