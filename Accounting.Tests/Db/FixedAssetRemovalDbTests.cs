using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>เคสจริง 2026-10-19: ทะเบียนสินทรัพย์ที่ขึ้นอัตโนมัติจาก PV ที่จ่ายแล้ว (อุปกรณ์สำนักงาน 138.32 ลง 12210)
/// — ด่านลบต้องตัดสินจาก GL ที่อ้างใบนั้น ไม่ใช่สถานะใบ (FixedAssetService.DeleteAsync + Helpers/AssetRemovalGlCheck)</summary>
[Trait("Category", "Db")]
public class FixedAssetRemovalDbTests
{
    private readonly ITestOutputHelper _out;
    public FixedAssetRemovalDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid DocId, Guid LineId, Guid AssetId, Guid Ppe, Guid Expense, Guid Cash);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบทะเบียนสินทรัพย์ " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000025" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        ChartOfAccount Acc(string code, string name, AccountType t)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = t, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        var cash = Acc("11110", "เงินสด", AccountType.Asset);
        var ppe = Acc("12210", "อุปกรณ์สำนักงาน", AccountType.Asset);
        var exp = Acc("51800", "วัสดุสิ้นเปลือง", AccountType.Expense);
        Acc("12230", "เครื่องตกแต่ง", AccountType.Asset);
        var contact = new Contact { CompanyId = company.Id, Name = "ร้านวันเวิร์คสตอรี่", IsSupplier = true };
        db.Contacts.Add(contact);
        var doc = new Document
        {
            CompanyId = company.Id, DocumentNumber = "PV-20260903-0001", DocumentType = DocumentType.PaymentVoucher,
            Status = DocumentStatus.Paid, DocumentDate = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
            ContactId = contact.Id, Currency = "THB", ExchangeRate = 1m, SubTotal = 138.32m, TotalAmount = 138.32m, PaidAmount = 138.32m,
        };
        var line = new DocumentLine
        {
            Description = "อุปกรณ์สำนักงาน - ภิญญาพร หงวนศิริ", Quantity = 1, Unit = "ชิ้น",
            UnitPrice = 138.32m, Amount = 138.32m, AccountId = ppe.Id, LineOrder = 1,
        };
        doc.Lines.Add(line);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        PostJe(db, company.Id, doc.Id, "UV-0001", ppe.Id, cash.Id, 138.32m);
        var asset = new FixedAsset
        {
            CompanyId = company.Id, AssetCode = "DRAFT-37f3b0a9", Name = line.Description, PurchaseDate = doc.DocumentDate,
            PurchaseCost = 138.32m, NetBookValue = 138.32m, UsefulLifeMonths = 60, Status = AssetStatus.Active,
            AssetAccountId = ppe.Id, SourceDocumentId = doc.Id, SourceDocumentLineId = line.Id, NeedsReview = true,
        };
        db.FixedAssets.Add(asset);
        await db.SaveChangesAsync();
        return new Seed(company.Id, doc.Id, line.Id, asset.Id, ppe.Id, exp.Id, cash.Id);
    }

    private static JournalEntry PostJe(AccountingDbContext db, Guid companyId, Guid docId, string no, Guid dr, Guid cr, decimal amt,
        JournalEntryStatus status = JournalEntryStatus.Posted)
    {
        var je = new JournalEntry
        {
            CompanyId = companyId, EntryNumber = no + "-" + Guid.NewGuid().ToString("N")[..4], EntryDate = DateTime.UtcNow,
            Status = status, SourceDocumentId = docId, TotalDebit = amt, TotalCredit = amt, Description = no,
        };
        je.Lines.Add(new JournalEntryLine { AccountId = dr, DebitAmount = amt, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = cr, CreditAmount = amt, LineOrder = 2 });
        db.JournalEntries.Add(je);
        return je;
    }

    [Fact]
    public async Task Paid_voucher_still_on_12210_blocks_with_amount_and_way_forward()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).DeleteAsync(s.CompanyId, s.AssetId));
        Assert.Contains("138.32", ex.Message);
        Assert.Contains("ไม่ใช่สินทรัพย์", ex.Message);          // ทางไปต่อ — ไม่ใช่ "ยกเลิกเอกสารต้นทาง" อย่างเดียว
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(await check.FixedAssets.AnyAsync(a => a.Id == s.AssetId));
    }

    [Fact]
    public async Task After_line_moved_to_expense_the_register_row_can_be_deleted_while_voucher_stays_paid()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        // ใบปรับปรุงย้ายผัง (แบบที่ "แก้ผังบัญชี"/"เปลี่ยนผัง" ลงให้) — Dr ค่าใช้จ่าย / Cr 12210 อ้างใบเดิม
        PostJe(db, s.CompanyId, s.DocId, "JV-RECLASS", s.Expense, s.Ppe, 138.32m);
        await db.SaveChangesAsync();

        await new FixedAssetService(db).DeleteAsync(s.CompanyId, s.AssetId);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.FixedAssets.AnyAsync(a => a.Id == s.AssetId));
        Assert.Equal(DocumentStatus.Paid, (await check.Documents.SingleAsync(d => d.Id == s.DocId)).Status);   // ใบเดิมไม่ถูกแตะ
    }

    [Fact]
    public async Task Duplicate_register_is_removable_but_the_last_row_backing_the_gl_is_not()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var twin = new FixedAsset
        {
            CompanyId = s.CompanyId, AssetCode = "DRAFT-twin0001", Name = "แถวซ้ำ", PurchaseDate = DateTime.UtcNow,
            PurchaseCost = 138.32m, NetBookValue = 138.32m, UsefulLifeMonths = 60, Status = AssetStatus.Active,
            AssetAccountId = s.Ppe, SourceDocumentId = s.DocId, NeedsReview = true,
        };
        db.FixedAssets.Add(twin);
        await db.SaveChangesAsync();
        var svc = new FixedAssetService(db);

        await svc.DeleteAsync(s.CompanyId, twin.Id);                                         // ตัวซ้ำ — GL มีแถวหลักรองรับครบ
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteAsync(s.CompanyId, s.AssetId));   // ตัวสุดท้าย — ห้าม
    }

    [Fact]
    public async Task Reversed_then_reposted_voucher_still_counts_the_original_and_blocks()
    {
        // ฝ่ายค้าน P1-1: "สลับบริการต่างประเทศ"/resync กลับรายการ JE#1 (สถานะ Reversed) + ตัวกลับ Cr อ้างใบ + ลง JE#2 ใหม่
        // นับแค่ Posted ⇒ เห็น Cr 138.32 + Dr 138.32 = 0 ⇒ เคยปล่อยลบ ทั้งที่ 12210 ยังมีจริง 138.32
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var original = await db.JournalEntries.SingleAsync(j => j.SourceDocumentId == s.DocId);
        original.Status = JournalEntryStatus.Reversed;
        PostJe(db, s.CompanyId, s.DocId, "JV-REV", s.Cash, s.Ppe, 138.32m);
        PostJe(db, s.CompanyId, s.DocId, "UV-REPOST", s.Ppe, s.Cash, 138.32m);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).DeleteAsync(s.CompanyId, s.AssetId));
        Assert.Contains("138.32", ex.Message);
    }

    [Fact]
    public async Task Register_account_edited_away_from_the_gl_account_still_blocks()
    {
        // ฝ่ายค้าน P1-2: ผู้ใช้แก้ผังของทะเบียน 12210 → 12230 ตอน "ตรวจสอบและยืนยัน" — GL ยังอยู่ 12210
        // ตรวจแค่ผังของทะเบียน ⇒ เห็น 0 ⇒ เคยปล่อยลบ · ตอนนี้รวมทุกผังสินทรัพย์ของทั้งใบ
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var s = await SeedAsync(db);
        var furniture = await db.ChartOfAccounts.SingleAsync(a => a.CompanyId == s.CompanyId && a.AccountCode == "12230");
        (await db.FixedAssets.SingleAsync(a => a.Id == s.AssetId)).AssetAccountId = furniture.Id;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => new FixedAssetService(db).DeleteAsync(s.CompanyId, s.AssetId));
    }
}
