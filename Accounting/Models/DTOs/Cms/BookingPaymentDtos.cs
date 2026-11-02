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
    /// <summary>รอบ 223: คำเตือนช่องทาง (ติ๊กแล้วแต่ลูกค้าจะไม่เห็น — เช่น ติ๊กโอนแต่ไม่มีเลขบัญชี/พร้อมเพย์/QR) · ตัวตัดสิน <c>BookingPaymentChannels.Warnings</c></summary>
    public List<string> ChannelWarnings { get; set; } = new();
    /// <summary>ผลกับแต่ละบริการ (ตัวตัดสินเดียว) — บริการที่ "ไม่มีผล" แสดงเหตุผล</summary>
    public List<BookingPaymentServiceEffectDto> Services { get; set; } = new();
    /// <summary>รอบ 226 (คำตัดสิน 226-2): ค่าตั้งต้นของ "ต้องชำระภายใน" เมื่อโหมดบังคับแต่ไม่ได้กรอก (นาที)</summary>
    public int DefaultDeadlineMinutes { get; set; } = BookingPaymentPolicy.DefaultRequiredDeadlineMinutes;
    /// <summary>รอบ 226 (คำตัดสิน 226-1): โหมดบังคับ + บริการที่เก็บเงินล่วงหน้าไม่ได้ ⇒ กล่องแดงพร้อมรายชื่อ (null = ไม่มี) ·
    /// บริการในรายการดูที่ <see cref="Services"/> ที่ <c>RequiredButUncollectable</c></summary>
    public string? UncollectableProblem { get; set; }
    /// <summary>ฝ่ายค้านรอบ 226 ข้อ 3: คำแนะนำ (ไม่ใช่ข้อผิดพลาด) — เว็บเดิมที่ไม่เคยกรอกกำหนดชำระถูกคงเป็น "ไม่กำหนด" (0) · null = ไม่มี</summary>
    public string? DeadlineNote { get; set; }
}

/// <summary>รอบ 226: แก้บริการที่โหมดบังคับเก็บเงินไม่ได้ในคลิกเดียว — "convert" = เปลี่ยนชนิดเป็นชำระมัดจำล่วงหน้า · "exempt" = ยกเว้น (บริการนี้ไม่รับชำระล่วงหน้า)</summary>
public class FixUncollectableServicesRequest
{
    public string Action { get; set; } = "";
    public List<Guid> ServiceIds { get; set; } = new();
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
    /// <summary>รอบ 226: โหมดบังคับแต่ชนิดบริการเก็บเงินไม่ได้ ⇒ การจองรอร้านยืนยัน (ปุ่มแก้: เปลี่ยนชนิด / ยกเว้น)</summary>
    public bool RequiredButUncollectable { get; set; }
    public bool IsActive { get; set; }
}

public class BookingPaymentPreviewRequest
{
    public Guid BookingServiceId { get; set; }
    public int Units { get; set; } = 1;
    /// <summary>รอบ 226: วิธีรับที่ลูกค้าเลือก + ที่อยู่ (คิดค่าส่งก่อนกดจอง — ตัวคิดเดียวกับตอนสร้างการจอง)</summary>
    public string? FulfillmentMethod { get; set; }
    public string? DeliveryProvince { get; set; }
    public string? DeliveryPostalCode { get; set; }
    public Guid? DeliveryRateId { get; set; }
}

/// <summary>รอบ 226: ขั้น "จัดส่ง / รับเอง" ก่อนกดจอง — server ตัดสินทั้งหมด (วิธีที่เปิด · ค่าส่ง · ปัญหา) หน้าเว็บแสดงอย่างเดียว</summary>
public class BookingFulfillmentQuoteDto
{
    public bool Enabled { get; set; }
    public bool DeliveryEnabled { get; set; }
    public bool PickupEnabled { get; set; }
    public string? DefaultMethod { get; set; }
    /// <summary>วิธีที่ใช้คิด (null = ยังไม่เลือก — เปิดสองแบบและไม่มีค่าตั้งต้น)</summary>
    public string? Method { get; set; }
    public string? MethodLabel { get; set; }
    public decimal DeliveryFee { get; set; }
    public List<BookingDeliveryRateOptionDto> RateOptions { get; set; } = new();
    public Guid? SelectedRateId { get; set; }
    /// <summary>ข้อความถึงลูกค้า (กรอกจังหวัด/รหัสไปรษณีย์ · ไม่มีวิธีส่งสำหรับพื้นที่นี้ · ยอดไม่ถึงขั้นต่ำ) · null = คิดได้</summary>
    public string? Problem { get; set; }
    public string? PickupNote { get; set; }
    public decimal? DeliveryMinAmount { get; set; }
}

public class BookingDeliveryRateOptionDto
{
    public Guid RateId { get; set; }
    public string Name { get; set; } = "";
    public string? ZoneName { get; set; }
    public decimal Fee { get; set; }
    public bool IsFree { get; set; }
}

/// <summary>รอบ 226: ค่าตั้งจัดส่ง/รับเองของการจองระดับเว็บ (GET/PUT <c>…/booking/fulfillment-settings</c>) · อัตราค่าส่งใช้โซน/อัตราของร้าน
/// (ตั้งค่าร้านค้า › โซน/อัตราค่าส่ง) + ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบของร้าน — ช่องท้ายอ่านอย่างเดียว</summary>
public class BookingFulfillmentSettingsDto
{
    public bool DeliveryEnabled { get; set; }
    public bool PickupEnabled { get; set; }
    public string? DefaultMethod { get; set; }
    public string? PickupNote { get; set; }
    public decimal? DeliveryMinAmount { get; set; }
    // ── อ่านอย่างเดียว ──
    public int ShippingZoneCount { get; set; }
    public decimal? ShopFlatShippingRate { get; set; }
    public decimal? ShopFreeShippingThreshold { get; set; }
    /// <summary>ข้อความสรุปว่าค่าส่งของการจองคิดอย่างไร (โซนของร้าน / เหมาจ่าย / ฟรี)</summary>
    public string? FeeSourceSummary { get; set; }
    public List<BookingFulfillmentServiceEffectDto> Services { get; set; } = new();
}

public class BookingFulfillmentServiceEffectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool UsesSiteSetting { get; set; }
    public string Summary { get; set; } = "";
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
    /// <summary>รอบ 223: ที่มาของยอด (เช่น "มัดจำ 50% ของค่าบริการ ฿35.00 = ฿17.50" / "มัดจำคงที่ ฿10.00") — <c>BookingPaymentPolicy.AmountBasis</c></summary>
    public string? AmountBasis { get; set; }
    /// <summary>รอบ 223: หลังกดจองจะเกิดอะไร (ไปขั้นชำระเงิน · ช่องทาง) — <c>BookingPaymentStep.PreviewNextStep</c></summary>
    public string? NextStepNote { get; set; }
    /// <summary>รอบ 223: ป้ายปุ่มกดจองเมื่อบังคับจ่าย / เมื่อลูกค้าติ๊กจ่ายมัดจำ (null = ป้ายเดิม)</summary>
    public string? SubmitLabel { get; set; }
    public string? SubmitLabelIfPay { get; set; }
    /// <summary>รอบ 226: ขั้นจัดส่ง/รับเอง (null = บริการไม่มีขั้นนี้) · ยอดรวมที่ลูกค้าจ่าย = ค่าบริการ + ค่าจัดส่ง</summary>
    public BookingFulfillmentQuoteDto? Fulfillment { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal GrandTotal { get; set; }
    /// <summary>รอบ 226 (คำตัดสิน 226-1): ร้านตั้งชำระล่วงหน้าแบบบังคับแต่บริการนี้เก็บในระบบไม่ได้ ⇒ กดจองแล้ว "รอร้านยืนยัน" (ไม่ยืนยันทันที)</summary>
    public bool ShopArrangesPayment { get; set; }
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
    /// <summary>รอบ 223: ที่มาของยอดมัดจำ (สูตร % / ยอดคงที่ / ปรับขั้นต่ำ-สูงสุด) — <c>BookingPaymentPolicy.AmountBasis</c></summary>
    public string? AmountBasis { get; set; }
    /// <summary>รอบ 223: ขั้นของลูกค้า (ชื่อ <c>BookingPaymentStepKind</c>) + หัวข้อ/รายละเอียด/ไอคอนหน้าผลการจอง — <c>BookingPaymentStep.Decide</c></summary>
    public string Step { get; set; } = nameof(BookingPaymentStepKind.None);
    public string? Headline { get; set; }
    public string? StepDetail { get; set; }
    public string? StepIcon { get; set; }
    /// <summary>true = หน้าผลการจองวาดกล่องชำระเงินก่อนรายละเอียด (ขั้นชำระมาก่อนคำยืนยัน)</summary>
    public bool PaymentFirst { get; set; }
    /// <summary>true = มีสลิปแนบอยู่ในแฟ้มของการจอง (รอตรวจ หรือยืนยันแล้ว) — พนักงานเปิดดูได้ผ่าน <see cref="SlipFileUrl"/></summary>
    public bool SlipOnFile { get; set; }
    /// <summary>พนักงานเท่านั้น (คำตอบลูกค้าถูกล้าง) — รายการชำระ manual-slip ของสลิปที่รอตรวจ</summary>
    public Guid? SlipIntentId { get; set; }
    /// <summary>พนักงานเท่านั้น — URL ดาวน์โหลดสลิป (ต้องล็อกอิน + สิทธิ์)</summary>
    public string? SlipFileUrl { get; set; }
    /// <summary>รอบ 226: พนักงานเท่านั้น — นโยบายชำระที่ตั้งไว้ใช้กับการจองนี้ไม่ได้ (โหมดบังคับ + ชนิดบริการเก็บเงินไม่ได้) พร้อมทางแก้</summary>
    public string? ShopPolicyWarning { get; set; }
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
