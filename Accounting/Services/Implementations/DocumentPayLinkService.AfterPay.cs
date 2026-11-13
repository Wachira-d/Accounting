using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P4 — <b>ส่วน "หลังจ่าย" ของหน้าเอกสารสำหรับลูกค้า</b> (คำตัดสิน 267-1-45…267-1-50 · DOCUMENT_FLOW §2.12n-P4) ·
/// ทุกข้อความ/สถานะ/การตัดสินอยู่ที่ <see cref="CustomerDocPageAfterPay"/> — ที่นี่โหลดข้อเท็จจริงแล้วส่งต่อ
///
/// <para><b>อ่านอย่างเดียว</b> ยกเว้นทางเดียว: <see cref="RelatedPageLinkAsync"/> บันทึก "คำขอลิงก์ใหม่" ของใบที่เกี่ยวข้องเมื่อยังไม่มีลิงก์ที่ใช้ได้
/// (audit · <b>ไม่ออก token</b> ให้ผู้ถือลิงก์ — ฝ่ายค้าน MED-2) · <b>ไม่ออกใบเสร็จ · ไม่ยืนยันสลิป · ไม่บันทึกรับชำระ</b> จากไฟล์นี้ (ใบเสร็จมาจากงานหลังรับชำระ/พนักงาน —
/// ล็อกด้วย <c>tools/required_call_site_check.py</c> กติกา R267d) · ทุก query ผูก CompanyId + ผู้ติดต่อของใบ</para>
/// </summary>
public partial class DocumentPayLinkService
{
    /// <summary>ข้อเท็จจริงของใบเสร็จที่ "อาจ" เป็นของใบนี้ — ตัวโหลดเดียวของรายการใบเสร็จบนหน้าและข้อความ "ยังไม่มีใบเสร็จ"</summary>
    private sealed record ReceiptCandidate(Guid Id, DocumentType DocumentType, DocumentStatus Status, string DocumentNumber, DateTime DocumentDate,
        decimal TotalAmount, decimal BalanceDue, bool? CnDnPurchaseSideOverride, bool Owned);

    /// <summary>
    /// รอบ 267 P4 ฝ่ายค้าน MED-1 (267-1-46): ใบเสร็จที่ออกจากการรับชำระซึ่ง<b>เป็นของใบนี้</b> — การชำระที่มีผลกับใบนี้
    /// (<c>MovedToDocumentId ?? DocumentId</c>) ∪ การชำระที่<b>จัดสรร</b>ให้ใบนี้ (<c>PaymentAllocations.DocumentId</c> ที่ยังไม่ถอน) ·
    /// ความเป็นเจ้าของตัดสินที่ <see cref="CustomerDocPageAfterPay.ReceiptBelongsTo"/> ตัวเดียว (ใบเสร็จของใบอื่นในการชำระเดียวกัน = ไม่ใช่ของใบนี้) ·
    /// คืนทุกใบที่ครอบการชำระ พร้อมธง <c>Owned</c> — ใบที่ไม่ใช่ของใบนี้ใช้บอกว่า "มีใบเสร็จแล้ว" (ไม่ขึ้นข้อความ "ยังไม่ออก" ที่ไม่จริง) เท่านั้น ไม่แสดง/ไม่ให้ดาวน์โหลด
    /// </summary>
    private async Task<List<ReceiptCandidate>> ReceiptCandidatesAsync(Guid companyId, DocFacts d, CancellationToken ct)
    {
        var empty = new List<ReceiptCandidate>();
        if (!CustomerDocPage.IsPayableType(d.Type)) return empty;
        var docId = d.Id;
        var contactId = d.ContactId;
        var payments = await _db.Payments.AsNoTracking()
            // รอบ 266 B: ใบที่มีผลของการรับชำระ = (MovedToDocumentId ?? DocumentId) — PaymentEffectiveDocument
            .Where(p => p.CompanyId == companyId && (p.MovedToDocumentId ?? p.DocumentId) == docId && !p.IsDeleted)
            .Select(p => new { p.Id, p.ReceiptDocumentId })
            .ToListAsync(ct);
        // การชำระที่บันทึกบนใบอื่นแต่จัดสรรมาให้ใบนี้ (ฝั่ง Y ของการชำระ X/Y/Z)
        var allocatedIn = await _db.PaymentAllocations.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.DocumentId == docId && a.ReleasedAt == null && !a.IsDeleted)
            .Select(a => a.PaymentId).Distinct().ToListAsync(ct);
        var paymentIds = payments.Select(p => p.Id).Concat(allocatedIn).Distinct().ToList();
        if (paymentIds.Count == 0) return empty;
        var ownPaymentIds = payments.Select(p => p.Id).ToList();
        // การชำระของใบนี้ที่มีการจัดสรรไปใบอื่นด้วย ⇒ ReceiptDocumentId ของมันอาจครอบหลายใบ (ไม่ถือเป็นของใบนี้ เว้นผูก RelatedDocumentId)
        var spreadElsewhere = (await _db.PaymentAllocations.AsNoTracking()
                .Where(a => a.CompanyId == companyId && ownPaymentIds.Contains(a.PaymentId) && a.DocumentId != docId && a.ReleasedAt == null && !a.IsDeleted)
                .Select(a => a.PaymentId).Distinct().ToListAsync(ct)).ToHashSet();
        var ownReceiptIds = payments.Where(p => p.ReceiptDocumentId != null && !spreadElsewhere.Contains(p.Id))
            .Select(p => p.ReceiptDocumentId!.Value).ToHashSet();
        var allReceiptIds = payments.Where(p => p.ReceiptDocumentId != null).Select(p => p.ReceiptDocumentId!.Value).Distinct().ToList();
        var rows = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.ContactId == contactId && x.Id != docId
                && (allReceiptIds.Contains(x.Id) || (x.SettlementPaymentId != null && paymentIds.Contains(x.SettlementPaymentId.Value)))
                && x.Status != DocumentStatus.Draft && x.Status != DocumentStatus.WaitingApproval
                && x.Status != DocumentStatus.Rejected && x.Status != DocumentStatus.Voided)
            .OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).Take(MaxRelated)
            .Select(x => new { x.Id, x.DocumentType, x.Status, x.DocumentNumber, x.DocumentDate, x.TotalAmount, x.BalanceDue, x.CnDnPurchaseSideOverride, x.RelatedDocumentId })
            .ToListAsync(ct);
        return rows.Select(x => new ReceiptCandidate(x.Id, x.DocumentType, x.Status, x.DocumentNumber, x.DocumentDate, x.TotalAmount, x.BalanceDue,
                x.CnDnPurchaseSideOverride, CustomerDocPageAfterPay.ReceiptBelongsTo(docId, x.RelatedDocumentId, ownReceiptIds.Contains(x.Id))))
            .ToList();
    }

    /// <summary>
    /// ใบเสร็จ/ใบกำกับภาษีที่ออกจาก<b>การรับชำระของใบนี้</b> (ตัวโหลด <see cref="ReceiptCandidatesAsync"/>) — บริษัทเดียวกัน + ผู้ติดต่อเดียวกัน +
    /// ออกแล้วยังมีผล + ลูกค้าเห็นได้ + <b>เป็นของใบนี้</b> (การชำระที่กระจายหลายใบ: แต่ละใบเห็นเฉพาะใบเสร็จของตัวเอง) · ขึ้นทันทีที่ออก (ไม่รอส่งอีเมล)
    /// </summary>
    private async Task<List<CustomerDocPageRelated>> PaymentReceiptsAsync(Guid companyId, DocFacts d, PageRow row, bool isEnglish, CancellationToken ct)
    {
        var result = new List<CustomerDocPageRelated>();
        var rows = (await ReceiptCandidatesAsync(companyId, d, ct)).Where(c => c.Owned).ToList();
        if (rows.Count == 0) return result;
        var ids = rows.Select(r => r.Id).ToList();
        // ป้าย e-Tax เฉพาะใบที่ส่งทางอีเมล e-Tax แล้วจริง (ยังไม่ส่ง = ไม่มีป้าย — ไม่อ้างสิ่งที่ยังไม่เกิด)
        var etaxSent = (await _db.EtaxInvoices.AsNoTracking()
                .Where(e => e.CompanyId == companyId && !e.IsDeleted && ids.Contains(e.DocumentId) && e.VoidedAt == null && e.ByEmailSentAt != null)
                .Select(e => new { e.DocumentId, e.ByEmailSentAt }).ToListAsync(ct))
            .GroupBy(e => e.DocumentId).ToDictionary(g => g.Key, g => g.Min(x => x.ByEmailSentAt));
        foreach (var r in rows)
        {
            if (!DocumentStatusRules.IsEffective(r.Status)) continue;
            if (!DocumentPayLinkPolicy.IsCustomerFacing(r.DocumentType, d.Type, r.CnDnPurchaseSideOverride, row.ContactSupplierOnly, row.ContactCustomerOnly))
                continue;
            string title;
            try { title = (await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, r.Id)).Title; }
            catch (KeyNotFoundException) { continue; }
            result.Add(new CustomerDocPageRelated(r.Id, CustomerDocPageAfterPay.KindReceipt, CustomerDocPageAfterPay.ReceiptKindLabel(isEnglish), title,
                r.DocumentNumber, r.DocumentDate, r.TotalAmount, null,
                CustomerDocPage.StatusLabel(isEnglish, r.DocumentType, r.Status, r.BalanceDue),
                Note: CustomerDocPageAfterPay.EtaxSentNote(isEnglish, etaxSent.TryGetValue(r.Id, out var sent) ? sent : null)));
        }
        return result;
    }

    /// <summary>รวมใบเสร็จเข้าชุดใบที่เกี่ยวข้อง — ใบที่อยู่ในชุดแล้ว (เป็นใบลูกของใบนี้) เปลี่ยนเป็นแถวใบเสร็จ (ไม่ซ้ำ) · ใบเสร็จขึ้นก่อน</summary>
    private static void MergeReceipts(List<CustomerDocPageRelated> related, List<CustomerDocPageRelated> receipts)
    {
        if (receipts.Count == 0) return;
        var ids = receipts.Select(r => r.RelId).ToHashSet();
        related.RemoveAll(r => ids.Contains(r.RelId));
        related.InsertRange(0, receipts);
    }

    /// <summary>
    /// ส่วนหลังจ่ายของหน้า — โหมดลิงก์ของใบและพอร์ทัลเท่านั้น (การ์ด LINE/พรีวิว = ไม่มี) · ลิงก์ชำระรุ่นก่อน (payOnly) = สถานะสลิปอย่างเดียว
    /// (ขอบเขตเดิมของลิงก์ชำระ — 267-1-30) · การ์ด 50 ทวิ = โหมดลิงก์ (ส่งไฟล์ด้วย token ของใบ)
    /// </summary>
    private async Task<CustomerDocPageAfterPayView?> AfterPayAsync(Guid companyId, DocFacts d, PageRow row, CustomerDocPage.Mode mode, bool isEnglish,
        bool payOnly, IReadOnlyList<CustomerDocPageRelated> related, CancellationToken ct)
    {
        if (mode != CustomerDocPage.Mode.Link && mode != CustomerDocPage.Mode.Portal) return null;
        var slips = new List<CustomerDocPageSlip>();
        var slipPending = false;
        if (CustomerDocPage.IsPayableType(d.Type))
        {
            var docId = d.Id;
            var rows = await _db.PaymentIntents.AsNoTracking()
                .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && i.SourceId == docId
                    && i.MethodKind == PaymentMethodKind.ManualSlip && !i.IsDeleted)
                .OrderByDescending(i => i.CreatedAt).Take(CustomerDocPageAfterPay.MaxSlips)
                .Select(i => new { i.CreatedAt, i.Amount, i.Status, i.FailureCode, i.CustomerVisibleRejectReason })
                .ToListAsync(ct);
            foreach (var s in rows)
            {
                var v = CustomerDocPageAfterPay.SlipState(isEnglish, s.Status, s.FailureCode, s.CustomerVisibleRejectReason);
                slips.Add(new CustomerDocPageSlip(s.CreatedAt, s.Amount, v.State, v.Label, v.Note));
            }
            // "มีสลิปรอตรวจ" ต้องดูทั้งใบ (ไม่ใช่แค่ชุดล่าสุดที่แสดง) — ตัวเดียวกับด่านของเส้นรับสลิป (PaymentIntentPolicy.OpenStatuses)
            var open = PaymentIntentPolicy.OpenStatuses;
            slipPending = await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId
                && i.SourceKind == PaymentSourceKind.Document && i.SourceId == docId
                && i.MethodKind == PaymentMethodKind.ManualSlip && !i.IsDeleted && open.Contains(i.Status), ct);
        }
        string? receiptNote = null;
        CustomerDocPageAfterPay.WhtCertView? wht = null;
        if (!payOnly)
        {
            var shownReceipts = related.Count(r => r.Kind == CustomerDocPageAfterPay.KindReceipt);
            receiptNote = CustomerDocPageAfterPay.ReceiptPendingNote(isEnglish, d.Type, d.Status, row.PaidAmount, shownReceipts);
            // ฝ่ายค้าน MED-1: มีใบเสร็จที่ครอบการชำระของใบนี้อยู่แล้ว (แต่เป็นใบเสร็จรวม/ของใบอื่น) ⇒ ไม่บอกว่า "ผู้ขายยังไม่ได้ออก" (ไม่จริง)
            if (receiptNote != null && (await ReceiptCandidatesAsync(companyId, d, ct)).Any(c => DocumentStatusRules.IsEffective(c.Status)))
                receiptNote = null;
            if (mode == CustomerDocPage.Mode.Link)
            {
                // ตัวโหลด/ตัวตัดสินเดียวกับหน้าอัปโหลด 50 ทวิ เดิม (ทะเบียนภาษีถูกหัก — แถวที่ยังตามใบของผู้ติดต่อนี้)
                var item = (await WhtCertUploadLinkService.ItemsForContactAsync(_db, companyId, d.ContactId, d.Id, ct))
                    .FirstOrDefault(i => i.DocumentId == d.Id);
                wht = CustomerDocPageAfterPay.WhtCert(isEnglish, item != null, item?.Submitted == true, item?.Missing ?? 0m);
            }
        }
        return new CustomerDocPageAfterPayView(slips, slipPending, receiptNote, wht, CustomerDocPageAfterPay.Labels(isEnglish));
    }

    /// <summary>
    /// ปุ่ม "ชำระใบนี้" ของใบที่เกี่ยวข้อง — รอบ 267 P4 ฝ่ายค้าน MED-2 (คำตัดสินผู้ประสานงาน · 267-1-47): <b>ไม่ออก token ให้ผู้ถือลิงก์ (ไม่ล็อกอิน)</b> ·
    /// คืนเฉพาะลิงก์ของใบนั้นที่ผู้ขาย<b>ออกไว้แล้วและยังใช้ได้</b> (ถอดจากฉบับเข้ารหัส — ไม่เขียนอะไร) · หมดอายุ/ไม่เคยออก ⇒ เส้น "ขอลิงก์ใหม่" ของใบนั้น
    /// (<see cref="RecordLinkRequestAsync"/> — audit + กระดิ่งพนักงาน · ≤ 1 ครั้ง/ใบ/วัน) · ผู้ขายยกเลิกไว้ ⇒ บอกให้ติดต่อผู้ขาย (ไม่ล้างการยกเลิก · ไม่กดกระดิ่ง)
    /// </summary>
    public async Task<RelatedPageLinkResult> RelatedPageLinkAsync(Guid companyId, string token, Guid relatedId, CancellationToken ct = default)
    {
        var notFound = new RelatedPageLinkResult(false, null, null, null);
        // ด่านเดียวกับการเปิดหน้า (token · เพิกถอน/หมดอายุ · ลูกค้าเห็นได้) แล้วต้องเป็นใบในชุดที่หน้าแสดง<b>และ</b>หน้าเสนอปุ่มจ่ายของใบนั้นจริง
        var view = await PublicViewAsync(companyId, token, ct);
        if (view?.Page is not { Viewable: true } page) return notFound;
        if (page.Related.FirstOrDefault(r => r.RelId == relatedId) is not { PayOffered: true }) return notFound;
        var rel = await LoadDocAsync(companyId, relatedId, null, ct);
        if (rel == null) return notFound;
        if (rel.PayLinkRevokedAt != null)
            return new RelatedPageLinkResult(true, null, CustomerDocPageAfterPay.RelatedLinkRefusedMessage(page.IsEnglish), null);
        var existing = // ตัวตัดสิน "ลิงก์ยังใช้ได้" ตัวเดียวกับตัวออกลิงก์ (ระดับหน้าเอกสาร — ไม่ผูกใบกำกับที่อ้าง)
            DocumentPayLinkPolicy.LinkUsable(rel.PayLinkTokenHash, rel.PayLinkExpiresAt, rel.PayLinkRevokedAt, DateTime.UtcNow, null)
            ? Recover(rel.PayLinkTokenProtected, rel.PayLinkTokenHash) : null;
        if (existing != null)
            return new RelatedPageLinkResult(true, $"{DocumentPayLinkPolicy.PagePath}?c={companyId:D}&t={Uri.EscapeDataString(existing)}", null, null);
        // ไม่มีลิงก์ที่ใช้ได้ ⇒ ขอให้ผู้ขายส่ง (ไม่ออก token เอง) — ผู้เรียกแจ้งพนักงานเมื่อบันทึกคำขอใหม่
        var req = await RecordLinkRequestAsync(companyId, rel, "related:" + (view.Target?.SourceId ?? Guid.Empty).ToString("N"), ct);
        if (!req.Found) return new RelatedPageLinkResult(true, null, CustomerDocPageAfterPay.RelatedLinkRefusedMessage(page.IsEnglish), null);
        return new RelatedPageLinkResult(true, null, CustomerDocPageAfterPay.RelatedLinkRequestedMessage(page.IsEnglish, req.Recorded), req);
    }

    public async Task<(Guid DocumentId, Guid ContactId)?> WhtCertTargetAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        var view = await PublicViewAsync(companyId, token, ct);
        if (view?.Page is not { Viewable: true } page) return null;
        if (page.AfterPay?.WhtCert is not { CanUpload: true }) return null;
        return view.Target.ContactId is Guid contactId ? (view.Target.SourceId, contactId) : null;
    }
}
