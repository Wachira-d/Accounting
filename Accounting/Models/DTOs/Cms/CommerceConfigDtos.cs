using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

public class UpdateCommerceConfigRequest
{
    // Checkout
    public CheckoutMode? CheckoutMode { get; set; }
    public bool? AutoGenerateQuotation { get; set; }
    public bool? RequireCustomerAccount { get; set; }
    public bool? EnableGuestCheckout { get; set; }

    // Tax
    public decimal? DefaultVatRate { get; set; }
    public bool? PricesIncludeVat { get; set; }
    public bool? EnableTaxInvoice { get; set; }

    // Shipping
    public bool? EnableShipping { get; set; }
    public bool? EnableShippingCalculation { get; set; }
    public decimal? FlatShippingRate { get; set; }
    public decimal? FreeShippingThreshold { get; set; }

    // Inventory
    public bool? EnableStockTracking { get; set; }
    public bool? AutoDeductStock { get; set; }
    public StockBehavior? DefaultStockBehavior { get; set; }
    public bool? ShowStockQuantity { get; set; }

    // Coupons
    public bool? EnableCoupons { get; set; }
    public bool? EnableTierPricing { get; set; }

    // Reviews
    public bool? EnableReviews { get; set; }
    public bool? ReviewAutoApprove { get; set; }
    public bool? ReviewRequirePurchase { get; set; }

    // Cart
    public bool? EnableWishlist { get; set; }
    public bool? EnableAbandonedCartRecovery { get; set; }
    public int? CartExpiryDays { get; set; }
    public int? AbandonedCartHours { get; set; }

    // Order
    public string? OrderNumberPrefix { get; set; }
    public bool? AutoConfirmOrders { get; set; }
    public bool? NotifyOnNewOrder { get; set; }
    public string? OrderNotificationEmails { get; set; }

    // ERP
    public bool? AutoSyncToErp { get; set; }
    public DocumentType? ErpDocumentType { get; set; }

    // Payment
    public bool? EnableOnlinePayment { get; set; }
    public bool? EnableCod { get; set; }
    public bool? EnableBankTransfer { get; set; }

    // Display
    public string? DefaultProductSort { get; set; }
    public int? ProductsPerPage { get; set; }
    public bool? ShowComparePrice { get; set; }
    public bool? ShowSku { get; set; }

    // Notification messages
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

    // Checkout
    public CheckoutMode CheckoutMode { get; set; }
    public bool AutoGenerateQuotation { get; set; }
    public bool RequireCustomerAccount { get; set; }
    public bool EnableGuestCheckout { get; set; }

    // Tax
    public decimal DefaultVatRate { get; set; }
    public bool PricesIncludeVat { get; set; }
    public bool EnableTaxInvoice { get; set; }

    // Shipping
    public bool EnableShipping { get; set; }
    public bool EnableShippingCalculation { get; set; }
    public decimal? FlatShippingRate { get; set; }
    public decimal? FreeShippingThreshold { get; set; }

    // Inventory
    public bool EnableStockTracking { get; set; }
    public bool AutoDeductStock { get; set; }
    public StockBehavior DefaultStockBehavior { get; set; }
    public bool ShowStockQuantity { get; set; }

    // Coupons & Reviews
    public bool EnableCoupons { get; set; }
    public bool EnableTierPricing { get; set; }
    public bool EnableReviews { get; set; }
    public bool ReviewAutoApprove { get; set; }
    public bool ReviewRequirePurchase { get; set; }

    // Cart
    public bool EnableWishlist { get; set; }
    public bool EnableAbandonedCartRecovery { get; set; }
    public int CartExpiryDays { get; set; }
    public int AbandonedCartHours { get; set; }

    // Order
    public string OrderNumberPrefix { get; set; } = "";
    public bool AutoConfirmOrders { get; set; }
    public bool NotifyOnNewOrder { get; set; }
    public string? OrderNotificationEmails { get; set; }

    // ERP
    public bool AutoSyncToErp { get; set; }
    public DocumentType ErpDocumentType { get; set; }

    // Payment
    public bool EnableOnlinePayment { get; set; }
    public bool EnableCod { get; set; }
    public bool EnableBankTransfer { get; set; }

    // Display
    public string DefaultProductSort { get; set; } = "";
    public int ProductsPerPage { get; set; }
    public bool ShowComparePrice { get; set; }
    public bool ShowSku { get; set; }

    // Notification messages
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
    public List<CommerceOptionValue> DepositInvoicingModes { get; set; } = new();
}

public class CommerceOptionValue
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>คำอธิบายผลของตัวเลือก (ภาษาไทยจากเซิร์ฟเวอร์) · null = ไม่มี</summary>
    public string? Description { get; set; }
}
