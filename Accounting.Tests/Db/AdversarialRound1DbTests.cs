using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Integration;
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
/// ฝ่ายค้านรอบ 1 (2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 3) — ผ่านเมธอดจริงบน PostgreSQL ทั้งสองทิศ:
/// (1) แหล่งเงินที่<b>ยกมาจากใบต้นทาง</b> (แปลง) ไม่ถูกตรวจแบบ "เพิ่งเลือก" — ธนาคารที่ปิดใช้ทีหลัง/ยังไม่ผูกผังไม่ทำให้แปลงไม่ได้ ·
/// ค่าที่เลือกใหม่ยังตรวจเข้ม · ค่าที่ยกมาแต่เป็นของบริษัทอื่นยังบล็อก ·
/// (2) statement ที่ไม่มียอดคงเหลือ: ลบบรรทัดแล้วยอดการ์ดบัญชีไม่กลายเป็น 0 · ยอดจริงคำนวณจากบรรทัดล่าสุดที่รู้ยอด ·
/// (5) ตัวเขียน statement ล้มตอนบันทึก ⇒ ไม่ทิ้งแถว/ยอดการ์ดไว้ใน tracker ให้ SaveChanges ถัดไปบันทึกแทน ·
/// (4a) ขายเงินสดผ่านระบบเชื่อมต่อด้วยบัญชีรับเงินที่ลงบัญชีไม่ได้ ⇒ ปฏิเสธก่อนออกเลข (ไม่ถอยไปตั้งหนี้เงียบ ๆ)
/// </summary>
[Trait("Category", "Db")]
public class AdversarialRound1DbTests
{
    private readonly ITestOutputHelper _out;
    public AdversarialRound1DbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid OtherCompanyId, Guid SupplierId, Guid BankGlId, Guid LinkedBankId,
        Guid UnlinkedBankId, Guid OtherCompanyBankId, Guid GroupLeafGlId, Guid CashGlId, Guid ControlGlId);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัทฝ่ายค้านรอบหนึ่ง", TaxId = "0105556000025", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105556000017", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var cash = Acc(company.Id, "11111", "เงินสด", AccountType.Asset);
        var bankGl = Acc(company.Id, "11121", "เงินฝากกระแสรายวัน", AccountType.Asset);
        // ผังระดับ 3 ที่ไม่มีบัญชีย่อย (ผังของบริษัทเอง) — รอบ 206: ลงรายการได้ทุกทาง (เดิมตัวตัดสินแหล่งเงินถือ "ระดับ < 4" เป็นบัญชีกลุ่ม = เจ้าของที่สาม)
        var level3 = Acc(company.Id, "112", "เงินฝากธนาคาร (หมวด)", AccountType.Asset, level: 3);
        // บัญชีคุมจริง — ผังระดับ 4 ที่มีบัญชีย่อย (แบบ 11122 + ผังเงินฝากย่อยที่ระบบสร้าง)
        var control = Acc(company.Id, "11122", "เงินฝากออมทรัพย์", AccountType.Asset);
        var controlChild = Acc(company.Id, "11122-001", "เงินฝากออมทรัพย์ - กสิกร xxx-7890", AccountType.Asset, level: 5);
        controlChild.ParentAccountId = control.Id;
        var expense = Acc(company.Id, "52110", "ค่าทำบัญชี", AccountType.Expense);
        var ap = Acc(company.Id, "21210", "เจ้าหนี้การค้า", AccountType.Liability);
        var otherGl = Acc(other.Id, "11121", "เงินฝาก (บริษัทอื่น)", AccountType.Asset);
        db.ChartOfAccounts.AddRange(cash, bankGl, level3, control, controlChild, expense, ap, otherGl);
        await db.SaveChangesAsync();
        var linked = new BankAccount { CompanyId = company.Id, AccountName = "บัญชีหลัก", BankName = "กสิกรไทย", AccountNumber = "1234567890", LinkedAccountId = bankGl.Id };
        var unlinked = new BankAccount { CompanyId = company.Id, AccountName = "บัญชียังไม่ผูก", BankName = "กรุงไทย", AccountNumber = "9876543210" };
        var otherBank = new BankAccount { CompanyId = other.Id, AccountName = "บัญชีบริษัทอื่น", BankName = "ไทยพาณิชย์", AccountNumber = "5555555555", LinkedAccountId = otherGl.Id };
        db.BankAccounts.AddRange(linked, unlinked, otherBank);
        var supplier = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson,
            IsSupplier = true, IsCustomer = true,
        };
        db.Contacts.Add(supplier);
        await db.SaveChangesAsync();
        return new Seed(company.Id, other.Id, supplier.Id, bankGl.Id, linked.Id, unlinked.Id, otherBank.Id, level3.Id, cash.Id, control.Id);
    }

    private static DocumentService Docs(AccountingDbContext db)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!, null!, null!);

    /// <summary>ใบแจ้งหนี้ซื้ออนุมัติแล้ว 1,000 (ไม่มี VAT/WHT) — แหล่งเงินตั้งตรงลงฐาน (ใบที่ออกไปแล้ว แก้ไม่ได้)</summary>
    private static async Task<Document> ApprovedPiAsync(AccountingDbContext db, Seed s, Guid? bankAccountId)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.SupplierId, DocumentType = DocumentType.PurchaseInvoice,
            DocumentNumber = "PI-ADV1-" + Guid.NewGuid().ToString("N")[..6], Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m, Currency = "THB", WhtCertAutoIssue = false,
            BankAccountId = bankAccountId,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าสินค้า", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    // ══════════════════ (1) ค่าที่ยกมาจากใบต้นทาง ══════════════════

    [Fact]
    public async Task แปลงใบที่ธนาคารถูกปิดใช้ทีหลัง_หรือยังไม่ผูกผัง_แปลงได้_ใบลูกเป็นร่างที่ถือค่าเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var piInactive = await ApprovedPiAsync(db, s, s.LinkedBankId);
        var piUnlinked = await ApprovedPiAsync(db, s, s.UnlinkedBankId);
        // ปิดใช้บัญชีธนาคารหลังใบต้นทางอนุมัติไปแล้ว (ใบเดิมแก้ไม่ได้ — เดิมแปลงไม่ได้เลย ทางตัน)
        (await db.BankAccounts.SingleAsync(b => b.Id == s.LinkedBankId && b.CompanyId == s.CompanyId)).IsActive = false;
        await db.SaveChangesAsync();

        DocumentResponse pvFromInactive, pvFromUnlinked;
        using (var req = DbTestDatabase.TryCreateContext()!)
            pvFromInactive = await Docs(req).ConvertDocumentAsync(s.CompanyId, piInactive.Id, DocumentType.PaymentVoucher, "tester");
        using (var req = DbTestDatabase.TryCreateContext()!)
            pvFromUnlinked = await Docs(req).ConvertDocumentAsync(s.CompanyId, piUnlinked.Id, DocumentType.PaymentVoucher, "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var a = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == pvFromInactive.Id && d.CompanyId == s.CompanyId);
        var b = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == pvFromUnlinked.Id && d.CompanyId == s.CompanyId);
        Assert.Equal(s.LinkedBankId, a.BankAccountId);   // สืบทอดค่าเดิม (ไม่แปลงเป็นค่าตั้งต้น)
        Assert.Equal(s.UnlinkedBankId, b.BankAccountId);
        Assert.Equal(DocumentStatus.Draft, b.Status);    // ใบลูกแก้ได้ — ตอนลงบัญชีตัดสินเฉพาะใบที่ขยับเงิน (UseMoneyAccount)
    }

    [Fact]
    public async Task ทิศตรงข้าม_ค่าที่เลือกใหม่ยังตรวจเข้ม_ค่าที่ยกมาของบริษัทอื่นยังบล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        CreateDocumentRequest Doc(DocumentType type, Guid? bank) => new(type, DateTime.UtcNow.Date, null, s.SupplierId, null, null,
            new List<DocumentLineRequest> { new("ค่าบริการ", 1m, null, 500m, 0m, 0m, 0m, null) }, BankAccountId: bank);
        CreateDocumentRequest Pv(Guid? bank) => Doc(DocumentType.PaymentVoucher, bank);

        // เลือกธนาคารที่ยังไม่ผูกผังเอง (ไม่ได้ยกมา) ⇒ ปฏิเสธพร้อมทางไปต่อ (พฤติกรรมเดิมของ PC#4)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var fresh = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).CreateDocumentAsync(s.CompanyId, Pv(s.UnlinkedBankId), "tester"));
            Assert.Contains("ยังไม่ได้ผูกผังบัญชี", fresh.Message);
        }
        // ยกมาจากต้นทาง แต่เป็นบัญชีของบริษัทอื่น ⇒ ยังบล็อก (tenant isolation · คำตัดสินข้อ 150) ข้อความไม่มี GUID
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var foreign = await Assert.ThrowsAsync<BusinessRuleException>(() => Docs(req).CreateDocumentAsync(s.CompanyId, Pv(s.OtherCompanyBankId), "tester",
                carriedMoneyHeader: new CarriedMoneyHeader(s.OtherCompanyBankId, null, null)));
            Assert.Contains("ไม่ใช่ของบริษัทนี้", foreign.Message);
            Assert.DoesNotContain(s.OtherCompanyBankId.ToString(), foreign.Message);
        }
        // ยกมาจากต้นทาง + ธนาคารยังไม่ผูกผัง บนใบตั้งหนี้ (ไม่เคยลงขาเงิน) ⇒ สร้างได้ (เส้นเดียวกับแปลง/คัดลอก/รายการประจำ)
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await Docs(req).CreateDocumentAsync(s.CompanyId, Doc(DocumentType.PurchaseInvoice, s.UnlinkedBankId), "tester",
                carriedMoneyHeader: new CarriedMoneyHeader(s.UnlinkedBankId, null, null));
            Assert.NotEqual(Guid.Empty, ok.Id);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.CompanyId && d.DocumentType == DocumentType.PaymentVoucher));
        Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.CompanyId && d.DocumentType == DocumentType.PurchaseInvoice));
    }

    // ══════════════════ (2) statement ไม่มียอดคงเหลือ + ลบบรรทัด ══════════════════

    [Fact]
    public async Task Statementไม่มียอด_ลบบรรทัดแล้วยอดการ์ดไม่กลายเป็น0_มียอดจริงใช้บรรทัดล่าสุดที่รู้ยอด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var acc = s.LinkedBankId;
        (await db.BankAccounts.SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance = 50_000m;
        await db.SaveChangesAsync();
        var day = DateTime.UtcNow.Date.AddDays(-5);

        // ไฟล์ไม่มีคอลัมน์ยอดคงเหลือ
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new BankService(req).ImportParsedStatementRowsAsync(s.CompanyId, acc, new[]
            {
                new BankStatementLineInput(day, "โอนเข้า ก", 0m, 1_000m),
                new BankStatementLineInput(day.AddDays(1), "โอนเข้า ข", 0m, 2_000m),
            }, new BankStatementWriteOptions(AutoMatch: false));
        Guid unknownRowId;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await check.BankTransactions.AsNoTracking().Where(t => t.CompanyId == s.CompanyId && t.BankAccountId == acc).ToListAsync();
            Assert.All(rows, r => Assert.True(r.BalanceAfterUnknown));   // "ไม่รู้" — ไม่ใช่ยอด 0
            unknownRowId = rows.OrderByDescending(r => r.TransactionDate).First().Id;
            Assert.True((await new BankService(check).GetTransactionsAsync(s.CompanyId, acc, new Accounting.Models.DTOs.PagedRequest()))
                .Items.All(t => t.BalanceAfterUnknown));   // echo ถึงหน้าเว็บ ⇒ แสดง "—"
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new BankService(req).DeleteTransactionAsync(s.CompanyId, unknownRowId);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(50_000m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance);

        // ทิศตรงข้าม: บรรทัดที่มียอดจริง ⇒ ลบบรรทัดล่าสุดแล้วยอดการ์ด = ยอดของบรรทัดล่าสุดที่ยังเหลือ (ข้ามบรรทัดที่ไม่รู้ยอด)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new BankService(req).ImportParsedStatementRowsAsync(s.CompanyId, acc, new[]
            {
                new BankStatementLineInput(day.AddDays(2), "โอนเข้า ค", 0m, 3_000m, 53_000m),
                new BankStatementLineInput(day.AddDays(3), "โอนออก ง", 500m, 0m, 52_500m),
            }, new BankStatementWriteOptions(AutoMatch: false));
        Guid latestKnown;
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(52_500m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance);
            latestKnown = (await check.BankTransactions.AsNoTracking()
                .SingleAsync(t => t.CompanyId == s.CompanyId && t.BankAccountId == acc && t.Description == "โอนออก ง")).Id;
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await new BankService(req).DeleteTransactionAsync(s.CompanyId, latestKnown);
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(53_000m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance);
    }

    // ══════════════════ (5) ตัวเขียน statement ล้มตอนบันทึก ══════════════════

    [Fact]
    public async Task ตัวเขียนStatementล้มตอนบันทึก_ไม่ทิ้งแถวและยอดการ์ดไว้ให้SaveChangesถัดไป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var acc = s.LinkedBankId;
        (await db.BankAccounts.SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance = 10_000m;
        await db.SaveChangesAsync();

        // แถวพิษที่ผู้เรียกทิ้งไว้ใน scope เดียวกัน (FK ไปบัญชีธนาคารที่ไม่มี) ⇒ SaveChanges ของตัวเขียนล้ม
        var poison = new BankTransaction { CompanyId = s.CompanyId, BankAccountId = Guid.NewGuid(), TransactionDate = DateTime.UtcNow.Date,
            TransactionType = BankTransactionType.Deposit, Amount = 1m };
        db.BankTransactions.Add(poison);
        await Assert.ThrowsAnyAsync<Exception>(() => new BankService(db).ImportParsedStatementRowsAsync(s.CompanyId, acc, new[]
        {
            new BankStatementLineInput(DateTime.UtcNow.Date.AddDays(-1), "โอนเข้า ลูกค้า", 0m, 700m, 99_999m),
        }, new BankStatementWriteOptions(AutoMatch: false)));

        // ผู้เรียกถอดแถวของตัวเองแล้วบันทึกต่อ (เช่นกลุ่มถัดไปของการนำเข้า) — ต้องไม่มีแถว/ยอดการ์ดที่ "ล้มแล้ว" ติดไปด้วย
        db.Entry(poison).State = EntityState.Detached;
        Assert.Empty(db.ChangeTracker.Entries<BankTransaction>().Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached));
        await db.SaveChangesAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.BankTransactions.AnyAsync(t => t.CompanyId == s.CompanyId && t.BankAccountId == acc));
        Assert.Equal(10_000m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == acc && b.CompanyId == s.CompanyId)).CurrentBalance);
    }

    // ══════════════════ (4a) ขายเงินสดผ่านระบบเชื่อมต่อ ══════════════════

    [Fact]
    public async Task ขายเงินสดผ่านระบบเชื่อมต่อ_บัญชีรับเงินลงบัญชีไม่ได้_ปฏิเสธก่อนออกเลข_ไม่ถอยไปตั้งหนี้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var integration = new ExternalIntegration { CompanyId = s.CompanyId, SystemName = "TakeTime", SystemType = "PMS" };
        db.Set<ExternalIntegration>().Add(integration);
        await db.SaveChangesAsync();

        InboundInvoiceRequest Sale(Guid payAccount) => new InboundInvoiceRequest(
            ExternalId: "EXT-" + Guid.NewGuid().ToString("N")[..6], ExternalRef: "REF-" + Guid.NewGuid().ToString("N")[..6],
            CustomerExternalId: null, CustomerName: "ลูกค้าเงินสด", CustomerTaxId: null,
            DocumentDate: DateTime.UtcNow.Date, DueDate: null, DocumentType: "TaxInvoice", Description: null,
            Lines: new List<InboundInvoiceLineRequest> { new(null, "ค่าห้องพัก", 1m, 1_000m, null, null, null, null, null) },
            PaymentMethod: "Cash", VatRate: null, Currency: null, Notes: null) with { IsCashSale = true, PaymentAccountId = payAccount };

        InboundSyncResponse rejected;
        using (var req = DbTestDatabase.TryCreateContext()!)
            rejected = await new IntegrationService(req, null!, NullLogger<IntegrationService>.Instance, null!, null!, null!)
                .ProcessInvoiceAsync(s.CompanyId, integration.Id, Sale(s.ControlGlId));
        Assert.False(rejected.Success);
        Assert.Contains("paymentAccountId", rejected.Message);
        Assert.Contains("บัญชีคุม", rejected.Message);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.CompanyId));   // ไม่ออกเลข · ไม่ตั้งหนี้
            var log = await check.Set<IntegrationSyncLog>().AsNoTracking().SingleAsync(l => l.CompanyId == s.CompanyId && l.IntegrationId == integration.Id);
            Assert.Equal("Failed", log.Status);
            Assert.Contains("บัญชีคุม", log.ErrorMessage);
        }

        // ทิศตรงข้าม: ผังเงินสดระดับรายการ + ผังระดับ 3 ที่ไม่มีบัญชีย่อย (รอบ 206) ผ่านด่านนี้
        // (เทสต์นี้ไม่ได้ต่อบริการที่เหลือ ⇒ ไปล้มที่อื่น — ต้องไม่ใช่ข้อความของด่าน)
        foreach (var okAccount in new[] { s.CashGlId, s.GroupLeafGlId })
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            InboundSyncResponse passed;
            try
            {
                passed = await new IntegrationService(req, null!, NullLogger<IntegrationService>.Instance, null!, null!, null!)
                    .ProcessInvoiceAsync(s.CompanyId, integration.Id, Sale(okAccount));
            }
            catch (Exception ex) { passed = new InboundSyncResponse(false, ex.Message, null, null, null, null, null); }
            Assert.DoesNotContain("บัญชีคุม", passed.Message ?? "");
            Assert.DoesNotContain("บัญชีกลุ่ม", passed.Message ?? "");
        }
    }
}
