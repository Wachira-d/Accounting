using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Accounting.Services.Implementations;

/// <summary>
/// Orchestrate cross-tenant B2B document flows:
///   • Auto-route Sent documents to the partner company's inbox.
///   • Approve incoming docs with stamped signature on the source side.
///   • Auto-create downstream documents (Quotation approval → PO → Invoice).
///
/// Trust model:
///   • Both sides must accept a TradingPartnership before any auto-flow.
///   • Every routing event is recorded in CrossTenantDocumentLink with an
///     immutable SnapshotJson at approval time, so post-edits at the
///     source don't change what the approver actually saw.
///
/// Toggle model:
///   • WorkflowAutomationConfig per company controls every auto step.
///   • Defaults are conservative (everything off). Companies opt in to
///     the level of automation matching their internal control standards.
/// </summary>
public class CrossTenantWorkflowService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CrossTenantWorkflowService> _logger;

    public CrossTenantWorkflowService(AccountingDbContext db, ILogger<CrossTenantWorkflowService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════
    // PARTNERSHIP MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Look up the partner tenant by TaxId and create a pending
    /// invitation. Returns null when no Company exists for that TaxId —
    /// the caller can fall back to email-based invite in that case.</summary>
    public async Task<TradingPartnership?> InvitePartnerAsync(
        Guid invitingCompanyId, string partnerTaxId, Guid invitedByUserId, string? message)
    {
        if (string.IsNullOrWhiteSpace(partnerTaxId)) throw new ArgumentException("ต้องระบุเลขประจำตัวผู้เสียภาษีของพาร์ทเนอร์");

        var partner = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TaxId == partnerTaxId && !c.IsDeleted && c.Id != invitingCompanyId);
        if (partner == null) return null;

        var (a, b) = OrderPair(invitingCompanyId, partner.Id);
        var existing = await _db.TradingPartnerships
            .FirstOrDefaultAsync(p => p.CompanyAId == a && p.CompanyBId == b);
        if (existing != null)
        {
            if (existing.Status == TradingPartnershipStatus.Rejected)
            {
                // Resending after a rejection — reset to Pending so the
                // partner sees the new invite fresh.
                existing.Status = TradingPartnershipStatus.Pending;
                existing.InvitedByCompanyId = invitingCompanyId;
                existing.InvitedByUserId = invitedByUserId;
                existing.InvitationMessage = message;
                existing.RejectedAt = null;
                existing.RejectionReason = null;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return existing;
        }

        var p = new TradingPartnership
        {
            CompanyAId = a,
            CompanyBId = b,
            Status = TradingPartnershipStatus.Pending,
            InvitedByCompanyId = invitingCompanyId,
            InvitedByUserId = invitedByUserId,
            InvitationMessage = message,
            CreatedBy = invitedByUserId.ToString(),
        };
        _db.TradingPartnerships.Add(p);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Trading partnership invited: {A} → {B} (id={Id})", invitingCompanyId, partner.Id, p.Id);
        return p;
    }

    public async Task<TradingPartnership> AcceptPartnershipAsync(
        Guid acceptingCompanyId, Guid partnershipId, Guid acceptedByUserId)
    {
        var p = await _db.TradingPartnerships
            .FirstOrDefaultAsync(x => x.Id == partnershipId)
            ?? throw new InvalidOperationException("ไม่พบ trading partnership");
        if (p.CompanyAId != acceptingCompanyId && p.CompanyBId != acceptingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ได้เป็นส่วนหนึ่งของ partnership นี้");
        if (p.InvitedByCompanyId == acceptingCompanyId)
            throw new InvalidOperationException("ผู้เชิญไม่สามารถยอมรับเอง — รอผู้รับเชิญตอบกลับ");
        if (p.Status == TradingPartnershipStatus.Accepted) return p;

        p.Status = TradingPartnershipStatus.Accepted;
        p.AcceptedAt = DateTime.UtcNow;
        p.AcceptedByUserId = acceptedByUserId;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return p;
    }

    public async Task<TradingPartnership> RejectPartnershipAsync(
        Guid rejectingCompanyId, Guid partnershipId, string? reason)
    {
        var p = await _db.TradingPartnerships
            .FirstOrDefaultAsync(x => x.Id == partnershipId)
            ?? throw new InvalidOperationException("ไม่พบ trading partnership");
        if (p.CompanyAId != rejectingCompanyId && p.CompanyBId != rejectingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ได้เป็นส่วนหนึ่งของ partnership นี้");

        p.Status = TradingPartnershipStatus.Rejected;
        p.RejectedAt = DateTime.UtcNow;
        p.RejectionReason = reason;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return p;
    }

    // ═══════════════════════════════════════════════════════════════════
    // DOCUMENT ROUTING (hook — called when a Document becomes "Sent")
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Called by the document service when a Document moves into
    /// Sent status. If the recipient (Contact) maps to a Company we have
    /// an Accepted trading partnership with, a CrossTenantDocumentLink in
    /// PendingApproval state is created — and the receiving company's
    /// inbox now shows the doc.
    /// </summary>
    public async Task<CrossTenantDocumentLink?> OnDocumentSentAsync(Document doc)
    {
        if (doc == null) return null;

        // Only route doc types that have a cross-tenant approval workflow:
        // Quotations are the canonical case. We also route PO/Invoice for
        // visibility but the linkType encodes which one.
        var linkType = doc.DocumentType switch
        {
            DocumentType.Quotation => CrossTenantLinkType.QuotationFlow,
            DocumentType.PurchaseOrder => CrossTenantLinkType.PoFlow,
            DocumentType.Invoice or DocumentType.TaxInvoice => CrossTenantLinkType.InvoiceFlow,
            DocumentType.Receipt or DocumentType.ReceiptVoucher => CrossTenantLinkType.ReceiptFlow,
            _ => (CrossTenantLinkType?)null
        };
        if (linkType == null) return null;

        // Lookup recipient Contact → corresponding Company (by TaxId)
        var contact = await _db.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == doc.ContactId && !c.IsDeleted);
        if (contact == null || string.IsNullOrEmpty(contact.TaxId)) return null;

        var partnerCompany = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TaxId == contact.TaxId && !c.IsDeleted && c.Id != doc.CompanyId);
        if (partnerCompany == null) return null;

        var (a, b) = OrderPair(doc.CompanyId, partnerCompany.Id);
        var partnership = await _db.TradingPartnerships
            .FirstOrDefaultAsync(p => p.CompanyAId == a && p.CompanyBId == b
                && p.Status == TradingPartnershipStatus.Accepted);
        if (partnership == null) return null;

        // Idempotent: if a link already exists for this doc, don't dup.
        var existingLink = await _db.CrossTenantDocumentLinks
            .FirstOrDefaultAsync(l => l.SourceDocumentId == doc.Id && l.LinkType == linkType);
        if (existingLink != null) return existingLink;

        var link = new CrossTenantDocumentLink
        {
            TradingPartnershipId = partnership.Id,
            SourceCompanyId = doc.CompanyId,
            SourceDocumentId = doc.Id,
            TargetCompanyId = partnerCompany.Id,
            LinkType = linkType.Value,
            Status = CrossTenantLinkStatus.PendingApproval,
            CreatedBy = "system-routing",
        };
        _db.CrossTenantDocumentLinks.Add(link);
        await _db.SaveChangesAsync();

        // Check target's config for auto-approve
        await TryAutoApproveAsync(link);

        return link;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INCOMING APPROVAL
    // ═══════════════════════════════════════════════════════════════════

    public async Task<CrossTenantDocumentLink> ApproveIncomingAsync(
        Guid approvingCompanyId, Guid linkId, Guid actingUserId,
        Guid? signatureUserId = null, bool? overrideAutoCreatePo = null, string? comment = null)
    {
        var link = await _db.CrossTenantDocumentLinks
            .Include(l => l.SourceDocument).ThenInclude(d => d.Lines)
            .FirstOrDefaultAsync(l => l.Id == linkId)
            ?? throw new InvalidOperationException("ไม่พบ document link");
        if (link.TargetCompanyId != approvingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ใช่ผู้รับ");
        if (link.Status != CrossTenantLinkStatus.PendingApproval)
            throw new InvalidOperationException($"link อยู่ในสถานะ {link.Status} — ไม่สามารถอนุมัติได้");

        var config = await GetOrCreateConfigAsync(approvingCompanyId);

        // Resolve signature: prefer signatureUserId arg, else config default,
        // else any active signature of the acting user.
        var signerUserId = signatureUserId ?? config.DefaultApproverUserId ?? actingUserId;
        var signature = await _db.UserSignatures
            .Where(s => !s.IsDeleted && s.IsActive && s.UserId == signerUserId)
            .OrderByDescending(s => s.IsDefault)
            .FirstOrDefaultAsync();
        var signer = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == signerUserId && !u.IsDeleted);

        // ─── Create DocumentApproval on the SOURCE side (A's doc) ───
        var approval = new DocumentApproval
        {
            CompanyId = link.SourceCompanyId,
            DocumentId = link.SourceDocumentId,
            ApprovalType = "External",
            ApproverRole = "Customer",
            StepOrder = 1,
            Status = ApprovalStatus.Approved,
            ApproverUserId = signerUserId,
            ApproverName = signer?.FullName,
            ApproverEmail = signer?.Email,
            SignatureId = signature?.Id,
            SignatureData = signature?.SignatureData,
            SignatureFormat = signature?.SignatureFormat,
            ApprovedAt = DateTime.UtcNow,
            Comments = comment,
            PostApprovalAction = link.LinkType == CrossTenantLinkType.QuotationFlow ? "CreatePO" : null,
            // รอบ 193 R4-1: ลายเซ็นคู่ค้า (บทบาท Customer) ต้องผูกกับเนื้อหาที่เซ็น — ตัวเดียวกับเส้นลายเซ็นลูกค้า
            // (ไม่มี hash ⇒ ใบที่ยังเป็นร่างไม่นับลายเซ็นนี้ · ใบที่อนุมัติแล้วไม่กระทบ)
            SignedContentHash = Accounting.Helpers.DocumentSignedContent.Hash(link.SourceDocument, link.SourceDocument.Lines),
        };
        _db.DocumentApprovals.Add(approval);

        // Also stamp a rendered DocumentSignature row at the source side
        // (matches how the existing approval flow works for in-tenant
        // approvals, so PDF rendering / display logic is identical).
        if (signature != null && config.AutoStampSignature)
        {
            _db.DocumentSignatures.Add(new DocumentSignature
            {
                CompanyId = link.SourceCompanyId,
                DocumentId = link.SourceDocumentId,
                DocumentApprovalId = approval.Id,
                SignerRole = "ผู้อนุมัติ (ภายนอก)",
                SignerName = signer?.FullName ?? "External Approver",
                SignerTitle = "Buyer",
                SignatureData = signature.SignatureData,
                SignatureFormat = signature.SignatureFormat,
                SignedAt = DateTime.UtcNow,
            });
        }

        // ─── Snapshot the source doc so the approval is immutable ───
        link.SnapshotJson = JsonSerializer.Serialize(new
        {
            link.SourceDocument.DocumentNumber,
            link.SourceDocument.DocumentType,
            link.SourceDocument.DocumentDate,
            link.SourceDocument.SubTotal,
            link.SourceDocument.VatAmount,
            link.SourceDocument.TotalAmount,
            Lines = link.SourceDocument.Lines.Select(l => new
            {
                l.Description, l.Quantity, l.UnitPrice, l.Amount, l.VatAmount
            }),
        });

        link.Status = CrossTenantLinkStatus.Approved;
        link.ApproverUserId = actingUserId;
        link.ApprovedAt = DateTime.UtcNow;
        link.DocumentApprovalId = approval.Id;
        link.Comment = comment;
        link.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // ─── Downstream automation ───
        await TryChainDownstreamAsync(link, config, overrideAutoCreatePo, actingUserId);

        return link;
    }

    public async Task<CrossTenantDocumentLink> RejectIncomingAsync(
        Guid rejectingCompanyId, Guid linkId, string reason)
    {
        var link = await _db.CrossTenantDocumentLinks
            .FirstOrDefaultAsync(l => l.Id == linkId)
            ?? throw new InvalidOperationException("ไม่พบ document link");
        if (link.TargetCompanyId != rejectingCompanyId)
            throw new UnauthorizedAccessException("บริษัทไม่ใช่ผู้รับ");
        if (link.Status != CrossTenantLinkStatus.PendingApproval)
            throw new InvalidOperationException($"link อยู่ในสถานะ {link.Status} — ไม่สามารถปฏิเสธได้");

        link.Status = CrossTenantLinkStatus.Rejected;
        link.RejectionReason = reason;
        link.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return link;
    }

    // ═══════════════════════════════════════════════════════════════════
    // AUTO-APPROVAL + DOWNSTREAM CHAIN
    // ═══════════════════════════════════════════════════════════════════

    private async Task TryAutoApproveAsync(CrossTenantDocumentLink link)
    {
        if (link.LinkType != CrossTenantLinkType.QuotationFlow) return;
        var cfg = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);
        if (cfg == null || !cfg.AutoApproveIncomingQuotations) return;
        if (cfg.DefaultApproverUserId == null) return;  // need a signer

        // Amount gating: respect min/max bounds + partnership credit limit
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == link.SourceDocumentId);
        if (doc == null) return;
        var partnership = await _db.TradingPartnerships.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == link.TradingPartnershipId);
        if (cfg.AutoApproveMinAmount.HasValue && doc.TotalAmount < cfg.AutoApproveMinAmount.Value) return;
        if (cfg.AutoApproveMaxAmount.HasValue && doc.TotalAmount > cfg.AutoApproveMaxAmount.Value) return;
        if (partnership?.AutoApproveAmountLimit.HasValue == true
            && doc.TotalAmount > partnership.AutoApproveAmountLimit.Value) return;

        try
        {
            await ApproveIncomingAsync(
                approvingCompanyId: link.TargetCompanyId,
                linkId: link.Id,
                actingUserId: cfg.DefaultApproverUserId.Value,
                comment: "Auto-approved by workflow config");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-approve failed for link {Id}", link.Id);
        }
    }

    private async Task TryChainDownstreamAsync(CrossTenantDocumentLink link, WorkflowAutomationConfig targetCfg,
        bool? overrideAutoCreatePo, Guid actingUserId)
    {
        if (link.LinkType == CrossTenantLinkType.QuotationFlow)
        {
            var shouldCreatePo = overrideAutoCreatePo ?? targetCfg.AutoCreatePoOnQuotationApproval;
            if (!shouldCreatePo) return;
            await CreateDownstreamPoAsync(link, actingUserId);
        }
        else if (link.LinkType == CrossTenantLinkType.PoFlow)
        {
            // Now check the SOURCE side's config (the original quotation
            // issuer received the PO — should they auto-create Invoice?)
            var sourceCfg = await _db.WorkflowAutomationConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyId == link.TargetCompanyId);  // note: target of PO = quotation issuer
            if (sourceCfg?.AutoCreateInvoiceFromIncomingPo == true)
                await CreateDownstreamInvoiceAsync(link, actingUserId);
        }
    }

    private async Task CreateDownstreamPoAsync(CrossTenantDocumentLink approvedQuoteLink, Guid actingUserId)
    {
        // Build a PO at the BUYER side (= target of the quotation link)
        // pointing back to the supplier (= source of the quotation).
        var quote = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == approvedQuoteLink.SourceDocumentId);
        if (quote == null) return;

        // The buyer needs a Contact for the supplier. Find by TaxId of the
        // supplier company. If no contact exists yet, create one.
        var supplier = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == approvedQuoteLink.SourceCompanyId);
        if (supplier == null) return;

        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา (Helpers/ContactTaxBranchKey) — สาขาของบริษัทคู่ค้า = Company.BranchCode
        // เดิม `c.TaxId == supplier.TaxId` หยิบแถวไหนก็ได้ของเลขนั้น และเมื่อบริษัทคู่ค้าไม่มีเลขภาษี EF แปลเป็น
        // `TaxId IS NULL` ⇒ ผูกผู้ติดต่อรายแรกที่ไม่มีเลข (คนละรายสิ้นเชิง)
        var supplierContact = await FindPartnerContactAsync(
            approvedQuoteLink.TargetCompanyId, supplier, asSupplier: true);
        if (supplierContact == null)
        {
            supplierContact = NewPartnerContact(approvedQuoteLink.TargetCompanyId, supplier, asSupplier: true);
            _db.Contacts.Add(supplierContact);
            await _db.SaveChangesAsync();
        }

        var po = new Document
        {
            CompanyId = approvedQuoteLink.TargetCompanyId,
            DocumentNumber = $"PO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}",
            DocumentType = DocumentType.PurchaseOrder,
            Status = DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow.Date,
            ContactId = supplierContact.Id,
            SubTotal = quote.SubTotal,
            VatAmount = quote.VatAmount,
            TotalAmount = quote.TotalAmount,
            BalanceDue = quote.TotalAmount,
            Reference = $"Quotation {quote.DocumentNumber}",
            Notes = $"Auto-created from approved Quotation (cross-tenant link {approvedQuoteLink.Id})",
            CreatedBy = "cross-tenant-chain",
        };
        int order = 1;
        foreach (var line in quote.Lines)
        {
            po.Lines.Add(new DocumentLine
            {
                LineOrder = order++,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = line.VatAmount,
            });
        }
        _db.Documents.Add(po);
        await _db.SaveChangesAsync();

        approvedQuoteLink.TargetDocumentId = po.Id;
        approvedQuoteLink.Status = CrossTenantLinkStatus.AutoChained;

        // Create the reverse routing link — PO back to supplier (A's side)
        var poLink = new CrossTenantDocumentLink
        {
            TradingPartnershipId = approvedQuoteLink.TradingPartnershipId,
            SourceCompanyId = po.CompanyId,
            SourceDocumentId = po.Id,
            TargetCompanyId = approvedQuoteLink.SourceCompanyId,
            LinkType = CrossTenantLinkType.PoFlow,
            Status = CrossTenantLinkStatus.PendingApproval,
            CreatedBy = "cross-tenant-chain",
        };
        _db.CrossTenantDocumentLinks.Add(poLink);
        await _db.SaveChangesAsync();

        // The supplier's config may auto-chain to invoice — re-enter the
        // chain logic with the PO as the trigger.
        var sourceCfg = await _db.WorkflowAutomationConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CompanyId == approvedQuoteLink.SourceCompanyId);
        if (sourceCfg?.AutoCreateInvoiceFromIncomingPo == true)
            await CreateDownstreamInvoiceAsync(poLink, actingUserId);
    }

    /// <summary>ผู้ติดต่อที่แทน "บริษัทคู่ค้าในระบบเดียวกัน" ในผังผู้ติดต่อของ <paramref name="ownerCompanyId"/> —
    /// ลำดับกุญแจ: (1) แถวที่ผูกกับบริษัทนั้นแล้ว (ExternalSystem "NextAccTenant" + Id บริษัท) → (2) เลขภาษี + สาขา
    /// (Helpers/ContactTaxBranchKey ตัวเดียวกับทุกทางเข้า) → (3) ชื่อบนชุด SoftScope · เลขนี้มีแล้วแต่คนละสาขา ⇒ คืน null ให้ผู้เรียกสร้างแถว
    /// ของสาขานั้น · แถวที่จับได้และยังไม่มีเลขรับเลขของคู่ค้า (AdoptTaxId) · "-" = ไม่มีเลข (รอบ 193 ทีม C3 + ฝ่ายค้านสองรอบ)</summary>
    private async Task<Contact?> FindPartnerContactAsync(Guid ownerCompanyId, Company partner, bool asSupplier)
    {
        var partnerKey = partner.Id.ToString();
        var branch = string.IsNullOrWhiteSpace(partner.BranchCode) ? Accounting.Helpers.TaxBranchCode.HeadOffice : partner.BranchCode;
        // ฝ่ายค้าน C-8: "-" (บริษัทที่สมัครใหม่ยังไม่กรอกเลข · AuthService) = ไม่มีเลข — เดิมถูกเทียบตรงตัว
        // ⇒ บริษัทคู่ค้ารายที่สองที่ยังไม่มีเลขได้ผู้ติดต่อ "-" ของรายแรก
        var taxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(partner.TaxId) ? partner.TaxId : null;

        // (1) กุญแจแรกเสมอ = แถวที่ผูกกับบริษัทคู่ค้านี้แล้ว (ฝ่ายค้านรอบสอง R2-C9 — เดิมไม่มีกุญแจนี้ ⇒ คู่ค้าที่กรอกเลขทีหลัง
        //     ได้ผู้ติดต่อแถวที่สอง) · ข้ามเมื่อแถวนั้นถือเลข/สาขาอื่นแล้ว (คู่ค้าเปลี่ยนสาขา ⇒ ผู้ติดต่อของสาขาใหม่ §86/4)
        var found = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == ownerCompanyId && !c.IsDeleted
            && c.ExternalSystem == "NextAccTenant" && c.ExternalId == partnerKey);
        if (found != null && taxId != null && Accounting.Helpers.ContactTaxBranchKey.HasTaxId(found.TaxId)
            && !Accounting.Helpers.ContactTaxBranchKey.Pick(
                new[] { new Accounting.Helpers.ContactKeyCandidate(found.Id, found.TaxId, found.BranchCode) }, taxId, branch).Found)
            found = null;
        var matchedBy = Accounting.Helpers.ContactMatchKind.ExternalKey;

        // (2) เลขภาษี + สาขา
        var key = default(Accounting.Helpers.ContactKeyMatch);
        if (found == null && taxId != null)
        {
            key = await Accounting.Helpers.ContactTaxBranchKey.FindAsync(_db.Contacts, ownerCompanyId, taxId, branch);
            if (key.ContactId is Guid id)
                found = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == id && c.CompanyId == ownerCompanyId && !c.IsDeleted);
            matchedBy = Accounting.Helpers.ContactMatchKind.TaxKey;
        }

        // (3) ชื่อ บนชุด SoftScope (เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข · เลขมีแล้วคนละสาขา ⇒ ห้าม) · ไม่เอาแถวที่ผูกกับบริษัทอื่น
        var softScope = Accounting.Helpers.ContactTaxBranchKey.SoftScope(_db.Contacts, ownerCompanyId, taxId, key);
        if (found == null && softScope != null && !string.IsNullOrWhiteSpace(partner.Name))
        {
            matchedBy = Accounting.Helpers.ContactMatchKind.ExactName;
            found = await softScope
                .Where(c => !c.IsDeleted && c.Name == partner.Name
                    && (asSupplier ? c.IsSupplier : c.IsCustomer)
                    && !(c.ExternalSystem == "NextAccTenant" && c.ExternalId != partnerKey))
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync();
        }

        // แถวที่ยังไม่มีเลขรับเลข + สาขาของคู่ค้า (ตัวช่วยเดียวของทุกทางเข้า — R2-C5/R2-C9) · แถวลูกค้าทั่วไปไม่รับเลข ⇒ สร้างใหม่ (B8)
        var adopt = Accounting.Helpers.ContactTaxBranchKey.AdoptTaxId(found, taxId, branch, matchedBy);
        if (adopt == Accounting.Helpers.ContactAdoptOutcome.Reject) found = null;

        if (found != null)
        {
            // ผูกกุญแจถ้ายังไม่ผูกกับระบบใด
            var dirty = adopt == Accounting.Helpers.ContactAdoptOutcome.Adopted;
            if (string.IsNullOrWhiteSpace(found.ExternalSystem) && string.IsNullOrWhiteSpace(found.ExternalId))
            {
                found.ExternalSystem = "NextAccTenant";
                found.ExternalId = partnerKey;
                dirty = true;
            }
            if (dirty) await _db.SaveChangesAsync();
        }
        return found;
    }

    /// <summary>แถวผู้ติดต่อใหม่ของบริษัทคู่ค้า — ชนิด + รหัสสาขาจากตัวตัดสินตัวเดียว (Helpers/ContactTypeResolver)
    /// เพื่อให้ใบกำกับที่ออกให้คู่ค้ารายนี้มีสาขาตาม §86/4 (เดิมไม่ตั้งสาขาเลย)</summary>
    private static Contact NewPartnerContact(Guid ownerCompanyId, Company partner, bool asSupplier)
    {
        var identity = Accounting.Helpers.ContactTypeResolver.ResolveWithBranch(
            declared: null, taxId: partner.TaxId,
            branchCode: string.IsNullOrWhiteSpace(partner.BranchCode) ? Accounting.Helpers.TaxBranchCode.HeadOffice : partner.BranchCode,
            name: partner.Name);
        return new Contact
        {
            CompanyId = ownerCompanyId,
            Name = partner.Name,
            TaxId = Accounting.Helpers.ContactTaxBranchKey.HasTaxId(partner.TaxId) ? partner.TaxId : null,   // "-" ไม่ใช่เลข (C-8)
            BranchCode = identity.BranchCode,
            ContactType = identity.Type,
            Address = partner.Address,
            IsSupplier = asSupplier,
            IsCustomer = !asSupplier,
            // ผูกกลับไปยังบริษัทคู่ค้า — กุญแจแรกของ FindPartnerContactAsync (R2-C9 · ชื่อระบบเดียวกับใบค่าบริการแพลตฟอร์ม)
            ExternalSystem = "NextAccTenant",
            ExternalId = partner.Id.ToString(),
            CreatedBy = "cross-tenant-routing",
        };
    }

    private async Task CreateDownstreamInvoiceAsync(CrossTenantDocumentLink poLink, Guid actingUserId)
    {
        var po = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == poLink.SourceDocumentId);
        if (po == null) return;

        // The supplier (target of the PO) creates an Invoice back to the buyer
        var buyer = await _db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == poLink.SourceCompanyId);
        if (buyer == null) return;

        // รอบ 193 ทีม C3: คีย์เลขภาษี + สาขา — ใบกำกับที่ออกให้ผู้ซื้อต้องเป็นสาขาของผู้ซื้อจริง (§86/4)
        var buyerContact = await FindPartnerContactAsync(
            poLink.TargetCompanyId, buyer, asSupplier: false);
        if (buyerContact == null)
        {
            buyerContact = NewPartnerContact(poLink.TargetCompanyId, buyer, asSupplier: false);
            _db.Contacts.Add(buyerContact);
            await _db.SaveChangesAsync();
        }

        var inv = new Document
        {
            CompanyId = poLink.TargetCompanyId,
            // ⚠️ ต้องเป็น DRAFT- placeholder — เดิม hardcode "INV-{วันที่}-{GUID}"
            // ให้เอกสารชนิด **TaxInvoice** ⇒ ใบกำกับถือเลขสุ่มที่ไม่เรียงลำดับ
            // ไม่อยู่เล่ม TIV และเพราะเลขไม่ใช่ DRAFT- ตอนอนุมัติก็ไม่ออกเลขใหม่
            // ให้ (ApproveDocumentAsync ออกเลขเฉพาะใบที่ยังเป็น DRAFT-) = ติดถาวร
            // ผิด §86/4 (เลขใบกำกับต้องเรียงต่อเนื่องไม่ขาดช่วง). ใช้ DRAFT- แล้ว
            // เลขจริงจะออกจาก DocumentNumberGenerator ตอน AR อนุมัติ เหมือนใบอื่น
            DocumentNumber = $"DRAFT-{Guid.NewGuid()}",
            DocumentType = DocumentType.TaxInvoice,
            Status = DocumentStatus.Draft,   // Invoice is Draft so AR team reviews before sending
            DocumentDate = DateTime.UtcNow.Date,
            ContactId = buyerContact.Id,
            SubTotal = po.SubTotal,
            VatAmount = po.VatAmount,
            TotalAmount = po.TotalAmount,
            BalanceDue = po.TotalAmount,
            Reference = $"PO {po.DocumentNumber}",
            Notes = $"Auto-created from incoming PO (cross-tenant link {poLink.Id})",
            CreatedBy = "cross-tenant-chain",
        };
        int order = 1;
        foreach (var line in po.Lines)
        {
            inv.Lines.Add(new DocumentLine
            {
                LineOrder = order++,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = line.VatAmount,
            });
        }
        _db.Documents.Add(inv);
        await _db.SaveChangesAsync();

        poLink.TargetDocumentId = inv.Id;
        poLink.Status = CrossTenantLinkStatus.AutoChained;

        // Forward routing link — Invoice to buyer
        _db.CrossTenantDocumentLinks.Add(new CrossTenantDocumentLink
        {
            TradingPartnershipId = poLink.TradingPartnershipId,
            SourceCompanyId = inv.CompanyId,
            SourceDocumentId = inv.Id,
            TargetCompanyId = poLink.SourceCompanyId,
            LinkType = CrossTenantLinkType.InvoiceFlow,
            Status = CrossTenantLinkStatus.PendingApproval,
            CreatedBy = "cross-tenant-chain",
        });
        await _db.SaveChangesAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONFIG
    // ═══════════════════════════════════════════════════════════════════

    public async Task<WorkflowAutomationConfig> GetOrCreateConfigAsync(Guid companyId)
    {
        var cfg = await _db.WorkflowAutomationConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId);
        if (cfg != null) return cfg;
        cfg = new WorkflowAutomationConfig { CompanyId = companyId, CreatedBy = "system" };
        _db.WorkflowAutomationConfigs.Add(cfg);
        await _db.SaveChangesAsync();
        return cfg;
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static (Guid A, Guid B) OrderPair(Guid x, Guid y)
        => x.CompareTo(y) < 0 ? (x, y) : (y, x);
}
