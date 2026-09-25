using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 194 ทีม M — แก้ผลฝ่ายค้านเงิน/ภาษี (<c>erp-review/2026-09-25/review194-money.md</c> M1–M6 + PLAUSIBLE · regsec C2/P1)
/// ทุกข้อล็อกสองครึ่ง: <b>ใบที่พังกลับมาถูก</b> · <b>ใบที่ถูกอยู่แล้วไม่ถูกแตะ</b> — ตัวเลขใช้ตัวอย่างจากรายงานฝ่ายค้านตรง ๆ
/// (1,070/70 → 35+35 · ส่งออก 0% 100,000 · มัดจำเต็มยอด 1,000)
/// </summary>
public class DepositForfeitRound194MTests
{
    private static readonly Guid Co = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid DepA = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid DepB = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");

    // ═════════════════ M2 — ใบ VAT 0 โดยชอบ ริบแล้วไม่ออกใบกำกับ 7% (ทิศตรงข้าม) ═════════════════

    [Fact]
    public void M2_มัดจำส่งออก0เปอร์เซ็นต์ไม่เลื่อนVAT_ริบแล้วไม่มีVAT_ไม่มีธงย้อนหลัง()
    {
        // รายงาน: มัดจำบริการส่งออก 100,000 บรรทัด 0% ไม่ deferred → เดิมได้ใบกำกับ VAT 6,542.06 + ธงยื่นเพิ่มเติม
        // รอบ 194 R2-2: "รู้แน่ว่า VAT 0 มาแต่แรก" ได้เฉพาะใบที่มีลักษณะเงิน (ใบรอบ 194+ — ธงเลื่อนตั้งโดยตัวจัดรูปตัวเดียว) · ใบเดิม (NULL)
        // หน้าตาเดียวกัน = กำกวม ⇒ ย้ายไปล็อกที่ DepositRound194R2Tests (ทิศปลอดภัย = คิด VAT / ผู้ใช้ยืนยัน "ไม่มี VAT มาแต่แรก" ได้)
        // เดิมเทสต์นี้วน { null, PartOfPrice } — ครึ่ง null ล็อกพฤติกรรมผิด (ใบมัดจำเต็มยอดก่อน 24/09 ถูกตีเป็น "VAT 0 โดยชอบ" ⇒ ภาษีขายหาย)
        var f = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 0m, vatPendingUnrecognized: false,
            companyVatRate: 7m, depositOutputVatDeferred: false);
        Assert.Equal(DepositForfeitVatAction.ZeroVatAtIssue, f.Action);
        Assert.Equal(0m, f.ForfeitInvoiceVatRate);
        Assert.False(f.LateVat);
        Assert.Contains("VAT 0 โดยชอบ", f.Explanation);
        // ไม่ถามว่า "มี VAT ไหม" — ป้ายจะเป็นข้อความเท็จ
        Assert.Null(DepositKindDocumentRules.ForfeitOptions(DepositNature.PartOfPrice, 0m, false, 7m, depositOutputVatDeferred: false));
    }

    [Fact]
    public void M2_มัดจำเต็มยอดจริง_deferred_ยังออกใบกำกับตามเดิม_และผู้เรียกเดิมที่ไม่ส่งธงคงพฤติกรรมเดิม()
    {
        var full = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 0m, true,
            companyVatRate: 7m, depositOutputVatDeferred: true);
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit, full.Action);
        Assert.Equal(7m, full.ForfeitInvoiceVatRate);
        // ผู้เรียกที่ไม่ส่งธง (null) = ไม่ทราบ ⇒ ถือว่าเลื่อน (ทิศปลอดภัย: ใบกำกับมองเห็นและแก้ได้)
        Assert.Equal(DepositForfeitVatAction.IssueTaxInvoiceForForfeit,
            DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, null, 0m, false).Action);
        // ค่าเสียหายของเงินประกัน VAT 0 ที่ไม่เลื่อน ยังเป็นค่าเสียหาย (บัญชีริบ) ไม่ถูกกลืนเป็น "VAT 0 โดยชอบ"
        // (R2-2: เดิมใช้ใบ NULL ในบรรทัดนี้แล้วเรียกมันว่า "ใบ VAT 0 โดยชอบ" — ใบ NULL คือใบที่กำกวม ไม่ใช่ใบที่รู้ว่า VAT 0)
        Assert.Equal(DepositForfeitVatAction.CompensationNoVat,
            DepositPolicyResolver.ForfeitVatDecision(DepositNature.RefundableSecurity, DepositForfeitAs.Compensation, 0m, false,
                depositOutputVatDeferred: false).Action);
        // บริษัทไม่จด VAT ยังได้ข้อความของตัวเอง (ไม่ใช่ "VAT 0 โดยชอบ")
        Assert.Equal(DepositForfeitVatAction.CompanyNotVatRegistered,
            DepositPolicyResolver.ForfeitVatDecision(null, null, 0m, false, companyVatRate: 0m, depositOutputVatDeferred: false).Action);
    }

    [Fact]
    public void P1_ที่พักไม่คิดVAT_ริบแล้วใบVAT0เป็นVAT0โดยชอบ_ที่พักคิดVATหรือไม่ระบุตามธงบนใบ()
    {
        // ใบเก่าที่ถูกจัดรูปเป็นเต็มยอด (0 + deferred) ด้วยอัตราบริษัทก่อนแก้ ⇒ ช่องทางบอก 0 ⇒ ไม่ออกใบกำกับ 7%
        // R2-2: ตัวนี้คืนสามสถานะ (false = รู้แน่ว่า VAT 0 · true = เลื่อน · null = กำกวม) และรับลักษณะเงินด้วย
        Assert.False(DepositPolicyResolver.ForfeitZeroVatDeferred(DepositNature.PartOfPrice, documentDeferred: true, channelVatRate: 0m));
        Assert.False(DepositPolicyResolver.ForfeitZeroVatDeferred(null, documentDeferred: true, channelVatRate: 0m));
        Assert.True(DepositPolicyResolver.ForfeitZeroVatDeferred(DepositNature.PartOfPrice, documentDeferred: true, channelVatRate: 7m));
        Assert.True(DepositPolicyResolver.ForfeitZeroVatDeferred(null, documentDeferred: true, channelVatRate: null));
        Assert.False(DepositPolicyResolver.ForfeitZeroVatDeferred(DepositNature.PartOfPrice, documentDeferred: false, channelVatRate: null));
        var f = DepositPolicyResolver.ForfeitVatDecision(DepositNature.PartOfPrice, DepositForfeitAs.PriceOrFee, 0m, true, 7m,
            depositOutputVatDeferred: true, channelVatRate: 0m);
        Assert.Equal(DepositForfeitVatAction.ZeroVatAtIssue, f.Action);
    }

    [Fact]
    public void P1_อัตราจัดรูปใบมัดจำ_ช่องทางต่ำกว่าบริษัทชนะ_สูงกว่าไม่มีผล_ไม่ระบุคืออัตราบริษัท()
    {
        Assert.Equal(7m, DepositPolicyResolver.ShapingVatRate(7m, null));
        Assert.Equal(0m, DepositPolicyResolver.ShapingVatRate(7m, 0m));
        Assert.Equal(7m, DepositPolicyResolver.ShapingVatRate(7m, 10m));   // ห้ามคิดเกินที่บริษัทคิดได้
        Assert.Equal(0m, DepositPolicyResolver.ShapingVatRate(7m, -1m));
        Assert.Equal(0m, DepositPolicyResolver.ShapingVatRate(0m, 7m));    // บริษัทไม่จด VAT
    }

    [Fact]
    public void P1_ตัวจัดรูปด้วยอัตราช่องทาง0_ใบมัดจำเต็มยอดของที่พักไม่คิดVAT_คงรูปVAT0ไม่เลื่อน()
    {
        var kind = new DepositKind
        {
            Id = DepA, CompanyId = Co, Code = "ADVANCE", Name = "มัดจำค่าห้องพัก", Nature = DepositNature.PartOfPrice,
            VatTreatment = DepositVatTreatment.FullDeposit, PolicyReason = "ตามสัญญา", IsActive = true,
        };
        var decision = DepositPolicyResolver.ResolveKind(Co, DepositSupplyNature.Service, kind, null, null, null, null, false);
        var lines = new List<DocumentLineRequest> { new("มัดจำ", 1m, "รายการ", 1000m, 0m, 0m, 0m, null) };
        // ก่อนแก้: DocumentService จัดซ้ำด้วยอัตราบริษัท 7 ⇒ deferred=true (มัดจำเต็มยอด)
        var companyRate = DepositDocumentShaping.Apply(lines, decision, DepositPolicyResolver.ShapingVatRate(7m, null), false, null);
        Assert.True(companyRate.DepositOutputVatDeferred);
        // หลังแก้: ที่พัก ChargeVat=false ส่งอัตรา 0 ⇒ คงรูป (VAT 0 · ไม่เลื่อน) เท่ากับที่ที่พักจัดเอง
        var channel = DepositDocumentShaping.Apply(lines, decision, DepositPolicyResolver.ShapingVatRate(7m, 0m), false, null);
        Assert.False(channel.DepositOutputVatDeferred);
        Assert.All(channel.Lines, l => Assert.Equal(0m, l.VatRate));
    }

    // ═════════════════ M3 — VAT พักที่ย้ายเข้า 21911 = ส่วนที่เหลือจริง (1,070/70 → 35 + 35) ═════════════════

    [Fact]
    public void M3_ค่าเสียหายครึ่งหนึ่งแล้วรับรู้ส่วนที่เหลือแบบราคา_ย้ายVATพักเฉพาะ35_ไม่ใช่70()
    {
        // มัดจำ VatPendingUndue 1,070 (ฐาน 1,000 · VAT 70) → ริบเป็นค่าเสียหายฐาน 500 = กลับ 21913 เข้ารายได้ 35
        var reversed = DepositKindDocumentRules.CompensationVatReversal(70m, 500m, 1000m);
        Assert.Equal(35m, reversed);
        var pendingAfter = 70m - reversed;
        // ส่วนที่เหลือฐาน 500 แบบราคา: เดิม Dr 21913 70 (ติดลบ 35) · ตอนนี้ = ที่ยังพักจริง 35
        Assert.Equal(35m, DepositPolicyResolver.UndueVatToRecognize(70m, pendingAfter));
        // ภ.พ.30: แถวมัดจำรายงานเท่าที่ย้ายเข้า 21911 จริง (35) ฐานตามสัดส่วน (500) — เดิม 70/1,000
        var reported = DepositPolicyResolver.ReportedRecognizedDepositVat(70m, 35m);
        Assert.Equal(35m, reported);
        Assert.Equal(500m, DepositPolicyResolver.ReportedRecognizedDepositBase(1000m, 70m, reported));
    }

    [Fact]
    public void M3_ใบที่ไม่เคยถูกตัด21913_ยังย้ายVATเต็มใบ_และใบเก่าไม่มีร่องรอยในGLคงพฤติกรรมเดิม()
    {
        Assert.Equal(70m, DepositPolicyResolver.UndueVatToRecognize(70m, 70m));
        Assert.Equal(70m, DepositPolicyResolver.UndueVatToRecognize(70m, null));    // ไม่มีร่องรอย 21913 = VAT เต็มใบ (เดิม)
        Assert.Equal(70m, DepositPolicyResolver.UndueVatToRecognize(70m, 100m));    // ไม่เกิน VAT ของใบ
        Assert.Equal(0m, DepositPolicyResolver.UndueVatToRecognize(70m, 0m));       // ไม่เหลือ = ไม่ย้าย (ไม่ประทับ)
        Assert.Equal(0m, DepositPolicyResolver.UndueVatToRecognize(70m, -5m));
        Assert.Equal(70m, DepositPolicyResolver.ReportedRecognizedDepositVat(70m, null));
        Assert.Equal(70m, DepositPolicyResolver.ReportedRecognizedDepositVat(70m, 0m));
        Assert.Equal(70m, DepositPolicyResolver.ReportedRecognizedDepositVat(70m, 70m));
        Assert.Equal(1000m, DepositPolicyResolver.ReportedRecognizedDepositBase(1000m, 70m, 70m));
        Assert.Equal(1000m, DepositPolicyResolver.ReportedRecognizedDepositBase(1000m, 0m, 0m));
    }

    // ═════════════════ M4 — tax point ของ VAT ที่เกิดจากการริบ ตามสถานะงวดเดือนรับเงิน ═════════════════

    [Fact]
    public void M4_งวดเดือนรับเงินยังไม่ยื่นและยังไม่เลยกำหนด_taxPointเท่ากับวันรับเงิน_ไม่มีธง()
    {
        // R2-1: "ยังไม่ยื่น" ต้องมีหลักฐานว่ายังไม่เลยกำหนดยื่น (วันนี้ 03/09 ≤ 15/09) — ไม่ใช่แค่ไม่มีแถวยื่นในระบบ
        var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 8, 28), new DateTime(2026, 9, 3),
            depositPeriodLocked: false, today: new DateTime(2026, 9, 3));
        Assert.Equal(new DateTime(2026, 8, 28), tp.TaxPointDate);
        Assert.False(tp.LateFlag);
        Assert.NotNull(tp.Note);
        Assert.DoesNotContain(DepositPolicyResolver.LateVatMarker, tp.Note);
        Assert.Contains("08/2026", tp.Note);
        Assert.Contains("ยังไม่เลยกำหนดยื่น", tp.Note);
    }

    [Fact]
    public void M4_งวดเดือนรับเงินยื่นแล้ว_VATเข้างวดปัจจุบัน_ธงบอกว่านำส่งงวดไหนห้ามนำส่งซ้ำ()
    {
        var tp = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 7, 3), new DateTime(2026, 9, 10),
            depositPeriodLocked: true, today: new DateTime(2026, 9, 10));
        Assert.Equal(new DateTime(2026, 9, 10), tp.TaxPointDate);
        Assert.True(tp.LateFlag);
        Assert.StartsWith(DepositPolicyResolver.LateVatMarker, tp.Note);
        Assert.Contains("นำส่งในงวด 09/2026", tp.Note);
        Assert.Contains("ถึงกำหนดงวด 07/2026", tp.Note);
        Assert.Contains("§89/1", tp.Note);
        Assert.Contains("ห้ามนำส่งซ้ำ", tp.Note);
        Assert.DoesNotContain("ต้องยื่น ภ.พ.30 เพิ่มเติมของเดือนนั้น", tp.Note);   // ข้อความเดิมที่ทำให้ VAT ซ้ำ
    }

    [Fact]
    public void M4_เดือนเดียวกัน_และเงินประกันที่หักเป็นค่าธรรมเนียม_ไม่ใช่ภาษีย้อนหลัง_ไม่แตะ()
    {
        var same = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, true, new DateTime(2026, 9, 2), new DateTime(2026, 9, 20),
            depositPeriodLocked: false, today: new DateTime(2026, 9, 20));
        Assert.Equal(new DateTime(2026, 9, 2), same.TaxPointDate);
        Assert.False(same.LateFlag);
        Assert.Null(same.Note);
        var sec = DepositPolicyResolver.ForfeitTaxPointDecision(DepositForfeitVatRoute.UndueReclassification, false, new DateTime(2026, 7, 3), new DateTime(2026, 9, 10),
            depositPeriodLocked: true, today: new DateTime(2026, 9, 10));
        Assert.Equal(new DateTime(2026, 9, 10), sec.TaxPointDate);
        Assert.False(sec.LateFlag);
        Assert.Null(sec.Note);
    }

    // ═════════════════ M5 — "ริบ" แยกจาก "รับรู้ตามปกติ" ═════════════════

    [Fact]
    public void M5_รับรู้ตามปกติ_ใช้บัญชีที่ผู้เรียกระบุ_บัญชีริบของประเภทไม่ทับ_คำอธิบายไม่ใช่ริบ()
    {
        var normal = DepositPolicyResolver.RevenueAccountPlan(null, "41100", kindForfeitCode: "43099");
        Assert.Equal(new[] { "41100", "41000", "42000" }, normal.Codes);
        Assert.False(normal.IsForfeit);
        Assert.False(normal.FailIfNone);
        Assert.Equal(new[] { "41000", "42000" }, DepositPolicyResolver.RevenueAccountPlan(null, null, "43099").Codes);
        // ริบแบบราคา (มี VAT) ก็ไม่ใช้บัญชีริบของประเภท
        Assert.DoesNotContain("43099",
            DepositPolicyResolver.RevenueAccountPlan(DepositForfeitVatAction.ReclassifyUndueToDue, null, "43099").Codes);
    }

    [Fact]
    public void M5_ริบนอกระบบVAT_ค่าผู้ใช้ชนะ_แล้วบัญชีริบของประเภท()
    {
        var nv = DepositPolicyResolver.RevenueAccountPlan(DepositForfeitVatAction.NonVatNoVat, null, "43040");
        Assert.Equal(new[] { "43040", "41000", "42000" }, nv.Codes);
        Assert.True(nv.IsForfeit);
        Assert.Equal("41500", DepositPolicyResolver.RevenueAccountPlan(DepositForfeitVatAction.NonVatNoVat, "41500", "43040").Codes[0]);
    }

    [Fact]
    public void Pc_ริบเงินประกันเป็นค่าเสียหาย_ไม่ตกไปรายได้ขาย_ไม่พบบัญชีต้องล้มดัง()
    {
        var comp = DepositPolicyResolver.RevenueAccountPlan(DepositForfeitVatAction.CompensationNoVat, null, null);
        Assert.Equal(new[] { "43080", "43070" }, comp.Codes);
        Assert.True(comp.FailIfNone);
        Assert.DoesNotContain("41000", comp.Codes);
        Assert.Equal(new[] { "43099", "43080", "43070" },
            DepositPolicyResolver.RevenueAccountPlan(DepositForfeitVatAction.CompensationNoVat, null, "43099").Codes);
        Assert.Contains("ตั้ง “บัญชีรายได้เมื่อริบ”", DepositPolicyResolver.CompensationAccountMissingMessage);
    }

    [Fact]
    public void M5_รับรู้ตามปกติ_มัดจำเต็มยอดที่เป็นราคายังไม่เคยเสียVAT_ปฏิเสธพร้อมทางไปต่อ()
    {
        foreach (var n in new DepositNature?[] { null, DepositNature.PartOfPrice })
        {
            var p = DepositPolicyResolver.PlainRealizeProblem(n, 0m, depositOutputVatDeferred: true, companyVatRate: 7m);
            Assert.NotNull(p);
            Assert.Contains(DepositPolicyResolver.PlainRealizeRuleCode, p);
            Assert.Contains("ริบมัดจำ", p);        // ทางไปต่อ 1
            Assert.Contains("หักมัดจำ", p);        // ทางไปต่อ 2
        }
    }

    [Fact]
    public void M5_รับรู้ตามปกติ_ใบที่ถูกอยู่แล้วไม่ถูกปฏิเสธ()
    {
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(DepositNature.PartOfPrice, 65.42m, false, 7m));   // VAT ทันที
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(DepositNature.PartOfPrice, 65.42m, true, 7m));    // VAT พัก (ย้ายเข้า 21911)
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(DepositNature.NonVatSupply, 0m, true, 7m));      // ค่าเช่าล่วงหน้าตามงวด
        // R2-3: เงินประกันที่ต้องคืน "ส่งมอบแล้ว" ถูกปฏิเสธแล้ว (เดิมบรรทัดนี้ล็อกว่าผ่าน = รายได้ขาย 41000 ไม่มี VAT) — ดู DepositRound194R2Tests
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(DepositNature.PartOfPrice, 0m, false, 7m));      // VAT 0 โดยชอบ (ใบรอบ 194+)
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(null, 0m, true, 0m));                            // บริษัทไม่จด VAT
        Assert.Null(DepositPolicyResolver.PlainRealizeProblem(null, 0m, false, 7m, channelVatRate: 0m));       // ช่องทางไม่คิด VAT
    }

    // ═════════════════ M6 — คำเตือน 21913 ค้างเกิน 90 วัน ไม่ฟ้องใบที่ปิดแล้ว ═════════════════

    private static Document Dep(decimal realized = 0m, decimal refunded = 0m, DateTime? realizedAt = null) => new()
    {
        CompanyId = Co, IsDeposit = true, DepositOutputVatDeferred = true, SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m,
        DepositRealizedAmount = realized, DepositRefundedAmount = refunded, DepositRealizedAt = realizedAt,
    };

    [Fact]
    public void M6_ใบที่ริบเป็นค่าเสียหายครบ_หรือรับรู้บวกคืนครบ_ไม่ถูกฟ้อง()
    {
        var open = DepositPolicyResolver.UndueVatStillOpen.Compile();
        Assert.False(open(Dep(realized: 1000m, realizedAt: new DateTime(2026, 6, 1))));   // ค่าเสียหายครบ (ไม่ประทับ RecognizedAt โดยตั้งใจ)
        Assert.False(open(Dep(realized: 500m, refunded: 535m)));                           // ค่าเสียหาย 500 + คืน 535 = ครบฐาน
        Assert.False(open(Dep(refunded: 1070m)));                                          // คืนครบ
    }

    [Fact]
    public void M6_ใบที่VATยังพักจริง_ยังถูกฟ้อง()
    {
        var open = DepositPolicyResolver.UndueVatStillOpen.Compile();
        Assert.True(open(Dep()));
        Assert.True(open(Dep(realized: 500m)));      // ค่าเสียหายครึ่งเดียว — อีกครึ่งยังพัก 21913
        Assert.True(open(Dep(refunded: 535m)));      // คืนครึ่งเดียว
    }

    // ═════════════════ M1 — ใบกำกับของการริบ: idempotent · ทางไปต่อข้อความเดียว · ริบหลายครั้ง (C2) ═════════════════

    private static ForfeitInvoiceCandidate Inv(DocumentStatus st, decimal total, decimal due, string no = "TIV-001")
        => new(Guid.NewGuid(), no, st, total, due);

    [Fact]
    public void M1_ร่างค้างยอดตรง_อนุมัติใบเดิม_ออกแล้วยังไม่ตัดชำระ_ตัดชำระใบเดิม_ไม่สร้างใหม่()
    {
        var draft = DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Draft, 1000m, 1000m, "DRAFT-1") }, 1000m, "DEP-1");
        Assert.Equal(ForfeitInvoiceStep.ApproveThenApply, draft.Step);
        Assert.Equal("DRAFT-1", draft.InvoiceNumber);
        var rejected = DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Rejected, 1000m, 1000m) }, 1000m, "DEP-1");
        Assert.Equal(ForfeitInvoiceStep.ApproveThenApply, rejected.Step);
        var approved = DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Approved, 1000m, 1000m) }, 1000m, "DEP-1");
        Assert.Equal(ForfeitInvoiceStep.ApplyOnly, approved.Step);
    }

    [Fact]
    public void M1_ไม่มีใบค้าง_หรือใบก่อนหน้าตัดชำระครบแล้ว_หรือถูกยกเลิก_สร้างใหม่ได้_ริบบางส่วนครั้งที่สอง()
    {
        Assert.Equal(ForfeitInvoiceStep.CreateNew,
            DepositKindDocumentRules.ResumeForfeitInvoice(Array.Empty<ForfeitInvoiceCandidate>(), 500m, "DEP-1").Step);
        // C2 regsec: ริบครั้งแรก 500 ออกใบกำกับ + ตัดชำระครบ (Paid) ⇒ ริบส่วนที่เหลือ 500 = ใบใหม่ ไม่ขัดกัน
        Assert.Equal(ForfeitInvoiceStep.CreateNew,
            DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Paid, 500m, 0m) }, 500m, "DEP-1").Step);
        Assert.Equal(ForfeitInvoiceStep.CreateNew,
            DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Voided, 1000m, 1000m) }, 1000m, "DEP-1").Step);
    }

    [Fact]
    public void M1_ใบค้างยอดไม่ตรง_หรือค้างหลายใบ_ขัดกัน_บอกทางไปต่อ_ห้ามออกใบที่สอง()
    {
        var diff = DepositKindDocumentRules.ResumeForfeitInvoice(new[] { Inv(DocumentStatus.Draft, 1000m, 1000m, "DRAFT-9") }, 500m, "DEP-1");
        Assert.Equal(ForfeitInvoiceStep.Conflict, diff.Step);
        Assert.Contains("DRAFT-9", diff.Problem);
        Assert.Contains("1,000.00", diff.Problem);
        Assert.Contains("500.00", diff.Problem);
        var many = DepositKindDocumentRules.ResumeForfeitInvoice(
            new[] { Inv(DocumentStatus.Draft, 500m, 500m, "A"), Inv(DocumentStatus.Approved, 500m, 500m, "B") }, 500m, "DEP-1");
        Assert.Equal(ForfeitInvoiceStep.Conflict, many.Step);
        Assert.Contains("A", many.Problem);
        Assert.Contains("B", many.Problem);
    }

    [Fact]
    public void M1_ป้ายกุญแจใบกำกับของการริบ_ผูกกับมัดจำใบเดียว()
    {
        var note = "ใบกำกับภาษีของยอดที่ริบจากมัดจำ DEP-1 " + DepositKindDocumentRules.ForfeitInvoiceMarker(DepA);
        Assert.True(DepositKindDocumentRules.IsForfeitInvoiceOf(note, DepA));
        Assert.False(DepositKindDocumentRules.IsForfeitInvoiceOf(note, DepB));
        Assert.False(DepositKindDocumentRules.IsForfeitInvoiceOf(null, DepA));
        Assert.False(DepositKindDocumentRules.IsForfeitInvoiceOf("ใบกำกับทั่วไป", DepA));
    }

    [Fact]
    public void M1_ทางไปต่อข้อความเดียว_กดริบซ้ำปลอดภัย_ไม่สั่งห้ามกดซ้ำอีก()
    {
        var hint = DepositKindDocumentRules.ForfeitRetryHint("RV-2026-0001", 1000m);
        Assert.Contains("RV-2026-0001", hint);
        Assert.Contains("ริบมัดจำ", hint);
        Assert.Contains("1,000.00", hint);
        Assert.Contains("ไม่ออกใบซ้ำ", hint);
        Assert.DoesNotContain("ห้ามกดรับรู้", hint);   // ข้อความเดิมของ DocumentService ที่ขัดกับที่พัก
    }

    [Fact]
    public void C2_ตัดชำระมัดจำใบเดียวเข้าหลายใบ_ผ่อนเฉพาะใบกำกับของการริบของมัดจำใบนี้()
    {
        Assert.False(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(false, false));   // ใบสุดท้ายสองใบ = one-shot เดิม
        Assert.True(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(false, true));     // ตัดใบสุดท้ายบางส่วน แล้วริบส่วนที่เหลือ
        Assert.True(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(true, false));     // ริบบางส่วน แล้วตัดชำระใบสุดท้าย
        Assert.True(DepositKindDocumentRules.ApplyToAnotherTargetAllowed(true, true));      // ริบบางส่วนสองครั้ง
    }

    // ═════════════════ P-e — ตัดสินเงินประกันจาก "ใบที่ถูกหักจริง" ═════════════════

    private static DepositRefCandidate C(Guid id, string no, string? rf, DepositNature? n) => new(id, no, rf, n);

    [Fact]
    public void Pe_เลขจองร่วม_มัดจำค่าห้องกับเงินประกัน_ใบที่ถูกหักคือมัดจำค่าห้อง_ไม่บล็อกผิด()
    {
        var room = C(DepA, "RV-1", "LR-2026-001", DepositNature.PartOfPrice);
        var sec = C(DepB, "RV-2", "LR-2026-001", DepositNature.RefundableSecurity);
        var picked = DepositPolicyResolver.ResolveDeductedDeposits(new[] { "LR-2026-001" }, new[] { room, sec });
        Assert.Equal(DepA, Assert.Single(picked).Id);
        Assert.Null(DepositPolicyResolver.SecurityDeductionProblemForRefs(new[] { "LR-2026-001" }, new[] { room, sec }));
    }

    [Fact]
    public void Pe_อ้างเงินประกันตรงตัว_ยังถูกบล็อก()
    {
        var room = C(DepA, "RV-1", "LR-2026-001", DepositNature.PartOfPrice);
        var sec = C(DepB, "RV-2", "LR-2026-001", DepositNature.RefundableSecurity);
        var byNumber = DepositPolicyResolver.SecurityDeductionProblemForRefs(new[] { "RV-2" }, new[] { room, sec });
        Assert.NotNull(byNumber);
        Assert.Contains("RV-2", byNumber);
        Assert.Contains(DepositPolicyResolver.SecurityDeductRuleCode, byNumber);
        // เลขอ้างอิงที่ชี้เงินประกันอย่างเดียว
        Assert.NotNull(DepositPolicyResolver.SecurityDeductionProblemForRefs(new[] { "LR-2026-001" }, new[] { sec }));
        // ใบเดิมไม่ทราบลักษณะ = ไม่บล็อก (พฤติกรรมเดิม)
        Assert.Null(DepositPolicyResolver.SecurityDeductionProblemForRefs(new[] { "RV-9" }, new[] { C(DepA, "RV-9", null, null) }));
    }

    // ═════════════════ P-f — บัญชี 21530 ตัวกรองเดียว ═════════════════

    [Fact]
    public void Pf_บัญชีเงินประกันต้องเปิดใช้และไม่ถูกลบและเป็นของบริษัทนี้()
    {
        var usable = DepositKindCatalog.UsableSecurityAccount(Co).Compile();
        ChartOfAccount Acc(bool active = true, bool deleted = false, Guid? co = null, string code = "21530")
            => new() { CompanyId = co ?? Co, AccountCode = code, AccountName = "เงินประกันความเสียหาย", IsActive = active, IsDeleted = deleted };
        Assert.True(usable(Acc()));
        Assert.False(usable(Acc(active: false)));    // เดิม DocumentService ดูแค่ !IsDeleted ⇒ บัญชีปิดใช้ถูกเลือก
        Assert.False(usable(Acc(deleted: true)));    // เดิม DepositKindCatalog ดูแค่ IsActive
        Assert.False(usable(Acc(co: Guid.NewGuid())));
        Assert.False(usable(Acc(code: "21620")));
    }
}
