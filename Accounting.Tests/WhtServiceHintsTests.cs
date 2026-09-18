using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"มีเหตุให้สงสัยว่าเป็นค่าจ้าง/ค่าบริการไหม"** — ตัวที่นิยามคำว่า "เหตุ"
/// หลังเจ้าของพลิกค่าตั้งต้นเป็น **"ไม่รู้ ⇒ เงียบ"** (รอบ 179)
///
/// ล็อกสองครึ่งตามกฎเหล็ก #4 H:
/// · ครึ่งที่ **ต้องสงสัย** — ไม่งั้นด่านหายทั้งด่าน (เงียบทุกใบ = ไม่มีด่าน)
/// · ครึ่งที่ **ต้องไม่สงสัย** — ไม่งั้นกลับไปเตือนทุกใบเหมือนก่อน ซึ่งเป็นบั๊ก
///   ที่ผู้ใช้รายงานมาตั้งแต่ต้น
/// </summary>
public class WhtServiceHintsTests
{
    private static WhtLineFact Line(string desc, decimal amount, ProductType? kind = null)
        => new(desc, null, kind, amount);

    // ══════════ ครึ่งแรก: ต้องสงสัย ══════════

    [Theory]
    [InlineData("ค่าจ้างเหมาติดตั้งระบบไฟฟ้า")]
    [InlineData("ค่าบริการรายเดือน")]
    [InlineData("ค่าที่ปรึกษากฎหมาย")]
    [InlineData("ค่าเช่าโกดัง")]
    [InlineData("ค่าโฆษณาออนไลน์")]
    [InlineData("ค่าสอบบัญชีประจำปี")]
    [InlineData("Service Fee - monthly")]
    [InlineData("Maintenance contract")]
    public void ใบที่มีแต่บรรทัดค่าบริการ_ต้องสงสัย(string desc)
    {
        var r = WhtServiceHints.Scan(new[] { Line(desc, 10_000m) });

        Assert.True(r.Suspicious);
        Assert.NotEqual("", r.MatchedKeyword);
    }

    [Fact]
    public void ใบผสมที่ค่าบริการเป็นเนื้อหลัก_ต้องสงสัย()
    {
        var r = WhtServiceHints.Scan(new[]
        {
            Line("อะไหล่", 2_000m, ProductType.Product),
            Line("ค่าแรงติดตั้ง", 8_000m),
        });

        Assert.True(r.Suspicious);
        Assert.Equal(0.80m, decimal.Round(r.AmountShare, 2));
    }

    [Fact]
    public void ต้องบอกคำที่ทำให้สงสัยได้_เพื่อให้คำเตือนมีเหตุผล()
        // คำเตือนที่บอกไม่ได้ว่า "สงสัยเพราะอะไร" = คำเตือนที่ผู้ใช้เถียงไม่ได้
        => Assert.Equal("ค่าเช่า",
            WhtServiceHints.Scan(new[] { Line("ค่าเช่าเครื่องถ่ายเอกสาร", 5_000m) }).MatchedKeyword);

    // ══════════ ครึ่งหลัง: ต้องไม่สงสัย ══════════

    [Fact]
    public void ค่าส่งเล็กน้อยบนใบซื้อของ_ต้องไม่สงสัย()
    {
        // เคสที่กับดักอยู่: "ค่าขนส่ง" บนใบซื้อของ = ค่าส่งที่ผู้ขายเรียกเก็บ
        // ไม่ใช่สัญญาจ้างขนส่งที่ต้องหัก 1%
        var r = WhtServiceHints.Scan(new[]
        {
            Line("แผ่นปะเต็นท์", 3_000m),
            Line("ผ้าปูพื้น", 2_000m),
            Line("ค่าขนส่ง", 50m),
        });

        Assert.False(r.Suspicious);
        Assert.True(r.AmountShare < WhtServiceHints.MinAmountShare);
    }

    [Fact]
    public void บรรทัดที่ผูกสินค้าใน_master_ไม่นับเป็นเหตุ_แม้คำอธิบายจะเข้าข่าย()
        // การผูกรหัสสินค้าเป็น "การประกาศของมนุษย์" ซึ่งแข็งกว่าคำในข้อความ (G1)
        => Assert.False(WhtServiceHints.Scan(new[]
        {
            Line("ชุดค่าบริการเสริม (สินค้า)", 10_000m, ProductType.Product),
        }).Suspicious);

    [Fact]
    public void ใบซื้อของล้วน_ต้องไม่สงสัย()
        => Assert.False(WhtServiceHints.Scan(new[]
        {
            Line("กระดาษ A4", 1_200m),
            Line("หมึกพิมพ์", 3_400m),
        }).Suspicious);

    [Fact]
    public void คำกำกวมที่พบบนใบซื้อของทั่วไป_ต้องไม่ถูกนับเป็นเหตุ()
        // "งาน" · "โครงการ" · "ทำ" ตั้งใจไม่อยู่ในชุดคำ — ถ้าใส่ จะเตือนแทบทุกใบ
        => Assert.False(WhtServiceHints.Scan(new[]
        {
            Line("อุปกรณ์สำหรับงานก่อสร้าง", 20_000m),
            Line("วัสดุโครงการ A", 30_000m),
        }).Suspicious);

    // ══════════ ไม่มีอะไรให้ตรวจ = ไม่ใช่เหตุ ══════════

    [Fact]
    public void ไม่มีบรรทัด_ต้องไม่สงสัย()
    {
        Assert.False(WhtServiceHints.Scan(null).Suspicious);
        Assert.False(WhtServiceHints.Scan(Array.Empty<WhtLineFact>()).Suspicious);
    }

    [Fact]
    public void แถวยอดศูนย์ไม่ใช่หลักฐาน()
        // แถวฟอร์มที่พิมพ์มาทุกใบ ("ค่าบริการ 0.00") ห้ามทำให้ทั้งใบกลายเป็นเหตุ
        => Assert.False(WhtServiceHints.Scan(new[]
        {
            Line("สินค้า", 5_000m),
            Line("ค่าบริการเสริม", 0m),
        }).Suspicious);

    [Fact]
    public void เกณฑ์สัดส่วนต้องเป็นค่าคงที่ที่เทสต์อ้างได้()
        => Assert.InRange(WhtServiceHints.MinAmountShare, 0.01m, 0.50m);
}
