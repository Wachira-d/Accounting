using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Owner-only "fresh start" tool. Lets the company owner wipe sample / stale
/// data before going live, without dropping the company itself.
///
/// Each endpoint refuses unless the caller is the company Owner. Operations
/// SOFT-DELETE (set IsDeleted=true) except where listed — never bypass FK
/// integrity. Returns the count of rows affected so the UI can confirm.
///
/// SCOPE WARNING: This is a maintenance tool — wipes are not reversible
/// from the UI. The endpoints surface in /pages/cleanup.html only after
/// the user types the confirmation phrase.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cleanup")]
[Authorize]
public class BulkCleanupController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public BulkCleanupController(AccountingDbContext db) { _db = db; }

    private async Task<bool> IsOwnerAsync(Guid companyId, Guid userId)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user?.IsSystemAdmin == true) return true;
        var cu = await _db.CompanyUsers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.UserId == userId);
        return cu?.Role == UserRole.Owner;
    }

    public record CleanupSummary(int Documents, int JournalEntries, int Contacts, int Products,
        int PosOrders, int Payments, int FixedAssets, int FiscalPeriods);

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<CleanupSummary>>> GetSummary(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await IsOwnerAsync(companyId, userId)) return Forbid();
        var s = new CleanupSummary(
            await _db.Documents.CountAsync(d => d.CompanyId == companyId && !d.IsDeleted),
            await _db.JournalEntries.CountAsync(j => j.CompanyId == companyId && !j.IsDeleted),
            await _db.Contacts.CountAsync(c => c.CompanyId == companyId && !c.IsDeleted),
            await _db.Products.CountAsync(p => p.CompanyId == companyId && !p.IsDeleted),
            await _db.PosOrders.CountAsync(p => p.CompanyId == companyId && !p.IsDeleted),
            await _db.Payments.CountAsync(p => p.CompanyId == companyId && !p.IsDeleted),
            await _db.FixedAssets.CountAsync(a => a.CompanyId == companyId && !a.IsDeleted),
            await _db.FiscalPeriods.CountAsync(f => f.CompanyId == companyId && !f.IsDeleted));
        return Ok(new ApiResponse<CleanupSummary>(true, s));
    }

    // ── Duplicate-contact diagnostic (read-only) — ตอบคำถามทีม integration
    // ว่า "ทั้งบริษัทมี contact ซ้ำกี่ราย" ก่อนตัดสินใจ merge มือ vs สร้าง endpoint.
    // จับซ้ำ 2 แบบ: (ก) เลขผู้เสียภาษี normalize ตัวเลขล้วนตรงกัน (ข) ชื่อ trim
    // lower ตรงกัน. นับ DocumentCount ต่อ contact ช่วยตัดสินว่าจะเก็บตัวไหน.
    private record RawContact(Guid Id, string Name, string? TaxId, string? ExternalId);
    public record DuplicateContactEntry(Guid Id, string Name, string? TaxId, int DocumentCount, bool IsFromIntegration);
    public record DuplicateContactGroup(string Key, string MatchBy, int Count, List<DuplicateContactEntry> Contacts);
    public record DuplicateContactReport(int TotalContacts, int TaxIdDuplicateGroups,
        int NameDuplicateGroups, int DuplicateContactsTotal, List<DuplicateContactGroup> Groups);

    [HttpGet("duplicate-contacts")]
    public async Task<ActionResult<ApiResponse<DuplicateContactReport>>> GetDuplicateContacts(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await IsOwnerAsync(companyId, userId)) return Forbid();

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new RawContact(c.Id, c.Name, c.TaxId, c.ExternalId))
            .ToListAsync();

        // จำนวนเอกสารต่อ contact (ช่วยตัดสินว่าจะเก็บตัวไหนตอน merge)
        var docCounts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId != null)
            .GroupBy(d => d.ContactId!.Value)
            .Select(g => new { ContactId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ContactId, x => x.Count);

        static string Digits(string? s) => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

        DuplicateContactEntry ToEntry(RawContact c) => new(
            c.Id, c.Name, c.TaxId,
            docCounts.GetValueOrDefault(c.Id, 0),
            !string.IsNullOrWhiteSpace(c.ExternalId));

        var groups = new List<DuplicateContactGroup>();

        // (ก) ซ้ำด้วยเลขผู้เสียภาษี (ตัวเลขล้วน, ต้องมีเลข)
        foreach (var grp in contacts.Where(c => Digits(c.TaxId).Length > 0)
                     .GroupBy(c => Digits(c.TaxId)).Where(g => g.Count() > 1))
            groups.Add(new DuplicateContactGroup(grp.Key, "TaxId", grp.Count(),
                grp.Select(ToEntry).OrderByDescending(e => e.DocumentCount).ToList()));

        var taxIdGroupCount = groups.Count;

        // (ข) ซ้ำด้วยชื่อ (trim + lower) — เฉพาะที่ยังไม่ถูกจับด้วย TaxId group
        var alreadyGrouped = groups.SelectMany(g => g.Contacts.Select(c => c.Id)).ToHashSet();
        foreach (var grp in contacts.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                     .GroupBy(c => c.Name.Trim().ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            var fresh = grp.Where(c => !alreadyGrouped.Contains(c.Id)).ToList();
            if (fresh.Count > 1)
                groups.Add(new DuplicateContactGroup(grp.First().Name.Trim(), "Name", fresh.Count,
                    fresh.Select(ToEntry).OrderByDescending(e => e.DocumentCount).ToList()));
        }

        var dupTotal = groups.Sum(g => g.Count);
        var report = new DuplicateContactReport(
            contacts.Count, taxIdGroupCount, groups.Count - taxIdGroupCount, dupTotal, groups);
        return Ok(new ApiResponse<DuplicateContactReport>(true, report));
    }

    public record DeleteTransactionsRequest(string Confirmation);

    /// <summary>Wipe all transactional data — documents, journal entries,
    /// payments, POS orders, stock movements. Master data (contacts,
    /// products, chart of accounts, fiscal periods) is kept. Used to reset
    /// a "playground" company back to clean books before going live.</summary>
    [HttpPost("transactions")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTransactions(Guid companyId, [FromBody] DeleteTransactionsRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await IsOwnerAsync(companyId, userId)) return Forbid();
        if (req.Confirmation != "ลบรายการทั้งหมด")
            return BadRequest(new ApiResponse<object>(false, null, "ยืนยันไม่ตรง — พิมพ์ 'ลบรายการทั้งหมด' ตัวอักษรเป๊ะ"));

        var counts = new Dictionary<string, int>();
        var uid = userId.ToString();

        async Task SoftDelete<T>(IQueryable<T> q, string label) where T : class
        {
            // Soft-delete by reflection — works for any TenantEntity / BaseEntity
            // that has IsDeleted. Avoids needing 8 separate UPDATE statements.
            var rows = await q.ToListAsync();
            counts[label] = rows.Count;
            var prop = typeof(T).GetProperty("IsDeleted");
            var updatedBy = typeof(T).GetProperty("UpdatedBy");
            var updatedAt = typeof(T).GetProperty("UpdatedAt");
            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                prop?.SetValue(r, true);
                updatedBy?.SetValue(r, uid);
                updatedAt?.SetValue(r, now);
            }
            await _db.SaveChangesAsync();
        }

        // Order matters: delete child rows before parents so the soft-delete
        // doesn't tickle any "child references deleted parent" guards in code.
        // PosPayments + PosOrderItems are BaseEntity (no CompanyId) — we
        // scope through their parent PosOrder's CompanyId instead.
        await SoftDelete(_db.PosPayments.Where(p => p.Order.CompanyId == companyId && !p.IsDeleted), "PosPayments");
        await SoftDelete(_db.PosOrderItems.Where(i => i.Order.CompanyId == companyId && !i.IsDeleted), "PosOrderItems");
        await SoftDelete(_db.PosOrders.Where(p => p.CompanyId == companyId && !p.IsDeleted), "PosOrders");
        await SoftDelete(_db.Payments.Where(p => p.CompanyId == companyId && !p.IsDeleted), "Payments");
        await SoftDelete(_db.JournalEntryLines.Where(l => l.JournalEntry.CompanyId == companyId && !l.IsDeleted), "JournalEntryLines");
        await SoftDelete(_db.JournalEntries.Where(j => j.CompanyId == companyId && !j.IsDeleted), "JournalEntries");
        await SoftDelete(_db.DocumentLines.Where(l => l.Document.CompanyId == companyId && !l.IsDeleted), "DocumentLines");
        await SoftDelete(_db.Documents.Where(d => d.CompanyId == companyId && !d.IsDeleted), "Documents");
        await SoftDelete(_db.StockMovements.Where(s => s.CompanyId == companyId && !s.IsDeleted), "StockMovements");

        return Ok(new ApiResponse<object>(true, counts, "ลบข้อมูลธุรกรรมสำเร็จ — master data (สินค้า / ผู้ติดต่อ / ผังบัญชี) ยังอยู่ครบ"));
    }

    public record DeleteMasterDataRequest(string Confirmation, bool IncludeContacts, bool IncludeProducts);

    /// <summary>Wipe master data on top of transactions. Touched separately
    /// because in many "reset" scenarios the master data (contacts /
    /// products) is what the owner wants to KEEP and re-import. Refuses if
    /// transactional data still exists.</summary>
    [HttpPost("master-data")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteMasterData(Guid companyId, [FromBody] DeleteMasterDataRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await IsOwnerAsync(companyId, userId)) return Forbid();
        if (req.Confirmation != "ลบ master data")
            return BadRequest(new ApiResponse<object>(false, null, "ยืนยันไม่ตรง — พิมพ์ 'ลบ master data' ตัวอักษรเป๊ะ"));

        // Don't allow if there are still active transactions — would leave
        // dangling FKs in the JE / Document tables.
        var hasActiveTxn = await _db.Documents.AnyAsync(d => d.CompanyId == companyId && !d.IsDeleted)
                       || await _db.JournalEntries.AnyAsync(j => j.CompanyId == companyId && !j.IsDeleted);
        if (hasActiveTxn)
            return BadRequest(new ApiResponse<object>(false, null, "ยังมีเอกสาร/JE ค้างอยู่ — ลบ transactions ก่อน"));

        var counts = new Dictionary<string, int>();
        var now = DateTime.UtcNow;
        var uid = userId.ToString();

        if (req.IncludeContacts)
        {
            var rows = await _db.Contacts.Where(c => c.CompanyId == companyId && !c.IsDeleted).ToListAsync();
            foreach (var r in rows) { r.IsDeleted = true; r.UpdatedBy = uid; r.UpdatedAt = now; }
            counts["Contacts"] = rows.Count;
        }
        if (req.IncludeProducts)
        {
            var rows = await _db.Products.Where(p => p.CompanyId == companyId && !p.IsDeleted).ToListAsync();
            foreach (var r in rows) { r.IsDeleted = true; r.UpdatedBy = uid; r.UpdatedAt = now; }
            counts["Products"] = rows.Count;
        }
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, counts, "ลบ master data สำเร็จ"));
    }
}
