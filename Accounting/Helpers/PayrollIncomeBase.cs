namespace Accounting.Helpers;

/// <summary>ฐานเงินได้ของงวดก่อน ๆ และฐานเงินสะสม PVD — ตัวตัดสินเดียวของเครื่องคำนวณเงินเดือน (รอบ 200 ทีม R · D-09/D-10)
///
/// ═══ ที่มา ═══
/// (D-09) ฐานภาษีสะสม (YTD) ที่ใช้ประมาณการทั้งปีเอา <c>GrossIncome</c> ของงวดก่อน ๆ มารวม ⇒ สวัสดิการยกเว้นภาษีของเดือนก่อน
/// (ค่ารักษาพยาบาล ฯลฯ) พองฐาน ⇒ หัก ณ ที่จ่ายเกินขึ้นเรื่อย ๆ ในงวดหลัง ขณะที่งวดปัจจุบันใช้ <c>TaxableGross</c> อยู่แล้ว (ไม่สมมาตร)
/// (D-10) เงินสะสม PVD คิดจากเงินเดือนเต็มเดือนเสมอ (เข้างานกลางเดือนถูกหักทั้งเดือน) และไม่ปัดเศษ (สลิป 308.625)</summary>
public static class PayrollIncomeBase
{
    /// <summary>ฐานภาษีของงวดที่บันทึกแล้ว: <c>TaxableGross</c> เมื่อมี (> 0) · แถวเก่าก่อนมีคอลัมน์นี้ (0) ⇒ ย้อนใช้ <c>GrossIncome</c> (พฤติกรรมเดิม)</summary>
    public static decimal PriorTaxBase(decimal taxableGross, decimal grossIncome)
        => taxableGross > 0 ? taxableGross : grossIncome;

    /// <summary>เงินสะสม/เงินสมทบ PVD ของงวด = ค่าจ้างที่จ่ายจริงของงวด (เฉลี่ยตามวันทำงานแล้ว) × อัตรา · ปัด AwayFromZero 2 ตำแหน่ง</summary>
    public static decimal PvdContribution(decimal proratedBaseSalary, decimal ratePercent)
        => proratedBaseSalary <= 0 || ratePercent <= 0 ? 0m
            : Math.Round(proratedBaseSalary * ratePercent / 100m, 2, MidpointRounding.AwayFromZero);
}
