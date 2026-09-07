using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **สูตรค่าเสื่อมราคาตัวเดียวของระบบ** — ทั้งตารางที่ผู้ใช้เห็นล่วงหน้า
/// (<c>FixedAssetService.BuildScheduleRows</c>) และยอดที่ลง GL จริง
/// (<c>CalculateDepreciationAsync</c>) ต้องเรียกตัวนี้
///
/// ═══ ที่มา (ผลตรวจทีม E · <c>SYSTEM_AUDIT_2026-09-07.md</c> E-01) ═══
/// สองที่คิดคนละสูตร: ตารางแผนมี **switch-to-straight-line** แต่เส้นที่โพสต์ JE
/// ไม่มี ⇒ วิธี <c>DecliningBalance</c> ให้ตัวเลขต่างกันตั้งแต่งวดที่ 2 และ
/// <b>ไม่มีวันจบ</b>:
/// <list type="bullet">
///   <item>ทุน 120,000 · อายุ 12 เดือน · salvage 0 — แผนตัดจบพอดี 120,000
///     แต่ที่ลง GL จริงได้ 77,760.52 (NBV ค้าง 42,239.48 = <b>35.2%</b>)
///     ⇒ ค่าใช้จ่ายต่ำกว่าจริง กำไรในงบและ ภ.ง.ด.50 สูงเกินจริง</item>
///   <item>salvage = 0 ⇒ <c>NBV − NBV/n</c> ไม่มีวันถึง 0 ⇒ โพสต์ JE ค่าเสื่อม
///     <b>ทุกเดือนตลอดกาล</b> และ <c>Status</c> ไม่มีวันเป็น
///     <c>FullyDepreciated</c> (งวดที่ 60 ยังคิด 61.60)</item>
///   <item>salvage 20,000 จากทุน 120,000 ⇒ ตัดจบที่เดือนที่ **20.6** แทน 12
///     (ยาวกว่าอายุที่ตั้งไว้ 72%)</item>
/// </list>
///
/// <para>อีกอย่างที่หายไปทั้งสองเส้นคือ <b>การปัดเศษ</b> — ทุน 100,000 / 60 เดือน
/// ได้ <c>1666.66666…</c> ลง GL ทั้งค่านั้น (Dr/Cr ใช้ค่าเดียวกันจึงยังบาลานซ์
/// แต่งบพิมพ์ <c>N2</c> ได้ 1,666.67 × 12 = 20,000.04 ≠ 20,000.00 ของยอดสะสม)
/// ที่นี่ปัด 2 ตำแหน่งด้วย <see cref="MidpointRounding.AwayFromZero"/> และให้
/// <b>งวดสุดท้ายรับเศษที่เหลือ</b> เพื่อให้ Σ ทุกงวด = <c>cost − salvage</c> พอดี</para>
/// </summary>
public static class DepreciationSchedule
{
    /// <summary>หนึ่งงวดของตาราง</summary>
    public readonly record struct Period(
        int Year, int Month, decimal Amount, decimal Accumulated, decimal NetBookValue);

    /// <summary>
    /// ลำดับงวดของวันที่หนึ่ง ๆ (0 = งวดแรก) — งวดแรกคือ <b>เดือนถัดจากเดือนที่ซื้อ</b>
    /// ตรงกับตารางแผนที่ใช้ <c>PurchaseDate.AddMonths(i + 1)</c>
    /// </summary>
    /// <returns>−1 หรือน้อยกว่า = ยังไม่ถึงงวดแรก (เดือนที่ซื้อเอง/ก่อนหน้า) ⇒ ไม่คิดค่าเสื่อม</returns>
    public static int MonthIndexFor(DateTime purchaseDate, int year, int month)
        => (year - purchaseDate.Year) * 12 + (month - purchaseDate.Month) - 1;

    /// <summary>
    /// ยอดค่าเสื่อมของ **หนึ่งงวด** — คืน 0 เมื่อไม่ควรคิด (หมดอายุใช้งานแล้ว ·
    /// NBV ถึงมูลค่าซากแล้ว · ที่ดิน/งานระหว่างก่อสร้าง · ยังไม่ถึงงวดแรก)
    /// </summary>
    /// <param name="nbv">มูลค่าตามบัญชีคงเหลือ ณ ต้นงวด</param>
    public static decimal AmountForPeriod(
        DepreciationMethod method,
        decimal cost,
        decimal salvage,
        int usefulLifeMonths,
        int monthIndex,
        decimal nbv)
    {
        if (method == DepreciationMethod.None) return 0m;
        if (usefulLifeMonths <= 0) return 0m;
        if (monthIndex < 0) return 0m;
        // หมดอายุใช้งานแล้ว — ต้องหยุด ไม่ใช่คิดต่อไปเรื่อย ๆ จนกว่าจะถึง salvage
        // (เส้น declining balance ที่ salvage = 0 ไม่มีวันถึง)
        if (monthIndex >= usefulLifeMonths) return 0m;
        var remainingDepreciable = nbv - salvage;
        if (remainingDepreciable <= 0m) return 0m;

        var amount = method switch
        {
            DepreciationMethod.StraightLine => (cost - salvage) / usefulLifeMonths,
            DepreciationMethod.DecliningBalance => nbv * (1.0m / usefulLifeMonths),
            DepreciationMethod.DoubleDecliningBalance => nbv * (2.0m / usefulLifeMonths),
            _ => throw new NotSupportedException(
                $"วิธีคิดค่าเสื่อมราคา '{method}' ไม่รองรับ"),
        };

        // Switch-to-straight-line (มาตรฐานสากล) — declining balance เป็น asymptotic
        // ไม่มีวันถึง salvage ภายในอายุใช้งาน เมื่อเส้นตรงจาก NBV คงเหลือ (เกลี่ย
        // เดือนที่เหลือ) สูงกว่า ให้สลับใช้เส้นตรง เพื่อให้ลงถึง salvage พอดีสิ้นอายุ
        if (method is DepreciationMethod.DecliningBalance
            or DepreciationMethod.DoubleDecliningBalance)
        {
            var slRemaining = remainingDepreciable / (usefulLifeMonths - monthIndex);
            if (slRemaining > amount) amount = slRemaining;
        }

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

        // งวดสุดท้ายรับเศษที่เหลือทั้งหมด ⇒ Σ ทุกงวด = cost − salvage พอดี
        // (ไม่งั้นยอดสะสมจะไม่ลงตัวกับราคาทุนเพราะเศษการปัดสะสม)
        if (monthIndex == usefulLifeMonths - 1 || amount > remainingDepreciable)
            amount = remainingDepreciable;

        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ตารางค่าเสื่อมทั้งอายุใช้งาน — ใช้สร้างแถวแผนตอนขึ้นทะเบียน</summary>
    public static List<Period> Build(
        DepreciationMethod method,
        decimal cost,
        decimal salvage,
        int usefulLifeMonths,
        DateTime purchaseDate)
    {
        var rows = new List<Period>();
        var nbv = cost;
        var accumulated = 0m;
        for (var i = 0; i < usefulLifeMonths; i++)
        {
            var amount = AmountForPeriod(method, cost, salvage, usefulLifeMonths, i, nbv);
            if (amount <= 0m) break;
            accumulated += amount;
            nbv -= amount;
            var periodDate = purchaseDate.AddMonths(i + 1);
            rows.Add(new Period(periodDate.Year, periodDate.Month, amount, accumulated, nbv));
        }
        return rows;
    }
}
