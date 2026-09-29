namespace Accounting.Helpers;

/// <summary>แบ่งกองทิปให้พนักงาน + ภาษีหัก ณ ที่จ่ายของทิป — ตัวตัดสินเดียว (รอบ 200 ทีม R · D-05)
///
/// ═══ ที่มา ═══
/// <c>TipPayoutService</c> (1) พิมพ์อัตรา 3% และเกณฑ์ 1,000 เอง (ตารางชุดที่สองของ <see cref="ThaiWhtRateTable"/>)
/// (2) ปัดส่วนแบ่งรายคนด้วย banker's rounding ต่อคน (3) ยอมสัดส่วนรวม 100.01% ⇒ Σ ส่วนแบ่ง ≠ กองทิป ⇒ JE
/// "ไม่สมดุล: Dr 10,000.00 ≠ Cr 10,001.00" จ่ายไม่ได้โดยไม่มีอะไรบอกทางแก้
/// ⇒ แบ่งเป็นสตางค์แบบเศษเหลือมากสุด (largest remainder) บนสัดส่วนที่ normalize แล้ว ⇒ Σ = กองทิปเสมอ</summary>
public static class TipShareAllocation
{
    /// <summary>ประเภทเงินได้ของทิปที่ระบบใช้วันนี้ (ม.40(2) ค่าบริการ · ภ.ง.ด.3) — ห้ามพิมพ์อัตราเองที่อื่น</summary>
    public const string IncomeTypeCode = "2";

    /// <summary>อัตรา WHT (%) ของทิปจ่ายบุคคล — อ่านจาก <see cref="ThaiWhtRateTable"/> ตัวเดียว</summary>
    public static decimal WhtRatePercent => ThaiWhtRateTable.RateFor(IncomeTypeCode, payeeIsJuristic: false) ?? 0m;

    /// <summary>แบ่งกองทิปตามสัดส่วน — Σ ผลลัพธ์ = <paramref name="totalTip"/> พอดี (ปัดเป็นสตางค์) · ลำดับผลลัพธ์ = ลำดับอินพุต ·
    /// เศษสตางค์ที่เหลือแจกให้คนที่เศษมากสุดก่อน (เท่ากัน = เรียงตาม StaffId ⇒ ไม่ขึ้นกับลำดับ)</summary>
    public static IReadOnlyList<(Guid StaffId, decimal Gross)> Split(decimal totalTip,
        IReadOnlyList<KeyValuePair<Guid, decimal>> sharePercent)
    {
        if (sharePercent.Count == 0) return Array.Empty<(Guid, decimal)>();
        var totalShare = sharePercent.Sum(s => s.Value);
        if (totalShare <= 0) throw new BusinessRuleException("สัดส่วนแจกจ่ายทิปรวมต้องมากกว่า 0%");
        var totalSatang = (long)Math.Round(totalTip * 100m, 0, MidpointRounding.AwayFromZero);
        var raw = sharePercent.Select((s, i) =>
        {
            var exact = totalSatang * s.Value / totalShare;
            var floor = (long)Math.Floor(exact);
            return (Index: i, s.Key, Floor: floor, Frac: exact - floor);
        }).ToList();
        var left = totalSatang - raw.Sum(r => r.Floor);
        var bonus = raw.OrderByDescending(r => r.Frac).ThenBy(r => r.Key)
            .Take((int)Math.Max(0, left)).Select(r => r.Index).ToHashSet();
        return raw.Select(r => (r.Key, (r.Floor + (bonus.Contains(r.Index) ? 1 : 0)) / 100m)).ToList();
    }

    /// <summary>ภาษีหัก ณ ที่จ่ายของส่วนแบ่งหนึ่งคน — ด่าน ฿1,000 ของ <see cref="ThaiWhtRateTable.ShouldWithhold"/> ·
    /// ปัด AwayFromZero · <paramref name="alreadyPaidThisYear"/> = ทิปที่จ่ายคนเดิมไปแล้วในปีภาษี (สะสมต่อผู้รับ)</summary>
    public static decimal Withholding(decimal gross, decimal alreadyPaidThisYear = 0m)
        => gross > 0 && ThaiWhtRateTable.ShouldWithhold(gross, alreadyPaidThisYear)
            ? Math.Round(gross * WhtRatePercent / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
}
