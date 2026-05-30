using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// Single source of truth for the per-tenant, per-month, per-type document
/// number sequence (e.g. "PV-202605-0001"). Both DocumentService (manual
/// creation) and OcrService (auto-create from scan) call into here so OCR-
/// generated documents get the same numbering convention as user-created
/// ones — no more "OCR-yyyyMMdd-XXXXXX" outliers.
///
/// Atomicity: PostgreSQL advisory lock keyed by (companyId, prefix) keeps
/// concurrent inserts from issuing duplicate sequence numbers. Caller is
/// responsible for being inside a transaction; the lock auto-releases at
/// transaction end.
/// </summary>
public static class DocumentNumberGenerator
{
    public static string GetPrefix(DocumentType type) => type switch
    {
        DocumentType.Quotation => "QT",
        DocumentType.Invoice => "INV",
        DocumentType.Receipt => "REC",
        DocumentType.TaxInvoice => "TIV",
        DocumentType.DebitNote => "DN",
        DocumentType.CreditNote => "CN",
        DocumentType.DeliveryNote => "DLV",
        DocumentType.BillingNote => "BN",
        DocumentType.ReceiptVoucher => "RV",
        DocumentType.PurchaseRequisition => "PR",
        DocumentType.PurchaseOrder => "PO",
        DocumentType.GoodsReceiptNote => "GR",
        DocumentType.PurchaseInvoice => "PI",
        DocumentType.Expense => "EXP",
        DocumentType.PaymentVoucher => "PV",
        DocumentType.CertificateInLieu => "CIL",
        _ => "DOC"
    };

    public static async Task<string> NextAsync(AccountingDbContext db, Guid companyId, DocumentType type)
    {
        var prefix = GetPrefix(type);
        var lockKey = HashCode.Combine(companyId, prefix, "doc-seq");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var docPrefix = $"{prefix}-{yearMonth}-";
        var maxNumber = await db.Documents
            .IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && d.DocumentNumber.StartsWith(docPrefix))
            .Select(d => d.DocumentNumber)
            .MaxAsync() as string;
        var nextSeq = 1;
        if (maxNumber != null)
        {
            var lastPart = maxNumber.Substring(docPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) nextSeq = parsed + 1;
        }
        return $"{docPrefix}{nextSeq:D4}";
    }
}
