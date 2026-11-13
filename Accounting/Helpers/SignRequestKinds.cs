using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ชนิดเอกสารที่ส่งลิงก์ให้ลูกค้า "เซ็น/ยอมรับออนไลน์" ได้ — ตัวตั้งเดียว** (รอบ 240 · คำตัดสิน 240-1 ใน erp-review/2026-09-29/DECISIONS.md)
///
/// <para>ผลสำรวจรอบ 240 (ทั้งเรพ): ลิงก์ที่ลูกค้าภายนอก "ลงนาม/ยอมรับ" ได้มี <b>2 ชนิด</b> เท่านั้น — ใบเสนอราคา (ยอมรับ + ลายเซ็น ·
/// <c>Document.QuotationAcceptToken</c>) และใบส่งของ (เซ็นรับสินค้า · <c>Document.DeliverySignToken</c>) · ลิงก์สาธารณะอื่น (สลิปเงินเดือน ·
/// ใบจองที่พัก · ลิงก์ชำระเงิน · พอร์ทัลลูกค้า) เป็น "ดู/จ่าย" ไม่ใช่ลงนาม · ใบสั่งซื้อ/สัญญา/ใบวางบิล ยังไม่มีเส้นลงนามของลูกค้า
/// (เพิ่มชนิดใหม่ = เพิ่มแถวที่นี่ + เส้นสาธารณะ + ตัวตรวจ token ของชนิดนั้น — ทุกผู้เรียก (ออกลิงก์ · อีเมล · webhook · ป้ายสถานะ) ตามมาเอง)</para>
///
/// <para>ผู้อ่าน: <c>SignRequestService</c> (ออกลิงก์ · อีเมล · ป้ายสถานะ · อัตโนมัติ) · <c>PublicQuotationController</c> (ชนิด event ตอนลูกค้าเซ็น) ·
/// <c>WebhookService.GetEventTypesAsync</c> (รายชื่อ event)</para>
/// </summary>
public static class SignRequestKinds
{
    /// <summary>ชนิดหนึ่ง — <paramref name="EventType"/> = ชื่อ event ของ webhook (สัญญากับผู้เชื่อมต่อ ห้ามเปลี่ยนชื่อ)</summary>
    public sealed record Kind(
        DocumentType DocumentType, string Code, string EventType, string PagePath, int TtlDays,
        string ActionTh, string ActionEn, string ButtonTh, string ButtonEn, string SignedTh, string SignedEn);

    public static readonly Kind QuotationAccept = new(
        DocumentType.Quotation, "quotation_accept", "quotation.accepted", "/pages/quotation-accept.html", 30,
        "ยอมรับใบเสนอราคา", "accept the quotation", "เปิดดูและยอมรับใบเสนอราคา", "Review and accept the quotation",
        "ลูกค้ายอมรับแล้ว", "Accepted");

    public static readonly Kind DeliverySign = new(
        DocumentType.DeliveryNote, "delivery_sign", "delivery.signed", "/pages/delivery-sign.html", 14,
        "เซ็นรับสินค้า", "sign for the delivery", "เปิดดูและเซ็นรับสินค้า", "Review and sign for the delivery",
        "ลูกค้าเซ็นรับแล้ว", "Signed");

    /// <summary>รอบ 267 P5 (267-1-70): ผู้ขาย "รับทราบ/ไม่รับ" ใบสั่งซื้อที่ผู้ซื้อ (บริษัทเรา) ส่งให้ — ลิงก์หน้าเอกสารตัวเดียวของใบ (ไม่มี token รุ่นก่อน) ·
    /// ไม่อยู่ในงานส่งอีเมลอัตโนมัติ (งานนั้นเลือกเฉพาะใบเสนอราคา) · อีเมลเตือนข้ามใบที่ผู้ขายตอบแล้ว (<c>CustomerResponse</c>)</summary>
    public static readonly Kind PurchaseOrderAck = new(
        DocumentType.PurchaseOrder, "purchase_order_ack", "purchase_order.acknowledged", "/pages/pay-document.html", 14,
        "รับทราบใบสั่งซื้อ", "acknowledge the purchase order", "เปิดดูและรับทราบใบสั่งซื้อ", "Review and acknowledge the purchase order",
        "ผู้ขายรับทราบแล้ว", "Acknowledged");

    public static readonly IReadOnlyList<Kind> All = new[] { QuotationAccept, DeliverySign, PurchaseOrderAck };

    public static Kind? For(DocumentType type) => All.FirstOrDefault(k => k.DocumentType == type);

    /// <summary>รอบ 267 P2: เหตุการณ์ของคำตอบที่ไม่ใช่การเซ็น — ลูกค้าไม่รับ / ขอแก้ใบเสนอราคา (ชื่อเหตุการณ์ตัวเดียวของ outbox · webhook · การแจ้งผู้ขาย)</summary>
    public const string QuotationDeclinedEvent = "quotation.declined";
    public const string QuotationChangeRequestedEvent = "quotation.change_requested";
    /// <summary>รอบ 267 P5: ผู้ขายไม่รับใบสั่งซื้อ (ดีไซน์ §5.2)</summary>
    public const string PurchaseOrderDeclinedEvent = "purchase_order.declined";

    /// <summary>ชื่อเหตุการณ์ outbox ของชนิดคำตอบ (ยอมรับ/เซ็นรับ = เหตุการณ์เดิมของรอบ 240)</summary>
    public static string EventFor(CustomerSignRule.Kind kind) => kind switch
    {
        CustomerSignRule.Kind.Accepted => QuotationAccept.EventType,
        CustomerSignRule.Kind.DeliverySigned => DeliverySign.EventType,
        CustomerSignRule.Kind.Declined => QuotationDeclinedEvent,
        CustomerSignRule.Kind.PoAcknowledged => PurchaseOrderAck.EventType,
        CustomerSignRule.Kind.PoDeclined => PurchaseOrderDeclinedEvent,
        _ => QuotationChangeRequestedEvent,
    };

    public static Kind? ForEvent(string? eventType) => All.FirstOrDefault(k => string.Equals(k.EventType, eventType, StringComparison.Ordinal));

    /// <summary>สถานะที่ "ออกเลขจริงแล้ว" ส่งให้ลูกค้าได้ — ร่าง/รออนุมัติยังเป็นเลข DRAFT (§86/4 ห้ามส่งออก) · ยกเลิก/ถูกปฏิเสธส่งไม่ได้</summary>
    private static bool IsIssuableStatus(DocumentStatus status)
        => status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected);

    /// <summary>เหตุที่ออกลิงก์/ส่งอีเมลขอเซ็นไม่ได้ (null = ได้) — ข้อความไทยพร้อมทางไปต่อ</summary>
    public static string? IssueRefusal(DocumentType type, DocumentStatus status)
    {
        var k = For(type);
        if (k == null) return "ลิงก์ให้ลูกค้าเซ็น/ยอมรับออนไลน์ใช้ได้เฉพาะใบเสนอราคา ใบส่งของ และใบสั่งซื้อ (ให้ผู้ขายรับทราบ)";
        if (IsIssuableStatus(status)) return null;
        return status is DocumentStatus.Voided or DocumentStatus.Rejected
            ? "เอกสารถูกยกเลิก/ปฏิเสธแล้ว — ส่งลิงก์ให้ลูกค้าเซ็นไม่ได้"
            : "ต้องอนุมัติเอกสารก่อนจึงส่งลิงก์ให้ลูกค้าเซ็นได้ (ฉบับร่าง/รออนุมัติยังไม่มีเลขเอกสารจริง)";
    }

    /// <summary>สถานะลิงก์ให้ลูกค้าตอบรับของใบ (ตามชนิด) — <paramref name="HasLink"/> = มีลิงก์ที่ส่งให้ลูกค้าตอบรับได้ ·
    /// <paramref name="ExpiresAt"/> = สิ้นสุดช่วงที่ตอบรับได้ (ไม่ใช่วันหมดอายุของลิงก์หน้าเอกสาร)</summary>
    public readonly record struct LinkState(bool HasLink, DateTime? ExpiresAt, DateTime? SignedAt, string? SignedBy);

    /// <summary>
    /// อ่านสถานะจากที่เดียว — รอบ 267 (267-1-3): สองรุ่นของลิงก์
    /// <list type="bullet">
    /// <item><b>ลิงก์หน้าเอกสาร</b> (ออกตั้งแต่รอบ 267): token ของใบตัวเดียว (<c>PayLink*</c>) ที่ผู้ขายเปิดให้ตอบรับแล้ว (<c>SignLinkIssuedAt</c>) และไม่ถูกเพิกถอน ·
    /// ช่วงตอบรับจากตัวตัดสินเดียว <see cref="CustomerDocPage.RespondWindowEnd"/> (ไม่เกินวันหมดอายุของลิงก์)</item>
    /// <item><b>ลิงก์ยอมรับ/เซ็นรับรุ่นก่อน</b> (ออกก่อนรอบ 267 · ฐานเก็บเป็น hash แล้ว): ใช้วันหมดอายุของตัวเองจนหมด — ไม่ออกเพิ่ม</item>
    /// </list>
    /// </summary>
    public static LinkState Read(Document d)
    {
        var (legacy, legacyExp, signedAt, signedBy) = d.DocumentType switch
        {
            DocumentType.Quotation => (d.QuotationAcceptToken, d.QuotationAcceptTokenExpiresAt, d.QuotationAcceptedAt, d.QuotationAcceptedBy),
            DocumentType.DeliveryNote => (d.DeliverySignToken, d.DeliverySignTokenExpiresAt, d.DeliverySignedAt, d.DeliverySignedBy),
            // รอบ 267 P5: ใบสั่งซื้อไม่มีลิงก์รุ่นก่อน · "ตอบรับแล้ว" = ผู้ขายรับทราบ (ภาพฉาย CustomerResponse — ผู้เขียนเดียว CustomerDocumentResponseService)
            DocumentType.PurchaseOrder => ((string?)null, (DateTime?)null,
                d.CustomerResponse == nameof(CustomerSignRule.Kind.PoAcknowledged) ? d.CustomerResponseAt : null, (string?)null),
            _ => ((string?)null, (DateTime?)null, (DateTime?)null, (string?)null),
        };
        if (For(d.DocumentType) == null) return default;
        if (d.SignLinkIssuedAt != null && !string.IsNullOrWhiteSpace(d.PayLinkTokenHash) && d.PayLinkRevokedAt == null)
        {
            var window = CustomerDocPage.RespondWindowEnd(d.DocumentType, d.DueDate, d.SignLinkIssuedAt);
            var end = window != null && d.PayLinkExpiresAt != null ? (window < d.PayLinkExpiresAt ? window : d.PayLinkExpiresAt) : window ?? d.PayLinkExpiresAt;
            return new(true, end, signedAt, signedBy);
        }
        // ฝ่ายค้าน 267 L3: ลิงก์รุ่นก่อนเข้มเท่ารุ่นใหม่ — ใบเสนอราคาพ้นวันยืนราคา = ตอบรับไม่ได้ (วันสิ้นสุด = ตัวที่มาก่อนระหว่างวันหมดอายุลิงก์กับสิ้นวันยืนราคา)
        if (d.DocumentType == DocumentType.Quotation && d.DueDate != null && !string.IsNullOrWhiteSpace(legacy))
        {
            var validity = CustomerDocPage.RespondWindowEnd(d.DocumentType, d.DueDate, d.DueDate);
            if (validity != null && (legacyExp == null || validity < legacyExp)) legacyExp = validity;
        }
        return new(!string.IsNullOrWhiteSpace(legacy), legacyExp, signedAt, signedBy);
    }

    /// <summary>ลิงก์ยังใช้ตอบรับได้ไหม (มีลิงก์ และยังไม่พ้นช่วงตอบรับ)</summary>
    public static bool LinkUsable(LinkState s, DateTime nowUtc)
        => s.HasLink && (s.ExpiresAt == null || s.ExpiresAt > nowUtc);

    /// <summary>ผู้ขาย "ส่งลิงก์ให้ลูกค้าตอบรับ" (รอบ 267 — แทน <c>SetNewToken</c>): เริ่มช่วงตอบรับใหม่ + ล้างสถานะติดตามของรอบก่อน
    /// (เปิดแล้ว/ส่งอีเมลแล้ว/เตือนแล้ว) + <b>เลิกลิงก์ยอมรับ/เซ็นรับรุ่นก่อนของใบนี้</b> (ความหมายเดิมของ "ออกใหม่ = ลิงก์เดิมใช้ไม่ได้") ·
    /// <b>ไม่ออก token</b> — token ของหน้าเอกสารออก/หมุนที่ <c>IDocumentPayLinkService</c> ตัวเดียว (ผู้เรียก <c>SignRequestService.IssueLinkAsync</c>:
    /// "ออกใหม่" = เพิกถอนลิงก์เดิมแล้วออก token ใหม่ — ฝ่ายค้าน 267 H2 · 267-1-24)</summary>
    public static void MarkIssued(Document d, DateTime nowUtc)
    {
        if (For(d.DocumentType) == null) throw new InvalidOperationException("ชนิดเอกสารนี้ไม่มีลิงก์ให้ลูกค้าเซ็น");
        if (d.DocumentType == DocumentType.Quotation) { d.QuotationAcceptToken = null; d.QuotationAcceptTokenExpiresAt = null; }
        else if (d.DocumentType == DocumentType.DeliveryNote) { d.DeliverySignToken = null; d.DeliverySignTokenExpiresAt = null; }
        d.SignLinkIssuedAt = nowUtc;
        // รอบ 267 P2 (267-1-11): ผู้ขายส่งให้ตอบรับใหม่ = เปิดให้ลูกค้าที่เคย "ไม่รับ/ขอแก้" ตอบอีกครั้ง (คำตอบ "ยอมรับ" คงอยู่ — ล้างได้ด้วยการออก Rev ใหม่เท่านั้น)
        // รอบ 267 P5: "รับทราบใบสั่งซื้อ" คงอยู่เหมือน "ยอมรับ" (ล้างได้ด้วยการออก Rev ใหม่เท่านั้น)
        if (d.CustomerResponse is not (nameof(CustomerSignRule.Kind.Accepted) or nameof(CustomerSignRule.Kind.PoAcknowledged)))
        { d.CustomerResponse = null; d.CustomerResponseAt = null; }
        ResetTracking(d);
    }

    /// <summary>รอบ 267 ฝ่ายค้าน H1 — ออก Rev ใหม่ = <b>ปิดช่วงตอบรับ</b>ของใบ: ล้าง <c>SignLinkIssuedAt</c> (ลิงก์หน้าเอกสารยังเปิดดู Rev ล่าสุดได้
    /// แต่ปุ่มยอมรับ/เซ็นรับปิดจนผู้ขายส่งให้ตอบรับใหม่ — <see cref="CustomerDocPage.RespondWindowEnd"/> ต้องมีค่านี้) + เลิกลิงก์ยอมรับ/เซ็นรับรุ่นก่อน
    /// (พฤติกรรมเดิมของรอบ 239) · ผู้เรียก: <c>DocumentService.UpdateDocumentAsync</c> เส้น revision (ทุกชนิดเอกสาร — ชนิดที่ไม่มีลิงก์เซ็นก็ไม่เสียอะไร)</summary>
    public static void CloseRespondOnRevision(Document d)
    {
        d.SignLinkIssuedAt = null;
        // รอบ 267 P2: คำตอบของลูกค้าเป็นของ Rev เดิม (แถวหลักฐานใน CustomerDocumentActions คงอยู่เป็นประวัติ)
        d.CustomerResponse = null;
        d.CustomerResponseAt = null;
        d.QuotationAcceptToken = null;
        d.QuotationAcceptTokenExpiresAt = null;
        d.DeliverySignToken = null;
        d.DeliverySignTokenExpiresAt = null;
    }

    /// <summary>ล้างสถานะติดตามของลิงก์ (เปิดแล้ว · ส่งอีเมลแล้ว · ผู้รับ · เตือนแล้ว) — ผู้เรียก: <see cref="MarkIssued"/> และ
    /// <c>QuotationAcceptance.ClearOn</c> (ออก Rev ใหม่ = ต้องส่งให้ลูกค้าใหม่ · ฝ่ายค้าน N4) · ไม่แตะ token/callback</summary>
    public static void ResetTracking(Document d)
    {
        d.SignLinkOpenedAt = null;
        d.SignLinkEmailedAt = null;
        d.SignLinkEmailedTo = null;
        d.SignReminderSentAt = null;
    }

    /// <summary>ป้ายสถานะบนรายการเอกสาร (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว) — Code: signed · expired · opened · emailed · issued</summary>
    public sealed record Chip(string Code, string Label, string Tone);

    /// <summary>ตัวตัดสินป้ายเดียว — "เปิดแล้ว" นับจากการโหลดหน้าลิงก์สาธารณะเท่านั้น (ไม่มี tracking pixel ในอีเมล) · null = ยังไม่เคยออกลิงก์</summary>
    public static Chip? Status(Document d, DateTime nowUtc)
    {
        var k = For(d.DocumentType);
        if (k == null) return null;
        var s = Read(d);
        if (s.SignedAt != null) return new("signed", k.SignedTh, "success");
        // รอบ 267 P2: ลูกค้าตอบแล้วแต่ไม่ใช่ "ยอมรับ" — พนักงานต้องเห็นบนรายการเอกสาร (ทางไปต่อ: ออก Rev ใหม่ หรือส่งให้ตอบรับใหม่)
        if (d.CustomerResponse == nameof(CustomerSignRule.Kind.Declined)) return new("declined", "ลูกค้าไม่รับใบเสนอราคา", "danger");
        if (d.CustomerResponse == nameof(CustomerSignRule.Kind.ChangeRequested)) return new("change_requested", "ลูกค้าขอแก้ไข", "warning");
        if (d.CustomerResponse == nameof(CustomerSignRule.Kind.PoDeclined)) return new("declined", "ผู้ขายไม่รับใบสั่งซื้อ", "danger");
        if (!s.HasLink) return null;
        if (!LinkUsable(s, nowUtc)) return new("expired", "ลิงก์หมดอายุ", "muted");
        if (d.SignLinkOpenedAt != null) return new("opened", "ลูกค้าเปิดลิงก์แล้ว", "info");
        if (d.SignLinkEmailedAt != null) return new("emailed", d.SignReminderSentAt != null ? "ส่งอีเมลแล้ว (เตือนแล้ว)" : "ส่งอีเมลแล้ว", "info");
        return new("issued", "สร้างลิงก์แล้ว", "muted");
    }
}
