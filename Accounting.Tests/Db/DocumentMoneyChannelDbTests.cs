using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
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
/// 2026-10-14 ทีม F2 (PAYMENT_CHANNEL_AUDIT PC#3/#4/#5/#9/#14/#21 · ID#4/#7) — แหล่งเงินของเอกสาร/การชำระผ่านเมธอดจริงบน PostgreSQL:
/// ตรวจหัวเอกสารตอนบันทึกทั้งสองทิศ · การชำระลงผังที่ธนาคารผูกไว้ + ยอดธนาคารขยับ · ธนาคารยังไม่ผูกผัง ⇒ ข้อความไทย ไม่ตกเงินสด
/// และไม่มีอะไรถูกบันทึก · ไม่มีผังเงินสดเลย ⇒ ล้มดัง (เดิมบันทึกการชำระโดยไม่มี JE) · การชำระสืบทอดผังตรงของเอกสาร · เลือกจ่ายจาก
/// ผังอื่น ⇒ ยอดธนาคารไม่ขยับ · ค่าตั้งต้นของเทมเพลตแปลงเป็นค่าช่องฟอร์มได้ · สร้างบัญชีธนาคารตรวจผังที่ผูก
/// </summary>
[Trait("Category", "Db")]
public class DocumentMoneyChannelDbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentMoneyChannelDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid SupplierId, Guid CashId, Guid BankGlId, Guid DirectorLoanId,
        Guid LinkedBankId, Guid UnlinkedBankId, Guid OtherCompanyBankId, Guid OtherCompanyGlId);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type, int level = 4)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true };

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, bool withCash = true)
    {
        var company = new Company { Name = "บริษัททดสอบแหล่งเงิน", TaxId = "0105556000025", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น", TaxId = "0105556000017", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();

        var cash = Acc(company.Id, "11111", "เงินสด", AccountType.Asset);
        // ผังกำหนดเองที่ไม่มีกลุ่ม 111 เลย (withCash=false) — เงินฝากอยู่รหัสอื่น ⇒ ไม่มีผังเงินสดตั้งต้นให้หา
        var savingsCode = withCash ? "11122" : "1020";
        var savings = Acc(company.Id, savingsCode, "เงินฝากออมทรัพย์", AccountType.Asset);
        var bankGl = Acc(company.Id, savingsCode + "-001", "เงินฝากออมทรัพย์ - กสิกร xxx-1234", AccountType.Asset, level: 5);
        var director = Acc(company.Id, "21230", "เงินกู้ยืมกรรมการ", AccountType.Liability);
        var ap = Acc(company.Id, "21210", "เจ้าหนี้การค้า", AccountType.Liability);
        db.ChartOfAccounts.AddRange(savings, bankGl, director, ap);
        if (withCash) db.ChartOfAccounts.Add(cash);
        foreach (var code in new[] { "21915", "21916", "21917", "21918" })
            db.ChartOfAccounts.Add(Acc(company.Id, code, "ภาษีหัก ณ ที่จ่ายค้างจ่าย " + code, AccountType.Liability));
        var otherGl = Acc(other.Id, "11122-001", "เงินฝากของบริษัทอื่น", AccountType.Asset, level: 5);
        db.ChartOfAccounts.Add(otherGl);
        await db.SaveChangesAsync();

        var linked = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์หลัก", BankName = "กสิกร", AccountNumber = "1234567890", LinkedAccountId = bankGl.Id, CurrentBalance = 50_000m };
        var unlinked = new BankAccount { CompanyId = company.Id, AccountName = "บัญชียังไม่ผูก", BankName = "กรุงไทย", AccountNumber = "9876543210", CurrentBalance = 10_000m };
        var otherBank = new BankAccount { CompanyId = other.Id, AccountName = "บัญชีบริษัทอื่น", BankName = "ไทยพาณิชย์", AccountNumber = "5555555555", LinkedAccountId = otherGl.Id };
        db.BankAccounts.AddRange(linked, unlinked, otherBank);
        var supplier = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ขาย จำกัด", TaxId = "0107537000017",
            ContactType = ContactType.JuristicPerson, IsSupplier = true,
        };
        db.Contacts.Add(supplier);
        await db.SaveChangesAsync();
        return new Seed(company.Id, supplier.Id, cash.Id, bankGl.Id, director.Id, linked.Id, unlinked.Id, otherBank.Id, otherGl.Id);
    }

    /// <summary>ใบแจ้งหนี้ซื้ออนุมัติแล้ว 1,000 ไม่มี VAT/WHT — แหล่งเงินตั้งตรงลงฐาน (จำลองข้อมูลที่บันทึกก่อนรอบนี้ได้)</summary>
    private static async Task<Document> PiAsync(Accounting.Data.AccountingDbContext db, Seed s, string no,
        Guid? bankAccountId = null, Guid? paymentAccountId = null, DocumentStatus status = DocumentStatus.Approved,
        DocumentType type = DocumentType.PurchaseInvoice)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.SupplierId, DocumentType = type, DocumentNumber = no,
            Status = status, DocumentDate = DateTime.UtcNow.Date,
            SubTotal = 1_000m, TotalAmount = 1_000m, BalanceDue = 1_000m, Currency = "THB", WhtCertAutoIssue = false,
            BankAccountId = bankAccountId, PaymentAccountId = paymentAccountId,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าสินค้า", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static DocumentService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static CreatePaymentRequest Pay(Guid docId, Guid? overrideBank = null, Guid? overrideGl = null)
        => new(docId, DateTime.UtcNow.Date, 1_000m, PaymentMethod.BankTransfer, null, null, null,
            OverrideBankAccountId: overrideBank, OverridePaymentAccountId: overrideGl);

    private static async Task<List<JournalEntryLine>> PaymentJeLinesAsync(Accounting.Data.AccountingDbContext db, Seed s, Guid docId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == s.CompanyId && j.SourceDocumentId == docId && j.JournalType == JournalType.CashPayments
                  select l).ToListAsync();

    // ── ตรวจหัวเอกสารตอนบันทึก (PC#4 · ID#7) ──

    [Fact]
    public async Task แก้แหล่งเงินเป็นค่าผิด_ปฏิเสธพร้อมบอกช่อง_ค่าถูกบันทึกได้_ใบเดิมแก้ช่องอื่นได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        // ใบร่างที่บันทึกไว้ก่อนรอบนี้ด้วยธนาคารที่ยังไม่ผูกผัง (ข้อมูลเก่า)
        var doc = await PiAsync(db, s, "PV-HDR-" + Guid.NewGuid().ToString("N")[..6], bankAccountId: s.UnlinkedBankId,
            status: DocumentStatus.Draft, type: DocumentType.PaymentVoucher);
        var none = new UpdateDocumentRequest(null, null, null, null, null, null);

        async Task<BusinessRuleException> Rejects(UpdateDocumentRequest r)
        {
            using var req = DbTestDatabase.TryCreateContext()!;
            return await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).UpdateDocumentAsync(s.CompanyId, doc.Id, r));
        }
        // บัญชีธนาคารของบริษัทอื่น (FK ผ่าน แต่ห้ามใช้ — เดิมลงเงินสดเงียบ ๆ + รั่วชื่อบัญชีบริษัทอื่น)
        Assert.Contains("ไม่มีในบริษัทนี้", (await Rejects(none with { BankAccountId = s.OtherCompanyBankId })).Message);
        // รหัสบัญชีธนาคารในช่องผังบัญชี (คลาสเดียวกับบั๊กรายการประจำ)
        Assert.Contains("ได้รหัสบัญชีธนาคารมาแทนรหัสผังบัญชี", (await Rejects(none with { PaymentAccountId = s.LinkedBankId })).Message);
        // รหัสผังบัญชีในช่องบัญชีธนาคาร
        Assert.Contains("ได้รหัสผังบัญชีมาแทนรหัสบัญชีธนาคาร", (await Rejects(none with { BankAccountId = s.BankGlId })).Message);
        // ผังของบริษัทอื่นในช่องหมวดค่าใช้จ่าย
        Assert.Contains("หมวดค่าใช้จ่าย", (await Rejects(none with { ExpenseCategoryId = s.OtherCompanyGlId })).Message);

        // ทิศตรงข้าม 1: แก้ช่องอื่น (ไม่ส่ง/ส่งค่าเดิม) — ธนาคารเดิมที่ยังไม่ผูกผังไม่บล็อกการแก้ไข (ตอนลงบัญชีตรวจอีกชั้น)
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).UpdateDocumentAsync(s.CompanyId, doc.Id, none with { Notes = "แก้หมายเหตุ", BankAccountId = s.UnlinkedBankId });
        // ทิศตรงข้าม 2: เลือกธนาคารที่ผูกผังแล้ว ⇒ บันทึกได้
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).UpdateDocumentAsync(s.CompanyId, doc.Id, none with { BankAccountId = s.LinkedBankId });
        using var check = DbTestDatabase.TryCreateContext()!;
        var stored = await check.Documents.AsNoTracking().FirstAsync(d => d.Id == doc.Id);
        Assert.Equal(s.LinkedBankId, stored.BankAccountId);
        Assert.Equal("แก้หมายเหตุ", stored.Notes);
        // ค่าช่องฟอร์มมาจากเซิร์ฟเวอร์ตามลำดับเดียวกับการลงบัญชี (PC#15)
        var resp = await Svc(check).GetDocumentAsync(s.CompanyId, doc.Id);
        Assert.Equal("bank:" + s.LinkedBankId, resp.PaymentChannel);
    }

    // ── การชำระ (PC#3/#4/#5/#21) ──

    [Fact]
    public async Task ชำระใบที่ผูกธนาคาร_ลงผังที่ธนาคารผูก_และยอดธนาคารขยับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pi = await PiAsync(db, s, "PI-BANK-" + Guid.NewGuid().ToString("N")[..6], bankAccountId: s.LinkedBankId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id), "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var lines = await PaymentJeLinesAsync(check, s, pi.Id);
        Assert.Equal(1_000m, lines.Where(l => l.AccountId == s.BankGlId).Sum(l => l.CreditAmount));
        Assert.DoesNotContain(lines, l => l.AccountId == s.CashId);
        Assert.Equal(49_000m, (await check.BankAccounts.AsNoTracking().FirstAsync(b => b.Id == s.LinkedBankId)).CurrentBalance);
    }

    [Fact]
    public async Task ธนาคารยังไม่ผูกผัง_ปฏิเสธเป็นภาษาไทย_ไม่ตกเงินสด_ไม่มีอะไรถูกบันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pi = await PiAsync(db, s, "PI-UNL-" + Guid.NewGuid().ToString("N")[..6], bankAccountId: s.UnlinkedBankId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id), "tester"));
            Assert.Contains("ยังไม่ได้ผูกผังบัญชี", ex.Message);
            Assert.Contains("หน้า “บัญชีธนาคาร”", ex.Message);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == pi.Id));
        Assert.Empty(await PaymentJeLinesAsync(check, s, pi.Id));
        Assert.Equal(0m, (await check.Documents.AsNoTracking().FirstAsync(d => d.Id == pi.Id)).PaidAmount);
        Assert.Equal(10_000m, (await check.BankAccounts.AsNoTracking().FirstAsync(b => b.Id == s.UnlinkedBankId)).CurrentBalance);

        // ทางไปต่อที่ข้อความบอก: เลือกธนาคารที่ผูกผังแล้วในหน้าบันทึกชำระ ⇒ ผ่าน
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id, overrideBank: s.LinkedBankId), "tester");
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1_000m, (await PaymentJeLinesAsync(after, s, pi.Id)).Where(l => l.AccountId == s.BankGlId).Sum(l => l.CreditAmount));
    }

    [Fact]
    public async Task ไม่มีผังเงินสดเลย_ล้มดัง_เดิมบันทึกการชำระโดยไม่มีJE()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db, withCash: false);
        var pi = await PiAsync(db, s, "PI-NOCASH-" + Guid.NewGuid().ToString("N")[..6]);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id), "tester"));
            Assert.Contains("11111", ex.Message);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == pi.Id));
    }

    [Fact]
    public async Task ใบที่ตั้งแหล่งเงินเป็นผังตรง_การชำระสืบทอดผังนั้น_ไม่ใช่เงินสด_PC5()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pi = await PiAsync(db, s, "PI-GL-" + Guid.NewGuid().ToString("N")[..6], paymentAccountId: s.DirectorLoanId);
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id), "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        var lines = await PaymentJeLinesAsync(check, s, pi.Id);
        Assert.Equal(1_000m, lines.Where(l => l.AccountId == s.DirectorLoanId).Sum(l => l.CreditAmount));
        Assert.DoesNotContain(lines, l => l.AccountId == s.CashId);
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == pi.Id);
        Assert.Equal(s.DirectorLoanId, pay.OverridePaymentAccountId);
        Assert.Null(pay.BankAccountId);
    }

    [Fact]
    public async Task เลือกจ่ายจากผังอื่น_ยอดธนาคารของเอกสารไม่ขยับ_PC21_และเลือกสองทางถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pi = await PiAsync(db, s, "PI-FUND-" + Guid.NewGuid().ToString("N")[..6], bankAccountId: s.LinkedBankId);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id, overrideBank: s.LinkedBankId, overrideGl: s.DirectorLoanId), "tester"));
            Assert.Contains("เลือกได้ทางเดียว", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).CreatePaymentAsync(s.CompanyId, Pay(pi.Id, overrideGl: s.DirectorLoanId), "tester");
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1_000m, (await PaymentJeLinesAsync(check, s, pi.Id)).Where(l => l.AccountId == s.DirectorLoanId).Sum(l => l.CreditAmount));
        Assert.Equal(50_000m, (await check.BankAccounts.AsNoTracking().FirstAsync(b => b.Id == s.LinkedBankId)).CurrentBalance);
    }

    // ── ค่าตั้งต้นของเทมเพลต (PC#14 · ID#4) ──

    [Fact]
    public async Task ค่าตั้งต้นเทมเพลต_ผังธนาคาร_เป็น_bank_ผังเงินสด_เป็น_account_ผังบริษัทอื่นไม่เติม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var tpl = new DocumentTemplate
        {
            CompanyId = s.CompanyId, Name = "ใบสำคัญจ่าย", DocumentType = DocumentType.PaymentVoucher,
            IsDefault = true, IsActive = true, DefaultPaymentAccountId = s.BankGlId,
        };
        db.DocumentTemplates.Add(tpl);
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("bank:" + s.LinkedBankId,
                (await new DocumentTemplateService(req).GetDefaultTemplateAsync(s.CompanyId, DocumentType.PaymentVoucher)).DefaultPaymentChannel);

        tpl.DefaultPaymentAccountId = s.CashId;
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
            Assert.Equal("account:" + s.CashId,
                (await new DocumentTemplateService(req).GetDefaultTemplateAsync(s.CompanyId, DocumentType.PaymentVoucher)).DefaultPaymentChannel);

        // ผังของบริษัทอื่น (ข้อมูลเสีย) ⇒ ไม่เติม (หน้าเว็บบอกผู้ใช้ให้เลือกเอง)
        Assert.Null(await DocumentMoneyChannel.ChannelValueForGlAsync(db, s.CompanyId, s.OtherCompanyGlId));
    }

    // ── สร้างบัญชีธนาคาร (PC#9 · ID#16) ──

    [Fact]
    public async Task สร้างบัญชีธนาคาร_ผูกผังบริษัทอื่นหรือผังที่ไม่ใช่เงินฝาก_ถูกปฏิเสธ_ผังเงินฝากของตัวเองผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        CreateBankAccountRequest Req(Guid linked) => new("บัญชีใหม่", "กสิกร", "1112223334", null, "Savings", "THB", 0m, linked);

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Assert.ThrowsAsync<BusinessRuleException>(() => new BankService(req).CreateBankAccountAsync(s.CompanyId, Req(s.OtherCompanyGlId)));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new BankService(req).CreateBankAccountAsync(s.CompanyId, Req(s.DirectorLoanId)));
            Assert.Contains("111x", ex.Message);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await new BankService(req).CreateBankAccountAsync(s.CompanyId, Req(s.BankGlId));
            Assert.Equal(s.BankGlId, ok.LinkedAccountId);
        }
        // รายการสำหรับเลือก (activeOnly) ไม่รวมบัญชีที่ปิดใช้ · หน้าจัดการยังเห็นทุกบัญชี (PC#18)
        var unl = await db.BankAccounts.FirstAsync(b => b.Id == s.UnlinkedBankId);
        unl.IsActive = false;
        await db.SaveChangesAsync();
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.DoesNotContain(await new BankService(check).GetBankAccountsAsync(s.CompanyId, activeOnly: true), b => b.Id == s.UnlinkedBankId);
        Assert.Contains(await new BankService(check).GetBankAccountsAsync(s.CompanyId), b => b.Id == s.UnlinkedBankId);
    }
}
