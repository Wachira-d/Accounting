using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.DocumentTemplate;
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
/// รอบ 266 แพ็ก B (erp-review/2026-11-12/team-266-manual-doc-link.md §4.3/§4.7 · คำตัดสิน 266-1-23…30) — <b>ย้ายการรับชำระที่ตัดผิดใบไปใบที่ถูก</b> ผ่านเมธอดจริงบน PostgreSQL ทั้งสองทิศ:
/// <list type="bullet">
/// <item>ย้ายสำเร็จ ⇒ ลูกหนี้ของทั้งสองใบถูก (เอกสาร + GL) · ผัง 21630 สุทธิ 0 · ขาเงินสด/คู่จับคู่ธนาคาร/ยอดธนาคาร/<c>Payment.DocumentId</c> ไม่ถูกแตะ ·
/// ใบเสร็จเดิมถูกยกเลิก (คงแถว) + ใบแทนลงวันที่รับเงินเดิม อ้างใบที่ถูก พิมพ์ "ออกแทนใบเลขที่ …" · ไม่ยิงงานหลังรับชำระซ้ำ · ใบเดิมถูกพักการทวง</item>
/// <item>ปฏิเสธ (ไม่มีอะไรถูกเขียน): e-Tax ถึงกรมสรรพากร · ภาษีขายของใบเดิมอยู่ในงวดที่ยื่นแล้ว · ใบผูกคู่ค้า · มีภาษีถูกหัก · ผู้บันทึกรับชำระย้ายเอง (SoD) — ทุกเหตุมีทางไปต่อ</item>
/// <item>กดซ้ำพร้อมกัน ⇒ ย้ายครั้งเดียว · ยกเลิกการรับชำระหลังย้าย ⇒ กลับครบทุกขา · ยกเลิกใบเดิมขณะเงินที่ย้ายออกยังมีผล ⇒ ปฏิเสธ · ถอด: ไม่ออกใบแทน = ย้ายกลับได้ · ออกใบแทนแล้ว = ปฏิเสธ</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class ReceiptLinkMoveDbTests
{
    private readonly ITestOutputHelper _out;
    public ReceiptLinkMoveDbTests(ITestOutputHelper output) => _out = output;

    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly DateTime Today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;
    private static readonly Guid Mover = Guid.NewGuid();

    private sealed record Seed(Guid CompanyId, Guid CustomerId, Guid ArId, Guid RevenueId, Guid BankGlId, Guid BankId, Guid Undue21913Id, Guid Vat21911Id);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบย้ายการรับชำระ " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000025", IsVatRegistered = false };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cash = Acc(company.Id, "11111", "เงินสด", AccountType.Asset);
        var bankGl = Acc(company.Id, "11121", "เงินฝากธนาคาร", AccountType.Asset);
        var ar = Acc(company.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var group216 = Acc(company.Id, "216", "เงินมัดจำและเงินรับล่วงหน้าอื่น", AccountType.Liability, level: 3);
        var vat = Acc(company.Id, "21911", "ภาษีขาย", AccountType.Liability);
        var undue = Acc(company.Id, "21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability);
        var revenue = Acc(company.Id, "41100", "รายได้จากการให้บริการ", AccountType.Revenue);
        db.ChartOfAccounts.AddRange(cash, bankGl, ar, group216, vat, undue, revenue);
        var customer = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า ก จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์หลัก", BankName = "กสิกร", AccountNumber = "1234567890", LinkedAccountId = bankGl.Id, CurrentBalance = 50_000m };
        db.BankAccounts.Add(bank);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id, ar.Id, revenue.Id, bankGl.Id, bank.Id, undue.Id, vat.Id);
    }

    /// <summary>ใบแจ้งหนี้ขายที่อนุมัติแล้ว (ไม่มี VAT) + JE ตั้งลูกหนี้</summary>
    private static async Task<Document> InvoiceAsync(Accounting.Data.AccountingDbContext db, Seed s, decimal amount, DateTime date)
    {
        var no = "INV-MV-" + Guid.NewGuid().ToString("N")[..8];
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentType = DocumentType.Invoice, DocumentNumber = no,
            Status = DocumentStatus.Approved, DocumentDate = date, SubTotal = amount, TotalAmount = amount, BalanceDue = amount,
            Currency = "THB", ExchangeRate = 1m, WhtCertAutoIssue = false,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = amount, Amount = amount, AccountId = s.RevenueId });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        var je = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "SV-" + no, EntryDate = d.DocumentDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.Sales, Description = "ออกใบ " + no, Reference = no, TotalDebit = amount, TotalCredit = amount,
            SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.ArId, DebitAmount = amount, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.RevenueId, CreditAmount = amount, LineOrder = 2 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return d;
    }

    private static DocumentService Docs(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!,
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            null!);

    private static ReceiptLinkService Links(Accounting.Data.AccountingDbContext db)
        => new(db, Docs(db), new CustomerCreditService(db, Docs(db), new AccountingService(db), NullLogger<CustomerCreditService>.Instance));

    /// <summary>บันทึกรับชำระเข้าบัญชีธนาคาร (เส้นจริง) — คืนแถวการรับชำระ (+ ใบเสร็จอัตโนมัติถ้าขอ)</summary>
    private static async Task<Payment> PayAsync(Seed s, Guid invoiceId, decimal amount, DateTime date, bool issueReceipt, string recordedBy = "recorder")
    {
        Guid id;
        using (var req = DbTestDatabase.TryCreateContext()!)
            id = (await Docs(req).CreatePaymentAsync(s.CompanyId,
                new CreatePaymentRequest(invoiceId, date, amount, PaymentMethod.BankTransfer, "SLIP-MV", null, null,
                    OverrideBankAccountId: s.BankId, IssueReceiptDocument: issueReceipt), recordedBy, PaymentEntrySource.Manual)).Id;
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.Payments.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    /// <summary>จับคู่รายการเดินบัญชีธนาคารกับการรับชำระ (สภาพหลังกระทบยอด)</summary>
    private static async Task<Guid> MatchBankLineAsync(Seed s, Payment pay)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        var line = new BankTransaction
        {
            CompanyId = s.CompanyId, BankAccountId = s.BankId, TransactionDate = pay.PaymentDate, TransactionType = BankTransactionType.Deposit,
            Amount = pay.Amount, Description = "โอนเข้า", ReconciliationStatus = ReconciliationStatus.Matched, MatchedPaymentId = pay.Id, ReconciledAt = DateTime.UtcNow,
        };
        db.BankTransactions.Add(line);
        await db.SaveChangesAsync();
        return line.Id;
    }

    private static async Task<ReceiptLinkResult> PreviewThenMoveAsync(Guid companyId, ReceiptLinkRequest request, Guid? user = null, string reason = "ลูกค้าแจ้งว่าโอนค่าใบนี้ ไม่ใช่ใบเดิม")
    {
        ReceiptLinkPreviewResponse p;
        using (var req = DbTestDatabase.TryCreateContext()!)
            p = await Links(req).PreviewAsync(companyId, request, user ?? Mover);
        Assert.True(p.Allowed, p.Message);
        Assert.True(p.RequiresReason);
        using var act = DbTestDatabase.TryCreateContext()!;
        return await Links(act).LinkAsync(companyId, request with { PreviewToken = p.PreviewToken, Reason = reason }, user ?? Mover);
    }

    /// <summary>ยอดสุทธิ (Dr − Cr) ของผังรหัสหนึ่ง จากสมุดรายวันที่ลงแล้วทั้งหมดของบริษัท (รวมรายการกลับ)</summary>
    private static async Task<decimal> GlNetAsync(Guid companyId, string accountCode, Guid? sourceDocumentId = null)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await (from l in db.JournalEntryLines.AsNoTracking()
                      join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                      join a in db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                      where j.CompanyId == companyId && !j.IsDeleted && !l.IsDeleted && a.CompanyId == companyId && a.AccountCode == accountCode
                            && (j.Status == JournalEntryStatus.Posted || j.Status == JournalEntryStatus.Reversed)
                            && (sourceDocumentId == null || j.SourceDocumentId == sourceDocumentId)
                      select (decimal?)(l.DebitAmount - l.CreditAmount)).SumAsync() ?? 0m;
    }

    private static async Task<Document> DocAsync(Guid id)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.Documents.AsNoTracking().IgnoreQueryFilters().SingleAsync(d => d.Id == id);
    }

    private static async Task<Payment> PaymentAsync(Guid id)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.Payments.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == id);
    }

    /// <summary>สภาพที่ต้อง "ไม่ถูกแตะ" เมื่อการย้ายถูกปฏิเสธ: ยอดของทั้งสองใบ · ตัวชี้ของการรับชำระ · ใบเสร็จ · ไม่มีสมุดรายวันของการย้าย</summary>
    private static async Task AssertNothingMovedAsync(Seed s, Document wrong, Document right, Payment pay)
    {
        var a = await DocAsync(wrong.Id);
        var b = await DocAsync(right.Id);
        Assert.Equal((0m, DocumentStatus.Paid), (a.BalanceDue, a.Status));
        Assert.Equal((right.TotalAmount, DocumentStatus.Approved), (b.BalanceDue, b.Status));
        var p = await PaymentAsync(pay.Id);
        Assert.Null(p.MovedToDocumentId);
        Assert.Equal(pay.ReceiptDocumentId, p.ReceiptDocumentId);
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));
        using var db = DbTestDatabase.TryCreateContext()!;
        Assert.False(await db.JournalEntries.AsNoTracking().AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference != null && j.Reference.StartsWith(pay.PaymentNumber + "/M")));
        Assert.False(await db.PaymentAllocations.IgnoreQueryFilters().AsNoTracking().AnyAsync(x => x.CompanyId == s.CompanyId && x.PaymentId == pay.Id));
        if (pay.ReceiptDocumentId is Guid rid)
            Assert.NotEqual(DocumentStatus.Voided, (await DocAsync(rid)).Status);
    }

    private static async Task<BusinessRuleException> RefusedAsync(Seed s, ReceiptLinkRequest request, string expectedCode, Guid? user = null)
    {
        ReceiptLinkPreviewResponse p;
        using (var req = DbTestDatabase.TryCreateContext()!)
            p = await Links(req).PreviewAsync(s.CompanyId, request, user ?? Mover);
        Assert.False(p.Allowed);
        Assert.Equal(expectedCode, p.RuleCode);
        Assert.False(string.IsNullOrWhiteSpace(p.WayForward));   // ทุกการปฏิเสธมีทางไปต่อ (F2 ข้อ 8)
        using var act = DbTestDatabase.TryCreateContext()!;
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Links(act).LinkAsync(s.CompanyId,
            request with { PreviewToken = "000000000000000000000000", Reason = "ทดสอบ" }, user ?? Mover));
        Assert.Equal(expectedCode, ex.RuleCode);
        return ex;
    }

    // ───────── ย้ายสำเร็จ ─────────

    [Fact]
    public async Task Move_ArBothDocsCorrect_21630NetZero_KeepsCashLineAndBankMatch()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var moneyDate = Today.AddDays(-3);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, moneyDate, issueReceipt: true);
        Assert.NotNull(pay.ReceiptDocumentId);
        var bankLineId = await MatchBankLineAsync(s, pay);
        decimal bankBefore;
        int automationRunsBefore;
        using (var read = DbTestDatabase.TryCreateContext()!)
        {
            bankBefore = await read.BankAccounts.AsNoTracking().Where(x => x.Id == s.BankId).Select(x => x.CurrentBalance).SingleAsync();
            automationRunsBefore = await read.PostPaymentAutomationRuns.AsNoTracking().CountAsync(r => r.CompanyId == s.CompanyId);
        }
        Assert.Equal(51_000m, bankBefore);

        // ผู้สมัคร: เปิดจากใบที่ถูก เห็นใบเสร็จของการรับชำระใบผิดเป็นแถวที่ "ย้ายได้ + ออกใบแทน"
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var c = await Links(req).CandidatesAsync(s.CompanyId, right.Id, Mover);
            var row = Assert.Single(c.Items, x => x.ReceiptDocumentId == pay.ReceiptDocumentId);
            Assert.Equal((true, nameof(ReceiptLinkRoute.MoveSettlementWithReissue)), (row.Allowed, row.Route));
        }
        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));
        Assert.Equal((nameof(ReceiptLinkRoute.MoveSettlementWithReissue), 0m, nameof(DocumentStatus.Paid)), (res.Route, res.InvoiceBalanceDue, res.InvoiceStatus));

        // ── ลูกหนี้ของทั้งสองใบ: ตัวเอกสาร + GL ──
        var a = await DocAsync(wrong.Id);
        var b = await DocAsync(right.Id);
        Assert.Equal((1_000m, 0m, DocumentStatus.Approved), (a.BalanceDue, a.PaidAmount, a.Status));
        Assert.Equal((0m, 1_000m, DocumentStatus.Paid), (b.BalanceDue, b.PaidAmount, b.Status));
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11310", wrong.Id));   // ตั้ง 1000 − รับ 1000 + ถอนกลับ 1000
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, "11310", right.Id));       // ตั้ง 1000 − ย้ายมา 1000
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11310"));             // ลูกหนี้รวมของบริษัทเท่าเดิม (ย้าย ไม่ใช่รับใหม่)
        // ── ผังพัก 21630 สุทธิ 0 ──
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));

        using var check = DbTestDatabase.TryCreateContext()!;
        // ── ขาเงินสด: สมุดรายวันของการรับชำระยังมีผล ไม่ถูกกลับ · ยอดธนาคารเท่าเดิม · คู่จับคู่ธนาคารคงเดิม ──
        var cashJe = await check.JournalEntries.AsNoTracking().SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == pay.PaymentNumber);
        Assert.Equal((JournalEntryStatus.Posted, (Guid?)null, moneyDate, (Guid?)wrong.Id), (cashJe.Status, cashJe.ReversedByEntryId, cashJe.EntryDate.Date, cashJe.SourceDocumentId));
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11121"));
        Assert.Equal(bankBefore, await check.BankAccounts.AsNoTracking().Where(x => x.Id == s.BankId).Select(x => x.CurrentBalance).SingleAsync());
        var bankLine = await check.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == bankLineId);
        Assert.Equal((ReconciliationStatus.Matched, (Guid?)pay.Id), (bankLine.ReconciliationStatus, bankLine.MatchedPaymentId));
        // ── การรับชำระ: ใบเดิมในประวัติไม่ถูกแก้ · ใบที่มีผล = ใบที่ถูก · แถวเดิมถูกปล่อย (ไม่ลบ) + แถวใหม่ชี้กลับ ──
        var p = await PaymentAsync(pay.Id);
        Assert.Equal((wrong.Id, (Guid?)right.Id, false), (p.DocumentId, p.MovedToDocumentId, p.IsDeleted));
        var rows = await check.PaymentAllocations.IgnoreQueryFilters().AsNoTracking().Where(x => x.PaymentId == pay.Id).ToListAsync();
        var released = Assert.Single(rows, x => x.IsDeleted);
        var live = Assert.Single(rows, x => !x.IsDeleted);
        Assert.Equal((wrong.Id, 1_000m, Mover.ToString()), (released.DocumentId, released.AllocatedAmount, released.ReleasedBy));
        Assert.NotNull(released.ReleasedAt);
        Assert.NotNull(released.ReleaseJournalEntryId);
        Assert.Equal((right.Id, 1_000m, (Guid?)released.Id, false), (live.DocumentId, live.AllocatedAmount, live.MovedFromAllocationId, live.IsLateAllocation));
        // ── สองขาอ้างเลขการรับชำระเดิม ลงวันที่รับเงินเดิม (งวดเปิด) ──
        var legs = await check.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == s.CompanyId && j.Reference != null && j.Reference.StartsWith(pay.PaymentNumber + "/M")).OrderBy(j => j.Reference).ToListAsync();
        Assert.Equal(new[] { pay.PaymentNumber + "/M1-IN", pay.PaymentNumber + "/M1-OUT" }, legs.Select(j => j.Reference).ToArray());
        Assert.All(legs, j => Assert.Equal(moneyDate, j.EntryDate.Date));
        // ── ไม่ยิงงานหลังรับชำระซ้ำ · ใบเดิมถูกพักการทวง (ไม่ขยับระดับ) · ร่องรอย ──
        Assert.Equal(automationRunsBefore, await check.PostPaymentAutomationRuns.AsNoTracking().CountAsync(r => r.CompanyId == s.CompanyId));
        Assert.NotNull(a.LastDunningSentAt);
        Assert.Null(a.LastDunningLevel);
        Assert.Equal(1, await check.AuditLogs.AsNoTracking().CountAsync(x => x.CompanyId == s.CompanyId && x.NewValues != null && x.NewValues.Contains(ReceiptLinkPolicy.MoveRuleCode)));
        // ── ผู้อ่าน "การชำระของใบ": ใบผิดไม่มีการชำระแล้ว · ใบถูกมี ──
        Assert.DoesNotContain(await Docs(check).GetPaymentsAsync(s.CompanyId, wrong.Id), x => x.Id == pay.Id);
        Assert.Contains(await Docs(check).GetPaymentsAsync(s.CompanyId, right.Id), x => x.Id == pay.Id);
        Assert.Contains(res.Notices, n => n.Contains("พักหนังสือทวง", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_OldReceiptVoidedNotDeleted_ReissuesReceiptOnOriginalMoneyDate_PrintsReplacementLine()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var moneyDate = Today.AddDays(-3);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, moneyDate, issueReceipt: true);
        var oldReceipt = await DocAsync(pay.ReceiptDocumentId!.Value);

        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, oldReceipt.Id, null), reason: "ตัดผิดใบ");

        var old = await DocAsync(oldReceipt.Id);
        Assert.Equal((DocumentStatus.Voided, false, oldReceipt.DocumentNumber), (old.Status, old.IsDeleted, old.DocumentNumber));   // ยกเลิก คงแถว คงเลข (ม.86/4 · ม.87)
        var p = await PaymentAsync(pay.Id);
        Assert.NotNull(p.ReceiptDocumentId);
        Assert.NotEqual(oldReceipt.Id, p.ReceiptDocumentId);
        Assert.Equal(res.ReceiptDocumentId, p.ReceiptDocumentId);
        var neo = await DocAsync(p.ReceiptDocumentId!.Value);
        Assert.Equal((Guid?)neo.Id, old.ReplacedByDocumentId);
        Assert.Equal((DocumentStatus.Paid, true, (Guid?)pay.Id, (Guid?)right.Id, (Guid?)old.Id, moneyDate, 1_000m, "ตัดผิดใบ"),
            (neo.Status, neo.IsSettlementReceipt, neo.SettlementPaymentId, neo.RelatedDocumentId, neo.ReplacesDocumentId, neo.DocumentDate.Date, neo.TotalAmount, neo.ReplacementReason));
        Assert.DoesNotContain("DRAFT", neo.DocumentNumber, StringComparison.Ordinal);
        Assert.NotEqual(old.DocumentNumber, neo.DocumentNumber);
        Assert.Equal(right.DocumentNumber, neo.Reference);

        // กระดาษ: ใบแทนพิมพ์ "ออกแทนใบเลขที่ {ใบเดิม}" (ตัวเรนเดอร์ HTML จริง · ข้อความเดียวกับ QuestPDF ผ่าน ReplacementReferenceText) · ใบเดิมไม่มีบรรทัดนี้
        using var render = DbTestDatabase.TryCreateContext()!;
        var pdf = new PdfGenerationService(render, new DocumentTemplateService(render));
        var html = await pdf.GenerateDocumentHtmlAsync(s.CompanyId, new GeneratePdfRequest(neo.Id, null, null, null, null));
        Assert.Contains("ออกแทนใบเลขที่ " + old.DocumentNumber, html, StringComparison.Ordinal);
        Assert.Contains(right.DocumentNumber, html, StringComparison.Ordinal);
        var invoiceHtml = await pdf.GenerateDocumentHtmlAsync(s.CompanyId, new GeneratePdfRequest(right.Id, null, null, null, null));
        Assert.DoesNotContain("ออกแทนใบเลขที่", invoiceHtml, StringComparison.Ordinal);   // ทิศตรงข้าม: ใบที่ไม่ได้ออกแทนใครไม่มีบรรทัดนี้
    }

    [Fact]
    public async Task Move_ClosedAccountingPeriod_PostsToday()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var moneyDate = Today.AddDays(-35);
        var wrong = await InvoiceAsync(db, s, 800m, Today.AddDays(-40));
        var right = await InvoiceAsync(db, s, 800m, Today.AddDays(-40));
        var pay = await PayAsync(s, wrong.Id, 800m, moneyDate, issueReceipt: true);
        // ปิดงวดบัญชีของเดือนที่รับเงิน "หลัง" บันทึกรับชำระ
        var start = new DateTime(moneyDate.Year, moneyDate.Month, 1);
        db.FiscalPeriods.Add(new FiscalPeriod
        {
            CompanyId = s.CompanyId, Name = $"{moneyDate:yyyy/MM}", Year = moneyDate.Year, Month = moneyDate.Month,
            StartDate = start, EndDate = start.AddMonths(1).AddDays(-1), Status = FiscalPeriodStatus.Closed,
        });
        await db.SaveChangesAsync();

        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));
        Assert.Contains(res.Notices, n => n.Contains("ปิดแล้ว", StringComparison.Ordinal));
        using var check = DbTestDatabase.TryCreateContext()!;
        var legs = await check.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == s.CompanyId && j.Reference != null && j.Reference.StartsWith(pay.PaymentNumber + "/M")).ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, j => Assert.Equal(Today, j.EntryDate.Date));   // สองขาลงวันที่ทำรายการ (งวดเดิมไม่ถูกแตะ)
        // ใบแทนยังลงวันที่รับเงินเดิม (ม.105 — วันที่บนใบรับ = วันรับเงิน ไม่ใช่วันลงบัญชี)
        var neo = await DocAsync((await PaymentAsync(pay.Id)).ReceiptDocumentId!.Value);
        Assert.Equal(moneyDate, neo.DocumentDate.Date);
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));
    }

    // ───────── ปฏิเสธ (ไม่มีอะไรถูกเขียน) ─────────

    [Fact]
    public async Task Move_RefusedWhenEtaxReachedRd()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        db.EtaxInvoices.Add(new EtaxInvoice
        {
            CompanyId = s.CompanyId, DocumentId = pay.ReceiptDocumentId!.Value, EtaxRefNumber = "ETX-" + Guid.NewGuid().ToString("N")[..8], XmlContent = "<x/>",
            Status = EtaxStatus.Accepted, SubmittedAt = DateTime.UtcNow, AcceptedAt = DateTime.UtcNow, SellerName = "ผู้ขาย", SellerTaxId = "0105556000025", BuyerName = "ผู้ซื้อ",
        });
        await db.SaveChangesAsync();

        await RefusedAsync(s, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.EtaxSentCode);
        await AssertNothingMovedAsync(s, wrong, right, pay);
    }

    [Fact]
    public async Task Move_RefusedWhenSourceVatPeriodFiled()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var moneyDate = Today.AddDays(-3);
        var wrong = await InvoiceAsync(db, s, 1_070m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_070m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_070m, moneyDate, issueReceipt: true);
        // สภาพของใบแจ้งหนี้บริการที่ภาษีขายถึงกำหนดเพราะเงินก้อนนี้ (21913 → 21911 ณ วันรับเงิน) แล้ว ภ.พ.30 ของเดือนนั้นยื่นไปแล้ว
        await db.Documents.Where(d => d.Id == wrong.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.OutputVatDueAt, (DateTime?)moneyDate));
        var reclass = new JournalEntry
        {
            CompanyId = s.CompanyId, EntryNumber = "JV-VAT-" + Guid.NewGuid().ToString("N")[..6], EntryDate = moneyDate, Status = JournalEntryStatus.Posted,
            JournalType = JournalType.General, Description = "ภาษีขายถึงกำหนด", Reference = wrong.DocumentNumber, TotalDebit = 70m, TotalCredit = 70m,
            SourceDocumentId = wrong.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        reclass.Lines.Add(new JournalEntryLine { AccountId = s.Undue21913Id, DebitAmount = 70m, LineOrder = 1 });
        reclass.Lines.Add(new JournalEntryLine { AccountId = s.Vat21911Id, CreditAmount = 70m, LineOrder = 2 });
        db.JournalEntries.Add(reclass);
        db.TaxReports.Add(new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VAT, Year = moneyDate.Year, Month = moneyDate.Month, Status = TaxReportStatus.Filed });
        await db.SaveChangesAsync();

        await RefusedAsync(s, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.VatFiledCode);
        await AssertNothingMovedAsync(s, wrong, right, pay);
        // สมุดรายวันภาษีของงวดที่ยื่นแล้วไม่ถูกกลับ
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.JournalEntries.AsNoTracking().SingleAsync(j => j.Id == reclass.Id)).ReversedByEntryId);
    }

    [Fact]
    public async Task Move_PartnerLinked_Refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        // การรับชำระนี้ถูกแจ้งไปยังผู้ซื้อที่เป็นคู่ค้า NextAcc แล้ว
        db.PartnerPaymentNotices.Add(new PartnerPaymentNotice
        {
            CompanyId = s.CompanyId, TradingPartnershipId = Guid.NewGuid(), LinkId = Guid.NewGuid(), SourceCompanyId = s.CompanyId, SourceDocumentId = wrong.Id,
            SourceDocumentNumber = wrong.DocumentNumber, TargetDocumentId = Guid.NewGuid(), SellerPaymentId = pay.Id, CashAmount = 1_000m, PaidAt = pay.PaymentDate,
        });
        await db.SaveChangesAsync();

        await RefusedAsync(s, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.PartnerCode);
        await AssertNothingMovedAsync(s, wrong, right, pay);
    }

    [Fact]
    public async Task Move_PaymentWithWithholdingTax_Refused_WithoutWht_Allowed()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        // การรับชำระที่มีภาษีถูกหัก ณ ที่จ่าย (50 ทวิ ของลูกค้าอ้างใบเดิม) — ระบบไม่เดาว่าภาษีเป็นของใบไหน
        await db.Payments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.WithholdingTaxAmount, 30m));
        var ex = await RefusedAsync(s, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.MoveWhtCode);
        Assert.Contains("ทางไปต่อ", ex.Message, StringComparison.Ordinal);
        await AssertNothingMovedAsync(s, wrong, right, pay);

        // ทิศตรงข้าม: การรับชำระเดียวกันเมื่อไม่มีภาษีถูกหัก ย้ายได้
        await db.Payments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.WithholdingTaxAmount, 0m));
        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));
        Assert.Equal(0m, res.InvoiceBalanceDue);
    }

    [Fact]
    public async Task Move_SegregationOfDuties_RecorderCannotMoveOwnPayment_OtherUserCan()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        db.CompanySettings.Add(new CompanySettings { CompanyId = s.CompanyId, SodBlockSelfApproval = true });
        await db.SaveChangesAsync();
        var recorder = Guid.NewGuid();
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true, recordedBy: recorder.ToString());

        await RefusedAsync(s, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.MoveApprovalCode, user: recorder);
        await AssertNothingMovedAsync(s, wrong, right, pay);
        // ทิศตรงข้าม: ผู้ใช้อีกคนย้ายได้
        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null), user: Mover);
        Assert.Equal(0m, res.InvoiceBalanceDue);
    }

    [Fact]
    public async Task Move_OverTargetBalance_Refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var small = await InvoiceAsync(db, s, 600m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        await RefusedAsync(s, new ReceiptLinkRequest(small.Id, pay.ReceiptDocumentId, null), ReceiptLinkPolicy.OverpayCode);
        await AssertNothingMovedAsync(s, wrong, small, pay);
    }

    // ───────── กดซ้ำ / ผลตามหลัง ─────────

    [Fact]
    public async Task Move_Concurrent_SecondGets409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        var request = new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null);
        ReceiptLinkPreviewResponse p;
        using (var req = DbTestDatabase.TryCreateContext()!)
            p = await Links(req).PreviewAsync(s.CompanyId, request, Mover);
        var confirmed = request with { PreviewToken = p.PreviewToken, Reason = "กดซ้ำ" };

        async Task<Exception?> AttemptAsync()
        {
            using var ctx = DbTestDatabase.TryCreateContext()!;
            try { await Links(ctx).LinkAsync(s.CompanyId, confirmed, Mover); return null; }
            catch (Exception ex) { return ex; }
        }
        var outcomes = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));
        Assert.Single(outcomes, o => o == null);                                   // ย้ายสำเร็จครั้งเดียว
        var failed = Assert.IsType<BusinessRuleException>(Assert.Single(outcomes, o => o != null));
        Assert.Equal(409, failed.StatusCode);

        var b = await DocAsync(right.Id);
        Assert.Equal((0m, 1_000m), (b.BalanceDue, b.PaidAmount));                  // ไม่ตัดยอดสองรอบ
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.PaymentAllocations.AsNoTracking().CountAsync(x => x.PaymentId == pay.Id));   // แถวที่มีผลแถวเดียว
        Assert.Equal(2, await check.JournalEntries.AsNoTracking().CountAsync(j => j.CompanyId == s.CompanyId && j.Reference != null && j.Reference.StartsWith(pay.PaymentNumber + "/M")));
        Assert.Equal(1, await check.Documents.AsNoTracking().CountAsync(d => d.CompanyId == s.CompanyId && d.IsSettlementReceipt && d.RelatedDocumentId == right.Id && d.Status != DocumentStatus.Voided));
    }

    [Fact]
    public async Task Move_ThenVoidPayment_ReversesCashAndBothLegs()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));
        var replacementId = (await PaymentAsync(pay.Id)).ReceiptDocumentId!.Value;

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Docs(req).VoidPaymentAsync(s.CompanyId, pay.Id);

        var a = await DocAsync(wrong.Id);
        var b = await DocAsync(right.Id);
        Assert.Equal((1_000m, DocumentStatus.Approved), (a.BalanceDue, a.Status));   // ใบผิดไม่ถูกแตะซ้ำ (ไม่ติดลบ ไม่กลับเป็นชำระแล้ว)
        Assert.Equal((1_000m, 0m, DocumentStatus.Approved), (b.BalanceDue, b.PaidAmount, b.Status));
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, "11121"));                    // เงินสดถูกกลับครั้งเดียว
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));
        Assert.Equal(2_000m, await GlNetAsync(s.CompanyId, "11310"));                // ลูกหนี้ทั้งสองใบเปิดเต็ม
        Assert.Equal(DocumentStatus.Voided, (await DocAsync(replacementId)).Status);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(50_000m, await check.BankAccounts.AsNoTracking().Where(x => x.Id == s.BankId).Select(x => x.CurrentBalance).SingleAsync());
    }

    [Fact]
    public async Task VoidWrongInvoice_WhileMovedPaymentAlive_Refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).VoidDocumentAsync(s.CompanyId, wrong.Id));
            Assert.Equal(ReceiptLinkPolicy.MoveVoidSourceCode, ex.RuleCode);
            Assert.Contains("ทางไปต่อ", ex.Message, StringComparison.Ordinal);
        }
        // เงินสดที่รับจริงไม่ถูกกลับ · ใบถูกยังชำระแล้ว
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11121"));
        Assert.Equal(DocumentStatus.Paid, (await DocAsync(right.Id)).Status);
        Assert.NotEqual(DocumentStatus.Voided, (await DocAsync(wrong.Id)).Status);
    }

    // ───────── ถอด ─────────

    [Fact]
    public async Task Undo_WithoutReissue_RestoresBoth()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: false);
        Assert.Null(pay.ReceiptDocumentId);

        // การรับชำระที่ไม่มีใบเสร็จแยก: ย้ายด้วยเลขการรับชำระ — ไม่มีกระดาษออกใหม่
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var c = await Links(req).CandidatesAsync(s.CompanyId, right.Id, Mover);
            var row = Assert.Single(c.Items, x => x.PaymentId == pay.Id);
            Assert.Equal((true, nameof(ReceiptLinkRoute.MoveSettlement), (Guid?)null), (row.Allowed, row.Route, row.ReceiptDocumentId));
        }
        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, null, pay.Id));
        Assert.Equal((nameof(ReceiptLinkRoute.MoveSettlement), (Guid?)null), (res.Route, res.ReceiptDocumentId));
        Assert.Null((await PaymentAsync(pay.Id)).ReceiptDocumentId);
        Assert.Equal(DocumentStatus.Paid, (await DocAsync(right.Id)).Status);

        ReceiptLinkResult undone;
        using (var req = DbTestDatabase.TryCreateContext()!)
            undone = await Links(req).UndoAsync(s.CompanyId,
                new ReceiptLinkUndoRequest(nameof(ReceiptLinkRoute.MoveSettlement), PaymentId: pay.Id, AllocationId: res.AllocationId), Mover);
        Assert.Equal(wrong.Id, undone.InvoiceId);
        var a = await DocAsync(wrong.Id);
        var b = await DocAsync(right.Id);
        Assert.Equal((0m, DocumentStatus.Paid), (a.BalanceDue, a.Status));
        Assert.Equal((1_000m, 0m, DocumentStatus.Approved), (b.BalanceDue, b.PaidAmount, b.Status));
        Assert.Null((await PaymentAsync(pay.Id)).MovedToDocumentId);                 // กลับใบเดิม = ไม่มีตัวชี้ค้าง
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, UnappliedReceiptAccount.Code));
        Assert.Equal(0m, await GlNetAsync(s.CompanyId, "11310", wrong.Id));
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11310", right.Id));
        Assert.Equal(1_000m, await GlNetAsync(s.CompanyId, "11121"));                // ขาเงินสดไม่ถูกแตะทั้งไปและกลับ
    }

    [Fact]
    public async Task Undo_AfterReissue_Refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var wrong = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var right = await InvoiceAsync(db, s, 1_000m, Today.AddDays(-10));
        var pay = await PayAsync(s, wrong.Id, 1_000m, Today.AddDays(-3), issueReceipt: true);
        var res = await PreviewThenMoveAsync(s.CompanyId, new ReceiptLinkRequest(right.Id, pay.ReceiptDocumentId, null));

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Links(req).UndoAsync(s.CompanyId,
                new ReceiptLinkUndoRequest(nameof(ReceiptLinkRoute.MoveSettlementWithReissue), PaymentId: pay.Id, AllocationId: res.AllocationId), Mover));
            Assert.Equal(ReceiptLinkPolicy.MoveNoUndoCode, ex.RuleCode);
            Assert.Contains("ทางไปต่อ", ex.Message, StringComparison.Ordinal);
        }
        Assert.Equal(DocumentStatus.Paid, (await DocAsync(right.Id)).Status);        // ไม่มีอะไรถูกย้ายกลับ
        Assert.Equal((Guid?)right.Id, (await PaymentAsync(pay.Id)).MovedToDocumentId);
    }
}
