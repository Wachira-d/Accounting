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

    public static readonly IReadOnlyList<Kind> All = new[] { QuotationAccept, DeliverySign };

    public static Kind? For(DocumentType type) => All.FirstOrDefault(k => k.DocumentType == type);

    public static Kind? ForEvent(string? eventType) => All.FirstOrDefault(k => string.Equals(k.EventType, eventType, StringComparison.Ordinal));

    /// <summary>สถานะที่ "ออกเลขจริงแล้ว" ส่งให้ลูกค้าได้ — ร่าง/รออนุมัติยังเป็นเลข DRAFT (§86/4 ห้ามส่งออก) · ยกเลิก/ถูกปฏิเสธส่งไม่ได้</summary>
    private static bool IsIssuableStatus(DocumentStatus status)
        => status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Voided or DocumentStatus.Rejected);

    /// <summary>เหตุที่ออกลิงก์/ส่งอีเมลขอเซ็นไม่ได้ (null = ได้) — ข้อความไทยพร้อมทางไปต่อ</summary>
    public static string? IssueRefusal(DocumentType type, DocumentStatus status)
    {
        var k = For(type);
        if (k == null) return "ลิงก์ให้ลูกค้าเซ็น/ยอมรับออนไลน์ใช้ได้เฉพาะใบเสนอราคาและใบส่งของ";
        if (IsIssuableStatus(status)) return null;
        return status is DocumentStatus.Voided or DocumentStatus.Rejected
            ? "เอกสารถูกยกเลิก/ปฏิเสธแล้ว — ส่งลิงก์ให้ลูกค้าเซ็นไม่ได้"
            : "ต้องอนุมัติเอกสารก่อนจึงส่งลิงก์ให้ลูกค้าเซ็นได้ (ฉบับร่าง/รออนุมัติยังไม่มีเลขเอกสารจริง)";
    }

    /// <summary>สถานะลิงก์ของใบ (ตามชนิด) — อ่านจากคอลัมน์ของชนิดนั้นที่เดียว</summary>
    public readonly record struct LinkState(string? Token, DateTime? ExpiresAt, DateTime? SignedAt, string? SignedBy);

    public static LinkState Read(Document d) => d.DocumentType switch
    {
        DocumentType.Quotation => new(d.QuotationAcceptToken, d.QuotationAcceptTokenExpiresAt, d.QuotationAcceptedAt, d.QuotationAcceptedBy),
        DocumentType.DeliveryNote => new(d.DeliverySignToken, d.DeliverySignTokenExpiresAt, d.DeliverySignedAt, d.DeliverySignedBy),
        _ => default,
    };

    /// <summary>ลิงก์ยังใช้ได้ไหม (มี token และไม่หมดอายุ)</summary>
    public static bool LinkUsable(LinkState s, DateTime nowUtc)
        => !string.IsNullOrWhiteSpace(s.Token) && (s.ExpiresAt == null || s.ExpiresAt > nowUtc);

    /// <summary>ออก token ใหม่ให้ใบ (token เดิมใช้ไม่ได้ทันที = เพิกถอน) + ล้างสถานะติดตามของลิงก์เดิม (เปิดแล้ว/ส่งอีเมลแล้ว/เตือนแล้ว)</summary>
    public static void SetNewToken(Document d, string token, DateTime nowUtc)
    {
        var k = For(d.DocumentType) ?? throw new InvalidOperationException("ชนิดเอกสารนี้ไม่มีลิงก์ให้ลูกค้าเซ็น");
        var exp = nowUtc.AddDays(k.TtlDays);
        if (d.DocumentType == DocumentType.Quotation) { d.QuotationAcceptToken = token; d.QuotationAcceptTokenExpiresAt = exp; }
        else { d.DeliverySignToken = token; d.DeliverySignTokenExpiresAt = exp; }
        d.SignLinkIssuedAt = nowUtc;
        ResetTracking(d);
    }

    /// <summary>ล้างสถานะติดตามของลิงก์ (เปิดแล้ว · ส่งอีเมลแล้ว · ผู้รับ · เตือนแล้ว) — ผู้เรียก: <see cref="SetNewToken"/> และ
    /// <c>QuotationAcceptance.ClearOn</c> (ออก Rev ใหม่ = ต้องส่งให้ลูกค้าใหม่ · ฝ่ายค้าน N4) · ไม่แตะ token/callback</summary>
    public static void ResetTracking(Document d)
    {
        d.SignLinkOpenedAt = null;
        d.SignLinkEmailedAt = null;
        d.SignLinkEmailedTo = null;
        d.SignReminderSentAt = null;
    }

    /// <summary>URL สาธารณะของลิงก์ — origin มาจาก <c>SystemBaseUrl.PublicOrigin</c> (ไม่ประกอบโดเมนเอง)</summary>
    public static string Url(string origin, Kind k, string token) => $"{origin.TrimEnd('/')}{k.PagePath}?token={token}";

    /// <summary>ป้ายสถานะบนรายการเอกสาร (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว) — Code: signed · expired · opened · emailed · issued</summary>
    public sealed record Chip(string Code, string Label, string Tone);

    /// <summary>ตัวตัดสินป้ายเดียว — "เปิดแล้ว" นับจากการโหลดหน้าลิงก์สาธารณะเท่านั้น (ไม่มี tracking pixel ในอีเมล) · null = ยังไม่เคยออกลิงก์</summary>
    public static Chip? Status(Document d, DateTime nowUtc)
    {
        var k = For(d.DocumentType);
        if (k == null) return null;
        var s = Read(d);
        if (s.SignedAt != null) return new("signed", k.SignedTh, "success");
        if (string.IsNullOrWhiteSpace(s.Token)) return null;
        if (!LinkUsable(s, nowUtc)) return new("expired", "ลิงก์หมดอายุ", "muted");
        if (d.SignLinkOpenedAt != null) return new("opened", "ลูกค้าเปิดลิงก์แล้ว", "info");
        if (d.SignLinkEmailedAt != null) return new("emailed", d.SignReminderSentAt != null ? "ส่งอีเมลแล้ว (เตือนแล้ว)" : "ส่งอีเมลแล้ว", "info");
        return new("issued", "สร้างลิงก์แล้ว", "muted");
    }
}
