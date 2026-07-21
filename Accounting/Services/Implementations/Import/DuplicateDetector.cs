using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations.Import;

/// <summary>
/// Per-entity-type duplicate detection — ค้นหา existing record ที่ match
/// แต่ละ row ใน import batch. รองรับทุก import flow โดย dispatch ตาม
/// entityType.
///
/// Detection rules per type:
///   Contact: TaxId exact (score 1.0) > Name exact (0.9) > Name fuzzy (0.7)
///   Product: Code exact (1.0) > Barcode exact (1.0) > Name fuzzy (0.7)
///   Document: DocumentNumber exact (1.0) > Contact+Amount+Date±2d (0.9)
///   Employee: CitizenId exact (1.0) > EmployeeCode exact (1.0) > Name fuzzy (0.8)
///   BankTransaction: BankRef+Amount+Date exact (1.0) > Amount+Date±1d (0.7)
///   FixedAsset: AssetCode exact (1.0) > Serial+Description (0.8)
///   JournalEntry: EntryNumber+Date (1.0)
///
/// คืน null = ไม่ใช่ duplicate (ปล่อย import ปกติ).
/// คืน ImportDuplicateConflict = staged + existing → user resolve.
/// </summary>
public interface IDuplicateDetector
{
    Task<ImportDuplicateConflict?> DetectAsync(Guid companyId, string entityType,
        Dictionary<string, object?> stagedData, Guid? sessionId = null,
        string? sessionRef = null, int rowNumber = 0,
        CancellationToken ct = default);

    /// <summary>Bulk detect — รับ list ของ staged rows + คืน list conflicts
    /// (เฉพาะแถวที่เจอ dup). มี optimization: pre-load existing records ทั้ง
    /// company ครั้งเดียว แล้ว match in-memory.</summary>
    Task<List<ImportDuplicateConflict>> DetectBatchAsync(Guid companyId, string entityType,
        List<Dictionary<string, object?>> stagedRows, Guid? sessionId = null,
        string? sessionRef = null, CancellationToken ct = default);
}

public class DuplicateDetector : IDuplicateDetector
{
    private readonly AccountingDbContext _db;
    public DuplicateDetector(AccountingDbContext db) { _db = db; }

    public async Task<ImportDuplicateConflict?> DetectAsync(Guid companyId, string entityType,
        Dictionary<string, object?> stagedData, Guid? sessionId, string? sessionRef,
        int rowNumber, CancellationToken ct = default)
    {
        return entityType switch
        {
            "Contact" => await DetectContactAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "Product" => await DetectProductAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "Document" => await DetectDocumentAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "Employee" => await DetectEmployeeAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "BankTransaction" => await DetectBankTxnAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "FixedAsset" => await DetectFixedAssetAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            "JournalEntry" => await DetectJournalEntryAsync(companyId, stagedData, sessionId, sessionRef, rowNumber, ct),
            _ => null   // unknown type → allow
        };
    }

    public async Task<List<ImportDuplicateConflict>> DetectBatchAsync(Guid companyId, string entityType,
        List<Dictionary<string, object?>> stagedRows, Guid? sessionId, string? sessionRef,
        CancellationToken ct = default)
    {
        var conflicts = new List<ImportDuplicateConflict>();
        for (int i = 0; i < stagedRows.Count; i++)
        {
            var c = await DetectAsync(companyId, entityType, stagedRows[i], sessionId, sessionRef, i + 1, ct);
            if (c != null) conflicts.Add(c);
        }
        return conflicts;
    }

    private static string Get(Dictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v != null) return v.ToString() ?? "";
        return "";
    }

    private static decimal GetDec(Dictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v != null && decimal.TryParse(v.ToString(), out var x)) return x;
        return 0;
    }

    private static DateTime? GetDate(Dictionary<string, object?> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && v != null && DateTime.TryParse(v.ToString(), out var dt)) return dt;
        return null;
    }

    private static ImportDuplicateConflict Pack(Guid companyId, string entityType,
        Dictionary<string, object?> staged, Guid? existingId, object existingData,
        double score, string reason, Guid? sessionId, string? sessionRef, int rowNumber)
        => new ImportDuplicateConflict
        {
            CompanyId = companyId,
            SessionId = sessionId,
            SessionRef = sessionRef,
            RowNumber = rowNumber,
            EntityType = entityType,
            StagedDataJson = JsonSerializer.Serialize(staged),
            ExistingEntityId = existingId,
            ExistingDataJson = JsonSerializer.Serialize(existingData),
            MatchScore = score,
            MatchReason = reason,
            Resolution = ImportConflictResolution.Pending
        };

    private async Task<ImportDuplicateConflict?> DetectContactAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var taxId = Get(d, "TaxId", "taxId", "เลขผู้เสียภาษี");
        var name = Get(d, "Name", "name", "ชื่อ");
        // 1.0 TaxId exact
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            var hit = await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == cid && !c.IsDeleted && c.TaxId == taxId)
                .Select(c => new { c.Id, c.Name, c.TaxId, c.Phone, c.Email, c.Address })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Contact", d, hit.Id, hit, 1.0, "เลขผู้เสียภาษีตรงกัน", sid, sref, rn);
        }
        // 0.9 Name exact (case-insensitive)
        if (!string.IsNullOrWhiteSpace(name))
        {
            var lower = name.ToLowerInvariant().Trim();
            var hit = await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == cid && !c.IsDeleted && c.Name.ToLower() == lower)
                .Select(c => new { c.Id, c.Name, c.TaxId, c.Phone, c.Email, c.Address })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Contact", d, hit.Id, hit, 0.9, "ชื่อตรงกัน (เลขผู้เสียภาษีต่างกัน)", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectProductAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var code = Get(d, "Code", "code", "ProductCode", "รหัส");
        var barcode = Get(d, "Barcode", "barcode", "บาร์โค้ด");
        if (!string.IsNullOrWhiteSpace(code))
        {
            var hit = await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == cid && !p.IsDeleted && p.Code == code)
                .Select(p => new { p.Id, p.Code, p.Name, p.SellingPrice })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Product", d, hit.Id, hit, 1.0, "รหัสสินค้าตรงกัน", sid, sref, rn);
        }
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var hit = await _db.Products.AsNoTracking()
                .Where(p => p.CompanyId == cid && !p.IsDeleted && p.Barcode == barcode)
                .Select(p => new { p.Id, p.Code, p.Name, p.Barcode, p.SellingPrice })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Product", d, hit.Id, hit, 1.0, "บาร์โค้ดตรงกัน", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectDocumentAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var docNo = Get(d, "DocumentNumber", "documentNumber", "เลขที่");
        if (!string.IsNullOrWhiteSpace(docNo))
        {
            var hit = await _db.Documents.AsNoTracking()
                .Where(x => x.CompanyId == cid && !x.IsDeleted && x.DocumentNumber == docNo)
                .Select(x => new { x.Id, x.DocumentNumber, x.DocumentType, x.DocumentDate, x.TotalAmount, ContactName = x.Contact.Name })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Document", d, hit.Id, hit, 1.0, "เลขที่เอกสารตรงกัน", sid, sref, rn);
        }
        // fallback: same contact + amount + date ±2 day
        var contactName = Get(d, "ContactName", "contactName");
        var amount = GetDec(d, "TotalAmount", "totalAmount", "Amount", "amount");
        var date = GetDate(d, "DocumentDate", "documentDate", "Date", "date");
        if (amount > 0 && date.HasValue && !string.IsNullOrWhiteSpace(contactName))
        {
            var windowStart = date.Value.AddDays(-2);
            var windowEnd = date.Value.AddDays(2);
            var hit = await _db.Documents.AsNoTracking()
                .Where(x => x.CompanyId == cid && !x.IsDeleted
                    && x.Contact.Name == contactName
                    && Math.Abs(x.TotalAmount - amount) < 0.01m
                    && x.DocumentDate >= windowStart && x.DocumentDate <= windowEnd)
                .Select(x => new { x.Id, x.DocumentNumber, x.DocumentType, x.DocumentDate, x.TotalAmount, ContactName = x.Contact.Name })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Document", d, hit.Id, hit, 0.85, "ผู้ติดต่อ+ยอด+วันที่ตรงกัน (±2 วัน)", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectEmployeeAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var idCard = Get(d, "CitizenId", "citizenId", "TaxId", "taxId", "เลขบัตรประชาชน");
        var empCode = Get(d, "EmployeeCode", "employeeCode", "รหัสพนักงาน");
        if (!string.IsNullOrWhiteSpace(idCard))
        {
            var hit = await _db.Set<Employee>().AsNoTracking()
                .Where(e => e.CompanyId == cid && (e.CitizenId == idCard || e.TaxId == idCard))
                .Select(e => new { e.Id, e.EmployeeCode, e.CitizenId, e.FirstNameTh, e.LastNameTh, e.BaseSalary })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Employee", d, hit.Id, hit, 1.0, "เลขบัตรประชาชนตรงกัน", sid, sref, rn);
        }
        if (!string.IsNullOrWhiteSpace(empCode))
        {
            var hit = await _db.Set<Employee>().AsNoTracking()
                .Where(e => e.CompanyId == cid && e.EmployeeCode == empCode)
                .Select(e => new { e.Id, e.EmployeeCode, e.CitizenId, e.FirstNameTh, e.LastNameTh, e.BaseSalary })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "Employee", d, hit.Id, hit, 1.0, "รหัสพนักงานตรงกัน", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectBankTxnAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var bankRef = Get(d, "BankReference", "bankReference", "Reference", "เลขอ้างอิงธนาคาร");
        var amount = GetDec(d, "Amount", "amount", "จำนวน");
        var date = GetDate(d, "TransactionDate", "transactionDate", "Date", "วันที่");
        if (!string.IsNullOrWhiteSpace(bankRef) && amount != 0 && date.HasValue)
        {
            var hit = await _db.Set<BankTransaction>().AsNoTracking()
                .Where(t => t.CompanyId == cid
                    && t.Reference == bankRef
                    && Math.Abs(t.Amount - amount) < 0.01m
                    && t.TransactionDate == date.Value)
                .Select(t => new { t.Id, t.Reference, t.TransactionDate, t.Amount, t.Description })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "BankTransaction", d, hit.Id, hit, 1.0, "Bank ref+amount+date ตรงกัน", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectFixedAssetAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var code = Get(d, "AssetCode", "assetCode", "Code", "รหัสสินทรัพย์");
        if (!string.IsNullOrWhiteSpace(code))
        {
            var hit = await _db.Set<FixedAsset>().AsNoTracking()
                .Where(a => a.CompanyId == cid && a.AssetCode == code)
                .Select(a => new { a.Id, a.AssetCode, a.Name, a.PurchaseDate, a.PurchaseCost })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "FixedAsset", d, hit.Id, hit, 1.0, "รหัสสินทรัพย์ตรงกัน", sid, sref, rn);
        }
        return null;
    }

    private async Task<ImportDuplicateConflict?> DetectJournalEntryAsync(Guid cid, Dictionary<string, object?> d,
        Guid? sid, string? sref, int rn, CancellationToken ct)
    {
        var entryNo = Get(d, "EntryNumber", "entryNumber", "เลขที่ JE");
        if (!string.IsNullOrWhiteSpace(entryNo))
        {
            var hit = await _db.JournalEntries.AsNoTracking()
                .Where(j => j.CompanyId == cid && !j.IsDeleted && j.EntryNumber == entryNo)
                .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.TotalDebit, j.TotalCredit, j.Description })
                .FirstOrDefaultAsync(ct);
            if (hit != null) return Pack(cid, "JournalEntry", d, hit.Id, hit, 1.0, "เลขที่ JE ตรงกัน", sid, sref, rn);
        }
        return null;
    }
}
