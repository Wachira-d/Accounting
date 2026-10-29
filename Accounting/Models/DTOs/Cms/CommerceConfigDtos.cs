using Accounting.Models.Enums;
using ShipVat = Accounting.Helpers.ShippingVatAllocation;

namespace Accounting.Models.DTOs.Cms;

public class UpdateCommerceConfigRequest
{
    // รอบ 209 P2 (คำตัดสินข้อ 168 ก): ค่าตั้งที่ไม่มีผู้อ่านถูกลบ (เหตุผลใน Helpers/CmsFeatureFlags) · null = ไม่เปลี่ยน
    // สมาชิก — EnableGuestCheckout=false ⇒ ต้องเป็นสมาชิก (RequireCustomerAccount ตั้งคู่กันให้อัตโนมัติ)
    public bool? EnableGuestCheckout { get; set; }

    // ภาษี
    public bool? EnableTaxInvoice { get; set; }

    // การจัดส่ง
    public bool? EnableShipping { get; set; }
    public decimal? FlatShippingRate { get; set; }
    public decimal? FreeShippingThreshold { get; set; }
    /// <summary>รอบ 209: true = ล้างค่าส่งเหมาจ่าย (ไม่คิดค่าส่ง)</summary>
    public bool? ClearFlatShippingRate { get; set; }
    /// <summary>รอบ 209: true = ล้างเกณฑ์ส่งฟรี</summary>
    public bool? ClearFreeShippingThreshold { get; set; }
    public bool? FreeShippingOverridesZones { get; set; }

    // คูปอง · รีวิว · รายการโปรด
    public bool? EnableCoupons { get; set; }
    public bool? EnableReviews { get; set; }
    public bool? ReviewAutoApprove { get; set; }
    public bool? ReviewRequirePurchase { get; set; }
    public bool? EnableWishlist { get; set; }

    // ตะกร้า (ผู้อ่าน: แพ็ก P6 · AbandonedCartService)
    public bool? EnableAbandonedCartRecovery { get; set; }
    public int? CartExpiryDays { get; set; }
    public int? AbandonedCartHours { get; set; }

    // คำสั่งซื้อ
    public string? OrderNumberPrefix { get; set; }
    public bool? AutoConfirmOrders { get; set; }
    public bool? NotifyOnNewOrder { get; set; }
    public string? OrderNotificationEmails { get; set; }
    public bool? EnableQuoteConversion { get; set; }
    /// <summary>รอบ 209: ค่าตั้งฟอร์มสั่งซื้อ (Helpers.OrderFormFields) · null = ไม่เปลี่ยน · ส่ง ResetOrderFormFields=true เพื่อกลับเป็นฟอร์มเดิม</summary>
    public global::Accounting.Helpers.OrderFormFieldsConfig? OrderFormFields { get; set; }
    public bool? ResetOrderFormFields { get; set; }

    // การชำระ
    public bool? EnableOnlinePayment { get; set; }
    public bool? EnableCod { get; set; }
    public bool? EnableBankTransfer { get; set; }

    // ข้อความ
    public string? OrderConfirmMessageTh { get; set; }
    public string? OrderConfirmMessageEn { get; set; }
    public string? QuotationMessageTh { get; set; }
    public string? QuotationMessageEn { get; set; }

    // ── สินค้าตามสั่ง (ทีม K) — null = ไม่เปลี่ยน · ช่องข้อความส่ง "" = ล้างค่า ──
    public bool? EnablePickup { get; set; }
    /// <summary>Helpers.PaymentTermMode: full / deposit_percent / deposit_fixed / pay_on_pickup</summary>
    public string? MtoPaymentTermMode { get; set; }
    public decimal? MtoDepositPercent { get; set; }
    public decimal? MtoDepositFixedAmount { get; set; }
    /// <summary>ทีม Q — Helpers.DepositInvoicingMode: full_at_first_payment / deposit_tax_invoice · null = ไม่เปลี่ยน</summary>
    public string? MtoDepositInvoicing { get; set; }
    /// <summary>รอบ 206 R-32: max_rate | proportional (Helpers.ShippingVatAllocation)</summary>
    public string? ShippingVatAllocation { get; set; }
    /// <summary>รอบ 206 R-31: % ริบมัดจำคงเหลือเมื่อยกเลิกออเดอร์ · null = คงค่าเดิม · ส่ง ClearDepositForfeitOnCancelPercent=true เพื่อปิด</summary>
    public decimal? DepositForfeitOnCancelPercent { get; set; }
    public bool? ClearDepositForfeitOnCancelPercent { get; set; }
    /// <summary>"0,6" (0 = อาทิตย์) · "" = เปิดทุกวัน</summary>
    public string? MtoClosedWeekdays { get; set; }
    public int? MtoCutoffHour { get; set; }
    /// <summary>true = ล้างชั่วโมงตัดรอบ (ไม่ตัดรอบ)</summary>
    public bool ClearMtoCutoffHour { get; set; }
    public bool? MtoSkipCompanyHolidays { get; set; }
}

public class CommerceConfigResponse
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }

    // รอบ 209 P2 — สวิตช์ร้าน (echo ทุกค่าที่รับ · ผู้อ่านเชิงธุรกิจผ่าน Helpers/CmsFeatureFlags)
    public bool EnableGuestCheckout { get; set; }
    public bool RequireCustomerAccount { get; set; }
    public bool EnableTaxInvoice { get; set; }
    public bool EnableShipping { get; set; }
    public decimal? FlatShippingRate { get; set; }
    public decimal? FreeShippingThreshold { get; set; }
    public bool FreeShippingOverridesZones { get; set; }
    public bool EnableCoupons { get; set; }
    public bool EnableReviews { get; set; }
    public bool ReviewAutoApprove { get; set; }
    public bool ReviewRequirePurchase { get; set; }
    public bool EnableWishlist { get; set; }
    public bool EnableAbandonedCartRecovery { get; set; }
    public int CartExpiryDays { get; set; }
    public int AbandonedCartHours { get; set; }
    public string OrderNumberPrefix { get; set; } = "";
    /// <summary>ตัวอย่างเลขคำสั่งซื้อถัดไปตามคำนำหน้า (เซิร์ฟเวอร์ประกอบ — หน้าเว็บไม่ประกอบเอง)</summary>
    public string OrderNumberExample { get; set; } = "";
    public bool AutoConfirmOrders { get; set; }
    public bool NotifyOnNewOrder { get; set; }
    public string? OrderNotificationEmails { get; set; }
    public bool EnableQuoteConversion { get; set; }
    /// <summary>ค่าตั้งฟอร์มที่ใช้วาด (ไม่ได้ตั้ง = ฟอร์มเดิม) + ตั้งแล้วหรือยัง</summary>
    public global::Accounting.Helpers.OrderFormFieldsConfig OrderFormFields { get; set; } = global::Accounting.Helpers.OrderFormFields.Default;
    public bool OrderFormFieldsConfigured { get; set; }
    public List<CommerceOptionValue> FormFieldRequirementOptions { get; set; } = new();
    public List<string> FormFieldTypes { get; set; } = new();
    public bool EnableOnlinePayment { get; set; }
    public bool EnableCod { get; set; }
    public bool EnableBankTransfer { get; set; }
    public string? OrderConfirmMessageTh { get; set; }
    public string? OrderConfirmMessageEn { get; set; }
    public string? QuotationMessageTh { get; set; }
    public string? QuotationMessageEn { get; set; }

    // ── สินค้าตามสั่ง (ทีม K) ──
    public bool EnablePickup { get; set; }
    public string MtoPaymentTermMode { get; set; } = "full";
    public decimal? MtoDepositPercent { get; set; }
    public decimal? MtoDepositFixedAmount { get; set; }
    public string? MtoClosedWeekdays { get; set; }
    public int? MtoCutoffHour { get; set; }
    public bool MtoSkipCompanyHolidays { get; set; }
    /// <summary>ตัวเลือกของช่องเงื่อนไขชำระ (เซิร์ฟเวอร์เป็นเจ้าของป้าย)</summary>
    public List<CommerceOptionValue> PaymentTermModes { get; set; } = new();
    /// <summary>ทีม Q — วิธีออกเอกสารของมัดจำที่ร้านเลือก + ตัวเลือกพร้อมคำอธิบายผลภาษี (เซิร์ฟเวอร์เป็นเจ้าของข้อความ)</summary>
    public string MtoDepositInvoicing { get; set; } = "full_at_first_payment";
    public string ShippingVatAllocation { get; set; } = ShipVat.MaxRate;
    public List<ShipVat.Option> ShippingVatAllocationOptions { get; set; } = new();
    public decimal? DepositForfeitOnCancelPercent { get; set; }
    public List<CommerceOptionValue> DepositInvoicingModes { get; set; } = new();
}

public class CommerceOptionValue
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>คำอธิบายผลของตัวเลือก (ภาษาไทยจากเซิร์ฟเวอร์) · null = ไม่มี</summary>
    public string? Description { get; set; }
}
