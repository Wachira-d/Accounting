using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 (คำตัดสินเจ้าของข้อ 148 ⇒ ข้อเสนอ 143) — ใบสำคัญจ่ายใบเดียวจ่ายหลายผู้ถูกหัก (โบรกเกอร์ส่งเบี้ยของบริษัทประกัน 2 แห่ง) ·
/// ตัวแบ่งเดียว <see cref="WhtPayeeSplit"/> ทั้งสองทิศ: ใบหลายผู้ถูกหักแยกส่วน · <b>ใบผู้ถูกหักรายเดียวได้ส่วนเดียวเหมือนเดิมทุกประการ</b>
/// </summary>
public class WhtPayeeSplitTests
{
    private static readonly Guid Broker = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InsurerA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InsurerB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static WhtPayeeLine L(decimal wht, Guid? payee = null) => new(payee, wht);

    // ── ทิศเดิม: ใบผู้ถูกหักรายเดียว ──

    [Fact]
    public void ใบไม่มีผู้ถูกหักรายบรรทัด_ส่วนเดียวของผู้ติดต่อ_ยอดเท่าหัวเอกสาร()
    {
        var p = Assert.Single(WhtPayeeSplit.Portions(Broker, null, new[] { L(30m), L(15m), L(0m) }, 45m));
        Assert.Equal(Broker, p.PayeeContactId);
        Assert.Equal(45m, p.WithholdingTaxAmount);
        Assert.False(WhtPayeeSplit.IsMultiPayee(new[] { p }));
    }

    [Fact]
    public void ผู้ถูกหักหัวเอกสาร_ทุกบรรทัดตาม_ส่วนเดียวของผู้ถูกหักนั้น()
    {
        var p = Assert.Single(WhtPayeeSplit.Portions(Broker, InsurerA, new[] { L(30m), L(15m) }, 45m));
        Assert.Equal(InsurerA, p.PayeeContactId);
    }

    [Fact]
    public void บรรทัดตั้งผู้ถูกหักซ้ำกับหัวเอกสาร_ยังเป็นส่วนเดียว()
        => Assert.Single(WhtPayeeSplit.Portions(Broker, InsurerA, new[] { L(30m, InsurerA), L(15m) }, 45m));

    [Fact]
    public void หักระดับหัวเอกสาร_ไม่มีบรรทัดหัก_ส่วนเดียวเต็มยอด_ไม่มีหัก_ว่าง()
    {
        var p = Assert.Single(WhtPayeeSplit.Portions(Broker, null, new[] { L(0m, InsurerA) }, 30m));
        Assert.Equal(Broker, p.PayeeContactId);
        Assert.Equal(30m, p.WithholdingTaxAmount);
        Assert.Empty(WhtPayeeSplit.Portions(Broker, null, Array.Empty<WhtPayeeLine>(), 0m));
    }

    [Fact]
    public void ใบรายเดียว_เฉลี่ยยอดคืนยอดที่ขอทั้งก้อน()
    {
        var portions = WhtPayeeSplit.Portions(Broker, null, new[] { L(30m) }, 30m);
        var (payee, amount) = Assert.Single(WhtPayeeSplit.Allocate(portions, 12.34m));
        Assert.Equal(Broker, payee);
        Assert.Equal(12.34m, amount);
    }

    // ── ทิศใหม่: ใบหลายผู้ถูกหัก ──

    [Fact]
    public void สองบริษัทประกันในใบเดียว_สองส่วน_เรียงผู้ถูกหักหัวเอกสารก่อน()
    {
        var portions = WhtPayeeSplit.Portions(Broker, null,
            new[] { L(10m, InsurerA), L(5m), L(7m, InsurerB), L(3m, InsurerA) }, 25m);
        Assert.True(WhtPayeeSplit.IsMultiPayee(portions));
        Assert.Equal(new[] { Broker, InsurerA, InsurerB }, portions.Select(p => p.PayeeContactId));
        Assert.Equal(new[] { 5m, 13m, 7m }, portions.Select(p => p.WithholdingTaxAmount));
        Assert.Equal(new[] { 0, 3 }, portions[1].LineIndexes);
    }

    [Fact]
    public void เศษหัวเอกสารไปที่ส่วนของผู้ถูกหักหัวเอกสาร_ผลรวมเท่ายอดหัวเอกสาร()
    {
        var portions = WhtPayeeSplit.Portions(Broker, null, new[] { L(10m, InsurerA), L(5m) }, 15.01m);
        Assert.Equal(5.01m, portions.Single(p => p.PayeeContactId == Broker).WithholdingTaxAmount);
        Assert.Equal(15.01m, portions.Sum(p => p.WithholdingTaxAmount));
    }

    [Fact]
    public void เฉลี่ยยอดงวดจ่าย_ผลรวมเท่ายอดเป๊ะ_และยอดเต็มคืนยอดของแต่ละราย()
    {
        var portions = WhtPayeeSplit.Portions(Broker, null, new[] { L(10m, InsurerA), L(20m, InsurerB) }, 30m);
        var part = WhtPayeeSplit.Allocate(portions, 10.01m);
        Assert.Equal(10.01m, part.Sum(x => x.Amount));
        Assert.Equal(3.34m, part[0].Amount);   // 10.01 × 10/30 = 3.3367 → 3.34
        Assert.Equal(6.67m, part[1].Amount);   // ส่วนสุดท้ายรับเศษ
        var full = WhtPayeeSplit.Allocate(portions, 30m);
        Assert.Equal(new[] { 10m, 20m }, full.Select(x => x.Amount));
    }

    // ── เก็บค่า (normalize) / แก้ไข / สืบทอด ──

    [Fact]
    public void Normalize_ค่าที่ไม่มีความหมายไม่เก็บ_ค่าที่ต่างจากหัวเอกสารเก็บ()
    {
        Assert.Null(WhtPayeeSplit.NormalizeLineOverride(Broker, null, InsurerA, payerSide: false, 3m));   // ใบขาย
        Assert.Null(WhtPayeeSplit.NormalizeLineOverride(Broker, null, InsurerA, true, 0m));                // บรรทัดไม่หัก
        Assert.Null(WhtPayeeSplit.NormalizeLineOverride(Broker, null, Guid.Empty, true, 3m));
        Assert.Null(WhtPayeeSplit.NormalizeLineOverride(Broker, null, Broker, true, 3m));                  // = ผู้ถูกหักหัวเอกสาร
        Assert.Null(WhtPayeeSplit.NormalizeLineOverride(Broker, InsurerA, InsurerA, true, 3m));
        Assert.Equal(InsurerA, WhtPayeeSplit.NormalizeLineOverride(Broker, null, InsurerA, true, 3m));
        // หัวเอกสารตั้งบริษัทประกัน A แต่บรรทัดนี้เป็นของโบรกเกอร์เอง (ค่านายหน้า) — เก็บ
        Assert.Equal(Broker, WhtPayeeSplit.NormalizeLineOverride(Broker, InsurerA, Broker, true, 3m));
    }

    [Fact]
    public void แก้ไข_ไม่ส่งคงค่าเดิมของบรรทัดเดียวกัน_GuidEmptyล้าง_ส่งค่าใหม่ชนะ()
    {
        var prev = new List<(string?, Guid?)> { ("เบี้ย A", InsurerA), ("เบี้ย B", InsurerB) };
        Assert.Equal(InsurerA, WhtPayeeSplit.RequestedOrKept(null, prev, 0, "เบี้ย A"));
        Assert.Null(WhtPayeeSplit.RequestedOrKept(null, prev, 0, "รายการอื่น"));     // คำอธิบายเปลี่ยน = ไม่เดา
        Assert.Null(WhtPayeeSplit.RequestedOrKept(null, prev, 5, "เบี้ย A"));
        Assert.Null(WhtPayeeSplit.RequestedOrKept(Guid.Empty, prev, 1, "เบี้ย B"));
        Assert.Equal(InsurerA, WhtPayeeSplit.RequestedOrKept(InsurerA, prev, 1, "เบี้ย B"));
    }

    [Fact]
    public void เอกสารลูก_ฝั่งผู้หักสืบทอด_ฝั่งอื่นเป็นnull_nullเป็นnull()
    {
        var active = new HashSet<Guid> { InsurerA, InsurerB };
        Assert.Equal(InsurerA, WhtPayeeSplit.InheritPayee(InsurerA, targetIsPayerSide: true, active));
        Assert.Null(WhtPayeeSplit.InheritPayee(InsurerA, targetIsPayerSide: false, active));
        Assert.Null(WhtPayeeSplit.InheritPayee(null, targetIsPayerSide: true, active));
        Assert.Equal(0, WhtPayeeSplit.DroppedInheritedWhtPayees(InsurerA, true,
            new (Guid?, bool)[] { (null, true), (InsurerB, true) }, true, active));
        Assert.Null(WhtPayeeSplit.DroppedInheritedPayeeNote("PI-001", 0));   // ไม่มีอะไรถูกตัด = ไม่มีป้าย (ใบปกติไม่ถูกแตะ)
    }

    // ── ฝ่ายค้าน P3-6: ผู้ติดต่อของผู้ถูกหักถูกลบ ⇒ สืบทอดเป็น null + ป้าย (ไม่ล้มทุกครั้ง) ──

    [Fact]
    public void เอกสารลูก_ผู้ถูกหักถูกลบ_สืบทอดเป็นnull_นับจำนวน_มีป้าย()
    {
        var active = new HashSet<Guid> { InsurerA };   // InsurerB ถูกลบ
        Assert.Null(WhtPayeeSplit.InheritPayee(InsurerB, targetIsPayerSide: true, active));
        Assert.Equal(InsurerA, WhtPayeeSplit.InheritPayee(InsurerA, targetIsPayerSide: true, active));
        // หัวเอกสาร B (ถูกลบ · มีบรรทัดหักตามหัว) + บรรทัดหัก A (อยู่) + บรรทัดหัก B (ถูกลบ) + บรรทัดหักตามหัว = 2
        Assert.Equal(2, WhtPayeeSplit.DroppedInheritedWhtPayees(InsurerB, true,
            new (Guid?, bool)[] { (InsurerA, true), (InsurerB, true), (null, true) }, true, active));
        Assert.Equal(0, WhtPayeeSplit.DroppedInheritedWhtPayees(InsurerB, false,
            new (Guid?, bool)[] { (InsurerB, true) }, false, active));   // ฝั่งอื่นไม่สืบทอดอยู่แล้ว
        var note = WhtPayeeSplit.DroppedInheritedPayeeNote("PI-001", 2);
        Assert.NotNull(note);
        Assert.Contains("PI-001", note);
        Assert.StartsWith(WhtPayeeSplit.DroppedPayeeTag, note);
    }

    // ── ฝ่ายค้าน P1: ทุกบรรทัดที่หักเป็นของผู้ถูกหักรายบรรทัดรายเดียว Y ≠ หัวเอกสาร X ──

    [Fact]
    public void บรรทัดเดียวตั้งผู้ถูกหักรายบรรทัด_ผู้ถูกหักที่มีผลคือรายนั้น_ตรงกับส่วนของตัวแบ่ง()
    {
        var lines = new[] { L(30m, InsurerA) };
        var portion = Assert.Single(WhtPayeeSplit.Portions(Broker, null, lines, 30m));
        Assert.Equal(InsurerA, portion.PayeeContactId);
        // ตัวอ่านเดียวของ 50 ทวิ/พรีวิว/หน้านำส่ง ต้องได้คนเดียวกับส่วนที่ JE ใช้ (เดิมได้หัวเอกสาร = นายหน้า)
        Assert.Equal(InsurerA, WhtPayeeSplit.EffectiveSinglePayee(Broker, null, lines));
        Assert.Equal(InsurerA, WhtPayeeSplit.LiftSinglePayee(Broker, null, lines).DocPayeeOverride);
        // บรรทัดไม่หัก (อัตรา 0) ที่ไม่มีผู้ถูกหักรายบรรทัดไม่ทำให้กลายเป็นใบหลายราย
        Assert.Equal(InsurerB, WhtPayeeSplit.EffectiveSinglePayee(Broker, InsurerA, new[] { L(10m, InsurerB), L(0m), L(5m, InsurerB) }));
    }

    [Fact]
    public void ใบรายเดียวปกติ_ผู้ถูกหักที่มีผล_เท่าหัวเอกสาร_ไม่ถูกแตะ()
    {
        Assert.Equal(Broker, WhtPayeeSplit.EffectiveSinglePayee(Broker, null, new[] { L(30m), L(15m) }));
        Assert.Equal(InsurerA, WhtPayeeSplit.EffectiveSinglePayee(Broker, InsurerA, new[] { L(30m), L(15m, InsurerA) }));
        Assert.Equal(Broker, WhtPayeeSplit.EffectiveSinglePayee(Broker, null, Array.Empty<WhtPayeeLine>()));   // หักระดับหัวเอกสาร
        // ใบหลายผู้ถูกหัก = หัวเอกสาร (ผู้เรียกแยกต่อรายเอง)
        Assert.Equal(Broker, WhtPayeeSplit.EffectiveSinglePayee(Broker, null, new[] { L(10m, InsurerA), L(5m) }));
        Assert.Null(WhtPayeeSplit.LiftSinglePayee(Broker, Broker, new[] { L(10m) }).DocPayeeOverride);   // override = ผู้ติดต่อ ⇒ normalize เป็น null
    }

    [Fact]
    public void ยกผู้ถูกหักรายบรรทัดขึ้นหัวเอกสาร_ทุกบรรทัดหักของY_หัวเป็นY_บรรทัดว่าง_ส่วนเท่าเดิม()
    {
        var lines = new[] { L(10m, InsurerA), L(0m), L(20m, InsurerA) };
        var (head, perLine) = WhtPayeeSplit.LiftSinglePayee(Broker, null, lines);
        Assert.Equal(InsurerA, head);
        Assert.All(perLine, x => Assert.False(x.HasValue));
        // JE เท่าเดิมทุกประการ: ส่วนของตัวแบ่งก่อน/หลังยก = ผู้ถูกหักเดียวกัน ยอดเดียวกัน
        var before = Assert.Single(WhtPayeeSplit.Portions(Broker, null, lines, 30m));
        var after = Assert.Single(WhtPayeeSplit.Portions(Broker, head,
            perLine.Select((p, i) => new WhtPayeeLine(p, lines[i].WithholdingTaxAmount)).ToList(), 30m));
        Assert.Equal((before.PayeeContactId, before.WithholdingTaxAmount), (after.PayeeContactId, after.WithholdingTaxAmount));
        // ยกกลับเป็นผู้ติดต่อของเอกสาร ⇒ หัวเอกสาร null (ไม่เก็บสำเนาที่ไม่มีความหมาย)
        var (backHead, backLines) = WhtPayeeSplit.LiftSinglePayee(Broker, InsurerA, new[] { L(10m, Broker) });
        Assert.Null(backHead);
        Assert.Null(Assert.Single(backLines));
    }

    [Fact]
    public void ยกผู้ถูกหัก_ทิศตรงข้าม_ใบหลายรายและใบรายเดียวปกติ_ไม่ถูกแตะ()
    {
        var multi = new[] { L(10m, InsurerA), L(5m), L(7m, InsurerB) };
        var (mHead, mLines) = WhtPayeeSplit.LiftSinglePayee(Broker, null, multi);
        Assert.Null(mHead);
        Assert.Equal(new Guid?[] { InsurerA, null, InsurerB }, mLines);
        var (sHead, sLines) = WhtPayeeSplit.LiftSinglePayee(Broker, InsurerA, new[] { L(10m), L(5m) });
        Assert.Equal(InsurerA, sHead);
        Assert.All(sLines, x => Assert.False(x.HasValue));
        var (hHead, hLines) = WhtPayeeSplit.LiftSinglePayee(Broker, null, new[] { L(0m, InsurerA) });   // ไม่มีบรรทัดหัก (หักระดับหัว)
        Assert.Null(hHead);
        Assert.Equal(InsurerA, Assert.Single(hLines));
    }

    // ── ฝ่ายค้าน P3-5: เฉลี่ยแบบเศษเหลือมากสุด — ไม่มีส่วนติดลบ ──

    [Fact]
    public void เฉลี่ย_สี่ส่วนเท่ากัน_ยอด002_ทุกส่วนไม่ติดลบ_ผลรวมเป๊ะ()
    {
        var Z = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var portions = WhtPayeeSplit.Portions(Broker, null, new[] { L(10m), L(10m, InsurerA), L(10m, InsurerB), L(10m, Z) }, 40m);
        Assert.Equal(4, portions.Count);
        var parts = WhtPayeeSplit.Allocate(portions, 0.02m);
        Assert.All(parts, p => Assert.True(p.Amount >= 0m, $"ส่วนติดลบ {p.Amount}"));
        Assert.Equal(0.02m, parts.Sum(p => p.Amount));
        Assert.Equal(new[] { 0.01m, 0.01m, 0m, 0m }, parts.Select(p => p.Amount));   // เศษเท่ากัน ⇒ ลำดับบนใบ (หัวเอกสารก่อน)
        // ยอดติดลบ (กลับรายการ) — ทุกส่วนติดลบหรือศูนย์ ผลรวมเป๊ะ
        var neg = WhtPayeeSplit.Allocate(portions, -0.03m);
        Assert.All(neg, p => Assert.True(p.Amount <= 0m));
        Assert.Equal(-0.03m, neg.Sum(p => p.Amount));
    }

    [Fact]
    public void เฉลี่ย_สัดส่วนไม่ลงตัว_ผลรวมเป๊ะ_ใกล้สัดส่วนจริง()
    {
        var portions = WhtPayeeSplit.Portions(Broker, null, new[] { L(1m), L(1m, InsurerA), L(1m, InsurerB) }, 3m);
        var parts = WhtPayeeSplit.Allocate(portions, 100m);
        Assert.Equal(100m, parts.Sum(p => p.Amount));
        Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, parts.Select(p => p.Amount));
        Assert.All(parts, p => Assert.True(p.Amount >= 0m));
    }

    // ── ฝ่ายค้าน P2c: ใบลด/เพิ่มหนี้ของใบต้นทางหลายผู้ถูกหัก ต้องอ้างบรรทัดต้นทาง ──

    [Fact]
    public void ใบลดหนี้ของใบหลายผู้ถูกหัก_บรรทัดหักไม่อ้างบรรทัดต้นทาง_ปฏิเสธพร้อมทางไปต่อ()
    {
        var reason = WhtPayeeSplit.UnmappedAdjustmentLineReason("CN-001", "PV-001", sourceIsMultiPayee: true,
            new (string?, decimal, bool)[] { ("ลดเบี้ย A", 3m, true), ("ลดค่าบริการ", 1.5m, false) }, sourceConvertsToNote: true);
        Assert.NotNull(reason);
        Assert.Contains("ลดค่าบริการ", reason);
        Assert.Contains("แปลง", reason);   // ทางไปต่อ
        Assert.DoesNotContain("ลดเบี้ย A", reason);
    }

    [Fact]
    public void ใบลดหนี้_ทิศตรงข้าม_ใบต้นทางรายเดียว_หรืออ้างครบ_หรือบรรทัดไม่หัก_ผ่าน()
    {
        var unmapped = new (string?, decimal, bool)[] { ("ลดค่าบริการ", 1.5m, false) };
        Assert.Null(WhtPayeeSplit.UnmappedAdjustmentLineReason("CN-001", "PV-001", sourceIsMultiPayee: false, unmapped, true));   // ใบรายเดียว = เส้นเดิม
        Assert.Null(WhtPayeeSplit.UnmappedAdjustmentLineReason("CN-001", "PV-001", true,
            new (string?, decimal, bool)[] { ("ลดเบี้ย A", 3m, true), ("ค่าขนส่ง", 0m, false) }, true));
    }

    [Fact]
    public void ลิสต์รอออกใบ_ใบรายเดียวมีใบใดก็ครบ_ใบหลายรายตรวจต่อผู้ถูกหัก()
    {
        var single = WhtPayeeSplit.Portions(Broker, null, new[] { L(30m) }, 30m);
        Assert.Empty(WhtPayeeSplit.UncoveredPayees(single, new[] { InsurerA }));   // ใบเก่าออกด้วยมือในนามอื่น = ไม่นับว่าขาด (พฤติกรรมเดิม)
        Assert.Equal(new[] { Broker }, WhtPayeeSplit.UncoveredPayees(single, Array.Empty<Guid>()));
        var multi = WhtPayeeSplit.Portions(Broker, null, new[] { L(10m, InsurerA), L(20m, InsurerB) }, 30m);
        Assert.Equal(new[] { InsurerB }, WhtPayeeSplit.UncoveredPayees(multi, new[] { InsurerA }));
        Assert.Empty(WhtPayeeSplit.UncoveredPayees(multi, new[] { InsurerA, InsurerB }));
    }

    [Fact]
    public void ผู้ถูกหักของบรรทัด_ค่าบนบรรทัดชนะ_ว่างตามหัวเอกสาร()
    {
        Assert.Equal(InsurerB, WhtPayeeSplit.LinePayeeOf(Broker, InsurerA, InsurerB));
        Assert.Equal(InsurerA, WhtPayeeSplit.LinePayeeOf(Broker, InsurerA, null));
        Assert.Equal(Broker, WhtPayeeSplit.LinePayeeOf(Broker, null, Guid.Empty));
    }

    [Fact]
    public void ชนิดเอกสารฝั่งผู้หัก_ชุดเดียวกับตัวตัดสิน50ทวิ()
    {
        Assert.True(WhtCertIssuePlan.AppliesTo(DocumentType.PaymentVoucher));
        Assert.False(WhtCertIssuePlan.AppliesTo(DocumentType.CreditNote));   // ใบลดหนี้ตั้งผู้ถูกหักเองไม่ได้ — อ่านจากใบต้นทาง
    }

    // ══════ ฝ่ายค้านรอบสอง (หลัง 0f428969) ══════

    [Fact]
    public void ผู้ถูกหักสืบทอดถูกตัด_นับเฉพาะบรรทัดที่หักภาษี_หัวเอกสารนับเมื่อมีบรรทัดหักตามหัว()
    {
        var active = new HashSet<Guid> { InsurerA };   // InsurerB ถูกลบ
        // บรรทัดที่ไม่หักภาษีชี้ผู้ถูกหักที่ถูกลบ + หัวเอกสารถูกลบแต่ไม่มีบรรทัดหักใดตามหัว ⇒ ไม่มีผลกับ 50 ทวิ = 0 (ไม่ติดป้ายเกินจริง)
        Assert.Equal(0, WhtPayeeSplit.DroppedInheritedWhtPayees(InsurerB, true,
            new (Guid?, bool)[] { (InsurerA, true), (InsurerB, false) }, true, active));
        // ทิศตรงข้าม: บรรทัดหักตามหัวเอกสาร ⇒ หัวเอกสารที่ถูกลบนับ · บรรทัดหักที่ชี้ผู้ถูกลบนับ
        Assert.Equal(1, WhtPayeeSplit.DroppedInheritedWhtPayees(InsurerB, true,
            new (Guid?, bool)[] { (InsurerA, true), (null, true) }, true, active));
        Assert.Equal(1, WhtPayeeSplit.DroppedInheritedWhtPayees(null, true,
            new (Guid?, bool)[] { (InsurerB, true), (InsurerB, false) }, true, active));
    }

    [Fact]
    public void ผู้ถูกหักสืบทอดถูกตัด_ติดป้ายต้องตรวจ_Decideและคำเตือนอนุมัติอ่านได้_รับทราบแล้วหาย()
    {
        const string before = "หมายเหตุเดิม";
        Assert.Equal(before, WhtPayeeSplit.MarkDroppedInheritedPayees(before, "PI-001", 0));   // ไม่มีอะไรถูกตัด = ไม่แตะ
        Assert.Null(WhtPayeeSplit.PendingDroppedPayeeReview(before));
        Assert.Null(WhtPayeeSplit.DroppedPayeeApprovalWarning(before));

        var marked = WhtPayeeSplit.MarkDroppedInheritedPayees(before, "PI-001", 1);
        Assert.True(WhtCertReviewNote.Has(marked));                                            // ลิสต์รอออกใบโชว์ "ต้องตรวจ"
        Assert.StartsWith(before, marked);
        Assert.Contains("PI-001", WhtPayeeSplit.PendingDroppedPayeeReview(marked));
        var warn = WhtPayeeSplit.DroppedPayeeApprovalWarning(marked);
        Assert.NotNull(warn);
        Assert.True(WhtCertIssuePlan.IsApprovalWarning(warn!));                                // ชุด [WHT-PAYEE] — เว็บถาม · ไม่มีคนผ่าน
        Assert.Equal(marked, WhtPayeeSplit.MarkDroppedInheritedPayees(marked, "PI-001", 1));  // ซ้ำเรื่องเดิมไม่เพิ่ม
        Assert.Null(WhtPayeeSplit.PendingDroppedPayeeReview(WhtCertReviewNote.Clear(marked))); // รับทราบ (ล้างป้าย) ⇒ หาย
    }

    [Fact]
    public void ป้ายต้องตรวจเรื่องอื่น_ไม่ใช่ผู้ถูกหักถูกตัด_ไม่ถูกนับ()
    {
        var other = WhtCertReviewNote.Append(null, "ใบลดหนี้ CN-001 ลดฐานหลังยื่นแล้ว — ยื่นแบบเพิ่มเติม");
        Assert.True(WhtCertReviewNote.Has(other));
        Assert.Null(WhtPayeeSplit.PendingDroppedPayeeReview(other));
        Assert.Null(WhtPayeeSplit.DroppedPayeeApprovalWarning(other));
    }

    [Fact]
    public void หัวเอกสารที่ระบุเอง_ขัดกับผู้ถูกหักรายบรรทัดรายเดียว_ปฏิเสธ()
    {
        var reason = WhtPayeeSplit.ExplicitHeaderConflictReason(InsurerB, new[] { L(30m, InsurerA), L(15m, InsurerA), L(0m) });
        Assert.NotNull(reason);
        Assert.Contains("ทางไปต่อ", reason);
    }

    [Fact]
    public void หัวเอกสารขัดกับรายบรรทัด_ทิศตรงข้าม_ไม่ระบุหัว_มีบรรทัดตามหัว_หลายราย_ไม่มีบรรทัดหัก_ผ่าน()
    {
        var allA = new[] { L(30m, InsurerA), L(15m, InsurerA) };
        Assert.Null(WhtPayeeSplit.ExplicitHeaderConflictReason(null, allA));          // ไม่ระบุหัว (แปลง/คัดลอก/ฟอร์มไม่ติ๊ก) = ยกตามเดิม
        Assert.Null(WhtPayeeSplit.ExplicitHeaderConflictReason(Guid.Empty, allA));    // ล้างหัว = ยกตามเดิม
        Assert.Null(WhtPayeeSplit.ExplicitHeaderConflictReason(InsurerB, new[] { L(30m, InsurerA), L(15m) }));          // บรรทัดตามหัว
        Assert.Null(WhtPayeeSplit.ExplicitHeaderConflictReason(InsurerB, new[] { L(30m, InsurerA), L(15m, Broker) }));  // หลายราย
        Assert.Null(WhtPayeeSplit.ExplicitHeaderConflictReason(InsurerB, new[] { L(0m, InsurerA) }));                   // ไม่มีบรรทัดหัก
    }

    [Fact]
    public void ใบลดหนี้ไม่อ้างบรรทัด_ทางไปต่อทำได้จริง_ไม่แนะแยกใบ_ไม่มีเส้นแปลงบอกทางอื่น()
    {
        var unmapped = new (string?, decimal, bool)[] { ("ลดค่าบริการ", 1.5m, false) };
        var withRoute = WhtPayeeSplit.UnmappedAdjustmentLineReason("CN-001", "PV-001", true, unmapped, sourceConvertsToNote: true);
        Assert.Contains("แปลง", withRoute);
        Assert.DoesNotContain("แยกใบลดหนี้", withRoute);   // เดิมแนะทางที่ด่านเดิมฟ้องซ้ำ
        var noRoute = WhtPayeeSplit.UnmappedAdjustmentLineReason("CN-002", "CIL-001", true, unmapped, sourceConvertsToNote: false);
        Assert.Contains("แปลงเป็นใบลด/เพิ่มหนี้ไม่ได้", noRoute);
        Assert.Contains("ยกเลิกใบ CIL-001", noRoute);
        Assert.DoesNotContain("แยกใบลดหนี้", noRoute);
    }

    // ── ฝ่ายค้านรอบ 204 (หลัง 7b88c053) ──

    [Fact]
    public void ป้ายผู้ถูกหักถูกตัดทิ้งของต้นทาง_ตามไปใบลูกฝั่งผู้จ่าย_ไม่ซ้ำ()
    {
        var src = WhtPayeeSplit.MarkDroppedInheritedPayees(null, "PV-001", 1);
        var child = WhtPayeeSplit.InheritPendingDroppedReview("หมายเหตุลูก", src, childIsPayerSide: true);
        Assert.NotNull(WhtPayeeSplit.PendingDroppedPayeeReview(child));
        Assert.Contains("หมายเหตุลูก", child);
        // ลูกมีป้ายอยู่แล้ว ⇒ ไม่ต่อซ้ำ
        Assert.Equal(child, WhtPayeeSplit.InheritPendingDroppedReview(child, src, childIsPayerSide: true));
    }

    [Fact]
    public void ทิศตรงข้าม_ต้นทางไม่มีป้าย_หรือลูกไม่ใช่ฝั่งผู้จ่าย_ไม่แตะหมายเหตุ()
    {
        var src = WhtPayeeSplit.MarkDroppedInheritedPayees(null, "PV-001", 1);
        Assert.Equal("ลูก", WhtPayeeSplit.InheritPendingDroppedReview("ลูก", null, childIsPayerSide: true));
        Assert.Equal("ลูก", WhtPayeeSplit.InheritPendingDroppedReview("ลูก", "หมายเหตุอื่น", childIsPayerSide: true));
        Assert.Equal("ลูก", WhtPayeeSplit.InheritPendingDroppedReview("ลูก", src, childIsPayerSide: false));
        Assert.Null(WhtPayeeSplit.InheritPendingDroppedReview(null, null, childIsPayerSide: true));
    }

    [Fact]
    public void ฟอร์มส่งหัวเอกสารค่าเดิมกลับ_ไม่นับว่าระบุใหม่()
    {
        // ค่าเดิม (หลัง normalize) ⇒ null — ไม่เข้าด่านขัดกับบรรทัด (ยกผู้ถูกหักรายบรรทัดขึ้นหัวตามปกติ)
        Assert.Null(WhtPayeeSplit.ChangedHeaderRequest(Broker, InsurerA, InsurerA));
        Assert.Null(WhtPayeeSplit.ChangedHeaderRequest(Broker, Broker, null));          // เท่าผู้ติดต่อ = ค่าเดิม (ไม่ตั้ง)
        Assert.Null(WhtPayeeSplit.ChangedHeaderRequest(Broker, Guid.Empty, InsurerA));  // ล้างค่า ไม่ใช่ระบุผู้ถูกหัก
        Assert.Null(WhtPayeeSplit.ChangedHeaderRequest(Broker, null, InsurerA));
        // ทิศตรงข้าม: เปลี่ยนจริง ⇒ คืนค่าที่ขอ (ด่านยังทำงาน)
        Assert.Equal(InsurerB, WhtPayeeSplit.ChangedHeaderRequest(Broker, InsurerB, InsurerA));
        Assert.Equal(InsurerA, WhtPayeeSplit.ChangedHeaderRequest(Broker, InsurerA, null));
    }
}
