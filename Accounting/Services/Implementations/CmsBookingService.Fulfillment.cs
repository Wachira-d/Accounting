using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Cms;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 226 (คำขอเจ้าของ · <c>erp-review/2026-11-01/BOOKING_PAYMENT_AUDIT.md</c> §6–§7) — ไฟล์ต่อสายของ
/// <list type="bullet">
///   <item><b>จัดส่ง / รับเอง</b>ของการจอง: ตัวตัดสินวิธีรับ <see cref="BookingFulfillment"/> + ตัวคิดค่าส่งของร้าน <see cref="ShopDeliveryFee"/> (ตัวเดียวกับคำสั่งซื้อ) ·
///     ตัวอย่างก่อนจอง = ตอนสร้างการจอง (เรียก <see cref="QuoteFulfillmentAsync"/> ตัวเดียว — ค่าส่งคิดที่เซิร์ฟเวอร์เสมอ)</item>
///   <item><b>โหมดชำระบังคับที่บริการเก็บเงินไม่ได้</b>: แก้ในคลิกเดียว (เปลี่ยนชนิด / ยกเว้น)</item>
///   <item><b>ยืนยันการจองหลังรับเงิน</b>: ข้อความ "ชำระแล้ว — ยืนยันการจอง" ถึงลูกค้า (คิวตามหลัง · คีย์กันซ้ำ)</item>
/// </list>
/// </summary>
public partial class CmsBookingService
{
    // ═══════════════ ตัวประกอบ ═══════════════

    private static BookingFulfillmentRule SiteFulfillmentRule(SiteBookingSettings? row)
        => new(row?.FulfillmentDeliveryEnabled ?? false, row?.FulfillmentPickupEnabled ?? false, row?.FulfillmentDefault);

    /// <summary>วิธีรับที่มีผลของบริการ (ค่าเว็บจากแถว <paramref name="siteRow"/>) — ตัวประกอบเดียวในไฟล์บริการ</summary>
    private static BookingFulfillmentEffective FulfillmentOf(SiteBookingService s, SiteBookingSettings? siteRow)
        => BookingFulfillment.Resolve(SiteFulfillmentRule(siteRow),
            new BookingFulfillmentRule(s.FulfillmentDeliveryEnabled, s.FulfillmentPickupEnabled, s.FulfillmentDefault));

    /// <summary>ผลคิดวิธีรับ + ค่าจัดส่งของการจองหนึ่งใบ</summary>
    private sealed record FulfillmentQuote(BookingFulfillmentQuoteDto Dto, string? Method, decimal Fee, DeliveryRateOption? Rate);

    /// <summary>
    /// <b>ตัวคิดเดียว</b>ของขั้นจัดส่ง/รับเอง — ตัวอย่างก่อนจอง (<paramref name="strict"/> false: ปัญหาเป็นข้อความในผล) และตอนสร้างการจอง
    /// (<paramref name="strict"/> true: ปัญหา = โยน <see cref="BusinessRuleException"/> ก่อนบันทึกอะไร) · ค่าส่ง = <see cref="ShopDeliveryFee.Compute"/>
    /// บนค่าบริการ (ไม่มีน้ำหนัก · ไม่มีคูปอง) · ลูกค้าส่งได้แค่วิธี/ที่อยู่/อัตราที่เลือก
    /// </summary>
    private async Task<FulfillmentQuote> QuoteFulfillmentAsync(Guid companyId, Guid siteId, SiteBookingService svc, SiteBookingSettings? siteRow,
        decimal serviceTotal, string? requestedMethod, string? province, string? postalCode, Guid? rateId, bool strict)
    {
        var eff = FulfillmentOf(svc, siteRow);
        var dto = new BookingFulfillmentQuoteDto
        {
            Enabled = eff.Enabled, DeliveryEnabled = eff.Delivery, PickupEnabled = eff.Pickup, DefaultMethod = eff.DefaultMethod,
            PickupNote = eff.Pickup ? siteRow?.FulfillmentPickupNote : null,
            DeliveryMinAmount = eff.Delivery ? siteRow?.FulfillmentDeliveryMinAmount : null,
        };
        if (!eff.Enabled)
        {
            // บริการไม่มีขั้นนี้ ⇒ ส่งวิธีมา = ปฏิเสธ (ไม่เก็บค่าที่ไม่มีผล) · ตัวอย่างก่อนจองไม่ล้ม
            if (strict) BookingFulfillment.ResolveMethod(eff, requestedMethod);
            return new FulfillmentQuote(dto, null, 0m, null);
        }

        string? method;
        try { method = BookingFulfillment.ResolveMethod(eff, requestedMethod); }
        catch (BusinessRuleException ex) when (!strict) { dto.Problem = ex.Message; return new FulfillmentQuote(dto, null, 0m, null); }
        dto.Method = method;
        dto.MethodLabel = BookingFulfillment.Label(method);
        if (method != FulfillmentMethod.Delivery) return new FulfillmentQuote(dto, method, 0m, null);

        if (BookingFulfillment.MinAmountProblem(method, serviceTotal, siteRow?.FulfillmentDeliveryMinAmount, eff.Pickup) is string minProblem)
        {
            if (strict) throw new BusinessRuleException(minProblem, BookingFulfillment.RuleCode);
            dto.Problem = minProblem;
            return new FulfillmentQuote(dto, method, 0m, null);
        }
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        var zones = await ShopDeliveryFee.LoadZonesAsync(_db, companyId, siteId);
        var address = new CalculateShippingRequest { Province = province, PostalCode = postalCode, SubTotal = serviceTotal, TotalWeightKg = 0m };
        DeliveryFeeResult r;
        try
        {
            r = ShopDeliveryFee.Compute(zones, address, serviceTotal, 0m, cfg?.FlatShippingRate, cfg?.FreeShippingThreshold,
                cfg?.FreeShippingOverridesZones ?? false, couponFreeShip: false, rateId, eff.Pickup);
        }
        catch (BusinessRuleException ex) when (!strict)
        {
            dto.Problem = ex.Message;
            return new FulfillmentQuote(dto, method, 0m, null);
        }
        dto.RateOptions = r.Options.Select(o => new BookingDeliveryRateOptionDto { RateId = o.RateId, Name = o.Name, ZoneName = o.ZoneName, Fee = o.Fee, IsFree = o.IsFree }).ToList();
        dto.SelectedRateId = r.Chosen?.RateId;
        dto.DeliveryFee = r.Fee;
        dto.Problem = r.Problem;
        if (strict && r.Problem != null) throw new BusinessRuleException(r.Problem, BookingFulfillment.RuleCode);
        return new FulfillmentQuote(dto, method, r.Problem == null ? r.Fee : 0m, r.Chosen);
    }

    /// <summary>เติมวิธีรับ/ค่าส่ง/ยอดรวมบนคำตอบ (ค่าที่ตรึงบนการจอง · ที่อยู่รับเองอ่านสดจากค่าตั้งเว็บ)</summary>
    private async Task FillFulfillmentInfoAsync(Guid companyId, Guid siteId, BookingResponse resp)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == resp.Id && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.FulfillmentMethod, x.DeliveryFee, x.DeliveryAddress, x.DeliveryProvince, x.DeliveryPostalCode, x.DeliveryRateName })
            .FirstOrDefaultAsync();
        if (b == null) return;
        resp.FulfillmentMethod = b.FulfillmentMethod;
        resp.FulfillmentLabel = b.FulfillmentMethod == null ? null : BookingFulfillment.Label(b.FulfillmentMethod);
        resp.DeliveryFee = b.DeliveryFee;
        resp.GrandTotal = BookingFulfillment.GrandTotal(resp.TotalAmount, b.DeliveryFee);
        resp.DeliveryAddress = b.DeliveryAddress; resp.DeliveryProvince = b.DeliveryProvince; resp.DeliveryPostalCode = b.DeliveryPostalCode;
        resp.DeliveryRateName = b.DeliveryRateName;
        if (b.FulfillmentMethod == FulfillmentMethod.Pickup)
            resp.PickupNote = await _db.SiteBookingSettings.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted).Select(s => s.FulfillmentPickupNote).FirstOrDefaultAsync();
        resp.FulfillmentSummary = BookingFulfillment.SummaryLine(b.FulfillmentMethod, b.DeliveryFee, b.DeliveryAddress, b.DeliveryProvince,
            b.DeliveryPostalCode, resp.PickupNote);
    }

    /// <summary>บรรทัดวิธีรับในข้อความถึงลูกค้า/ร้าน (null = การจองไม่มีขั้นนี้)</summary>
    private async Task<string?> FulfillmentNotifyLineAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.FulfillmentMethod, x.DeliveryFee, x.DeliveryAddress, x.DeliveryProvince, x.DeliveryPostalCode })
            .FirstOrDefaultAsync();
        if (b?.FulfillmentMethod == null) return null;
        var pickupNote = b.FulfillmentMethod == FulfillmentMethod.Pickup
            ? await _db.SiteBookingSettings.AsNoTracking().Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted)
                .Select(s => s.FulfillmentPickupNote).FirstOrDefaultAsync()
            : null;
        return "วิธีรับ: " + BookingFulfillment.SummaryLine(b.FulfillmentMethod, b.DeliveryFee, b.DeliveryAddress, b.DeliveryProvince, b.DeliveryPostalCode, pickupNote);
    }

    /// <summary>บรรทัด "ค่าจัดส่ง" ของเอกสาร ERP — อัตรา VAT แบบเดียวกับคำสั่งซื้อ (<see cref="ShippingVatAllocation.Split"/> อัตราสูงสุดในบิล · §79) ·
    /// <paramref name="fee"/> = ส่วนค่าส่งที่อยู่ในใบนี้ (ใบรับมัดจำ = ส่วนที่เกินค่าบริการ · ใบเต็ม = ค่าส่งทั้งหมด) · 0 = ไม่มีบรรทัด
    /// · รอบ 228: <paramref name="revenueAccountId"/> = บัญชีรายได้ค่าจัดส่งของเว็บ (<see cref="ShippingRevenueAccount.ResolveAsync"/>) — ผู้เรียกส่ง null สำหรับ<b>ใบรับมัดจำ</b>
    /// (ลงเงินรับล่วงหน้าผ่าน IsDeposit) · null = บัญชีรายได้ตั้งต้น (เดิม)</summary>
    private static IEnumerable<Models.DTOs.Document.DocumentLineRequest> DeliveryErpLines(bool vatRegistered, decimal fee, decimal serviceGross, decimal serviceVatRate,
        Guid? revenueAccountId)
    {
        var parts = ShippingVatAllocation.Split(ShippingVatAllocation.MaxRate, vatRegistered, fee, new[] { (serviceGross, serviceVatRate) });
        return parts.Select(p => new Models.DTOs.Document.DocumentLineRequest(
            Description: ShippingVatAllocation.LineDescription(p, parts.Count), Quantity: 1, Unit: "ครั้ง", UnitPrice: p.Fee,
            DiscountPercent: 0m, VatRate: p.Rate, WithholdingTaxRate: 0m, AccountId: revenueAccountId, ProductCode: null));
    }

    // ═══════════════ ค่าตั้งเว็บ: จัดส่ง / รับเอง ═══════════════

    public async Task<BookingFulfillmentSettingsDto> GetFulfillmentSettingsAsync(Guid companyId, Guid siteId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var row = await _db.SiteBookingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        var cfg = await _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);
        var zoneCount = (await ShopDeliveryFee.LoadZonesAsync(_db, companyId, siteId)).Count;
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        return new BookingFulfillmentSettingsDto
        {
            DeliveryEnabled = row?.FulfillmentDeliveryEnabled ?? false, PickupEnabled = row?.FulfillmentPickupEnabled ?? false,
            DefaultMethod = row?.FulfillmentDefault, PickupNote = row?.FulfillmentPickupNote, DeliveryMinAmount = row?.FulfillmentDeliveryMinAmount,
            // รอบ 228: ลูกค้าเปลี่ยนวิธีรับเองได้ถึงกี่ ชม. ก่อนนัด · บัญชีรายได้ค่าจัดส่ง (ตั้งที่ร้านค้า — ตัวอ่านเดียว ShippingRevenueAccount)
            ChangeCutoffHours = row?.FulfillmentChangeCutoffHours,
            ChangeCutoffSummary = BookingFulfillmentChange.DescribeCutoff(row?.FulfillmentChangeCutoffHours),
            ShippingRevenueAccountLabel = await ShippingAccountLabelAsync(companyId, siteId),
            ShippingZoneCount = zoneCount,
            FreeDeliveryWarning = (await FreeDeliveryStateAsync(companyId, siteId)).Warning,   // รอบ 228 แพ็ก C (Q3)
 ShopFlatShippingRate = cfg?.FlatShippingRate, ShopFreeShippingThreshold = cfg?.FreeShippingThreshold,
            FeeSourceSummary = zoneCount > 0
                ? $"ค่าจัดส่งคิดตามโซน/อัตราของร้าน ({zoneCount} โซน) — ลูกค้ากรอกจังหวัด + รหัสไปรษณีย์แล้วระบบคิดให้"
                : cfg?.FlatShippingRate is decimal fr && fr > 0m
                    ? $"ร้านยังไม่ตั้งโซน — ใช้ค่าส่งเหมาจ่ายของร้าน ฿{fr:N2}" + (cfg.FreeShippingThreshold is decimal t && t > 0m ? $" (ส่งฟรีเมื่อค่าบริการ ≥ ฿{t:N2})" : "")
                    : "ร้านยังไม่ตั้งโซนหรือค่าส่งเหมาจ่าย — การจองแบบจัดส่งจะ “ส่งฟรี” จนกว่าจะตั้ง (ตั้งค่าร้านค้า › โซน/อัตราค่าส่ง)",
            Services = services.Select(s => new BookingFulfillmentServiceEffectDto
            {
                Id = s.Id, Name = s.Name,
                UsesSiteSetting = BookingFulfillment.ServiceMode(s.FulfillmentDeliveryEnabled, s.FulfillmentPickupEnabled, s.FulfillmentDefault) == "Inherit",
                Summary = BookingFulfillment.Describe(FulfillmentOf(s, row)),
            }).ToList(),
        };
    }

    public async Task<BookingFulfillmentSettingsDto> UpdateFulfillmentSettingsAsync(Guid companyId, Guid siteId, BookingFulfillmentSettingsDto request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var errs = BookingFulfillment.ValidateSettings(request.DefaultMethod, request.DeliveryMinAmount, request.PickupNote);
        var def = BookingFulfillment.StoredDefault(request.DefaultMethod);
        if (BookingFulfillment.DefaultPointsToDisabled(request.DefaultMethod, request.DeliveryEnabled, request.PickupEnabled) is string defErr) errs.Add(defErr);
        if (BookingFulfillmentChange.ValidateCutoff(request.ChangeCutoffHours) is string cutErr) errs.Add(cutErr);   // รอบ 228
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), BookingFulfillment.RuleCode);
        var row = await _db.SiteBookingSettings.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        if (row == null)
        {
            row = new SiteBookingSettings { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
            _db.SiteBookingSettings.Add(row);
        }
        row.FulfillmentDeliveryEnabled = request.DeliveryEnabled;
        row.FulfillmentPickupEnabled = request.PickupEnabled;
        row.FulfillmentDefault = def;
        row.FulfillmentPickupNote = string.IsNullOrWhiteSpace(request.PickupNote) ? null : request.PickupNote.Trim();
        row.FulfillmentDeliveryMinAmount = request.DeliveryMinAmount is > 0m ? request.DeliveryMinAmount : null;
        row.FulfillmentChangeCutoffHours = request.ChangeCutoffHours;   // รอบ 228 · null = ลูกค้าเปลี่ยนเองไม่ได้ (เดิม)
        row.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return await GetFulfillmentSettingsAsync(companyId, siteId);
    }

    /// <summary>รอบ 228: "รหัส ชื่อ" ของบัญชีรายได้ค่าจัดส่งที่มีผล · null = บัญชีรายได้ตั้งต้น (ข้อความบอกบนหน้าตั้งค่าการจอง)</summary>
    private async Task<string> ShippingAccountLabelAsync(Guid companyId, Guid siteId)
    {
        var id = await ShippingRevenueAccount.ResolveAsync(_db, companyId, siteId);
        if (id is not Guid accId) return "บัญชีรายได้ตั้งต้นของเอกสาร (ยังไม่ได้ตั้งบัญชีรายได้ค่าจัดส่ง)";
        var a = await _db.ChartOfAccounts.AsNoTracking().Where(x => x.Id == accId && x.CompanyId == companyId)
            .Select(x => x.AccountCode + " " + x.AccountName).FirstOrDefaultAsync();
        return a ?? "บัญชีรายได้ตั้งต้นของเอกสาร";
    }

    // ═══════════════ โหมดบังคับ × บริการที่เก็บเงินไม่ได้ (คำตัดสิน 226-1) ═══════════════

    /// <summary>บริการที่ใช้งานอยู่ซึ่งโหมดบังคับ (ตามเว็บ) แต่ชนิดเก็บเงินไม่ได้ — <paramref name="siteRule"/> = ค่าที่จะมีผล (ก่อน/หลังบันทึก)</summary>
    private async Task<List<SiteBookingService>> UncollectableServicesAsync(Guid companyId, Guid siteId, BookingPaymentRuleInput siteRule)
    {
        var active = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && s.IsActive && !s.IsDeleted)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync();
        return active.Where(s => BookingPaymentPolicy.Resolve(s.BookingType, siteRule, BookingPaymentContextLoader.ServiceRule(s)).RequiredButUncollectable).ToList();
    }

    /// <summary>แก้ในคลิกเดียว: "convert" = เปลี่ยนชนิดเป็น "ชำระมัดจำล่วงหน้า" (ใบรับมัดจำ §78/1) · "exempt" = บริการนี้ "ไม่รับชำระล่วงหน้า" ·
    /// ฝ่ายค้านรอบ 226: (ข้อ 8) รับเฉพาะ id ที่อยู่ในรายการ "เก็บเงินไม่ได้" ของค่าตั้งปัจจุบัน + ผ่านด่านบันทึกบริการตัวเดียวกับฟอร์ม ·
    /// (ข้อ 1) เปลี่ยนชนิดมีผลกับ<b>การจองใหม่เท่านั้น</b> — การจองที่มีอยู่ใช้ชนิดที่ตรึงบนการจอง (<c>SiteBooking.BookingType</c>) จึงไม่ต้องกันด้วย 409</summary>
    public async Task<BookingPaymentSettingsDto> FixUncollectableServicesAsync(Guid companyId, Guid siteId, FixUncollectableServicesRequest request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("convert" or "exempt"))
            throw new BusinessRuleException("คำสั่งไม่ถูกต้อง (ได้: convert / exempt)", BookingPaymentPolicy.RuleCode);
        var ids = (request.ServiceIds ?? new()).Distinct().ToList();
        if (ids.Count == 0) throw new BusinessRuleException("ไม่ได้เลือกบริการ", BookingPaymentPolicy.RuleCode);
        var ctx = await LoadPaymentContextAsync(companyId, siteId);
        var eligible = (await UncollectableServicesAsync(companyId, siteId, ctx.SiteRule)).Select(x => x.Id).ToHashSet();
        if (ids.Any(id => !eligible.Contains(id)))
            throw new BusinessRuleException("บริการที่เลือกไม่อยู่ในรายการที่ต้องแก้ (รีเฟรชหน้าตั้งค่าแล้วลองใหม่)", BookingPaymentPolicy.UncollectableRuleCode, 409);
        var services = await _db.SiteBookingServices
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted && ids.Contains(s.Id)).ToListAsync();
        foreach (var s in services)
        {
            if (action == "convert") s.BookingType = BookingType.PrePayment;
            else s.PaymentMode = BookingPaymentMode.Off;
            // ด่านบันทึกบริการตัวเดียวกับฟอร์ม: ชนิด × โหมดของบริการ · โหมด/ช่องทาง/ยอดที่จะมีผล (ช่องทางของเว็บพร้อม · มัดจำมียอด)
            if (BookingPaymentPolicy.ServiceSaveProblem(s.BookingType, s.PaymentMode) is string saveProblem)
                throw new BusinessRuleException($"{s.Name}: {saveProblem}", BookingPaymentPolicy.RuleCode);
            var eff = BookingPaymentPolicy.Resolve(s.BookingType, ctx.SiteRule, BookingPaymentContextLoader.ServiceRule(s));
            if (action == "convert" && BookingPaymentPolicy.SiteSaveProblem(ctx.SiteRule, ctx.GatewayReady, ctx.TransferReady) is string chProblem)
                throw new BusinessRuleException($"{s.Name}: {chProblem}", BookingPaymentChannels.RuleCode);
            if (action == "convert" && eff.RequiredButUncollectable)
                throw new BusinessRuleException($"{s.Name}: {eff.Problem}", BookingPaymentPolicy.UncollectableRuleCode);
            s.UpdatedBy = userId;
        }
        await _db.SaveChangesAsync();
        _logger.LogInformation("แก้บริการโหมดบังคับที่เก็บเงินไม่ได้ ({Action}) {Count} รายการ · site {SiteId} โดย {User}", action, services.Count, siteId, userId);
        return await GetPaymentSettingsAsync(companyId, siteId);
    }

    // ═══════════════ ชำระแล้ว ⇒ ยืนยันการจอง (ข้อความถึงลูกค้า) ═══════════════

    /// <summary>คิวตามหลัง (<c>DeferredNotifyDispatchJob</c>) — การจองที่ "ยืนยันเพราะรับเงินครบ" ⇒ ข้อความ "ชำระแล้ว — ยืนยันการจองแล้ว" ·
    /// คีย์กันซ้ำ <c>booking:{id}:booking.paid-confirmed</c> (webhook/poll ซ้ำ ไม่ส่งซ้ำ) · การจองไม่อยู่ในสถานะยืนยันแล้ว ⇒ ไม่ส่ง (คืนเหตุ)</summary>
    public async Task<string?> DispatchPaidConfirmedNotifyAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var status = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            .Select(b => (BookingStatus?)b.Status).FirstOrDefaultAsync();
        if (DeferredNotifyPolicy.PaidConfirmedSkipReason(status) is string skip) return skip;
        if (_shopNotifier == null) return null;
        var f = await LoadBookingNotifyFactsAsync(companyId, siteId, bookingId);
        if (f == null) return "ไม่ส่ง: ไม่พบการจอง";
        var site = await SiteNameAsync(companyId, siteId);
        var paid = await _db.SiteBookings.AsNoTracking().Where(b => b.Id == bookingId && b.CompanyId == companyId).Select(b => b.PaidAmount).FirstOrDefaultAsync();
        // วิธีรับ (จัดส่ง + ที่อยู่ / รับเอง + ที่รับ) ต่อท้าย — ลูกค้าแบบจัดส่งไม่ได้ "พบกันตามนัด" ที่ร้าน
        var msg = await WithBookingLinkAsync(companyId, siteId, f, CmsNotificationText.WithLine(
            CmsNotificationText.CustomerBookingPaidConfirmed(site, f.BookingNumber, f.ServiceName, BookingDisplayName(f),
                DateOnly.FromDateTime(f.BookingDate), f.StartTime, f.ResourceName, paid),
            await FulfillmentNotifyLineAsync(companyId, siteId, bookingId)));
        var err = await _shopNotifier.NotifyCustomerAsync(companyId, siteId, CustomerNotifyEvents.BookingPaidConfirmed,
            CustomerNotifyKeys.Booking(bookingId, CustomerNotifyEvents.BookingPaidConfirmed),
            ContactOf(f.GuestEmail, f.CustomerEmail, f.AuthProvider, f.AuthProviderId, CustomerLineTarget.ForBooking(bookingId, f.CustomerId)), msg);
        if (err != null)
            await StampBookingNotesAsync(companyId, siteId, bookingId, new[] { CmsNotificationText.FailureNote("ลูกค้า", msg.Subject, err, DateTime.UtcNow) });
        return null;
    }
}
