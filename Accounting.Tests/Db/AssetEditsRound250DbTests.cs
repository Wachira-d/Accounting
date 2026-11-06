using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 250 WP-08 — สินทรัพย์ P2 (คำตัดสิน 250-26/27 · ASSET_MULTI_DOCUMENT_DESIGN §6 Q1/Q5) บน PostgreSQL จริง:
/// ราคาตามสัญญาแก้ได้ (สิทธิ์ Asset.Manage + audit chain · ทิศตรงข้ามไม่มีสิทธิ์ 403 ไม่เขียน) ·
/// วันที่พร้อมใช้งานของสินทรัพย์ใบเดียวแก้ได้ก่อนมีค่าเสื่อมลงบัญชี (แผนค่าเสื่อมเริ่มเดือนถัดจากวันนั้น) · หลังลงแล้วล็อก · ว่าง = วันที่เอกสาร
/// </summary>
[Trait("Category", "Db")]
public class AssetEditsRound250DbTests
{
    private readonly ITestOutputHelper _out;
    public AssetEditsRound250DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private const string Actor = "25080000-0000-0000-0000-000000000001";

    /// <summary>ซื้อ 15 ม.ค. ปีก่อน · 120,000 · 12 เดือน เส้นตรง · แผนค่าเสื่อม (ยังไม่ลงบัญชี) จากวันที่ซื้อ</summary>
    private static async Task<(Guid CompanyId, Guid AssetId)> SeedSingleAssetAsync(AccountingDbContext db, string tag)
    {
        var company = new Company { Name = "บริษัทสินทรัพย์ใบเดียว " + tag, TaxId = "" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var asset = new FixedAsset
        {
            CompanyId = company.Id, AssetCode = "FA-W8-" + Guid.NewGuid().ToString("N")[..6], Name = "เครื่องพิมพ์",
            PurchaseDate = Bought(), PurchaseCost = 120_000m, NetBookValue = 120_000m, UsefulLifeMonths = 12,
            DepreciationMethod = DepreciationMethod.StraightLine, Status = AssetStatus.Active,
        };
        db.FixedAssets.Add(asset);
        await db.SaveChangesAsync();
        foreach (var p in DepreciationSchedule.Build(DepreciationMethod.StraightLine, 120_000m, 0m, 12, asset.PurchaseDate))
            db.AssetDepreciations.Add(new AssetDepreciation
            {
                CompanyId = company.Id, FixedAssetId = asset.Id, Year = p.Year, Month = p.Month, Amount = p.Amount,
                AccumulatedAmount = p.Accumulated, NetBookValue = p.NetBookValue, IsPosted = false,
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (company.Id, asset.Id);
    }

    private static DateTime Bought() => DateTime.SpecifyKind(new DateTime(DateTime.UtcNow.Year - 1, 1, 15), DateTimeKind.Utc);

    private static async Task<(int Year, int Month)> FirstPlannedAsync(AccountingDbContext db, Guid assetId)
    {
        var first = await db.AssetDepreciations.AsNoTracking().Where(d => d.FixedAssetId == assetId && !d.IsPosted)
            .OrderBy(d => d.Year).ThenBy(d => d.Month).FirstAsync();
        return (first.Year, first.Month);
    }

    private static FixedAssetController Controller(AccountingDbContext db, bool allowed)
    {
        var perms = F4Proxy<IPermissionService>.Create((m, _) => m == nameof(IPermissionService.HasPermissionAsync) ? Task.FromResult(allowed) : null);
        return new FixedAssetController(new FixedAssetService(db), perms)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Actor) }, "test")),
                },
            },
        };
    }

    private static int? StatusOf<T>(ActionResult<T> r) => r.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult sc => sc.StatusCode,
        _ => null,
    };

    private static Task<int> AuditCountAsync(AccountingDbContext db, Guid companyId, Guid assetId, string action)
        => db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == companyId && a.EntityType == nameof(FixedAsset)
            && a.EntityId == assetId.ToString() && a.Action == AuditAction.Update && a.NewValues != null && a.NewValues.Contains(action));

    [Fact]
    public async Task Contract_price_patch_requires_Asset_Manage_and_audits()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, assetId) = await SeedSingleAssetAsync(db, "contract " + Guid.NewGuid().ToString("N")[..6]);

        // ทิศตรงข้าม: ไม่มีสิทธิ์ ⇒ 403 · ไม่เขียนค่า · ไม่มี audit
        var denied = await Controller(db, allowed: false).PatchContractPrice(cid, assetId, new PatchContractPriceRequest(900_000m));
        Assert.Equal(403, StatusOf(denied));
        Assert.Null((await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId)).ContractPrice);
        Assert.Equal(0, await AuditCountAsync(db, cid, assetId, "contract-price-updated"));

        var ok = await Controller(db, allowed: true).PatchContractPrice(cid, assetId, new PatchContractPriceRequest(900_000m));
        Assert.Equal(200, StatusOf(ok));
        db.ChangeTracker.Clear();
        Assert.Equal(900_000m, (await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId)).ContractPrice);
        Assert.Equal(1, await AuditCountAsync(db, cid, assetId, "contract-price-updated"));

        // ค่าเดิมซ้ำ ⇒ ไม่เขียน audit ซ้ำ · ติดลบ ⇒ ปฏิเสธ · null ⇒ ล้าง
        await new FixedAssetService(db).SetContractPriceAsync(cid, assetId, 900_000m, Actor);
        Assert.Equal(1, await AuditCountAsync(db, cid, assetId, "contract-price-updated"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).SetContractPriceAsync(cid, assetId, -1m, Actor));
        await new FixedAssetService(db).SetContractPriceAsync(cid, assetId, null, Actor);
        db.ChangeTracker.Clear();
        Assert.Null((await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId)).ContractPrice);
        // tenant: บริษัทอื่น ⇒ ไม่พบ
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new FixedAssetService(db).SetContractPriceAsync(Guid.NewGuid(), assetId, 1m, Actor));
    }

    [Fact]
    public async Task Single_line_asset_available_date_editable_until_depreciation_posted()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, assetId) = await SeedSingleAssetAsync(db, "avail " + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal((Bought().Year, 2), await FirstPlannedAsync(db, assetId));   // ก่อนแก้: เดือนถัดจากวันที่ซื้อ

        var available = DateTime.SpecifyKind(new DateTime(Bought().Year, 3, 1), DateTimeKind.Utc);
        var r = await new FixedAssetService(db).SetAvailableForUseDateAsync(cid, assetId, available, Actor);
        Assert.Equal(available.Date, r.AvailableForUseDate?.Date);
        Assert.Equal(available.Date, r.DepreciationStartDate?.Date);
        Assert.Null(r.AvailableDateLockReason);
        db.ChangeTracker.Clear();
        Assert.Equal((Bought().Year, 4), await FirstPlannedAsync(db, assetId));   // แผนเริ่มเดือนถัดจากพร้อมใช้
        Assert.Equal(12, await db.AssetDepreciations.CountAsync(d => d.FixedAssetId == assetId && !d.IsPosted));
        var stored = await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        Assert.Equal(Bought().Date, stored.PurchaseDate.Date);                    // วันที่ซื้อไม่ถูกแตะ
        Assert.Equal(1, await AuditCountAsync(db, cid, assetId, "available-for-use-date-updated"));

        // ก่อนวันที่ซื้อ ⇒ ปฏิเสธ (ไม่แตะค่า)
        await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).SetAvailableForUseDateAsync(cid, assetId,
            DateTime.SpecifyKind(new DateTime(Bought().Year, 1, 1), DateTimeKind.Utc), Actor));
        db.ChangeTracker.Clear();
        Assert.Equal(available.Date, (await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId)).AvailableForUseDate?.Date);
    }

    [Fact]
    public async Task Single_line_asset_available_date_locked_after_first_depreciation()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, assetId) = await SeedSingleAssetAsync(db, "locked " + Guid.NewGuid().ToString("N")[..6]);
        await db.AssetDepreciations.Where(d => d.FixedAssetId == assetId && d.Year == Bought().Year && d.Month == 2)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.IsPosted, true));
        db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).SetAvailableForUseDateAsync(cid, assetId,
            DateTime.SpecifyKind(new DateTime(Bought().Year, 3, 1), DateTimeKind.Utc), Actor));
        Assert.Contains("ลงบัญชีแล้ว", ex.Message);
        db.ChangeTracker.Clear();
        Assert.Null((await db.FixedAssets.AsNoTracking().SingleAsync(a => a.Id == assetId)).AvailableForUseDate);
        Assert.Equal(0, await AuditCountAsync(db, cid, assetId, "available-for-use-date-updated"));
        // ฟอร์มแก้ไขได้เหตุเดียวกัน (ล็อกช่องก่อนกด — ไม่ใช่กดแล้วถูกปฏิเสธ)
        var view = await new FixedAssetService(db).GetByIdAsync(cid, assetId);
        Assert.NotNull(view.AvailableDateLockReason);
    }

    [Fact]
    public async Task Empty_available_date_falls_back_to_document_date()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, assetId) = await SeedSingleAssetAsync(db, "fallback " + Guid.NewGuid().ToString("N")[..6]);
        var svc = new FixedAssetService(db);
        await svc.SetAvailableForUseDateAsync(cid, assetId, DateTime.SpecifyKind(new DateTime(Bought().Year, 5, 10), DateTimeKind.Utc), Actor);
        db.ChangeTracker.Clear();
        Assert.Equal((Bought().Year, 6), await FirstPlannedAsync(db, assetId));

        var cleared = await new FixedAssetService(db).SetAvailableForUseDateAsync(cid, assetId, null, Actor);
        Assert.Null(cleared.AvailableForUseDate);
        Assert.Equal(Bought().Date, cleared.DepreciationStartDate?.Date);
        db.ChangeTracker.Clear();
        Assert.Equal((Bought().Year, 2), await FirstPlannedAsync(db, assetId));   // กลับไปเริ่มเดือนถัดจากวันที่ซื้อ
        Assert.Equal(2, await AuditCountAsync(db, cid, assetId, "available-for-use-date-updated"));
    }

    /// <summary>รอบ 252 WP-13: ฟอร์มสินทรัพย์ "รหัสว่าง = ออกให้อัตโนมัติ" — เซิร์ฟเวอร์ออก FA-yyyyMM-#### ด้วยตัวออกรหัสตัวเดียว ·
    /// ทิศตรงข้าม: กรอกเอง = ใช้ตามที่กรอก (ตัดช่องว่าง) · รหัสซ้ำยังถูกปฏิเสธ · validator ไม่บังคับรหัสแต่ยังบังคับชื่อ</summary>
    [Fact]
    public async Task Create_with_blank_code_generates_FA_code_and_manual_code_kept()
    {
        var v = new Accounting.Validators.CreateFixedAssetRequestValidator();
        CreateFixedAssetRequest Req(string code, string name = "โต๊ะ") => new(code, name, null, null, null, null,
            Bought(), 10_000m, 0m, 12);
        Assert.True(v.Validate(Req("")).IsValid);
        Assert.False(v.Validate(Req("", name: "")).IsValid);
        Assert.False(v.Validate(Req(new string('X', 51))).IsValid);

        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var company = new Company { Name = "บริษัทรหัสอัตโนมัติ " + Guid.NewGuid().ToString("N")[..6], TaxId = "" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);

        var auto = await svc.CreateAsync(company.Id, Req("   "), Actor);
        Assert.Matches(@"^FA-\d{6}-\d{4}$", auto.AssetCode);
        var manual = await svc.CreateAsync(company.Id, Req("  MY-001 "), Actor);
        Assert.Equal("MY-001", manual.AssetCode);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.CreateAsync(company.Id, Req("MY-001"), Actor));
        var auto2 = await svc.CreateAsync(company.Id, Req(""), Actor);
        Assert.NotEqual(auto.AssetCode, auto2.AssetCode);
    }
}
