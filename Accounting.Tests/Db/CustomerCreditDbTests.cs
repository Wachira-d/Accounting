using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
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
        DateTime? date = null, Guid? companyId = null, decimal wht = 0m)
    {
        var cid = companyId ?? s.CompanyId;
        var no = "INV-" + Guid.NewGuid().ToString("N")[..8];
        var gross = net + vat;
        var total = gross - wht;
        var d = new Document
        {
            CompanyId = cid, ContactId = contactId, DocumentType = DocumentType.Invoice, DocumentNumber = no,
            Status = DocumentStatus.Approved, DocumentDate = date ?? Today,
            SubTotal = net, VatAmount = vat, TotalAmount = total, BalanceDue = total, WithholdingTaxAmount = wht, Currency = "THB", WhtCertAutoIssue = false,
            BankAccountId = cid == s.CompanyId ? s.BankId : null,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = net, Amount = net });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        if (cid != s.CompanyId) return d;
        var je = new JournalEntry
        {
            CompanyId = cid, EntryNumber = "SV-" + no, EntryDate = d.DocumentDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.Sales, Description = "ออกใบ " + no, Reference = no, TotalDebit = gross, TotalCredit = gross,
            SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.ArId, DebitAmount = gross, LineOrder = 1 });
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
                new AllocateCustomerCreditRequest(own.Id, 600m, Today), "tester"));   // ระบุวันที่ตรงกับงวดที่ปิด (ไม่ขึ้นกับเขตเวลาตอนรันเทสต์)
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
        var docC = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var (cleared, _) = await LegacyExcessAsync(db, s, docC);   // เงินส่วนเกินที่ผู้ทำบัญชีล้างด้วยสมุดรายวันไปแล้ว (ระบบไม่รู้) ⇒ ต้อง "ปิดรายการ"
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
            // ฝ่ายค้าน M4: แถวก่อนรอบ 258 = "ต้องตรวจ" — ไม่นับเป็นเงินรอจัดสรร ไม่ระงับการทวง ไม่โผล่ในรายงานอายุลูกหนี้ จนกว่าคนจะยืนยัน
            Assert.True(o.UnappliedNeedsReview);
            Assert.Empty(await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId));
            Assert.Empty(await CustomerCreditQuery.DunningHoldsAsync(check, s.CompanyId));
            var listed = await Credits(check).ListAsync(s.CompanyId, null, includeResolved: false);
            Assert.Equal(0m, listed.TotalUnapplied);
            Assert.Contains(listed.Items, r => r.PaymentId == open.Id && r.NeedsReview);
            var suspects = await CustomerCreditQuery.LegacyExcessSuspectsAsync(check, s.CompanyId);
            Assert.Contains(suspects, x => x.Number == open.PaymentNumber);
            var u = await check.Payments.AsNoTracking().SingleAsync(p => p.Id == used.Id);
            Assert.Equal((0m, (Guid?)null, (Guid?)null), (u.UnappliedAmount, u.UnappliedAccountId, u.ContactId));
        }

        // เงินเก่าที่ backfill แล้ว จัดสรรให้ใบใหม่ได้ — ตัดจากผังเดิมที่ลงไว้ (21712) ไม่ใช่ 21630
        var later = await InvoiceAsync(db, s, s.CustomerId, 400m, 0m);
        // ยังไม่ยืนยัน = จัดสรรไม่ได้ (ไม่มีอะไรถูกเขียน)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).AllocateAsync(s.CompanyId, open.Id, new AllocateCustomerCreditRequest(later.Id, 400m), "tester"),
                CustomerCreditAllocation.NeedsReviewRuleCode);
        // ปิดรายการต้องมีเหตุผล · ปิดแล้วยอดเป็นศูนย์ (ไม่ลงบัญชี) · ปิดซ้ำ/ยืนยันแถวที่ไม่ต้องตรวจ = ปฏิเสธ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => Credits(req).CloseLegacyAsync(s.CompanyId, cleared.Id, "  ", "tester"));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var closed = await Credits(req).CloseLegacyAsync(s.CompanyId, cleared.Id, "ล้างด้วย JV-2026-0412 แล้ว", "tester");
            Assert.Equal((0m, false), (closed.UnappliedAmount, closed.NeedsReview));
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).ConfirmLegacyAsync(s.CompanyId, cleared.Id, "tester"), CustomerCreditAllocation.NeedsReviewRuleCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var confirmed = await Credits(req).ConfirmLegacyAsync(s.CompanyId, open.Id, "tester");
            Assert.Equal((400m, false), (confirmed.UnappliedAmount, confirmed.NeedsReview));
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(400m, (await CustomerCreditQuery.UnappliedByContactAsync(check, s.CompanyId))[s.CustomerId]);
            Assert.True(await check.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "Payment"
                && a.EntityId == cleared.Id.ToString() && a.NewValues!.Contains("CC-LEGACY-CLOSE")));
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, open.Id, new AllocateCustomerCreditRequest(later.Id, 400m), "tester");
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(DocumentStatus.Paid, (await after.Documents.AsNoTracking().SingleAsync(d => d.Id == later.Id)).Status);
        // 21712: แถวที่ถูกใช้ไปแล้ว 400 + แถวที่ปิดรายการ 400 (ปิดรายการไม่ลงบัญชี) ยังอยู่ · ของแถวที่จัดสรรถูกตัดออก
        Assert.Equal(800m, await GlCreditNetAsync(after, s.CompanyId, s.LegacyAdvanceId));   // 400 (ของแถวที่ถูกใช้ไปแล้ว) เหลือ · ของแถวที่จัดสรรถูกตัดออก
        var list = await Credits(after).ListAsync(s.CompanyId, null, includeResolved: false);
        Assert.Empty(list.Items);

        // ฐานที่ backfill รุ่นแรกประทับไปแล้ว (ยังไม่มีธง): ตัวติดธงย้อนหลังครั้งเดียว (LegacyReviewSql) ติดเฉพาะแถวที่ผังพักไม่ใช่ 21630 —
        // เงินรอจัดสรรที่เกิดหลังรอบ 258 (ลง 21630) ไม่ถูกแตะ
        var docD = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var (firstGen, _) = await LegacyExcessAsync(db, s, docD);
        await db.Payments.Where(x => x.Id == firstGen.Id).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.UnappliedAmount, 400m).SetProperty(x => x.UnappliedAccountId, (Guid?)s.LegacyAdvanceId)
            .SetProperty(x => x.ContactId, (Guid?)s.CustomerId).SetProperty(x => x.UnappliedNeedsReview, false));
        var modern = await CreditAsync(db, s, 250m, UnappliedReason.Overpaid);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"OneShotDataFixes\" WHERE \"Key\" = {0}", CustomerCreditMigration.LegacyReviewMarkerKey);
        await db.Database.ExecuteSqlRawAsync(CustomerCreditMigration.LegacyReviewSql());
        using var flagged = DbTestDatabase.TryCreateContext()!;
        Assert.True((await flagged.Payments.AsNoTracking().SingleAsync(x => x.Id == firstGen.Id)).UnappliedNeedsReview);
        Assert.False((await flagged.Payments.AsNoTracking().SingleAsync(x => x.Id == modern.Id)).UnappliedNeedsReview);
        Assert.Equal(250m, (await CustomerCreditQuery.UnappliedByContactAsync(flagged, s.CompanyId))[s.CustomerId]);   // นับเฉพาะก้อนใหม่
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

    // ═════════ ฝ่ายค้านรอบ 258 (erp-review/2026-11-11/review-258-customer-credit.md) ═════════

    private static async Task<BusinessRuleException> RefusedAsync(Func<Task> act, string ruleCode)
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(act);
        Assert.Equal(ruleCode, ex.RuleCode);
        return ex;
    }

    /// <summary>วันกลางเดือนของ 2 เดือนก่อน — งวด ภ.พ.30 ของวันรับเงิน "เลยกำหนดยื่นแล้ว" แน่นอน</summary>
    private static readonly DateTime TwoMonthsAgo = new DateTime(Today.Year, Today.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-2).AddDays(9);

    /// <summary>รับเงินเกิน <paramref name="excess"/> บาทเมื่อ <paramref name="on"/> (ปิดใบไม่มี VAT 1,000 ที่ออกวันเดียวกัน) แล้วคืนการรับชำระ</summary>
    private async Task<PaymentResponse> CreditAsync(Accounting.Data.AccountingDbContext db, Seed s, decimal excess, UnappliedReason reason, DateTime? on = null)
    {
        var day = on ?? Today;
        var first = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m, date: day);
        using var req = DbTestDatabase.TryCreateContext()!;
        return await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(1_000m + excess, reason, (first.Id, 1_000m)) with { PaymentDate = day }, "tester");
    }

    private static async Task<Document?> ReceiptOfAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid paymentId, Guid documentId)
        => await db.Documents.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == companyId && r.SettlementPaymentId == paymentId
            && r.RelatedDocumentId == documentId && r.IsSettlementReceipt && !r.IsDeleted && r.Status != DocumentStatus.Voided);

    // ───────── H1: จุดความรับผิดขึ้นกับเหตุผลของเงิน ─────────

    [Fact]
    public async Task H1_เงินโอนเกิน_จุดความรับผิดคือวันที่จัดสรร_ไม่มีธงล่าช้า_แต่เงินค้างออกใบยังยึดวันรับเงิน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }

        // (ก) เงินโอนเกินเมื่อ 2 เดือนก่อน → นำมาหักกลบใบที่ออกวันนี้: ภาษีขายเข้างวดของวันที่จัดสรร · ไม่ประทับวันรับชำระย้อนหลัง · ไม่มีธงล่าช้า
        var s = await SeedAsync(db);
        var overpaid = await CreditAsync(db, s, 1_070m, UnappliedReason.Overpaid, TwoMonthsAgo);
        var inv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 70m);
        CustomerCreditAllocationResult result;
        using (var req = DbTestDatabase.TryCreateContext()!)
            result = await Credits(req).AllocateAsync(s.CompanyId, overpaid.Id, new AllocateCustomerCreditRequest(inv.Id, 1_070m, Today), "tester");
        Assert.DoesNotContain(result.Notices, n => n.Contains(DepositPolicyResolver.LateVatMarker, StringComparison.Ordinal));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == inv.Id);
            Assert.Equal(DocumentStatus.Paid, d.Status);
            Assert.Equal(Today, d.OutputVatDueAt?.Date);                 // วันที่จัดสรร ไม่ใช่วันรับเงิน
            Assert.Null(d.PaymentDate);                                   // ไม่ประทับวันรับเงินเก่าลงใบ
            Assert.DoesNotContain(DepositPolicyResolver.LateVatMarker, d.InternalNotes ?? "", StringComparison.Ordinal);
            Assert.False(await check.PaymentAllocations.AsNoTracking().AnyAsync(a => a.DocumentId == inv.Id && a.StampedDocumentPaymentDate));
            Assert.Equal(70m, await GlCreditNetAsync(check, s.CompanyId, s.DueVatId));
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.UndueVatId));
        }

        // (ข) ทิศตรงข้าม — เงิน "ค้างออกใบ" ก้อนเดียวกัน: วันรับเงินเป็นจุดความรับผิด ⇒ ประทับวันรับชำระ + ธงภาษีขายล่าช้า (งวดของวันรับเงินเลยกำหนดยื่นแล้ว)
        var s2 = await SeedAsync(db);
        var uninvoiced = await CreditAsync(db, s2, 1_070m, UnappliedReason.UninvoicedSupply, TwoMonthsAgo);
        var late = await InvoiceAsync(db, s2, s2.CustomerId, 1_000m, 70m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            result = await Credits(req).AllocateAsync(s2.CompanyId, uninvoiced.Id, new AllocateCustomerCreditRequest(late.Id, 1_070m, Today), "tester");
        Assert.Contains(result.Notices, n => n.Contains(DepositPolicyResolver.LateVatMarker, StringComparison.Ordinal));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == late.Id);
            Assert.Equal(TwoMonthsAgo.Date, d.PaymentDate?.Date);
            Assert.NotNull(d.OutputVatDueAt);
            Assert.Contains(DepositPolicyResolver.LateVatMarker, d.InternalNotes ?? "", StringComparison.Ordinal);
            Assert.Equal(70m, await GlCreditNetAsync(check, s2.CompanyId, s2.DueVatId));
        }
    }

    // ───────── H2: ใบแจ้งหนี้มี VAT ที่ปิดผ่านแผงเงินเกิน/การจัดสรร ต้องได้ใบเสร็จ/ใบกำกับภาษี ─────────

    [Fact]
    public async Task H2_ใบแจ้งหนี้มีVatที่จ่ายเต็มพร้อมเงินเกิน_และใบที่ปิดด้วยการจัดสรร_ได้ใบเสร็จถือภาษีรายใบ_ยกเลิกแล้วใบเสร็จถูกยกเลิกตาม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv1 = await InvoiceAsync(db, s, s.CustomerId, 10_000m, 700m);
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(16_050m, UnappliedReason.UninvoicedSupply, (inv1.Id, 10_700m)), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var notes = (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv1.Id)).InternalNotes;
            var receipt = await ReceiptOfAsync(check, s.CompanyId, pay.Id, inv1.Id);
            Assert.True(receipt != null, "ใบแจ้งหนี้ที่จ่ายเต็มพร้อมเงินเกินไม่ได้ใบเสร็จ/ใบกำกับภาษีอัตโนมัติ — หมายเหตุบนใบ: " + notes);
            Assert.Equal((10_700m, 700m), (receipt!.TotalAmount, receipt.VatAmount));        // ยอดของใบนี้ (ไม่ใช่ 16,050) + ถือภาษีขาย = ใบกำกับภาษี ณ วันรับเงิน
            Assert.Equal(receipt.Id, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).ReceiptDocumentId);
            Assert.DoesNotContain(PaymentExcessPlan.ReceiptPendingMarker, notes ?? "", StringComparison.Ordinal);
            Assert.Empty((await Credits(check).ListAsync(s.CompanyId, null, includeResolved: true)).ReceiptPending!);
        }

        // จัดสรรทีหลังจนปิดใบมี VAT ⇒ ใบเสร็จของใบนั้น (รายใบ · ยอด = ที่จัดสรร)
        var inv2 = await InvoiceAsync(db, s, s.CustomerId, 5_000m, 350m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv2.Id, 5_350m), "tester");
        Guid allocationId;
        Guid receipt2Id;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var notes = (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id)).InternalNotes;
            var receipt2 = await ReceiptOfAsync(check, s.CompanyId, pay.Id, inv2.Id);
            Assert.True(receipt2 != null, "ใบที่ปิดด้วยการจัดสรรไม่ได้ใบเสร็จ/ใบกำกับภาษี — หมายเหตุบนใบ: " + notes);
            Assert.Equal((5_350m, 350m), (receipt2!.TotalAmount, receipt2.VatAmount));
            receipt2Id = receipt2.Id;
            allocationId = await check.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == pay.Id && a.IsLateAllocation).Select(a => a.Id).SingleAsync();
            // ออกซ้ำ (ปุ่ม "ออกใบเสร็จ" รายใบ) = คืนใบเดิม ไม่สร้างใบที่สอง
            var again = await Docs(check).IssueReceiptForPaymentAsync(s.CompanyId, pay.Id, "tester", inv2.Id);
            Assert.Equal((receipt2.Id, true), (again.ReceiptId, again.AlreadyExisted));
        }

        // ใบเสร็จของการจัดสรรที่ยืนยันทาง e-Tax ว่า "ใบเดิมยังใช้ได้" ⇒ ยกเลิกการจัดสรรไม่ได้ (ไม่มีอะไรถูกเขียน)
        await db.Documents.Where(d => d.Id == receipt2Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.EtaxKeptOriginalAt, (DateTime?)DateTime.UtcNow));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var kept = await RefusedAsync(() => Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester"), CustomerCreditAllocation.RuleCode);
            Assert.Contains("e-Tax", kept.Message, StringComparison.Ordinal);
        }
        await db.Documents.Where(d => d.Id == receipt2Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.EtaxKeptOriginalAt, (DateTime?)null));
        // ใบเสร็จของการจัดสรรถึงกรมสรรพากรแล้ว (e-Tax ตอบรับ) ⇒ ผู้ใช้ยกเลิกการจัดสรรเอง = ปฏิเสธ · ทุกอย่างคงเดิม
        var etax = new EtaxInvoice
        {
            CompanyId = s.CompanyId, DocumentId = receipt2Id, EtaxRefNumber = "ETX-" + Guid.NewGuid().ToString("N")[..8], XmlContent = "<x/>",
            Status = EtaxStatus.Accepted, SubmittedAt = DateTime.UtcNow, AcceptedAt = DateTime.UtcNow, SellerName = "ผู้ขาย", SellerTaxId = "0105556000025", BuyerName = "ผู้ซื้อ",
        };
        db.EtaxInvoices.Add(etax);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester"), "RD-ETAX-RECEIPT-SENT");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d2 = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
            Assert.Equal((DocumentStatus.Paid, 0m), (d2.Status, d2.BalanceDue));
            Assert.NotNull(d2.OutputVatDueAt);
            Assert.NotNull(await ReceiptOfAsync(check, s.CompanyId, pay.Id, inv2.Id));
            Assert.Equal(0m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, await SuspenseAccountIdAsync(check, s.CompanyId)));
        }

        // ทิศตรงข้าม: e-Tax ยังไม่ถึงกรมสรรพากร ⇒ ยกเลิกการจัดสรรได้ · ใบเสร็จของการจัดสรรถูกยกเลิกตาม · ภาษีขายถอยกลับไปพัก · ใบเสร็จของใบแรกไม่ถูกแตะ
        await db.EtaxInvoices.Where(e => e.Id == etax.Id).ExecuteDeleteAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Null(await ReceiptOfAsync(check, s.CompanyId, pay.Id, inv2.Id));
            Assert.NotNull(await ReceiptOfAsync(check, s.CompanyId, pay.Id, inv1.Id));
            var d2 = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv2.Id);
            Assert.Equal((DocumentStatus.Approved, 5_350m), (d2.Status, d2.BalanceDue));
            Assert.Null(d2.OutputVatDueAt);
            Assert.Equal(350m, await GlCreditNetAsync(check, s.CompanyId, s.UndueVatId));
            Assert.Equal(700m, await GlCreditNetAsync(check, s.CompanyId, s.DueVatId));
        }
    }

    // ───────── ภ.พ.30 ที่ยื่นแล้ว ─────────

    [Fact]
    public async Task งวดภพ30ของวันที่จัดสรรยื่นแล้ว_ใบที่ยังพักภาษีขายจัดสรรไม่ได้_ใบไม่มีVatยังจัดสรรได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 2_000m, UnappliedReason.Overpaid);
        var vatInv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 70m);
        var plain = await InvoiceAsync(db, s, s.CustomerId, 500m, 0m);
        db.TaxReports.Add(new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VAT, Year = Today.Year, Month = Today.Month, Status = TaxReportStatus.Filed });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await RefusedAsync(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(vatInv.Id, 1_070m, Today), "tester"),
                LateReceiptTaxPoint.RuleCode);
            Assert.Contains("ภ.พ.30", ex.Message, StringComparison.Ordinal);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == vatInv.Id);
            Assert.Equal((DocumentStatus.Approved, 1_070m, 0m), (d.Status, d.BalanceDue, d.PaidAmount));
            Assert.Null(d.OutputVatDueAt);
            Assert.Equal(2_000m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
            Assert.False(await check.PaymentAllocations.AsNoTracking().AnyAsync(a => a.DocumentId == vatInv.Id));
            Assert.Equal(70m, await GlCreditNetAsync(check, s.CompanyId, s.UndueVatId));
        }
        // ทิศตรงข้าม: ใบไม่มี VAT ไม่มีอะไรเข้า ภ.พ.30 ⇒ จัดสรรได้ในงวดที่ยื่นแล้ว
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(plain.Id, 500m, Today), "tester");
            Assert.Equal((1_500m, DocumentStatus.Paid), (ok.RemainingUnapplied, ok.DocumentStatus));
        }
    }

    // ───────── ภาษีถูกหัก ณ ที่จ่ายตอนปิดยอดด้วยการจัดสรร + การกลับ ─────────

    [Fact]
    public async Task ปิดยอดใบที่ถูกหักณที่จ่ายด้วยเงินรอจัดสรร_ลง11910ตัดลูกหนี้ส่วนภาษี_ยกเลิกการจัดสรรกลับทั้งคู่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var whtAsset = Acc(s.CompanyId, "11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset);
        db.ChartOfAccounts.Add(whtAsset);
        await db.SaveChangesAsync();
        var pay = await CreditAsync(db, s, 1_040m, UnappliedReason.Overpaid);
        // ค่าบริการ 1,000 + VAT 70 − หัก ณ ที่จ่าย 30 ⇒ ยอดค้าง (เงินสด) 1,040 · ลูกหนี้ตั้งไว้ 1,070
        var inv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 70m, wht: 30m);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var done = await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv.Id, 1_040m, Today), "tester");
            Assert.Equal((0m, DocumentStatus.Paid), (done.DocumentBalanceDue, done.DocumentStatus));
        }
        Guid allocationId;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(-30m, await GlCreditNetAsync(check, s.CompanyId, whtAsset.Id));          // Dr 11910 30
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, s.ArId));                 // ลูกหนี้ของทุกใบถูกตัดครบ (1,040 เงิน + 30 ภาษีถูกหัก)
            allocationId = await check.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == pay.Id && a.IsLateAllocation).Select(a => a.Id).SingleAsync();
            // JE ภาษีถูกหักอ้างเลขของการจัดสรร ⇒ ยกเลิกการจัดสรรหาเจอ
            var reference = CustomerCreditAllocation.JournalReference(pay.PaymentNumber, 1);
            Assert.Equal(2, await check.JournalEntries.AsNoTracking().CountAsync(j => j.CompanyId == s.CompanyId && j.Reference == reference && j.OriginalEntryId == null));
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).UndoAllocationAsync(s.CompanyId, pay.Id, allocationId, "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(0m, await GlCreditNetAsync(check, s.CompanyId, whtAsset.Id));
            Assert.Equal(-1_070m, await GlCreditNetAsync(check, s.CompanyId, s.ArId));
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == inv.Id);
            Assert.Equal((DocumentStatus.Approved, 1_040m), (d.Status, d.BalanceDue));
            Assert.Equal(1_040m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
        }
    }

    // ───────── M1: ระงับการทวง — เงินแต่ละบาทนับครั้งเดียว เก่าสุดก่อน ─────────

    [Fact]
    public async Task M1_ระงับการทวงหลายใบ_หักเงินสะสมเก่าสุดก่อน_ใบที่เงินไม่พอและใบที่จัดสรรไม่ได้ยังถูกทวง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        await CreditAsync(db, s, 1_000m, UnappliedReason.Overpaid);
        var oldest = await InvoiceAsync(db, s, s.CustomerId, 800m, 0m, date: Today.AddDays(-20));
        var newer = await InvoiceAsync(db, s, s.CustomerId, 800m, 0m, date: Today.AddDays(-5));     // เงินเหลือ 200 < 800 ⇒ ยังทวง (เดิมถูกระงับทั้งสองใบ)
        var small = await InvoiceAsync(db, s, s.CustomerId, 150m, 0m, date: Today.AddDays(-2));     // 200 ที่เหลือพอปิดใบนี้
        var otherCustomer = await InvoiceAsync(db, s, s.OtherCustomerId, 100m, 0m, date: Today.AddDays(-20));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var holds = await CustomerCreditQuery.DunningHoldsAsync(check, s.CompanyId);
            Assert.True(holds.ContainsKey(oldest.Id));
            Assert.False(holds.ContainsKey(newer.Id));
            Assert.True(holds.ContainsKey(small.Id));
            Assert.False(holds.ContainsKey(otherCustomer.Id));
        }
        // ใบเก่าสุดไม่มีสมุดรายวันตอนออกใบ (จัดสรรจริงไม่ได้) ⇒ ไม่ระงับ และไม่กินเงินของใบถัดไป
        await db.JournalEntries.Where(j => j.CompanyId == s.CompanyId && j.SourceDocumentId == oldest.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, JournalEntryStatus.Draft));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var holds = await CustomerCreditQuery.DunningHoldsAsync(check, s.CompanyId);
            Assert.False(holds.ContainsKey(oldest.Id));
            Assert.True(holds.ContainsKey(newer.Id));
            Assert.True(holds.ContainsKey(small.Id));
        }
    }

    // ───────── M2 / M5 / M7 ─────────

    [Fact]
    public async Task M2M5M7_ยอดเงินเกินไม่ตรง_ผูกใบมีVatบางส่วน_ยอดคงเหลือเปลี่ยนแล้ว_ถูกปฏิเสธโดยไม่มีอะไรถูกเขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var plain = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        var vatInv = await InvoiceAsync(db, s, s.CustomerId, 10_000m, 700m);

        // M2: เงินเกินที่หน้าจอแสดงไม่ตรงกับที่เซิร์ฟเวอร์คิด
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).CreateMultiDocPaymentAsync(s.CompanyId,
                Receive(1_500m, UnappliedReason.Overpaid, (plain.Id, 1_000m)) with { ExpectedExcess = 300m }, "tester"));
            Assert.Contains("ไม่ตรงกับที่ระบบคำนวณ", ex.Message, StringComparison.Ordinal);
        }
        // M5 (แผงเงินเกิน/หลายใบ): ผูกใบแจ้งหนี้มี VAT บางส่วน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Docs(req).CreateMultiDocPaymentAsync(s.CompanyId,
                Receive(1_500m, null, (plain.Id, 1_000m), (vatInv.Id, 500m)), "tester"), VatInvoiceSettlementGate.RuleCode);
        // M5 (รับชำระใบเดียวจากหน้าจอ) — ทางเข้าอัตโนมัติ (เงินเข้าจริงจากภายนอก) ไม่ถูกกัน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Docs(req).CreatePaymentAsync(s.CompanyId,
                new CreatePaymentRequest(vatInv.Id, Today, 500m, PaymentMethod.BankTransfer, "SLIP-P", null, null), "tester", PaymentEntrySource.Manual),
                VatInvoiceSettlementGate.RuleCode);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await check.Payments.AsNoTracking().AnyAsync(p => p.CompanyId == s.CompanyId));
            Assert.Equal(10_700m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == vatInv.Id)).BalanceDue);
            Assert.Equal(1_000m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == plain.Id)).BalanceDue);
        }

        // ยอดตรง (ExpectedExcess = 500) ผ่าน
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId,
                Receive(1_500m, UnappliedReason.Overpaid, (plain.Id, 1_000m)) with { ExpectedExcess = 500m }, "tester");
        Assert.Equal(500m, pay.UnappliedCredit);

        // M5 (จัดสรรทีหลัง): 500 ตัดใบมี VAT 10,700 บางส่วน = ปฏิเสธ · ทิศตรงข้าม: ใบไม่มี VAT จัดสรรบางส่วนได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(vatInv.Id, 500m), "tester"),
                VatInvoiceSettlementGate.RuleCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var targets = await Credits(req).TargetsAsync(s.CompanyId, pay.Id);
            Assert.NotNull(targets.Single(t => t.DocumentId == vatInv.Id).BlockedReason);   // หน้าจอเห็นเหตุผลก่อนกด
        }
        var plain2 = await InvoiceAsync(db, s, s.CustomerId, 900m, 0m);
        // M7: ยอดคงเหลือที่หน้าจอเห็นตอนกดไม่ตรงของจริง (กดซ้ำ/สองแท็บ)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(plain2.Id, 200m, null, ExpectedUnappliedBefore: 700m), "tester"), CustomerCreditAllocation.StaleRuleCode);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var part = await Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(plain2.Id, 200m, null, ExpectedUnappliedBefore: 500m), "tester");
            Assert.Equal((300m, 700m, DocumentStatus.PartiallyPaid), (part.RemainingUnapplied, part.DocumentBalanceDue, part.DocumentStatus));
        }
        // กดซ้ำด้วยคำขอเดิม (ยังเห็น 500) = ถูกกัน ไม่จัดสรรรอบสอง
        using (var req = DbTestDatabase.TryCreateContext()!)
            await RefusedAsync(() => Credits(req).AllocateAsync(s.CompanyId, pay.Id,
                new AllocateCustomerCreditRequest(plain2.Id, 200m, null, ExpectedUnappliedBefore: 500m), "tester"), CustomerCreditAllocation.StaleRuleCode);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(300m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
    }

    // ───────── M3: วันที่รับชำระที่ระบบประทับไม่ไหลไปเอกสารลูก ─────────

    [Fact]
    public async Task M3_วันที่รับชำระที่ระบบประทับตอนจัดสรร_ไม่สืบทอดไปใบที่แปลงต่อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 1_070m, UnappliedReason.UninvoicedSupply);
        var inv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 70m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(inv.Id, 1_070m, Today), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(Today, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).PaymentDate?.Date);   // ระบบประทับ
        DocumentResponse child;
        using (var req = DbTestDatabase.TryCreateContext()!)
            child = await Docs(req).ConvertDocumentAsync(s.CompanyId, inv.Id, DocumentType.TaxInvoice, "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await check.Documents.AsNoTracking().SingleAsync(d => d.Id == child.Id)).PaymentDate);
    }

    // ───────── ยกเลิกเอกสาร/การรับชำระที่ผูกกับเงินรอจัดสรร ─────────

    [Fact]
    public async Task ยกเลิกใบที่การรับชำระยังมีเงินรอจัดสรรค้าง_และยกเลิกการรับชำระที่เงินส่วนเกินถูกนำไปตัดใบอื่น_ถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s, s.CustomerId, 1_000m, 0m);
        PaymentResponse pay;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pay = await Docs(req).CreateMultiDocPaymentAsync(s.CompanyId, Receive(1_500m, UnappliedReason.Overpaid, (inv.Id, 1_000m)), "tester");
        // CC-VOID-DOC-CREDIT: ยกเลิกใบ = cascade ยกเลิกการรับชำระ = เงินรอจัดสรร 500 หายเงียบ ⇒ ปฏิเสธ + ทางไปต่อ
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await RefusedAsync(() => Docs(req).VoidDocumentAsync(s.CompanyId, inv.Id), "CC-VOID-DOC-CREDIT");
            Assert.Contains(pay.PaymentNumber, ex.Message, StringComparison.Ordinal);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == inv.Id);
            Assert.Equal((DocumentStatus.Paid, 0m), (d.Status, d.BalanceDue));
            Assert.Equal(500m, (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).UnappliedAmount);
            Assert.Equal(500m, await GlCreditNetAsync(check, s.CompanyId, await SuspenseAccountIdAsync(check, s.CompanyId)));
        }

        // CC-VOID-EXCESS-APPLIED (ข้อมูลก่อนรอบ 258): JE เงินส่วนเกินของการชำระถูก "นำมัดจำ (JV) ตัดชำระ" ใบอื่นไปแล้ว ⇒ ยกเลิกการชำระไม่ได้
        var docA = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        var victim = await InvoiceAsync(db, s, s.OtherCustomerId, 400m, 0m);
        var (legacy, legacyJe) = await LegacyExcessAsync(db, s, docA);
        legacyJe.DepositAppliedToDocumentId = victim.Id;
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await RefusedAsync(() => Docs(req).VoidPaymentAsync(s.CompanyId, legacy.Id), "CC-VOID-EXCESS-APPLIED");
            Assert.Contains(victim.DocumentNumber, ex.Message, StringComparison.Ordinal);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False((await check.Payments.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == legacy.Id)).IsDeleted);
            Assert.Equal(JournalEntryStatus.Posted, (await check.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == legacyJe.Id)).Status);
        }
    }

    // ───────── สิทธิ์ของ CustomerCreditController ─────────

    private static (CustomerCreditController Controller, ImportHubControllerGateDbTests.Rec<ICustomerCreditService> Service) Controller(
        Accounting.Data.AccountingDbContext db, params string[] keys)
    {
        var (service, rec) = ImportHubControllerGateDbTests.Rec<ICustomerCreditService>.Create((name, a) => name switch
        {
            nameof(ICustomerCreditService.AllocateAsync) or nameof(ICustomerCreditService.UndoAllocationAsync)
                => new CustomerCreditAllocationResult((Guid)a[1]!, "PAY-X", Guid.Empty, "INV-X", 0m, 0m, 0m, DocumentStatus.Paid, Array.Empty<string>()),
            nameof(ICustomerCreditService.ConfirmLegacyAsync) or nameof(ICustomerCreditService.CloseLegacyAsync)
                => new CustomerCreditRow((Guid)a[1]!, "PAY-X", null, null, Today, 0m, 0m, nameof(UnappliedReason.Unknown), "", null, 0, null,
                    new List<CustomerCreditAllocationRow>()),
            _ => null,
        });
        var (perms, _) = ImportHubControllerGateDbTests.Rec<IPermissionService>.Create((name, a) => name == nameof(IPermissionService.HasPermissionAsync)
            ? keys.Contains((string)a[2]!)
            : new List<string>(keys));
        var controller = new CustomerCreditController(service, perms, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "Test")),
                },
            },
        };
        return (controller, rec);
    }

    private static int StatusOf(object result)
    {
        var r = result is IConvertToActionResult c ? c.Convert() : (IActionResult)result;
        return r is IStatusCodeActionResult sc ? sc.StatusCode ?? 200 : 200;
    }

    [Fact]
    public async Task สิทธิ์_ไม่เห็นฝั่งขาย_ไม่มีสิทธิ์อนุมัติหรือยกเลิก_ได้403และserviceไม่ถูกเรียก_เอกสารบริษัทอื่น404_ถือสิทธิ์จึงผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pay = await CreditAsync(db, s, 600m, UnappliedReason.Overpaid);
        var target = await InvoiceAsync(db, s, s.CustomerId, 600m, 0m);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Credits(req).AllocateAsync(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(target.Id, 600m), "tester");
        var allocationId = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == pay.Id && a.IsLateAllocation).Select(a => a.Id).SingleAsync();
        var otherCompany = new Company { Name = "บริษัทอื่น (สิทธิ์)", TaxId = "0105556000033" };
        db.Companies.Add(otherCompany);
        await db.SaveChangesAsync();
        var foreignDoc = await InvoiceAsync(db, s, s.CustomerId, 100m, 0m, companyId: otherCompany.Id);
        var body = new AllocateCustomerCreditRequest(target.Id, 100m);

        // (1) เห็นเฉพาะฝั่งซื้อ ⇒ ทุกเส้นอ่าน 403
        var (purchaseOnly, purchaseRec) = Controller(db, PermissionKeys.DocumentPurchaseView);
        Assert.Equal(403, StatusOf(await purchaseOnly.List(s.CompanyId)));
        Assert.Equal(403, StatusOf(await purchaseOnly.Targets(s.CompanyId, pay.Id)));
        Assert.Equal(403, StatusOf(await purchaseOnly.ForDocument(s.CompanyId, target.Id)));
        Assert.Equal(403, StatusOf(await purchaseOnly.ExcessPlan(s.CompanyId, target.Id, 700m)));
        Assert.Empty(purchaseRec.Calls);

        // (2) เห็นฝั่งขายแต่ไม่มีสิทธิ์อนุมัติ/ยกเลิก ⇒ อ่านได้ · เขียนทุกเส้น 403 · service ไม่ถูกเรียกสำหรับเส้นเขียน
        var (viewer, viewerRec) = Controller(db, PermissionKeys.DocumentRevenueView);
        Assert.Equal(200, StatusOf(await viewer.List(s.CompanyId)));
        Assert.Equal(403, StatusOf(await viewer.Allocate(s.CompanyId, pay.Id, body)));
        Assert.Equal(403, StatusOf(await viewer.UndoAllocation(s.CompanyId, pay.Id, allocationId)));
        Assert.Equal(403, StatusOf(await viewer.ConfirmLegacy(s.CompanyId, pay.Id)));
        Assert.Equal(403, StatusOf(await viewer.CloseLegacy(s.CompanyId, pay.Id, new CloseLegacyCreditRequest("ล้างด้วยสมุดรายวันแล้ว"))));
        Assert.Equal(new[] { nameof(ICustomerCreditService.ListAsync) }, viewerRec.Calls.Select(c => c.Name).ToArray());

        // (3) อนุมัติฝั่งขายได้ แต่ยกเลิกไม่ได้ ⇒ จัดสรร/ยืนยัน 200 · ยกเลิกการจัดสรร/ปิดรายการ 403 · เอกสารของบริษัทอื่น 404 (ไม่แตะ service)
        var (approver, approverRec) = Controller(db, PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueApprove);
        Assert.Equal(404, StatusOf(await approver.Allocate(s.CompanyId, pay.Id, new AllocateCustomerCreditRequest(foreignDoc.Id, 100m))));
        Assert.Empty(approverRec.Calls);
        Assert.Equal(200, StatusOf(await approver.Allocate(s.CompanyId, pay.Id, body)));
        Assert.Equal(200, StatusOf(await approver.ConfirmLegacy(s.CompanyId, pay.Id)));
        Assert.Equal(403, StatusOf(await approver.UndoAllocation(s.CompanyId, pay.Id, allocationId)));
        Assert.Equal(403, StatusOf(await approver.CloseLegacy(s.CompanyId, pay.Id, new CloseLegacyCreditRequest("ล้างด้วยสมุดรายวันแล้ว"))));
        Assert.Equal(new[] { nameof(ICustomerCreditService.AllocateAsync), nameof(ICustomerCreditService.ConfirmLegacyAsync) },
            approverRec.Calls.Select(c => c.Name).ToArray());
        Assert.All(approverRec.Calls, c => Assert.Equal(s.CompanyId, (Guid)c.Args[0]!));

        // (4) สิทธิ์ยกเลิกฝั่งขาย ⇒ ยกเลิกการจัดสรร/ปิดรายการผ่าน · การจัดสรรของบริษัทอื่น = 404
        var (voider, voiderRec) = Controller(db, PermissionKeys.DocumentRevenueView, PermissionKeys.DocumentRevenueVoid);
        Assert.Equal(404, StatusOf(await voider.UndoAllocation(otherCompany.Id, pay.Id, allocationId)));
        Assert.Equal(200, StatusOf(await voider.UndoAllocation(s.CompanyId, pay.Id, allocationId)));
        Assert.Equal(200, StatusOf(await voider.CloseLegacy(s.CompanyId, pay.Id, new CloseLegacyCreditRequest("ล้างด้วยสมุดรายวันแล้ว"))));
        Assert.Equal(403, StatusOf(await voider.Allocate(s.CompanyId, pay.Id, body)));
        Assert.Equal(2, voiderRec.Calls.Count);
    }
}
