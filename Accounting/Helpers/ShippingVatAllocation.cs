namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "VAT ของค่าจัดส่งในบิลที่สินค้าหลายอัตรา" (รอบ 206 · ทะเบียน R-32 · POS_MTO_DESIGN §3.6 ข้อ 8)
/// <para><b>ที่มา</b>: เดิมค่าส่งใช้อัตราสูงสุดในบิลทั้งก้อน (<see cref="ShippingFeeRules.VatRateForShipping"/> — 7% เมื่อมีสินค้า 7% แม้แค่ชิ้นเดียว)
/// = ทิศปลอดภัย (เสียเกินแก้ได้ · เสียขาดถูกประเมิน) · นักบัญชีบางร้านต้องการแบ่งค่าส่งตามสัดส่วนมูลค่าสินค้าแต่ละอัตรา
/// (ค่าส่งเป็นส่วนของการขายสินค้านั้น ⇒ อัตราตามสินค้า) ⇒ <b>คำตัดสินข้อ 165</b>: เปิดเป็นค่าตั้งต่อร้าน <c>SiteCommerceConfig.ShippingVatAllocation</c>
/// ค่าเริ่มต้น = อัตราสูงสุด (เดิมทุกสตางค์) · ตรึงบนออเดอร์ตอนสั่ง (<c>SiteOrder.ShippingVatAllocation</c>) — ออเดอร์เก่า null = เดิม</para>
/// <para>ผู้อ่าน: ยอด VAT ของออเดอร์ตอนสร้าง (<c>CreateOrderAsync</c>) · บรรทัดเอกสาร ERP (<c>BuildOrderErpLines</c> — แบ่งเป็นหลายบรรทัดค่าจัดส่งตามอัตรา) ·
/// ใบเสนอราคาค่าส่ง (quote) — ทั้งสามต้องให้ตัวเลขชุดเดียว</para>
/// </summary>
public static class ShippingVatAllocation
{
    public const string RuleCode = "CMS-SHIPPING-VAT-ALLOCATION";
    public const string LegalReference = "RD-79 · RD-81 · DECISIONS-165";

    /// <summary>อัตราสูงสุดในบิลทั้งก้อน (ค่าเริ่มต้น · พฤติกรรมเดิม)</summary>
    public const string MaxRate = "max_rate";
    /// <summary>แบ่งค่าส่งตามสัดส่วนมูลค่าสินค้า (รวม VAT) ของแต่ละอัตรา ⇒ หลายบรรทัดค่าจัดส่ง</summary>
    public const string Proportional = "proportional";

    public sealed record Option(string Value, string Label, string Description);
    public static readonly IReadOnlyList<Option> All = new[]
    {
        new Option(MaxRate, "อัตราสูงสุดในบิล (ค่าเริ่มต้น)",
            "ค่าจัดส่งทั้งก้อนคิด VAT ตามอัตราสูงสุดของสินค้าในบิล (มีสินค้า 7% ⇒ 7%) — ทิศปลอดภัยทางภาษี เสียเกินขอคืน/แก้ได้"),
        new Option(Proportional, "แบ่งตามสัดส่วนสินค้าแต่ละอัตรา",
            "แบ่งค่าจัดส่งตามมูลค่าสินค้าแต่ละอัตรา (7% / 0% / ยกเว้น) เป็นคนละบรรทัดในเอกสาร — ใช้เมื่อนักบัญชียืนยันแล้วเท่านั้น"),
    };

    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s == MaxRate) return MaxRate;
        if (s == Proportional) return Proportional;
        throw new BusinessRuleException($"วิธีคิด VAT ค่าจัดส่ง \"{raw}\" ไม่รู้จัก (ได้: {string.Join(" / ", All.Select(a => a.Value))})", RuleCode);
    }

    /// <summary>ค่าที่ตรึงบนออเดอร์ (null = ออเดอร์ก่อนรอบ 206 ⇒ อัตราสูงสุด)</summary>
    public static string OfOrder(string? snapshot) => snapshot == Proportional ? Proportional : MaxRate;

    public static string Label(string? mode) => All.FirstOrDefault(a => a.Value == OfOrder(mode))?.Label ?? All[0].Label;

    /// <summary>ส่วนของค่าจัดส่งที่คิดอัตรานี้</summary>
    public sealed record Part(decimal Rate, decimal Fee);

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แบ่งค่าส่งเป็นส่วนต่ออัตรา — <see cref="MaxRate"/> ⇒ ส่วนเดียวที่อัตราสูงสุด (เดิม) · <see cref="Proportional"/> ⇒ ตามสัดส่วน
    /// มูลค่าสินค้าแต่ละอัตรา (เศษปัดลงส่วนที่มูลค่ามากสุด) · ร้านไม่จด VAT ⇒ ส่วนเดียวอัตรา 0 · ไม่มีสินค้า/ค่าส่ง 0 ⇒ ว่าง</summary>
    public static IReadOnlyList<Part> Split(string? mode, bool sellerVatRegistered, decimal fee, IReadOnlyList<(decimal Gross, decimal Rate)> goods)
    {
        if (fee <= 0m) return Array.Empty<Part>();
        var maxRate = ShippingFeeRules.VatRateForShipping(sellerVatRegistered, goods.Select(g => g.Rate));
        if (OfOrder(mode) == MaxRate || !sellerVatRegistered) return new[] { new Part(maxRate, R2(fee)) };
        var groups = goods.Where(g => g.Gross > 0m).GroupBy(g => g.Rate)
            .Select(g => (Rate: g.Key, Gross: g.Sum(x => x.Gross))).OrderByDescending(g => g.Gross).ThenByDescending(g => g.Rate).ToList();
        var total = groups.Sum(g => g.Gross);
        if (groups.Count <= 1 || total <= 0m) return new[] { new Part(maxRate, R2(fee)) };
        var parts = new List<Part>();
        var allocated = 0m;
        for (var i = 0; i < groups.Count; i++)
        {
            var share = i == groups.Count - 1 ? R2(fee - allocated) : R2(fee * groups[i].Gross / total);
            allocated += share;
            if (share > 0m) parts.Add(new Part(groups[i].Rate, share));
        }
        // เศษปัด (ถ้ามี) เข้าส่วนที่มูลค่ามากสุด ⇒ Σ = ค่าส่งเสมอ
        var drift = R2(fee - parts.Sum(p => p.Fee));
        if (drift != 0m && parts.Count > 0) parts[0] = parts[0] with { Fee = R2(parts[0].Fee + drift) };
        return parts;
    }

    /// <summary>VAT ที่อยู่ในค่าส่ง (ราคาค่าส่งรวม VAT) ของทุกส่วน — สูตรเดียวกับ <see cref="ShippingFeeRules.VatInShipping"/></summary>
    public static decimal VatIn(IReadOnlyList<Part> parts) => parts.Sum(p => ShippingFeeRules.VatInShipping(p.Fee, p.Rate));

    /// <summary>คำอธิบายบรรทัดเอกสาร — ส่วนเดียว = "ค่าจัดส่ง" (เดิมทุกตัวอักษร) · หลายส่วน = บอกอัตรา</summary>
    public static string LineDescription(Part p, int partCount)
        => partCount <= 1 ? "ค่าจัดส่ง" : $"ค่าจัดส่ง ({RateLabel(p.Rate)})";


    private static string RateLabel(decimal rate)
        => rate == ThaiVatTypeRule.ExemptRate ? "ยกเว้น VAT" : rate <= 0m ? "VAT 0%" : $"VAT {rate:0.##}%";
}
