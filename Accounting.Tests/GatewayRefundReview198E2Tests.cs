using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 198 ของทีม E2 (review198-E2.md) — E2-1..E2-8 ที่ทีม E3 แก้
///
/// <para>ทุกกลุ่มมีสองทิศ: เคสที่เคยพัง (คืนซ้ำได้ · ปลดล็อกเร็วเกิน · ลงบัญชีทั้งก้อน · เคลมใบเดิมซ้ำ · บล็อกทุกรอบถาวร) ต้องถูกกัน
/// และเคสที่ถูกอยู่แล้ว (4xx ปฏิเสธจริง · พ้นเวลารอ · ส่วนต่างเท่ายอดที่สั่งคืน · คนละผู้ออกใบ · รายการในช่วงของรอบ) ต้องไม่ถูกแตะ</para>
/// </summary>
public class GatewayRefundReview198E2Tests
{
    private const string ValidTaxId = "0105536000313";
    private const string OtherTaxId = "0105556000019";

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ═══ E2-1: HTTP 5xx/408 ของคำขอคืนเงิน = ผลไม่แน่ชัด (ล็อก) · 4xx = ปฏิเสธจริง (ไม่ล็อก) ═══

    [Theory]
    [InlineData(504)]
    [InlineData(502)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(302)]
    public void E21_ผู้ให้บริการตอบ5xxหรือ408_ไม่รู้ผล_ต้องล็อก(int status)
        => Assert.Equal(GatewayRefundHttpOutcome.Unknown, GatewayRefundMath.ClassifyRefundHttpStatus(status));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(429)]
    public void E21_ทิศตรงข้าม_4xxคือปฏิเสธจริง_ไม่ล็อก(int status)
        => Assert.Equal(GatewayRefundHttpOutcome.Refused, GatewayRefundMath.ClassifyRefundHttpStatus(status));

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void E21_2xxคือสำเร็จ(int status)
        => Assert.Equal(GatewayRefundHttpOutcome.Succeeded, GatewayRefundMath.ClassifyRefundHttpStatus(status));

    // ═══ E2-2: "ไม่มีเงินออก" ต้องรอพ้นช่วงที่คำขออาจยังค้าง · พบเครื่องหมาย = เงินออกทันที ═══

    [Fact]
    public void E22_ยอดยังไม่ขยับแต่เพิ่งพยายามคืน_ยังไม่ปลดล็อก_พ้นเวลาแล้วปลดล็อก()
    {
        var at = Utc(2026, 9, 20, 3);
        var early = GatewayRefundMath.Verify(0m, 0m, 1000m, 300m, null, at, at.AddSeconds(10));
        Assert.Equal(GatewayRefundVerificationOutcome.TooEarly, early.Outcome);
        Assert.False(early.Resolves);
        Assert.Contains("ห้ามคืนซ้ำ", early.Message);

        var justBefore = GatewayRefundMath.Verify(0m, 0m, 1000m, 300m, null, at, at + GatewayRefundMath.MinVerifyWait - TimeSpan.FromSeconds(1));
        Assert.Equal(GatewayRefundVerificationOutcome.TooEarly, justBefore.Outcome);

        var ready = GatewayRefundMath.Verify(0m, 0m, 1000m, 300m, null, at, at + GatewayRefundMath.MinVerifyWait);
        Assert.Equal(GatewayRefundVerificationOutcome.NoMoneyOut, ready.Outcome);
        Assert.True(ready.Resolves);
        Assert.True(GatewayRefundMath.MinVerifyWait >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void E22_เงินออกแล้วตัดสินได้ทันที_ไม่ต้องรอ()
    {
        var at = Utc(2026, 9, 20, 3);
        var v = GatewayRefundMath.Verify(0m, 300m, 1000m, 300m, null, at, at.AddSeconds(5));
        Assert.Equal(GatewayRefundVerificationOutcome.MoneyWentOut, v.Outcome);
        Assert.Equal(300m, v.AmountToBook);
    }

    [Fact]
    public void E22_พบเครื่องหมายของครั้งนี้แต่ยอดสะสมไม่ขยับ_ข้อมูลขัดกัน_ไม่ปลดล็อก()
    {
        var at = Utc(2026, 9, 20, 3);
        var v = GatewayRefundMath.Verify(0m, 0m, 1000m, 300m, 300m, at, at.AddHours(1));
        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent, v.Outcome);
        Assert.False(v.Resolves);
    }

    // ═══ E2-7: ลงบัญชีได้เฉพาะเมื่อส่วนต่างเท่ายอดของครั้งที่ผลไม่แน่ชัด ═══

    [Fact]
    public void E27_ตัวเลขจากผลตรวจ_ส่วนต่าง500แต่สั่งคืน300_ขัดกัน_ไม่ลงบัญชีทั้งก้อน()
    {
        // คืน 200 เคยลงบัญชีมือ (RefundedAmount ค้าง 0) · ครั้งนี้สั่ง 300 หมดเวลา · ผู้ให้บริการรายงาน 500
        var at = Utc(2026, 9, 20, 3);
        var v = GatewayRefundMath.Verify(0m, 500m, 1000m, 300m, null, at, at.AddHours(1));
        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent, v.Outcome);
        Assert.Equal(0m, v.AmountToBook);
        Assert.False(v.Resolves);
        Assert.Contains("บันทึกผลด้วยมือ", v.Message);
    }

    [Fact]
    public void E27_ทิศตรงข้าม_ส่วนต่างเท่ายอดที่สั่งคืน_ลงบัญชีตามนั้น()
    {
        var at = Utc(2026, 9, 20, 3);
        var v = GatewayRefundMath.Verify(200m, 500m, 1000m, 300m, null, at, at.AddHours(1));
        Assert.Equal(GatewayRefundVerificationOutcome.MoneyWentOut, v.Outcome);
        Assert.Equal(300m, v.AmountToBook);
    }

    [Fact]
    public void E27_แถวเก่าไม่รู้ยอดที่สั่งคืน_ขัดกัน_แต่พบเครื่องหมายใช้ยอดของรายการนั้น()
    {
        var at = Utc(2026, 9, 20, 3);
        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent,
            GatewayRefundMath.Verify(0m, 300m, 1000m, null, null, at, at.AddHours(1)).Outcome);

        var marked = GatewayRefundMath.Verify(0m, 300m, 1000m, null, 300m, at, at.AddHours(1));
        Assert.Equal(GatewayRefundVerificationOutcome.MoneyWentOut, marked.Outcome);
        Assert.Equal(300m, marked.AmountToBook);

        // เครื่องหมายกับยอดที่จดไว้ไม่ตรงกัน = ขัดกัน
        Assert.Equal(GatewayRefundVerificationOutcome.Inconsistent,
            GatewayRefundMath.Verify(0m, 250m, 1000m, 300m, 250m, at, at.AddHours(1)).Outcome);
    }

    // ═══ E2-3: บันทึกผลด้วยมือ (ทางไปต่อเมื่อผู้ให้บริการเงียบ) ═══

    [Fact]
    public void E23_บันทึกผลด้วยมือ_ด่านที่ต้องปฏิเสธ()
    {
        var at = Utc(2026, 9, 20, 3);
        var later = at.AddHours(1);
        // ไม่มีเรื่องค้าง
        Assert.False(GatewayRefundMath.CheckManualResolution(false, GatewayRefundManualDecision.NoMoneyOut, null, null,
            "ดูแดชบอร์ดแล้ว", 0m, 1000m, at, later).Ok);
        // ไม่มีหลักฐาน
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.NoMoneyOut, null, null,
            "  ", 0m, 1000m, at, later).Ok);
        // ไม่ได้เลือกผล (ค่าที่อ่านไม่ออกตกเป็น Unspecified) — ห้ามตกเป็นผลใดผลหนึ่ง
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.Unspecified, null, null,
            "ดูแล้ว", 0m, 1000m, at, later).Ok);
        // "ไม่มีเงินออก" เร็วเกินไป
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.NoMoneyOut, null, null,
            "ดูแล้ว", 0m, 1000m, at, at.AddMinutes(1)).Ok);
        // "เงินออก" ไม่มียอด / เกินยอดที่ยังคืนได้ / ไม่มีเลขอ้างอิง
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.MoneyWentOut, null, "rfnd_1",
            "ดูแล้ว", 0m, 1000m, at, later).Ok);
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.MoneyWentOut, 900m, "rfnd_1",
            "ดูแล้ว", 200m, 1000m, at, later).Ok);
        Assert.False(GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.MoneyWentOut, 300m, " ",
            "ดูแล้ว", 0m, 1000m, at, later).Ok);
    }

    [Fact]
    public void E23_ทิศตรงข้าม_บันทึกผลที่ครบหลักฐานผ่าน()
    {
        var at = Utc(2026, 9, 20, 3);
        var none = GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.NoMoneyOut, null, null,
            "แดชบอร์ดไม่มีรายการคืนของวันที่ 20/09", 0m, 1000m, at, at + GatewayRefundMath.MinVerifyWait);
        Assert.True(none.Ok);
        Assert.Equal(0m, none.AmountToBook);

        // "เงินออก" ไม่ต้องรอ (ผู้ให้บริการแสดงรายการแล้ว)
        var went = GatewayRefundMath.CheckManualResolution(true, GatewayRefundManualDecision.MoneyWentOut, 300m, "rfnd_test_1",
            "แดชบอร์ดแสดงรายการคืน 300.00", 200m, 1000m, at, at.AddSeconds(30));
        Assert.True(went.Ok);
        Assert.Equal(300m, went.AmountToBook);
    }

    [Fact]
    public void E23_รายการที่บันทึกรอบโอนแล้วคืนเงินผลไม่แน่ชัด_ไม่บล็อกรอบ_แต่เตือน()
    {
        // เดิม: รายการที่บันทึกรอบโอนแล้ว + ธงผลไม่แน่ชัด เข้าทุกรอบในอนาคต (ไม่มีตัวกรองวัน) ⇒ บล็อกถาวรถ้าผู้ให้บริการเงียบ
        var normal = new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m);
        var p = GatewaySettlementMath.Plan(new[] { normal }, 961m, GatewayFeeWhtMode.None, "PO-1001",
            settledRefundOutcomeUnknown: 1);
        Assert.True(p.Ok);
        Assert.NotNull(p.Warning);
        Assert.Contains("ผลไม่แน่ชัด", p.Warning);

        // ถ้ายอดไม่ตรง ข้อความบอกสาเหตุที่เป็นไปได้นี้ด้วย
        var mismatch = GatewaySettlementMath.Plan(new[] { normal }, 661m, GatewayFeeWhtMode.None, "PO-1001",
            settledRefundOutcomeUnknown: 1);
        Assert.Equal(SettlementBlockReason.NetMismatch, mismatch.Reason);
        Assert.Contains("ตรวจผลการคืนเงิน", mismatch.Message);

        // รายการที่บันทึกแล้วซึ่งยังมียอดคืน (รู้ยอด) ค้างหัก + ธง ⇒ ไม่บล็อกด้วยเหตุ "ผลไม่แน่ชัด"
        var settledFlagged = new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m, RefundedAmount: 100m, AlreadySettled: true,
            RefundOutcomeUnknown: true);
        var p2 = GatewaySettlementMath.Plan(new[] { normal, settledFlagged }, 861m, GatewayFeeWhtMode.None, "PO-1002");
        Assert.NotEqual(SettlementBlockReason.RefundOutcomeUnknown, p2.Reason);
        Assert.True(p2.Ok);
    }

    [Fact]
    public void E23_ทิศตรงข้าม_รายการที่ยังไม่บันทึกรอบโอนในช่วงของรอบยังบล็อก_และไม่มีธงไม่เตือน()
    {
        var unsettled = new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m, RefundOutcomeUnknown: true);
        var p = GatewaySettlementMath.Plan(new[] { unsettled }, 961m, GatewayFeeWhtMode.None, "T");
        Assert.False(p.Ok);
        Assert.Equal(SettlementBlockReason.RefundOutcomeUnknown, p.Reason);

        // บริษัทไม่จด VAT ⇒ ไม่มีคำเตือนโหมด VAT (R-E6) · ไม่มีรายการผลไม่แน่ชัด ⇒ ไม่มีคำเตือนเลย
        var clean = GatewaySettlementMath.Plan(new[] { new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m) }, 961m,
            GatewayFeeWhtMode.None, "T", companyVatRegistered: false);
        Assert.True(clean.Ok);
        Assert.Null(clean.Warning);
        Assert.Null(GatewaySettlementMath.Plan(new[] { new SettlementIntentInput(Guid.NewGuid(), 1000m, 39m, 0m) }, 961m,
            GatewayFeeWhtMode.None, "T", companyVatRegistered: false, settledRefundOutcomeUnknown: 0).Warning);
    }

    // ═══ E2-4: ใบกำกับค่าธรรมเนียมฉบับเดียวเคลมได้ครั้งเดียว ═══

    [Fact]
    public void E24_ใบกำกับเลขเดิมผู้ออกเดิม_ถูกเคลมแล้ว_บล็อกพร้อมเลขใบสำคัญเดิม()
    {
        var priors = new[] { GatewayFeeVatClaim.PriorClaim("JV-2026-09-0007", "INV-A", ValidTaxId, null) };
        var dup = GatewayFeeVatClaim.FindDuplicate(priors, "INV-A", ValidTaxId);
        Assert.NotNull(dup);
        Assert.Equal("JV-2026-09-0007", dup!.Value.EntryNumber);
        Assert.Contains("JV-2026-09-0007", GatewayFeeVatClaim.DuplicateMessage(dup.Value, "INV-A"));

        // ตัวพิมพ์/ช่องว่างหัวท้าย/ขีดคั่นเลขผู้เสียภาษีไม่ทำให้หลุด
        Assert.NotNull(GatewayFeeVatClaim.FindDuplicate(priors, " inv-a ", "0-1055-36000-31-3"));
    }

    [Fact]
    public void E24_ทิศตรงข้าม_เลขที่เดียวกันจากผู้ออกคนละราย_หรือคนละเลขที่_ผ่าน()
    {
        var priors = new[] { GatewayFeeVatClaim.PriorClaim("JV-1", "INV-A", ValidTaxId, null) };
        Assert.Null(GatewayFeeVatClaim.FindDuplicate(priors, "INV-A", OtherTaxId));
        Assert.Null(GatewayFeeVatClaim.FindDuplicate(priors, "INV-B", ValidTaxId));
        Assert.Null(GatewayFeeVatClaim.FindDuplicate(Array.Empty<GatewayFeeVatPriorClaim>(), "INV-A", ValidTaxId));
    }

    [Fact]
    public void E24_ใบสำคัญเก่าก่อนมีช่องโครงสร้าง_อ่านเลขผู้เสียภาษีจากคำอธิบาย()
    {
        var legacy = GatewayFeeVatClaim.PriorClaim("JV-9", "INV-A", null, $"ภาษีซื้อ-ผู้ให้บริการ {ValidTaxId} สาขา 00000");
        Assert.Equal(ValidTaxId, legacy.SupplierTaxId);
        Assert.NotNull(GatewayFeeVatClaim.FindDuplicate(new[] { legacy }, "INV-A", ValidTaxId));
        Assert.Null(GatewayFeeVatClaim.FindDuplicate(new[] { legacy }, "INV-A", OtherTaxId));

        // หาเลขผู้เสียภาษีไม่เจอเลย = ทิศปลอดภัย (บล็อกให้คนตรวจ ไม่ปล่อยเคลมซ้ำเงียบ)
        var unknown = GatewayFeeVatClaim.PriorClaim("JV-8", "INV-A", null, "ภาษีซื้อ-ไม่มีเลข");
        Assert.Null(unknown.SupplierTaxId);
        Assert.NotNull(GatewayFeeVatClaim.FindDuplicate(new[] { unknown }, "INV-A", OtherTaxId));
    }

    // ═══ E2-6: เดือนภาษีที่ยื่นแล้ว ═══

    [Fact]
    public void E26_ข้อความเดือนภาษีที่ยื่นแล้วบอกเดือนและทางไปต่อ()
    {
        var m = GatewayFeeVatClaim.DeclaredVatMonthMessage(Utc(2026, 8, 31, 5));
        Assert.Contains("08/2026", m);
        Assert.Contains("§82/3", m);
    }

    // ═══ E2-5: บรรทัดรายงานภาษีซื้อของใบสำคัญเคลมอ่านจากช่องโครงสร้าง ═══

    [Fact]
    public void E25_ใบสำคัญที่มีใบกำกับโครงสร้าง_วันที่เลขที่สาขามาจากใบกำกับ()
    {
        var f = JournalInputTaxInvoice.Resolve(Utc(2026, 10, 15), "2026090001", Utc(2026, 9, 30), "ผู้ให้บริการ จำกัด",
            ValidTaxId, "00001", "JV-2026-10-0003", "ผู้ให้บริการ จำกัด 0105536000313 สาขา 00001", null);
        Assert.True(f.FromStructuredFields);
        Assert.Equal(Utc(2026, 9, 30), f.TransactionDate);   // วันที่บนใบกำกับ ไม่ใช่วันที่เคลม
        Assert.Equal("2026090001", f.DocumentNo);             // เลขล้วนไม่ตกเป็นเลขใบสำคัญ
        Assert.Equal("ผู้ให้บริการ จำกัด", f.SupplierName);
        Assert.Equal(ValidTaxId, f.SupplierTaxId);
        Assert.Equal("00001", f.SupplierBranch);

        // เลขที่ยาวไม่ถูกตัด
        Assert.Equal("OMTH-INV-2026-09-0001", JournalInputTaxInvoice.Resolve(Utc(2026, 10, 1), "OMTH-INV-2026-09-0001",
            Utc(2026, 9, 30), "x", ValidTaxId, "00000", "INV-2026-09-0001", null, null).DocumentNo);
    }

    [Fact]
    public void E25_ทิศตรงข้าม_ใบสำคัญอื่นใช้ค่าที่แกะมาตามเดิม()
    {
        var f = JournalInputTaxInvoice.Resolve(Utc(2026, 10, 15), null, null, null, null, null,
            "REC260601001", "ผู้ขาย", ValidTaxId);
        Assert.False(f.FromStructuredFields);
        Assert.Equal(Utc(2026, 10, 15), f.TransactionDate);
        Assert.Equal("REC260601001", f.DocumentNo);
        Assert.Equal("ผู้ขาย", f.SupplierName);
        Assert.Equal(ValidTaxId, f.SupplierTaxId);
        Assert.Null(f.SupplierBranch);
    }
}
