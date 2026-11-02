using Accounting.Helpers;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.PettyCash;
using Accounting.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// 2026-11-02 · erp-review/2026-11-01/PETTY_CASH_AUDIT.md — รันบน PostgreSQL จริง
///
/// <para>(A) ตัวเลือก "เติมจากบัญชี" (MoneyChannel.fill) เรียกสอง endpoint: บัญชีธนาคาร + ช่องทางเงิน — ล็อกว่าทั้งคู่ตอบได้กับบริษัทที่มีข้อมูลแบบใช้งานจริง
/// (ผังที่ผูกถูกลบ · บัญชีปิดใช้ · ผังกลุ่ม · กองทุนเงินสดย่อย · ใบสำคัญที่ลงแล้ว)</para>
/// <para>(B) ผัง 1 ผัง ↔ กองทุน 1 กองทุน (ยอมผังร่วมเมื่อติ๊กเอง) · (C) ใบสำคัญจากทางอื่นที่แตะผังกองทุนโผล่ในกระทบยอด แล้วระบุกองทุนได้ครั้งเดียว ·
/// ยอดกองทุนกลับมาตรง GL</para>
/// </summary>
[Trait("Category", "Db")]
public class PettyCashReconciliationDbTests
{
    private readonly ITestOutputHelper _out;
    public PettyCashReconciliationDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid PettyGl, Guid PettyGl2, Guid ExpenseGl, Guid BankGl, Guid BankId);

    private static ChartOfAccount Coa(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัทเงินสดย่อยหลายกองทุน", TaxId = "0105558" + Random.Shared.Next(100000, 999999) };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var group = Coa(company.Id, "111", "เงินสดและรายการเทียบเท่าเงินสด", AccountType.Asset, level: 3);
        var cash = Coa(company.Id, "11111", "เงินสด", AccountType.Asset);
        var petty = Coa(company.Id, PettyCashJePlan.DefaultFundAccountCode, "เงินสดย่อย", AccountType.Asset);
        var petty2 = Coa(company.Id, "11113", "เงินสดย่อย สาขา 2", AccountType.Asset);
        cash.ParentAccountId = group.Id; petty.ParentAccountId = group.Id; petty2.ParentAccountId = group.Id;
        var bankGl = Coa(company.Id, "11121", "เงินฝากกระแสรายวัน", AccountType.Asset);
        var deletedGl = Coa(company.Id, "11129", "เงินฝาก (ผังที่ถูกลบ)", AccountType.Asset);
        deletedGl.IsDeleted = true;
        var expense = Coa(company.Id, "53110", "ค่าเครื่องเขียน", AccountType.Expense);
        db.ChartOfAccounts.AddRange(group, cash, petty, petty2, bankGl, deletedGl, expense);
        await db.SaveChangesAsync();
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีหลัก", BankName = "กสิกรไทย", AccountNumber = "123-4-56789-0", LinkedAccountId = bankGl.Id };
        var orphan = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีผังถูกลบ", BankName = "กรุงไทย", AccountNumber = "555-5-55555-5", LinkedAccountId = deletedGl.Id };
        var closed = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีปิด", BankName = "ออมสิน", AccountNumber = "777", IsActive = false };
        db.BankAccounts.AddRange(bank, orphan, closed);
        await db.SaveChangesAsync();
        return new Seed(company.Id, petty.Id, petty2.Id, expense.Id, bankGl.Id, bank.Id);
    }

    /// <summary>ใบสำคัญที่ลงผังเงินสดย่อยจาก "ทางอื่น" (เหมือนใบสำคัญจ่าย/สมุดรายวันที่เลือกผัง 11112 เป็นช่องทางจ่าย)</summary>
    private static async Task<Guid> PostExternalAsync(Guid companyId, Guid debitGl, Guid creditGl, decimal amount, string no)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = no, EntryDate = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            JournalType = JournalType.General, Description = "ใบสำคัญจ่ายค่าเครื่องเขียน", Status = JournalEntryStatus.Posted,
            TotalDebit = amount, TotalCredit = amount,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = debitGl, DebitAmount = amount, CreditAmount = 0 });
        je.Lines.Add(new JournalEntryLine { AccountId = creditGl, DebitAmount = 0, CreditAmount = amount });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return je.Id;
    }

    [Fact]
    public async Task ตัวเลือกเติมจากบัญชี_สองendpointตอบได้กับข้อมูลแบบใช้งานจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สำนักงานใหญ่", null, 5000m, null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).ReplenishAsync(s.CompanyId,
                (await req.PettyCashFunds.AsNoTracking().FirstAsync(f => f.CompanyId == s.CompanyId)).Id,
                5000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);

        // GET bank/accounts (BankService.GetBankAccountsAsync) — ทุกบัญชี + เฉพาะเปิดใช้
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var all = await new BankService(req).GetBankAccountsAsync(s.CompanyId);
            Assert.Equal(3, all.Count);
            var main = Assert.Single(all, b => b.Id == s.BankId);
            Assert.Equal("11121", main.LinkedAccountCode);
            Assert.Equal(-5000m, main.CurrentBalance);   // ยอดจาก GL (โอนออกไปเติมเงินสดย่อย)
            var orphan = Assert.Single(all, b => b.AccountName == "บัญชีผังถูกลบ");
            Assert.NotNull(orphan.LinkedAccountId);
            Assert.Null(orphan.LinkedAccountCode);       // ผังที่ผูกถูกลบ — ไม่โยน แสดงเหมือนไม่มีรหัส
            Assert.DoesNotContain(await new BankService(req).GetBankAccountsAsync(s.CompanyId, activeOnly: true), b => !b.IsActive);
        }
        // GET accounting/accounts/payment-channels (MoneyAccountDirectory.GetPaymentChannelsAsync)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var channels = await req.GetPaymentChannelsAsync(s.CompanyId, includeBank: false);
            Assert.Contains(channels, c => c.AccountCode == "11111");
            Assert.Contains(channels, c => c.AccountCode == PettyCashJePlan.DefaultFundAccountCode);
            Assert.DoesNotContain(channels, c => c.AccountCode == "111");   // บัญชีคุม
            Assert.All(channels, c => Assert.Equal("other", c.ChannelGroup));
        }
    }

    [Fact]
    public async Task ผังเดียวหลายกองทุน_ปฏิเสธเป็นค่าตั้งต้น_ติ๊กยอมผังร่วมได้_ผังย่อยแยกผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สำนักงานใหญ่", null, 5000m, null);   // ได้ 11112

        // ค่าตั้งต้น (ไม่เลือกผัง ⇒ 11112 อีก) ⇒ ปฏิเสธ พร้อมทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).CreateFundAsync(s.CompanyId, "หน้าร้าน", null, 3000m, null));
            Assert.Equal(PettyCashFundGlRule.RuleSharedAccount, ex.RuleCode);
            Assert.Contains("สำนักงานใหญ่", ex.Message);
            Assert.Contains("ใช้ผังร่วมกับกองทุนอื่น", ex.Message);
        }
        // ทิศตรงข้าม 1: ผังย่อยแยก ⇒ ผ่าน
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(s.PettyGl2, (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สาขา 2", null, 2000m, s.PettyGl2)).LinkedAccountId);
        // ทิศตรงข้าม 2: ผู้ใช้ติ๊กยอมผังร่วม ⇒ ผ่าน
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(s.PettyGl, (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "หน้าร้าน", null, 3000m, s.PettyGl,
                allowSharedAccount: true)).LinkedAccountId);
        // แก้กองทุน "สาขา 2" ไปผัง 11112 (ยังไม่มีรายการ) โดยไม่ติ๊ก ⇒ ปฏิเสธ · แก้ชื่อเฉยๆ ⇒ ผ่าน (ไม่ตรวจผังที่ไม่ได้เปลี่ยน)
        Guid branch2;
        using (var req = DbTestDatabase.TryCreateContext()!)
            branch2 = (await req.PettyCashFunds.AsNoTracking().SingleAsync(f => f.CompanyId == s.CompanyId && f.Name == "สาขา 2")).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PettyCashFundGlRule.RuleSharedAccount, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).UpdateFundAsync(s.CompanyId, branch2, null, null, s.PettyGl))).RuleCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("สาขา 2 (แก้ชื่อ)", (await new PettyCashService(req).UpdateFundAsync(s.CompanyId, branch2, "สาขา 2 (แก้ชื่อ)", null, null)).Name);
    }

    [Fact]
    public async Task ใบสำคัญจากทางอื่นที่เครดิตผังกองทุน_โผล่ในกระทบยอด_ระบุกองทุนแล้วยอดตรงGL_ระบุซ้ำไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid fundId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            fundId = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สำนักงานใหญ่", null, 5000m, null)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).ReplenishAsync(s.CompanyId, fundId, 1000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);

        // ทิศตรงข้าม: รายการจากหน้าเงินสดย่อยเอง ⇒ ตรงกัน ไม่มีอะไรค้าง
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var g = Assert.Single(await new PettyCashService(req).GetGlReconciliationAsync(s.CompanyId));
            Assert.Equal(nameof(PettyCashGlReconStatus.Matched), g.Status);
            Assert.Equal(1000m, g.GlBalance);
            Assert.Equal(0, g.UnattributedCount);
        }

        // ใบสำคัญจ่ายที่เลือกผัง 11112 เป็นช่องทางจ่าย (ทางอื่น) ⇒ GL ลด แต่กองทุนไม่รู้
        var jeId = await PostExternalAsync(s.CompanyId, s.ExpenseGl, s.PettyGl, 300m, "PV-TEST-001");
        PettyCashGlRecon recon;
        using (var req = DbTestDatabase.TryCreateContext()!)
            recon = Assert.Single(await new PettyCashService(req).GetGlReconciliationAsync(s.CompanyId));
        Assert.Equal(nameof(PettyCashGlReconStatus.Unattributed), recon.Status);
        Assert.Equal(700m, recon.GlBalance);
        Assert.Equal(1000m, recon.FundsTotal);
        Assert.Equal(-300m, recon.Difference);
        var entry = Assert.Single(recon.UnattributedEntries);
        Assert.Equal(jeId, entry.JournalEntryId);
        Assert.Equal("Disbursement", entry.Type);
        Assert.Equal(300m, entry.Amount);
        Assert.Equal(fundId, entry.SuggestedFundId);   // ผังมีกองทุนเดียว — เสนอ แต่ไม่ประทับเอง
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1000m, (await req.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId)).CurrentBalance);

        // ระบุกองทุน ⇒ แถวทะเบียนอ้างใบเดิม ไม่ลง JE ใหม่ · ยอดกองทุน = GL
        var jeCountBefore = await db.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var t = await new PettyCashService(req).AttributeExternalEntryAsync(s.CompanyId, fundId, jeId);
            Assert.Equal(jeId, t.JournalEntryId);
            Assert.Equal("Disbursement", t.Type);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(jeCountBefore, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId));
            Assert.Equal(700m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId)).CurrentBalance);
            var g = Assert.Single(await new PettyCashService(check).GetGlReconciliationAsync(s.CompanyId));
            Assert.Equal(nameof(PettyCashGlReconStatus.Matched), g.Status);
            Assert.Equal(0m, g.Difference);
            var reg = await new PettyCashService(check).GetRegisterAsync(s.CompanyId, fundId, null, null);
            Assert.Contains(reg.Rows, r => r.JournalEntryId == jeId && r.AmountOut == 300m);
        }
        // ระบุซ้ำ ⇒ 409 ไม่เกิดแถวที่สอง
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var dup = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).AttributeExternalEntryAsync(s.CompanyId, fundId, jeId));
            Assert.Equal(409, dup.StatusCode);
        }
        // ใบที่ไม่ได้แตะผังกองทุน ⇒ ปฏิเสธ
        var unrelated = await PostExternalAsync(s.CompanyId, s.ExpenseGl, s.BankGl, 50m, "PV-TEST-002");
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(PettyCashFundGlRule.RuleAttribute, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).AttributeExternalEntryAsync(s.CompanyId, fundId, unrelated))).RuleCode);
    }

    [Fact]
    public async Task ผังร่วมสองกองทุน_ใบจากทางอื่นไม่เสนอกองทุน_ผู้ใช้ต้องเลือกเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid a, b;
        using (var req = DbTestDatabase.TryCreateContext()!)
            a = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "กองทุน ก", null, 5000m, s.PettyGl)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            b = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "กองทุน ข", null, 5000m, s.PettyGl, allowSharedAccount: true)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).ReplenishAsync(s.CompanyId, b, 800m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);
        var jeId = await PostExternalAsync(s.CompanyId, s.ExpenseGl, s.PettyGl, 120m, "PV-TEST-003");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var g = Assert.Single(await new PettyCashService(req).GetGlReconciliationAsync(s.CompanyId));
            Assert.True(g.SharedByMultipleFunds);
            Assert.Null(Assert.Single(g.UnattributedEntries).SuggestedFundId);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).AttributeExternalEntryAsync(s.CompanyId, b, jeId);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == a)).CurrentBalance);
        Assert.Equal(680m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == b)).CurrentBalance);
        Assert.Equal(nameof(PettyCashGlReconStatus.Matched), Assert.Single(await new PettyCashService(check).GetGlReconciliationAsync(s.CompanyId)).Status);
    }

    // ══════════════ ส่วน C — หลักฐาน (สลิปเติม / ใบเสร็จเบิก) ══════════════

    private sealed class NullErrorLog : IErrorLogService
    {
        public Task LogErrorAsync(Exception exception, string source, string? userId = null) => Task.CompletedTask;
    }

    /// <summary>ที่เก็บไฟล์แนบกลางตัวจริง (FileAttachmentService) ชี้โฟลเดอร์ชั่วคราว — แถว FileAttachments อยู่ในธุรกรรมเดียวกับรายการ</summary>
    private static FileAttachmentService Files(Accounting.Data.AccountingDbContext db) => new(db, NullLogger<FileAttachmentService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:BasePath"] = Path.Combine(Path.GetTempPath(), "petty-evidence-dbtest"),
        }).Build(), new NullErrorLog());

    /// <summary>PNG 1×1 จริง (ไบต์ขึ้นต้น 89 50 4E 47) — ผ่าน <see cref="PettyCashEvidence.Check"/></summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static UpdateCompanySettingsRequest SettingsReq(bool? pettyEvidenceRequired)
        => new(null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            null, null, null, null,
            null, null, null, null,
            null, null, null,
            null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            PettyCashTopUpEvidenceRequired: pettyEvidenceRequired);

    [Fact]
    public async Task เติมเงิน_บริษัทตั้งบังคับสลิป_ไม่แนบปฏิเสธไม่บันทึกอะไร_แนบแล้วได้รายการไฟล์และaudit()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid fundId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            fundId = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สำนักงานใหญ่", null, 5000m, null)).Id;

        // ค่าตั้งต้น = ไม่บังคับ ⇒ เติมแบบไม่แนบได้ (ทิศตรงข้าม)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await new PettyCashService(req).IsTopUpEvidenceRequiredAsync(s.CompanyId));
            await new PettyCashService(req).ReplenishAsync(s.CompanyId, fundId, 100m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);
        }

        // ตั้งผ่านหน้า ตั้งค่า (SettingsService) ⇒ echo กลับ + ผู้อ่าน (service เงินสดย่อย) เห็น
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var resp = await new SettingsService(req, null!, null!, null!).UpdateSettingsAsync(s.CompanyId, SettingsReq(true));
            Assert.True(resp.PettyCashTopUpEvidenceRequired);
        }

        // บังคับแล้ว: ไม่แนบ (ทั้ง JSON เดิมและ multipart ที่ไม่มีไฟล์ เดินเส้นเดียวกัน) ⇒ ปฏิเสธ · ยอด/รายการไม่ขยับ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req, Files(req)).ReplenishWithEvidenceAsync(s.CompanyId, fundId, 200m, null, DateTime.UtcNow,
                    null, s.BankId, null, null));
            Assert.Equal(PettyCashEvidence.RuleRequired, ex.RuleCode);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(100m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId)).CurrentBalance);
            Assert.Equal(1, await check.PettyCashTransactions.CountAsync(t => t.FundId == fundId));
        }

        // แนบสลิป ⇒ รายการ + JE + ไฟล์ (ที่เก็บกลาง) + audit
        var (kind, err) = PettyCashEvidence.Check(Png, "slip.png");
        Assert.Null(err);
        var evidence = new PettyCashEvidenceFile(PettyCashEvidence.DisplayName("slip.png", kind!.Value.Extension), kind.Value.ContentType, Png);
        PettyCashTransaction t;
        using (var req = DbTestDatabase.TryCreateContext()!)
            t = await new PettyCashService(req, Files(req)).ReplenishWithEvidenceAsync(s.CompanyId, fundId, 200m, null, DateTime.UtcNow,
                null, s.BankId, evidence, null);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(300m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId)).CurrentBalance);
            var file = await check.FileAttachments.AsNoTracking()
                .SingleAsync(a => a.CompanyId == s.CompanyId && a.EntityType == PettyCashEvidence.EntityType && a.EntityId == t.Id);
            Assert.Equal("image/png", file.ContentType);
            Assert.True(await check.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == t.Id.ToString()
                && a.NewValues!.Contains("petty-cash-evidence-attached")));
            var reg = await new PettyCashService(check).GetRegisterAsync(s.CompanyId, fundId, null, null);
            var row = Assert.Single(reg.Rows, r => r.Id == t.Id);
            Assert.Equal(file.Id, Assert.Single(row.Evidence!).AttachmentId);
            Assert.Empty(Assert.Single(reg.Rows, r => r.Id != t.Id).Evidence!);
            // บริษัทอื่นมองไม่เห็นไฟล์ (ที่เก็บกลางกรองบริษัท)
            Assert.Null(await Files(check).GetByIdAsync(Guid.NewGuid(), file.Id));
        }

        // เบิก + ใบเสร็จ (กลไกเดียวกัน · ไม่บังคับ) และแนบภายหลังให้รายการเดิม
        PettyCashTransaction pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await new PettyCashService(req, Files(req)).DisburseWithEvidenceAsync(s.CompanyId, fundId, 50m, "ค่าน้ำดื่ม", s.ExpenseGl,
                "RC-9", DateTime.UtcNow, null, evidence, null);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req, Files(req)).AttachEvidenceAsync(s.CompanyId, pay.Id, evidence, null);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(2, await check.FileAttachments.CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == pay.Id));
        // แนบให้รายการของบริษัทอื่น/ไม่มีจริง ⇒ ไม่พบ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                new PettyCashService(req, Files(req)).AttachEvidenceAsync(Guid.NewGuid(), pay.Id, evidence, null));
    }

    // ══════════════ ฝ่ายค้าน: ล็อก · ปรับเฉพาะ GL · audit ══════════════

    [Fact]
    public async Task เบิกสองรายการพร้อมกัน_ยอดกองทุนไม่หาย_และระบุใบเดียวกันพร้อมกันสองกองทุนผังร่วม_ได้แถวเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid a, b;
        using (var req = DbTestDatabase.TryCreateContext()!)
            a = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "กองทุน ก", null, 5000m, s.PettyGl)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            b = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "กองทุน ข", null, 5000m, s.PettyGl, allowSharedAccount: true)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).ReplenishAsync(s.CompanyId, a, 1000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);

        // lost update: สองคำขอ (context แยก) เบิกพร้อมกัน — ใต้ FOR UPDATE ยอดต้องลดครบทั้งสอง
        async Task Pay(decimal amt)
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            await new PettyCashService(c).DisburseAsync(s.CompanyId, a, amt, "ค่ารถ", s.ExpenseGl, "R", DateTime.UtcNow);
        }
        await Task.WhenAll(Pay(100m), Pay(250m), Pay(50m));
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(600m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == a)).CurrentBalance);

        // ใบเดียวกัน ระบุให้กองทุน ก และ ข (ผังร่วม) พร้อมกัน ⇒ สำเร็จหนึ่ง อีกหนึ่ง 409
        var jeId = await PostExternalAsync(s.CompanyId, s.ExpenseGl, s.PettyGl, 80m, "PV-RACE-1");
        async Task<Exception?> Attr(Guid fund)
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            try { await new PettyCashService(c).AttributeExternalEntryAsync(s.CompanyId, fund, jeId); return null; }
            catch (Exception ex) { return ex; }
        }
        var results = await Task.WhenAll(Attr(a), Attr(b));
        Assert.Single(results, r => r == null);
        Assert.IsType<BusinessRuleException>(Assert.Single(results, r => r != null));
        using var final = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await final.PettyCashTransactions.CountAsync(t => t.CompanyId == s.CompanyId && t.JournalEntryId == jeId && !t.IsDeleted));
        Assert.True(await final.AuditLogs.AnyAsync(l => l.CompanyId == s.CompanyId && l.NewValues!.Contains("petty-cash-entry-attributed")));
    }

    [Fact]
    public async Task ใบจากทางอื่นที่ไม่กระทบกองทุน_ทำเครื่องหมายปรับเฉพาะGL_ออกจากรายการรอ_ยอดกองทุนเดิม_ยกเลิกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        Guid fundId;
        using (var req = DbTestDatabase.TryCreateContext()!)
            fundId = (await new PettyCashService(req).CreateFundAsync(s.CompanyId, "สำนักงานใหญ่", null, 5000m, null)).Id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).ReplenishAsync(s.CompanyId, fundId, 1000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);
        // ปรับยอดยกมาเข้าผังเงินสดย่อย (ไม่ใช่เงินในลิ้นชัก) — เดบิต 11112 / เครดิตเงินสด
        var jeId = await PostExternalAsync(s.CompanyId, s.PettyGl, s.BankGl, 40m, "JV-OPEN-1");

        PettyCashTransaction mark;
        using (var req = DbTestDatabase.TryCreateContext()!)
            mark = await new PettyCashService(req).MarkGlOnlyAsync(s.CompanyId, s.PettyGl, jeId, "ปรับยอดยกมา");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1000m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId)).CurrentBalance);
            var g = Assert.Single(await new PettyCashService(check).GetGlReconciliationAsync(s.CompanyId));
            Assert.Equal(0, g.UnattributedCount);
            Assert.Equal(1040m, g.GlBalance);
            Assert.Equal(40m, g.GlOnlyNet);
            Assert.Equal(0m, g.Difference);
            Assert.Equal(nameof(PettyCashGlReconStatus.Matched), g.Status);
            Assert.Equal(mark.Id, Assert.Single(g.GlOnlyEntries!).TransactionId);
            Assert.True(await check.AuditLogs.AnyAsync(l => l.EntityId == mark.Id.ToString() && l.NewValues!.Contains("petty-cash-entry-gl-only")));
            var reg = await new PettyCashService(check).GetRegisterAsync(s.CompanyId, fundId, null, null);
            var row = Assert.Single(reg.Rows, r => r.Id == mark.Id);
            Assert.Equal(0m, row.AmountIn + row.AmountOut);   // ไม่นับเป็นเงินเข้า/ออก
        }
        // ทำซ้ำ / ระบุกองทุนหลังทำเครื่องหมาย ⇒ 409
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).MarkGlOnlyAsync(s.CompanyId, s.PettyGl, jeId, null))).StatusCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).AttributeExternalEntryAsync(s.CompanyId, fundId, jeId))).StatusCode);

        // ยกเลิก ⇒ กลับไปรอระบุ + audit · ยกเลิกแถวเบิก/เติมด้วยเส้นนี้ไม่ได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).UndoGlOnlyAsync(s.CompanyId, mark.Id);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var g = Assert.Single(await new PettyCashService(check).GetGlReconciliationAsync(s.CompanyId));
            Assert.Equal(nameof(PettyCashGlReconStatus.Unattributed), g.Status);
            Assert.Equal(jeId, Assert.Single(g.UnattributedEntries).JournalEntryId);
            Assert.True(await check.AuditLogs.AnyAsync(l => l.EntityId == mark.Id.ToString() && l.OldValues!.Contains("petty-cash-entry-gl-only-undone")));
            var topUp = await check.PettyCashTransactions.AsNoTracking().FirstAsync(t => t.FundId == fundId && t.Type == "Replenishment");
            using var req = DbTestDatabase.TryCreateContext()!;
            await Assert.ThrowsAsync<BusinessRuleException>(() => new PettyCashService(req).UndoGlOnlyAsync(s.CompanyId, topUp.Id));
        }
    }
}
