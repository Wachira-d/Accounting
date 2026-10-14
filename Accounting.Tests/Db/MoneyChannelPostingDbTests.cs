using System.Text.Json;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs.Loan;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.PettyCash;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม F1 · 2026-10-14 (PAYMENT_CHANNEL_AUDIT ส่วนที่ 1 ข้อ 1 · ส่วนที่ 2 ข้อ 1/2) — รันเส้นเงินจริงบน PostgreSQL จาก<b>รูป JSON ที่หน้าเว็บส่ง</b>
///
/// <para>บั๊กต้นเรื่อง (คลาสเดียวกับรายการเกิดซ้ำ): หน้าเงินสดย่อยส่ง <c>BankAccount.Id</c> ไปในช่องผังบัญชี แล้วเซิร์ฟเวอร์เขียนตรงลง
/// <c>JournalEntryLine.AccountId</c> ⇒ FK ล้ม ผู้ใช้เห็น "ถูกอ้างอิงโดยข้อมูลอื่น…" · หน้าสินเชื่อส่งชื่อช่องไม่ตรงสัญญา ⇒ สร้างไม่ได้เลย</para>
/// <para>ล็อกทั้งสองทิศ: (ก) รหัสบัญชีธนาคารถูกแปลงเป็นผังที่ผูก (LinkedAccountId) ก่อนลง JE · (ข) รหัสผิดตาราง/ข้ามบริษัท/ไม่ผูกผัง
/// ⇒ ข้อความไทยพร้อมทางไปต่อ และ<b>ไม่มีอะไรถูกบันทึก</b> · (ค) กองทุนเก่าที่ไม่มีผัง ผูกผังได้แล้วใช้งานต่อได้ (ทางตันเดิม)</para>
/// </summary>
[Trait("Category", "Db")]
public class MoneyChannelPostingDbTests
{
    private readonly ITestOutputHelper _out;
    public MoneyChannelPostingDbTests(ITestOutputHelper output) => _out = output;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Seed(Guid CompanyId, Guid PettyGl, Guid ExpenseGl, Guid BankGl, Guid BankId, Guid UnlinkedBankId,
        Guid LoanGl, Guid InterestGl, Guid GroupGl, Guid OtherCompanyExpenseGl);

    private static ChartOfAccount Coa(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบช่องทางเงิน", TaxId = "0105556" + Random.Shared.Next(100000, 999999) };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105557" + Random.Shared.Next(100000, 999999) };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var group = Coa(company.Id, "111", "เงินสดและรายการเทียบเท่าเงินสด", AccountType.Asset, level: 3);
        var petty = Coa(company.Id, PettyCashJePlan.DefaultFundAccountCode, "เงินสดย่อย", AccountType.Asset);
        var bankGl = Coa(company.Id, "11121", "เงินฝากกระแสรายวัน", AccountType.Asset);
        var expense = Coa(company.Id, "53110", "ค่าเครื่องเขียน", AccountType.Expense);
        var loanGl = Coa(company.Id, "22100", "เงินกู้ยืมระยะยาว", AccountType.Liability);
        var interest = Coa(company.Id, LoanPostingPlan.DefaultInterestExpenseCode, "ดอกเบี้ยจ่าย - เงินกู้ยืม", AccountType.Expense);
        var otherExpense = Coa(other.Id, "53110", "ค่าเครื่องเขียน (บริษัทอื่น)", AccountType.Expense);
        db.ChartOfAccounts.AddRange(group, petty, bankGl, expense, loanGl, interest, otherExpense);
        await db.SaveChangesAsync();
        var bank = new BankAccount
        {
            CompanyId = company.Id, AccountName = "บัญชีหลัก", BankName = "กสิกรไทย", AccountNumber = "123-4-56789-0",
            AccountType = "Current", LinkedAccountId = bankGl.Id, IsActive = true,
        };
        var unlinked = new BankAccount
        {
            CompanyId = company.Id, AccountName = "บัญชีสำรอง", BankName = "กรุงเทพ", AccountNumber = "999-9-99999-9",
            AccountType = "Savings", LinkedAccountId = null, IsActive = true,
        };
        db.BankAccounts.AddRange(bank, unlinked);
        await db.SaveChangesAsync();
        return new Seed(company.Id, petty.Id, expense.Id, bankGl.Id, bank.Id, unlinked.Id, loanGl.Id, interest.Id, group.Id, otherExpense.Id);
    }

    private static async Task<List<JournalEntryLine>> LinesAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid? jeId)
    {
        Assert.NotNull(jeId);
        var je = await db.JournalEntries.AsNoTracking().Include(j => j.Lines)
            .SingleAsync(j => j.Id == jeId && j.CompanyId == companyId);
        return je.Lines.ToList();
    }

    // ══════════════════════════════ เงินสดย่อย ══════════════════════════════

    [Fact]
    public async Task เงินสดย่อย_เปิดกองทุนจากหน้าเว็บ_เติมจากบัญชีธนาคาร_เบิก_ลงJEถูกผังทุกขา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        // เปิดกองทุนแบบหน้าเว็บที่ไม่ได้แตะช่องผัง — เซิร์ฟเวอร์ใช้ผังมาตรฐาน 11112 (เดิม linkedAccountId: null ⇒ ทางตัน)
        Guid fundId;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var body = JsonSerializer.Deserialize<SmeOperationsController.CreateFundRequest>(
                """{"name":"สำนักงานใหญ่","custodianUserId":null,"imprestAmount":5000,"linkedAccountId":null}""", Web)!;
            var fund = await new PettyCashService(req).CreateFundAsync(s.CompanyId, body.Name, body.CustodianUserId,
                body.ImprestAmount, body.LinkedAccountId);
            Assert.Equal(s.PettyGl, fund.LinkedAccountId);
            fundId = fund.Id;
        }

        // เติมเงิน: หน้าเว็บส่ง sourceBankAccountId = BankAccounts.Id ⇒ JE เครดิต "ผังที่ผูก" ไม่ใช่รหัสบัญชีธนาคาร
        PettyCashTransaction topUp;
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var body = JsonSerializer.Deserialize<SmeOperationsController.ReplenishRequest>(
                $$"""{"amount":5000,"notes":null,"txnDate":"2026-10-14T03:00:00Z","sourceBankAccountId":"{{s.BankId}}","sourceAccountId":null}""", Web)!;
            topUp = await new PettyCashService(req).ReplenishAsync(s.CompanyId, fundId, body.Amount, body.Notes, body.TxnDate,
                body.SourceAccountId, body.SourceBankAccountId);
        }
        var topLines = await LinesAsync(db, s.CompanyId, topUp.JournalEntryId);
        Assert.Contains(topLines, l => l.AccountId == s.PettyGl && l.DebitAmount == 5000m);
        Assert.Contains(topLines, l => l.AccountId == s.BankGl && l.CreditAmount == 5000m);
        Assert.DoesNotContain(topLines, l => l.AccountId == s.BankId);

        // เบิก: Dr ค่าใช้จ่าย / Cr ผังของกองทุน · ยอดคงเหลือลด
        PettyCashTransaction pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await new PettyCashService(req).DisburseAsync(s.CompanyId, fundId, 350m, "ซื้อกระดาษ", s.ExpenseGl,
                "RC-001", new DateTime(2026, 10, 14));
        var payLines = await LinesAsync(db, s.CompanyId, pay.JournalEntryId);
        Assert.Contains(payLines, l => l.AccountId == s.ExpenseGl && l.DebitAmount == 350m);
        Assert.Contains(payLines, l => l.AccountId == s.PettyGl && l.CreditAmount == 350m);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(4650m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fundId && f.CompanyId == s.CompanyId)).CurrentBalance);
    }

    [Fact]
    public async Task เงินสดย่อย_รหัสผิดตาราง_ข้ามบริษัท_ไม่ผูกผัง_ปฏิเสธเป็นไทยและไม่บันทึกอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var fund = new PettyCashFund { CompanyId = s.CompanyId, Name = "หน้าร้าน", ImprestAmount = 3000m, CurrentBalance = 1000m, LinkedAccountId = s.PettyGl };
        db.PettyCashFunds.Add(fund);
        await db.SaveChangesAsync();

        async Task<BusinessRuleException> Fails(Func<PettyCashService, Task> act)
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            return await Assert.ThrowsAsync<BusinessRuleException>(() => act(new PettyCashService(req)));
        }

        // หน้าเว็บรุ่นเก่า: BankAccounts.Id ในช่องผังบัญชี ⇒ ข้อความ "ไม่พบผังบัญชี" (ไม่ใช่ FK 409 "ลบหรือเปลี่ยนไม่ได้")
        var wrongTable = await Fails(svc => svc.ReplenishAsync(s.CompanyId, fund.Id, 500m, null, DateTime.UtcNow, sourceAccountId: s.BankId));
        Assert.Contains("ไม่พบผังบัญชี", wrongTable.Message);
        Assert.Contains("เติมจากบัญชี", wrongTable.Message);
        // บัญชีธนาคารที่ยังไม่ผูกผัง ⇒ บอกให้ผูกที่หน้าบัญชีธนาคาร (ไม่ใช่ "ไม่พบ")
        var unlinked = await Fails(svc => svc.ReplenishAsync(s.CompanyId, fund.Id, 500m, null, DateTime.UtcNow, sourceBankAccountId: s.UnlinkedBankId));
        Assert.Contains("ยังไม่ได้ผูกผังบัญชี", unlinked.Message);
        // ผังค่าใช้จ่ายของบริษัทอื่น / บัญชีคุม ⇒ ปฏิเสธ
        var crossTenant = await Fails(svc => svc.DisburseAsync(s.CompanyId, fund.Id, 100m, "ค่ารถ", s.OtherCompanyExpenseGl, "R1", DateTime.UtcNow));
        Assert.Contains("บัญชีค่าใช้จ่าย", crossTenant.Message);
        await Fails(svc => svc.DisburseAsync(s.CompanyId, fund.Id, 100m, "ค่ารถ", s.GroupGl, "R1", DateTime.UtcNow));
        // เงินไม่พอ ⇒ ไทย + ทางไปต่อ (เดิม "Insufficient petty cash…")
        var insufficient = await Fails(svc => svc.DisburseAsync(s.CompanyId, fund.Id, 99999m, "ค่ารถ", s.ExpenseGl, "R1", DateTime.UtcNow));
        Assert.Contains("เติมเงิน", insufficient.Message);
        // กองทุนไม่พบ ⇒ ไทย (เดิม "Fund not found.")
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var nf = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                new PettyCashService(req).DisburseAsync(s.CompanyId, Guid.NewGuid(), 10m, "x", s.ExpenseGl, "R", DateTime.UtcNow));
            Assert.Contains("ไม่พบกองทุน", nf.Message);
        }

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1000m, (await check.PettyCashFunds.AsNoTracking().SingleAsync(f => f.Id == fund.Id)).CurrentBalance);
        Assert.False(await check.PettyCashTransactions.AnyAsync(t => t.CompanyId == s.CompanyId && t.FundId == fund.Id));
    }

    [Fact]
    public async Task เงินสดย่อย_กองทุนเก่าไม่มีผัง_ผูกผังผ่านการแก้กองทุนแล้วเติมเงินได้_ทางตันเดิมหายไป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var legacy = new PettyCashFund { CompanyId = s.CompanyId, Name = "กองทุนเก่า", ImprestAmount = 2000m, LinkedAccountId = null };
        db.PettyCashFunds.Add(legacy);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).ReplenishAsync(s.CompanyId, legacy.Id, 2000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId));
            Assert.Equal(PettyCashJePlan.RuleNoFundAccount, ex.RuleCode);
            Assert.Contains("แก้ไข / ผูกผังบัญชี", ex.Message);   // ชี้ปุ่มที่มีจริงบนหน้า
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            // ผังของกองทุนต้องเป็นสินทรัพย์ — ผังค่าใช้จ่ายถูกปฏิเสธ
            await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).UpdateFundAsync(s.CompanyId, legacy.Id, null, null, s.ExpenseGl));
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new PettyCashService(req).UpdateFundAsync(s.CompanyId, legacy.Id, null, null, s.PettyGl);
        PettyCashTransaction t;
        using (var req = DbTestDatabase.TryCreateContext()!)
            t = await new PettyCashService(req).ReplenishAsync(s.CompanyId, legacy.Id, 2000m, null, DateTime.UtcNow, sourceBankAccountId: s.BankId);
        var lines = await LinesAsync(db, s.CompanyId, t.JournalEntryId);
        Assert.Contains(lines, l => l.AccountId == s.PettyGl && l.DebitAmount == 2000m);

        // มีรายการลงบัญชีแล้ว ⇒ เปลี่ยนผังของกองทุนไม่ได้ (ยอดเดิมอยู่ผังเก่า) — พร้อมทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PettyCashService(req).UpdateFundAsync(s.CompanyId, legacy.Id, null, null, s.BankGl));
            Assert.Contains("สมุดรายวันทั่วไป", ex.Message);
        }
        // ทิศตรงข้าม: แก้ชื่อ/วงเงินได้ตามปกติ
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("กองทุนเก่า (แก้ชื่อ)", (await new PettyCashService(req).UpdateFundAsync(s.CompanyId, legacy.Id, "กองทุนเก่า (แก้ชื่อ)", 2500m, null)).Name);
    }

    // ══════════════════════════════ สินเชื่อ ══════════════════════════════

    [Fact]
    public async Task สินเชื่อ_สร้างจากรูปคำขอของหน้าเว็บ_รับเงินเข้าผังที่ผูกของบัญชีธนาคาร_ชำระใช้บัญชีของสินเชื่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        // JSON เดียวกับที่ loans.html → Page.save() ส่ง
        var json = $$"""
            {"name":"เงินกู้ซื้อเครื่องจักร","loanType":"BankLoan","lender":"ธนาคารกสิกรไทย","principalAmount":120000,
             "interestRate":6,"interestType":"Fixed","termMonths":12,"disbursementDate":"2026-10-01","firstPaymentDate":"2026-11-01",
             "repaymentFrequency":"Monthly","loanAccountId":"{{s.LoanGl}}","interestExpenseAccountId":"{{s.InterestGl}}",
             "bankAccountId":"{{s.BankId}}","moneyAccountId":null,"postDisbursementEntry":true}
            """;
        var request = JsonSerializer.Deserialize<CreateLoanRequest>(json, Web)!;
        LoanResponse created;
        using (var req = DbTestDatabase.TryCreateContext()!)
            created = await new LoanService(req).CreateAsync(s.CompanyId, request);

        Assert.Equal(s.BankGl, created.MoneyAccountId);      // เก็บผังที่ผูก ไม่ใช่ BankAccounts.Id
        Assert.Equal(new DateTime(2027, 10, 1), created.MaturityDate.Date);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var je = await check.JournalEntries.AsNoTracking().Include(j => j.Lines)
                .SingleAsync(j => j.CompanyId == s.CompanyId && j.EntryNumber.StartsWith("LD-"));
            Assert.Contains(je.Lines, l => l.AccountId == s.BankGl && l.DebitAmount == 120000m);
            Assert.Contains(je.Lines, l => l.AccountId == s.LoanGl && l.CreditAmount == 120000m);
        }

        // บันทึกชำระโดยไม่เลือกบัญชี ⇒ ใช้บัญชีของสินเชื่อ (เดิม 11122/111x ตัวแรกเสมอ)
        LoanPaymentResponse paid;
        using (var req = DbTestDatabase.TryCreateContext()!)
            paid = await new LoanService(req).MakePaymentAsync(s.CompanyId, created.Id,
                new MakeLoanPaymentRequest(new DateTime(2026, 11, 1), Amount: 10600m, PrincipalPortion: 10000m, InterestPortion: 600m), "db-test");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var lp = await check.LoanPayments.AsNoTracking().SingleAsync(p => p.Id == paid.Id && p.CompanyId == s.CompanyId);
            var lines = await LinesAsync(check, s.CompanyId, lp.JournalEntryId);
            Assert.Contains(lines, l => l.AccountId == s.BankGl && l.CreditAmount == 10600m);
            Assert.Contains(lines, l => l.AccountId == s.LoanGl && l.DebitAmount == 10000m);
            Assert.Contains(lines, l => l.AccountId == s.InterestGl && l.DebitAmount == 600m);
        }
    }

    [Fact]
    public async Task สินเชื่อ_คำขอรูปเก่าของหน้าเว็บ_และผังผิด_ปฏิเสธเป็นไทย_ไม่สร้างสินเชื่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        async Task<BusinessRuleException> Fails(CreateLoanRequest r)
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            return await Assert.ThrowsAsync<BusinessRuleException>(() => new LoanService(req).CreateAsync(s.CompanyId, r));
        }

        // รูปเก่า (loanNumber/lenderName/startDate/maturityDate) — ไม่มีชื่อ/วันรับเงิน ⇒ ข้อความบอกช่อง ไม่ใช่วันที่ 0001-01-01
        var old = JsonSerializer.Deserialize<CreateLoanRequest>(
            """{"loanNumber":"L-1","loanType":"BankLoan","lenderName":"ธ.","principalAmount":1000,"interestRate":5,"interestType":"Fixed","termMonths":12,"startDate":"2026-10-01","maturityDate":"2027-10-01","repaymentFrequency":"Monthly"}""", Web)!;
        Assert.Contains("ชื่อสินเชื่อ", (await Fails(old)).Message);

        CreateLoanRequest Good(Guid? loanGl, Guid? bankId, Guid? moneyGl = null, bool? post = true) => new(
            "เงินกู้ทดสอบ", "BankLoan", null, null, 1000m, 5m, "Fixed", 12, new DateTime(2026, 10, 1), null, "Monthly",
            loanGl, s.InterestGl, bankId, moneyGl, post);
        // ผังเงินกู้เป็นผังค่าใช้จ่าย ⇒ ชนิดไม่ตรงช่อง
        Assert.Contains("ผังบัญชีเงินกู้", (await Fails(Good(s.ExpenseGl, s.BankId))).Message);
        // ส่งผังบัญชีมาในช่องบัญชีธนาคาร (สัญญาเดิมของ Loan.BankAccountId) ⇒ "ไม่พบบัญชีธนาคาร" ไม่ใช่ FK
        Assert.Contains("ไม่พบบัญชีธนาคาร", (await Fails(Good(s.LoanGl, s.BankGl))).Message);
        // ขอลงบัญชีแต่ไม่มีช่องทางเงิน ⇒ ปฏิเสธ (เดิมข้าม JE เงียบ ๆ)
        Assert.Contains("บัญชีรับ/จ่ายเงินกู้", (await Fails(Good(s.LoanGl, null))).Message);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.False(await check.Loans.AnyAsync(l => l.CompanyId == s.CompanyId));

        // ทิศตรงข้าม: สินเชื่อเดิมที่ยอดยกมาแล้ว — ไม่ลงบัญชี ไม่ต้องมีผัง สร้างได้ ไม่มี JE
        LoanResponse opening;
        using (var req = DbTestDatabase.TryCreateContext()!)
            opening = await new LoanService(req).CreateAsync(s.CompanyId, Good(null, null, post: false));
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.False(await check.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.EntryNumber.StartsWith("LD-")));

        // บันทึกชำระสินเชื่อที่ยังไม่มีบัญชีเงิน ⇒ บอกให้เลือก “จ่ายจากบัญชี” (เดิมจ่ายจาก 11122 เงียบ ๆ)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            await new LoanService(req).UpdateAsync(s.CompanyId, opening.Id, new UpdateLoanRequest(null, null, null, null,
                LoanAccountId: s.LoanGl, InterestExpenseAccountId: s.InterestGl));
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new LoanService(req).MakePaymentAsync(s.CompanyId, opening.Id,
                new MakeLoanPaymentRequest(new DateTime(2026, 11, 1), Amount: 100m, PrincipalPortion: 100m, InterestPortion: 0m), "db-test"));
            Assert.Equal(LoanPostingPlan.RuleNoMoneyAccount, ex.RuleCode);
        }
        // เลือกบัญชีที่จ่ายมากับการชำระ ⇒ ผ่าน และลงผังที่ผูกของธนาคาร
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var p = await new LoanService(req).MakePaymentAsync(s.CompanyId, opening.Id,
                new MakeLoanPaymentRequest(new DateTime(2026, 11, 1), Amount: 100m, PrincipalPortion: 100m, InterestPortion: 0m,
                    BankAccountId: s.BankId), "db-test");
            Assert.Equal(100m, p.TotalPaid);
        }
    }
}
