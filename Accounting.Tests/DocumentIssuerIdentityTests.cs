using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ชื่อทางการค้าบนหัวเอกสาร — ด่านกฎหมาย + resolver ที่ renderer ทั้งสองตัวใช้ร่วม
///
/// สองสิ่งที่เทสต์ชุดนี้ล็อกไว้:
/// 1. <b>ใบกำกับภาษีต้องขึ้นชื่อนิติบุคคลเสมอ</b> (ป.รัษฎากร §86/4(2) "ชื่อ...ของ
///    ผู้ประกอบการจดทะเบียน") — ชื่อร้านขึ้นแทนไม่ได้ ไม่ว่าจะตั้งค่าอย่างไร
/// 2. <b>ถึงชื่อร้านขึ้นหัวได้ ก็ต้องมีบรรทัดนิติบุคคลตัวเล็กเสมอ</b> — ปิดไม่ได้
///    (เอกสารที่ไม่บอกว่าใครเป็นคู่สัญญา ใช้เป็นหลักฐานลงบัญชีไม่ได้)
/// </summary>
public class DocumentIssuerIdentityTests
{
    private const string CoName = "บริษัท ตัวอย่าง จำกัด";
    private const string CoNameEn = "Example Co., Ltd.";
    private const string CoTax = "0105512345678";

    private static DocumentBrandView Brand(string? placement = "Footer", bool active = true) =>
        new("บ้านสวนคาเฟ่", "Baan Suan Cafe", "กาแฟดี ทุกวัน", "Good coffee, every day",
            LogoPath: "/uploads/brand.png", LogoUrl: "/uploads/brand.png",
            Address: "99 ถนนสุขุมวิท กรุงเทพฯ", AddressEn: "99 Sukhumvit Rd, Bangkok",
            Phone: "02-111-2222", Email: "hello@baansuan.co", Website: "baansuan.co",
            PrimaryColor: "#0F766E", LegalNamePlacement: placement, IsActive: active);

    private static IssuerIdentity Resolve(
        DocumentType type, string? title = null, bool isEn = false,
        DocumentBrandView? brand = null) =>
        DocumentIssuerIdentity.Resolve(
            type, title, isEn, CoName, CoNameEn, CoTax, "สำนักงานใหญ่",
            "1 ถนนพระราม 4 กรุงเทพฯ", "02-999-8888", "acc@example.co",
            "/uploads/company.png", "/uploads/company.png", "#1F2937", brand);

    // ── ด่านกฎหมาย ────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.BillingNote)]
    [InlineData(DocumentType.DeliveryNote)]
    [InlineData(DocumentType.PurchaseOrder)]
    [InlineData(DocumentType.PurchaseRequisition)]
    public void เอกสารที่ไม่ใช่หลักฐานภาษี_ชื่อทางการค้าขึ้นหัวได้(DocumentType type)
    {
        Assert.True(DocumentIssuerIdentity.CanBrandBePrimary(type));
        Assert.Equal("บ้านสวนคาเฟ่", Resolve(type, brand: Brand()).PrimaryName);
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    [InlineData(DocumentType.DebitNote)]
    [InlineData(DocumentType.CreditNote)]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    [InlineData(DocumentType.GoodsReceiptNote)]
    public void เอกสารภาษี_ต้องขึ้นชื่อนิติบุคคลเสมอ(DocumentType type)
    {
        Assert.False(DocumentIssuerIdentity.CanBrandBePrimary(type));
        var id = Resolve(type, brand: Brand());
        Assert.Equal(CoName, id.PrimaryName);
        Assert.False(id.BrandIsPrimary);
    }

    [Theory]
    [InlineData("ใบแจ้งหนี้/ใบกำกับภาษี")]
    [InlineData("ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน")]
    [InlineData("Invoice / Tax Invoice")]
    [InlineData("ใบกำกับภาษีอย่างย่อ")]
    public void ใบแจ้งหนี้ที่หัวมีคำว่าใบกำกับภาษี_ต้องกลับไปใช้ชื่อนิติบุคคล(string title)
    {
        // ชนิดเป็น Invoice (อยู่ในลิสต์ที่อนุญาต) แต่กระดาษใบนี้ทำหน้าที่เป็น
        // ใบกำกับภาษี → §86/4 บังคับ. เช็คที่ "หัวจริง" จึงแม่นกว่าเช็คที่ enum
        Assert.False(DocumentIssuerIdentity.CanBrandBePrimary(DocumentType.Invoice, title));
        Assert.Equal(CoName, Resolve(DocumentType.Invoice, title, brand: Brand()).PrimaryName);
    }

    [Fact]
    public void ใบแจ้งหนี้ธรรมดา_ชื่อร้านขึ้นหัวได้()
    {
        Assert.Equal("บ้านสวนคาเฟ่",
            Resolve(DocumentType.Invoice, "ใบแจ้งหนี้", brand: Brand()).PrimaryName);
    }

    // ── ชื่อนิติบุคคลตัวเล็ก ปิดไม่ได้ ─────────────────────────────

    [Theory]
    [InlineData("Footer")]
    [InlineData("Header")]
    [InlineData("Both")]
    [InlineData("ค่ามั่ว")]
    [InlineData(null)]
    public void แบรนด์ขึ้นหัว_ต้องมีบรรทัดนิติบุคคลเสมอ(string? placement)
    {
        var id = Resolve(DocumentType.Quotation, brand: Brand(placement));
        Assert.True(id.BrandIsPrimary);
        Assert.False(string.IsNullOrWhiteSpace(id.LegalLine));
        Assert.Contains(CoName, id.LegalLine!);
        Assert.Contains(CoTax, id.LegalLine!);
        // ต้องพิมพ์อย่างน้อยหนึ่งที่ — ไม่มีทางตั้งให้หายทั้งคู่
        Assert.True(id.LegalLineInHeader || id.LegalLineInFooter);
    }

    [Fact]
    public void ตั้ง_Both_พิมพ์ทั้งหัวและท้าย()
    {
        var id = Resolve(DocumentType.Quotation, brand: Brand("Both"));
        Assert.True(id.LegalLineInHeader);
        Assert.True(id.LegalLineInFooter);
    }

    [Fact]
    public void ตั้ง_Header_พิมพ์เฉพาะหัว()
    {
        var id = Resolve(DocumentType.Quotation, brand: Brand("Header"));
        Assert.True(id.LegalLineInHeader);
        Assert.False(id.LegalLineInFooter);
    }

    // ── fallback / พฤติกรรมเดิมต้องไม่เปลี่ยน ──────────────────────

    [Fact]
    public void ไม่มีแบรนด์_ผลลัพธ์เหมือนก่อนมีฟีเจอร์นี้()
    {
        var id = Resolve(DocumentType.Quotation);
        Assert.Equal(CoName, id.PrimaryName);
        Assert.Equal(CoNameEn, id.SecondaryName);
        Assert.Equal("/uploads/company.png", id.LogoPath);
        Assert.Equal("1 ถนนพระราม 4 กรุงเทพฯ", id.Address);
        Assert.Null(id.LegalLine);
        Assert.False(id.BrandIsPrimary);
    }

    [Fact]
    public void โหมดอังกฤษไม่มีแบรนด์_ใช้ชื่ออังกฤษของบริษัท()
    {
        Assert.Equal(CoNameEn, Resolve(DocumentType.Quotation, isEn: true).PrimaryName);
    }

    [Fact]
    public void แบรนด์ปิดใช้งาน_ถอยไปใช้ชื่อบริษัท()
    {
        var id = Resolve(DocumentType.Quotation, brand: Brand(active: false));
        Assert.Equal(CoName, id.PrimaryName);
        Assert.False(id.BrandIsPrimary);
    }

    [Fact]
    public void แบรนด์ขึ้นหัวโหมดอังกฤษ_ใช้ชื่ออังกฤษของแบรนด์()
    {
        var id = Resolve(DocumentType.Quotation, isEn: true, brand: Brand());
        Assert.Equal("Baan Suan Cafe", id.PrimaryName);
        Assert.Contains("Operated by", id.LegalLine!);
        Assert.Contains("Tax ID", id.LegalLine!);
    }

    [Fact]
    public void ใบกำกับภาษียังได้โลโก้_สี_ที่อยู่หน้าร้านของแบรนด์()
    {
        // สิ่งที่กฎหมายคุมคือ "ชื่อ" ไม่ใช่ภาพ — ใบยังดูเป็นแบรนด์ได้
        var id = Resolve(DocumentType.TaxInvoice, brand: Brand());
        Assert.Equal("/uploads/brand.png", id.LogoPath);
        Assert.Equal("#0F766E", id.PrimaryColor);
        Assert.Equal("99 ถนนสุขุมวิท กรุงเทพฯ", id.Address);
        Assert.Equal("02-111-2222", id.Phone);
        // ชื่อร้านลงเป็นบรรทัดรอง ไม่หายไปเฉย ๆ
        Assert.Equal("บ้านสวนคาเฟ่", id.SecondaryName);
    }

    [Fact]
    public void ช่องที่แบรนด์ไม่ได้กรอก_ถอยไปใช้ของบริษัท()
    {
        var sparse = new DocumentBrandView("ร้านเล็ก");   // ไม่กรอกอะไรเลยนอกจากชื่อ
        var id = Resolve(DocumentType.Quotation, brand: sparse);
        Assert.Equal("ร้านเล็ก", id.PrimaryName);
        Assert.Equal("/uploads/company.png", id.LogoPath);
        Assert.Equal("1 ถนนพระราม 4 กรุงเทพฯ", id.Address);
        Assert.Equal("02-999-8888", id.Phone);
        Assert.Equal("#1F2937", id.PrimaryColor);
    }
}
