using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 216 (คำตัดสินข้อ 174) — ข้อมูลส่วนตัวของสมาชิกหน้าร้าน: ตัวตรวจโปรไฟล์ (<see cref="StorefrontMemberProfile.Validate"/>) ·
/// ตาข่ายเติมชื่อ/เบอร์/อีเมลฝั่งเซิร์ฟเวอร์ (<see cref="StorefrontMemberProfile.FillContact"/>) · "บันทึกเป็นข้อมูลของฉัน"
/// (<see cref="StorefrontMemberProfile.MergeCheckout"/>) · เจ้าของโปรไฟล์ = โทเคนของเว็บนี้เท่านั้น (<see cref="StorefrontCustomerAuth.ProfileOwner"/>) ·
/// ล็อกทั้ง "เคสใหม่" และ "เคสเดิมไม่ถูกแตะ"
/// </summary>
public class CmsRound216MemberProfileTests
{
    private const string ValidTaxId = "0105556000009";      // ผ่าน mod-11
    private const string BadChecksumTaxId = "0105556000002"; // 13 หลักแต่หลักสุดท้ายผิด
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000216");
    private static readonly Guid SiteB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000216");
    private static readonly Guid CustA = Guid.Parse("cccccccc-0000-0000-0000-00000000216a");

    private static MemberProfile Profile(string? name = "สมชาย ใจดี", string? phone = "0812345678") =>
        new(name, phone, null, null, null, null, null);

    // ═════════ ตัวตรวจหน้า "ข้อมูลส่วนตัว" ═════════

    [Fact]
    public void Validate_NormalizesPhoneAndTaxId_DefaultsHeadOffice()
    {
        var (errs, clean) = StorefrontMemberProfile.Validate(new MemberProfile("  สมชาย ใจดี ", "081-234-5678", "บจก. ทดสอบ", "0-1055-56000-00-9", null,
            new MemberAddress(null, "+66 81 234 5678", "99/1 ถ.สุขุมวิท", "คลองเตย", "คลองเตย", "กรุงเทพมหานคร", "10110"), null));
        Assert.Empty(errs);
        Assert.Equal("สมชาย ใจดี", clean.FullName);
        Assert.Equal("0812345678", clean.Phone);
        Assert.Equal(ValidTaxId, clean.TaxId);
        Assert.Equal(StorefrontMemberProfile.HeadOffice, clean.BranchCode);   // ไม่ใส่สาขา + มีเลขภาษี ⇒ สำนักงานใหญ่
        Assert.Equal("0812345678", clean.Shipping!.Phone);                    // +66 ⇒ 0
        Assert.Null(clean.Billing);
    }

    [Fact]
    public void Validate_RejectsBadValues_AllInOneMessage()
    {
        var (errs, _) = StorefrontMemberProfile.Validate(new MemberProfile(" ", "12345", null, BadChecksumTaxId, "12",
            new MemberAddress(null, null, null, null, null, "เชียงใหม่", "5020"), null));
        Assert.Contains(errs, e => e.Contains("ชื่อ"));
        Assert.Contains(errs, e => e.Contains("เบอร์โทรไม่ถูกต้อง"));
        Assert.Contains(errs, e => e.Contains("เลขประจำตัวผู้เสียภาษีไม่ถูกต้อง"));
        Assert.Contains(errs, e => e.Contains("รหัสสาขา"));
        Assert.Contains(errs, e => e.Contains("รหัสไปรษณีย์"));
        Assert.Contains(errs, e => e.Contains("บ้านเลขที่"));   // มีจังหวัดแต่ไม่มีบรรทัดที่อยู่ = ส่งของไม่ได้
    }

    [Fact]
    public void Validate_EmptyOptionalFields_ClearValues_NotErrors()
    {
        // ทิศตรงข้าม: ช่องไม่บังคับที่ว่าง = ล้างค่า (ไม่ใช่ error) · ที่อยู่ว่างทั้งชุด = ไม่มีที่อยู่
        var (errs, clean) = StorefrontMemberProfile.Validate(new MemberProfile("ก ข", "", "", "", "",
            new MemberAddress("", "", "", "", "", "", ""), null));
        Assert.Empty(errs);
        Assert.Null(clean.Phone);
        Assert.Null(clean.TaxId);
        Assert.Null(clean.BranchCode);
        Assert.Null(clean.Shipping);
    }

    [Fact]
    public void Validate_BranchWithoutTaxId_IsRejected()
    {
        var (errs, _) = StorefrontMemberProfile.Validate(new MemberProfile("ก ข", null, null, null, "00001", null, null));
        Assert.Contains(errs, e => e.Contains("เลขประจำตัวผู้เสียภาษีด้วย"));
    }

    [Fact]
    public void ComposeAddress_CheckoutVsFull()
    {
        var a = new MemberAddress("ผู้รับ", null, "99/1 ถ.สุขุมวิท", "คลองเตย", "คลองเตย", "กรุงเทพมหานคร", "10110");
        Assert.Equal("99/1 ถ.สุขุมวิท คลองเตย คลองเตย", StorefrontMemberProfile.ComposeAddress(a, withProvince: false));
        Assert.Equal("99/1 ถ.สุขุมวิท คลองเตย คลองเตย กรุงเทพมหานคร 10110", StorefrontMemberProfile.ComposeAddress(a, withProvince: true));
        Assert.Null(StorefrontMemberProfile.ComposeAddress(null, true));
    }

    // ═════════ ตาข่ายฝั่งเซิร์ฟเวอร์: เติมช่องที่เว้นว่าง ═════════

    [Fact]
    public void FillContact_FillsBlanks_TypedValuesWin()
    {
        var (n, p, e) = StorefrontMemberProfile.FillContact("", null, "  ", "สมชาย", "0812345678", "a@shop.test");
        Assert.Equal(("สมชาย", "0812345678", "a@shop.test"), (n, p, e));
        // ค่าที่ผู้ใช้พิมพ์ชนะเสมอ (ทิศตรงข้าม)
        var typed = StorefrontMemberProfile.FillContact("คนอื่นจองแทน", "0899999999", "x@y.test", "สมชาย", "0812345678", "a@shop.test");
        Assert.Equal(("คนอื่นจองแทน", "0899999999", "x@y.test"), typed);
        // โปรไฟล์ไม่มีค่า ⇒ ไม่แต่งค่าขึ้นเอง
        var none = StorefrontMemberProfile.FillContact(null, null, null, null, " ", null);
        Assert.Null(none.Name);
        Assert.Null(none.Phone);
        Assert.Null(none.Email);
    }

    // ═════════ "บันทึกเป็นข้อมูลของฉัน" ═════════

    [Fact]
    public void MergeCheckout_FillsEmptyProfile_FromBookingForm()
    {
        var m = StorefrontMemberProfile.MergeCheckout(Profile(phone: null), new CheckoutProfileInput("สมชาย ใจดี", "081 234 5678"));
        Assert.Equal("0812345678", m.Next.Phone);
        Assert.Equal(new[] { "เบอร์โทร" }, m.Saved);
        Assert.Empty(m.Skipped);
        Assert.Null(StorefrontMemberProfile.SaveNote(m.Skipped));
    }

    [Fact]
    public void MergeCheckout_InvalidPhone_SkippedAndReported_NotSilent()
    {
        var m = StorefrontMemberProfile.MergeCheckout(Profile(), new CheckoutProfileInput(null, "12-34"));
        Assert.Equal("0812345678", m.Next.Phone);   // คงค่าเดิม
        Assert.Empty(m.Saved);
        Assert.Contains(m.Skipped, s => s.Contains("เบอร์โทร"));
        Assert.Contains("ไม่ได้บันทึก", StorefrontMemberProfile.SaveNote(m.Skipped));
    }

    [Fact]
    public void MergeCheckout_SameValues_NoChange()
    {
        var current = Profile() with { Shipping = new MemberAddress("สมชาย", null, "99/1 ถ.สุขุมวิท", "คลองเตย", null, "กรุงเทพมหานคร", "10110") };
        var m = StorefrontMemberProfile.MergeCheckout(current, new CheckoutProfileInput("สมชาย ใจดี", "0812345678",
            ShippingAddressText: "99/1  ถ.สุขุมวิท คลองเตย", ShippingProvince: "กรุงเทพมหานคร", ShippingPostalCode: "10110"));
        Assert.Empty(m.Saved);
        Assert.Equal(current, m.Next);   // ที่อยู่ที่มีโครง (ตำบล) ไม่ถูกยุบเป็นข้อความเมื่อข้อความตรงกับของเดิม
    }

    [Fact]
    public void MergeCheckout_NewShippingText_ReplacesStructuredAddress()
    {
        var current = Profile() with { Shipping = new MemberAddress("สมชาย", "0812345678", "99/1", "คลองเตย", "คลองเตย", "กรุงเทพมหานคร", "10110") };
        var m = StorefrontMemberProfile.MergeCheckout(current, new CheckoutProfileInput(null, null,
            ShippingAddressText: "5 หมู่ 2 ต.สันทราย อ.เมือง", ShippingProvince: "เชียงใหม่", ShippingPostalCode: "50000"));
        Assert.Contains("ที่อยู่จัดส่ง", m.Saved);
        Assert.Equal("5 หมู่ 2 ต.สันทราย อ.เมือง", m.Next.Shipping!.AddressLine);
        Assert.Null(m.Next.Shipping.SubDistrict);
        Assert.Equal("เชียงใหม่", m.Next.Shipping.Province);
        Assert.Equal("50000", m.Next.Shipping.PostalCode);
        Assert.Equal("สมชาย", m.Next.Shipping.RecipientName);
    }

    [Fact]
    public void MergeCheckout_TaxInvoice_OnlyWithValidChecksum()
    {
        var ok = StorefrontMemberProfile.MergeCheckout(Profile(), new CheckoutProfileInput(null, null,
            BillingName: "บจก. ทดสอบ", BillingTaxId: ValidTaxId, BillingBranchCode: null, BillingAddressText: "1 ถ.พระราม 9 กรุงเทพมหานคร 10310"));
        Assert.Equal(ValidTaxId, ok.Next.TaxId);
        Assert.Equal("00000", ok.Next.BranchCode);
        Assert.Equal("บจก. ทดสอบ", ok.Next.CompanyName);
        Assert.Equal("1 ถ.พระราม 9 กรุงเทพมหานคร 10310", ok.Next.Billing!.AddressLine);

        var bad = StorefrontMemberProfile.MergeCheckout(Profile(), new CheckoutProfileInput(null, null,
            BillingName: "บจก. ทดสอบ", BillingTaxId: BadChecksumTaxId));
        Assert.Null(bad.Next.TaxId);
        Assert.Null(bad.Next.CompanyName);   // ชื่อ/ที่อยู่ใบกำกับไปเป็นชุดกับเลขที่ผ่าน checksum เท่านั้น
        Assert.Contains(bad.Skipped, s => s.Contains("ใบกำกับ"));
    }

    // ═════════ เจ้าของโปรไฟล์ = โทเคนหน้าร้านของเว็บนี้เท่านั้น ═════════

    [Fact]
    public void ProfileOwner_OnlyTokenOfThisSite_AndOnlyWhenRequested()
    {
        var me = new StorefrontCustomerIdentity(CustA, SiteA);
        Assert.Equal(CustA, StorefrontCustomerAuth.ProfileOwner(true, me, SiteA));
        Assert.Null(StorefrontCustomerAuth.ProfileOwner(false, me, SiteA));   // ไม่ติ๊ก ⇒ ไม่เขียน
        Assert.Null(StorefrontCustomerAuth.ProfileOwner(true, me, SiteB));    // โทเคนเว็บอื่น ⇒ ไม่เขียน (cross-site)
        Assert.Null(StorefrontCustomerAuth.ProfileOwner(true, null, SiteA));  // แขกนิรนาม/พนักงาน ⇒ ไม่เขียน
    }
}
