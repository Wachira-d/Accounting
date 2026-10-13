using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 — รวมงานสองทีม: ใบเดียวหลายผู้ถูกหัก (ข้อ 143 · <see cref="WhtPayeeSplit"/>) × แบบ ภ.ง.ด.2 ตามประเภทเงินได้ (ข้อ 148 · <see cref="WhtIncomeCodes"/>)
///
/// <para>ที่มา: ตัวแบ่งผังต่อผู้ถูกหัก (JE · พรีวิว GL · หน้านำส่ง/ปฏิทิน · รายงาน · 50 ทวิ ต่อราย) ต้องส่ง "รหัสเงินได้ของบรรทัดของรายนั้น" ·
/// ถ้าส่งรหัสทั้งใบ: ดอกเบี้ยของบุคคล ก + ค่าบริการของบริษัท ข ⇒ ชุดรหัสปน ⇒ ภ.ง.ด.3/21916 ผิดแบบ · ถ้าไม่ส่งเลย ⇒ 21916 เช่นกัน</para>
/// <para>ทิศตรงข้าม: ใบผู้ถูกหักรายเดียว ⇒ ชุดรหัสต่อราย = ชุดรหัสทั้งใบเป๊ะ (เส้นเดิมของทีม ภ.ง.ด.2 ไม่ขยับ)</para>
/// </summary>
public class WhtPayeeIncomeCodesTests
{
    private static readonly Guid PersonA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FirmB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private const string PersonId = "1103700123456";

    private static DocumentLine Line(int order, string? code, decimal wht, Guid? payee = null, bool deleted = false)
        => new() { LineOrder = order, IncomeTypeCode = code, WithholdingTaxAmount = wht, WhtPayeeContactId = payee, IsDeleted = deleted };

    private static IReadOnlyList<WhtPayeePortion> PortionsOf(Guid contact, IReadOnlyList<DocumentLine> lines, decimal total)
        => WhtPayeeSplit.Portions(contact, null, lines.Select(l => new WhtPayeeLine(l.WhtPayeeContactId, l.WithholdingTaxAmount)).ToList(), total);

    [Fact]
    public void ใบหลายผู้ถูกหัก_รหัสต่อราย_ดอกเบี้ยของบุคคลไม่ปนค่าบริการของบริษัท()
    {
        // ผู้ติดต่อ = นาย ก (ดอกเบี้ย 15%) · บรรทัดที่สองผู้ถูกหัก = บริษัท ข (ค่าบริการ 3%)
        var lines = new List<DocumentLine> { Line(1, "4a", 150m), Line(2, "8", 30m, FirmB) };
        var portions = PortionsOf(PersonA, lines, 180m);
        Assert.Equal(2, portions.Count);

        var aCodes = WhtIncomeCodes.WithheldAt(lines, portions.Single(p => p.PayeeContactId == PersonA).LineIndexes);
        var bCodes = WhtIncomeCodes.WithheldAt(lines, portions.Single(p => p.PayeeContactId == FirmB).LineIndexes);
        Assert.Equal(new string?[] { "4a" }, aCodes);
        Assert.Equal(new string?[] { "8" }, bCodes);

        // นาย ก ⇒ ภ.ง.ด.2 · ผัง 21915 (ตัวตัดสินเดียว) — ถ้าส่งรหัสทั้งใบจะปน ⇒ ภ.ง.ด.3 (บั๊กที่การรวมงานต้องกัน)
        Assert.Equal(TaxType.WithholdingTax2,
            WhtPayeeKind.ResolveForm(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", aCodes));
        Assert.Equal(WhtPayableAccount.Pnd2Code,
            WhtPayableAccount.CodeChain(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", aCodes)[0]);
        Assert.Equal(TaxType.WithholdingTax3,
            WhtPayeeKind.ResolveForm(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี", WhtIncomeCodes.Withheld(lines)));
    }

    [Fact]
    public void WithheldForPayee_ตรงกับ_WithheldAt_ของส่วนเดียวกัน()
    {
        var lines = new List<DocumentLine> { Line(1, "4a", 150m), Line(2, "8", 30m, FirmB), Line(3, "4b", 10m) };
        var portions = PortionsOf(PersonA, lines, 190m);
        foreach (var p in portions)
            Assert.Equal(WhtIncomeCodes.WithheldAt(lines, p.LineIndexes),
                WhtIncomeCodes.WithheldForPayee(lines, PersonA, null, p.PayeeContactId));
        Assert.Equal(new string?[] { "4a", "4b" }, WhtIncomeCodes.WithheldForPayee(lines, PersonA, null, PersonA));
    }

    [Fact]
    public void At_ใช้กับรหัสที่_project_ใน_SQL_ลำดับเดียวกับตัวแบ่ง()
    {
        var codes = new string?[] { "4a", "8", "4b" };
        Assert.Equal(new string?[] { "4a", "4b" }, WhtIncomeCodes.At(codes, new[] { 0, 2 }));
        Assert.Empty(WhtIncomeCodes.At(codes, Array.Empty<int>()));
        Assert.Empty(WhtIncomeCodes.At(codes, new[] { 7 }));   // index เกินช่วง ⇒ ไม่ throw ไม่แต่งรหัส
    }

    // ── ทิศตรงข้าม: ใบผู้ถูกหักรายเดียว ⇒ ชุดรหัสต่อราย = ทั้งใบ (เส้นเดิมของ ภ.ง.ด.2 ไม่ขยับ) ──

    [Fact]
    public void ใบรายเดียว_รหัสต่อราย_เท่ากับรหัสทั้งใบ()
    {
        var lines = new List<DocumentLine> { Line(1, "4a", 150m), Line(2, "8", 0m), Line(3, "4b", 20m), Line(4, "8", 9m, deleted: true) };
        var live = lines.Where(l => !l.IsDeleted).ToList();
        var p = Assert.Single(PortionsOf(PersonA, live, 170m));
        Assert.Equal(WhtIncomeCodes.Withheld(lines), WhtIncomeCodes.WithheldAt(live, p.LineIndexes));
        Assert.Equal(WhtIncomeCodes.Withheld(lines), WhtIncomeCodes.WithheldForPayee(lines, PersonA, null, PersonA));
    }

    [Fact]
    public void ใบรายเดียวที่ปน_ยังเป็น_ภงด3_เหมือนเดิม()
    {
        var lines = new List<DocumentLine> { Line(1, "4a", 150m), Line(2, "8", 30m) };
        var p = Assert.Single(PortionsOf(PersonA, lines, 180m));
        Assert.Equal(TaxType.WithholdingTax3, WhtPayeeKind.ResolveForm(false, null, PersonId, ContactType.Individual, "นายสมชาย ใจดี",
            WhtIncomeCodes.WithheldAt(lines, p.LineIndexes)));
    }

    [Fact]
    public void ผู้ถูกหักที่ไม่มีบรรทัด_ได้ชุดรหัสว่าง()
        => Assert.Empty(WhtIncomeCodes.WithheldForPayee(new[] { Line(1, "4a", 150m) }, PersonA, null, FirmB));
}
