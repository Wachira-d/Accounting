using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// กวาด JE ที่ **post ไปแล้ว** หาใบที่โครงสร้างผิด — คู่กับ JournalPostingGuard
/// ที่กันใบใหม่: guard กันไม่ให้เกิดเพิ่ม, scanner หาใบเก่าที่หลุดมาก่อน
/// แล้วชี้ทางแก้ (แผง "📒 รายการบัญชี" → แก้ผังบัญชี / ยกเลิกเอกสาร)
///
/// ตรวจ 2 มุม:
///   1. JE ที่มีอยู่ — รันกฎ JournalPostingGuard ชุดเดียวกับตอน post
///      (canonical function เดียว ห้ามเขียนกฎซ้ำสองที่ — กฎเหล็ก #4 C)
///   2. เอกสารที่ **ควรมี JE แต่ไม่มี** — เส้น integration เดิมเจอ JE ไม่
///      สมดุล/โครงสร้างผิดจะ "return null เงียบ ๆ" = ค่าใช้จ่ายอนุมัติแล้ว
///      แต่ไม่ลง GL เลย ไม่มีใครรู้จนงบไม่ตรง
/// </summary>
public class JournalAnomalyService
{
    private readonly AccountingDbContext _db;

    public JournalAnomalyService(AccountingDbContext db) => _db = db;

    public record Anomaly(
        string RuleCode,
        string Severity,           // "Error" | "Warning"
        string Message,
        string Fix,                // ทางแก้ที่ทำได้จริงในระบบ
        Guid? DocumentId,
        string? DocumentNumber,
        Guid? JournalEntryId,
        string? EntryNumber,
        DateTime? EntryDate);

    public record ScanResult(
        DateTime FromDate, DateTime ToDate,
        int JournalsScanned, int DocumentsScanned,
        List<Anomaly> Anomalies);

    /// <summary>ชนิดเอกสารที่อนุมัติแล้ว "ต้องมี JE" — ใบเสนอราคา/PO/ใบวางบิล
    /// ไม่ลงบัญชี จึงไม่อยู่ในลิสต์</summary>
    private static readonly DocumentType[] JePostingTypes =
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt,
        DocumentType.Expense, DocumentType.PurchaseInvoice, DocumentType.PaymentVoucher,
        DocumentType.CreditNote, DocumentType.DebitNote,
    };

    public async Task<ScanResult> ScanAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        var anomalies = new List<Anomaly>();

        // ── 1) JE ที่ post แล้ว — รันกฎ guard ─────────────────────────────
        var journals = await _db.JournalEntries.AsNoTracking()
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= from && j.EntryDate <= to)
            .ToListAsync();

        var srcDocIds = journals.Where(j => j.SourceDocumentId.HasValue)
            .Select(j => j.SourceDocumentId!.Value).Distinct().ToList();
        var docsById = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && srcDocIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id);

        foreach (var j in journals)
        {
            var lines = j.Lines
                .Where(l => l.Account != null)
                .Select(l => new JournalPostingGuard.LineFacts(
                    l.Account.AccountCode, l.Account.AccountType,
                    l.DebitAmount, l.CreditAmount))
                .ToList();

            // เทียบกับเอกสารเฉพาะ "JE หลัก" (primary posting ที่ AutoPost/
            // integration สร้างตอนอนุมัติ — ใบเดียวที่ยอดต้องเท่าเอกสารทั้งใบ)
            // ใช้ **whitelist ตาม description จริง** ไม่ใช่ blacklist marker:
            // JE ลูกทุกชนิด (รับ/จ่ายชำระบางส่วน, ตัวกลับ, ใบปรับปรุง, reclassify,
            // ย้าย VAT พัก 21913→21911, ตัดมัดจำ, settlement) ยอดเป็น "บางส่วน/
            // ผลต่าง" โดยธรรมชาติ — เทียบทั้งใบเมื่อไรก็ false positive เมื่อนั้น
            // (เคสจริงจากการ self-review: JE รับชำระงวด 500 ของใบ 1,040 โดนกฎ
            // JE-NO-COUNTERPART ทั้งที่ถูกต้อง). ตรวจโครงสร้าง (doc=null) ยังทำ
            // ทุกใบ — กฎ WHT ≤ 15% ของฐานจับ JE เสียแบบเคสจริงได้โดยไม่รู้เอกสาร
            Document? doc = null;
            var desc = j.Description ?? "";
            var isPrimaryPosting = j.OriginalEntryId == null
                && (desc.StartsWith("Auto-post จาก", StringComparison.Ordinal)
                    || desc.StartsWith("Auto:", StringComparison.Ordinal)
                    || desc.Contains("(integration sync)", StringComparison.Ordinal));
            if (isPrimaryPosting && j.SourceDocumentId.HasValue)
                docsById.TryGetValue(j.SourceDocumentId.Value, out doc);

            var facts = doc != null
                ? new JournalPostingGuard.DocFacts(
                    doc.DocumentType, doc.SubTotal, doc.VatAmount,
                    doc.WithholdingTaxAmount, doc.TotalAmount, doc.IsDeposit)
                : null;

            foreach (var f in JournalPostingGuard.Validate(lines, facts))
                anomalies.Add(new Anomaly(
                    f.RuleCode, f.IsError ? "Error" : "Warning", f.Message,
                    doc != null
                        ? "เปิดเอกสาร → แผง 📒 รายการบัญชี → ✏️ แก้ผังบัญชี (หรือยกเลิกเอกสารแล้วออกใหม่)"
                        : "เปิดใบสำคัญในหน้าสมุดรายวัน → กลับ-แก้ / แก้ไข",
                    doc?.Id, doc?.DocumentNumber, j.Id, j.EntryNumber, j.EntryDate.Date));
        }

        // ── 2) เอกสารอนุมัติแล้วแต่ "ไม่มี JE เลย" ────────────────────────
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && JePostingTypes.Contains(d.DocumentType)
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Sent
                    || d.Status == DocumentStatus.PartiallyPaid || d.Status == DocumentStatus.Paid)
                && d.DocumentDate >= from && d.DocumentDate <= to
                && d.TotalAmount > 1m
                && !d.IsSettlementReceipt)   // ใบเสร็จหลักฐาน = evidence-only ไม่ลง JE
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount })
            .ToListAsync();

        var docIdsWithJe = (await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.SourceDocumentId != null
                    && j.Status == JournalEntryStatus.Posted)
                .Select(j => j.SourceDocumentId!.Value)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        foreach (var d in docs.Where(x => !docIdsWithJe.Contains(x.Id)))
            anomalies.Add(new Anomaly(
                "DOC-NO-JE", "Error",
                $"เอกสาร {d.DocumentNumber} ({d.DocumentType}) ยอด {d.TotalAmount:N2} " +
                "อนุมัติแล้วแต่ไม่มีรายการบัญชีเลย — ยอดนี้หายจากงบ/รายงานภาษี " +
                "(มักเกิดจาก JE integration ที่โครงสร้างผิดถูกระบบปฏิเสธ)",
                "ยกเลิกเอกสารแล้วอนุมัติใหม่ (ระบบจะลง JE ให้) หรือคีย์ JE เองในหน้าสมุดรายวันอ้างเลขเอกสาร",
                d.Id, d.DocumentNumber, null, null, d.DocumentDate.Date));

        return new ScanResult(from, to, journals.Count, docs.Count,
            anomalies
                .OrderBy(a => a.Severity == "Error" ? 0 : 1)
                .ThenByDescending(a => a.EntryDate)
                .ToList());
    }
}
