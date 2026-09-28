using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 ทีม S3 — แก้ผลฝ่ายค้าน review198-B (นำเข้า/จับคู่) + review198-C (ลงบัญชี) + review198-E2 E2-10 · ตัวตัดสินบริสุทธิ์ทุกตัว
/// มีสองครึ่ง: เคสที่พังกลับมาถูก และเคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// </summary>
public class SettlementReview198FixTests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DocA = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid DocB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid BatchA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BatchB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime Day = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

    // ═════════════ R-B5/R-B6: คีย์กันซ้ำ v2 ═════════════

    [Fact]
    public void RB5_แถวไม่มีid_สองรอบโอนวันเดียวกัน_ค่าธรรมเนียมถอนเงิน10_เก็บทั้งคู่_ไฟล์เดิมนำเข้าซ้ำได้คีย์เดิม()
    {
        var fee = new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Day);
        var payout1 = SettlementTxnKey.Assign(new[] { fee with { PayoutRef = "PO-1" } });
        var payout2 = SettlementTxnKey.Assign(new[] { fee with { PayoutRef = "PO-2" } });
        Assert.NotEqual(payout1[0], payout2[0]);                                   // รายการจริงคนละรอบ — ห้ามชน

        // ทิศตรงข้าม: ไฟล์เดิม (รอบเดิม) นำเข้าซ้ำ ⇒ คีย์เดิมทุกแถว (0 แถวใหม่)
        var file = new[] { fee with { PayoutRef = "PO-1" }, new SettlementTxnKeyInput(null, "Sale", "O-1", 500m, Day, "PO-1") };
        Assert.Equal(SettlementTxnKey.Assign(file), SettlementTxnKey.Assign(file));
        Assert.All(SettlementTxnKey.Assign(file), k => Assert.StartsWith(SettlementTxnKey.Version, k));
    }

    [Fact]
    public void RB5_idเดียวกัน_คืนเงินสองครั้งยอดต่างกัน_หรือป้ายต่างแค่ตัวเลข_ได้คีย์คนละตัว()
    {
        var keys = SettlementTxnKey.Assign(new[]
        {
            new SettlementTxnKeyInput("T1", "Refund", "O-1", -100m, Day),
            new SettlementTxnKeyInput("T1", "Refund", "O-1", -50m, Day.AddDays(3)),
            new SettlementTxnKeyInput("T2", "ค่าธรรมเนียม 3%", null, -30m, Day),
            new SettlementTxnKeyInput("T2", "ค่าธรรมเนียม 5%", null, -30m, Day),
        });
        Assert.Equal(4, keys.Distinct().Count());
        Assert.All(keys, k => Assert.DoesNotContain("#", k));                      // ไม่ต้องพึ่งเลขลำดับจากแถวข้างเคียง
    }

    [Fact]
    public void RB5_แถวเหมือนกันทุกช่องในไฟล์เดียว_ยังเป็นสองรายการ_ลำดับนับเฉพาะแถวที่เหมือนกัน()
    {
        var dup = new SettlementTxnKeyInput(null, "ค่าบริการ", "O-9", -10m, Day, "PO-1");
        var other = new SettlementTxnKeyInput(null, "ค่าบริการ", "O-9", -11m, Day, "PO-1");
        var withNeighbour = SettlementTxnKey.Assign(new[] { other, dup, dup });
        var alone = SettlementTxnKey.Assign(new[] { dup, dup });
        Assert.EndsWith("#2", alone[1]);
        Assert.Equal(alone, withNeighbour.Skip(1).ToList());                       // แถวข้างเคียงที่ไม่เหมือนกันไม่เปลี่ยนคีย์ (R-A9)
    }

    [Fact]
    public void RB6_คีย์ไม่มีPII_ไม่ขึ้นกับตัวตัดPII_ป้ายต่างแค่ช่องว่างหรือตัวพิมพ์ได้คีย์เดิม_intentคงคีย์()
    {
        var k = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T9", "คุณสมมติ ทดสอบ 081-234-5678", null, 10m, Day) })[0];
        Assert.DoesNotContain("สมมติ", k);
        Assert.DoesNotContain("081", k);
        var a = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "Commission  Fee", null, -5m, Day) })[0];
        var b = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", " commission fee ", null, -5m, Day) })[0];
        Assert.Equal(a, b);
        Assert.Equal("fee 3%", SettlementTxnKey.FrozenLabel("  Fee   3% "));    // คงตัวเลข
        var pi = SettlementTxnKey.ForPaymentIntent(DocA, "sale");
        Assert.Equal(pi, SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput(pi, "Payment", "ch_1", 100m, Day) })[0]);
    }

    // ═════════════ R-B14: คำตอบตัวจัดประเภทต้องเป็นชื่อเดียว ═════════════

    [Theory]
    [InlineData("Sale, Refund")]
    [InlineData("Commission,PaymentFee")]
    [InlineData("Sale|Refund")]
    public void RB14_คำตอบหลายชื่อ_ไม่กลายเป็นประเภทที่สาม(string answer)
        => Assert.Null(SettlementLineTypeRules.ParseClassifierAnswer(answer));

    [Theory]
    [InlineData("sale", SettlementLineType.Sale)]
    [InlineData(" Refund ", SettlementLineType.Refund)]
    [InlineData("ShippingFeeCharged", SettlementLineType.ShippingFeeCharged)]
    public void RB14_ชื่อเดียวตรงตัว_ยังรับได้(string answer, SettlementLineType expected)
        => Assert.Equal(expected, SettlementLineTypeRules.ParseClassifierAnswer(answer));

    // ═════════════ R-B1: การจับคู่ที่คนตัดสิน ═════════════

    [Fact]
    public void RB1_คนตัดสินแล้ว_เปลี่ยนประเภทในกลุ่มเดิม_คงคำตัดสิน_ข้ามกลุ่มหรือไม่ต้องจับคู่_ล้าง()
    {
        Assert.True(SettlementSaleMatch.KeepUserMatch(true, SettlementLineType.Sale, SettlementLineType.Sale));
        Assert.True(SettlementSaleMatch.KeepUserMatch(true, SettlementLineType.Sale, SettlementLineType.PlatformVoucherSubsidy));
        Assert.False(SettlementSaleMatch.KeepUserMatch(true, SettlementLineType.Sale, SettlementLineType.Refund));
        Assert.False(SettlementSaleMatch.KeepUserMatch(true, SettlementLineType.Sale, SettlementLineType.Commission));
        // ทิศตรงข้าม: ระบบจับคู่เอง ⇒ จับใหม่ได้ตามเดิม
        Assert.False(SettlementSaleMatch.KeepUserMatch(false, SettlementLineType.Sale, SettlementLineType.Sale));
    }

    // ═════════════ C-1(b): รอบที่ลงบัญชีค้างครึ่งทาง ═════════════

    [Fact]
    public void C1_รอบที่ลงค้างครึ่งทาง_แก้และยกเลิกไม่ได้_รอบที่ยังไม่มีของ_แก้ได้ตามเดิม()
    {
        Assert.False(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Matched, 2));
        Assert.True(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Matched, 0));
        Assert.True(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Imported, 0));
        Assert.False(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Posted, 0));
    }

    // ═════════════ R-B2/R-B4/R-B12/E2-10: ผู้สมัครจาก PaymentIntent ═════════════

    private static SettlementIntentFact Intent(bool usable = true, bool received = true, bool legacy = false, Guid? owner = null,
        decimal amount = 1000m, decimal refunded = 0m, decimal counted = 0m, bool unknown = false)
        => new(DocA, "ch_1", amount, refunded, usable, received, legacy, owner, counted, unknown);

    [Fact]
    public void RB4_พบintentแต่ช่องทางใช้ไม่ได้_ไม่ตกใบขายสรุป_ให้คนตัดสินพร้อมเหตุผล()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(usable: false), BatchA);
        Assert.False(c.CanReceive);
        var d = SettlementSaleMatch.Decide(SettlementLineType.Sale, "ch_1", 1000m, new[] { c });
        Assert.Equal(SettlementMatchStatus.Unmatched, d.Status);
        Assert.Contains("ห้ามออกใบขายสรุปซ้ำ", d.Note);
        // ทิศตรงข้าม: ไม่มีร่องรอยเลย ⇒ ใบขายสรุปตามเดิม · ช่องทางใช้ได้ ⇒ จับคู่ intent
        Assert.Equal(SettlementMatchStatus.AutoSummary,
            SettlementSaleMatch.Decide(SettlementLineType.Sale, "ch_1", 1000m, Array.Empty<SettlementMatchCandidate>()).Status);
        var ok = SettlementSaleMatch.Decide(SettlementLineType.Sale, "ch_1", 1000m, new[] { SettlementSaleMatch.IntentCandidate(Intent(), BatchA) });
        Assert.Equal(SettlementMatchStatus.Matched, ok.Status);
        Assert.Equal(DocA, ok.PaymentIntentId);
    }

    [Fact]
    public void RB12_ยอดขายในไฟล์ไม่เท่ายอดที่รับชำระผ่านintent_AmountMismatch()
    {
        var d = SettlementSaleMatch.Decide(SettlementLineType.Sale, "ch_1", 800m, new[] { SettlementSaleMatch.IntentCandidate(Intent(), BatchA) });
        Assert.Equal(SettlementMatchStatus.AmountMismatch, d.Status);
        Assert.Equal(DocA, d.PaymentIntentId);
    }

    [Fact]
    public void RB2_คืนเงินภายหลังของintentที่รอบก่อนเป็นเจ้าของ_จับคู่ได้_ไม่ได้ข้อความเท็จว่าไม่เคยคืน()
    {
        var late = SettlementSaleMatch.IntentCandidate(Intent(owner: BatchB, refunded: 300m), BatchA);
        Assert.False(late.CanReceive);                                              // ยอดขายเป็นของรอบก่อน
        Assert.True(late.IsRefundTarget);
        var d = SettlementSaleMatch.Decide(SettlementLineType.Refund, "ch_1", 0m, new[] { late });
        Assert.Equal(SettlementMatchStatus.Matched, d.Status);

        // นับครบแล้วในรอบอื่น ⇒ ไม่ใช่ต้นทาง + เหตุผลตามสาเหตุจริง (ไม่ใช่ "ยังไม่เคยคืน")
        var counted = SettlementSaleMatch.IntentCandidate(Intent(owner: BatchB, refunded: 300m, counted: 300m), BatchA);
        var dc = SettlementSaleMatch.Decide(SettlementLineType.Refund, "ch_1", 0m, new[] { counted });
        Assert.Equal(SettlementMatchStatus.Unmatched, dc.Status);
        Assert.Contains("ครบ", dc.Note);
        Assert.DoesNotContain("ยังไม่เคยคืนเงิน", dc.Note);
        // ไม่เคยคืนผ่านระบบ ⇒ ข้อความเดิม
        var never = SettlementSaleMatch.Decide(SettlementLineType.Refund, "ch_1", 0m,
            new[] { SettlementSaleMatch.IntentCandidate(Intent(), BatchA) });
        Assert.Contains("ยังไม่เคยคืนเงินผ่านระบบ", never.Note);
    }

    [Fact]
    public void RB2_ยอดคืนผ่านระบบจัดสรรทีละบรรทัด_บรรทัดที่เกินให้คนเลือก()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(refunded: 50m), BatchA);
        var d = SettlementSaleMatch.Decide(SettlementLineType.Refund, "ch_1", 0m, new[] { c });
        var (first, left) = SettlementSaleMatch.ApplyIntentRefundCapacity(d, -50m, c.RefundRemaining!.Value);
        Assert.Equal(SettlementMatchStatus.Matched, first.Status);
        var (second, _) = SettlementSaleMatch.ApplyIntentRefundCapacity(d, -50m, left);
        Assert.Equal(SettlementMatchStatus.Unmatched, second.Status);
        Assert.Null(second.PaymentIntentId);
    }

    [Fact]
    public void E2_10_คืนเงินผลไม่แน่ชัด_intentใช้รับชำระหรือคืนเงินไม่ได้_จนกว่าจะตรวจผล()
    {
        var c = SettlementSaleMatch.IntentCandidate(Intent(refunded: 300m, unknown: true), BatchA);
        Assert.False(c.CanReceive);
        Assert.False(c.IsRefundTarget);
        var d = SettlementSaleMatch.Decide(SettlementLineType.Sale, "ch_1", 1000m, new[] { c });
        Assert.Equal(SettlementMatchStatus.Unmatched, d.Status);
        Assert.Contains("ตรวจผลการคืนเงิน", d.Note);
        // ทิศตรงข้าม: intent เดียวกันที่ผลคืนเงินชัดแล้ว ⇒ รับได้ตามปกติ
        Assert.True(SettlementSaleMatch.IntentCandidate(Intent(refunded: 300m), BatchA).CanReceive);
    }

    [Fact]
    public void RB2_ด่านผู้ลงบัญชี_บรรทัดคืนเงินของintentที่รอบก่อนเป็นเจ้าของ_ไม่ใช่นับซ้ำ_บรรทัดขายยังนับซ้ำ()
    {
        Assert.False(SettlementSaleMatch.IntentSettledElsewhere(true, null, BatchB, BatchA));
        Assert.True(SettlementSaleMatch.IntentSettledElsewhere(false, null, BatchB, BatchA));
        Assert.True(SettlementSaleMatch.IntentSettledElsewhere(true, Guid.NewGuid(), null, BatchA));     // เส้นเดิมบันทึกแล้ว
        Assert.False(SettlementSaleMatch.IntentSettledElsewhere(false, null, BatchA, BatchA));
    }

    [Fact]
    public void RB16_แก้ค่าธรรมเนียมของintentที่อยู่ในรอบโอนแล้ว_บอกว่าไม่มีผล_ไม่เงียบ()
    {
        Assert.Contains("รอบโอน settlement", SettlementSaleMatch.FeeEditBlockedByBatch(BatchA));
        Assert.Null(SettlementSaleMatch.FeeEditBlockedByBatch(null));
    }

    // ═════════════ C-1(a): ล็อกเดียวของทั้งสองฝั่ง ═════════════

    [Fact]
    public void C1_ล็อกนำเข้ากับล็อกลงบัญชีเป็นคีย์เดียวกัน_คนละช่องทางคนละคีย์()
    {
        var ch = Guid.NewGuid();
        // ฝั่งลงบัญชีส่ง (Scope, Part) เข้า JobLock ซึ่งคำนวณ AdvisoryLockKey.For(companyId, scope, part) — ต้องเท่ากับคีย์ของฝั่งนำเข้า
        Assert.Equal(AdvisoryLockKey.For(Co, SettlementChannelLock.Scope, SettlementChannelLock.Part(ch)), SettlementChannelLock.Key(Co, ch));
        Assert.Equal(AdvisoryLockKey.SettlementImport, SettlementChannelLock.Scope);
        Assert.NotEqual(SettlementChannelLock.Key(Co, ch), SettlementChannelLock.Key(Co, Guid.NewGuid()));
        Assert.NotEqual(SettlementChannelLock.Key(Co, ch), AdvisoryLockKey.For(Co, SettlementPostingKeys.LockScope, SettlementChannelLock.Part(ch)));
    }

    // ═════════════ C-2: ด่านก่อนยกเลิกการลงบัญชี ═════════════

    private static SettlementUnpostDocument Sum(bool accepted = false, bool locked = false)
        => new(DocA, "TIV-0001", DocumentType.TaxInvoice, SettlementPostingKeys.SummaryComponent(Day), Day, 70m, false, accepted, locked);

    private static SettlementUnpostDocument Fee(bool foreign = false)
        => new(DocB, "PV-0001", DocumentType.PaymentVoucher, SettlementPostingKeys.FeeComponent(SettlementFeeVatTreatment.InputVatPending),
            Day, 7m, foreign, false, false);

    [Fact]
    public void C2_eTaxตอบรับแล้ว_หรือภพ30ประกาศว่ายื่นแล้ว_ปฏิเสธทั้งรอบก่อนแตะชิ้นแรก()
    {
        var none = new HashSet<(TaxType, int, int)>();
        Assert.NotEmpty(SettlementUnpostGate.Evaluate(new[] { Sum(accepted: true), Fee() }, Array.Empty<SettlementUnpostCertificate>(), none));
        Assert.NotEmpty(SettlementUnpostGate.Evaluate(new[] { Sum(locked: true) }, Array.Empty<SettlementUnpostCertificate>(), none));
        var declared = new HashSet<(TaxType, int, int)> { (TaxType.VAT, 2026, 9) };
        var r = SettlementUnpostGate.Evaluate(new[] { Sum(), Fee() }, Array.Empty<SettlementUnpostCertificate>(), declared);
        Assert.Contains(r, x => x.Subject == "TIV-0001" && x.Reason.Contains("ภ.พ.30"));
        Assert.DoesNotContain(r, x => x.Subject == "PV-0001");                     // ภาษีซื้อรอใบกำกับไม่อยู่ใน ภ.พ.30 ของเดือนนั้น
        Assert.Contains("ใบลดหนี้", r[0].NextStep);
        // ทิศตรงข้าม: ไม่มีอะไรยื่น/ตอบรับ ⇒ ยกเลิกได้
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { Sum(), Fee() }, Array.Empty<SettlementUnpostCertificate>(), none));
    }

    [Fact]
    public void C2_ภพ36และ50ทวิที่ยื่นแล้ว_ปฏิเสธ_50ทวิร่าง_ไม่ปฏิเสธ()
    {
        var filed = new HashSet<(TaxType, int, int)> { (TaxType.VatPp36, 2026, 9), (TaxType.WithholdingTax53, 2026, 9) };
        Assert.NotEmpty(SettlementUnpostGate.Evaluate(new[] { Fee(foreign: true) }, Array.Empty<SettlementUnpostCertificate>(), filed));
        var issued = new SettlementUnpostCertificate(Guid.NewGuid(), DocB, "WHT-2609-0001", TaxType.WithholdingTax53, 2026, 9,
            WithholdingTaxCertStatus.Issued);
        Assert.NotEmpty(SettlementUnpostGate.Evaluate(new[] { Fee() }, new[] { issued }, filed));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { Fee() }, new[] { issued with { Status = WithholdingTaxCertStatus.Draft } }, filed));
        Assert.Empty(SettlementUnpostGate.Evaluate(new[] { Fee() }, new[] { issued }, new HashSet<(TaxType, int, int)>()));
    }

    [Fact]
    public void C2_ลำดับยกเลิกคงที่_ฝั่งขายก่อนใบค่าธรรมเนียม()
    {
        var order = SettlementUnpostGate.VoidOrder(new[] { Fee(), Sum() });
        Assert.Equal("TIV-0001", order[0].Number);
        Assert.Equal("PV-0001", order[1].Number);
    }

    [Fact]
    public void C2_50ทวิที่อยู่ในแบบที่ยื่นแล้ว_ยกเลิกไม่ได้_ร่างหรือยังไม่ยื่น_ยกเลิกได้()
    {
        Assert.NotNull(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-1", TaxType.WithholdingTax53, 2026, 9, true));
        Assert.NotNull(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Printed, "WHT-1", TaxType.WithholdingTax3, 2026, 9, true));
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Issued, "WHT-1", TaxType.WithholdingTax53, 2026, 9, false));
        Assert.Null(WhtCertVoidGuard.Reason(WithholdingTaxCertStatus.Draft, "WHT-1", TaxType.WithholdingTax53, 2026, 9, true));
    }

    // ═════════════ C-3: 50 ทวิ ค้างเป็นร่าง ═════════════

    [Fact]
    public void C3_ร่างที่ค้างถูกออกตอนทำต่อ_ออกแล้วยอดตรงไม่ทำซ้ำ_ยอดไม่ตรงล้มดัง()
    {
        Assert.Equal(SettlementWhtCertStep.Create, SettlementWhtCertResume.Decide(null, null, 30.93m));
        Assert.Equal(SettlementWhtCertStep.IssueDraft, SettlementWhtCertResume.Decide(WithholdingTaxCertStatus.Draft, 30.93m, 30.93m));
        Assert.Equal(SettlementWhtCertStep.Done, SettlementWhtCertResume.Decide(WithholdingTaxCertStatus.Issued, 30.93m, 30.93m));
        Assert.Equal(SettlementWhtCertStep.Stale, SettlementWhtCertResume.Decide(WithholdingTaxCertStatus.Draft, 20m, 30.93m));
    }

    // ═════════════ C-4/C-5: การรับชำระค้าง · ความครบก่อนประทับ Posted ═════════════

    private static SettlementPostingPlan PlanWithReceiptAndSummary()
    {
        var batch = new SettlementBatch { CompanyId = Co, PayoutRef = "PO-001", PayoutDate = Day, NetPayout = 1963m, BankAccountId = Guid.NewGuid() };
        var channel = new SettlementChannel
        {
            CompanyId = Co, Kind = SettlementChannelKind.Marketplace, DisplayName = "Shopee", ClearingAccountId = Clearing,
            CounterpartyContactId = Guid.NewGuid(),
        };
        var lines = new[]
        {
            new SettlementLine { CompanyId = Co, Seq = 1, LineType = SettlementLineType.Sale, Amount = 1000m, MatchedDocumentId = DocA,
                TxnDate = Day, MatchStatus = SettlementMatchStatus.Matched },
            new SettlementLine { CompanyId = Co, Seq = 2, LineType = SettlementLineType.Sale, Amount = 1070m, TxnDate = Day,
                ExternalOrderId = "SP-1", MatchStatus = SettlementMatchStatus.AutoSummary },
            new SettlementLine { CompanyId = Co, Seq = 3, LineType = SettlementLineType.Commission, Amount = -107m, TxnDate = Day,
                MatchStatus = SettlementMatchStatus.NotRequired },
        };
        return SettlementBatchMath.Plan(batch, lines, channel, true);
    }

    [Fact]
    public void C4_การรับชำระค้างจากครั้งก่อน_ใบอื่นหรือยอดต่าง_ฟ้อง_ตรงแผน_ไม่ฟ้อง()
    {
        var plan = PlanWithReceiptAndSummary();
        var r = Assert.Single(plan.Receipts);
        Assert.Empty(SettlementReceiptReconcile.Stale(plan.Receipts, new[] { new SettlementMarkerPayment(Guid.NewGuid(), r.DocumentId, r.Amount, "RV-1") }));
        Assert.Empty(SettlementReceiptReconcile.Stale(plan.Receipts, Array.Empty<SettlementMarkerPayment>()));
        Assert.Single(SettlementReceiptReconcile.Stale(plan.Receipts, new[] { new SettlementMarkerPayment(Guid.NewGuid(), DocB, 500m, "RV-2") }));
        Assert.Single(SettlementReceiptReconcile.Stale(plan.Receipts, new[] { new SettlementMarkerPayment(Guid.NewGuid(), r.DocumentId, r.Amount - 1m, "RV-3") }));
    }

    [Fact]
    public void C5_ประทับPostedได้เมื่อทุกชิ้นมีและออกแล้วยอดตรง_ขาดชิ้นใดฟ้อง()
    {
        var plan = PlanWithReceiptAndSummary();
        var feeComp = SettlementPostingKeys.FeeComponent(Assert.Single(plan.FeeDocuments).VatTreatment);
        var sumComp = SettlementPostingKeys.SummaryComponent(Assert.Single(plan.SummarySales).Date);
        var expected = new Dictionary<string, decimal> { [feeComp] = 107m, [sumComp] = 1070m };
        var docs = new List<SettlementPostedDocumentFact>
        {
            new(feeComp, "PV-1", DocumentStatus.Approved, 107m), new(sumComp, "TIV-1", DocumentStatus.Approved, 1070m),
        };
        var r = plan.Receipts[0];
        var pays = new[] { new SettlementMarkerPayment(Guid.NewGuid(), r.DocumentId, r.Amount, "RV-1") };
        Assert.Empty(SettlementPostingCompleteness.Missing(plan, expected, docs, pays));

        Assert.NotEmpty(SettlementPostingCompleteness.Missing(plan, expected, docs.Take(1).ToList(), pays));           // ใบสรุปถูกยกเลิกไประหว่างทาง
        Assert.NotEmpty(SettlementPostingCompleteness.Missing(plan, expected,
            new List<SettlementPostedDocumentFact> { docs[0], docs[1] with { Status = DocumentStatus.Draft } }, pays));     // ยังไม่ออก
        Assert.NotEmpty(SettlementPostingCompleteness.Missing(plan, expected,
            new List<SettlementPostedDocumentFact> { docs[0], docs[1] with { Total = 1000m } }, pays));                    // ยอดไม่ตรง
        Assert.NotEmpty(SettlementPostingCompleteness.Missing(plan, expected, docs, Array.Empty<SettlementMarkerPayment>())); // ยังไม่รับชำระ
        Assert.NotEmpty(SettlementPostingCompleteness.Missing(plan, new Dictionary<string, decimal>(), docs, pays));      // ไม่ได้ตรวจยอดรอบนี้
    }

    [Fact]
    public void C1c_แผนเดิมลายนิ้วมือเดิม_บรรทัดถูกแก้ลายนิ้วมือเปลี่ยน()
    {
        var a = PlanWithReceiptAndSummary();
        Assert.Equal(SettlementPlanFingerprint.Of(a), SettlementPlanFingerprint.Of(PlanWithReceiptAndSummary()));
        var changed = a with { Receipts = new[] { a.Receipts[0] with { Amount = a.Receipts[0].Amount + 1m } } };
        Assert.NotEqual(SettlementPlanFingerprint.Of(a), SettlementPlanFingerprint.Of(changed));
        // ปัญหาที่ด่านเติมทีหลังไม่ทำให้ต่าง (เทียบแผนก่อนด่านกับหลังด่านได้)
        Assert.Equal(SettlementPlanFingerprint.Of(a), SettlementPlanFingerprint.Of(a with { Issues = Array.Empty<SettlementPlanIssue>() }));
    }

    // ═════════════ C-6 · C-4 · C-1(d) · E2-10 ในด่านผู้ลงบัญชี ═════════════

    private static SettlementPostingFacts Facts(SettlementPostingPlan plan) => new(
        SettlementBatchStatus.Matched, Day, Day, null, new Dictionary<DateTime, string>(),
        new HashSet<(int, int)>(), new HashSet<(int, int)>(), Array.Empty<string>(),
        true, true, Clearing,
        plan.Receipts.Select(r => new SettlementReceiptTarget(r.DocumentId, true, "TIV-0001", DocumentType.TaxInvoice,
            DocumentStatus.Approved, r.Amount, false)).ToList(),
        Array.Empty<SettlementClearingSource>(), Array.Empty<SettlementDuplicateSale>(), true, true, 0);

    [Fact]
    public void C6_ใบขายสรุปมีVAT_บริษัทไม่มีสิทธิ์86_6_บล็อกพร้อมทางไปต่อ_มีสิทธิ์หรือไม่จดVAT_ไม่แตะ()
    {
        var plan = PlanWithReceiptAndSummary();
        Assert.True(plan.CanPost);
        var blocked = SettlementPostingGate.Evaluate(plan, Facts(plan) with
        {
            SummaryAbbreviatedBlock = AbbreviatedInvoiceBlockReason.NotRetailBusiness,
        });
        Assert.False(blocked.CanPost);
        var issue = Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.SummaryTaxInvoiceNotAllowed);
        Assert.Contains("ประกอบกิจการขายปลีก", issue.NextStep);
        Assert.True(SettlementPostingGate.Evaluate(plan, Facts(plan)).CanPost);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Facts(plan) with
            {
                SummaryAbbreviatedBlock = AbbreviatedInvoiceBlockReason.NotVatRegistered,
            }).Issues, i => i.Code == SettlementPlanIssueCode.SummaryTaxInvoiceNotAllowed);
    }

    [Fact]
    public void C4_C1d_E210_การรับชำระค้าง_ของกำพร้า_คืนเงินผลไม่แน่ชัด_บล็อก_ไม่มี_ไม่บล็อก()
    {
        var plan = PlanWithReceiptAndSummary();
        var stale = SettlementPostingGate.Evaluate(plan, Facts(plan) with { StaleReceipts = new[] { "การรับชำระ RV-9 ไม่อยู่ในแผน" } });
        Assert.Contains(stale.Issues, i => i.Code == SettlementPlanIssueCode.StaleDocument && i.Blocking);
        var orphan = SettlementPostingGate.Evaluate(plan, Facts(plan) with { OrphanArtifacts = new[] { "เอกสาร PV-9 ของรอบที่ยกเลิก" } });
        Assert.Contains(orphan.Issues, i => i.Code == SettlementPlanIssueCode.OrphanPostingArtifacts && i.Blocking);
        Assert.False(orphan.CanPost);
        var lineId = Guid.NewGuid();
        var unknown = SettlementPostingGate.Evaluate(plan, Facts(plan) with
        {
            ClearingSources = new[] { new SettlementClearingSource(new[] { lineId }, "รายการรับชำระออนไลน์ ch_1", true, Clearing, false, true) },
        });
        var u = Assert.Single(unknown.Issues, i => i.Code == SettlementPlanIssueCode.RefundOutcomeUnknown);
        Assert.Contains("ตรวจผลการคืนเงินก่อน", u.NextStep);
        // ทิศตรงข้าม: รายการเดียวกันที่ผลคืนเงินชัดแล้ว ⇒ ไม่มีปัญหา
        Assert.True(SettlementPostingGate.Evaluate(plan, Facts(plan) with
        {
            ClearingSources = new[] { new SettlementClearingSource(new[] { lineId }, "รายการรับชำระออนไลน์ ch_1", true, Clearing, false) },
        }).CanPost);
    }

    // ═════════════ C-7 (คำตัดสินเจ้าของข้อ 7): ผู้อนุมัติ = คนกดลงบัญชี · แยกหน้าที่ ═════════════

    [Fact]
    public void C7_เปิดแยกหน้าที่_ผู้นำเข้ากดลงบัญชีเอง_บล็อก_คนอื่นกด_หรือไม่เปิด_ผ่าน()
    {
        var importer = Guid.NewGuid();
        Assert.True(SettlementPostingGate.SodSelfApproval(true, importer.ToString(), importer));
        Assert.True(SettlementPostingGate.SodSelfApproval(true, importer.ToString().ToUpperInvariant(), importer));
        Assert.True(SettlementPostingGate.SodSelfApproval(true, null, importer));                   // ไม่รู้ผู้ทำ ⇒ ไม่ปล่อยผ่าน
        Assert.False(SettlementPostingGate.SodSelfApproval(true, importer.ToString(), Guid.NewGuid()));
        Assert.False(SettlementPostingGate.SodSelfApproval(false, importer.ToString(), importer));

        var plan = PlanWithReceiptAndSummary();
        var blocked = SettlementPostingGate.Evaluate(plan, Facts(plan) with { SodSelfApprovalBlocked = true });
        Assert.False(blocked.CanPost);
        Assert.Contains("คนอื่น", Assert.Single(blocked.Issues, i => i.Code == SettlementPlanIssueCode.SodSelfApproval).NextStep);
        Assert.DoesNotContain(SettlementPostingGate.Evaluate(plan, Facts(plan)).Issues, i => i.Code == SettlementPlanIssueCode.SodSelfApproval);
    }

    // ═════════════ C-5: ยกเลิกชิ้นของรอบโอนทีละชิ้นผ่านหน้าปกติ ═════════════

    [Fact]
    public void C5_ป้ายเจ้าของอ่านได้จากCreatedByและNotes_ของที่ไม่ใช่ของรอบโอนไม่ถูกตีความ()
    {
        Assert.Equal(BatchA, SettlementArtifactGuard.BatchIdFromCreator(SettlementPostingKeys.Creator(BatchA, "fee-InputVatPending")));
        Assert.Null(SettlementArtifactGuard.BatchIdFromCreator("user-1"));
        Assert.Null(SettlementArtifactGuard.BatchIdFromCreator(null));
        Assert.Equal(BatchA, SettlementArtifactGuard.BatchIdFromPaymentNotes(SettlementPostingKeys.PaymentMarker(BatchA) + " รับเงินผ่าน Shopee"));
        Assert.Null(SettlementArtifactGuard.BatchIdFromPaymentNotes("[SETTLEMENT:ไม่ใช่เลข]"));
        Assert.Null(SettlementArtifactGuard.BatchIdFromPaymentNotes("โอนเงินปกติ"));
    }

    [Fact]
    public void C5_รอบโอนลงบัญชีแล้ว_ยกเลิกทีละชิ้นไม่ได้_ผ่านUnpost_หรือรอบค้างครึ่งทาง_หรือรอบถูกยกเลิก_ได้()
    {
        Assert.NotNull(SettlementArtifactGuard.VoidBlockedReason(SettlementBatchStatus.Posted, false, false, "PO-1"));
        Assert.NotNull(SettlementArtifactGuard.VoidBlockedReason(SettlementBatchStatus.BankMatched, false, false, "PO-1"));
        Assert.Null(SettlementArtifactGuard.VoidBlockedReason(SettlementBatchStatus.Posted, false, true, "PO-1"));
        Assert.Null(SettlementArtifactGuard.VoidBlockedReason(SettlementBatchStatus.Matched, false, false, "PO-1"));
        Assert.Null(SettlementArtifactGuard.VoidBlockedReason(SettlementBatchStatus.Posted, true, false, "PO-1"));
        Assert.Null(SettlementArtifactGuard.VoidBlockedReason(null, false, false, null));
    }

    [Fact]
    public void C5_ขอบเขตUnpost_เฉพาะรอบโอนนั้น_ออกจากขอบเขตแล้วกลับเป็นเดิม()
    {
        Assert.False(SettlementUnpostScope.IsUnposting(BatchA));
        using (SettlementUnpostScope.Enter(BatchA))
        {
            Assert.True(SettlementUnpostScope.IsUnposting(BatchA));
            Assert.False(SettlementUnpostScope.IsUnposting(BatchB));
        }
        Assert.False(SettlementUnpostScope.IsUnposting(BatchA));
    }
}
