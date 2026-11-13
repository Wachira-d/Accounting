using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 268 (backlog cleanup) บน PostgreSQL จริง:
/// <list type="bullet">
/// <item>268-1-2 — ยืนยันสลิปออเดอร์หน้าเว็บ ⇒ ขาเงินเข้าเป็นธนาคารที่ลูกค้าโอน (ตัวตัดสินเดียว <see cref="SlipReceivingAccount"/>) · พนักงานเลือกเองได้ · บัญชีที่ใช้ไม่ได้ ⇒ ปฏิเสธ ·
/// ทิศตรงข้าม: เงินผ่านผู้ให้บริการ (มีรหัสธุรกรรม) ไม่ใช่สลิป · ผังพักของผู้ให้บริการชนะธนาคาร</item>
/// <item>268-1-3 — มัดจำการจองหลายงวดคนละช่องทาง ⇒ แต่ละงวดลงผังของช่องทางที่เข้าจริง (JE ย้ายเงินผูกใบเสร็จมัดจำ · กันซ้ำ) · ทิศตรงข้าม: ช่องทางเดียวกัน ⇒ ไม่มี JE · งวดก่อนรอบ 268 ⇒ ไม่เดา</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class Round268DbTests
{
    private readonly ITestOutputHelper _out;
    public Round268DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed record Seed(Guid CompanyId, Guid SiteId, Guid ContactId, Guid BankA, Guid BankB, Guid GlA, Guid GlB, Guid Cash);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool siteTransferRow = true)
    {
        var company = new Company { Name = "ร้าน 268 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "ร้านเว็บ 268", Slug = "r268-" + tag, Subdomain = "r268-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        var glA = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11210", AccountName = "เงินฝากกสิกร", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var glB = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11220", AccountName = "เงินฝากกรุงเทพ", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var cash = new ChartOfAccount { CompanyId = company.Id, AccountCode = "11110", AccountName = "เงินสด", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var dep = new ChartOfAccount { CompanyId = company.Id, AccountCode = "21710", AccountName = "เงินมัดจำรับ", AccountType = AccountType.Liability, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(glA, glB, cash, dep);
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้า 268", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var a = new BankAccount { CompanyId = company.Id, AccountName = "ร้าน 268", BankName = "กสิกรไทย", AccountNumber = "1234567890", LinkedAccountId = glA.Id };
        var b = new BankAccount { CompanyId = company.Id, AccountName = "ร้าน 268", BankName = "กรุงเทพ", AccountNumber = "9876543210", LinkedAccountId = glB.Id };
        db.BankAccounts.AddRange(a, b);
        if (siteTransferRow)
            db.SitePaymentGateways.Add(new SitePaymentGateway
            {
                CompanyId = company.Id, SiteId = site.Id, GatewayType = PaymentGatewayType.BankTransfer, Name = "กสิกรไทย", IsTestMode = false,
                BankName = "กสิกรไทย", BankAccountNumber = "123-4-56789-0", BankAccountName = "ร้าน 268",
            });
        await db.SaveChangesAsync();
        return new Seed(company.Id, site.Id, contact.Id, a.Id, b.Id, glA.Id, glB.Id, cash.Id);
    }

    // ═══ 268-1-2: สลิปออเดอร์หน้าเว็บ ═══

    private sealed record Captured(CreatePaymentRequest Req, string Actor);

    private static IDocumentService Docs(List<Captured> calls) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m != nameof(IDocumentService.CreatePaymentAsync)) return null;
        var req = (CreatePaymentRequest)args[1]!;
        calls.Add(new Captured(req, (string)args[2]!));
        return new PaymentResponse(Guid.NewGuid(), "RC268", req.DocumentId, req.PaymentDate, req.Amount, req.PaymentMethod, req.Reference, null,
            req.OverrideBankAccountId, req.Notes, DateTime.UtcNow);
    });

    private static CmsCommerceService Commerce(AccountingDbContext db, IDocumentService docs) => new(db, NullLogger<CmsCommerceService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), null, docs);

    /// <summary>ออเดอร์ที่มีใบขาย ERP อนุมัติแล้ว + แถวรับเงินรอยืนยัน 1 แถว</summary>
    private static async Task<(Guid OrderId, Guid PaymentId)> OrderAsync(AccountingDbContext db, Seed s, bool slip, string? gatewayTxn = null)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice, DocumentNumber = "IV268-" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        db.Documents.Add(doc);
        var order = new SiteOrder
        {
            CompanyId = s.CompanyId, SiteId = s.SiteId, OrderNumber = "WO268-" + Random.Shared.Next(100000, 999999), Status = SiteOrderStatus.Confirmed,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, ErpDocumentId = doc.Id,
        };
        db.SiteOrders.Add(order);
        var pay = new SiteOrderPayment
        {
            CompanyId = s.CompanyId, OrderId = order.Id, Amount = 1_070m, PaymentMethod = PaymentMethod.BankTransfer, Reference = "SLIP268-" + Guid.NewGuid().ToString("N")[..6],
            SlipUrl = slip ? "/uploads/slips/x.png" : null, GatewayTransactionId = gatewayTxn, Status = SitePaymentStatus.Pending,
        };
        db.SiteOrderPayments.Add(pay);
        await db.SaveChangesAsync();
        return (order.Id, pay.Id);
    }

    [Fact]
    public async Task C268_1_2_ยืนยันสลิปออเดอร์_ลงธนาคารที่แสดงให้ลูกค้าโอน_ไม่ใช่เงินสด_พนักงานเลือกบัญชีอื่นได้_บัญชีนอกบริษัทปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (orderId, payId) = await OrderAsync(db, s, slip: true);
        var calls = new List<Captured>();

        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Commerce(c1, Docs(calls));
            var r = await svc.SlipReceivingAccountsAsync(s.CompanyId, s.SiteId, orderId, payId, null);
            Assert.NotNull(r);
            Assert.True(r!.Applies);
            Assert.Equal(SlipReceivingAccount.Origin.ShownToCustomer, r.Decision.Origin);   // เลขบัญชีรับโอนของเว็บ (มีขีด) ตรงกสิกร
            Assert.Equal(s.BankA, r.Decision.BankAccountId);
            Assert.True(await svc.ConfirmPaymentAsync(s.CompanyId, s.SiteId, orderId, payId, "staff-268", receivingBankAccountId: r.Decision.BankAccountId));
        }
        var call = Assert.Single(calls);
        Assert.Equal(s.BankA, call.Req.OverrideBankAccountId);      // เดิม null ⇒ ใบขายไม่มีแหล่งเงิน ⇒ เงินสด 111
        Assert.Null(call.Req.OverridePaymentAccountId);

        // พนักงานเลือกบัญชีอื่น (ใช้ได้) ⇒ ที่มา = เลือกเอง · บัญชีที่ไม่ใช่ของบริษัท ⇒ ปัญหา (ผู้เรียกตอบ 400 — ไม่ตกไปเงินสด)
        var (order2, pay2) = await OrderAsync(db, s, slip: true);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Commerce(c2, Docs(calls));
            var chosen = await svc.SlipReceivingAccountsAsync(s.CompanyId, s.SiteId, order2, pay2, s.BankB);
            Assert.True(chosen!.Decision.Ok);
            Assert.Equal(SlipReceivingAccount.Origin.Chosen, chosen.Decision.Origin);
            Assert.Equal(s.BankB, chosen.Decision.BankAccountId);
            var foreign = await svc.SlipReceivingAccountsAsync(s.CompanyId, s.SiteId, order2, pay2, Guid.NewGuid());
            Assert.False(foreign!.Decision.Ok);
            Assert.NotNull(foreign.Decision.Problem);
        }
        // 268-1-13 (ฝ่ายค้าน L7/M6): ทางของหน้า — ตัดสินใต้ล็อกกับแถวที่ยืนยันจริง · บัญชีนอกบริษัท ⇒ 400 ไม่มีการรับชำระ · เลือกได้ ⇒ ธนาคารนั้น + audit ผู้เลือก/ที่มา
        var before = calls.Count;
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Commerce(c3, Docs(calls)).ConfirmSlipPaymentAsync(s.CompanyId, s.SiteId, order2, pay2, "staff-268", Guid.NewGuid()));
            Assert.Equal(400, ex.StatusCode);
        }
        Assert.Equal(before, calls.Count);
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.True(await Commerce(c4, Docs(calls)).ConfirmSlipPaymentAsync(s.CompanyId, s.SiteId, order2, pay2, "staff-268", s.BankB));
        Assert.Equal(s.BankB, calls[^1].Req.OverrideBankAccountId);
        using var chk = DbTestDatabase.TryCreateContext()!;
        var audit = Assert.Single(await chk.AuditLogs.AsNoTracking().Where(a => a.CompanyId == s.CompanyId && a.EntityId == order2.ToString()).Select(a => a.NewValues!).ToListAsync(),
            v => v.Contains("slip-receiving-account"));
        Assert.Contains("Chosen", audit);
        Assert.Contains(s.BankB.ToString(), audit);
    }

    [Fact]
    public async Task C268_1_2_ทิศตรงข้าม_เงินผ่านผู้ให้บริการไม่ใช่สลิป_ผังพักชนะธนาคาร_หลายบัญชีไม่มีแถวรับโอน_ไม่เดา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (orderGw, payGw) = await OrderAsync(db, s, slip: false, gatewayTxn: "chrg_test_268");
        var calls = new List<Captured>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var svc = Commerce(c1, Docs(calls));
            var r = await svc.SlipReceivingAccountsAsync(s.CompanyId, s.SiteId, orderGw, payGw, null);
            Assert.False(r!.Applies);
            Assert.Null(r.Decision.BankAccountId);
            Assert.False((await svc.SlipReceivingAccountsAsync(s.CompanyId, s.SiteId, orderGw, payGw, s.BankA))!.Decision.Ok);   // เลือกธนาคารไม่ได้
            // ผังพักของผู้ให้บริการ (moneyIn) ชนะธนาคารที่ส่งมา
            Assert.True(await svc.ConfirmPaymentAsync(s.CompanyId, s.SiteId, orderGw, payGw, "payment-gateway", moneyInAccountId: s.Cash, receivingBankAccountId: s.BankA));
        }
        var call = Assert.Single(calls);
        Assert.Null(call.Req.OverrideBankAccountId);
        Assert.Equal(s.Cash, call.Req.OverridePaymentAccountId);

        // เว็บไม่มีแถวรับโอน + บริษัทมีสองบัญชี ⇒ ไม่แนะนำ (คงพฤติกรรมเดิม · ไม่เดา)
        var s2 = await SeedAsync(db, siteTransferRow: false);
        var (o2, p2) = await OrderAsync(db, s2, slip: true);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var none = await Commerce(c2, Docs(calls)).SlipReceivingAccountsAsync(s2.CompanyId, s2.SiteId, o2, p2, null);
        Assert.True(none!.Applies);
        Assert.Equal(SlipReceivingAccount.Origin.None, none.Decision.Origin);
        Assert.Null(none.Decision.BankAccountId);
        // บริษัทอื่นมองไม่เห็นออเดอร์นี้
        Assert.Null(await Commerce(c2, Docs(calls)).SlipReceivingAccountsAsync(s.CompanyId, s2.SiteId, o2, p2, null));
    }

    // ═══ 268-1-3: มัดจำการจองหลายงวด ═══

    private static async Task<(Guid BookingId, Guid DocId, List<Guid> PaymentIds)> BookingAsync(AccountingDbContext db, Seed s,
        params (decimal Amount, bool Stamped, Guid? Bank)[] instalments)
    {
        var svc = new SiteBookingService
        {
            CompanyId = s.CompanyId, SiteId = s.SiteId, Name = "ทำฟัน 268", Slug = "dent-" + Guid.NewGuid().ToString("N")[..8], SlotMode = BookingSlotMode.Interval,
            DurationMinutes = 60, SlotIntervalMinutes = 60, MaxCapacity = 5, BookingType = BookingType.PrePayment, IsActive = true, Price = 500m,
        };
        db.SiteBookingServices.Add(svc);
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt, IsDeposit = true, Status = DocumentStatus.Approved,
            DocumentNumber = "RD268-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, SubTotal = 150m, TotalAmount = 150m, Currency = "THB",
            BankAccountId = s.BankA,
        };
        db.Documents.Add(doc);
        var booking = new SiteBooking
        {
            CompanyId = s.CompanyId, SiteId = s.SiteId, BookingServiceId = svc.Id, BookingNumber = "BK268-" + Random.Shared.Next(100000, 999999),
            BookingDate = DateTime.UtcNow.Date.AddDays(3), StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(11, 0), GuestName = "คุณลูกค้า",
            BookingType = BookingType.PrePayment, TotalAmount = 500m, DepositAmount = 150m, PaidAmount = 150m, Status = BookingStatus.Confirmed, ErpDocumentId = doc.Id,
        };
        db.SiteBookings.Add(booking);
        var ids = new List<Guid>();
        var at = DateTime.UtcNow.AddHours(-instalments.Length);
        foreach (var (amount, stamped, bank) in instalments)
        {
            var p = new SiteBookingPayment
            {
                CompanyId = s.CompanyId, BookingId = booking.Id, Amount = amount, PaymentMethod = bank == null ? PaymentMethod.Cash : PaymentMethod.BankTransfer,
                Status = SitePaymentStatus.Completed, PaidAt = at, ChannelStamped = stamped, BankAccountId = bank,
            };
            at = at.AddHours(1);
            db.SiteBookingPayments.Add(p);
            ids.Add(p.Id);
        }
        await db.SaveChangesAsync();
        return (booking.Id, doc.Id, ids);
    }

    private static CmsBookingService Bookings(AccountingDbContext db) => new(db, NullLogger<CmsBookingService>.Instance);

    private static async Task<List<JournalEntryLine>> MoveLinesAsync(AccountingDbContext db, Guid companyId, Guid docId)
        => await (from l in db.JournalEntryLines.AsNoTracking()
                  join j in db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                  where j.CompanyId == companyId && j.SourceDocumentId == docId && j.Reference != null && j.Reference.StartsWith("BKDEP-")
                  select l).ToListAsync();

    [Fact]
    public async Task C268_1_3_มัดจำสองงวดคนละธนาคาร_งวดแรกย้ายไปผังของงวดนั้น_รันซ้ำไม่ซ้ำ_ทิศตรงข้าม_ช่องทางเดียวกันไม่มี_JE()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // งวดแรก 60 เข้ากรุงเทพ (B) · งวดสุดท้าย 90 เข้ากสิกร (A) = แหล่งเงินของใบ
        var (bookingId, docId, ids) = await BookingAsync(db, s, (60m, true, s.BankB), (90m, true, s.BankA));
        var notices = new List<string>();
        var notes = new List<string>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c1).MoveEarlierInstalmentsAsync(s.CompanyId, bookingId, docId, ids[1], "staff-268", notices, notes);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var lines = await MoveLinesAsync(c2, s.CompanyId, docId);
            Assert.Equal(2, lines.Count);
            Assert.Equal(60m, lines.Single(l => l.AccountId == s.GlB).DebitAmount);    // เงินงวดแรกอยู่ธนาคารกรุงเทพ
            Assert.Equal(60m, lines.Single(l => l.AccountId == s.GlA).CreditAmount);   // ย้ายออกจากผังของใบ
            Assert.Contains(notices, n => n.Contains(BookingDepositInstalmentChannel.RuleCode));
            var je = await c2.JournalEntries.AsNoTracking().SingleAsync(j => j.CompanyId == s.CompanyId && j.Reference == BookingDepositInstalmentChannel.ReferenceFor(ids[0]));
            var paid0 = await c2.SiteBookingPayments.AsNoTracking().Where(p => p.Id == ids[0]).Select(p => p.PaidAt).SingleAsync();
            Assert.Equal(paid0!.Value, je.EntryDate, TimeSpan.FromSeconds(1));   // ฝ่ายค้าน M5: วันที่เงินงวดนั้นเข้า (งวดบัญชีเปิด) ไม่ใช่วันที่ใบ
        }
        // รันซ้ำ (ยืนยันซ้ำ/งานลองใหม่) ⇒ ไม่มี JE ที่สอง
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c3).MoveEarlierInstalmentsAsync(s.CompanyId, bookingId, docId, ids[1], "staff-268", new List<string>(), new List<string>());
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(2, (await MoveLinesAsync(c4, s.CompanyId, docId)).Count);

        // ทิศตรงข้าม: สองงวดเข้าธนาคารเดียวกับใบ ⇒ ไม่มี JE ย้าย · งวดที่บันทึกก่อนรอบ 268 ⇒ ไม่เดา + หมายเหตุ
        var (b2, d2, ids2) = await BookingAsync(db, s, (50m, false, null), (60m, true, s.BankA), (40m, true, s.BankA));
        var n2 = new List<string>();
        using (var c5 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c5).MoveEarlierInstalmentsAsync(s.CompanyId, b2, d2, ids2[2], "staff-268", n2, new List<string>());
        using var c6 = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await MoveLinesAsync(c6, s.CompanyId, d2));
        Assert.Contains(BookingDepositInstalmentChannel.UnknownNote(1), n2);
    }

    [Fact]
    public async Task C268_1_3_บันทึกรับเงินมัดจำ_ประทับช่องทางของงวดบนแถวรับเงิน_ผังพักชนะธนาคาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (bookingId, _, _) = await BookingAsync(db, s);
        // ปลดยอดรับเดิมของการจองทดสอบ (สร้างว่าง) ⇒ รับได้
        await db.SiteBookings.Where(b => b.Id == bookingId).ExecuteUpdateAsync(x => x.SetProperty(b => b.PaidAmount, 0m).SetProperty(b => b.ErpDocumentId, (Guid?)null));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c1).RecordDepositPaymentAsync(s.CompanyId, s.SiteId, bookingId, new RecordBookingPaymentRequest(60m, DateTime.UtcNow, "T268-1", s.BankB), "staff-268");
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c2).RecordDepositPaymentAsync(s.CompanyId, s.SiteId, bookingId, new RecordBookingPaymentRequest(40m, DateTime.UtcNow, "T268-2", s.BankB), "payment-gateway",
                moneyInAccountId: s.Cash, fromOnlinePayment: true);
        // ฝ่ายค้าน M3 (268-1-10): เงินออนไลน์/สลิปที่ไม่รู้บัญชี ⇒ ไม่ประทับ (ไม่แต่งเป็นเงินสด)
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Bookings(c3).RecordDepositPaymentAsync(s.CompanyId, s.SiteId, bookingId, new RecordBookingPaymentRequest(10m, DateTime.UtcNow, "T268-3", null), "payment-gateway",
                moneyInAccountId: null, fromOnlinePayment: true);
        using var check = DbTestDatabase.TryCreateContext()!;
        var all = await check.SiteBookingPayments.AsNoTracking().Where(p => p.CompanyId == s.CompanyId && p.BookingId == bookingId).OrderBy(p => p.Amount).ToListAsync();
        Assert.Equal(3, all.Count);
        Assert.False(all[0].ChannelStamped);                // 10 ไม่รู้บัญชี
        var rows = all.Skip(1).ToList();
        Assert.All(rows, r => Assert.True(r.ChannelStamped));
        Assert.Null(rows[0].BankAccountId);                 // 40 ผ่านผู้ให้บริการ ⇒ ผังพักชนะธนาคารที่ส่งมา
        Assert.Equal(s.Cash, rows[0].MoneyInAccountId);
        Assert.Equal(s.BankB, rows[1].BankAccountId);       // 60 โอนเข้ากรุงเทพ
        Assert.Null(rows[1].MoneyInAccountId);
    }

    [Fact]
    public async Task C268_1_10_ใบเสร็จมัดจำอนุมัติทางอื่น_hook_หลังอนุมัติย้ายเงินงวดก่อน_หมายเหตุบนการจอง_รันซ้ำไม่ซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (bookingId, docId, ids) = await BookingAsync(db, s, (60m, true, s.BankB), (90m, true, s.BankA));
        var etax = F4Proxy<IEtaxInvoiceService>.Create((_, _) => null);
        for (var i = 0; i < 2; i++)
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            await new IssuedDocumentHooks(c, etax, NullLogger<IssuedDocumentHooks>.Instance).RunAsync(s.CompanyId, docId);   // ตัวเดียวกับหลังอนุมัติทุกทาง
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.JournalEntries.CountAsync(j => j.CompanyId == s.CompanyId && j.Reference == BookingDepositInstalmentChannel.ReferenceFor(ids[0])));
        var lines = await MoveLinesAsync(check, s.CompanyId, docId);
        Assert.Equal(60m, lines.Single(l => l.AccountId == s.GlB).DebitAmount);
        Assert.Contains(BookingDepositInstalmentChannel.RuleCode, (await check.SiteBookings.AsNoTracking().SingleAsync(b => b.Id == bookingId)).InternalNotes);
        // ทิศตรงข้าม: ใบที่ไม่ใช่ใบมัดจำของการจอง ⇒ hook ไม่แตะ
        var other = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Receipt, IsDeposit = false, Status = DocumentStatus.Approved,
            DocumentNumber = "RC268-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, TotalAmount = 10m, Currency = "THB",
        };
        check.Documents.Add(other);
        await check.SaveChangesAsync();
        await new IssuedDocumentHooks(check, etax, NullLogger<IssuedDocumentHooks>.Instance).RunAsync(s.CompanyId, other.Id);
        Assert.Empty(await MoveLinesAsync(check, s.CompanyId, other.Id));
    }
}
