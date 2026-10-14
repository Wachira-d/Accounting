using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// "แถวในไฟล์ส่งออก == แถวบนหน้าจอ" บนฐานจริง (ทีม X2 · EXPORT_AUDIT 2026-10-14) — ไฟล์ใช้ query เจ้าของเดียวกับรายการ
/// (DocumentService.GetDocumentsAsync / ProductService.GetAllAsync) แล้ววนทุกหน้า ⇒ เกิน 200 แถวก็ครบ
/// · ทิศตรงข้าม: วิธีเดิม (ขอหน้าเดียว pageSize=10000) ได้แค่ 200
/// </summary>
[Trait("Category", "Db")]
public class TradeListExportDbTests
{
    private readonly ITestOutputHelper _out;
    public TradeListExportDbTests(ITestOutputHelper output) => _out = output;

    private static DocumentService Docs(AccountingDbContext db) =>
        new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static async Task<(Guid Company, Guid Customer, Guid Supplier)> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบส่งออก", TaxId = "0105556000001" };
        db.Companies.Add(company);
        var cust = new Contact { CompanyId = company.Id, Name = "ลูกค้าทดสอบ", IsCustomer = true, TaxId = "0105556000002", BranchCode = "00000" };
        var sup = new Contact { CompanyId = company.Id, Name = "ผู้ขายทดสอบ", IsSupplier = true };
        db.Contacts.AddRange(cust, sup);
        await db.SaveChangesAsync();
        return (company.Id, cust.Id, sup.Id);
    }

    private static Document Doc(Guid cid, Guid contactId, string no, DocumentType type, int day, decimal sub,
        bool? purchaseSide = null) => new()
    {
        CompanyId = cid, ContactId = contactId, DocumentNumber = no, DocumentType = type, Status = DocumentStatus.Approved,
        DocumentDate = new DateTime(2026, 1, 1).AddDays(day % 60), SubTotal = sub, VatAmount = Math.Round(sub * 0.07m, 2, MidpointRounding.AwayFromZero),
        TotalAmount = sub + Math.Round(sub * 0.07m, 2, MidpointRounding.AwayFromZero), BalanceDue = 0m,
        CnDnPurchaseSideOverride = purchaseSide,
    };

    [Fact]
    public async Task ส่งออกเอกสารฝั่งรายรับ_ครบทุกแถว_เท่ากับเดินรายการทีละหน้า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, cust, sup) = await SeedAsync(db);
        for (var i = 0; i < 230; i++) db.Documents.Add(Doc(cid, cust, $"TIV-X2-{i:D4}", DocumentType.TaxInvoice, i, 100m + i));
        for (var i = 0; i < 15; i++) db.Documents.Add(Doc(cid, sup, $"PI-X2-{i:D4}", DocumentType.PurchaseInvoice, i, 50m + i));
        db.Documents.Add(Doc(cid, cust, "CN-X2-SALE", DocumentType.CreditNote, 3, 10m, purchaseSide: false));
        db.Documents.Add(Doc(cid, sup, "CN-X2-BUY", DocumentType.CreditNote, 4, 20m, purchaseSide: true));
        await db.SaveChangesAsync();

        var docs = Docs(db);
        var revTypes = TradeListExport.TypesForSide("revenue");

        // หน้าจอ: เดินทีละหน้า 20 แถว (types ของฝั่ง + กรองฝั่งรายใบ — แบบ documents.html?side=revenue)
        var onScreen = new List<DocumentResponse>();
        for (var p = 1; ; p++)
        {
            var page = await docs.GetDocumentsAsync(cid, null, new PagedRequest(p, 20), types: revTypes);
            onScreen.AddRange(page.Items.Where(d => TradeListExport.IsOnSide(d.DocumentType, d.CnDnPurchaseSideOverride, "revenue")));
            if (p >= page.TotalPages) break;
        }

        // ไฟล์: query เดียวกัน วนทุกหน้า 200
        var exported = (await TradeListExport.CollectAllAsync(
                req => docs.GetDocumentsAsync(cid, null, req, types: revTypes),
                p => new PagedRequest(p, TradeListExport.FetchPageSize), TradeListExport.MaxDocumentRows))
            .Where(d => TradeListExport.IsOnSide(d.DocumentType, d.CnDnPurchaseSideOverride, "revenue")).ToList();

        Assert.Equal(231, exported.Count);                                    // 230 ใบกำกับ + ใบลดหนี้ขาย (ใบลดหนี้ซื้อไม่ปน)
        Assert.Equal(onScreen.Select(d => d.Id), exported.Select(d => d.Id));  // ลำดับ/ชุดเดียวกับหน้าจอ
        Assert.DoesNotContain(exported, d => d.DocumentNumber == "CN-X2-BUY");
        Assert.Equal(onScreen.Sum(d => d.TotalAmount), exported.Sum(d => d.TotalAmount));

        // ทิศตรงข้าม: ปุ่มเดิมขอหน้าเดียว pageSize=10000 ⇒ เซิร์ฟเวอร์ให้ 200 (ขาดเงียบ)
        var old = await docs.GetDocumentsAsync(cid, null, new PagedRequest(1, 10000), types: revTypes);
        Assert.Equal(200, old.Items.Count);

        // ไฟล์ CSV: หัว + 231 แถว · ยอดตรงกับ DTO (ไม่คำนวณใหม่)
        var csv = Encoding.UTF8.GetString(TradeListExport.Write(TabularFormat.Csv, TradeListExport.DocumentColumns,
            exported.Select(d => TradeListExport.DocumentRow(d)), "เอกสาร").Content);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(232, lines.Length);
        var first = exported[0];
        Assert.Contains($"{first.DocumentNumber},", lines[1]);
        Assert.Contains(first.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), lines[1]);

        // ตัวกรองคำค้นไหลไปทั้งสองทางเหมือนกัน
        var searched = await TradeListExport.CollectAllAsync(
            req => docs.GetDocumentsAsync(cid, null, req, types: revTypes),
            p => new PagedRequest(p, TradeListExport.FetchPageSize, "TIV-X2-000"), TradeListExport.MaxDocumentRows);
        Assert.Equal(10, searched.Count);                                     // TIV-X2-0000 … 0009
    }

    [Fact]
    public async Task ส่งออกสินค้า_ครบเกิน200_และตัวกรองประเภทมีผลจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _) = await SeedAsync(db);
        for (var i = 0; i < 205; i++)
            db.Products.Add(new Product { CompanyId = cid, Code = $"P{i:D4}", Name = $"สินค้า {i}", ProductType = ProductType.Product, Unit = "ชิ้น", SellingPrice = 10m + i });
        for (var i = 0; i < 5; i++)
            db.Products.Add(new Product { CompanyId = cid, Code = $"S{i:D4}", Name = $"บริการ {i}", ProductType = ProductType.Service, Unit = "งาน", SellingPrice = 500m });
        await db.SaveChangesAsync();

        var svc = new ProductService(db, null!, null!, null!, null!);
        var all = await TradeListExport.CollectAllAsync(req => svc.GetAllAsync(cid, req),
            p => new PagedRequest(p, TradeListExport.FetchPageSize), TradeListExport.MaxMasterRows);
        Assert.Equal(210, all.Count);
        var services = await TradeListExport.CollectAllAsync(req => svc.GetAllAsync(cid, req, ProductType.Service),
            p => new PagedRequest(p, TradeListExport.FetchPageSize), TradeListExport.MaxMasterRows);
        Assert.Equal(5, services.Count);
        Assert.All(services, p => Assert.Equal(ProductType.Service, p.ProductType));

        // หน้าจอ (หน้าละ 20 ตามประเภท) = ไฟล์
        var page1 = await svc.GetAllAsync(cid, new PagedRequest(1, 20), ProductType.Service);
        Assert.Equal(page1.Items.Select(p => p.Id), services.Select(p => p.Id));
    }
}
