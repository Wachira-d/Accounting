using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>กติกาค่าตอบแทนของผู้ให้บริการหนึ่งคน (ตรึงจาก <c>SiteBookingResource</c>)</summary>
public sealed record InstructorPaySettings(InstructorPayMode Mode, decimal Rate, decimal MinimumPerClass);

/// <summary>งานหนึ่งรายการของผู้ให้บริการ — <paramref name="Value"/> = มูลค่าการจองตาม <see cref="BookingServiceValue"/> · <paramref name="SharePercent"/> = % งานของคนนี้
/// (รอบ 207 <c>SiteBookingAssignment</c> · ไม่มีการมอบหมายหลายคน = 100) · <paramref name="Heads"/> = จำนวนคนของการจอง (≥ 1)</summary>
public sealed record InstructorWorkBooking(Guid BookingId, Guid? ServiceId, DateTime Date, string StartTime, BookingStatus Status, int Heads,
    decimal Value, decimal SharePercent, bool BilledThroughPos);

public sealed record InstructorPayLine(Guid ResourceId, string Name, Guid? EmployeeId, InstructorPayMode Mode, decimal Rate, decimal MinimumPerClass,
    int Classes, int Heads, decimal RevenueShare, decimal Pay, string Basis);

/// <summary>
/// รอบ 209 · P5 — <b>ตัวตั้งเดียว</b>ของ "มูลค่าการจอง" ที่ใช้ทั้งรายงานผู้ให้บริการและฐานคอมมิชชัน (R-209-C6 · D8 · คำตัดสินข้อ 168 ฏ) — pure
/// <para><b>การจองที่จ่ายด้วยบัตรคอร์ส</b> ยอดการจอง (<c>TotalAmount</c>) = 0 (ไม่เก็บเงิน — <c>CreditBookingLink</c>) ⇒ มูลค่าจริง = <b>รายได้ที่รับรู้จากบัตร</b>
/// (เครดิต × ราคาต่อเครดิตของบัตร · สุทธิก่อน VAT — <c>CreditClassBooking.RecognizedAmount</c>) · สมาชิกไม่จำกัด = 0 (รับรู้ตามวัน ไม่ผูกการเข้าเรียน) ·
/// การจองปกติ = ราคาการจอง (รายงาน: รวม VAT ตามหน้าเว็บ · คอมมิชชัน: ถอด VAT ⇒ ฐานเดียวกับ <c>PosStaffAttribution</c> ที่เป็นยอดก่อน VAT · D-Q4)</para>
/// </summary>
public static class BookingServiceValue
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>มูลค่าในรายงานผู้ให้บริการ — บัตร ⇒ รายได้ที่รับรู้ · ปกติ ⇒ ราคาการจอง</summary>
    public static decimal ForReport(decimal totalAmount, decimal? creditRecognized) => creditRecognized ?? totalAmount;

    /// <summary>ถอด VAT จากราคารวม VAT (อัตรา ≤ 0 = ไม่จด VAT/ยกเว้น ⇒ คืนเดิม)</summary>
    private static decimal ExVat(decimal totalInclVat, decimal vatRatePercent)
        => vatRatePercent <= 0m ? totalInclVat : R2(totalInclVat * 100m / (100m + vatRatePercent));

    /// <summary>มูลค่าสำหรับฐานคอมมิชชัน (ก่อน VAT) — บัตร ⇒ รายได้ที่รับรู้ (สุทธิอยู่แล้ว) · ปกติ ⇒ ราคาถอด VAT</summary>
    public static decimal ForCommission(decimal totalAmount, decimal? creditRecognized, decimal vatRatePercent)
        => creditRecognized ?? ExVat(totalAmount, vatRatePercent);

    /// <summary>ดีดซ้ำกับคอมมิชชัน POS (D8): การจองที่ปิดบิลที่ POS แล้ว (<c>PosOrder.SourceBookingId</c> สถานะเสร็จ/คืน) นับในฐาน "ยอดที่ช่างทำบน POS" แล้ว ⇒ ไม่นับซ้ำในฐานการจอง</summary>
    private static bool CountsInBookingBasis(BookingStatus status, bool billedThroughPos) => status == BookingStatus.Completed && !billedThroughPos;

    /// <summary>ฐานคอมมิชชัน "บริการจากการจอง" = Σ round(มูลค่า × % งาน) ของการจองที่เสร็จและไม่ได้ปิดบิลที่ POS</summary>
    public static decimal BookingSalesBasis(IEnumerable<InstructorWorkBooking> work)
        => work.Where(w => CountsInBookingBasis(w.Status, w.BilledThroughPos)).Sum(w => R2(w.Value * w.SharePercent / 100m));
}

/// <summary>
/// รอบ 209 · P5 (R-209-C6 · คำตัดสินข้อ 168 ฏ) — <b>ตัวคิดเดียว</b>ของค่าตอบแทนผู้สอน/ผู้ให้บริการ — pure
/// <para><b>คลาส</b> = รอบเดียวกัน (บริการ + วัน + เวลาเริ่ม) ที่ "สอนจริง" (มีการจองเสร็จหรือไม่มา ≥ 1 — ยกเลิกทั้งรอบ/ยังไม่ถึงไม่นับ) ·
/// % งานของคนนี้ในคลาส = ค่ามากสุดของการจองในรอบนั้น</para>
/// <list type="bullet">
/// <item><b>ต่อคลาส</b>: round(อัตรา × % งาน) ต่อคลาส</item>
/// <item><b>ต่อหัว</b>: round(max(คนที่มา × อัตรา, ขั้นต่ำ) × % งาน) ต่อคลาส — มา 0 คนแต่คลาสเกิด (ทุกคนไม่มา) ⇒ ได้ขั้นต่ำ</item>
/// <item><b>% รายได้</b>: round(Σ round(มูลค่า × % งาน) × อัตรา ÷ 100) ของการจองที่เสร็จ</item>
/// </list>
/// <para>นับทุกการจอง (รวมที่ปิดบิลที่ POS) — เป็นค่าตอบแทนตามงาน ไม่ใช่คอมมิชชันยอดขาย · ถ้าเจ้าของตั้งทั้งแผน "ยอดที่ช่างทำบน POS" และ "% รายได้" ให้คนเดียวกัน = ตั้งใจจ่ายสองชั้น (บอกบนหน้าตั้งค่า)</para>
/// </summary>
public static class InstructorPayRule
{
    public const string RuleCode = "CMS-INSTRUCTOR-PAY";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static bool Held(BookingStatus s) => s is BookingStatus.Completed or BookingStatus.NoShow;

    /// <summary>ตรวจค่าตั้ง — อัตราไม่ติดลบ · % ไม่เกิน 100 · ขั้นต่ำเฉพาะต่อหัว · null = ใช้ได้</summary>
    public static string? SettingsProblem(InstructorPayMode mode, decimal rate, decimal minimum)
    {
        if (rate < 0m || minimum < 0m) return "อัตรา/ขั้นต่ำต้องไม่ติดลบ";
        if (mode == InstructorPayMode.PercentOfRevenue && rate > 100m) return "เปอร์เซ็นต์ของรายได้ต้องไม่เกิน 100";
        if (mode != InstructorPayMode.None && rate == 0m && minimum == 0m) return "ระบุอัตราค่าตอบแทน (หรือเลือก \"ไม่คิดค่าตอบแทน\")";
        if (mode != InstructorPayMode.PerHead && minimum > 0m) return "ขั้นต่ำต่อคลาสใช้กับแบบ \"ต่อหัว\" เท่านั้น";
        return null;
    }

    public static InstructorPayLine Compute(Guid resourceId, string name, Guid? employeeId, InstructorPaySettings rule, IReadOnlyList<InstructorWorkBooking> work)
    {
        var classes = work.Where(w => Held(w.Status))
            .GroupBy(w => (w.ServiceId, w.Date.Date, w.StartTime))
            .Select(g => new
            {
                Share = g.Max(w => w.SharePercent) / 100m,
                Heads = g.Where(w => w.Status == BookingStatus.Completed).Sum(w => Math.Max(1, w.Heads)),
            })
            .ToList();
        var revenueShare = work.Where(w => w.Status == BookingStatus.Completed).Sum(w => R2(w.Value * w.SharePercent / 100m));
        var heads = classes.Sum(c => c.Heads);
        decimal pay;
        string basis;
        switch (rule.Mode)
        {
            case InstructorPayMode.PerClass:
                pay = classes.Sum(c => R2(rule.Rate * c.Share));
                basis = $"{classes.Count} คลาส × {rule.Rate:N2}";
                break;
            case InstructorPayMode.PerHead:
                pay = classes.Sum(c => R2(Math.Max(c.Heads * rule.Rate, rule.MinimumPerClass) * c.Share));
                basis = $"{heads} คน × {rule.Rate:N2}" + (rule.MinimumPerClass > 0m ? $" (ขั้นต่ำ {rule.MinimumPerClass:N2}/คลาส)" : "");
                break;
            case InstructorPayMode.PercentOfRevenue:
                pay = R2(revenueShare * rule.Rate / 100m);
                basis = $"{rule.Rate:0.##}% ของ {revenueShare:N2}";
                break;
            default:
                pay = 0m;
                basis = "ไม่คิดค่าตอบแทน";
                break;
        }
        return new InstructorPayLine(resourceId, name, employeeId, rule.Mode, rule.Rate, rule.MinimumPerClass, classes.Count, heads, revenueShare, pay, basis);
    }
}
