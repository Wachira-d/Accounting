using Accounting.Helpers;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 258 "เงินรอจัดสรร" (DECISIONS 258-1-x · erp-review/2026-11-11/team-D-lump-payment.md §3.9) — ผ่านเมธอดจริงบน PostgreSQL ทั้งสองทิศ:
/// <list type="bullet">
/// <item>เคสที่เคยติด: ลูกค้าโอน 16,050 ปิดใบ 10,700 + ยอด 5,350 ที่ยังไม่ออกใบ ⇒ เงินส่วนเกินลงผัง 21630 · ออกใบทีหลังแล้วจัดสรร ⇒ ใบปิด · 21630 เป็นศูนย์ ·
/// ภาษีขายที่พักย้ายเข้างวดของวันรับเงิน</item>
/// <item>ทิศตรงข้าม: ยอดพอดี = ไม่มีอะไรลง 21630 · จ่ายเกินทางใบเดียวยังถูกปฏิเสธ · เงินเกินไม่มีเหตุผล/ฝั่งจ่าย/เงินล่วงหน้าของกิจการจด VAT = ปฏิเสธ ไม่มีอะไรถูกเขียน</item>
/// <item>จัดสรรข้ามลูกค้า = 409 · ข้ามบริษัท = ไม่พบ · งวดบัญชีปิด = ปฏิเสธ ไม่มีอะไรถูกเขียน</item>
/// <item>ยกเลิกการจัดสรร ⇒ ยอดกลับครบ + ถอยภาษีขาย · ยกเลิกการรับชำระหลังจัดสรร ⇒ ทุกใบกลับ ผังพักเป็นศูนย์</item>
/// <item>P-2: JE เงินส่วนเกินเดิม (217xx) ไม่ถูกเสนอ/ใช้เป็น "มัดจำจากสมุดรายวัน" อีก · backfill ประทับแถวเดิม เว้นแถวที่ถูกใช้ไปแล้ว</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class CustomerCreditDbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerCreditDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid CustomerId, Guid OtherCustomerId, Guid SupplierId, Guid ArId, Guid RevenueId,
        Guid UndueVatId, Guid DueVatId, Guid LegacyAdvanceId, Guid BankId, Guid BankGlId, Guid ApId, Guid ExpenseId);

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, bool vatRegistered = true)
    {
        var company = new Company { Name = "บริษัททดสอบเงินรอจัดสรร", TaxId = "0105556000025", IsVatRegistered = vatRegistered };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cash = Acc(company.Id, "11111", "เงินสด", AccountType.Asset);
        var savings = Acc(company.Id, "11122", "เงินฝากออมทรัพย์", AccountType.Asset);
        var bankGl = Acc(company.Id, "11122-001", "เงินฝากออมทรัพย์ - กสิกร", AccountType.Asset, level: 5);
        var ar = Acc(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var ap = Acc(company.Id, "21210", "เจ้าหนี้การค้า", AccountType.Liability);
        var group216 = Acc(company.Id, "216", "เงินมัดจำและเงินรับล่วงหน้าอื่น", AccountType.Liability, level: 3);
        var legacyAdvance = Acc(company.Id, "21712", "ค่าสินค้ารับล่วงหน้า", AccountType.Liability);
        var dueVat = Acc(company.Id, "21911", "ภาษีขาย", AccountType.Liability);
        var undueVat = Acc(company.Id, "21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability);
        var revenue = Acc(company.Id, "41100", "รายได้จากการให้บริการ", AccountType.Revenue);
        var expense = Acc(company.Id, "51110", "ซื้อสินค้า", AccountType.Expense);
        db.ChartOfAccounts.AddRange(cash, savings, bankGl, ar, ap, group216, legacyAdvance, dueVat, undueVat, revenue, expense);
        await db.SaveChangesAsync();
        var bank = new BankAccount
        {
            CompanyId = company.Id, AccountName = "ออมทรัพย์หลัก", BankName = "กสิกร", AccountNumber = "1234567890",
            LinkedAccountId = bankGl.Id, CurrentBalance = 0m,
        };
        db.BankAccounts.Add(bank);
        var customer = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า ก จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        var other = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า ข จำกัด", TaxId = "0105537000014", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        var supplier = new Contact { CompanyId = company.Id, Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0105556000017", ContactType = ContactType.JuristicPerson, IsSupplier = true };
        db.Contacts.AddRange(customer, other, supplier);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id, other.Id, supplier.Id, ar.Id, revenue.Id, undueVat.Id, dueVat.Id, legacyAdvance.Id,
            bank.Id, bankGl.Id, ap.Id, expense.Id);
    }

    /// <summary>ใบแจ้งหนี้ขายที่อนุมัติแล้ว + JE ตอนออกใบ (Dr ลูกหนี้ / Cr รายได้ + Cr ภาษีขายที่พัก 21913 ถ้ามี VAT)</summary>
    private static async Task<Document> InvoiceAsync(Accounting.Data.AccountingDbContext db, Seed s, Guid contactId, decimal net, decimal vat,
        DateTime? date = null, Guid? companyId = null)
    {
        var cid = companyId ?? s.CompanyId;
        var no = "INV-" + Guid.NewGuid().ToString("N")[..8];
        var total = net + vat;
        var d = new Document
        {
            CompanyId = cid, ContactId = contactId, DocumentType = DocumentType.Invoice, DocumentNumber = no,
            Status = DocumentStatus.Approved, DocumentDate = date ?? Today,
            SubTotal = net, VatAmount = vat, TotalAmount = total, BalanceDue = total, Currency = "THB", WhtCertAutoIssue = false,
            BankAccountId = cid == s.CompanyId ? s.BankId : null,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = net, Amount = net });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        if (cid != s.CompanyId) return d;
        var je = new JournalEntry
        {
            CompanyId = cid, EntryNumber = "SV-" + no, EntryDate = d.DocumentDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.Sales, Description = "ออกใบ " + no, Reference = no, TotalDebit = total, TotalCredit = total,
            SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.ArId, DebitAmount = total, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.RevenueId, CreditAmount = net, LineOrder = 2 });
        if (vat > 0) je.Lines.Add(new JournalEntryLine { AccountId = s.UndueVatId, CreditAmount = vat, LineOrder = 3 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return d;
    }

    private static DocumentService Docs(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static CustomerCreditService Credits(Accounting.Data.AccountingDbContext db)
        => new(db, Docs(db), new AccountingService(db), NullLogger<CustomerCreditService>.Instance);

    /// <summary>รับเงิน <paramref name="amount"/> จัดสรรให้ใบตามรายการ — ส่วนที่เหลือ = เงินเกิน (ต้องมีเหตุผล)</summary>
    private static CreatePaymentRequest Receive(decimal amount, UnappliedReason? reason, params (Guid DocId, decimal Amount)[] allocations)
        => new(allocations[0].DocId, Today, amount, PaymentMethod.BankTransfer, "SLIP-1", null, null,
            Allocations: allocations.Select(a => new PaymentAllocationRequest(a.DocId, a.Amount)).ToList(),
            ExcessReason: reason, ExcessNote: reason == null ? null : "ค่าติดตั้งงวด 2 ยังไม่ได้ออกใบ");

    /// <summary>ยอดสุทธิฝั่งเครดิตของผังในบัญชีแยกประเภท (นับใบที่ถูกกลับและตัวกลับด้วย ⇒ หักล้างกัน)</summary>
    private static async Task<decimal> GlCreditNetAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid accountId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == companyId && l.AccountId == accountId
                        && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                  select (decimal?)(l.CreditAmount - l.DebitAmount)).SumAsync() ?? 0m;

    private static async Task<Guid> SuspenseAccountIdAsync(Accounting.Data.AccountingDbContext db, Guid companyId)
        => await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.AccountCode == UnappliedReceiptAccount.Code).Select(a => a.Id).SingleAsync();

    // ───────── เคสที่เคยติด ─────────

    [Fact]
    public async Task โอนก้อนเดียวปิดสองยอด_ใบเดียวออกไว้_เงินเกินลง21630_แล้วจัดสรรให้ใบที่ออกทีหลัง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var inv1 = await InvoiceAsync(db, s, s.CustomerId, 10_000m, 700m);

        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId,
                Receive(16_050m, UnappliedReason.UninvoicedSupply, (inv1.Id, 10_700m)), "tester");
        Assert.Equal(5_350m, pay.UnappliedCredit);

        Guid suspense;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            suspense = await SuspenseAccountIdAsync(check, s.CompanyId);   // ผัง 21630 ถูกสร้างให้บริษัทเดิม (ใต้กลุ่ม 216)
            var p = await check.Payments.AsNoTracking().SingleAsync(x => x.Id == pay.Id && x.CompanyId == s.CompanyId);
            Assert.Equal((5_350m, (Guid?)s.CustomerId, (Guid?)suspense, nameof(UnappliedReason.UninvoicedSupply)),
                (p.UnappliedAmount, p.ContactId, p.UnappliedAccountId, p.UnappliedReason));
            Assert.Equal(5_350m, await GlCreditNetAsync(check, s.CompanyId, suspense));
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.LegacyAdvanceId));            // P-1: ไม่ตกผังแรกใต้ 217 อีก
            Assert.Equal(16_050m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankId)).CurrentBalance);
            Assert.Equal(DocumentStatus.Paid, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv1.Id)).Status);
            Assert.Equal(5_350m, (await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId))[s.CustomerId]);
        }

        // ออกใบที่ 2 ทีหลัง (ค่าบริการ 5,000 + VAT 350 พักไว้ 21913) → ระบบเสนอ "ใช้เงินรอจัดสรร" เท่ายอดค้าง → จัดสรร
        var inv2 = await InvoiceAsync(db, s, s.CustomerId, 5_000m, 350m);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var usable = await Credits(req).ForDocumentAsync(s.CompanyId, inv2.Id);
            Assert.Equal((5_350m, 5_350m), (usable.TotalUsable, usable.ContactUnapplied));
            var targets = await Credits(req).TargetsAsync(s.CompanyId, pay.Id);
            Assert.Equal((inv2.Id, 5_350m, (string?)null), (targets.Single().DocumentId, targets.Single().Suggested, targets.Single().BlockedReason));
        }
        CustomerCreditAllocationResult result;
        using (var req = DbTestDatabase.TryCreateContext()!)
            result = await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv2.Id, 5_350m), "tester");
        Assert.Equal((0m, 0m, DocumentStatus.Paid), (result.RemainingUnapplied, result.DocumentBalanceDue, result.DocumentStatus));

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d2 = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
            Assert.Equal((DocumentStatus.Paid, 0m, 5_350m), (d2.Status, d2.BalanceDue, d2.PaidAmount));
            Assert.Equal(Today, d2.PaymentDate);                      // ใบที่ออกหลังเงินเข้า: วันที่รับชำระ = วันรับเงิน (เซิร์ฟเวอร์เติม)
            Assert.Equal(Today, d2.OutputVatDueAt?.Date);             // ภาษีขายที่พักย้ายเข้างวดของวันรับเงิน
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, suspense));          // ผังพักเป็นศูนย์
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.UndueVatId));      // ภาษีที่พักของทั้งสองใบถูกย้ายครบ (ใบ 1 ตอนรับเงิน · ใบ 2 ตอนจัดสรร)
            Assert.Equal(1_050m, await GlCreditNetAsync(check, s.CompanyId, s.DueVatId));    // 700 + 350 เข้า ภ.พ.30
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.ArId));            // ลูกหนี้ของทั้งสองใบถูกตัดครบ
            var p = await check.Payments.AsNoTracking().SingleAsync(x => x.Id == pay.Id);
            Assert.Equal(0m, p.UnappliedAmount);
            Assert.NotNull(p.UnappliedResolvedAt);
            var list = await Credits(check).ListAsync(s.CompanyId, null, includeResolved: true);
            Assert.Equal((0m, 0m), (list.TotalUnapplied, list.GlDifference));
            Assert.Null(list.GlNote);
            Assert.Contains(list.Items.Single().Allocations, a => a.DocumentId == inv2.Id && a.IsLate && a.CanUndo);
            Assert.Empty(await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId));
            // ประวัติการชำระของใบ 2 เห็นเงินก้อนนี้ (เดิมกรองเฉพาะใบแรกของการชำระ)
            var history = await Docs(check).GetPaymentsAsync(s.CompanyId, inv2.Id);
            Assert.Contains(history, h => h.Id == pay.Id && h.AllocatedToDocument == 5_350m && h.Source == "allocation");
        }
    }

    // ───────── ทิศตรงข้าม ─────────

    [Fact]
    public async Task ยอดพอดี_ไม่มีอะไรลงผังพัก_และจ่ายเกินทางใบเดียวยังถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var a = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        var b = await InvoiceAsync(db, s, s.CustomerId, 2_000m, 0m);
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(3_000m, null, (a.Id, 1_000m), (b.Id, 2_000m)), "tester");
        Assert.Equal(0m, pay.UnappliedCredit);

        var c = await InvoiceAsync(db, s, s.CustomerId, 500m, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Docs(req).CreatePaymentAsync(s.CompanyId,
                new CreatePaymentRequest(c.Id, Today, 800m, PaymentMethod.BankTransfer, null, null, null), "tester", PaymentEntrySource.Manual));
            Assert.Contains("มากกว่ายอดค้างชำระ", ex.Message, StringComparison.Ordinal);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var p = await check.Payments.AsNoTracking().SingleAsync(x => x.Id == pay.Id);
        Assert.Equal((0m, (Guid?)null, (string?)null), (p.UnappliedAmount, p.UnappliedAccountId, p.UnappliedReason));
        Assert.False(await check.ChartOfAccounts.AnyAsync(x => x.CompanyId == s.CompanyId && x.AccountCode == UnappliedReceiptAccount.Code));
        Assert.Empty(await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId));
        Assert.Equal(500m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == c.Id)).BalanceDue);
    }

    [Fact]
    public async Task เงินเกินไม่มีเหตุผล_เงินล่วงหน้าของกิจการจดVat_ฝั่งจ่าย_ปฏิเสธและไม่มีอะไรถูกเขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        async Task<BusinessRuleException> Rejects(CreatePaymentRequest r)
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            return await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, r, "tester"));
        }
        var noReason = await Rejects(Receive(1_500m, null, (inv.Id, 1_000m)));
        Assert.Equal(PaymentExcessPlan.RuleCode, noReason.RuleCode);
        Assert.Contains("เลือกเหตุผล", noReason.Message, StringComparison.Ordinal);
        Assert.Equal(PaymentExcessPlan.AdvanceVatBlocked, (await Rejects(Receive(1_500m, UnappliedReason.Advance, (inv.Id, 1_000m)))).Message);

        // ฝั่งจ่ายเจ้าหนี้ (258-1-8): เดิมลงเงินจ่ายเกินเข้า 11400 เงินให้กู้ยืม ⇒ ปิดไว้ก่อน
        var pi = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.SupplierId, DocumentType = DocumentType.PurchaseInvoice,
            DocumentNumber = "PI-" + Guid.NewGuid().ToString("N")[..8], Status = DocumentStatus.Approved, DocumentDate = Today,
            SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m, Currency = "THB", WhtCertAutoIssue = false, BankAccountId = s.BankId,
        };
        db.Documents.Add(pi);
        await db.SaveChangesAsync();
        Assert.Contains("ฝั่งจ่ายเจ้าหนี้", (await Rejects(Receive(1_500m, UnappliedReason.Overpaid, (pi.Id, 1_000m)))).Message, StringComparison.Ordinal);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.IgnoreQueryFilters().AnyAsync(p => p.CompanyId == s.CompanyId));
        Assert.Equal(1_000m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
        Assert.Equal(0m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankId)).CurrentBalance);
    }

    [Fact]
    public async Task จัดสรรข้ามลูกค้า409_ข้ามบริษัทไม่พบ_งวดปิดปฏิเสธ_ไม่มีอะไรถูกเขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv1 = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(1_600m, UnappliedReason.Unknown, (inv1.Id, 1_000m)), "tester");

        // ลูกค้าอีกราย
        var foreign = await InvoiceAsync(db, s, s.OtherCustomerId, 600m, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(foreign.Id, 600m), "tester"));
            Assert.Equal((CustomerCreditAllocation.ContactRuleCode, 409), (ex.RuleCode, ex.StatusCode));
            Assert.Equal(0m, (await Credits(req).ForDocumentAsync(s.CompanyId, foreign.Id)).TotalUsable);   // ไม่มีปุ่มบนใบของลูกค้าอีกราย
        }
        // บริษัทอื่น: ใบของบริษัทอื่น และ การชำระของบริษัทนี้ผ่าน companyId ของบริษัทอื่น
        var otherCompany = new Company { Name = "บริษัทอื่น", TaxId = "0105556000033", IsVatRegistered = true };
        db.Companies.Add(otherCompany);
        await db.SaveChangesAsync();
        var otherContact = new Contact { CompanyId = otherCompany.Id, Name = "ลูกค้าของบริษัทอื่น", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.Add(otherContact);
        await db.SaveChangesAsync();
        var otherDoc = await InvoiceAsync(db, s, otherContact.Id, 600m, 0m, companyId: otherCompany.Id);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(otherDoc.Id, 600m), "tester"));
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Credits(req).AllocateAsync(otherCompany.Id, pay.Id,
                new AllocateCustomerCreditRequest(otherDoc.Id, 600m), "tester"));

        // งวดบัญชีของวันที่จัดสรรปิดแล้ว
        var own = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var period = new FiscalPeriod
        {
            CompanyId = s.CompanyId, Name = "งวดปิดทดสอบ", Year = Today.Year, Month = Today.Month,
            StartDate = new DateTime(Today.Year, Today.Month, 1), EndDate = new DateTime(Today.Year, Today.Month, 1).AddMonths(1).AddDays(-1),
            Status = FiscalPeriodStatus.Closed,
        };
        db.FiscalPeriods.Add(period);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(own.Id, 600m), "tester"));
            Assert.Contains("ปิดแล้ว", ex.Message, StringComparison.Ordinal);
        }

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(600m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
        Assert.Equal(1, await check.PaymentAllocations.IgnoreQueryFilters().CountAsync(a => a.PaymentId == pay.Id));   // มีแต่แถวตอนรับเงิน
        Assert.Equal(600m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == own.Id)).BalanceDue);
        Assert.Equal(600m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == foreign.Id)).BalanceDue);
        Assert.Equal(600m, await GlCreditNetAsync(check, s.CompanyId, await SuspenseAccountIdAsync(check, s.CompanyId)));
    }

    // ───────── ยกเลิก ─────────

    [Fact]
    public async Task ยกเลิกการจัดสรร_ยอดกลับครบและถอยภาษีขาย_จัดสรรใหม่ได้_แล้วยกเลิกการรับชำระทั้งก้อน_ทุกอย่างกลับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv1 = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(2_070m, UnappliedReason.UninvoicedSupply, (inv1.Id, 1_000m)), "tester");
        var inv2 = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 70m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv2.Id, 1_070m), "tester");

        Guid suspense;
        Guid allocationId;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            suspense = await SuspenseAccountIdAsync(check, s.CompanyId);
            allocationId = await check.PaymentAllocations.AsNoTracking()
                .Where(a => a.PaymentId == pay.Id && a.IsLateAllocation).Select(a => a.Id).SingleAsync();
            Assert.Equal(70m, await GlCreditNetAsync(check, s.CompanyId, s.DueVatId));     // ภาษีขายย้ายเข้า 21911 แล้ว
        }

        // การจัดสรรที่เกิดพร้อมรับเงิน ยกเลิกเฉพาะส่วนไม่ได้
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var initial = await req.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == pay.Id && !a.IsLateAllocation).Select(a => a.Id).SingleAsync();
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, initial, "tester"));
            Assert.Contains("ยกเลิกการรับชำระทั้งรายการ", ex.Message, StringComparison.Ordinal);
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var undone = await Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester");
            Assert.Equal((1_070m, 1_070m, DocumentStatus.Approved), (undone.RemainingUnapplied, undone.DocumentBalanceDue, undone.DocumentStatus));
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d2 = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
            Assert.Equal((DocumentStatus.Approved, 1_070m, 0m), (d2.Status, d2.BalanceDue, d2.PaidAmount));
            Assert.Null(d2.PaymentDate);
            Assert.Null(d2.OutputVatDueAt);                                                  // ถอยภาษีขายถึงกำหนด (ใบไม่เหลือเงินรับ)
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.DueVatId));
            Assert.Equal(70m, await GlCreditNetAsync(check, s.CompanyId, s.UndueVatId));
            Assert.Equal(1_070m, await GlCreditNetAsync(check, s.CompanyId, suspense));
            Assert.Equal(-1_070m, await GlCreditNetAsync(check, s.CompanyId, s.ArId));       // ลูกหนี้ค้าง (เดบิตสุทธิ) = ใบ 2 เต็มยอด
        }
        // ยกเลิกซ้ำ = ไม่พบ (แถวถูกลบแบบ soft แล้ว)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester"));

        // จัดสรรใหม่ (ลำดับ A2) แล้วยกเลิกการรับชำระทั้งก้อน ⇒ ใบ 1 และใบ 2 กลับเป็นค้างทั้งคู่ · ผังพัก/ธนาคารเป็นศูนย์
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv2.Id, 1_070m), "tester");
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).VoidPaymentAsync(s.CompanyId, pay.Id);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(1_000m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv1.Id)).BalanceDue);
            var d2 = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
            Assert.Equal((DocumentStatus.Approved, 1_070m), (d2.Status, d2.BalanceDue));
            Assert.Null(d2.PaymentDate);
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, suspense));
            Assert.Equal(0m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankId)).CurrentBalance);
            Assert.Empty(await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId));
            Assert.False(await check.PaymentAllocations.AnyAsync(a => a.PaymentId == pay.Id));
        }
    }

    // ───────── P-2 + backfill ─────────

    /// <summary>การรับชำระหลายใบ "ก่อนรอบ 258": จัดสรร 600 จาก 1,000 · เงินส่วนเกิน 400 ลง 21712 (ผังแรกใต้ 217) โดยไม่มีคอลัมน์ใดถูกประทับ</summary>
    private static async Task<(Payment Payment, JournalEntry ExcessJe)> LegacyExcessAsync(Accounting.Data.AccountingDbContext db, Seed s, Document doc)
    {
        var payNo = "PAY-LEG-" + Guid.NewGuid().ToString("N")[..8];
        var payment = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = payNo, DocumentId = doc.Id, PaymentDate = Today, Amount = 1_000m,
            PaymentMethod = PaymentMethod.BankTransfer, CreatedBy = "tester",
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.PaymentAllocations.Add(new PaymentAllocation { CompanyId = s.CompanyId, PaymentId = payment.Id, DocumentId = doc.Id, AllocatedAmount = 600m });
        var je = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "RV-" + payNo, EntryDate = Today, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.CashReceipts, Description = CustomerCreditMigration.ExcessJournalDescriptionPrefix + " - " + payNo,
            Reference = payNo, TotalDebit = 400m, TotalCredit = 400m, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.BankGlId, DebitAmount = 400m, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.LegacyAdvanceId, CreditAmount = 400m, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return (payment, je);
    }

    [Fact]
    public async Task เงินส่วนเกินเดิม_ไม่ถูกเสนอหรือใช้เป็นมัดจำจากสมุดรายวัน_แต่JVมัดจำจริงยังใช้ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var paid = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var (_, excessJe) = await LegacyExcessAsync(db, s, paid);
        // JV มัดจำจริงที่ลงมือ (ไม่อ้างเลขการชำระ) — ทิศตรงข้าม: ต้องยังถูกเสนอ
        var manual = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "JV-DEP-" + Guid.NewGuid().ToString("N")[..8], EntryDate = Today, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.General, Description = "รับมัดจำลูกค้า", Reference = "BOOKING-77", TotalDebit = 300m, TotalCredit = 300m, CreatedBy = "tester",
        };
        manual.Lines.Add(new JournalEntryLine { AccountId = s.BankGlId, DebitAmount = 300m, LineOrder = 1 });
        manual.Lines.Add(new JournalEntryLine { AccountId = s.LegacyAdvanceId, CreditAmount = 300m, LineOrder = 2 });
        db.JournalEntries.Add(manual);
        await db.SaveChangesAsync();

        var victim = await InvoiceAsync(db, s, s.OtherCustomerId, 400m, 0m);   // ใบของลูกค้าอีกรายที่เดิมถูกตัดด้วยเงินของคนอื่นได้
        using var req = DbTestDatabase.TryCreateContext()!;
        var offered = await Docs(req).SearchJournalDepositsAsync(s.CompanyId, null);
        Assert.DoesNotContain(offered, c => c.EntryNumber == excessJe.EntryNumber);
        Assert.Contains(offered, c => c.EntryNumber == manual.EntryNumber);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Docs(req).ApplyJournalDepositToInvoiceAsync(s.CompanyId, victim.Id, excessJe.EntryNumber, "tester"));
        Assert.Contains("เงินรอจัดสรร", ex.Message, StringComparison.Ordinal);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(400m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == victim.Id)).BalanceDue);
        Assert.Null((await check.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == excessJe.Id)).DepositAppliedToDocumentId);
    }

    [Fact]
    public async Task backfill_ประทับแถวเดิมด้วยสูตรเดียวกัน_เว้นแถวที่ถูกใช้ไปแล้ว_แล้วจัดสรรตัดจากผังเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var docA = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var docB = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var (open, _) = await LegacyExcessAsync(db, s, docA);
        var (used, usedJe) = await LegacyExcessAsync(db, s, docB);
        usedJe.DepositAppliedToDocumentId = docB.Id;   // เคยถูก "นำมัดจำจากสมุดรายวัน" ไปตัดใบแล้ว (P-2) ⇒ ให้คนตัดสิน ไม่ backfill
        await db.SaveChangesAsync();

        // ฐานเทสต์ใช้ร่วมกัน: ปลด marker แล้วรัน backfill ซ้ำได้ (แถวที่ประทับแล้วถูกข้ามด้วยเงื่อนไข UnappliedAccountId IS NULL)
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"OneShotDataFixes\" WHERE \"Key\" = {0}", CustomerCreditMigration.BackfillMarkerKey);
        await db.Database.ExecuteSqlRawAsync(CustomerCreditMigration.BackfillSql());

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var o = await check.Payments.AsNoTracking().SingleAsync(p => p.Id == open.Id);
            Assert.Equal((400m, (Guid?)s.LegacyAdvanceId, (Guid?)s.CustomerId, nameof(UnappliedReason.Unknown)),
                (o.UnappliedAmount, o.UnappliedAccountId, o.ContactId, o.UnappliedReason));
            Assert.Equal(UnappliedCreditBalance.Compute(1_000m, 600m), o.UnappliedAmount);   // SQL = สูตร C# ตัวเดียวกัน
            var u = await check.Payments.AsNoTracking().SingleAsync(p => p.Id == used.Id);
            Assert.Equal((0m, (Guid?)null, (Guid?)null), (u.UnappliedAmount, u.UnappliedAccountId, u.ContactId));
        }

        // เงินเก่าที่ backfill แล้ว จัดสรรให้ใบใหม่ได้ — ตัดจากผังเดิมที่ลงไว้ (21712) ไม่ใช่ 21630
        var later = await InvoiceAsync(db, s, s.CustomerId, 400m, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, open.Id, new AllocateCustomerCreditRequest(later.Id, 400m), "tester");
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(DocumentStatus.Paid, (await after.Documents.AsNoTracking().SingleAsync(d => d.Id == later.Id)).Status);
        Assert.Equal(400m, await GlCreditNetAsync(after, s.CompanyId, s.LegacyAdvanceId));   // 400 (ของแถวที่ถูกใช้ไปแล้ว) เหลือ · ของแถวที่จัดสรรถูกตัดออก
        var list = await Credits(after).ListAsync(s.CompanyId, null, includeResolved: false);
        Assert.Empty(list.Items);
    }

    // ───────── รายงานอายุลูกหนี้ ─────────

    [Fact]
    public async Task รายงานอายุลูกหนี้_แสดงเงินรอจัดสรรแยกบรรทัด_ไม่หักจากยอดค้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv1 = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(1_500m, UnappliedReason.Overpaid, (inv1.Id, 1_000m)), "tester");
        await InvoiceAsync(db, s, s.CustomerId, 800m, 0m);              // ใบค้างที่ยังไม่ได้จัดสรร
        await InvoiceAsync(db, s, s.OtherCustomerId, 300m, 0m);         // ลูกค้าที่ไม่มีเงินรอจัดสรร (ทิศตรงข้าม)

        using var check = DbTestDatabase.TryCreateContext()!;
        var report = await new AgingReportService(check).GetReceivableAgingAsync(s.CompanyId, new AgingReportRequest(Today));
        var mine = report.Details.Single(d => d.ContactId == s.CustomerId);
        Assert.Equal((800m, 500m), (mine.TotalBalance, mine.UnappliedCredit));         // ยอดค้างไม่ถูกหัก
        Assert.Equal(0m, report.Details.Single(d => d.ContactId == s.OtherCustomerId).UnappliedCredit);
        Assert.Equal(1_100m, report.Totals.GrandTotal);
        Assert.Equal(500m, report.UnappliedCreditTotal);
        Assert.Contains("ยังไม่หัก", report.UnappliedCreditNote!, StringComparison.Ordinal);
        // ฝั่งเจ้าหนี้ไม่เกี่ยว
        var payables = await new AgingReportService(check).GetPayableAgingAsync(s.CompanyId, new AgingReportRequest(Today));
        Assert.Equal(0m, payables.UnappliedCreditTotal);
        Assert.Null(payables.UnappliedCreditNote);
    }
}
