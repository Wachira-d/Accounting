using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// CRUD + lifecycle for <see cref="CmsLead"/> rows. The public-facing
/// storefront writes here through <see cref="CreateAsync"/>; the
/// site-owner UI reads through <see cref="ListAsync"/> + state
/// transitions.
///
/// On qualification (lead → Qualified or higher), a Contact row is
/// either matched (by tax-id, then by email) or created so subsequent
/// quotations/invoices attach to a real customer record.
/// </summary>
public class CmsLeadService
{
    private readonly AccountingDbContext _db;
    private readonly Services.Interfaces.IDocumentService _docService;

    public CmsLeadService(AccountingDbContext db, Services.Interfaces.IDocumentService docService)
    { _db = db; _docService = docService; }

    // ====================================================================
    // PUBLIC submission (called from storefront — no auth)
    // ====================================================================

    public async Task<LeadDetailResponse> CreateAsync(Guid companyId, Guid siteId, CreateLeadRequest req)
    {
        var lead = new CmsLead
        {
            CompanyId = companyId,
            SiteId = siteId,
            LeadType = req.LeadType,
            Status = LeadStatus.New,
            SourceSlug = Trim(req.SourceSlug, 100),
            CustomerName = Trim(req.CustomerName, 200),
            CustomerEmail = Trim(req.CustomerEmail, 200),
            CustomerPhone = Trim(req.CustomerPhone, 50),
            CustomerCompany = Trim(req.CustomerCompany, 200),
            CustomerTaxId = Trim(req.CustomerTaxId, 50),
            Message = Trim(req.Message, 2000),
            DataJson = req.Fields != null && req.Fields.Count > 0
                ? JsonSerializer.Serialize(req.Fields)
                : null,
            LeadNumber = await NextLeadNumberAsync(companyId),
            CreatedBy = req.CustomerEmail ?? "storefront"
        };
        _db.Set<CmsLead>().Add(lead);
        await _db.SaveChangesAsync();
        return MapDetail(lead);
    }

    // ====================================================================
    // OWNER reads + state transitions (authenticated)
    // ====================================================================

    public async Task<PagedResponse<LeadListResponse>> ListAsync(
        Guid companyId, Guid siteId,
        LeadType? type, LeadStatus? status, string? search,
        int page = 1, int pageSize = 50)
    {
        var q = _db.Set<CmsLead>()
            .AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.SiteId == siteId && !l.IsDeleted);

        if (type.HasValue) q = q.Where(l => l.LeadType == type);
        if (status.HasValue) q = q.Where(l => l.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search}%";
            q = q.Where(l =>
                EF.Functions.ILike(l.LeadNumber, s)
                || (l.CustomerName != null && EF.Functions.ILike(l.CustomerName, s))
                || (l.CustomerEmail != null && EF.Functions.ILike(l.CustomerEmail, s))
                || (l.CustomerPhone != null && EF.Functions.ILike(l.CustomerPhone, s))
                || (l.CustomerCompany != null && EF.Functions.ILike(l.CustomerCompany, s)));
        }

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new LeadListResponse(
                l.Id, l.LeadNumber, l.LeadType, l.Status,
                l.CustomerName, l.CustomerEmail, l.CustomerPhone,
                l.SourceSlug, l.CreatedAt))
            .ToListAsync();

        return new PagedResponse<LeadListResponse>(items, total, page, pageSize,
            (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<LeadDetailResponse?> GetAsync(Guid companyId, Guid siteId, Guid leadId)
    {
        var lead = await _db.Set<CmsLead>()
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.CompanyId == companyId && l.SiteId == siteId
                                   && l.Id == leadId && !l.IsDeleted);
        return lead == null ? null : MapDetail(lead);
    }

    public async Task<LeadDetailResponse?> UpdateStatusAsync(
        Guid companyId, Guid siteId, Guid leadId,
        UpdateLeadStatusRequest req, string userId)
    {
        var lead = await _db.Set<CmsLead>()
            .FirstOrDefaultAsync(l => l.CompanyId == companyId && l.SiteId == siteId
                                   && l.Id == leadId && !l.IsDeleted);
        if (lead == null) return null;

        var now = DateTime.UtcNow;
        lead.Status = req.Status;
        if (!string.IsNullOrEmpty(req.InternalNotes))
        {
            var stamp = $"[{now:yyyy-MM-dd HH:mm}] {userId}: {req.InternalNotes}";
            lead.InternalNotes = string.IsNullOrEmpty(lead.InternalNotes)
                ? stamp : lead.InternalNotes + "\n" + stamp;
        }
        if (req.AssignedToUserId.HasValue) lead.AssignedToUserId = req.AssignedToUserId;
        if (!string.IsNullOrEmpty(req.AssignedToName)) lead.AssignedToName = req.AssignedToName;

        // Match-or-create Contact on first qualification + mark
        // timestamp transitions.
        if (req.Status == LeadStatus.Qualified && lead.QualifiedAt == null)
        {
            lead.QualifiedAt = now;
            await EnsureContactLinkedAsync(lead);
        }
        else if (req.Status == LeadStatus.Quoted && lead.QuotedAt == null) lead.QuotedAt = now;
        else if (req.Status == LeadStatus.Won && lead.WonAt == null) lead.WonAt = now;
        else if (req.Status == LeadStatus.Lost && lead.LostAt == null)
        {
            lead.LostAt = now;
            lead.LostReason = req.LostReason;
        }

        lead.UpdatedAt = now;
        lead.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return MapDetail(lead);
    }

    /// <summary>Spin up a Quotation document seeded from this lead.
    /// Auto-qualifies the lead if not already, ensures a Contact
    /// exists, then calls IDocumentService.CreateAsync with a single
    /// placeholder line. Owner can refine the line items in the
    /// document form afterward. The lead transitions to Quoted +
    /// gets its ErpDocumentId set.</summary>
    public async Task<LeadDetailResponse?> ConvertToQuotationAsync(
        Guid companyId, Guid siteId, Guid leadId,
        ConvertLeadToQuotationRequest req, string userId)
    {
        var lead = await _db.Set<CmsLead>()
            .FirstOrDefaultAsync(l => l.CompanyId == companyId && l.SiteId == siteId
                                   && l.Id == leadId && !l.IsDeleted);
        if (lead == null) return null;

        if (lead.ContactId == null) await EnsureContactLinkedAsync(lead);
        if (lead.ContactId == null) throw new InvalidOperationException("ไม่สามารถสร้าง Contact จากข้อมูล lead ที่มี");

        // Compose a one-line placeholder; owner edits the document
        // afterwards to break out actual items / pricing / tax.
        var lineDesc = !string.IsNullOrEmpty(req.QuotationNotes) ? req.QuotationNotes!
                     : !string.IsNullOrEmpty(lead.Message) ? lead.Message!
                     : $"คำขอจาก lead {lead.LeadNumber} ({lead.LeadType})";
        var amount = req.EstimatedAmount ?? 0m;

        // อัตรา VAT จาก OutputVatRate ตัวเดียว — เดิม 7 ตายตัว ⇒ ใบเสนอราคาของบริษัทที่ไม่จด VAT
        // เสนอ VAT 7% ให้ลูกค้า (§90/2 ถ้าใบนี้ถูกแปลงเป็นใบกำกับ) · รอบ 193 S-10
        var vatProfile = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.IsVatRegistered, c.VatRate })
            .FirstOrDefaultAsync();
        var quoteVatRate = vatProfile == null ? 0m
            : Accounting.Helpers.OutputVatRate.ForCompany(vatProfile.IsVatRegistered, vatProfile.VatRate);

        var docReq = new Models.DTOs.Document.CreateDocumentRequest(
            DocumentType: Models.Enums.DocumentType.Quotation,
            DocumentDate: DateTime.UtcNow.Date,
            DueDate: null,
            ContactId: lead.ContactId.Value,
            Reference: lead.LeadNumber,
            // ★ รอบ 193 — เดิมส่ง `lead.InternalNotes` เข้า `Notes` ซึ่ง **พิมพ์ลงใบเสนอราคาที่ส่งลูกค้า**
            // ⇒ บันทึกภายในของทีมขาย (เช่น "ลูกค้าต่อราคาเก่ง") หลุดถึงลูกค้า · ย้ายไป InternalNotes ข้างล่าง
            Notes: null,
            Lines: new List<Models.DTOs.Document.DocumentLineRequest>
            {
                new(Description: lineDesc, Quantity: 1m, Unit: "งาน",
                    UnitPrice: amount, DiscountPercent: 0m, VatRate: quoteVatRate,
                    WithholdingTaxRate: 0m, AccountId: null)
            });

        var doc = await _docService.CreateDocumentAsync(companyId, docReq, userId);

        // บันทึกภายในของ lead → หมายเหตุภายในของเอกสาร (ไม่พิมพ์ · ช่องเดียวกับที่ DocumentService ใช้แทน Notes)
        // CreateDocumentRequest ไม่มีช่อง InternalNotes จึงเติมหลังสร้าง (เอกสารยังเป็นร่าง)
        if (!string.IsNullOrWhiteSpace(lead.InternalNotes))
        {
            var quote = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == doc.Id && d.CompanyId == companyId);
            if (quote != null)
            {
                var carried = $"[จาก lead {lead.LeadNumber}] {lead.InternalNotes!.Trim()}";
                quote.InternalNotes = string.IsNullOrWhiteSpace(quote.InternalNotes)
                    ? carried
                    : quote.InternalNotes.TrimEnd() + "\n\n" + carried;
            }
        }

        lead.ErpDocumentId = doc.Id;
        lead.Status = LeadStatus.Quoted;
        lead.QuotedAt = DateTime.UtcNow;
        lead.UpdatedAt = DateTime.UtcNow;
        lead.UpdatedBy = userId;
        await _db.SaveChangesAsync();
        return MapDetail(lead);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private async Task EnsureContactLinkedAsync(CmsLead lead)
    {
        if (lead.ContactId.HasValue) return;

        // Match by tax-id first (most reliable for businesses),
        // then by email. Skip name-only matching — too many false
        // positives, the user can manually merge later if needed.
        // รอบ 193 ทีม C3 (ฝ่ายค้าน C-6 · แก้เฉพาะการจับคู่ผู้ติดต่อ): คีย์เลขภาษี + สาขาตัวเดียว (Helpers/ContactTaxBranchKey)
        // — lead ไม่มีช่องสาขา ⇒ "ไม่ระบุ" = แถวสำนักงานใหญ่ก่อน (เดิม `c.TaxId ==` หยิบแถวไหนก็ได้ของเลขนั้น) ·
        // อีเมลจับได้เฉพาะชุด SoftScope (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข — เดิมอีเมลตรงแถวที่ถือเลขอื่น = ผูกคนละนิติบุคคล)
        Contact? existing = null;
        var taxKey = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(
            _db.Contacts.Where(c => !c.IsDeleted), lead.CompanyId, lead.CustomerTaxId, branchCode: null);
        if (taxKey.ContactId is Guid keyId)
            existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == keyId && c.CompanyId == lead.CompanyId);
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(
            _db.Contacts.Where(c => !c.IsDeleted), lead.CompanyId, lead.CustomerTaxId, taxKey);
        if (existing == null && softScope != null && !string.IsNullOrEmpty(lead.CustomerEmail))
            existing = await softScope.FirstOrDefaultAsync(c => c.Email == lead.CustomerEmail);

        if (existing != null)
        {
            lead.ContactId = existing.Id;
            // Mark as customer if previously only a supplier.
            if (!existing.IsCustomer)
            {
                existing.IsCustomer = true;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            return;
        }

        // Create a new Contact. Use the company name when present
        // (business customer), else the customer's name (individual).
        var contact = new Contact
        {
            CompanyId = lead.CompanyId,
            Name = !string.IsNullOrEmpty(lead.CustomerCompany)
                ? lead.CustomerCompany!
                : (lead.CustomerName ?? "(ไม่ระบุชื่อ)"),
            ContactType = !string.IsNullOrEmpty(lead.CustomerCompany)
                ? Models.Enums.ContactType.JuristicPerson
                : Models.Enums.ContactType.Individual,
            TaxId = lead.CustomerTaxId,
            Email = lead.CustomerEmail,
            Phone = lead.CustomerPhone,
            IsCustomer = true,
            IsSupplier = false,
            CreatedBy = "lead-qualify"
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();
        lead.ContactId = contact.Id;
    }

    private async Task<string> NextLeadNumberAsync(Guid companyId)
    {
        // L-YYMM-NNNN — sequential per company per month
        var prefix = $"L-{DateTime.UtcNow:yyMM}-";
        var lastNum = await _db.Set<CmsLead>()
            .Where(l => l.CompanyId == companyId && l.LeadNumber.StartsWith(prefix))
            .Select(l => l.LeadNumber)
            .OrderByDescending(n => n)
            .FirstOrDefaultAsync();
        var seq = 1;
        if (lastNum != null && int.TryParse(lastNum.AsSpan(prefix.Length), out var n)) seq = n + 1;
        return $"{prefix}{seq:D4}";
    }

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static LeadDetailResponse MapDetail(CmsLead l)
    {
        Dictionary<string, object?>? fields = null;
        if (!string.IsNullOrEmpty(l.DataJson))
        {
            try { fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(l.DataJson); }
            catch { /* malformed JSON — ignore */ }
        }
        return new LeadDetailResponse(
            l.Id, l.LeadNumber, l.LeadType, l.Status, l.SourceSlug,
            l.CustomerName, l.CustomerEmail, l.CustomerPhone,
            l.CustomerCompany, l.CustomerTaxId, l.Message, fields,
            l.AssignedToUserId, l.AssignedToName,
            l.QualifiedAt, l.QuotedAt, l.WonAt, l.LostAt, l.LostReason,
            l.InternalNotes, l.ErpDocumentId, l.ContactId, l.CreatedAt);
    }
}
