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
}
