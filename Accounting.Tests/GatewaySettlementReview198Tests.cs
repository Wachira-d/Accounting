using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 198 ของทีม E (review198-A §E · review198-E) — R-E2..R-E6 + E-2
///
/// <para>ทุกกลุ่มมีสองครึ่ง: ตัวเลขจากผลตรวจที่เคยพังกลับมาถูก และเคสที่ถูกอยู่แล้ว<b>ไม่ถูกแตะ</b>
/// (คืนก่อนวันเงินเข้ายังหักในรอบนั้น · โหมดที่ไม่ใช่ AddedOnTop · ค่าธรรมเนียมไม่ตรงยังเป็นผลต่างที่อธิบายไม่ได้ ·
/// บริษัทไม่จด VAT ไม่ถูกเตือน · คืนเงินครั้งแรกไม่ถูกล็อก)</para>
/// </summary>
public class GatewaySettlementReview198Tests
{
    private const string ValidTaxId = "0105536000313";

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private static SettlementIntentInput I(decimal amount, decimal? fee, decimal refunded = 0m,
        bool alreadySettled = false, decimal refundSettled = 0m, bool unknown = false)
        => new(Guid.NewGuid(), amount, fee, 0m, refunded, alreadySettled, refundSettled, unknown);

    // ═══ R-E2: คืนเงินหลังวันเงินเข้า แต่บันทึกรอบโอนทีหลัง ═══

    [Fact]
    public void RE2_จุดตัดคือเที่ยงคืนต้นวันเงินเข้าตามเวลาไทย()
    {
        // เงินเข้า 20 ก.ย. (ไทย) ⇒ จุดตัด 19 ก.ย. 17:00 UTC = 20 ก.ย. 00:00 ไทย
        Assert.Equal(Utc(2026, 9, 19, 17), GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20)));
        var cut = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20));
        var before = new[] { new GatewayRefundEntry(Utc(2026, 9, 19, 16, 59), 300m) };   // 23:59 ไทย 19 ก.ย.
        var onDay = new[] { new GatewayRefundEntry(Utc(2026, 9, 19, 17, 0), 300m) };     // 00:00 ไทย 20 ก.ย.
        Assert.Equal(300m, GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 19, 16, 59), before, cut).Amount);
        Assert.Equal(0m, GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 19, 17, 0), onDay, cut).Amount);
    }

    [Fact]
    public void RE2_ตัวเลขจากผลตรวจ_คืนหลังวันเงินเข้า_บันทึกรอบทีหลัง_ยอดตรงและหักรอบถัดไป()
    {
        // รับ 1,000 (18 ก.ย.) · เงินเข้า 20 ก.ย. 961.00 (ค่าธรรมเนียม 39) · คืน 300 วันที่ 22 ก.ย. · บันทึกรอบ 20 ก.ย. ในวันที่ 25
        var cut = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20));
        var refunds = new[] { new GatewayRefundEntry(Utc(2026, 9, 22, 3), 300m) };
        var asOf = GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 22, 3), refunds, cut);
        Assert.True(asOf.Known);
        Assert.Equal(0m, asOf.Amount);

        var round1 = GatewaySettlementMath.Plan(new[] { I(1000m, 39m, refunded: asOf.Amount) }, 961m,
            GatewayFeeWhtMode.None, "PO-0920");
        Assert.True(round1.Ok, round1.Message);          // เดิม: คาด 661 เทียบ 961 ⇒ ต่าง 300 บล็อกถาวร
        Assert.Equal(1000m, round1.Gross);
        Assert.Equal(round1.Lines.Sum(l => l.Debit), round1.Lines.Sum(l => l.Credit));

        // รอบถัดไป (27 ก.ย.): รายการใหม่ 500 ค่าธรรมเนียม 19.50 + ยอดคืน 300 ที่ยังไม่ถูกหัก (RefundSettledAmount = 0 จากรอบแรก)
        var cut2 = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 27));
        var asOf2 = GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 22, 3), refunds, cut2);
        var round2 = GatewaySettlementMath.Plan(new[]
        {
            I(500m, 19.50m),
            I(1000m, 39m, refunded: asOf2.Amount, alreadySettled: true, refundSettled: 0m),
        }, 180.50m, GatewayFeeWhtMode.None, "PO-0927");
        Assert.True(round2.Ok, round2.Message);
        Assert.Equal(300m, round2.RefundDeducted);
        Assert.Equal(180.50m, round2.ExpectedNet);
    }

    [Fact]
    public void RE2_ทิศตรงข้าม_คืนก่อนวันเงินเข้ายังถูกหักในรอบนั้น()
    {
        var cut = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20));
        var refunds = new[] { new GatewayRefundEntry(Utc(2026, 9, 18, 9), 300m) };
        var asOf = GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 18, 9), refunds, cut);
        Assert.Equal(300m, asOf.Amount);
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 39m, refunded: asOf.Amount) }, 661m,
            GatewayFeeWhtMode.None, "PO-0920");
        Assert.True(p.Ok, p.Message);
        Assert.Equal(700m, p.Gross);
        // ไม่มีจุดตัด (หน้ารายการค้างโอน) = ยอดสะสมทั้งหมดเหมือนเดิม
        Assert.Equal(300m, GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 22), refunds, null).Amount);
    }

    [Fact]
    public void RE2_คืนสองครั้งคร่อมวันเงินเข้า_แยกตามยอดรายครั้ง()
    {
        var cut = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20));
        var refunds = new[]
        {
            new GatewayRefundEntry(Utc(2026, 9, 18, 9), 100m),
            new GatewayRefundEntry(Utc(2026, 9, 21, 9), 200m),
        };
        var asOf = GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 21, 9), refunds, cut);
        Assert.True(asOf.Known);
        Assert.Equal(100m, asOf.Amount);
    }

    [Fact]
    public void RE2_ยอดรายครั้งไม่ครบ_ไม่รู้_แผนบล็อกพร้อมสาเหตุ_ห้ามเดา()
    {
        var cut = GatewaySettlementMath.RefundCutoffUtc(Utc(2026, 9, 20));
        // คืนสะสม 300 แต่มียอดรายครั้งบันทึกแค่ 200 (ครั้งแรกคืนก่อนระบบเก็บรายครั้ง) · ครั้งล่าสุดหลังวันเงินเข้า
        var partial = new[] { new GatewayRefundEntry(Utc(2026, 9, 21, 9), 200m) };
        var asOf = GatewaySettlementMath.RefundedAsOf(300m, Utc(2026, 9, 21, 9), partial, cut);
        Assert.False(asOf.Known);

        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 39m, refunded: 0m, unknown: true) }, 961m,
            GatewayFeeWhtMode.None, "PO-0920");
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.RefundTimingUnknown, p.Reason);
        Assert.Empty(p.Lines);
        Assert.Contains("วันเงินเข้า", p.Message);
        // review198-E2 E2-8: ห้ามชี้ไปเครื่องมือที่ไม่มีอยู่จริง ("ให้ผู้ดูแลระบบบันทึกยอดคืนรายครั้ง") — บอกตรง ๆ ว่ายังไม่มีหน้าจอ
        Assert.DoesNotContain("ให้ผู้ดูแลระบบบันทึกยอดคืนรายครั้ง", p.Message);
        Assert.Contains("ยังไม่มีหน้าจอ", p.Message);
        Assert.Contains("อย่าลงใบสำคัญรอบโอนด้วยมือ", p.Message);   // ทางที่ห้าม (รายการจะค้างแล้วนับซ้ำ) ยังต้องบอก
    }

    [Fact]
    public void RE2_ข้อความยอดไม่ตรงบอกเรื่องวันเงินเข้าเฉพาะรอบที่มีคืนเงิน()
    {
        var withRefund = GatewaySettlementMath.Plan(new[] { I(1000m, 39m, refunded: 300m) }, 961m,
            GatewayFeeWhtMode.None, "T");
        Assert.Equal(SettlementBlockReason.NetMismatch, withRefund.Reason);
        Assert.Contains("วันที่เงินเข้าบัญชี", withRefund.Message);

        var plain = GatewaySettlementMath.Plan(new[] { I(1000m, 39m) }, 900m, GatewayFeeWhtMode.None, "T");
        Assert.Equal(SettlementBlockReason.NetMismatch, plain.Reason);
        Assert.DoesNotContain("วันที่เงินเข้าบัญชี", plain.Message);
    }

    // ═══ R-E4: ปุ่ม "แก้ค่าธรรมเนียม" ในโหมด AddedOnTop ═══

    [Fact]
    public void RE4_กดตกลงโดยไม่แก้_ต้องไม่เปลี่ยนยอดที่ถูกหัก()
    {
        // ค่าธรรมเนียม 39.06 · VAT 2.73 · ถูกหักจริง 41.79
        var before = I(1070m, 39.06m);
        var c = GatewaySettlementMath.Contribution(before, GatewayFeeVatMode.AddedOnTop);
        Assert.Equal(41.79m, c.FeeDeducted);

        var prefill = GatewaySettlementMath.FeeInput(before);
        Assert.Equal(39.06m, prefill);   // เติมค่าที่เก็บ (ก่อน VAT) ไม่ใช่ยอดที่ถูกหัก
        var after = GatewaySettlementMath.Contribution(before with { FeeActual = prefill }, GatewayFeeVatMode.AddedOnTop);
        Assert.Equal(c.FeeDeducted, after.FeeDeducted);

        // ตัวเลขจากผลตรวจ: เติม 41.79 แล้วบันทึกกลับ ⇒ VAT 2.93 · ถูกหัก 44.72 (บั๊กเดิม — ล็อกว่าค่าตั้งต้นต้องไม่ใช่ตัวนี้)
        var old = GatewaySettlementMath.Contribution(before with { FeeActual = c.FeeDeducted }, GatewayFeeVatMode.AddedOnTop);
        Assert.Equal(44.72m, old.FeeDeducted);
        Assert.NotEqual(c.FeeDeducted, prefill);
    }

    [Fact]
    public void RE4_ป้ายช่องค่าธรรมเนียมบอกความหมายตามโหมด()
    {
        Assert.Contains("ก่อน VAT", GatewaySettlementMath.FeeInputLabel(GatewayFeeVatMode.AddedOnTop));
        Assert.Contains("รวม VAT", GatewaySettlementMath.FeeInputLabel(GatewayFeeVatMode.IncludedInFee));
        Assert.DoesNotContain("VAT", GatewaySettlementMath.FeeInputLabel(GatewayFeeVatMode.None));
        // ทิศตรงข้าม: โหมดที่ค่าที่เก็บ = ยอดที่ถูกหัก ค่าตั้งต้นเท่าเดิม
        var i = I(1000m, 42.80m);
        Assert.Equal(GatewaySettlementMath.Contribution(i, GatewayFeeVatMode.IncludedInFee).FeeDeducted,
            GatewaySettlementMath.FeeInput(i));
    }

    // ═══ R-E5: รายงานกระทบยอดใช้สูตรเดียวกับรอบโอน ═══

    [Fact]
    public void RE5_โหมดAddedOnTop_สมดุล_ไม่ค้าง2560ทุกเดือน()
    {
        // 10 รายการ × 1,000 ค่าธรรมเนียม 36.50 (+VAT 2.56) — รอบโอนบันทึกสุทธิ 960.94 ต่อรายการ
        var net = GatewaySettlementMath.Contribution(I(1000m, 36.50m), GatewayFeeVatMode.AddedOnTop).Net;
        Assert.Equal(960.94m, net);
        var rows = Enumerable.Range(0, 10).Select(_ => new GatewayIntentAmounts(1000m, 36.50m, 0m, net, false, true,
            FeeVatMode: GatewayFeeVatMode.AddedOnTop));
        var r = GatewayReconciliation.Compute(rows);
        Assert.Equal(390.60m, r.FeeTotal);
        Assert.Equal(0m, r.UnexplainedDifference);   // เดิม +25.60
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void RE5_คืนหลังรอบโอน_ก่อนและหลังถูกหัก_สมดุลทั้งสองจังหวะ()
    {
        // รับ 1,000 ค่าธรรมเนียม 30 โอนแล้ว 970 · คืน 300 หลังรอบโอน
        var pendingDeduction = new GatewayIntentAmounts(1000m, 30m, 0m, 970m, false, true, RefundedAmount: 300m);
        var r1 = GatewayReconciliation.Compute(new[] { pendingDeduction });
        Assert.Equal(670m, r1.ExpectedNet);
        Assert.Equal(-300m, r1.UnsettledAmount);   // จะถูกหักรอบถัดไป — ไม่ใช่ผลต่างอธิบายไม่ได้ (เดิม −300 ถาวร)
        Assert.True(r1.IsBalanced);

        var deducted = pendingDeduction with { RefundSettledAmount = 300m, RefundDeductedAfterSettlement = 300m };
        var r2 = GatewayReconciliation.Compute(new[] { deducted });
        Assert.Equal(670m, r2.SettledTotal);
        Assert.Equal(0, r2.UnsettledCount);
        Assert.True(r2.IsBalanced);
    }

    [Fact]
    public void RE5_คืนเต็มที่ยังไม่ถึงรอบโอน_ค่าธรรมเนียมยังจะถูกหัก_สมดุล()
    {
        var r = GatewayReconciliation.Compute(new[]
        {
            new GatewayIntentAmounts(500m, 15m, 0m, null, IsRefundedFully: true, IsSettled: false, RefundedAmount: 500m),
        });
        Assert.Equal(1, r.UnsettledCount);
        Assert.Equal(-15m, r.UnsettledAmount);     // เดิมถูกตัดออก ⇒ ผลต่าง −15
        Assert.True(r.IsBalanced);
    }

    [Fact]
    public void RE5_ทิศตรงข้าม_ค่าธรรมเนียมจริงไม่ตรงยังเป็นผลต่างที่อธิบายไม่ได้()
    {
        var r = GatewayReconciliation.Compute(new[]
        {
            new GatewayIntentAmounts(1000m, 36.50m, 0m, 960.94m, false, true, FeeVatMode: GatewayFeeVatMode.None),
        });
        // โหมด None ค่าธรรมเนียม 36.50 ⇒ ควรได้ 963.50 แต่โอนเข้า 960.94 ⇒ ต้องเห็น 2.56
        Assert.Equal(2.56m, r.UnexplainedDifference);
        Assert.False(r.IsBalanced);
    }

    // ═══ R-E6: โหมด VAT ค่าธรรมเนียม "ไม่แยก" บนบริษัทจด VAT ═══

    [Fact]
    public void RE6_เตือนเฉพาะบริษัทจดVATที่ตั้งไม่แยกVAT()
    {
        Assert.NotNull(GatewaySettlementMath.FeeVatModeWarning(GatewayFeeVatMode.None, companyVatRegistered: true));
        Assert.Null(GatewaySettlementMath.FeeVatModeWarning(GatewayFeeVatMode.None, companyVatRegistered: false));
        Assert.Null(GatewaySettlementMath.FeeVatModeWarning(GatewayFeeVatMode.IncludedInFee, companyVatRegistered: true));
        Assert.Null(GatewaySettlementMath.FeeVatModeWarning(GatewayFeeVatMode.AddedOnTop, companyVatRegistered: true));

        // พรีวิวรอบโอนพกคำเตือนตัวเดียวกัน — แต่ไม่บล็อก (ผู้ให้บริการที่ไม่คิด VAT มีจริง)
        var p = GatewaySettlementMath.Plan(new[] { I(1000m, 30m) }, 970m, GatewayFeeWhtMode.None, "T",
            GatewayFeeVatMode.None, companyVatRegistered: true);
        Assert.True(p.Ok, p.Message);
        Assert.Equal(GatewaySettlementMath.FeeVatModeWarning(GatewayFeeVatMode.None, true), p.Warning);
        var q = GatewaySettlementMath.Plan(new[] { I(1000m, 30m) }, 970m, GatewayFeeWhtMode.None, "T",
            GatewayFeeVatMode.IncludedInFee, companyVatRegistered: true);
        Assert.Null(q.Warning);
    }

    // ═══ R-E3: VAT ค่าธรรมเนียม 11630 → 11610 + อายุ §82/3 ═══

    [Fact]
    public void RE3_อายุตัดยอดเคลมจากเดือนเก่าสุด_และเตือนใกล้ครบหกเดือน()
    {
        var a = GatewayFeeVatClaim.Aging(new[]
        {
            new GatewayFeeVatMonth(Utc(2026, 3, 15), 10m),
            new GatewayFeeVatMonth(Utc(2026, 4, 1), 20m),
            new GatewayFeeVatMonth(Utc(2026, 5, 20), 5m),
        }, claimedTotal: 15m, todayUtc: Utc(2026, 9, 10, 3));
        Assert.Equal(35m, a.DeferredTotal);
        Assert.Equal(20m, a.Outstanding);
        Assert.Equal(0m, a.Buckets[0].Outstanding);
        Assert.Equal(15m, a.Buckets[1].Outstanding);
        Assert.Equal(GatewayFeeVatAgeLevel.NearDeadline, a.Buckets[1].Level);   // เม.ย. → ก.ย. = 5 เดือน
        Assert.Equal(GatewayFeeVatAgeLevel.Ok, a.Buckets[2].Level);
        Assert.Equal(GatewayFeeVatAgeLevel.NearDeadline, a.WorstLevel);
        Assert.NotNull(a.Warning);
    }

    [Fact]
    public void RE3_ค้างเกินหกเดือนเตือนว่าอาจเลยกำหนด_ทิศตรงข้ามเคลมครบไม่เตือน()
    {
        var old = GatewayFeeVatClaim.Aging(new[] { new GatewayFeeVatMonth(Utc(2026, 1, 5), 7m) }, 0m, Utc(2026, 9, 1));
        Assert.Equal(GatewayFeeVatAgeLevel.PastWindow, old.WorstLevel);
        Assert.Contains("§82/3", old.Warning);

        var cleared = GatewayFeeVatClaim.Aging(new[] { new GatewayFeeVatMonth(Utc(2026, 1, 5), 7m) }, 7m, Utc(2026, 9, 1));
        Assert.Equal(GatewayFeeVatAgeLevel.Ok, cleared.WorstLevel);
        Assert.Null(cleared.Warning);
        Assert.Equal(0m, cleared.Outstanding);
    }

    [Fact]
    public void RE3_รับใบกำกับ_ผ่านเมื่อครบและไม่เกินยอดพัก()
    {
        var c = GatewayFeeVatClaim.Check(25.55m, 30m, "INV-2026-09-001", Utc(2026, 9, 30), Utc(2026, 9, 30),
            "บริษัท ผู้ให้บริการ จำกัด", ValidTaxId, "00000", null);
        Assert.True(c.Ok, c.Message);
        Assert.Equal(4.45m, c.OutstandingAfter);   // ส่วนต่างที่ยังค้าง — บอกผู้ใช้
        Assert.False(c.IsLate);
        Assert.Equal("00000", c.BranchCode);
        // สาขาเว้นว่าง = สำนักงานใหญ่
        Assert.Equal("00000", GatewayFeeVatClaim.Check(1m, 30m, "X1", Utc(2026, 9, 1), Utc(2026, 9, 1),
            "ผู้ให้บริการ", ValidTaxId, null, null).BranchCode);
    }

    [Fact]
    public void RE3_รับใบกำกับ_ด่านที่ต้องบล็อก()
    {
        var inv = Utc(2026, 9, 30);
        Assert.False(GatewayFeeVatClaim.Check(31m, 30m, "I", inv, inv, "ผู้ให้บริการ", ValidTaxId, "00000", null).Ok);        // เกินยอดพัก
        Assert.False(GatewayFeeVatClaim.Check(0m, 30m, "I", inv, inv, "ผู้ให้บริการ", ValidTaxId, "00000", null).Ok);         // ศูนย์
        Assert.False(GatewayFeeVatClaim.Check(5m, 30m, " ", inv, inv, "ผู้ให้บริการ", ValidTaxId, "00000", null).Ok);         // ไม่มีเลขที่
        Assert.False(GatewayFeeVatClaim.Check(5m, 30m, "I", inv, inv, "", ValidTaxId, "00000", null).Ok);                      // ไม่มีชื่อ
        Assert.False(GatewayFeeVatClaim.Check(5m, 30m, "I", inv, inv, "ผู้ให้บริการ", "0105536000314", "00000", null).Ok);    // checksum ผิด
        Assert.False(GatewayFeeVatClaim.Check(5m, 30m, "I", inv, inv, "ผู้ให้บริการ", ValidTaxId, "0001", null).Ok);          // สาขาไม่ครบ 5 หลัก
        Assert.False(GatewayFeeVatClaim.Check(5m, 30m, "I", inv, Utc(2026, 9, 29), "ผู้ให้บริการ", ValidTaxId, "00000", null).Ok); // เคลมก่อนได้ใบ
    }

    [Fact]
    public void RE3_กำหนดหกเดือน_82_3()
    {
        var inv = Utc(2026, 1, 31);
        var late7 = GatewayFeeVatClaim.Check(5m, 30m, "I", inv, Utc(2026, 8, 1), "ผู้ให้บริการ", ValidTaxId, "00000", "รอใบ");
        Assert.False(late7.Ok);
        Assert.Contains("§82/3", late7.Message);

        var late2NoReason = GatewayFeeVatClaim.Check(5m, 30m, "I", inv, Utc(2026, 3, 5), "ผู้ให้บริการ", ValidTaxId, "00000", " ");
        Assert.False(late2NoReason.Ok);

        var late6 = GatewayFeeVatClaim.Check(5m, 30m, "I", inv, Utc(2026, 7, 31), "ผู้ให้บริการ", ValidTaxId, "00000", "ผู้ให้บริการส่งใบช้า");
        Assert.True(late6.Ok, late6.Message);
        Assert.True(late6.IsLate);
    }

    [Fact]
    public void RE3_tagของใบสำคัญเคลมคงที่ต่อผู้ให้บริการ()
    {
        Assert.Equal("gateway-fee-vat:omise", GatewayFeeVatClaim.ClaimTag("omise"));
        Assert.NotEqual(GatewayFeeVatClaim.ClaimTag("omise"), GatewayFeeVatClaim.ClaimTag("other"));
        // นับเดือนปฏิทิน ไม่ใช่ 30 วัน: 31 ม.ค. → 1 ส.ค. = 7 เดือน (บล็อก) · 31 ม.ค. → 31 ก.ค. = 6 เดือน (ยังเคลมได้)
        Assert.False(GatewayFeeVatClaim.Check(1m, 5m, "I", Utc(2026, 1, 31), Utc(2026, 8, 1), "ผู้ให้บริการ", ValidTaxId, "00000", "x").Ok);
        Assert.True(GatewayFeeVatClaim.Check(1m, 5m, "I", Utc(2026, 1, 31), Utc(2026, 7, 31), "ผู้ให้บริการ", ValidTaxId, "00000", "x").Ok);
    }

    // ═══ E-2: ผู้ให้บริการไม่ตอบผลการคืนเงิน ═══

    [Fact]
    public void E2_ผลไม่แน่ชัด_ล็อกการคืนเพิ่ม_ทิศตรงข้ามไม่มีธงคืนได้()
    {
        var locked = GatewayRefundMath.Check(PaymentIntentStatus.Succeeded, 1000m, 0m, 100m, refundOutcomeUnknown: true);
        Assert.False(locked.Ok);
        Assert.Equal(GatewayRefundMath.OutcomeUnknownMessage, locked.Message);

        var free = GatewayRefundMath.Check(PaymentIntentStatus.Succeeded, 1000m, 0m, 100m, refundOutcomeUnknown: false);
        Assert.True(free.Ok);
        Assert.Equal(100m, free.Amount);
    }

    [Fact]
    public void E2_รอบโอนที่มีรายการคืนเงินผลไม่แน่ชัด_บล็อกพร้อมสาเหตุจริง_ทิศตรงข้ามผ่าน()
    {
        var unknown = new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m, RefundOutcomeUnknown: true);
        var p = GatewaySettlementMath.Plan(new[] { unknown }, 961m, GatewayFeeWhtMode.None, "T");
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.RefundOutcomeUnknown, p.Reason);   // ไม่ใช่ NetMismatch ที่ชี้ไปแก้ค่าธรรมเนียม
        Assert.Contains("ตรวจผลการคืนเงิน", p.Message);

        var known = unknown with { RefundOutcomeUnknown = false };
        Assert.True(GatewaySettlementMath.Plan(new[] { known }, 961m, GatewayFeeWhtMode.None, "T").Ok);
    }

    [Fact]
    public void E2_ตรวจผลกับยอดคืนสะสมของผู้ให้บริการ()
    {
        // review198-E2: ตัวตัดสินรับยอดที่พยายามคืน + เวลา — ที่นี่ใช้เวลาที่พ้นช่วงรอแล้ว (พฤติกรรมเดิมของ E-2)
        var at = Utc(2026, 9, 20, 3);
        var later = at.AddMinutes(30);
        var silent = GatewayRefundMath.Verify(0m, null, 1000m, 100m, null, at, later);
        Assert.Equal(GatewayRefundVerificationOutcome.ProviderSilent, silent.Outcome);
        Assert.False(silent.Resolves);    // ไม่รู้ = ล็อกต่อ (ห้ามเดาว่าไม่มีเงินออก)

        var none = GatewayRefundMath.Verify(200m, 200m, 1000m, 100m, null, at, later);
        Assert.Equal(GatewayRefundVerificationOutcome.NoMoneyOut, none.Outcome);
        Assert.True(none.Resolves);
        Assert.Equal(0m, none.AmountToBook);

        var out1 = GatewayRefundMath.Verify(200m, 500m, 1000m, 300m, null, at, later);
        Assert.Equal(GatewayRefundVerificationOutcome.MoneyWentOut, out1.Outcome);
        Assert.Equal(300m, out1.AmountToBook);
        Assert.True(out1.Resolves);

        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent, GatewayRefundMath.Verify(500m, 200m, 1000m, 100m, null, at, later).Outcome);
        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent, GatewayRefundMath.Verify(0m, 1200m, 1000m, 100m, null, at, later).Outcome);
        Assert.False(GatewayRefundMath.Verify(500m, 200m, 1000m, 100m, null, at, later).Resolves);
    }
}
