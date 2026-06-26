using Accounting.Data;
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
    public static async Task<string> NextAsync(AccountingDbContext db, Guid companyId, DocumentType type,
        DateTime? documentDate)
    {
        var prefix = GetPrefix(type);
        var lockKey = HashCode.Combine(companyId, prefix, "doc-seq");
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", lockKey);

        // เลือกวันที่จาก DocumentDate ก่อน — สอดคล้องกับวันที่ลงในเอกสาร.
        // ⚠️ TZ FIX: DocumentDate ที่ round-trip ผ่าน DB (timestamptz) กลับมา
        // เป็น Kind=Utc ที่ shift แล้ว — เช่น 02/06 BKK = 01/06 17:00 UTC.
        // ถ้า format UTC ดิบ → ได้ "20260601" แต่ display แปลงเป็น BKK = "02/06"
        // → เลข ≠ วันที่. แก้: แปลงเป็น Asia/Bangkok ก่อน format ทุกครั้ง
        // (ตรงกับที่ UI แสดง) → เลข + วันที่สอดคล้องกัน 100%.
        var datePart = ToBangkokDate(documentDate ?? DateTime.UtcNow);
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

    /// <summary>คืน yyyyMMdd ของ "วันที่ตามปฏิทินไทย" (Asia/Bangkok) — ตรงกับ
    /// ที่ UI แสดง. รับ DateTime ทุก Kind: Utc → แปลง +07:00; Unspecified/Local
    /// (calendar date จาก date input ที่ยังไม่ผ่าน DB) → treat เป็น UTC แล้ว
    /// แปลง (midnight Unspecified → 07:00 BKK = วันเดิม ไม่ shift).</summary>
    private static string ToBangkokDate(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Utc
            ? dt
            : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        DateTime bkk;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Bangkok");
            bkk = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        }
        catch { bkk = utc.AddHours(7); }   // +07:00 ตลอดปี ไม่มี DST
        return bkk.ToString("yyyyMMdd");
    }
}
