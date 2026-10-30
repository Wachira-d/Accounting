using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// รอบ 209 · P1 — DTO ฝั่งลูกค้าหน้าร้าน (แยกจาก OrderResponse ของหลังบ้าน — ห้าม reuse เพราะตัวนั้นมี InternalNotes/สลิป/ที่อยู่)

/// <summary>หน้าสถานะออเดอร์ของแขก (<c>GET commerce/orders/public/{token}</c>) — ฟิลด์จำกัดตามคำตัดสินข้อ 168 ข ·
/// ทุกค่าคำนวณที่เซิร์ฟเวอร์ (<c>Helpers.SiteOrderPublic.ToPublic</c>) · หน้าเว็บแสดงอย่างเดียว</summary>
public class PublicOrderResponse
{
    /// <summary>id ของออเดอร์ — ใช้กับทางเดิมที่แขกถืออยู่แล้ว (แนบสลิป · จ่ายออนไลน์ · ขอใบเสนอราคา)</summary>
    public Guid Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal AmountDueNow { get; set; }
    public decimal NextPayableAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    public string? PaymentTermMode { get; set; }
    public string? PaymentTermLabel { get; set; }
    public decimal DepositRequiredAmount { get; set; }
    public string? DepositInvoicingNote { get; set; }
    /// <summary>yyyy-MM-dd · null = ไม่มีสินค้าตามสั่ง</summary>
    public string? EarliestReadyDate { get; set; }
    public string FulfillmentMethod { get; set; } = "delivery";
    public string FulfillmentLabel { get; set; } = "";
    public string? ShippingMethod { get; set; }
    public string? ShippingCarrier { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>ลิงก์ติดตามพัสดุ — เฉพาะผู้ขนส่งที่รู้รูปแบบ URL · null = แสดงเลขอย่างเดียว</summary>
    public string? TrackingUrl { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public string? OnlinePaymentBlockedNote { get; set; }
    /// <summary>มี QR พร้อมเพย์ให้สแกน (ร้านตั้งพร้อมเพย์ + มียอดค้าง) — รูปสร้างที่เซิร์ฟเวอร์ (<c>commerce/promptpay-qr</c>)</summary>
    public bool PromptPayQrAvailable { get; set; }
    /// <summary>เลขใบรับเงินมัดจำที่ออกแล้ว (อ้างอิงกับร้าน — ไม่มีลิงก์เปิดเอกสารสาธารณะ)</summary>
    public List<string> DepositDocumentNumbers { get; set; } = new();
    public List<PublicOrderLineResponse> Lines { get; set; } = new();
}

public class PublicOrderLineResponse
{
    public string ProductName { get; set; } = "";
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
    public string? ModifierSummary { get; set; }
}

/// <summary>ข้อมูลบัญชีลูกค้าหน้าร้านของตัวเอง (<c>GET portal/me</c>)</summary>
public class StorefrontMeResponse
{
    public Guid CustomerId { get; set; }
    public string Email { get; set; } = "";
    public string? FullName { get; set; }
    /// <summary>ผูกผู้ติดต่อในระบบบัญชีแล้ว (จำเป็นสำหรับบัตรคอร์ส)</summary>
    public bool HasErpContact { get; set; }

    // ── รอบ 216 (คำตัดสินข้อ 174): ข้อมูลส่วนตัวของสมาชิก — ใช้เติมฟอร์มจอง/สั่งซื้อ/จองที่พัก · แก้ที่ PUT portal/me/profile ──
    /// <summary>เบอร์โทร (ตัวเลขล้วน · normalize แล้ว)</summary>
    public string? Phone { get; set; }
    /// <summary>ชื่อผู้รับใบกำกับภาษี (บุคคล/นิติบุคคล)</summary>
    public string? CompanyName { get; set; }
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public StorefrontAddressDto? ShippingAddress { get; set; }
    public StorefrontAddressDto? BillingAddress { get; set; }
    /// <summary>ที่อยู่จัดส่งเป็นข้อความ (ไม่รวมจังหวัด/รหัสไปรษณีย์ — เช็กเอาต์มีช่องแยก) · เซิร์ฟเวอร์ประกอบ (<c>StorefrontMemberProfile.ComposeAddress</c>)</summary>
    public string? ShippingAddressText { get; set; }
    /// <summary>ที่อยู่ใบกำกับเป็นข้อความเต็มบรรทัดเดียว (รวมจังหวัด/รหัสไปรษณีย์)</summary>
    public string? BillingAddressText { get; set; }
}

/// <summary>ที่อยู่ของสมาชิก (โครงเดียวกับ <c>SiteCustomerAddress</c>)</summary>
public class StorefrontAddressDto
{
    public string? RecipientName { get; set; }
    public string? Phone { get; set; }
    /// <summary>บ้านเลขที่ · หมู่บ้าน/อาคาร · ถนน</summary>
    public string? AddressLine { get; set; }
    public string? SubDistrict { get; set; }
    public string? District { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
}

/// <summary>รอบ 216: สมาชิกแก้ข้อมูลส่วนตัวของตัวเอง (<c>PUT portal/me/profile</c>) — ส่งครบทุกช่อง · ว่าง = ล้างค่า · อีเมลเข้าระบบแก้ไม่ได้ ·
/// ตัวตรวจ <c>Helpers/StorefrontMemberProfile.Validate</c></summary>
public class UpdateStorefrontProfileRequest
{
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? CompanyName { get; set; }
    public string? TaxId { get; set; }
    public string? BranchCode { get; set; }
    public StorefrontAddressDto? ShippingAddress { get; set; }
    public StorefrontAddressDto? BillingAddress { get; set; }
}

public class StorefrontMyOrderItem
{
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime CreatedAt { get; set; }
    /// <summary>โทเคนหน้าสถานะ — พาไป <c>track-order/{token}</c></summary>
    public string? PublicToken { get; set; }
    public string? ShippingCarrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
}

public class StorefrontMyBookingItem
{
    public string BookingNumber { get; set; } = "";
    public string ServiceName { get; set; } = "";
    /// <summary>yyyy-MM-dd</summary>
    public string BookingDate { get; set; } = "";
    /// <summary>HH:mm</summary>
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public BookingStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    /// <summary>โทเคนหน้า my-booking (ยกเลิก/เลื่อน/จ่ายมัดจำ — ทางเดิม) · ใบเก่า null = ไม่มีลิงก์</summary>
    public string? PublicToken { get; set; }
}

public class StorefrontMyCreditItem
{
    public Guid PassId { get; set; }
    public string PassNumber { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string UnitLabel { get; set; } = "";
    public decimal CreditsTotal { get; set; }
    public decimal CreditsUsed { get; set; }
    public decimal CreditsReserved { get; set; }
    public decimal CreditsAvailable { get; set; }
    /// <summary>yyyy-MM-dd · null = ไม่หมดอายุ</summary>
    public string? ExpiresOn { get; set; }
    public bool IsExpired { get; set; }
    public CreditPassStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    /// <summary>ใช้จองได้ตอนนี้ (Active · ไม่หมดอายุ · เหลือเครดิต) — เซิร์ฟเวอร์ตัดสินซ้ำตอนจองจริง</summary>
    public bool Usable { get; set; }
}

// ── รอบ 210 (คำตัดสินข้อ 169): แขกค้นการจอง/คำสั่งซื้อด้วยเลขที่ + เบอร์โทรหรืออีเมล (ตัวตัดสิน Helpers/GuestRecordLookup) ──

/// <summary>ค้นการจอง — <see cref="Contact"/> = เบอร์โทรหรืออีเมลที่ใช้ตอนจอง</summary>
public record GuestBookingLookupRequest(string? BookingNumber, string? Contact);

/// <summary>ค้นคำสั่งซื้อ — <see cref="Contact"/> = เบอร์โทรหรืออีเมลที่ใช้ตอนสั่ง</summary>
public record GuestOrderLookupRequest(string? OrderNumber, string? Contact);

/// <summary>ผลค้นที่ตรง — เฉพาะโทเคนสาธารณะ + ชนิด ("booking" ⇒ หน้า my-booking · "order" ⇒ หน้า track-order) · ไม่มีข้อมูลรายการ</summary>
public record GuestLookupResponse(string PublicToken, string Kind);
