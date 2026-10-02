namespace Accounting.Helpers;

/// <summary>
/// ด่านช่วงค่าของหน้าตั้งค่าที่พัก — ตัวตัดสินตัวเดียวของ "ต่ำสุด ≤ สูงสุด" (รอบ 202 ทีม LS · S-P2-7)
///
/// <para><b>ที่มา</b>: เดิม <c>LodgingService.Apply</c> เขียน <c>MaxNights = Math.Max(MinNights, d.MaxNights)</c> ⇒ ผู้ใช้ตั้ง
/// "พักสูงสุด 3 คืน · ขั้นต่ำ 5 คืน" กดบันทึกได้ "สำเร็จ" แต่ระบบยกสูงสุดเป็น 5 เงียบ ๆ (ค่าที่ผู้ใช้พิมพ์หายโดยไม่มีใครบอก) ·
/// แผนราคาไม่มีด่านเลย ⇒ แผนที่ขั้นต่ำ &gt; สูงสุด หรือ "ใช้ได้ตั้งแต่" หลัง "ถึง" ถูกบันทึก แล้ว <c>PlanApplies</c> ไม่มีวันเลือก
/// แผนนั้น (แผนตายเงียบ)</para>
///
/// <para>กติกา: ค่าว่าง (null) = ไม่จำกัดฝั่งนั้น ⇒ ผ่าน · ทั้งสองฝั่งมีค่าแล้วต่ำสุด &gt; สูงสุด ⇒ ปฏิเสธพร้อมข้อความที่บอก
/// <b>ป้ายบนจอ</b>ทั้งสองช่อง + ค่าที่ผู้ใช้กรอก (ไม่แก้ค่าให้เอง)</para>
/// </summary>
public static class LodgingSettingsRules
{
    public static void EnsureNightsRange(int? minNights, int? maxNights, string minLabel, string maxLabel)
    {
        if (minNights is int min && maxNights is int max && max < min)
            throw new BusinessRuleException(
                $"«{maxLabel}» ({max}) ต้องไม่น้อยกว่า «{minLabel}» ({min}) — แก้ตัวใดตัวหนึ่งแล้วบันทึกอีกครั้ง",
                "LODGING-NIGHTS-RANGE");
    }

    public static void EnsureDateRange(DateTime? from, DateTime? to, string fromLabel, string toLabel)
    {
        if (from is DateTime f && to is DateTime t && t.Date < f.Date)
            throw new BusinessRuleException(
                $"«{toLabel}» ({ThaiDateText(t)}) ต้องไม่ก่อน «{fromLabel}» ({ThaiDateText(f)}) — แก้ช่วงวันที่แล้วบันทึกอีกครั้ง",
                "LODGING-DATE-RANGE");
    }

    private static string ThaiDateText(DateTime d) => $"{d.Day:00}/{d.Month:00}/{d.Year + 543}";

    // ── เตียงเสริม/คนเสริมต่อประเภทห้อง (คำตัดสินเจ้าของข้อ 123 · รอบ 202 ทีม LS) ──
    public const string AllowExtraBedLabel = "เพิ่มเตียงเสริม/คนเสริมได้";
    public const string MaxExtraBedsLabel = "เพิ่มได้สูงสุดกี่คน";
    public const string ExtraBedPriceLabel = "ราคาต่อคน/คืน (บาท)";

    /// <summary>ค่าที่บันทึกจริงของเตียงเสริม — ติ๊ก "เพิ่มได้" แล้วต้องมี <b>จำนวน ≥ 1</b> และ<b>ราคา</b> (0 = ไม่คิดเงิน ใส่ได้ · ว่าง = ปฏิเสธ)
    /// · ไม่ติ๊ก ⇒ จำนวน = 0 (engine ไม่ขายเตียงเสริม) ราคาคงตามที่ส่งมา (ช่องถูกล็อกบนจอ ค่าเดิมไม่หายเมื่อเปิดใหม่)
    /// <para>ที่มา: เดิมติ๊ก "มีเตียงเสริม" ได้โดยจำนวน 0 / ราคาว่าง ⇒ แขกเลือกเตียงเสริมไม่ได้เลย หรือได้ฟรีเงียบ ๆ (engine ใช้ <c>ExtraBedPrice ?? 0</c>)</para></summary>
    public static ExtraBedSetting NormalizeExtraBed(bool allow, int maxExtraBeds, decimal? pricePerPersonNight)
    {
        if (pricePerPersonNight is decimal neg && neg < 0)
            throw new BusinessRuleException($"«{ExtraBedPriceLabel}» ต้องไม่ติดลบ", "LODGING-EXTRA-BED");
        if (!allow) return new ExtraBedSetting(false, 0, pricePerPersonNight);
        if (maxExtraBeds < 1)
            throw new BusinessRuleException(
                $"ติ๊ก «{AllowExtraBedLabel}» แล้วต้องระบุ «{MaxExtraBedsLabel}» อย่างน้อย 1 คน — หรือเอาติ๊กออกถ้าไม่รับคนเสริม",
                "LODGING-EXTRA-BED");
        if (pricePerPersonNight is null)
            throw new BusinessRuleException(
                $"ติ๊ก «{AllowExtraBedLabel}» แล้วต้องระบุ «{ExtraBedPriceLabel}» (ไม่คิดเงินให้ใส่ 0)",
                "LODGING-EXTRA-BED");
        return new ExtraBedSetting(true, maxExtraBeds, Math.Round(pricePerPersonNight.Value, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>ป้ายสรุปบนการ์ดประเภทห้อง (หน้าเว็บแสดงอย่างเดียว) — ค่าเดิมที่ตั้งไม่ครบ (ก่อนมีด่าน) บอกตรง ๆ ว่าไม่ครบ ไม่แต่งตัวเลข</summary>
    public static string ExtraBedSummary(bool allow, int maxExtraBeds, decimal? pricePerPersonNight)
    {
        if (!allow) return "ไม่รับเตียงเสริม/คนเสริม";
        if (maxExtraBeds < 1 || pricePerPersonNight is null)
            return "⚠️ เปิดเตียงเสริมแต่ตั้งค่าไม่ครบ (จำนวน/ราคา) — แก้ไขประเภทห้องนี้แล้วบันทึก";
        var price = pricePerPersonNight.Value == 0m
            ? "ไม่คิดเงิน"
            : $"฿{pricePerPersonNight.Value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}/คน/คืน";
        return $"เตียงเสริมสูงสุด {maxExtraBeds} คน · {price}";
    }
}

/// <summary>ค่าเตียงเสริมที่ผ่านด่านแล้ว (<see cref="LodgingSettingsRules.NormalizeExtraBed"/>)</summary>
public sealed record ExtraBedSetting(bool Allow, int MaxExtraBeds, decimal? PricePerPersonNight);
