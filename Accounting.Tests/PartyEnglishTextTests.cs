using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ชื่อ/ที่อยู่ "คู่สัญญา" บนเอกสารภาษาอังกฤษ — resolver กลางตัวเดียว
///
/// ที่มา: เดิมตรรกะนี้ถูกเขียนซ้ำ 4 จุด (บริษัท×HTML, บริษัท×QuestPDF,
/// ผู้ติดต่อ×HTML, ผู้ติดต่อ×QuestPDF) และ **สองจุดของผู้ติดต่อไม่มีชั้น
/// "ที่ผู้ใช้กรอกเอง" เลย** — ถอดอักษรอัตโนมัติเสมอ ⇒ ต่อให้รู้ว่าตัวถอดสะกด
/// ตำบล/อำเภอเพี้ยน ก็แก้ไม่ได้ตลอดกาล
/// </summary>
public class PartyEnglishTextTests
{
    // ── ที่อยู่: ลำดับชั้น กรอกเอง > ถอดอักษร > ไทย ──────────────────────

    [Fact]
    public void English_document_prefers_the_address_the_user_typed()
    {
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: "44 Moo 9, Nong Hiang, Phanat Nikhom, Chon Buri 20140",
            freeText: null, buildingNumber: "44", buildingName: null, moo: "9", street: null,
            subDistrict: "หนองเหียง", district: "พนัสนิคม", province: "ชลบุรี", postalCode: "20140");

        Assert.Equal("44 Moo 9, Nong Hiang, Phanat Nikhom, Chon Buri 20140", addr);
    }

    [Fact]
    public void Without_a_typed_address_it_transliterates_automatically()
    {
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: null,
            freeText: null, buildingNumber: "44", buildingName: null, moo: "9", street: null,
            subDistrict: "หนองเหียง", district: "พนัสนิคม", province: "ชลบุรี", postalCode: "20140");

        Assert.DoesNotContain(addr, c => c >= '฀' && c <= '๿');   // ไม่มีไทยหลุด
        Assert.Contains("Chon Buri", addr);                                 // จังหวัดจากตารางทางการ
        Assert.Contains("20140", addr);
    }

    [Fact]
    public void A_blank_typed_address_falls_through_instead_of_printing_nothing()
    {
        // ผู้ใช้เปิดกล่องแล้วไม่ได้กรอก / กรอกแล้วลบทิ้งเหลือช่องว่าง — ต้องไม่
        // ทำให้ที่อยู่บนเอกสารหายไปทั้งบรรทัด
        foreach (var blank in new[] { "", "   ", "\t" })
        {
            var addr = ThaiAddressFormatter.ResolvePartyAddress(
                isEnglish: true, addressEn: blank,
                freeText: null, buildingNumber: "44", buildingName: null, moo: "9", street: null,
                subDistrict: "หนองเหียง", district: "พนัสนิคม", province: "ชลบุรี", postalCode: "20140");
            Assert.False(string.IsNullOrWhiteSpace(addr));
            Assert.Contains("Chon Buri", addr);
        }
    }

    [Fact]
    public void A_thai_document_ignores_the_english_address_entirely()
    {
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: false, addressEn: "44 Moo 9, Chon Buri",
            freeText: null, buildingNumber: "44", buildingName: null, moo: "9", street: null,
            subDistrict: "หนองเหียง", district: "พนัสนิคม", province: "ชลบุรี", postalCode: "20140");

        Assert.Contains("หนองเหียง", addr);
        Assert.DoesNotContain("Moo 9", addr);
    }

    [Fact]
    public void The_typed_address_is_trimmed_before_printing()
    {
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: "  44 Moo 9, Chon Buri 20140  ",
            freeText: null, buildingNumber: null, buildingName: null, moo: null, street: null,
            subDistrict: null, district: null, province: null, postalCode: null);
        Assert.Equal("44 Moo 9, Chon Buri 20140", addr);
    }

    [Fact]
    public void A_foreign_party_whose_address_is_already_latin_passes_through()
    {
        // ลูกค้าต่างชาติ (Booking.com B.V.) — ที่อยู่เป็นละตินอยู่แล้ว
        // ตัวถอดต้องไม่ไปแตะ
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: null,
            freeText: "Oosterdoksstraat 80, Amsterdam", buildingNumber: null, buildingName: null,
            moo: null, street: null, subDistrict: null, district: null,
            province: null, postalCode: null);
        Assert.Contains("Amsterdam", addr);
    }

    // ── ชื่อ: ห้ามถอดอักษรชื่อเฉพาะให้เอง ───────────────────────────────

    [Fact]
    public void English_document_uses_the_registered_english_name()
    {
        Assert.Equal("ABC Service Engineering Co., Ltd.",
            ThaiAddressFormatter.ResolvePartyName(
                isEnglish: true, nameEn: "ABC Service Engineering Co., Ltd.",
                name: "บริษัท เอบีซี เซอร์วิส เอ็นจิเนียริ่ง จำกัด"));
    }

    [Fact]
    public void Without_an_english_name_the_thai_name_stays_as_is()
    {
        // **ห้ามถอดอักษรชื่อให้อัตโนมัติ** — การสะกดชื่อเฉพาะเป็นสิทธิ์ของ
        // เจ้าของชื่อ (จดทะเบียนไว้อย่างไรต้องตามนั้น) เดาผิด = เอกสารระบุ
        // คู่สัญญาผิดคน ต่างจากที่อยู่ที่ถอดผิดยังสื่อสารได้
        var thaiName = "บริษัท เอบีซี เซอร์วิส เอ็นจิเนียริ่ง จำกัด";
        Assert.Equal(thaiName,
            ThaiAddressFormatter.ResolvePartyName(isEnglish: true, nameEn: null, name: thaiName));
        Assert.Equal(thaiName,
            ThaiAddressFormatter.ResolvePartyName(isEnglish: true, nameEn: "  ", name: thaiName));
    }

    [Fact]
    public void A_thai_document_always_uses_the_thai_name()
    {
        Assert.Equal("บริษัท เอบีซี จำกัด",
            ThaiAddressFormatter.ResolvePartyName(
                isEnglish: false, nameEn: "ABC Co., Ltd.", name: "บริษัท เอบีซี จำกัด"));
    }

    [Fact]
    public void A_missing_name_resolves_to_empty_not_null()
    {
        // renderer ต่อสตริงตรง ๆ — null ทำให้พังหรือพิมพ์ "null" ออกกระดาษ
        Assert.Equal("", ThaiAddressFormatter.ResolvePartyName(true, null, null));
        Assert.Equal("", ThaiAddressFormatter.ResolvePartyName(false, null, null));
    }

    // ── ทั้งสองฝ่ายต้องได้ชั้น "กรอกเอง" เท่ากัน ────────────────────────

    [Fact]
    public void Company_and_contact_go_through_the_same_resolver()
    {
        // ยิงค่าชุดเดียวกันเข้า resolver — ผลต้องเท่ากันไม่ว่าจะเรียกจากฝั่งไหน
        // (เดิมฝั่งผู้ติดต่อไม่มีชั้น addressEn ⇒ ผลต่างกันเสมอ)
        const string typed = "1 Silom Rd., Bang Rak, Bangkok 10500";
        var asCompany = ThaiAddressFormatter.ResolvePartyAddress(
            true, typed, "1 ถนนสีลม", null, null, null, "สีลม", "สีลม", "บางรัก", "กรุงเทพมหานคร", "10500");
        var asContact = ThaiAddressFormatter.ResolvePartyAddress(
            true, typed, "1 ถนนสีลม", null, null, null, "สีลม", "สีลม", "บางรัก", "กรุงเทพมหานคร", "10500");
        Assert.Equal(asCompany, asContact);
        Assert.Equal(typed, asContact);
    }

    // ── ตารางสะกดทางการระดับอำเภอ/เขต ชนะตัวถอดอักษร ──────────────────

    [Theory]
    [InlineData("บางรัก", "Bang Rak")]
    [InlineData("ปทุมวัน", "Pathum Wan")]
    [InlineData("ห้วยขวาง", "Huai Khwang")]
    [InlineData("ป้อมปราบศัตรูพ่าย", "Pom Prap Sattru Phai")]
    [InlineData("บางกอกน้อย", "Bangkok Noi")]
    [InlineData("จตุจักร", "Chatuchak")]
    [InlineData("คลองเตย", "Khlong Toei")]
    [InlineData("สาทร", "Sathon")]
    public void Bangkok_districts_use_the_verified_spelling(string thai, string expected)
        => Assert.Equal(expected, ThaiRomanizer.PlaceNameEn(thai));

    /// <summary>อำเภอเมือง 75 แห่งประกอบจากตารางจังหวัดทางการ — ไม่มีใครพิมพ์
    /// ชื่อเหล่านี้ด้วยมือ จึงไม่มีทางสะกดผิด</summary>
    [Theory]
    [InlineData("เมืองชลบุรี", "Mueang Chon Buri")]
    [InlineData("เมืองเชียงใหม่", "Mueang Chiang Mai")]
    [InlineData("เมืองกาญจนบุรี", "Mueang Kanchanaburi")]
    [InlineData("เมืองนครราชสีมา", "Mueang Nakhon Ratchasima")]
    public void Provincial_capital_districts_compose_from_the_official_province_table(
        string thai, string expected)
        => Assert.Equal(expected, ThaiRomanizer.PlaceNameEn(thai));

    /// <summary>ชื่อที่ขึ้นต้น "เมือง" แต่ส่วนหลัง **ไม่ใช่ชื่อจังหวัด** ต้องตกไป
    /// ตัวถอด ไม่ใช่ประกอบมั่ว — เคสจริงในทะเบียน: เมืองจันทร์ (ศรีสะเกษ),
    /// เมืองปาน (ลำปาง), เมืองยาง (นครราชสีมา), เมืองสรวง (ร้อยเอ็ด)</summary>
    [Theory]
    [InlineData("เมืองจันทร์")]
    [InlineData("เมืองปาน")]
    [InlineData("เมืองยาง")]
    [InlineData("เมืองสรวง")]
    public void A_mueang_prefix_that_is_not_a_province_is_not_composed(string thai)
        => Assert.Null(ThaiRomanizer.PlaceNameEn(thai));

    [Fact]
    public void An_unknown_place_falls_through_to_the_transliterator()
    {
        Assert.Null(ThaiRomanizer.PlaceNameEn("หนองเหียง"));
        Assert.Null(ThaiRomanizer.PlaceNameEn(null));
        Assert.Null(ThaiRomanizer.PlaceNameEn("   "));
    }

    [Fact]
    public void The_table_beats_the_transliterator_inside_a_full_address()
    {
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: null,
            freeText: null, buildingNumber: "1", buildingName: null, moo: null, street: "สีลม",
            subDistrict: "สีลม", district: "บางรัก", province: "กรุงเทพมหานคร", postalCode: "10500");

        Assert.Contains("Bang Rak", addr);   // จากตาราง ไม่ใช่ผลถอดอักษร
        Assert.Contains("Bangkok", addr);    // จังหวัดจากตารางทางการ
        Assert.Contains("10500", addr);
    }

    [Fact]
    public void Prefixes_are_stripped_before_the_table_lookup()
    {
        // ผู้ใช้กรอก "เขตบางรัก" / "แขวงบางรัก" / "อ.เมืองชลบุรี" มาก็ต้องเจอ
        var addr = ThaiAddressFormatter.ResolvePartyAddress(
            isEnglish: true, addressEn: null,
            freeText: null, buildingNumber: "1", buildingName: null, moo: null, street: null,
            subDistrict: "แขวงบางรัก", district: "เขตบางรัก",
            province: "กรุงเทพมหานคร", postalCode: "10500");
        Assert.Contains("Bang Rak", addr);
    }

    [Fact]
    public void Fixing_the_address_once_fixes_every_later_document()
    {
        // เก็บที่ผู้ติดต่อ ไม่ใช่ที่ใบ ⇒ ใบต่อ ๆ ไปของลูกค้ารายนั้นถูกตลอด
        string Render(string? saved) => ThaiAddressFormatter.ResolvePartyAddress(
            true, saved, null, "44", null, "9", null, "หนองเหียง", "พนัสนิคม", "ชลบุรี", "20140");

        var beforeFix = Render(null);
        var afterFix = Render("44 Moo 9, Nong Hiang, Phanat Nikhom, Chonburi 20140");
        Assert.NotEqual(beforeFix, afterFix);
        // ใบที่ 2 และ 3 หลังแก้ ได้ค่าเดียวกันโดยไม่ต้องแก้ซ้ำ
        Assert.Equal(afterFix, Render("44 Moo 9, Nong Hiang, Phanat Nikhom, Chonburi 20140"));
    }
}
