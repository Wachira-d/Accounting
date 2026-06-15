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

        // BUG FIX (TZ): ใช้ Asia/Bangkok แทน UTC. เดิม UtcNow ที่
        // 00:00-06:59 BKK ของวันแรกของเดือน = previous month UTC →
        // เอกสารที่สร้างกลางคืน 7 ชม.แรกของเดือนใหม่ ได้เลขเดือนก่อน
        // (เช่น 1 มี.ค. 03:00 BKK → "INV-202402-001" แทน "INV-202403-001").
        DateTime bkkNow;
        try
        {
            var bkkTz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok");
            bkkNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, bkkTz);
        }
        catch { bkkNow = DateTime.UtcNow.AddHours(7); }   // hardcoded fallback (+07:00 ตลอดปี ไม่มี DST)
        var yearMonth = bkkNow.ToString("yyyyMM");
        var docPrefix = $"{prefix}-{yearMonth}-";
        // BUG FIX: previously used `MaxAsync()` over the string column. That
        // returns the LEXICOGRAPHIC max — "9999" > "10000" because '9' > '1'.
        // The moment a company hit 10000 documents/month the next call
        // returned "9999" → next = 10000 → DUPLICATE. Pulling the suffix
        // numbers + integer-max in-memory is correct regardless of digit
        // width; we cap the pull at 1000 latest just to bound memory.
        var suffixes = await db.Documents
            .IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && d.DocumentNumber.StartsWith(docPrefix))
            .OrderByDescending(d => d.CreatedAt)
            .Take(2000)
            .Select(d => d.DocumentNumber.Substring(docPrefix.Length))
            .ToListAsync();
        var nextSeq = 1;
        foreach (var s in suffixes)
            if (int.TryParse(s, out var parsed) && parsed >= nextSeq) nextSeq = parsed + 1;
        // D5 (5 digits) supports 99,999 per month — well past any realistic
        // SME volume but still narrow enough to sort visually. Existing 4-
        // digit numbers (0001-9999) remain readable; new ones beyond 9999
        // just gain a digit.
        return nextSeq <= 9999
            ? $"{docPrefix}{nextSeq:D4}"
            : $"{docPrefix}{nextSeq:D5}";
    }
}
