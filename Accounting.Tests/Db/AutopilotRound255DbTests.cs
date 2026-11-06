using Accounting.Helpers;
using Accounting.Models.Constants;
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
/// รอบ 255 WP-12B ข้อ 1 — แจ้งโอนจากหน้าชำระของเอกสารผ่านประตูเดียว <see cref="SlipEvidenceService"/> บน PostgreSQL:
/// สลิป = รายการรอยืนยัน (ไม่มีแถว Payment) · ไฟล์แนบบอกช่องทาง · ด่านสลิปรอตรวจ/เพดานต่อชั่วโมงนับจากฐาน · บริษัทอื่นใช้รหัสใบของเราไม่ได้
/// <para>seed pitfalls: บริษัทใหม่ทุกเทสต์ (ฐานร่วม) · เลขเอกสารสุ่ม · ที่เก็บไฟล์ปลอม (เส้นที่เก็บจริงมีเทสต์ของตัวเอง)</para>
/// </summary>
[Trait("Category", "Db")]
public class AutopilotRound255DbTests
{
    private readonly ITestOutputHelper _out;
    public AutopilotRound255DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private sealed class FakeNotify : INotificationEngine
    {
        public List<string> Keys { get; } = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context) { Keys.Add(eventKey); return Task.CompletedTask; }
    }

    private static SlipEvidenceService Slips(Accounting.Data.AccountingDbContext db, FakeNotify notify)
    {
        var storage = F4Proxy<Accounting.Services.Storage.IFileStorage>.Create((name, args) =>
        {
            if (name == "get_" + nameof(Accounting.Services.Storage.IFileStorage.Locator))
            {
                var root = Path.GetTempPath();
                return new Accounting.Services.Storage.FileStorageLocator(root, root, root, root);
            }
            if (name == nameof(Accounting.Services.Storage.IFileStorage.WriteAsync)) return Task.CompletedTask;
            if (name == nameof(Accounting.Services.Storage.IFileStorage.DeleteAsync)) return Task.FromResult(true);
            return null;
        });
        var intents = new Accounting.Services.Payments.PaymentIntentService(db,
            new Accounting.Services.Payments.IPaymentProvider[] { new Accounting.Services.Payments.Providers.ManualSlipPaymentProvider() },
            Array.Empty<Accounting.Services.Payments.IPaymentCompletionHandler>(),
            NullLogger<Accounting.Services.Payments.PaymentIntentService>.Instance);
        return new SlipEvidenceService(db, intents, storage, null, notify);
    }

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid InvoiceId, Guid OtherCompanyId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัท 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000007", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000015", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้าโอน จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson, TaxId = "0107537000017",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "INV255" + Random.Shared.Next(100000, 999999),
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date.AddDays(-3), SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
            PaidAmount = 0m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id, inv.Id, other.Id);
    }

    private static SlipFile Pdf(string tag) => new(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 slip255 " + tag + " " + Guid.NewGuid()), "application/pdf", "slip.pdf");

    [Fact]
    public async Task R255_แจ้งโอนจากหน้าชำระ_เป็นรายการรอยืนยัน_ไม่ใช่รับชำระ_ด่านสลิปรอตรวจนับจากฐาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var notify = new FakeNotify();

        using (var before = DbTestDatabase.TryCreateContext()!)
        {
            var (pending0, hour0) = await Slips(before, notify).PayLinkLoadAsync(s.CompanyId, s.InvoiceId);
            Assert.False(pending0);   // ทิศตรงข้าม: ยังไม่เคยแจ้ง ⇒ ไม่มีด่านใดปิด
            Assert.Equal(0, hour0);
            Assert.Null(SlipEvidenceRule.PayLinkSlipProblem(true, pending0, hour0));
        }

        var r = await Slips(db, notify).SubmitAsync(s.CompanyId, s.InvoiceId, contactId: null, Pdf("a"), 1_070m, "PAYREF 0012345",
            SlipEvidenceService.ChannelPayLink);
        Assert.Equal(PaymentMethodKind.ManualSlip, r.Intent.MethodKind);
        Assert.Equal(1_070m, r.Intent.Amount);
        Assert.Equal("PAYREF0012345", r.SlipRef);
        Assert.Equal(NotificationEvents.AutopilotSlipSubmitted, Assert.Single(notify.Keys));

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Payments.AnyAsync(p => p.DocumentId == s.InvoiceId));   // สลิป ≠ รับชำระ
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == s.InvoiceId);
        Assert.Equal(1_070m, doc.BalanceDue);
        Assert.Equal(DocumentStatus.Approved, doc.Status);
        var att = await check.Set<FileAttachment>().AsNoTracking().SingleAsync(a => a.EntityId == s.InvoiceId && a.CompanyId == s.CompanyId);
        Assert.Equal(SlipEvidenceService.ChannelPayLink, att.CreatedBy);
        Assert.Null(att.UploadedByUserId);

        var (pending, lastHour) = await Slips(check, notify).PayLinkLoadAsync(s.CompanyId, s.InvoiceId);
        Assert.True(pending);
        Assert.Equal(1, lastHour);
        Assert.NotNull(SlipEvidenceRule.PayLinkSlipProblem(true, pending, lastHour));   // หน้าชำระไม่รับสลิปที่สองระหว่างรอตรวจ
    }

    [Fact]
    public async Task R255_บริษัทอื่นใช้รหัสใบของเราแจ้งโอนไม่ได้_ไม่มีแถวใดถูกเขียน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var notify = new FakeNotify();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Slips(db, notify).SubmitAsync(s.OtherCompanyId, s.InvoiceId, null, Pdf("x"), 100m, null,
            SlipEvidenceService.ChannelPayLink));
        // พอร์ทัล: ผู้ติดต่อในโทเคนไม่ใช่เจ้าของใบ ⇒ ไม่พบ
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Slips(db, notify).SubmitAsync(s.CompanyId, s.InvoiceId, Guid.NewGuid(), Pdf("y"), 100m, null,
            SlipEvidenceService.ChannelPortal));
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.PaymentIntents.AnyAsync(i => i.SourceId == s.InvoiceId));
        Assert.Empty(notify.Keys);
    }
}
