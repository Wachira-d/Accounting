using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 แพ็ก P2 — เช็กเอาต์ร้านออนไลน์: ตัวคิดยอดตัวเดียว (หน้าเช็กเอาต์ = สร้างออเดอร์) + สวิตช์ร้าน (<see cref="CmsFeatureFlags"/>) +
/// ฟอร์มสั่งซื้อ (<see cref="OrderFormFields"/>) + วันรับที่ลูกค้าเลือก (<see cref="MtoLeadTime.RequestedDateProblem"/>) + COD ·
/// คูปองส่งฟรี (B7) · ส่งฟรีระดับร้านกับโซน + น้ำหนักตัวเลือกสินค้า (B8) · วันส่งถึงโดยประมาณ (B11)
/// <para>ส่วนนี้แค่ดึงข้อมูล (กรอง CompanyId ทุก query) แล้วส่งเข้าตัวตัดสินใน Helpers/ — หน้าร้านแสดงผลอย่างเดียว</para>
/// </summary>
public partial class CmsCommerceService
{
    private sealed record CheckoutLine(SiteProduct Sp, decimal Quantity, decimal TotalPrice, decimal VatRate, int ExtraLeadDays,
        string? VariantOptionsJson = null);
    private sealed record CheckoutComputation(CheckoutQuoteResponse Quote, PaymentDue Due, DateOnly? ReadyDate,
        decimal? DepositPercent, decimal? DepositFixed,
        string DepositInvoicing = Accounting.Helpers.DepositInvoicingMode.FullAtFirstPayment,
        string ShippingVatAllocation = Accounting.Helpers.ShippingVatAllocation.MaxRate,   // รอบ 206 R-32
        string PaymentChoice = CmsFeatureFlags.PayNow,
        DateOnly? RequestedDate = null,
        DateOnly? EstimatedDelivery = null);

    /// <summary>ผลตรวจฟอร์มสั่งซื้อก่อนบันทึกอะไร (รอบ 209 P2)</summary>
    private sealed record CheckoutFormContext(CmsFeatureSet Flags, OrderFormCleaned Form, string Fulfillment);

    private Task<SiteCommerceConfig?> LoadStoreConfigAsync(Guid companyId, Guid siteId)
        => _db.SiteCommerceConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.SiteId == siteId && c.CompanyId == companyId);

    /// <summary>
    /// <b>ด่านฟอร์มสั่งซื้อ</b> — เรียกจาก <c>CreateOrderAsync</c> ก่อนสร้าง/บันทึกอะไร (คำตัดสินข้อ 168 ง): สวิตช์สมาชิก · ขอใบกำกับ ·
    /// วิธีรับ · ฟอร์ม (ขั้นต่ำ + ค่าตั้งร้าน + ผู้ซื้อใบกำกับ §86/4 + ช่องเพิ่มเอง) · ผิดหลายข้อ = รวมข้อความเดียว (ผู้ใช้แก้ครบในรอบเดียว)
    /// </summary>
    private async Task<CheckoutFormContext> ValidateCheckoutFormAsync(Guid companyId, Guid siteId, CreateOrderRequest request)
    {
        var cfg = await LoadStoreConfigAsync(companyId, siteId);
        var flags = CmsFeatureFlags.Resolve(cfg);

        var verifiedCustomer = false;
        if (request.CustomerId is Guid cid)
        {
            var member = await _db.SiteCustomers.AsNoTracking()
                .Where(c => c.Id == cid && c.SiteId == siteId && c.CompanyId == companyId)
                .Select(c => new { c.FullName, c.Phone, c.Email, c.IsActive }).FirstOrDefaultAsync();
            verifiedCustomer = member != null;
            if (!verifiedCustomer)
                throw new BusinessRuleException("ไม่พบบัญชีลูกค้าของเว็บนี้ — เข้าสู่ระบบใหม่แล้วสั่งอีกครั้ง", CmsFeatureFlags.RuleCode);
            // รอบ 216 (คำตัดสินข้อ 174): สมาชิกเว้นชื่อ/เบอร์/อีเมล ⇒ เติมจากโปรไฟล์ก่อนด่านฟอร์ม (ไม่ล้มเพราะช่องบังคับว่าง · ค่าที่พิมพ์ชนะ) ·
            // ที่อยู่จัดส่งไม่เติมฝั่งเซิร์ฟเวอร์ (ค่าส่งคิดจากจังหวัด/รหัสไปรษณีย์ที่ลูกค้าเห็นบนหน้า — หน้าเว็บเติมให้ก่อนแล้ว)
            if (member is { IsActive: true })
                (request.ShippingName, request.ShippingPhone, request.ShippingEmail) = StorefrontMemberProfile.FillContact(
                    request.ShippingName, request.ShippingPhone, request.ShippingEmail, member.FullName, member.Phone, member.Email);
        }
        if (CmsFeatureFlags.AccountProblem(flags, verifiedCustomer) is string accErr)
            throw new BusinessRuleException(accErr, CmsFeatureFlags.RuleCode);
        if (request.RequestTaxInvoice && !flags.TaxInvoiceRequest)
            throw new BusinessRuleException(CmsFeatureFlags.TaxInvoiceOffMessage, CmsFeatureFlags.RuleCode);

        var fulfillment = CmsFeatureFlags.ResolveFulfillment(flags, request.FulfillmentMethod);
        var (errs, clean) = OrderFormFields.Check(OrderFormFields.Parse(cfg?.OrderFormFieldsJson), new OrderFormInput(
            request.ShippingName, request.ShippingPhone, request.ShippingEmail, request.ShippingAddress, request.CustomerNotes,
            request.RequestTaxInvoice, request.BillingName, request.BillingAddress, request.BillingTaxId, request.BillingBranchCode,
            request.CustomFields), fulfillment);
        if (errs.Count > 0)
            throw new BusinessRuleException(string.Join(" · ", errs), OrderFormFields.RuleCode);
        return new CheckoutFormContext(flags, clean, fulfillment);
    }

    /// <summary>ค่าที่ผ่านด่านฟอร์มแล้วทับค่าดิบบนออเดอร์ (ช่องซ่อน = null · เลขผู้เสียภาษีตัวเลขล้วน · ช่องเพิ่มเองตรึงป้าย+ค่า)</summary>
    private static void ApplyCheckoutForm(SiteOrder order, CheckoutFormContext ctx)
    {
        var f = ctx.Form;
        order.ShippingName = f.Name;
        order.ShippingPhone = f.Phone;
        order.ShippingEmail = f.Email;
        order.ShippingAddress = f.Address;
        order.CustomerNotes = f.Notes;
        order.BillingName = f.BillingName;
        order.BillingAddress = f.BillingAddress;
        order.BillingTaxId = f.BillingTaxId;
        order.BillingBranchCode = f.BillingBranchCode;
        order.CustomFieldsJson = f.CustomFieldsJson;
    }

    /// <summary>ผลตัวคิดเช็กเอาต์ที่ตรึงลงออเดอร์ (รอบ 209 P2) — วิธีชำระ · วันรับที่ขอ · วันส่งถึงโดยประมาณ · ยืนยันอัตโนมัติ (สถานะอย่างเดียว ไม่ใช่การรับเงิน)</summary>
    private static void ApplyCheckoutDecisions(SiteOrder order, CheckoutComputation checkout, CheckoutFormContext ctx)
    {
        static DateTime? AsUtcDate(DateOnly? d) => d is DateOnly x ? DateTime.SpecifyKind(x.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc) : null;
        order.PaymentMethodChoice = checkout.PaymentChoice;
        order.RequestedReadyDate = AsUtcDate(checkout.RequestedDate);
        order.EstimatedDeliveryDate = AsUtcDate(checkout.EstimatedDelivery);
        if (ctx.Flags.AutoConfirm) order.Status = SiteOrderStatus.Confirmed;
    }

    /// <summary>ปฏิทินร้าน — วันปิดประจำสัปดาห์ + ตัดรอบ (ค่าตั้งร้าน) + วันหยุดบริษัท (หน้า "วันหยุด" เมื่อเปิดใช้)</summary>
    private async Task<(ShopCalendar Cal, SiteCommerceConfig? Cfg)> LoadShopCalendarAsync(Guid companyId, Guid siteId)
    {
        var cfg = await LoadStoreConfigAsync(companyId, siteId);
        var holidays = new HashSet<DateOnly>();
        if (cfg?.MtoSkipCompanyHolidays ?? true)
        {
            var from = DateTime.UtcNow.AddDays(-2);
            var until = DateTime.UtcNow.AddDays(800);
            var rows = await _db.PublicHolidays.AsNoTracking()
                .Where(h => h.CompanyId == companyId && !h.IsDeleted && h.Date >= from && h.Date <= until)
                .Select(h => h.Date).ToListAsync();
            foreach (var d in rows) holidays.Add(DateOnly.FromDateTime(ThaiDate.CalendarDateUtc(d)));
        }
        var cutoff = cfg?.MtoCutoffHour is int h && h >= 0 && h <= 23 ? h : (int?)null;
        return (new ShopCalendar(MtoLeadTime.ParseClosedWeekdays(cfg?.MtoClosedWeekdays), holidays, cutoff), cfg);
    }

    /// <summary>คูปองที่ใช้ตัดสิน "ส่งฟรี" — ร้านปิดคูปอง/ไม่มีรหัส = null</summary>
    private async Task<CouponShippingFacts?> LoadCouponShippingFactsAsync(Guid companyId, Guid siteId, CmsFeatureSet flags, string? couponCode)
    {
        if (!flags.Coupons || string.IsNullOrWhiteSpace(couponCode)) return null;
        var code = couponCode.Trim().ToUpperInvariant();
        return await _db.SiteCoupons.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.SiteId == siteId && c.Code == code)
            .Select(c => new CouponShippingFacts(c.IsActive, c.IsDeleted, c.FreeShipping, c.DiscountType,
                c.StartsAt, c.ExpiresAt, c.MaxUses, c.CurrentUses, c.MinOrderAmount))
            .FirstOrDefaultAsync();
    }

    /// <summary>น้ำหนักรวมของตะกร้า — น้ำหนักตัวเลือกสินค้า (variant) ที่ตรงกับตัวเลือกในตะกร้า ?? น้ำหนักสินค้า (B8)</summary>
    private async Task<decimal> CheckoutWeightKgAsync(Guid companyId, IReadOnlyList<CheckoutLine> lines)
    {
        var withVariant = lines.Where(l => !string.IsNullOrWhiteSpace(l.VariantOptionsJson)).Select(l => l.Sp.Id).Distinct().ToList();
        var variants = new List<(Guid SiteProductId, string? OptionsJson, decimal? WeightKg, bool IsActive)>();
        if (withVariant.Count > 0)
        {
            var rows = await _db.SiteProductVariants.AsNoTracking()
                .Where(v => v.CompanyId == companyId && withVariant.Contains(v.SiteProductId) && !v.IsDeleted)
                .Select(v => new { v.SiteProductId, v.OptionsJson, v.WeightKg, v.IsActive })
                .ToListAsync();
            foreach (var r in rows) variants.Add((r.SiteProductId, r.OptionsJson, r.WeightKg, r.IsActive));
        }
        return lines.Sum(l => ShippingFeeRules.LineWeightKg(l.Quantity, l.Sp.WeightKg,
            ShippingFeeRules.VariantWeightOf(l.VariantOptionsJson,
                variants.Where(v => v.SiteProductId == l.Sp.Id).Select(v => (v.OptionsJson, v.WeightKg, v.IsActive)))));
    }

    /// <summary>ตัวคิดยอดสั่งซื้อตัวเดียว — หน้าเช็กเอาต์ (<see cref="CheckoutQuoteAsync"/>) และ <c>CreateOrderAsync</c> ได้ผลเดียวกันเสมอ</summary>
    private async Task<CheckoutComputation> ComputeCheckoutAsync(Guid companyId, Guid siteId,
        IReadOnlyList<CheckoutLine> lines, decimal discount, CheckoutQuoteRequest req, bool vatRegistered, string? couponCode = null)
    {
        var (cal, cfg) = await LoadShopCalendarAsync(companyId, siteId);
        var flags = CmsFeatureFlags.Resolve(cfg);
        // รอบ 209 P2: วิธีรับผ่านสวิตช์ร้าน — ปิดจัดส่ง ⇒ ขอจัดส่ง = ปฏิเสธ (ว่าง = มารับเองเมื่อร้านรับเองอย่างเดียว)
        var method = CmsFeatureFlags.ResolveFulfillment(flags, req.FulfillmentMethod);
        discount = CmsFeatureFlags.EffectiveDiscount(flags, discount);
        var formCfg = OrderFormFields.ForDisplay(cfg?.OrderFormFieldsJson);

        var goods = lines.Sum(l => l.TotalPrice);
        var goodsAfterDiscount = Math.Max(0m, goods - discount);
        var quote = new CheckoutQuoteResponse
        {
            GoodsTotal = goods, DiscountAmount = discount,
            FulfillmentMethod = method, FulfillmentLabel = Accounting.Helpers.FulfillmentMethod.Label(method),
            PickupEnabled = flags.Pickup,
            DeliveryEnabled = flags.Delivery,
            Features = CmsFeatureFlags.ToStorefront(flags),
            FormFields = formCfg,
            CustomFields = OrderFormFields.ApplicableFields(formCfg, method).ToList(),
            CouponCode = flags.Coupons && !string.IsNullOrWhiteSpace(couponCode) ? couponCode.Trim().ToUpperInvariant() : null,
        };
        if (flags.OnlinePayment || flags.BankTransfer)
            quote.PaymentChoices.Add(new CommerceOptionValue { Value = CmsFeatureFlags.PayNow, Label = CmsFeatureFlags.PaymentChoiceLabel(CmsFeatureFlags.PayNow, method) });
        if (flags.Cod)
            quote.PaymentChoices.Add(new CommerceOptionValue { Value = CmsFeatureFlags.PayCod, Label = CmsFeatureFlags.PaymentChoiceLabel(CmsFeatureFlags.PayCod, method) });

        var couponFreeShip = ShippingFeeRules.CouponGrantsFreeShipping(
            await LoadCouponShippingFactsAsync(companyId, siteId, flags, couponCode), DateTime.UtcNow, goodsAfterDiscount);

        decimal fee = 0m;
        CheckoutShippingOption? chosen = null;
        if (method == Accounting.Helpers.FulfillmentMethod.Delivery)
        {
            var weight = await CheckoutWeightKgAsync(companyId, lines);
            var zones = await _db.SiteShippingZones.AsNoTracking()
                .Include(z => z.Rates.Where(r => r.IsActive && !r.IsDeleted))
                .Where(z => z.SiteId == siteId && z.CompanyId == companyId && z.IsActive && !z.IsDeleted)
                .OrderBy(z => z.SortOrder)
                .ToListAsync();
            var address = new CalculateShippingRequest
            {
                CountryCode = req.CountryCode, Province = req.Province, PostalCode = req.PostalCode,
                TotalWeightKg = weight, SubTotal = goodsAfterDiscount,
            };
            foreach (var zone in zones.Where(z => CmsShippingService.ZoneMatchesAddress(z, address)))
                foreach (var rate in zone.Rates.OrderBy(r => r.SortOrder))
                {
                    var q = ShippingFeeRules.Quote(new ShippingRateFacts(rate.RateType, rate.BaseRate, rate.PerKgRate,
                        rate.FreeAboveAmount, rate.MinWeightKg, rate.MaxWeightKg), goodsAfterDiscount, weight);
                    // B8 — เกณฑ์ส่งฟรีระดับร้านทับอัตราโซนเฉพาะเมื่อร้านเลือก (ค่าเริ่มต้น = เดิม)
                    q = ShippingFeeRules.ApplyShopFreeThreshold(q, cfg?.FreeShippingThreshold, flags.FreeShippingOverridesZones, goodsAfterDiscount);
                    if (!q.Applicable) continue;
                    quote.ShippingOptions.Add(new CheckoutShippingOption
                    {
                        RateId = rate.Id, Name = rate.Name, ZoneName = zone.Name,
                        // B7 — คูปองส่งฟรี ⇒ ทุกตัวเลือก 0 (ลูกค้าเห็นตรงกับยอดที่ถูกเก็บ)
                        Fee = couponFreeShip ? 0m : q.Fee, IsFree = couponFreeShip || q.IsFree,
                        EstimatedDeliveryDaysMin = rate.EstimatedDeliveryDaysMin, EstimatedDeliveryDaysMax = rate.EstimatedDeliveryDaysMax,
                    });
                }

            if (quote.ShippingOptions.Count > 0)
            {
                if (req.ShippingRateId is Guid rid)
                    chosen = quote.ShippingOptions.FirstOrDefault(o => o.RateId == rid)
                        ?? throw new BusinessRuleException("วิธีจัดส่งที่เลือกใช้กับที่อยู่/น้ำหนักนี้ไม่ได้ — เลือกใหม่", "SHIP-RATE");
                else if (quote.ShippingOptions.Count == 1)
                    chosen = quote.ShippingOptions[0];
                if (chosen != null) { fee = chosen.Fee; quote.SelectedShippingRateId = chosen.RateId; }
                else quote.Problem = "กรุณาเลือกวิธีจัดส่ง";
            }
            else if (zones.Count > 0)
            {
                // โซนจำกัดพื้นที่ + ยังไม่กรอกที่อยู่ ⇒ บอกให้กรอก (ไม่ใช่ "ไม่มีวิธีส่ง") · กรอกแล้วไม่ตรง ⇒ บอกทางไปต่อ
                var missingAddress = string.IsNullOrWhiteSpace(req.Province) || string.IsNullOrWhiteSpace(req.PostalCode);
                quote.Problem = missingAddress && zones.Any(CmsShippingService.ZoneNeedsAddress)
                    ? "กรุณากรอกจังหวัดและรหัสไปรษณีย์เพื่อคำนวณค่าจัดส่ง"
                    : "ยังไม่มีวิธีจัดส่งสำหรับจังหวัด/รหัสไปรษณีย์นี้ — ตรวจการสะกด"
                        + (flags.Pickup ? " หรือเลือกมารับเองที่ร้าน" : " หรือติดต่อร้านเพื่อสั่งทางอื่น");
            }
            else
            {
                // ร้านไม่ได้ตั้งโซน ⇒ ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบยอดระดับร้าน
                fee = couponFreeShip ? 0m : ShippingFeeRules.SiteFlatFee(cfg?.FlatShippingRate, cfg?.FreeShippingThreshold, goodsAfterDiscount);
            }
            quote.FreeShippingByCoupon = couponFreeShip;
        }
        quote.ShippingFee = fee;
        quote.ShippingVatRate = fee > 0m ? ShippingFeeRules.VatRateForShipping(vatRegistered, lines.Select(l => l.VatRate)) : 0m;
        // สูตรยอดเดียวกับ CreateOrderAsync: Σ สินค้า + ค่าส่ง − ส่วนลด (ไม่ติดลบ)
        quote.OrderTotal = Math.Max(0m, goods + fee - discount);

        // วันรับเร็วสุด = ช้าสุดในบรรทัดที่เป็นสินค้าตามสั่ง (ของต้องครบทั้งออเดอร์)
        DateOnly? ready = null;
        var now = BangkokNow();
        foreach (var l in lines.Where(l => l.Sp.LeadTimeDays.HasValue))
        {
            var d = MtoLeadTime.EarliestReadyDate(now, l.Sp.LeadTimeDays!.Value, l.ExtraLeadDays, cal);
            if (ready == null || d > ready) ready = d;
        }
        quote.HasMadeToOrder = ready != null;
        quote.EarliestReadyDate = ready?.ToString("yyyy-MM-dd");

        // รอบ 209 (คำตัดสินข้อ 168 จ): วันที่ลูกค้าขอรับ — ≥ วันพร้อมเร็วสุด (ไม่มีสินค้าตามสั่ง = วันเปิดร้านแรกที่รับได้) · ไม่ใช่วันปิดร้าน · ไม่เลือก = เดิม
        DateOnly? requested = null;
        if (formCfg.RequestedDate != BookingFieldRequirement.Hidden)
        {
            var minDate = ready ?? MtoLeadTime.EarliestReadyDate(now, 0, 0, cal);
            quote.MinRequestedDate = minDate.ToString("yyyy-MM-dd");
            var raw = (req.RequestedReadyDate ?? "").Trim();
            if (raw.Length > 0)
            {
                if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var rd))
                    quote.Problem ??= "วันที่ต้องการรับไม่ถูกต้อง";
                else if (MtoLeadTime.RequestedDateProblem(rd, minDate, cal) is string dateErr)
                    quote.Problem ??= dateErr;
                else
                {
                    requested = rd;
                    quote.RequestedReadyDate = rd.ToString("yyyy-MM-dd");
                }
            }
            else if (formCfg.RequestedDate == BookingFieldRequirement.Required)
                quote.Problem ??= "กรุณาเลือกวันที่ต้องการรับสินค้า";
        }

        // เงื่อนไขชำระ — ใช้เฉพาะออเดอร์ที่มีสินค้าตามสั่ง · ไม่มี = เต็มจำนวน (พฤติกรรมเดิม)
        var mode = quote.HasMadeToOrder ? PaymentTermMode.Normalize(cfg?.MtoPaymentTermMode) : PaymentTermMode.Full;
        // รอบ 209 (คำตัดสินข้อ 168 ค): COD = ชำระเมื่อรับสินค้า (ยังไม่ชำระจนพนักงานบันทึกรับเงิน) · สินค้าตามสั่งที่ต้องมัดจำ ใช้ COD ไม่ได้
        var choice = CmsFeatureFlags.ResolvePaymentChoice(flags, req.PaymentMethodChoice);
        if (choice == CmsFeatureFlags.PayCod)
        {
            if (CmsFeatureFlags.CodProblem(mode) is string codErr) quote.Problem ??= codErr;
            else mode = CmsFeatureFlags.PaymentTermFor(choice, mode);
        }
        quote.PaymentMethodChoice = choice;
        quote.PaymentMethodLabel = CmsFeatureFlags.PaymentChoiceLabel(choice, method);
        var due = MtoPaymentTerms.Compute(mode, quote.OrderTotal, cfg?.MtoDepositPercent, cfg?.MtoDepositFixedAmount, 0m);
        quote.PaymentTermMode = due.Mode;
        quote.PaymentTermLabel = PaymentTermMode.Label(due.Mode);
        quote.DepositRequiredAmount = due.DepositRequired;
        quote.AmountDueNow = due.DueNow;
        quote.BalanceAfterDueNow = Math.Max(0m, due.Balance - due.DueNow);
        // ทีม Q — วิธีออกเอกสารของมัดจำ (ค่าตั้งร้าน) · ข้อความถึงลูกค้ามาจากตัวตัดสินตัวเดียว (หน้าเว็บแสดงอย่างเดียว)
        var depositInvoicing = Accounting.Helpers.DepositInvoicingMode.OfOrder(cfg?.MtoDepositInvoicing);
        quote.DepositInvoicingNote = Accounting.Helpers.SiteOrderDepositInvoicing.CustomerNote(depositInvoicing,
            paysInInstallments: due.DueNow < due.Balance);

        // B11 — วันส่งถึงโดยประมาณ: วันออกของ (วันที่ขอ ?? วันพร้อม ?? วันเปิดร้านแรก) + วันขนส่งสูงสุดของอัตราที่เลือก · ไม่รู้ = null
        DateOnly? eta = null;
        if (method == Accounting.Helpers.FulfillmentMethod.Delivery && chosen?.EstimatedDeliveryDaysMax is int maxDays)
            eta = MtoLeadTime.EstimatedDeliveryDate(requested ?? ready ?? MtoLeadTime.EarliestReadyDate(now, 0, 0, cal), maxDays);
        quote.EstimatedDeliveryDate = eta?.ToString("yyyy-MM-dd");

        return new CheckoutComputation(quote, due, ready, cfg?.MtoDepositPercent, cfg?.MtoDepositFixedAmount, depositInvoicing,
            Accounting.Helpers.ShippingVatAllocation.OfOrder(cfg?.ShippingVatAllocation), choice, requested, eta);
    }

    public async Task<CheckoutQuoteResponse> CheckoutQuoteAsync(Guid companyId, Guid siteId, Guid cartId, CheckoutQuoteRequest request)
    {
        await EnsureCartScopeAsync(companyId, siteId, cartId);
        var cart = await _db.SiteCarts.AsNoTracking().FirstAsync(c => c.Id == cartId && c.CompanyId == companyId);
        var items = await _db.SiteCartItems.AsNoTracking()
            .Include(i => i.SiteProduct).ThenInclude(p => p.Product)
            .Where(i => i.CartId == cartId && i.CompanyId == companyId).ToListAsync();
        var lines = items.Select(i => new CheckoutLine(i.SiteProduct, i.Quantity, i.TotalPrice, i.SiteProduct.Product.VatRate, i.ExtraLeadDays,
            i.VariantOptionsJson)).ToList();
        var vatRegistered = await Accounting.Helpers.CompanyVatStatus.IsRegisteredAsync(_db, companyId);
        return (await ComputeCheckoutAsync(companyId, siteId, lines, cart.DiscountAmount, request, vatRegistered, cart.CouponCode)).Quote;
    }

    /// <summary>ช่องทางชำระที่หน้าสั่งซื้อสำเร็จแสดง — ผ่านสวิตช์ร้าน (โอน/ออนไลน์/COD/ขอใบเสนอราคา) · ร้านปิดโอน ⇒ ไม่ส่งเลขบัญชี/PromptPay</summary>
    public async Task<StorefrontPaymentOptions> GetStorefrontPaymentOptionsAsync(Guid companyId, Guid siteId)
    {
        var flags = CmsFeatureFlags.Resolve(await LoadStoreConfigAsync(companyId, siteId));
        var result = new StorefrontPaymentOptions
        {
            BankTransferEnabled = flags.BankTransfer,
            OnlinePaymentEnabled = flags.OnlinePayment,
            CodEnabled = flags.Cod,
            QuoteConversionEnabled = flags.QuoteConversion,
        };
        if (!flags.BankTransfer) return result;   // HasPaymentMethod = false · หน้าเว็บซ่อนกล่องโอน (ไม่ใช่ "ร้านยังไม่ตั้งบัญชี")
        var gw = await _db.Set<SitePaymentGateway>()
            .AsNoTracking()
            .Where(g => g.CompanyId == companyId && g.SiteId == siteId
                     && g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.SortOrder)
            .FirstOrDefaultAsync();
        if (gw == null) return result;
        result.PromptPayId = gw.PromptPayId;
        result.PromptPayQrUrl = gw.PromptPayQrUrl;
        result.BankName = gw.BankName;
        result.BankAccountNumber = gw.BankAccountNumber;
        result.BankAccountName = gw.BankAccountName;
        result.HasPaymentMethod = !string.IsNullOrEmpty(gw.PromptPayId) || !string.IsNullOrEmpty(gw.BankAccountNumber);
        return result;
    }

    /// <summary>สวิตช์ร้านของเว็บนี้ (ใช้โดยคอนโทรลเลอร์ที่ต้องปฏิเสธฟังก์ชันที่ร้านปิด — ขอใบเสนอราคา ฯลฯ)</summary>
    public async Task<CmsFeatureSet> GetFeatureFlagsAsync(Guid companyId, Guid siteId)
        => CmsFeatureFlags.Resolve(await LoadStoreConfigAsync(companyId, siteId));
}
