using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 267 P3 — สถานะลิงก์หน้าเอกสารบนหน้าต่าง "ส่งให้ลูกค้า" · <paramref name="State"/> = ชื่อ <see cref="CustomerDocPage.LinkState"/> ·
/// <paramref name="ReissueOffered"/> = ลิงก์ถูกยกเลิกไว้ ⇒ หน้าต่างเสนอช่องติ๊ก "ออกลิงก์ใหม่แทน" (ตั้งใจเท่านั้น — 259-2-8)</summary>
public sealed record CustomerShareLink(string State, string? Url, DateTime? ExpiresAt, string? IssueRefusal, string? Note, bool ReissueOffered);

/// <summary>ช่องทางหนึ่งบนหน้าต่าง — <paramref name="Available"/> false ⇒ ติ๊กไม่ได้ + <paramref name="Note"/> (เหตุ)</summary>
public sealed record CustomerShareChannelOption(string Code, string Label, string Hint, bool Available, string? Note);

/// <summary>อีเมล: ทาง (<c>EtaxEmailChannel</c> เป็นชื่อ) + ผู้รับตั้งต้น + ลิงก์หน้าเอกสารในอีเมลได้ไหม + ลิงก์ชำระ + เหตุ/คำเตือนของทาง e-Tax</summary>
public sealed record CustomerShareEmail(string Route, string? RouteNote, string? DefaultTo, bool PageLinkIncluded, string? PageLinkNote,
    bool PayLinkOffered, string? PayLinkNote, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

/// <summary>LINE: ปลายทางของผู้ติดต่อ (ส่งได้/เหตุ · ได้ลิงก์หน้าเอกสารไหม/เหตุ — <see cref="DocumentLineSendRule.PageLinkAllowed"/>) + ลิงก์ชำระ</summary>
public sealed record CustomerShareLine(string? Problem, IReadOnlyList<DocumentLineSendRule.Target> Targets, bool PayLinkOffered, string? PayLinkNote);

/// <summary>ค่าตั้ง "ออก/ส่งใบเสร็จอัตโนมัติหลังรับชำระ" ของใบ (รอบ 267 P4 · แก้ผ่าน <c>PUT …/post-pay</c> เดิม)</summary>
public sealed record CustomerSharePostPay(bool Applies, string ReceiptMode, string PartialMode, string? EffectiveLabel);

/// <summary>ปุ่มที่ลูกค้าจะเห็นบนหน้าเอกสาร (ป้ายจากตัวตัดสินเดียว <see cref="CustomerDocPage.Actions"/>)</summary>
public sealed record CustomerShareAction(string Id, string Label);

/// <summary>
/// รอบ 267 P3 — <b>หน้าต่าง "📤 ส่งให้ลูกค้า"</b> (เซิร์ฟเวอร์ประกอบทุกค่า/ป้าย/เหตุ · หน้าเว็บแสดงอย่างเดียว — F2 ข้อ 5) ·
/// <paramref name="CanShare"/> false ⇒ <paramref name="Problem"/> (ทางไปต่อ)
/// </summary>
public sealed record CustomerShareOptions(
    Guid DocumentId, string DocumentNumber, string Title, bool CanShare, string? Problem,
    CustomerShareLink Link, IReadOnlyList<CustomerShareAction> CustomerActions, CustomerShareRule.RespondOption Respond,
    IReadOnlyList<CustomerShareChannelOption> Channels, CustomerShareEmail Email, CustomerShareLine Line, CustomerSharePostPay PostPay,
    IReadOnlyList<string> Warnings);

/// <summary>คำขอ "ส่งให้ลูกค้า" — ช่องทางที่เลือก + ตัวเลือกต่อช่องทาง · <paramref name="OpenRespond"/> = เปิดช่วงตอบรับ (ใบเสนอราคา/ใบส่งของ) ·
/// <paramref name="ReissueRevoked"/> = ตั้งใจออกลิงก์ใหม่แทนลิงก์ที่ถูกยกเลิก</summary>
public sealed record CustomerShareRequest(List<string>? Channels, bool OpenRespond = false, bool ReissueRevoked = false,
    string? EmailTo = null, string? EmailCc = null, bool AttachPdf = true, bool EmailPayLink = false,
    List<Guid>? LineEndpointIds = null, bool LinePayLink = false);

/// <summary>ผล — <paramref name="Success"/> = ทุกช่องทางที่เลือกผ่าน (ช่องทางหนึ่งล้ม ⇒ false พร้อมผลของทุกช่องทาง · ช่องทางที่ผ่านแล้วไม่ย้อน) ·
/// <paramref name="QrDataUri"/> = เมื่อเลือก QR · <paramref name="NoPageLinkNote"/> = ฝ่ายค้าน 267-P3 MED-3: ครั้งนี้ไม่มีลิงก์หน้าเอกสาร (เหตุ) —
/// อีเมล/LINE ส่งแบบเดิม · <paramref name="Url"/> null</summary>
public sealed record CustomerShareResult(bool Success, string Message, string? Url, DateTime? ExpiresAt, string? QrDataUri,
    IReadOnlyList<CustomerShareRule.ChannelOutcome> Outcomes, string? NoPageLinkNote = null);

/// <summary>ไทม์ไลน์ลูกค้าของใบ (อ่านอย่างเดียว · แถวจริงเท่านั้น)</summary>
public sealed record CustomerTimelineView(Guid DocumentId, IReadOnlyList<CustomerShareRule.TimelineItem> Items, bool Truncated);

public interface ICustomerShareService
{
    /// <summary>หน้าต่าง "ส่งให้ลูกค้า" — ผู้เรียกตรวจสิทธิ์ (สร้างเอกสารชนิดนั้น + ชั้นความลับ) ก่อน · ไม่ออกลิงก์ · ไม่มีผลข้างเคียง</summary>
    Task<CustomerShareOptions> OptionsAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>ส่ง — ออก/ใช้ลิงก์เดิม (<c>EnsureCustomerLinkAsync</c>) · เปิดช่วงตอบรับ (<c>ISignRequestService.IssueLinkAsync</c>) · อีเมล (<c>SendDocumentRoutedAsync</c>) ·
    /// LINE (<c>DocumentLineSendService.SendAsync</c>) · ผลแยกต่อช่องทาง</summary>
    Task<CustomerShareResult> ShareAsync(Guid companyId, Guid documentId, CustomerShareRequest request, Guid? userId, string? actor, string origin,
        CancellationToken ct = default);
}

public interface ICustomerTimelineService
{
    Task<CustomerTimelineView> TimelineAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
}
