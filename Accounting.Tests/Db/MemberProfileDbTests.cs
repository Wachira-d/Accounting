using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 216 (คำตัดสินข้อ 174) — ข้อมูลส่วนตัวของสมาชิกหน้าร้านบน PostgreSQL จริง ผ่าน<b>บริการจริง</b> (<see cref="StorefrontCustomerService"/>):
/// แก้โปรไฟล์ → อ่านกลับ (echo ครบ · ที่อยู่หลักชนิดละแถว) · บันทึกจากฟอร์มจอง/สั่งซื้อ · เว็บอื่น/บริษัทอื่นแตะโปรไฟล์ไม่ได้ ·
/// ผู้ติดต่อ ERP ที่ผูกไว้ไม่ถูกเขียนทับ · ร่องรอยลง audit chain (ชื่อช่อง ไม่ใช่ค่า)
/// </summary>
[Trait("Category", "Db")]
public class MemberProfileDbTests
{
    private const string ValidTaxId = "0105556000009";
    private readonly ITestOutputHelper _out;
    public MemberProfileDbTests(ITestOutputHelper output) => _out = output;

    private static StorefrontCustomerService Svc(AccountingDbContext db) => new(db, null!, null!);

    private static async Task<(Guid CompanyId, Guid SiteA, Guid SiteB, Guid CustomerId, Guid ContactId)> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "ร้านทดสอบโปรไฟล์สมาชิก", TaxId = "0105556000009" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var siteA = new Site { CompanyId = company.Id, Name = "เว็บ A", Slug = "pa-" + tag, Subdomain = "pa-" + tag, Status = SiteStatus.Published };
        var siteB = new Site { CompanyId = company.Id, Name = "เว็บ B", Slug = "pb-" + tag, Subdomain = "pb-" + tag, Status = SiteStatus.Published };
        db.Sites.AddRange(siteA, siteB);
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าในระบบบัญชี", TaxId = "3100100123451", BranchCode = "00000" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var cust = new SiteCustomer
        {
            CompanyId = company.Id, SiteId = siteA.Id, Email = $"a-{tag}@shop.test", FullName = "a", ContactId = contact.Id,
            ConsentGiven = true, IsActive = true,
        };
        db.SiteCustomers.Add(cust);
        await db.SaveChangesAsync();
        return (company.Id, siteA.Id, siteB.Id, cust.Id, contact.Id);
    }

    [Fact]
    public async Task UpdateProfile_RoundTrip_EchoesAllFields_AndAudits()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, siteA, _, custId, contactId) = await SeedAsync(db);

        var me = await Svc(db).UpdateProfileAsync(cid, siteA, custId, new UpdateStorefrontProfileRequest
        {
            FullName = "สมชาย ใจดี", Phone = "081-234-5678", CompanyName = "บจก. ทดสอบ", TaxId = ValidTaxId,
            ShippingAddress = new StorefrontAddressDto { RecipientName = "สมชาย", AddressLine = "99/1 ถ.สุขุมวิท", SubDistrict = "คลองเตย", Province = "กรุงเทพมหานคร", PostalCode = "10110" },
            BillingAddress = new StorefrontAddressDto { AddressLine = "1 ถ.พระราม 9", Province = "กรุงเทพมหานคร", PostalCode = "10310" },
        });
        Assert.NotNull(me);
        db.ChangeTracker.Clear();
        var back = await Svc(db).GetMeAsync(cid, siteA, custId);
        Assert.Equal("สมชาย ใจดี", back!.FullName);
        Assert.Equal("0812345678", back.Phone);
        Assert.Equal(ValidTaxId, back.TaxId);
        Assert.Equal("00000", back.BranchCode);
        Assert.Equal("99/1 ถ.สุขุมวิท คลองเตย", back.ShippingAddressText);
        Assert.Equal("10110", back.ShippingAddress!.PostalCode);
        Assert.Equal("1 ถ.พระราม 9 กรุงเทพมหานคร 10310", back.BillingAddressText);
        Assert.Equal(2, await db.SiteCustomerAddresses.CountAsync(a => a.CompanyId == cid && a.CustomerId == custId));

        // แก้อีกรอบ: ที่อยู่หลักแถวเดิมถูกแก้ (ไม่งอกแถวใหม่) · ล้างที่อยู่ใบกำกับ = ลบแถวหลักของชนิดนั้น
        await Svc(db).UpdateProfileAsync(cid, siteA, custId, new UpdateStorefrontProfileRequest
        {
            FullName = "สมชาย ใจดี", Phone = "0812345678",
            ShippingAddress = new StorefrontAddressDto { AddressLine = "5 หมู่ 2", Province = "เชียงใหม่", PostalCode = "50000" },
        });
        db.ChangeTracker.Clear();
        var rows = await db.SiteCustomerAddresses.AsNoTracking().Where(a => a.CompanyId == cid && a.CustomerId == custId).ToListAsync();
        var ship = Assert.Single(rows);
        Assert.Equal(SiteAddressType.Shipping, ship.AddressType);
        Assert.Equal("เชียงใหม่", ship.Province);

        // ผู้ติดต่อ ERP ไม่ถูกเขียนทับ (ข้อ 174 ข)
        var contact = await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == contactId && c.CompanyId == cid);
        Assert.Equal("ลูกค้าในระบบบัญชี", contact.Name);
        Assert.Equal("3100100123451", contact.TaxId);

        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid && a.EntityType == nameof(SiteCustomer) && a.EntityId == custId.ToString()).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.DoesNotContain("0812345678", a.NewValues));   // ชื่อช่องเท่านั้น ไม่ใช่ค่า
    }

    [Fact]
    public async Task UpdateProfile_InvalidValues_Throw_NothingSaved()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, siteA, _, custId, _) = await SeedAsync(db);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UpdateProfileAsync(cid, siteA, custId,
            new UpdateStorefrontProfileRequest { FullName = "x", Phone = "12", TaxId = "0105556000002" }));
        Assert.Equal(StorefrontMemberProfile.RuleCode, ex.RuleCode);
        db.ChangeTracker.Clear();
        Assert.Equal("a", (await db.SiteCustomers.AsNoTracking().SingleAsync(c => c.Id == custId && c.CompanyId == cid)).FullName);
    }

    [Fact]
    public async Task CrossSite_And_CrossTenant_CannotReadOrWrite()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, siteA, siteB, custId, _) = await SeedAsync(db);
        var req = new UpdateStorefrontProfileRequest { FullName = "ผู้บุกรุก", Phone = "0899999999" };

        Assert.Null(await Svc(db).UpdateProfileAsync(cid, siteB, custId, req));            // เว็บอื่นของบริษัทเดียวกัน
        Assert.Null(await Svc(db).UpdateProfileAsync(Guid.NewGuid(), siteA, custId, req)); // บริษัทอื่น
        Assert.Null(await Svc(db).SaveFromCheckoutAsync(cid, siteB, custId, new CheckoutProfileInput("ผู้บุกรุก", "0899999999")));
        Assert.Null(await Svc(db).GetMeAsync(Guid.NewGuid(), siteA, custId));
        db.ChangeTracker.Clear();
        var c = await db.SiteCustomers.AsNoTracking().SingleAsync(x => x.Id == custId && x.CompanyId == cid);
        Assert.Equal("a", c.FullName);
        Assert.Null(c.Phone);
    }

    [Fact]
    public async Task SaveFromCheckout_FillsEmpty_ReportsSkipped_LeavesValidUntouched()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, siteA, _, custId, _) = await SeedAsync(db);

        // เช็กเอาต์ (ฝ่ายค้าน S2): ชื่อ/เบอร์บนฟอร์ม = ผู้รับ — ไม่แตะชื่อ/เบอร์ของบัญชี
        var note = await Svc(db).SaveFromCheckoutAsync(cid, siteA, custId, new CheckoutProfileInput("คุณแม่", "0899999999",
            ShippingAddressText: "99/1 ถ.สุขุมวิท", ShippingProvince: "กรุงเทพมหานคร", ShippingPostalCode: "10110", IdentityFromForm: false));
        Assert.Contains("บันทึกลงบัญชีของคุณแล้ว", note);
        db.ChangeTracker.Clear();
        var me = await Svc(db).GetMeAsync(cid, siteA, custId);
        Assert.Equal("a", me!.FullName);
        Assert.Null(me.Phone);
        Assert.Equal("99/1 ถ.สุขุมวิท", me.ShippingAddressText);
        Assert.Equal("คุณแม่", me.ShippingAddress!.RecipientName);

        // ฟอร์มจอง: บัญชียังไม่มีเบอร์ ⇒ เติม · ส่งซ้ำค่าเดิม ⇒ ไม่มีข้อความ (ไม่มี "บันทึกแล้ว" ปลอม)
        Assert.NotNull(await Svc(db).SaveFromCheckoutAsync(cid, siteA, custId, new CheckoutProfileInput(null, "0812345678")));
        db.ChangeTracker.Clear();
        Assert.Null(await Svc(db).SaveFromCheckoutAsync(cid, siteA, custId, new CheckoutProfileInput(null, "081-234-5678")));

        var bad = await Svc(db).SaveFromCheckoutAsync(cid, siteA, custId, new CheckoutProfileInput(null, "12", BillingTaxId: "0105556000002"));
        Assert.NotNull(bad);
        Assert.Contains("ไม่ได้บันทึก", bad);
        db.ChangeTracker.Clear();
        var after = await Svc(db).GetMeAsync(cid, siteA, custId);
        Assert.Equal("0812345678", after!.Phone);   // ค่าที่ถูกอยู่แล้วไม่ถูกแตะ
        Assert.Null(after.TaxId);
    }

    [Fact]
    public async Task BothRow_And_DuplicateDefaults_AreNormalizedOnSave()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, siteA, _, custId, _) = await SeedAsync(db);
        // ข้อมูลเดิม: แถว Both หลัก (มีคอลัมน์โครง) + แถว Shipping หลักเก่าซ้ำ
        db.SiteCustomerAddresses.Add(new SiteCustomerAddress
        {
            CompanyId = cid, CustomerId = custId, AddressType = SiteAddressType.Both, IsDefault = true,
            AddressLine = "1 ถ.เดิม", BuildingNumber = "1", StreetName = "ถ.เดิม", Province = "กรุงเทพมหานคร", PostalCode = "10100",
            CreatedAt = DateTime.UtcNow,
        });
        db.SiteCustomerAddresses.Add(new SiteCustomerAddress
        {
            CompanyId = cid, CustomerId = custId, AddressType = SiteAddressType.Shipping, IsDefault = true,
            AddressLine = "เก่า", CreatedAt = DateTime.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var before = await Svc(db).GetMeAsync(cid, siteA, custId);
        Assert.Equal("1 ถ.เดิม กรุงเทพมหานคร 10100", before!.BillingAddressText);   // ใบกำกับอ่านจากแถว Both ได้

        await Svc(db).UpdateProfileAsync(cid, siteA, custId, new UpdateStorefrontProfileRequest
        {
            FullName = "a",
            ShippingAddress = new StorefrontAddressDto { AddressLine = "9 ถ.ใหม่", Province = "เชียงใหม่", PostalCode = "50000" },
            BillingAddress = new StorefrontAddressDto { AddressLine = "1 ถ.เดิม", Province = "กรุงเทพมหานคร", PostalCode = "10100" },
        });
        db.ChangeTracker.Clear();
        var rows = await db.SiteCustomerAddresses.AsNoTracking().Where(a => a.CompanyId == cid && a.CustomerId == custId).ToListAsync();
        Assert.Single(rows, a => a.IsDefault && a.AddressType == SiteAddressType.Shipping);   // แถวหลักต่อชนิดแถวเดียว
        Assert.Single(rows, a => a.IsDefault && a.AddressType == SiteAddressType.Billing);
        var ship = rows.Single(a => a.IsDefault && a.AddressType == SiteAddressType.Shipping);
        Assert.Equal("9 ถ.ใหม่", ship.AddressLine);
        Assert.Null(ship.StreetName);   // บรรทัดที่อยู่เปลี่ยน ⇒ คอลัมน์โครงเดิมถูกล้าง
        var me = await Svc(db).GetMeAsync(cid, siteA, custId);
        Assert.Equal("1 ถ.เดิม กรุงเทพมหานคร 10100", me!.BillingAddressText);   // ที่อยู่ใบกำกับไม่หายเมื่อแยกแถว Both
    }
}
