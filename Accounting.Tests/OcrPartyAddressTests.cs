using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// เศษท้ายชื่อคู่ค้าที่หลุดมาเกาะหัวที่อยู่ (Azure ตัดกรอบผิด)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-11 · ใบ HS6909180 “ลักกี้ เวย์”) ═══
/// กระดาษพิมพ์ “หจก. แอม แฮปปี้เนส” บรรทัดหนึ่ง แล้ว “202/24 ม.5 ต.บางพระ …”
/// บรรทัดถัดไป — Azure คืนชื่อขาดท้าย (“แอม แฮปปี้”) และที่อยู่ = “เนส202/24 …”
/// ⇒ ช่อง “ที่อยู่ผู้ซื้อ” บนหน้า review ขึ้น “เนส202/24 ม.5 …”
///
/// เทสต์ล็อก **สองทิศ**: ใบที่พังต้องถูกซ่อม · ที่อยู่ที่ถูกอยู่แล้วห้ามถูกแตะ
/// (เทสต์ที่มีแต่ทิศแรกผ่านได้ทั้งตอนแก้ถูกและตอน “ตัดมั่ว”)
/// </summary>
public class OcrPartyAddressTests
{
    private const string BuyerName = "หจก. แอม แฮปปี้เนส";
    private const string PaperAddress = "202/24 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี20110";

    // ── ทิศที่ 1: เคสจริงที่ผู้ใช้เจอ ──
    [Fact]
    public void เคสจริง_เศษท้ายชื่อเกาะหัวที่อยู่_ต้องถูกตัดออก()
        => Assert.Equal(PaperAddress,
            OcrPartyAddress.StripLeakedNameFragment("เนส" + PaperAddress, BuyerName));

    [Fact]
    public void เศษที่ยาวกว่าหนึ่งพยางค์_ก็ต้องตัดได้()
        // กรอบตัดเร็วกว่าเดิมหนึ่งตัว ⇒ ที่อยู่ได้ “ปี้เนส202/24 …”
        => Assert.Equal(PaperAddress,
            OcrPartyAddress.StripLeakedNameFragment("ปี้เนส" + PaperAddress, BuyerName));

    [Fact]
    public void ช่องว่างคั่นระหว่างเศษกับที่อยู่_ยังต้องตัดได้()
        => Assert.Equal(PaperAddress,
            OcrPartyAddress.StripLeakedNameFragment("เนส " + PaperAddress, BuyerName));

    // ── ทิศที่ 2: ห้ามแตะของที่ถูกอยู่แล้ว ──
    [Fact]
    public void ที่อยู่ที่ถูกต้องอยู่แล้ว_ต้องไม่ถูกแตะ()
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment(PaperAddress, BuyerName));

    [Fact]
    public void ที่อยู่ผู้ขายบนใบเดียวกัน_ต้องไม่ถูกแตะ()
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "เลขที่ 6 ซอยท่าข้าม5 แขวงแสมดำ เขตบางขุนเทียน กรุงเทพมหานคร 10150",
            "บริษัท ลักกี้ เวย์ จำกัด"));

    [Fact]
    public void บริษัทที่ตั้งชื่อตามสถานที่_ห้ามตัดคำแรกของที่อยู่ทิ้ง()
    {
        // “บางพระ” เป็น**คำเต็ม**ในชื่อ (มีช่องว่างนำหน้า) ⇒ ไม่ใช่ลายเซ็นของการ
        // ตัดกรอบผิด · ถ้าตัด ที่อยู่จริงจะหายไปหนึ่งคำโดยไม่มีใครรู้
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "บางพระ 99/1 ถ.สุขุมวิท อ.ศรีราชา จ.ชลบุรี 20110",
            "บริษัท บางพระ จำกัด"));
    }

    [Fact]
    public void เศษสั้นเกินไป_ถือเป็นเรื่องบังเอิญ_ห้ามตัด()
        // ตัวเดียวตรงกันเกิดขึ้นได้ตลอด — ไม่ใช่หลักฐาน
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "ส 99 หมู่ 3 ต.หนองขาม", "บริษัท ทดสอบ จำกัดส"));

    [Fact]
    public void ตัดแล้วเหลือนิดเดียว_แปลว่าช่องนั้นไม่ใช่ที่อยู่_ห้ามตัด()
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment("เนส123", BuyerName));

    [Fact]
    public void ค่าว่าง_ต้องไม่พัง()
    {
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment(null, BuyerName));
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment(PaperAddress, null));
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment("   ", BuyerName));
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment(PaperAddress, "ก"));
    }

    // ── ที่อยู่สองแห่งถูกต่อกันเป็นสตริงเดียว (SplitGlued) ──────────────
    // ที่มา (สแกนจริง 2026-09-11 · บิลเงินสดเขียนมือ): Azure คืน VendorAddress
    // เดียวที่เอาที่อยู่ร้าน (กรอบบน) ต่อกับที่อยู่ลูกค้า (ช่อง “ที่อยู่/ADDRESS”)
    [Fact]
    public void เคสจริง_ที่อยู่สองแห่งต่อกัน_ต้องผ่าถูกจุด()
    {
        var (first, second) = OcrPartyAddress.SplitGlued(
            "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี202/24 ม.5 ซ. บ้านห้วยกุ่ม 4");
        Assert.Equal("177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี", first);
        Assert.Equal("202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", second);
    }

    [Fact]
    public void ที่อยู่แห่งเดียว_ต้องไม่ถูกผ่า()
        => Assert.Null(OcrPartyAddress.SplitGlued(PaperAddress).Second);

    [Fact]
    public void เลขทับสองตัวในที่อยู่เดียวกัน_ต้องไม่ถูกผ่า()
        // ไม่มี “จบที่อยู่” (จังหวัด/รหัสไปรษณีย์) คั่นกลาง ⇒ ยังเป็นแห่งเดียว
        => Assert.Null(OcrPartyAddress.SplitGlued(
            "เลขที่ 1/2 และ 1/3 ถ.สุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110").Second);

    [Fact]
    public void รหัสไปรษณีย์ก็นับเป็นจุดจบที่อยู่()
    {
        var (first, second) = OcrPartyAddress.SplitGlued(
            "99/1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110 55/7 ม.2 ต.หนองขาม");
        Assert.EndsWith("10110", first);
        Assert.StartsWith("55/7", second!);
    }

    [Fact]
    public void ก้อนที่สองสั้นเกินจะเป็นที่อยู่_ต้องไม่ผ่า()
        => Assert.Null(OcrPartyAddress.SplitGlued(
            "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 1/2").Second);

    [Fact]
    public void ห้ามตัดจนเหลือสระลอยเป็นตัวแรก()
        // ชื่อลงท้าย “แฮปป” + ที่อยู่ขึ้นต้น “แฮปปี้เนส…” ⇒ ถ้าตัดตรง ๆ ที่อยู่จะเริ่ม
        // ด้วย “ี้…” = เราตัดกลางคำของ**ที่อยู่**เอง (ที่อยู่จริงคือหมู่บ้านชื่อคล้ายกัน)
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "แฮปปี้เนสวิลล์ 202/24 ม.5 ต.บางพระ", "หจก.แอมแฮปป"));
}
