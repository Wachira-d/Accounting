using Accounting.Data;
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
/// รอบ 270 (งานค้าง 266/268/269 · คำตัดสิน 270-1-1/4/5) บน PostgreSQL จริง ทั้งสองทิศ:
/// <list type="bullet">
/// <item>270-1-1 คืนมัดจำการจองที่รับสองงวดคนละธนาคาร ⇒ ขาเงินออกตามบัญชีของแต่ละงวด · ไม่รู้บัญชี ⇒ 409 ให้เลือก · เลือกเอง ⇒ ขาเดียว</item>
/// <item>270-1-4 ใบลดหนี้ร่างของตัวช่วยใบซ้ำขึ้นคิว Autopilot · อนุมัติแล้วหลุดจากคิว · ใบลดหนี้ที่ไม่ได้มาจากตัวช่วยไม่ขึ้น</item>
/// <item>270-1-5 ตัวสร้างบรรทัดจริง: ใบกำกับที่อ่านแถว VAT ไม่ได้ ⇒ [VAT-ZERO-UNREAD] · ใบส่งออกที่พิมพ์ 0% ⇒ ไม่ติด</item>
/// </list>
/// (270-1-2/3 แจ้งแชตหลังผูกสลิป อยู่ใน <see cref="LineChatSlipRound260DbTests"/> — ใช้ตัวช่วยของคลาสนั้น)
/// </summary>
[Trait("Category", "Db")]
public class Round270DbTests
{
    private readonly ITestOutputHelper _out;
    public Round270DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed record Seed(Guid CompanyId, Guid SiteId, Guid ContactId, Guid BankA, Guid BankB, Guid GlA, Guid GlB, Guid Cash);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "ร้าน 270 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "ร้านเว็บ 270", Slug = "r270-" + tag, Subdomain = "r270-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        var glA = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11210", AccountName = "เงินฝากกสิกร", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var glB = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11220", AccountName = "เงินฝากกรุงเทพ", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var cash = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11110", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var dep = new ChartOfAccount { CompanyId = company.Id, AccountCode = "21712", AccountName = "เงินมัดจำรับ", AccountType = AccountType.Liability, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(glA, glB, cash, dep);
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 270", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var a = new BankAccount { CompanyId = company.Id, AccountName = "ร้าน 270", BankName = "กสิกรไทย", AccountNumber = "1234567890", LinkedAccountId = glA.Id };
        var b = new BankAccount { CompanyId = company.Id, AccountName = "ร้าน 270", BankName = "กรุงเทพ", AccountNumber = "9876543210", LinkedAccountId = glB.Id };
        db.BankAccounts.AddRange(a, b);
        await db.SaveChangesAsync();
        return new Seed(company.Id, site.Id, contact.Id, a.Id, b.Id, glA.Id, glB.Id, cash.Id);
    }

    // ═══ 270-1-1 คืนมัดจำตามบัญชีของแต่ละงวด ═══

    private static Task<(Guid BookingId, Guid DocId, List<Guid> PaymentIds)> BookingAsync(AccountingDbContext db, Seed s,
        params (decimal Amount, bool Stamped, Guid? Bank)[] instalments) => BookingAsync(db, s, null, instalments);

    private static async Task<(Guid BookingId, Guid DocId, List<Guid> PaymentIds)> BookingAsync(AccountingDbContext db, Seed s, decimal? depositTotal,
        params (decimal Amount, bool Stamped, Guid? Bank)[] instalments)
    {
        var total = depositTotal ?? instalments.Sum(i => i.Amount);
        var svc = new SiteBookingService
        {
            CompanyId = s.CompanyId, SiteId = s.SiteId, Name = "ทำฟัน 270", Slug = "dent-" + Guid.NewGuid().ToString("N")[..8], SlotMode = BookingSlotMode.Interval,
            DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5, BookingType = BookingType.PrePayment, IsActive = true, Price = 500m,
        };
        db.SiteBookingServices.Add(svc);
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt, IsDeposit = true, Status = DocumentStatus.Approved,
            DocumentNumber = "RD270-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, SubTotal = total, TotalAmount = total, Currency = "THB",
            BankAccountId = s.BankA, DepositDeferredAccountCode = "21712",
        };
        db.Documents.Add(doc);
        var booking = new SiteBooking
        {
            CompanyId = s.CompanyId, SiteId = s.SiteId, BookingServiceId = svc.Id, BookingNumber = "BK270-" + Random.Shared.Next(100000, 999999),
            BookingDate = DateTime.UtcNow.Date.AddDays(3), StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0), GuestName = "คุณลูกค้า",
            BookingType = BookingType.PrePayment, TotalAmount = 500m, DepositAmount = total, PaidAmount = total, Status = BookingStatus.Confirmed, ErpDocumentId = doc.Id,
        };
        db.SiteBookings.Add(booking);
        var ids = new List<Guid>();
        var at = DateTime.UtcNow.AddHours(-instalments.Length);
        foreach (var (amount, stamped, bank) in instalments)
        {
            var p = new SiteBookingPayment
            {
                CompanyId = s.CompanyId, BookingId = booking.Id, Amount = amount, PaymentMethod = bank == null ? PaymentMethod.Cash : PaymentMethod.BankTransfer,
                Status = SitePaymentStatus.Completed, PaidAt = at, ChannelStamped = stamped, BankAccountId = bank, CreatedAt = at,
            };
            at = at.AddHours(1);
            db.SiteBookingPayments.Add(p);
            ids.Add(p.Id);
        }
        await db.SaveChangesAsync();
        return (booking.Id, doc.Id, ids);
    }

    private static DocumentService Docs(AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static async Task<List<JournalEntryLine>> RefundMoneyLinesAsync(AccountingDbContext db, Guid companyId, Guid docId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == companyId && j.SourceDocumentId == docId && l.CreditAmount > 0m
                        && l.Description == BookingDepositInstalmentChannel.RefundMoneyLineDescription
                  select l).ToListAsync();

    [Fact]
    public async Task C270_1_1_คืนมัดจำสองงวดคนละธนาคาร_ขาเงินออกตามบัญชีของแต่ละงวด_คืนครั้งที่สองออกจากบัญชีที่เหลือ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // งวดแรก 60 เข้ากรุงเทพ (B) · งวดสุดท้าย 90 เข้ากสิกร (A) = หัวใบ · ย้ายเงินงวดแรกแล้ว (BKDEP)
        var (bookingId, docId, ids) = await BookingAsync(db, s, (60m, true, s.BankB), (90m, true, s.BankA));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new CmsBookingService(c1, NullLogger<CmsBookingService>.Instance).MoveEarlierInstalmentsAsync(s.CompanyId, bookingId, docId, ids[1], "staff-270",
                new List<string>(), new List<string>());
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await Docs(c2).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(100m, DateTime.UtcNow, "ลูกค้ายกเลิก"), "staff-270");
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var lines = await RefundMoneyLinesAsync(c3, s.CompanyId, docId);
            Assert.Equal(90m, lines.Single(l => l.AccountId == s.GlA).CreditAmount);   // งวดล่าสุดก่อน — เงินงวดนั้นอยู่กสิกร
            Assert.Equal(10m, lines.Single(l => l.AccountId == s.GlB).CreditAmount);   // ส่วนที่เหลือจากกรุงเทพ (ไม่ใช่หัวใบทั้งก้อน)
        }
        // คืนส่วนที่เหลือ 50 ⇒ ออกจากกรุงเทพทั้งหมด (กสิกรคืนครบแล้ว)
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            await Docs(c4).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(50m, DateTime.UtcNow, "คืนส่วนที่เหลือ"), "staff-270");
        using var c5 = DbTestDatabase.TryCreateContext()!;
        var all = await RefundMoneyLinesAsync(c5, s.CompanyId, docId);
        Assert.Equal(90m, all.Where(l => l.AccountId == s.GlA).Sum(l => l.CreditAmount));
        Assert.Equal(60m, all.Where(l => l.AccountId == s.GlB).Sum(l => l.CreditAmount));
    }

    [Fact]
    public async Task C270_1_1_ทิศตรงข้าม_งวดไม่รู้บัญชี_ปฏิเสธให้เลือก_ไม่ลงอะไร_เลือกเองแล้วขาเดียว_ช่องทางเดียวขาเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // งวดแรกบันทึกก่อนรอบ 268 (ไม่ประทับช่องทาง) ⇒ ไม่รู้ว่าเงินอยู่ไหน
        var (_, docId, _) = await BookingAsync(db, s, (60m, false, null), (90m, true, s.BankA));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Docs(c1).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(150m, DateTime.UtcNow, "ยกเลิก"), "staff-270"));
            Assert.Equal(BookingDepositInstalmentChannel.RefundChoiceCode, ex.RuleCode);
            Assert.Equal(409, ex.StatusCode);
        }
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Empty(await RefundMoneyLinesAsync(c2, s.CompanyId, docId));
            Assert.Equal(0m, await c2.Documents.Where(d => d.Id == docId).Select(d => d.DepositRefundedAmount).SingleAsync());
        }
        // ผู้ใช้เลือกบัญชีเอง ⇒ ทั้งยอดจากบัญชีนั้น
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Docs(c3).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(150m, DateTime.UtcNow, "ยกเลิก", s.Cash), "staff-270");
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(new[] { (s.Cash, 150m) }, (await RefundMoneyLinesAsync(c4, s.CompanyId, docId)).Select(l => (l.AccountId, l.CreditAmount)).ToArray());
        // ช่องทางเดียว (สองงวดเข้ากสิกร) ⇒ ขาเดียวจากกสิกร ไม่ถาม
        var (_, doc2, _) = await BookingAsync(db, s, (60m, true, s.BankA), (90m, true, s.BankA));
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            await Docs(c5).RefundDepositAsync(s.CompanyId, doc2, new RefundDepositRequest(150m, DateTime.UtcNow, "ยกเลิก"), "staff-270");
        using var c6 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(new[] { (s.GlA, 150m) }, (await RefundMoneyLinesAsync(c6, s.CompanyId, doc2)).Select(l => (l.AccountId, l.CreditAmount)).ToArray());
    }

    [Fact]
    public async Task C270_2_1_มัดจำ500Bบวก500A_แล้วรับเพิ่ม3000C_คืนเต็ม_CrB500และA500_ไม่ใช่A1000_ตัวย้ายเงินตัดที่งวดที่ครบมัดจำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var glC = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "11230", AccountName = "เงินฝากไทยพาณิชย์", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        db.ChartOfAccounts.Add(glC);
        await db.SaveChangesAsync();
        var bankC = new BankAccount { CompanyId = s.CompanyId, AccountName = "ร้าน 270", BankName = "ไทยพาณิชย์", AccountNumber = "5555555555", LinkedAccountId = glC.Id };
        db.BankAccounts.Add(bankC);
        await db.SaveChangesAsync();
        // มัดจำ 1,000 = 500 เข้า B + 500 เข้า A (หัวใบ) · หลังออกใบ ลูกค้าโอนค่าบริการที่เหลือ 3,000 เข้า C (ลงบนการจองอย่างเดียว)
        var (bookingId, docId, ids) = await BookingAsync(db, s, 1_000m, (500m, true, s.BankB), (500m, true, s.BankA), (3_000m, true, bankC.Id));
        // ตัวย้ายเงินไม่รู้งวดสุดท้าย (เส้น hook) ⇒ งวดสุดท้าย = งวดที่ครบมัดจำ (ids[1]) — ไม่ใช่แถว 3,000 ที่เข้าทีหลัง
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new CmsBookingService(c1, NullLogger<CmsBookingService>.Instance).MoveEarlierInstalmentsAsync(s.CompanyId, bookingId, docId, null, "staff-270",
                new List<string>(), new List<string>());
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await c2.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == BookingDepositInstalmentChannel.ReferenceFor(ids[0])));
            Assert.False(await c2.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == BookingDepositInstalmentChannel.ReferenceFor(ids[1])));
            Assert.False(await c2.JournalEntries.AnyAsync(j => j.CompanyId == s.CompanyId && j.Reference == BookingDepositInstalmentChannel.ReferenceFor(ids[2])));
            await Docs(c2).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(1_000m, DateTime.UtcNow, "ยกเลิก"), "staff-270");
        }
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var lines = await RefundMoneyLinesAsync(c3, s.CompanyId, docId);
        Assert.Equal(500m, lines.Where(l => l.AccountId == s.GlB).Sum(l => l.CreditAmount));
        Assert.Equal(500m, lines.Where(l => l.AccountId == s.GlA).Sum(l => l.CreditAmount));
        Assert.DoesNotContain(lines, l => l.AccountId == glC.Id);
        Assert.Equal(1_000m, lines.Sum(l => l.CreditAmount));
    }

    [Fact]
    public async Task C270_2_1_ทิศตรงข้าม_บัญชีธนาคารของงวดก่อนถูกถอดผัง_ไม่400_ให้เลือกบัญชี409_คืนเฉพาะส่วนที่รู้ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (bookingId, docId, ids) = await BookingAsync(db, s, (60m, true, s.BankB), (90m, true, s.BankA));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await new CmsBookingService(c1, NullLogger<CmsBookingService>.Instance).MoveEarlierInstalmentsAsync(s.CompanyId, bookingId, docId, ids[1], "staff-270",
                new List<string>(), new List<string>());
        // ฝ่ายค้าน 270 L1: ถอดผังของบัญชี B หลังรับเงิน ⇒ ไม่รู้ ⇒ 409 ให้เลือก (เดิม 400 คืนเงินไม่ได้)
        await db.BankAccounts.Where(b => b.Id == s.BankB).ExecuteUpdateAsync(x => x.SetProperty(b => b.LinkedAccountId, (Guid?)null));
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Docs(c2).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(150m, DateTime.UtcNow, "ยกเลิก"), "staff-270"));
            Assert.Equal(BookingDepositInstalmentChannel.RefundChoiceCode, ex.RuleCode);
        }
        // คืนเฉพาะส่วนที่อยู่บัญชี A (ยังใช้ได้) ⇒ ไม่ต้องถาม
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Docs(c3).RefundDepositAsync(s.CompanyId, docId, new RefundDepositRequest(90m, DateTime.UtcNow, "คืนบางส่วน"), "staff-270");
        using var c4 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(new[] { (s.GlA, 90m) }, (await RefundMoneyLinesAsync(c4, s.CompanyId, docId)).Select(l => (l.AccountId, l.CreditAmount)).ToArray());
    }

    // ═══ 270-2-5 ใบกำกับ OCR VAT 0% ที่อนุมัติไปแล้ว (ตัวตรวจย้อนหลัง อ่านอย่างเดียว) ═══

    [Fact]
    public async Task C270_2_5_ใบกำกับOCRอนุมัติแล้วVAT0_กระดาษไม่บอกอัตราศูนย์_ขึ้นรายการตรวจ_ไม่แก้ใบ_ทิศตรงข้าม_ใบส่งออก_ร่าง_ใบ7_ไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        async Task<Guid> DocFromScan(string raw, DocumentStatus status, decimal vatRate, decimal vat)
        {
            var d = new Document
            {
                CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.TaxInvoice, Status = status, Currency = "THB",
                DocumentNumber = "IV270-" + Guid.NewGuid().ToString("N")[..8], DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = vat, TotalAmount = 1_000m + vat,
                Lines = new List<DocumentLine> { new() { Description = "สินค้า", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = vatRate, VatAmount = vat, LineOrder = 1 } },
            };
            db.Documents.Add(d);
            db.Set<OcrScanResult>().Add(new OcrScanResult { CompanyId = s.CompanyId, OriginalFileName = "x.jpg", ScanStatus = "Completed", DocumentType = "TaxInvoice",
                RawTextContent = raw, CreatedDocumentId = d.Id });
            await db.SaveChangesAsync();
            return d.Id;
        }
        var bad = await DocFromScan(OcrPaperSamples.HardwareBillDiscount, DocumentStatus.Approved, 0m, 0m);
        var export = await DocFromScan(Round270Tests.ExportTaxInvoicePaper, DocumentStatus.Approved, 0m, 0m);
        var draft = await DocFromScan(OcrPaperSamples.HardwareBillDiscount, DocumentStatus.Draft, 0m, 0m);
        var ok = await DocFromScan(OcrPaperSamples.HardwareBillDiscount, DocumentStatus.Approved, 7m, 70m);
        using var c = DbTestDatabase.TryCreateContext()!;
        var rows = await new TaxService(c).GetOcrZeroVatReviewAsync(s.CompanyId);
        var hit = Assert.Single(rows);
        Assert.Equal(bad, hit.DocumentId);
        Assert.Contains("ยกเลิก", hit.HowToFix);
        Assert.False(hit.PeriodFiled);
        Assert.DoesNotContain(rows, r => r.DocumentId == export || r.DocumentId == draft || r.DocumentId == ok);
        // อ่านอย่างเดียว: ใบไม่ถูกแตะ
        var after = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == bad);
        Assert.Equal((DocumentStatus.Approved, 0m), (after.Status, after.VatAmount));
    }

    // ═══ 270-1-4 คิวใบลดหนี้ร่างของตัวช่วยใบซ้ำ ═══

    [Fact]
    public async Task C270_1_4_ใบลดหนี้ร่างจากตัวช่วยใบซ้ำขึ้นคิวAutopilot_อนุมัติแล้วหลุด_ใบลดหนี้อื่นไม่ขึ้น_ข้ามบริษัทไม่เห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        Document Cn(Guid companyId, Guid contactId, string no) => new()
        {
            CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.CreditNote, Status = DocumentStatus.Draft,
            DocumentNumber = no, DocumentDate = DateTime.UtcNow.Date, SubTotal = 100m, TotalAmount = 107m, VatAmount = 7m, Currency = "THB",
        };
        var fromWizard = Cn(s.CompanyId, s.ContactId, "DRAFT-CN270-" + Guid.NewGuid().ToString("N")[..6]);
        var manual = Cn(s.CompanyId, s.ContactId, "DRAFT-CN270M-" + Guid.NewGuid().ToString("N")[..6]);
        var otherCompanyCn = Cn(other.CompanyId, other.ContactId, "DRAFT-CN270X-" + Guid.NewGuid().ToString("N")[..6]);
        // ฝ่ายค้าน 270 L7: audit ของตัวช่วยบันทึกไม่ได้ ⇒ ทางสำรองจากหมายเหตุที่ตัวช่วยเขียนบนใบ
        var noAudit = Cn(s.CompanyId, s.ContactId, "DRAFT-CN270N-" + Guid.NewGuid().ToString("N")[..6]);
        noAudit.Notes = DuplicateSalePolicy.CreditNoteNote("INV270-2", "RC270-2");
        db.Documents.AddRange(fromWizard, manual, otherCompanyCn, noAudit);
        string Audit(Guid cn) => System.Text.Json.JsonSerializer.Serialize(new
        {
            action = DuplicateSalePolicy.CreditNoteAuditAction, RuleCode = DuplicateSalePolicy.RuleCode, CashSale = "RC270-1", Invoice = "INV270-1",
            extra = new { creditNote = cn, DocumentNumber = "CN" },
        });
        db.AddChainedAuditLog(new AuditLog { CompanyId = s.CompanyId, UserEmail = "u", Action = AuditAction.Update, EntityType = "Document", EntityId = Guid.NewGuid().ToString(),
            NewValues = Audit(fromWizard.Id), Timestamp = DateTime.UtcNow.AddDays(-2) });
        // แถวของบริษัทอื่นชี้ใบของบริษัทนี้ (ข้อมูลเพี้ยน) ⇒ ต้องไม่ขึ้นในบริษัทอื่น
        db.AddChainedAuditLog(new AuditLog { CompanyId = other.CompanyId, UserEmail = "u", Action = AuditAction.Update, EntityType = "Document", EntityId = Guid.NewGuid().ToString(),
            NewValues = Audit(fromWizard.Id), Timestamp = DateTime.UtcNow });
        await db.SaveChangesAsync();
        // ใบลดหนี้สร้างก่อนแถว audit ของตัวช่วย (ลำดับจริง)
        await db.Documents.Where(d => d.Id == fromWizard.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.CreatedAt, DateTime.UtcNow.AddDays(-2).AddMinutes(-1)));

        var bank = F4Proxy<IBankReceiptSettleService>.Create((_, _) => null);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var issues = await new AutopilotService(c1, bank).IssuesAsync(s.CompanyId, DateTime.UtcNow);
            var hit = Assert.Single(issues, i => i.Kind == DuplicateSalePolicy.CreditNoteDraftIssueKind && i.DocumentId == fromWizard.Id);
            Assert.Contains("INV270-1", hit.Title);
            Assert.DoesNotContain("DRAFT-", hit.Title);   // ร่างยังไม่มีเลข (§86/4) — ไม่โชว์เลขชั่วคราว
            var fallback = Assert.Single(issues, i => i.Kind == DuplicateSalePolicy.CreditNoteDraftIssueKind && i.DocumentId == noAudit.Id);
            Assert.Contains("INV270-2", fallback.Title);
            Assert.Contains("ค้าง 2 วัน", hit.Detail);
            Assert.DoesNotContain(issues, i => i.DocumentId == manual.Id && i.Kind == DuplicateSalePolicy.CreditNoteDraftIssueKind);
            var otherIssues = await new AutopilotService(c1, bank).IssuesAsync(other.CompanyId, DateTime.UtcNow);
            Assert.DoesNotContain(otherIssues, i => i.Kind == DuplicateSalePolicy.CreditNoteDraftIssueKind);
        }
        // ทิศตรงข้าม: อนุมัติแล้ว ⇒ หลุดจากคิวเอง (ไม่มีสถานะให้ประทับ)
        await db.Documents.Where(d => d.Id == fromWizard.Id || d.Id == noAudit.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.DoesNotContain(await new AutopilotService(c2, bank).IssuesAsync(s.CompanyId, DateTime.UtcNow), i => i.Kind == DuplicateSalePolicy.CreditNoteDraftIssueKind);
    }

    // ═══ 270-1-5 ตัวสร้างบรรทัดจริง ═══

    private const string ExportTaxInvoice =
        "บริษัท สยามเอ็กซ์ปอร์ต เทรดดิ้ง จำกัด (สำนักงานใหญ่)\nเลขประจำตัวผู้เสียภาษี 0105548012345\nใบกำกับภาษี / TAX INVOICE (EXPORT)\n" +
        "เลขที่ EX6811-0021   วันที่ 05/11/2569\n1  Dried Mango 10 kg x 100 cartons        100   1,000.00   100,000.00\n" +
        "รวมเงิน                                               100,000.00\nภาษีมูลค่าเพิ่ม 0% (ส่งออก มาตรา 80/1)                      0.00\n" +
        "จำนวนเงินรวมทั้งสิ้น                                  100,000.00";

    private static async Task<(Guid Company, Guid Scan)> ScanAsync(AccountingDbContext db, string raw, decimal sub, decimal? vat, decimal total)
    {
        var company = new Company { Name = "OCR270 " + Guid.NewGuid(), TaxId = "0203562005871", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = company.Id, AccountCode = "52120", AccountName = "ค่าวัสดุสิ้นเปลือง" });
        var scan = new OcrScanResult
        {
            CompanyId = company.Id, OriginalFileName = "ocr270.jpg", ScanStatus = "Completed", DocumentType = "TaxInvoice", OurRole = "Buyer",
            RawTextContent = raw, ExtractedSubTotal = sub, ExtractedVatAmount = vat, ExtractedTotalAmount = total,
            ExtractedDocumentNumber = "IV6809-0142", ExtractedVendorName = "บริษัท ไทยวัสดุภัณฑ์ก่อสร้าง จำกัด",
            SuggestedAccountsJson = "{\"DebitAccountCode\":\"52120\",\"CreditAccountCode\":null}",
        };
        db.Set<OcrScanResult>().Add(scan);
        await db.SaveChangesAsync();
        return (company.Id, scan.Id);
    }

    private static OcrService Ocr(AccountingDbContext db)
        => new(db, null!, null!, NullLogger<OcrService>.Instance, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public async Task C270_1_5_ใบกำกับที่อ่านแถวVATไม่ได้_ตัวสร้างบรรทัดจริง_ติดแท็ก_หยุดอนุมัติเอง_ทิศตรงข้าม_อ่านได้และใบส่งออกไม่ติด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // กระดาษจริง (ร้านวัสดุ) พิมพ์ VAT 92.77 แต่ engine อ่านแถว VAT ไม่ได้ ⇒ ยอดหัวใบ = ยอดรวม ไม่มี VAT
        var (c1, s1) = await ScanAsync(db, OcrPaperSamples.HardwareBillDiscount, 1418.02m, null, 1418.02m);
        using (var r1 = DbTestDatabase.TryCreateContext()!)
        {
            var p = await Ocr(r1).PreviewDocumentLinesAsync(c1, s1, null);
            _out.WriteLine(string.Join(" | ", p.Lines.Select(l => $"{l.Amount}/{l.VatRate}/{l.VatAmount}")) + " ‖ " + p.Notes);
            // บรรทัดสรุปบรรทัดเดียว 1,418.02 VAT 0 (บั๊กที่ฝ่ายค้าน 269 ข้อ 3 รายงาน — ไม่แต่ง 7% เอง) ⇒ ติดแท็ก + หยุดการอนุมัติเอง
            Assert.Equal(new[] { (1418.02m, 0m, 0m) }, p.Lines.Select(l => (l.Amount, l.VatRate, l.VatAmount)).ToArray());
            Assert.Contains(OcrZeroVatTaxInvoiceGuard.Tag, p.Notes ?? "");
            Assert.False(OcrPostingReadiness.Evaluate(p.Notes, hasUsableDate: true).CanAutoApprove);
        }
        // ทิศตรงข้าม 1: อ่าน VAT ได้ ⇒ ไม่ติด
        var (c2, s2) = await ScanAsync(db, OcrPaperSamples.HardwareBillDiscount, 1325.25m, 92.77m, 1418.02m);
        using (var r2 = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(OcrZeroVatTaxInvoiceGuard.Tag, (await Ocr(r2).PreviewDocumentLinesAsync(c2, s2, null)).Notes ?? "");
        // ทิศตรงข้าม 2: ใบกำกับส่งออกจริงที่พิมพ์ 0% ⇒ ไม่ติด
        var (c3, s3) = await ScanAsync(db, ExportTaxInvoice, 100000m, 0m, 100000m);
        using var r3 = DbTestDatabase.TryCreateContext()!;
        var exp = await Ocr(r3).PreviewDocumentLinesAsync(c3, s3, null);
        Assert.All(exp.Lines, l => Assert.Equal(0m, l.VatRate));
        Assert.DoesNotContain(OcrZeroVatTaxInvoiceGuard.Tag, exp.Notes ?? "");
    }
}
