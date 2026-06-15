using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// #22 — Sample/demo data seed สำหรับบริษัทเพิ่งสร้าง — เพื่อให้ลอง
/// ระบบโดยไม่ต้อง setup ทั้งหมด. Idempotent: ถ้ามี data sample
/// เก่าจาก seed → ไม่ทับใหม่ (Reference="SAMPLE_DATA" marker).
///
/// Seeds:
///   - 5 contacts (3 customers + 2 vendors) ชื่อสุ่ม Thai
///   - 2 products ตัวอย่าง
///   - 3 sample invoices + 2 sample expenses ในเดือนปัจจุบัน
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/sample-data")]
[Authorize]
public class SampleDataController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public SampleDataController(AccountingDbContext db) { _db = db; }

    [HttpGet("status")]
    public async Task<ActionResult<ApiResponse<object>>> Status(Guid companyId)
    {
        var hasSample = await _db.Documents.AnyAsync(d => d.CompanyId == companyId
            && d.Reference == "SAMPLE_DATA" && !d.IsDeleted);
        return Ok(new ApiResponse<object>(true, new { hasSample }));
    }

    [HttpPost("seed")]
    public async Task<ActionResult<ApiResponse<object>>> Seed(Guid companyId)
    {
        var existing = await _db.Documents.AnyAsync(d => d.CompanyId == companyId
            && d.Reference == "SAMPLE_DATA");
        if (existing)
            return BadRequest(new ApiResponse<object>(false, null, "บริษัทนี้มี sample data อยู่แล้ว — ลบก่อนถึงจะ seed ใหม่ได้"));

        var now = DateTime.UtcNow.Date;

        // 5 contacts
        var customers = new[]
        {
            new Contact { CompanyId = companyId, Name = "บริษัท ตัวอย่าง ก จำกัด", TaxId = "0105566123456", IsCustomer = true, ContactType = ContactType.JuristicPerson, Phone = "02-111-1111", Email = "sample-a@example.com" },
            new Contact { CompanyId = companyId, Name = "บริษัท ตัวอย่าง ข จำกัด", TaxId = "0105566234567", IsCustomer = true, ContactType = ContactType.JuristicPerson, Phone = "02-222-2222", Email = "sample-b@example.com" },
            new Contact { CompanyId = companyId, Name = "ร้านค้าตัวอย่าง ค", TaxId = "3101234567890", IsCustomer = true, ContactType = ContactType.Individual, Phone = "081-333-3333" }
        };
        var vendors = new[]
        {
            new Contact { CompanyId = companyId, Name = "บริษัท ผู้ขาย ก จำกัด", TaxId = "0105566345678", IsSupplier = true, ContactType = ContactType.JuristicPerson, Phone = "02-444-4444" },
            new Contact { CompanyId = companyId, Name = "บริษัท ผู้ขาย ข จำกัด", TaxId = "0105566456789", IsSupplier = true, ContactType = ContactType.JuristicPerson, Phone = "02-555-5555" }
        };
        var allContacts = customers.Concat(vendors).ToArray();
        _db.Contacts.AddRange(allContacts);
        await _db.SaveChangesAsync();

        // 3 invoices to customers
        for (int i = 0; i < 3; i++)
        {
            var c = customers[i % customers.Length];
            var amt = 5000m + i * 3000m;
            var subTotal = Math.Round(amt / 1.07m, 2);
            var vat = amt - subTotal;
            var inv = new Document
            {
                CompanyId = companyId,
                DocumentNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, DocumentType.Invoice),
                DocumentType = DocumentType.Invoice,
                DocumentDate = now.AddDays(-i * 5),
                DueDate = now.AddDays(-i * 5 + 30),
                ContactId = c.Id,
                Reference = "SAMPLE_DATA",
                Status = DocumentStatus.Approved,
                SubTotal = subTotal,
                VatAmount = vat,
                TotalAmount = amt,
                BalanceDue = amt,
                PricesIncludeVat = true,
                Currency = "THB",
                ExchangeRate = 1m,
                Notes = "ตัวอย่างสำหรับทดลองระบบ"
            };
            inv.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = $"บริการที่ปรึกษา รอบที่ {i + 1}",
                Quantity = 1, Unit = "งวด",
                UnitPrice = amt, DiscountPercent = 0, DiscountAmount = 0,
                Amount = subTotal, VatRate = 7, VatAmount = vat
            });
            _db.Documents.Add(inv);
        }

        // 2 expenses
        for (int i = 0; i < 2; i++)
        {
            var v = vendors[i];
            var amt = 1200m + i * 800m;
            var exp = new Document
            {
                CompanyId = companyId,
                DocumentNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, DocumentType.Expense),
                DocumentType = DocumentType.Expense,
                DocumentDate = now.AddDays(-i * 3 - 1),
                ContactId = v.Id,
                Reference = "SAMPLE_DATA",
                Status = DocumentStatus.Approved,
                SubTotal = amt, TotalAmount = amt, BalanceDue = amt,
                Currency = "THB", ExchangeRate = 1m,
                Notes = "ตัวอย่างค่าใช้จ่ายสำหรับทดลองระบบ"
            };
            exp.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = i == 0 ? "ค่าวัสดุสำนักงาน" : "ค่าน้ำมันรถ",
                Quantity = 1, Unit = "ครั้ง",
                UnitPrice = amt, DiscountPercent = 0, DiscountAmount = 0,
                Amount = amt, VatRate = 0, VatAmount = 0
            });
            _db.Documents.Add(exp);
        }

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            contactsCreated = allContacts.Length,
            invoicesCreated = 3,
            expensesCreated = 2,
            totalAR = 5000m + 8000m + 11000m,
            totalAP = 1200m + 2000m
        }, "Sample data seed แล้ว — ทดลองระบบได้เลย!"));
    }

    [HttpDelete]
    public async Task<ActionResult<ApiResponse<object>>> Cleanup(Guid companyId)
    {
        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId && d.Reference == "SAMPLE_DATA")
            .ToListAsync();
        foreach (var d in docs) d.IsDeleted = true;
        // ลบ contact ที่ชื่อเริ่ม "บริษัท ตัวอย่าง" / "ร้านค้าตัวอย่าง" / "บริษัท ผู้ขาย"
        var sampleContacts = await _db.Contacts
            .Where(c => c.CompanyId == companyId && (
                c.Name.StartsWith("บริษัท ตัวอย่าง")
                || c.Name.StartsWith("ร้านค้าตัวอย่าง")
                || c.Name.StartsWith("บริษัท ผู้ขาย ก")
                || c.Name.StartsWith("บริษัท ผู้ขาย ข")))
            .ToListAsync();
        foreach (var c in sampleContacts) c.IsDeleted = true;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            docsRemoved = docs.Count, contactsRemoved = sampleContacts.Count
        }, "ลบ sample data แล้ว"));
    }
}
