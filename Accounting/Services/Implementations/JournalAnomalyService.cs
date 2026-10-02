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

        // ── รอบ 201 ทีม TX (A-TX9 · team-W Q-W6): JE รอบโอน settlement ลงเฉพาะขา WHT ของใบค่าธรรมเนียม (ค่าธรรมเนียมอยู่ในใบสำคัญจ่าย) ──
        // ⇒ ส่งเงินได้ตาม 50 ทวิ ของใบค่าธรรมเนียมเป็นฐาน JE-WHT-RATIO (เดิมรอบที่โอนสุทธิ 0 ฟ้อง 100% ทั้งที่ถูก) · tenant ทุก query ·
        // JSON อ่านไม่ได้ = ไม่ส่งฐาน (กฎฟ้องตามเดิม — ทิศที่มองเห็น)
        var externalWhtBase = new Dictionary<Guid, decimal>();
        var scannedJeIds = journals.Select(j => j.Id).ToList();
        var payoutBatches = await _db.SettlementBatches.AsNoTracking()
                .Where(b => b.CompanyId == companyId && !b.IsDeleted && b.PayoutJournalEntryId != null
                    && scannedJeIds.Contains(b.PayoutJournalEntryId.Value) && b.FeeDocumentIdsJson != null)
                .Select(b => new { JeId = b.PayoutJournalEntryId!.Value, Json = b.FeeDocumentIdsJson! })
                .ToListAsync();
        if (payoutBatches.Count > 0)
        {
            var feeDocsByJe = new Dictionary<Guid, List<Guid>>();
            foreach (var b in payoutBatches)
            {
                try { feeDocsByJe[b.JeId] = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(b.Json) ?? new List<Guid>(); }
                catch (System.Text.Json.JsonException) { continue; }   // อ่านไม่ได้ ⇒ ไม่ส่งฐาน · JE-WHT-RATIO ฟ้องตามเดิม (มองเห็น)
            }
            var allFeeDocIds = feeDocsByJe.Values.SelectMany(x => x).Distinct().ToList();
            var filedStatuses = Accounting.Helpers.WhtCertFilingScope.Filed;
            var certIncomeByDoc = (await _db.WithholdingTaxCerts.AsNoTracking()
                        .Where(w => w.CompanyId == companyId && w.DocumentId != null && allFeeDocIds.Contains(w.DocumentId.Value)
                            && filedStatuses.Contains(w.Status))
                        .Select(w => new { DocId = w.DocumentId!.Value, w.TotalIncomeAmount })
                        .ToListAsync())
                    .GroupBy(x => x.DocId).ToDictionary(g => g.Key, g => g.Sum(x => x.TotalIncomeAmount));
            foreach (var (jeId, feeIds) in feeDocsByJe)
                externalWhtBase[jeId] = feeIds.Sum(id => certIncomeByDoc.GetValueOrDefault(id));
        }

        var srcDocIds = journals.Where(j => j.SourceDocumentId.HasValue)
            .Select(j => j.SourceDocumentId!.Value).Distinct().ToList();
        var docsById = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && srcDocIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id);
        // ฝ่ายค้าน F2+F3 P1-1: ใบสำคัญจ่ายที่ปิดหนี้ใบเจ้าของ ภ.พ.36 — ด่านต้องรู้ค่าเดียวกับที่ JE ใช้ (ใบต้นทางเป็นเจ้าของ)
        var pvSourceIds = docsById.Values
            .Where(d => d.DocumentType == DocumentType.PaymentVoucher && d.RelatedDocumentId.HasValue)
            .Select(d => d.RelatedDocumentId!.Value).Distinct().ToList();
        var pp36OwnerSourceIds = pvSourceIds.Count == 0 ? new HashSet<Guid>()
            : (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && pvSourceIds.Contains(d.Id))
                .Where(Accounting.Helpers.ForeignServiceVat.OwnsPp36Query)
                .Select(d => d.Id).ToListAsync()).ToHashSet();

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
                    doc.WithholdingTaxAmount, doc.TotalAmount, doc.IsDeposit,
                    // JE หลักของ AutoPost ลงเป็นบาทผ่าน Conv() ⇒ แปลงยอดเอกสารก่อนเทียบ
                    ExchangeRate: doc.ExchangeRate <= 0m ? 1m : doc.ExchangeRate,
                    // §83/6 — กฎชุดเดียวกับด่านก่อนบันทึก (PP36_REVIEW P0-1)
                    IsForeignService: doc.IsForeignService,
                    SourceOwnsPp36: doc.RelatedDocumentId is Guid srcId && pp36OwnerSourceIds.Contains(srcId))
                : null;

            foreach (var f in JournalPostingGuard.Validate(lines, facts, externalWhtBase.GetValueOrDefault(j.Id)))
                anomalies.Add(new Anomaly(
                    f.RuleCode, f.IsError ? "Error" : "Warning", f.Message,
                    doc != null
                        ? "เปิดเอกสาร → แผง 📒 รายการบัญชี → ✏️ แก้ผังบัญชี (หรือยกเลิกเอกสารแล้วออกใหม่)"
                        : "เปิดใบสำคัญในหน้าสมุดรายวัน → กลับ-แก้ / แก้ไข",
                    doc?.Id, doc?.DocumentNumber, j.Id, j.EntryNumber, j.EntryDate.Date));
        }

        // ── 2) เอกสารอนุมัติแล้วแต่ "ไม่มี JE เลย" ────────────────────────
        // ฝ่ายค้าน P2-5 (PP36): เงื่อนไขเดียวกับเครื่องมือซ่อม (MissingJournalRepair) — "ควรมี JE" = DocumentJournalExpectation.ExpectsLiveJournal
        // (ชุดชนิด · สถานะมีผล · ไม่ใช่ใบเสร็จหลักฐาน · ไม่ใช่ใบแทนกระดาษ) · "มี JE" = JE หลักที่ยังมีผล (Posted · ไม่ใช่ตัวกลับ · ยังไม่ถูกกลับ)
        // เดิม: ชุดชนิดสำเนาของตัวเอง (ขาด ReceiptVoucher/CIL/GRN) + นับ JE ใดก็ได้ที่ Posted (JE ที่ถูกกลับแล้วก็นับว่า "มี")
        var docs = (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && Accounting.Helpers.DocumentJournalExpectation.PostingTypes.Contains(d.DocumentType)
                    && d.DocumentDate >= from && d.DocumentDate <= to
                    && d.TotalAmount > 1m)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.TotalAmount, d.Status,
                    d.IsSettlementReceipt, d.ReplacesDocumentId, d.ReplacementCarriesPostings })
                .ToListAsync())
            .Where(d => Accounting.Helpers.DocumentJournalExpectation.ExpectsLiveJournal(d.DocumentType, d.Status,
                d.IsSettlementReceipt, d.ReplacesDocumentId.HasValue && !d.ReplacementCarriesPostings))
            .ToList();

        var docIdsWithJe = (await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && !j.IsDeleted
                    && j.SourceDocumentId != null
                    && j.Status == JournalEntryStatus.Posted
                    && j.OriginalEntryId == null && j.ReversedByEntryId == null)
                .Select(j => j.SourceDocumentId!.Value)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        foreach (var d in docs.Where(x => !docIdsWithJe.Contains(x.Id)))
            anomalies.Add(new Anomaly(
                "DOC-NO-JE", "Error",
                $"เอกสาร {d.DocumentNumber} ({d.DocumentType}) ยอด {d.TotalAmount:N2} " +
                "อนุมัติแล้วแต่ไม่มีรายการบัญชีเลย — ยอดนี้หายจากงบ/รายงานภาษี " +
                "(มักเกิดจาก JE integration ที่โครงสร้างผิดถูกระบบปฏิเสธ หรือการอนุมัติที่ล้มแต่สถานะค้างถูกบันทึก — PP36_REVIEW P0-2)",
                // เดิม "ยกเลิกเอกสารแล้วอนุมัติใหม่" — ทำไม่ได้กับใบที่ออกเลขแล้ว (ยกเลิกใบที่ไม่มี JE ไม่มีอะไรให้กลับ · เลขต้องคงเดิม §86/4)
                Accounting.Helpers.MissingJournalRepair.ScannerFix,
                d.Id, d.DocumentNumber, null, null, d.DocumentDate.Date));

        // ── 3) ใบปรับปรุงผังบัญชีที่ "หายไปตอนอนุมัติใหม่" (X-6) ──────────────
        // Adjust ไม่แก้ document line เลย — เจตนาผู้ใช้อยู่ใน **JE ปรับปรุง**
        // ใบเดียว. ยกเลิกเอกสารแล้วคืนชีพ+อนุมัติใหม่ → ใบปรับปรุงถูกกลับไปพร้อม
        // JE หลัก แต่ AutoPost ลง JE ใหม่จาก doc.Lines (ผังเดิม) ⇒ ผังที่ผู้ใช้
        // แก้ไว้หายเงียบ ๆ ไม่มีอะไรเตือน และ Dr=Cr ยังสมดุลทุกใบ
        const string AdjustMarker = "ปรับปรุงผังบัญชีของ";
        var adjustJournals = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted
                && j.SourceDocumentId != null
                && j.Description != null && j.Description.Contains(AdjustMarker))
            .Select(j => new
            {
                DocId = j.SourceDocumentId!.Value,
                j.EntryNumber, j.EntryDate, j.Status, j.ReversedByEntryId, j.OriginalEntryId,
            })
            .ToListAsync();

        foreach (var g in adjustJournals.Where(a => a.OriginalEntryId == null).GroupBy(a => a.DocId))
        {
            // ยังมีใบปรับปรุงที่ยังไม่ถูกกลับ = เจตนายังอยู่ในบัญชี → ไม่ต้องเตือน
            if (g.Any(a => a.Status == JournalEntryStatus.Posted && a.ReversedByEntryId == null))
                continue;
            var lastReversed = g.Where(a => a.ReversedByEntryId != null)
                .OrderByDescending(a => a.EntryDate).FirstOrDefault();
            if (lastReversed == null) continue;

            // มี JE หลักที่ active อยู่ = เอกสารถูกอนุมัติใหม่แล้ว (ถ้าไม่มี แปลว่า
            // ยังยกเลิกอยู่ ซึ่งถูกต้องแล้ว ไม่ใช่ความผิดปกติ)
            var repostedAt = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == companyId && j.SourceDocumentId == g.Key
                    && !j.IsDeleted && j.Status == JournalEntryStatus.Posted
                    && j.OriginalEntryId == null && j.ReversedByEntryId == null
                    && (j.Description == null || !j.Description.Contains(AdjustMarker)))
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => (DateTime?)j.CreatedAt)
                .FirstOrDefaultAsync();
            if (repostedAt == null) continue;

            var docRow = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == g.Key && d.CompanyId == companyId && !d.IsDeleted)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate })
                .FirstOrDefaultAsync();
            if (docRow == null) continue;
            if (docRow.DocumentDate.Date < from || docRow.DocumentDate.Date > to) continue;

            anomalies.Add(new Anomaly(
                "DOC-ADJUST-LOST", "Warning",
                $"เอกสาร {docRow.DocumentNumber} เคยมีใบสำคัญปรับปรุงผังบัญชี ({lastReversed.EntryNumber}) " +
                "ที่ถูกกลับรายการไปแล้ว และเอกสารถูกลงบัญชีใหม่ด้วยผังเดิม — " +
                "ผังที่แก้ไว้ไม่ได้ถูกนำกลับมาใช้ (เครื่องมือปรับปรุงเก็บเจตนาไว้ในใบสำคัญ ไม่ได้แก้ตัวเอกสาร)",
                "เปิดเอกสาร → แผง 📒 รายการบัญชี → ปรับปรุงผังบัญชีอีกครั้ง " +
                "(หรือแก้ผังที่บรรทัดเอกสารด้วย ✏️ เปลี่ยนผัง เพื่อให้อยู่ถาวร)",
                docRow.Id, docRow.DocumentNumber, null, lastReversed.EntryNumber,
                lastReversed.EntryDate.Date));
        }

        // ── 4) JE เก่าที่ลงขาเงิน/ลูกหนี้ผิดหมวด (รอบ 198 I-1/P-1) — รายงานให้นักบัญชีตรวจ ไม่แก้อัตโนมัติ (คำตัดสินรอบ 200 ข้อ 20) ──
        // ใช้ JE ชุดเดียวกับข้อ 1 (โพสต์แล้ว · ในช่วงที่เลือก) · ใบที่ถูกกลับรายการแล้ว = นักบัญชีจัดการแล้ว ไม่ฟ้อง · 1 ข้อต่อ JE ต่อกฎ
        // T-7 (ฝ่ายค้านรอบ 200): JE ที่นักบัญชีปรับปรุงแล้ว (มี JE อื่นอ้างเลขนี้ในช่องเลขอ้างอิง) ไม่ฟ้องซ้ำ — หลักฐานจาก LegacyMoneyLegAudit.AdjustedBy ตัวเดียว
        var legacyCandidates = journals.Where(j => j.ReversedByEntryId == null && j.OriginalEntryId == null).ToList();
        var legacyNumbers = legacyCandidates.Select(j => j.EntryNumber).Distinct().ToList();
        var adjusting = legacyNumbers.Count == 0
            ? new List<Accounting.Helpers.LegacyMoneyLegAudit.AdjustingEntry>()
            : await _db.JournalEntries.AsNoTracking()
                .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.Status == JournalEntryStatus.Posted
                    && e.ReversedByEntryId == null && e.Reference != null && legacyNumbers.Contains(e.Reference.Trim()))
                .Select(e => new Accounting.Helpers.LegacyMoneyLegAudit.AdjustingEntry(e.Id, e.EntryNumber, e.Reference))
                .ToListAsync();
        foreach (var j in legacyCandidates)
        {
            if (Accounting.Helpers.LegacyMoneyLegAudit.AdjustedBy(j.Id, j.EntryNumber, adjusting) != null) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in j.Lines.Where(l => l.Account != null))
            {
                var isDebit = l.DebitAmount > 0m;
                if (Accounting.Helpers.LegacyMoneyLegAudit.Classify(j.Description, l.Description, l.Account.AccountCode, isDebit) is not { } f
                    || !seen.Add(f.RuleCode))
                    continue;
                anomalies.Add(new Anomaly(
                    f.RuleCode, "Warning",
                    $"{f.Message} · ผัง {l.Account.AccountCode} {l.Account.AccountName} ยอด {(isDebit ? l.DebitAmount : l.CreditAmount):N2}",
                    f.Fix, j.SourceDocumentId, null, j.Id, j.EntryNumber, j.EntryDate.Date));
            }
        }

        return new ScanResult(from, to, journals.Count, docs.Count,
            anomalies
                .OrderBy(a => a.Severity == "Error" ? 0 : 1)
                .ThenByDescending(a => a.EntryDate)
                .ToList());
    }
}
