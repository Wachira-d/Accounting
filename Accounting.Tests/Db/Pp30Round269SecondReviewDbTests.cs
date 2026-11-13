using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 269 ฝ่ายค้านชุดสอง (`erp-review/2026-11-13/review-269-2.md` · คำตัดสิน 269-3-*) บน PostgreSQL จริง —
/// H1 เปิดหน้าไม่ลบบรรทัดที่ดึงเข้า (ภาษีซื้อ 7,000) · สร้างใหม่คืนบรรทัด + audit ผู้กดจริง + ช่อง ภ.พ.30 ·
/// M1 ติ๊กแถว 0% ออกแล้วช่อง 2/4 ถูก · M2 ใบลดหนี้ของใบเสร็จขายสดยกเว้นลดช่อง 3 (ทิศตรงข้าม ใบรับชำระไม่ลด) ·
/// M5 แปลงเป็นใบกำกับบางส่วน/ใบกำกับร่าง ช่อง 3 ไม่หาย · L2 ปัดเศษ POS ไม่ใช่ 0% · M6 ตัวทบทวนบอกรุ่นกติกา
/// </summary>
[Trait("Category", "Db")]
public class Pp30Round269SecondReviewDbTests
{
    private readonly ITestOutputHelper _out;
    public Pp30Round269SecondReviewDbTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime ThisMonth = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
    private static readonly DateTime Month = ThisMonth.AddMonths(-3);
    private static DateTime Day(int d) => Month.AddDays(d - 1);

    private sealed record Seed(Guid CompanyId, Guid CustomerId, Guid SupplierId);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบ 269 ชุดสอง", TaxId = "0105556000001", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var customer = new Contact { CompanyId = company.Id, Name = "ลูกค้า", IsCustomer = true, TaxId = "0105556000002" };
        var supplier = new Contact { CompanyId = company.Id, Name = "ผู้ขาย", IsSupplier = true, TaxId = "0105556000011" };
        db.Contacts.AddRange(customer, supplier);
        await db.SaveChangesAsync();
        return new Seed(company.Id, customer.Id, supplier.Id);
    }

    private static Document Doc(Seed s, DocumentType type, string no, DateTime date, params (decimal Amount, decimal Rate, string Desc)[] lines)
    {
        static decimal Vat(decimal a, decimal r) => r > 0 ? Math.Round(a * r / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
        var sub = lines.Sum(l => l.Amount);
        var vat = lines.Sum(l => Vat(l.Amount, l.Rate));
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = DocumentSide.IsPurchase(type) ? s.SupplierId : s.CustomerId, DocumentNumber = no, DocumentType = type,
            Status = DocumentStatus.Approved, DocumentDate = date, TaxPointDate = date, SubTotal = sub, VatAmount = vat, TotalAmount = sub + vat,
        };
        var n = 1;
        foreach (var l in lines)
            d.Lines.Add(new DocumentLine { LineOrder = n++, Description = l.Desc, Quantity = 1, UnitPrice = l.Amount, Amount = l.Amount,
                VatRate = l.Rate, VatAmount = Vat(l.Amount, l.Rate), IsVatClaimable = true });
        return d;
    }

    private static decimal Box(TaxReport r, string code) => r.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode == code).Sum(l => l.IncomeAmount);
    private static Dictionary<Pp30FormBox, decimal> Form(TaxReport r)
        => Pp30FormBoxes.Figures(r.Lines.Select(l => (l.IncomeTypeCode, l.IncomeAmount, l.TaxAmount, l.IsExcluded)), r.OutputVat, r.InputVat)
            .ToDictionary(x => x.Box, x => x.Amount);

    private static async Task<TaxReport> LoadAsync(Guid companyId, Guid reportId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await c.TaxReports.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == reportId && r.CompanyId == companyId);
    }

    [Fact]
    public async Task R269_3_H1_เปิดหน้าไม่ลบบรรทัดภาษีซื้อที่ดึงเข้า7000_สร้างใหม่คืนบรรทัด_auditผู้กดจริงและช่องภพ30()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        // ใบกำกับซื้อมาช้า (3 เดือนก่อน · VAT 7,000) — ดึงเข้าร่างเดือนนี้
        var pi = Doc(s, DocumentType.PurchaseInvoice, "PI-269-LATE", Day(10), (100_000m, 7m, "วัตถุดิบ"));
        db.Documents.Add(pi);
        await db.SaveChangesAsync();
        TaxReportResponse draft;
        using (var c = DbTestDatabase.TryCreateContext()!)
            draft = await new TaxService(c).GenerateTaxReportAsync(s.CompanyId, new CreateTaxReportRequest(TaxType.VAT, ThisMonth.Year, ThisMonth.Month));
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new TaxService(c).PullDocumentIntoReportAsync(s.CompanyId, draft.Id, pi.Id, Guid.NewGuid(), "puller@test");
        Assert.Equal(7_000m, (await LoadAsync(s.CompanyId, draft.Id)).InputVat);

        // เปิดหน้า tax.html = AutoRefresh — ต้องไม่ลบ/สร้างทับร่างเดิม
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new TaxService(c).AutoRefreshReportsAsync(s.CompanyId, 2);
        var afterOpen = await LoadAsync(s.CompanyId, draft.Id);   // Id เดิม = ไม่ถูกลบ-สร้าง
        Assert.Equal(7_000m, afterOpen.InputVat);
        Assert.Contains(afterOpen.Lines, l => l.DocumentId == pi.Id && !l.IsExcluded && l.TaxAmount == 7_000m);
        // ร่างที่มีแต่บรรทัดดึงด้วยมือไม่ถูกนับว่าล้าสมัย
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(await new TaxService(c).GetStaleDraftReportsAsync(s.CompanyId, 2), r => r.ReportId == draft.Id);

        // ผู้ใช้กด "สร้างใหม่" — บรรทัดดึงกลับมา · audit มีผู้กดจริง + ช่อง ภ.พ.30
        var actor = Guid.NewGuid();
        TaxReportResponse regen;
        using (var c = DbTestDatabase.TryCreateContext()!)
            regen = await new TaxService(c).RegenerateTaxReportAsync(s.CompanyId, draft.Id, actor, "presser@test");
        var after = await LoadAsync(s.CompanyId, regen.Id);
        Assert.Equal(7_000m, after.InputVat);
        using var check = DbTestDatabase.TryCreateContext()!;
        var audit = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.EntityId == regen.Id.ToString() && a.UserId == actor).ToListAsync();
        var row = Assert.Single(audit);
        Assert.Equal("presser@test", row.UserEmail);
        Assert.Contains("pp30Boxes", row.NewValues);
        Assert.Contains(Pp30SalesBoxReview.RuleCode, row.NewValues);
    }

    [Fact]
    public async Task R269_3_H1_ร่างล้าสมัยขึ้นแถบเมื่อเอกสารเปลี่ยน_ทิศตรงข้ามไม่เปลี่ยนไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.Documents.Add(Doc(s, DocumentType.TaxInvoice, "TIV-269-S1", ThisMonth, (1_000m, 7m, "สินค้า")));
        await db.SaveChangesAsync();
        TaxReportResponse draft;
        using (var c = DbTestDatabase.TryCreateContext()!)
            draft = await new TaxService(c).GenerateTaxReportAsync(s.CompanyId, new CreateTaxReportRequest(TaxType.VAT, ThisMonth.Year, ThisMonth.Month));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.DoesNotContain(await new TaxService(c).GetStaleDraftReportsAsync(s.CompanyId, 2), r => r.ReportId == draft.Id);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            c.Documents.Add(Doc(s, DocumentType.TaxInvoice, "TIV-269-S2", ThisMonth, (2_000m, 7m, "สินค้า")));
            await c.SaveChangesAsync();
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var stale = Assert.Single(await new TaxService(c).GetStaleDraftReportsAsync(s.CompanyId, 2), r => r.ReportId == draft.Id);
            Assert.Equal(1, stale.AddedDocuments);
            Assert.Contains("สร้างใหม่", stale.Message);
        }
        Assert.Equal(1, (await LoadAsync(s.CompanyId, draft.Id)).Lines.Count(l => l.DocumentId != null));   // ตรวจอย่างเดียว ไม่แก้ร่าง
    }

    [Fact]
    public async Task R269_3_M1_ติ๊กแถวใบส่งออก0ออก_ช่อง2ลด_ช่อง4ไม่เปลี่ยน_ติ๊กกลับได้คืน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        db.Documents.AddRange(
            Doc(s, DocumentType.TaxInvoice, "TIV-269-M1A", Day(5), (100_000m, 7m, "สินค้า")),
            Doc(s, DocumentType.TaxInvoice, "TIV-269-M1B", Day(6), (50_000m, 0m, "Export FOB")));
        await db.SaveChangesAsync();
        TaxReportResponse rep;
        using (var c = DbTestDatabase.TryCreateContext()!)
            rep = await new TaxService(c).GenerateTaxReportAsync(s.CompanyId, new CreateTaxReportRequest(TaxType.VAT, Month.Year, Month.Month));
        var r0 = await LoadAsync(s.CompanyId, rep.Id);
        Assert.Equal((50_000m, 100_000m), (Form(r0)[Pp30FormBox.ZeroRatedSales], Form(r0)[Pp30FormBox.TaxableSales]));
        var exportRow = r0.Lines.Single(l => l.Description == "TIV-269-M1B");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new TaxService(c).UpdateTaxReportAsync(s.CompanyId, rep.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(exportRow.Id, null, null, null, null, Excluded: true) }));
        var r1 = await LoadAsync(s.CompanyId, rep.Id);
        Assert.Equal(0m, Form(r1)[Pp30FormBox.ZeroRatedSales]);
        Assert.Equal(100_000m, Form(r1)[Pp30FormBox.TaxableSales]);   // เดิม 50,000 (100,000 − ช่อง 2 ที่ค้าง)
        Assert.Equal(100_000m, Form(r1)[Pp30FormBox.TotalSales]);
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new TaxService(c).UpdateTaxReportAsync(s.CompanyId, rep.Id,
                new UpdateTaxReportRequest(null, new List<UpdateTaxReportLineRequest> { new(exportRow.Id, null, null, null, null, Excluded: false) }));
        Assert.Equal(50_000m, Box(await LoadAsync(s.CompanyId, rep.Id), VatReportLineKind.ZeroRatedSales));
    }

    [Fact]
    public async Task R269_3_M2_ใบลดหนี้ของใบเสร็จขายสดยกเว้นลดช่อง3_ทิศตรงข้ามใบลดหนี้ของใบรับชำระไม่ลด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var cash = Doc(s, DocumentType.Receipt, "REC-269-M2", Day(5), (10_000m, -1m, "ผักสด"));
        var inv = Doc(s, DocumentType.Invoice, "INV-269-M2", Day(6), (4_000m, -1m, "ผักสด"));
        db.Documents.AddRange(cash, inv);
        await db.SaveChangesAsync();
        var settle = Doc(s, DocumentType.Receipt, "REC-269-M2S", Day(7), (4_000m, -1m, "รับชำระเงินตามใบแจ้งหนี้"));
        settle.RelatedDocumentId = inv.Id;
        var cn = Doc(s, DocumentType.CreditNote, "CN-269-M2", Day(8), (2_000m, -1m, "คืนสินค้า"));
        cn.RelatedDocumentId = cash.Id;
        db.Documents.AddRange(settle, cn);
        await db.SaveChangesAsync();
        // ใบลดหนี้ที่อ้างใบรับชำระ (ทางที่ DocumentService กันไว้ — สร้างตรงในฐานเพื่อล็อกทิศตรงข้าม)
        var cnSettle = Doc(s, DocumentType.CreditNote, "CN-269-M2S", Day(9), (1_000m, -1m, "ลดหนี้"));
        cnSettle.RelatedDocumentId = settle.Id;
        db.Documents.Add(cnSettle);
        await db.SaveChangesAsync();

        using var c = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(c).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);
        Assert.Equal(10_000m + 4_000m - 2_000m, Box(report, VatReportLineKind.ExemptSales));   // เดิม 14,000 (ใบลดหนี้ของใบเสร็จขายสดไม่ลด)
    }

    [Fact]
    public async Task R269_3_M5_แปลงเป็นใบกำกับบางส่วน_ยกเว้นที่เหลือยังนับ_ใบกำกับร่างไม่แทน_แปลงทั้งใบนับครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // (ก) แปลงบางส่วน: ใบแจ้งหนี้ผสม 7% 10,000 + ยกเว้น 3,000 → ใบกำกับ (ออกแล้ว) เฉพาะบรรทัด 7%
        var invA = Doc(s, DocumentType.Invoice, "INV-269-M5A", Day(5), (10_000m, 7m, "บริการ"), (3_000m, -1m, "ผักสด"));
        // (ข) ใบกำกับยังเป็นร่าง ⇒ ใบแจ้งหนี้ยังถือภาษีขาย + ยกเว้น
        var invB = Doc(s, DocumentType.Invoice, "INV-269-M5B", Day(6), (1_000m, 7m, "บริการ"), (500m, -1m, "ผักสด"));
        // (ค) แปลงทั้งใบ (ใบกำกับมีบรรทัดยกเว้นด้วย) ⇒ นับที่ใบกำกับครั้งเดียว
        var invC = Doc(s, DocumentType.Invoice, "INV-269-M5C", Day(7), (2_000m, 7m, "บริการ"), (700m, -1m, "ผักสด"));
        db.Documents.AddRange(invA, invB, invC);
        await db.SaveChangesAsync();
        var tivA = Doc(s, DocumentType.TaxInvoice, "TIV-269-M5A", Day(5), (10_000m, 7m, "บริการ"));
        tivA.RelatedDocumentId = invA.Id;
        var tivB = Doc(s, DocumentType.TaxInvoice, "TIV-269-M5B", Day(6), (1_000m, 7m, "บริการ"), (500m, -1m, "ผักสด"));
        tivB.RelatedDocumentId = invB.Id; tivB.Status = DocumentStatus.Draft; tivB.DocumentNumber = "DRAFT-" + Guid.NewGuid().ToString("N");
        var tivC = Doc(s, DocumentType.TaxInvoice, "TIV-269-M5C", Day(7), (2_000m, 7m, "บริการ"), (700m, -1m, "ผักสด"));
        tivC.RelatedDocumentId = invC.Id;
        db.Documents.AddRange(tivA, tivB, tivC);
        await db.SaveChangesAsync();

        using var c = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(c).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);
        Assert.Equal(3_000m + 500m + 700m, Box(report, VatReportLineKind.ExemptSales));   // เดิม 700 (ก/ข หาย)
        var rows = report.Lines.Where(l => !l.IsExcluded && l.DocumentId != null).Select(l => l.Description).ToList();
        Assert.Contains("TIV-269-M5A", rows);
        Assert.Contains("INV-269-M5B", rows);      // ใบกำกับร่างไม่ใช่ใบกำกับ ⇒ ใบแจ้งหนี้ยังถือภาษีขาย
        Assert.DoesNotContain("INV-269-M5A", rows);
        Assert.Equal(700m + 70m + 140m, report.OutputVat);
    }

    [Fact]
    public async Task R269_3_L2_ปัดเศษขึ้นของใบกำกับPOSไม่ใช่ยอดขาย0_ทิศตรงข้ามบรรทัด0ที่มีรหัสสินค้ายังเป็น0()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        var pos = Doc(s, DocumentType.TaxInvoice, "TIV-269-POS", Day(5), (93.22m, 7m, "กาแฟ"), (0.25m, 0m, PosTaxInvoiceLines.RoundingLabel));
        var exp = Doc(s, DocumentType.TaxInvoice, "TIV-269-EXP", Day(6), (400m, 0m, "Export goods"));
        exp.Lines.First().ProductCode = "EXP-1";
        db.Documents.AddRange(pos, exp);
        await db.SaveChangesAsync();
        using var c = DbTestDatabase.TryCreateContext()!;
        var report = await new TaxService(c).ComputeVatReportAsync(s.CompanyId, Month.Year, Month.Month);
        Assert.Equal(400m, Box(report, VatReportLineKind.ZeroRatedSales));   // เดิม 400.25
        Assert.Equal(93.47m, Form(report)[Pp30FormBox.TaxableSales]);
    }

    [Fact]
    public async Task R269_3_M6_ตัวทบทวนบอกรุ่นกติกา_ก่อน269_และ2691_ทิศตรงข้ามยอดตรงไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var s = await SeedAsync(db);
        // ใบกำกับ 7% + ใบเสร็จรับชำระ (VAT 0 — กติกาเดิมนับเป็น 0%) + ลูกหนี้ยกมา VAT 0 (รุ่น 269-1 นับเป็น 0%)
        var tiv = Doc(s, DocumentType.TaxInvoice, "TIV-269-M6", Day(5), (1_000m, 7m, "สินค้า"));
        db.Documents.Add(tiv);
        await db.SaveChangesAsync();
        var settle = Doc(s, DocumentType.Receipt, "REC-269-M6", Day(10), (1_070m, 0m, "รับชำระเงินตามใบกำกับภาษี"));
        settle.RelatedDocumentId = tiv.Id;
        var ob = Doc(s, DocumentType.Invoice, "OB-AR:INV-M6", Day(12), (500m, 0m, "ยอดยกมา"));
        ob.IsOpeningBalance = true;
        db.Documents.AddRange(settle, ob);
        // สามรายงานงวดเดียวกันไม่ได้ ⇒ ใช้สามบริษัท? ไม่ — ใช้งวดเดียว เปลี่ยนแถวสรุปทีละรุ่นแล้วตรวจ
        var stored = new TaxReport { CompanyId = s.CompanyId, TaxType = TaxType.VAT, Year = Month.Year, Month = Month.Month, Status = TaxReportStatus.Filed };
        db.TaxReports.Add(stored);
        await db.SaveChangesAsync();
        db.TaxReportLines.AddRange(
            new TaxReportLine { TaxReportId = stored.Id, LineOrder = 1, Description = "TIV-269-M6", IncomeAmount = 1_000m, TaxRate = 7, TaxAmount = 70m,
                DocumentId = tiv.Id, TransactionDate = Day(5) },
            new TaxReportLine { TaxReportId = stored.Id, LineOrder = 2, Description = "ช่อง 7 (เก่า)", IncomeAmount = 1_570m,
                IncomeTypeCode = VatReportLineKind.ZeroRatedSales });   // กติกาก่อน 269: 1,070 + 500
        await db.SaveChangesAsync();

        Pp30SalesBoxReviewRow Row()
        {
            using var c = DbTestDatabase.TryCreateContext()!;
            return new TaxService(c).GetPp30SalesBoxReviewAsync(s.CompanyId).GetAwaiter().GetResult().Single(r => r.ReportId == stored.Id);
        }
        var pre = Row();
        Assert.Equal(Pp30SalesBoxReview.CauseRuleChange, pre.Cause);
        Assert.Contains(Pp30SalesBoxReview.RulePre269, pre.Message);
        Assert.Equal(0m, pre.FreshZeroRated);

        await db.TaxReportLines.Where(l => l.TaxReportId == stored.Id && l.IncomeTypeCode == VatReportLineKind.ZeroRatedSales)
            .ExecuteUpdateAsync(x => x.SetProperty(l => l.IncomeAmount, 500m));   // รุ่น 269-1: ยอดยกมานับ ใบรับชำระไม่นับ
        var v1 = Row();
        Assert.Equal(Pp30SalesBoxReview.CauseRuleChange, v1.Cause);
        Assert.Contains(Pp30SalesBoxReview.Rule2691, v1.Message);

        await db.TaxReportLines.Where(l => l.TaxReportId == stored.Id && l.IncomeTypeCode == VatReportLineKind.ZeroRatedSales)
            .ExecuteUpdateAsync(x => x.SetProperty(l => l.IncomeAmount, 321m));   // ไม่ตรงรุ่นใด
        Assert.Equal(Pp30SalesBoxReview.CauseDataChanged, Row().Cause);

        await db.TaxReportLines.Where(l => l.TaxReportId == stored.Id && l.IncomeTypeCode == VatReportLineKind.ZeroRatedSales).ExecuteDeleteAsync();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.DoesNotContain(await new TaxService(c2).GetPp30SalesBoxReviewAsync(s.CompanyId), r => r.ReportId == stored.Id);   // ยอดตรงกติกาปัจจุบัน
    }
}
