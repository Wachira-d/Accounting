using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ที่อยู่บนหัวเอกสารมาจากไหน — ป.รัษฎากร §86/4 + ป.86/2542
///
/// ═══ สิ่งที่ผู้ใช้ขอ ═══
/// "ที่อยู่ในใบกำกับภาษีหรือเอกสารที่บังคับอื่น ๆ ต้องเป็นไปตามที่อยู่บริษัทหรือสาขา
/// ที่ตั้งค่าไว้แล้ว เลือกผูกกันไว้ได้ด้วย"
///
/// แยกเป็นสองกฎที่ห้ามปนกัน:
/// 1. <b>เอกสารที่กฎหมายบังคับ</b> — ที่อยู่ = สถานประกอบการที่ออกใบ **เสมอ**
///    (ที่อยู่หน้าร้านของแบรนด์ทับไม่ได้) → ล็อกด้วยเทสต์ใน DocumentIssuerIdentity
/// 2. <b>เอกสารที่แบรนด์ขึ้นหัวได้</b> — ที่อยู่ของแบรนด์ ซึ่ง "ผูก" กับทะเบียน
///    บริษัท/สาขาได้ เพื่อไม่ต้องพิมพ์ซ้ำแล้ว drift ตอนย้ายที่อยู่
/// </summary>
public class BrandAddressSourceTests
{
    private const string CoAddr = "144 หมู่ 5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110";
    private const string BranchAddr = "99/9 ถ.นิมมานเหมินท์ ต.สุเทพ อ.เมือง จ.เชียงใหม่ 50200";
    private const string ShopAddr = "เลขที่ 1 ตลาดนัดสวนจตุจักร";

    // ───────── โหมดของที่อยู่แบรนด์ ─────────

    [Fact]
    public void ผูกกับบริษัท_คืนค่าว่างเพื่อให้ผู้เรียกใช้ที่อยู่บริษัท()
    {
        // null = "ใช้ที่อยู่บริษัท" — แหล่งเดียว ย้ายออฟฟิศแก้ที่เดียวจบ
        Assert.Null(BrandAddressSource.Resolve("Company", ShopAddr, BranchAddr));
    }

    [Fact]
    public void ผูกกับสาขา_ใช้ที่อยู่ของสาขานั้น()
        => Assert.Equal(BranchAddr, BrandAddressSource.Resolve("Branch", ShopAddr, BranchAddr));

    [Fact]
    public void ผูกกับสาขาที่ยังไม่กรอกที่อยู่_ตกกลับไปใช้ที่อยู่บริษัท()
    {
        Assert.Null(BrandAddressSource.Resolve("Branch", ShopAddr, null));
        Assert.Null(BrandAddressSource.Resolve("Branch", ShopAddr, "   "));
    }

    [Fact]
    public void พิมพ์เอง_ใช้ที่อยู่ที่พิมพ์()
        => Assert.Equal(ShopAddr, BrandAddressSource.Resolve("Custom", ShopAddr, BranchAddr));

    [Fact]
    public void พิมพ์เองแต่เว้นว่าง_ตกกลับไปใช้ที่อยู่บริษัท()
        => Assert.Null(BrandAddressSource.Resolve("Custom", "  ", null));

    [Theory]
    [InlineData(null, "Custom")]
    [InlineData("", "Custom")]
    [InlineData("อะไรก็ไม่รู้", "Custom")]
    [InlineData("Company", "Company")]
    [InlineData("Branch", "Branch")]
    public void ค่านอกลิสต์ตกไปโหมดพิมพ์เองเสมอ_คือพฤติกรรมเดิมของแถวเก่า(string? raw, string expected)
        => Assert.Equal(expected, BrandAddressSource.Normalize(raw));

    // ───────── ด่านกฎหมาย: เอกสารที่บังคับ ห้ามใช้ที่อยู่หน้าร้าน ─────────

    private static DocumentBrandView Shop(string? addr) => new(
        Name: "มังกร ขนส่ง เครน", NameEn: null, Address: addr, AddressEn: addr);

    private static IssuerIdentity ResolveFor(DocumentType type, string? title, string? brandAddr)
        => DocumentIssuerIdentity.Resolve(
            type, title, isEnglish: false,
            companyName: "บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด",
            companyNameEn: null, companyTaxId: "0205565017741",
            branchLabel: "สำนักงานใหญ่",
            companyAddress: CoAddr,
            companyPhone: null, companyEmail: null,
            companyLogoPath: null, companyLogoUrl: null, companyPrimaryColor: null,
            brand: Shop(brandAddr));

    [Theory]
    [InlineData(DocumentType.TaxInvoice, "ใบกำกับภาษี")]
    [InlineData(DocumentType.Receipt, "ใบเสร็จรับเงิน")]
    [InlineData(DocumentType.CreditNote, "ใบลดหนี้")]
    [InlineData(DocumentType.DebitNote, "ใบเพิ่มหนี้")]
    public void เอกสารที่กฎหมายบังคับ_ใช้ที่อยู่สถานประกอบการเสมอ(DocumentType type, string title)
    {
        var id = ResolveFor(type, title, ShopAddr);
        Assert.Equal(CoAddr, id.Address);            // ที่อยู่หน้าร้านทับไม่ได้ (ป.86/2542)
        Assert.False(id.BrandIsPrimary);             // ชื่อนิติบุคคลเป็นตัวหลัก §86/4(2)
    }

    [Fact]
    public void ใบแจ้งหนี้ที่หัวเป็นใบกำกับภาษีด้วย_ยังถูกบังคับที่อยู่จดทะเบียน()
    {
        // หัวรวมสองหน้าที่ — ตัดสินจากหัวจริง ไม่ใช่แค่ชนิดเอกสาร
        var id = ResolveFor(DocumentType.Invoice, "ใบแจ้งหนี้/ใบกำกับภาษี", ShopAddr);
        Assert.Equal(CoAddr, id.Address);
    }

    [Fact]
    public void ใบเสนอราคา_ใช้ที่อยู่ของแบรนด์ได้()
    {
        var id = ResolveFor(DocumentType.Quotation, "ใบเสนอราคา", ShopAddr);
        Assert.Equal(ShopAddr, id.Address);
        Assert.True(id.BrandIsPrimary);
    }

    [Fact]
    public void ใบเสนอราคาที่แบรนด์ผูกที่อยู่บริษัท_ได้ที่อยู่บริษัท()
    {
        // ผู้เรียก (BuildIssuer) resolve เป็น null มาก่อน → Resolve ตกไปที่บริษัท
        var id = ResolveFor(DocumentType.Quotation, "ใบเสนอราคา", null);
        Assert.Equal(CoAddr, id.Address);
    }
}
