using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม T — เวลา/ภาษีของรอบโอน settlement: review198-A R-A11/R-A12 · review198-B R-B13 · review198-C C-11/C-12/C-15/C-16/C-17 ·
/// คำตัดสินรอบ 200 ข้อ 15 (ใบสรุปเพิ่มเติมของวันเดียวกัน) · ข้อ 16 (O-3 ทางไปต่อ) · ข้อ 20 (รายงาน JE เก่าลงผิดหมวด 112) ·
/// ทุกเรื่องมีสองครึ่ง: เคสที่ต้องถูกกัน/ถูกเตือน และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementRound200TimeTaxTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BankAcc = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Counterparty = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel(SettlementFeeVatMode vat = SettlementFeeVatMode.ThaiVat7,
        SettlementFeeWhtMode wht = SettlementFeeWhtMode.None)
        => new()
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee ร้านหลัก",
            ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht, CounterpartyContactId = Counterparty,
        };

    private static SettlementBatch Batch(decimal net, DateTime? payout = null)
        => new() { CompanyId = Co, PayoutRef = "PO-200", PayoutDate = payout ?? Day, NetPayout = net, BankAccountId = BankAcc };

    private static int _seq;
    private static SettlementLine L(SettlementLineType t, decimal amount, Guid? doc = null, DateTime? txn = null, string? order = null,
        SettlementMatchStatus? match = null, bool noDate = false)
        => new()
        {
            CompanyId = Co, Seq = ++_seq, LineType = t, Amount = amount, MatchedDocumentId = doc, TxnDate = noDate ? null : txn ?? Day,
            ExternalOrderId = order,
            MatchStatus = match ?? (doc is not null ? SettlementMatchStatus.Matched : SettlementMatchStatus.AutoSummary),
        };

    private static SettlementPostingFacts Ok(SettlementPostingPlan plan) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    // ═════════════ R-A12: ยอด wallet ต่อเนื่องข้ามรอบโอน ═════════════

    [Fact]
    public void RA12_ต้นรอบไม่เท่าปลายรอบก่อน_บล็อกพร้อมผลต่าง_เท่ากันไม่แตะ_รอบแรกเตือนว่าไม่รู้()
    {
        var prev = new SettlementWalletPrevious("PO-199", Day.AddDays(-7), -100m);
        var gap = SettlementWalletContinuity.Judge(0m, prev);
        Assert.Equal(SettlementWalletContinuityKind.Gap, gap.Kind);
        Assert.Equal(100m, gap.Difference);
        Assert.Equal(SettlementWalletContinuityKind.Continuous, SettlementWalletContinuity.Judge(-100m, prev).Kind);
        Assert.Equal(SettlementWalletContinuityKind.Continuous, SettlementWalletContinuity.Judge(-99.99m, prev).Kind);   // เกณฑ์เดียวกับสมการรอบโอน
        Assert.Equal(SettlementWalletContinuityKind.NoPreviousBatch, SettlementWalletContinuity.Judge(0m, null).Kind);

        // สมการภายในรอบลงตัว (ผู้กรอกยอดยกมา 0 และไฟล์มีบรรทัดหักยอดติดลบเก่า) — ตัวเดียวที่จับได้คือความต่อเนื่อง
        var plan = SettlementBatchMath.Plan(Batch(1000m), new[] { L(SettlementLineType.Sale, 1000m, DocA) }, Channel(), true);
        Assert.True(plan.CanPost);
        var blocked = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Wallet = gap });
        Assert.False(blocked.CanPost);
        var issue = Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.WalletContinuityGap);
        Assert.True(issue.Blocking);
        Assert.Contains("PO-199", issue.Message);
        Assert.Contains("นำเข้าใหม่", issue.NextStep);

        // ทิศตรงข้าม: ต่อเนื่อง ⇒ ไม่มีปัญหา · ไม่ได้ตรวจ (null) ⇒ ไม่ประดิษฐ์ปัญหา · รอบแรก ⇒ เตือน ไม่บล็อก
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan) with { Wallet = SettlementWalletContinuity.Judge(-100m, prev) }).CanPost);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Ok(plan)).Issues,
            i => i.Code is SettlementPlanIssueCode.WalletContinuityGap or SettlementPlanIssueCode.WalletContinuityUnknown);
        var first = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Wallet = SettlementWalletContinuity.Judge(0m, null) });
        Assert.True(first.CanPost);
        Assert.Contains(first.Issues, i => i.Code == SettlementPlanIssueCode.WalletContinuityUnknown && !i.Blocking);
    }

    // ═════════════ R-B13: รับชำระเกินข้ามรอบโอน ═════════════

    [Fact]
    public void RB13_ยอดที่รอบอื่นยังไม่ลงจับคู่ใบเดียวกัน_นับตามกติกาแผน()
    {
        var bA = Guid.NewGuid();
        var bB = Guid.NewGuid();
        var bC = Guid.NewGuid();
        var lines = new[]
        {
            new SettlementOtherBatchSaleLine(bA, "PO-A", DocA, SettlementLineType.Sale, 1070m, false, SettlementMatchStatus.Matched),
            new SettlementOtherBatchSaleLine(bA, "PO-A", DocA, SettlementLineType.SellerVoucher, -70m, false, SettlementMatchStatus.Matched),
            // ไม่นับ: อยู่ในผังพักแล้ว · ยอดไม่ตรงที่ยังไม่ยืนยัน · ค่าธรรมเนียม · รอบที่รับชำระใบนี้ไปแล้ว (ยอดค้างลดแล้ว)
            new SettlementOtherBatchSaleLine(bB, "PO-B", DocA, SettlementLineType.Sale, 500m, true, SettlementMatchStatus.Matched),
            new SettlementOtherBatchSaleLine(bB, "PO-B", DocA, SettlementLineType.Sale, 300m, false, SettlementMatchStatus.AmountMismatch),
            new SettlementOtherBatchSaleLine(bB, "PO-B", DocA, SettlementLineType.Commission, -40m, false, SettlementMatchStatus.NotRequired),
            new SettlementOtherBatchSaleLine(bC, "PO-C", DocA, SettlementLineType.Sale, 200m, false, SettlementMatchStatus.Matched),
        };
        var pending = SettlementCrossBatchReceipts.PendingElsewhere(lines, new HashSet<(Guid, Guid)> { (bC, DocA) });
        var p = Assert.Single(pending).Value;
        Assert.Equal(1000m, p.Amount);
        Assert.Equal("PO-A", Assert.Single(p.PayoutRefs));
        Assert.Empty(SettlementCrossBatchReceipts.PendingElsewhere(Array.Empty<SettlementOtherBatchSaleLine>(), new HashSet<(Guid, Guid)>()));
    }

    [Fact]
    public void RB13_สองรอบจับคู่ใบ1000เดียวกันคนละ1000_บล็อก_ผ่อนสองรอบ600บวก400ผ่าน()
    {
        var plan = SettlementBatchMath.Plan(Batch(1000m), new[] { L(SettlementLineType.Sale, 1000m, DocA) }, Channel(), true);
        SettlementReceiptTarget Target(decimal pending) => new(DocA, true, "TIV-0009", DocumentType.TaxInvoice, DocumentStatus.Approved, 1000m,
            false, pending, pending > 0m ? new[] { "PO-OTHER" } : null);

        var dup = SettlementPostingGate.Evaluate(plan, Ok(plan) with { ReceiptTargets = new[] { Target(1000m) } });
        Assert.False(dup.CanPost);
        var i = Assert.Single(dup.Issues, x => x.Code == SettlementPlanIssueCode.ReceiptDocumentNotPayable);
        Assert.Contains("PO-OTHER", i.Message);
        Assert.Contains("รับชำระเกิน", i.Message);
        Assert.Contains("ถอดการจับคู่", i.NextStep);

        // ทิศตรงข้าม: ผ่อนสองรอบรวมไม่เกินยอดค้าง ⇒ ผ่าน · ไม่มีรอบอื่น ⇒ ผ่าน (ด่านเดิมไม่เปลี่ยน)
        var split = SettlementBatchMath.Plan(Batch(600m), new[] { L(SettlementLineType.Sale, 600m, DocA) }, Channel(), true);
        Assert.True(SettlementPostingGate.Evaluate(split, Ok(split) with { ReceiptTargets = new[] { Target(400m) } }).CanPost);
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan) with { ReceiptTargets = new[] { Target(0m) } }).CanPost);
    }

    // ═════════════ C-11: ด่านเดือนที่ยื่นแล้ว — ภ.ง.ด. ตามผู้รับเงินจริง + ภ.พ.36 ═════════════

    [Fact]
    public void C11_ผู้รับเงินบุคคลธรรมดา_เดือนภงด3ที่ยื่นแล้ว_บล็อกด้วยชื่อแบบที่ถูก()
    {
        var plan = SettlementBatchMath.Plan(Batch(930m),
            new[] { L(SettlementLineType.Sale, 2000m, DocA), L(SettlementLineType.Commission, -1070m) },
            Channel(wht: SettlementFeeWhtMode.SelfWithholdPayerBorne), true);
        Assert.True(plan.CanPost);
        var filed = new HashSet<(int, int)> { (2026, 9) };
        var g = SettlementPostingGate.Evaluate(plan, Ok(plan) with { FiledWhtPeriods = filed, WhtFormType = TaxType.WithholdingTax3 });
        Assert.False(g.CanPost);
        var i = Assert.Single(g.Issues, x => x.Code == SettlementPlanIssueCode.TaxPeriodFiled);
        Assert.Contains("ภ.ง.ด.3", i.Message);
        Assert.DoesNotContain("ภ.ง.ด.53", i.Message);
        // ทิศตรงข้าม: เดือนนั้นยังไม่ยื่น ⇒ ผ่าน · ค่าเริ่มต้นยังเป็น ภ.ง.ด.53
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan) with { WhtFormType = TaxType.WithholdingTax3 }).CanPost);
        Assert.Contains("ภ.ง.ด.53", Assert.Single(SettlementPostingGate.Evaluate(plan, Ok(plan) with { FiledWhtPeriods = filed }).Issues,
            x => x.Code == SettlementPlanIssueCode.TaxPeriodFiled).Message);
    }

    [Fact]
    public void C11_ค่าธรรมเนียมต่างประเทศ_เดือนภพ36ที่ยื่นแล้ว_บล็อก_เดือนอื่นหรือไม่มีภพ36ไม่แตะ()
    {
        var plan = SettlementBatchMath.Plan(Batch(900m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -100m) },
            Channel(vat: SettlementFeeVatMode.ForeignPp36), true);
        Assert.Equal(7m, plan.FeeDocuments.Sum(d => d.Pp36Payable));
        var g = SettlementPostingGate.Evaluate(plan, Ok(plan) with { FiledPp36Periods = new HashSet<(int, int)> { (2026, 9) } });
        Assert.Contains(g.Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled && i.Blocking && i.Message.Contains("ภ.พ.36"));
        Assert.False(g.CanPost);

        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Ok(plan) with { FiledPp36Periods = new HashSet<(int, int)> { (2026, 8) } }).Issues,
            i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Ok(plan)).Issues, i => i.Code == SettlementPlanIssueCode.TaxPeriodFiled);
        // ช่องทางในประเทศ (ไม่มี ภ.พ.36) ⇒ เดือน ภ.พ.36 ที่ยื่นแล้วไม่เกี่ยว
        var thai = SettlementBatchMath.Plan(Batch(900m),
            new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -100m) }, Channel(), true);
        Assert.True(SettlementPostingGate.Evaluate(thai, Ok(thai) with { FiledPp36Periods = new HashSet<(int, int)> { (2026, 9) } }).CanPost);
    }

    // ═════════════ C-15: ใบสรุปไม่ตัดสต็อก ═════════════

    [Fact]
    public void C15_กิจการถือสต็อกหรือยังไม่ระบุ_เตือน_กิจการบริการหรือไม่ได้ตรวจ_ไม่เตือน()
    {
        Assert.Equal(SettlementStockStance.KeepsInventory, SettlementStock.StanceOf(IndustryType.Trading));
        Assert.Equal(SettlementStockStance.KeepsInventory, SettlementStock.StanceOf(IndustryType.Ecommerce));
        Assert.Equal(SettlementStockStance.NoInventory, SettlementStock.StanceOf(IndustryType.Service));
        Assert.Equal(SettlementStockStance.Unknown, SettlementStock.StanceOf(IndustryType.General));
        Assert.Equal(SettlementStockStance.Unknown, SettlementStock.StanceOf(null));

        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m, order: "SP-1") }, Channel(), true);
        Assert.Single(plan.SummarySales);
        var keeps = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Stock = SettlementStockStance.KeepsInventory });
        Assert.True(keeps.CanPost);
        Assert.Contains(keeps.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleNoStock && !i.Blocking && i.Message.Contains("ต้นทุนขาย"));
        Assert.Contains(SettlementPostingGate.Evaluate(plan, Ok(plan) with { Stock = SettlementStockStance.Unknown }).Issues,
            i => i.Code == SettlementPlanIssueCode.SummarySaleNoStock && i.NextStep.Contains("ประเภทธุรกิจ"));
        foreach (var st in new[] { SettlementStockStance.NoInventory, SettlementStockStance.NotChecked })
            Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Ok(plan) with { Stock = st }).Issues,
                i => i.Code == SettlementPlanIssueCode.SummarySaleNoStock);
        // ไม่มีใบสรุป (รับชำระใบที่จับคู่) ⇒ ไม่เตือน แม้กิจการถือสต็อก (ใบขายเดิมตัดสต็อกไปแล้ว)
        var matched = SettlementBatchMath.Plan(Batch(1000m), new[] { L(SettlementLineType.Sale, 1000m, DocA) }, Channel(), true);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(matched, Ok(matched) with { Stock = SettlementStockStance.KeepsInventory }).Issues,
            i => i.Code == SettlementPlanIssueCode.SummarySaleNoStock);
    }

    // ═════════════ C-16: ค่าธรรมเนียมข้ามเดือน ═════════════

    [Fact]
    public void C16_ค่าธรรมเนียมเดือนกันยาโอนตุลา_เตือนไม่บล็อก_เดือนเดียวกันหรือไม่มีวันที่ไม่เตือน()
    {
        var payout = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var sep = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var plan = SettlementBatchMath.Plan(Batch(948.48m, payout),
            new[] { L(SettlementLineType.Sale, 1000m, DocA, txn: sep), L(SettlementLineType.Commission, -51.52m, txn: sep) }, Channel(), true);
        Assert.True(plan.CanPost);
        var i = Assert.Single(plan.Issues, x => x.Code == SettlementPlanIssueCode.FeeCutoffCrossesMonth);
        Assert.False(i.Blocking);
        Assert.Contains("09/2569", i.Message);
        Assert.Contains("10/2569", i.Message);
        Assert.Equal(51.52m, i.Amount);

        // ทิศตรงข้าม: ค่าธรรมเนียมเดือนเดียวกับวันเงินเข้า · ไม่มีวันที่รายการ · ตี 1 วันที่ 1 ต.ค. เวลาไทย (= 30 ก.ย. 18:00Z) = ต.ค.
        var oct = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var fee in new[]
                 {
                     L(SettlementLineType.Commission, -51.52m, txn: oct),
                     L(SettlementLineType.Commission, -51.52m, noDate: true),
                     L(SettlementLineType.Commission, -51.52m, txn: new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc)),
                 })
            Assert.DoesNotContain(SettlementBatchMath.Plan(Batch(948.48m, payout), new[] { L(SettlementLineType.Sale, 1000m, DocA, txn: oct), fee },
                Channel(), true).Issues, x => x.Code == SettlementPlanIssueCode.FeeCutoffCrossesMonth);
    }

    // ═════════════ C-17: คืนเงินของออเดอร์ที่อยู่ในใบสรุปของรอบนี้เอง ═════════════

    [Fact]
    public void C17_คืนเงินของออเดอร์ในใบสรุปรอบเดียวกัน_บอกทางที่ทำได้จริง_ออเดอร์อื่นยังบอกให้เลือกใบเดิม()
    {
        var plan = SettlementBatchMath.Plan(Batch(535m),
            new[] { L(SettlementLineType.Sale, 1070m, order: "SP-5"), L(SettlementLineType.Refund, -535m, order: "SP-5", match: SettlementMatchStatus.Unmatched) },
            Channel(), true);
        Assert.False(plan.CanPost);
        var i = Assert.Single(plan.Issues, x => x.Code == SettlementPlanIssueCode.RefundUnmatched);
        Assert.Contains("ใบขายสรุปที่รอบนี้", i.Message);
        Assert.Contains("ออกเอกสารขายของออเดอร์ SP-5", i.NextStep);
        Assert.Contains("86/10", i.NextStep);

        var other = SettlementBatchMath.Plan(Batch(535m),
            new[] { L(SettlementLineType.Sale, 1070m, order: "SP-5"), L(SettlementLineType.Refund, -535m, order: "SP-6", match: SettlementMatchStatus.Unmatched) },
            Channel(), true);
        var j = Assert.Single(other.Issues, x => x.Code == SettlementPlanIssueCode.RefundUnmatched);
        Assert.StartsWith("เลือกใบขายเดิม", j.NextStep);
    }

    // ═════════════ คำตัดสินข้อ 15 (O-2 · C-9): ใบสรุปเพิ่มเติมของวันเดียวกัน ═════════════

    [Fact]
    public void ข้อ15_ใบแรกของวันคือใบที่สร้างก่อน_ใบแรกออกเลขแล้วเตือน_ยังไม่ออกบล็อก()
    {
        var t0 = new DateTime(2026, 9, 21, 3, 0, 0, DateTimeKind.Utc);
        var sameDay = new[]
        {
            new SettlementSameDaySummary("TIV-0012", "PO-2", true, t0.AddHours(2)),
            new SettlementSameDaySummary("TIV-0010", "PO-1", true, t0),
        };
        var sale = L(SettlementLineType.Sale, 1070m, order: "SP-7");
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { sale }, Channel(), true);
        var s = Assert.Single(plan.SummarySales);
        var sup = SettlementSummarySupplement.Judge(s.Date, s.LineIds, sameDay);
        Assert.NotNull(sup);
        Assert.Equal("TIV-0010", sup!.FirstNumber);
        Assert.Equal(2, sup.ExistingCount);
        Assert.Null(SettlementSummarySupplement.Judge(s.Date, s.LineIds, Array.Empty<SettlementSameDaySummary>()));

        var ok = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Supplementary = new[] { sup! } });
        Assert.True(ok.CanPost);                                                   // เดิมบล็อก SummarySaleDuplicate
        Assert.Contains(ok.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleSupplementary && !i.Blocking && i.Message.Contains("TIV-0010"));
        Assert.DoesNotContain(ok.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleDuplicate);

        var draftFirst = SettlementSummarySupplement.Judge(s.Date, s.LineIds,
            new[] { new SettlementSameDaySummary("DRAFT-x", "PO-1", false, t0) });
        var blocked = SettlementPostingGate.Evaluate(plan, Ok(plan) with { Supplementary = new[] { draftFirst! } });
        Assert.False(blocked.CanPost);
        Assert.Contains(blocked.Issues, i => i.Code == SettlementPlanIssueCode.SummarySaleFirstNotIssued && i.NextStep.Contains("PO-1"));

        // รายได้ซ้ำระดับออเดอร์ยังบล็อกเหมือนเดิม (ใบสรุปเพิ่มเติมไม่ใช่ทางลัดข้ามด่านนี้)
        var dup = SettlementPostingGate.Evaluate(plan, Ok(plan) with
        {
            Supplementary = new[] { sup! },
            DuplicateSales = new[] { new SettlementDuplicateSale(new[] { sale.Id }, "ออเดอร์ SP-7 ลงรายได้ไปแล้วในรอบโอน PO-1") },
        });
        Assert.False(dup.CanPost);
    }

    [Fact]
    public void ข้อ15_ใบสรุปเพิ่มเติม_อ้างใบแรก_วันที่และยอดเท่าเดิม_ไม่มีใบแรกไม่เปลี่ยนข้อความ()
    {
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m) }, Channel(), true);
        var s = Assert.Single(plan.SummarySales);
        var normal = SettlementDocumentBuilder.SummaryDocument(s, true, Guid.NewGuid(), Clearing, "PO-2", "Shopee");
        var sup = SettlementDocumentBuilder.SummaryDocument(s, true, Guid.NewGuid(), Clearing, "PO-2", "Shopee", "TIV-0010");
        Assert.Contains("TIV-0010", sup.Lines[0].Description);
        Assert.Contains("เพิ่มเติม", sup.Lines[0].Description);
        Assert.Contains("TIV-0010", sup.Notes);
        Assert.DoesNotContain("เพิ่มเติม", normal.Lines[0].Description);
        Assert.Equal(normal.DocumentDate, sup.DocumentDate);                    // จุดความรับผิดวันเดิม
        Assert.Equal(normal.DeliveryDate, sup.DeliveryDate);
        Assert.Equal(normal.DocumentType, sup.DocumentType);
        Assert.Equal(normal.Lines[0].UnitPrice, sup.Lines[0].UnitPrice);
        Assert.Equal(normal.Lines[0].VatAmountOverride, sup.Lines[0].VatAmountOverride);
        Assert.DoesNotContain("เพิ่มเติม", SettlementDocumentBuilder.SummaryDocument(s, true, Guid.NewGuid(), Clearing, "PO-2", "Shopee", "  ").Lines[0].Description);
    }

    // ═════════════ คำตัดสินข้อ 16 (O-3 · C-6): คงบล็อก + ทางไปต่อ ═════════════

    [Fact]
    public void ข้อ16_ใบสรุปมีVATบริษัทไม่ใช่ขายปลีก_คงบล็อก_ทางไปต่อบอกว่าไม่ต้องใช้ภพ06()
    {
        var plan = SettlementBatchMath.Plan(Batch(1070m), new[] { L(SettlementLineType.Sale, 1070m) }, Channel(), true);
        var g = SettlementPostingGate.Evaluate(plan, Ok(plan) with { SummaryAbbreviatedBlock = AbbreviatedInvoiceBlockReason.NotRetailBusiness });
        Assert.False(g.CanPost);
        var i = Assert.Single(g.Issues, x => x.Code == SettlementPlanIssueCode.SummaryTaxInvoiceNotAllowed);
        Assert.Equal(SettlementPostingGate.SummaryRetailNextStep, i.NextStep);
        Assert.Contains("ประกอบกิจการขายปลีก", i.NextStep);
        Assert.Contains("ไม่ต้องขอ ภ.พ.06", i.NextStep);
        Assert.Contains("เต็มรูป", i.NextStep);
        Assert.True(SettlementPostingGate.Evaluate(plan, Ok(plan)).CanPost);   // บริษัทมีสิทธิ์ ⇒ ไม่แตะ
    }

    // ═════════════ C-12 · R-A11: ใบที่ builder ประกอบ → สูตรของ DocumentService ได้ยอดเท่าแผน ═════════════

    [Fact]
    public void RA11_ใบค่าธรรมเนียมและใบสรุป_ผ่านสูตรDocumentService_ยอดเท่าที่ถูกหัก_ทุกยอด()
    {
        var misses = new List<decimal>();
        for (var cents = 1; cents <= 30000; cents++)
        {
            var amount = cents / 100m;
            var fee = SettlementBatchMath.Plan(Batch(1000m - amount),
                new[] { L(SettlementLineType.Sale, 1000m, DocA), L(SettlementLineType.Commission, -amount) }, Channel(), true);
            var f = Assert.Single(fee.FeeDocuments);
            var req = SettlementDocumentBuilder.FeeDocument(f, new[] { Guid.NewGuid() }, Counterparty, Clearing, Day, "PO", "Shopee");
            var total = DocumentService.PreviewTotals(req.Lines, req.PricesIncludeVat, 0m).Total;
            if (total != f.Deducted || total != amount) misses.Add(amount);

            var sum = SettlementBatchMath.Plan(Batch(amount), new[] { L(SettlementLineType.Sale, amount) }, Channel(), true);
            var s = Assert.Single(sum.SummarySales);
            var sreq = SettlementDocumentBuilder.SummaryDocument(s, true, Guid.NewGuid(), Clearing, "PO", "Shopee");
            if (DocumentService.PreviewTotals(sreq.Lines, sreq.PricesIncludeVat, 0m).Total != amount) misses.Add(-amount);
        }
        Assert.Empty(misses);   // ทีม A เคยพบ 13,084/200,000 ยอดที่ ×7% ไม่เท่าการถอด 7/107 (เช่น 1.15) — ใบต้องส่ง VAT ของแผนตรง
    }

    // ═════════════ คำตัดสินข้อ 20: รายงาน JE เก่าที่ลงขาเงิน/ลูกหนี้ผิดหมวด (อ่านอย่างเดียว) ═════════════

    [Fact]
    public void ข้อ20_JEเก่าลงขาเงิน112_ลูกหนี้ไม่ใช่11310_POSลง112หรือ113อื่น_ถูกรายงาน_ของถูกไม่ถูกแตะ()
    {
        Assert.Equal(LegacyMoneyLegAudit.IntegrationMoney112,
            LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "รับชำระ (banktransfer)", "11200", true)?.RuleCode);
        Assert.Equal(LegacyMoneyLegAudit.IntegrationMoney112,
            LegacyMoneyLegAudit.Classify("จ่ายชำระ PI-0001", "จ่ายชำระ (promptpay)", "11210", false)?.RuleCode);
        Assert.Equal(LegacyMoneyLegAudit.IntegrationReceivable,
            LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "ตัดลูกหนี้ INV-0001", "11340", false)?.RuleCode);
        Assert.Equal(LegacyMoneyLegAudit.PosMoney,
            LegacyMoneyLegAudit.Classify("ขาย POS", "รับเงิน QR POS #A-12", "11200", true)?.RuleCode);
        Assert.Equal(LegacyMoneyLegAudit.PosMoney,
            LegacyMoneyLegAudit.Classify("ขาย POS", "รับเงิน บัตร POS #A-13", "11320", true)?.RuleCode);
        var f = LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "รับชำระ (creditcard)", "11200", true)!;
        Assert.Contains("ไม่แก้อัตโนมัติ", f.Fix);
        Assert.Contains("เงินลงทุนชั่วคราว", f.Message);

        // ทิศตรงข้าม: ผังที่ถูก · JE อื่นที่ลง 112 โดยตั้งใจ (ซื้อเงินลงทุน) · ขาเครดิตของ POS · ข้อมูลว่าง
        Assert.Null(LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "รับชำระ (banktransfer)", "11120", true));
        Assert.Null(LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "ตัดลูกหนี้ INV-0001", "11310", false));
        Assert.Null(LegacyMoneyLegAudit.Classify("รับชำระ INV-0001", "ตัดลูกหนี้ INV-0001", "11340", true));
        Assert.Null(LegacyMoneyLegAudit.Classify("ขาย POS", "รับเงิน บัตร POS #A-13", "11340", true));
        Assert.Null(LegacyMoneyLegAudit.Classify("ขาย POS", "รับเงิน POS #A-14", "11111", true));
        Assert.Null(LegacyMoneyLegAudit.Classify("ขาย POS", "จ่ายคืนเงิน POS #A-15", "11200", false));
        Assert.Null(LegacyMoneyLegAudit.Classify("ซื้อเงินลงทุนระยะสั้น", "ฝากประจำ 3 เดือน", "11210", true));
        Assert.Null(LegacyMoneyLegAudit.Classify(null, null, null, true));
    }
}
