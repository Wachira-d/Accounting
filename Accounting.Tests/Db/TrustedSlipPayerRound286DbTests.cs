using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 286 (คำสั่งเจ้าของ · คำตัดสิน 286-1…286-9) บน PostgreSQL จริง — ผู้จ่ายที่ไว้ใจได้:
/// สลิปที่รายการเดินบัญชียืนยัน ⇒ เรียนกุญแจ · สลิปถัดไปตรงกุญแจ ⇒ บันทึกรับเงินล่วงหน้า · รายการเดินบัญชีตามมา ⇒ ผูกกับการชำระเดิม (ไม่ซ้ำ) + ยืนยัน ·
/// ไม่ตามมาในกำหนด ⇒ ล้ม + ถอนความไว้ใจ (ไม่ยกเลิกเงินเอง) · ต่างผู้โอน/เลขบัญชี/ชื่อ ⇒ ไม่ใช้กุญแจ · เกินเพดาน ⇒ หน้ายืนยัน · ค่าตั้งปิด ⇒ 282 เดิม ·
/// ผู้ขายถอน ⇒ ไม่ใช้ · ผู้ขายกดยืนยันอย่างเดียวไม่เกิดความไว้ใจ (ต้องมีรายการเดินบัญชีตามมา) · ไม่มีบริการ AI ในเทสต์นี้ (kill-switch)
/// </summary>
[Trait("Category", "Db")]
public class TrustedSlipPayerRound286DbTests
{
    private readonly ITestOutputHelper _out;
    public TrustedSlipPayerRound286DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("trusted-payer-286-db");

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid BankId, LocalDiskFileStorage Storage);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool trustedOn = true, decimal dailyCap = 50_000m)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var company = new Company { Name = "สำนักงาน 286 " + tag, TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, SlipAutoRecordEnabled = true, BankAutoSettleEnabled = true, SlipTrustedAutoEnabled = trustedOn, SlipTrustedDailyCapPerKey = dailyCap,
        });
        ChartOfAccount Gl(string code, string name, AccountType type)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        Gl("11111", "เงินสด", AccountType.Asset);
        var glK = Gl("11121", "เงินฝากกสิกร", AccountType.Asset);
        Gl("11310", "ลูกหนี้การค้า", AccountType.Asset);
        Gl("11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset);
        Gl("21911", "ภาษีขาย", AccountType.Liability);
        Gl("21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability);
        await db.SaveChangesAsync();
        var bankK = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์ กสิกร", BankName = "กสิกรไทย", AccountNumber = "123-4-50621-9", LinkedAccountId = glK.Id };
        db.BankAccounts.Add(bankK);
        var buyer = new Contact { CompanyId = company.Id, Name = "บริษัท ลูกค้า 286 จำกัด", IsCustomer = true, ContactType = ContactType.Individual, Address = "1 ถนนสีลม" };
        db.Contacts.Add(buyer);
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "tsp286-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new Seed(company.Id, buyer.Id, bankK.Id, storage);
    }

    private static async Task<Document> InvoiceAsync(AccountingDbContext db, Seed s, decimal subTotal = 1_000m)
    {
        var vat = Math.Round(subTotal * 0.07m, 2, MidpointRounding.AwayFromZero);
        var inv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV286-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.AddHours(7).Date.AddDays(-3),
            SubTotal = subTotal, VatAmount = vat, TotalAmount = subTotal + vat, BalanceDue = subTotal + vat, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = subTotal, Amount = subTotal, VatRate = 7m, VatAmount = vat });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    private static byte[] Png() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(System.Text.Encoding.UTF8.GetBytes("slip-286-" + Guid.NewGuid())).ToArray();

    private static readonly DateTime SlipDay = DateTime.UtcNow.AddHours(7).Date.AddDays(-1);

    /// <summary>สลิป: ผู้รับ = กสิกร ••0621 (บัญชีของบริษัท) · ผู้โอน = SCB xxx-x-x1234-x ชื่อ "นาย สมชาย ใจดี" (ค่าตั้งต้น)</summary>
    private static OcrSlipText.Reading Slip(decimal amount, string qrRef, string payerName = "นาย สมชาย ใจดี", string payerMask = "xxx-x-x1234-x")
        => new(new[] { amount }, amount, SlipDay, qrRef, LooksLikeSlip: true,
            Payer: new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "014", false, payerMask, payerName, null),
            Payee: new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, "xxx-x-x0621-x", null, null),
            Roles: OcrSlipText.RoleSource.Label, Qr: new ThaiSlipQr.Payload("014", qrRef));

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "trusted-payer-286-db-test-secret-0123456789",
    }).Build();

    private static DocumentService DocSvc(AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private sealed record Svcs(LineChatSlipService Line, TrustedSlipVerifyService Verify, TrustedSlipPayerService Trusted, BankReceiptSettleService Bank,
        IPaymentIntentService Intents);

    private static Svcs Build(AccountingDbContext db, Seed s)
    {
        var handler = new Accounting.Services.Payments.Handlers.DocumentPaymentHandler(db, DocSvc(db), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null),
            NullLogger<Accounting.Services.Payments.Handlers.DocumentPaymentHandler>.Instance, s.Storage);
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() }, new IPaymentCompletionHandler[] { handler },
            NullLogger<PaymentIntentService>.Instance);
        var slips = new SlipEvidenceService(db, intents, s.Storage);
        var payLinks = new DocumentPayLinkService(db, Array.Empty<IPaymentProvider>(), Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, s.Storage);
        var verify = new TrustedSlipVerifyService(db, NullLogger<TrustedSlipVerifyService>.Instance);
        var trusted = new TrustedSlipPayerService(db, intents, NullLogger<TrustedSlipPayerService>.Instance, Config(), s.Storage);
        var bank = new BankReceiptSettleService(db, DocSvc(db), NullLogger<BankReceiptSettleService>.Instance, null,
            F4Proxy<IPermissionService>.Create((m, _) => m == nameof(IPermissionService.HasPermissionAsync) ? true : null), verify);
        var auto = new SlipAutoRecordService(db, intents, NullLogger<SlipAutoRecordService>.Instance, s.Storage, bankSettle: bank, trusted: trusted, trustVerify: verify);
        var reader = F4Proxy<ISlipImageReader>.Create((m, _) => m == "get_IsAvailable" ? true : null);
        var line = F4Proxy<ICustomerLineLinkService>.Create((_, _) => null);
        return new Svcs(new LineChatSlipService(db, Secrets, line, reader, payLinks, slips, s.Storage, NullLogger<LineChatSlipService>.Instance, auto: auto),
            verify, trusted, bank, intents);
    }

    private static readonly Guid Uploader = Guid.NewGuid();

    private static async Task<Guid> AddLineAsync(AccountingDbContext db, Seed s, decimal amount, string? reference)
    {
        var t = new BankTransaction
        {
            CompanyId = s.CompanyId, BankAccountId = s.BankId, TransactionDate = SlipDay, TransactionType = BankTransactionType.Deposit, Amount = amount,
            Description = "รับโอน", Reference = reference, ReconciliationStatus = ReconciliationStatus.Unmatched, EntrySource = BankReceiptAutoSettle.SourceStatement,
            CreatedByUserId = Uploader, CreatedBy = Uploader.ToString(),
            AutoSettleVerdict = BankReceiptAutoSettle.Stamps.Suggest, AutoSettleEvaluatedAt = DateTime.UtcNow,
        };
        db.BankTransactions.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    private static ForwardedSlip Fwd(Seed s, OcrSlipText.Reading r)
        => new(s.CompanyId, new[] { s.ContactId }, LineChatSlipRule.Sources.CentralGroup, "grp-" + s.CompanyId.ToString("N")[..12], Guid.NewGuid().ToString("N"), Png(), r,
            ShowAmounts: true, SenderVerified: true);

    /// <summary>ครั้งแรก: สลิป + รายการเดินบัญชีตรง (เส้น 282) ⇒ บันทึกรับเงิน + เรียนกุญแจทันที</summary>
    private static async Task<Document> FirstTrustedAsync(AccountingDbContext db, Seed s, Svcs v)
    {
        var inv = await InvoiceAsync(db, s);
        await AddLineAsync(db, s, 1_070m, "286FIRSTREF0001");
        var res = await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286FIRSTREF0001")));
        Assert.Contains("บันทึกรับเงิน", res.Reply);
        return inv;
    }

    [Fact]
    public async Task T286_01_ยืนยันด้วยรายการเดินบัญชี_เกิดกุญแจ_สลิปถัดไปตรงกุญแจ_บันทึกล่วงหน้า_รายการเดินบัญชีตามมา_ผูกการชำระเดิม_ยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var v = Build(db, s);
        await FirstTrustedAsync(db, s, v);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var row = await c.TrustedSlipPayers.AsNoTracking().SingleAsync(t => t.CompanyId == s.CompanyId);
            Assert.Equal(1_070m, row.MaxConfirmedAmount);
            Assert.Equal(1, row.ConfirmedCount);
            Assert.Null(row.RevokedAt);
            Assert.Equal(s.ContactId, row.ContactId);
            Assert.Equal(s.BankId, row.ReceivingBankAccountId);
            Assert.DoesNotContain("สมชาย", row.Label + row.KeyHash);   // PDPA: ไม่เก็บชื่อผู้โอน
        }

        // สลิปครั้งที่สอง: ผู้โอนเดิมทุกช่อง ต่างแค่เลขอ้างอิง (+ ยอดไม่เกิน 1,605) · ยังไม่มีรายการเดินบัญชี ⇒ บันทึกล่วงหน้า
        var inv2 = await InvoiceAsync(db, s, 1_200m);
        var res2 = await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_284m, "286SECONDREF002")));
        Assert.Contains("บันทึกรับเงิน", res2.Reply);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv2.Id);
            Assert.Equal(PaymentIntentStatus.Succeeded, intent.Status);
            Assert.NotNull(intent.TrustProvisionalAt);
            Assert.NotNull(intent.TrustReviewDueAt);
            Assert.Null(intent.TrustVerifiedAt);
            Assert.Equal(TrustedSlipPayerRule.Codes.Provisional, intent.AutoRecordCode);
            var pay = await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv2.Id);
            Assert.Equal(1_284m, pay.Amount);
            Assert.Equal(SlipDay, pay.PaymentDate.Date);
            Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == intent.Id.ToString() && a.NewValues!.Contains("SlipTrustedProvisional")));
        }

        // รายการเดินบัญชีตามมา ⇒ งานตัดชำระเดิม (EvaluateLineAsync) ยืนยันรายการล่วงหน้า — ผูกกับการชำระเดิม ไม่สร้างการชำระที่สอง
        var lineId = await AddLineAsync(db, s, 1_284m, "286SECONDREF002");
        await v.Bank.EvaluateLineAsync(s.CompanyId, lineId, true, DateTime.UtcNow);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv2.Id);
            Assert.NotNull(intent.TrustVerifiedAt);
            var pays = await c.Payments.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.DocumentId == inv2.Id && !p.IsDeleted).ToListAsync();
            Assert.Single(pays);
            var line = await c.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == lineId);
            Assert.Equal(pays[0].Id, line.MatchedPaymentId);
            Assert.Equal(TrustedSlipPayerRule.RuleCode, line.MatchRuleCode);
            var row = await c.TrustedSlipPayers.AsNoTracking().SingleAsync(t => t.CompanyId == s.CompanyId);
            Assert.Equal(2, row.ConfirmedCount);
            Assert.Equal(1_284m, row.MaxConfirmedAmount);
        }
    }

    [Fact]
    public async Task T286_02_รายการเดินบัญชีไม่ตามมาในกำหนด_ล้ม_ถอนความไว้ใจ_ไม่ยกเลิกเงินเอง_สลิปถัดไปเข้าหน้ายืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var v = Build(db, s);
        await FirstTrustedAsync(db, s, v);
        var inv2 = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286NOLINEREF003")));
        DateTime due;
        using (var c = DbTestDatabase.TryCreateContext()!)
            due = (await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv2.Id)).TrustReviewDueAt!.Value;

        // ก่อนกำหนด: ยังไม่ล้ม
        await v.Verify.SweepAsync(due.AddHours(-1));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv2.Id)).TrustFailedAt);

        await v.Verify.SweepAsync(due.AddHours(1));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv2.Id);
            Assert.NotNull(intent.TrustFailedAt);
            Assert.Equal(TrustedSlipPayerRule.Codes.Unverified, intent.AutoRecordCode);
            var row = await c.TrustedSlipPayers.AsNoTracking().SingleAsync(t => t.CompanyId == s.CompanyId);
            Assert.NotNull(row.RevokedAt);
            Assert.Equal("system", row.RevokedBy);
            Assert.True(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv2.Id && !p.IsDeleted));   // ผู้ขายเลือกยกเลิกเอง
        }

        // สลิปถัดไปของผู้โอนเดิม ⇒ ความไว้ใจถูกถอน ⇒ หน้ายืนยัน (ไม่มีการชำระ) + เหตุบอกว่าถูกถอน
        var inv3 = await InvoiceAsync(db, s);
        var res = await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286AFTERREVOKE4")));
        Assert.Equal(LineChatSlipRule.Replies.AwaitingSeller, res.Reply);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv3.Id));
            var pending = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv3.Id);
            Assert.Equal(SlipAutoRecordRule.Codes.NoBankLine, pending.AutoRecordCode);
            Assert.Contains("ถูกถอน", pending.AutoRecordReason);
        }
    }

    [Fact]
    public async Task T286_03_ต่างชื่อ_ต่างเลขบัญชี_ไม่ใช้กุญแจ_รอยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var v = Build(db, s);
        await FirstTrustedAsync(db, s, v);
        var a = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286OTHERNAME005", payerName: "นาง สมหญิง ใจดี")));
        var b = await InvoiceAsync(db, s, 1_100m);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_177m, "286OTHERMASK006", payerMask: "xxx-x-x9876-x")));
        using var c = DbTestDatabase.TryCreateContext()!;
        foreach (var d in new[] { a.Id, b.Id })
        {
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == d));
            var i = await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == d);
            Assert.True(PaymentIntentPolicy.IsOpen(i.Status));
            Assert.Null(i.TrustProvisionalAt);
            Assert.NotNull(i.TrustKeyHash);   // กุญแจของผู้โอนใหม่ถูกประทับไว้เรียนภายหลัง
        }
        Assert.Equal(1, await c.TrustedSlipPayers.CountAsync(t => t.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task T286_04_เกินเพดานต่อครั้ง_และเกินเพดานต่อวัน_หน้ายืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, dailyCap: 2_000m);
        var v = Build(db, s);
        await FirstTrustedAsync(db, s, v);
        // ต่อครั้ง: 1,070 × 1.5 = 1,605 ⇒ 2,140 เกิน
        var big = await InvoiceAsync(db, s, 2_000m);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(2_140m, "286OVERMAX00007")));
        // ต่อวัน 2,000: ใบแรก 1,070 ผ่าน · ใบที่สอง 1,070 (รวม 2,140) เกิน
        var d1 = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286DAILY0000008")));
        var d2 = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286DAILY0000009")));
        using var c = DbTestDatabase.TryCreateContext()!;
        var iBig = await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == big.Id);
        Assert.Null(iBig.TrustProvisionalAt);
        Assert.Contains("เพดานต่อครั้ง", iBig.AutoRecordReason);
        Assert.NotNull((await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == d1.Id)).TrustProvisionalAt);
        var i2 = await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == d2.Id);
        Assert.Null(i2.TrustProvisionalAt);
        Assert.Contains("เพดานต่อวัน", i2.AutoRecordReason);
        Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && (p.DocumentId == big.Id || p.DocumentId == d2.Id)));
    }

    [Fact]
    public async Task T286_05_ค่าตั้งปิด_พฤติกรรม282เดิม_และผู้ขายถอน_ไม่ใช้_ให้คืนแล้วใช้ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, trustedOn: false);
        var v = Build(db, s);
        await FirstTrustedAsync(db, s, v);   // ค่าตั้งปิดก็เรียนได้ (เปิดทีหลังพร้อมใช้) — แต่ไม่บันทึกล่วงหน้า
        var off = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286SETTINGOFF10")));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var i = await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == off.Id);
            Assert.Equal(SlipAutoRecordRule.Codes.NoBankLine, i.AutoRecordCode);
            Assert.Null(i.TrustProvisionalAt);
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == off.Id));
        }

        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SlipTrustedAutoEnabled, true));
        Guid rowId;
        using (var c = DbTestDatabase.TryCreateContext()!) rowId = (await c.TrustedSlipPayers.AsNoTracking().SingleAsync(t => t.CompanyId == s.CompanyId)).Id;
        Assert.True(await v.Trusted.SetTrustAsync(s.CompanyId, rowId, false, Guid.NewGuid(), "ทดสอบถอน"));
        Assert.False(await v.Trusted.SetTrustAsync(Guid.NewGuid(), rowId, true, Guid.NewGuid(), null));   // tenant isolation: บริษัทอื่นแตะไม่ได้
        var revoked = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286REVOKEDUSER11")));
        Assert.True(await v.Trusted.SetTrustAsync(s.CompanyId, rowId, true, Guid.NewGuid(), null));
        var back = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286REINSTATED012")));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await c2.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == revoked.Id));
        Assert.True(await c2.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == back.Id));
        Assert.True(await c2.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == rowId.ToString() && a.NewValues!.Contains("TrustedPayerRevoked")));
        var list = await v.Trusted.ListAsync(s.CompanyId);
        Assert.Single(list);
        Assert.Equal("บริษัท ลูกค้า 286 จำกัด", list[0].ContactName);
    }

    [Fact]
    public async Task T286_06_ผู้ขายกดยืนยันอย่างเดียวไม่เกิดความไว้ใจ_รายการเดินบัญชีจับคู่ภายหลังจึงเกิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var v = Build(db, s);
        var inv = await InvoiceAsync(db, s);
        await v.Line.HandleForwardedAsync(Fwd(s, Slip(1_070m, "286MANUALREF013")));   // ไม่มีรายการเดินบัญชี ⇒ รอยืนยัน
        PaymentIntent intent;
        using (var c = DbTestDatabase.TryCreateContext()!) intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv.Id);
        Assert.NotNull(intent.TrustKeyHash);
        var confirm = new SlipIntentConfirmService(db, v.Intents);
        Assert.True((await confirm.ConfirmAsync(s.CompanyId, intent, SlipDay, s.BankId, Guid.NewGuid(), "manual:test", "ผู้ขายยืนยัน", null)).Ok);
        Assert.Equal(0, await v.Verify.SweepAsync(DateTime.UtcNow));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.False(await c.TrustedSlipPayers.AnyAsync(t => t.CompanyId == s.CompanyId));   // การกดยืนยันอย่างเดียวไม่พอ

        // ผู้ขายจับคู่รายการเดินบัญชีกับการชำระที่หน้ากระทบยอด ⇒ รอบงานเรียนกุญแจ
        var lineId = await AddLineAsync(db, s, 1_070m, "286MANUALREF013");
        var payId = await db.Payments.Where(p => p.CompanyId == s.CompanyId && p.DocumentId == inv.Id).Select(p => p.Id).SingleAsync();
        await db.BankTransactions.Where(t => t.Id == lineId).ExecuteUpdateAsync(u => u.SetProperty(t => t.MatchedPaymentId, (Guid?)payId)
            .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Matched));
        Assert.True(await v.Verify.SweepAsync(DateTime.UtcNow) >= 1);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var row = await c3.TrustedSlipPayers.AsNoTracking().SingleAsync(t => t.CompanyId == s.CompanyId);
        Assert.Equal(1_070m, row.MaxConfirmedAmount);
    }
}
