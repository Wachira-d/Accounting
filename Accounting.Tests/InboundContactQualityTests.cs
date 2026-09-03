using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คุณภาพข้อมูลผู้ติดต่อที่มาจากระบบภายนอก (บั๊กจริง — ผู้ใช้รายงาน 2026-09-03)
///
/// ═══ อาการที่ผู้ใช้เจอ ═══
/// <list type="number">
/// <item>ชื่อบริษัทขึ้นว่า <c>"ทบริษัท คาร์วิน ไทย แอดวานซ์…"</c> — มีอักษรแปลกนำหน้า
///   ทั้งที่ระบบต้นทางส่ง<b>เลขผู้เสียภาษีที่ถูกต้อง</b>มาด้วย แต่เราไม่เคยเอาเลขไป
///   ตรวจกับทะเบียนกรมพัฒนาธุรกิจการค้าเลย</item>
/// <item>ช่อง <b>หมู่ที่</b> มีค่า <c>"ทะเบียนการค้า : 0105564045849 บริษั"</c> —
///   ฟิลด์เหลื่อมฝั่งต้นทาง (ข้อความถูกตัดกลางคำ = ลายเซ็นของการ truncate)</item>
/// </list>
/// เราแก้ต้นทางไม่ได้ แต่ต้องไม่ปล่อยให้ไหลลงใบกำกับภาษี (§86/4 บังคับชื่อ+ที่อยู่ผู้ซื้อ)
/// </summary>
public class InboundContactQualityTests
{
    // ── ด่าน "ทะเบียนชนะได้ก็ต่อเมื่อกุญแจถูก" ──

    [Fact]
    public void ชื่อที่ต้นทางส่งมาเพี้ยนเล็กน้อย_ทะเบียนต้องชนะ()
    {
        // เคสจริงจากผู้ใช้: ต่างกันแค่อักษร "ท" นำหน้า
        const string official = "บริษัท คาร์วิน ไทย แอดวานซ์ เทคโนโลยี อินดัสเทรียล จำกัด";
        const string incoming = "ทบริษัท คาร์วิน ไทย แอดวานซ์ เทคโนโลยี อินดัสเทรียล จำกัด (สำนักงานใหญ่)";
        var sim = Accounting.Services.Implementations.Ocr.FuzzyMatcher.Similarity(official, incoming);
        var verdict = DbdIdentityGuard.Judge(official, incoming, sim);
        Assert.True(DbdIdentityGuard.RegistryWins(verdict),
            $"ได้ {verdict} (คะแนน {sim:F3}) — ด่านต้องไม่บล็อกการแก้ที่ถูกต้อง");
    }

    [Fact]
    public void เลขผู้เสียภาษีผิด_ได้บริษัทอื่น_ห้ามทับชื่อ()
    {
        // เลขผิดหนึ่งหลัก ⇒ ทะเบียนคืน "คนละบริษัท" ที่ถูกต้อง 100% ตามทะเบียน
        // ถ้าปล่อยให้ชนะ = ทับชื่อที่ถูกอยู่แล้วด้วยชื่อบริษัทที่ไม่เกี่ยวกันเลย
        const string official = "บริษัท สยามแม็คโคร จำกัด (มหาชน)";
        const string incoming = "บริษัท คาร์วิน ไทย แอดวานซ์ เทคโนโลยี อินดัสเทรียล จำกัด";
        var sim = Accounting.Services.Implementations.Ocr.FuzzyMatcher.Similarity(official, incoming);
        Assert.Equal(DbdTrustVerdict.KeyLooksWrong, DbdIdentityGuard.Judge(official, incoming, sim));
        Assert.False(DbdIdentityGuard.RegistryWins(DbdIdentityGuard.Judge(official, incoming, sim)));
    }

    [Fact]
    public void ต้นทางไม่ส่งชื่อมา_ใช้ชื่อทางการได้เลย()
        => Assert.Equal(DbdTrustVerdict.NoIncomingName,
            DbdIdentityGuard.Judge("บริษัท ก จำกัด", "", 0));

    [Fact]
    public void ตัดคำมาตรฐานก่อนเทียบ_ชื่อเดียวกันคนละรูปแบบต้องตรงกัน()
    {
        // "(สำนักงานใหญ่)" · "บริษัท"/"จำกัด" ไม่ช่วยแยกตัวตน
        var v = DbdIdentityGuard.Judge(
            "บริษัท คาร์วิน ไทย จำกัด", "คาร์วิน ไทย (สำนักงานใหญ่)", 0.0);
        Assert.Equal(DbdTrustVerdict.ExactMatch, v);
    }

    [Fact]
    public void ข้อความปฏิเสธต้องบอกว่าให้ไปตรวจอะไร()
    {
        var msg = DbdIdentityGuard.KeyMismatchMessage("0105564045849", "บริษัท ก จำกัด", "บริษัท ข จำกัด");
        Assert.Contains("0105564045849", msg);
        Assert.Contains("ตรวจเลข", msg);   // บอกทางไปต่อ ไม่ใช่แค่บอกว่าไม่ผ่าน
    }

    // ── ด่านค่าที่ต้นทางส่งผิดช่อง ──

    [Fact]
    public void ค่าที่ผู้ใช้เจอจริงในช่องหมู่ที่_ต้องถูกปฏิเสธ()
    {
        var r = InboundAddressSanity.CheckStructured("ทะเบียนการค้า : 0105564045849 บริษั");
        Assert.False(r.Accepted);
        Assert.NotNull(r.Reason);
    }

    [Theory]
    [InlineData("เลขประจำตัวผู้เสียภาษี 0105564045849")]
    [InlineData("0105564045849")]
    [InlineData("Tax ID 0105564045849")]
    public void ค่าที่เป็นเนื้อหาของช่องอื่น_ต้องถูกปฏิเสธ(string value)
        => Assert.False(InboundAddressSanity.CheckStructured(value).Accepted);

    [Theory]
    [InlineData("5")]
    [InlineData("หมู่ 5")]
    [InlineData("มาบโป่ง")]
    [InlineData("พานทอง")]
    [InlineData("ชลบุรี")]
    [InlineData("บ้านหนองตะเคียนบอน")]     // ชื่อยาวแต่เป็นที่อยู่จริง — ต้องผ่าน
    public void ค่าที่อยู่ปกติ_ต้องผ่าน(string value)
        => Assert.True(InboundAddressSanity.CheckStructured(value).Accepted,
            $"\"{value}\" ควรผ่าน — ด่านที่เข้มเกินจะไปตัดที่อยู่จริงทิ้ง");

    [Fact]
    public void ค่าว่าง_ถือว่าผ่าน_ไม่ใช่ข้อผิดพลาด()
    {
        Assert.True(InboundAddressSanity.CheckStructured(null).Accepted);
        Assert.True(InboundAddressSanity.CheckStructured("   ").Accepted);
    }

    [Fact]
    public void ค่ายาวผิดปกติ_คือฟิลด์เหลื่อม()
        => Assert.False(InboundAddressSanity.CheckStructured(new string('ก', 80)).Accepted);

    [Theory]
    [InlineData("20160", true)]
    [InlineData("2016", false)]      // ไม่ครบ 5 หลัก
    [InlineData("201601", false)]    // เกิน
    [InlineData("2016A", false)]     // ไม่ใช่ตัวเลขล้วน — e-Tax XML จะไม่ผ่าน validation
    public void รหัสไปรษณีย์ต้องเป็นตัวเลข_5_หลัก(string value, bool expected)
        => Assert.Equal(expected, InboundAddressSanity.CheckPostalCode(value).Accepted);

    // ── ประเภทผู้ติดต่อ: ตัวตัดสิน ภ.ง.ด.3 vs 53 ──

    [Fact]
    public void เลข_13_หลักขึ้นต้นศูนย์_คือนิติบุคคล()
    {
        // เดิม ParseContactType default เป็น Individual เสมอเมื่อต้นทางไม่ส่งประเภทมา
        // ⇒ นิติบุคคลถูกจัดเป็นบุคคลธรรมดาเงียบ ๆ แล้วยื่น ภ.ง.ด. ผิดแบบ
        Assert.True(ThaiTaxId.IsJuristic("0105564045849"));
    }

    [Fact]
    public void เลขบัตรประชาชน_ไม่ใช่นิติบุคคล()
        // checksum ถูกต้อง (ขึ้นต้น 1) — เทสต์นี้ต้องพิสูจน์กติกา "ขึ้นต้น 0 = นิติบุคคล"
        // ไม่ใช่ผ่านเพราะ checksum ผิด ซึ่งจะทำให้เทสต์เขียวด้วยเหตุผลที่ผิด
        => Assert.False(ThaiTaxId.IsJuristic("1101700207251"));
}
