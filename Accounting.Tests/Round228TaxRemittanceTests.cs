using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 228 ทีมภาษี (erp-review/2026-11-01/TAX_PENALTY_AUDIT.md §3 + ช่องว่างรอบ 225) — ภ.ง.ด.51/50 · กองทุนเงินทดแทนรายปี ·
/// อากรแสตมป์ · เบี้ยปรับ ภ.พ.36 ที่เสนอ · มัดจำ VAT พักที่ดึงเข้า ภ.พ.30
/// <para>ทุกหมวดล็อกสองทิศ: เคสที่ต้องเกิดยอด และเคสที่ต้องเป็น 0/ไม่แตะ (ทันกำหนด · ไม่ถึงเกณฑ์ · ตั้งหนี้แบบใหม่)</para>
/// </summary>
public class Round228TaxRemittanceTests
{
    // ═══ ภ.ง.ด.50/51 — เครดิตภาษี ═══

    [Fact]
    public void CIT_หักเครดิต_ภงด51ก่อนแล้วภาษีถูกหัก_ไม่เกินภาษี()
    {
        var o = CitPayment.Split(100_000m, 30_000m, 10_000m);
        Assert.Equal((30_000m, 10_000m, 60_000m), (o.PrepaidUsed, o.WhtUsed, o.Payable));
        var over = CitPayment.Split(20_000m, 30_000m, 10_000m);   // เครดิตเกินภาษี ⇒ ใช้แค่เท่าภาษี (ส่วนเกิน = สิทธิขอคืน คงในผัง)
        Assert.Equal((20_000m, 0m, 0m), (over.PrepaidUsed, over.WhtUsed, over.Payable));
        var neg = CitPayment.Split(1_000m, -5m, -5m);
        Assert.Equal((0m, 0m, 1_000m), (neg.PrepaidUsed, neg.WhtUsed, neg.Payable));
    }

    [Fact]
    public void CIT_ตอนชำระ_ตั้งหนี้แบบใหม่ไม่มีส่วนล้าง_แบบเดิมล้างเครดิต()
    {
        // ใหม่: 21920 = ยอดสุทธิ ⇒ จ่ายเท่าหนี้ ไม่แตะเครดิต
        var fresh = CitPayment.AtPayment(60_000m, 60_000m, 30_000m, 10_000m);
        Assert.Equal((0m, 0m, 60_000m), (fresh.PrepaidUsed, fresh.WhtUsed, fresh.Payable));
        // เดิม: 21920 = ภาษีเต็ม 100,000 ⇒ จ่ายธนาคาร 60,000 + ล้าง ภ.ง.ด.51 30,000 + ภาษีถูกหัก 10,000
        var legacy = CitPayment.AtPayment(100_000m, 60_000m, 30_000m, 10_000m);
        Assert.Equal((30_000m, 10_000m, 60_000m), (legacy.PrepaidUsed, legacy.WhtUsed, legacy.Payable));
        // ส่วนเกินที่อธิบายด้วยเครดิตไม่ได้ ⇒ ไม่เดาผัง (คงใน 21920 ให้ผู้ทำบัญชีตรวจ)
        var odd = CitPayment.AtPayment(100_000m, 60_000m, 5_000m, 0m);
        Assert.Equal((5_000m, 0m, 60_000m), (odd.PrepaidUsed, odd.WhtUsed, odd.Payable));
    }

    [Fact]
    public void CIT_รอบภาษีและปี()
    {
        Assert.Equal(CitPayment.Pnd51Type, CitPayment.TypeOf("HalfYear"));
        Assert.Equal(CitPayment.Pnd50Type, CitPayment.TypeOf("Annual"));
        Assert.Null(CitPayment.TypeOf("Quarterly"));
        Assert.Equal(2026, CitPayment.CeYear("2569"));
        Assert.Equal(2026, CitPayment.CeYear("2026"));
        Assert.Null(CitPayment.CeYear("abc"));
        Assert.Equal((2026, 6), CitPayment.PeriodKey(CitPayment.Pnd51Type, 2026));
        Assert.Equal((2026, 12), CitPayment.PeriodKey(CitPayment.Pnd50Type, 2026));
    }

    [Fact]
    public void CIT_กำหนดยื่น_ภงด51_สองเดือนหลังครึ่งปี_ภงด50_150วัน_เตือนตามวันกระดาษ()
    {
        var (p51, _) = TaxFilingDeadline.For(CitPayment.Pnd51Type, 2026, 6);
        Assert.Equal(new DateTime(2026, 8, 31), p51);                     // จันทร์
        var (p50, e50) = TaxFilingDeadline.For(CitPayment.Pnd50Type, 2026, 12);
        Assert.Equal(new DateTime(2027, 5, 31), p50);                     // 30 พ.ค. 2570 = อาทิตย์ ⇒ เลื่อนเป็นจันทร์
        Assert.True(e50 > p50);
        // ยังไม่ยืนยันมาตรการ e-Filing ⇒ เตือนตามวันกระดาษ (ทิศไม่ประเมินขาด)
        Assert.Equal(p50, TaxFilingDeadline.WarnBy(CitPayment.Pnd50Type, 2026, 12));
        // แบบรายเดือนไม่ถูกแตะ
        Assert.Equal(new DateTime(2026, 4, 7), TaxFilingDeadline.For("WhtPnd3", 2026, 3).Paper);
    }

    [Fact]
    public void CIT_เงินเพิ่ม_ม27_จ่ายช้า_ทันกำหนดเป็นศูนย์()
    {
        var due = TaxFilingDeadline.WarnBy(CitPayment.Pnd51Type, 2026, 6);
        Assert.Equal(0m, LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd51Type, 2026, 6, 50_000m, due, null).Total);
        var late = LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd51Type, 2026, 6, 50_000m, due.AddDays(10), null);
        Assert.Equal(750m, late.Surcharge);                               // 1.5% × 1 เดือน (เศษของเดือน = 1)
        Assert.Equal("RD-27", late.SurchargeReference);
        Assert.Equal(0m, late.Penalty);                                   // ไม่เดาเบี้ยปรับ
        Assert.Contains("ม.35", late.FineNote);
    }

    // ═══ §67 ตรี ═══

    [Fact]
    public void มาตรา67ตรี_ประมาณการขาดเกิน25เปอร์เซ็นต์_เงินเพิ่ม20เปอร์เซ็นต์ของภาษีที่ชำระขาด()
    {
        // ประมาณการ 600,000 · จริง 1,000,000 (ขาด 40%) · ภาษีจริง 200,000 ⇒ ครึ่งปีควรจ่าย 100,000 · จ่ายไป 50,000 ⇒ ขาด 50,000 × 20% = 10,000
        var p = CitPayment.Section67Ter(new CitPayment.EstimateFacts(600_000m, 1_000_000m, 200_000m, 50_000m));
        Assert.True(p.Applies);
        Assert.Equal(10_000m, p.Amount);
    }

    [Theory]
    [InlineData(800_000, 1_000_000)]   // ขาด 20%
    [InlineData(750_000, 1_000_000)]   // ขาด 25% พอดี — "เกิน" เท่านั้น
    [InlineData(1_200_000, 1_000_000)] // ประมาณการสูงกว่า
    [InlineData(500_000, 0)]           // ขาดทุน/กำไร 0
    public void มาตรา67ตรี_ไม่ถึงเกณฑ์_เป็นศูนย์(int estimate, int actual)
    {
        var p = CitPayment.Section67Ter(new CitPayment.EstimateFacts(estimate, actual, 200_000m, 10_000m));
        Assert.False(p.Applies);
        Assert.Equal(0m, p.Amount);
    }

    [Fact]
    public void ภงด50_มาตรา67ตรีแยกก้อน_ไม่รวมเงินเพิ่ม_ไม่ทำให้ข้อความทันกำหนดหาย()
    {
        var due = TaxFilingDeadline.WarnBy(CitPayment.Pnd50Type, 2026, 12);
        var facts = new CitPayment.EstimateFacts(600_000m, 1_000_000m, 200_000m, 50_000m, "ยอดนำส่ง ภ.ง.ด.51 ในระบบ 45,000.00 + ภาษีถูกหักครึ่งปี 5,000.00");
        var onTime = LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd50Type, 2026, 12, 150_000m, due, null, citEstimate: facts);
        Assert.Equal(0m, onTime.Surcharge);                                // ฝ่ายค้าน S6: ไม่รวมในช่องเงินเพิ่ม
        Assert.Contains("ทันกำหนด", onTime.SurchargeNote);
        Assert.Equal(10_000m, onTime.Section67Ter);
        Assert.Contains("ฐาน: ยอดนำส่ง ภ.ง.ด.51", onTime.Section67TerNote);  // S5a บอกฐาน
        Assert.Contains("ตรวจสอบ", onTime.Section67TerNote);               // S5c ข้อยกเว้น
        var late = LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd50Type, 2026, 12, 150_000m, due.AddDays(5), null, citEstimate: facts);
        Assert.Equal(2_250m, late.Surcharge);                              // ม.27 เท่านั้น
        Assert.Equal(10_000m, late.Section67Ter);
        var noFacts = LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd50Type, 2026, 12, 150_000m, due, null);
        Assert.Equal(0m, noFacts.Section67Ter);
        Assert.Contains("ไม่พบ ภ.ง.ด.51", noFacts.Section67TerNote);
        // ภ.ง.ด.51 ไม่มี §67 ตรี
        var p51 = LatePaymentPenaltyPolicy.Suggest(CitPayment.Pnd51Type, 2026, 6, 50_000m,
            TaxFilingDeadline.WarnBy(CitPayment.Pnd51Type, 2026, 6), null, citEstimate: facts);
        Assert.Equal(0m, p51.Section67Ter);
        Assert.Null(p51.Section67TerNote);
        // รอบแรก ⇒ เตือนให้ตรวจสอบ
        Assert.Contains("รอบบัญชีแรก", CitPayment.Section67Ter(facts with { PossiblyFirstYear = true }).Note);
    }

    [Fact]
    public void CIT_ครบวงจร_ภงด51ถึงภงด50_11910และ11920กลับเป็นศูนย์()
    {
        // ฝ่ายค้าน S1: ภ.ง.ด.51 ภาษี 50,000 ภาษีถูกหักครึ่งปี 5,000 ⇒ ชำระ 45,000 · ภ.ง.ด.50 ภาษี 200,000 ภาษีถูกหักทั้งปี 10,000 · เครดิต 51 = 45,000
        var bal = new Dictionary<CitPayment.AccrualRole, decimal>();
        void Post(IEnumerable<CitPayment.AccrualLine> ls)
        {
            Assert.Equal(ls.Sum(x => x.Debit), ls.Sum(x => x.Credit));
            foreach (var l in ls) bal[l.Role] = bal.GetValueOrDefault(l.Role) + l.Debit - l.Credit;
        }
        bal[CitPayment.AccrualRole.WhtCredit] = 10_000m;                    // ลูกค้าหัก ณ ที่จ่ายทั้งปี (Dr 11910)
        var p51 = CitPayment.AccrualPlan(CitPayment.Pnd51Type, 50_000m, 0m, 5_000m);
        Assert.DoesNotContain(p51, l => l.Role == CitPayment.AccrualRole.WhtCredit);   // ไม่แตะ 11910
        Assert.Equal(45_000m, p51.Single(l => l.Role == CitPayment.AccrualRole.Prepaid).Debit);
        Post(p51);
        bal[CitPayment.AccrualRole.Payable] += 45_000m;                    // ชำระ ภ.ง.ด.51 (Dr 21920 / Cr ธนาคาร)
        var p50 = CitPayment.AccrualPlan(CitPayment.Pnd50Type, 200_000m, 45_000m, 10_000m);
        // ภ.ง.ด.50 ตั้งแบบใหม่: เครดิต ภ.ง.ด.51 Cr ผังเดียวกับที่ ภ.ง.ด.51 ลง Dr (11920)
        foreach (var l in p50) bal[l.Role == CitPayment.AccrualRole.PrepaidOffset ? CitPayment.AccrualRole.Prepaid : l.Role]
            = bal.GetValueOrDefault(l.Role == CitPayment.AccrualRole.PrepaidOffset ? CitPayment.AccrualRole.Prepaid : l.Role) + l.Debit - l.Credit;
        Assert.Equal(p50.Sum(x => x.Debit), p50.Sum(x => x.Credit));
        Assert.Equal(145_000m, -bal[CitPayment.AccrualRole.Payable]);
        bal[CitPayment.AccrualRole.Payable] += 145_000m;                   // ชำระ ภ.ง.ด.50
        Assert.Equal(0m, bal[CitPayment.AccrualRole.Prepaid]);             // 11920
        Assert.Equal(0m, bal[CitPayment.AccrualRole.WhtCredit]);           // 11910
        Assert.Equal(0m, bal[CitPayment.AccrualRole.Payable]);             // 21920
        Assert.Equal(200_000m, bal[CitPayment.AccrualRole.Expense]);       // ค่าใช้จ่ายภาษีครั้งเดียว
        // ภาษีครึ่งปีถูกภาษีถูกหักครอบทั้งก้อน ⇒ ไม่มี JE
        Assert.Empty(CitPayment.AccrualPlan(CitPayment.Pnd51Type, 5_000m, 0m, 6_000m));
    }

    [Fact]
    public void ชำระนอกระบบ_จับคู่FIFO_ตามวันตั้งหนี้และผัง()
    {
        var acc = Guid.NewGuid(); var other = Guid.NewGuid();
        var claims = new List<(Guid, DateTime, decimal)>
        {
            (acc, new DateTime(2025, 9, 1), 45_000m),   // ภ.ง.ด.51
            (acc, new DateTime(2026, 5, 1), 145_000m),  // ภ.ง.ด.50
        };
        var debits = new List<(Guid, DateTime, decimal)>
        {
            (acc, new DateTime(2025, 8, 1), 99_999m),   // ก่อนตั้งหนี้ — ไม่นับ
            (acc, new DateTime(2025, 9, 10), 45_000m),  // ชำระ ภ.ง.ด.51 เอง
            (other, new DateTime(2026, 5, 20), 145_000m), // ผังอื่น — ไม่นับ
            (acc, new DateTime(2026, 5, 25), 100_000m), // ชำระ ภ.ง.ด.50 บางส่วน
        };
        var m = StatutoryRemittanceService.MatchFifo(claims, debits);
        Assert.Equal(45_000m, m[0]);
        Assert.Equal(100_000m, m[1]);                  // บางส่วน ⇒ ยังขึ้นค้างพร้อมคำเตือน (ไม่เดา)
        Assert.Equal(new[] { 0m, 0m }, StatutoryRemittanceService.MatchFifo(claims, new List<(Guid, DateTime, decimal)>()));
    }

    [Fact]
    public void บวกกลับ65ตรี6_เครื่องหมายอยู่เฉพาะบรรทัดค่าปรับ_บรรทัดหักได้ไม่มี()
    {
        var lines = LatePaymentPenaltyPolicy.JournalLines("กท.20ก", new LatePaymentPenaltyPolicy.Applied(10m, 20m, 30m), "WCA-46").ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Contains(LatePaymentPenaltyPolicy.Section65Ter6Marker, l.Description));
        Assert.DoesNotContain(LatePaymentPenaltyPolicy.Section65Ter6Marker, WorkersCompensationBase.TrueUpLineDescription);      // 54121
        Assert.DoesNotContain(LatePaymentPenaltyPolicy.Section65Ter6Marker, WorkersCompensationBase.OverAccrualLineDescription);
        Assert.DoesNotContain(LatePaymentPenaltyPolicy.Section65Ter6Marker, StampDutySchedule.ExpenseLineDescription("สัญญา-1", 4)); // 54810
        foreach (var t in new[] { WorkersCompensationBase.RemittanceType, StampDutySchedule.RemittanceType, CitPayment.Pnd51Type, CitPayment.Pnd50Type })
            Assert.True(LatePaymentPenaltyPolicy.ExpenseLinesMixed(t));
        foreach (var t in new[] { "VatPp30", "VatPp36", "SsoSps110", "WhtPnd53" })
            Assert.False(LatePaymentPenaltyPolicy.ExpenseLinesMixed(t));   // แบบเดิมนับทุกบรรทัดค่าใช้จ่ายตามเดิม (JE ก่อนรอบ 220 ไม่มีเครื่องหมาย)
    }

    // ═══ กองทุนเงินทดแทน ═══

    [Fact]
    public void กองทุนเงินทดแทน_รายปีตัดเพดาน240000ทั้งปี()
    {
        Assert.Equal(480m, WorkersCompensationBase.AnnualContribution(300_000m, 0.2m));   // 240,000 × 0.2%
        Assert.Equal(200m, WorkersCompensationBase.AnnualContribution(100_000m, 0.2m));
        Assert.Equal(0m, WorkersCompensationBase.AnnualContribution(100_000m, 0m));
        // ค่าจ้างไม่สม่ำเสมอ: 30,000 หนึ่งเดือน + 10,000 สิบเอ็ดเดือน = 140,000 — รายเดือนตัด 20,000 ⇒ 130,000 · รายปี = 140,000
        var monthly = WorkersCompensationBase.Contribution(30_000m, 0.5m) + 11 * WorkersCompensationBase.Contribution(10_000m, 0.5m);
        Assert.Equal(650m, monthly);
        Assert.Equal(700m, WorkersCompensationBase.AnnualContribution(140_000m, 0.5m));
        Assert.Equal(20_000m, WorkersCompensationBase.WageFromContribution(100m, 0.5m));
    }

    [Fact]
    public void กองทุนเงินทดแทน_แผนJE_ดุลทุกกรณี()
    {
        void Balanced(WorkersCompensationBase.PaymentPlan p)
        {
            var dr = p.DrPayable + p.ReverseOverAccrual + p.DrExpenseTrueUp;
            var cr = p.ReverseOverAccrual + p.Cash;
            Assert.Equal(dr, cr);
        }
        var under = WorkersCompensationBase.Plan(annualContribution: 700m, accruedInPayroll: 650m, remittedBefore: 0m, amountNow: 700m);
        Assert.Equal((650m, 50m, 0m), (under.DrPayable, under.DrExpenseTrueUp, under.ReverseOverAccrual));
        Balanced(under);
        var over = WorkersCompensationBase.Plan(450m, 500m, 0m, 450m);   // ตั้งรายเดือนเกิน ⇒ ปิดปีแล้วกลับส่วนเกิน
        Assert.Equal((450m, 0m, 50m), (over.DrPayable, over.DrExpenseTrueUp, over.ReverseOverAccrual));
        Balanced(over);
        var partial = WorkersCompensationBase.Plan(450m, 500m, 0m, 200m); // ยังไม่ปิดปี ⇒ ไม่กลับ
        Assert.Equal((200m, 0m, 0m), (partial.DrPayable, partial.DrExpenseTrueUp, partial.ReverseOverAccrual));
        var second = WorkersCompensationBase.Plan(450m, 500m, 200m, 250m);
        Assert.Equal((250m, 0m, 50m), (second.DrPayable, second.DrExpenseTrueUp, second.ReverseOverAccrual));
        Balanced(second);
    }

    [Fact]
    public void กองทุนเงินทดแทน_กำหนดสิ้นกุมภาปีถัดไป_เงินเพิ่มเป็นข้อมูล_ไม่เกินเงินสมทบ()
    {
        var (paper, efiling) = TaxFilingDeadline.For(WorkersCompensationBase.RemittanceType, 2026, 12);
        Assert.Equal(new DateTime(2027, 3, 1), paper);    // 28 ก.พ. 2570 = อาทิตย์ ⇒ จันทร์
        Assert.Equal(paper, efiling);                      // ไม่อยู่ในมาตรการ e-Filing ของกระทรวงการคลัง
        Assert.Equal(0m, WorkersCompensationBase.LateSurcharge(10_000m, paper, paper).Amount);
        Assert.Equal(200m, WorkersCompensationBase.LateSurcharge(10_000m, paper, paper.AddDays(10)).Amount);   // 2% (ฉบับแก้ไข 2561 — ต้องยืนยัน)
        var capped = WorkersCompensationBase.LateSurcharge(10_000m, paper, paper.AddMonths(60));
        Assert.True(capped.Capped);
        Assert.Equal(10_000m, capped.Amount);
        var s = LatePaymentPenaltyPolicy.Suggest(WorkersCompensationBase.RemittanceType, 2026, 12, 10_000m, paper.AddDays(10), null);
        Assert.Equal(200m, s.Surcharge);
        Assert.True(s.SurchargeNeedsConfirmation);       // ฝ่ายค้าน S4: ข้อมูลเท่านั้น หน้าไม่เติม
        Assert.Equal(0m, LatePaymentPenaltyPolicy.Resolve(WorkersCompensationBase.RemittanceType, s, true, null, null, null).Surcharge);
        Assert.Equal(0m, s.Penalty);
        Assert.Equal(WorkersCompensationBase.LegalReference, s.SurchargeReference);
    }

    // ═══ ภ.พ.36 เบี้ยปรับที่เสนอ (ต้องยืนยัน) — ไม่ลงเองเมื่อไม่ส่งค่า ═══

    [Fact]
    public void ภพ36_เสนอเบี้ยปรับตารางลดเป็นข้อมูล_ไม่เติมช่อง_ไม่ส่งค่าไม่ลงบัญชี()
    {
        var due = LatePaymentPenaltyPolicy.DueDate("VatPp36", 2026, 3, null, paperFiling: true);
        var s = LatePaymentPenaltyPolicy.Suggest("VatPp36", 2026, 3, 10_000m, due.AddDays(20), null, paperFiling: true);
        Assert.Equal(500m, s.Penalty);                     // ช้า 20 วัน ⇒ 5%
        Assert.Contains("ต้องยืนยัน", s.PenaltyNote);
        Assert.True(s.PenaltyNeedsConfirmation);           // หน้าไม่เติมลงช่อง (PenaltyPrefill = 0)
        // ภ.พ.30 ตารางเดียวกันแต่ยืนยันแล้ว ⇒ เติมได้
        var pp30 = LatePaymentPenaltyPolicy.Suggest("VatPp30", 2026, 3, 10_000m,
            LatePaymentPenaltyPolicy.DueDate("VatPp30", 2026, 3, null, paperFiling: true).AddDays(20), null, paperFiling: true);
        Assert.False(pp30.PenaltyNeedsConfirmation);
        Assert.Equal(0m, LatePaymentPenaltyPolicy.Resolve("VatPp36", s, true, null, null, null).Penalty);
        Assert.Equal(0m, LatePaymentPenaltyPolicy.Suggest("VatPp36", 2026, 3, 10_000m, due, null, paperFiling: true).Penalty);
    }

    // ═══ อากรแสตมป์ ═══

    [Theory]
    [InlineData(4, 1_000, 1)]
    [InlineData(4, 1_500, 2)]          // เดิมปัดลงได้ 1 — "หรือเศษของ 1,000" ⇒ 2
    [InlineData(4, 100_001, 101)]
    [InlineData(4, 300_000_000, 300_000)]   // เดิมเพดาน 200,000 ที่บัญชีอัตราไม่มี
    [InlineData(1, 180_000, 180)]       // ค่าเช่ารวมทั้งสัญญา
    [InlineData(5, 3_001, 2)]           // ทุก 2,000 หรือเศษ
    [InlineData(5, 30_000_000, 10_000)] // เพดานกู้ยืม
    [InlineData(7, 0, 30)]
    [InlineData(17, 0, 3)]
    [InlineData(99, 50_000, 0)]
    [InlineData(4, 0, 0)]
    public void อากรแสตมป์_อัตราตามบัญชี(int schedule, int value, int duty)
        => Assert.Equal((decimal)duty, StampDutySchedule.Compute(schedule, value));

    [Fact]
    public void อากรแสตมป์_ครบกำหนดและเงินเพิ่มอากร()
    {
        var d = new DateTime(2026, 11, 2);                // จันทร์
        Assert.Equal(d, StampDutySchedule.DueDate(d, "Affix"));
        Assert.Equal(new DateTime(2026, 11, 17), StampDutySchedule.DueDate(d, "ESD"));
        var due = StampDutySchedule.DueDate(d, "ESD");
        Assert.Equal(0m, StampDutySchedule.SuggestSurcharge(100m, due, due).Amount);
        Assert.Equal(200m, StampDutySchedule.SuggestSurcharge(100m, due, due.AddDays(10)).Amount);   // 2 เท่า ม.114
        Assert.Equal(500m, StampDutySchedule.SuggestSurcharge(100m, due, due.AddDays(100)).Amount);  // 5 เท่า ม.113
        Assert.Equal(2m, StampDutySchedule.SuggestSurcharge(0.5m, due, due.AddDays(1)).Amount);      // ขั้นต่ำ
    }

    // ═══ ช่องว่างรอบ 225 — มัดจำ VAT พักที่ดึงเข้า ภ.พ.30 ═══

    [Fact]
    public void ดึงมัดจำVATพัก_ใช้ยอดที่GLย้ายเข้า21911_ไม่ใช่VATเต็มใบ()
    {
        var doc = new Document { IsDeposit = true, VatAmount = 70m, SubTotal = 1_000m, ExchangeRate = 1m, Lines = new List<DocumentLine>() };
        var none = new HashSet<Guid>();
        var moved = TaxService.PullLineAmounts(doc, isInput: false, none, depositVatDeferred: true, reclassifiedVatThb: 35m);
        Assert.Equal((500m, 35m), moved);                  // เท่าที่ย้ายจริง + ฐานตามสัดส่วน (ตัวเดียวกับ loop หลัก)
        // ทิศที่ห้ามแตะ: ไม่มีร่องรอยใน GL ⇒ VAT เต็มใบ · มัดจำ immediate ⇒ VAT เต็มใบ · ฝั่งซื้อไม่ผ่านทางนี้
        Assert.Equal((1_000m, 70m), TaxService.PullLineAmounts(doc, false, none, true, null));
        Assert.Equal((1_000m, 70m), TaxService.PullLineAmounts(doc, false, none, false, 35m));
        Assert.Equal((1_000m, 70m), TaxService.PullLineAmounts(doc, false, none));
    }
}
