using Accounting.Helpers;

namespace Accounting.Models.DTOs.Cms;

// รอบ 218 (คำตัดสินข้อ 177 · erp-review/2026-10-30/BOOKING_PAYMENT_DESIGN.md) — การชำระล่วงหน้าของการจองนัดหมาย

/// <summary>ค่าตั้งการชำระล่วงหน้าของเว็บ (GET/PUT <c>…/booking/payment-settings</c>) · ช่องทางโอนเก็บที่แถว <c>SitePaymentGateway</c> ของเว็บ
/// (ตารางเดียวกับคำสั่งซื้อ) · ช่องท้าย "อ่านอย่างเดียว" server ประกอบ — PUT ไม่อ่าน</summary>
public class BookingPaymentSettingsDto
{
    /// <summary>"" = ยังไม่ได้ตั้ง (แบบเดิม) · ชื่อ <c>BookingPaymentMode</c></summary>
    public string? Mode { get; set; }
    public decimal? DepositFixedAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public decimal? DepositMinAmount { get; set; }
    public decimal? DepositMaxAmount { get; set; }
    /// <summary>ชำระภายในกี่นาทีหลังจอง (โหมดบังคับ) · null/0 = ไม่กำหนด</summary>
    public int? DeadlineMinutes { get; set; }
    /// <summary>ชำระออนไลน์ผ่านระบบรับชำระเงินของบริษัท (ช่องเดิม <c>OnlineDepositEnabled</c>)</summary>
    public bool OnlineGatewayEnabled { get; set; }
    public bool BankTransferEnabled { get; set; } = true;
    public string? PaymentNote { get; set; }

    // ── ช่องทางโอน (แถว SitePaymentGateway แรกที่ใช้งานของเว็บ) — null ทั้งชุด = ไม่แตะแถวเดิม (PUT) ──
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }
    public string? BankBranch { get; set; }
    public string? PromptPayId { get; set; }
    public string? PromptPayQrUrl { get; set; }
    /// <summary>PUT: true = บันทึกช่องทางโอนตามช่องด้านบน (ว่าง = ล้าง) · false = ไม่แตะแถวช่องทาง</summary>
    public bool UpdateTransfer { get; set; }
    /// <summary>ฝ่ายค้านรอบ 218 (B1): ยืนยันการล้างช่องบัญชีรับโอนที่มีค่าอยู่ (ใช้ร่วมกับคำสั่งซื้อ) · false + มีช่องถูกล้าง ⇒ 409 พร้อมรายชื่อช่อง</summary>
    public bool ConfirmClearTransfer { get; set; }

    // ── อ่านอย่างเดียว ──
    public List<BookingPaymentModeOption> ModeOptions { get; set; } = new();
    public bool CompanyGatewayConfigured { get; set; }
    public string? CompanyGatewayName { get; set; }
    public bool GatewayReady { get; set; }
    public bool TransferReady { get; set; }
    public bool PromptPayIdValid { get; set; }
    /// <summary>ปัญหาของค่าตั้งปัจจุบัน (เช่น โหมดบังคับแต่ไม่มีช่องทาง) · null = ไม่มี</summary>
    public string? Problem { get; set; }
    /// <summary>ผลกับแต่ละบริการ (ตัวตัดสินเดียว) — บริการที่ "ไม่มีผล" แสดงเหตุผล</summary>
    public List<BookingPaymentServiceEffectDto> Services { get; set; } = new();
}

public class BookingPaymentServiceEffectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string BookingType { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Problem { get; set; }
    public bool Collects { get; set; }
    public bool UsesSiteSetting { get; set; }
}

public class BookingPaymentPreviewRequest
{
    public Guid BookingServiceId { get; set; }
    public int Units { get; set; } = 1;
}

/// <summary>สิ่งที่ลูกค้าต้องรู้ก่อนกดจอง — ยอด/บังคับไหม/กำหนดเวลา/ช่องทาง/นโยบายยกเลิก (server ตัดสิน · หน้าเว็บแสดงอย่างเดียว)</summary>
public class BookingPaymentPreviewResponse
{
    public string Mode { get; set; } = "";
    public string ModeLabel { get; set; } = "";
    public bool Collects { get; set; }
    public bool Required { get; set; }
    public bool Optional { get; set; }
    public decimal TotalAmount { get; set; }
    /// <summary>ยอดที่ต้อง/เลือกจ่ายตอนจอง (มัดจำไม่บังคับ = ยอดถ้าเลือกจ่าย)</summary>
    public decimal AmountDue { get; set; }
    public string Currency { get; set; } = "THB";
    public int DeadlineMinutes { get; set; }
    public string? Summary { get; set; }
    public bool GatewayAvailable { get; set; }
    public bool TransferAvailable { get; set; }
    public string? CancellationPolicy { get; set; }
    public string? PaymentNote { get; set; }
}

public class BookingTransferInfoDto
{
    public string? BankName { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountName { get; set; }
    public string? Branch { get; set; }
    public string? PromptPayId { get; set; }
    public string? QrImageUrl { get; set; }
}

/// <summary>การชำระล่วงหน้าบน <c>BookingResponse</c> — server ประกอบ (<c>BookingPaymentPolicy</c> · <c>BookingPaymentChannels</c>)</summary>
public class BookingPaymentInfoDto
{
    /// <summary>null = แบบเดิม (ไม่ได้ตั้งโหมด)</summary>
    public string? Mode { get; set; }
    public string ModeLabel { get; set; } = "";
    /// <summary>ยอดค้างที่ต้องชำระล่วงหน้า (มัดจำ − ที่รับแล้ว) · 0 = ไม่มี/ครบ</summary>
    public decimal AmountDue { get; set; }
    public string? StatusLabel { get; set; }
    public DateTime? HoldUntil { get; set; }
    public bool SlipPending { get; set; }
    public DateTime? SlipUploadedAt { get; set; }
    public string? SlipRejectedReason { get; set; }
    public bool CanUploadSlip { get; set; }
    public bool GatewayAvailable { get; set; }
    public bool TransferAvailable { get; set; }
    public bool PromptPayQrAvailable { get; set; }
    public BookingTransferInfoDto? Transfer { get; set; }
    public string? Summary { get; set; }
    public string? PaymentNote { get; set; }
    /// <summary>พนักงานเท่านั้น (คำตอบลูกค้าถูกล้าง) — รายการชำระ manual-slip ของสลิปที่รอตรวจ</summary>
    public Guid? SlipIntentId { get; set; }
    /// <summary>พนักงานเท่านั้น — URL ดาวน์โหลดสลิป (ต้องล็อกอิน + สิทธิ์)</summary>
    public string? SlipFileUrl { get; set; }
}

public class RejectBookingSlipRequest
{
    public string? Reason { get; set; }
}

public class VerifyBookingSlipRequest
{
    /// <summary>บันทึกถึงผู้สอบบัญชี (เช่น "ตรวจยอดในบัญชีธนาคารแล้ว") — ว่าง = ข้อความมาตรฐาน · ตัดที่ 300 ตัว · เก็บในหมายเหตุ ไม่ใช่ผู้ยืนยัน</summary>
    public string? Note { get; set; }
    /// <summary>ฝ่ายค้านรอบ 218 (B2): วันที่โอนตามสลิป (วันที่ไทย yyyy-MM-dd) = วันที่ใบรับมัดจำ (§78/1) · ว่าง = เวลาที่ลูกค้าแนบสลิป</summary>
    public DateOnly? TransferDate { get; set; }
}
