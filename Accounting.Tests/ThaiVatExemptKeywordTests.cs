using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำยกเว้น §81 ที่สั้น 2 พยางค์ ("นม" · "ผัก") เคยถูกจับด้วย <c>Contains</c> ตรง ๆ
/// ⇒ "ขนมปัง" · "หนังสือค้ำประกัน" · "milk tea" ถูกตีเป็นยกเว้น แล้ว VAT ของบรรทัดนั้น
/// หายไปจากรายงานภาษีซื้อ §87 (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · T2-07)
/// </summary>
public class ThaiVatExemptKeywordTests
{
    [Theory]
    [InlineData("นมสด 2 ลิตร")]
    [InlineData("ค่านมโรงเรียน")]
    [InlineData("ผักบุ้งจีน")]
    [InlineData("และผักสลัด")]
    [InlineData("ข้าวสารหอมมะลิ")]
    [InlineData("หนังสือเรียน ป.4")]
    [InlineData("fresh milk 1L")]
    [InlineData("ค่ารักษาพยาบาล")]
    public void รายการยกเว้นจริง_ต้องถูกตีเป็นยกเว้น(string desc)
        => Assert.True(ThaiVatTypeRule.LooksExempt(desc));

    [Theory]
    [InlineData("ขนมปังโฮลวีท")]          // มี "นม" อยู่กลางคำ
    [InlineData("ขนมเค้ก")]
    [InlineData("ชานมไข่มุก")]            // "นม" ไม่ได้อยู่ต้นคำ
    [InlineData("milk tea 16oz")]         // อยู่ในรายการยกเว้นซ้อน
    [InlineData("หนังสือค้ำประกันธนาคาร")]  // เอกสารการเงิน ไม่ใช่หนังสือ §81
    [InlineData("ค่าธรรมเนียมหนังสือรับรองบริษัท")]
    [InlineData("ผักดองญี่ปุ่น")]           // แปรรูปแล้ว = เสียภาษี
    [InlineData("ผลไม้กระป๋อง")]
    [InlineData("นมข้นหวาน")]
    [InlineData("ค่าบริการรายเดือน")]
    public void รายการที่ไม่ยกเว้น_ต้องไม่ถูกตีเป็นยกเว้น(string desc)
        => Assert.False(ThaiVatTypeRule.LooksExempt(desc));

    [Fact]
    public void คำว่างหรือ_null_ไม่ยกเว้น()
    {
        Assert.False(ThaiVatTypeRule.LooksExempt(null));
        Assert.False(ThaiVatTypeRule.LooksExempt("   "));
    }

    [Fact]
    public void ต้นคำหลังตัวเลขหรือเครื่องหมาย_ยังนับเป็นต้นคำ()
    {
        Assert.True(ThaiVatTypeRule.ContainsAtWordStart("2 นมสด", "นม"));
        Assert.True(ThaiVatTypeRule.ContainsAtWordStart("อาหาร/ผักสด", "ผัก"));
        Assert.False(ThaiVatTypeRule.ContainsAtWordStart("ขนมปัง", "นม"));
    }

    // ── รอบ 195 (P2): นมแปรรูปเสีย VAT — ขอบเขตนมสดยกเว้น §81 ยังคงเดิม (รอเจ้าของ/นักบัญชีตัดสิน) ──────────
    [Theory]
    [InlineData("นมผงเอนฟาโกร เอนฟินิทัส สูตร3 นมผง เด็ก นมเอนฟาโกร enfa Enfinitas ชนิดจืด 1425 กรัม:สูตร3")]   // กระดาษจริง Scommerce
    [InlineData("Enfagrow A+ milk powder 1650g")]
    [InlineData("Similac infant formula 850g")]
    [InlineData("นมถั่วเหลือง แลคตาซอย 300 มล.")]
    public void นมแปรรูป_ต้องไม่ถูกตีเป็นยกเว้น(string desc)
        => Assert.False(ThaiVatTypeRule.LooksExempt(desc));

    // ── รอบ 195 ฝ่ายค้าน P2: คำที่กว้างเกินถูกถอด — นม UHT กลับเป็นพฤติกรรมเดิม (ขอบเขตนมโคล้วน UHT รอเจ้าของ/นักบัญชี) ·
    //    formula เดี่ยวชนปุ๋ย ⇒ แคบเป็น infant formula ────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("นม UHT รสจืด 200 มล. x 36")]
    [InlineData("นมยูเอชที ไทย-เดนมาร์ค")]
    [InlineData("ปุ๋ย formula 15-15-15 50 กก.")]
    [InlineData("ปุ๋ยเคมี สูตร 15-15-15")]
    public void คำกว้างเกินถูกถอด_กลับเป็นพฤติกรรมเดิม_ยังเดาเป็นยกเว้น(string desc)
        => Assert.True(ThaiVatTypeRule.LooksExempt(desc));

    [Fact]
    public void infant_formulaยังไม่ยกเว้น_คำนี้ไม่อยู่ในรายการกว้าง()
    {
        Assert.DoesNotContain("formula", ThaiVatTypeRule.NotExemptDespiteKeyword);
        Assert.DoesNotContain("นม uht", ThaiVatTypeRule.NotExemptDespiteKeyword);
        Assert.False(ThaiVatTypeRule.LooksExempt("Similac infant formula 850g"));
    }

    [Theory]
    [InlineData("นมสด 2 ลิตร")]
    [InlineData("ค่านม")]
    [InlineData("ค่านมโรงเรียน")]
    [InlineData("fresh milk 1L")]
    public void ทิศตรงข้าม_นมสดยังเดาเป็นยกเว้นตามเดิม(string desc)
        => Assert.True(ThaiVatTypeRule.LooksExempt(desc));
}
