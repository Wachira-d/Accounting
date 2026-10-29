using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs;
using Accounting.Models.Constants;
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
    [RequirePermission(PermissionKeys.CmsSiteSettings)]   // รอบ 209 P2: ค่าตั้งร้าน = สิทธิ์ตั้งค่าเว็บ (เดียวกับค่าตั้งการจอง)
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

        // ── รอบ 209 P2 (คำตัดสินข้อ 168 ก) — สวิตช์ร้าน · ผู้อ่านเชิงธุรกิจทุกตัวผ่าน Helpers/CmsFeatureFlags ──
        if (request.EnableGuestCheckout.HasValue)
        {
            // สองคอลัมน์เดิมความหมายเดียว ⇒ ตั้งคู่กันเสมอ (ไม่ให้ค่าค้างขัดกัน)
            config.EnableGuestCheckout = request.EnableGuestCheckout.Value;
            config.RequireCustomerAccount = !request.EnableGuestCheckout.Value;
        }
        if (request.EnableTaxInvoice.HasValue) config.EnableTaxInvoice = request.EnableTaxInvoice.Value;

        if (request.EnableShipping.HasValue) config.EnableShipping = request.EnableShipping.Value;
        if (request.ClearFlatShippingRate == true) config.FlatShippingRate = null;
        else if (request.FlatShippingRate.HasValue)
        {
            if (request.FlatShippingRate < 0m) return BadRequest(new ApiResponse<CommerceConfigResponse>(false, null, "ค่าส่งเหมาจ่ายต้องไม่ติดลบ"));
            config.FlatShippingRate = request.FlatShippingRate;
        }
        if (request.ClearFreeShippingThreshold == true) config.FreeShippingThreshold = null;
        else if (request.FreeShippingThreshold.HasValue)
        {
            if (request.FreeShippingThreshold < 0m) return BadRequest(new ApiResponse<CommerceConfigResponse>(false, null, "ยอดส่งฟรีต้องไม่ติดลบ"));
            config.FreeShippingThreshold = request.FreeShippingThreshold;
        }
        if (request.FreeShippingOverridesZones.HasValue) config.FreeShippingOverridesZones = request.FreeShippingOverridesZones.Value;

        if (request.EnableCoupons.HasValue) config.EnableCoupons = request.EnableCoupons.Value;
        if (request.EnableReviews.HasValue) config.EnableReviews = request.EnableReviews.Value;
        if (request.ReviewAutoApprove.HasValue) config.ReviewAutoApprove = request.ReviewAutoApprove.Value;
        if (request.ReviewRequirePurchase.HasValue) config.ReviewRequirePurchase = request.ReviewRequirePurchase.Value;
        if (request.EnableWishlist.HasValue) config.EnableWishlist = request.EnableWishlist.Value;

        if (request.EnableAbandonedCartRecovery.HasValue) config.EnableAbandonedCartRecovery = request.EnableAbandonedCartRecovery.Value;
        if (request.CartExpiryDays.HasValue) config.CartExpiryDays = request.CartExpiryDays.Value;
        if (request.AbandonedCartHours.HasValue) config.AbandonedCartHours = request.AbandonedCartHours.Value;

        // คำนำหน้าเลขคำสั่งซื้อ — ผิดรูป = 400 (ไม่เก็บค่าที่ตัวรันเลขใช้ไม่ได้) · มีผลกับออเดอร์ที่สั่งหลังบันทึก
        if (request.OrderNumberPrefix != null) config.OrderNumberPrefix = CmsFeatureFlags.NormalizeOrderPrefix(request.OrderNumberPrefix);
        if (request.AutoConfirmOrders.HasValue) config.AutoConfirmOrders = request.AutoConfirmOrders.Value;
        if (request.NotifyOnNewOrder.HasValue) config.NotifyOnNewOrder = request.NotifyOnNewOrder.Value;
        if (request.OrderNotificationEmails != null) config.OrderNotificationEmails = request.OrderNotificationEmails;
        if (request.EnableQuoteConversion.HasValue) config.EnableQuoteConversion = request.EnableQuoteConversion.Value;
        if (request.ResetOrderFormFields == true) config.OrderFormFieldsJson = null;
        else if (request.OrderFormFields != null)
        {
            if (OrderFormFields.ValidateConfig(request.OrderFormFields) is string formErr)
                return BadRequest(new ApiResponse<CommerceConfigResponse>(false, null, formErr));
            config.OrderFormFieldsJson = OrderFormFields.Serialize(request.OrderFormFields);
        }

        if (request.EnableOnlinePayment.HasValue) config.EnableOnlinePayment = request.EnableOnlinePayment.Value;
        if (request.EnableCod.HasValue) config.EnableCod = request.EnableCod.Value;
        if (request.EnableBankTransfer.HasValue) config.EnableBankTransfer = request.EnableBankTransfer.Value;

        if (request.OrderConfirmMessageTh != null) config.OrderConfirmMessageTh = request.OrderConfirmMessageTh;
        if (request.OrderConfirmMessageEn != null) config.OrderConfirmMessageEn = request.OrderConfirmMessageEn;
        if (request.QuotationMessageTh != null) config.QuotationMessageTh = request.QuotationMessageTh;
        if (request.QuotationMessageEn != null) config.QuotationMessageEn = request.QuotationMessageEn;

        // ── สินค้าตามสั่ง (ทีม K) — ตรวจด้วยตัวตั้งตัวเดียวใน Helpers/ (ผิด = 400 พร้อมเหตุ ไม่เก็บค่าที่ทำให้สั่งไม่ได้) ──
        if (request.EnablePickup.HasValue) config.EnablePickup = request.EnablePickup.Value;
        if (request.MtoPaymentTermMode != null) config.MtoPaymentTermMode = PaymentTermMode.Normalize(request.MtoPaymentTermMode);
        if (request.MtoDepositPercent.HasValue) config.MtoDepositPercent = request.MtoDepositPercent;
        if (request.MtoDepositFixedAmount.HasValue) config.MtoDepositFixedAmount = request.MtoDepositFixedAmount;
        // ทีม Q (คำตัดสินข้อ 156) — ไม่รู้จัก = 400 พร้อมรายการที่เลือกได้ (ห้ามเก็บค่าที่ไม่มีผู้อ่าน) · มีผลกับออเดอร์ที่สั่งหลังบันทึกเท่านั้น
        if (request.MtoDepositInvoicing != null) config.MtoDepositInvoicing = DepositInvoicingMode.Normalize(request.MtoDepositInvoicing);
        // รอบ 206 (คำตัดสินข้อ 165): VAT ค่าส่งตามสัดส่วน · ริบมัดจำเมื่อยกเลิก — ตัวตัดสินใน Helpers (ค่าผิด = ปฏิเสธ ไม่บันทึกเงียบ)
        if (request.ShippingVatAllocation != null) config.ShippingVatAllocation = ShippingVatAllocation.Normalize(request.ShippingVatAllocation);
        if (request.ClearDepositForfeitOnCancelPercent == true) config.DepositForfeitOnCancelPercent = null;
        else if (request.DepositForfeitOnCancelPercent.HasValue)
        {
            if (SiteOrderDepositForfeit.ValidatePercent(request.DepositForfeitOnCancelPercent) is string pctErr)
                return BadRequest(new ApiResponse<CommerceConfigResponse>(false, null, pctErr));
            config.DepositForfeitOnCancelPercent = request.DepositForfeitOnCancelPercent.Value;
        }
        MtoPaymentTerms.ValidateConfig(PaymentTermMode.Normalize(config.MtoPaymentTermMode),
            config.MtoDepositPercent, config.MtoDepositFixedAmount);
        if (request.MtoClosedWeekdays != null) config.MtoClosedWeekdays = MtoLeadTime.NormalizeClosedWeekdays(request.MtoClosedWeekdays);
        if (request.ClearMtoCutoffHour) config.MtoCutoffHour = null;
        else if (request.MtoCutoffHour.HasValue)
        {
            if (request.MtoCutoffHour < 0 || request.MtoCutoffHour > 23)
                throw new BusinessRuleException("ชั่วโมงตัดรอบต้องอยู่ระหว่าง 0–23", "MTO-CALENDAR");
            config.MtoCutoffHour = request.MtoCutoffHour;
        }
        if (request.MtoSkipCompanyHolidays.HasValue) config.MtoSkipCompanyHolidays = request.MtoSkipCompanyHolidays.Value;

        // ชุดค่าตั้งทั้งก้อนต้องสั่งซื้อได้ (มีวิธีรับ ≥ 1 · วิธีชำระ ≥ 1) — ตรวจหลังรวมค่าใหม่ ไม่ใช่รายช่อง
        if (CmsFeatureFlags.ValidateConfig(config) is string cfgErr)
            return BadRequest(new ApiResponse<CommerceConfigResponse>(false, null, cfgErr));

        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        config.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<CommerceConfigResponse>(true, MapConfig(config), CmsMessages.Get("config.updated", Lang)));
    }

    private static CommerceConfigResponse MapConfig(SiteCommerceConfig c) => new()
    {
        Id = c.Id, SiteId = c.SiteId,
        EnableGuestCheckout = c.EnableGuestCheckout, RequireCustomerAccount = c.RequireCustomerAccount,
        EnableTaxInvoice = c.EnableTaxInvoice,
        EnableShipping = c.EnableShipping,
        FlatShippingRate = c.FlatShippingRate, FreeShippingThreshold = c.FreeShippingThreshold,
        FreeShippingOverridesZones = c.FreeShippingOverridesZones,
        EnableCoupons = c.EnableCoupons,
        EnableReviews = c.EnableReviews, ReviewAutoApprove = c.ReviewAutoApprove, ReviewRequirePurchase = c.ReviewRequirePurchase,
        EnableWishlist = c.EnableWishlist, EnableAbandonedCartRecovery = c.EnableAbandonedCartRecovery,
        CartExpiryDays = c.CartExpiryDays, AbandonedCartHours = c.AbandonedCartHours,
        OrderNumberPrefix = CmsFeatureFlags.OrderPrefixOf(c.OrderNumberPrefix),
        OrderNumberExample = CmsFeatureFlags.OrderNumberStem(CmsFeatureFlags.Resolve(c), DateTime.UtcNow) + "0001",
        AutoConfirmOrders = c.AutoConfirmOrders,
        NotifyOnNewOrder = c.NotifyOnNewOrder, OrderNotificationEmails = c.OrderNotificationEmails,
        EnableQuoteConversion = c.EnableQuoteConversion,
        OrderFormFields = OrderFormFields.ForDisplay(c.OrderFormFieldsJson),
        OrderFormFieldsConfigured = OrderFormFields.Parse(c.OrderFormFieldsJson) != null,
        FormFieldRequirementOptions = BookingFormFields.RequirementOptions()
            .Select(o => new CommerceOptionValue { Value = o.Value, Label = o.Label }).ToList(),
        FormFieldTypes = OrderFormFields.FieldTypes.ToList(),
        EnableOnlinePayment = c.EnableOnlinePayment, EnableCod = c.EnableCod, EnableBankTransfer = c.EnableBankTransfer,
        OrderConfirmMessageTh = c.OrderConfirmMessageTh, OrderConfirmMessageEn = c.OrderConfirmMessageEn,
        QuotationMessageTh = c.QuotationMessageTh, QuotationMessageEn = c.QuotationMessageEn,
        EnablePickup = c.EnablePickup,
        MtoPaymentTermMode = PaymentTermMode.Normalize(c.MtoPaymentTermMode),
        MtoDepositPercent = c.MtoDepositPercent, MtoDepositFixedAmount = c.MtoDepositFixedAmount,
        MtoClosedWeekdays = c.MtoClosedWeekdays, MtoCutoffHour = c.MtoCutoffHour,
        MtoSkipCompanyHolidays = c.MtoSkipCompanyHolidays,
        PaymentTermModes = PaymentTermMode.All.Select(a => new CommerceOptionValue { Value = a.Value, Label = a.Label }).ToList(),
        MtoDepositInvoicing = DepositInvoicingMode.OfOrder(c.MtoDepositInvoicing),
        ShippingVatAllocation = ShippingVatAllocation.OfOrder(c.ShippingVatAllocation),
        ShippingVatAllocationOptions = ShippingVatAllocation.All.ToList(),
        DepositForfeitOnCancelPercent = c.DepositForfeitOnCancelPercent,
        DepositInvoicingModes = DepositInvoicingMode.All
            .Select(a => new CommerceOptionValue { Value = a.Value, Label = a.Label, Description = a.TaxEffect }).ToList(),
    };
}
