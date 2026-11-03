using System.Linq;
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
/// <para>รอบ 230 (TAX_PENALTY_AUDIT §6): ตารางมี<b>มิติรอบบัญชี</b>แล้ว — แถว <see cref="CitRateBracket"/>
/// (ปีรอบบัญชีที่มีผล · SME/ทั่วไป · ขอบล่าง/บน · อัตรา) ชุดเดียวที่ <see cref="RateBrackets"/> · อัตราเปลี่ยนตามปี
/// ⇒ เพิ่มแถวที่นี่ที่เดียว ห้ามให้ผู้เรียกไปคิดเอง · "ปีรอบบัญชี" = ปี ค.ศ. ที่รอบ<b>เริ่ม</b> (นิยามเดียวกับ <see cref="FiscalYear"/>)</para>
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

    /// <summary>แถวตารางอัตรา CIT (CLAUDE.md กฎเหล็ก #2 §I <c>CitRateBracket(fiscalYear, taxpayerType, min, max, rate)</c>)</summary>
    /// <param name="FromFiscalYear">ปีรอบบัญชี (ค.ศ. ที่รอบเริ่ม) แรกที่แถวนี้มีผล</param>
    /// <param name="ToFiscalYear">ปีรอบบัญชีสุดท้ายที่มีผล — null = ยังมีผล</param>
    /// <param name="Sme">true = ขั้นบันได SME · false = บริษัททั่วไป</param>
    /// <param name="Min">ขอบล่างของกำไรสุทธิสะสม (ไม่รวม)</param>
    /// <param name="Max">ขอบบน (รวม) — <see cref="decimal.MaxValue"/> = ขั้นบนสุด</param>
    /// <param name="Rate">อัตราของส่วนที่อยู่ในขั้นนี้ (สัดส่วน ไม่ใช่ %)</param>
    public sealed record CitRateBracket(int FromFiscalYear, int? ToFiscalYear, bool Sme, decimal Min, decimal Max, decimal Rate);

    /// <summary>ปีรอบบัญชีแรกที่ตารางรู้จัก — ชุด 0/15/20 ของ SME ใช้กับรอบบัญชีที่เริ่มตั้งแต่ 1 ม.ค. 2560 (พ.ร.ฎ. 603)</summary>
    public const int EarliestScheduledFiscalYear = 2017;

    /// <summary><b>ตารางอัตรา CIT ชุดเดียวของระบบ</b> — SME: 0–300,000 = 0% · 300,001–3,000,000 = 15% · ส่วนที่เกิน = 20% ·
    /// ทั่วไป: 20% ทั้งก้อน · รอบก่อน <see cref="EarliestScheduledFiscalYear"/> ใช้ชุดที่เก่าที่สุด (ผู้เรียกต้องบอกผู้ใช้ผ่าน <see cref="HasScheduleFor"/>)</summary>
    public static readonly IReadOnlyList<CitRateBracket> RateBrackets = new[]
    {
        new CitRateBracket(EarliestScheduledFiscalYear, null, true, 0m, 300_000m, 0m),
        new CitRateBracket(EarliestScheduledFiscalYear, null, true, 300_000m, 3_000_000m, 0.15m),
        new CitRateBracket(EarliestScheduledFiscalYear, null, true, 3_000_000m, decimal.MaxValue, StandardRate),
        new CitRateBracket(EarliestScheduledFiscalYear, null, false, 0m, decimal.MaxValue, StandardRate),
    };

    private static bool Covers(CitRateBracket b, int fiscalYear)
        => fiscalYear >= b.FromFiscalYear && (b.ToFiscalYear is null || fiscalYear <= b.ToFiscalYear);

    /// <summary>ตารางมีแถวของรอบบัญชีนี้จริงไหม — false ⇒ <see cref="BracketsFor"/> ใช้ชุดที่ใกล้ที่สุด (ต้องบอกผู้ใช้ — ต้องยืนยันกับผู้ทำบัญชี)</summary>
    public static bool HasScheduleFor(int fiscalYear) => RateBrackets.Any(b => Covers(b, fiscalYear));

    /// <summary>ขั้นของรอบบัญชี <paramref name="fiscalYear"/> เรียงตามขอบล่าง — ไม่มีแถวของปีนั้น ⇒ ชุดที่ปีใกล้ที่สุด (ก่อนตาราง = ชุดแรก · หลังแถวที่ปิดปี = ชุดล่าสุด)</summary>
    private static IReadOnlyList<CitRateBracket> BracketsFor(int fiscalYear, bool isSme)
    {
        var rows = RateBrackets.Where(b => b.Sme == isSme && Covers(b, fiscalYear)).OrderBy(b => b.Min).ToList();
        if (rows.Count > 0) return rows;
        var mine = RateBrackets.Where(b => b.Sme == isSme).ToList();
        var from = fiscalYear < mine.Min(b => b.FromFiscalYear) ? mine.Min(b => b.FromFiscalYear) : mine.Max(b => b.FromFiscalYear);
        return mine.Where(b => b.FromFiscalYear == from).OrderBy(b => b.Min).ToList();
    }

    /// <summary>ปีรอบบัญชีล่าสุดที่ตารางมี — ค่าตั้งต้นของ <see cref="Compute(decimal, bool)"/> (ผู้เรียกที่รู้ปีต้องใช้ overload ที่รับปี)</summary>
    private static int LatestScheduledFiscalYear => RateBrackets.Max(b => b.FromFiscalYear);

    /// <summary>ขั้นบันได SME ของตารางล่าสุด (มุมมองเดิม — อ่านจาก <see cref="RateBrackets"/> ไม่ใช่สำเนาที่สอง)</summary>
    public static IReadOnlyList<Bracket> SmeBrackets
        => BracketsFor(LatestScheduledFiscalYear, true).Select(b => new Bracket(b.Max, b.Rate)).ToList();

    /// <summary>
    /// บริษัทนี้เข้าเกณฑ์ SME หรือไม่ — <b>ต้องเข้าทั้งสองข้อ</b>
    /// (ทุนที่ชำระแล้ว ≤ 5 ล. <b>และ</b> รายได้ทั้งรอบ ≤ 30 ล.)
    ///
    /// <para>⚠️ ผู้เรียกต้องส่ง<b>รายได้ทั้งรอบ</b> — ถ้ามีแค่ครึ่งรอบ (ภ.ง.ด.51)
    /// ให้ประมาณเป็นทั้งรอบก่อนส่งเข้ามา ห้ามส่งยอดครึ่งเดียว มิฉะนั้นบริษัท
    /// รายได้ 40 ล. จะถูกนับเป็น SME</para>
    /// </summary>
    /// <param name="paidUpCapital">ทุนที่ชำระแล้ว — <b><c>null</c> หรือ ≤ 0 =
    /// "ยังไม่กรอก"</b> ไม่ใช่ "ทุนศูนย์" (นิติบุคคลมีทุน 0 ไม่ได้)</param>
    /// <param name="annualRevenue">รายได้ทั้งรอบ</param>
    public static bool IsSme(decimal? paidUpCapital, decimal annualRevenue)
        => KnownCapital(paidUpCapital) is { } capital
        && capital <= SmePaidUpCapitalCeiling
        && annualRevenue <= SmeAnnualRevenueCeiling;

    /// <summary>ทุนที่ชำระแล้วที่ "รู้จริง" — null/≤0 = ไม่รู้
    ///
    /// <para>คอลัมน์ <c>Companies.PaidUpCapital</c> เป็น <c>NOT NULL DEFAULT 0</c>
    /// และก่อนรอบ 182 <b>ไม่มีช่องให้กรอกเลย</b> ⇒ ทุกบริษัทมีค่า 0 ⇒ "ไม่รู้"
    /// ถูกแปลงเป็น "ทุนน้อยมาก" โดยอัตโนมัติ. นิติบุคคลจดทะเบียนโดยมีทุนชำระแล้ว
    /// 0 บาท<b>ไม่ได้</b> ⇒ 0 แปลว่า "ยังไม่กรอก" เสมอ</para></summary>
    private static decimal? KnownCapital(decimal? paidUpCapital)
        => paidUpCapital is { } v && v > 0m ? v : null;

    /// <summary>เหตุผลภาษาไทยว่าทำไมบริษัทนี้ถูกจัดเป็น SME หรือไม่ —
    /// ต้องเดินทางไปถึงจอ/ไฟล์ยื่น ไม่ใช่ตัดสินเงียบ (DOCTRINE §1 G3)
    ///
    /// <para>⚠️ ทิศที่เลือก (คำตัดสินรอบ 182 · Q10): <b>ไม่รู้ทุน = ไม่ใช่ SME</b>
    /// เพราะทิศตรงข้าม ("ไม่รู้ = SME") ทำให้เสียภาษี<b>ต่ำกว่า</b>กฎหมายโดย
    /// ความเสียหาย<b>มองไม่เห็น</b>จนกว่าสรรพากรจะประเมินย้อนหลังพร้อมเบี้ยปรับ ·
    /// ทิศนี้ถ้าผิด บริษัทจะเห็นยอดภาษีสูงผิดปกติ<b>ก่อน</b>ยื่น และมีช่องกรอกทุน
    /// ให้แก้ได้ทันที = ความเสียหายมองเห็นและแก้ทัน (DOCTRINE §1 G5)</para></summary>
    public static string SmeReason(decimal? paidUpCapital, decimal annualRevenue)
    {
        var capital = KnownCapital(paidUpCapital);
        if (capital is null)
            return "ไม่ใช่ SME — ยังไม่ได้กรอก \"ทุนที่ชำระแล้ว\" ของบริษัท ระบบจึงใช้อัตราทั่วไป 20% "
                 + "(กรอกทุนที่หน้าตั้งค่าบริษัท แล้วสร้างรายงานใหม่ ถ้าเข้าเกณฑ์จะได้ขั้นบันได SME)";
        if (capital > SmePaidUpCapitalCeiling)
            return $"ไม่ใช่ SME — ทุนที่ชำระแล้ว {capital:N2} บาท เกิน {SmePaidUpCapitalCeiling:N0} บาท";
        if (annualRevenue > SmeAnnualRevenueCeiling)
            return $"ไม่ใช่ SME — รายได้ทั้งรอบ {annualRevenue:N2} บาท เกิน {SmeAnnualRevenueCeiling:N0} บาท";
        return $"SME — ทุนที่ชำระแล้ว {capital:N2} บาท และรายได้ทั้งรอบ {annualRevenue:N2} บาท "
             + "เข้าเกณฑ์ทั้งสองข้อ (พ.ร.ฎ. 530)";
    }

    /// <summary>
    /// ภาษีเงินได้นิติบุคคลจากกำไรสุทธิ (บาท) — ขาดทุนหรือศูนย์ = 0
    /// </summary>
    /// <param name="netProfit">กำไรสุทธิทางภาษี (หลังบวกกลับ §65 ตรี และหักผลขาดทุนยกมาแล้ว)</param>
    /// <param name="isSme">ผลของ <see cref="IsSme"/> — ห้ามเดา ห้ามตั้ง true ตายตัว</param>
    public static decimal Compute(decimal netProfit, bool isSme)
        => Compute(netProfit, isSme, LatestScheduledFiscalYear);

    /// <summary>ภาษีเงินได้นิติบุคคลตามตารางของรอบบัญชี <paramref name="fiscalYear"/> (รอบ 230) — ขาดทุนหรือศูนย์ = 0</summary>
    /// <param name="fiscalYear">ปี ค.ศ. ที่รอบบัญชีเริ่ม (<see cref="FiscalYear.RangeFor"/>)</param>
    public static decimal Compute(decimal netProfit, bool isSme, int fiscalYear)
    {
        if (netProfit <= 0m) return 0m;

        // ⚠️ ทุกจุดที่ปัดต้องระบุ AwayFromZero — default ของ .NET คือ banker's
        // rounding. `×0.15` **ตกจุดกึ่งกลางจริง** (ยอดที่สตางค์ ≡ 10 mod 20:
        // 0.30 → 0.045 · 0.70 → 0.105) ⇒ ราว 5% ของยอดจะปัดลงถ้าลืม
        // ส่วน `×0.20` ไม่เคยตกจุดกึ่งกลาง แต่เขียนให้เหมือนกันทั้งเมธอด
        // เพื่อไม่ให้คนถัดไปคัดลอกรูปที่ไม่มี MidpointRounding ไปใช้
        decimal tax = 0m;
        foreach (var b in BracketsFor(fiscalYear, isSme))
        {
            if (netProfit <= b.Min) break;
            tax += (Math.Min(netProfit, b.Max) - b.Min) * b.Rate;
        }

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
    }
}
