using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 267 แพ็ก P4 — <b>ส่วน "หลังจ่าย" ของหน้าเอกสารสำหรับลูกค้า</b> (คำตัดสิน 267-1-41…267-1-46 · DOCUMENT_FLOW §2.12n-P4) ·
/// ทุกข้อความ/สถานะ/การตัดสินอยู่ที่ <see cref="CustomerDocPageAfterPay"/> — ที่นี่โหลดข้อเท็จจริงแล้วส่งต่อ
///
/// <para><b>อ่านอย่างเดียว</b> ยกเว้นทางเดียว: <see cref="RelatedPageLinkAsync"/> ออกลิงก์หน้าเอกสารของใบที่เกี่ยวข้อง (ผ่านตัวออกเดียว
/// <see cref="EnsureCustomerLinkAsync"/>) · <b>ไม่ออกใบเสร็จ · ไม่ยืนยันสลิป · ไม่บันทึกรับชำระ</b> จากไฟล์นี้ (ใบเสร็จมาจากงานหลังรับชำระ/พนักงาน —
/// ล็อกด้วย <c>tools/required_call_site_check.py</c> กติกา R267d) · ทุก query ผูก CompanyId + ผู้ติดต่อของใบ</para>
/// </summary>
public partial class DocumentPayLinkService
{
    /// <summary>
    /// ใบเสร็จ/ใบกำกับภาษีที่ออกจาก<b>การรับชำระของใบนี้</b> (สองทางเหมือนงานหลังรับชำระ: <c>Payment.ReceiptDocumentId</c> หรือ
    /// <c>Document.SettlementPaymentId</c>) — บริษัทเดียวกัน + ผู้ติดต่อเดียวกัน + ออกแล้วยังมีผล + ลูกค้าเห็นได้ ·
    /// การชำระที่กระจายหลายใบไม่นับ (ใบเสร็จนั้นครอบใบอื่นด้วย — ไม่แสดงบนหน้าของใบเดียว) · ขึ้นทันทีที่ออก (ไม่รอส่งอีเมล)
    /// </summary>
    private async Task<List<CustomerDocPageRelated>> PaymentReceiptsAsync(Guid companyId, DocFacts d, PageRow row, bool isEnglish, CancellationToken ct)
    {
        var result = new List<CustomerDocPageRelated>();
        if (!CustomerDocPage.IsPayableType(d.Type)) return result;
        var docId = d.Id;
        var contactId = d.ContactId;
        var payments = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId == docId && !p.IsDeleted)
            .Select(p => new { p.Id, p.ReceiptDocumentId })
            .ToListAsync(ct);
        if (payments.Count == 0) return result;
        var paymentIds = payments.Select(p => p.Id).ToList();
        var receiptIds = payments.Where(p => p.ReceiptDocumentId != null).Select(p => p.ReceiptDocumentId!.Value).Distinct().ToList();
        var rows = await _db.Documents.AsNoTracking()
            .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.ContactId == contactId && x.Id != docId
                && (receiptIds.Contains(x.Id) || (x.SettlementPaymentId != null && paymentIds.Contains(x.SettlementPaymentId.Value)))
                && x.Status != DocumentStatus.Draft && x.Status != DocumentStatus.WaitingApproval
                && x.Status != DocumentStatus.Rejected && x.Status != DocumentStatus.Voided)
            .OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).Take(MaxRelated)
            .Select(x => new { x.Id, x.DocumentType, x.Status, x.DocumentNumber, x.DocumentDate, x.TotalAmount, x.BalanceDue, x.CnDnPurchaseSideOverride })
            .ToListAsync(ct);
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
                .Select(i => new { i.CreatedAt, i.Amount, i.Status, i.CustomerVisibleRejectReason })
                .ToListAsync(ct);
            foreach (var s in rows)
            {
                var v = CustomerDocPageAfterPay.SlipState(isEnglish, s.Status, s.CustomerVisibleRejectReason);
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
            receiptNote = CustomerDocPageAfterPay.ReceiptPendingNote(isEnglish, d.Type, d.Status, row.PaidAmount,
                related.Count(r => r.Kind == CustomerDocPageAfterPay.KindReceipt));
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

    public async Task<(bool Found, string? Path, string? Message)> RelatedPageLinkAsync(Guid companyId, string token, Guid relatedId,
        CancellationToken ct = default)
    {
        // ด่านเดียวกับการเปิดหน้า (token · เพิกถอน/หมดอายุ · ลูกค้าเห็นได้) แล้วต้องเป็นใบในชุดที่หน้าแสดง<b>และ</b>หน้าเสนอปุ่มจ่ายของใบนั้นจริง
        var view = await PublicViewAsync(companyId, token, ct);
        if (view?.Page is not { Viewable: true } page) return (false, null, null);
        if (page.Related.FirstOrDefault(r => r.RelId == relatedId) is not { PayOffered: true }) return (false, null, null);
        // ตัวออก token ตัวเดียว (ด่านชนิด/ฝั่ง/สถานะ/ช่องทางที่ตรึง — 267-1-27) · ลิงก์ที่ผู้ขายยกเลิกไว้ไม่ถูกออกใหม่จากคำขอของลูกค้า ·
        // fallbackOrigin "/" = ขอ token แม้ยังไม่ได้ตั้งที่อยู่เว็บของระบบ (หน้านี้ใช้ที่อยู่ภายในเว็บเดียวกัน ไม่ต้องรู้ origin)
        CustomerLinkResult link;
        try
        {
            link = await EnsureCustomerLinkAsync(companyId, relatedId, "ลูกค้าเปิดจากหน้าเอกสารที่เกี่ยวข้อง", ct, allowReissueRevoked: false,
                actor: null, fallbackOrigin: "/");
        }
        catch (PayLinkRevokedException)
        {
            // ผู้ขายยกเลิกลิงก์ของใบนั้นไว้ — คำขอของลูกค้าไม่ล้างการยกเลิก (259-2-8) · บอกให้ติดต่อผู้ขาย (ไม่บอกเหตุภายใน)
            return (true, null, CustomerDocPageAfterPay.RelatedLinkRefusedMessage(page.IsEnglish));
        }
        if (string.IsNullOrEmpty(link.Token))
            return (true, null, CustomerDocPageAfterPay.RelatedLinkRefusedMessage(page.IsEnglish));
        return (true, $"{DocumentPayLinkPolicy.PagePath}?c={companyId:D}&t={Uri.EscapeDataString(link.Token)}", null);
    }

    public async Task<(Guid DocumentId, Guid ContactId)?> WhtCertTargetAsync(Guid companyId, string token, CancellationToken ct = default)
    {
        var view = await PublicViewAsync(companyId, token, ct);
        if (view?.Page is not { Viewable: true } page) return null;
        if (page.AfterPay?.WhtCert is not { CanUpload: true }) return null;
        return view.Target.ContactId is Guid contactId ? (view.Target.SourceId, contactId) : null;
    }
}
