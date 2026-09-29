using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม P2 — settlement เฟส 2: รายการ payment gateway (PaymentIntent) เข้ารอบโอน <c>SettlementBatch</c> ของช่องทาง Gateway
/// (<c>Helpers/GatewayBatchIntentRules</c> · <c>Adapters/PaymentIntentAdapter</c> · <c>SettlementImportService.Gateway.cs</c>)
///
/// <para>สองครึ่งทุกเรื่อง: (1) ของที่พัง — โหมด VAT ของ config ถูกเพิกเฉย (บวก VAT เพิ่ม ⇒ ยอดไม่ลงตัว · ไม่แยก VAT + ช่องทาง VAT 7% ⇒ ภาษีซื้อแต่งขึ้น)
/// · VAT ปัดรวมทั้งก้อนไม่ตรงเส้นเดิม · ยอดคืนเงินนับทั้งที่เกิดหลังวันเงินเข้า · บรรทัดคืนเงินซ้ำข้ามช่องทาง (2) ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ —
/// โหมดที่ตรงกันผ่าน · รายการที่เส้นเดิมเป็นเจ้าของไม่ถูกดึง · marketplace ไม่ถูกแตะ · รายได้ไม่ถูกนับซ้ำ (ไม่มีใบขายสรุป)</para>
/// </summary>
public class SettlementGatewayPhase2Tests
{
    private static readonly Guid Co = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Clearing = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Bank = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime T0 = new(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc);
    private const string Provider = "gw";

    private static SettlementChannel Channel(SettlementFeeVatMode vat, SettlementFeeWhtMode wht = SettlementFeeWhtMode.None,
        SettlementChannelKind kind = SettlementChannelKind.Gateway)
        => new() { CompanyId = Co, Kind = kind, DisplayName = "gateway", ClearingAccountId = Clearing, FeeVatMode = vat, FeeWhtMode = wht };

    private static SettlementBatch Batch(decimal net)
        => new() { CompanyId = Co, PayoutRef = "PO-1", PayoutDate = T0.Date.AddDays(2), NetPayout = net, BankAccountId = Bank };

    private static SettlementIntentSnapshot Fresh(decimal amount, decimal? feeActual, decimal refundedAsOf = 0m, decimal feeEstimated = 0m)
        => new(Guid.NewGuid(), "ch_" + Guid.NewGuid().ToString("N")[..6], amount, refundedAsOf, feeActual, feeEstimated, T0, false, 0m);

    /// <summary>แถวจาก adapter → บรรทัดรอบโอนแบบที่ <c>PersistAsync</c> เก็บ (ประเภทจาก adapter · VAT ต่อรายการ · พก PaymentIntentId)</summary>
    private static List<SettlementLine> ToLines(IEnumerable<SettlementParsedRow> rows)
        => rows.Select((r, i) => new SettlementLine
        {
            CompanyId = Co, Seq = i + 1, LineType = r.ExplicitType!.Value, Amount = r.Amount, VatAmount = r.VatAmount,
            PaymentIntentId = r.PaymentIntentId, TxnDate = r.TxnDate, MatchStatus = SettlementMatchStatus.Unmatched,
        }).ToList();

    private static SettlementIntentInput Input(SettlementIntentSnapshot s)
        => new(s.Id, s.Amount, s.FeeActual, s.FeeEstimated, s.RefundedAmount);

    /// <summary>แผนของเส้นเดิมที่ยอดโอน = ยอดที่คำนวณได้ (ตัวเทียบ — "สูตรเดียว" ต้องได้ตัวเลขเท่ากันทุกช่อง)</summary>
    private static SettlementPlan LegacyPlan(IReadOnlyList<SettlementIntentSnapshot> snaps, GatewayFeeVatMode mode, bool vatRegistered = true)
    {
        var inputs = snaps.Select(Input).ToList();
        var net = inputs.Sum(i => GatewaySettlementMath.Contribution(i, mode).Net);
        var plan = GatewaySettlementMath.Plan(inputs, net, GatewayFeeWhtMode.None, "PO-1", mode, vatRegistered);
        Assert.True(plan.Ok, plan.Message);
        return plan;
    }

    // ═════════════════ สูตรเดียวกับเส้นเดิม (ค่าธรรมเนียม · VAT ค่าธรรมเนียม · ผังพัก) ═════════════════

    [Fact]
    public void บวกVATเพิ่ม_บรรทัดค่าธรรมเนียมเป็นยอดที่ถูกหักจริง_รอบโอนลงตัวและภาษีซื้อเท่าเส้นเดิม()
    {
        // ค่าธรรมเนียมก่อน VAT 39.06 + VAT 2.73 · 18.25 + 1.28 ⇒ ถูกหัก 61.32 · เดิม adapter ใส่ -57.31 (ก่อน VAT) ⇒ ต่าง 4.01 ทุกรอบ
        var snaps = new[] { Fresh(1070m, 39.06m), Fresh(500m, 18.25m) };
        var rows = PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.AddedOnTop);
        var fees = rows.Rows.Where(r => r.ExplicitType == SettlementLineType.PaymentFee).ToList();
        Assert.Equal(new[] { -41.79m, -19.53m }, fees.Select(r => r.Amount));
        Assert.Equal(new decimal?[] { -2.73m, -1.28m }, fees.Select(r => r.VatAmount));

        var legacy = LegacyPlan(snaps, GatewayFeeVatMode.AddedOnTop);
        var plan = SettlementBatchMath.Plan(Batch(legacy.ExpectedNet), ToLines(rows.Rows), Channel(SettlementFeeVatMode.ThaiVat7), true);
        Assert.True(plan.CanPost, string.Join(" | ", plan.Issues.Select(i => i.Message)));
        Assert.True(plan.Balanced);
        Assert.Equal(1508.68m, plan.NetPayout);
        var doc = Assert.Single(plan.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.InputVatPending, doc.VatTreatment);
        Assert.Equal(legacy.FeeNetPaid, doc.Deducted);                  // 61.32
        Assert.Equal(legacy.FeeVat, doc.InputVat);                      // 4.01 → 11630
        Assert.Equal(legacy.FeeNetPaid - legacy.FeeVat, doc.Expense);   // 57.31
        Assert.Equal(legacy.Gross, plan.AlreadyInClearing);             // 1,570 ล้างผังพัก ไม่ใช่ขายใหม่
    }

    [Fact]
    public void รวมVATในค่าธรรมเนียม_VATปัดต่อรายการเท่าเส้นเดิม_ไม่ใช่ปัดรวมทั้งก้อน()
    {
        // 10.00 ×3: ต่อรายการ VAT 0.65 ×3 = 1.95 (ผู้ให้บริการคิดต่อ charge · เส้นเดิม) · ถ้าให้รอบโอนแยก 7/107 จากก้อน 30.00 = 1.96 (สองความจริง)
        var snaps = new[] { Fresh(300m, 10m), Fresh(300m, 10m), Fresh(300m, 10m) };
        var rows = PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.IncludedInFee);
        Assert.All(rows.Rows.Where(r => r.ExplicitType == SettlementLineType.PaymentFee), r =>
        {
            Assert.Equal(-10m, r.Amount);
            Assert.Equal(-0.65m, r.VatAmount);
        });
        var legacy = LegacyPlan(snaps, GatewayFeeVatMode.IncludedInFee);
        Assert.Equal(1.95m, legacy.FeeVat);
        var plan = SettlementBatchMath.Plan(Batch(legacy.ExpectedNet), ToLines(rows.Rows), Channel(SettlementFeeVatMode.ThaiVat7), true);
        Assert.True(plan.CanPost);
        var doc = Assert.Single(plan.FeeDocuments);
        Assert.Equal(1.95m, doc.InputVat);
        Assert.Equal(28.05m, doc.Expense);
    }

    [Fact]
    public void ไม่แยกVAT_ช่องทางไม่มีVAT_ค่าธรรมเนียมเป็นค่าใช้จ่ายทั้งก้อนเท่าเส้นเดิม()
    {
        var snaps = new[] { Fresh(1070m, 41.79m) };
        var rows = PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.None);
        Assert.Null(Assert.Single(rows.Rows, r => r.ExplicitType == SettlementLineType.PaymentFee).VatAmount);
        var legacy = LegacyPlan(snaps, GatewayFeeVatMode.None);
        var plan = SettlementBatchMath.Plan(Batch(legacy.ExpectedNet), ToLines(rows.Rows), Channel(SettlementFeeVatMode.None), true);
        Assert.True(plan.CanPost);
        var doc = Assert.Single(plan.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.NoVat, doc.VatTreatment);
        Assert.Equal(0m, doc.InputVat);
        Assert.Equal(41.79m, doc.Expense);
    }

    [Fact]
    public void บริษัทไม่จดVAT_VATค่าธรรมเนียมเป็นต้นทุน_ไม่มีภาษีซื้อ_เท่าเส้นเดิม()
    {
        var snaps = new[] { Fresh(1070m, 39.06m) };
        var rows = PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.AddedOnTop);
        var legacy = LegacyPlan(snaps, GatewayFeeVatMode.AddedOnTop, vatRegistered: false);
        var plan = SettlementBatchMath.Plan(Batch(legacy.ExpectedNet), ToLines(rows.Rows), Channel(SettlementFeeVatMode.ThaiVat7), false);
        Assert.True(plan.CanPost);
        var doc = Assert.Single(plan.FeeDocuments);
        Assert.Equal(SettlementFeeVatTreatment.VatNotClaimable, doc.VatTreatment);
        Assert.Equal(0m, doc.InputVat);
        Assert.Equal(41.79m, doc.Expense);
        Assert.Equal(0m, legacy.FeeVat);
    }

    [Theory]
    [InlineData(GatewayFeeVatMode.None)]
    [InlineData(GatewayFeeVatMode.IncludedInFee)]
    [InlineData(GatewayFeeVatMode.AddedOnTop)]
    public void ผลรวมบรรทัดต่อรายการ_เท่ายอดสุทธิของเส้นเดิม_รวมคืนเต็มและคืนบางส่วน(GatewayFeeVatMode mode)
    {
        var snaps = new[]
        {
            Fresh(1070m, 39.06m),
            Fresh(1000m, 30m, refundedAsOf: 1000m),       // คืนเต็มก่อนรอบโอน — ยอดขาย 0 แต่ค่าธรรมเนียมยังถูกหัก
            Fresh(800m, 20m, refundedAsOf: 300m),         // คืนบางส่วน
            Fresh(500m, null, feeEstimated: 17.5m),       // ยังเป็นค่าประมาณ — ใช้ได้เหมือนเส้นเดิม
        };
        var rows = PaymentIntentAdapter.BuildRows(snaps, mode);
        foreach (var s in snaps)
            Assert.Equal(GatewaySettlementMath.Contribution(Input(s), mode).Net,
                rows.Rows.Where(r => r.PaymentIntentId == s.Id).Sum(r => r.Amount));
        Assert.Equal(0, rows.FeeUnknownCount);
    }

    [Fact]
    public void ค่าธรรมเนียมจริงเป็นศูนย์_คือรู้แล้ว_ไม่นับว่าไม่รู้_ส่วนไม่มีทั้งจริงและประมาณการ_นับแจ้งไม่แต่งตัวเลข()
    {
        var known0 = Fresh(500m, 0m);
        var unknown = Fresh(500m, null);
        var rows = PaymentIntentAdapter.BuildRows(new[] { known0, unknown }, GatewayFeeVatMode.AddedOnTop);
        Assert.DoesNotContain(rows.Rows, r => r.ExplicitType == SettlementLineType.PaymentFee);
        Assert.Equal(1, rows.FeeUnknownCount);
    }

    // ═════════════════ รายได้ไม่นับซ้ำ (R-B4) · marketplace ไม่ถูกแตะ ═════════════════

    [Fact]
    public void บรรทัดจากintent_ล้างผังพักเท่านั้น_ไม่มีใบขายสรุป_ส่วนบรรทัดขายไม่มีร่องรอยของไฟล์ยังเป็นใบสรุปตามเดิม()
    {
        var snaps = new[] { Fresh(1070m, 41.79m) };
        var lines = ToLines(PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.IncludedInFee).Rows);
        // บรรทัดขายจากไฟล์ที่ตัวจับคู่ยืนยันว่าไม่มีร่องรอย (ทางของ marketplace) — ต้องยังได้ใบขายสรุปเหมือนเดิม
        lines.Add(new SettlementLine
        {
            CompanyId = Co, Seq = 99, LineType = SettlementLineType.Sale, Amount = 535m, TxnDate = T0,
            MatchStatus = SettlementMatchStatus.AutoSummary,
        });
        var plan = SettlementBatchMath.Plan(Batch(1070m - 41.79m + 535m), lines, Channel(SettlementFeeVatMode.ThaiVat7), true);
        Assert.True(plan.CanPost);
        Assert.Equal(1070m, plan.AlreadyInClearing);
        var summary = Assert.Single(plan.SummarySales);
        Assert.Equal(535m, summary.Gross);                                   // เฉพาะบรรทัดที่ไม่มี intent
        Assert.Empty(plan.Receipts);
        Assert.Equal(plan.LinesTotal - plan.AlreadyInClearing - plan.NetPayout, plan.ClearingMovement);
    }

    [Fact]
    public void ช่องทางmarketplace_หรือgatewayที่ไม่ผูกconfig_ประกอบจากรายการรับชำระไม่ได้_gatewayที่ผูกได้()
    {
        Assert.NotNull(GatewayBatchIntentRules.ChannelRefusal(SettlementChannelKind.Marketplace, Guid.NewGuid()));
        Assert.NotNull(GatewayBatchIntentRules.ChannelRefusal(SettlementChannelKind.Marketplace, null));
        Assert.NotNull(GatewayBatchIntentRules.ChannelRefusal(SettlementChannelKind.Gateway, null));
        Assert.Null(GatewayBatchIntentRules.ChannelRefusal(SettlementChannelKind.Gateway, Guid.NewGuid()));
    }

    // ═════════════════ ข้อเท็จจริงเดียว: โหมดภาษีค่าธรรมเนียม config ↔ ช่องทาง ═════════════════

    [Theory]
    // ขัดกัน ⇒ บล็อก
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, true)]
    [InlineData(GatewayFeeVatMode.AddedOnTop, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.None, true)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.None, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.Withhold3Percent, SettlementFeeVatMode.None, SettlementFeeWhtMode.None, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.Withhold3Percent, SettlementFeeVatMode.None, SettlementFeeWhtMode.SelfWithholdReimbursed, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.SelfWithholdPayerBorne, true)]
    // X-3 (ฝ่ายค้านรอบ 200 · DECISIONS ข้อ 26): คู่ที่เคยผ่านแต่สองเส้นให้ภาษีต่างกัน ⇒ ไม่ตรง
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.ForeignPp36, SettlementFeeWhtMode.None, true)]
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.SelfWithholdReimbursed, true)]
    // ตรงกัน ⇒ ผ่าน (ทิศตรงข้าม — ของที่ตั้งถูกต้องไม่ถูกบล็อก)
    [InlineData(GatewayFeeVatMode.None, GatewayFeeWhtMode.None, SettlementFeeVatMode.None, SettlementFeeWhtMode.None, false)]
    [InlineData(GatewayFeeVatMode.IncludedInFee, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, false)]
    [InlineData(GatewayFeeVatMode.AddedOnTop, GatewayFeeWhtMode.Withhold3Percent, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.SelfWithholdPayerBorne, false)]
    [InlineData(GatewayFeeVatMode.AddedOnTop, GatewayFeeWhtMode.None, SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.AgentWithholds, false)]
    public void โหมดภาษีค่าธรรมเนียม_configกับช่องทาง_ต้องตรงกัน(GatewayFeeVatMode gVat, GatewayFeeWhtMode gWht,
        SettlementFeeVatMode cVat, SettlementFeeWhtMode cWht, bool blocked)
    {
        var why = GatewayBatchIntentRules.ModeMismatch(gVat, gWht, cVat, cWht, companyVatRegistered: true);
        Assert.Equal(blocked, why != null);
        if (blocked) Assert.Contains("ทางไปต่อ", why);
    }

    [Fact]
    public void เหตุผลของด่านโหมด_configไม่แยกVATแต่ช่องทางVAT7_รอบโอนจะแต่งภาษีซื้อจากค่าธรรมเนียมที่ไม่มีVAT()
    {
        // ล็อก "ทำไมต้องมีด่าน": ไม่มีด่าน ⇒ ภาษีซื้อ 2.73 ที่ไม่มีจริง (เส้นเดิม 0) — ด่านต้องฟ้องคู่นี้เสมอ
        var snaps = new[] { Fresh(1070m, 41.79m) };
        var rows = PaymentIntentAdapter.BuildRows(snaps, GatewayFeeVatMode.None);
        var plan = SettlementBatchMath.Plan(Batch(1028.21m), ToLines(rows.Rows), Channel(SettlementFeeVatMode.ThaiVat7), true);
        Assert.Equal(2.73m, Assert.Single(plan.FeeDocuments).InputVat);
        Assert.Equal(0m, LegacyPlan(snaps, GatewayFeeVatMode.None).FeeVat);
        Assert.NotNull(GatewayBatchIntentRules.ModeMismatch(GatewayFeeVatMode.None, GatewayFeeWhtMode.None,
            SettlementFeeVatMode.ThaiVat7, SettlementFeeWhtMode.None, companyVatRegistered: true));
    }

    // ═════════════════ หนึ่งรายการ หนึ่งเจ้าของ (สองทิศ) ═════════════════

    private static PaymentIntent Intent(PaymentIntentStatus status = PaymentIntentStatus.Succeeded, decimal refunded = 0m,
        Guid? je = null, Guid? batch = null, DateTime? confirmed = null, Guid? company = null, string provider = Provider)
        => new()
        {
            CompanyId = company ?? Co, ProviderCode = provider, Amount = 1000m, Status = status, RefundedAmount = refunded,
            SettlementJournalEntryId = je, SettlementBatchId = batch, ConfirmedAt = confirmed ?? T0,
        };

    [Fact]
    public void รายการที่ยังไม่มีเจ้าของเท่านั้นที่เข้ารอบโอนใหม่_ของเส้นเดิมและของรอบโอนอื่นไม่ถูกดึงซ้ำ()
    {
        var fresh = Intent();
        var partial = Intent(PaymentIntentStatus.PartiallyRefunded, refunded: 100m);
        var legacyOwned = Intent(je: Guid.NewGuid());
        var batchOwned = Intent(batch: Guid.NewGuid());
        var legacyRefundUnknownAmount = Intent(PaymentIntentStatus.Refunded, refunded: 0m);   // คืนก่อนระบบเก็บยอดคืน — ไม่รู้ยอด ไม่นับ
        var pending = Intent(PaymentIntentStatus.Pending);
        var otherCompany = Intent(company: Guid.NewGuid());
        var otherProvider = Intent(provider: "other");
        var beforeFrom = Intent(confirmed: T0.AddDays(-10));
        var afterTo = Intent(confirmed: T0.AddDays(10));
        var all = new[] { fresh, partial, legacyOwned, batchOwned, legacyRefundUnknownAmount, pending, otherCompany, otherProvider, beforeFrom, afterTo };

        var pick = GatewayBatchIntentRules.UnclaimedForBatch(Co, Provider, T0.AddDays(-1), T0.AddDays(1)).Compile();
        Assert.Equal(new[] { fresh.Id, partial.Id }, all.Where(pick).Select(i => i.Id));

        // ไม่ระบุช่วง ⇒ ไม่จำกัดฝั่งนั้น (รายการเก่า/ใหม่ที่ยังไม่มีเจ้าของเข้าได้) — แต่เจ้าของเดิมยังไม่ถูกแตะ
        var open = GatewayBatchIntentRules.UnclaimedForBatch(Co, Provider, null, null).Compile();
        Assert.Equal(new[] { fresh.Id, partial.Id, beforeFrom.Id, afterTo.Id }, all.Where(open).Select(i => i.Id));
    }

    [Fact]
    public void คืนเงินภายหลัง_ตามเจ้าของ_ของเส้นเดิมไม่เข้ารอบโอนใหม่_ของรอบโอนเข้า()
    {
        var inBatchRefunded = Intent(PaymentIntentStatus.PartiallyRefunded, refunded: 200m, batch: Guid.NewGuid());
        var inBatchNoRefund = Intent(batch: Guid.NewGuid());
        var legacyRefunded = Intent(PaymentIntentStatus.PartiallyRefunded, refunded: 200m, je: Guid.NewGuid());
        var both = Intent(PaymentIntentStatus.PartiallyRefunded, refunded: 200m, je: Guid.NewGuid(), batch: Guid.NewGuid());
        var late = GatewayBatchIntentRules.LateRefundInBatch(Co, Provider).Compile();
        Assert.True(late(inBatchRefunded));
        Assert.False(late(inBatchNoRefund));
        Assert.False(late(legacyRefunded));   // เส้นเดิมหักยอดคืนเองในรอบถัดไปของมัน — ห้ามหักสองเส้น
        Assert.False(late(both));
    }

    // ═════════════════ ยอดคืน ณ วันเงินเข้า + ตาข่ายใต้ล็อก ═════════════════

    [Fact]
    public void คืนเงินหลังวันเงินเข้า_ไม่อยู่ในรอบนี้_ด้วยสูตรเดียวกับเส้นเดิม()
    {
        var payout = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        var cutoff = GatewaySettlementMath.RefundCutoffUtc(payout);
        var refunds = new[]
        {
            new GatewayRefundEntry(cutoff.AddHours(-5), 100m),   // ก่อนวันเงินเข้า ⇒ หักรอบนี้
            new GatewayRefundEntry(cutoff.AddHours(2), 200m),    // วันเงินเข้า ⇒ รอบถัดไป
        };
        var asOf = GatewaySettlementMath.RefundedAsOf(300m, cutoff.AddHours(2), refunds, cutoff);
        Assert.True(asOf.Known);
        var snap = Fresh(1000m, 30m, refundedAsOf: asOf.Amount);
        var rows = PaymentIntentAdapter.BuildRows(new[] { snap }, GatewayFeeVatMode.None);
        Assert.Equal(-100m, Assert.Single(rows.Rows, r => r.ExplicitType == SettlementLineType.Refund).Amount);

        // รอบถัดไป: intent อยู่ในรอบโอนแล้ว · ยอดคืน ณ วันเงินเข้ารอบใหม่ 300 − ที่อยู่ในบรรทัดแล้ว 100 = 200 · ไม่มีขาย/ค่าธรรมเนียมซ้ำ
        var next = new SettlementIntentSnapshot(snap.Id, snap.ProviderRef, 1000m, 300m, 30m, 0m, T0, true, 100m);
        var nextRows = PaymentIntentAdapter.BuildRows(new[] { next }, GatewayFeeVatMode.None);
        Assert.Equal(-200m, Assert.Single(nextRows.Rows).Amount);
        Assert.Empty(nextRows.NewIntentIds);

        // ทิศตรงข้าม: ยอดรายครั้งไม่ครบ (คืนก่อนระบบเก็บ) และคืนล่าสุดหลังจุดตัด ⇒ ไม่รู้ ⇒ ผู้เรียกบล็อก (ห้ามเดา)
        var unknown = GatewaySettlementMath.RefundedAsOf(300m, cutoff.AddHours(2), new[] { refunds[1] }, cutoff);
        Assert.False(unknown.Known);
        Assert.Contains("ทางไปต่อ", GatewayBatchIntentRules.RefundTimingRefusal(1));
    }

    [Fact]
    public void ตาข่ายใต้ล็อก_บรรทัดคืนเงินรวมเกินยอดคืนผ่านระบบ_ฟ้อง_ไม่เกินผ่าน()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var refunded = new Dictionary<Guid, decimal> { [a] = 100m, [b] = 50m };
        var inLines = new Dictionary<Guid, decimal> { [a] = 60m };
        Assert.Equal(new[] { a }, GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, inLines, new[] { (a, -50m) }));
        Assert.Empty(GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, inLines, new[] { (a, -40m), (b, -50m) }));
        Assert.Empty(GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, inLines, new[] { (a, -40.01m) }));   // เศษ ±0.01
        Assert.Equal(new[] { b }, GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, inLines, new[] { (b, -30m), (b, -30m) }));
        Assert.Equal(new[] { Guid.Empty }, GatewayBatchIntentRules.RefundLinesOverRefunded(refunded, inLines, new[] { (Guid.Empty, -1m) }));
    }
}
