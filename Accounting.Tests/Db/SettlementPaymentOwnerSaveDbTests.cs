using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 201 ทีม ST (A-ST1 · ฝ่ายค้าน ST-X2) — <b>ตาข่าย <c>SavingChanges</c> ของ <see cref="SettlementPaymentOwner.StampOnSave"/></b> บน PostgreSQL จริง
/// (ฝ่ายค้าน ภ.ง.ด.1/Tb P2-10)
///
/// <para>ที่มา: ce448d56 ย้ายเทสต์ ST1 ให้เพิ่มแถว<b>ใน</b>ขอบเขต (ตัวฟัง Tracked) เพราะตั้งแต่ 9f226f15 SaveChanges เปิดธุรกรรม (= เชื่อมต่อฐาน)
/// ก่อนยิง SavingChanges ⇒ บนฐานออฟไลน์ไปไม่ถึงตาข่าย · เส้น "แถวถูกแนบก่อนเปิดขอบเขต" (ตาข่าย) จึงไม่มีเทสต์ใดยืนยัน — ไฟล์นี้ปิดช่องนั้น:
/// แถวที่ถูกเพิ่ม<b>ก่อน</b>เปิดขอบเขตต้องได้เจ้าของใน SaveChanges เดียวกับ INSERT (อ่านกลับจากฐาน) · ทิศตรงข้าม: แถวของใบอื่น/แถวที่มีเจ้าของแล้ว/
/// SaveChanges หลังปิดขอบเขต ไม่ถูกแตะ</para>
/// </summary>
[Trait("Category", "Db")]
public class SettlementPaymentOwnerSaveDbTests
{
    private readonly ITestOutputHelper _out;
    public SettlementPaymentOwnerSaveDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid DocX, Guid DocY)> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบเจ้าของการรับชำระ", TaxId = "0105561012346", BranchCode = "00000" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var customer = new Contact { CompanyId = company.Id, Name = "ลูกค้าทดสอบ", ContactType = ContactType.Individual, IsCustomer = true };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        Document Inv(string no) => new()
        {
            CompanyId = company.Id, ContactId = customer.Id, DocumentType = DocumentType.Invoice, DocumentNumber = no, Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 100m, TotalAmount = 100m, BalanceDue = 100m, Currency = "THB",
        };
        var x = Inv("INV-ST1-X-" + Guid.NewGuid().ToString("N")[..6]);
        var y = Inv("INV-ST1-Y-" + Guid.NewGuid().ToString("N")[..6]);
        db.Documents.AddRange(x, y);
        await db.SaveChangesAsync();
        return (company.Id, x.Id, y.Id);
    }

    private static Payment Pay(Guid companyId, Guid docId, string no, Guid? owner = null) => new()
    {
        CompanyId = companyId, DocumentId = docId, PaymentNumber = no, PaymentDate = DateTime.UtcNow.Date, Amount = 10m,
        PaymentMethod = PaymentMethod.BankTransfer, SettlementBatchId = owner,
    };

    [Fact]
    public async Task ตาข่ายSavingChanges_แถวที่แนบก่อนเปิดขอบเขต_ได้เจ้าของในSaveChangesเดียวกับINSERT_บนฐานจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, docX, docY) = await SeedAsync(db);
        var batchA = Guid.NewGuid();
        var batchB = Guid.NewGuid();

        using var req = DbTestDatabase.TryCreateContext()!;
        // แนบ "ก่อน" เปิดขอบเขต ⇒ ตัวฟัง Tracked ไม่เห็น · เหลือตาข่าย SavingChanges ตัวเดียว
        var early = Pay(cid, docX, "PAY-ST1-EARLY");
        var otherDoc = Pay(cid, docY, "PAY-ST1-OTHER");
        var owned = Pay(cid, docX, "PAY-ST1-OWNED", owner: batchB);
        req.Payments.AddRange(early, otherDoc, owned);
        Assert.Null(early.SettlementBatchId);
        using (SettlementPaymentOwner.StampOnSave(req, batchA, docX))
            await req.SaveChangesAsync();

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.Payments.AsNoTracking()
            .Where(p => p.CompanyId == cid)
            .ToDictionaryAsync(p => p.PaymentNumber, p => p.SettlementBatchId);
        Assert.Equal(batchA, rows["PAY-ST1-EARLY"]);    // ตาข่ายประทับใน SaveChanges เดียวกับ INSERT
        Assert.Null(rows["PAY-ST1-OTHER"]);             // ทิศตรงข้าม: ใบอื่นไม่ถูกแตะ
        Assert.Equal(batchB, rows["PAY-ST1-OWNED"]);    // ทิศตรงข้าม: ห้ามย้ายเจ้าของ
    }

    [Fact]
    public async Task ทิศตรงข้าม_SaveChangesหลังปิดขอบเขต_ไม่มีใครประทับ_บนฐานจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, docX, _) = await SeedAsync(db);

        using var req = DbTestDatabase.TryCreateContext()!;
        var late = Pay(cid, docX, "PAY-ST1-LATE");
        req.Payments.Add(late);
        using (SettlementPaymentOwner.StampOnSave(req, Guid.NewGuid(), docX)) { }   // เปิดแล้วปิดโดยไม่ save
        await req.SaveChangesAsync();                                              // เส้นรับชำระทั่วไปหลังขอบเขต

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null(await check.Payments.AsNoTracking()
            .Where(p => p.CompanyId == cid && p.PaymentNumber == "PAY-ST1-LATE")
            .Select(p => p.SettlementBatchId).SingleAsync());
    }
}
