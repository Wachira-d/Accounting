using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เงินมัดจำโมดูลที่พัก (รอบ 193 · #34 + F-03)
/// <list type="bullet">
/// <item>เช็คเอาต์: การเข้าพัก 7,450 มัดจำ 2,000 — ต่อโหมด VAT ของใบมัดจำ · VAT รวมทั้งเรื่องต้อง = VAT ของ 7,450 ครั้งเดียว</item>
/// <item>ยกเลิก: ริบ/คืน แยกฐาน-VAT ด้วยสูตรเดียวกับ RefundDepositAsync ⇒ คืนภายหลังไม่ติดด่าน "คืนเกินคงเหลือ"</item>
/// <item>"ต้องคืน" ≠ "คืนแล้ว" — สถานะคืนเงินมาจากหลักฐาน (ยอดที่ยืนยัน) เท่านั้น</item>
/// </list>
/// </summary>
public class LodgingDepositSettlementTests
{
    private static readonly Guid Guest = Guid.NewGuid();

    // มัดจำ 2,000 รวม VAT 7% → ฐาน 1,869.16 + VAT 130.84
    private static LodgingDepositSnapshot Deposit2000(bool vatPending = false, decimal realized = 0m, decimal refunded = 0m, string no = "TIV-0001")
        => new(Guid.NewGuid(), no, Guest, 1869.16m, 130.84m, 2000m, vatPending, realized, refunded);

    // ใบสุดท้ายยอดใหญ่กว่ามัดจำเสมอ (เส้นปกติ) — แผนใช้มัดจำครบทุกใบ
    private static LodgingCheckoutDepositPlan PlanBig(IReadOnlyList<LodgingDepositSnapshot> deposits)
        => LodgingDepositSettlement.PlanCheckout(deposits, 1_000_000m, _ => 1_000_000m);

    private static LodgingDepositSnapshot FullDeposit2000()
        => new(Guid.NewGuid(), "REC-0001", Guest, 2000m, 0m, 2000m, VatPending: true, 0m, 0m);

    [Fact]
    public void แยกฐานVATราคารวม_1000_เป็น_934_58_กับ_65_42()
    {
        Assert.Equal((934.58m, 65.42m), LodgingDepositSettlement.SplitInclusive(1000m, 7m));
        Assert.Equal((1869.16m, 130.84m), LodgingDepositSettlement.SplitInclusive(2000m, 7m));
        Assert.Equal((500m, 0m), LodgingDepositSettlement.SplitInclusive(500m, 0m));
    }

    // ═══ เช็คเอาต์ 7,450 − มัดจำ 2,000 ═══

    [Fact]
    public void VATทันที_ใบสุดท้ายหักฐานมัดจำ_VATไม่ซ้ำ()
    {
        var plan = PlanBig(new[] { Deposit2000() });
        Assert.Single(plan.Deduct);
        Assert.Empty(plan.Apply);
        Assert.Equal(1869.16m, plan.BaseDeducted);
        Assert.Equal(130.84m, plan.VatDeducted);
        Assert.Equal(2000m, plan.GrossDeducted);
        Assert.Equal("TIV-0001", plan.DeductionRef);

        var (fullBase, fullVat) = LodgingDepositSettlement.SplitInclusive(7450m, 7m);   // 6,962.62 / 487.38
        var finalVat = LodgingDepositSettlement.FinalInvoiceVat(fullBase, plan.BaseDeducted, 7m);
        Assert.Equal(356.54m, finalVat);
        Assert.Equal(5450m, fullBase - plan.BaseDeducted + finalVat);   // ยอดที่เก็บตอนเช็คเอาต์
        Assert.Equal(fullVat, plan.VatDeducted + finalVat);              // 130.84 (เดือนรับมัดจำ) + 356.54 = 487.38 ครั้งเดียว
    }

    [Fact]
    public void ภาษีรอเรียกเก็บ_ใบสุดท้ายเต็มจำนวน_นำมัดจำไปตัดชำระ()
    {
        var plan = PlanBig(new[] { Deposit2000(vatPending: true) });
        Assert.Empty(plan.Deduct);
        Assert.Null(plan.DeductionRef);
        Assert.Equal(0m, plan.BaseDeducted);
        var a = Assert.Single(plan.Apply);
        Assert.Equal(2000m, a.Gross);
        // VAT ทั้งเรื่องรายงานที่ใบสุดท้ายใบเดียว (มัดจำพัก 21913 ไม่เคยเข้า ภ.พ.30)
        Assert.Equal(487.38m, LodgingDepositSettlement.FinalInvoiceVat(6962.62m, plan.BaseDeducted, 7m));
    }

    [Fact]
    public void มัดจำเต็มยอด_ใบสุดท้ายเต็มจำนวน_นำมัดจำไปตัดชำระ()
    {
        var plan = PlanBig(new[] { FullDeposit2000() });
        Assert.Empty(plan.Deduct);
        Assert.Equal(2000m, Assert.Single(plan.Apply).Gross);
    }

    [Fact]
    public void มัดจำที่ใช้หมดแล้ว_ไม่ถูกนำมาใช้ซ้ำ()
    {
        var used = Deposit2000(realized: 1869.16m);
        var plan = PlanBig(new[] { used });
        Assert.Empty(plan.Deduct);
        Assert.Empty(plan.Apply);
    }

    [Fact]
    public void ไม่มีมัดจำ_ใบสุดท้ายไม่มีการหัก()
    {
        var plan = PlanBig(Array.Empty<LodgingDepositSnapshot>());
        Assert.Equal(0m, plan.BaseDeducted);
        Assert.Empty(plan.Apply);
        Assert.Null(plan.DeductionRef);
    }

    [Fact]
    public void มัดจำสองใบผสมโหมด_แยกเส้นถูกใบ()
    {
        var plan = PlanBig(new[] { Deposit2000(no: "TIV-1"), Deposit2000(vatPending: true, no: "REC-2") });
        Assert.Equal("TIV-1", plan.DeductionRef);
        Assert.Equal("REC-2", Assert.Single(plan.Apply).Number);
    }

    // ═══ ยกเลิก / no-show ═══

    [Fact]
    public void ยกเลิก_ริบครึ่งคืนครึ่ง_ฐานรวมพอดีไม่เหลือเศษ()
    {
        var d = Deposit2000();
        var plan = LodgingDepositSettlement.PlanCancellation(1000m, 2000m, new[] { d });
        Assert.Equal(1000m, plan.Forfeit);
        Assert.Equal(1000m, plan.Refund);
        var line = Assert.Single(plan.Lines);
        Assert.Equal(934.58m, line.ForfeitBase);
        Assert.Equal(934.58m, line.RefundBase);
        Assert.Equal(65.42m, line.RefundVat);
        Assert.Equal(d.SubTotal, line.ForfeitBase + line.RefundBase);
        // สูตรเดียวกับ RefundDepositAsync (vat = round(gross × VAT/Total)) ⇒ ยอดคืนภายหลังพอดีกับฐานคงเหลือหลังริบ
        Assert.Equal(Math.Round(line.RefundGross * d.VatAmount / d.TotalAmount, 2, MidpointRounding.AwayFromZero), line.RefundVat);
        Assert.Equal(line.RefundBase, LodgingDepositSettlement.Remaining(d with { RealizedBase = line.ForfeitBase }).Base);
    }

    [Fact]
    public void ยกเลิก_ยอดเศษ_ฐานริบคำนวณจากฐานที่จะคืน()
    {
        var d = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-9", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(333.33m, 1000m, new[] { d }).Lines);
        Assert.Equal(666.67m, line.RefundGross);
        Assert.Equal(43.61m, line.RefundVat);
        Assert.Equal(623.06m, line.RefundBase);
        Assert.Equal(311.52m, line.ForfeitBase);
        // หลังริบ ฐานคงเหลือ = ฐานที่จะคืน → คืนครบได้ไม่ติดด่าน
        var afterForfeit = d with { RealizedBase = line.ForfeitBase };
        Assert.Equal(line.RefundBase, LodgingDepositSettlement.Remaining(afterForfeit).Base);
    }

    [Fact]
    public void ยกเลิกฟรี_คืนทั้งหมด_ไม่ริบ()
    {
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(0m, 2000m, new[] { Deposit2000() }).Lines);
        Assert.Equal(0m, line.ForfeitBase);
        Assert.Equal(2000m, line.RefundGross);
    }

    [Fact]
    public void ค่าปรับเกินมัดจำ_ริบทั้งหมด_ส่วนต่างไม่ได้เรียกเก็บ()
    {
        var plan = LodgingDepositSettlement.PlanCancellation(7450m, 2000m, new[] { Deposit2000() });
        Assert.Equal(2000m, plan.Forfeit);
        Assert.Equal(0m, plan.Refund);
        Assert.Equal(5450m, plan.UncollectedFee);
        Assert.Equal(1869.16m, plan.Lines[0].ForfeitBase);
    }

    [Fact]
    public void มัดจำเต็มยอด_ยกเลิก_ไม่มีVATให้แยก()
    {
        var line = Assert.Single(LodgingDepositSettlement.PlanCancellation(300m, 2000m, new[] { FullDeposit2000() }).Lines);
        Assert.Equal(300m, line.ForfeitBase);
        Assert.Equal(1700m, line.RefundBase);
        Assert.Equal(0m, line.RefundVat);
    }

    [Fact]
    public void ไม่ออกเอกสาร_ใช้ยอดมัดจำบนการจอง_ไม่มีบรรทัดเอกสาร()
    {
        var plan = LodgingDepositSettlement.PlanCancellation(500m, 2000m, Array.Empty<LodgingDepositSnapshot>());
        Assert.Equal(500m, plan.Forfeit);
        Assert.Equal(1500m, plan.Refund);
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void มัดจำสองใบ_ริบใบเก่าก่อน_คืนจากใบใหม่()
    {
        var d1 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-1", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var d2 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-2", Guest, 1000m, 70m, 1070m, false, 0m, 0m);
        var plan = LodgingDepositSettlement.PlanCancellation(1500m, 2070m, new[] { d1, d2 });
        Assert.Equal(934.58m, plan.Lines[0].ForfeitBase);
        Assert.Equal(0m, plan.Lines[0].RefundGross);
        Assert.Equal(570m, plan.Lines[1].RefundGross);
        Assert.Equal(37.29m, plan.Lines[1].RefundVat);
        Assert.Equal(467.29m, plan.Lines[1].ForfeitBase);

        // ตอนโอนคืนจริง: แบ่งยอดลงใบที่ยังเหลือ (ใบใหม่ก่อน)
        var afterCancel = new[] { d1 with { RealizedBase = 934.58m }, d2 with { RealizedBase = 467.29m } };
        var alloc = LodgingDepositSettlement.AllocateRefund(570m, afterCancel);
        var only = Assert.Single(alloc);
        Assert.Equal("TIV-2", only.Number);
        Assert.Equal(570m, only.Gross);
    }

    // ═══ F-03: "ต้องคืน" ≠ "คืนแล้ว" ═══

    [Fact]
    public void สถานะคืนเงิน_มาจากยอดที่ยืนยันเท่านั้น()
    {
        Assert.Equal(LodgingRefundState.None, LodgingDepositSettlement.RefundStateOf(0m, 0m, null));
        Assert.Equal(LodgingRefundState.Pending, LodgingDepositSettlement.RefundStateOf(1000m, 0m, null));   // ยกเลิกแล้ว ยังไม่โอน
        Assert.Equal(LodgingRefundState.Pending, LodgingDepositSettlement.RefundStateOf(1000m, 400m, null));
        Assert.Equal(LodgingRefundState.Paid, LodgingDepositSettlement.RefundStateOf(1000m, 1000m, null));
        Assert.Equal(600m, LodgingDepositSettlement.RefundPending(1000m, 400m));
        Assert.Equal(0m, LodgingDepositSettlement.RefundPending(1000m, 1200m));
    }

    [Fact]
    public void ยืนยันคืนเงิน_ห้ามเกินยอดค้าง_ห้ามศูนย์_ยอดถูกผ่าน()
    {
        Assert.Null(LodgingDepositSettlement.ValidateRefundPayment(1000m, 1000m, 0m));
        Assert.Null(LodgingDepositSettlement.ValidateRefundPayment(400m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(1200m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(0m, 1000m, 0m));
        Assert.NotNull(LodgingDepositSettlement.ValidateRefundPayment(100m, 1000m, 1000m));   // คืนครบแล้ว
    }

    // ═══ S-06: AutoConfirmOnDeposit ต่อสายแล้ว ═══

    [Fact]
    public void เงินเข้าออนไลน์_ที่พักปิดยืนยันอัตโนมัติ_การจองยังรอพนักงาน()
        => Assert.Equal(LodgingReservationStatus.Pending,
            LodgingDepositSettlement.StatusAfterDeposit(LodgingReservationStatus.Pending, autoConfirmOnDeposit: false, explicitStaffConfirm: false));

    [Fact]
    public void เงินเข้าออนไลน์_ที่พักเปิดยืนยันอัตโนมัติ_ยืนยันทันที_พฤติกรรมเดิม()
        => Assert.Equal(LodgingReservationStatus.Confirmed,
            LodgingDepositSettlement.StatusAfterDeposit(LodgingReservationStatus.Pending, autoConfirmOnDeposit: true, explicitStaffConfirm: false));

    [Fact]
    public void พนักงานกดยืนยันเอง_ยืนยันเสมอ_ไม่ว่าตั้งค่าไหน()
        => Assert.Equal(LodgingReservationStatus.Confirmed,
            LodgingDepositSettlement.StatusAfterDeposit(LodgingReservationStatus.Pending, autoConfirmOnDeposit: false, explicitStaffConfirm: true));

    [Theory]
    [InlineData(LodgingReservationStatus.Confirmed)]
    [InlineData(LodgingReservationStatus.CheckedIn)]
    public void รับชำระเพิ่มบนการจองที่ไม่ใช่รอมัดจำ_สถานะไม่ถอยกลับ(LodgingReservationStatus current)
        => Assert.Equal(current, LodgingDepositSettlement.StatusAfterDeposit(current, autoConfirmOnDeposit: true, explicitStaffConfirm: true));

    // ═══ หลังฝ่ายค้าน C2: มัดจำมากกว่ายอดใบสุดท้าย ═══

    // ใบสุดท้ายราคารวม VAT บรรทัดเดียว — ยอดจากตัวคำนวณจริงของ DocumentService (สูตรเดียวกับ CreateDocumentAsync)
    private static List<DocumentLineRequest> Stay(decimal gross) => new()
    {
        new(Description: "ค่าห้องพัก", Quantity: 1, Unit: "รายการ", UnitPrice: gross, DiscountPercent: 0, VatRate: 7m,
            WithholdingTaxRate: 0, AccountId: null),
    };

    private static LodgingCheckoutDepositPlan PlanFor(decimal finalGross, params LodgingDepositSnapshot[] deposits)
    {
        var lines = Stay(finalGross);
        var full = DocumentService.PreviewTotals(lines, true, 0m);
        return LodgingDepositSettlement.PlanCheckout(deposits, full.Net, d => DocumentService.PreviewTotals(lines, true, 0m, d).Total);
    }

    [Fact]
    public void VATทันที_จ่ายเต็ม7450แล้วเลื่อนเหลือ5000_หักเท่าฐานใบสุดท้าย_ส่วนเกินค้างคืน()
    {
        var paidFull = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-1", Guest, 6962.62m, 487.38m, 7450m, false, 0m, 0m);
        var plan = PlanFor(5000m, paidFull);
        // ฐานใบสุดท้าย 5,000 = 4,672.90 → หักได้เท่านั้น (เดิม Realize เต็ม 6,962.62 = รายได้เกิน 2,289.72)
        Assert.Equal(4672.90m, plan.BaseDeducted);
        Assert.Equal(0m, DocumentService.PreviewTotals(Stay(5000m), true, 0m, plan.BaseDeducted).Total);
        var ex = Assert.Single(plan.Excess);
        Assert.Equal(2450m, ex.Gross);   // แขกจ่ายเกิน 2,450 ⇒ ค้างคืน (เดิมไม่มียอดค้างคืน)
        Assert.Empty(plan.Apply);
    }

    [Theory]
    [InlineData(true)]    // ภาษีรอเรียกเก็บ
    [InlineData(false)]   // เต็มยอด
    public void มัดจำพักหรือเต็มยอด_เกินยอดใบสุดท้าย_ตัดชำระเท่ายอดใบ_ส่วนเกินค้างคืน(bool pendingVat)
    {
        var d = pendingVat
            ? new LodgingDepositSnapshot(Guid.NewGuid(), "REC-1", Guest, 6962.62m, 487.38m, 7450m, true, 0m, 0m)
            : new LodgingDepositSnapshot(Guid.NewGuid(), "REC-1", Guest, 7450m, 0m, 7450m, true, 0m, 0m);
        var plan = PlanFor(5000m, d);
        // เดิม Apply 7,450 ชนด่าน "เกินยอดค้าง" หลังประทับเลขใบ ⇒ การจองค้างเช็คอินถาวร
        Assert.Equal(5000m, Assert.Single(plan.Apply).Gross);
        Assert.Equal(2450m, Assert.Single(plan.Excess).Gross);
    }

    [Fact]
    public void ยอดใบสุดท้ายมากกว่ามัดจำ_ไม่มีส่วนเกิน_เส้นปกติไม่ถูกแตะ()
    {
        var plan = PlanFor(7450m, Deposit2000());
        Assert.Equal(1869.16m, plan.BaseDeducted);
        Assert.Empty(plan.Excess);
        Assert.Equal(0m, plan.ExcessGross);
        var planPending = PlanFor(7450m, Deposit2000(vatPending: true));
        Assert.Equal(2000m, Assert.Single(planPending.Apply).Gross);
        Assert.Empty(planPending.Excess);
    }

    [Fact]
    public void ทำเช็คเอาต์ต่อ_มัดจำที่ตัดชำระใบนี้แล้วไม่ตัดซ้ำ()
    {
        var finalId = Guid.NewGuid();
        var applied = new LodgingDepositSnapshot(Guid.NewGuid(), "REC-1", Guest, 1869.16m, 130.84m, 2000m, true, 1869.16m, 0m, finalId);
        var notYet = new LodgingDepositSnapshot(Guid.NewGuid(), "REC-2", Guest, 1869.16m, 130.84m, 2000m, true, 0m, 0m);
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { applied, notYet }, 0m, _ => 3450m, finalId);
        var a = Assert.Single(plan.Apply);
        Assert.Equal("REC-2", a.Number);
        Assert.Empty(plan.Excess);
    }

    // ═══ หลังฝ่ายค้าน C7: ยอดจริงผ่านตัวคำนวณของ DocumentService (ไม่ใช่สูตรของ helper ตรวจตัวเอง) ═══

    [Fact]
    public void เครื่องคำนวณจริง_7450หักฐานมัดจำ2000_ได้5450_VAT356_54()
    {
        var plan = PlanFor(7450m, Deposit2000());
        var t = DocumentService.PreviewTotals(Stay(7450m), true, 0m, plan.BaseDeducted);
        Assert.Equal(1869.16m, t.DepositBase);   // R3-1: ลงช่องฐานมัดจำ ไม่ใช่ส่วนลดการค้า
        Assert.Equal(0m, t.TradeDiscount);
        Assert.Equal(5093.46m, t.Net);
        Assert.Equal(356.54m, t.Vat);
        Assert.Equal(5450m, t.Total);
        Assert.Equal(0m, LodgingDepositSettlement.RoundingDelta(7450m, plan.GrossDeducted, t.Total));
    }

    [Fact]
    public void เครื่องคำนวณจริง_1000มัดจำ59_85_ส่วนต่างปัดเศษ_0_01_อยู่ที่VAT_ฐานไม่เพี้ยน()
    {
        var (depBase, depVat) = LodgingDepositSettlement.SplitInclusive(59.85m, 7m);   // 55.93 / 3.92
        var d = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-5", Guest, depBase, depVat, 59.85m, false, 0m, 0m);
        var plan = PlanFor(1000m, d);
        var t = DocumentService.PreviewTotals(Stay(1000m), true, 0m, plan.BaseDeducted);
        Assert.Equal(940.16m, t.Total);                                            // คู่ยอดที่ฝ่ายค้านยกมา
        Assert.Equal(0.01m, LodgingDepositSettlement.RoundingDelta(1000m, plan.GrossDeducted, t.Total));
        Assert.Equal(934.58m, depBase + t.Net);                                    // ฐานรวม = ฐานของ 1,000 พอดี
        Assert.Equal(65.43m, depVat + t.Vat);                                      // VAT คิดรายใบ (ทิศที่เลือก)
    }

    // ═══ หลังฝ่ายค้าน C6: สูตรปัดตัวเดียวระหว่างแผนยกเลิกกับการแบ่งยอดคืน ═══

    [Fact]
    public void มัดจำสองใบ1000_ค่าปรับ1171_43_คืน828_57ได้ครบไม่ค้าง0_01()
    {
        var d1 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-1", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var d2 = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-2", Guest, 934.58m, 65.42m, 1000m, false, 0m, 0m);
        var plan = LodgingDepositSettlement.PlanCancellation(1171.43m, 2000m, new[] { d1, d2 });
        Assert.Equal(828.57m, plan.Refund);
        var after = new[]
        {
            d1 with { RealizedBase = plan.Lines[0].ForfeitBase },
            d2 with { RealizedBase = plan.Lines[1].ForfeitBase },
        };
        var alloc = LodgingDepositSettlement.AllocateRefund(plan.Refund, after);
        Assert.Equal(828.57m, alloc.Sum(a => a.Gross));     // เดิมได้ 828.56 ⇒ ปฏิเสธ หรือค้าง 0.01 ถาวร
        var one = Assert.Single(alloc);
        Assert.Equal("TIV-2", one.Number);
        Assert.Equal(774.36m, plan.Lines[1].RefundBase);     // แยกฐานด้วยสูตรเดียวกับ RefundDepositAsync
        Assert.Equal(934.58m - 774.36m, plan.Lines[1].ForfeitBase);
    }

    [Fact]
    public void มัดจำใบเดียว_ยอดคืนเท่าเดิม_สูตรใหม่ไม่เปลี่ยนเส้นที่ถูกอยู่แล้ว()
    {
        var plan = LodgingDepositSettlement.PlanCancellation(1000m, 2000m, new[] { Deposit2000() });
        Assert.Equal(1000m, plan.Refund);
        var after = new[] { Deposit2000(realized: plan.Lines[0].ForfeitBase) };
        Assert.Equal(1000m, LodgingDepositSettlement.AllocateRefund(1000m, after).Sum(a => a.Gross));
    }

    // ═══ หลังฝ่ายค้าน C10: แถวยกเลิกก่อนรอบ 193 = ไม่มีข้อมูลการโอน (ไม่ใช่ "คืนแล้ว") ═══

    [Fact]
    public void แถวlegacy_สถานะไม่ทราบ_ยอดค้างที่กดคืนได้เป็นศูนย์()
    {
        const string legacy = LodgingDepositSettlement.LegacyRefundMarker + " (ก่อนรอบ 193)";
        Assert.Equal(LodgingRefundState.Unknown, LodgingDepositSettlement.RefundStateOf(1000m, 0m, legacy));
        Assert.Equal(0m, LodgingDepositSettlement.RefundPendingOf(1000m, 0m, legacy));
        Assert.True(LodgingDepositSettlement.IsLegacyRefund(legacy));
    }

    [Fact]
    public void แถวใหม่ไม่ถูกนับเป็นlegacy_ยอดค้างและสถานะตามจริง()
    {
        Assert.False(LodgingDepositSettlement.IsLegacyRefund("user-123"));
        Assert.False(LodgingDepositSettlement.IsLegacyRefund(null));
        Assert.Equal(LodgingRefundState.Paid, LodgingDepositSettlement.RefundStateOf(1000m, 1000m, "user-123"));
        Assert.Equal(600m, LodgingDepositSettlement.RefundPendingOf(1000m, 400m, "user-123"));
        Assert.Equal(LodgingRefundState.None, LodgingDepositSettlement.RefundStateOf(0m, 0m, LodgingDepositSettlement.LegacyRefundMarker));
    }

    // ═══ ฝ่ายค้านรอบสอง N3 — ยอดคืนแล้วตามใบมัดจำ นับเฉพาะการคืนหลังจุดตั้งยอดค้างคืน ═══

    [Fact]
    public void คืนจากหน้าเงินมัดจำก่อนยกเลิก_ไม่ถูกนับเป็นการคืนของยอดค้าง_แขกยังได้คืน500()
        // มัดจำ 2,000 · คืน 500 ก่อนยกเลิก · ค่าปรับ 1,000 ⇒ ค้างคืน 500 · baseline = 500 (เดิมนับซ้ำ ⇒ ยอดค้าง 0 ⇒ throw ถาวร)
        => Assert.Equal(0m, LodgingDepositSettlement.RefundPaidCatchUp(500m, 0m, refundedOnDocsNow: 500m, refundBaseline: 500m));

    [Fact]
    public void คำขอก่อนล้มหลังลงบัญชีคืนครบแล้ว_ตามทันเท่ายอดค้างทั้งหมด()
    {
        Assert.Equal(1000m, LodgingDepositSettlement.RefundPaidCatchUp(1000m, 0m, refundedOnDocsNow: 1000m, refundBaseline: 0m));
        Assert.Equal(300m, LodgingDepositSettlement.RefundPaidCatchUp(1000m, 400m, refundedOnDocsNow: 700m, refundBaseline: 0m));
        Assert.Equal(0m, LodgingDepositSettlement.RefundPaidCatchUp(1000m, 400m, refundedOnDocsNow: 400m, refundBaseline: 0m));
    }

    // ═══ ฝ่ายค้านรอบสอง N4 — คืนสองงวดไม่ค้าง 0.01 (คู่ยอดของฝ่ายค้าน) ═══

    [Fact]
    public void คืนสองงวด_72_02_แล้ว3154_54_ครบ3226_56_ไม่ค้าง()
    {
        var d = new LodgingDepositSnapshot(Guid.NewGuid(), "TIV-7", Guest, 4417.82m, 309.25m, 4727.07m, false, 0m, 0m);
        var plan = LodgingDepositSettlement.PlanCancellation(1500.51m, 4727.07m, new[] { d });
        Assert.Equal(3226.56m, plan.Refund);
        var afterForfeit = d with { RealizedBase = plan.Lines[0].ForfeitBase };
        Assert.Equal(72.02m, LodgingDepositSettlement.AllocateRefund(72.02m, new[] { afterForfeit }).Sum(a => a.Gross));
        var afterFirst = afterForfeit with { RefundedGross = 72.02m };
        Assert.Equal(3154.54m, LodgingDepositSettlement.AllocateRefund(3154.54m, new[] { afterFirst }).Sum(a => a.Gross));
    }

    // ═══ ฝ่ายค้านรอบสอง — สาขา "ตัดชำระใบนี้ไปแล้ว" ของการทำเช็คเอาต์ต่อ (เดิมเทสต์ไม่ถึงสาขานี้) ═══

    [Fact]
    public void ทำเช็คเอาต์ต่อ_มัดจำที่ตัดชำระใบนี้บางส่วนแล้ว_ส่วนที่เหลือเป็นค้างคืน_ไม่ตัดซ้ำ()
    {
        var finalId = Guid.NewGuid();
        // มัดจำพัก VAT 2,000 ตัดชำระใบนี้ไปแล้ว 1,500 (ฐาน 1,401.87) ⇒ เหลือ 500 = ส่วนเกินที่ต้องคืน
        var partlyApplied = new LodgingDepositSnapshot(Guid.NewGuid(), "REC-1", Guest, 1869.16m, 130.84m, 2000m, true, 1401.87m, 0m, finalId);
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { partlyApplied }, 0m, _ => 999m, finalId);
        Assert.Empty(plan.Apply);
        Assert.Equal(500m, Assert.Single(plan.Excess).Gross);
    }

    [Fact]
    public void เช็คเอาต์ครั้งแรก_มัดจำใบเดียวกันที่ยังไม่ผูกใบนี้_ตัดชำระตามปกติ()
    {
        var d = new LodgingDepositSnapshot(Guid.NewGuid(), "REC-1", Guest, 1869.16m, 130.84m, 2000m, true, 1401.87m, 0m);
        var plan = LodgingDepositSettlement.PlanCheckout(new[] { d }, 0m, _ => 999m, Guid.NewGuid());
        Assert.Equal(500m, Assert.Single(plan.Apply).Gross);
        Assert.Empty(plan.Excess);
    }
}
