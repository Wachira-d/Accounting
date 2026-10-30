using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Site Product ====================

public class CreateSiteProductRequest
{
    [Required]
    public Guid ProductId { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior StockBehavior { get; set; } = StockBehavior.InStockOnly;
    public DateTime? PreorderAvailableDate { get; set; }
    public int? MaxOrderQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsFeatured { get; set; } = false;
    public int SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public List<CreatePricingTierRequest>? PricingTiers { get; set; }
    /// <summary>สินค้าตามสั่ง: ผลิตกี่วัน (null = ไม่ใช่สินค้าตามสั่ง) · ทีม K</summary>
    public int? LeadTimeDays { get; set; }
    /// <summary>R-41 B2: ขายเป็นคอร์ส — แพ็กเกจคอร์สที่ผูก (บรรทัดใบขายลง 21713 + เปิดบัตรหลังอนุมัติ) · null = สินค้าปกติ</summary>
    public Guid? CreditPackageId { get; set; }
    /// <summary>น้ำหนักต่อชิ้น (กก.) สำหรับค่าส่งตามน้ำหนัก · null = ไม่คิดน้ำหนัก</summary>
    public decimal? WeightKg { get; set; }
}

public class UpdateSiteProductRequest
{
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior? StockBehavior { get; set; }
    public DateTime? PreorderAvailableDate { get; set; }
    public int? MaxOrderQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public bool? IsVisible { get; set; }
    public bool? IsFeatured { get; set; }
    public int? SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    /// <summary>null = ไม่เปลี่ยน · ล้างค่า (เลิกเป็นสินค้าตามสั่ง) ใช้ <see cref="ClearLeadTime"/></summary>
    public int? LeadTimeDays { get; set; }
    public bool ClearLeadTime { get; set; }
    /// <summary>R-41 B2: null = ไม่เปลี่ยน · เลิกขายเป็นคอร์ส ใช้ <see cref="ClearCreditPackage"/></summary>
    public Guid? CreditPackageId { get; set; }
    public bool ClearCreditPackage { get; set; }
    /// <summary>null = ไม่เปลี่ยน · ล้างค่าใช้ <see cref="ClearWeight"/></summary>
    public decimal? WeightKg { get; set; }
    public bool ClearWeight { get; set; }
}

/// <summary>กลุ่มตัวเลือกของสินค้าบนหน้าร้าน (ไม่เปิดเผยข้อมูลหลังบ้าน) — ขอบเขตจำนวนเลือกเป็นค่าที่ด่านใช้จริง</summary>
public class StorefrontOptionGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string InputType { get; set; } = "radio";
    public bool IsRequired { get; set; }
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; }
    public int? MaxTextLength { get; set; }
    public decimal InputPriceAdjustment { get; set; }
    public int InputExtraLeadDays { get; set; }
    public List<StorefrontOptionValue> Options { get; set; } = new();
}

public class StorefrontOptionValue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal PriceAdjustment { get; set; }
    public int ExtraLeadDays { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>ตัวเลือกที่ลูกค้าเลือก — <b>ไม่มีราคา</b> โดยตั้งใจ (เซิร์ฟเวอร์คิดเองจากฐานข้อมูล)</summary>
public class CartModifierChoice
{
    public Guid GroupId { get; set; }
    public List<Guid>? OptionIds { get; set; }
    public string? Text { get; set; }
    public Guid? AttachmentId { get; set; }
}

public class SiteProductResponse
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNameEn { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public decimal ErpSellingPrice { get; set; }
    public decimal? OverrideSellingPrice { get; set; }
    public decimal EffectivePrice { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public StockBehavior StockBehavior { get; set; }
    public DateTime? PreorderAvailableDate { get; set; }
    public decimal AvailableStock { get; set; }
    public Guid? SiteCategoryId { get; set; }
    public string? CategoryName { get; set; }
    public bool IsVisible { get; set; }
    public bool IsFeatured { get; set; }
    public int SortOrder { get; set; }
    public string? Tags { get; set; }
    public string? Slug { get; set; }
    public string? ImageUrlsJson { get; set; }
    /// <summary>Master Product gallery JSON — used as fallback when
    /// SiteProduct.ImageUrlsJson hasn't been overridden. Not exposed
    /// to the storefront client; consumed server-side to compute
    /// FeaturedImageUrl + merge into ImageUrlsJson before serialization.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProductImageUrlsJson { get; set; }
    /// <summary>First URL extracted from ImageUrlsJson — what the
    /// product card on the storefront shows as the main thumbnail.
    /// Computed server-side so the client doesn't have to parse
    /// JSON-in-JSON and pick the first entry every render.</summary>
    public string? FeaturedImageUrl { get; set; }
    public List<PricingTierResponse>? PricingTiers { get; set; }
    public DateTime CreatedAt { get; set; }
    // ── สินค้าตามสั่ง (ทีม K) ──
    public int? LeadTimeDays { get; set; }
    /// <summary>R-41 B2: แพ็กเกจคอร์สที่สินค้านี้ขาย · null = สินค้าปกติ</summary>
    public Guid? CreditPackageId { get; set; }
    public decimal? WeightKg { get; set; }
    /// <summary>วันรับเร็วสุดถ้าสั่งตอนนี้ (yyyy-MM-dd · ยังไม่รวมวันเพิ่มของตัวเลือก) — null = ไม่ใช่สินค้าตามสั่ง</summary>
    public string? EarliestReadyDate { get; set; }
    /// <summary>กลุ่มตัวเลือก (dynamic option builder) — null/ว่าง = ไม่มี</summary>
    public List<StorefrontOptionGroup>? OptionGroups { get; set; }
    /// <summary>รอบ 209 P6 (E5): ตัวเลือกย่อยที่เลือกได้ (ราคา/มีของคิดที่เซิร์ฟเวอร์) — null = สินค้าไม่มี variant (หน้าร้านไม่แสดงตัวเลือก)</summary>
    public List<StorefrontVariant>? Variants { get; set; }
}

/// <summary>รอบ 209 P6 (E5): ตัวเลือกย่อยบนหน้าร้าน — ราคาสุดท้าย (variant ตั้งราคา ⇒ ราคานั้น · ไม่ตั้ง ⇒ ราคาสินค้า) · ไม่ส่งยอดสต็อกจริง (แค่มี/หมด)</summary>
public class StorefrontVariant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string? OptionsJson { get; set; }
    public string? ImageUrl { get; set; }
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public bool InStock { get; set; }
}

// ==================== Pricing Tier ====================

public class CreatePricingTierRequest
{
    [Required, MaxLength(128)]
    public string TierName { get; set; } = "";
    public int MinQuantity { get; set; } = 1;
    public int? MaxQuantity { get; set; }
    [Required]
    public decimal UnitPrice { get; set; }
    public string? CustomerGroupTag { get; set; }
}

public class PricingTierResponse
{
    public Guid Id { get; set; }
    public string TierName { get; set; } = "";
    public int MinQuantity { get; set; }
    public int? MaxQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string? CustomerGroupTag { get; set; }
}

// ==================== Category ====================

public class CreateCategoryRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(256)]
    public string? NameEn { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public int SortOrder { get; set; }
}

public class CategoryResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public Guid? ParentCategoryId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public int ProductCount { get; set; }
    public List<CategoryResponse>? Children { get; set; }
}

// ==================== Cart ====================

public class AddToCartRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? VariantOptionsJson { get; set; }
    public string? Notes { get; set; }
    /// <summary>ตัวเลือกสินค้าตามสั่ง (ทีม K) — ราคา/วันเพิ่มคิดที่เซิร์ฟเวอร์</summary>
    public List<CartModifierChoice>? Modifiers { get; set; }
    /// <summary>รอบ 209 P6 (E5): ตัวเลือกย่อย (ไซซ์/สี) — บังคับเมื่อสินค้ามี variant ใช้งานอยู่ · ราคา/สต็อกคิดที่เซิร์ฟเวอร์ (Helpers/CmsVariantPricing)</summary>
    public Guid? VariantId { get; set; }
}

public class UpdateCartItemRequest
{
    [Range(1, int.MaxValue)]
    public decimal Quantity { get; set; }
}

public class CartResponse
{
    public Guid Id { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public string? CouponCode { get; set; }
    public List<CartItemResponse> Items { get; set; } = new();
}

public class CartItemResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public string? ImageUrl { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string? VariantOptionsJson { get; set; }
    /// <summary>คำอธิบายตัวเลือกที่เลือก ("ขนาด: 2 ปอนด์ · ข้อความ: HBD") — เซิร์ฟเวอร์ประกอบ</summary>
    public string? ModifierSummary { get; set; }
    public int ExtraLeadDays { get; set; }
    /// <summary>รอบ 209 P6 (E5): ตัวเลือกย่อยที่เลือก (null = สินค้าไม่มี variant)</summary>
    public Guid? VariantId { get; set; }
    public string? VariantName { get; set; }
}

// ==================== Order ====================

/// <summary>
/// At least one of CartId or Lines must be provided. CartId creates an order from an existing cart;
/// Lines allows creating a direct order without a cart.
/// </summary>
public class CreateOrderRequest
{
    public Guid? CustomerId { get; set; }
    public Guid? CartId { get; set; }

    // Shipping
    public string? ShippingName { get; set; }
    public string? ShippingAddress { get; set; }
    public string? ShippingPhone { get; set; }
    public string? ShippingEmail { get; set; }
    public string? ShippingMethod { get; set; }

    // Billing (for tax invoice)
    public string? BillingName { get; set; }
    public string? BillingAddress { get; set; }
    public string? BillingTaxId { get; set; }
    public string? BillingBranchCode { get; set; }
    public bool RequestTaxInvoice { get; set; } = false;

    public Guid? PaymentGatewayId { get; set; }
    public string? CouponCode { get; set; }
    public string? CustomerNotes { get; set; }

    // ── การรับ + ค่าส่ง (ทีม K) — เซิร์ฟเวอร์คิดค่าส่งเองจากอัตราที่เลือก ไม่รับยอดจาก client ──
    /// <summary>"delivery" / "pickup" · ว่าง = จัดส่ง</summary>
    public string? FulfillmentMethod { get; set; }
    public Guid? ShippingRateId { get; set; }
    public string? ShippingCountryCode { get; set; }
    public string? ShippingProvince { get; set; }
    public string? ShippingPostalCode { get; set; }

    // ── รอบ 209 P2 (คำตัดสินข้อ 168 ค/ง/จ) — ตรวจที่เซิร์ฟเวอร์ทั้งหมด ──
    /// <summary>"prepay" (ว่าง = เดิม) / "cod" — Helpers.CmsFeatureFlags.ResolvePaymentChoice</summary>
    public string? PaymentMethodChoice { get; set; }
    /// <summary>yyyy-MM-dd · ว่าง = ไม่ได้เลือก (เดิม)</summary>
    public string? RequestedReadyDate { get; set; }
    /// <summary>คำตอบช่องที่ร้านเพิ่มเอง (key → ค่า) — Helpers.OrderFormFields.Check</summary>
    public Dictionary<string, string?>? CustomFields { get; set; }

    // Direct order (without cart)
    public List<CreateOrderLineRequest>? Lines { get; set; }

    /// <summary>รอบ 209 P1 (R-209-A9): คีย์กันกดสั่งซ้ำ (หน้าเว็บสุ่มต่อการกดยืนยัน 1 ครั้ง · หรือส่งเป็น header <c>Idempotency-Key</c>) —
    /// คีย์เดิมซ้ำ ⇒ คืนออเดอร์เดิม ไม่สร้างใหม่ · null = เดิม (ไม่กัน)</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>รอบ 209 P6 (ฑ): ลูกค้าติ๊กยินยอม PDPA บนฟอร์มเช็กเอาต์ — บังคับเมื่อเว็บตั้ง RequireDataConsent (ตรวจที่เซิร์ฟเวอร์)</summary>
    public bool ConsentGiven { get; set; }
    /// <summary>รอบ 216 (คำตัดสินข้อ 174): สมาชิกติ๊ก "บันทึกเป็นข้อมูลของฉัน" ⇒ หลังสั่งสำเร็จ controller บันทึกชื่อ/เบอร์/ที่อยู่/ข้อมูลใบกำกับลงโปรไฟล์ ·
    /// สมาชิก = id ในโทเคนหน้าร้านของเว็บนี้เท่านั้น (ไม่ใช่ CustomerId ใน body) · แขก/โทเคนเว็บอื่น ⇒ ไม่มีผล</summary>
    public bool SaveToProfile { get; set; }
    /// <summary>รอบ 216 ฝ่ายค้าน S3: หน้าเว็บยืนยันว่าชื่อ/เบอร์/อีเมลมาจากโปรไฟล์ (ผู้ใช้ไม่ได้ลบ/แก้เอง) ⇒ เซิร์ฟเวอร์เติมช่องที่ว่างจากโปรไฟล์ได้ ·
    /// มีผลเฉพาะสมาชิกในโทเคนของเว็บนี้ (controller ตัดเป็น false สำหรับพนักงาน/แขก/ผู้เรียก API) · false = ไม่เติม (ช่องบังคับว่าง ⇒ ข้อความปกติ)</summary>
    public bool ContactFromProfile { get; set; }
}

public class CreateOrderLineRequest
{
    [Required]
    public Guid SiteProductId { get; set; }
    public decimal Quantity { get; set; } = 1;
    public string? VariantOptionsJson { get; set; }
    /// <summary>รอบ 209 P6 (E5): ตัวเลือกย่อยที่เลือก (บังคับเมื่อสินค้ามี variant ใช้งานอยู่)</summary>
    public Guid? VariantId { get; set; }
}

public class UpdateOrderStatusRequest
{
    [Required]
    public SiteOrderStatus Status { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>รอบ 209 P1 (R-209-B4): ผู้ขนส่ง — null = ไม่แก้ · "" = ล้างค่า</summary>
    public string? ShippingCarrier { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }
    /// <summary>รอบ 209 P3 (E8): ยอดคืนเงิน (รวม VAT) เมื่อเปลี่ยนเป็นคืนเงิน/คืนเงินบางส่วน — คืนบางส่วนต้องระบุ · คืนเต็มไม่ระบุ = คืนทั้งหมดที่คืนได้ ·
    /// ใช้ร่างใบลดหนี้ (<c>Helpers.SiteOrderRefundPlan</c>) — ไม่อนุมัติเอง</summary>
    public decimal? RefundAmount { get; set; }
    /// <summary>เหตุผลการคืนเงิน (พิมพ์บนใบลดหนี้ร่าง §86/10)</summary>
    public string? RefundReason { get; set; }
}

/// <summary>รอบ 209 P3 (E7): พนักงานบันทึกรับเงินหน้าร้าน/ปลายทางของออเดอร์ — สร้างรายการรับเงินแล้วเดินเส้นยืนยันรับเงินเดิม (ConfirmPaymentAsync)</summary>
public class RecordCounterPaymentRequest
{
    /// <summary>ยอดที่รับ · ว่าง = ยอดคงค้างทั้งหมด</summary>
    public decimal? Amount { get; set; }
    /// <summary>วิธีรับเงิน (Cash · BankTransfer · PromptPay · CreditCard · EWallet · Other) · ว่าง = เงินสด</summary>
    public PaymentMethod? PaymentMethod { get; set; }
    public string? Reference { get; set; }
    /// <summary>ผังเงินเข้า (เงินสด/ธนาคาร) — ว่าง = ตามค่าเริ่มต้นของเส้นยืนยันรับเงิน</summary>
    public Guid? MoneyInAccountId { get; set; }
}

public class UploadSlipResponse
{
    public Guid OrderId { get; set; }
    public Guid PaymentId { get; set; }
    public string SlipUrl { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public class ConvertToQuotationResponse
{
    public Guid OrderId { get; set; }
    public Guid LeadId { get; set; }
    public string LeadNumber { get; set; } = "";
    public Guid? QuotationDocumentId { get; set; }
    /// <summary>Document number of the newly-created Quotation (e.g.
    /// "QUO-202601-0042"). Surfaced so the storefront can show a
    /// "ดูใบเสนอราคา {QuotationNumber}" link immediately.</summary>
    public string? QuotationNumber { get; set; }
}

/// <summary>Optional body for the storefront's "ขอใบเสนอราคา" button —
/// lets the customer attach a free-text note that lands on the
/// Quotation document's footer (CustomFooterNotes) so the printed PDF
/// shows their custom request below the line items.</summary>
public class ConvertOrderToQuotationRequest
{
    /// <summary>Free-text note from the customer — shown at the bottom
    /// of the Quotation PDF in the Notes section. Limited to 1000 chars
    /// server-side. Optional; null leaves the document without a footer.</summary>
    public string? CustomerNotes { get; set; }
}

public class StorefrontPaymentOptions
{
    /// <summary>PromptPay ID (phone or tax-id) displayed as QR code.</summary>
    public string? PromptPayId { get; set; }
    /// <summary>Pre-rendered QR image URL if the site uploaded one.
    /// Otherwise the storefront generates the QR client-side from
    /// <see cref="PromptPayId"/>.</summary>
    public string? PromptPayQrUrl { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }
    /// <summary>True when at least one payment method is configured.
    /// When false the storefront falls back to "pay later only" mode
    /// and instructs the customer that the shop will contact them.</summary>
    public bool HasPaymentMethod { get; set; }
    // ── รอบ 209 P2 — สวิตช์ร้าน (เซิร์ฟเวอร์ตัดสิน · หน้าเว็บแสดงตาม) ──
    /// <summary>false = ร้านไม่รับโอน/แนบสลิป (ไม่ส่งเลขบัญชี · ซ่อนกล่องโอน — ไม่ใช่ "ยังไม่ตั้งบัญชี")</summary>
    public bool BankTransferEnabled { get; set; } = true;
    public bool OnlinePaymentEnabled { get; set; } = true;
    public bool CodEnabled { get; set; }
    /// <summary>false = ซ่อนปุ่ม "ขอใบเสนอราคา" (เซิร์ฟเวอร์ปฏิเสธด้วย)</summary>
    public bool QuoteConversionEnabled { get; set; } = true;
}

public class OrderResponse
{
    public Guid Id { get; set; }
    /// <summary>รอบ 216 (ข้อ 174 · ต่อจากรอบ 217): คำขอนี้ไม่ได้สร้างออเดอร์ใหม่ — คืนออเดอร์เดิมด้วยคีย์กันกดซ้ำ ⇒ controller ไม่บันทึกโปรไฟล์ซ้ำ</summary>
    public bool Replayed { get; set; }
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string Currency { get; set; } = "THB";
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string? ShippingName { get; set; }
    public string? ShippingAddress { get; set; }
    public string? ShippingMethod { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>รอบ 209 P1 (R-209-B4): ผู้ขนส่ง (echo กลับให้หลังบ้านแก้ได้)</summary>
    public string? ShippingCarrier { get; set; }
    /// <summary>รอบ 209 P1 (R-209-A1): โทเคนหน้าสถานะออเดอร์ของลูกค้า — หน้าร้านพาไป order-success/{token} (echo ให้ผู้สร้างออเดอร์ · หน้า cms-orders ยังไม่แสดงลิงก์ — แพ็ก P3)</summary>
    public string? PublicToken { get; set; }
    public bool RequestTaxInvoice { get; set; }
    public string? BillingTaxId { get; set; }
    public Guid? ErpDocumentId { get; set; }
    /// <summary>รอบ 205 ทีม W1 (R-02): คืนเงินผ่าน gateway สำเร็จแล้วเมื่อ (null = ไม่เคย) — ยังต้องออกใบลดหนี้ที่เอกสารต้นทาง</summary>
    public DateTime? RefundedAtGatewayAt { get; set; }
    public string? CouponCode { get; set; }
    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<OrderLineResponse> Lines { get; set; } = new();
    public List<OrderPaymentResponse>? Payments { get; set; }
    // ── สินค้าตามสั่ง (ทีม K) — ทุกค่าคำนวณที่เซิร์ฟเวอร์ (หน้าเว็บแสดงอย่างเดียว) ──
    public string? FulfillmentMethod { get; set; }
    public string? FulfillmentLabel { get; set; }
    public string? ShippingProvince { get; set; }
    public string? ShippingPostalCode { get; set; }
    /// <summary>yyyy-MM-dd · null = ไม่มีสินค้าตามสั่ง</summary>
    public string? EarliestReadyDate { get; set; }
    public string? PaymentTermMode { get; set; }
    public string? PaymentTermLabel { get; set; }
    public decimal DepositRequiredAmount { get; set; }
    /// <summary>ยอดที่ต้องจ่ายตอนนี้เพื่อยืนยันคำสั่งซื้อ (มัดจำที่ยังขาด)</summary>
    public decimal AmountDueNow { get; set; }
    /// <summary>ยอดที่ช่องจ่ายเปิดให้จ่ายรอบนี้ (มัดจำที่ขาด หรือยอดคงค้างที่เหลือ)</summary>
    public decimal NextPayableAmount { get; set; }
    public decimal BalanceAmount { get; set; }
    // ── ทีม Q — วิธีออกเอกสารของมัดจำที่ตรึงบนออเดอร์ + ใบรับเงินมัดจำที่ออกแล้ว ──
    public string DepositInvoicingMode { get; set; } = "full_at_first_payment";
    public string DepositInvoicingLabel { get; set; } = "";
    /// <summary>ข้อความถึงลูกค้า/พนักงานเรื่องใบกำกับของมัดจำ (null = ไม่ต้องแสดง)</summary>
    public string? DepositInvoicingNote { get; set; }
    public List<OrderDepositDocumentResponse> DepositDocuments { get; set; } = new();
    /// <summary>รอบ 205 W2 (R-06): ช่องจ่ายออนไลน์ปิดชั่วคราวเพราะอะไร (สลิปรอตรวจ) · null = เปิด — หน้าเว็บแสดงตรง ๆ</summary>
    public string? OnlinePaymentBlockedNote { get; set; }
    // ── รอบ 209 P2 — ค่าที่ตรึงตอนสั่ง (echo ทุกค่าที่รับ · กฎเหล็ก #4 A) ──
    /// <summary>"prepay"/"cod" · ออเดอร์เดิม = prepay</summary>
    public string PaymentMethodChoice { get; set; } = "prepay";
    public string PaymentMethodLabel { get; set; } = "";
    /// <summary>yyyy-MM-dd · null = ลูกค้าไม่ได้เลือก</summary>
    public string? RequestedReadyDate { get; set; }
    /// <summary>yyyy-MM-dd · null = รับเอง/ไม่รู้วันขนส่ง</summary>
    public string? EstimatedDeliveryDate { get; set; }
    public List<global::Accounting.Helpers.OrderCustomAnswer> CustomFields { get; set; } = new();
    /// <summary>JSON ดิบจากฐาน (ใช้ภายในเพื่อแปลงเป็น <see cref="CustomFields"/> หลัง query) — ไม่ส่งออก</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CustomFieldsRaw { get; set; }
    public string? BillingName { get; set; }
    public string? BillingAddress { get; set; }
    public string? BillingBranchCode { get; set; }
    /// <summary>รอบ 209 P3 (A6): ปุ่ม "ดำเนินการต่อ" จากตัวตัดสินเดียว <c>SiteOrderStatusTransition.NextActions</c> (หน้าเว็บไม่มีตารางสถานะของตัวเอง)</summary>
    public List<global::Accounting.Helpers.SiteOrderNextAction> NextActions { get; set; } = new();
    /// <summary>รอบ 209 P3 (E7): ปุ่ม "บันทึกรับเงินหน้าร้าน/ปลายทาง" ใช้ได้ไหม (ยังมียอดค้าง · ออเดอร์ยังไม่ปิด)</summary>
    public bool CanRecordCounterPayment { get; set; }
}

/// <summary>ใบรับเงินมัดจำ 1 ใบของออเดอร์ (ทีม Q)</summary>
public class OrderDepositDocumentResponse
{
    public Guid Id { get; set; }
    public string DocumentNumber { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public decimal VatAmount { get; set; }
    public DocumentStatus Status { get; set; }
}

/// <summary>คำขอคิดยอดก่อนยืนยันสั่งซื้อ — ตัวเดียวกับที่ <c>CreateOrderAsync</c> ใช้จริง (ผลไม่ต่างกัน)</summary>
public class OptionImageUploadResponse
{
    public Guid AttachmentId { get; set; }
    public string FileName { get; set; } = "";
}

public class CheckoutQuoteRequest
{
    /// <summary>รอบ 209 P2: "prepay"/"cod" (ว่าง = เดิม)</summary>
    public string? PaymentMethodChoice { get; set; }
    /// <summary>รอบ 209 P2: yyyy-MM-dd · ว่าง = ไม่ได้เลือก</summary>
    public string? RequestedReadyDate { get; set; }
    public string? FulfillmentMethod { get; set; }
    public Guid? ShippingRateId { get; set; }
    public string? CountryCode { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
}

public class CheckoutShippingOption
{
    public Guid RateId { get; set; }
    public string Name { get; set; } = "";
    public string? ZoneName { get; set; }
    public decimal Fee { get; set; }
    public bool IsFree { get; set; }
    public int? EstimatedDeliveryDaysMin { get; set; }
    public int? EstimatedDeliveryDaysMax { get; set; }
}

public class CheckoutQuoteResponse
{
    public decimal GoodsTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string FulfillmentMethod { get; set; } = "delivery";
    public string FulfillmentLabel { get; set; } = "";
    public bool PickupEnabled { get; set; }
    public List<CheckoutShippingOption> ShippingOptions { get; set; } = new();
    public Guid? SelectedShippingRateId { get; set; }
    public decimal ShippingFee { get; set; }
    /// <summary>อัตรา VAT ของบรรทัดค่าส่ง (ตามสินค้าในบิล — §79)</summary>
    public decimal ShippingVatRate { get; set; }
    public decimal OrderTotal { get; set; }
    public bool HasMadeToOrder { get; set; }
    public string? EarliestReadyDate { get; set; }
    public string PaymentTermMode { get; set; } = "full";
    public string PaymentTermLabel { get; set; } = "";
    public decimal DepositRequiredAmount { get; set; }
    public decimal AmountDueNow { get; set; }
    public decimal BalanceAfterDueNow { get; set; }
    /// <summary>ทีม Q — ข้อความเรื่องใบกำกับของมัดจำ (null = ไม่ต้องแสดง)</summary>
    public string? DepositInvoicingNote { get; set; }
    /// <summary>เหตุที่ยังสั่งไม่ได้ (เช่น ต้องเลือกวิธีส่ง) — null = สั่งได้</summary>
    public string? Problem { get; set; }
    // ── รอบ 209 P2 — หน้าเว็บวาดฟอร์มจากค่าที่เซิร์ฟเวอร์ตัดสินแล้ว (ไม่มีสำเนากติกาใน JS · F2 ข้อ 5) ──
    public bool DeliveryEnabled { get; set; } = true;
    /// <summary>สวิตช์ร้าน (ชื่อ → ค่า) — Helpers.CmsFeatureFlags.ToStorefront</summary>
    public Dictionary<string, bool> Features { get; set; } = new();
    /// <summary>ค่าตั้งฟอร์มสั่งซื้อ (ไม่ได้ตั้ง = ฟอร์มเดิม)</summary>
    public global::Accounting.Helpers.OrderFormFieldsConfig FormFields { get; set; } = global::Accounting.Helpers.OrderFormFields.Default;
    /// <summary>ช่องเพิ่มเองที่ต้องแสดงกับวิธีรับนี้ (เซิร์ฟเวอร์กรองแล้ว — หน้าเว็บวาดตามลำดับ)</summary>
    public List<global::Accounting.Helpers.OrderCustomField> CustomFields { get; set; } = new();
    /// <summary>วิธีชำระที่ลูกค้าเลือกได้ (ว่าง/ตัวเดียว = ไม่ต้องแสดงตัวเลือก)</summary>
    public List<CommerceOptionValue> PaymentChoices { get; set; } = new();
    public string PaymentMethodChoice { get; set; } = "prepay";
    public string PaymentMethodLabel { get; set; } = "";
    /// <summary>yyyy-MM-dd — วันแรกที่เลือกเป็นวันรับได้ (null = ช่องวันรับซ่อน)</summary>
    public string? MinRequestedDate { get; set; }
    /// <summary>yyyy-MM-dd — วันรับที่ผ่านการตรวจแล้ว</summary>
    public string? RequestedReadyDate { get; set; }
    /// <summary>yyyy-MM-dd — วันส่งถึงโดยประมาณ (null = ไม่รู้)</summary>
    public string? EstimatedDeliveryDate { get; set; }
    /// <summary>คูปองในตะกร้าให้ส่งฟรี</summary>
    public bool FreeShippingByCoupon { get; set; }
    /// <summary>คูปองที่ใช้อยู่ (ร้านปิดคูปอง = null)</summary>
    public string? CouponCode { get; set; }
}

public class OrderListResponse
{
    public Guid Id { get; set; }
    /// <summary>R-45 มุมมองทุกไซต์ — เว็บเจ้าของแถว (ปุ่ม action ต้องยิงไปเว็บนี้) · endpoint รายเว็บเดิมก็ใส่ให้ (ค่าเดียวกับ siteId ใน URL)</summary>
    public Guid SiteId { get; set; }
    /// <summary>ชื่อเว็บเจ้าของแถว — ใส่เฉพาะมุมมองทุกไซต์ (null = หน้ารายเว็บ ไม่ต้องโชว์)</summary>
    public string? SiteName { get; set; }
    public string OrderNumber { get; set; } = "";
    public SiteOrderStatus Status { get; set; }
    public string? CustomerName { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public int ItemCount { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; }
    // ── รอบ 209 P3 (A5 คิวผลิต) — คำนวณที่เซิร์ฟเวอร์ (หน้าเว็บแสดงอย่างเดียว) ──
    /// <summary>yyyy-MM-dd วันพร้อมเร็วสุดที่สัญญาตอนสั่ง · null = ไม่มีสินค้าตามสั่ง</summary>
    public string? EarliestReadyDate { get; set; }
    public string? FulfillmentMethod { get; set; }
    public string? FulfillmentLabel { get; set; }
    public decimal PaidAmount { get; set; }
    public string? PaymentTermMode { get; set; }
    public decimal DepositRequiredAmount { get; set; }
    /// <summary>ยอดคงค้างทั้งใบ (ปิดแล้ว = 0)</summary>
    public decimal BalanceDue { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>คิวผลิตเท่านั้น: รายการ + ตัวเลือก (ใบงานครัว) · null = มุมมองรายการปกติ</summary>
    public string? LineSummary { get; set; }
    /// <summary>คิวผลิตเท่านั้น: เลยวันพร้อมแล้วยังไม่ส่งมอบ</summary>
    public bool IsOverdue { get; set; }
    /// <summary>คิวผลิตเท่านั้น: ปุ่มด่วน (ตัวตัดสินเดียวกับหน้ารายละเอียด)</summary>
    public List<global::Accounting.Helpers.SiteOrderNextAction>? NextActions { get; set; }
}

public class OrderLineResponse
{
    public Guid Id { get; set; }
    public Guid SiteProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductSku { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string? ModifierSummary { get; set; }
}

public class OrderPaymentResponse
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public PaymentMethod PaymentMethod { get; set; }
    public SitePaymentStatus Status { get; set; }
    public string? Reference { get; set; }
    public DateTime? PaidAt { get; set; }
    /// <summary>ทีม Q — ใบรับเงินมัดจำที่ออกจากรายการนี้ (null = บันทึกเป็นรับชำระของใบขาย/ยังไม่ยืนยัน)</summary>
    public Guid? DepositDocumentId { get; set; }
    /// <summary>ทีม Q (ฝ่ายค้านข้อ 2) — ป้ายปุ่ม "ทำขั้นที่ค้าง" ของรายการนี้ (เซิร์ฟเวอร์ตัดสิน · null = ไม่มีอะไรค้าง)</summary>
    public string? PendingAction { get; set; }
    /// <summary>R-43 — URL สลิปที่ลูกค้าแนบ (null = รายการนี้ไม่มีสลิป เช่น จ่ายผ่าน gateway)</summary>
    public string? SlipUrl { get; set; }
    /// <summary>true = สลิปอยู่ในที่เก็บกลาง ต้องดึงผ่าน API พร้อม JWT (<c>API.fetchFile</c>) · false = URL เดิมเปิดตรงได้</summary>
    public bool SlipRequiresAuth { get; set; }
}

// ==================== Payment Gateway ====================

public class CreatePaymentGatewayRequest
{
    public PaymentGatewayType GatewayType { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ApiKey { get; set; }
    public string? SecretKey { get; set; }
    public string? MerchantId { get; set; }
    public string? PromptPayId { get; set; }
    public string? PromptPayQrUrl { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankAccountName { get; set; }
    public bool IsTestMode { get; set; } = true;
    public string SupportedCurrencies { get; set; } = "THB";
}

public class PaymentGatewayResponse
{
    public Guid Id { get; set; }
    public PaymentGatewayType GatewayType { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool HasApiKey { get; set; }
    public string? MerchantId { get; set; }
    public string? PromptPayId { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountName { get; set; }
    public bool IsTestMode { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public string SupportedCurrencies { get; set; } = "";
}
