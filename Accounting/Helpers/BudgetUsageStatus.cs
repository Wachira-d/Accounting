namespace Accounting.Helpers;

/// <summary>
/// สถานะการใช้งบต่อบัญชี (งบประมาณเทียบยอดจริง) — <b>ตัวตัดสินเดียว</b> ของหน้า budget.html และไฟล์ส่งออก
/// (2026-10-15 ทีม X5 · เดิมเกณฑ์ "เกินงบ / ใกล้ถึงงบ &gt; 80%" อยู่ใน JS ของหน้าเท่านั้น ⇒ ไฟล์ส่งออกต้องเขียนซ้ำ — F2 ข้อ 4/5)
/// </summary>
public static class BudgetUsageStatus
{
    /// <summary>ใช้ไปเกินเปอร์เซ็นต์นี้ของงบ (แต่ยังไม่เกินงบ) = "ใกล้ถึงงบ"</summary>
    public const decimal NearLimitPercent = 80m;

    public const string Over = "เกินงบ";
    public const string Near = "ใกล้ถึงงบ";
    public const string Normal = "ปกติ";

    /// <summary>ใช้ไปกี่ % ของงบ (ปัด 2 ตำแหน่ง AwayFromZero) · งบ ≤ 0 = 0</summary>
    public static decimal UsedPercent(decimal budget, decimal actual)
        => budget > 0 ? Math.Round(actual / budget * 100m, 2, MidpointRounding.AwayFromZero) : 0m;

    /// <summary>ยอดจริง &gt; งบ = เกินงบ (รวมงบ 0 ที่มียอดจริง) · ใช้ไป &gt; 80% = ใกล้ถึงงบ · อื่น ๆ = ปกติ</summary>
    public static string Label(decimal budget, decimal actual)
    {
        if (actual > budget) return Over;
        if (budget > 0 && actual / budget * 100m > NearLimitPercent) return Near;
        return Normal;
    }
}
