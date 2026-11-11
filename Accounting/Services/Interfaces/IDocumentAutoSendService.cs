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
    Task<PayLinkResult> EnsureLinkAsync(Guid companyId, Guid documentId, IReadOnlyCollection<string>? scheduleChannels, CancellationToken ct = default,
        bool allowReissueRevoked = false, string? actor = null);

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
    /// <summary>รอบ 253 (WP-10): ลูกค้าเลือก "ไม่หัก ณ ที่จ่าย" — บันทึกการยอมรับแล้วคืนเป้าหมายยอดเต็ม · <c>Target</c> null = ไม่พบ token</summary>
    Task<NoWhtAcceptResult> AcceptNoWhtAsync(Guid companyId, string token, NoWhtAcceptRequest request, CancellationToken ct = default);
    /// <summary>รอบ 253 (WP-10): ผูกการยอมรับ "ไม่หัก" กับรายการชำระที่สร้างตามมา</summary>
    Task LinkNoWhtIntentAsync(Guid companyId, Guid acceptanceId, Guid paymentIntentId, CancellationToken ct = default);
    /// <summary>รอบ 250 WP-03 (ฝ่ายค้าน H1): ตัวเลือกโอน/QR ของใบสำหรับพอร์ทัล — กติกาเดียวกับหน้าชำระ (IssueRefusal · ยอดสุทธิหลัง WHT · บัญชีของใบ) · null = ไม่พบใบ</summary>
    Task<DocumentTransferView?> TransferForDocumentAsync(Guid companyId, Guid documentId, CancellationToken ct = default);
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
}

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
    bool? CanEdit = null);

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
    List<PostPaymentRunDto> Runs);

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
