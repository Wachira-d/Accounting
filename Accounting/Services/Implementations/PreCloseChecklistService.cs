using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// Pre-Close Checklist (Phase J of accountant-tools). Aggregates 8 health
/// checks every Thai accountant runs before closing a month — surfaces
/// them as red/green items on a single dashboard so they don't have to
/// hop across 6 pages every period-end.
/// </summary>
public class PreCloseChecklistService
{
    private readonly AccountingDbContext _db;
    public PreCloseChecklistService(AccountingDbContext db) { _db = db; }

    public record CheckItem(string Code, string Label, bool Passed, string Severity,
        string Message, int Count, string? ActionUrl);

    public record ChecklistResult(int Year, int Month, bool ReadyToClose,
        int PassedCount, int FailedCount, int WarningCount, List<CheckItem> Items);

    /// <summary>ชนิดเอกสารที่ AutoPost ลง JE จริง (ใบเสนอราคา/PR/PO/ใบส่งของ/ใบวางบิล ไม่มี JE โดยธรรมชาติ) — ตัวเดียวของเช็กลิสต์ปิดงวดและตัวตรวจก่อนปิดเดิม</summary>
    public static readonly DocumentType[] JeBearingTypes =
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher,
        DocumentType.DebitNote, DocumentType.CreditNote, DocumentType.PurchaseInvoice, DocumentType.Expense,
        DocumentType.PaymentVoucher, DocumentType.CertificateInLieu, DocumentType.GoodsReceiptNote,
    };

    /// <summary>เดือนปฏิทิน — ถ้ามีงวดบัญชีของ year/month ใช้ช่วงวันที่ของงวดนั้น (ตัวเดียวกับปุ่มปิดงวด)</summary>
    public async Task<ChecklistResult> RunAsync(Guid companyId, int year, int month)
    {
        var period = await _db.FiscalPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == year && p.Month == month);
        if (period != null) return await RunCoreAsync(companyId, year, month, period.StartDate, period.EndDate, period);
        var start = new DateTime(year, month, 1);
        return await RunCoreAsync(companyId, year, month, start, start.AddMonths(1).AddTicks(-1), null);
    }

    /// <summary>รอบ 259-3 (ฝ่ายค้าน M2 · คำตัดสิน 259-3-8): เช็กลิสต์ของงวดที่ระบุ — <c>AccountingService.CloseFiscalPeriodAsync</c> บล็อกด้วย
    /// <see cref="ChecklistResult.ReadyToClose"/> ของตัวนี้ตัวเดียว ⇒ "หน้าบอกพร้อม ⇔ กดปิดได้" (เดิมสองชุดกติกาต่างกัน)</summary>
    public Task<ChecklistResult> RunForPeriodAsync(Guid companyId, FiscalPeriod period)
        => RunCoreAsync(companyId, period.Year, period.Month, period.StartDate, period.EndDate, period);

    private async Task<ChecklistResult> RunCoreAsync(Guid companyId, int year, int month, DateTime from, DateTime toInclusive, FiscalPeriod? fp)
    {
        var periodStart = from;
        var periodEnd = toInclusive;
        var fpId = fp?.Id;
        var items = new List<CheckItem>();

        // 1. No Draft JEs left in period (ผูกงวด หรือวันที่อยู่ในช่วง — ตัวเดียวกับด่านปิดงวดเดิม)
        var draftJeCount = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                && ((fpId != null && j.FiscalPeriodId == fpId) || (j.EntryDate >= periodStart && j.EntryDate <= periodEnd))
                && j.Status == JournalEntryStatus.Draft);
        items.Add(new("DRAFT_JE", "ใบสำคัญที่ยัง Draft", draftJeCount == 0,
            draftJeCount == 0 ? "Info" : "Error",
            draftJeCount == 0 ? "ไม่มี Draft JE ค้าง" : $"พบ Draft JE ค้าง {draftJeCount} ใบ — ต้อง Post หรือ Void ก่อนปิดงวด",
            draftJeCount, "/pages/journals.html?status=Draft"));

        // 2. Posted JE balance check (Dr=Cr per entry — should always be true but verify)
        var unbalancedJeCount = await _db.JournalEntries.AsNoTracking()
            .CountAsync(j => j.CompanyId == companyId && !j.IsDeleted
                && ((fpId != null && j.FiscalPeriodId == fpId) || (j.EntryDate >= periodStart && j.EntryDate <= periodEnd))
                && j.Status == JournalEntryStatus.Posted
                && Math.Abs(j.TotalDebit - j.TotalCredit) > 0.01m);
        items.Add(new("JE_BALANCE", "ใบสำคัญสมดุล Dr=Cr", unbalancedJeCount == 0,
            unbalancedJeCount == 0 ? "Info" : "Error",
            unbalancedJeCount == 0 ? "ใบสำคัญทุกใบสมดุล" : $"⚠️ พบใบสำคัญไม่สมดุล {unbalancedJeCount} ใบ — เร่งตรวจสอบ",
            unbalancedJeCount, "/pages/journals.html"));

        // 3. Unreconciled bank transactions in period
        var unrecBankCount = await _db.Set<BankTransaction>().AsNoTracking()
            .CountAsync(t => t.CompanyId == companyId && !t.IsDeleted
                && t.TransactionDate >= periodStart && t.TransactionDate <= periodEnd
                && t.ReconciliationStatus == ReconciliationStatus.Unmatched);
        // รอบ 259-3 (M2): กติกาเดียวกับด่านปิดงวดเดิม — ค้างแม้รายการเดียว = ปิดไม่ได้ (เดิม checklist ปล่อย 1–4 รายการเป็นคำเตือน แต่ปุ่มปิดบล็อก)
        items.Add(new("BANK_UNRECONCILED", "รายการธนาคารยังไม่กระทบยอด", unrecBankCount == 0,
            unrecBankCount == 0 ? "Info" : "Error",
            unrecBankCount == 0 ? "กระทบยอดธนาคารครบทุกรายการ" : $"พบ {unrecBankCount} รายการที่ยังไม่กระทบยอด — กระทบยอดหรือตั้งเป็น \"ไม่รวม\" ก่อนปิดงวด",
            unrecBankCount, "/pages/bank.html"));

        // รอบ 259-3 (M2): เอกสารร่างในงวด = ปิดไม่ได้ (ด่านเดิมของปุ่มปิด · หน้าเคยแสดง แต่เช็กลิสต์ไม่มีข้อนี้)
        var draftDocCount = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= periodStart && d.DocumentDate <= periodEnd
                && d.Status == DocumentStatus.Draft);
        items.Add(new("DRAFT_DOCS", "เอกสารที่ยังเป็นร่าง", draftDocCount == 0,
            draftDocCount == 0 ? "Info" : "Error",
            draftDocCount == 0 ? "ไม่มีเอกสารร่างในงวด" : $"ยังมีเอกสารร่าง {draftDocCount} ฉบับในงวดนี้ — อนุมัติหรือยกเลิกก่อนปิดงวด",
            draftDocCount, "/pages/documents.html"));

        // รอบ 259-3 (M2): เอกสารที่ออกแล้วแต่ไม่มี JE (AutoPost ล้ม) — ย้ายจากตัวตรวจก่อนปิดเดิม (ป้าย 🤖 ที่ถอดไป) · คำเตือน ไม่บล็อก
        // (ข้อมูลเก่าที่ลงบัญชีด้วยมือแยกใบได้ — ให้คนตัดสิน · คำตัดสิน 259-3-8)
        var docNoJeCount = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= periodStart && d.DocumentDate <= periodEnd
                && JeBearingTypes.Contains(d.DocumentType)
                && !Accounting.Helpers.DocumentStatusRules.NotIssued.Contains(d.Status)
                && d.Status != DocumentStatus.Voided
                && !_db.JournalEntries.Any(j => j.SourceDocumentId == d.Id && !j.IsDeleted));
        items.Add(new("DOC_NO_JE", "เอกสารที่ออกแล้วแต่ยังไม่ลงบัญชี", docNoJeCount == 0,
            docNoJeCount == 0 ? "Info" : "Warning",
            docNoJeCount == 0 ? "เอกสารที่ออกแล้วลงบัญชีครบ" : $"พบเอกสาร {docNoJeCount} ใบที่ออกแล้วแต่ไม่มีรายการบัญชี — ลงบัญชีใหม่จากหน้าเอกสาร หรือบันทึกด้วยมือ",
            docNoJeCount, "/pages/documents.html"));

        // 4. Documents pending approval that should have been actioned
        var pendingDocCount = await _db.Documents.AsNoTracking()
            .CountAsync(d => d.CompanyId == companyId && !d.IsDeleted
                && d.DocumentDate >= periodStart && d.DocumentDate <= periodEnd
                && d.Status == DocumentStatus.WaitingApproval);
        items.Add(new("DOC_PENDING_APPROVAL", "เอกสารรออนุมัติ", pendingDocCount == 0,
            pendingDocCount == 0 ? "Info" : "Warning",
            pendingDocCount == 0 ? "เอกสารอนุมัติครบ" : $"พบเอกสารรออนุมัติ {pendingDocCount} ฉบับ",
            pendingDocCount, "/pages/approval.html"));

        // 5. VAT report exists for this period
        var vatExists = await _db.TaxReports.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.TaxType == TaxType.VAT
                && r.Year == year && r.Month == month);
        items.Add(new("VAT_REPORT", "รายงาน ภพ.30 ของงวด", vatExists,
            vatExists ? "Info" : "Warning",
            vatExists ? "สร้างรายงาน ภพ.30 แล้ว" : "ยังไม่ได้สร้างรายงาน ภพ.30",
            0, "/pages/tax.html"));

        // 6. WHT reports exist
        // ⚠️ `TaxService.CreateTaxReport` **ปฏิเสธ** WithholdingTax1 โดยตั้งใจ (ภ.ง.ด.1 ยื่นผ่าน
        // เมนูส่งออกไฟล์จากรอบเงินเดือน ไม่ใช่ TaxReport) ⇒ เงื่อนไขเดิมที่นับ WithholdingTax1
        // เป็นจริงไม่ได้เลย และลิงก์ไป wht.html (ทะเบียน 50 ทวิ) ไม่ใช่ที่สร้างรายงาน —
        // checklist ข้อนี้จึงเตือนผิดที่มาตลอด. ตรวจเฉพาะ 3/53 และพาไปหน้ารายงานภาษี
        var whtExists = await _db.TaxReports.AsNoTracking()
            .CountAsync(r => r.CompanyId == companyId
                && (r.TaxType == TaxType.WithholdingTax3 || r.TaxType == TaxType.WithholdingTax53
                    || r.TaxType == TaxType.WithholdingTax2)   // รอบ 204: ภ.ง.ด.2 นับเป็นรายงาน WHT ของงวดด้วย
                && r.Year == year && r.Month == month);
        items.Add(new("WHT_REPORTS", "รายงาน ภงด.3/53", whtExists >= 1,
            whtExists >= 1 ? "Info" : "Warning",
            whtExists >= 1 ? $"สร้างรายงาน WHT {whtExists} ฉบับ" : "ยังไม่ได้สร้างรายงาน ภ.ง.ด.3/53 (ภ.ง.ด.1 ดูที่ไฟล์ยื่นจากรอบเงินเดือน)",
            whtExists, "/pages/tax.html"));

        // 7. Fixed Assets depreciation run for the month
        // Heuristic: any auto-generated JE in the period that references a
        // depreciation expense account (51x or 53x with "เสื่อม" in name).
        var depJeCount = await (
            from j in _db.JournalEntries
            join l in _db.JournalEntryLines on j.Id equals l.JournalEntryId
            join a in _db.ChartOfAccounts on l.AccountId equals a.Id
            where j.CompanyId == companyId
                && j.EntryDate >= periodStart && j.EntryDate <= periodEnd
                && j.Status == JournalEntryStatus.Posted
                && a.AccountName.Contains("ค่าเสื่อม")
            select j.Id).Distinct().CountAsync();
        var hasFa = await _db.FixedAssets.AsNoTracking()
            .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted && a.Status == AssetStatus.Active
                && !a.IsUnderAcquisition);   // รอบ 249 (L8): ระหว่างจัดหายังไม่คิดค่าเสื่อม — ไม่ใช่เหตุให้เตือน "ยังไม่ได้รันค่าเสื่อม"
        var depPassed = !hasFa || depJeCount > 0;
        items.Add(new("DEPRECIATION_RUN", "รันค่าเสื่อมสินทรัพย์ถาวร", depPassed,
            depPassed ? "Info" : "Warning",
            !hasFa ? "ไม่มีสินทรัพย์ถาวร — ข้ามได้"
                : (depJeCount > 0 ? $"พบ JE ค่าเสื่อมในงวด {depJeCount} ใบ"
                                  : "⚠️ ยังไม่ได้รันค่าเสื่อมในงวดนี้"),
            depJeCount, "/pages/fixed-assets.html"));

        // 9. ทบทวนอายุใช้งาน/ซาก/วิธีคิดค่าเสื่อมสิ้นรอบบัญชี (รอบ 201 ทีม IN · C-6 · คำตัดสินข้อ 79 · TFRS for NPAEs บทที่ 10)
        // เตือนเฉพาะเดือนสุดท้ายของรอบบัญชี · ตัวตัดสินเดียว Helpers/UsefulLifeReview · ไม่บล็อกการปิด (Warning) — ทางไปต่อ:
        // หน้าสินทรัพย์ถาวร → “ปรับอายุการใช้งาน” (ยืนยันค่าเดิมได้ = บันทึกว่าทบทวนแล้ว)
        var fyStartMonth = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.FiscalYearStartMonth).FirstOrDefaultAsync();
        if (Accounting.Helpers.UsefulLifeReview.IsFiscalYearEndMonth(month, fyStartMonth))
        {
            var fyStart = Accounting.Helpers.UsefulLifeReview.FiscalYearStart(year, month, fyStartMonth);
            var assets = await _db.FixedAssets.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Status == AssetStatus.Active)
                .Select(a => new { a.AssetCode, a.Status, a.DepreciationMethod, a.UsefulLifeMonths, a.PurchaseDate, a.UsefulLifeReviewedAt })
                .ToListAsync();
            var notReviewed = assets
                .Where(a => Accounting.Helpers.UsefulLifeReview.NeedsReview(a.Status, a.DepreciationMethod, a.UsefulLifeMonths,
                    a.PurchaseDate, a.UsefulLifeReviewedAt, fyStart))
                .Select(a => a.AssetCode).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var sample = string.Join(", ", notReviewed.Take(5)) + (notReviewed.Count > 5 ? " …" : "");
            items.Add(new("USEFUL_LIFE_REVIEW", "ทบทวนอายุใช้งานสินทรัพย์สิ้นรอบบัญชี", notReviewed.Count == 0,
                notReviewed.Count == 0 ? "Info" : "Warning",
                notReviewed.Count == 0
                    ? "สินทรัพย์ที่คิดค่าเสื่อมทุกตัวทบทวนอายุใช้งานในรอบบัญชีนี้แล้ว (หรือขึ้นทะเบียนในรอบนี้)"
                    : $"สินทรัพย์ {notReviewed.Count} รายการยังไม่ได้ทบทวนอายุใช้งาน/มูลค่าซากในรอบบัญชีนี้ ({sample}) — "
                      + "TFRS for NPAEs บทที่ 10 ให้ทบทวนทุกสิ้นรอบ · เปิดหน้าสินทรัพย์ถาวร → “ปรับอายุการใช้งาน” (ยืนยันค่าเดิมได้)",
                notReviewed.Count, "/pages/fixed-assets.html"));
        }

        // 8. Period not already closed
        var period = fp;
        var notClosed = period == null || period.Status == FiscalPeriodStatus.Open;
        items.Add(new("PERIOD_OPEN", "สถานะงวดบัญชี", notClosed,
            notClosed ? "Info" : "Warning",
            period == null ? "ยังไม่มีงวดบัญชีของเดือนนี้ — ระบบจะสร้างให้อัตโนมัติเมื่อปิด"
                : (notClosed ? "งวด Open — พร้อมปิด"
                    : $"งวดนี้สถานะ {period.Status} อยู่แล้ว"),
            0, "/pages/fiscal.html"));

        var failed = items.Count(i => !i.Passed && i.Severity == "Error");
        var warning = items.Count(i => !i.Passed && i.Severity == "Warning");
        var passed = items.Count(i => i.Passed);
        return new ChecklistResult(year, month, failed == 0, passed, failed, warning, items);
    }
}
