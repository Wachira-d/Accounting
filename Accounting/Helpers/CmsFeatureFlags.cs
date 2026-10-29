using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>สวิตช์ร้านค้าออนไลน์ที่ตัดสินแล้ว (ต่อเว็บ) — ผู้อ่านทุกทางเข้าใช้ค่าชุดนี้ · หน้าร้านได้ชุดเดียวกันเป็น "ชื่อ" (<see cref="CmsFeatureFlags.ToStorefront"/>)</summary>
/// <param name="Delivery">รับคำสั่งซื้อแบบจัดส่ง (ปิด = ร้านมารับเองอย่างเดียว · เซิร์ฟเวอร์ปฏิเสธวิธีจัดส่ง)</param>
/// <param name="Pickup">ให้ลูกค้ามารับเองที่ร้าน</param>
/// <param name="OnlinePayment">ช่องชำระออนไลน์ (gateway ระดับบริษัท) บนหน้าสั่งซื้อสำเร็จ / public-pay</param>
/// <param name="BankTransfer">โอน/PromptPay + แนบสลิป</param>
/// <param name="Cod">เก็บเงินปลายทาง/ชำระเมื่อรับสินค้า (คำตัดสินข้อ 168 ค · ค่าเริ่มต้นปิด)</param>
/// <param name="GuestCheckout">สั่งได้โดยไม่ต้องเป็นสมาชิก (ปิด = ต้องมีบัญชีลูกค้าของเว็บนี้)</param>
/// <param name="AutoConfirm">ออเดอร์ใหม่ขึ้นสถานะ "ยืนยันแล้ว" ทันที (สถานะอย่างเดียว — ไม่ใช่การรับเงิน)</param>
/// <param name="Coupons">ช่องคูปองหน้าร้าน + ส่วนลด/ส่งฟรีจากคูปอง</param>
/// <param name="TaxInvoiceRequest">ลูกค้าขอใบกำกับภาษีเต็มรูปที่หน้าร้านได้</param>
/// <param name="Reviews">รับ/แสดงรีวิวสินค้า</param>
/// <param name="ReviewAutoApprove">รีวิวขึ้นทันทีไม่ต้องรออนุมัติ</param>
/// <param name="ReviewRequirePurchase">รับรีวิวเฉพาะผู้ที่ซื้อและได้รับสินค้าแล้ว</param>
/// <param name="Wishlist">รายการโปรดของลูกค้า</param>
/// <param name="QuoteConversion">ลูกค้ากด "ขอใบเสนอราคา" แทนการชำระบนหน้าสั่งซื้อสำเร็จได้</param>
/// <param name="FreeShippingOverridesZones">"ส่งฟรีเมื่อครบ" ระดับร้านใช้กับอัตราในโซนด้วย (ปิด = ใช้เฉพาะร้านที่ไม่ได้ตั้งโซน — เดิม)</param>
/// <param name="OrderNumberPrefix">คำนำหน้าเลขคำสั่งซื้อ (รูปมาตรฐานแล้ว)</param>
public sealed record CmsFeatureSet(
    bool Delivery, bool Pickup,
    bool OnlinePayment, bool BankTransfer, bool Cod,
    bool GuestCheckout, bool AutoConfirm,
    bool Coupons, bool TaxInvoiceRequest,
    bool Reviews, bool ReviewAutoApprove, bool ReviewRequirePurchase,
    bool Wishlist, bool QuoteConversion, bool FreeShippingOverridesZones,
    string OrderNumberPrefix);

/// <summary>
/// <b>ตัวตัดสินกลางตัวเดียว</b>ของสวิตช์ร้านค้าออนไลน์ต่อเว็บ (รอบ 209 · คำตัดสินข้อ 168 ก · R-209-D1)
///
/// <para>═══ ที่มา ═══ <c>SiteCommerceConfig</c> มีสวิตช์ ~38 ตัวที่ "เก็บ + echo กลับครบ แต่ไม่มีใครอ่าน" (ทีม D รอบ 209 §1.1) —
/// ร้านปิดจัดส่งแล้วลูกค้ายังเลือกจัดส่งได้ · ปิดคูปองแล้ว API ยังรับ · COD มีธงแต่ไม่มีวิธีชำระ · เลขออเดอร์ฮาร์ดโค้ด "WEB-"</para>
///
/// <para><b>กติกา</b>: อ่านคอลัมน์เดิมของ <c>SiteCommerceConfig</c> (+ คอลัมน์ใหม่เท่าที่จำเป็น) · <b>ค่าเริ่มต้นทุกตัว = พฤติกรรมเดิม</b>
/// (เว็บที่ไม่มีแถวค่าตั้ง = <see cref="Resolve"/>(null) ได้ชุดเดียวกับแถวที่สร้างใหม่) · ทุกทางเข้าฝั่งเซิร์ฟเวอร์เรียกตัวนี้
/// (สร้างออเดอร์ · คิดยอดเช็กเอาต์ · ใช้คูปอง · รีวิว · wishlist · แนบสลิป · public-pay · ขอใบเสนอราคา) · หน้าร้านได้ผลเป็นชื่อ
/// ผ่าน checkout-quote / payment-options — <b>JS ไม่มีสำเนากติกา</b> (CLAUDE.md F2 ข้อ 5)</para>
///
/// <para><b>ไม่เปิดเป็นสวิตช์โดยเจตนา</b> (ลบออกจาก entity/DTO รอบ 209 — คอลัมน์เดิมไม่เคยมีผู้อ่าน):
/// <list type="bullet">
/// <item><c>AutoSyncToErp</c> — บัญชีต้องลงเสมอเมื่อรับเงิน (ใบขาย/JE/ภาษีขาย §86/4) · ปิดได้ = รายได้หายจากงบเงียบ ๆ</item>
/// <item><c>AutoDeductStock</c> / <c>EnableStockTracking</c> / <c>DefaultStockBehavior</c> — สต็อกออกพร้อมการขายเสมอผ่าน <c>IStockLedger</c> ตัวเดียว ·
///   ของที่ไม่ตัดสต็อกตั้งรายสินค้า (<c>SiteProduct.StockBehavior</c>)</item>
/// <item><c>DefaultVatRate</c> / <c>PricesIncludeVat</c> / <c>ErpDocumentType</c> — ความจริงอยู่ที่ <c>Product.VatRate</c> + <c>CompanyVatStatus</c>
///   (สองความจริงอันตราย) · <c>CheckoutMode</c> / <c>AutoGenerateQuotation</c> / <c>EnableTierPricing</c> / <c>EnableShippingCalculation</c> /
///   ค่าตั้งการแสดงสินค้า — ไม่มีเครื่องรองรับ (ปุ่มขอใบเสนอราคาของลูกค้า = <see cref="CmsFeatureSet.QuoteConversion"/>)</item>
/// </list></para>
/// </summary>
public static class CmsFeatureFlags
{
    public const string RuleCode = "CMS-FEATURE-FLAGS";
    public const string DefaultOrderPrefix = "WEB";
    public const int MaxOrderPrefixLength = 10;

    /// <summary>เว็บที่ไม่มีแถวค่าตั้ง — ค่าเดียวกับแถวที่สร้างใหม่ (พฤติกรรมเดิมทุกตัว)</summary>
    public static readonly CmsFeatureSet Defaults = Resolve(null);

    public static CmsFeatureSet Resolve(SiteCommerceConfig? c)
    {
        c ??= new SiteCommerceConfig();
        return new CmsFeatureSet(
            Delivery: c.EnableShipping,
            Pickup: c.EnablePickup,
            OnlinePayment: c.EnableOnlinePayment,
            BankTransfer: c.EnableBankTransfer,
            Cod: c.EnableCod,
            // สองคอลัมน์เดิมความหมายเดียวกัน (ตั้งจากหน้าตั้งค่าคู่กันเสมอ) — ปิดอันใดอันหนึ่ง = ต้องเป็นสมาชิก
            GuestCheckout: c.EnableGuestCheckout && !c.RequireCustomerAccount,
            AutoConfirm: c.AutoConfirmOrders,
            Coupons: c.EnableCoupons,
            TaxInvoiceRequest: c.EnableTaxInvoice,
            Reviews: c.EnableReviews,
            ReviewAutoApprove: c.ReviewAutoApprove,
            ReviewRequirePurchase: c.ReviewRequirePurchase,
            Wishlist: c.EnableWishlist,
            QuoteConversion: c.EnableQuoteConversion,
            FreeShippingOverridesZones: c.FreeShippingOverridesZones,
            OrderNumberPrefix: OrderPrefixOf(c.OrderNumberPrefix));
    }

    // ───────────────────────── ค่าตั้ง (ตรวจก่อนบันทึก) ─────────────────────────

    /// <summary>ตรวจชุดค่าตั้งก่อนบันทึก · null = ผ่าน · ข้อความไทยถึงเจ้าของร้าน</summary>
    public static string? ValidateConfig(SiteCommerceConfig c)
    {
        if (!c.EnableShipping && !c.EnablePickup)
            return "ต้องเปิดอย่างน้อย 1 วิธีรับสินค้า (จัดส่ง หรือ มารับเองที่ร้าน) — ไม่งั้นลูกค้าสั่งซื้อไม่ได้เลย";
        if (!c.EnableOnlinePayment && !c.EnableBankTransfer && !c.EnableCod)
            return "ต้องเปิดอย่างน้อย 1 วิธีชำระเงิน (ออนไลน์ · โอน/แนบสลิป · เก็บเงินปลายทาง)";
        return null;
    }

    /// <summary>ทำให้คำนำหน้าเลขออเดอร์เป็นรูปมาตรฐาน (A–Z 0–9 · ≤ 10 ตัว · ตัวพิมพ์ใหญ่) · ว่าง = "WEB" · ผิดรูป = โยน (ไม่เก็บค่าที่ใช้ไม่ได้)</summary>
    public static string NormalizeOrderPrefix(string? raw)
    {
        var s = (raw ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0) return DefaultOrderPrefix;
        if (s.Length > MaxOrderPrefixLength || !s.All(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            throw new BusinessRuleException(
                $"คำนำหน้าเลขคำสั่งซื้อใช้ได้เฉพาะตัวอักษรอังกฤษ A–Z และตัวเลข ไม่เกิน {MaxOrderPrefixLength} ตัว (เช่น WEB · SHOP1)", RuleCode);
        return s;
    }

    /// <summary>ค่าที่เก็บไว้ ⇒ คำนำหน้าที่ใช้จริง — ค่าเก่าที่ผิดรูป (ก่อนมีด่าน) ⇒ "WEB" (ไม่โยนตอนสั่งซื้อ · ลูกค้าไม่ควรสั่งไม่ได้เพราะค่าตั้ง)</summary>
    public static string OrderPrefixOf(string? stored)
    {
        try { return NormalizeOrderPrefix(stored); }
        catch (BusinessRuleException) { return DefaultOrderPrefix; }
    }

    /// <summary>ส่วนต้นของเลขออเดอร์ "{prefix}-{yyMM}-" — ตัวประกอบเดียว (ตัวรันเลขต่อ prefix ต่อเว็บ)</summary>
    public static string OrderNumberStem(CmsFeatureSet f, DateTime utcNow) => $"{f.OrderNumberPrefix}-{utcNow:yyMM}-";

    // ───────────────────────── วิธีรับ ─────────────────────────

    /// <summary>วิธีรับที่ลูกค้าขอ ⇒ วิธีที่ใช้จริง · ว่าง = จัดส่ง (เดิม) แต่ร้านปิดจัดส่ง ⇒ มารับเอง · ขอวิธีที่ร้านปิด = โยน (ไม่สลับเงียบ)</summary>
    public static string ResolveFulfillment(CmsFeatureSet f, string? requested)
    {
        var blank = string.IsNullOrWhiteSpace(requested);
        var m = FulfillmentMethod.Normalize(requested);
        if (m == FulfillmentMethod.Delivery && !f.Delivery)
        {
            if (blank && f.Pickup) return FulfillmentMethod.Pickup;
            throw new BusinessRuleException(f.Pickup
                ? "ร้านนี้ไม่รับจัดส่ง — เลือก \"มารับเองที่ร้าน\""
                : "ร้านนี้ยังไม่เปิดรับคำสั่งซื้อ (ปิดทั้งจัดส่งและมารับเอง) — ติดต่อร้านโดยตรง", "SHIP-METHOD");
        }
        if (m == FulfillmentMethod.Pickup && !f.Pickup)
            throw new BusinessRuleException("ร้านนี้ยังไม่เปิดให้มารับสินค้าเอง — เลือกจัดส่ง", "SHIP-METHOD");
        return m;
    }

    // ───────────────────────── วิธีชำระ (เลือกตอนสั่ง) ─────────────────────────

    public const string PayNow = "prepay";
    public const string PayCod = "cod";

    /// <summary>ป้ายวิธีชำระที่ลูกค้าเลือก — เซิร์ฟเวอร์เป็นเจ้าของข้อความ</summary>
    public static string PaymentChoiceLabel(string? choice, string? fulfillment)
        => choice == PayCod
            ? (fulfillment == FulfillmentMethod.Pickup ? "ชำระเงินเมื่อมารับสินค้า" : "เก็บเงินปลายทาง (ชำระเมื่อรับสินค้า)")
            : "ชำระก่อน (โอน/ชำระออนไลน์)";

    /// <summary>วิธีชำระที่ลูกค้าเลือกตอนสั่ง · ว่าง = ชำระก่อน (เดิม) · COD ที่ร้านไม่เปิด / ค่าไม่รู้จัก = โยน</summary>
    public static string ResolvePaymentChoice(CmsFeatureSet f, string? requested)
    {
        var s = (requested ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s == PayNow)
        {
            if (!f.OnlinePayment && !f.BankTransfer && f.Cod) return PayCod;   // ร้านรับปลายทางอย่างเดียว
            return PayNow;
        }
        if (s == PayCod)
        {
            if (!f.Cod) throw new BusinessRuleException("ร้านนี้ไม่รับเก็บเงินปลายทาง — เลือกชำระก่อน", RuleCode);
            return PayCod;
        }
        throw new BusinessRuleException($"วิธีชำระ \"{requested}\" ไม่รู้จัก", RuleCode);
    }

    /// <summary>COD กับเงื่อนไขชำระของออเดอร์ — สินค้าตามสั่งที่ต้องมัดจำก่อนผลิต ใช้ COD ไม่ได้ (ร้านต้องได้มัดจำก่อนทำ) · null = ได้</summary>
    public static string? CodProblem(string paymentTermMode)
        => paymentTermMode is PaymentTermMode.DepositPercent or PaymentTermMode.DepositFixed
            ? "คำสั่งซื้อนี้มีสินค้าตามสั่งที่ร้านต้องได้มัดจำก่อนผลิต — ใช้เก็บเงินปลายทางไม่ได้ (เลือกชำระก่อน)"
            : null;

    /// <summary>เงื่อนไขชำระที่ตรึงบนออเดอร์ COD — "ชำระเมื่อรับสินค้า" (มัดจำ 0 · ยังไม่ชำระจนพนักงานบันทึกรับเงิน · ใบกำกับตามเส้นเดิมตอนรับเงิน) ·
    /// ไม่ใช่ COD = ตามที่ตัวคิดเช็กเอาต์ตัดสิน</summary>
    public static string PaymentTermFor(string paymentChoice, string computedMode)
        => paymentChoice == PayCod ? PaymentTermMode.PayOnPickup : computedMode;

    // ───────────────────────── สมาชิก ─────────────────────────

    /// <summary>ร้านบังคับสมัครสมาชิก ⇒ ต้องมีลูกค้าของเว็บนี้ที่ยังใช้งานได้ · null = สั่งได้</summary>
    public static string? AccountProblem(CmsFeatureSet f, bool hasVerifiedCustomer)
        => f.GuestCheckout || hasVerifiedCustomer ? null
            : "ร้านนี้ให้สั่งซื้อเฉพาะสมาชิก — เข้าสู่ระบบหรือสมัครสมาชิกก่อนสั่งซื้อ";

    // ───────────────────────── คูปอง / ใบกำกับ / รีวิว / wishlist / ใบเสนอราคา ─────────────────────────

    public const string CouponsOffMessage = "ร้านนี้ไม่ได้เปิดใช้คูปองส่วนลด";
    public const string TaxInvoiceOffMessage = "ร้านนี้ไม่รับคำขอใบกำกับภาษีเต็มรูปผ่านหน้าเว็บ — ติดต่อร้านโดยตรง";
    public const string ReviewsOffMessage = "ร้านนี้ปิดการรีวิวสินค้า";
    public const string ReviewNeedsPurchaseMessage = "รีวิวได้เฉพาะผู้ที่ซื้อและได้รับสินค้านี้แล้ว (ระบุเลขคำสั่งซื้อที่ส่งมอบแล้ว)";
    public const string WishlistOffMessage = "ร้านนี้ไม่ได้เปิดใช้รายการโปรด";
    public const string OnlinePaymentOffMessage = "ร้านนี้ไม่เปิดรับชำระออนไลน์ — โอนแล้วแนบสลิป หรือติดต่อร้าน";
    public const string QuoteConversionOffMessage = "ร้านนี้ไม่รับเปลี่ยนคำสั่งซื้อเป็นใบเสนอราคา — ชำระเงินหรือติดต่อร้าน";

    /// <summary>ส่วนลดจากคูปองที่ใช้ได้จริง — ร้านปิดคูปอง ⇒ 0 (ส่วนลดค้างในตะกร้าก่อนปิดไม่ถูกนำมาใช้ · หน้าเช็กเอาต์แสดงยอดเดียวกัน)</summary>
    public static decimal EffectiveDiscount(CmsFeatureSet f, decimal cartDiscount) => f.Coupons ? Math.Max(0m, cartDiscount) : 0m;

    /// <summary>รีวิวใหม่: รับไหม + อนุมัติทันทีไหม · Problem != null = ปฏิเสธพร้อมเหตุ</summary>
    public static (string? Problem, bool Approved) ReviewDecision(CmsFeatureSet f, bool verifiedPurchase)
    {
        if (!f.Reviews) return (ReviewsOffMessage, false);
        if (f.ReviewRequirePurchase && !verifiedPurchase) return (ReviewNeedsPurchaseMessage, false);
        return (null, f.ReviewAutoApprove);
    }

    /// <summary>ข้อความท้ายใบเสนอราคาที่ลูกค้าขอจากหน้าเว็บ — เว็บภาษาอังกฤษ ("en") ใช้ฉบับอังกฤษเมื่อร้านตั้งไว้ · ไม่ตั้ง/เว็บไทย = ฉบับไทย (เดิม)</summary>
    public static string? QuotationFooter(string? th, string? en, string? siteLanguage)
        => string.Equals((siteLanguage ?? "").Trim(), "en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(en) ? en : th;

    // ───────────────────────── หน้าร้าน (ชื่อ ไม่ใช่กติกา) ─────────────────────────

    /// <summary>ชุดสวิตช์ที่หน้าร้านใช้วาด (ชื่อ → ค่า) — JS อ่านอย่างเดียว ไม่คิดซ้ำ</summary>
    public static Dictionary<string, bool> ToStorefront(CmsFeatureSet f) => new()
    {
        ["delivery"] = f.Delivery,
        ["pickup"] = f.Pickup,
        ["onlinePayment"] = f.OnlinePayment,
        ["bankTransfer"] = f.BankTransfer,
        ["cod"] = f.Cod,
        ["guestCheckout"] = f.GuestCheckout,
        ["coupons"] = f.Coupons,
        ["taxInvoiceRequest"] = f.TaxInvoiceRequest,
        ["reviews"] = f.Reviews,
        ["wishlist"] = f.Wishlist,
        ["quoteConversion"] = f.QuoteConversion,
    };
}
