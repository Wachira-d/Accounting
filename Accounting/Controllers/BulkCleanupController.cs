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

    // ── Double-revenue diagnostic (read-only) — หา "ใบแจ้งหนี้ที่ลง GL แล้ว
    // + มีใบกำกับภาษีลูก active ที่ลง GL ด้วย" = รายได้/ภาษีขายถูกบันทึก 2 ครั้ง.
    // เกิดจากข้อมูลก่อน SupersedeSourceInvoiceAsync ถูก deploy (แปลง INV→TIV
    // แล้ว INV เดิมไม่ถูก reverse/void). ใช้ไล่เก็บกวาดให้งบถูกต้องตามกฎหมาย:
    // ทางแก้ต่อรายการ = ยกเลิกการชำระ/ใบเสร็จลูกของ TIV → ยกเลิก TIV → แปลง
    // INV ใหม่ (supersede ทำงาน + บรรทัดครบหลัง fix) → บันทึกรับเงินใหม่.
    public record DoubleRevenuePair(
        Guid InvoiceId, string InvoiceNumber, DateTime InvoiceDate, string InvoiceStatus, decimal InvoiceTotal,
        Guid TaxInvoiceId, string TaxInvoiceNumber, string TaxInvoiceStatus, decimal TaxInvoiceTotal,
        bool AmountMismatch);
    public record DoubleRevenueReport(int PairCount, decimal TotalDoubledRevenue, List<DoubleRevenuePair> Pairs);

    [HttpGet("double-revenue")]
    public async Task<ActionResult<ApiResponse<DoubleRevenueReport>>> GetDoubleRevenue(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();

        // INV ที่ "ลง GL แล้ว" (พ้น Draft/WaitingApproval, ไม่ voided/rejected)
        var postedStatuses = new[] { DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Paid, DocumentStatus.Overdue };

        var pairs = await (
            from tiv in _db.Documents.AsNoTracking()
            join inv in _db.Documents.AsNoTracking() on tiv.RelatedDocumentId equals inv.Id
            where tiv.CompanyId == companyId && !tiv.IsDeleted
                && tiv.DocumentType == DocumentType.TaxInvoice
                && postedStatuses.Contains(tiv.Status)
                && inv.CompanyId == companyId && !inv.IsDeleted
                && inv.DocumentType == DocumentType.Invoice
                && postedStatuses.Contains(inv.Status)   // ⬅ INV ควรถูก void โดย supersede — ยัง posted = ซ้ำ
            select new DoubleRevenuePair(
                inv.Id, inv.DocumentNumber, inv.DocumentDate, inv.Status.ToString(), inv.TotalAmount,
                tiv.Id, tiv.DocumentNumber, tiv.Status.ToString(), tiv.TotalAmount,
                inv.TotalAmount != tiv.TotalAmount))
            .ToListAsync();

        var report = new DoubleRevenueReport(
            pairs.Count,
            pairs.Sum(p => p.TaxInvoiceTotal),   // รายได้ส่วนที่ลงซ้ำ ≈ ยอด TIV (INV ควรถูก reverse)
            pairs.OrderByDescending(p => p.InvoiceDate).ToList());
        return Ok(new ApiResponse<DoubleRevenueReport>(true, report));
    }

    // ── Paid-VAT-invoice diagnostic (read-only) — หา "ใบแจ้งหนี้ (Invoice) ที่มี
    // VAT + รับเงินแล้ว แต่ไม่มีใบกำกับภาษีลูก active" = tax point เกิดแล้ว (รับเงิน)
    // แต่ยังไม่ได้ออกใบกำกับตาม §86/4 → ลูกค้าเคลมภาษีซื้อไม่ได้ + ผู้ขายผิดหน้าที่
    // ออกใบกำกับ. (VAT ขายลง GL/ภ.พ.30 ครบแล้วผ่าน F5 — ที่ขาดคือ "เอกสาร" ใบกำกับ)
    // frontend เตือนก่อนรับเงินแล้ว (recordPayment/payments) — ตัวนี้ไว้กวาด legacy.
    public record PaidVatInvoiceEntry(Guid Id, string DocumentNumber, DateTime DocumentDate,
        string ContactName, decimal VatAmount, decimal TotalAmount, decimal PaidAmount, string Status);
    public record PaidVatInvoiceReport(int Count, decimal TotalVat, List<PaidVatInvoiceEntry> Invoices);

    [HttpGet("paid-vat-invoices")]
    public async Task<ActionResult<ApiResponse<PaidVatInvoiceReport>>> GetPaidVatInvoicesWithoutTaxInvoice(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();

        var rows = await (
            from inv in _db.Documents.AsNoTracking()
            where inv.CompanyId == companyId && !inv.IsDeleted
                && inv.DocumentType == DocumentType.Invoice
                && inv.VatAmount > 0 && inv.PaidAmount > 0.005m
                && inv.Status != DocumentStatus.Voided && inv.Status != DocumentStatus.Rejected
                // ไม่มีใบกำกับภาษีลูก active
                && !_db.Documents.Any(t => t.CompanyId == companyId && !t.IsDeleted
                    && t.RelatedDocumentId == inv.Id && t.DocumentType == DocumentType.TaxInvoice
                    && t.Status != DocumentStatus.Voided && t.Status != DocumentStatus.Rejected)
            orderby inv.DocumentDate descending
            select new { inv.Id, inv.DocumentNumber, inv.DocumentDate, inv.ContactId,
                inv.VatAmount, inv.TotalAmount, inv.PaidAmount, inv.Status })
            .ToListAsync();

        // ชื่อผู้ติดต่อ (batch, รวมที่ถูกลบ — กัน INNER JOIN ตัดแถว)
        var cids = rows.Select(r => r.ContactId).Distinct().ToList();
        var names = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && cids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var items = rows.Select(r => new PaidVatInvoiceEntry(
            r.Id, r.DocumentNumber, r.DocumentDate,
            names.GetValueOrDefault(r.ContactId, "-"),
            r.VatAmount, r.TotalAmount, r.PaidAmount, r.Status.ToString())).ToList();

        return Ok(new ApiResponse<PaidVatInvoiceReport>(true,
            new PaidVatInvoiceReport(items.Count, items.Sum(i => i.VatAmount), items)));
    }

    // ── Orphaned settlement receipts (ใบเสร็จหลักฐานรับเงินที่ต้นทางหายแล้ว) ──
    // เกิดจากลบใบกำกับก่อนหน้า: purge เดิมแค่ NULL RelatedDocumentId ไม่ได้ลบใบเสร็จ
    // → REC ลอยค้างใน list ("ใบเสร็จงอกเยอะ"). GET = diagnostic (read-only, เปิดให้
    // API key ดูได้), POST purge = Owner soft-delete (ใบ settlement ไม่มี JE ของ
    // ตัวเอง — payment ถือ JE ซึ่งถูกลบไปกับใบกำกับแล้ว จึงลบทิ้งปลอดภัย).
    // ParentReference = เลขใบกำกับต้นทางเดิม (REC.Reference — คงไว้แม้ RelatedDocumentId
    // ถูก NULL ตอนลบ parent) → TakeTime ใช้เจาะจงลบเฉพาะ REC ของใบที่เพิ่งลบได้
    public record OrphanReceiptEntry(Guid Id, string DocumentNumber, DateTime DocumentDate,
        string ContactName, decimal TotalAmount, string? ParentReference, string Reason);
    public record OrphanReceiptReport(int Count, decimal TotalAmount, List<OrphanReceiptEntry> Items);
    // scoped purge: ระบุ ReceiptIds หรือ Reference (เลขใบกำกับต้นทาง) → ลบเฉพาะที่ระบุ
    // (intersect กับ orphan set เสมอ). ไม่ระบุ = กวาดทั้ง company (Owner เท่านั้น)
    public record PurgeOrphanRequest(List<Guid>? ReceiptIds = null, string? Reference = null);
    public record PurgeOrphanResult(int Deleted, List<Guid> DeletedIds);

    private async Task<List<Guid>> FindOrphanSettlementReceiptIdsAsync(Guid companyId)
    {
        var recs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.IsSettlementReceipt)
            .Select(d => new { d.Id, d.RelatedDocumentId })
            .ToListAsync();
        var parentIds = recs.Where(r => r.RelatedDocumentId.HasValue)
            .Select(r => r.RelatedDocumentId!.Value).Distinct().ToList();
        var aliveSet = (await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && parentIds.Contains(d.Id) && !d.IsDeleted
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => d.Id).ToListAsync()).ToHashSet();
        // orphan = ไม่มีต้นทาง (ถูกลบ → RelatedDocumentId NULL) หรือต้นทางถูกยกเลิก/ลบ
        return recs.Where(r => !r.RelatedDocumentId.HasValue
            || !aliveSet.Contains(r.RelatedDocumentId.Value)).Select(r => r.Id).ToList();
    }

    [HttpGet("orphaned-settlement-receipts")]
    public async Task<ActionResult<ApiResponse<OrphanReceiptReport>>> GetOrphanedSettlementReceipts(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();

        var orphanIds = await FindOrphanSettlementReceiptIdsAsync(companyId);
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && orphanIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.ContactId,
                d.TotalAmount, d.RelatedDocumentId, d.Reference })
            .OrderByDescending(d => d.DocumentDate)
            .ToListAsync();

        var cids = rows.Select(r => r.ContactId).Distinct().ToList();
        var names = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && cids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var items = rows.Select(r => new OrphanReceiptEntry(
            r.Id, r.DocumentNumber, r.DocumentDate,
            names.GetValueOrDefault(r.ContactId, "-"), r.TotalAmount, r.Reference,
            r.RelatedDocumentId.HasValue ? "ใบกำกับต้นทางถูกยกเลิก/ลบ" : "ไม่มีใบกำกับต้นทาง (ถูกลบไปแล้ว)")).ToList();

        return Ok(new ApiResponse<OrphanReceiptReport>(true,
            new OrphanReceiptReport(items.Count, items.Sum(i => i.TotalAmount), items)));
    }

    /// <summary>ลบใบเสร็จ orphan. **เจาะจง** (ส่ง ReceiptIds หรือ Reference=เลขใบกำกับ
    /// ต้นทางที่เพิ่งลบ) → acc_ key เรียกได้ (ปลอดภัย เพราะ intersect กับ orphan set
    /// เสมอ — REC ที่ยังมี parent ใช้งานจะไม่ถูกลบ). **ไม่ระบุ** = กวาดทั้ง company →
    /// Owner เท่านั้น. soft-delete (ใบ settlement ไม่มี JE ของตัวเอง).</summary>
    [HttpPost("orphaned-settlement-receipts/purge")]
    public async Task<ActionResult<ApiResponse<PurgeOrphanResult>>> PurgeOrphanedSettlementReceipts(
        Guid companyId, [FromBody] PurgeOrphanRequest? req = null)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var scoped = req != null && ((req.ReceiptIds is { Count: > 0 }) || !string.IsNullOrWhiteSpace(req.Reference));
        if (scoped)
        {
            // เจาะจง = ปลอดภัย → acc_ key หรือ Owner
            if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();
        }
        else
        {
            // กวาดทั้ง company → Owner เท่านั้น (กันลบเป็นวงกว้างโดยไม่ตั้งใจ)
            if (!await IsOwnerAsync(companyId, userId)) return Forbid();
        }

        var orphanSet = (await FindOrphanSettlementReceiptIdsAsync(companyId)).ToHashSet();
        List<Guid> target;
        if (req?.ReceiptIds is { Count: > 0 })
            // เฉพาะ id ที่เป็น orphan จริง (กันลบ REC ที่ยังมี parent ใช้งาน)
            target = req.ReceiptIds.Where(orphanSet.Contains).ToList();
        else if (!string.IsNullOrWhiteSpace(req?.Reference))
        {
            var refKey = req.Reference.Trim();
            target = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && orphanSet.Contains(d.Id) && d.Reference == refKey)
                .Select(d => d.Id).ToListAsync();
        }
        else
            target = orphanSet.ToList();

        if (target.Count == 0)
            return Ok(new ApiResponse<PurgeOrphanResult>(true, new PurgeOrphanResult(0, new List<Guid>()),
                "ไม่มีใบเสร็จหลักฐานรับเงินที่เข้าเกณฑ์ (ต้นทางหาย) ให้ลบ"));

        await _db.Documents
            .Where(d => d.CompanyId == companyId && target.Contains(d.Id) && d.IsSettlementReceipt)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDeleted, true));

        return Ok(new ApiResponse<PurgeOrphanResult>(true, new PurgeOrphanResult(target.Count, target),
            $"ลบใบเสร็จหลักฐานรับเงินที่ไม่มีใบกำกับต้นทาง {target.Count} ใบ (soft-delete) — resync ใหม่ได้สะอาด"));
    }

    // ── Deposit-GL debris diagnostic (read-only) — ตามรอย 21510 ติดลบ/21913 ค้าง ──
    // จากการ resync (ลบ+สร้างใหม่) ซ้ำบน env เก่า: JV ที่ integration post ผ่าน
    // /integration/journals **ไม่มี SourceDocumentId** และ JE ที่ source ถูกลบ/void
    // ไปแล้ว จะไม่ถูกกวาดตอนลบเอกสาร → ค้างสะสมบนบัญชีมัดจำ (215xx/217xx) และ
    // ภาษีขายรอเรียกเก็บ (21913). endpoint นี้แจกแจง "ทุก JE ที่แตะบัญชีกลุ่มนี้"
    // พร้อมสถานะ source (live / ถูกลบ / ไม่มี) + net ต่อบัญชี → TakeTime reverse
    // JV ของตัวเอง (รู้ EntryNumber) หรือนักบัญชีออก JV ปรับปรุงยอดเดียวได้ตรงจุด.
    public record DepositDebrisLine(string EntryNumber, DateTime EntryDate, string? Description,
        string AccountCode, decimal Debit, decimal Credit, string SourceStatus, string? SourceDocumentNumber);
    public record DepositDebrisAccount(string AccountCode, string AccountName,
        decimal NetBalance, decimal SuspectNet, List<DepositDebrisLine> SuspectLines);
    public record DepositDebrisReport(int AccountCount, int SuspectLineCount, List<DepositDebrisAccount> Accounts);

    [HttpGet("deposit-gl-debris")]
    public async Task<ActionResult<ApiResponse<DepositDebrisReport>>> GetDepositGlDebris(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();

        // บัญชีเป้าหมาย: มัดจำ/รับล่วงหน้า (215xx/217xx) + ภาษีขายรอเรียกเก็บ 21913
        var accts = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId
                && (a.AccountCode.StartsWith("215") || a.AccountCode.StartsWith("217")
                    || a.AccountCode == "21913"))
            .Select(a => new { a.Id, a.AccountCode, a.AccountName })
            .ToListAsync();
        var acctIds = accts.Select(a => a.Id).ToList();

        // ทุกบรรทัด GL บนบัญชีเป้าหมาย (Posted/Reversed = ยังอยู่ใน ledger)
        var lines = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => !l.IsDeleted && acctIds.Contains(l.AccountId)
                && l.JournalEntry.CompanyId == companyId && !l.JournalEntry.IsDeleted
                && (l.JournalEntry.Status == JournalEntryStatus.Posted
                    || l.JournalEntry.Status == JournalEntryStatus.Reversed))
            .Select(l => new
            {
                l.AccountId, l.DebitAmount, l.CreditAmount,
                l.JournalEntry.EntryNumber, l.JournalEntry.EntryDate,
                l.JournalEntry.Description, l.JournalEntry.SourceDocumentId,
            })
            .ToListAsync();

        // สถานะ source ของแต่ละ JE: null = JV integration/manual (ไม่ผูกเอกสาร),
        // มีแต่หาไม่เจอ/ลบ/void = ซากจากการลบเอกสาร — สองกลุ่มนี้คือ "suspect"
        var srcIds = lines.Where(l => l.SourceDocumentId.HasValue)
            .Select(l => l.SourceDocumentId!.Value).Distinct().ToList();
        var srcDocs = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.CompanyId == companyId && srcIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.IsDeleted, d.Status })
            .ToDictionaryAsync(d => d.Id);

        var result = new List<DepositDebrisAccount>();
        var suspectTotal = 0;
        foreach (var a in accts)
        {
            var accLines = lines.Where(l => l.AccountId == a.Id).ToList();
            if (accLines.Count == 0) continue;
            var net = accLines.Sum(l => l.CreditAmount - l.DebitAmount);
            var suspects = new List<DepositDebrisLine>();
            foreach (var l in accLines)
            {
                string status; string? srcNum = null;
                if (!l.SourceDocumentId.HasValue)
                    status = "ไม่ผูกเอกสาร (JV integration/manual)";
                else if (!srcDocs.TryGetValue(l.SourceDocumentId.Value, out var sd))
                    status = "เอกสารต้นทางถูกลบถาวรแล้ว";
                else if (sd.IsDeleted || sd.Status == DocumentStatus.Voided)
                { status = sd.IsDeleted ? "เอกสารต้นทางถูกลบ (soft)" : "เอกสารต้นทางถูกยกเลิก"; srcNum = sd.DocumentNumber; }
                else
                    continue;   // source live → ปกติ ไม่ใช่ซาก
                suspects.Add(new DepositDebrisLine(l.EntryNumber, l.EntryDate, l.Description,
                    a.AccountCode, l.DebitAmount, l.CreditAmount, status, srcNum));
            }
            if (suspects.Count == 0 && net >= 0m) continue;   // บัญชีสะอาด + ยอดปกติ → ข้าม
            suspectTotal += suspects.Count;
            result.Add(new DepositDebrisAccount(a.AccountCode, a.AccountName, net,
                suspects.Sum(s => s.Credit - s.Debit),
                suspects.OrderBy(s => s.EntryDate).ToList()));
        }

        return Ok(new ApiResponse<DepositDebrisReport>(true,
            new DepositDebrisReport(result.Count, suspectTotal, result),
            result.Count == 0
                ? "ไม่พบซาก GL บนบัญชีมัดจำ/ภาษีขายรอเรียกเก็บ"
                : "พบรายการต้องตรวจ — reverse JV ของ integration (รู้ EntryNumber) หรือออก JV ปรับปรุงตาม SuspectNet ต่อบัญชี"));
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

    /// <summary>true ถ้า request มาจาก X-Api-Key/X-Integration-Key ที่ผูกกับ
    /// companyId นี้ (ApiKeyMiddleware ตั้ง Items เหล่านี้ + คีย์ act ได้เฉพาะ
    /// บริษัทตัวเอง). ใช้เปิดให้ integration ดึงรายงาน read-only ได้จากแอปตัวเอง
    /// โดยไม่ต้องมี Owner JWT — เฉพาะ endpoint อ่านอย่างเดียวนี้เท่านั้น.</summary>
    private bool IsCompanyScopedApiKey(Guid companyId) =>
        HttpContext.Items.TryGetValue("IsApiKeyAuth", out var ak) && ak is true
        && HttpContext.Items.TryGetValue("CompanyId", out var kc) && kc is Guid kg && kg == companyId;

    [HttpGet("duplicate-contacts")]
    public async Task<ActionResult<ApiResponse<DuplicateContactReport>>> GetDuplicateContacts(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        // read-only diagnostic — อนุญาต Owner JWT หรือ API key ที่ผูกกับบริษัทนี้
        // (ให้ integration เช่น TakeTime กดดูจากแอปตัวเองผ่าน X-Api-Key ได้).
        // endpoint ที่ลบ/ล้างข้อมูลด้านล่างยังคง Owner JWT เท่านั้น.
        if (!IsCompanyScopedApiKey(companyId) && !await IsOwnerAsync(companyId, userId)) return Forbid();

        var contacts = await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new RawContact(c.Id, c.Name, c.TaxId, c.ExternalId))
            .ToListAsync();

        // จำนวนเอกสารต่อ contact (ช่วยตัดสินว่าจะเก็บตัวไหนตอน merge)
        var docCounts = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted)
            .GroupBy(d => d.ContactId)
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
