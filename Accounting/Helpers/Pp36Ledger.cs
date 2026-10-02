using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>1 ใบ ภ.พ.36 พร้อมข้อเท็จจริงทุกชั้น (ตัวเอกสาร · GL ที่มีผล · รายการนำส่ง) และสถานะที่ตัดสินแล้ว</summary>
public sealed record Pp36DocRow(
    Guid Id, string DocumentNumber, DocumentType Type, DateTime PeriodDate, Guid ContactId,
    bool Owns, Pp36LedgerFacts Ledger, Pp36RemittanceDocument? Link, DateTime? ClaimAt, Pp36DocState State)
{
    /// <summary>ยอดที่ "นับ" เป็น ภ.พ.36 ของใบ (บาท) — ใบในรายการนำส่งใช้ยอดที่นำส่งจริง · ใบที่ยังไม่นำส่งใช้ Cr 21912 ใน GL ·
    /// ใบไม่มี JE / ไม่ใช่เจ้าของ = 0 (ไม่นับเงียบ — ผู้เรียกแสดงเป็นรายการ "ต้องซ่อม")</summary>
    public decimal CountedVat => State switch
    {
        Pp36DocState.AwaitingRemittance => Ledger.Pp36Payable,
        Pp36DocState.RemittedAwaitingRecognition or Pp36DocState.RemittedNoInputVat or Pp36DocState.Recognized
            => Link?.VatAmount ?? Ledger.Pp36Payable,
        _ => 0m,
    };
}

/// <summary>
/// <b>ตัวโหลดข้อเท็จจริง ภ.พ.36 จากฐาน — ตัวเดียวของทุกเส้น</b> (รอบ 203 ทีม F3 · PP36_REVIEW E-3/E-5/E-6/E-8)
///
/// <para>═══ ที่มา ═══ ยอดค้าง/นำส่ง/รับรู้/รายงาน ภ.พ.36 เคยคัดจาก "ธง + สถานะเอกสาร" อย่างเดียว ⇒ ใบที่อนุมัติแล้วแต่ไม่มี JE (P0-1/P0-2:
/// JE §83/6 ถูกด่านตีตก แต่สถานะ "อนุมัติ" ถูกบันทึก) ถูกนำส่ง Dr 21912 ที่ไม่เคยถูกตั้ง (21912 ติดเดบิต) และถูกรับรู้ Cr 11640 ที่ไม่เคยพัก ·
/// รับรู้ย้าย <c>VatAmount</c> ทั้งก้อนแม้บริษัทไม่จด VAT · ใบ USD นับยอดสกุลเอกสาร</para>
/// <para>═══ กติกา ═══ (1) GL ที่มีผล = JE ของใบเอง (<c>SourceDocumentId</c>) Posted ไม่ใช่คู่กลับรายการ — สูตรเดียวกับกระทบยอดภาษี-GL ·
/// (2) ชุดชนิด/ความเป็นเจ้าของจาก <see cref="ForeignServiceVat.OwnsPp36"/> · (3) งวดจาก <see cref="ForeignServiceVat.Pp36PeriodDate"/> ·
/// (4) สถานะจาก <see cref="Pp36Lifecycle.Classify"/> · CompanyId ทุกคิวรี</para>
/// </summary>
public static class Pp36Ledger
{
    private static readonly string[] UndueCodes = { ForeignServiceVat.Pp36InputVatCode, "11630" };

    /// <summary>ผังที่ภาษีซื้อ ภ.พ.36 พักอยู่ (11640 · 11630 สำรองเมื่อไม่มี 11640 — ตรงกับ AutoPost/รับรู้)</summary>
    public static bool IsUndueCode(string accountCode) => UndueCodes.Contains(accountCode);

    /// <summary>ข้อเท็จจริง GL ต่อใบ (บาท) — ใบที่ไม่มี JE ไม่อยู่ใน dictionary (= <c>default</c> ศูนย์ทั้งคู่)</summary>
    private static async Task<Dictionary<Guid, Pp36LedgerFacts>> LoadAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, CancellationToken ct = default)
    {
        if (docIds.Count == 0) return new();
        var ids = docIds.Distinct().ToList();
        var rows = await (
            from l in db.JournalEntryLines.AsNoTracking()
            join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
            join a in db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
            where j.CompanyId == companyId && a.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.OriginalEntryId == null && j.ReversedByEntryId == null
                && !j.IsDeleted && !l.IsDeleted
                && j.SourceDocumentId != null && ids.Contains(j.SourceDocumentId.Value)
                && (a.AccountCode == ForeignServiceVat.Pp36PayableCode || UndueCodes.Contains(a.AccountCode))
            select new { DocId = j.SourceDocumentId!.Value, a.AccountCode, l.DebitAmount, l.CreditAmount })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.DocId).ToDictionary(g => g.Key, g => new Pp36LedgerFacts(
            g.Where(r => r.AccountCode == ForeignServiceVat.Pp36PayableCode).Sum(r => r.CreditAmount - r.DebitAmount),
            g.Where(r => UndueCodes.Contains(r.AccountCode)).Sum(r => r.DebitAmount - r.CreditAmount)));
    }

    /// <summary>แถวผูกรายการนำส่งที่ยังไม่ลบของใบชุดนี้</summary>
    private static async Task<Dictionary<Guid, Pp36RemittanceDocument>> LinksAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, CancellationToken ct = default)
    {
        if (docIds.Count == 0) return new();
        var ids = docIds.Distinct().ToList();
        return (await db.Pp36RemittanceDocuments.AsNoTracking()
                .Where(x => x.CompanyId == companyId && !x.IsDeleted && ids.Contains(x.DocumentId))
                .ToListAsync(ct))
            .GroupBy(x => x.DocumentId).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>
    /// ใบที่ "เป็นเจ้าของหนี้ ภ.พ.36" ทุกใบ (<c>ForeignServiceVat.OwnsPp36Query</c> · ออกแล้วไม่ยกเลิก) ในช่วงงวด ภ.พ.36 [from, toExclusive) พร้อมสถานะ ·
    /// null = ไม่จำกัดฝั่งนั้น · ใบสำคัญจ่ายที่ปิดหนี้ใบต้นทาง (สืบทอดธงจากใบต้นทาง) ไม่อยู่ในผล
    /// </summary>
    public static Task<List<Pp36DocRow>> LoadDocsAsync(
        AccountingDbContext db, Guid companyId, DateTime? fromInclusive, DateTime? toExclusive, CancellationToken ct = default)
        => LoadDocsCoreAsync(db, companyId, fromInclusive, toExclusive, null, ct);

    /// <summary>เหมือน <see cref="LoadDocsAsync"/> แต่คัดตาม id (ไม่ดูช่วงงวด) — ใบที่ไม่ได้ติ๊กธง/ยังไม่ออก/ยกเลิก ไม่อยู่ในผล</summary>
    private static Task<List<Pp36DocRow>> LoadDocsByIdsAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, CancellationToken ct = default)
        => docIds.Count == 0
            ? Task.FromResult(new List<Pp36DocRow>())
            : LoadDocsCoreAsync(db, companyId, null, null, docIds.Distinct().ToList(), ct);

    private static async Task<List<Pp36DocRow>> LoadDocsCoreAsync(
        AccountingDbContext db, Guid companyId, DateTime? fromInclusive, DateTime? toExclusive, List<Guid>? onlyIds,
        CancellationToken ct)
    {
        // เจ้าของหนี้ ภ.พ.36 = predicate ตัวเดียวของทั้งระบบ (ForeignServiceVat.OwnsPp36Query — ทีม F2 · ตรงกับ JE ที่ Cr 21912 จริง:
        // PI/Expense ที่ติ๊ก + PV ที่ไม่ปิดหนี้ใบต้นทาง + VAT > 0) · PV ที่ปิดหนี้ใบต้นทางสืบทอดธงมา (F2) แต่ไม่ใช่เจ้าของ ⇒ ไม่นับซ้ำ
        var notIssued = DocumentStatusRules.NotIssued;
        var q = db.Documents.AsNoTracking()
            .Where(ForeignServiceVat.OwnsPp36Query)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && !notIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided);
        if (onlyIds != null) q = q.Where(d => onlyIds.Contains(d.Id));
        if (fromInclusive is DateTime f) q = q.Where(d => (d.PaymentDate ?? d.DocumentDate) >= f);
        if (toExclusive is DateTime t) q = q.Where(d => (d.PaymentDate ?? d.DocumentDate) < t);
        var docs = await q.Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.PaymentDate, d.DocumentDate, d.ContactId,
                d.IsForeignService, d.VatAmount, d.RelatedDocumentId, d.InputVatBecameClaimableAt,
            })
            .ToListAsync(ct);
        if (docs.Count == 0) return new();

        var ids = docs.Select(d => d.Id).ToList();
        var ledger = await LoadAsync(db, companyId, ids, ct);
        var links = await LinksAsync(db, companyId, ids, ct);

        return docs.Select(d =>
        {
            var owns = ForeignServiceVat.OwnsPp36(d.DocumentType, d.IsForeignService, d.VatAmount, d.RelatedDocumentId.HasValue);
            var facts = ledger.GetValueOrDefault(d.Id);
            var link = links.GetValueOrDefault(d.Id);
            var recognized = link?.RecognizedJournalEntryId != null || d.InputVatBecameClaimableAt != null;
            var state = Pp36Lifecycle.Classify(owns, DocumentStatusRules.IsEffective(d.Status), facts, link != null, recognized);
            return new Pp36DocRow(d.Id, d.DocumentNumber, d.DocumentType,
                ForeignServiceVat.Pp36PeriodDate(d.PaymentDate, d.DocumentDate), d.ContactId,
                owns, facts, link, d.InputVatBecameClaimableAt, state);
        }).ToList();
    }

    /// <summary>
    /// <b>ด่าน "แก้ใบหลังนำส่ง/รับรู้"</b> (คำตัดสินข้อ 134) — คืนข้อความบล็อกต่อใบ (ไม่มี = ผ่าน) · ใบที่อยู่ในรายการนำส่ง หรือรับรู้ภาษีซื้อแล้ว
    /// (ข้อมูลเก่าก่อนมีตารางผูก: ธงบริการต่างประเทศ + <c>InputVatBecameClaimableAt</c>) · ใช้ร่วมกันทุกทางเข้า (ยกเลิกใบ · ยกเลิกการลงบัญชีรอบโอน ·
    /// ปลดธง · แก้ยอด)
    /// </summary>
    /// <param name="action">คำกริยาต้นข้อความ เช่น "ยกเลิก" · "ปลดธงบริการต่างประเทศของ" · "แก้ยอด"</param>
    public static async Task<Dictionary<Guid, string>> ChangeBlocksAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, string action, CancellationToken ct = default)
        => (await RemittedStatusAsync(db, companyId, docIds, ct))
            .ToDictionary(kv => kv.Key, kv => Pp36Lifecycle.ChangeBlockMessage(kv.Value.DocumentNumber, action, kv.Value));

    /// <summary>
    /// <b>"ใบนี้นำส่ง ภ.พ.36 แล้ว/รับรู้แล้วหรือยัง" — ตัวตรวจเดียว</b> ของด่านยกเลิก · ปลดธง · ปรับยอด · ยกเลิกรอบโอน (F3) และด่านใบลด/เพิ่มหนี้ (F2 E-7 ·
    /// <c>DocumentService.Pp36SettledReasonAsync</c>) · ตัดสินต่อใบจากตาราง <c>Pp36RemittanceDocuments</c> (คำตัดสินข้อ 133) — ไม่ใช่ "งวดนี้มีการนำส่ง"
    /// (ใบที่อนุมัติหลังนำส่งยังไม่ถูกนับ ⇒ ยังแก้ได้) · ข้อมูลก่อนตารางผูก: รับรู้แล้ว (<c>InputVatBecameClaimableAt</c>) หรือมีเลขใบเสร็จ RD บนใบ
    /// </summary>
    public static async Task<Dictionary<Guid, Pp36RemitStatus>> RemittedStatusAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, Pp36RemitStatus>();
        if (docIds.Count == 0) return result;
        var ids = docIds.Distinct().ToList();
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id) && d.IsForeignService)
            .Select(d => new { d.Id, d.DocumentNumber, d.InputVatBecameClaimableAt, d.PaymentDate, d.DocumentDate,
                d.Pp36RdReceiptDate, d.Pp36RdReceiptNumber })
            .ToListAsync(ct);
        if (docs.Count == 0) return result;
        var links = await LinksAsync(db, companyId, docs.Select(d => d.Id).ToList(), ct);
        var remitIds = links.Values.Select(l => l.StatutoryRemittanceId).Distinct().ToList();
        var remits = remitIds.Count == 0
            ? new Dictionary<Guid, DateTime>()
            : await db.StatutoryRemittances.AsNoTracking()
                .Where(r => r.CompanyId == companyId && remitIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.PayDate, ct);
        foreach (var d in docs)
        {
            var link = links.GetValueOrDefault(d.Id);
            var recognized = link?.RecognizedJournalEntryId != null || d.InputVatBecameClaimableAt != null;
            if (link == null && !recognized && string.IsNullOrWhiteSpace(d.Pp36RdReceiptNumber)) continue;
            var period = ForeignServiceVat.Pp36PeriodDate(d.PaymentDate, d.DocumentDate);
            result[d.Id] = new Pp36RemitStatus(d.DocumentNumber,
                link?.PeriodYear ?? period.Year, link?.PeriodMonth ?? period.Month,
                link != null && remits.TryGetValue(link.StatutoryRemittanceId, out var paid) ? paid : (d.Pp36RdReceiptDate ?? period),
                recognized, d.Pp36RdReceiptNumber);
        }
        return result;
    }

    /// <summary>สถานะ + ป้ายของใบชุดหนึ่ง (หน้ารายการ/รายละเอียดเอกสาร) — ใบที่ไม่เกี่ยวกับ ภ.พ.36 ไม่อยู่ใน dictionary</summary>
    public static async Task<Dictionary<Guid, (Pp36DocState State, string? Label)>> StatesAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> docIds, CancellationToken ct = default)
    {
        if (docIds.Count == 0) return new();
        var ids = docIds.Distinct().ToList();
        var flagged = await db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id) && d.IsForeignService)
            .Select(d => d.Id).ToListAsync(ct);
        if (flagged.Count == 0) return new();
        // ใช้ตัวโหลดเดียวกับหน้านำส่ง (ไม่จำกัดช่วงงวด) แล้วคัดเฉพาะใบในหน้า — กติกาไม่มีสำเนาที่สอง
        var rows = await LoadDocsByIdsAsync(db, companyId, flagged, ct);
        return rows.Where(r => r.State != Pp36DocState.NotApplicable)
            .ToDictionary(r => r.Id, r => (r.State, Pp36Lifecycle.Label(r.State, r.ClaimAt)));
    }
}
