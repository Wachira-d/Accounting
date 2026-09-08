using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// Covers ExpenseCategoryResolver.Resolve — the Thailand-wide default
/// expense-categorisation rules that bootstrap OCR account suggestions
/// before a tenant has any history of its own. Pure static logic.
/// </summary>
public class ExpenseCategoryResolverTests
{
    [Fact]
    public void Resolves_fuel_from_ptt_vendor_brand()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "บริษัท ปตท. จำกัด (มหาชน)",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.Equal("ค่าน้ำมันเชื้อเพลิง", result!.Category);
        Assert.Equal("5402", result.AccountCode);
    }

    [Fact]
    public void Resolves_electricity_from_keyword_in_raw_text()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: null,
            lineDescriptions: null,
            rawText: "ใบแจ้งค่าไฟฟ้า การไฟฟ้านครหลวง");

        Assert.NotNull(result);
        Assert.Equal("5303", result!.AccountCode);
    }

    [Fact]
    public void Resolves_repair_with_statutory_withholding_tax()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: "ค่าซ่อมแซมเครื่องปรับอากาศ",
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.Equal("5306", result!.AccountCode);
        Assert.Equal(3m, result.StatutoryWhtRate);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: null,
            headerDescription: "อออ",
            lineDescriptions: null,
            rawText: "บบบ");

        Assert.Null(result);
    }

    // ═══ T1-16: แบรนด์ค้าส่งบังคับผัง "ต้นทุนสินค้า" โดยไม่ดูว่าบริษัทเราขายอะไร ═══
    //
    // เดิม: ชื่อผู้ขายเป็นแบรนด์ค้าส่ง = +4 คะแนน ⇒ ชนะทุกอย่างเสมอ ⇒ บริษัท
    // ซอฟต์แวร์ที่ซื้อกาแฟ/กระดาษ A4 ที่ Makro ได้ผัง 51110 ต้นทุนสินค้าทุกใบ
    // ⇒ กำไรขั้นต้นในงบเพี้ยน

    [Fact]
    public void บริษัทซอฟต์แวร์ซื้อกระดาษที่_Makro_ต้องได้ค่าวัสดุสำนักงานไม่ใช่ต้นทุนสินค้า()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "สยามแม็คโคร จำกัด (มหาชน) makro",
            headerDescription: null,
            lineDescriptions: new[] { "กระดาษถ่ายเอกสาร A4 80 แกรม", "ปากกาลูกลื่น" },
            rawText: null,
            industry: Accounting.Models.Enums.IndustryType.Technology);

        Assert.NotNull(result);
        Assert.NotEqual("51110", result!.AccountCode);
    }

    [Fact]
    public void บริษัทค้าขายซื้อของที่_Makro_ต้องยังได้ต้นทุนสินค้าเหมือนเดิม()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "สยามแม็คโคร จำกัด (มหาชน) makro",
            headerDescription: null,
            lineDescriptions: new[] { "กระดาษถ่ายเอกสาร A4 80 แกรม", "ปากกาลูกลื่น" },
            rawText: null,
            industry: Accounting.Models.Enums.IndustryType.Trading);

        Assert.NotNull(result);
        Assert.Equal("51110", result!.AccountCode);
    }

    [Fact]
    public void บิลค้าส่งที่อ่านรายการไม่ได้เลย_ยังต้องตกที่ต้นทุนสินค้าแบบมั่นใจต่ำ()
    {
        // เจตนาเดิมของ rule นี้ (กันบิล Makro ที่อ่านรายการไม่ได้แล้วแพ้ keyword
        // หลง ๆ ท้ายบิล) ต้องไม่หายไป — แค่ confidence ต่ำลงจนหน้า review
        // ไฮไลต์เหลืองตามกฎเหล็ก #3
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "makro",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null,
            industry: Accounting.Models.Enums.IndustryType.Technology);

        Assert.NotNull(result);
        Assert.Equal("51110", result!.AccountCode);
        Assert.True(result.Confidence < 0.85m);
    }

    [Fact]
    public void ไม่รู้ประเภทกิจการ_ต้องไม่ลงโทษ_rule_ใด()
    {
        // "ยังไม่ได้ตั้งค่า" ≠ "รู้ว่าไม่ถือสต๊อก" — ห้ามเปลี่ยนพฤติกรรมเดิม
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "makro",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null,
            industry: null);

        Assert.NotNull(result);
        Assert.Equal("51110", result!.AccountCode);
    }

    [Fact]
    public void Confidence_is_bounded_between_zero_and_one()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "ปตท.",
            headerDescription: null,
            lineDescriptions: null,
            rawText: null);

        Assert.NotNull(result);
        Assert.InRange(result!.Confidence, 0m, 1m);
    }

    // ─────────────────────────────────────────────────────────────────────
    // แถวที่แบบฟอร์มพิมพ์ไว้ยอด 0 ห้ามตัดสินประเภทเงินได้ ม.40
    // (ใบจริง TXE05202609T000434 — Scommerce: แถวที่มีเงิน 2,137.38 อ่านคำอธิบาย
    //  ไม่ออก "0" · แถวที่อ่านออกคือ "ค่าจัดส่ง / Shipping Fee 0.00")
    // ─────────────────────────────────────────────────────────────────────

    private const string ScommerceRawText =
        "ใบกำกับภาษี TXE05202609T000434 Scommerce (Thailand) Co., Ltd.\n"
        + "1 0 2 1,068.69 2,137.38\n"
        + "2 ค่าจัดส่ง / Shipping Fee 1 0.00 0.00\n"
        + "VAT 140.46 ยอดรวมทั้งสิ้น 2,147";

    [Fact]
    public void แถวค่าจัดส่งยอดศูนย์_ไม่เสนอประเภทเงินได้และอัตราหัก()
    {
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "Scommerce (Thailand) Co., Ltd.",
            headerDescription: null,
            lineDescriptions: new[] { "0" },                          // แถวที่มีเงินจริง
            rawText: ScommerceRawText,
            zeroAmountLineDescriptions: new[] { "ค่าจัดส่ง / Shipping Fee" });

        // ยังเสนอหมวดได้ (ไม่ปิดด่านทิ้ง) แต่ต้องไม่ลากไปถึงประเภทเงินได้
        Assert.NotNull(result);
        Assert.False(result!.MoneyBackedEvidence);

        var data = new OcrExtractedData();
        ExpenseCategoryResolver.ApplyTo(data, result, ScommerceRawText);
        Assert.Null(data.WhtIncomeTypeCode);
        Assert.Null(data.SuggestedWhtRate);
        Assert.False(data.HasWht);
    }

    [Fact]
    public void ค่าขนส่งบนแถวที่มีเงินจริง_ยังเสนอ_40_8_ขนส่ง_1_เปอร์เซ็นต์()
    {
        // ทิศตรงข้าม — ถ้าไม่ล็อกไว้ การแก้ข้างบนอาจกลายเป็น "ปิดด่านทิ้ง"
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "บริษัท ขนส่งด่วน จำกัด",
            headerDescription: null,
            lineDescriptions: new[] { "ค่าขนส่งสินค้า / Shipping Fee กรุงเทพ-ชลบุรี" },
            rawText: "ค่าขนส่งสินค้า / Shipping Fee 10,000 บาท");

        Assert.NotNull(result);
        Assert.True(result!.MoneyBackedEvidence);
        Assert.Equal(1m, result.StatutoryWhtRate);

        var data = new OcrExtractedData();
        ExpenseCategoryResolver.ApplyTo(data, result, "ค่าขนส่งสินค้า / Shipping Fee 10,000 บาท");
        Assert.Equal("8tr", data.WhtIncomeTypeCode);
        Assert.Equal(1m, data.SuggestedWhtRate);
    }

    [Fact]
    public void ชื่อผู้ขายเป็นแบรนด์ขนส่ง_นับเป็นหลักฐานที่ผูกกับเงิน()
    {
        // ชื่อผู้ขายผูกกับ "ใครรับเงิน" เสมอ จึงเป็นหลักฐานเต็มแม้บรรทัดอ่านไม่ออก
        var result = ExpenseCategoryResolver.Resolve(
            vendorName: "บริษัท เคอรี่ เอ็กซ์เพรส (ประเทศไทย) จำกัด",
            headerDescription: null,
            lineDescriptions: new[] { "0" },
            rawText: "ใบกำกับภาษี");

        Assert.NotNull(result);
        Assert.True(result!.MoneyBackedEvidence);
        Assert.Equal("8tr", result.WhtIncomeTypeCode);
    }
}
