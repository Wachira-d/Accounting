using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// วิธีออกเอกสารภาษีของออเดอร์ร้านออนไลน์ที่ลูกค้าจ่ายเป็นงวด (มัดจำ → ส่วนที่เหลือ) — ค่าที่เก็บใน
/// <c>SiteCommerceConfig.MtoDepositInvoicing</c> (ค่าตั้งร้าน) และตรึงลง <c>SiteOrder.DepositInvoicingMode</c> ตอนสั่ง
/// (ร้านเปลี่ยนค่าตั้งภายหลัง ⇒ ออเดอร์ที่ค้างอยู่ไม่เปลี่ยนวิธีกลางทาง)
///
/// <para>═══ ที่มา (ทีม Q · คำสั่งเจ้าของ 2026-10-23 · คำตัดสินข้อ 156) ═══ <c>ConfirmPaymentAsync</c> ออกใบกำกับเต็มใบ + e-Tax + ตัดสต็อก
/// ตั้งแต่มัดจำงวดแรก (POS_MTO_DESIGN §3.6 ข้อ 7) · เจ้าของ: "ต้องเปิดให้ตั้งค่าได้ว่าจะเป็นแบบไหนก็ได้" ⇒ สองโหมด ค่าเริ่มต้น = เดิม</para>
/// </summary>
public static class DepositInvoicingMode
{
    /// <summary>เดิม — ยืนยันรับเงินงวดแรก ⇒ ใบขายเต็มใบ + ตัดสต็อก · งวดถัดไป = รับชำระบางส่วนของใบเดิม</summary>
    public const string FullAtFirstPayment = "full_at_first_payment";
    /// <summary>มัดจำ ⇒ ใบรับเงินมัดจำ/ใบกำกับภาษีเฉพาะยอดมัดจำ · ส่วนที่เหลือ + ตัดสต็อก ⇒ ตอนจัดส่ง/ส่งมอบ หรือชำระครบ</summary>
    public const string DepositTaxInvoice = "deposit_tax_invoice";

    /// <param name="TaxEffect">คำอธิบายผลภาษี/บัญชีเป็นภาษาไทย — หน้าตั้งค่าแสดงตรง ๆ (Server computes · page displays)</param>
    public sealed record Option(string Value, string Label, string TaxEffect);

    public static readonly IReadOnlyList<Option> All = new[]
    {
        new Option(FullAtFirstPayment, "ออกใบกำกับเต็มใบตั้งแต่รับเงินงวดแรก (แบบเดิม)",
            "ยืนยันรับเงินงวดแรก (รวมมัดจำ) ⇒ ออกใบกำกับภาษี/ใบแจ้งหนี้เต็มยอดออเดอร์ทันที — ภาษีขายเต็มจำนวนเข้า ภ.พ.30 เดือนที่รับมัดจำ "
            + "และตัดสต็อกทันที · เงินงวดถัดไปบันทึกเป็นรับชำระของใบเดิม · ง่ายที่สุด แต่รับรู้รายได้/ภาษีขายของส่วนที่ยังไม่ได้รับเงิน"
            + "และยังไม่ส่งมอบก่อนเวลา (เสียภาษีเร็วกว่ากำหนด — ไม่ใช่ภาษีขาด) และสต็อกออกก่อนผลิตเสร็จ"),
        new Option(DepositTaxInvoice, "มัดจำออกใบกำกับเฉพาะยอดมัดจำ · ส่วนที่เหลือออกตอนส่งมอบ/ชำระครบ",
            "มัดจำเป็นส่วนหนึ่งของราคาสินค้า ⇒ ป.รัษฎากร มาตรา 78(1)(ข): ได้รับชำระราคาก่อนส่งมอบ = ความรับผิดเกิดเฉพาะยอดที่รับ "
            + "⇒ ระบบออก \"ใบรับเงินมัดจำ/ใบกำกับภาษี\" เฉพาะยอดมัดจำ (มาตรา 86/4) ภาษีขายเข้า ภ.พ.30 เดือนที่รับเงิน (บริการ มาตรา 78/1 ผลเดียวกัน) "
            + "· ส่วนที่เหลือ ⇒ ออกใบกำกับภาษีเมื่อกดสถานะ \"จัดส่งแล้ว\"/\"ส่งมอบแล้ว\" หรือเมื่อลูกค้าชำระครบ แล้วแต่อย่างใดถึงก่อน "
            + "โดยหักฐานมัดจำที่ออกใบกำกับแล้วออกจากฐานภาษี (ภาษีสองใบรวม = ภาษีของยอดเต็ม · ไม่ออกซ้ำ) และตัดสต็อกครั้งเดียวตอนนั้น "
            + "· ยกเลิก/ลูกค้าไม่มารับหลังรับมัดจำ ⇒ มัดจำค้างเป็นหนี้สิน: คืนเงิน = \"คืนมัดจำ\" ที่ใบมัดจำ (ใบลดหนี้เมื่อมีหลักฐานคืนจริง) "
            + "· ริบ = \"รับรู้/ริบมัดจำ\" · บิลที่มีสินค้าหลายอัตรา VAT ใช้แบบเดิม · บริษัทที่ไม่จด VAT = ใบรับเงินมัดจำไม่มี VAT แล้วนำไปตัดชำระใบสุดท้าย"),
    };

    public static string Label(string? m) => All.FirstOrDefault(a => a.Value == m)?.Label ?? All[0].Label;

    /// <summary>ค่าที่ส่งมาจากหน้าตั้งค่า · ว่าง = แบบเดิม · ไม่รู้จัก = โยน (ห้ามเก็บค่าที่ไม่มีผู้อ่าน)</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return FullAtFirstPayment;
        foreach (var o in All) if (o.Value == s) return o.Value;
        throw new BusinessRuleException(
            $"วิธีออกเอกสารของมัดจำ \"{raw}\" ไม่รู้จัก — เลือกได้: {string.Join(" · ", All.Select(a => a.Label))}",
            SiteOrderDepositInvoicing.ConfigRuleCode);
    }

    /// <summary>ค่าที่ตรึงบนออเดอร์ · null/ว่าง/ไม่รู้จัก = แบบเดิม (ออเดอร์ก่อนมีช่องนี้ — ห้ามเปลี่ยนวิธีย้อนหลัง)</summary>
    public static string OfOrder(string? snapshot)
        => string.Equals((snapshot ?? "").Trim(), DepositTaxInvoice, StringComparison.OrdinalIgnoreCase) ? DepositTaxInvoice : FullAtFirstPayment;
}

/// <summary>ขั้นที่ <c>ConfirmPaymentAsync</c> ต้องทำกับรายการรับเงิน 1 รายการ</summary>
public enum SiteOrderPaymentStep
{
    /// <summary>เส้นเดิมทุกตัวอักษร — ใบขายเต็มใบ (ถ้ายังไม่มี) + อนุมัติ + บันทึกรับชำระ + ตัดสต็อก</summary>
    LegacyFullInvoice = 1,
    /// <summary>ออกใบรับเงินมัดจำของยอดนี้ (ใบเสร็จ <c>IsDeposit</c> · ลงเงินเข้าเอง) — ไม่ออกใบขาย ไม่ตัดสต็อก ไม่บันทึกรับชำระซ้ำ</summary>
    DepositReceipt = 2,
    /// <summary>ออกใบขายส่วนที่เหลือ (หักมัดจำที่ออกแล้ว) + อนุมัติ + ตัดชำระด้วยมัดจำที่ไม่มี VAT + บันทึกรับชำระยอดนี้ + ตัดสต็อก</summary>
    FinalInvoiceThenPayment = 3,
    /// <summary>มีใบขายแล้ว (ส่งมอบไปก่อน) ⇒ บันทึกรับชำระของยอดนี้กับใบนั้น</summary>
    PaymentOnFinalInvoice = 4,
    /// <summary>รายการรับเงินนี้ออกใบมัดจำไปแล้ว (ยืนยันซ้ำ / webhook ซ้ำ) ⇒ ไม่ทำอะไรซ้ำ</summary>
    AlreadyDeposited = 5,
}

/// <param name="Step">ขั้นที่ต้องทำ</param>
/// <param name="Note">เหตุที่โหมดใหม่ต้องถอยไปเส้นเดิม (ประทับบนออเดอร์ให้ร้านเห็น) — null = ไม่มีอะไรต้องบอก</param>
public sealed record SiteOrderPaymentPlan(SiteOrderPaymentStep Step, string? Note)
{
    /// <summary>ขั้นนี้ตัดสต็อกไหม — ตัดเฉพาะตอนมีใบขาย (สต็อกออกพร้อมรายได้ · ครั้งเดียว — <c>DeductStockAsync</c> ข้ามบรรทัดที่ตัดแล้ว)</summary>
    public bool DeductsStock => Step is SiteOrderPaymentStep.LegacyFullInvoice or SiteOrderPaymentStep.FinalInvoiceThenPayment
        or SiteOrderPaymentStep.PaymentOnFinalInvoice;
    /// <summary>ขั้นนี้บันทึกรับชำระของยอดนี้กับใบขายไหม (ใบมัดจำลงเงินเข้าเองแล้ว ⇒ ห้ามบันทึกซ้ำ)</summary>
    public bool RecordsPaymentOnSalesDocument => Step is SiteOrderPaymentStep.LegacyFullInvoice
        or SiteOrderPaymentStep.FinalInvoiceThenPayment or SiteOrderPaymentStep.PaymentOnFinalInvoice;
}

/// <summary>
/// **ตัวตัดสินตัวเดียว** ของ "เงินงวดนี้ / การส่งมอบนี้ ต้องออกเอกสารอะไร" ของออเดอร์ร้านออนไลน์ — ทุกทางเข้า (gateway webhook ·
/// สลิปที่พนักงานยืนยัน · พนักงานกดยืนยันรับเงิน · กดจัดส่ง/ส่งมอบ · ยกเลิก) เดินผ่านตัวนี้ (ล็อกด้วย tools/required_call_site_check.py)
///
/// <para><b>ขอบเขต</b>: ตัดสินแค่ "ขั้น" — การลงบัญชีเป็นเส้นเดิมของระบบทั้งหมด (ใบเสร็จมัดจำ <c>IsDeposit</c> แบบที่พักใช้ ·
/// ใบสุดท้ายหักฐานมัดจำ <c>DepositBaseDeducted</c> + รับรู้ตอนอนุมัติ · มัดจำไม่มี VAT ⇒ <c>ApplyDepositToInvoiceAsync</c> ·
/// วางแผนด้วย <see cref="LodgingDepositSettlement.PlanCheckout"/> ตัวเดียวกับเช็คเอาต์) · ห้ามเขียน posting ใหม่</para>
/// </summary>
public static class SiteOrderDepositInvoicing
{
    public const string ConfigRuleCode = "SITE-ORDER-DEPOSIT-INVOICING";
    /// <summary>รหัสกฎที่ประทับบนใบมัดจำ/หมายเหตุ — มัดจำค่าสินค้า = ส่วนหนึ่งของราคา ⇒ ภาษีขายถึงกำหนดตอนได้รับชำระ</summary>
    public const string RuleCode = "RD-78(1)(b)";
    /// <summary>OriginModule ของใบมัดจำ/ใบสุดท้ายที่ออกจากออเดอร์ร้านออนไลน์</summary>
    public const string OriginModule = "site-order";

    /// <summary>สถานะที่นับเป็น "ส่งมอบ" (§78(1) ส่งมอบสินค้า) — จัดส่งแล้ว (ส่งให้ผู้ขนส่งไปถึงผู้ซื้อ) หรือ ส่งมอบ/ลูกค้ามารับแล้ว ·
    /// เลือกจุดที่เกิดก่อน = ทิศปลอดภัย (ภาษีไม่ช้า)</summary>
    public static bool IsHandoverStatus(SiteOrderStatus s) => s is SiteOrderStatus.Shipped or SiteOrderStatus.Delivered;

    /// <summary>อัตรา VAT เดียวของบิล · null = ไม่มีบรรทัด หรือหลายอัตรา (ใบมัดจำแยกอัตราไม่ได้ ⇒ ใช้เส้นเดิม)</summary>
    public static decimal? SingleVatRate(IEnumerable<decimal> lineVatRates)
    {
        var rates = lineVatRates.Distinct().ToList();
        return rates.Count == 1 ? rates[0] : null;
    }

    public const string MixedRateNote =
        "บิลนี้มีสินค้าหลายอัตรา VAT — ใบรับเงินมัดจำแยกอัตราไม่ได้ ระบบใช้แบบเดิม (ใบกำกับเต็มใบตั้งแต่รับเงินงวดแรก) กับออเดอร์นี้";

    /// <summary>ตัดสินขั้นของรายการรับเงิน 1 รายการ (เรียกหลังประทับรายการนี้เป็นรับแล้ว)</summary>
    /// <param name="orderMode">โหมดที่ตรึงบนออเดอร์ (<c>SiteOrder.DepositInvoicingMode</c>)</param>
    /// <param name="hasSalesDocument">ออเดอร์มีใบขาย (<c>ErpDocumentId</c>) แล้ว</param>
    /// <param name="handedOver">สถานะออเดอร์ตอนนี้เป็นส่งมอบแล้ว (<see cref="IsHandoverStatus"/>)</param>
    /// <param name="orderTotal">ยอดออเดอร์</param>
    /// <param name="paidIncludingThis">ยอดรับรวมทุกรายการที่รับแล้ว รวมรายการนี้</param>
    /// <param name="lineVatRates">อัตรา VAT ของบรรทัดสินค้า</param>
    /// <param name="paymentAlreadyDeposited">รายการนี้ออกใบมัดจำไปแล้ว (<c>SiteOrderPayment.DepositDocumentId</c>)</param>
    public static SiteOrderPaymentPlan OnPayment(string? orderMode, bool hasSalesDocument, bool handedOver,
        decimal orderTotal, decimal paidIncludingThis, IEnumerable<decimal> lineVatRates, bool paymentAlreadyDeposited)
    {
        if (paymentAlreadyDeposited) return new(SiteOrderPaymentStep.AlreadyDeposited, null);
        if (DepositInvoicingMode.OfOrder(orderMode) != DepositInvoicingMode.DepositTaxInvoice)
            return new(SiteOrderPaymentStep.LegacyFullInvoice, null);
        if (hasSalesDocument) return new(SiteOrderPaymentStep.PaymentOnFinalInvoice, null);
        if (SingleVatRate(lineVatRates) is null) return new(SiteOrderPaymentStep.LegacyFullInvoice, MixedRateNote);
        if (handedOver || paidIncludingThis >= orderTotal - 0.005m) return new(SiteOrderPaymentStep.FinalInvoiceThenPayment, null);
        return new(SiteOrderPaymentStep.DepositReceipt, null);
    }

    /// <summary>เปลี่ยนสถานะเป็น <paramref name="newStatus"/> แล้วต้องออกใบส่วนที่เหลือ + ตัดสต็อกไหม — เฉพาะโหมดใหม่ ·
    /// โหมดเดิม = false เสมอ (พฤติกรรมเดิม: ส่งมอบไม่แตะเอกสาร/สต็อก) · ใบขายมีแล้ว ⇒ ยังต้องเรียกเพื่อ "ตัดสต็อกที่ค้าง" (ทำซ้ำได้)</summary>
    public static bool SettlesOnHandover(string? orderMode, SiteOrderStatus newStatus)
        => DepositInvoicingMode.OfOrder(orderMode) == DepositInvoicingMode.DepositTaxInvoice && IsHandoverStatus(newStatus);

    /// <summary>อัตรา VAT ของบรรทัดใบมัดจำ — จด VAT = อัตราเดียวของบิล (7% ⇒ VAT ทันที §78(1)(ข) · 0%/ยกเว้น ตามสินค้า) · ไม่จด VAT = 0 (§90/2)</summary>
    public static decimal DepositLineVatRate(bool vatRegistered, decimal singleRate) => vatRegistered ? singleRate : 0m;

    /// <summary>คำอธิบายบรรทัดใบมัดจำ (พิมพ์บนเอกสาร — ผู้ซื้อเห็นว่าจ่ายค่าอะไร)</summary>
    public static string DepositLineDescription(string orderNumber, int installment, decimal orderTotal)
        => $"รับเงินมัดจำค่าสินค้า คำสั่งซื้อ #{orderNumber} งวดที่ {installment} (ยอดคำสั่งซื้อ {orderTotal:N2} บาท)";

    /// <summary>ข้อความถึงลูกค้าบนหน้าเช็กเอาต์/หน้าคำสั่งซื้อ (เฉพาะออเดอร์ที่จ่ายเป็นงวดในโหมดใหม่) · null = ไม่ต้องแสดง</summary>
    public static string? CustomerNote(string? mode, bool paysInInstallments)
        => paysInInstallments && DepositInvoicingMode.OfOrder(mode) == DepositInvoicingMode.DepositTaxInvoice
            ? "เงินมัดจำจะได้รับใบรับเงินมัดจำ/ใบกำกับภาษีตามยอดที่ชำระ · ใบกำกับภาษีส่วนที่เหลือออกเมื่อจัดส่ง/รับสินค้า หรือเมื่อชำระครบ"
            : null;

    /// <summary>ยกเลิก/คืนเงินออเดอร์ที่มีใบมัดจำค้าง ⇒ ข้อความประทับบนออเดอร์ (มัดจำไม่ถูกยกเลิกเอง — เงินยังอยู่กับร้านจนกว่าจะคืน/ริบจริง ·
    /// DECISION_DOCTRINE R1: ใบลดหนี้/JE คืนเงินเกิดตอนมีหลักฐานคืนเท่านั้น) · null = ไม่มีมัดจำค้าง</summary>
    public static string? CancelNotice(IReadOnlyList<(string Number, decimal Gross)> openDeposits)
    {
        var open = openDeposits.Where(d => d.Gross > 0.005m).ToList();
        if (open.Count == 0) return null;
        return $"มีใบรับเงินมัดจำค้าง {string.Join(", ", open.Select(d => $"{d.Number} ({d.Gross:N2})"))} รวม {open.Sum(d => d.Gross):N2} บาท — "
            + "ระบบไม่ยกเลิกใบมัดจำเอง (เงินยังอยู่กับร้าน) · คืนเงินลูกค้า ⇒ เปิดใบมัดจำแล้วกด \"คืนมัดจำ\" เมื่อโอนคืนจริง (ออกใบลดหนี้ให้) "
            + "· ลูกค้าไม่มารับ/ริบตามเงื่อนไข ⇒ กด \"รับรู้/ริบมัดจำ\" ที่ใบมัดจำ";
    }
}
