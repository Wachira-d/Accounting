namespace Accounting.Models.Entities;

/// <summary>
/// "This OCR'd wording is NOT this product" — user-confirmed rejection.
/// Filed when the user, presented with a suggested match in the import
/// modal, explicitly picks "ไม่ใช่อันนี้". On the next scan that
/// produces the same normalized wording, the rejected ProductId is
/// excluded from the candidate set, so the same wrong suggestion can't
/// re-surface.
///
/// Scope is per (CompanyId, NormalizedName, RejectedProductId). Tying
/// to NormalizedName (not the raw description) lets one rejection
/// silence all spelling variants of the same wording. ContactId is
/// optional — null = "never propose this product for this wording from
/// ANY vendor"; non-null = "only suppress for this vendor".
/// </summary>
public class ProductNegativeAlias : TenantEntity
{
    public string NormalizedName { get; set; } = null!;
    public Guid RejectedProductId { get; set; }
    public Product RejectedProduct { get; set; } = null!;
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public string? Reason { get; set; }   // optional free-text the user typed
}
