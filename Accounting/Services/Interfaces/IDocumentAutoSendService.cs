using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Payments;

namespace Accounting.Services.Interfaces;

/// <summary>
/// รอบ 241 — <b>ลิงก์ชำระเงินของเอกสาร + ค่าตั้ง "ส่งเอกสารอัตโนมัติ"</b> (ไม่พึ่ง <c>IDocumentService</c> — <c>EmailScheduleService</c> ใช้ได้โดยไม่เกิดวง DI) ·
/// ตัวตัดสินอยู่ที่ <see cref="DocumentPayLinkPolicy"/> · DOCUMENT_FLOW §2.12g · คำตัดสิน 241-1..241-4
/// </summary>
public interface IDocumentPayLinkService
{
    /// <summary>ความจริงของบริษัท: ช่องทางออนไลน์ที่หน้าชำระทำจบได้ + มีบัญชีรับโอนไหม</summary>
    Task<DocumentPayLinkPolicy.CompanyFacts> CompanyFactsAsync(Guid companyId, CancellationToken ct = default);

    Task<DocumentAutoSendSettingsDto> GetSettingsAsync(Guid companyId, CancellationToken ct = default);
    Task<DocumentAutoSendSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateDocumentAutoSendSettings request, string actor, CancellationToken ct = default);

    /// <summary>ออก/ใช้ลิงก์เดิมของใบ (หนึ่งลิงก์ต่อใบ · หมดอายุ/เพิกถอน ⇒ ออกใหม่) · <paramref name="scheduleChannels"/> = ช่องทางที่ตารางเลือก (null = ค่าบริษัท) ·
    /// คืนลิงก์ หรือเหตุที่ออกไม่ได้ (ไม่โยน)</summary>
    /// <param name="allowReissueRevoked">รอบ 259 (259-2-8): ลิงก์ที่ถูกเพิกถอน ⇒ ออกใหม่ได้เฉพาะผู้เรียกที่ตั้งใจ (ปุ่มออกลิงก์ของพนักงาน · อีเมลตามตาราง) ·
    /// false (หน้าต่างส่งเอง · พอร์ทัล) ⇒ โยน <see cref="Accounting.Helpers.PayLinkRevokedException"/> · ลิงก์ที่ยังใช้ได้ไม่ถูกเขียนทับช่องทาง · ออกใหม่แทนลิงก์เดิม = audit</param>
    /// <param name="customerPage">รอบ 267 (267-1-1): ออกลิงก์ในฐานะ "ลิงก์หน้าเอกสาร" — ด่าน = <see cref="CustomerDocPage.IssueRefusal"/> (ใบที่จ่ายไม่ได้ เช่น
    /// ใบเสนอราคา/ใบเสร็จ ก็ออกได้ · ปุ่มจ่ายยังตัดสินสดที่ชั้นจ่าย) · ผู้เรียกภายนอกใช้ <see cref="EnsureCustomerLinkAsync"/> เท่านั้น
    /// (ล็อก R267a) — token/คอลัมน์/การเพิกถอนชุดเดียวกับลิงก์ชำระ</param>
    Task<PayLinkResult> EnsureLinkAsync(Guid companyId, Guid documentId, IReadOnlyCollection<string>? scheduleChannels, CancellationToken ct = default,
        bool allowReissueRevoked = false, string? actor = null, bool customerPage = false);

    /// <summary>รอบ 267 P1 (267-1-1) — <b>ตัวออกเดียวของลิงก์หน้าเอกสาร</b> (หนึ่งลิงก์ต่อใบ · คอลัมน์ <c>PayLink*</c> เดิม · URL เดิม) ·
    /// ลิงก์ที่ยังใช้ได้ไม่ถูกทับ · ลิงก์ที่ถูกเพิกถอนออกใหม่ได้เฉพาะ <paramref name="allowReissueRevoked"/> (259-2-8) ·
    /// ใบค่าบริการ NextAcc ของผู้เช่า = ปฏิเสธ (262-1-9) · คืนลิงก์ หรือเหตุที่ออกไม่ได้ (ไม่โยน ยกเว้นลิงก์ถูกเพิกถอน)</summary>
    /// <param name="purpose">ใครขอ (บันทึกใน audit ตอนออกใหม่) เช่น <c>sign-link</c> · <c>staff-copy</c></param>
    /// <param name="fallbackOrigin">origin ของคำขอ (ปุ่มของพนักงาน) — ใช้ประกอบ URL เมื่อยังไม่ได้ตั้ง URL ของระบบ (เส้นลิงก์เซ็นเดิมทำแบบนี้)</param>
    Task<CustomerLinkResult> EnsureCustomerLinkAsync(Guid companyId, Guid documentId, string purpose, CancellationToken ct = default,
        bool allowReissueRevoked = false, string? actor = null, string? fallbackOrigin = null);

    /// <summary>รอบ 267 P1 — URL ของลิงก์หน้าเอกสารที่ยังใช้ได้ (อ่านอย่างเดียว · ไม่ออก/ต่ออายุ) · null = ยังไม่มี/เพิกถอน/หมดอายุ/ใบที่ลูกค้าไม่เห็น ·
    /// ต่างจาก <see cref="CurrentUrlAsync"/> (ชั้นจ่าย — ใบที่จ่ายไม่ได้คืน null)</summary>
    Task<string?> CurrentCustomerUrlAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>รอบ 267 P1 — view model ของหน้าเอกสารในโหมดที่ไม่ใช้ token ของใบ (พอร์ทัล · การ์ด LINE · พรีวิวของพนักงาน) ·
    /// <b>ผู้เรียกต้องพิสูจน์สิทธิ์ก่อน</b> (JWT พอร์ทัล + ลูกค้าเห็นได้ · HMAC แถวคิว LINE · สิทธิ์อ่านเอกสารของพนักงาน) ·
    /// ไม่มีเป้าหมายรายการชำระ (จ่าย/สลิปทำได้เฉพาะโหมดลิงก์) · null = ไม่พบ/ใบที่ลูกค้าไม่เห็น</summary>
    Task<PublicDocumentPayView?> PageForDocumentAsync(Guid companyId, Guid documentId, CustomerDocPage.Mode mode, CancellationToken ct = default);

    /// <summary>รอบ 267 P1 — เอกสารที่ token นี้ดาวน์โหลด PDF ได้: ตัวใบเอง (<paramref name="relatedId"/> = null) หรือใบที่เกี่ยวข้อง
    /// ที่อยู่<b>ในชุดที่หน้าแสดง</b>เท่านั้น (ไม่ใช่แค่บริษัทเดียวกัน) · null = ไม่พบ/ลิงก์ใช้ไม่ได้/นอกชุด</summary>
    Task<Guid?> ResolvePdfDocumentAsync(Guid companyId, string token, Guid? relatedId, CancellationToken ct = default);

    /// <summary>รอบ 267 P1 — ประทับ "ลูกค้าเปิดลิงก์แล้ว" ครั้งแรก (เฉพาะชนิดที่มีป้ายติดตามลิงก์เซ็น · เรียกจากการโหลดหน้าจริงเท่านั้น)</summary>
    Task MarkOpenedAsync(Guid companyId, string token, CancellationToken ct = default);

    /// <summary>รอบ 267 P1 — ผู้ถือลิงก์ที่<b>หมดอายุ</b> (หรือลิงก์ที่ยังใช้ได้แต่ปุ่มปิดเพราะพ้นกำหนด) ขอให้ผู้ขายส่งลิงก์ใหม่:
    /// ไม่ออก token ให้คนนอก · บันทึก audit + คืนข้อมูลให้ผู้เรียกแจ้งพนักงาน · ≤ 1 ครั้ง/ใบ/วัน · ลิงก์ที่ถูกเพิกถอน = ไม่พบ</summary>
    Task<CustomerLinkRequestResult> RequestNewLinkAsync(Guid companyId, string token, CancellationToken ct = default);

    /// <summary>เหมือน <see cref="RequestNewLinkAsync"/> สำหรับผู้เรียกที่พิสูจน์สิทธิ์ด้วยทางอื่นแล้ว (การ์ด LINE)</summary>
    Task<CustomerLinkRequestResult> RequestLinkForDocumentAsync(Guid companyId, Guid documentId, string source, CancellationToken ct = default);

    /// <summary>รอบ 267 P4 (267-1-47): ผู้ถือลิงก์ของใบ (เช่นใบวางบิล) ขอเปิด<b>หน้าเอกสารของใบที่เกี่ยวข้อง</b>เพื่อชำระรายใบ — ใบนั้นต้องอยู่ในชุดที่หน้า
    /// แสดงและมีปุ่ม "ชำระใบนี้" (<c>CustomerDocPageRelated.PayOffered</c>) · คืนเฉพาะลิงก์ที่ผู้ขายออกไว้และยังใช้ได้ — ไม่ออก token ให้ผู้ถือลิงก์ (ฝ่ายค้าน MED-2) ·
    /// <c>Found</c> false = ข้อความเดียว (ไม่พบ) · <c>Path</c> = ที่อยู่ภายในเว็บของหน้าเอกสารใบนั้น · <c>Message</c> = เหตุที่เปิดไม่ได้</summary>
    Task<RelatedPageLinkResult> RelatedPageLinkAsync(Guid companyId, string token, Guid relatedId, CancellationToken ct = default);

    /// <summary>รอบ 267 P4 (267-1-48): เอกสาร + ผู้ติดต่อ ที่ token นี้ส่งหนังสือรับรอง 50 ทวิ ได้ ณ ตอนนี้ (หน้าเสนอฟอร์มจริง) · null = ไม่ได้</summary>
    Task<(Guid DocumentId, Guid ContactId)?> WhtCertTargetAsync(Guid companyId, string token, CancellationToken ct = default);

    /// <summary>บล็อกลิงก์ชำระเงินสำหรับอีเมลตามตาราง — null เมื่อไม่แนบ (ปิด · ใบไม่เข้าเงื่อนไข · บริษัทยังไม่ตั้งรับชำระออนไลน์)</summary>
    Task<PayLinkResult?> ForScheduledEmailAsync(EmailScheduleRule rule, Document doc, CancellationToken ct = default);

    Task RevokeAsync(Guid companyId, Guid documentId, string actor, CancellationToken ct = default);

    /// <summary>URL ของลิงก์ปัจจุบัน ณ ตอนส่งอีเมล (ฝ่ายค้าน S1 — คิวเก็บที่หมาย) · null = ลิงก์ใช้ไม่ได้/ใบไม่เข้าเงื่อนไขแล้ว (ไม่ออกลิงก์ใหม่)</summary>
    Task<string?> CurrentUrlAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>รอบ 246 QR — ข้อเท็จจริงของ QR บนกระดาษ (ยอดสุทธิของลิงก์ · URL ลิงก์ที่ยังใช้ได้ · ช่องทางออนไลน์) — อ่านอย่างเดียว ไม่ออกลิงก์ใหม่</summary>
    Task<DocumentPayQrFacts?> PayQrFactsAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>สถานะลิงก์ + งานหลังรับชำระของใบ (หน้าเอกสาร)</summary>
    Task<DocumentPayLinkStatusDto> StatusAsync(Guid companyId, Guid documentId, CancellationToken ct = default);

    /// <summary>หน้าชำระสาธารณะ — null = ไม่พบ token ของบริษัทนี้ · <see cref="PublicDocumentPayView.Target"/> = เป้าหมายของการสร้างรายการชำระ (ยอด/ช่องทางจากเซิร์ฟเวอร์)</summary>
    Task<PublicDocumentPayView?> PublicViewAsync(Guid companyId, string token, CancellationToken ct = default);
    /// <summary>รอบ 264 P4 (คำตัดสิน 264-5-11): เป้าหมายรายการชำระของเอกสารสำหรับ "จ่ายจากระบบของคู่ค้า" — ตัดสินด้วย <see cref="PublicViewAsync"/> ตัวเดียวกับหน้าชำระ
    /// (โทเคนของลิงก์ที่ใช้อยู่ของใบ — ไม่มีลิงก์/ใช้ไม่ได้ ⇒ null) · ผู้เรียกต้องตรวจว่าเป็นคู่ค้าที่ใบนี้ถูกส่งถึงแล้ว</summary>
    Task<Accounting.Services.Payments.PublicPayTarget?> PartnerPayTargetAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
    /// <summary>รอบ 253 (WP-10): ลูกค้าเลือก "ไม่หัก ณ ที่จ่าย" — บันทึกการยอมรับแล้วคืนเป้าหมายยอดเต็ม · <c>Target</c> null = ไม่พบ token</summary>
    Task<NoWhtAcceptResult> AcceptNoWhtAsync(Guid companyId, string token, NoWhtAcceptRequest request, CancellationToken ct = default);
    /// <summary>รอบ 253 (WP-10): ผูกการยอมรับ "ไม่หัก" กับรายการชำระที่สร้างตามมา</summary>
    Task LinkNoWhtIntentAsync(Guid companyId, Guid acceptanceId, Guid paymentIntentId, CancellationToken ct = default);
    /// <summary>รอบ 250 WP-03 (ฝ่ายค้าน H1): ตัวเลือกโอน/QR ของใบสำหรับพอร์ทัล — กติกาเดียวกับหน้าชำระ (IssueRefusal · ยอดสุทธิหลัง WHT · บัญชีของใบ) · null = ไม่พบใบ</summary>
    Task<DocumentTransferView?> TransferForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
    /// <summary>รอบ 260: ยอดของใบตามกติกาเดียวกับ <see cref="TransferForDocumentAsync"/> (IssueRefusal · ยอดสุทธิหลัง WHT) แต่<b>ไม่สร้างกล่องโอน/QR</b> —
    /// ผู้เรียกที่ต้องการแค่ "ยอดที่ลูกค้าต้องโอน" ของหลายใบ (จับคู่สลิปจากแชต LINE) · <c>Transfer</c> = null เสมอ · null = ไม่พบใบ</summary>
    Task<DocumentTransferView?> DueAmountsForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
}

/// <summary>รอบ 241 — งานอัตโนมัติหลังรับชำระ (ออก/ส่งใบเสร็จ หรือใบเสร็จ-ใบกำกับภาษี · ตรวจแถวรอ 50 ทวิ) · ตัวตัดสิน <see cref="PostPaymentAutomationPolicy"/></summary>
public interface IPostPaymentAutomationService
{
    /// <summary>จุดเข้าเดียวหลังบันทึกรับ/จ่ายชำระ (gateway · มือ) — สร้างแถวงาน 1 แถวต่อการชำระ (ซ้ำ = ไม่เพิ่ม) · ไม่โยน (การชำระสำเร็จแล้ว) ·
    /// คืน true เมื่อสร้างแถวใหม่</summary>
    Task<bool> EnqueueAsync(Guid companyId, Guid paymentId, Accounting.Helpers.PaymentEntrySource source, string? actor, CancellationToken ct = default);

    /// <summary>ประมวลผลแถวที่ถึงเวลา (ทุกบริษัท · จองแถวด้วย SKIP LOCKED) — เรียกจาก <c>EmailScheduleWorker</c> ทุกนาที</summary>
    Task<int> ProcessDueAsync(CancellationToken ct = default);

    /// <summary>ลองแถวที่หยุด/ล้มใหม่ (ผู้ใช้กดหลังแก้ข้อมูล)</summary>
    Task RetryAsync(Guid companyId, Guid runId, CancellationToken ct = default);

    /// <summary>รอบ 267 P4 (267-1-44): ตั้งค่า "ออก/ส่งใบเสร็จอัตโนมัติหลังรับชำระ" ต่อใบของใบที่ออกแล้ว — ชื่อ Inherit · On · Off (null = ไม่แตะช่องนั้น) ·
    /// ตัวตัดสินเดียวกับฟอร์มเอกสาร · มีผลกับการรับชำระครั้งถัดไป · งานที่ยังรอประมวลผล/กดลองใหม่ใช้ค่าปัจจุบัน (ปิดที่ใบ = งานที่ค้างจบพร้อมเหตุ) · เปิดทีหลังไม่ออกใบเสร็จย้อนหลังของการชำระที่ผ่านไปแล้ว · ลง audit chain</summary>
    Task SetDocumentLayerAsync(Guid companyId, Guid documentId, string? receiptMode, string? partialMode, Guid? userId, string actor,
        CancellationToken ct = default);
}

public sealed record PayChannelOption(string Code, string Label);

/// <summary>รอบ 246 QR — <paramref name="PayLinkRefusal"/> = เหตุที่ใบนี้ออกลิงก์ไม่ได้ (null = ได้) · <paramref name="NetAmount"/> = ยอดสุทธิหลังหัก ณ ที่จ่าย
/// (เฉพาะใบที่ออกลิงก์ได้) · <paramref name="OnlineUrl"/> = ลิงก์ที่ยังใช้ได้ (null = ไม่มี/เพิกถอน/หมดอายุ) ·
/// <paramref name="WhtNote"/> = คำเตือนเดียวกับหน้าลิงก์ (รอบ 244 S1: ใบตั้ง WHT แต่ผู้ซื้อไม่มีหน้าที่หัก — ยอดอาจขาดเท่า WHT) · ฝ่ายค้าน N5</summary>
public sealed record DocumentPayQrFacts(string? PayLinkRefusal, decimal? NetAmount, string? OnlineUrl, DateTime? ExpiresAt, int OnlineChannelCount,
    string? WhtNote = null);

public sealed record PayLinkResult(string? Url, DocumentPayLinkPolicy.Amounts? Amounts, IReadOnlyList<string> Channels, string? Refusal)
{
    public bool Ok => Url != null && Refusal == null;
    /// <summary>รอบ 267: token ของลิงก์ (เฉพาะผู้เรียกในเซิร์ฟเวอร์ — ไม่ส่งออกใน JSON) · วันหมดอายุของลิงก์</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Token { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>รอบ 267 P1 — ผลของ <c>EnsureCustomerLinkAsync</c>: <paramref name="Refusal"/> ไม่ว่าง = ออกไม่ได้ (ข้อความไทยพร้อมทางไปต่อ)</summary>
public sealed record CustomerLinkResult(string? Url, string? Token, DateTime? ExpiresAt, string? Refusal)
{
    public bool Ok => Url != null && Refusal == null;
}

/// <summary>บรรทัดของใบบนหน้าเอกสาร — ราคา/ยอด = null เมื่อหน้านี้ไม่แสดงยอด (<c>CustomerDocPage.ShowsAmounts</c>)</summary>
public sealed record CustomerDocPageLine(string Description, decimal Quantity, string? Unit, decimal? UnitPrice, decimal? Discount, decimal? Amount);

/// <summary>ใบที่เกี่ยวข้องของหน้าเอกสาร — <paramref name="RelId"/> ใช้ได้เฉพาะกับ token ของใบที่ส่งมันมา (เซิร์ฟเวอร์ตรวจชุดซ้ำตอนดาวน์โหลด) ·
/// <paramref name="Kind"/>: parent · child · replacement · billed</summary>
public sealed record CustomerDocPageRelated(Guid RelId, string Kind, string KindLabel, string Title, string DocumentNumber, DateTime DocumentDate,
    decimal? TotalAmount, decimal? BalanceDue, string StatusLabel,
    // รอบ 267 P4: Kind เพิ่ม "receipt" (ใบเสร็จ/ใบกำกับที่ออกจากการชำระของใบนี้) · PayOffered = มีปุ่ม "ชำระใบนี้" (พาไปหน้าเอกสารของใบนั้น —
    // CustomerDocPageAfterPay.RelatedPayOffered) · Note = ข้อความประกอบจากเซิร์ฟเวอร์ (ป้าย e-Tax เมื่อส่งแล้วจริง)
    bool PayOffered = false, string? PayLabel = null, string? Note = null);

/// <summary>รอบ 267 P4 — สลิปหนึ่งใบที่ลูกค้าแจ้งโอนของเอกสารนี้ (สถานะ/ข้อความจาก <c>CustomerDocPageAfterPay.SlipState</c> · ไม่มีรหัสรายการ/ที่อยู่ไฟล์)</summary>
public sealed record CustomerDocPageSlip(DateTime SubmittedAt, decimal Amount, string State, string Label, string? Note);

/// <summary>รอบ 267 P4 — ส่วน "หลังจ่าย" ของหน้าเอกสาร (แสดงผลอย่างเดียว): สถานะสลิป · ข้อความเมื่อยังไม่มีใบเสร็จ · การ์ด 50 ทวิ ·
/// <paramref name="SlipPending"/> = มีสลิปรอตรวจ (หน้าไม่ชวนแนบซ้ำ — ด่านจริงอยู่ที่เส้นรับสลิปเดิม)</summary>
public sealed record CustomerDocPageAfterPayView(IReadOnlyList<CustomerDocPageSlip> Slips, bool SlipPending, string? ReceiptNote,
    Accounting.Helpers.CustomerDocPageAfterPay.WhtCertView? WhtCert, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// รอบ 267 P1 — ส่วน "หน้าเอกสาร" ของ view model (หัวใบอยู่ในช่องเดิมของ <see cref="PublicDocumentPayView"/>) ·
/// ทุกค่ามาจากเซิร์ฟเวอร์ (<c>CustomerDocPage</c>) — หน้าเว็บแสดงอย่างเดียว · ไม่มี id ภายใน/ผังบัญชี/หมายเหตุภายใน/ผู้สร้าง/IP
/// </summary>
public sealed record CustomerDocPageView(
    string Mode,
    string LinkState,
    bool Viewable,
    bool CanRequestLink,
    bool IsEnglish,
    string StatusLabel,
    bool Voided,
    int RevisionNumber,
    bool ShowAmounts,
    string Currency,
    decimal SubTotal,
    decimal DiscountAmount,
    decimal VatAmount,
    decimal WithholdingTaxAmount,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceDue,
    IReadOnlyList<CustomerDocPageLine> Lines,
    IReadOnlyList<CustomerDocPage.PageAction> Actions,
    IReadOnlyList<CustomerDocPageRelated> Related,
    IReadOnlyDictionary<string, string> Labels,
    string? RespondedBy,
    DateTime? RespondedAt,
    DateTime? ExpiresAt)
{
    /// <summary>ที่อยู่ PDF ของใบในโหมดนี้ (เส้นที่ผู้ถือสิทธิ์โหมดนั้นเรียกได้ตรง ๆ) · null = โหมดนี้ไม่มีเส้นดาวน์โหลดตรง — controller เติม</summary>
    public string? PdfPath { get; init; }
    /// <summary>ฐานของที่อยู่ PDF ใบที่เกี่ยวข้อง (ต่อท้ายด้วย <c>{relId}/pdf</c>) · null = โหมดนี้ไม่ให้ดาวน์โหลดใบที่เกี่ยวข้อง</summary>
    public string? RelatedPdfBase { get; init; }
    /// <summary>ที่อยู่เส้น "ขอลิงก์ใหม่/ขอลิงก์ทำรายการ" ของโหมดนี้ (POST) · null = ไม่มี</summary>
    public string? RequestLinkPath { get; init; }
    /// <summary>รอบ 267 P2: ฟอร์มผู้ลงนามของปุ่มตอบรับที่เปิดอยู่ (ข้อความรับรอง/ยินยอม/ป้าย จากเซิร์ฟเวอร์) · null = ไม่มีปุ่มตอบรับที่เปิด</summary>
    public CustomerDocRespondForm? Respond { get; init; }
    /// <summary>รอบ 267 P2: คำตอบที่ลูกค้าเคยทำบนใบนี้ (ใหม่ → เก่า · ชื่อ/เวลา/Rev — ไม่มี IP) + ฐานของที่อยู่ดาวน์โหลดฉบับที่ลงนาม (ต่อท้ายด้วย <c>{actionId}</c>)</summary>
    public IReadOnlyList<CustomerDocResponseItem> Responses { get; init; } = Array.Empty<CustomerDocResponseItem>();
    public string? SignedCopyBase { get; init; }
    /// <summary>รอบ 267 P2: เส้นขอรหัสยืนยันอีเมล (POST) — ใช้เมื่อ <c>Respond.Actions[].OtpRequired</c></summary>
    public string? OtpPath { get; init; }

    // ── รอบ 267 P4 (หลังจ่าย) ──
    /// <summary>สถานะสลิป · ข้อความใบเสร็จ · การ์ด 50 ทวิ (โหมดลิงก์/พอร์ทัล) · null = โหมดนี้ไม่มีส่วนหลังจ่าย</summary>
    public CustomerDocPageAfterPayView? AfterPay { get; init; }
    /// <summary>ที่อยู่เส้นส่งหนังสือรับรอง 50 ทวิ ของโหมดนี้ (POST multipart) · null = ไม่มี — controller เติม</summary>
    public string? WhtCertPath { get; init; }
    /// <summary>ฐานของเส้น "เปิดหน้าเอกสารของใบที่เกี่ยวข้อง" (POST · ต่อท้ายด้วย <c>{relId}/page-link</c>) — ปุ่ม "ชำระใบนี้" · null = ไม่มี</summary>
    public string? RelatedLinkBase { get; init; }
    /// <summary>โหมดพอร์ทัล: เส้นขอลิงก์หน้าชำระของใบนี้ (POST · เส้นเดิมของพอร์ทัล) · null = ชำระจากหน้านี้ไม่ได้/ไม่มีสิทธิ์</summary>
    public string? PortalPayLinkPath { get; init; }

    /// <summary>ลิงก์ใช้ไม่ได้ — ไม่มีข้อมูลของใบเลย (มีแค่สถานะลิงก์ + ขอลิงก์ใหม่ได้ไหม)</summary>
    public static CustomerDocPageView Unusable(CustomerDocPage.LinkState state, bool canRequestLink) => new(
        CustomerDocPage.ModeCode(CustomerDocPage.Mode.Link), state.ToString(), false, canRequestLink, false, "", false, 0, false, "THB",
        0m, 0m, 0m, 0m, 0m, 0m, 0m, Array.Empty<CustomerDocPageLine>(), Array.Empty<CustomerDocPage.PageAction>(),
        Array.Empty<CustomerDocPageRelated>(), CustomerDocPage.Labels(false, Accounting.Models.Enums.DocumentType.Invoice), null, null, null);
}

/// <summary>รอบ 267 P2 — ฟอร์มผู้ลงนามของหน้าเอกสาร: เลข Rev + hash เนื้อหาที่หน้าแสดง (ส่งกลับตอนกด — ตอบรับได้เฉพาะฉบับที่เห็น) ·
/// รุ่นข้อความรับรอง · ปุ่มที่เปิดพร้อมข้อความของแต่ละปุ่ม · ป้ายของฟอร์ม — ทุกค่าจาก <c>CustomerSignText</c>/<c>CustomerSignRule</c></summary>
public sealed record CustomerDocRespondForm(
    int RevisionNumber, string ContentHash, string AttestationVersion, bool TitleRequired,
    IReadOnlyList<CustomerDocRespondAction> Actions, IReadOnlyList<CustomerDocRespondReason> DeclineReasons,
    string PrivacyNotice, string LegalReference, IReadOnlyDictionary<string, string> Labels);

/// <summary>ปุ่มตอบรับหนึ่งปุ่ม — <paramref name="Signature"/>: required · optional · none</summary>
public sealed record CustomerDocRespondAction(
    string Id, string Label, string Attestation, string Consent, string Signature, bool OtpRequired, bool NeedsReason, bool NeedsMessage);

public sealed record CustomerDocRespondReason(string Code, string Label);

/// <summary>คำตอบของลูกค้าหนึ่งครั้งบนหน้าเอกสาร — <paramref name="SignedCopyExpected"/> = คำตอบชนิดที่มีฉบับที่ลงนาม (ยังไม่ Ready = กำลังเตรียม)</summary>
public sealed record CustomerDocResponseItem(
    Guid ActionId, string Action, string Label, int RevisionNumber, string SignerName, string? SignerTitle, DateTime At,
    bool SignedCopyReady, bool SignedCopyExpected);

/// <summary>รอบ 267 P2 — หลักฐานการตอบรับของลูกค้าสำหรับพนักงาน (หน้าเอกสารของพนักงาน/ไทม์ไลน์ของ P3) + คำแนะนำหลังยอมรับ + คำเตือนอากรแสตมป์ (เตือนเท่านั้น)</summary>
public sealed record CustomerDocEvidenceView(
    IReadOnlyList<CustomerDocEvidenceItem> Items, string? CustomerResponse, string? ConvertSuggestion, IReadOnlyList<string> ConvertTo,
    string? StampDutyWarning, string? StampDutyPage, string LegalReference);

public sealed record CustomerDocEvidenceItem(
    Guid ActionId, string Action, string Label, int RevisionNumber, string Channel, string IdentityLevel,
    string? SignerFirstName, string? SignerLastName, string? SignerTitle, string? SignerOrgName, string? SignerEmail, bool EmailVerified,
    string? AttestationVersion, string? ReasonCode, string? ReasonLabel, string? Message, string ContentHash, string? Ip,
    bool HasSignature, string? SignedPdfSha256, DateTime? SignedPdfAt, DateTime At, DateTime? ErasedAt, bool IsCurrentRevision);

/// <summary>ผลของการขอลิงก์ใหม่ — <paramref name="Found"/> false = ข้อความเดียว (ไม่พบ/เพิกถอน/เกินช่วงขอ) ·
/// <paramref name="Recorded"/> true = บันทึกคำขอครั้งนี้ (ผู้เรียกแจ้งพนักงาน) · false = วันนี้ขอไปแล้ว (ไม่แจ้งซ้ำ)</summary>
/// <summary>รอบ 267 P4 (ฝ่ายค้าน MED-2 · 267-1-47): ผลของปุ่ม "ชำระใบนี้" — <paramref name="Path"/> = หน้าเอกสารของใบนั้น (ลิงก์ที่ผู้ขายออกไว้และยังใช้ได้เท่านั้น) ·
/// <paramref name="Path"/> null + <paramref name="Request"/> = บันทึกคำขอลิงก์ใหม่ถึงผู้ขายแล้ว (ผู้เรียกแจ้งพนักงานเมื่อ <c>Recorded</c>) ·
/// <paramref name="Request"/> null = ผู้ขายยกเลิกลิงก์ของใบนั้นไว้ (ติดต่อผู้ขาย)</summary>
public sealed record RelatedPageLinkResult(bool Found, string? Path, string? Message, CustomerLinkRequestResult? Request);

public sealed record CustomerLinkRequestResult(bool Found, bool Recorded, Guid DocumentId, string DocumentNumber, string Message);

public sealed record DocumentAutoSendSettingsDto(
    bool PayLinkAvailable,
    string? PayLinkUnavailableReason,
    List<PayChannelOption> CompanyChannels,
    bool PayLinkDefaultEnabled,
    List<string>? PayLinkDefaultChannels,
    bool PostPayEmailReceipt,
    bool PostPayIncludePartial,
    string SenderNote,
    // รอบ 253 (WP-10): ทวง 50 ทวิ อัตโนมัติ (ค่าตั้งต้นปิด)
    bool WhtCertReminderEnabled = false,
    // รอบ 259-3 ฝ่ายค้าน L5: ผู้เรียกแก้ค่าชุดนี้ได้ไหม (PUT = เจ้าของบริษัท) — หน้าที่แสดงสวิตช์ล็อกช่อง + บอกเหตุ · null = ไม่ได้ตรวจ
    bool? CanEdit = null)
{
    /// <summary>รอบ 267 P4 (267-1-44): ชนิดเอกสาร (ชื่อ enum) ที่ตั้งค่า "ออกใบเสร็จอัตโนมัติหลังรับชำระ" ต่อใบได้ — ฟอร์มเอกสารแสดงช่องตามชุดนี้
    /// (ตัวตัดสิน <c>PostPaymentAutomationPolicy.DocumentLayerAllowed</c> · หน้าเว็บไม่มีรายการชนิดของตัวเอง)</summary>
    public IReadOnlyList<string> PostPayDocumentTypes { get; init; } =
        DocumentPayLinkPolicy.PayableTypes.Where(PostPaymentAutomationPolicy.DocumentLayerAllowed).Select(t => t.ToString()).ToList();
}

/// <summary>null = ไม่แตะช่องนั้น · <see cref="PayLinkDefaultChannels"/> ว่าง = ล้าง (ทุกช่องทางที่บริษัทเปิด)</summary>
public sealed record UpdateDocumentAutoSendSettings(
    bool? PayLinkDefaultEnabled = null,
    List<string>? PayLinkDefaultChannels = null,
    bool? PostPayEmailReceipt = null,
    bool? PostPayIncludePartial = null,
    bool? WhtCertReminderEnabled = null);

/// <summary>รอบ 241 — จุดต่อ "บล็อกเพิ่มในอีเมลใบเสร็จอัตโนมัติ" (รอบนี้ไม่มีตัวลงทะเบียน · ทีมออกแบบเส้นรับ 50 ทวิ เสียบรอบถัดไป) ·
/// ผู้ประกอบเนื้อเดียว <see cref="ReceiptEmailSections.ComposeBody"/></summary>
public interface IReceiptEmailSectionProvider
{
    Task<IReadOnlyList<ReceiptEmailSections.Section>> SectionsAsync(Guid companyId, Guid receiptDocumentId, Guid paymentId, CancellationToken ct = default);
}

public sealed record PostPaymentRunDto(Guid Id, Guid PaymentId, string Source, string Status, string StatusLabel, int Attempts,
    string? Message, string? StepsDone, Guid? ReceiptDocumentId, DateTime CreatedAt, DateTime? CompletedAt);

public sealed record DocumentPayLinkStatusDto(
    string? Refusal,
    string? Url,
    DateTime? ExpiresAt,
    bool Usable,
    List<PayChannelOption> Channels,
    decimal Gross,
    decimal Wht,
    decimal Net,
    /// <summary>ฝ่ายค้าน S3: ใบตั้ง WHT ไว้แต่ผู้ซื้อไม่มีหน้าที่หัก (ลิงก์เรียกยอดเต็ม) — null = ไม่มีอะไรต้องเตือน</summary>
    string? WhtNote,
    List<PostPaymentRunDto> Runs)
{
    /// <summary>รอบ 267 ฝ่ายค้าน H2: ลูกค้ายังเปิดหน้าเอกสารด้วยลิงก์ของใบนี้ได้ไหม (ระดับหน้า — ไม่ขึ้นกับว่าจ่ายได้) ·
    /// หน้าพนักงานแสดงปุ่ม "ยกเลิกลิงก์" ตามค่านี้ (ทุกชนิดเอกสาร) · <see cref="PageUrl"/> = ลิงก์นั้น · <see cref="PageLinkNote"/> = คำอธิบายขอบเขต</summary>
    public bool PageLinkUsable { get; init; }
    public string? PageUrl { get; init; }
    public string? PageLinkNote { get; init; }

    /// <summary>รอบ 267 P4 (267-1-44): ค่าตั้ง "ออก/ส่งใบเสร็จอัตโนมัติหลังรับชำระ" ต่อใบ (ชื่อ Inherit · On · Off) + ข้อความค่าที่มีผลตอนนี้ (เซิร์ฟเวอร์ประกอบ —
    /// ชั้นไหนตัดสิน) · <see cref="PostPaySettingApplies"/> false = ใบชนิดนี้ตั้งไม่ได้ (หน้าไม่แสดงช่อง)</summary>
    public bool PostPaySettingApplies { get; init; }
    public string PostPayReceiptMode { get; init; } = AutopilotSettingsResolver.Inherit;
    public string PostPayPartialMode { get; init; } = AutopilotSettingsResolver.Inherit;
    public string? PostPayEffectiveLabel { get; init; }
}

/// <summary><paramref name="PromptPayQrDataUri"/> — รอบ 246 QR: QR พร้อมเพย์ที่เซิร์ฟเวอร์สร้าง (ฝังยอดสุทธิของลิงก์) · null = ไม่มีเลขพร้อมเพย์ที่ใช้ได้</summary>
/// <summary>รอบ 250 WP-03 — ผลของ <c>TransferForDocumentAsync</c>: <paramref name="Refusal"/> ไม่ว่าง = ใบนี้รับชำระทางนี้ไม่ได้ (ข้อความไทย) · ยอดค้าง/WHT/สุทธิ · กล่องโอน (null = ไม่มีบัญชีรับโอน)</summary>
public sealed record DocumentTransferView(string? Refusal, decimal Gross, decimal Wht, decimal Net, PublicTransferInfo? Transfer);

/// <summary>รอบ 253 (WP-10): คำขอยอมรับ "ไม่หัก ณ ที่จ่าย" — <paramref name="NoticeVersion"/> = version ของข้อความที่หน้าแสดง · <paramref name="Ip"/> ไม่เก็บดิบ (hash ผูกบริษัท)</summary>
public sealed record NoWhtAcceptRequest(bool Accepted, string? NoticeVersion, string? PayerName, string? Ip);

/// <summary><paramref name="Target"/> null = ไม่พบ token · <paramref name="Refusal"/> มีค่า = ไม่รับ (ข้อความไทย) · <paramref name="AcceptanceId"/> = แถวหลักฐาน</summary>
public sealed record NoWhtAcceptResult(PublicPayTarget? Target, Guid? AcceptanceId, string? Refusal);

public sealed record PublicTransferInfo(string? BankName, string? AccountNumber, string? AccountName, string? PromptPayId, string? PromptPayQrDataUri = null);

public sealed record PublicDocumentPayView(
    string CompanyName,
    string DocumentTitle,
    string DocumentNumber,
    DateTime DocumentDate,
    DateTime? DueDate,
    string? CustomerName,
    decimal Gross,
    decimal Wht,
    decimal Net,
    string Currency,
    bool CanPayOnline,
    string? BlockedNote,
    List<PayChannelOption> OnlineMethods,
    PublicTransferInfo? Transfer,
    PaymentClientSetup? Card,
    bool IsTestMode,
    // รอบ 253 (WP-10 · AUTO_SEND §6.3): ตัวเลือก "ไม่หัก ณ ที่จ่าย" (เซิร์ฟเวอร์ตัดสิน · หน้าแสดงอย่างเดียว) — ยอดเมื่อไม่หัก = Gross ของตัวคิดยอดเดิม
    bool NoWhtOffered = false,
    bool NoWhtWarn = false,
    string? NoWhtNotice = null,
    string? NoWhtNoticeVersion = null,
    decimal NoWhtAmount = 0m)
{
    /// <summary>เป้าหมายของรายการชำระ (ไม่ส่งออกใน JSON ของหน้า — controller เลือกช่องที่คืนเอง)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PublicPayTarget Target { get; init; } = null!;

    /// <summary>รอบ 267 P1: ส่วนหน้าเอกสาร (บรรทัด · ยอด · ปุ่มที่เซิร์ฟเวอร์ตัดสิน · ใบที่เกี่ยวข้อง) — ช่องเดิมด้านบนคงชื่อเดิม
    /// (หน้าชำระรุ่นก่อนที่เบราว์เซอร์แคชไว้ยังทำงาน) · null เฉพาะผู้สร้างรุ่นเก่า (เทสต์)</summary>
    public CustomerDocPageView? Page { get; init; }

    /// <summary>รอบ 255 WP-12B ข้อ 1: หน้าชำระแสดงฟอร์ม "แจ้งโอน + แนบสลิป" ไหม (เซิร์ฟเวอร์ตัดสิน — <c>SlipEvidenceRule.PayLinkSlipAllowed</c>) ·
    /// ใช้ได้ทั้งโหมดโอนอย่างเดียว (ไม่มีช่องทางออนไลน์) — ไม่ผูกกับ <see cref="CanPayOnline"/></summary>
    public bool CanSubmitSlip { get; init; }

    /// <summary>รอบ 257 ชุด 4 (A L14 · server computes): บอกลูกค้าว่าสลิปจากหน้านี้บันทึกเป็นยอดเท่าไร (ยอดสุทธิของลิงก์เสมอ) — เดิมไม่บอก ⇒ โอนยอดเต็มแล้วงง ·
    /// null = ไม่มีฟอร์มสลิป</summary>
    public string? SlipAmountNote { get; init; }

    /// <summary>ยอดที่สลิปจากหน้านี้แทน (ยอดสุทธิของลิงก์ — ไม่รับยอดจากหน้าเว็บ) · ไม่ส่งออกใน JSON</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal SlipAmount { get; init; }

    /// <summary>เอกสารของลิงก์ (สลิปผูกใบนี้) · ไม่ส่งออกใน JSON</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid SlipDocumentId { get; init; }
}
