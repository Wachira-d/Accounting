using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวคำนวณคอร์ส/เครดิต (ทีม L · <c>Helpers/CourseCreditMath</c>) — TFRS for NPAEs บทที่ 6:
/// ราคาต่อเครดิตปัด AwayFromZero · เครดิตสุดท้ายรับเศษที่เหลือทั้งหมด (ไม่มีสตางค์ค้างในหนี้สิน) · หมดอายุ = breakage ส่วนคงเหลือ ·
/// คืนเครดิตไม่กระทบรายได้ · ยกเลิกช้า = ริบ
/// </summary>
public class CourseCreditMathTests
{
    /// <summary>ใช้ทีละ <paramref name="step"/> เครดิตจนครบ แล้วคืนยอดรับรู้แต่ละครั้ง</summary>
    private static List<decimal> UseAll(decimal contract, decimal total, decimal step)
    {
        var amounts = new List<decimal>();
        decimal used = 0m, recognized = 0m;
        while (used < total)
        {
            var now = Math.Min(step, total - used);
            var a = CourseCreditMath.RecognizeOnUse(contract, total, used, recognized, now);
            amounts.Add(a);
            used += now; recognized += a;
        }
        return amounts;
    }

    [Fact]
    public void สามครั้ง_1000บาท_ครั้งสุดท้ายรับเศษ()
    {
        Assert.Equal(333.33m, CourseCreditMath.PricePerCredit(1000m, 3m));
        var a = UseAll(1000m, 3m, 1m);
        Assert.Equal(new[] { 333.33m, 333.33m, 333.34m }, a);
        Assert.Equal(1000m, a.Sum());
    }

    [Fact]
    public void สิบครั้ง_ราคารวมVAT4500_สุทธิ4205_61_ไม่มีเศษค้าง()
    {
        // 4,500 รวม VAT ⇒ สุทธิ 4,205.61 (ยอดที่ใบขายลง 21713)
        var a = UseAll(4205.61m, 10m, 1m);
        Assert.All(a.Take(9), x => Assert.Equal(420.56m, x));
        Assert.Equal(420.57m, a[9]);
        Assert.Equal(4205.61m, a.Sum());
    }

    [Fact]
    public void ราคาต่อเครดิตปัดขึ้น_สะสมไม่เกินสัญญา_และไม่ติดลบ()
    {
        // 0.15 / 10 = 0.015 ⇒ ปัด 0.02 · 7 ครั้ง = 0.14 · ครั้งที่ 8 เหลือ 0.01 · ครั้ง 9–10 = 0
        var a = UseAll(0.15m, 10m, 1m);
        Assert.Equal(new[] { 0.02m, 0.02m, 0.02m, 0.02m, 0.02m, 0.02m, 0.02m, 0.01m, 0m, 0m }, a);
        Assert.Equal(0.15m, a.Sum());
        Assert.All(a, x => Assert.True(x >= 0m));
    }

    [Fact]
    public void ปัดครึ่ง_AwayFromZero_ไม่ใช่BankersRounding()
    {
        // 0.25 / 2 = 0.125 ⇒ AwayFromZero = 0.13 (banker's = 0.12)
        Assert.Equal(0.13m, CourseCreditMath.PricePerCredit(0.25m, 2m));
        var a = UseAll(0.25m, 2m, 1m);
        Assert.Equal(new[] { 0.13m, 0.12m }, a);
    }

    [Fact]
    public void หน่วยชั่วโมง_ใช้ทีละครึ่งชั่วโมง_รวมเท่าสัญญา()
    {
        var a = UseAll(1000m, 3m, 0.5m);   // 6 ครั้ง ๆ ละ 0.5 ชม.
        Assert.Equal(6, a.Count);
        Assert.Equal(166.67m, a[0]);       // round(333.33 × 0.5 = 166.665) = 166.67
        Assert.Equal(1000m, a.Sum());
    }

    [Fact]
    public void หมดอายุ_breakage_เท่าส่วนคงเหลือ_รวมกับที่รับรู้แล้วเท่าสัญญา()
    {
        decimal used = 0m, recognized = 0m;
        for (var i = 0; i < 4; i++)
        {
            recognized += CourseCreditMath.RecognizeOnUse(4205.61m, 10m, used, recognized, 1m);
            used += 1m;
        }
        Assert.Equal(1682.24m, recognized);
        var breakage = CourseCreditMath.BreakageAmount(4205.61m, recognized);
        Assert.Equal(2523.37m, breakage);
        Assert.Equal(4205.61m, recognized + breakage);
        // ใช้ครบแล้ว ⇒ breakage 0 (ห้ามติดลบ)
        Assert.Equal(0m, CourseCreditMath.BreakageAmount(1000m, 1000m));
    }

    [Fact]
    public void คืนเครดิต_ไม่กระทบรายได้_ยอดคงเหลือกลับมา()
    {
        // จอง 2 ⇒ คงเหลือ 8 · คืน 2 ⇒ คงเหลือ 10 · รายได้ไม่ขยับ (ไม่มีการเรียก RecognizeOnUse)
        Assert.Equal(8m, CourseCreditMath.Available(10m, 0m, 2m));
        Assert.Equal(10m, CourseCreditMath.Available(10m, 0m, 0m));
        // ใช้เครดิตครั้งแรกหลังคืน ยังได้ราคาต่อเครดิตเท่าเดิม
        Assert.Equal(100m, CourseCreditMath.RecognizeOnUse(1000m, 10m, 0m, 0m, 1m));
        Assert.Equal(0m, CourseCreditMath.Available(10m, 9m, 3m));   // ไม่ติดลบ
        Assert.Equal(0m, CourseCreditMath.RecognizeOnUse(1000m, 10m, 0m, 0m, 0m));
    }

    [Fact]
    public void นโยบายยกเลิก_ทันเวลาคืน_ช้าริบ()
    {
        var start = new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);   // 10:00 เวลาไทย
        Assert.Equal(CreditCancelOutcome.Refund, CourseCreditMath.CancelOutcome(start, start.AddHours(-24), 24));
        Assert.Equal(CreditCancelOutcome.Forfeit, CourseCreditMath.CancelOutcome(start, start.AddHours(-24).AddMinutes(1), 24));
        Assert.Equal(CreditCancelOutcome.Refund, CourseCreditMath.CancelOutcome(start, start.AddMinutes(-1), 0));
        Assert.Equal(CreditCancelOutcome.Forfeit, CourseCreditMath.CancelOutcome(start, start.AddMinutes(1), 0));
        Assert.Equal(CreditCancelOutcome.Refund, CourseCreditMath.CancelOutcome(start, start.AddMinutes(-1), -5));   // ค่าติดลบ = 0
    }

    [Fact]
    public void วันหมดอายุ_นับตามปฏิทินไทย()
    {
        // 22 ต.ค. 20:00 UTC = 23 ต.ค. 03:00 เวลาไทย ⇒ วันซื้อ = 23 ต.ค. · 30 วัน ⇒ ใช้ได้ถึง 22 พ.ย.
        var bought = new DateTime(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);
        var exp = CourseCreditMath.ExpiresOn(bought, 30);
        Assert.Equal(new DateTime(2026, 11, 22), exp!.Value.Date);
        Assert.False(CourseCreditMath.IsExpired(exp, new DateTime(2026, 11, 22, 16, 59, 0, DateTimeKind.Utc)));   // 23:59 ไทย
        Assert.True(CourseCreditMath.IsExpired(exp, new DateTime(2026, 11, 22, 17, 0, 0, DateTimeKind.Utc)));     // 00:00 ไทยวันถัดไป
        Assert.Null(CourseCreditMath.ExpiresOn(bought, 0));
        Assert.False(CourseCreditMath.IsExpired(null, DateTime.UtcNow.AddYears(10)));
    }

    [Fact]
    public void เครดิตต่อรอบ_ครั้งหรือชั่วโมง()
    {
        Assert.Equal(1.5m, CourseCreditMath.CreditsForSession(CreditUnit.Hour, 90, 1m));
        Assert.Equal(1m, CourseCreditMath.CreditsForSession(CreditUnit.Session, 90, 0m));
        Assert.Equal(2m, CourseCreditMath.CreditsForSession(CreditUnit.Session, 30, 2m));
    }

    [Theory]
    [InlineData(true, 7, DocumentType.TaxInvoice, 7, true)]
    [InlineData(true, 0, DocumentType.TaxInvoice, 0, true)]
    [InlineData(true, -1, DocumentType.Receipt, -1, false)]   // §81 ยกเว้น — ห้ามออกใบกำกับ
    [InlineData(false, 7, DocumentType.Receipt, 0, false)]    // ไม่จด VAT — ใบเสร็จ VAT 0
    public void รูปเอกสารขาย(bool vatRegistered, int pkgRate, DocumentType type, int lineRate, bool cashReceipt)
    {
        var s = CourseCreditMath.SaleDocumentShape(vatRegistered, pkgRate);
        Assert.Equal(type, s.Type);
        Assert.Equal((decimal)lineRate, s.LineVatRate);
        Assert.Equal(cashReceipt, s.IssuedAsCashReceipt);
    }

    private static readonly DateTime Now = new(2026, 10, 22, 3, 0, 0, DateTimeKind.Utc);

    private static CourseCreditMath.PassFacts Pass(decimal total = 10m, decimal used = 0m, decimal reserved = 0m,
        CreditPassStatus status = CreditPassStatus.Active, DateTime? exp = null, CreditClassFormat format = CreditClassFormat.Any,
        Guid[]? allowed = null)
        => new(status, exp ?? new DateTime(2026, 12, 31), total, used, reserved, format, allowed ?? Array.Empty<Guid>());

    [Fact]
    public void ตัวตัดสินการจอง_ผ่านและเหตุที่ไม่ผ่าน()
    {
        var svc = Guid.NewGuid();
        var later = Now.AddDays(1);
        Assert.Null(CourseCreditMath.CannotBookReason(Pass(), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("ไม่พอ", CourseCreditMath.CannotBookReason(Pass(used: 9m, reserved: 1m), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("หมดอายุ", CourseCreditMath.CannotBookReason(Pass(exp: new DateTime(2026, 10, 1)), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("หลังวันหมดอายุ", CourseCreditMath.CannotBookReason(Pass(exp: new DateTime(2026, 10, 22)), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("สถานะ", CourseCreditMath.CannotBookReason(Pass(status: CreditPassStatus.Exhausted), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("ตัวต่อตัว", CourseCreditMath.CannotBookReason(Pass(format: CreditClassFormat.OneOnOne), 1m, later, null, CreditClassFormat.Group, Now));
        Assert.Contains("ใช้กับบริการ", CourseCreditMath.CannotBookReason(Pass(allowed: new[] { svc }), 1m, later, Guid.NewGuid(), CreditClassFormat.Group, Now));
        Assert.Null(CourseCreditMath.CannotBookReason(Pass(allowed: new[] { svc }), 1m, later, svc, CreditClassFormat.Group, Now));
        // ทิศตรงข้าม: แพ็กเกจไม่จำกัดบริการ ⇒ บริการใดก็จองได้ · รอบวันสุดท้ายของอายุยังจองได้
        Assert.Null(CourseCreditMath.CannotBookReason(Pass(), 1m, later, Guid.NewGuid(), CreditClassFormat.OneOnOne, Now));
        Assert.Null(CourseCreditMath.CannotBookReason(Pass(exp: new DateTime(2026, 10, 23)), 1m, later, null, CreditClassFormat.Group, Now));
    }

    [Fact]
    public void เลือกครูอัตโนมัติ_ตัดคนติดรอบ_ภาระน้อยสุด_เสมอตามลำดับ()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var order = new List<Guid> { a, b, c };
        Assert.Equal(a, CourseCreditMath.PickTrainer(order, new Dictionary<Guid, int>(), new HashSet<Guid>()));
        Assert.Equal(b, CourseCreditMath.PickTrainer(order, new Dictionary<Guid, int> { [a] = 3, [b] = 1, [c] = 1 }, new HashSet<Guid>()));
        Assert.Equal(c, CourseCreditMath.PickTrainer(order, new Dictionary<Guid, int> { [c] = 5 }, new HashSet<Guid> { a, b }));
        Assert.Null(CourseCreditMath.PickTrainer(order, new Dictionary<Guid, int>(), new HashSet<Guid> { a, b, c }));
    }

    // ===== R-38 คืนเงินบางส่วน (รอบ 205 W6) =====

    [Fact]
    public void สัญญาที่มีผลหลังคืนบางส่วน_ลดตามยอดคืน_ไม่ติดลบ_และบัตรไม่เคยคืนเท่าเดิม()
    {
        Assert.Equal(4205.61m, CourseCreditMath.EffectiveContract(4205.61m, 0m));
        Assert.Equal(3205.61m, CourseCreditMath.EffectiveContract(4205.61m, 1000m));
        Assert.Equal(0m, CourseCreditMath.EffectiveContract(1000m, 1500m));
        Assert.Equal(4205.61m, CourseCreditMath.EffectiveContract(4205.61m, -5m));   // ค่าลบไม่เพิ่มสัญญา
    }

    [Fact]
    public void เครดิตที่ลดจากยอดคืน_ปัดลงเป็นหน่วยนับ_ครั้งเต็ม_ชั่วโมงขั้น15นาที()
    {
        Assert.Equal(2m, CourseCreditMath.CreditsRefundable(1000m, 420.56m, CreditUnit.Session));
        Assert.Equal(0m, CourseCreditMath.CreditsRefundable(100m, 420.56m, CreditUnit.Session));
        Assert.Equal(3.25m, CourseCreditMath.CreditsRefundable(1000m, 300m, CreditUnit.Hour));
        Assert.Equal(0m, CourseCreditMath.CreditsRefundable(1000m, 0m, CreditUnit.Session));   // ราคา 0 = ไม่รู้ ⇒ ไม่ลด
        // รอบ 208 (ฝ่ายค้าน W6 ข้อ 4): ผลหารที่ควรเป็นหน่วยเต็มแต่ทศนิยมขาดไปเศษเสี้ยว ไม่หล่นลงหนึ่งขั้น
        Assert.Equal(1m, CourseCreditMath.CreditsRefundable(99.999m, 100m, CreditUnit.Session));
        Assert.Equal(0.25m, CourseCreditMath.CreditsRefundable(83.33m, 1000m / 3m, CreditUnit.Hour));
        // ทิศตรงข้าม: ขาดจริงเกินค่าเผื่อ ⇒ ยังปัดลง
        Assert.Equal(0m, CourseCreditMath.CreditsRefundable(99.9m, 100m, CreditUnit.Session));
        Assert.Equal(1m, CourseCreditMath.CreditStep(CreditUnit.Session));
        Assert.Equal(0.25m, CourseCreditMath.CreditStep(CreditUnit.Hour));
    }

    [Fact]
    public void หลังคืนบางส่วน_การรับรู้ที่เหลือใช้สัญญาที่มีผล_รวมแล้วเท่าสัญญาหักยอดคืน()
    {
        // 10 ครั้ง 4,205.61 · ใช้ 4 (1,682.24) · คืน 2 เครดิต 1,000 ⇒ สัญญาที่มีผล 3,205.61 · เครดิตทั้งหมด 8 · เหลือใช้ 4
        var effective = CourseCreditMath.EffectiveContract(4205.61m, 1000m);
        decimal used = 4m, recognized = 1682.24m, sum = 0m;
        for (var i = 0; i < 4; i++)
        {
            var a = CourseCreditMath.RecognizeOnUse(effective, 8m, used, recognized, 1m);
            sum += a; used += 1m; recognized += a;
        }
        Assert.Equal(1523.37m, sum);                         // 3,205.61 − 1,682.24
        Assert.Equal(effective, recognized);                 // รับรู้ครบ = สัญญาที่มีผล · 21713 ของบัตร = 0
        Assert.Equal(0m, CourseCreditMath.BreakageAmount(effective, recognized));
    }
}
