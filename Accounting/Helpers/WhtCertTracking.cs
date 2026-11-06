using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND §4.1–§4.5) — <b>ตัวโหลดข้อเท็จจริงเดียว</b>ของสถานะ 50 ทวิ (แถวทะเบียน + กล่องรับ) แล้วส่งให้
/// <see cref="WhtCertIntakeStatus.Of"/> ตัดสิน · ผู้เรียก: รายการ/หน้าเอกสาร (<c>DocumentService</c>) · ทะเบียน (<c>WhtCreditService.ListAsync</c>) ·
/// การ์ดแดชบอร์ด/ผู้ติดต่อ + รายงานปลายปี (<c>WhtCertIntakeService</c>) — query คงที่ 2 ครั้งต่อการเรียก (ไม่ N+1) · ทุก query ผูก CompanyId
/// </summary>
public static class WhtCertTracking
{
    /// <summary>ชนิดเอกสารขายที่ "คาดว่าจะถูกหัก" ได้ (ตัดสิน NotYetPaid เมื่อยังไม่มีแถวทะเบียน) — ฝั่งซื้อมี WHT ที่<b>เราหัก</b> ไม่ใช่เรื่องนี้</summary>
    public static readonly IReadOnlyList<DocumentType> ExpectedWhtTypes = new[]
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.DebitNote,
    };

    /// <summary>ชนิดที่ "ชำระใบอื่น" — ไม่มีแถวของตัวเอง ⇒ แสดงสถานะของใบต้นทาง (RelatedDocumentId) · resolver เดียวของรายการ/หน้าเอกสาร</summary>
    public static readonly IReadOnlyList<DocumentType> InheritFromSourceTypes = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher };

    /// <summary>ชนิดที่ตั้ง 11910 ได้ตอนออกใบ (เกณฑ์สิทธิ์) — ยังไม่รับเงิน ⇒ ยังไม่ใช่การค้างของลูกค้า</summary>
    private static readonly DocumentType[] InvoiceLike = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote };

    public sealed record Row(
        Guid CreditId, Guid? DocumentId, string? DocumentNumber, DateTime? DocumentDate,
        Guid? PayerContactId, string PayerName, string? PayerTaxId, int TaxYear,
        decimal WhtAmount, decimal ReceivedAmount, WhtCreditStatus Status, decimal IncomeAmount,
        DateTime? WithheldSince, WhtCertIntakeStatus.Verdict Verdict, string? LastChannel);

    /// <param name="documentIds">จำกัดเฉพาะแถวที่ผูกเอกสารเหล่านี้ (null = ไม่จำกัด)</param>
    /// <param name="contactId">จำกัดผู้จ่ายรายนี้ (null = ทุกราย)</param>
    /// <param name="taxYear">จำกัดปีภาษี (null = ทุกปี)</param>
    /// <param name="creditIds">จำกัดแถวเหล่านี้ (null = ไม่จำกัด)</param>
    public static async Task<List<Row>> LoadAsync(AccountingDbContext db, Guid companyId, DateTime nowUtc,
        IReadOnlyCollection<Guid>? documentIds = null, Guid? contactId = null, int? taxYear = null,
        IReadOnlyCollection<Guid>? creditIds = null, CancellationToken ct = default)
    {
        var q = db.WhtCreditsReceived.AsNoTracking().Where(w => w.CompanyId == companyId);
        if (documentIds != null) q = q.Where(w => w.DocumentId != null && documentIds.Contains(w.DocumentId.Value));
        if (contactId != null) q = q.Where(w => w.PayerContactId == contactId);
        if (taxYear != null) q = q.Where(w => w.TaxYear == taxYear.Value);
        if (creditIds != null) q = q.Where(w => creditIds.Contains(w.Id));
        var rows = await q.Select(w => new
        {
            w.Id, w.DocumentId, w.PayerContactId, w.PayerName, w.PayerTaxId, w.TaxYear, w.WhtAmount, w.ReceivedAmount,
            w.Status, w.EvidenceKind, w.FirstWithheldAt, w.CreatedAt, w.IncomeAmount,
            Doc = db.Documents.Where(d => d.Id == w.DocumentId && d.CompanyId == companyId)
                .Select(d => new { d.DocumentNumber, d.DocumentDate, d.DocumentType, d.PaidAmount }).FirstOrDefault(),
        }).ToListAsync(ct);
        if (rows.Count == 0) return new List<Row>();

        var ids = rows.Select(r => r.Id).ToList();
        var links = await (from a in db.WhtCertAllocations.AsNoTracking()
                           join i in db.WhtCertIntakes.AsNoTracking() on a.IntakeId equals i.Id
                           where a.CompanyId == companyId && i.CompanyId == companyId && ids.Contains(a.WhtCreditId)
                           select new { a.WhtCreditId, a.Status, IntakeStatus = i.Status, i.CreatedAt, i.Channel })
            .ToListAsync(ct);
        var byCredit = links.GroupBy(l => l.WhtCreditId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).ToList());

        var result = new List<Row>(rows.Count);
        foreach (var r in rows)
        {
            var l = byCredit.TryGetValue(r.Id, out var list) ? list : null;
            var pendingIntake = l?.Any(x => x.Status == WhtCertAllocationStatus.Proposed && x.IntakeStatus == WhtCertIntakeRecordStatus.AwaitingConfirm) == true;
            var lastRejected = l is { Count: > 0 } && l[0].IntakeStatus == WhtCertIntakeRecordStatus.Rejected;
            // ยังไม่รับเงิน: ใบแจ้งหนี้/ใบกำกับที่ตั้ง 11910 ตอนออกใบ (เกณฑ์สิทธิ์) และยังไม่มีการชำระ
            var unpaid = r.Doc != null && InvoiceLike.Contains(r.Doc.DocumentType) && r.Doc.PaidAmount <= 0.005m;
            DateTime? since = unpaid ? null : (r.FirstWithheldAt ?? r.CreatedAt);
            var verdict = WhtCertIntakeStatus.Of(new WhtCertIntakeStatus.Facts(
                r.WhtAmount, 0m, r.ReceivedAmount, r.Status, pendingIntake, lastRejected, r.EvidenceKind, since), nowUtc);
            result.Add(new Row(r.Id, r.DocumentId, r.Doc?.DocumentNumber, r.Doc?.DocumentDate, r.PayerContactId, r.PayerName, r.PayerTaxId,
                r.TaxYear, r.WhtAmount, r.ReceivedAmount, r.Status, r.IncomeAmount, since, verdict, l is { Count: > 0 } ? l[0].Channel.ToString() : null));
        }
        return result;
    }

    /// <summary>สถานะ 50 ทวิ ต่อเอกสารของหน้า (รายการ/รายละเอียด) — แถวของตัวเองก่อน · ใบเสร็จที่ไม่มีแถวของตัวเองใช้ของใบต้นทาง ·
    /// ไม่มีแถวเลย + เป็นเอกสารขายที่คาดว่าจะถูกหัก ⇒ NotYetPaid · อื่น ๆ ⇒ NotApplicable · ทุกใบในหน้าได้คำตอบ (ไม่มี "ไม่ได้ตรวจ" ในเส้นนี้)</summary>
    public static async Task<Dictionary<Guid, WhtCertIntakeStatus.Verdict>> StatesForDocumentsAsync(
        AccountingDbContext db, Guid companyId, IReadOnlyCollection<(Guid Id, Guid? RelatedId, DocumentType Type, DocumentStatus Status, decimal ExpectedWht)> docs,
        DateTime nowUtc, CancellationToken ct = default)
    {
        var lookup = docs.Select(d => d.Id)
            .Concat(docs.Where(d => d.RelatedId != null && InheritFromSourceTypes.Contains(d.Type)).Select(d => d.RelatedId!.Value))
            .Distinct().ToList();
        var rows = lookup.Count == 0 ? new List<Row>() : await LoadAsync(db, companyId, nowUtc, documentIds: lookup, ct: ct);
        var byDoc = rows.Where(r => r.DocumentId != null).GroupBy(r => r.DocumentId!.Value).ToDictionary(g => g.Key, g => g.First().Verdict);
        var result = new Dictionary<Guid, WhtCertIntakeStatus.Verdict>();
        foreach (var d in docs)
        {
            if (byDoc.TryGetValue(d.Id, out var own)) { result[d.Id] = own; continue; }
            if (d.RelatedId is Guid rid && InheritFromSourceTypes.Contains(d.Type) && byDoc.TryGetValue(rid, out var src)) { result[d.Id] = src; continue; }
            var expected = ExpectedWhtTypes.Contains(d.Type) && !DocumentStatusRules.NotIssued.Contains(d.Status) && d.Status != DocumentStatus.Voided
                ? d.ExpectedWht : 0m;
            result[d.Id] = WhtCertIntakeStatus.Of(new WhtCertIntakeStatus.Facts(0m, expected, 0m, null, false, false,
                WhtEvidenceKind.PaperCertificate, null), nowUtc);
        }
        return result;
    }
}
