using Accounting.Helpers;
using Accounting.Models.Constants;
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
/// รอบ 252 WP-12A — เส้นจริงบน PostgreSQL ของ Autopilot ส่วน A:
/// เงินโอนเข้า ⇒ <c>BankReceiptSettleService</c> (เปิด = บันทึกรับชำระทางเดียว <c>CreatePaymentAsync</c> source BankAutoSettle + ผูกบรรทัด · ปิด = เสนอเท่านั้น ·
/// พิมพ์มือ/ยอดซ้ำ = ไม่ปิดเอง · ล้ม = ประทับเหตุ + แจ้งทันที · ล้มกลางทางกู้ด้วยเลขอ้างอิง · คนยืนยันได้เฉพาะใบในชุดตัวเลือก) ·
/// ใบเสนอราคาที่ลูกค้ายอมรับ ⇒ <c>QuotationAcceptAutopilotService</c> (แปลงครั้งเดียวผ่าน <c>ConvertDocumentAsync</c> · ค่าตั้งต้นไม่แปลง · watermark บริษัท ·
/// เนื้อหาเปลี่ยน = หยุด + แจ้ง · ล้ม = ประทับ + แจ้ง + ลองใหม่ได้) · สรุปประจำวัน (ส่งวันละครั้ง · ไม่มีอะไร = ไม่ส่ง)
/// <para>seed pitfalls: บริษัทใหม่ทุกเทสต์ (ฐานร่วม) · เลขเอกสารสุ่ม · <c>IDocumentService</c> ปลอมเขียนแถว Payment/Document จริงผ่าน context อื่น
/// (เส้นจริงของ CreatePaymentAsync/ConvertDocumentAsync มีเทสต์ของตัวเองแล้ว — ที่นี่ล็อก "ถูกเรียกครั้งเดียว ด้วยอาร์กิวเมนต์ที่ถูก")</para>
/// </summary>
[Trait("Category", "Db")]
public class AutopilotRound252DbTests
{
    private readonly ITestOutputHelper _out;
    public AutopilotRound252DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed class FakeNotify : INotificationEngine
    {
        public List<(string Key, NotificationContext Ctx)> Calls { get; } = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context)
        {
            Calls.Add((eventKey, context));
            return Task.CompletedTask;
        }
    }

    private sealed class Recorder
    {
        public List<(CreatePaymentRequest Req, string Actor, PaymentEntrySource Source)> Payments { get; } = new();
        public List<(Guid Source, DocumentType Target, string Actor)> Converts { get; } = new();
        public Exception? ThrowOnPayment { get; set; }
        /// <summary>ฝ่ายค้าน H2: บันทึกการชำระสำเร็จ (commit) แล้วขั้นหลัง commit โยน — จำลองงานหลังรับชำระ/แจ้งเตือนล้มหลังบันทึก</summary>
        public Exception? ThrowAfterPaymentCommit { get; set; }
        public Exception? ThrowOnConvert { get; set; }
    }

    /// <summary>IDocumentService ปลอม — CreatePaymentAsync เขียนแถว Payment จริง (อ้างอิง + ยอดค้างลด) · ConvertDocumentAsync เขียนใบลูกร่างจริง</summary>
    private static IDocumentService Docs(Guid companyId, Recorder rec) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m == nameof(IDocumentService.CreatePaymentAsync))
        {
            var req = (CreatePaymentRequest)args[1]!;
            rec.Payments.Add((req, (string)args[2]!, (PaymentEntrySource)args[3]!));
            if (rec.ThrowOnPayment != null) throw rec.ThrowOnPayment;
            using var other = DbTestDatabase.TryCreateContext()!;
            var pay = new Payment
            {
                CompanyId = companyId, PaymentNumber = "PAY252-" + Random.Shared.Next(100000, 999999), DocumentId = req.DocumentId,
                PaymentDate = req.PaymentDate, Amount = req.Amount, PaymentMethod = req.PaymentMethod, Reference = req.Reference,
            };
            other.Payments.Add(pay);
            other.SaveChanges();
            other.Documents.Where(d => d.Id == req.DocumentId)
                .ExecuteUpdate(x => x.SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.Status, DocumentStatus.Paid));
            if (rec.ThrowAfterPaymentCommit != null) throw rec.ThrowAfterPaymentCommit;
            return new PaymentResponse(pay.Id, pay.PaymentNumber, req.DocumentId, req.PaymentDate, req.Amount, req.PaymentMethod,
                req.Reference, null, null, req.Notes, DateTime.UtcNow);
        }
        if (m == nameof(IDocumentService.ConvertDocumentAsync))
        {
            var src = (Guid)args[1]!;
            var target = (DocumentType)args[2]!;
            rec.Converts.Add((src, target, (string)args[3]!));
            if (rec.ThrowOnConvert != null) throw rec.ThrowOnConvert;
            using var other = DbTestDatabase.TryCreateContext()!;
            var qt = other.Documents.AsNoTracking().First(d => d.Id == src);
            var child = new Document
            {
                CompanyId = companyId, ContactId = qt.ContactId, DocumentType = target, DocumentNumber = "DRAFT-" + Guid.NewGuid().ToString("N"),
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = src, Currency = "THB",
            };
            other.Documents.Add(child);
            other.SaveChanges();
            return new DocumentResponse(child.Id, child.DocumentNumber, target, DocumentStatus.Draft, child.DocumentDate, null, null!,
                0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, null, null, new List<DocumentLineResponse>(), DateTime.UtcNow);
        }
        return null;
    });

    private sealed record BankSeed(Guid CompanyId, Guid ContactId, Guid InvoiceId, string InvoiceNumber, Guid BankAccountId);

    private static async Task<BankSeed> SeedBankAsync(Accounting.Data.AccountingDbContext db, bool enabled, decimal balance = 1_040m, decimal wht = 30m)
    {
        var company = new Company { Name = "บริษัท 252 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, BankAutoSettleEnabled = enabled });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท พีพี ลูกค้า จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@pp252.co.th",
        };
        db.Contacts.Add(contact);
        var bank = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์", BankName = "กสิกร", AccountNumber = "1112223334" };
        db.BankAccounts.Add(bank);
        await db.SaveChangesAsync();
        var inv = await AddInvoiceAsync(db, company.Id, contact.Id, balance, wht);
        return new BankSeed(company.Id, contact.Id, inv.Id, inv.DocumentNumber, bank.Id);
    }

    private static async Task<Document> AddInvoiceAsync(Accounting.Data.AccountingDbContext db, Guid companyId, Guid contactId, decimal balance, decimal wht)
    {
        var inv = new Document
        {
            CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV252" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Approved,
            // TotalAmount = SubTotal + VAT − WHT (ตัวสร้างเอกสารจริง) · ยอดค้าง = ยอดสุทธิที่ลูกค้าต้องโอน
            DocumentDate = DateTime.UtcNow.Date.AddDays(-5), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m - wht,
            WithholdingTaxAmount = wht, PaidAmount = 0m, BalanceDue = balance, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    /// <summary>ผู้อัปโหลด statement ของบรรทัดทดสอบ (ฝ่ายค้าน H1: ปิดเองได้เมื่อผู้อัปโหลดมีสิทธิ์อนุมัติ — สิทธิ์จาก <see cref="Perms"/>)</summary>
    private static readonly Guid Uploader = Guid.NewGuid();

    private static async Task<BankTransaction> AddLineAsync(Accounting.Data.AccountingDbContext db, BankSeed s, decimal amount, string description,
        string? source = BankReceiptAutoSettle.SourceStatement, string? payee = null)
    {
        var line = new BankTransaction
        {
            CompanyId = s.CompanyId, BankAccountId = s.BankAccountId, TransactionDate = DateTime.UtcNow.Date.AddDays(-1),
            TransactionType = BankTransactionType.Deposit, Amount = amount, Description = description, Payee = payee,
            ReconciliationStatus = ReconciliationStatus.Unmatched, EntrySource = source, CreatedByUserId = Uploader, CreatedBy = Uploader.ToString(),
        };
        db.BankTransactions.Add(line);
        await db.SaveChangesAsync();
        return line;
    }

    /// <summary>ตัวตรวจสิทธิ์ปลอม — <paramref name="approve"/> = ผู้อัปโหลดมีสิทธิ์อนุมัติเอกสาร</summary>
    private static IPermissionService Perms(bool approve) => F4Proxy<IPermissionService>.Create((m, _) =>
        m == nameof(IPermissionService.HasPermissionAsync) ? approve : null);

    private static BankReceiptSettleService Bank(Accounting.Data.AccountingDbContext db, Guid companyId, Recorder rec, FakeNotify notify, bool uploaderMayApprove = true)
        => new(db, Docs(companyId, rec), NullLogger<BankReceiptSettleService>.Instance, notify, Perms(uploaderMayApprove));

    private static async Task<BankTransaction> LineOfAsync(Guid id)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    // ═══════════════ เงินโอนเข้า ⇒ ปิดหนี้ ═══════════════

    [Fact]
    public async Task เปิดปิดหนี้อัตโนมัติ_ยอดสุทธิตรง_อ้างเลขใบ_จากstatement_บันทึกรับชำระครั้งเดียว_ผูกบรรทัด_ประเมินซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "TRF FR PP " + s.InvoiceNumber);
        var rec = new Recorder();
        var notify = new FakeNotify();

        await Bank(db, s.CompanyId, rec, notify).EvaluateLineAsync(s.CompanyId, line.Id, companyEnabled: true, DateTime.UtcNow);

        var call = Assert.Single(rec.Payments);
        Assert.Equal(PaymentEntrySource.BankAutoSettle, call.Source);
        Assert.Equal(s.InvoiceId, call.Req.DocumentId);
        Assert.Equal(1_040m, call.Req.Amount);
        Assert.Equal((decimal?)30m, call.Req.WithholdingTaxAmount);   // นิติบุคคลหักตามใบ ⇒ WHT ที่เหลือทั้งก้อน (สูตรลิงก์ชำระ)
        Assert.Equal((Guid?)s.BankAccountId, call.Req.OverrideBankAccountId);
        Assert.Equal(BankReceiptSettleService.PaymentReference(line.Id), call.Req.Reference);
        var after = await LineOfAsync(line.Id);
        Assert.Equal(ReconciliationStatus.Matched, after.ReconciliationStatus);
        Assert.NotNull(after.MatchedPaymentId);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Settled, after.AutoSettleVerdict);
        Assert.Equal(BankMatchAttribution.BankAutoSettle, after.ReconciledBy);
        Assert.Empty(notify.Calls);

        // งานรอบถัดไป (ทุกบริษัท) — บรรทัดจับคู่แล้วไม่ถูกหยิบ ⇒ ไม่บันทึกซ้ำ
        using var db2 = DbTestDatabase.TryCreateContext()!;
        await Bank(db2, s.CompanyId, rec, notify).EvaluatePendingAsync(DateTime.UtcNow);
        Assert.Single(rec.Payments);
    }

    [Fact]
    public async Task ทิศตรงข้าม_บริษัทยังไม่เปิด_เสนอให้คนยืนยัน_ไม่มีการรับชำระ_ใบยังค้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: false);
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder();

        await Bank(db, s.CompanyId, rec, new FakeNotify()).EvaluateLineAsync(s.CompanyId, line.Id, companyEnabled: false, DateTime.UtcNow);

        Assert.Empty(rec.Payments);
        var after = await LineOfAsync(line.Id);
        Assert.Equal(ReconciliationStatus.Unmatched, after.ReconciliationStatus);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Suggest, after.AutoSettleVerdict);
        Assert.Equal((Guid?)s.InvoiceId, after.AutoSettleDocumentId);
        Assert.Contains("บริษัทยังไม่เปิด", after.AutoSettleReason);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1_040m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == s.InvoiceId)).BalanceDue);
    }

    [Fact]
    public async Task รายการพิมพ์มือ_เปิดอยู่ก็ไม่ปิดหนี้เอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "ฝาก " + s.InvoiceNumber, source: BankReceiptAutoSettle.SourceManual);
        var rec = new Recorder();
        await Bank(db, s.CompanyId, rec, new FakeNotify()).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Suggest, (await LineOfAsync(line.Id)).AutoSettleVerdict);
    }

    [Fact]
    public async Task ยอดเท่ากันสองใบ_ไม่อ้างเลขใบ_ไม่ปิดเอง_คิวมีตัวเลือกสองใบ_คนยืนยันใบที่เลือกได้_ใบนอกชุดถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var second = await AddInvoiceAsync(db, s.CompanyId, s.ContactId, 1_040m, 30m);
        var outsider = await AddInvoiceAsync(db, s.CompanyId, s.ContactId, 5_000m, 0m);
        var line = await AddLineAsync(db, s, 1_040m, "TRF FR KASIKORN", payee: "บริษัท พีพี ลูกค้า จำกัด");
        var rec = new Recorder();
        var svc = Bank(db, s.CompanyId, rec, new FakeNotify());

        await svc.EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        var stamped = await LineOfAsync(line.Id);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Suggest, stamped.AutoSettleVerdict);
        Assert.Null(stamped.AutoSettleDocumentId);

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var svc2 = Bank(db2, s.CompanyId, rec, new FakeNotify());
        var row = Assert.Single(await svc2.QueueAsync(s.CompanyId));
        Assert.Equal(2, row.Choices.Count);
        Assert.DoesNotContain(row.Choices, c => c.DocumentId == outsider.Id);

        var userId = Guid.NewGuid();
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc2.ConfirmAsync(s.CompanyId, line.Id, outsider.Id, userId));
        Assert.Empty(rec.Payments);
        await svc2.ConfirmAsync(s.CompanyId, line.Id, second.Id, userId);
        var call = Assert.Single(rec.Payments);
        Assert.Equal(second.Id, call.Req.DocumentId);
        Assert.Equal(PaymentEntrySource.BankAutoSettle, call.Source);
        var after = await LineOfAsync(line.Id);
        Assert.Equal(ReconciliationStatus.Matched, after.ReconciliationStatus);
        Assert.Equal(BankMatchAttribution.Person(userId), after.ReconciledBy);
    }

    [Fact]
    public async Task บันทึกรับชำระล้ม_ประทับเหตุ_แจ้งทันที_ใบยังค้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder { ThrowOnPayment = new InvalidOperationException("งวดบัญชีเดือนนี้ปิดแล้ว") };
        var notify = new FakeNotify();

        await Bank(db, s.CompanyId, rec, notify).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);

        var after = await LineOfAsync(line.Id);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Failed, after.AutoSettleVerdict);
        Assert.Contains("งวดบัญชีเดือนนี้ปิดแล้ว", after.AutoSettleReason);
        Assert.Equal(ReconciliationStatus.Unmatched, after.ReconciliationStatus);
        var n = Assert.Single(notify.Calls);
        Assert.Equal(NotificationEvents.AutopilotBankSettleFailed, n.Key);
        Assert.Contains(s.InvoiceNumber, n.Ctx.Message);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1_040m, (await check.Documents.AsNoTracking().SingleAsync(d => d.Id == s.InvoiceId)).BalanceDue);
        // ไม่ถูกหยิบซ้ำอัตโนมัติ (ต้องให้คนแก้แล้วยืนยัน) — ไม่วนสร้าง/แจ้งซ้ำทุก 5 นาที
        await Bank(check, s.CompanyId, rec, notify).EvaluatePendingAsync(DateTime.UtcNow);
        Assert.Single(rec.Payments);
        Assert.Single(notify.Calls);
    }

    [Fact]
    public async Task ล้มกลางทางหลังสร้างการชำระ_รอบหน้ากู้ด้วยเลขอ้างอิง_ไม่สร้างซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var pay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY252R-" + Random.Shared.Next(100000, 999999), DocumentId = s.InvoiceId,
            PaymentDate = DateTime.UtcNow.Date, Amount = 1_040m, PaymentMethod = PaymentMethod.BankTransfer,
            Reference = BankReceiptSettleService.PaymentReference(line.Id),
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        await db.BankTransactions.Where(t => t.Id == line.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Applying));
        var rec = new Recorder();

        await Bank(db, s.CompanyId, rec, new FakeNotify()).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);

        Assert.Empty(rec.Payments);
        var after = await LineOfAsync(line.Id);
        Assert.Equal((Guid?)pay.Id, after.MatchedPaymentId);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Settled, after.AutoSettleVerdict);
    }

    [Fact]
    public async Task ฝ่ายค้านH1_ผู้อัปโหลดstatementไม่มีสิทธิ์อนุมัติ_เสนอเท่านั้น_ไฟล์ศูนย์นำเข้าก็เสนอเท่านั้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder();
        await Bank(db, s.CompanyId, rec, new FakeNotify(), uploaderMayApprove: false).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        var after = await LineOfAsync(line.Id);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Suggest, after.AutoSettleVerdict);
        Assert.Contains("ไม่มีสิทธิ์อนุมัติ", after.AutoSettleReason);

        // ไฟล์จากศูนย์นำเข้า/นำเข้า-ส่งออก (UserFile) — แม้ผู้ส่งมีสิทธิ์อนุมัติก็เสนอเท่านั้น
        var s2 = await SeedBankAsync(db, enabled: true);
        var userFile = await AddLineAsync(db, s2, 1_040m, "TRF " + s2.InvoiceNumber, source: BankReceiptAutoSettle.SourceUserFile);
        await Bank(db, s2.CompanyId, rec, new FakeNotify()).EvaluateLineAsync(s2.CompanyId, userFile.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Suggest, (await LineOfAsync(userFile.Id)).AutoSettleVerdict);
    }

    [Fact]
    public async Task ฝ่ายค้านH2_บันทึกการชำระสำเร็จแต่ขั้นหลังcommitโยน_บรรทัดจบSettled_การชำระมีหนึ่งรายการ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder { ThrowAfterPaymentCommit = new InvalidOperationException("แจ้งเตือนหลังบันทึกล้ม") };
        var notify = new FakeNotify();

        await Bank(db, s.CompanyId, rec, notify).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);

        var after = await LineOfAsync(line.Id);
        Assert.Equal(BankReceiptAutoSettle.Stamps.Settled, after.AutoSettleVerdict);
        Assert.Equal(ReconciliationStatus.Matched, after.ReconciliationStatus);
        using var check = DbTestDatabase.TryCreateContext()!;
        var reference = BankReceiptSettleService.PaymentReference(line.Id);
        var pay = Assert.Single(await check.Payments.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.Reference == reference).ToListAsync());
        Assert.Equal((Guid?)pay.Id, after.MatchedPaymentId);
        Assert.Empty(notify.Calls);   // ไม่ประทับล้ม ไม่แจ้งล้ม

        // รอบถัดไปไม่สร้างซ้ำ
        rec.ThrowAfterPaymentCommit = null;
        await Bank(check, s.CompanyId, rec, notify).EvaluatePendingAsync(DateTime.UtcNow);
        Assert.Single(rec.Payments);
    }

    [Fact]
    public async Task ฝ่ายค้านH3_ใบแจ้งหนี้ที่มีใบกำกับภาษีมีผลแล้ว_ไม่ใช่ผู้สมัคร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        db.Documents.Add(new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.TaxInvoice, DocumentNumber = "TIV252" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-4), RelatedDocumentId = s.InvoiceId, Currency = "THB",
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m, WithholdingTaxAmount = 30m, BalanceDue = 0m, PaidAmount = 1_040m,
        });
        await db.SaveChangesAsync();
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder();
        await Bank(db, s.CompanyId, rec, new FakeNotify()).EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        var after = await LineOfAsync(line.Id);
        Assert.Equal(BankReceiptAutoSettle.Stamps.None, after.AutoSettleVerdict);
        Assert.Null(after.AutoSettleDocumentId);
    }

    [Fact]
    public async Task ฝ่ายค้านH4_บัญชีไม่ใช่THB_ไม่เข้าเส้น_ยืนยันมือก็ปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: true);
        await db.BankAccounts.Where(a => a.Id == s.BankAccountId).ExecuteUpdateAsync(x => x.SetProperty(a => a.Currency, "USD"));
        var line = await AddLineAsync(db, s, 1_040m, "TRF " + s.InvoiceNumber);
        var rec = new Recorder();
        var svc = Bank(db, s.CompanyId, rec, new FakeNotify());
        await svc.EvaluateLineAsync(s.CompanyId, line.Id, true, DateTime.UtcNow);
        Assert.Empty(rec.Payments);
        Assert.Equal(BankReceiptAutoSettle.Stamps.None, (await LineOfAsync(line.Id)).AutoSettleVerdict);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ConfirmAsync(s.CompanyId, line.Id, s.InvoiceId, Guid.NewGuid()));
        Assert.Empty(rec.Payments);
    }

    // ═══════════════ ใบเสนอราคา ⇒ แปลงอัตโนมัติ ═══════════════

    private static async Task<(Guid CompanyId, Document Qt)> SeedQuotationAsync(Accounting.Data.AccountingDbContext db, string? docMode,
        string? companyDefault = null, DateTime? since = null, DateTime? acceptedAt = null, string? contentHash = null)
    {
        var company = new Company { Name = "บริษัท 252Q " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, QuotationAcceptAutopilotDefault = companyDefault, QuotationAcceptAutopilotSince = since,
        });
        var contact = new Contact { CompanyId = company.Id, Name = "หจก.คิว ลูกค้า", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var qt = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Quotation,
            DocumentNumber = "QT252" + Random.Shared.Next(100000, 999999), Status = DocumentStatus.Sent,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, Currency = "THB",
            QuotationAcceptedAt = acceptedAt ?? DateTime.UtcNow.AddHours(-1), QuotationAcceptedBy = "คุณคิว",
            QuotationAcceptAutopilot = docMode, QuotationAcceptContentHash = contentHash,
        };
        qt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "งานติดตั้ง", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(qt);
        await db.SaveChangesAsync();
        return (company.Id, qt);
    }

    private static QuotationAcceptAutopilotService Qts(Accounting.Data.AccountingDbContext db, Guid companyId, Recorder rec, FakeNotify notify)
        => new(db, Docs(companyId, rec), NullLogger<QuotationAcceptAutopilotService>.Instance, notify);

    private static async Task<Document> QtOfAsync(Guid id)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await check.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task ตั้งต่อใบให้แปลง_ลูกค้ายอมรับ_แปลงเป็นร่างใบแจ้งหนี้ครั้งเดียว_รันซ้ำไม่แปลงซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, "ToInvoice");
        var rec = new Recorder();
        var notify = new FakeNotify();

        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        using var db2 = DbTestDatabase.TryCreateContext()!;
        await Qts(db2, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);   // เครื่องที่สอง/รอบถัดไป

        var call = Assert.Single(rec.Converts);
        Assert.Equal((qt.Id, DocumentType.Invoice, QuotationAcceptAutopilotService.Actor), call);
        var after = await QtOfAsync(qt.Id);
        Assert.Equal(nameof(QuotationAcceptAutopilot.Outcome.Converted), after.QuotationAutopilotOutcome);
        Assert.NotNull(after.QuotationAutopilotChildId);
        Assert.Empty(notify.Calls);   // สำเร็จ = สรุปประจำวัน ไม่ใช่แจ้งทันที
    }

    [Fact]
    public async Task ทิศตรงข้าม_ไม่ตั้งที่ใบและบริษัทไม่แปลง_งานไม่หยิบ_ไม่ประทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, null);
        var rec = new Recorder();
        await Qts(db, cid, rec, new FakeNotify()).RunOnceAsync(DateTime.UtcNow);
        Assert.DoesNotContain(rec.Converts, c => c.Source == qt.Id);
        Assert.Null((await QtOfAsync(qt.Id)).QuotationAutopilotAt);
    }

    [Fact]
    public async Task ค่าบริษัทแปลง_ใช้กับใบที่ยอมรับหลังวันเปิดเท่านั้น_ไม่แปลงย้อนหลัง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var since = DateTime.UtcNow.AddDays(-1);
        var (cidOld, oldQt) = await SeedQuotationAsync(db, null, "ToInvoice", since, acceptedAt: since.AddDays(-2));
        var (cidNew, newQt) = await SeedQuotationAsync(db, null, "ToInvoice", since, acceptedAt: since.AddHours(2));
        var rec = new Recorder();
        // ทีละบริษัท (Docs ปลอมผูกบริษัท) — ProcessOne ของใบเก่าไม่ได้ถูกเรียกเพราะ RunOnce ไม่หยิบ
        await Qts(db, cidNew, rec, new FakeNotify()).RunOnceAsync(DateTime.UtcNow);
        Assert.Contains(rec.Converts, c => c.Source == newQt.Id);
        Assert.DoesNotContain(rec.Converts, c => c.Source == oldQt.Id);
        Assert.Null((await QtOfAsync(oldQt.Id)).QuotationAutopilotAt);
        _ = cidOld;
    }

    [Fact]
    public async Task เนื้อหาเปลี่ยนหลังลูกค้ายอมรับ_หยุด_แจ้งทันทีครั้งเดียว_ไม่แปลง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, "ToInvoice", contentHash: new string('a', 64));
        var rec = new Recorder();
        var notify = new FakeNotify();
        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        Assert.Empty(rec.Converts);
        var after = await QtOfAsync(qt.Id);
        Assert.Equal(nameof(QuotationAcceptAutopilot.Outcome.Held), after.QuotationAutopilotOutcome);
        Assert.Equal(QuotationAcceptAutopilot.ContentChangedReason, after.QuotationAutopilotMessage);
        var n = Assert.Single(notify.Calls);
        Assert.Equal(NotificationEvents.AutopilotQuotationBlocked, n.Key);
        Assert.Contains(qt.DocumentNumber, n.Ctx.Title);
    }

    [Fact]
    public async Task มีใบลูกที่คนแปลงเองแล้ว_ไม่แปลงซ้ำ_ไม่แจ้ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, "ToInvoice");
        db.Documents.Add(new Document
        {
            CompanyId = cid, ContactId = qt.ContactId, DocumentType = DocumentType.Invoice, DocumentNumber = "INV252M" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = qt.Id, Currency = "THB",
        });
        await db.SaveChangesAsync();
        var rec = new Recorder();
        var notify = new FakeNotify();
        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        Assert.Empty(rec.Converts);
        Assert.Empty(notify.Calls);
        Assert.Equal(nameof(QuotationAcceptAutopilot.Outcome.AlreadyConverted), (await QtOfAsync(qt.Id)).QuotationAutopilotOutcome);
    }

    [Fact]
    public async Task แปลงล้ม_ประทับเหตุ_แจ้ง_ลองใหม่แล้วงานหยิบอีกครั้ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, "ToInvoice");
        var rec = new Recorder { ThrowOnConvert = new InvalidOperationException("เอกสารถูกแปลงครบแล้ว") };
        var notify = new FakeNotify();
        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        var failed = await QtOfAsync(qt.Id);
        Assert.Equal(nameof(QuotationAcceptAutopilot.Outcome.Failed), failed.QuotationAutopilotOutcome);
        Assert.Contains("เอกสารถูกแปลงครบแล้ว", failed.QuotationAutopilotMessage);
        Assert.Equal(NotificationEvents.AutopilotQuotationBlocked, Assert.Single(notify.Calls).Key);

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var autopilot = new AutopilotService(db2, Bank(db2, cid, rec, notify));
        await autopilot.RetryQuotationAsync(cid, qt.Id, Guid.NewGuid());
        Assert.Null((await QtOfAsync(qt.Id)).QuotationAutopilotAt);
        rec.ThrowOnConvert = null;
        using var db3 = DbTestDatabase.TryCreateContext()!;
        await Qts(db3, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);
        Assert.Equal(nameof(QuotationAcceptAutopilot.Outcome.Converted), (await QtOfAsync(qt.Id)).QuotationAutopilotOutcome);
        Assert.Equal(2, rec.Converts.Count);
    }

    // ═══════════════ สรุปประจำวัน ═══════════════

    [Fact]
    public async Task สรุปประจำวัน_ส่งครั้งเดียวต่อวัน_ไม่มีอะไร_ไม่ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (cid, qt) = await SeedQuotationAsync(db, "ToInvoice");
        var rec = new Recorder();
        var notify = new FakeNotify();
        await Qts(db, cid, rec, notify).ProcessOneAsync(cid, qt.Id, DateTime.UtcNow);   // มีกิจกรรม: แปลง 1 ใบ

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var digest = new AutopilotDigestService(db2, new AutopilotService(db2, Bank(db2, cid, rec, notify)), notify,
            NullLogger<AutopilotDigestService>.Instance);
        var now = DateTime.UtcNow;
        Assert.True(await digest.SendForCompanyAsync(cid, now));
        Assert.False(await digest.SendForCompanyAsync(cid, now));   // เครื่องที่สอง/รอบถัดไปของวันเดียวกัน
        var sent = Assert.Single(notify.Calls, c => c.Key == NotificationEvents.AutopilotDailyDigest);
        Assert.Contains("แปลงใบเสนอราคา", sent.Ctx.Message);

        // ทิศตรงข้าม: บริษัทที่ไม่มีอะไรเกิดขึ้น — ประทับวันแต่ไม่ส่งอีเมลว่าง
        var (quietCid, _) = await SeedQuotationAsync(db2, null);
        Assert.False(await digest.SendForCompanyAsync(quietCid, now));
        Assert.Single(notify.Calls, c => c.Key == NotificationEvents.AutopilotDailyDigest);
    }

    // ═══════════════ สลิปกันซ้ำ (พอร์ทัล) ═══════════════

    [Fact]
    public async Task สลิปพอร์ทัล_ไฟล์เดิมส่งซ้ำ_ปฏิเสธSLIPDUP_แจ้งทีมเฉพาะครั้งที่รับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedBankAsync(db, enabled: false, balance: 1_070m, wht: 0m);
        var notify = new FakeNotify();
        var portal = PortalWith(db, notify);
        var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 slip252 " + Guid.NewGuid());
        var first = await portal.UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.InvoiceId, File(bytes), 500m, "ref-abc-123456");
        Assert.True(first.AwaitingReview);
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var intent = await check.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == first.PaymentIntentId);
            Assert.Equal(SlipEvidenceRule.Sha256Hex(bytes), intent.SlipFileSha256);
            Assert.Equal("REFABC123456", intent.SlipBankRef);
            Assert.False(await check.Payments.AnyAsync(p => p.DocumentId == s.InvoiceId));   // สลิปไม่ใช่รับชำระ
        }
        Assert.Equal(NotificationEvents.AutopilotSlipSubmitted, Assert.Single(notify.Calls).Key);

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var dup = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            PortalWith(db2, notify).UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.InvoiceId, File(bytes), 500m, null));
        Assert.Equal(SlipEvidenceRule.RuleDuplicate, dup.RuleCode);
        var dupRef = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            PortalWith(db2, notify).UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.InvoiceId,
                File(System.Text.Encoding.UTF8.GetBytes("other " + Guid.NewGuid())), 500m, "REF ABC 123456"));
        Assert.Equal(SlipEvidenceRule.RuleDuplicate, dupRef.RuleCode);
        // ทิศตรงข้าม: สลิปใบใหม่ เลขใหม่ ⇒ รับ
        var ok = await PortalWith(db2, notify).UploadDocumentSlipAsync(s.CompanyId, s.ContactId, s.InvoiceId,
            File(System.Text.Encoding.UTF8.GetBytes("third " + Guid.NewGuid())), 500m, "XYZ9876543");
        Assert.NotEqual(first.PaymentIntentId, ok.PaymentIntentId);
        Assert.Equal(2, notify.Calls.Count);
    }

    private static Microsoft.AspNetCore.Http.IFormFile File(byte[] bytes)
        => new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.pdf")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "application/pdf",
        };

    /// <summary>พอร์ทัลแบบเดียวกับ <c>PartnerSecurityRound250DbTests.Portal</c> (ที่เก็บไฟล์ปลอม · intent จริงผ่าน ManualSlip) + เครื่องแจ้งเตือนปลอม</summary>
    private static PortalService PortalWith(Accounting.Data.AccountingDbContext db, FakeNotify notify)
    {
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:SigningKey"] = "r252-portal-signing-key-for-db-tests-0123456789",
        }).Build();
        var storage = F4Proxy<Accounting.Services.Storage.IFileStorage>.Create((name, args) =>
        {
            if (name == "get_" + nameof(Accounting.Services.Storage.IFileStorage.Locator))
            {
                var root = Path.GetTempPath();
                return new Accounting.Services.Storage.FileStorageLocator(root, root, root, root);
            }
            if (name == nameof(Accounting.Services.Storage.IFileStorage.WriteAsync)) return Task.CompletedTask;
            return null;
        });
        var intents = new Accounting.Services.Payments.PaymentIntentService(db,
            new Accounting.Services.Payments.IPaymentProvider[] { new Accounting.Services.Payments.Providers.ManualSlipPaymentProvider() },
            Array.Empty<Accounting.Services.Payments.IPaymentCompletionHandler>(),
            NullLogger<Accounting.Services.Payments.PaymentIntentService>.Instance);
        return new PortalService(db, cfg, storage: storage, intents: intents, notify: notify);
    }
}
