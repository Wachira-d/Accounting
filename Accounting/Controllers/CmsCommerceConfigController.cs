using Accounting.Data;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/config")]
[Authorize]
public class CmsCommerceConfigController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public CmsCommerceConfigController(AccountingDbContext db)
    {
        _db = db;
    }

    private string Lang => CmsMessages.ResolveLanguage(HttpContext);

    [HttpGet]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<CommerceConfigResponse>>> GetConfig(Guid companyId, Guid siteId)
    {
        var config = await _db.SiteCommerceConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);

        if (config == null)
        {
            config = new SiteCommerceConfig { CompanyId = companyId, SiteId = siteId };
            _db.SiteCommerceConfigs.Add(config);
            await _db.SaveChangesAsync();
        }

        return Ok(new ApiResponse<CommerceConfigResponse>(true, MapConfig(config)));
    }

    [HttpPut]
    [RequireSiteRole(SiteStaffRole.Admin)]
    public async Task<ActionResult<ApiResponse<CommerceConfigResponse>>> UpdateConfig(
        Guid companyId, Guid siteId, [FromBody] UpdateCommerceConfigRequest request)
    {
        var config = await _db.SiteCommerceConfigs
            .FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);

        if (config == null)
        {
            config = new SiteCommerceConfig { CompanyId = companyId, SiteId = siteId };
            _db.SiteCommerceConfigs.Add(config);
        }

        if (request.CheckoutMode.HasValue) config.CheckoutMode = request.CheckoutMode.Value;
        if (request.AutoGenerateQuotation.HasValue) config.AutoGenerateQuotation = request.AutoGenerateQuotation.Value;
        if (request.RequireCustomerAccount.HasValue) config.RequireCustomerAccount = request.RequireCustomerAccount.Value;
        if (request.EnableGuestCheckout.HasValue) config.EnableGuestCheckout = request.EnableGuestCheckout.Value;

        if (request.DefaultVatRate.HasValue) config.DefaultVatRate = request.DefaultVatRate.Value;
        if (request.PricesIncludeVat.HasValue) config.PricesIncludeVat = request.PricesIncludeVat.Value;
        if (request.EnableTaxInvoice.HasValue) config.EnableTaxInvoice = request.EnableTaxInvoice.Value;

        if (request.EnableShipping.HasValue) config.EnableShipping = request.EnableShipping.Value;
        if (request.EnableShippingCalculation.HasValue) config.EnableShippingCalculation = request.EnableShippingCalculation.Value;
        if (request.FlatShippingRate.HasValue) config.FlatShippingRate = request.FlatShippingRate;
        if (request.FreeShippingThreshold.HasValue) config.FreeShippingThreshold = request.FreeShippingThreshold;

        if (request.EnableStockTracking.HasValue) config.EnableStockTracking = request.EnableStockTracking.Value;
        if (request.AutoDeductStock.HasValue) config.AutoDeductStock = request.AutoDeductStock.Value;
        if (request.DefaultStockBehavior.HasValue) config.DefaultStockBehavior = request.DefaultStockBehavior.Value;
        if (request.ShowStockQuantity.HasValue) config.ShowStockQuantity = request.ShowStockQuantity.Value;

        if (request.EnableCoupons.HasValue) config.EnableCoupons = request.EnableCoupons.Value;
        if (request.EnableTierPricing.HasValue) config.EnableTierPricing = request.EnableTierPricing.Value;

        if (request.EnableReviews.HasValue) config.EnableReviews = request.EnableReviews.Value;
        if (request.ReviewAutoApprove.HasValue) config.ReviewAutoApprove = request.ReviewAutoApprove.Value;
        if (request.ReviewRequirePurchase.HasValue) config.ReviewRequirePurchase = request.ReviewRequirePurchase.Value;

        if (request.EnableWishlist.HasValue) config.EnableWishlist = request.EnableWishlist.Value;
        if (request.EnableAbandonedCartRecovery.HasValue) config.EnableAbandonedCartRecovery = request.EnableAbandonedCartRecovery.Value;
        if (request.CartExpiryDays.HasValue) config.CartExpiryDays = request.CartExpiryDays.Value;
        if (request.AbandonedCartHours.HasValue) config.AbandonedCartHours = request.AbandonedCartHours.Value;

        if (request.OrderNumberPrefix != null) config.OrderNumberPrefix = request.OrderNumberPrefix;
        if (request.AutoConfirmOrders.HasValue) config.AutoConfirmOrders = request.AutoConfirmOrders.Value;
        if (request.NotifyOnNewOrder.HasValue) config.NotifyOnNewOrder = request.NotifyOnNewOrder.Value;
        if (request.OrderNotificationEmails != null) config.OrderNotificationEmails = request.OrderNotificationEmails;

        if (request.AutoSyncToErp.HasValue) config.AutoSyncToErp = request.AutoSyncToErp.Value;
        if (request.ErpDocumentType.HasValue) config.ErpDocumentType = request.ErpDocumentType.Value;

        if (request.EnableOnlinePayment.HasValue) config.EnableOnlinePayment = request.EnableOnlinePayment.Value;
        if (request.EnableCod.HasValue) config.EnableCod = request.EnableCod.Value;
        if (request.EnableBankTransfer.HasValue) config.EnableBankTransfer = request.EnableBankTransfer.Value;

        if (request.DefaultProductSort != null) config.DefaultProductSort = request.DefaultProductSort;
        if (request.ProductsPerPage.HasValue) config.ProductsPerPage = request.ProductsPerPage.Value;
        if (request.ShowComparePrice.HasValue) config.ShowComparePrice = request.ShowComparePrice.Value;
        if (request.ShowSku.HasValue) config.ShowSku = request.ShowSku.Value;

        if (request.OrderConfirmMessageTh != null) config.OrderConfirmMessageTh = request.OrderConfirmMessageTh;
        if (request.OrderConfirmMessageEn != null) config.OrderConfirmMessageEn = request.OrderConfirmMessageEn;
        if (request.QuotationMessageTh != null) config.QuotationMessageTh = request.QuotationMessageTh;
        if (request.QuotationMessageEn != null) config.QuotationMessageEn = request.QuotationMessageEn;

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        config.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<CommerceConfigResponse>(true, MapConfig(config), CmsMessages.Get("config.updated", Lang)));
    }

    private static CommerceConfigResponse MapConfig(SiteCommerceConfig c) => new()
    {
        Id = c.Id, SiteId = c.SiteId,
        CheckoutMode = c.CheckoutMode, AutoGenerateQuotation = c.AutoGenerateQuotation,
        RequireCustomerAccount = c.RequireCustomerAccount, EnableGuestCheckout = c.EnableGuestCheckout,
        DefaultVatRate = c.DefaultVatRate, PricesIncludeVat = c.PricesIncludeVat, EnableTaxInvoice = c.EnableTaxInvoice,
        EnableShipping = c.EnableShipping, EnableShippingCalculation = c.EnableShippingCalculation,
        FlatShippingRate = c.FlatShippingRate, FreeShippingThreshold = c.FreeShippingThreshold,
        EnableStockTracking = c.EnableStockTracking, AutoDeductStock = c.AutoDeductStock,
        DefaultStockBehavior = c.DefaultStockBehavior, ShowStockQuantity = c.ShowStockQuantity,
        EnableCoupons = c.EnableCoupons, EnableTierPricing = c.EnableTierPricing,
        EnableReviews = c.EnableReviews, ReviewAutoApprove = c.ReviewAutoApprove, ReviewRequirePurchase = c.ReviewRequirePurchase,
        EnableWishlist = c.EnableWishlist, EnableAbandonedCartRecovery = c.EnableAbandonedCartRecovery,
        CartExpiryDays = c.CartExpiryDays, AbandonedCartHours = c.AbandonedCartHours,
        OrderNumberPrefix = c.OrderNumberPrefix, AutoConfirmOrders = c.AutoConfirmOrders,
        NotifyOnNewOrder = c.NotifyOnNewOrder, OrderNotificationEmails = c.OrderNotificationEmails,
        AutoSyncToErp = c.AutoSyncToErp, ErpDocumentType = c.ErpDocumentType,
        EnableOnlinePayment = c.EnableOnlinePayment, EnableCod = c.EnableCod, EnableBankTransfer = c.EnableBankTransfer,
        DefaultProductSort = c.DefaultProductSort, ProductsPerPage = c.ProductsPerPage,
        ShowComparePrice = c.ShowComparePrice, ShowSku = c.ShowSku,
        OrderConfirmMessageTh = c.OrderConfirmMessageTh, OrderConfirmMessageEn = c.OrderConfirmMessageEn,
        QuotationMessageTh = c.QuotationMessageTh, QuotationMessageEn = c.QuotationMessageEn
    };
}
