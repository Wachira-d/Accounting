using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>รอบ 232 — ผู้ใช้ผูกช่องทาง Omise แล้วล้ม "ไม่มีผัง 11340" ทั้งที่ระบบควรสร้างให้ (บริษัทเดิมสร้างก่อน 11340 เข้าแม่แบบ) ·
/// resolver ต้องสร้าง 11340 ใต้กลุ่ม 113 เอง (idempotent · ลบแล้วกู้คืน) · ทิศตรงข้าม: ไม่มีกลุ่ม 113 ⇒ null (ไม่แต่งผังมั่ว)</summary>
[Trait("Category", "Db")]
public class GatewayClearingSelfHealRound232DbTests
{
    private readonly ITestOutputHelper _out;
    public GatewayClearingSelfHealRound232DbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> SeedCompanyAsync(AccountingDbContext db, bool withGroup113)
    {
        var c = new Company { Name = "บริษัทไม่มี 11340 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000004" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        var a1 = new ChartOfAccount { CompanyId = c.Id, AccountCode = "1", AccountName = "สินทรัพย์", AccountType = AccountType.Asset, Level = 1, IsSystemAccount = true };
        var a11 = new ChartOfAccount { CompanyId = c.Id, AccountCode = "11", AccountName = "สินทรัพย์หมุนเวียน", AccountType = AccountType.Asset, Level = 2, IsSystemAccount = true, ParentAccountId = a1.Id };
        db.ChartOfAccounts.AddRange(a1, a11);
        if (withGroup113)
            db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = c.Id, AccountCode = "113", AccountName = "ลูกหนี้การค้าและลูกหนี้อื่น", AccountType = AccountType.Asset, Level = 3, IsSystemAccount = true, ParentAccountId = a11.Id });
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static GatewayAccountResolver Resolver(AccountingDbContext db) => new(db,
        new IPaymentProvider[] { new OmisePaymentProvider(null!, null!, NullLogger<OmisePaymentProvider>.Instance) },
        NullLogger<GatewayAccountResolver>.Instance);

    [Fact]
    public async Task ไม่มี11340_resolverสร้างให้ใต้113_เรียกซ้ำได้ตัวเดิม_ลบแล้วกู้คืน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, withGroup113: true);
        var id = await Resolver(db).ResolveClearingAccountAsync(cid, OmisePaymentProvider.Code, null);
        Assert.NotNull(id);
        var acc = await db.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(GatewayAccountResolver.StandardClearingCode, acc.AccountCode);
        Assert.Equal(cid, acc.CompanyId);
        Assert.Equal(AccountType.Asset, acc.AccountType);
        Assert.True(acc.IsSystemAccount && acc.IsActive);
        var parent = await db.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.Id == acc.ParentAccountId);
        Assert.Equal("113", parent.AccountCode);
        Assert.Equal(id, await Resolver(db).ResolveClearingAccountAsync(cid, OmisePaymentProvider.Code, null));   // idempotent
        Assert.Equal(1, await db.ChartOfAccounts.IgnoreQueryFilters().CountAsync(a => a.CompanyId == cid && a.AccountCode == "11340"));

        // ลบ/ปิดใช้แล้ว ⇒ กู้คืนตัวเดิม (unique CompanyId+AccountCode ห้ามสร้างซ้ำ)
        var row = await db.ChartOfAccounts.SingleAsync(a => a.Id == id);
        row.IsDeleted = true; row.IsActive = false; await db.SaveChangesAsync();
        Assert.Equal(id, await Resolver(db).ResolveClearingAccountAsync(cid, OmisePaymentProvider.Code, null));
        var restored = await db.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.True(restored.IsActive && !restored.IsDeleted);

        // ช่องทาง gateway ผูก config ⇒ EnsureClearingAccountAsync ใช้ผังนี้ ไม่ล้ม ไม่สร้าง 1134x
        var cfg = new PaymentProviderConfig { CompanyId = cid, ProviderCode = OmisePaymentProvider.Code, DisplayName = "NextAcc Omise" };
        db.PaymentProviderConfigs.Add(cfg);
        var channel = new SettlementChannel { CompanyId = cid, DisplayName = "Omise", Kind = SettlementChannelKind.Gateway, PaymentProviderConfigId = cfg.Id };
        db.SettlementChannels.Add(channel);
        await db.SaveChangesAsync();
        var resolved = await Resolver(db).ResolveClearingAccountAsync(cid, OmisePaymentProvider.Code, cfg.Id);
        Assert.Equal(id, await SettlementChannelAccounts.EnsureClearingAccountAsync(db, cid, channel, resolved));
        Assert.Equal(id, channel.ClearingAccountId);
        Assert.False(await db.ChartOfAccounts.AsNoTracking().AnyAsync(a => a.CompanyId == cid && a.AccountCode.StartsWith("1134") && a.AccountCode != "11340"));
    }

    [Fact]
    public async Task ทิศตรงข้าม_ไม่มีกลุ่ม113_ไม่สร้าง_คืนnull_ผูกช่องทางล้มดังบอกทางไปต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, withGroup113: false);
        Assert.Null(await Resolver(db).ResolveClearingAccountAsync(cid, OmisePaymentProvider.Code, null));
        Assert.False(await db.ChartOfAccounts.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == cid && a.AccountCode == "11340"));
        var cfg = new PaymentProviderConfig { CompanyId = cid, ProviderCode = OmisePaymentProvider.Code, DisplayName = "NextAcc Omise" };
        db.PaymentProviderConfigs.Add(cfg);
        var channel = new SettlementChannel { CompanyId = cid, DisplayName = "Omise", Kind = SettlementChannelKind.Gateway, PaymentProviderConfigId = cfg.Id };
        db.SettlementChannels.Add(channel);
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => SettlementChannelAccounts.EnsureClearingAccountAsync(db, cid, channel, null));
        Assert.Equal("SETTLEMENT-GATEWAY-CLEARING", ex.RuleCode);
        Assert.Contains("กลุ่ม 113", ex.Message);
    }
}
