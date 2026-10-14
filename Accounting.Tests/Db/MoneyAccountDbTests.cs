using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Settings;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// 2026-10-14 ทีม F3 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 PC#6/#8/#11/#12/#17/#22 · ส่วนที่ 2 ID#15/#19/#22/#24) — เส้นจริงบน PostgreSQL
///
/// <para>ที่มา: รายการประจำบันทึกไม่ได้เพราะ id บัญชีธนาคารถูกตรวจเป็น id ผังบัญชี · ทีมตรวจพบว่ากติกา "ผังเงิน" เป็นรหัสขึ้นต้นที่เขียนซ้ำ 9 ที่
/// (บริษัทที่ใช้ผังของตัวเองใช้ไม่ได้) · ค่าแหล่งเงินเริ่มต้นถูกล้างทุกครั้งที่บันทึกหน้าตั้งค่า · เงินเดือนเครดิตบัญชีคุม 11122 ·
/// "ไม่พบ" ถูกรายงานเป็น "ยังไม่ผูก" · ใบสำคัญด้วยมือลงผังที่ปิดใช้/บัญชีคุมได้</para>
/// <para>ทุกเทสต์มีทิศตรงข้าม: ผังมาตรฐานเดิมต้องได้ผลเท่าเดิม · ค่าที่ถูกต้องต้องผ่าน · ทางอัตโนมัติ (ปิดงวด) ยังลงผังที่ปิดใช้ได้</para>
/// </summary>
[Trait("Category", "Db")]
public class MoneyAccountDbTests
{
    private readonly ITestOutputHelper _out;
    public MoneyAccountDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Dictionary<string, Guid> Acc, Guid BankId, Guid UnlinkedBankId, Guid InactiveBankId);

    /// <summary>ผังมาตรฐานย่อ: 111 (คุม) · 11111 เงินสด · 11113 กระเป๋าเงิน · 11122 ออมทรัพย์ (คุม — มีบัญชีย่อยธนาคาร 11122-001) ·
    /// 21230 เจ้าหนี้กรรมการ · 51110 ค่าใช้จ่าย · 11131 เช็คในมือ (ปิดใช้)</summary>
    private static async Task<Seed> SeedStandardAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบแหล่งเงิน", TaxId = "0105556000001" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var acc = new Dictionary<string, Guid>();
        ChartOfAccount Add(string code, string name, AccountType type, int level, string? parent = null, bool active = true)
        {
            var a = new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = active,
                ParentAccountId = parent == null ? null : acc[parent],
            };
            db.ChartOfAccounts.Add(a);
            acc[code] = a.Id;
            return a;
        }
        Add("111", "เงินสดและรายการเทียบเท่าเงินสด", AccountType.Asset, 3);
        Add("11111", "เงินสด", AccountType.Asset, 4, "111");
        Add("11113", "กระเป๋าเงิน Digital", AccountType.Asset, 4, "111");
        Add("11122", "เงินฝากออมทรัพย์", AccountType.Asset, 4, "111");
        Add("11122-001", "เงินฝากออมทรัพย์ - กสิกร xxx-1234", AccountType.Asset, 5, "11122");
        Add("11131", "เช็คในมือ", AccountType.Asset, 4, "111", active: false);
        Add("21230", "เจ้าหนี้กรรมการ", AccountType.Liability, 4);
        Add("51110", "ค่าใช้จ่ายสำนักงาน", AccountType.Expense, 4);
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีเงินเดือน", BankName = "กสิกร", AccountNumber = "1234561234", LinkedAccountId = acc["11122-001"] };
        var unlinked = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีสำรอง", BankName = "กรุงไทย", AccountNumber = "9876549876" };
        var inactive = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีปิดแล้ว", BankName = "ไทยพาณิชย์", AccountNumber = "5555500000", LinkedAccountId = acc["11122-001"], IsActive = false };
        db.BankAccounts.AddRange(bank, unlinked, inactive);
        await db.SaveChangesAsync();
        return new Seed(company.Id, acc, bank.Id, unlinked.Id, inactive.Id);
    }

    private static UpdateCompanySettingsRequest SettingsReq(Guid? defaultPaymentAccountId = null, string? moneyPrefixes = null, string? emailFromName = null)
        => new(null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            emailFromName, null, null, null,
            null, null, null, null,
            null, null, null,
            null, null, null, null, null, null, null, null, null,
            null, null, null, null,
            DefaultPaymentAccountId: defaultPaymentAccountId,
            MoneyAccountCodePrefixes: moneyPrefixes);

    private static SettingsService Settings(AccountingDbContext db) => new(db, null!, null!, null!);

    // ── PC#11 รายการช่องทางเงิน ──

    [Fact]
    public async Task ผังมาตรฐาน_ช่องทางเงินค่าเดิม_ไม่มีผังธนาคาร_ไม่มีบัญชีคุม_ไม่มีผังปิดใช้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedStandardAsync(db);

        var rows = await db.GetPaymentChannelsAsync(s.CompanyId, includeBank: false);
        var codes = rows.Select(r => r.AccountCode).ToList();
        // พฤติกรรมเดิมของผังมาตรฐาน (111/1133/2123 · ตัด 1112 · ระดับลงรายการ) — หน้าที่ยังกรอง '1112' เองได้ผลเท่าเดิม
        Assert.Equal(new[] { "11111", "11113", "21230" }, codes);
        Assert.All(rows, r => Assert.Equal("other", r.ChannelGroup));

        var all = await db.GetPaymentChannelsAsync(s.CompanyId, includeBank: true);
        var bankRow = Assert.Single(all, r => r.AccountCode == "11122-001");
        Assert.Equal("bank", bankRow.ChannelGroup);
        Assert.Equal(s.BankId, bankRow.BankAccountId);
        Assert.Contains("บัญชีเงินเดือน", bankRow.BankAccountLabel);
        Assert.DoesNotContain(all, r => r.AccountCode == "11122");   // บัญชีคุมที่ไม่มีบัญชีธนาคารผูก — ไม่ใช่ตัวเลือก (PC#12)
        Assert.DoesNotContain(all, r => r.AccountCode == "11131");   // ปิดใช้
    }

    [Fact]
    public async Task ผังของบริษัทเอง_เงินสด1010_ขึ้นเมื่อตั้งรหัส_ธนาคารที่ผูกขึ้นเสมอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var company = new Company { Name = "บริษัทผังของตัวเอง", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cash = new ChartOfAccount { CompanyId = company.Id, AccountCode = "1010", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 2 };
        var bankGl = new ChartOfAccount { CompanyId = company.Id, AccountCode = "1020", AccountName = "เงินฝากธนาคาร", AccountType = AccountType.Asset, Level = 2 };
        db.ChartOfAccounts.AddRange(cash, bankGl);
        db.BankAccounts.Add(new BankAccount { CompanyId = company.Id, AccountName = "บัญชีหลัก", BankName = "กรุงเทพ", AccountNumber = "1112223334", LinkedAccountId = bankGl.Id });
        await db.SaveChangesAsync();

        // ยังไม่ตั้งรหัส: เงินสด 1010 ไม่อยู่ในรายการ (ไม่เดา) · ผังที่ผูกธนาคารอยู่ (กลุ่ม bank)
        var before = await db.GetPaymentChannelsAsync(company.Id, includeBank: true);
        Assert.DoesNotContain(before, r => r.AccountCode == "1010");
        Assert.Equal("bank", Assert.Single(before, r => r.AccountCode == "1020").ChannelGroup);
        // ค่าเริ่มต้นใช้ผังที่ผูกธนาคารได้ (ไม่ตกไปหา 111x ที่ไม่มี)
        var def0 = await db.ResolveDefaultMoneySourceAsync(company.Id);
        Assert.Equal(bankGl.Id, def0.Account?.Id);

        // ตั้งรหัสผังเงินของบริษัทผ่านหน้าตั้งค่า ⇒ เงินสด 1010 เป็นช่องทาง "other"
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Settings(req).UpdateSettingsAsync(company.Id, SettingsReq(moneyPrefixes: " 101 "));
        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.GetPaymentChannelsAsync(company.Id, includeBank: false);
        Assert.Equal("1010", Assert.Single(after).AccountCode);
        var def = await check.ResolveDefaultMoneySourceAsync(company.Id);
        Assert.Equal(cash.Id, def.Account?.Id);   // รหัสต่ำสุดที่ลงรายการได้
    }

    // ── PC#6 / ID#22 แหล่งเงินเริ่มต้น ──

    [Fact]
    public async Task ตั้งค่าแหล่งเงินเริ่มต้นเป็นผังธนาคาร_บันทึกเรื่องอื่นไม่ล้างค่า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);

        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var resp = await Settings(r1).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: s.Acc["11122-001"]));
            Assert.Equal(s.Acc["11122-001"], resp.DefaultPaymentAccountId);
            Assert.Contains("11122-001", resp.DefaultPaymentAccountLabel);
            Assert.Null(resp.DefaultPaymentAccountWarning);
        }
        // บันทึกเรื่องอื่น (ไม่ส่งช่องนี้ = null) — เดิมหน้าเว็บส่ง Guid.Empty ล้างทิ้ง · null ต้องไม่แตะ
        using (var r2 = DbTestDatabase.TryCreateContext()!)
            await Settings(r2).UpdateSettingsAsync(s.CompanyId, SettingsReq(emailFromName: "ฝ่ายบัญชี"));
        using var check = DbTestDatabase.TryCreateContext()!;
        var saved = await check.Set<CompanySettings>().AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(s.Acc["11122-001"], saved.DefaultPaymentAccountId);
        Assert.Equal("ฝ่ายบัญชี", saved.EmailFromName);

        // ทิศตรงข้าม: Guid.Empty = ผู้ใช้เลือก "อัตโนมัติ" ⇒ ล้างได้จริง
        using (var r3 = DbTestDatabase.TryCreateContext()!)
            await Settings(r3).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: Guid.Empty));
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check2.Set<CompanySettings>().AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId)).DefaultPaymentAccountId);
    }

    [Fact]
    public async Task ตั้งค่าแหล่งเงินเริ่มต้นเป็นผังปิดใช้หรือบัญชีคุม_ถูกปฏิเสธพร้อมทางไปต่อ_ค่าที่บันทึกแล้วปิดใช้ทีหลังมีคำเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);

        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Settings(r1).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: s.Acc["11131"])));
            Assert.Contains("แหล่งเงินเริ่มต้น", ex.Message);
            Assert.Contains("ปิดใช้งาน", ex.Message);
        }
        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Settings(r2).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: s.Acc["111"])));
            Assert.Contains("บัญชีคุม", ex.Message);
        }
        // บันทึกเงินสดได้ แล้วผังถูกปิดใช้ทีหลัง ⇒ GET แสดงป้าย + คำเตือน (ไม่หายเงียบ) และการใช้ค่าเริ่มต้นล้มดัง
        using (var r3 = DbTestDatabase.TryCreateContext()!)
            await Settings(r3).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: s.Acc["11111"]));
        await db.ChartOfAccounts.Where(a => a.Id == s.Acc["11111"]).ExecuteUpdateAsync(u => u.SetProperty(a => a.IsActive, false));
        using var r4 = DbTestDatabase.TryCreateContext()!;
        var view = await Settings(r4).GetSettingsAsync(s.CompanyId);
        Assert.Contains("11111", view.DefaultPaymentAccountLabel);
        Assert.Contains("ปิดใช้งาน", view.DefaultPaymentAccountWarning);
        var use = await Assert.ThrowsAsync<BusinessRuleException>(() => r4.ResolveSourceGlAsync(s.CompanyId, null, null));
        Assert.Contains("ตั้งค่า", use.Message);
    }

    // ── PC#17 / ID#19 บัญชีธนาคาร → ผัง (ตัวหาตัวเดียวของ ปกส./ภาษี/ที่พัก/รอบโอน gateway) ──

    [Fact]
    public async Task บัญชีธนาคาร_ผูกแล้วได้ผัง_ไม่พบ_ปิดใช้_ยังไม่ผูก_ข้อความต่างกันและบอกที่แก้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);

        Assert.Equal(s.Acc["11122-001"], await db.ResolveBankGlAsync(s.CompanyId, s.BankId));

        var notFound = await db.TryResolveBankGlAsync(s.CompanyId, Guid.NewGuid());
        Assert.Equal(BankLinkProblem.NotFound, notFound.Problem);
        Assert.DoesNotContain("ยังไม่ได้ผูก", notFound.Message);

        var inactive = await db.TryResolveBankGlAsync(s.CompanyId, s.InactiveBankId);
        Assert.Equal(BankLinkProblem.Inactive, inactive.Problem);
        Assert.Contains("บัญชีปิดแล้ว", inactive.Message);

        var unlinked = await Assert.ThrowsAsync<BusinessRuleException>(() => db.ResolveBankGlAsync(s.CompanyId, s.UnlinkedBankId));
        Assert.Contains("ยังไม่ได้ผูกผังบัญชี", unlinked.Message);
        Assert.Contains("หน้า \"บัญชีธนาคาร\"", unlinked.Message);

        // tenant: บัญชีธนาคารของบริษัทอื่น = ไม่พบ (ไม่ใช่ผังของบริษัทอื่น)
        var other = await SeedStandardAsync(db);
        Assert.Equal(BankLinkProblem.NotFound, (await db.TryResolveBankGlAsync(other.CompanyId, s.BankId)).Problem);
    }

    [Fact]
    public async Task แหล่งเงิน_ผังที่เลือกตรงต้องลงรายการได้_ไม่เลือกใช้เงินสดรหัสต่ำสุดไม่ใช่บัญชีคุม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);

        Assert.Equal(s.Acc["11111"], await db.ResolveSourceGlAsync(s.CompanyId, null, null));
        Assert.Equal(s.Acc["21230"], await db.ResolveSourceGlAsync(s.CompanyId, null, s.Acc["21230"]));
        Assert.Equal(s.Acc["11122-001"], await db.ResolveSourceGlAsync(s.CompanyId, s.BankId, null));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => db.ResolveSourceGlAsync(s.CompanyId, null, s.Acc["11131"], "บัญชีที่จ่าย"));
        Assert.StartsWith("บัญชีที่จ่าย:", ex.Message);
        var other = await SeedStandardAsync(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => db.ResolveSourceGlAsync(other.CompanyId, null, s.Acc["11111"]));
    }

    // ── PC#12 / PC#22 / ID#15 เงินเดือน ──

    private static async Task<(Guid RunId, Guid EmpId)> SeedPayrollRunAsync(AccountingDbContext db, Guid companyId, string? empPayCode)
    {
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
        var emp = new Employee { CompanyId = companyId, EmployeeCode = "E001", FirstNameTh = "อัมรา", LastNameTh = "รุ่งเรือง", StartDate = start.AddYears(-1) };
        db.Set<Employee>().Add(emp);
        var run = new PayrollRun
        {
            CompanyId = companyId, PayrollNumber = "PR-202609-M", Name = "เงินเดือน กันยายน 2569",
            Year = 2026, Month = 9, PeriodStart = start, PeriodEnd = end, PayDate = end, Status = "Calculated",
        };
        run.Details.Add(new PayrollDetail { CompanyId = companyId, EmployeeId = emp.Id, BaseSalary = 12000m, GrossIncome = 12000m, NetPay = 12000m, NetPaymentAccountCode = empPayCode });
        db.Set<PayrollRun>().Add(run);
        await db.SaveChangesAsync();
        return (run.Id, emp.Id);
    }

    [Fact]
    public async Task เงินเดือน_ค่าเริ่มต้นอ่านค่าตั้งบริษัท_ไม่ใช่บัญชีคุม11122()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);
        var (runId, _) = await SeedPayrollRunAsync(db, s.CompanyId, null);

        using (var r0 = DbTestDatabase.TryCreateContext()!)
        {
            var view0 = await new PayrollService(r0).GetPayrollRunAsync(s.CompanyId, runId);
            // ฝ่ายค้านรอบ 2 P2(a): ไม่มีค่าตั้ง + ธนาคารที่ผูกผังบัญชีเดียว ⇒ ผังของธนาคารนั้น (เดิม 11111 เงินสดรหัสต่ำสุดเงียบ ๆ) — ไม่ใช่ 11122 (บัญชีคุม)
            Assert.StartsWith("11122-001", view0.DefaultPaymentSourceLabel);
            Assert.Null(view0.DefaultPaymentSourceWarning);
        }
        using (var r1 = DbTestDatabase.TryCreateContext()!)
            await Settings(r1).UpdateSettingsAsync(s.CompanyId, SettingsReq(defaultPaymentAccountId: s.Acc["11122-001"]));
        using var r2 = DbTestDatabase.TryCreateContext()!;
        var view = await new PayrollService(r2).GetPayrollRunAsync(s.CompanyId, runId);
        Assert.StartsWith("11122-001", view.DefaultPaymentSourceLabel);   // ตัวเดียวกับนำส่ง สปส./ภาษี
    }

    [Fact]
    public async Task เงินเดือน_ไม่มีค่าตั้งและธนาคารไม่ใช่บัญชีเดียว_ไม่ตกไปเงินสด_เตือนให้เลือก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);
        // บัญชีธนาคารที่ผูกผังบัญชีที่สอง ⇒ ระบบเดาไม่ได้ว่าจ่ายจากบัญชีไหน
        db.BankAccounts.Add(new BankAccount { CompanyId = s.CompanyId, AccountName = "บัญชีที่สอง", BankName = "กรุงเทพ", AccountNumber = "2223334445",
            LinkedAccountId = s.Acc["11111"] });
        await db.SaveChangesAsync();
        var (runId, _) = await SeedPayrollRunAsync(db, s.CompanyId, null);

        using var r0 = DbTestDatabase.TryCreateContext()!;
        var view = await new PayrollService(r0).GetPayrollRunAsync(s.CompanyId, runId);
        Assert.Null(view.DefaultPaymentSourceLabel);
        Assert.Contains("แหล่งเงินเริ่มต้น", view.DefaultPaymentSourceWarning);
        Assert.Contains("ไม่เดา", view.DefaultPaymentSourceWarning);
        // ทิศตรงข้าม: เส้นอื่น (นำส่ง สปส./ภาษี) ยังใช้ผังเงินสดรหัสต่ำสุดตามเดิม แต่ไม่ใช่เงินสดย่อย
        Assert.Equal(s.Acc["11111"], await r0.ResolveSourceGlAsync(s.CompanyId, null, null));
    }

    [Fact]
    public async Task เงินเดือน_แหล่งจ่ายรายคนที่ปิดใช้แล้ว_หน้ารอบเตือน_ไม่แสดงเป็นค่าเริ่มต้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);
        var (runId, empId) = await SeedPayrollRunAsync(db, s.CompanyId, "11131");   // เช็คในมือ (ปิดใช้แล้ว)

        using var r1 = DbTestDatabase.TryCreateContext()!;
        var view = await new PayrollService(r1).GetPayrollRunAsync(s.CompanyId, runId);
        var line = Assert.Single(view.Details!);
        Assert.Contains("ใช้ไม่ได้แล้ว", line.NetPaymentAccountWarning);
        Assert.Contains("ระบบไม่สลับ", line.NetPaymentAccountWarning);
        Assert.Contains("11131", line.NetPaymentAccountLabel);

        // ทิศตรงข้าม: เลือกแหล่งจ่ายที่ใช้ได้ ⇒ คำเตือนหาย
        using var r2 = DbTestDatabase.TryCreateContext()!;
        var after = await new PayrollService(r2).SetEmployeePaymentAccountAsync(s.CompanyId, runId, empId, "11122-001");
        Assert.Null(Assert.Single(after.Details!).NetPaymentAccountWarning);
        Assert.Null(after.Notice);
    }

    [Fact]
    public async Task เงินเดือน_ตั้งแหล่งจ่ายรายคน_ผังปิดใช้ถูกบล็อก_ผังที่ไม่ใช่เงินบันทึกพร้อมคำเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);
        var (runId, empId) = await SeedPayrollRunAsync(db, s.CompanyId, null);

        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PayrollService(r1).SetEmployeePaymentAccountAsync(s.CompanyId, runId, empId, "11131"));
            Assert.Contains("ปิดใช้งาน", ex.Message);
        }
        using (var r2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PayrollService(r2).SetEmployeePaymentAccountAsync(s.CompanyId, runId, empId, "11122"));
            Assert.Contains("บัญชีคุม", ex.Message);   // PC#12: บัญชีคุมของธนาคารที่สร้างอัตโนมัติ
        }
        // คำตัดสินข้อ 150: ไม่ใช่ผังเงิน = เตือน ไม่บล็อก (เดิมบล็อกด้วย prefix ⇒ ผังของบริษัทเองตั้งไม่ได้เลย)
        using var r3 = DbTestDatabase.TryCreateContext()!;
        var resp = await new PayrollService(r3).SetEmployeePaymentAccountAsync(s.CompanyId, runId, empId, "51110");
        Assert.Contains("ไม่ใช่บัญชีเงินสด/ธนาคาร", resp.Notice);
        Assert.Equal("51110", Assert.Single(resp.Details!).NetPaymentAccountCode);
    }

    // ── ID#24 ใบสำคัญด้วยมือ ──

    [Fact]
    public async Task ใบสำคัญด้วยมือ_ผังปิดใช้และบัญชีคุมถูกบล็อก_ทางอัตโนมัติยังลงผังปิดใช้ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedStandardAsync(db);
        CreateJournalEntryRequest Req(Guid dr, Guid cr, Guid? branchId = null) => new(
            DateTime.UtcNow.Date, "ทดสอบ", null,
            new List<JournalLineRequest> { new(dr, 100m, 0m, "Dr"), new(cr, 0m, 100m, "Cr") },
            BranchId: branchId);

        var svc = new AccountingService(db);
        var inactive = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ValidateManualJournalAsync(s.CompanyId, Req(s.Acc["51110"], s.Acc["11131"])));
        Assert.Contains("บรรทัดที่ 2", inactive.Message);
        Assert.DoesNotContain(s.Acc["11131"].ToString(), inactive.Message);
        var group = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.ValidateManualJournalAsync(s.CompanyId, Req(s.Acc["51110"], s.Acc["111"])));
        Assert.Contains("บัญชีคุม", group.Message);
        // ทิศตรงข้าม: ผังปกติผ่าน
        await svc.ValidateManualJournalAsync(s.CompanyId, Req(s.Acc["51110"], s.Acc["11111"]));

        // ทางอัตโนมัติ (เช่น ปิดงวดสิ้นปีล้างยอดผังที่ปิดใช้แล้ว) ไม่ถูกด่านเข้ม — ยังลงได้
        var je = await svc.CreateJournalEntryAsync(s.CompanyId, Req(s.Acc["51110"], s.Acc["11131"]), "db-test");
        Assert.Equal(2, je.Lines.Count);

        // ผังของบริษัทอื่น = ไม่พบ ทั้งสองทาง · ข้อความไม่มี GUID
        var other = await SeedStandardAsync(db);
        var cross = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateJournalEntryAsync(s.CompanyId, Req(s.Acc["51110"], other.Acc["11111"]), "db-test"));
        Assert.Contains("ไม่พบบัญชีที่เลือก", cross.Message);
        Assert.DoesNotContain(other.Acc["11111"].ToString(), cross.Message);

        // สาขาของบริษัทอื่น = บล็อก (ไม่มี FK — เดิมเก็บได้)
        var foreignBranch = new Branch { CompanyId = other.CompanyId, Code = "00001", Name = "สาขาบริษัทอื่น" };
        db.Branches.Add(foreignBranch);
        await db.SaveChangesAsync();
        var br = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateJournalEntryAsync(s.CompanyId, Req(s.Acc["51110"], s.Acc["11111"], foreignBranch.Id), "db-test"));
        Assert.Contains("ไม่พบสาขา", br.Message);
    }
}
