using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 269 — <b>ผู้ใช้รายงาน</b>: รายงานภาษีขาย ก.ย. 2569 ของบริษัทที่ไม่มีขาย 0% (ส่งออก §80/1) เลย ขึ้นหมายเหตุ
/// "ยอดขายที่เสียภาษีอัตราร้อยละ 0 (ส่งออก §80/1) — ช่อง 7 1,235,494.24" ทั้งที่ตาราง §87 มีแต่ใบกำกับ TIV-…
///
/// <para>ต้นเหตุ: <c>TaxService.GenerateVatReport</c> บวก <c>Pp30SalesClassifier.Split</c> ของทุกใบฝั่งขายที่ออกแล้วและมีบรรทัด VatRate 0/-1 ⇒
/// ใบเสร็จรับชำระ ("รับชำระเงินตามใบกำกับภาษีเลขที่ …" บรรทัด VAT 0 ที่ระบบสร้างเอง) · ใบวางบิล · ใบส่งของ · ใบเสนอราคา ·
/// ใบแจ้งหนี้ที่ออกใบกำกับแทนแล้ว กลายเป็น "ยอดขาย 0%" — ตอนนี้ช่อง 7/8 นับเฉพาะใบที่ <see cref="OutputVatOwnership.SalesBoxOwner"/> ตัดสิน</para>
/// <para>ทิศตรงข้าม: ใบกำกับส่งออก 0% จริงนับครั้งเดียว (ใบแจ้งหนี้ที่ถูกแทน + ใบเสร็จรับชำระไม่นับซ้ำ) · ใบแจ้งหนี้ยกเว้น §81 (ออกใบกำกับไม่ได้) ยังเข้าช่อง 8</para>
/// </summary>
[Trait("Category", "Db")]
public class Pp30SalesBoxOwnerDbTests
{
    private readonly ITestOutputHelper _out;
    public Pp30SalesBoxOwnerDbTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime Month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-2);
    private static DateTime Day(int d) => Month.AddDays(d - 1);

    private sealed record Seed(Guid CompanyId, Guid CustomerId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบช่อง 7", TaxId = "0105556000001", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var customer = new Contact { CompanyId = company.Id, Name = "ลูกค้าในประเทศ", IsCustomer = true, TaxId = "0105556000002" };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string number,
        DateTime date, decimal amount, decimal vatRate, Guid? relatedId = null, DocumentStatus status = DocumentStatus.Approved,
        string description = "สินค้า")
    {
        var vat = vatRate > 0 ? Math.Round(amount * vatRate / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentNumber = number, DocumentType = type,
            Status = status, DocumentDate = date, TaxPointDate = date, RelatedDocumentId = relatedId,
            SubTotal = amount, VatAmount = vat, TotalAmount = amount + vat,
        };
        doc.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = description, Quantity = 1, UnitPrice = amount, Amount = amount, VatRate = vatRate, VatAmount = vat,
        });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static decimal Box(TaxReport r, string code) => r.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode == code).Sum(l => l.IncomeAmount);

    [Fact]
    public async Task R269_ใบกำกับ7เปอร์เซ็นต์_ใบเสร็จรับชำระ_ใบวางบิล_ใบส่งของ_ใบเสนอราคา_ช่อง7และ8เป็นศูนย์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        // ใบเสนอราคา → ใบส่งของ → ใบกำกับ 7% → ใบวางบิล → ใบเสร็จรับชำระ (บรรทัด VAT 0 ที่ระบบสร้างเอง — ทรงเดียวกับ DocumentService)
        var qt = await DocAsync(db, s, DocumentType.Quotation, "QT-269-0001", Day(2), 1_154_667.51m, 0m);
        var dn = await DocAsync(db, s, DocumentType.DeliveryNote, "DN-269-0001", Day(3), 1_154_667.51m, 0m, qt.Id);
        var tiv = await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0001", Day(5), 1_154_667.51m, 7m, dn.Id, DocumentStatus.Paid);
        await DocAsync(db, s, DocumentType.BillingNote, "BN-269-0001", Day(6), 1_235_494.24m, 0m, tiv.Id);
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0001", Day(20), 1_235_494.24m, 0m, tiv.Id, DocumentStatus.Paid,
            description: "รับชำระเงินตามใบกำกับภาษีเลขที่ TIV-269-0001");
        // ใบเสนอราคา/ใบวางบิลที่มีบรรทัดยกเว้น (-1) ก็ต้องไม่เข้าช่อง 8
        await DocAsync(db, s, DocumentType.Quotation, "QT-269-0002", Day(7), 900m, -1m);

        using var check = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(check).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);

        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));   // เดิม 1,154,667.51 + 1,154,667.51 + 1,235,494.24 + 1,235,494.24
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptSales));      // เดิม 900
        // แถว §87 มีเฉพาะใบกำกับ — ภาษีขายครั้งเดียว
        var rows = report.Lines.Where(l => !l.IsExcluded && l.DocumentId != null).ToList();
        Assert.Equal(new[] { "TIV-269-0001" }, rows.Select(l => l.Description).ToArray());
        Assert.Equal(80_826.73m, report.OutputVat);
    }

    [Fact]
    public async Task R269_ทิศตรงข้าม_ใบกำกับส่งออก0เปอร์เซ็นต์จริงนับครั้งเดียว_ใบแจ้งหนี้ที่ถูกแทนและใบเสร็จไม่นับซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-269-0002", Day(4), 2_000m, 0m);
        var tiv = await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0002", Day(4), 2_000m, 0m, inv.Id, DocumentStatus.Paid,
            description: "Export goods FOB");
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0002", Day(15), 2_000m, 0m, tiv.Id, DocumentStatus.Paid,
            description: "รับชำระเงินตามใบกำกับภาษีเลขที่ TIV-269-0002");

        using var check = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(check).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);

        Assert.Equal(2_000m, Box(report, VatReportLineKind.ZeroRatedSales));   // เดิม 6,000 (ใบแจ้งหนี้ + ใบกำกับ + ใบเสร็จ)
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptSales));
    }

    [Fact]
    public async Task R269_ทิศตรงข้าม_ใบแจ้งหนี้ยกเว้นภาษี81_เข้าช่อง8ครั้งเดียว_ใบเสร็จรับชำระไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        // ขายยกเว้น §81 ออกใบกำกับไม่ได้ ⇒ ใบแจ้งหนี้ (VAT 0 ทั้งใบ) คือใบที่ถือการขาย
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-269-0003", Day(8), 300m, -1m, status: DocumentStatus.Paid,
            description: "ผักสด (ยกเว้น §81)");
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0003", Day(12), 300m, 0m, inv.Id, DocumentStatus.Paid,
            description: "รับชำระเงินตามใบแจ้งหนี้เลขที่ INV-269-0003");
        await DocAsync(db, s, DocumentType.BillingNote, "BN-269-0003", Day(9), 300m, -1m, inv.Id);

        using var check = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(check).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);

        Assert.Equal(300m, Box(report, VatReportLineKind.ExemptSales));        // เดิม 600 (ใบแจ้งหนี้ + ใบวางบิล)
        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));       // เดิม 300 (ใบเสร็จรับชำระ VAT 0)
    }
}
