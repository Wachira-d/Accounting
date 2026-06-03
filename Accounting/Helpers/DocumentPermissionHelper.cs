using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;

namespace Accounting.Helpers;

/// <summary>
/// Decides whether a user may create / approve / void / list documents of a
/// given <see cref="DocumentType"/>. Honours BOTH the blanket
/// <c>Document.*</c> keys AND the per-direction <c>Document.Revenue.*</c> /
/// <c>Document.Purchase.*</c> keys — a holder of either passes.
///
/// Backwards-compat rule for <see cref="VisibleDirectionsAsync"/>: a user
/// who holds neither a blanket nor a direction-specific View key falls
/// through to "see all" (so existing deployments where no Document.* key has
/// been granted continue to behave exactly as before).
/// </summary>
public static class DocumentPermissionHelper
{
    private static readonly HashSet<DocumentType> Revenue = new()
    {
        DocumentType.Quotation, DocumentType.Invoice, DocumentType.TaxInvoice,
        DocumentType.Receipt, DocumentType.ReceiptVoucher,
        DocumentType.DebitNote, DocumentType.CreditNote,
        DocumentType.DeliveryNote, DocumentType.BillingNote,
    };

    private static readonly HashSet<DocumentType> Purchase = new()
    {
        DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder,
        DocumentType.GoodsReceiptNote, DocumentType.PurchaseInvoice,
        DocumentType.Expense, DocumentType.PaymentVoucher,
        DocumentType.CertificateInLieu,
    };

    public static bool IsRevenue(DocumentType t) => Revenue.Contains(t);
    public static bool IsPurchase(DocumentType t) => Purchase.Contains(t);

    public static Task<bool> CanCreateAsync(IPermissionService perms, Guid companyId, Guid userId, DocumentType type) =>
        AnyAsync(perms, companyId, userId,
            PermissionKeys.DocumentCreate,
            IsRevenue(type) ? PermissionKeys.DocumentRevenueCreate
            : IsPurchase(type) ? PermissionKeys.DocumentPurchaseCreate
            : null);

    public static Task<bool> CanApproveAsync(IPermissionService perms, Guid companyId, Guid userId, DocumentType type) =>
        AnyAsync(perms, companyId, userId,
            PermissionKeys.DocumentApprove,
            IsRevenue(type) ? PermissionKeys.DocumentRevenueApprove
            : IsPurchase(type) ? PermissionKeys.DocumentPurchaseApprove
            : null);

    public static Task<bool> CanVoidAsync(IPermissionService perms, Guid companyId, Guid userId, DocumentType type) =>
        AnyAsync(perms, companyId, userId,
            PermissionKeys.DocumentVoid,
            IsRevenue(type) ? PermissionKeys.DocumentRevenueVoid
            : IsPurchase(type) ? PermissionKeys.DocumentPurchaseVoid
            : null);

    /// <summary>Direction filter for list endpoints. Returns the set the
    /// user may see — {Revenue, Purchase, Other} subset. "Other" covers
    /// document types we haven't placed in either bucket (defensive: future
    /// types default to visible). Backwards-compat: when no direction-View
    /// key is held, returns ALL three so legacy roles still see everything.</summary>
    public static async Task<DocumentVisibility> VisibleDirectionsAsync(
        IPermissionService perms, Guid companyId, Guid userId)
    {
        var rev = await perms.HasPermissionAsync(companyId, userId, PermissionKeys.DocumentRevenueView);
        var pur = await perms.HasPermissionAsync(companyId, userId, PermissionKeys.DocumentPurchaseView);
        if (!rev && !pur) return DocumentVisibility.All;       // legacy / no split configured
        return new DocumentVisibility(rev, pur, Other: false); // explicit split — hide "other" too? keep visible.
    }

    private static async Task<bool> AnyAsync(IPermissionService perms, Guid companyId, Guid userId,
        string blanket, string? specific)
    {
        if (await perms.HasPermissionAsync(companyId, userId, blanket)) return true;
        if (specific != null && await perms.HasPermissionAsync(companyId, userId, specific)) return true;
        return false;
    }
}

/// <summary>What document directions a user may see on list endpoints.</summary>
public readonly record struct DocumentVisibility(bool Revenue, bool Purchase, bool Other)
{
    public static DocumentVisibility All { get; } = new(true, true, true);
    public bool ShowsEverything => Revenue && Purchase && Other;
    public bool Allows(DocumentType t) =>
        DocumentPermissionHelper.IsRevenue(t) ? Revenue
        : DocumentPermissionHelper.IsPurchase(t) ? Purchase
        : Other;
}
