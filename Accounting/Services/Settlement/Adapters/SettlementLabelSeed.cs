using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>
/// **ความรู้ตั้งต้น (cold-start seed) ของป้ายประเภทรายการ + หัวคอลัมน์ใน settlement report** — กฎเหล็ก #1 ข้อ 3
/// (tenant ใหม่ต้องจัดประเภทได้ตั้งแต่วันแรกโดยไม่ต้องรอ AI สอน · ปิด AI แล้วป้ายที่รู้จักยังจัดได้ครบ)
///
/// <para>ป้ายเฉพาะแพลตฟอร์ม (Shopee · Lazada · TikTok Shop · Omise · 2C2P · GB Prime Pay · Stripe · Agoda · Booking.com) อยู่ที่นี่
/// <b>ที่เดียว</b> (<c>tools/settlement_adapter_boundary_check.py</c>) · คีย์ = <see cref="SettlementLineClassification.NormalizeLabel"/> ·
/// ป้ายที่ความหมายขึ้นกับชนิดช่องทาง ("fee" ของ gateway = ค่าธรรมเนียมรับชำระ) อยู่ในตารางต่อชนิด</para>
/// <para>⚠️ <b>ห้าม seed</b> <c>Adjustment</c> (ต้องมีเหตุผล+ผังจากคน) และห้ามป้ายกำกวม ("adjustment" · "other" · "misc") —
/// ไม่รู้ = ปล่อย Unclassified ให้คน/ครูตัดสิน (DOCTRINE §1: ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ)</para>
/// <para>ที่มาของป้าย: ความรู้ทั่วไปของรูปแบบรายงาน (report-S2 หมายเหตุหัวไฟล์ — ยังไม่ยืนยันกับไฟล์จริง) · ป้ายผิด = แค่ไม่ถูกจัด
/// (ตกไป AI/คน) ไม่ใช่จัดผิด เพราะทุกป้ายผ่าน <c>SignAllowed</c> ก่อนใช้</para>
/// </summary>
public static class SettlementLabelSeed
{
    private static readonly Dictionary<string, SettlementLineType> Generic = Build(new (string, SettlementLineType)[]
    {
        // ── ยอดขาย ──
        ("sale", SettlementLineType.Sale), ("sales", SettlementLineType.Sale), ("order", SettlementLineType.Sale),
        ("order income", SettlementLineType.Sale), ("order amount", SettlementLineType.Sale), ("product price", SettlementLineType.Sale),
        ("original price", SettlementLineType.Sale), ("item price", SettlementLineType.Sale), ("merchandise subtotal", SettlementLineType.Sale),
        ("product subtotal", SettlementLineType.Sale), ("item subtotal", SettlementLineType.Sale), ("gross amount", SettlementLineType.Sale),
        ("transaction amount", SettlementLineType.Sale), ("payment received", SettlementLineType.Sale), ("charge", SettlementLineType.Sale),
        ("ยอดขาย", SettlementLineType.Sale), ("รายได้จากการขาย", SettlementLineType.Sale), ("ราคาสินค้า", SettlementLineType.Sale),
        ("ยอดสั่งซื้อ", SettlementLineType.Sale), ("ยอดรวมสินค้า", SettlementLineType.Sale), ("ชำระเงิน", SettlementLineType.Sale),
        // ── คืนเงิน ──
        ("refund", SettlementLineType.Refund), ("refunds", SettlementLineType.Refund), ("refund amount", SettlementLineType.Refund),
        ("return refund", SettlementLineType.Refund), ("return & refund", SettlementLineType.Refund), ("reversal of payment", SettlementLineType.Refund),
        ("คืนเงิน", SettlementLineType.Refund), ("คืนเงินผู้ซื้อ", SettlementLineType.Refund), ("คืนสินค้า", SettlementLineType.Refund),
        ("คืนสินค้าและคืนเงิน", SettlementLineType.Refund),
        // ── chargeback ──
        ("chargeback", SettlementLineType.Chargeback), ("dispute", SettlementLineType.Chargeback), ("chargeback debit", SettlementLineType.Chargeback),
        ("dispute opened", SettlementLineType.Chargeback), ("ถูกปฏิเสธรายการ", SettlementLineType.Chargeback), ("ข้อพิพาท", SettlementLineType.Chargeback),
        ("chargeback reversal", SettlementLineType.ChargebackReversal), ("dispute won", SettlementLineType.ChargebackReversal),
        ("chargeback won", SettlementLineType.ChargebackReversal), ("dispute reversal", SettlementLineType.ChargebackReversal),
        // ── ค่าคอมมิชชัน ──
        ("commission", SettlementLineType.Commission), ("commission fee", SettlementLineType.Commission),
        ("marketplace commission", SettlementLineType.Commission), ("platform commission", SettlementLineType.Commission),
        ("marketplace fee", SettlementLineType.Commission), ("ค่าคอมมิชชั่น", SettlementLineType.Commission),
        ("ค่าคอมมิชชัน", SettlementLineType.Commission), ("ค่าธรรมเนียมการขาย", SettlementLineType.Commission),
        // ── ค่าธรรมเนียมรับชำระ ──
        ("transaction fee", SettlementLineType.PaymentFee), ("payment fee", SettlementLineType.PaymentFee),
        ("payment processing fee", SettlementLineType.PaymentFee), ("processing fee", SettlementLineType.PaymentFee),
        ("payment handling fee", SettlementLineType.PaymentFee), ("mdr", SettlementLineType.PaymentFee), ("merchant discount rate", SettlementLineType.PaymentFee),
        ("ค่าธรรมเนียมการชำระเงิน", SettlementLineType.PaymentFee), ("ค่าธรรมเนียมธุรกรรม", SettlementLineType.PaymentFee),
        ("ค่าธรรมเนียมรับชำระเงิน", SettlementLineType.PaymentFee), ("ค่าธรรมเนียมบัตรเครดิต", SettlementLineType.PaymentFee),
        // ── ค่าขนส่ง ──
        ("shipping fee", SettlementLineType.ShippingFeeCharged), ("shipping fee paid by seller", SettlementLineType.ShippingFeeCharged),
        ("actual shipping fee", SettlementLineType.ShippingFeeCharged), ("delivery fee", SettlementLineType.ShippingFeeCharged),
        ("ค่าขนส่ง", SettlementLineType.ShippingFeeCharged), ("ค่าจัดส่ง", SettlementLineType.ShippingFeeCharged),
        ("ค่าส่งสินค้า", SettlementLineType.ShippingFeeCharged),
        ("shipping fee paid by buyer", SettlementLineType.ShippingSubsidy), ("shipping rebate", SettlementLineType.ShippingSubsidy),
        ("shipping fee rebate", SettlementLineType.ShippingSubsidy), ("shipping subsidy", SettlementLineType.ShippingSubsidy),
        ("ส่วนลดค่าจัดส่งจากแพลตฟอร์ม", SettlementLineType.ShippingSubsidy), ("ค่าจัดส่งที่ผู้ซื้อชำระ", SettlementLineType.ShippingSubsidy),
        // ── ส่วนลด ──
        ("seller voucher", SettlementLineType.SellerVoucher), ("shop voucher", SettlementLineType.SellerVoucher),
        ("seller discount", SettlementLineType.SellerVoucher), ("seller promotion", SettlementLineType.SellerVoucher),
        ("seller funded discount", SettlementLineType.SellerVoucher), ("โค้ดส่วนลดร้านค้า", SettlementLineType.SellerVoucher),
        ("ส่วนลดจากร้านค้า", SettlementLineType.SellerVoucher), ("ส่วนลดร้านค้า", SettlementLineType.SellerVoucher),
        ("platform voucher", SettlementLineType.PlatformVoucherSubsidy), ("platform discount", SettlementLineType.PlatformVoucherSubsidy),
        ("platform subsidy", SettlementLineType.PlatformVoucherSubsidy), ("platform funded discount", SettlementLineType.PlatformVoucherSubsidy),
        ("coins", SettlementLineType.PlatformVoucherSubsidy), ("coin cashback", SettlementLineType.PlatformVoucherSubsidy),
        ("โค้ดส่วนลดจากแพลตฟอร์ม", SettlementLineType.PlatformVoucherSubsidy), ("ส่วนลดจากแพลตฟอร์ม", SettlementLineType.PlatformVoucherSubsidy),
        // ── โฆษณา/บริการ ──
        ("ads", SettlementLineType.AdsFee), ("ads fee", SettlementLineType.AdsFee), ("advertising", SettlementLineType.AdsFee),
        ("advertising fee", SettlementLineType.AdsFee), ("sponsored solutions", SettlementLineType.AdsFee), ("ค่าโฆษณา", SettlementLineType.AdsFee),
        ("service fee", SettlementLineType.ServiceFee), ("free shipping program fee", SettlementLineType.ServiceFee),
        ("program fee", SettlementLineType.ServiceFee), ("platform service fee", SettlementLineType.ServiceFee),
        ("ค่าบริการ", SettlementLineType.ServiceFee), ("ค่าบริการแพลตฟอร์ม", SettlementLineType.ServiceFee),
        ("ค่าธรรมเนียมบริการ", SettlementLineType.ServiceFee),
        // ── ถอนเงิน ──
        ("withdrawal fee", SettlementLineType.WithdrawalFee), ("payout fee", SettlementLineType.WithdrawalFee),
        ("transfer fee", SettlementLineType.WithdrawalFee), ("ค่าธรรมเนียมถอนเงิน", SettlementLineType.WithdrawalFee),
        ("ค่าธรรมเนียมการโอน", SettlementLineType.WithdrawalFee), ("ค่าธรรมเนียมโอนเงิน", SettlementLineType.WithdrawalFee),
        // ── reserve ──
        ("reserve", SettlementLineType.ReserveHold), ("rolling reserve", SettlementLineType.ReserveHold), ("reserve hold", SettlementLineType.ReserveHold),
        ("holdback", SettlementLineType.ReserveHold), ("เงินกันสำรอง", SettlementLineType.ReserveHold),
        ("reserve release", SettlementLineType.ReserveRelease), ("released reserve", SettlementLineType.ReserveRelease),
        ("rolling reserve release", SettlementLineType.ReserveRelease), ("ปล่อยเงินสำรอง", SettlementLineType.ReserveRelease),
        // ── ภาษีถูกหัก · FX ──
        ("withholding tax", SettlementLineType.TaxWithheldByPlatform), ("wht", SettlementLineType.TaxWithheldByPlatform),
        ("e-withholding tax", SettlementLineType.TaxWithheldByPlatform), ("ภาษีหัก ณ ที่จ่าย", SettlementLineType.TaxWithheldByPlatform),
        ("ภาษีเงินได้หัก ณ ที่จ่าย", SettlementLineType.TaxWithheldByPlatform),
        ("exchange rate difference", SettlementLineType.FxDifference), ("fx difference", SettlementLineType.FxDifference),
        ("currency conversion", SettlementLineType.FxDifference), ("ผลต่างอัตราแลกเปลี่ยน", SettlementLineType.FxDifference),
        // ── ป้ายของเจ้า (เฉพาะแพลตฟอร์ม) ──
        ("shopee voucher", SettlementLineType.PlatformVoucherSubsidy), ("shopee coins", SettlementLineType.PlatformVoucherSubsidy),
        ("shopee ads", SettlementLineType.AdsFee), ("lazada voucher", SettlementLineType.PlatformVoucherSubsidy),
        ("lazada bonus", SettlementLineType.PlatformVoucherSubsidy), ("lazada sponsored solutions", SettlementLineType.AdsFee),
        ("tiktok shop commission", SettlementLineType.Commission), ("tiktok ads", SettlementLineType.AdsFee),
        ("affiliate commission", SettlementLineType.AdsFee),
        ("omise fee", SettlementLineType.PaymentFee), ("2c2p fee", SettlementLineType.PaymentFee),
        ("gbprimepay fee", SettlementLineType.PaymentFee), ("gb prime pay fee", SettlementLineType.PaymentFee),
        ("stripe fee", SettlementLineType.PaymentFee),
    });

    /// <summary>ป้ายที่ความหมายขึ้นกับชนิดช่องทาง</summary>
    private static readonly Dictionary<SettlementChannelKind, Dictionary<string, SettlementLineType>> ByKind = new()
    {
        [SettlementChannelKind.Gateway] = Build(new (string, SettlementLineType)[]
        {
            ("fee", SettlementLineType.PaymentFee), ("fees", SettlementLineType.PaymentFee), ("ค่าธรรมเนียม", SettlementLineType.PaymentFee),
            ("payment", SettlementLineType.Sale),
        }),
        [SettlementChannelKind.CardAcquirer] = Build(new (string, SettlementLineType)[]
        {
            ("fee", SettlementLineType.PaymentFee), ("ค่าธรรมเนียม", SettlementLineType.PaymentFee), ("payment", SettlementLineType.Sale),
        }),
        [SettlementChannelKind.Ota] = Build(new (string, SettlementLineType)[]
        {
            ("booking", SettlementLineType.Sale), ("reservation", SettlementLineType.Sale), ("room revenue", SettlementLineType.Sale),
            ("agoda commission", SettlementLineType.Commission), ("booking.com commission", SettlementLineType.Commission),
            ("expedia commission", SettlementLineType.Commission), ("ค่าคอมมิชชั่นห้องพัก", SettlementLineType.Commission),
        }),
    };

    private static Dictionary<string, SettlementLineType> Build(IEnumerable<(string Label, SettlementLineType Type)> rows)
    {
        var d = new Dictionary<string, SettlementLineType>(StringComparer.Ordinal);
        foreach (var (label, type) in rows)
            d[SettlementLineClassification.NormalizeLabel(label)] = type;
        return d;
    }

    /// <summary>ประเภทของป้ายนี้ตาม seed — ตารางของชนิดช่องทางก่อน แล้วตารางทั่วไป · ไม่รู้จัก ⇒ null</summary>
    public static SettlementLineType? Lookup(string? rawLabel, SettlementChannelKind kind)
    {
        var key = SettlementLineClassification.NormalizeLabel(rawLabel);
        if (key.Length == 0) return null;
        if (ByKind.TryGetValue(kind, out var k) && k.TryGetValue(key, out var kt)) return kt;
        return Generic.TryGetValue(key, out var t) ? t : null;
    }

    // ═══ หัวคอลัมน์ (ช่วยเสนอการจับคู่คอลัมน์ — ผู้ใช้ยืนยันก่อนใช้เสมอ) ═══

    /// <summary>ช่องของการจับคู่ → หัวคอลัมน์ที่พบบ่อย (ตัวพิมพ์เล็ก ยุบช่องว่างแล้ว) · เรียงจากเจาะจงไปทั่วไป</summary>
    public static readonly IReadOnlyDictionary<string, string[]> HeaderSynonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["txnId"] = new[] { "transaction id", "transaction no", "txn id", "id รายการ", "เลขที่รายการ", "รหัสรายการ", "reference id",
            "charge id", "transfer id", "statement id" },
        ["orderId"] = new[] { "order id", "order no", "order number", "order sn", "order no.", "หมายเลขคำสั่งซื้อ", "เลขที่คำสั่งซื้อ",
            "รหัสคำสั่งซื้อ", "เลขออเดอร์", "booking id", "booking number", "reservation id", "merchant order id" },
        ["date"] = new[] { "transaction date", "date", "created at", "created", "วันที่ทำรายการ", "วันที่", "วันที่ชำระเงิน",
            "payment date", "order date", "settlement date", "release date" },
        ["type"] = new[] { "transaction type", "fee name", "fee type", "type", "ประเภทรายการ", "ประเภท", "รายการ", "category",
            "description type" },
        ["description"] = new[] { "description", "details", "detail", "remark", "remarks", "รายละเอียด", "หมายเหตุ", "memo" },
        ["amount"] = new[] { "amount", "net amount", "จำนวนเงิน", "ยอดเงิน", "amount (thb)", "amount thb", "value" },
        ["amountIn"] = new[] { "credit", "money in", "เงินเข้า", "รับ", "deposit", "income" },
        ["amountOut"] = new[] { "debit", "money out", "เงินออก", "จ่าย", "withdrawal", "deduction" },
        ["vat"] = new[] { "vat", "vat amount", "ภาษีมูลค่าเพิ่ม", "tax", "fee vat" },
        ["wht"] = new[] { "wht", "withholding tax", "ภาษีหัก ณ ที่จ่าย" },
        ["payoutRef"] = new[] { "payout id", "payout reference", "withdrawal id", "settlement id", "transfer reference", "รอบการโอน" },
    };

    /// <summary>หัวคอลัมน์ที่เป็นข้อมูลส่วนบุคคลของผู้ซื้อ — เสนอให้อยู่ในรายการ "ไม่ใช้" เสมอ (PDPA)</summary>
    public static readonly IReadOnlyList<string> PersonalHeaderHints = new[]
    {
        "buyer", "customer", "recipient", "receiver", "username", "user name", "ship to", "shipping address", "address", "phone",
        "tel", "mobile", "email", "ผู้ซื้อ", "ลูกค้า", "ผู้รับ", "ชื่อ", "ที่อยู่", "เบอร์", "โทร", "อีเมล",
    };
}
