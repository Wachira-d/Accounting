using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 209 · P5 (R-209-C5 · คำตัดสินข้อ 168 ฎ) — สมาชิกไม่จำกัดครั้งตามช่วงเวลา: รับรู้รายได้เส้นตรงรายวัน (AwayFromZero · วันสุดท้ายรับส่วนที่เหลือ · Σ = ราคา) ·
/// พักบัตรเลื่อนวันหมดอายุและไม่รับรู้ช่วงพัก · ขยายอายุตั้งฐานใหม่ · โอนบัตรยอดเท่าเดิม · แจ้งเตือนใกล้หมดอายุ ·
/// ทิศตรงข้าม: บัตรนับครั้ง/ชั่วโมงได้ตัวเลขเดิมทุกสตางค์
/// </summary>
public class CourseCreditMembershipTests
{
    private static readonly DateTime Nov1 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime D(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CourseCreditMath.FreezeSpan[] NoFreeze = Array.Empty<CourseCreditMath.FreezeSpan>();

    private static CourseCreditMath.PeriodFacts Facts(decimal contract, int days, DateTime start, decimal recognized = 0m, DateTime? through = null,
        IReadOnlyCollection<CourseCreditMath.FreezeSpan>? freezes = null, decimal baseRec = 0m, int baseDays = 0)
        => new(contract, days, start, baseRec, baseDays, recognized, through, freezes ?? NoFreeze);

    /// <summary>รันแผนทีละวัน (เหมือนงานรายคืน) แล้วคืนยอดต่อวัน</summary>
    private static List<decimal> Daily(decimal contract, int days, DateTime start, IReadOnlyCollection<CourseCreditMath.FreezeSpan>? freezes = null, int totalCalendarDays = 0)
    {
        var amounts = new List<decimal>();
        decimal rec = 0m; DateTime? through = null;
        for (var i = 0; i < (totalCalendarDays > 0 ? totalCalendarDays : days); i++)
        {
            var plan = CourseCreditMath.PeriodRecognitionPlan(Facts(contract, days, start, rec, through, freezes), start.AddDays(i));
            var a = plan.Sum(x => x.Amount);
            amounts.Add(a); rec += a; through = start.AddDays(i);
        }
        return amounts;
    }

    [Fact]
    public void สามพันบาท30วัน_วันละ100_รวมเท่าราคา()
    {
        var a = Daily(3000m, 30, Nov1);
        Assert.All(a, x => Assert.Equal(100m, x));
        Assert.Equal(3000m, a.Sum());
        Assert.Equal(100m, CourseCreditMath.PeriodDailyAmount(Facts(3000m, 30, Nov1)));
    }

    [Fact]
    public void พันบาท30วัน_ปัดรายวัน_วันสุดท้ายรับเศษ()
    {
        var a = Daily(1000m, 30, Nov1);
        Assert.All(a.Take(29), x => Assert.Equal(33.33m, x));
        Assert.Equal(33.43m, a[29]);
        Assert.Equal(1000m, a.Sum());
    }

    [Fact]
    public void ปัดแบบAwayFromZero_ไม่ใช่BankersRounding()
    {
        // 0.25 ÷ 2 = 0.125 ⇒ AwayFromZero 0.13 (banker's จะได้ 0.12) · วันสุดท้ายรับส่วนที่เหลือ 0.12 ⇒ Σ = 0.25
        var a = Daily(0.25m, 2, Nov1);
        Assert.Equal(new[] { 0.13m, 0.12m }, a);
    }

    [Fact]
    public void งานไม่ได้รันข้ามเดือน_แยกJEต่อเดือนปฏิทิน()
    {
        var start = D(2026, 11, 16);
        var plan = CourseCreditMath.PeriodRecognitionPlan(Facts(3000m, 30, start), D(2026, 12, 15));
        Assert.Equal(2, plan.Count);
        Assert.Equal(D(2026, 11, 30), plan[0].PostingDate);
        Assert.Equal(1500m, plan[0].Amount);     // 15 วันของ พ.ย.
        Assert.Equal(D(2026, 12, 15), plan[1].PostingDate);
        Assert.Equal(1500m, plan[1].Amount);
        Assert.Equal(3000m, plan.Sum(x => x.Amount));
    }

    [Fact]
    public void รันซ้ำวันเดิม_แผนว่าง_และก่อนวันเริ่ม_แผนว่าง()
    {
        Assert.Empty(CourseCreditMath.PeriodRecognitionPlan(Facts(3000m, 30, Nov1, 500m, D(2026, 11, 5)), D(2026, 11, 5)));
        Assert.Empty(CourseCreditMath.PeriodRecognitionPlan(Facts(3000m, 30, Nov1), D(2026, 10, 31)));
    }

    [Fact]
    public void เลยวันใช้งานครบ_รับรู้ไม่เกินสัญญา()
    {
        var plan = CourseCreditMath.PeriodRecognitionPlan(Facts(3000m, 30, Nov1), D(2027, 1, 31));
        Assert.Equal(3000m, plan.Sum(x => x.Amount));
    }

    [Fact]
    public void พัก7วัน_วันหมดอายุเลื่อน7วัน_ไม่รับรู้ช่วงพัก()
    {
        var freeze = new[] { new CourseCreditMath.FreezeSpan(D(2026, 11, 10), D(2026, 11, 16)) };
        Assert.Equal(7, freeze[0].Days);
        Assert.Equal(D(2026, 11, 30), CourseCreditMath.UnlimitedExpiresOn(Nov1, 30, 0));
        Assert.Equal(D(2026, 12, 7), CourseCreditMath.UnlimitedExpiresOn(Nov1, 30, 7));
        var a = Daily(3000m, 30, Nov1, freeze, totalCalendarDays: 37);
        Assert.Equal(900m, a.Take(9).Sum());          // 1–9 พ.ย.
        Assert.All(a.Skip(9).Take(7), x => Assert.Equal(0m, x));   // 10–16 พ.ย. พัก
        Assert.Equal(100m, a[16]);                     // 17 พ.ย. กลับมารับรู้
        Assert.Equal(3000m, a.Sum());                  // ครบสัญญาที่ 7 ธ.ค.
        Assert.Equal(9, CourseCreditMath.ActiveDaysThrough(Nov1, D(2026, 11, 16), freeze));
    }

    [Fact]
    public void ขยายอายุ_ตั้งฐานใหม่_ส่วนที่เหลือกระจายบนวันที่เหลือ()
    {
        // รับรู้แล้ว 15 วัน = 1,500 · ขยาย 15 วัน ⇒ วันใช้งาน 45 · เหลือ 1,500 บน 30 วัน = วันละ 50
        var f = Facts(3000m, 45, Nov1, 1500m, D(2026, 11, 15), baseRec: 1500m, baseDays: 15);
        Assert.Equal(50m, CourseCreditMath.PeriodDailyAmount(f));
        Assert.Equal(50m, CourseCreditMath.PeriodRecognitionPlan(f, D(2026, 11, 16)).Sum(x => x.Amount));
        Assert.Equal(1500m, CourseCreditMath.PeriodRecognitionPlan(f, D(2026, 12, 15)).Sum(x => x.Amount));   // ครบ 45 วัน ⇒ Σ = สัญญา
        var plan = CourseCreditMath.PeriodRecognitionPlan(f, D(2026, 11, 20));
        Assert.Equal(250m, plan.Sum(x => x.Amount));
    }

    [Fact]
    public void ทิศตรงข้าม_บัตรนับครั้งรับรู้เดิมทุกสตางค์_สมาชิกไม่จำกัดไม่รับรู้ต่อการเข้าเรียน()
    {
        decimal used = 0m, rec = 0m; var amounts = new List<decimal>();
        for (var i = 0; i < 10; i++)
        {
            var a = CourseCreditMath.RecognizeOnUseFor(CreditPackageKind.Credits, 4205.61m, 10m, used, rec, 1m);
            Assert.Equal(CourseCreditMath.RecognizeOnUse(4205.61m, 10m, used, rec, 1m), a);
            amounts.Add(a); used += 1m; rec += a;
        }
        Assert.All(amounts.Take(9), x => Assert.Equal(420.56m, x));
        Assert.Equal(420.57m, amounts[9]);
        Assert.Equal(4205.61m, amounts.Sum());
        Assert.Equal(0m, CourseCreditMath.RecognizeOnUseFor(CreditPackageKind.UnlimitedPeriod, 3000m, 0m, 0m, 0m, 1m));
        Assert.False(CourseCreditMath.IsUnlimited(CreditPackageKind.Credits));
    }

    private static CourseCreditMath.UnlimitedFacts U(int perDay = 1, int maxActive = 0, IReadOnlyCollection<CourseCreditMath.FreezeSpan>? freezes = null,
        CreditPassStatus status = CreditPassStatus.Active)
        => new(status, D(2026, 11, 30), Nov1, perDay, maxActive, freezes ?? NoFreeze, CreditClassFormat.Any, Array.Empty<Guid>());

    private static readonly DateTime Now = new(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc);
    private static DateTime At(int day, int hourThai) => new DateTime(2026, 11, day, hourThai, 0, 0, DateTimeKind.Utc).AddHours(-7);

    [Fact]
    public void จองสมาชิกไม่จำกัด_เพดานต่อวันตั้งต้น1_วันอื่นจองได้()
    {
        Assert.Null(CourseCreditMath.CannotBookUnlimitedReason(U(), At(5, 18), 1, 0, 0, null, CreditClassFormat.Group, Now));
        Assert.Contains("ครบ 1 ครั้ง", CourseCreditMath.CannotBookUnlimitedReason(U(), At(5, 19), 1, 1, 1, null, CreditClassFormat.Group, Now));
        Assert.Null(CourseCreditMath.CannotBookUnlimitedReason(U(perDay: 2), At(5, 19), 1, 1, 1, null, CreditClassFormat.Group, Now));
        Assert.Contains("จองค้างครบ 3", CourseCreditMath.CannotBookUnlimitedReason(U(perDay: 2, maxActive: 3), At(6, 9), 1, 0, 3, null, CreditClassFormat.Group, Now));
    }

    [Fact]
    public void จองสมาชิกไม่จำกัด_ช่วงพัก_หลายที่นั่ง_หลังหมดอายุ_ก่อนวันเริ่ม_ถูกกัน()
    {
        var fz = new[] { new CourseCreditMath.FreezeSpan(D(2026, 11, 10), D(2026, 11, 16)) };
        Assert.Contains("บัตรพักอยู่", CourseCreditMath.CannotBookUnlimitedReason(U(freezes: fz), At(12, 10), 1, 0, 0, null, CreditClassFormat.Any, Now));
        Assert.Null(CourseCreditMath.CannotBookUnlimitedReason(U(freezes: fz), At(17, 10), 1, 0, 0, null, CreditClassFormat.Any, Now));
        Assert.Contains("ครั้งละ 1 ที่", CourseCreditMath.CannotBookUnlimitedReason(U(), At(5, 10), 2, 0, 0, null, CreditClassFormat.Any, Now));
        Assert.Contains("หลังวันหมดอายุ", CourseCreditMath.CannotBookUnlimitedReason(U(), new DateTime(2026, 12, 1, 3, 0, 0, DateTimeKind.Utc), 1, 0, 0, null, CreditClassFormat.Any, Now));
        Assert.Contains("ก่อนวันเริ่ม", CourseCreditMath.CannotBookUnlimitedReason(U(), new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc), 1, 0, 0, null, CreditClassFormat.Any, Now));
        Assert.Contains("สถานะ", CourseCreditMath.CannotBookUnlimitedReason(U(status: CreditPassStatus.Transferred), At(5, 10), 1, 0, 0, null, CreditClassFormat.Any, Now));
        // บัตรครั้งที่พักไว้ใช้ตัวตรวจช่วงพักตัวเดียวกัน
        Assert.NotNull(CourseCreditMath.FrozenOnReason(fz, At(10, 6)));
        Assert.Null(CourseCreditMath.FrozenOnReason(fz, At(9, 23)));
    }

    [Fact]
    public void พักบัตร_ด่านครบทุกข้อ()
    {
        var today = D(2026, 11, 5); var exp = D(2026, 11, 30);
        string? P(DateTime f, DateTime t, int used = 0, int max = 14, DateTime? rec = null, int open = 0, string? reason = "ป่วย",
            IReadOnlyCollection<CourseCreditMath.FreezeSpan>? ex = null, CreditPassStatus st = CreditPassStatus.Active, DateTime? expires = null)
            => CourseCreditMath.FreezeProblem(st, f, t, today, rec, expires ?? exp, used, max, ex ?? NoFreeze, open, reason);
        Assert.Null(P(D(2026, 11, 5), D(2026, 11, 11)));
        Assert.Contains("ไม่อนุญาต", P(D(2026, 11, 5), D(2026, 11, 11), max: 0));
        Assert.Contains("ย้อนหลัง", P(D(2026, 11, 4), D(2026, 11, 11)));
        Assert.Contains("ไม่เกิน 14 วัน", P(D(2026, 11, 5), D(2026, 11, 11), used: 10));
        Assert.Contains("ซ้อน", P(D(2026, 11, 6), D(2026, 11, 8), ex: new[] { new CourseCreditMath.FreezeSpan(D(2026, 11, 7), D(2026, 11, 9)) }));
        Assert.Contains("การจองค้าง 2", P(D(2026, 11, 6), D(2026, 11, 8), open: 2));
        Assert.Contains("รับรู้รายได้ถึง", P(D(2026, 11, 5), D(2026, 11, 8), rec: D(2026, 11, 5)));
        Assert.Contains("เหตุผล", P(D(2026, 11, 5), D(2026, 11, 8), reason: " "));
        Assert.Contains("หลังวันหมดอายุ", P(D(2026, 12, 1), D(2026, 12, 3)));
        Assert.Contains("สถานะ", P(D(2026, 11, 5), D(2026, 11, 8), st: CreditPassStatus.Expired));
        Assert.Contains("1–3650", CourseCreditMath.ExtendProblem(CreditPassStatus.Active, exp, 0, "x"));
        Assert.Null(CourseCreditMath.ExtendProblem(CreditPassStatus.Active, exp, 7, "ลูกค้าป่วย"));
        Assert.NotNull(CourseCreditMath.ExtendProblem(CreditPassStatus.Active, null, 7, "x"));
        Assert.NotNull(CourseCreditMath.ExtendProblem(CreditPassStatus.Active, exp, 7, ""));
    }

    [Fact]
    public void โอนบัตรครั้ง_ยอดที่เหลือเท่าเดิม()
    {
        // 10 ครั้ง 4,205.61 ใช้ 4 รับรู้ 1,682.24 ⇒ บัตรใหม่ 6 ครั้ง 2,523.37 (ราคาต่อเครดิตคงเหลือเท่าเดิม) · วันหมดอายุเดิม
        var plan = CourseCreditMath.PlanTransfer(CreditPackageKind.Credits, CreditPassStatus.Active, false, 4205.61m, 1682.24m, 10m, 4m, 0, false,
            D(2027, 1, 31), 0, 0, D(2026, 11, 5));
        Assert.Null(plan.Problem);
        Assert.Equal(2523.37m, plan.Contract);
        Assert.Equal(6m, plan.CreditsTotal);
        Assert.Equal(D(2027, 1, 31), plan.ExpiresOn);
        Assert.Equal(4205.61m, 1682.24m + plan.Contract);
    }

    [Fact]
    public void โอนสมาชิกไม่จำกัด_วันใช้งานที่เหลือ_เริ่มวันนี้()
    {
        var plan = CourseCreditMath.PlanTransfer(CreditPackageKind.UnlimitedPeriod, CreditPassStatus.Active, false, 3000m, 1000m, 0m, 3m, 0, false,
            D(2026, 11, 30), 30, 10, D(2026, 11, 11));
        Assert.Null(plan.Problem);
        Assert.Equal(2000m, plan.Contract);
        Assert.Equal(20, plan.PeriodDays);
        Assert.Equal(D(2026, 11, 30), plan.ExpiresOn);   // วันหมดอายุเท่าเดิม
    }

    [Fact]
    public void โอนบัตร_ถูกกันเมื่อจองค้าง_คนเดิม_พักค้าง_ไม่มียอด()
    {
        CourseCreditMath.TransferPlan P(bool same = false, int open = 0, bool freeze = false, decimal rec = 0m, CreditPassStatus st = CreditPassStatus.Active)
            => CourseCreditMath.PlanTransfer(CreditPackageKind.Credits, st, same, 1000m, rec, 10m, 0m, open, freeze, D(2027, 1, 1), 0, 0, D(2026, 11, 5));
        Assert.Contains("การจองค้าง", P(open: 1).Problem);
        Assert.Contains("ลูกค้าคนอื่น", P(same: true).Problem);
        Assert.Contains("ช่วงพัก", P(freeze: true).Problem);
        Assert.Contains("ไม่มียอด", P(rec: 1000m).Problem);
        Assert.Contains("สถานะ", P(st: CreditPassStatus.Exhausted).Problem);
    }

    [Fact]
    public void แจ้งเตือนใกล้หมดอายุ_ถึงกำหนด_ส่งแล้วไม่ส่งซ้ำ_ล้มแล้วเว้นระยะ()
    {
        var exp = D(2026, 11, 30); var now = new DateTime(2026, 11, 25, 2, 0, 0, DateTimeKind.Utc);
        Assert.True(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 23), 7, null, null, now));
        Assert.False(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 22), 7, null, null, now));       // ก่อนช่วงแจ้ง
        Assert.False(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 25), 0, null, null, now));       // แพ็กเกจปิดแจ้งเตือน (เดิม)
        Assert.False(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 25), 7, now, null, now));        // ส่งถึงแล้ว
        Assert.False(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 25), 7, null, now.AddHours(-1), now));  // เพิ่งล้ม
        Assert.True(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 11, 25), 7, null, now.AddHours(-7), now));
        Assert.False(CourseCreditMath.ExpiryReminderDue(exp, D(2026, 12, 1), 7, null, null, now));        // หมดอายุแล้ว
        Assert.Contains("CP-202611-0001", CourseCreditMath.ExpiryReminderText("สมชาย", "CP-202611-0001", "โยคะ 10 ครั้ง", exp, "3 ครั้ง", "สตูดิโอ"));
    }

    [Fact]
    public void บัตรที่โอนแล้ว_ยกเลิกหรือลดหนี้ใบขายเดิมถูกบล็อก_บัตรปกติไม่ถูกแตะ()
    {
        var transferred = new CreditPassDocumentRule.PassFacts("CP-1", CreditPassStatus.Transferred, 0m, 0m, 1000m, 0m, 0m, 10m);
        Assert.Equal(CreditPassDocumentRule.DocAction.Block, CreditPassDocumentRule.OnVoidSale(transferred, "TIV-1").Action);
        Assert.Equal(CreditPassDocumentRule.DocAction.Block, CreditPassDocumentRule.OnCreditNote(transferred, "TIV-1", 1000m, true).Action);
        var untouched = transferred with { Status = CreditPassStatus.Active };
        Assert.Equal(CreditPassDocumentRule.DocAction.CancelPass, CreditPassDocumentRule.OnVoidSale(untouched, "TIV-1").Action);
        Assert.Equal(CreditPassDocumentRule.DocAction.CancelPass, CreditPassDocumentRule.OnCreditNote(untouched, "TIV-1", 1000m, true).Action);
    }

    [Fact]
    public void สมาชิกไม่จำกัด_คืนเงินบางส่วนไม่รองรับ_คืนทั้งก้อนได้()
    {
        // บัตรสมาชิกเก็บ CreditsTotal = 0 ⇒ ตัวตัดสินคืนเงินเดิมตอบ "ไม่รองรับคืนบางส่วน" · คืนส่วนที่เหลือทั้งหมด = ปิดบัตร
        var pass = new CreditPassDocumentRule.PassFacts("CP-2", CreditPassStatus.Active, 3m, 0m, 3000m, 1000m, 0m, 0m);
        Assert.Contains("ไม่รองรับการคืนเงินบางส่วน", CreditPassDocumentRule.RefundPreview(pass, 500m).Problem);
        var full = CreditPassDocumentRule.RefundPreview(pass, 2000m);
        Assert.Null(full.Problem);
        Assert.True(full.ClosesPass);
    }
}
