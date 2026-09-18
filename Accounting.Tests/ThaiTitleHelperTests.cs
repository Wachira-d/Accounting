using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตารางคำนำหน้าชื่อกลาง + ตัวแยกคำนำหน้าออกจากชื่อเต็ม
///
/// ล็อก **สองทิศ** ตามกฎ CLAUDE.md §H: ครึ่งหนึ่งพิสูจน์ว่าเคสที่เคยตัดผิดกลับมาถูก
/// อีกครึ่งพิสูจน์ว่าเคสที่เคยถูกอยู่แล้ว **ไม่ถูกแตะ** — เทสต์ที่มีแต่ครึ่งแรก
/// ผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดตัวแยกทิ้ง"
/// (จำลองด้วย tools/thai_title_split_sim.py: ก่อนแก้ผิด 2/11 · หลังแก้ 0/11)
/// </summary>
public class ThaiTitleHelperTests
{
    // ═══ ทิศที่เคยพัง: ตัวย่อที่ไม่มีจุด เป็นต้นคำของคำอื่นได้ ═══

    [Theory]
    [InlineData("ดรุณี ใจดี")]        // "ดร" + สระอุ — เคยได้ ("ดร.", "ุณี ใจดี")
    [InlineData("Drake Co Ltd")]      // "Dr" + ake — เคยได้ ("ดร.", "ake Co Ltd")
    public void ตัวย่อไม่มีจุดที่ติดกับคำ_ต้องไม่ถูกตัด(string full)
    {
        var (title, rest) = ThaiTitleHelper.Split(full);
        Assert.Equal("", title);
        Assert.Equal(full, rest);
    }

    // ═══ ทิศที่ต้องยังทำงาน: ตัดได้เหมือนเดิม ═══

    [Theory]
    [InlineData("นางสาวสมหญิง ใจดี", "นางสาว", "สมหญิง ใจดี")]   // รูปเต็มติดชื่อ
    [InlineData("น.ส.สมหญิง ใจดี", "นางสาว", "สมหญิง ใจดี")]     // ตัวย่อมีจุดติดชื่อ
    [InlineData("นาย สมชาย ใจดี", "นาย", "สมชาย ใจดี")]
    [InlineData("ดร. สมชาย ใจดี", "ดร.", "สมชาย ใจดี")]
    [InlineData("เด็กชาย สมชาย ใจดี", "เด็กชาย", "สมชาย ใจดี")]
    [InlineData("หจก. แอม แฮปปี้เนส", "ห้างหุ้นส่วนจำกัด", "แอม แฮปปี้เนส")]
    [InlineData("บริษัท ก จำกัด", "บริษัท", "ก จำกัด")]
    public void แยกคำนำหน้าได้_และคืนรูปเต็มภาษาไทยเสมอ(string full, string title, string rest)
    {
        var got = ThaiTitleHelper.Split(full);
        Assert.Equal(title, got.Title);
        Assert.Equal(rest, got.Remainder);
    }

    [Theory]
    [InlineData("นายช่างการไฟฟ้า")]
    [InlineData("นางเลิ้งพาณิชย์")]
    [InlineData("นายหน้าประกันภัย")]
    public void ชื่อกิจการที่บังเอิญขึ้นต้นเหมือนคำนำหน้า_ห้ามถูกตัด(string full)
    {
        var (title, rest) = ThaiTitleHelper.Split(full);
        Assert.Equal("", title);
        Assert.Equal(full, rest);
    }

    // ═══ ด่านตรวจคำนำหน้า ═══

    [Theory]
    [InlineData("นาย", "นาย")]
    [InlineData("น.ส.", "นางสาว")]
    [InlineData("Mrs.", "นาง")]
    [InlineData("Mr", "นาย")]
    [InlineData("ด.ช.", "เด็กชาย")]
    [InlineData("ดร", "ดร.")]
    [InlineData("หจก.", "ห้างหุ้นส่วนจำกัด")]
    [InlineData("Mister", "นาย")]       // ผ่าน Normalize → ตาราง
    public void คำนำหน้าที่รู้จัก_ต้องผ่านและคืนรูปเต็มภาษาไทย(string input, string expected)
    {
        Assert.True(ThaiTitleHelper.TryCanonical(input, out var canonical, out var reason));
        Assert.Equal(expected, canonical);
        Assert.Null(reason);
    }

    [Fact]
    public void ไม่ระบุคำนำหน้า_ถือว่าถูกต้อง()
    {
        Assert.True(ThaiTitleHelper.TryCanonical(null, out var c1, out _));
        Assert.Equal("", c1);
        Assert.True(ThaiTitleHelper.TryCanonical("   ", out var c2, out _));
        Assert.Equal("", c2);
    }

    [Theory]
    [InlineData("คุณ")]        // สุภาพแต่ไม่ใช่คำนำหน้าตามแบบราชการ
    [InlineData("Sir")]
    [InlineData("<script>")]   // ค่าที่ยิงผ่าน API ได้ ถ้าไม่มีด่าน
    public void คำนำหน้าที่ไม่อยู่ในตาราง_ต้องไม่ผ่าน_และบอกทางไปต่อ(string input)
    {
        Assert.False(ThaiTitleHelper.TryCanonical(input, out var canonical, out var reason));
        Assert.Equal("", canonical);
        Assert.NotNull(reason);
        // ข้อความต้องสร้างจากตารางกลาง ไม่ใช่พิมพ์ลิสต์ซ้ำในสตริง
        Assert.Contains("นางสาว", reason);
        Assert.Contains("ห้างหุ้นส่วนจำกัด", reason);
    }

    // ═══ Normalize กับตารางกลางต้องตอบตรงกัน ═══

    [Fact]
    public void ทุกรูปย่อในตาราง_Normalize_ต้องคืนรูปเต็มเดียวกัน()
    {
        foreach (var p in ThaiTitleHelper.All)
        {
            Assert.Equal(p.Thai, ThaiTitleHelper.Normalize(p.Thai));
            foreach (var alias in p.Aliases)
                Assert.Equal(p.Thai, ThaiTitleHelper.Normalize(alias));
        }
    }

    [Fact]
    public void คำนำหน้าที่ยื่นประกันสังคมได้_ต้องอยู่ในตารางกลางด้วย()
    {
        foreach (var t in ThaiTitleHelper.SsoValidTitles)
            Assert.Contains(ThaiTitleHelper.All, p => p.Thai == t);
    }

    /// <summary>ชื่อบนหนังสือรับรอง 50 ทวิ = คำนำหน้า + ชื่อ — เดิม PDF พิมพ์ `Contact.Name`
    /// เปล่า ๆ ขณะไฟล์ ภ.ง.ด.3 ประกาศคำนำหน้า ⇒ สอง renderer ของคนเดียวเล่าคนละชื่อ.
    /// ล็อกสองทิศ: ต่อเมื่อควรต่อ · **ไม่ต่อซ้ำ**เมื่อชื่อมีคำนำหน้าอยู่แล้ว · ไม่ต่อให้นิติบุคคล</summary>
    [Theory]
    [InlineData("นาย", "สมชาย ใจดี", "นาย สมชาย ใจดี")]
    [InlineData("Mr.", "สมชาย ใจดี", "นาย สมชาย ใจดี")]          // รูปอังกฤษถูก normalize
    [InlineData("นาย", "นายสมชาย ใจดี", "นายสมชาย ใจดี")]        // มีอยู่แล้ว — ห้ามซ้ำ
    [InlineData("นางสาว", "น.ส.สมหญิง ใจดี", "น.ส.สมหญิง ใจดี")]   // รูปย่อของคำเดียวกัน — ห้ามซ้ำ
    [InlineData("บริษัท", "บริษัท ก จำกัด", "บริษัท ก จำกัด")]      // นิติบุคคล — ไม่ต่อ
    [InlineData("", "สมชาย ใจดี", "สมชาย ใจดี")]                   // ไม่รู้คำนำหน้า — คงเดิม
    [InlineData(null, "สมชาย ใจดี", "สมชาย ใจดี")]
    public void WithTitle_ต่อคำนำหน้าเฉพาะเมื่อควรต่อ(string? title, string name, string expected)
        => Assert.Equal(expected, ThaiTitleHelper.WithTitle(title, name));
}
