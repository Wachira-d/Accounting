using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>งวดที่สั่งคำนวณค่าเสื่อมได้ — <b>ตัวตัดสินเดียว</b> (รอบ 252 WP-13 ฝ่ายค้าน MED-3) · pure
/// <para>ที่มา: หน้าทะเบียนสินทรัพย์เปิดให้เลือกงวดได้ (แทน "เดือนนี้เท่านั้น") แต่เซิร์ฟเวอร์ไม่ตรวจงวด ⇒ เลือกเดือนอนาคต = ลงค่าเสื่อมล่วงหน้า
/// (JE ลงวันอนาคต · ยังไม่มีงวดบัญชี) · เดือน 13 = <c>new DateTime</c> ล้มเป็น 500 · วิธียอดลดลงลงย้อนหลังข้ามงวดที่ลงแล้ว = คิดจากมูลค่าสุทธิผิดลำดับ</para>
/// <para>กติกา (ทางปลอดภัย): เดือน 1–12 · ปี 2000–2100 · ไม่เกินเดือนปัจจุบันตามเวลาไทย · วิธียอดลดลง (DecliningBalance/DoubleDecliningBalance)
/// ห้ามลงงวดที่เก่ากว่างวดที่ลงไว้แล้ว (เส้นตรงลงย้อนหลังได้ — ยอดต่องวดไม่ขึ้นกับลำดับ)</para>
/// ผู้เรียก: <c>FixedAssetService.CalculateDepreciationAsync</c> (ทั้งปุ่มบนหน้าและงานรายเดือน)</summary>
public static class DepreciationPeriodPolicy
{
    private static readonly TimeSpan BangkokOffset = TimeSpan.FromHours(7);

    /// <summary>null = งวดใช้ได้ · ข้อความไทย = เหตุที่ปฏิเสธ</summary>
    public static string? PeriodProblem(int year, int month, DateTime nowUtc)
    {
        if (month < 1 || month > 12) return $"เดือนต้องอยู่ระหว่าง 1–12 (ได้ {month})";
        if (year < 2000 || year > 2100) return $"ปีไม่ถูกต้อง ({year})";
        var th = nowUtc + BangkokOffset;
        if (year * 12 + month > th.Year * 12 + th.Month)
            return $"งวด {month:D2}/{year} ยังไม่ถึง — คำนวณค่าเสื่อมได้ถึงเดือนปัจจุบัน ({th.Month:D2}/{th.Year}) เท่านั้น (ห้ามลงค่าเสื่อมล่วงหน้า)";
        return null;
    }

    /// <summary>วิธีที่ยอดต่องวดขึ้นกับมูลค่าสุทธิก่อนหน้า ⇒ ต้องลงตามลำดับ</summary>
    public static bool IsOrderSensitive(DepreciationMethod m)
        => m is DepreciationMethod.DecliningBalance or DepreciationMethod.DoubleDecliningBalance;

    /// <summary>งวด (y,m) เก่ากว่างวดที่ลงแล้วล่าสุด (ly,lm) หรือไม่</summary>
    public static bool IsBefore(int year, int month, int latestYear, int latestMonth)
        => year * 12 + month < latestYear * 12 + latestMonth;
}
