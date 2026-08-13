using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ที่อยู่บนเอกสารราชการ (50 ทวิ §50 ทวิ, ใบกำกับภาษีเต็มรูป §86/4) ต้องระบุ
/// "ตำบล/แขวง อำเภอ/เขต จังหวัด" ให้อ่านออกว่าส่วนไหนคืออะไร — ตามข้อความกำกับ
/// ในแบบฟอร์มของกรมสรรพากรเอง เคสในไฟล์นี้คือเคสจริงที่เคยพิมพ์ผิดออกมา
/// </summary>
public class ThaiAddressFormatterTests
{
    private static string Fmt(string? free = null, string? bno = null, string? bname = null,
        string? moo = null, string? street = null, string? sub = null, string? dist = null,
        string? prov = null, string? post = null)
        => ThaiAddressFormatter.Format(free, bno, bname, moo, street, sub, dist, prov, post);

    // ── เคสจริงจากหนังสือรับรองหัก ณ ที่จ่ายที่พิมพ์ผิด ──────────────

    [Fact]
    public void FreeTextWithoutPrefixes_GetsPrefixes()
    {
        // ที่ผู้ใช้เห็นบนกระดาษ: "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140"
        var r = Fmt(free: "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140");
        Assert.Equal("44 หมู่ 9 ต.หนองเหียง อ.พนัสนิคม จ.ชลบุรี 20140", r);
    }

    [Fact]
    public void FreeTextNoPrefixes_ButProvinceAlreadyStructured_StillGetsTambonAmphoe()
    {
        // เคสที่หลุดกฎเดิม (เดิม parse เฉพาะตอน sub+dist+prov ว่างครบ 3 ช่อง)
        var r = Fmt(free: "44 หมู่ 9 หนองเหียง พนัสนิคม ชลบุรี 20140", prov: "ชลบุรี");
        Assert.Contains("ต.หนองเหียง", r);
        Assert.Contains("อ.พนัสนิคม", r);
        Assert.Contains("จ.ชลบุรี", r);
    }

    [Fact]
    public void StructuredOnly_NoFreeText_GetsPrefixes()
    {
        // เส้นทางที่ ComposeAddress ของ Contact/Company ใช้เขียนลงฐาน
        var r = Fmt(bno: "44", moo: "9", sub: "หนองเหียง", dist: "พนัสนิคม",
            prov: "ชลบุรี", post: "20140");
        Assert.Equal("44 หมู่ 9 ต.หนองเหียง อ.พนัสนิคม จ.ชลบุรี 20140", r);
    }

    [Fact]
    public void FreeTextAlreadyPrefixed_IsNotDuplicated()
    {
        // ที่อยู่ผู้ถูกหักในเอกสารเดียวกัน — เคยถูกต้องอยู่แล้ว ห้าม regress
        var r = Fmt(free: "91/9 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110");
        Assert.Equal("91/9 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", r);
    }

    [Fact]
    public void FreeTextAndStructured_Both_NoEcho()
    {
        var r = Fmt(free: "91/9 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110",
            sub: "บางพระ", dist: "ศรีราชา", prov: "ชลบุรี", post: "20110");
        Assert.Equal("91/9 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", r);
        // ชื่อตำบลต้องปรากฏครั้งเดียว ไม่ใช่ทั้งใน street part และ locality
        Assert.Equal(1, CountOccurrences(r, "บางพระ"));
        Assert.Equal(1, CountOccurrences(r, "20110"));
    }

    // ── กรุงเทพมหานคร: แขวง/เขต และไม่มี "จ." ────────────────────────

    [Theory]
    [InlineData("กรุงเทพมหานคร")]
    [InlineData("กรุงเทพฯ")]
    [InlineData("กทม")]
    public void Bangkok_UsesKhwaengKhet_AndNoChorPrefix(string provinceSpelling)
    {
        var r = Fmt(bno: "88", street: "ถ.พระราม 4", sub: "สีลม", dist: "บางรัก",
            prov: provinceSpelling, post: "10500");
        Assert.Contains("แขวงสีลม", r);
        Assert.Contains("เขตบางรัก", r);
        Assert.Contains("กรุงเทพมหานคร", r);
        Assert.DoesNotContain("จ.กรุงเทพ", r);
        Assert.DoesNotContain("ต.สีลม", r);
        Assert.DoesNotContain("อ.บางรัก", r);
    }

    [Fact]
    public void Bangkok_MisEnteredIntoDistrictField_NotPrintedTwice()
    {
        var r = Fmt(bno: "8/36", sub: "ดอกไม้", dist: "กทม",
            prov: "กรุงเทพมหานคร", post: "10250");
        Assert.Equal(1, CountOccurrences(r, "กรุงเทพมหานคร"));
        Assert.DoesNotContain("กทม", r);
    }

    [Fact]
    public void Bangkok_DoubledInFreeText_IsCollapsed()
    {
        var r = Fmt(free: "8/36 แขวงดอกไม้ เขตประเวศ กทม กรุงเทพมหานคร 10250");
        Assert.Equal(1, CountOccurrences(r, "กรุงเทพมหานคร"));
    }

    // ── กันคำนำหน้าซ้อน / ชื่อถนนโดนกิน ────────────────────────────

    [Fact]
    public void PrefixTypedIntoStructuredField_IsNotDoubled()
    {
        // ผู้ใช้พิมพ์ "ต.หนองเหียง" ลงช่องตำบลเอง → ต้องไม่ได้ "ต.ต.หนองเหียง"
        var r = Fmt(bno: "44", sub: "ต.หนองเหียง", dist: "อ.พนัสนิคม",
            prov: "จ.ชลบุรี", post: "20140");
        Assert.Equal("44 ต.หนองเหียง อ.พนัสนิคม จ.ชลบุรี 20140", r);
    }

    [Fact]
    public void RoadNameContainingTambonName_IsNotMangled()
    {
        // regression: substring replace เคยตัด "บางนาตราด" เหลือ "ตราด"
        var r = Fmt(free: "12 ถนนบางนาตราด ตำบลบางนา อำเภอบางพลี จังหวัดสมุทรปราการ 10540");
        Assert.Contains("บางนาตราด", r);
        Assert.Contains("ต.บางนา", r);
        Assert.Contains("อ.บางพลี", r);
        Assert.Contains("จ.สมุทรปราการ", r);
    }

    // ── กู้จังหวัดจากรหัสไปรษณีย์เมื่อที่อยู่ไม่ได้เขียนชื่อจังหวัดไว้เลย ──────

    [Fact]
    public void NoProvinceNameAtAll_RecoveredFromPostalCode()
    {
        // "44 หมู่ 9 หนองเหียง พนัสนิคม 20140" — ไม่มีคำว่า "ชลบุรี" ในข้อความ
        // รหัสไปรษณีย์ชี้จังหวัดได้แน่นอนจากทะเบียนราชการ (ไม่ใช่การเดา)
        var r = Fmt(free: "44 หมู่ 9 หนองเหียง พนัสนิคม 20140");
        Assert.Contains("จ.ชลบุรี", r);
        Assert.Contains("อ.พนัสนิคม", r);
        Assert.Contains("ต.หนองเหียง", r);
    }

    // ── ขอบเขต: ไม่มีข้อมูลพอ ต้องไม่พังและไม่แต่งเติมมั่ว ─────────────

    [Fact]
    public void NoLocalityAnywhere_ReturnsFreeTextUnchanged()
    {
        var r = Fmt(free: "123 อาคารเอบีซี ชั้น 5");
        Assert.Equal("123 อาคารเอบีซี ชั้น 5", r);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.True(string.IsNullOrWhiteSpace(Fmt()));

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
