using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 269 — <b>ผู้ใช้รายงาน</b>: รายงานภาษีขาย ก.ย. 2569 ของบริษัทที่ไม่มีขาย 0% (ส่งออก §80/1) เลย ขึ้นหมายเหตุ
/// "ยอดขายที่เสียภาษีอัตราร้อยละ 0 (ส่งออก §80/1) 1,235,494.24" ทั้งที่ตาราง §87 มีแต่ใบกำกับ TIV-…
///
/// <para>ต้นเหตุ: <c>TaxService.GenerateVatReport</c> บวก <c>Pp30SalesClassifier.Split</c> ของทุกใบฝั่งขายที่ออกแล้วและมีบรรทัด VatRate 0/-1 ⇒
/// ใบเสร็จรับชำระ ("รับชำระเงินตามใบกำกับภาษีเลขที่ …" บรรทัด VAT 0 ที่ระบบสร้างเอง) · ใบวางบิล · ใบส่งของ · ใบเสนอราคา ·
/// ใบแจ้งหนี้ที่ออกใบกำกับแทนแล้ว · ลูกหนี้ยกมา (OB-AR) · ใบเสร็จจากใบวางบิลรวม · ใบสำคัญรับเดี่ยว กลายเป็น "ยอดขาย 0%" —
/// ตอนนี้ช่อง 0%/ยกเว้นนับเฉพาะใบที่ <see cref="OutputVatOwnership.SalesBoxOwner"/> ตัดสิน (ใบกำกับ หรือใบเจ้าของที่มี VAT)</para>
/// <para>ทิศตรงข้าม: ใบกำกับส่งออก 0% จริงนับครั้งเดียว · ใบกำกับที่มีบรรทัดยกเว้นยังเข้าช่องยกเว้น · ใบสำคัญรับ/ใบวางบิลจากใบเสนอราคาที่มี VAT ยังมีแถว §87</para>
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
        var company = new Company { Name = "บริษัททดสอบช่อง 0%", TaxId = "0105556000001", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var customer = new Contact { CompanyId = company.Id, Name = "ลูกค้าในประเทศ", IsCustomer = true, TaxId = "0105556000002" };
        db.Contacts.Add(customer);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id);
    }

    private static async Task<Document> DocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string number,
        DateTime date, decimal amount, decimal vatRate, Guid? relatedId = null, DocumentStatus status = DocumentStatus.Approved,
        string description = "สินค้า", bool openingBalance = false, Guid? lineSourceId = null,
        (decimal Amount, decimal VatRate)? extraLine = null)
    {
        static decimal Vat(decimal a, decimal r) => r > 0 ? Math.Round(a * r / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
        var vat = Vat(amount, vatRate) + (extraLine is { } x ? Vat(x.Amount, x.VatRate) : 0m);
        var sub = amount + (extraLine?.Amount ?? 0m);
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.CustomerId, DocumentNumber = number, DocumentType = type,
            Status = status, DocumentDate = date, TaxPointDate = date, RelatedDocumentId = relatedId,
            SubTotal = sub, VatAmount = vat, TotalAmount = sub + vat, IsOpeningBalance = openingBalance,
        };
        doc.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = description, Quantity = 1, UnitPrice = amount, Amount = amount, VatRate = vatRate,
            VatAmount = Vat(amount, vatRate), SourceDocumentId = lineSourceId,
        });
        if (extraLine is { } e)
            doc.Lines.Add(new DocumentLine
            {
                LineOrder = 2, Description = "รายการที่สอง", Quantity = 1, UnitPrice = e.Amount, Amount = e.Amount, VatRate = e.VatRate,
                VatAmount = Vat(e.Amount, e.VatRate),
            });
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static decimal Box(TaxReport r, string code) => r.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode == code).Sum(l => l.IncomeAmount);
    private static string[] Rows(TaxReport r) => r.Lines.Where(l => !l.IsExcluded && l.DocumentId != null).Select(l => l.Description ?? "").ToArray();
    private static Dictionary<Pp30FormBox, decimal> Form(TaxReport r)
        => Pp30FormBoxes.Figures(r.Lines.Select(l => (l.IncomeTypeCode, l.IncomeAmount, l.IsExcluded)), r.OutputVat, r.InputVat, r.NetVat).ToDictionary(x => x.Box, x => x.Amount);

    private static async Task<TaxReport> Pp30Async(Seed s)
    {
        using var check = DbTestDatabase.TryCreateContext()!;
        return await new TaxService(check).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);
    }

    [Fact]
    public async Task R269_ใบกำกับ7เปอร์เซ็นต์_ใบเสร็จรับชำระ_ใบวางบิล_ใบส่งของ_ใบเสนอราคา_ช่อง0และยกเว้นเป็นศูนย์()
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
        await DocAsync(db, s, DocumentType.Quotation, "QT-269-0002", Day(7), 900m, -1m);
        // L1: ใบส่งของบรรทัดยกเว้น (ชนิดสองฝั่ง) ต้องไม่ไปเป็น "ยอดซื้อยกเว้น"
        await DocAsync(db, s, DocumentType.DeliveryNote, "DN-269-0002", Day(8), 900m, -1m);

        var report = await Pp30Async(s);
        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptSales));
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptPurchases));
        Assert.Equal(new[] { "TIV-269-0001" }, Rows(report));
        Assert.Equal(80_826.73m, report.OutputVat);
        Assert.Equal(1_154_667.51m, Form(report)[Pp30FormBox.TaxableSales]);   // เดิม −80,826.73 (1,154,667.51 − 1,235,494.24)
    }

    [Fact]
    public async Task R269_ทิศตรงข้าม_ใบกำกับส่งออก0เปอร์เซ็นต์จริงนับครั้งเดียว_ยอดขายที่ต้องเสียภาษีไม่ถูกหัก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-269-0002", Day(4), 2_000m, 0m);
        var tiv = await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0002", Day(4), 2_000m, 0m, inv.Id, DocumentStatus.Paid,
            description: "Export goods FOB");
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0002", Day(15), 2_000m, 0m, tiv.Id, DocumentStatus.Paid,
            description: "รับชำระเงินตามใบกำกับภาษีเลขที่ TIV-269-0002");
        await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0003", Day(6), 10_000m, 7m, status: DocumentStatus.Paid);

        var report = await Pp30Async(s);
        Assert.Equal(2_000m, Box(report, VatReportLineKind.ZeroRatedSales));   // เดิม 6,000 (ใบแจ้งหนี้ + ใบกำกับ + ใบเสร็จ)
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptSales));
        var f = Form(report);
        Assert.Equal(10_000m, f[Pp30FormBox.TaxableSales]);
        Assert.Equal(12_000m, f[Pp30FormBox.TotalSales]);
    }

    [Fact]
    public async Task R269_M1_ใบแจ้งหนี้ไม่มีVAT_ไม่เข้าช่อง0และยกเว้น_มีบรรทัดเตือน_ยอดขายที่ต้องเสียภาษีไม่ถูกหัก_ใบกำกับที่มีบรรทัดยกเว้นยังนับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0004", Day(3), 10_000m, 7m, status: DocumentStatus.Paid);
        // ใบแจ้งหนี้ส่งออก/ยกเว้นที่ไม่ออกใบกำกับ (VAT 0 ทั้งใบ) — ไม่นับเข้าแบบเงียบ ๆ แต่มีบรรทัดเตือนที่ติ๊กออก
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-269-0004", Day(8), 2_000m, 0m, status: DocumentStatus.Paid);
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0004", Day(12), 2_000m, 0m, inv.Id, DocumentStatus.Paid,
            description: "รับชำระเงินตามใบแจ้งหนี้เลขที่ INV-269-0004");
        // ทิศตรงข้าม: ใบกำกับที่มีบรรทัดยกเว้น (บิลผสม) — ยกเว้น 300 เข้าช่องยกเว้น
        await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0005", Day(9), 1_000m, 7m, status: DocumentStatus.Paid,
            extraLine: (300m, -1m));

        var report = await Pp30Async(s);
        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));
        Assert.Equal(300m, Box(report, VatReportLineKind.ExemptSales));
        var warn = Assert.Single(report.Lines, l => l.IsExcluded && l.DocumentId == inv.Id);
        Assert.Equal(2_000m, warn.IncomeAmount);
        Assert.StartsWith("⚠️", warn.Description ?? "");
        var f = Form(report);
        Assert.Equal(11_000m, f[Pp30FormBox.TaxableSales]);   // 10,000 + 1,000 — ไม่ถูกหัก 2,000 ของใบที่ไม่มีแถว
        Assert.Equal(300m, f[Pp30FormBox.ExemptSales]);
    }

    [Fact]
    public async Task R269_H1_ลูกหนี้ยกมาไม่เข้าแบบทั้งสองฝั่ง_ทิศตรงข้ามใบกำกับ0เปอร์เซ็นต์ปกติยังนับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        // ทรงเดียวกับ ImportExportService (Opening AR): Invoice · Approved · IsOpeningBalance · บรรทัดเดียว VatRate 0 = ยอดค้างรวม VAT
        await DocAsync(db, s, DocumentType.Invoice, "OB-AR:TIV-0925", Day(25), 1_235_494.24m, 0m, openingBalance: true,
            description: "ยอดยกมา TIV-0925");
        // เจ้าหนี้ยกมา (ฝั่งซื้อ) บรรทัดยกเว้น — ต้องไม่ไปเป็นยอดซื้อยกเว้น
        await DocAsync(db, s, DocumentType.PurchaseInvoice, "OB-AP:PI-0925", Day(25), 500m, -1m, openingBalance: true);
        await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0006", Day(10), 700m, 0m, status: DocumentStatus.Paid);

        var report = await Pp30Async(s);
        Assert.Equal(700m, Box(report, VatReportLineKind.ZeroRatedSales));     // เดิม 1,236,194.24
        Assert.Equal(0m, Box(report, VatReportLineKind.ExemptPurchases));
        Assert.DoesNotContain(report.Lines, l => (l.Description ?? "").Contains("OB-A"));   // ไม่มีแถว §87 และไม่มีบรรทัดเตือนของใบยอดยกมา
    }

    [Fact]
    public async Task R269_H2_ใบเสร็จจากใบวางบิลรวม_และใบสำคัญรับเดี่ยวไม่มีVAT_ไม่เข้าช่อง0และไม่มีแถว87_ทิศตรงข้ามมีVATยังมีแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        var tiv = await DocAsync(db, s, DocumentType.TaxInvoice, "TIV-269-0007", Day(5), 1_154_667.51m, 7m, status: DocumentStatus.Paid);
        // ใบวางบิลรวม (บรรทัดอ้างใบกำกับ = BillingNoteKind.IsRollup) → ใบเสร็จที่แปลงก่อนปิดทาง 2026-10-08
        var bn = await DocAsync(db, s, DocumentType.BillingNote, "BN-269-0007", Day(6), 1_235_494.24m, 0m, lineSourceId: tiv.Id);
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0007", Day(18), 1_235_494.24m, 0m, bn.Id, DocumentStatus.Paid);
        // ใบสำคัญรับเดี่ยวจากสลิปรับโอน (OCR) — VAT 0
        await DocAsync(db, s, DocumentType.ReceiptVoucher, "RV-269-0007", Day(19), 1_235_494.24m, 0m, status: DocumentStatus.Paid,
            description: "รับโอนจากลูกค้า");
        // ทิศตรงข้าม: ใบสำคัญรับเดี่ยวขายสดที่มี VAT = เจ้าของภาษีขาย
        await DocAsync(db, s, DocumentType.ReceiptVoucher, "RV-269-0008", Day(21), 1_000m, 7m, status: DocumentStatus.Paid);

        var report = await Pp30Async(s);
        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));       // เดิม 2,470,988.48 (+ ใบวางบิล)
        var rows = Rows(report);
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, r => r == "TIV-269-0007");
        Assert.Contains(rows, r => r.StartsWith("RV-269-0008"));      // ป้าย "ไม่ใช่ใบกำกับเต็มรูป" ต่อท้ายได้
        Assert.Equal(80_826.73m + 70m, report.OutputVat);
        // 269-2-9: ใบสำคัญรับสลิป (บรรทัด 0) ⇒ บรรทัดเตือน ไม่ใช่ช่อง 2
        Assert.Contains(report.Lines, l => l.IsExcluded && (l.Description ?? "").StartsWith("⚠️") && (l.Description ?? "").Contains("RV-269-0007"));
    }

    [Fact]
    public async Task R269_2_9_ขายยกเว้น81บนใบแจ้งหนี้ตรงและใบเสร็จขายสด_เข้าช่อง3ครั้งเดียว_ใบรับชำระและยอดยกมาไม่นับ_ใบสำคัญรับ0เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);

        // ใบแจ้งหนี้ตรงขายผักสด (ยกเว้น §81 — ออกใบกำกับไม่ได้) + ใบเสร็จรับชำระของใบนั้น (บรรทัด −1 คัดลอกมา) ⇒ นับที่ใบแจ้งหนี้ครั้งเดียว
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-269-0009", Day(4), 300m, -1m, status: DocumentStatus.Paid,
            description: "ผักสด (ยกเว้น §81)");
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0009", Day(14), 300m, -1m, inv.Id, DocumentStatus.Paid);
        // ใบเสร็จขายสดยกเว้น (ไม่อ้างใบใด) ⇒ นับ
        await DocAsync(db, s, DocumentType.Receipt, "REC-269-0010", Day(15), 400m, -1m, status: DocumentStatus.Paid);
        // ใบสำคัญรับขายสดยกเว้น ⇒ นับ
        await DocAsync(db, s, DocumentType.ReceiptVoucher, "RV-269-0010", Day(16), 50m, -1m, status: DocumentStatus.Paid);
        // ยอดยกมาบรรทัดยกเว้น ⇒ ไม่นับ
        await DocAsync(db, s, DocumentType.Invoice, "OB-AR:INV-0801", Day(17), 900m, -1m, openingBalance: true);
        // ใบวางบิลของใบแจ้งหนี้ยกเว้น ⇒ ไม่นับ
        await DocAsync(db, s, DocumentType.BillingNote, "BN-269-0009", Day(5), 300m, -1m, inv.Id);
        // ใบสำคัญรับสลิป (บรรทัด 0) ⇒ ไม่เข้าช่อง 2 · เตือน
        var rv = await DocAsync(db, s, DocumentType.ReceiptVoucher, "RV-269-0011", Day(18), 1_000m, 0m, status: DocumentStatus.Paid,
            description: "รับโอนจากลูกค้า");

        var report = await Pp30Async(s);
        Assert.Equal(750m, Box(report, VatReportLineKind.ExemptSales));        // 300 + 400 + 50
        Assert.Equal(0m, Box(report, VatReportLineKind.ZeroRatedSales));
        Assert.Equal(750m, Form(report)[Pp30FormBox.ExemptSales]);
        var warn = Assert.Single(report.Lines, l => l.IsExcluded && l.DocumentId == rv.Id);
        Assert.Equal(1_000m, warn.IncomeAmount);
        Assert.DoesNotContain(report.Lines, l => l.IsExcluded && l.DocumentId == inv.Id);   // ยอดยกเว้นนับแล้ว ไม่มีบรรทัดเตือน
    }
}
