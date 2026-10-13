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
        Assert.Equal(InsurerA, WhtPayeeSplit.InheritLinePayee(InsurerA, targetIsPayerSide: true));
        Assert.Null(WhtPayeeSplit.InheritLinePayee(InsurerA, targetIsPayerSide: false));
        Assert.Null(WhtPayeeSplit.InheritLinePayee(null, targetIsPayerSide: true));
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
}
