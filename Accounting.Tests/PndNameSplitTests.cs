using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// การแยก "คำนำหน้า | ชื่อตัว | ชื่อสกุล" ของไฟล์ ภ.ง.ด.3 (Col4/Col5/Col12)
///
/// ที่มา (รอบ 168): ด่านกัน "นายช่างการไฟฟ้า" (ชื่อร้าน — ห้ามผ่า) บังคับว่าส่วนที่
/// เหลือหลังคำนำหน้าต้องมีช่องว่าง ⇒ ผู้ถูกหักที่บันทึกชื่อไว้ติดกันโดยไม่มีนามสกุล
/// ("นายสมชาย") ได้ Col4 = "นายสมชาย" (คำนำหน้าค้าง) และ Col12 ว่าง — **แย่กว่าเดิม
/// ทั้งสองช่อง** เพราะก่อนรอบ 162 มันตัด "นาย" ทิ้งได้
///
/// ทางแก้ที่ **ไม่ใช่**การให้ตัวเดาเดาหนักขึ้น (จะพา "นายช่างการไฟฟ้า" พังกลับ):
/// ใช้ <c>Contact.TitleTh</c> ที่ผู้ใช้ยืนยันแล้วมาตัดแบบ deterministic
///
/// ล็อกสองทิศเสมอ — เคสที่พังต้องกลับมาถูก **และ** เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ
/// (จำลองสามยุคด้วย tools/thai_title_split_sim.py: A ผิด 11/13 · B ผิด 3/13 · C ผิด 0/13)
/// </summary>
public class PndNameSplitTests
{
    private static PndTextFileFormat.Row Row(string name, bool juristic, string? title = null)
        => new(PayeeTaxId: "0105556000000", BranchCode: "00000", PayeeName: name,
               IsJuristic: juristic, PayDate: new DateTime(2026, 7, 1),
               IncomeTypeCode: "5", IncomeAmount: 1000m, TaxRate: 3m, TaxAmount: 30m,
               Condition: 1, PayeeTitle: title);

    // ── ทิศที่ 1: regression ที่รอบ 162 ทำพัง ต้องกลับมาถูก ──────────────
    [Theory]
    [InlineData("นายสมชาย", "นาย", "นาย", "สมชาย", "")]
    [InlineData("นายสมชาย ใจดี", "นาย", "นาย", "สมชาย", "ใจดี")]
    // ผู้ใช้บันทึกชื่อแบบย่อ แต่ช่องคำนำหน้าเก็บรูปเต็ม — ต้องตัดรูปย่อออกได้
    [InlineData("น.ส.สมหญิง", "นางสาว", "นางสาว", "สมหญิง", "")]
    [InlineData("ด.ช.สมชาย ใจดี", "เด็กชาย", "เด็กชาย", "สมชาย", "ใจดี")]
    // ชื่อที่ไม่มีคำนำหน้าติดอยู่แล้ว — ห้ามตัดตัวอักษรใด ๆ ทิ้ง
    [InlineData("สมชาย ใจดี", "นาย", "นาย", "สมชาย", "ใจดี")]
    public void คำนำหน้าที่ผู้ใช้ยืนยันแล้ว_ตัดออกจากชื่อได้แม้ไม่มีนามสกุล(
        string name, string knownTitle, string expTitle, string expFirst, string expLast)
    {
        var (first, last) = PndTextFileFormat.SplitName(name, false, knownTitle);
        Assert.Equal(expFirst, first);
        Assert.Equal(expLast, last);
        var cols = PndTextFileFormat.DetailRow(1, Row(name, false, knownTitle)).Split('|');
        Assert.Equal(expFirst, cols[3]);   // Col4
        Assert.Equal(expLast, cols[4]);    // Col5
        Assert.Equal(expTitle, cols[11]);  // Col12
    }

    // ── ทิศที่ 2: เคสที่ถูกอยู่แล้ว ต้องไม่ถูกแตะ ────────────────────────
    [Theory]
    // ชื่อร้านที่ขึ้นต้นเหมือนคำนำหน้า + ไม่รู้คำนำหน้า ⇒ ห้ามผ่า
    [InlineData("นายช่างการไฟฟ้า", "นายช่างการไฟฟ้า", "")]
    [InlineData("นางเลิ้งพาณิชย์", "นางเลิ้งพาณิชย์", "")]
    // ชื่อจริงที่ขึ้นต้นด้วย "ดร" ⇒ ห้ามผ่ากลางคำ
    [InlineData("ดรุณี ใจดี", "ดรุณี", "ใจดี")]
    // รูปเต็ม/ตัวย่อมีจุด ติดชื่อ + มีนามสกุล ⇒ ยังตัดได้เหมือนเดิม
    [InlineData("นางสาวสมหญิง ใจดี", "สมหญิง", "ใจดี")]
    [InlineData("น.ส.สมหญิง ใจดี", "สมหญิง", "ใจดี")]
    [InlineData("เด็กชาย สมชาย ใจดี", "สมชาย", "ใจดี")]
    public void ไม่รู้คำนำหน้า_พฤติกรรมเดิมทุกประการ(string name, string expFirst, string expLast)
    {
        var (first, last) = PndTextFileFormat.SplitName(name, false);
        Assert.Equal(expFirst, first);
        Assert.Equal(expLast, last);
    }

    /// <summary>นิติบุคคลไม่มีนามสกุล — ชื่อเต็มอยู่ Col4 เสมอ ไม่ว่าจะส่ง title มาหรือไม่</summary>
    [Fact]
    public void นิติบุคคล_ชื่อเต็มอยู่ช่องแรกเสมอ()
    {
        var (first, last) = PndTextFileFormat.SplitName("บริษัท ก จำกัด", true, "บริษัท");
        Assert.Equal("บริษัท ก จำกัด", first);
        Assert.Equal("", last);
        Assert.False(PndTextFileFormat.NeedsNameReview(Row("บริษัท ก จำกัด", true, "บริษัท")));
    }

    /// <summary>แถวที่แยกชื่อตัว/ชื่อสกุลไม่ได้ ต้องถูก **รายงาน** ไม่ใช่เดาให้
    /// (ระบบแยกไม่ออกว่าคำแรกเป็นคำนำหน้าหรือเป็นส่วนของชื่อร้าน)</summary>
    [Theory]
    [InlineData("นายช่างการไฟฟ้า", null, true)]   // ไม่รู้คำนำหน้า + คำเดียว → ต้องรายงาน
    [InlineData("นายสมชาย", "นาย", true)]          // ตัดคำนำหน้าได้ แต่ยังไม่มีนามสกุล
    [InlineData("นายสมชาย ใจดี", "นาย", false)]    // ครบแล้ว → ไม่ต้องรายงาน
    [InlineData("สมชาย ใจดี", null, false)]
    public void แถวที่แยกชื่อไม่ได้_ต้องถูกรายงาน(string name, string? title, bool expect)
        => Assert.Equal(expect, PndTextFileFormat.NeedsNameReview(Row(name, false, title)));
}
