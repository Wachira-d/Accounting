using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม W1d P2-6 — คีย์ตัวเรียนรู้ที่เก็บเลขผู้เสียภาษีเป็นเลขไทย (<c>tax:๐๑๐๕…</c>) ต้องหาเจอด้วยคีย์ที่ผู้อ่านสร้างตอนนี้
/// (<see cref="VendorLearningKey.For"/> → <see cref="ThaiTaxId.Normalize"/> = ASCII) หลัง migration · round-trip: เขียนแบบเก่า → migrate → อ่านด้วยคีย์ใหม่
/// <para>ทิศตรงข้าม: คีย์ <c>name:</c> ที่มีเลขไทยในชื่อร้านไม่ถูกแตะ · มีแถว ASCII ของผู้ขายเดียวกันอยู่แล้ว ⇒ แถวเลขไทยคงเดิม (ไม่ชน unique · ไม่ลบความรู้) ·
/// รันซ้ำไม่เปลี่ยนอะไร</para>
/// </summary>
[Trait("Category", "Db")]
public class ThaiDigitLearnerKeyMigrationDbTests
{
    private readonly ITestOutputHelper _out;
    public ThaiDigitLearnerKeyMigrationDbTests(ITestOutputHelper output) => _out = output;

    private const string ThaiTax = "๐๑๐๕๕๖๑๐๑๒๓๔๖";   // = 0105561012346

    private static async Task MigrateAsync(AccountingDbContext db)
    {
        foreach (var sql in DatabaseMigrationHelper.ThaiDigitLearnerKeyStatements())
            await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task คีย์เลขไทย_migrateแล้ว_หาเจอด้วยคีย์ที่ผู้อ่านสร้างตอนนี้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "W1d คีย์เลขไทย", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        var readerKey = VendorLearningKey.For(ThaiTax, "ร้านเลขไทย");
        Assert.Equal("tax:0105561012346", readerKey);   // ผู้อ่านตอนนี้ = ASCII

        db.OcrVendorIntelligence.Add(new OcrVendorIntelligence { CompanyId = cid, VendorKey = "tax:" + ThaiTax, VendorTaxId = ThaiTax, TotalDocuments = 7 });
        db.OcrVendorIntelligence.Add(new OcrVendorIntelligence { CompanyId = cid, VendorKey = "name:ร้าน ๑๒", TotalDocuments = 2 });
        db.OcrCategoryMappings.Add(new OcrCategoryMapping { CompanyId = cid, VendorKey = "tax:" + ThaiTax, DescriptionKeyword = "น้ำมัน", AccountCode = "5402" });
        db.VendorKnownGoodValues.Add(new VendorKnownGoodValue { CompanyId = cid, VendorTaxId = ThaiTax, FieldName = "SellerName", Value = "ร้านเลขไทย" });
        await db.SaveChangesAsync();

        using var mig = DbTestDatabase.TryCreateContext()!;
        await MigrateAsync(mig);
        await MigrateAsync(mig);   // รันซ้ำทุกบูตได้

        using var check = DbTestDatabase.TryCreateContext()!;
        var intel = await check.OcrVendorIntelligence.AsNoTracking().SingleAsync(v => v.CompanyId == cid && v.VendorKey == readerKey);
        Assert.Equal(7, intel.TotalDocuments);
        Assert.Equal("0105561012346", intel.VendorTaxId);
        Assert.True(await check.OcrCategoryMappings.AnyAsync(m => m.CompanyId == cid && m.VendorKey == readerKey && m.DescriptionKeyword == "น้ำมัน"));
        Assert.True(await check.VendorKnownGoodValues.AnyAsync(v => v.CompanyId == cid && v.VendorTaxId == "0105561012346"));
        // ทิศตรงข้าม: คีย์ชื่อร้าน (มีเลขไทยในชื่อ) ไม่ถูกแตะ
        Assert.True(await check.OcrVendorIntelligence.AnyAsync(v => v.CompanyId == cid && v.VendorKey == "name:ร้าน ๑๒"));
    }

    [Fact]
    public async Task ทิศตรงข้าม_มีแถวASCIIของผู้ขายเดียวกันแล้ว_แถวเลขไทยคงเดิม_ไม่ชนunique()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "W1d คีย์ซ้ำ", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;
        db.OcrVendorIntelligence.Add(new OcrVendorIntelligence { CompanyId = cid, VendorKey = "tax:0105561012346", TotalDocuments = 30 });
        db.OcrVendorIntelligence.Add(new OcrVendorIntelligence { CompanyId = cid, VendorKey = "tax:" + ThaiTax, TotalDocuments = 1 });
        await db.SaveChangesAsync();

        using var mig = DbTestDatabase.TryCreateContext()!;
        await MigrateAsync(mig);   // ไม่ล้มด้วย unique (CompanyId, VendorKey)

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.OcrVendorIntelligence.AsNoTracking().Where(v => v.CompanyId == cid).OrderBy(v => v.TotalDocuments).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("tax:" + ThaiTax, rows[0].VendorKey);           // คงไว้ (ไม่ลบความรู้)
        Assert.Equal("tax:0105561012346", rows[1].VendorKey);       // แถวเดิมที่ผู้อ่านใช้ ไม่ถูกทับ
        Assert.Equal(30, rows[1].TotalDocuments);
    }
}
