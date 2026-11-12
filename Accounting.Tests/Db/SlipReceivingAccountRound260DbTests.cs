using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 260-2 — เส้นจริงบน PostgreSQL: ยืนยันสลิปโอนพร้อมบัญชีที่เงินเข้า ⇒ JE เดบิตผังของบัญชีนั้น · การชำระผูกบัญชีนั้น (ผู้สมัครกระทบยอดธนาคาร:
/// <c>Payment.BankAccountId</c> + บรรทัด JE บนผังที่บัญชีผูก — ตัวกรองของ <c>BuildSummary</c>/<c>FindBestJournalMatch</c>) · ยอดคงเหลือธนาคารขยับ ·
/// ทิศตรงข้าม: ไม่เลือก + เอกสารตั้งธนาคารไว้ ⇒ เท่าเดิม (ธนาคารของเอกสาร · ไม่มี override) · ไม่เลือก + หลายบัญชีไม่มีหลักฐาน ⇒ เท่าเดิม (เงินสด) ·
/// บัญชีบริษัทอื่น/ปิดใช้ ⇒ ปฏิเสธ
/// </summary>
[Trait("Category", "Db")]
public class SlipReceivingAccountRound260DbTests
{
    private readonly ITestOutputHelper _out;
    public SlipReceivingAccountRound260DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed record Seed(Guid CompanyId, Document Invoice, BankAccount BankA, BankAccount BankB, Guid CashGlId);

    private static DocumentService DocSvc(Accounting.Data.AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static Accounting.Services.Payments.Handlers.DocumentPaymentHandler Handler(Accounting.Data.AccountingDbContext db)
        => new(db, DocSvc(db), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null),
            NullLogger<Accounting.Services.Payments.Handlers.DocumentPaymentHandler>.Instance);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, bool twoBanks = true, bool docBankA = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var company = new Company { Name = "บริษัท 260-2 " + tag, TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        ChartOfAccount Gl(string code, string name, AccountType type)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = Gl("11111", "เงินสด", AccountType.Asset);
        var glA = Gl("11121", "เงินฝากกสิกร", AccountType.Asset);
        var glB = Gl("11122", "เงินฝากไทยพาณิชย์", AccountType.Asset);
        Gl("11310", "ลูกหนี้การค้า", AccountType.Asset);
        Gl("11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset);
        Gl("21911", "ภาษีขาย", AccountType.Liability);
        Gl("21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability);
        await db.SaveChangesAsync();
        var bankA = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์ A", BankName = "กสิกร", AccountNumber = "1111111111", LinkedAccountId = glA.Id };
        var bankB = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์ B", BankName = "ไทยพาณิชย์", AccountNumber = "2222222222", LinkedAccountId = glB.Id,
            IsActive = twoBanks };
        db.BankAccounts.AddRange(bankA, bankB);
        var buyer = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ผู้ซื้อ 260 จำกัด", TaxId = "0107537000017", ContactType = ContactType.JuristicPerson,
            IsCustomer = true, Address = "1 ถนนสีลม กรุงเทพฯ",
        };
        db.Contacts.Add(buyer);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = buyer.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INV260-" + tag,
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-2),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, WithholdingTaxAmount = 0m,
            PaidAmount = 0m, BalanceDue = 1_070m, Currency = "THB",
            BankAccountId = docBankA ? bankA.Id : null,
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return new Seed(company.Id, inv, bankA, bankB, cash.Id);
    }

    private static PaymentIntent SlipIntent(Seed s, Guid? stampedBank, string? origin, PaymentIntentStatus status = PaymentIntentStatus.Succeeded) => new()
    {
        CompanyId = s.CompanyId, ProviderCode = Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code,
        SourceKind = PaymentSourceKind.Document, SourceId = s.Invoice.Id, Amount = 1_070m, Currency = "THB",
        MethodKind = PaymentMethodKind.ManualSlip, Status = status, IsTestMode = false, ConfirmedAt = DateTime.UtcNow,
        IdempotencyKey = "idem-260-" + Guid.NewGuid().ToString("N"),
        ReceivingBankAccountId = stampedBank, ReceivingAccountOrigin = origin,
    };

    private static async Task<List<JournalEntryLine>> LinesAsync(Accounting.Data.AccountingDbContext db, Guid cid, Guid docId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == cid && j.SourceDocumentId == docId && !j.IsDeleted
                  select l).ToListAsync();

    [Fact]
    public async Task ยืนยันพร้อมบัญชีที่เลือก_JE_เดบิตผังของบัญชีนั้น_การชำระผูกบัญชี_ยอดธนาคารขยับ_เป็นผู้สมัครกระทบยอด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // ทางเดียวกับ ConfirmManually: ตัดสิน (เลือก B) แล้วประทับบนรายการ
        var intent = SlipIntent(s, null, null, PaymentIntentStatus.Pending);
        var r = await SlipReceivingAccount.ResolveAsync(db, null, intent, s.BankB.Id);
        Assert.True(r.Decision.Ok, r.Decision.Problem);
        Assert.Equal(s.BankB.Id, r.Decision.BankAccountId);
        intent.ReceivingBankAccountId = r.Decision.BankAccountId;
        intent.ReceivingAccountOrigin = r.Decision.Origin.ToString();
        intent.Status = PaymentIntentStatus.Succeeded;

        await Handler(db).HandleSucceededAsync(intent);

        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == s.Invoice.Id);
        Assert.Equal(s.BankB.Id, pay.BankAccountId);
        var glB = (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankB.Id)).LinkedAccountId!.Value;
        var lines = await LinesAsync(check, s.CompanyId, s.Invoice.Id);
        Assert.Equal(1_070m, lines.Where(l => l.AccountId == glB).Sum(l => l.DebitAmount));
        Assert.Equal(0m, lines.Where(l => l.AccountId == s.CashGlId).Sum(l => l.DebitAmount));
        Assert.Equal(lines.Sum(l => l.DebitAmount), lines.Sum(l => l.CreditAmount));
        Assert.Equal(1_070m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankB.Id)).CurrentBalance);
        Assert.Equal(0m, (await check.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == s.BankA.Id)).CurrentBalance);
        // ผู้สมัครกระทบยอดของบัญชี B (ตัวกรองเดียวกับสรุปบัญชี/จับคู่ JE): การชำระของบัญชี B + JE ที่มีบรรทัดบนผังของ B
        Assert.True(await check.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.BankAccountId == s.BankB.Id && p.Id == pay.Id));
        Assert.True(lines.Any(l => l.AccountId == glB));
    }

    [Fact]
    public async Task ไม่เลือก_เอกสารตั้งธนาคารไว้_เหมือนเดิม_ธนาคารของเอกสาร_ไม่มี_override()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, docBankA: true);
        var pending = SlipIntent(s, null, null, PaymentIntentStatus.Pending);
        var r = await SlipReceivingAccount.ResolveAsync(db, null, pending, null);
        Assert.Equal(SlipReceivingAccount.Origin.DocumentChannel, r.Decision.Origin);
        Assert.Null(r.Decision.BankAccountId);
        Assert.Equal(s.BankA.Id, r.Decision.Suggested.DisplayBankAccountId);

        await Handler(db).HandleSucceededAsync(SlipIntent(s, null, r.Decision.Origin.ToString()));
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == s.Invoice.Id);
        Assert.Equal(s.BankA.Id, pay.BankAccountId);
        Assert.Null(pay.OverrideBankAccountId);   // เท่าเดิมทุกประการ — สืบทอดแหล่งเงินของเอกสาร
        var glA = s.BankA.LinkedAccountId!.Value;
        Assert.Equal(1_070m, (await LinesAsync(check, s.CompanyId, s.Invoice.Id)).Where(l => l.AccountId == glA).Sum(l => l.DebitAmount));
    }

    [Fact]
    public async Task ไม่เลือก_หลายบัญชีไม่มีหลักฐาน_ไม่ประทับ_เหมือนเดิม_ลงเงินสด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // รายการก่อนรอบนี้ (ไม่ประทับ) ⇒ handler คำนวณค่าแนะนำจากตัวเดียวกัน ⇒ ไม่เดา
        await Handler(db).HandleSucceededAsync(SlipIntent(s, null, null));
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == s.Invoice.Id);
        Assert.Null(pay.BankAccountId);
        Assert.Equal(1_070m, (await LinesAsync(check, s.CompanyId, s.Invoice.Id)).Where(l => l.AccountId == s.CashGlId).Sum(l => l.DebitAmount));
    }

    [Fact]
    public async Task ไม่เลือก_บัญชีรับโอนบัญชีเดียว_ค่าแนะนำลงบัญชีนั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, twoBanks: false);   // B ปิดใช้ ⇒ เหลือ A บัญชีเดียว
        var r = await SlipReceivingAccount.ResolveAsync(db, null, SlipIntent(s, null, null, PaymentIntentStatus.Pending), null);
        Assert.Equal(SlipReceivingAccount.Origin.SoleTransferAccount, r.Decision.Origin);
        Assert.Equal(s.BankA.Id, r.Decision.BankAccountId);
        await Handler(db).HandleSucceededAsync(SlipIntent(s, null, null));
        using var check = DbTestDatabase.TryCreateContext()!;
        var pay = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == s.Invoice.Id);
        Assert.Equal(s.BankA.Id, pay.BankAccountId);
    }

    [Fact]
    public async Task เลือกบัญชีบริษัทอื่นหรือปิดใช้_ปฏิเสธพร้อมเหตุ_ไม่บันทึกอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, twoBanks: false);
        var other = await SeedAsync(db);
        var pending = SlipIntent(s, null, null, PaymentIntentStatus.Pending);
        var cross = await SlipReceivingAccount.ResolveAsync(db, null, pending, other.BankA.Id);
        Assert.False(cross.Decision.Ok);
        Assert.Contains("ไม่พบบัญชีธนาคาร", cross.Decision.Problem);
        var inactive = await SlipReceivingAccount.ResolveAsync(db, null, pending, s.BankB.Id);
        Assert.False(inactive.Decision.Ok);
        Assert.Contains("ปิดใช้งาน", inactive.Decision.Problem);
        // บัญชีของบริษัทอื่นไม่โผล่ในตัวเลือก (tenant)
        Assert.DoesNotContain(cross.Accounts, a => a.Id == other.BankA.Id);
        Assert.Equal(0, await db.Payments.CountAsync(p => p.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task เงินผ่านผู้ให้บริการ_ไม่ใช้ตัวนี้_เลือกธนาคารถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, twoBanks: false);
        var gw = SlipIntent(s, null, null, PaymentIntentStatus.Pending);
        gw.ProviderCode = "omise";
        gw.MethodKind = PaymentMethodKind.PromptPay;
        var r = await SlipReceivingAccount.ResolveAsync(db, null, gw, s.BankA.Id);
        Assert.False(r.Applies);
        Assert.False(r.Decision.Ok);
        Assert.Null(await SlipReceivingAccount.OverrideForHandlerAsync(db, null, gw));
    }
}
