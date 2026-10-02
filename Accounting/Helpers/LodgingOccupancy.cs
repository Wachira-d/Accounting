namespace Accounting.Helpers;

/// <summary>จำนวนผู้เข้าพักของการจอง/ใบเสนอราคา — ค่าที่หน้าเว็บ/voucher/อีเมลแสดง (server computes · page displays)</summary>
/// <param name="ExtraGuests">คนเสริม (ช่อง <c>extraBeds</c> ของห้อง) — <b>แยกจาก</b>ผู้ใหญ่ ไม่ซ้อนนับ</param>
/// <param name="Total">ผู้ใหญ่ + เด็ก + ทารก + คนเสริม</param>
public readonly record struct LodgingGuestTotals(int Adults, int Children, int Infants, int ExtraGuests, int Total);

/// <summary>
/// **ความจุห้อง + จำนวนผู้เข้าพัก — ตัวตัดสินตัวเดียว** (รอบ 202 ทีม LO · คำตัดสินเจ้าของข้อ 123/124)
///
/// <para><b>แบบจำลอง</b> (ห้ามเปลี่ยนชื่อช่อง request เดิม <c>rooms[].adults/children/extraBeds</c>):
/// <c>adults</c> = ผู้ใหญ่บนเตียงปกติ · <c>children</c> = เด็ก · <c>extraBeds</c> = <b>คนเสริม</b> (คนละคนกับ adults — ไม่ซ้อนนับ) ·
/// ทารก = ระดับการจอง (<c>infants</c> ของคำขอจอง/ใบเสนอราคา)</para>
/// <list type="bullet">
///   <item><b>ข้อ 124 ความจุ</b>: ตรวจเฉพาะผู้ใหญ่ — ผู้ใหญ่ทั้งหมดในห้อง (adults + คนเสริม) ≤ <c>MaxAdults</c> + คนเสริมที่ซื้อ
///     ⇔ adults ≤ MaxAdults · เด็ก/ทารก<b>ไม่นับ</b> (เดิมนับ MaxChildren/MaxOccupancy ⇒ ครอบครัวมีลูกเล็กจองไม่ได้)</item>
///   <item><b>ข้อ 123 คนเสริม</b>: ต้อง <c>AllowExtraBed</c> และ ≤ <c>MaxExtraBeds</c> — <b>เกิน = ปฏิเสธพร้อมข้อความ</b>
///     (เดิมตัดทิ้งเงียบ ๆ ⇒ แขกคิดว่าซื้อคนเสริมแล้วแต่ไม่ถูกคิดเงิน/ไม่มีที่นอน) · ราคา = <c>ExtraBedPrice</c> × คน × คืน (engine ตัวเดิม)</item>
/// </list>
/// <para>ราคาเด็ก/แขกเกินมาตรฐาน = กติกาเดิมของ engine (นับผู้ใหญ่+เด็กบนเตียงปกติ · คนเสริมคิดผ่านราคาคนเสริมอย่างเดียว ไม่คิดซ้ำ)</para>
/// </summary>
public static class LodgingOccupancy
{
    /// <summary>ห้องประเภทนี้ "ขายคนเสริม" จริงไหม — เปิดเตียงเสริม + จำนวน ≥ 1 + <b>มีราคา</b> (0 = ตั้งใจไม่คิดเงิน · ว่าง = ตั้งค่าไม่ครบ)
    /// <para>ฝ่ายค้านรอบ 202 P2-5: แถวเก่าที่ติ๊กเตียงเสริมแต่ราคาว่าง (ก่อนด่านบันทึก <c>LodgingSettingsRules.NormalizeExtraBed</c>) เดิม engine ใช้
    /// <c>?? 0</c> ⇒ แขกได้คนเสริมฟรีโดยที่พักไม่ได้ตั้งใจ · ตอนนี้ราคาว่าง = ไม่ขายคนเสริม (เจ้าของเห็นป้าย "ตั้งค่าไม่ครบ" ในหน้าตั้งค่า)</para></summary>
    public static bool SellsExtraBeds(bool allowExtraBed, int maxExtraBeds, decimal? extraBedPrice)
        => allowExtraBed && maxExtraBeds > 0 && extraBedPrice is not null;

    /// <summary>ปัญหาเรื่องคนเสริมของห้อง 1 ห้อง (null = ใช้ได้) — แยกจากปัญหาผู้ใหญ่ เพราะเส้นเลื่อนวันของพนักงานผ่อนเฉพาะข้อนี้ (ใบเดิมซื้อไว้แล้ว)</summary>
    public static string? ExtraBedProblem(string roomTypeName, int extraGuests, bool allowExtraBed, int maxExtraBeds, decimal? extraBedPrice)
    {
        if (extraGuests <= 0) return null;
        if (!SellsExtraBeds(allowExtraBed, maxExtraBeds, extraBedPrice))
            return $"{roomTypeName}: ห้องนี้ไม่รับคนเสริม/เตียงเสริม — เลือกห้องเพิ่ม หรือประเภทห้องที่ใหญ่ขึ้น";
        if (extraGuests > maxExtraBeds)
            return $"{roomTypeName}: เพิ่มคนเสริมได้สูงสุด {maxExtraBeds} คน/ห้อง";
        return null;
    }

    /// <summary>ปัญหาผู้ใหญ่เกินความจุ (null = ใช้ได้) — เด็ก/ทารกไม่นับ (ข้อ 124)</summary>
    public static string? AdultProblem(string roomTypeName, int adults, int maxAdults, bool allowExtraBed, int maxExtraBeds, decimal? extraBedPrice)
    {
        if (adults <= maxAdults) return null;
        var extraOffer = SellsExtraBeds(allowExtraBed, maxExtraBeds, extraBedPrice)
            ? $" — เพิ่มได้อีกเป็นคนเสริม สูงสุด {maxExtraBeds} คน/ห้อง ({extraBedPrice!.Value:N0} บาท/คน/คืน)" : "";
        return $"{roomTypeName}: ผู้ใหญ่สูงสุด {maxAdults} คน/ห้อง{extraOffer}";
    }

    /// <summary>ผู้ใหญ่สูงสุดที่ห้องประเภทนี้รับได้เมื่อซื้อคนเสริมเต็มที่ — ใช้ตัดสิน "จองได้ไหม" บนผลค้นหา (ยังไม่ได้เลือกคนเสริม)</summary>
    public static int MaxAdultsWithExtras(int maxAdults, bool allowExtraBed, int maxExtraBeds, decimal? extraBedPrice)
        => maxAdults + (SellsExtraBeds(allowExtraBed, maxExtraBeds, extraBedPrice) ? maxExtraBeds : 0);

    /// <summary>ราคาคนเสริมต่อคนต่อคืนที่การจองเดิมจ่ายไว้ (ย้อนจากยอดคนเสริมใน snapshot ราคา) — null = หาไม่ได้ (ไม่มีคนเสริม/ไม่มีคืน) ·
    /// ใช้คงราคาเดิมเมื่อพนักงานเลื่อนวัน/ขยายคืนใบที่ซื้อคนเสริมไว้ก่อนที่พักปิดเตียงเสริม (ฝ่ายค้านรอบ 202 P2-4)</summary>
    public static decimal? PerPersonNightPrice(decimal extraBedCharge, int extraGuests, int nights)
        => extraGuests <= 0 || nights <= 0 || extraBedCharge < 0m ? null
            : Math.Round(extraBedCharge / (extraGuests * nights), 2, MidpointRounding.AwayFromZero);

    /// <summary>รวมจำนวนผู้เข้าพัก (ค่าติดลบถือเป็น 0)</summary>
    public static LodgingGuestTotals Totals(int adults, int children, int infants, int extraGuests)
    {
        int Z(int x) => Math.Max(0, x);
        return new LodgingGuestTotals(Z(adults), Z(children), Z(infants), Z(extraGuests),
            Z(adults) + Z(children) + Z(infants) + Z(extraGuests));
    }

    /// <summary>ข้อความแสดงผล "รวม N คน (ผู้ใหญ่ a · เด็ก c · ทารก i · คนเสริม e)" — ตัวเดียวของหน้าเว็บ/voucher/อีเมล</summary>
    public static string Summary(LodgingGuestTotals t)
    {
        var parts = new List<string> { $"ผู้ใหญ่ {t.Adults}" };
        if (t.Children > 0) parts.Add($"เด็ก {t.Children}");
        if (t.Infants > 0) parts.Add($"ทารก {t.Infants}");
        if (t.ExtraGuests > 0) parts.Add($"คนเสริม {t.ExtraGuests}");
        return $"รวม {t.Total} คน ({string.Join(" · ", parts)})";
    }

    /// <summary>จำนวนคนที่ใช้คิดบริการเสริมแบบ "ต่อคน" — ทุกคนที่พักจริงยกเว้นทารก (คนเสริมกินอาหารเช้าด้วย)</summary>
    public static int ChargeableGuests(int adults, int children, int extraGuests)
        => Math.Max(0, adults) + Math.Max(0, children) + Math.Max(0, extraGuests);
}
