namespace Accounting.Helpers;

/// <summary>
/// ตารางอัตรา **ภาษีเงินได้นิติบุคคล (CIT)** + เกณฑ์ SME
/// — <b>ตัวอ้างอิงกลางตัวเดียวของระบบ</b> (OWNER file)
///
/// ═══ ทำไมต้องมี (ผลตรวจรอบ 181 · DECISION_AUDIT_2026-09-18.md §3 D2-B2a) ═══
/// <para>คำถามเดียวกัน ("กำไรสุทธิเท่านี้ ต้องเสีย CIT เท่าไร") เคยถูกตอบด้วย
/// ตารางคนละชุด <b>2 ที่</b> และ <b>ไม่ตรงกัน</b>:</para>
/// <list type="bullet">
/// <item><c>TaxService.CalculateThaiCit(netProfit)</c> — ใช้ <b>ขั้นบันได SME
///   กับทุกบริษัท</b> ไม่ดูทุน/รายได้เลย และผู้เรียกคือเส้น <b>ภ.ง.ด.50</b>
///   ⇒ บริษัททั่วไป (ทุน &gt; 5 ล. หรือรายได้ &gt; 30 ล.) เสียภาษี<b>ต่ำกว่า
///   กฎหมาย</b> บนแบบที่ยื่นจริง (กำไร 1,000,000 ได้ 105,000 แทน 200,000)</item>
/// <item><c>TaxFilingExportService.ComputeCit(netProfit, isSme)</c> — ถูกต้อง
///   แต่ถือเกณฑ์ SME เป็นสูตร inline ของตัวเอง</item>
/// </list>
/// <para>ตารางกฎหมาย (อัตรา · ขั้น · เกณฑ์) <b>ห้ามมีสำเนาที่สอง</b> —
/// หลักการ "ตัวตั้งตัวเดียว" ใน CLAUDE.md กฎเหล็ก #4 F2 ข้อ 4</para>
///
/// ═══ ฐานกฎหมาย ═══
/// <para>ประมวลรัษฎากร §65 (ฐานกำไรสุทธิ) · อัตราทั่วไป 20% ·
/// อัตราขั้นบันไดของ <b>SME</b> ตามพระราชกฤษฎีกาฯ ฉบับที่ 530 (และที่แก้ไข
/// เพิ่มเติม — ฉบับที่ 603 ทำให้ชุด 0 / 15 / 20 นี้ใช้ตั้งแต่รอบบัญชี 2560
/// เป็นต้นไป) · เกณฑ์ SME = ทุนที่ชำระแล้ว ณ วันสุดท้ายของรอบ ≤ 5 ล้านบาท
/// <b>และ</b> รายได้จากการขายสินค้า/บริการทั้งรอบ ≤ 30 ล้านบาท (ต้องเข้า
/// <b>ทั้งสองข้อ</b> — ข้อเดียวไม่พอ)</para>
///
/// <para>⚠️ ข้อจำกัดที่รู้ตัว: ตารางนี้ยังไม่มีมิติ "รอบบัญชี" — ถ้าวันหนึ่ง
/// อัตราเปลี่ยนตามปี ต้องเพิ่มพารามิเตอร์ปีที่ <b>ที่นี่ที่เดียว</b>
/// ห้ามให้ผู้เรียกไปคิดเอง</para>
/// </summary>
public static class CitRateTable
{
    /// <summary>เพดานทุนที่ชำระแล้วของ SME (พ.ร.ฎ. 530) — ≤ ค่านี้ = เข้าเกณฑ์</summary>
    public const decimal SmePaidUpCapitalCeiling = 5_000_000m;

    /// <summary>เพดานรายได้ทั้งรอบของ SME (พ.ร.ฎ. 530) — ≤ ค่านี้ = เข้าเกณฑ์</summary>
    public const decimal SmeAnnualRevenueCeiling = 30_000_000m;

    /// <summary>อัตราทั่วไป (ไม่ใช่ SME) — 20% ของกำไรสุทธิทั้งก้อน</summary>
    public const decimal StandardRate = 0.20m;

    /// <param name="UpTo">ขอบบนของขั้นนี้ (กำไรสุทธิสะสม) — <see cref="decimal.MaxValue"/> = ขั้นบนสุด</param>
    /// <param name="Rate">อัตราของส่วนที่อยู่ในขั้นนี้ (สัดส่วน ไม่ใช่ %)</param>
    public sealed record Bracket(decimal UpTo, decimal Rate);

    /// <summary>ขั้นบันได SME — 0–300,000 = 0% · 300,001–3,000,000 = 15% · ส่วนที่เกิน = 20%</summary>
    public static readonly IReadOnlyList<Bracket> SmeBrackets = new[]
    {
        new Bracket(300_000m, 0m),
        new Bracket(3_000_000m, 0.15m),
        new Bracket(decimal.MaxValue, 0.20m),
    };

    /// <summary>
    /// บริษัทนี้เข้าเกณฑ์ SME หรือไม่ — <b>ต้องเข้าทั้งสองข้อ</b>
    /// (ทุนที่ชำระแล้ว ≤ 5 ล. <b>และ</b> รายได้ทั้งรอบ ≤ 30 ล.)
    ///
    /// <para>⚠️ ผู้เรียกต้องส่ง<b>รายได้ทั้งรอบ</b> — ถ้ามีแค่ครึ่งรอบ (ภ.ง.ด.51)
    /// ให้ประมาณเป็นทั้งรอบก่อนส่งเข้ามา ห้ามส่งยอดครึ่งเดียว มิฉะนั้นบริษัท
    /// รายได้ 40 ล. จะถูกนับเป็น SME</para>
    /// </summary>
    public static bool IsSme(decimal paidUpCapital, decimal annualRevenue)
        => paidUpCapital <= SmePaidUpCapitalCeiling
        && annualRevenue <= SmeAnnualRevenueCeiling;

    /// <summary>
    /// ภาษีเงินได้นิติบุคคลจากกำไรสุทธิ (บาท) — ขาดทุนหรือศูนย์ = 0
    /// </summary>
    /// <param name="netProfit">กำไรสุทธิทางภาษี (หลังบวกกลับ §65 ตรี และหักผลขาดทุนยกมาแล้ว)</param>
    /// <param name="isSme">ผลของ <see cref="IsSme"/> — ห้ามเดา ห้ามตั้ง true ตายตัว</param>
    public static decimal Compute(decimal netProfit, bool isSme)
    {
        if (netProfit <= 0m) return 0m;

        // ⚠️ ทุกจุดที่ปัดต้องระบุ AwayFromZero — default ของ .NET คือ banker's
        // rounding. `×0.15` **ตกจุดกึ่งกลางจริง** (ยอดที่สตางค์ ≡ 10 mod 20:
        // 0.30 → 0.045 · 0.70 → 0.105) ⇒ ราว 5% ของยอดจะปัดลงถ้าลืม
        // ส่วน `×0.20` ไม่เคยตกจุดกึ่งกลาง แต่เขียนให้เหมือนกันทั้งเมธอด
        // เพื่อไม่ให้คนถัดไปคัดลอกรูปที่ไม่มี MidpointRounding ไปใช้
        if (!isSme) return Math.Round(netProfit * StandardRate, 2, MidpointRounding.AwayFromZero);

        decimal tax = 0m;
        decimal previousBound = 0m;
        foreach (var b in SmeBrackets)
        {
            if (netProfit <= previousBound) break;
            var inBracket = Math.Min(netProfit, b.UpTo) - previousBound;
            tax += inBracket * b.Rate;
            previousBound = b.UpTo;
        }

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
    }
}
