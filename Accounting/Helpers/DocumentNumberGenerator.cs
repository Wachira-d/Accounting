using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// Single source of truth for the per-tenant, per-DAY, per-type document
/// number sequence — format {PREFIX}-{yyyyMMdd}-{NNNN} (เช่น "PV-20260615-0001").
/// เลขฝังวันเดือนปีของเอกสาร → เลขสอดคล้องกับวันที่เสมอ + sequence เริ่มใหม่
/// ทุกวัน. Both DocumentService (manual) และ OcrService (auto-create from scan)
/// เรียกที่นี่ → เลขแบบเดียวกันทุก channel.
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

    public static Task<string> NextAsync(AccountingDbContext db, Guid companyId, DocumentType type)
        => NextAsync(db, companyId, type, documentDate: null);

    /// <summary>เลขเอกสารใช้ yyyyMM ของ <paramref name="documentDate"/> (ถ้าระบุ)
    /// — เลขกับวันที่จะสอดคล้องกันเสมอ. เดิมใช้ "เดือนปัจจุบัน" ของเครื่อง
    /// → เอกสารวันที่ 28/05 ที่ approve วันที่ 1/06 จะได้ "PV-202606-0001"
    /// (ผิด — เลขควรเป็น 202605). null = fallback bkkNow (เคสไม่รู้วันที่
    /// ตอน generate เช่น JE manual).</summary>
    /// <summary>ตัวย่อที่บริษัทนี้ใช้จริงสำหรับชนิดเอกสารนี้ — <c>NumberSeries</c>
    /// (ถ้าตั้งไว้) ชนะค่ามาตรฐาน
    ///
    /// ⚠️ override ได้เฉพาะ **ตัวย่อ** เท่านั้น ไม่ใช่รูปแบบเลข: รูปแบบยังเป็น
    /// <c>{PREFIX}-{yyyyMMdd}-{NNNN}</c> เสมอทั้งระบบ. เดิม NumberSeries มีช่อง
    /// <c>Format</c>/<c>CurrentNumber</c> ที่สร้างเลข**คนละทรง**ขึ้นมา (รายเดือน
    /// นับเอง ไม่มี advisory lock) — ถ้ามีใครสร้างแถวขึ้นมา บริษัทเดียวจะมีเลข
    /// สองทรงปนกันและเส้นนั้นออกเลขซ้ำได้ (§86/4 บังคับไม่ซ้ำ ไม่ขาดช่วง)
    /// จึงเก็บไว้แค่ส่วนที่ผู้ใช้ต้องการจริง คือ "ขอเปลี่ยนตัวย่อ"</summary>
    public static async Task<string> ResolvePrefixAsync(AccountingDbContext db, Guid companyId, DocumentType type)
    {
        var custom = await db.Set<NumberSeries>().AsNoTracking()
            .Where(n => n.CompanyId == companyId && n.DocumentType == type && n.IsActive && !n.IsDeleted)
            .Select(n => n.Prefix)
            .FirstOrDefaultAsync();
        return string.IsNullOrWhiteSpace(custom) ? GetPrefix(type) : custom.Trim();
    }

    public static async Task<string> NextAsync(AccountingDbContext db, Guid companyId, DocumentType type,
        DateTime? documentDate)
    {
        var prefix = await ResolvePrefixAsync(db, companyId, type);
        // ⚠️ HashCode.Combine สุ่ม seed ต่อ process ⇒ สอง instance ได้คีย์คนละค่า
        // = ล็อกกันข้ามเครื่องไม่ได้ ⇒ เลขเอกสารซ้ำ (§86/4 บังคับไม่ซ้ำ ไม่ขาดช่วง)
        var lockKey = AdvisoryLockKey.For(companyId, AdvisoryLockKey.DocumentSequence, prefix);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        // เลขใช้ yyyyMMdd ของวันที่ "ตามปฏิทินไทย" (ThaiDate.YyyyMmDd) — แม้
        // DocumentDate ถูก store เป็น UTC ที่ shift (02/06 BKK = 01/06 17:00 UTC)
        // ก็ได้เลข 20260602 ตรงกับ display เสมอ.
        var datePart = ThaiDate.YyyyMmDd(documentDate ?? DateTime.UtcNow);
        var docPrefix = $"{prefix}-{datePart}-";
        // BUG FIX: previously used `MaxAsync()` over the string column. That
        // returns the LEXICOGRAPHIC max — "9999" > "10000" because '9' > '1'.
        // Pulling the suffix numbers + integer-max in-memory is correct
        // regardless of digit width. Daily prefix → same-day docs only (small set).
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
        // D4 (0001-9999/วัน) เพียงพอกับ SME; เกิน 9999/วัน → ขยายเป็น D5 เอง
        return nextSeq <= 9999
            ? $"{docPrefix}{nextSeq:D4}"
            : $"{docPrefix}{nextSeq:D5}";
    }

}
