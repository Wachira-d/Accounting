using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 แพ็ก P2 — ค่าตั้งร้าน (CmsFeatureFlags) · ฟอร์มสั่งซื้อ (OrderFormFields) · วันรับที่เลือก (MtoLeadTime) · COD ·
/// คูปองส่งฟรี (B7) · ส่งฟรีระดับร้านกับโซน + น้ำหนักตัวเลือก (B8) · ไม่คืนค่าส่งหลังจัดส่ง (B10) · วันส่งถึง (B11)
/// ทุกกลุ่มล็อกทั้ง "เคสใหม่" และ "ค่าเริ่มต้น = พฤติกรรมเดิม"
/// </summary>
public class CmsRound209P2Tests
{
    private const string ValidTaxId = "0105536000313";   // checksum ผ่าน (หลักสุดท้าย 3)

    // ═════════ CmsFeatureFlags — ค่าเริ่มต้น = เดิม ═════════

    [Fact]
    public void Defaults_equal_old_behaviour()
    {
        var f = CmsFeatureFlags.Resolve(null);
        Assert.True(f.Delivery); Assert.False(f.Pickup);
        Assert.True(f.OnlinePayment); Assert.True(f.BankTransfer); Assert.False(f.Cod);
        Assert.True(f.GuestCheckout); Assert.False(f.AutoConfirm);
        Assert.True(f.Coupons); Assert.True(f.TaxInvoiceRequest);
        Assert.True(f.Reviews); Assert.False(f.ReviewAutoApprove); Assert.False(f.ReviewRequirePurchase);
        Assert.True(f.Wishlist); Assert.True(f.QuoteConversion); Assert.False(f.FreeShippingOverridesZones);
        Assert.Equal("WEB", f.OrderNumberPrefix);
        Assert.Equal(f, CmsFeatureFlags.Resolve(new SiteCommerceConfig()));
        Assert.Equal("WEB-2610-", CmsFeatureFlags.OrderNumberStem(f, new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Switches_off_are_resolved()
    {
        var f = CmsFeatureFlags.Resolve(new SiteCommerceConfig
        {
            EnableShipping = false, EnablePickup = true, EnableCod = true, RequireCustomerAccount = true,
            EnableCoupons = false, EnableTaxInvoice = false, EnableReviews = false, EnableWishlist = false,
            EnableQuoteConversion = false, AutoConfirmOrders = true, OrderNumberPrefix = "shop1",
        });
        Assert.False(f.Delivery); Assert.True(f.Pickup); Assert.True(f.Cod);
        Assert.False(f.GuestCheckout); Assert.True(f.AutoConfirm);
        Assert.False(f.Coupons); Assert.False(f.TaxInvoiceRequest); Assert.False(f.Reviews);
        Assert.False(f.Wishlist); Assert.False(f.QuoteConversion);
        Assert.Equal("SHOP1", f.OrderNumberPrefix);
        Assert.False(CmsFeatureFlags.ToStorefront(f)["delivery"]);
        Assert.True(CmsFeatureFlags.ToStorefront(f)["cod"]);
    }

    [Fact]
    public void Config_must_keep_one_fulfilment_and_one_payment()
    {
        Assert.Null(CmsFeatureFlags.ValidateConfig(new SiteCommerceConfig()));
        Assert.NotNull(CmsFeatureFlags.ValidateConfig(new SiteCommerceConfig { EnableShipping = false, EnablePickup = false }));
        Assert.NotNull(CmsFeatureFlags.ValidateConfig(new SiteCommerceConfig { EnableOnlinePayment = false, EnableBankTransfer = false, EnableCod = false }));
        Assert.Null(CmsFeatureFlags.ValidateConfig(new SiteCommerceConfig { EnableShipping = false, EnablePickup = true }));
        Assert.Null(CmsFeatureFlags.ValidateConfig(new SiteCommerceConfig { EnableOnlinePayment = false, EnableBankTransfer = false, EnableCod = true }));
    }

    [Theory]
    [InlineData("", "WEB")]
    [InlineData(" web ", "WEB")]
    [InlineData("Shop2", "SHOP2")]
    public void Order_prefix_normalized(string raw, string expected) => Assert.Equal(expected, CmsFeatureFlags.NormalizeOrderPrefix(raw));

    [Theory]
    [InlineData("WEB-")]
    [InlineData("ร้าน")]
    [InlineData("ABCDEFGHIJK")]
    public void Order_prefix_invalid_rejected_but_stored_bad_value_falls_back(string raw)
    {
        Assert.Throws<BusinessRuleException>(() => CmsFeatureFlags.NormalizeOrderPrefix(raw));
        Assert.Equal("WEB", CmsFeatureFlags.OrderPrefixOf(raw));
    }

    [Fact]
    public void Fulfilment_follows_switches()
    {
        var both = CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnablePickup = true });
        Assert.Equal(FulfillmentMethod.Delivery, CmsFeatureFlags.ResolveFulfillment(both, null));       // เดิม
        Assert.Equal(FulfillmentMethod.Pickup, CmsFeatureFlags.ResolveFulfillment(both, "pickup"));

        var pickupOnly = CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableShipping = false, EnablePickup = true });
        Assert.Equal(FulfillmentMethod.Pickup, CmsFeatureFlags.ResolveFulfillment(pickupOnly, null));   // ร้านรับเองอย่างเดียว
        Assert.Throws<BusinessRuleException>(() => CmsFeatureFlags.ResolveFulfillment(pickupOnly, "delivery"));

        var deliveryOnly = CmsFeatureFlags.Defaults;
        Assert.Throws<BusinessRuleException>(() => CmsFeatureFlags.ResolveFulfillment(deliveryOnly, "pickup"));
    }

    // ═════════ COD (คำตัดสินข้อ 168 ค) ═════════

    [Fact]
    public void Cod_choice_requires_switch()
    {
        Assert.Equal(CmsFeatureFlags.PayNow, CmsFeatureFlags.ResolvePaymentChoice(CmsFeatureFlags.Defaults, null));
        Assert.Throws<BusinessRuleException>(() => CmsFeatureFlags.ResolvePaymentChoice(CmsFeatureFlags.Defaults, "cod"));
        var cod = CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableCod = true });
        Assert.Equal(CmsFeatureFlags.PayCod, CmsFeatureFlags.ResolvePaymentChoice(cod, "COD"));
        Assert.Equal(CmsFeatureFlags.PayNow, CmsFeatureFlags.ResolvePaymentChoice(cod, ""));
        var codOnly = CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableCod = true, EnableOnlinePayment = false, EnableBankTransfer = false });
        Assert.Equal(CmsFeatureFlags.PayCod, CmsFeatureFlags.ResolvePaymentChoice(codOnly, null));
        Assert.Throws<BusinessRuleException>(() => CmsFeatureFlags.ResolvePaymentChoice(cod, "bitcoin"));
    }

    [Fact]
    public void Cod_order_is_unpaid_pay_on_pickup_and_blocked_for_deposit_mto()
    {
        Assert.Equal(PaymentTermMode.PayOnPickup, CmsFeatureFlags.PaymentTermFor(CmsFeatureFlags.PayCod, PaymentTermMode.Full));
        Assert.Equal(PaymentTermMode.Full, CmsFeatureFlags.PaymentTermFor(CmsFeatureFlags.PayNow, PaymentTermMode.Full));
        var due = MtoPaymentTerms.Compute(PaymentTermMode.PayOnPickup, 500m, null, null, 0m);
        Assert.Equal(0m, due.DueNow);           // ไม่ต้องจ่ายตอนสั่ง — ยังไม่ชำระจนพนักงานบันทึกรับเงิน
        Assert.Equal(500m, due.Balance);
        Assert.NotNull(CmsFeatureFlags.CodProblem(PaymentTermMode.DepositPercent));
        Assert.NotNull(CmsFeatureFlags.CodProblem(PaymentTermMode.DepositFixed));
        Assert.Null(CmsFeatureFlags.CodProblem(PaymentTermMode.Full));
    }

    // ═════════ สมาชิก · คูปอง · รีวิว · ใบเสนอราคา ═════════

    [Fact]
    public void Account_required_only_when_guest_off()
    {
        Assert.Null(CmsFeatureFlags.AccountProblem(CmsFeatureFlags.Defaults, false));
        var members = CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableGuestCheckout = false });
        Assert.NotNull(CmsFeatureFlags.AccountProblem(members, false));
        Assert.Null(CmsFeatureFlags.AccountProblem(members, true));
    }

    [Fact]
    public void Coupon_discount_dropped_when_coupons_off()
    {
        Assert.Equal(50m, CmsFeatureFlags.EffectiveDiscount(CmsFeatureFlags.Defaults, 50m));
        Assert.Equal(0m, CmsFeatureFlags.EffectiveDiscount(CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableCoupons = false }), 50m));
    }

    [Fact]
    public void Review_decision_both_directions()
    {
        var old = CmsFeatureFlags.ReviewDecision(CmsFeatureFlags.Defaults, false);   // เดิม: รับ + รออนุมัติ
        Assert.Null(old.Problem); Assert.False(old.Approved);
        Assert.NotNull(CmsFeatureFlags.ReviewDecision(CmsFeatureFlags.Resolve(new SiteCommerceConfig { EnableReviews = false }), true).Problem);
        var purchase = CmsFeatureFlags.Resolve(new SiteCommerceConfig { ReviewRequirePurchase = true, ReviewAutoApprove = true });
        Assert.NotNull(CmsFeatureFlags.ReviewDecision(purchase, false).Problem);
        var bought = CmsFeatureFlags.ReviewDecision(purchase, true);
        Assert.Null(bought.Problem); Assert.True(bought.Approved);
    }

    [Fact]
    public void Quotation_footer_language()
    {
        Assert.Equal("ไทย", CmsFeatureFlags.QuotationFooter("ไทย", "EN", "th"));
        Assert.Equal("EN", CmsFeatureFlags.QuotationFooter("ไทย", "EN", "en"));
        Assert.Equal("ไทย", CmsFeatureFlags.QuotationFooter("ไทย", " ", "en"));   // ไม่ตั้งฉบับอังกฤษ = ไทยเดิม
    }

    // ═════════ OrderFormFields (คำตัดสินข้อ 168 ง · A10) ═════════

    private static OrderFormInput Input(string? name = "สมชาย", string? phone = "0812345678", string? email = null,
        string? address = "1 ถ.สุขุมวิท", bool tax = false, string? taxId = null, string? branch = null,
        Dictionary<string, string?>? custom = null, string? notes = null)
        => new(name, phone, email, address, notes, tax, tax ? "บริษัท ก จำกัด" : null, tax ? "กรุงเทพฯ" : null, taxId, branch, custom);

    [Fact]
    public void Unconfigured_form_enforces_minimum_only()
    {
        var (errs, v) = OrderFormFields.Check(null, Input(), FulfillmentMethod.Delivery);
        Assert.Empty(errs);                                  // ทิศตรงข้าม: ฟอร์มเดิมที่กรอกครบยังผ่าน
        Assert.Equal("สมชาย", v.Name);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(name: " "), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(phone: null, email: null), FulfillmentMethod.Delivery).Errors);
        Assert.Empty(OrderFormFields.Check(null, Input(phone: null, email: "a@b.co"), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(address: null), FulfillmentMethod.Delivery).Errors);
        var pickup = OrderFormFields.Check(null, Input(address: null), FulfillmentMethod.Pickup);
        Assert.Empty(pickup.Errors);
        Assert.Equal("มารับเองที่ร้าน", pickup.Values.Address);
    }

    [Fact]
    public void Configured_form_required_and_hidden()
    {
        var cfg = new OrderFormFieldsConfig(Phone: BookingFieldRequirement.Hidden, Email: BookingFieldRequirement.Required,
            Notes: BookingFieldRequirement.Required);
        var (errs, _) = OrderFormFields.Check(cfg, Input(), FulfillmentMethod.Delivery);
        Assert.Contains(errs, e => e.Contains("อีเมล"));
        Assert.Contains(errs, e => e.Contains("หมายเหตุ"));
        var ok = OrderFormFields.Check(cfg, Input(email: "a@b.co", notes: "ด่วน"), FulfillmentMethod.Delivery);
        Assert.Empty(ok.Errors);
        Assert.Null(ok.Values.Phone);                        // ช่องซ่อน ⇒ ไม่เก็บแม้ส่งมา
    }

    [Fact]
    public void Config_validation()
    {
        Assert.Null(OrderFormFields.ValidateConfig(OrderFormFields.Default));
        Assert.NotNull(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(Phone: BookingFieldRequirement.Hidden, Email: BookingFieldRequirement.Hidden)));
        Assert.NotNull(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("bad key", "x") })));
        Assert.NotNull(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("a", "x"), new OrderCustomField("A", "y") })));
        Assert.NotNull(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("s", "ขนาด", Type: "select") })));
        Assert.NotNull(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("s", "x", Type: "color") })));
        Assert.Null(OrderFormFields.ValidateConfig(new OrderFormFieldsConfig(CustomFields: new[] { new OrderCustomField("cake_text", "ข้อความบนเค้ก") })));
        Assert.Null(OrderFormFields.Parse(null));
        Assert.Null(OrderFormFields.Parse("{bad"));
        var rt = OrderFormFields.Parse(OrderFormFields.Serialize(new OrderFormFieldsConfig(RequestedDate: BookingFieldRequirement.Required)));
        Assert.Equal(BookingFieldRequirement.Required, rt!.RequestedDate);
    }

    [Fact]
    public void Custom_fields_checked_and_snapshot()
    {
        var cfg = new OrderFormFieldsConfig(CustomFields: new[]
        {
            new OrderCustomField("cake_text", "ข้อความบนเค้ก", Requirement: BookingFieldRequirement.Required, MaxLength: 10),
            new OrderCustomField("size", "ขนาด", Type: "select", Options: new[] { "1 ปอนด์", "2 ปอนด์" }),
            new OrderCustomField("room", "ห้อง", AppliesTo: "delivery", Requirement: BookingFieldRequirement.Required),
            new OrderCustomField("agree", "ยอมรับเงื่อนไข", Type: "checkbox", Requirement: BookingFieldRequirement.Required),
        });
        var bad = OrderFormFields.Check(cfg, Input(custom: new() { ["size"] = "3 ปอนด์", ["cake_text"] = "สุขสันต์วันเกิดนะคะลูกรัก" }), FulfillmentMethod.Delivery);
        Assert.Contains(bad.Errors, e => e.Contains("ขนาด"));
        Assert.Contains(bad.Errors, e => e.Contains("ข้อความบนเค้ก"));
        Assert.Contains(bad.Errors, e => e.Contains("ห้อง"));
        Assert.Contains(bad.Errors, e => e.Contains("ยอมรับ"));
        // มารับเอง ⇒ ช่องเฉพาะจัดส่งไม่บังคับ
        Assert.Equal(new[] { "cake_text", "size", "agree" }, OrderFormFields.ApplicableFields(cfg, FulfillmentMethod.Pickup).Select(f => f.Key));
        var ok = OrderFormFields.Check(cfg, Input(custom: new() { ["cake_text"] = "HBD", ["size"] = "2 ปอนด์", ["agree"] = "true" }), FulfillmentMethod.Pickup);
        Assert.Empty(ok.Errors);
        var answers = OrderFormFields.ParseAnswers(ok.Values.CustomFieldsJson);
        Assert.Contains(answers, a => a.Label == "ข้อความบนเค้ก" && a.Value == "HBD");
        Assert.Contains("ข้อความบนเค้ก: HBD", OrderFormFields.DocumentNotes("WEB-1", null, ok.Values.CustomFieldsJson));
        Assert.Equal("Online order #WEB-1", OrderFormFields.DocumentNotes("WEB-1", null, null));   // เดิมทุกตัวอักษร
    }

    [Fact]
    public void Tax_invoice_buyer_validated_server_side()
    {
        Assert.True(ThaiTaxId.IsValid(ValidTaxId));
        Assert.Empty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: "00000"), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: "0105536000315", branch: "00000"), FulfillmentMethod.Delivery).Errors); // checksum ผิด
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: "12345", branch: "00000"), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: null), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: "8A"), FulfillmentMethod.Delivery).Errors);
        Assert.Empty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: "00001"), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: "0001"), FulfillmentMethod.Delivery).Errors);
        Assert.NotEmpty(OrderFormFields.Check(null, Input(tax: true, taxId: ValidTaxId, branch: "๐๐๐๐๐"), FulfillmentMethod.Delivery).Errors);
        // ไม่ขอใบกำกับ ⇒ ข้อมูลผู้ซื้อไม่ถูกตรวจ/ไม่เก็บ (ทิศตรงข้าม)
        var none = OrderFormFields.Check(null, Input(tax: false, taxId: "garbage"), FulfillmentMethod.Delivery);
        Assert.Empty(none.Errors);
        Assert.Null(none.Values.BillingTaxId);
    }

    // ═════════ วันรับที่ลูกค้าเลือก (คำตัดสินข้อ 168 จ) · วันส่งถึง (B11) ═════════

    private static readonly ShopCalendar Cal = new(new[] { DayOfWeek.Sunday }, new[] { new DateOnly(2026, 11, 3) }, null);

    [Fact]
    public void Requested_date_rules()
    {
        var earliest = new DateOnly(2026, 10, 31);   // เสาร์
        Assert.NotNull(MtoLeadTime.RequestedDateProblem(new DateOnly(2026, 10, 30), earliest, Cal));   // ก่อนวันพร้อม
        Assert.NotNull(MtoLeadTime.RequestedDateProblem(new DateOnly(2026, 11, 1), earliest, Cal));    // อาทิตย์ร้านปิด
        Assert.NotNull(MtoLeadTime.RequestedDateProblem(new DateOnly(2026, 11, 3), earliest, Cal));    // วันหยุด
        Assert.NotNull(MtoLeadTime.RequestedDateProblem(earliest.AddDays(400), earliest, Cal));
        Assert.Null(MtoLeadTime.RequestedDateProblem(earliest, earliest, Cal));
        Assert.Null(MtoLeadTime.RequestedDateProblem(new DateOnly(2026, 11, 2), earliest, Cal));
    }

    [Fact]
    public void Estimated_delivery_date()
    {
        Assert.Equal(new DateOnly(2026, 11, 3), MtoLeadTime.EstimatedDeliveryDate(new DateOnly(2026, 10, 31), 3));
        Assert.Null(MtoLeadTime.EstimatedDeliveryDate(new DateOnly(2026, 10, 31), null));   // ไม่รู้ = ไม่แต่ง
    }

    // ═════════ ค่าส่ง: คูปองส่งฟรี (B7) · ส่งฟรีระดับร้านกับโซน + น้ำหนักตัวเลือก (B8) ═════════

    private static CouponShippingFacts Coupon(bool free = true, CouponDiscountType type = CouponDiscountType.Percentage,
        bool active = true, DateTime? expires = null, int? maxUses = null, int used = 0, decimal? min = null)
        => new(active, false, free, type, null, expires, maxUses, used, min);

    [Fact]
    public void Coupon_free_shipping_both_directions()
    {
        var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(), now, 100m));
        Assert.True(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(free: false, type: CouponDiscountType.FreeShipping), now, 100m));
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(free: false), now, 100m));   // คูปองลดราคาธรรมดา ⇒ ค่าส่งเดิม
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(null, now, 100m));
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(active: false), now, 100m));
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(expires: now.AddDays(-1)), now, 100m));
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(maxUses: 5, used: 5), now, 100m));
        Assert.False(ShippingFeeRules.CouponGrantsFreeShipping(Coupon(min: 500m), now, 100m));
    }

    [Fact]
    public void Shop_free_threshold_over_zones_only_when_enabled()
    {
        var zoneQuote = new ShippingQuote(true, 60m, false);
        Assert.Equal(zoneQuote, ShippingFeeRules.ApplyShopFreeThreshold(zoneQuote, 500m, false, 900m));   // เดิม
        Assert.Equal(0m, ShippingFeeRules.ApplyShopFreeThreshold(zoneQuote, 500m, true, 900m).Fee);
        Assert.Equal(60m, ShippingFeeRules.ApplyShopFreeThreshold(zoneQuote, 500m, true, 400m).Fee);
        var na = new ShippingQuote(false, 0m, false);
        Assert.False(ShippingFeeRules.ApplyShopFreeThreshold(na, 500m, true, 900m).Applicable);
    }

    [Fact]
    public void Variant_weight_used_when_options_match()
    {
        var variants = new (string?, decimal?, bool)[]
        {
            ("{\"size\":\"S\"}", 0.2m, true),
            ("{\"size\":\"XL\"}", 0.8m, true),
            ("{\"size\":\"M\"}", 0.5m, false),
        };
        Assert.Equal(0.8m, ShippingFeeRules.VariantWeightOf("{\"Size\":\"xl\"}", variants));
        Assert.Null(ShippingFeeRules.VariantWeightOf("{\"size\":\"M\"}", variants));   // variant ปิด ⇒ ไม่ใช้
        Assert.Null(ShippingFeeRules.VariantWeightOf(null, variants));
        Assert.Null(ShippingFeeRules.VariantWeightOf("not json", variants));
        Assert.Equal(1.6m, ShippingFeeRules.LineWeightKg(2m, 0.3m, 0.8m));
        Assert.Equal(0.6m, ShippingFeeRules.LineWeightKg(2m, 0.3m, null));             // ไม่มี variant ⇒ น้ำหนักสินค้า (เดิม)
        Assert.Equal(0m, ShippingFeeRules.LineWeightKg(2m, null, null));
    }

    // ═════════ ยกเลิกหลังจัดส่ง (คำตัดสินข้อ 168 ฉ · B10) ═════════

    [Fact]
    public void Credit_note_excludes_shipping_only_after_shipped()
    {
        Assert.False(SiteOrderCancelPlan.ExcludesShippingLine(null));                       // ก่อนจัดส่ง = คืนเต็มเดิม
        Assert.True(SiteOrderCancelPlan.ExcludesShippingLine(DateTime.UtcNow));
        Assert.True(ShippingVatAllocation.IsShippingLineDescription("ค่าจัดส่ง"));
        Assert.True(ShippingVatAllocation.IsShippingLineDescription("ค่าจัดส่ง (VAT 7%)"));
        Assert.False(ShippingVatAllocation.IsShippingLineDescription("เค้กช็อกโกแลต (ค่าจัดส่งรวม)"));
        // คู่สมมาตรกับตัวสร้างบรรทัด
        var part = ShippingVatAllocation.Split(ShippingVatAllocation.MaxRate, true, 50m, new List<(decimal, decimal)> { (100m, 7m) })[0];
        Assert.True(ShippingVatAllocation.IsShippingLineDescription(ShippingVatAllocation.LineDescription(part, 1)));
    }
}
