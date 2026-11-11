using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 259 แพ็ก P4/P6 "เงินรอจัดสรร ต่อจนจบ" (DECISIONS 259-1-x) — ผ่านเมธอดจริงบน PostgreSQL ทั้งสองทิศ:
/// <list type="bullet">
/// <item>คืนเงิน: ยอดคงเหลือ 0 · ผัง 21630 เป็นศูนย์ · ยอดธนาคารลด · ยกเลิกการชำระถูกบล็อกจนยกเลิกการคืนเงิน · ยกเลิกแล้วทุกอย่างกลับ</item>
/// <item>เกินยอดที่ใช้ได้ (มีการจองให้ใบร่าง) = ปฏิเสธ ไม่มีอะไรถูกเขียน · ใบร่างถูกลบ = การจองหมดผลเอง</item>
/// <item>ตัดเศษ ≤ เพดาน ได้ · เกินเพดาน = ปฏิเสธ · กดซ้ำด้วยกุญแจเดิม = ไม่ทำซ้ำ</item>
/// <item>อนุมัติใบที่ออกจากเงินรอจัดสรร ⇒ จัดสรรเอง · จัดสรรไม่ได้ ⇒ ป้ายบนใบ + ข้อความ + การจองถูกปล่อย (เงินไม่ค้างเงียบ ๆ)</item>
/// <item>ยกเลิกใบมัดจำที่ย้ายมา ⇒ เงินกลับเป็นเงินรอจัดสรร · คิว Autopilot (ค้างเกิน 30 วัน) · ใบเก่าที่ปิดหลายใบแต่ไม่มีใบเสร็จ</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class CustomerCreditRound259DbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerCreditRound259DbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid CustomerId, Guid ArId, Guid RevenueId, Guid BankId, Guid BankGlId, Guid RoundingId);

    private static readonly DateTime Today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบเงินรอจัดสรร 259", TaxId = "0105556000025", IsVatRegistered = false };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var bankGl = Acc(company.Id, "11122-001", "เงินฝากออมทรัพย์ - กสิกร", AccountType.Asset, level: 5);
        var ar = Acc(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var group216 = Acc(company.Id, "216", "เงินมัดจำและเงินรับล่วงหน้าอื่น", AccountType.Liability, level: 3);
        var revenue = Acc(company.Id, "41100", "รายได้จากการให้บริการ", AccountType.Revenue);
        var rounding = Acc(company.Id, DocumentRounding.AccountCode, "ผลต่างจากการปัดเศษ", AccountType.Expense);
        var cash = Acc(company.Id, "111", "เงินสด", AccountType.Asset);
        var deposit = Acc(company.Id, "21712", "ค่าสินค้ารับล่วงหน้า", AccountType.Liability);
        db.ChartOfAccounts.AddRange(bankGl, ar, group216, revenue, rounding, cash, deposit);
        await db.SaveChangesAsync();
        var bank = new BankAccount
        {
            CompanyId = company.Id, AccountName = "ออมทรัพย์หลัก", BankName = "กสิกร", AccountNumber = "1234567890",
            LinkedAccountId = bankGl.Id, CurrentBalance = 0m, IsActive = true,
        };
        db.BankAccounts.Add(bank);
        var customer = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า ก จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson, IsCustomer = true,
            BranchCode = "00000", Address = "1 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพมหานคร 10110" };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id, ar.Id, revenue.Id, bank.Id, bankGl.Id, rounding.Id);
    }

    /// <summary>ใบแจ้งหนี้ขาย (ไม่มี VAT) อนุมัติแล้ว + JE ตอนออกใบ (ตั้งลูกหนี้) · <paramref name="status"/> = ร่างได้ (ไม่มี JE)</summary>
    private static async Task<Document> InvoiceAsync(Accounting.Data.AccountingDbContext db, Seed s, decimal amount, DateTime? date = null,
        DocumentStatus status = DocumentStatus.Approved, bool withJournal = true)
    {
        var no = (status == DocumentStatus.Draft ? "DRAFT-" : "INV-") + Guid.NewGuid().ToString("N")[..8];
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentType = DocumentType.Invoice, DocumentNumber = no,
            Status = status, DocumentDate = date ?? Today, SubTotal = amount, TotalAmount = amount, BalanceDue = amount,
            Currency = "THB", WhtCertAutoIssue = false, BankAccountId = s.BankId,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = amount, Amount = amount });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        if (status == DocumentStatus.Draft || !withJournal) return d;
        await IssueJournalAsync(db, s, d);
        return d;
    }

    private static async Task IssueJournalAsync(Accounting.Data.AccountingDbContext db, Seed s, Document d)
    {
        var je = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "SV-" + d.DocumentNumber, EntryDate = d.DocumentDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.Sales, Description = "ออกใบ " + d.DocumentNumber, Reference = d.DocumentNumber,
            TotalDebit = d.TotalAmount, TotalCredit = d.TotalAmount, SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.ArId, DebitAmount = d.TotalAmount, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.RevenueId, CreditAmount = d.TotalAmount, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
    }

    private static DocumentService Docs(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static CustomerCreditService Credits(Accounting.Data.AccountingDbContext db)
        => new(db, Docs(db), new AccountingService(db), NullLogger<CustomerCreditService>.Instance);

    /// <summary>รับเงิน 1,000 + <paramref name="excess"/> ปิดใบ 1,000 ⇒ เงินรอจัดสรร = excess</summary>
    private static async Task<PaymentResponse> CreditAsync(Accounting.Data.AccountingDbContext db, Seed s, decimal excess,
        UnappliedReason reason = UnappliedReason.Overpaid, DateTime? on = null)
    {
        var day = on ?? Today;
        var first = await InvoiceAsync(db, s, 1_000m, day);
        using var req = DbTestDatabase.TryCreateContext()!;
        return await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId,
            new CreatePaymentRequest(first.Id, day, 1_000m + excess, PaymentMethod.BankTransfer, "SLIP-259", null, null,
                Allocations: new List<PaymentAllocationRequest> { new(first.Id, 1_000m) },
                ExcessReason: reason, ExcessNote: "ทดสอบรอบ 259"), "tester");
    }

    private static async Task<decimal> GlCreditNetAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid accountId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == companyId && l.AccountId == accountId
                        && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                  select (decimal?)(l.CreditAmount - l.DebitAmount)).SumAsync() ?? 0m;

    private static async Task<Guid> SuspenseAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == UnappliedReceiptAccount.Code).Select(a => a.Id).SingleAsync();

    private static async Task<BusinessRuleException> RefusedAsync(Func<Task> act, string ruleCode)
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(act);
        Assert.Equal(ruleCode, ex.RuleCode);
        return ex;
    }

    [Fact]
    public async Task คืนเงิน_ยอดคงเหลือศูนย์_ผังพักศูนย์_ธนาคารลด_ยกเลิกการชำระถูกบล็อก_ยกเลิกการคืนแล้วกลับครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m);
        decimal bankBefore;
        using (var c = DbTestDatabase.TryCreateContext()!)
            bankBefore = await c.BankAccounts.AsNoTracking().Where(b => b.Id == s.BankId).Select(b => b.CurrentBalance).SingleAsync();

        CustomerCreditMovementResult refund;
        using (var req = DbTestDatabase.TryCreateContext()!)
            refund = await Credits(req).RefundAsync(s.CompanyId, pay.Id,
                new CustomerCreditRefundRequest(600m, s.BankId, "TRX-REFUND-01", ExpectedUnappliedBefore: 600m), "tester");
        Assert.Equal(0m, refund.RemainingUnapplied);
        Guid suspense;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            suspense = await SuspenseAsync(c, s.CompanyId);
            Assert.Equal(0m, await GlCreditNetAsync(c, s.CompanyId, suspense));
            Assert.Equal(bankBefore - 600m, await c.BankAccounts.AsNoTracking().Where(b => b.Id == s.BankId).Select(b => b.CurrentBalance).SingleAsync());
            var je = await c.JournalEntries.AsNoTracking().SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == pay.PaymentNumber + "#R1");
            Assert.Equal(JournalType.CashPayments, je.JournalType);
            Assert.True(await c.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "PaymentCreditMovement"
                && a.NewValues!.Contains(CustomerCreditMovementRules.RefundRuleCode)));
            var list = await Credits(c).ListAsync(s.CompanyId, null, includeResolved: true);
            Assert.Equal(0m, list.GlDifference);
            var row = list.Items.Single(r => r.PaymentId == pay.Id);
            Assert.True(Assert.Single(row.Movements!).CanUndo);
        }

        // ยกเลิกการชำระทั้งรายการ = ปฏิเสธ (เงินคืนลูกค้าไปแล้ว — ต้องยกเลิกการคืนก่อน) · ไม่มีอะไรถูกกลับ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Docs(req).VoidPaymentAsync(s.CompanyId, pay.Id), CustomerCreditMovementRules.VoidBlockRuleCode);

        // ยกเลิกการคืนเงิน ⇒ JE ถูกกลับ · ยอดธนาคารกลับ · เงินรอจัดสรรกลับ 600 · แล้วจึงยกเลิกการชำระได้
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var undone = await Credits(req).UndoMovementAsync(s.CompanyId, pay.Id, refund.MovementId, "โอนคืนผิดบัญชี ธนาคารตีกลับ", "tester");
            Assert.Equal(600m, undone.RemainingUnapplied);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(600m, await GlCreditNetAsync(c, s.CompanyId, suspense));
            Assert.Equal(bankBefore, await c.BankAccounts.AsNoTracking().Where(b => b.Id == s.BankId).Select(b => b.CurrentBalance).SingleAsync());
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).VoidPaymentAsync(s.CompanyId, pay.Id);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0m, await GlCreditNetAsync(c, s.CompanyId, suspense));
    }

    [Fact]
    public async Task เกินยอดที่ใช้ได้เพราะจองให้ใบร่าง_ปฏิเสธไม่มีอะไรถูกเขียน_ลบใบร่างแล้วการจองหมดผล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m);
        var draft = await InvoiceAsync(db, s, 400m, status: DocumentStatus.Draft);
        db.PaymentCreditMovements.Add(new PaymentCreditMovement
        {
            CompanyId = s.CompanyId, PaymentId = pay.Id, Kind = nameof(PaymentCreditMovementKind.ToInvoice), Amount = 400m,
            MovedAt = Today, TargetDocumentId = draft.Id, CreatedBy = "tester",
        });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).RefundAsync(s.CompanyId, pay.Id, new CustomerCreditRefundRequest(300m, s.BankId, "TRX-02"), "tester"),
                CustomerCreditMovementRules.RefundRuleCode);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference!.StartsWith(pay.PaymentNumber + "#")));
            Assert.Equal(1, await c.PaymentCreditMovements.AsNoTracking().CountAsync(m => m.PaymentId == pay.Id));
            var row = (await Credits(c).ListAsync(s.CompanyId, null, false)).Items.Single(r => r.PaymentId == pay.Id);
            Assert.Equal(600m, row.UnappliedAmount);   // การจองไม่ตัดยอดคงเหลือ
            Assert.Equal(200m, row.Available);         // แต่ลดยอดที่ใช้ได้
        }
        // ยอดที่ใช้ได้ (200) คืนได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(400m, (await Credits(req).RefundAsync(s.CompanyId, pay.Id, new CustomerCreditRefundRequest(200m, s.BankId, "TRX-03"), "tester")).RemainingUnapplied);

        // ใบร่างถูกลบ ⇒ การจองหมดผลเอง (ไม่ต้องมีงานล้าง) — ยอดที่ใช้ได้ = คงเหลือ
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.Documents.Where(d => d.Id == draft.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsDeleted, true));
            var row = (await Credits(c).ListAsync(s.CompanyId, null, false)).Items.Single(r => r.PaymentId == pay.Id);
            Assert.Equal(400m, row.Available);
        }
    }

    [Fact]
    public async Task ตัดเศษ_ไม่เกินเพดานได้_เกินเพดานปฏิเสธ_กดซ้ำกุญแจเดิมไม่ทำซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var crumb = await CreditAsync(db, s, 0.75m);
        var big = await CreditAsync(db, s, 5m);

        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).WriteOffAsync(s.CompanyId, big.Id, new CustomerCreditWriteOffRequest(5m, Note: "ลูกค้าโอนเกินเศษ"), "tester"), CustomerCreditMovementRules.WriteOffRuleCode);

        var key = "wo-" + Guid.NewGuid().ToString("N");
        CustomerCreditMovementResult first, second;
        using (var req = DbTestDatabase.TryCreateContext()!)
            first = await Credits(req).WriteOffAsync(s.CompanyId, crumb.Id, new CustomerCreditWriteOffRequest(0.75m, Note: "ลูกค้าโอนเกินเศษสตางค์", IdempotencyKey: key), "tester");
        using (var req = DbTestDatabase.TryCreateContext()!)
            second = await Credits(req).WriteOffAsync(s.CompanyId, crumb.Id, new CustomerCreditWriteOffRequest(0.75m, Note: "ลูกค้าโอนเกินเศษสตางค์", IdempotencyKey: key), "tester");
        Assert.Equal(first.MovementId, second.MovementId);
        Assert.Contains(second.Notices, n => n.Contains("กดซ้ำ", StringComparison.Ordinal));
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await c.PaymentCreditMovements.AsNoTracking().CountAsync(m => m.PaymentId == crumb.Id));
        Assert.Equal(0.75m, await GlCreditNetAsync(c, s.CompanyId, s.RoundingId));
        Assert.Equal(0m, await c.Payments.AsNoTracking().Where(p => p.Id == crumb.Id).Select(p => p.UnappliedAmount).SingleAsync());
        Assert.Equal(5m, await c.Payments.AsNoTracking().Where(p => p.Id == big.Id).Select(p => p.UnappliedAmount).SingleAsync());
    }

    [Fact]
    public async Task อนุมัติใบที่ออกจากเงินรอจัดสรร_จัดสรรเอง_และจัดสรรไม่ได้ก็บอกพร้อมป้ายบนใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        // (ก) ใบร่างที่จองเงินไว้ ถูกอนุมัติ (มี JE ตั้งลูกหนี้) ⇒ จัดสรรเอง · การจองถูกปล่อย · ใบปิด
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.UninvoicedSupply);
        var doc = await InvoiceAsync(db, s, 600m, status: DocumentStatus.Draft);
        db.PaymentCreditMovements.Add(new PaymentCreditMovement
        {
            CompanyId = s.CompanyId, PaymentId = pay.Id, Kind = nameof(PaymentCreditMovementKind.ToInvoice), Amount = 600m,
            MovedAt = Today, TargetDocumentId = doc.Id, CreatedBy = "tester",
        });
        await db.SaveChangesAsync();
        await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Approved));
        await IssueJournalAsync(db, s, doc);
        IReadOnlyList<string> notices;
        using (var req = DbTestDatabase.TryCreateContext()!)
            notices = await Credits(req).AfterDocumentApprovedAsync(s.CompanyId, doc.Id, "tester");
        Assert.Contains(notices, n => n.Contains("จัดสรรเงินรอจัดสรร", StringComparison.Ordinal));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(DocumentStatus.Paid, await c.Documents.AsNoTracking().Where(d => d.Id == doc.Id).Select(d => d.Status).SingleAsync());
            Assert.Equal(0m, await c.Payments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.UnappliedAmount).SingleAsync());
            Assert.NotNull(await c.PaymentCreditMovements.AsNoTracking().Where(m => m.PaymentId == pay.Id).Select(m => m.ReleasedAt).SingleAsync());
        }

        // (ข) ทิศตรงข้าม — ใบไม่มี JE ตั้งลูกหนี้ ⇒ จัดสรรไม่ได้: ข้อความ + ป้าย [CC-HOLD-FAILED] บนใบ · การจองถูกปล่อย · เงินยังอยู่ครบ
        var pay2 = await CreditAsync(db, s, 300m);
        var doc2 = await InvoiceAsync(db, s, 300m, status: DocumentStatus.Draft);
        db.PaymentCreditMovements.Add(new PaymentCreditMovement
        {
            CompanyId = s.CompanyId, PaymentId = pay2.Id, Kind = nameof(PaymentCreditMovementKind.ToInvoice), Amount = 300m,
            MovedAt = Today, TargetDocumentId = doc2.Id, CreatedBy = "tester",
        });
        await db.SaveChangesAsync();
        await db.Documents.Where(d => d.Id == doc2.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Approved));
        using (var req = DbTestDatabase.TryCreateContext()!)
            notices = await Credits(req).AfterDocumentApprovedAsync(s.CompanyId, doc2.Id, "tester");
        Assert.Contains(notices, n => n.StartsWith("[CC-HOLD-FAILED]", StringComparison.Ordinal));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Contains("[CC-HOLD-FAILED]", await c.Documents.AsNoTracking().Where(d => d.Id == doc2.Id).Select(d => d.InternalNotes).SingleAsync() ?? "",
                StringComparison.Ordinal);
            Assert.Equal(300m, await c.Payments.AsNoTracking().Where(p => p.Id == pay2.Id).Select(p => p.UnappliedAmount).SingleAsync());
            Assert.NotNull(await c.PaymentCreditMovements.AsNoTracking().Where(m => m.PaymentId == pay2.Id).Select(m => m.ReleasedAt).SingleAsync());
        }

        // (ค) ใบที่ไม่มีการจอง ⇒ ไม่มีอะไรเกิด (การอนุมัติทั่วไปไม่ถูกแตะ)
        var plain = await InvoiceAsync(db, s, 100m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Empty(await Credits(req).AfterDocumentApprovedAsync(s.CompanyId, plain.Id, "tester"));
    }

    [Fact]
    public async Task ยกเลิกใบมัดจำที่ย้ายจากเงินรอจัดสรร_เงินกลับเป็นเงินรอจัดสรร_ใบอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.Advance);
        var deposit = await InvoiceAsync(db, s, 600m, withJournal: false);   // แทนใบมัดจำ (ทดสอบเฉพาะการปล่อยขาออก)
        var other = await InvoiceAsync(db, s, 50m, withJournal: false);
        db.PaymentCreditMovements.Add(new PaymentCreditMovement
        {
            CompanyId = s.CompanyId, PaymentId = pay.Id, Kind = nameof(PaymentCreditMovementKind.ToDeposit), Amount = 600m,
            MovedAt = Today, TargetDocumentId = deposit.Id, CreatedBy = "tester",
        });
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var p = await c.Payments.SingleAsync(x => x.Id == pay.Id);
            await CustomerCreditLedger.RecomputeAsync(c, s.CompanyId, p);
            await c.SaveChangesAsync();
            Assert.Equal(0m, p.UnappliedAmount);
            Assert.NotNull(await CustomerCreditLedger.VoidBlockAsync(c, s.CompanyId, pay.Id, pay.PaymentNumber));
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await using var tx = await c.Database.BeginTransactionAsync();
            Assert.Equal(0, await CustomerCreditLedger.ReleaseForVoidedDocumentAsync(c, s.CompanyId, other.Id, "tester"));
            Assert.Equal(1, await CustomerCreditLedger.ReleaseForVoidedDocumentAsync(c, s.CompanyId, deposit.Id, "tester"));
            await tx.CommitAsync();
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(600m, await c.Payments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.UnappliedAmount).SingleAsync());
            Assert.Null(await CustomerCreditLedger.VoidBlockAsync(c, s.CompanyId, pay.Id, pay.PaymentNumber));
        }
    }

    [Fact]
    public async Task คิวAutopilot_เงินค้างเกิน30วันขึ้น_เงินใหม่ไม่ขึ้น_และใบเก่าที่ปิดหลายใบโดยไม่มีใบเสร็จขึ้นรายการ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var old = await CreditAsync(db, s, 700m, on: Today.AddDays(-40));
        var fresh = await CreditAsync(db, s, 800m);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var issues = await CustomerCreditWorkQueue.AutopilotIssuesAsync(c, s.CompanyId, DateTime.UtcNow);
            Assert.Contains(issues, i => i.Kind == "CustomerCreditAged" && i.Title.Contains(old.PaymentNumber, StringComparison.Ordinal));
            Assert.DoesNotContain(issues, i => i.Kind == "CustomerCreditAged" && i.Title.Contains(fresh.PaymentNumber, StringComparison.Ordinal));
        }

        // ใบเก่า (ก่อนรอบ 258): การรับชำระที่มีแถวจัดสรร · ใบปิดแล้ว · ไม่มีใบเสร็จ ⇒ ขึ้นรายการ · มีใบเสร็จแล้ว ⇒ ไม่ขึ้น
        var legacyDoc = await InvoiceAsync(db, s, 900m);
        await db.Documents.Where(d => d.Id == legacyDoc.Id).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 900m));
        var legacyPay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY-OLD-" + Guid.NewGuid().ToString("N")[..6], DocumentId = legacyDoc.Id,
            PaymentDate = Today.AddDays(-90), Amount = 900m, PaymentMethod = PaymentMethod.BankTransfer, CreatedBy = "tester",
            CreatedAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),   // ก่อนรอบ 258 แน่นอน (ทั้ง marker และวันตั้งต้น)
        };
        db.Payments.Add(legacyPay);
        await db.SaveChangesAsync();
        db.PaymentAllocations.Add(new PaymentAllocation { CompanyId = s.CompanyId, PaymentId = legacyPay.Id, DocumentId = legacyDoc.Id, AllocatedAmount = 900m });
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await CustomerCreditWorkQueue.LegacyMissingReceiptsAsync(c, s.CompanyId), r => r.DocumentId == legacyDoc.Id && r.PaymentId == legacyPay.Id);

        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentType = DocumentType.Receipt, DocumentNumber = "RE-" + Guid.NewGuid().ToString("N")[..6],
            Status = DocumentStatus.Approved, DocumentDate = Today, TotalAmount = 900m, Currency = "THB", WhtCertAutoIssue = false,
            SettlementPaymentId = legacyPay.Id, RelatedDocumentId = legacyDoc.Id, IsSettlementReceipt = true,
        });
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(await CustomerCreditWorkQueue.LegacyMissingReceiptsAsync(c, s.CompanyId), r => r.DocumentId == legacyDoc.Id);
    }

    // ═════════ ฝ่ายค้านรอบ 259 (erp-review/2026-11-11/review-259-pack-A.md) ═════════

    /// <summary>DocumentService เต็ม (สร้าง + อนุมัติได้) · <paramref name="hooks"/> = แทนผลข้างเคียงหลังอนุมัติ (จำลองล้มหลัง commit)</summary>
    private static DocumentService FullDocs(Accounting.Data.AccountingDbContext db, IIssuedDocumentHooks? hooks = null)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!,
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            null!, issuedHooks: hooks);

    private static CustomerCreditService FullCredits(Accounting.Data.AccountingDbContext db, IIssuedDocumentHooks? hooks = null)
        => new(db, FullDocs(db, hooks), new AccountingService(db), NullLogger<CustomerCreditService>.Instance);

    [Fact]
    public async Task H1_คืนเงินแล้วยกเลิกใบต้นทาง_ถูกปฏิเสธพร้อมเหตุ_ยกเลิกการคืนก่อนแล้วยกเลิกใบได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 500m);
        CustomerCreditMovementResult refund;
        using (var req = DbTestDatabase.TryCreateContext()!)
            refund = await Credits(req).RefundAsync(s.CompanyId, pay.Id, new CustomerCreditRefundRequest(500m, s.BankId, "TRX-H1"), "tester");

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await RefusedAsync(() => Docs(req).VoidDocumentAsync(s.CompanyId, pay.DocumentId), CustomerCreditMovementRules.VoidBlockRuleCode);
            Assert.Contains("คืนเงินลูกค้า", ex.Message, StringComparison.Ordinal);
        }
        Guid suspense;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            suspense = await SuspenseAsync(c, s.CompanyId);
            Assert.Equal(0m, await GlCreditNetAsync(c, s.CompanyId, suspense));   // ไม่มีอะไรถูกกลับ
            Assert.NotEqual(DocumentStatus.Voided, await c.Documents.AsNoTracking().Where(d => d.Id == pay.DocumentId).Select(d => d.Status).SingleAsync());
        }

        // ทิศตรงข้าม: ไม่มีขาออกที่มีผล ⇒ ยกเลิกใบได้ (cascade การรับชำระตามเดิม) — แต่ยังมีเงินรอจัดสรร 500 ⇒ ด่านเดิม CC-VOID-DOC-CREDIT
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UndoMovementAsync(s.CompanyId, pay.Id, refund.MovementId, "คืนผิดบัญชี ธนาคารตีกลับ", "tester");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Docs(req).VoidDocumentAsync(s.CompanyId, pay.DocumentId), "CC-VOID-DOC-CREDIT");
        // ใบที่ไม่มีการรับชำระที่มีขาออก ⇒ ยกเลิกได้ตามปกติ
        var plain = await InvoiceAsync(db, s, 300m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).VoidDocumentAsync(s.CompanyId, plain.Id);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(DocumentStatus.Voided, await c.Documents.AsNoTracking().Where(d => d.Id == plain.Id).Select(d => d.Status).SingleAsync());
    }

    [Fact]
    public async Task M3_กลับJEคืนเงินด้วยมือ_ถูกปฏิเสธ_ชี้ทางยกเลิกรายการ_JEอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 400m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).RefundAsync(s.CompanyId, pay.Id, new CustomerCreditRefundRequest(400m, s.BankId, "TRX-M3"), "tester");
        Guid jeId;
        using (var c = DbTestDatabase.TryCreateContext()!)
            jeId = await c.JournalEntries.AsNoTracking().Where(j => j.CompanyId == s.CompanyId && j.Reference == pay.PaymentNumber + "#R1").Select(j => j.Id).SingleAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await RefusedAsync(() => new AccountingService(req).ReverseJournalEntryAsync(s.CompanyId, jeId), CustomerCreditMovementRules.UndoRuleCode);
            Assert.Contains("ยกเลิกรายการ", ex.Message, StringComparison.Ordinal);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Null(await c.JournalEntries.AsNoTracking().Where(j => j.Id == jeId).Select(j => j.ReversedByEntryId).SingleAsync());
        Assert.Null(await CustomerCreditLedger.ManualReverseBlockAsync(db, s.CompanyId, Guid.NewGuid()));
    }

    [Fact]
    public async Task ToDeposit_สำเร็จ_ใบมัดจำขาเงินเข้าเป็นผังพัก_ยอดตัด_ผูกใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.Advance);
        CustomerCreditMovementResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await FullCredits(req).ToDepositAsync(s.CompanyId, pay.Id, new CustomerCreditToDepositRequest(600m, ExpectedUnappliedBefore: 600m), "tester");
        Assert.Equal(0m, r.RemainingUnapplied);
        Assert.NotNull(r.TargetDocumentId);
        using var c = DbTestDatabase.TryCreateContext()!;
        var dep = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == r.TargetDocumentId);
        Assert.True(dep.IsDeposit);
        Assert.True(CustomerCreditMovementRules.DepositIssued(dep.Status));
        var suspense = await SuspenseAsync(c, s.CompanyId);
        Assert.Equal(0m, await GlCreditNetAsync(c, s.CompanyId, suspense));   // Dr 21630 ของใบมัดจำหักล้าง Cr ตอนรับเงิน
        Assert.Equal(r.TargetDocumentId, await c.PaymentCreditMovements.AsNoTracking().Where(m => m.Id == r.MovementId).Select(m => m.TargetDocumentId).SingleAsync());
    }

    [Fact]
    public async Task ToDeposit_ล้มก่อนอนุมัติ_ปล่อยการจองเงินอยู่ครบ_กดซ้ำกุญแจเดิมทำงานจริงไม่ใช่บันทึกไปแล้วปลอม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.Advance);
        var key = "dep-" + Guid.NewGuid().ToString("N");
        // ชนิดมัดจำที่ไม่มีจริง ⇒ สร้างใบไม่ได้ (ก่อนอนุมัติ)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => FullCredits(req).ToDepositAsync(s.CompanyId, pay.Id,
                new CustomerCreditToDepositRequest(600m, DepositKindId: Guid.NewGuid(), IdempotencyKey: key), "tester"), CustomerCreditMovementRules.ToDepositRuleCode);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(600m, await c.Payments.AsNoTracking().Where(p => p.Id == pay.Id).Select(p => p.UnappliedAmount).SingleAsync());
            Assert.False(await c.Documents.AsNoTracking().AnyAsync(d => d.CompanyId == s.CompanyId && d.IsDeposit && d.Reference == pay.PaymentNumber));
            Assert.Null(await c.PaymentCreditMovements.AsNoTracking().Where(m => m.PaymentId == pay.Id).Select(m => m.IdempotencyKey).SingleAsync());
        }
        // M2: กดใหม่ด้วยคีย์เดิม (แก้ชนิดแล้ว) ⇒ ทำงานจริง
        CustomerCreditMovementResult again;
        using (var req = DbTestDatabase.TryCreateContext()!)
            again = await FullCredits(req).ToDepositAsync(s.CompanyId, pay.Id, new CustomerCreditToDepositRequest(600m, IdempotencyKey: key), "tester");
        Assert.DoesNotContain(again.Notices, n => n.Contains("กดซ้ำ", StringComparison.Ordinal));
        Assert.NotNull(again.TargetDocumentId);
        Assert.Equal(0m, again.RemainingUnapplied);
    }

    [Fact]
    public async Task ToDeposit_ล้มหลังอนุมัติcommit_ห้ามปล่อยการจอง_ผูกใบและบอกผู้ใช้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.Advance);
        var (hooks, _) = ImportHubControllerGateDbTests.Rec<IIssuedDocumentHooks>.Create((name, a) =>
            throw new InvalidOperationException("จำลอง: ขั้นหลังอนุมัติล้ม"));
        CustomerCreditMovementResult r;
        using (var req = DbTestDatabase.TryCreateContext()!)
            r = await FullCredits(req, hooks).ToDepositAsync(s.CompanyId, pay.Id, new CustomerCreditToDepositRequest(600m), "tester");
        Assert.Contains(r.Notices, n => n.Contains("ขั้นหลังอนุมัติล้ม", StringComparison.Ordinal));
        Assert.Equal(0m, r.RemainingUnapplied);                     // ไม่ถูกปล่อย — เงินไม่ถูกนับสองที่
        Assert.NotNull(r.TargetDocumentId);
        using var c = DbTestDatabase.TryCreateContext()!;
        var m = await c.PaymentCreditMovements.AsNoTracking().SingleAsync(x => x.Id == r.MovementId);
        Assert.Null(m.ReleasedAt);
        Assert.Equal(r.TargetDocumentId, m.TargetDocumentId);
    }

    [Fact]
    public async Task M5_ใบเก่าที่ผู้ใช้ออกใบเสร็จเองแล้ว_ไม่ขึ้นรายการ_ใบเสร็จงวดอื่นยอดไม่ตรงยังขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var doc = await InvoiceAsync(db, s, 900m);
        await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.Status, DocumentStatus.Paid).SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 900m));
        var p = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY-M5-" + Guid.NewGuid().ToString("N")[..6], DocumentId = doc.Id, PaymentDate = Today.AddDays(-90),
            Amount = 600m, PaymentMethod = PaymentMethod.BankTransfer, CreatedBy = "tester", CreatedAt = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Payments.Add(p);
        await db.SaveChangesAsync();
        db.PaymentAllocations.Add(new PaymentAllocation { CompanyId = s.CompanyId, PaymentId = p.Id, DocumentId = doc.Id, AllocatedAmount = 600m });
        // ใบเสร็จงวดแรก 300 ที่ออกเอง (ยอดไม่ตรง 600 และไม่ครอบทั้งใบ) ⇒ ยังขึ้นรายการ
        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentType = DocumentType.Receipt, DocumentNumber = "RE-M5a-" + Guid.NewGuid().ToString("N")[..4],
            Status = DocumentStatus.Approved, DocumentDate = Today, TotalAmount = 300m, Currency = "THB", WhtCertAutoIssue = false, RelatedDocumentId = doc.Id,
        });
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(await CustomerCreditWorkQueue.LegacyMissingReceiptsAsync(c, s.CompanyId), x => x.DocumentId == doc.Id);
        // ใบเสร็จที่ออกเองยอดเท่าการรับเงินนี้ (600) ⇒ ไม่ขึ้น (ปุ่มจะไม่ออกซ้ำ — ตัวตัดสินเดียว ManualReceiptCoverage)
        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentType = DocumentType.Receipt, DocumentNumber = "RE-M5b-" + Guid.NewGuid().ToString("N")[..4],
            Status = DocumentStatus.Approved, DocumentDate = Today, TotalAmount = 600m, Currency = "THB", WhtCertAutoIssue = false, RelatedDocumentId = doc.Id,
        });
        await db.SaveChangesAsync();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(await CustomerCreditWorkQueue.LegacyMissingReceiptsAsync(c, s.CompanyId), x => x.DocumentId == doc.Id);
    }
}
