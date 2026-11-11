using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้าน 259-D H1 (F2 ข้อ 9 — ของเสียที่ persist แล้วต้องมี migration): ก่อนรอบ 259 กรมตอบไม่สำเร็จแล้วระบบประทับ "Submitted" + เลขส่ง <c>ERR-…</c> ⇒
/// แถวพวกนี้ต้องกลับเป็น Error (ไม่มี SubmittedAt) ให้งาน/ปุ่มส่งใหม่ได้ · ทิศตรงข้าม: แถวที่ส่งถึงกรมจริง (เลขส่งจริง) ไม่ถูกแตะ ·
/// แถวที่กรมตอบสำเร็จแต่ระบบแต่งเลข <c>RD-yyyyMMddHHmmss</c> ล้างเฉพาะเลข (คงสถานะส่งแล้ว) · รันซ้ำได้ผลเดิม
/// </summary>
[Trait("Category", "Db")]
public class EtaxLegacyStampRepairDbTests
{
    private readonly ITestOutputHelper _out;
    public EtaxLegacyStampRepairDbTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task H1_แถวERRกลับเป็นError_แถวเลขจริงไม่ถูกแตะ_แถวRDแต่งเลขล้างเฉพาะเลข_รันซ้ำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var co = new Company { Name = "บริษัททดสอบ e-Tax", TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = co.Id, Name = "ลูกค้าทดสอบ e-Tax", TaxId = "0105556000017", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        Document Doc(string no) => new()
        {
            CompanyId = co.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Approved, DocumentNumber = no,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-20), SubTotal = 1000m, VatAmount = 70m, TotalAmount = 1070m, Currency = "THB", ExchangeRate = 1m,
        };
        var d1 = Doc("TIV-R259-1"); var d2 = Doc("TIV-R259-2"); var d3 = Doc("TIV-R259-3");
        db.Documents.AddRange(d1, d2, d3);
        await db.SaveChangesAsync();
        var stamped = DateTime.UtcNow.AddDays(-10);
        EtaxInvoice E(Document d, string subId) => new()
        {
            CompanyId = co.Id, DocumentId = d.Id, EtaxRefNumber = d.DocumentNumber, Status = EtaxStatus.Submitted,
            SubmittedAt = stamped, SubmissionId = subId, SignedAt = stamped, DigitalSignature = "sig", ErrorCode = "RD_500",
        };
        var fake = E(d1, "ERR-20261101093000");
        var real = E(d2, "RDSUB-8F2A9C");
        var invented = E(d3, "RD-20261101093000");
        real.ErrorCode = null; invented.ErrorCode = null;
        db.EtaxInvoices.AddRange(fake, real, invented);
        await db.SaveChangesAsync();

        for (var run = 0; run < 2; run++)   // idempotent
            foreach (var sql in EtaxRdSubmission.LegacyStampRepairSql())
                await db.Database.ExecuteSqlRawAsync(sql);

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.EtaxInvoices.AsNoTracking().Where(e => e.CompanyId == co.Id).ToDictionaryAsync(e => e.Id);
        var f = rows[fake.Id];
        Assert.Equal(EtaxStatus.Error, f.Status);
        Assert.Null(f.SubmittedAt);
        Assert.Null(f.SubmissionId);
        Assert.Equal(EtaxRdSubmission.ErrorSubmit, f.ErrorCode);
        Assert.True(EtaxRdSubmission.CanSubmit(f.Status, f.SignedAt, f.DigitalSignature, f.ErrorCode));   // ส่งใหม่ได้
        // ทิศตรงข้าม: เลขส่งจริงไม่ถูกแตะ
        var r = rows[real.Id];
        Assert.Equal(EtaxStatus.Submitted, r.Status);
        Assert.NotNull(r.SubmittedAt);
        Assert.Equal("RDSUB-8F2A9C", r.SubmissionId);
        // กรมตอบสำเร็จแต่เลขแต่ง ⇒ ล้างเฉพาะเลข · สถานะส่งแล้วคงไว้
        var i = rows[invented.Id];
        Assert.Equal(EtaxStatus.Submitted, i.Status);
        Assert.NotNull(i.SubmittedAt);
        Assert.Null(i.SubmissionId);
    }
}
