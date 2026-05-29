using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Expense;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ExpenseClaimService : IExpenseClaimService
{
    private readonly AccountingDbContext _db;
    private readonly IAccountingService? _accountingService;
    private readonly IDocumentService? _documentService;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<ExpenseClaimService>? _logger;

    public ExpenseClaimService(AccountingDbContext db, IAccountingService? accountingService = null,
        IDocumentService? documentService = null, INotificationEngine? notify = null,
        ILogger<ExpenseClaimService>? logger = null)
    {
        _db = db;
        _accountingService = accountingService;
        _documentService = documentService;
        _notify = notify;
        _logger = logger;
    }

    /// <summary>Fire-and-forget HR notification. Resolves the claim
    /// submitter's Employee record (if any) so the engine can route to
    /// DirectManager / DepartmentHead — falls back gracefully when the
    /// user has no employee record (e.g. external accountant).</summary>
    private async Task NotifyClaimEventAsync(Guid companyId, string eventKey, ExpenseClaim claim,
        Guid? actorUserId, string title, string message)
    {
        if (_notify == null) return;
        var employeeId = await _db.Employees
            .Where(e => e.CompanyId == companyId && e.UserId == claim.SubmittedByUserId && !e.IsDeleted)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync();
        await _notify.DispatchAsync(companyId, eventKey, new NotificationContext
        {
            Title = title, Message = message,
            ActionUrl = "/pages/expense.html",
            EntityType = "ExpenseClaim", EntityId = claim.Id,
            RequesterEmployeeId = employeeId,
            ActorUserId = actorUserId,
        });
    }

    public async Task<ExpenseClaimResponse> CreateAsync(Guid companyId, CreateExpenseClaimRequest request, Guid submittedByUserId)
    {
        var expYearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var expPrefix = $"EXP-{expYearMonth}-";
        var maxExp = await _db.ExpenseClaims
            .IgnoreQueryFilters()
            .Where(e => e.CompanyId == companyId && e.ClaimNumber.StartsWith(expPrefix))
            .Select(e => e.ClaimNumber)
            .MaxAsync() as string;
        var expSeq = 1;
        if (maxExp != null)
        {
            var lastPart = maxExp.Substring(expPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) expSeq = parsed + 1;
        }
        var claimNumber = $"{expPrefix}{expSeq:D4}";

        // No-receipt validation per §65 ทวิ — reason is mandatory because
        // it ends up on the auto-generated CertificateInLieu document and
        // is what the Revenue Department audits.
        if (request.NoReceipt && string.IsNullOrWhiteSpace(request.NoReceiptReason))
            throw new InvalidOperationException(
                "เบิกค่าใช้จ่ายไม่มีใบเสร็จต้องระบุเหตุผล (เช่น 'ผู้ขายไม่ออก', 'ใบเสร็จสูญหาย', 'ตลาดสด') — §65 ทวิ");

        var claim = new ExpenseClaim
        {
            CompanyId = companyId,
            ClaimNumber = claimNumber,
            Title = request.Title,
            Description = request.Description,
            ExpenseDate = request.ExpenseDate,
            SubmittedByUserId = submittedByUserId,
            NoReceipt = request.NoReceipt,
            NoReceiptReason = request.NoReceiptReason,
            WitnessName = request.WitnessName,
            WitnessPosition = request.WitnessPosition,
        };

        var order = 1;
        foreach (var line in request.Lines)
        {
            claim.Lines.Add(new ExpenseClaimLine
            {
                LineOrder = order++,
                Description = line.Description,
                Amount = line.Amount,
                VatRate = line.VatRate,
                VatAmount = line.VatAmount,
                WithholdingTaxRate = line.WithholdingTaxRate,
                WithholdingTaxAmount = line.WithholdingTaxAmount,
                NetAmount = line.NetAmount,
                AccountId = line.AccountId,
                Category = line.Category,
                Reference = line.Reference
            });
        }

        claim.SubTotal = claim.Lines.Sum(l => l.Amount);
        claim.VatAmount = claim.Lines.Sum(l => l.VatAmount);
        claim.WithholdingTaxAmount = claim.Lines.Sum(l => l.WithholdingTaxAmount);
        claim.TotalAmount = claim.SubTotal + claim.VatAmount - claim.WithholdingTaxAmount;

        // Policy compliance checks
        ValidateExpensePolicy(claim);

        _db.ExpenseClaims.Add(claim);
        await _db.SaveChangesAsync();

        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> GetByIdAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .Include(e => e.CertificateInLieuDocument)   // for the UI deep-link
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        return MapToResponse(claim);
    }

    public async Task<PagedResponse<ExpenseClaimResponse>> GetAllAsync(Guid companyId, ExpenseClaimStatus? status, PagedRequest request)
    {
        var query = _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .Where(e => e.CompanyId == companyId);

        if (status.HasValue)
            query = query.Where(e => e.Status == status.Value);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(e => e.ClaimNumber.Contains(request.Search) || e.Title.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<ExpenseClaimResponse>(
            items.Select(MapToResponse).ToList(), total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<ExpenseClaimResponse> UpdateAsync(Guid companyId, Guid claimId, UpdateExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Draft)
            throw new InvalidOperationException("สามารถแก้ไขได้เฉพาะใบเบิกที่เป็น Draft เท่านั้น");

        if (request.Title != null) claim.Title = request.Title;
        if (request.Description != null) claim.Description = request.Description;
        if (request.ExpenseDate.HasValue) claim.ExpenseDate = request.ExpenseDate.Value;
        if (request.NoReceipt.HasValue) claim.NoReceipt = request.NoReceipt.Value;
        if (request.NoReceiptReason != null) claim.NoReceiptReason = request.NoReceiptReason;
        if (request.WitnessName != null) claim.WitnessName = request.WitnessName;
        if (request.WitnessPosition != null) claim.WitnessPosition = request.WitnessPosition;
        // §65 ทวิ enforcement on update too — same rule as Create.
        if (claim.NoReceipt && string.IsNullOrWhiteSpace(claim.NoReceiptReason))
            throw new InvalidOperationException(
                "เบิกค่าใช้จ่ายไม่มีใบเสร็จต้องระบุเหตุผล — §65 ทวิ");

        if (request.Lines != null)
        {
            _db.Set<ExpenseClaimLine>().RemoveRange(claim.Lines);
            claim.Lines.Clear();

            var order = 1;
            foreach (var line in request.Lines)
            {
                claim.Lines.Add(new ExpenseClaimLine
                {
                    ExpenseClaimId = claim.Id,
                    LineOrder = order++,
                    Description = line.Description,
                    Amount = line.Amount,
                    VatRate = line.VatRate,
                    VatAmount = line.VatAmount,
                    WithholdingTaxRate = line.WithholdingTaxRate,
                    WithholdingTaxAmount = line.WithholdingTaxAmount,
                    NetAmount = line.NetAmount,
                    AccountId = line.AccountId,
                    Category = line.Category,
                    Reference = line.Reference
                });
            }

            claim.SubTotal = claim.Lines.Sum(l => l.Amount);
            claim.VatAmount = claim.Lines.Sum(l => l.VatAmount);
            claim.WithholdingTaxAmount = claim.Lines.Sum(l => l.WithholdingTaxAmount);
            claim.TotalAmount = claim.SubTotal + claim.VatAmount - claim.WithholdingTaxAmount;
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> SubmitAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Draft)
            throw new InvalidOperationException("สามารถส่งอนุมัติได้เฉพาะใบเบิกที่เป็น Draft");

        claim.Status = ExpenseClaimStatus.Submitted;
        await _db.SaveChangesAsync();

        await NotifyClaimEventAsync(companyId, NotificationEvents.ExpenseSubmitted, claim,
            actorUserId: null,
            title: $"ใบเบิกค่าใช้จ่ายใหม่ {claim.ClaimNumber}",
            message: $"{claim.Title} · {claim.TotalAmount:N2} บาท");

        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> ApproveAsync(Guid companyId, Guid claimId, Guid approverUserId, ApproveExpenseClaimRequest request)
    {
        // Load lines + approver upfront — both are needed when this is a
        // no-receipt claim because we have to auto-generate the Document
        // (CertificateInLieu) before the SaveChanges below.
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines)
            .Include(e => e.SubmittedByUser)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Submitted)
            throw new InvalidOperationException("สามารถอนุมัติได้เฉพาะใบเบิกที่ Submitted");

        var approver = await _db.Users.FirstOrDefaultAsync(u => u.Id == approverUserId);

        claim.Status = ExpenseClaimStatus.Approved;
        claim.ApprovedByUserId = approverUserId;
        claim.ApprovedAt = DateTime.UtcNow;
        claim.ApprovalNotes = request.Notes;
        await _db.SaveChangesAsync();

        // ───── No-receipt claim → auto-generate CertificateInLieu ─────
        // §65 ทวิ allows companies to claim an expense without a vendor
        // receipt provided they issue their own certificate carrying the
        // reason + signatory. The certificate lives as a regular
        // Document(CertificateInLieu) so it gets the same PDF, e-Tax
        // integration, audit log, and PV chain as any other expense
        // document. We create it ONCE here on first approval; if approval
        // is rejected and re-submitted, CertificateInLieuDocumentId is
        // already set so we skip (idempotent).
        if (claim.NoReceipt && !claim.CertificateInLieuDocumentId.HasValue && _documentService != null)
        {
            try
            {
                await AutoGenerateCertificateInLieuAsync(companyId, claim, approver);
            }
            catch (Exception ex)
            {
                // The approval succeeded; the certificate gen is a follow-
                // up that we can retry. Log + surface a non-fatal warning.
                // The Pay step will still work without the certificate
                // (it creates a PaymentVoucher independently); the
                // certificate just won't be auto-linked.
                _logger?.LogWarning(ex,
                    "Failed to auto-generate CertificateInLieu for claim {ClaimId} — " +
                    "approval saved, certificate can be created manually",
                    claim.Id);
            }
        }

        await NotifyClaimEventAsync(companyId, NotificationEvents.ExpenseApproved, claim,
            actorUserId: approverUserId,
            title: $"ใบเบิก {claim.ClaimNumber} ได้รับอนุมัติ",
            message: $"{claim.Title} · {claim.TotalAmount:N2} บาท — รอจ่ายเงิน" +
                     (claim.CertificateInLieuDocumentId.HasValue
                        ? " · สร้างใบรับรองแทนใบเสร็จอัตโนมัติแล้ว"
                        : ""));

        return await GetByIdAsync(companyId, claim.Id);
    }

    /// <summary>
    /// Creates a Document(CertificateInLieu) carrying the §65 ทวิ legal
    /// fields. Called from ApproveAsync when claim.NoReceipt == true.
    /// The document is left in Draft status — accountant should review
    /// + approve via the regular document flow. Sets
    /// claim.CertificateInLieuDocumentId so the UI can deep-link.
    /// </summary>
    private async Task AutoGenerateCertificateInLieuAsync(
        Guid companyId, ExpenseClaim claim, User? approver)
    {
        if (_documentService == null) return;
        if (claim.SubmittedByUser == null)
            throw new InvalidOperationException(
                "ไม่พบข้อมูลผู้เบิก — ไม่สามารถสร้างใบรับรองแทนใบเสร็จอัตโนมัติได้");

        var contactId = await EnsurePayeeContactAsync(
            companyId, claim.SubmittedByUserId, claim.SubmittedByUser);

        // Map each ExpenseClaimLine → DocumentLineRequest. Quantity is 1,
        // UnitPrice = line.Amount; matches the way MarkAsPaidAsync builds
        // the PaymentVoucher line list.
        var docLines = claim.Lines.OrderBy(l => l.LineOrder).Select(l =>
            new DocumentLineRequest(
                l.Description, 1m, null, l.Amount, 0m,
                l.VatRate, l.WithholdingTaxRate, l.AccountId)).ToList();

        var approverName = approver != null
            ? (approver.FullName ?? approver.Email ?? "(ผู้อนุมัติ)")
            : "(ผู้อนุมัติ)";

        var createReq = new CreateDocumentRequest(
            DocumentType.CertificateInLieu,
            DateTime.UtcNow.Date,
            null,
            contactId,
            claim.ClaimNumber,            // reference back to the claim
            $"ใบรับรองแทนใบเสร็จ — {claim.Title} (จากใบเบิก {claim.ClaimNumber})",
            docLines);

        var doc = await _documentService.CreateDocumentAsync(
            companyId, createReq, "system:expense-claim:no-receipt");

        // Patch the §65 ทวิ-required fields directly on the entity since
        // CreateDocumentRequest doesn't carry them. Field names match
        // Document.cs lines 86-91.
        var docEntity = await _db.Set<Document>()
            .FirstOrDefaultAsync(d => d.Id == doc.Id && d.CompanyId == companyId);
        if (docEntity != null)
        {
            docEntity.CertificateReason = claim.NoReceiptReason ?? claim.Description ?? claim.Title;
            docEntity.CertifierName = approverName;
            docEntity.CertifierPosition = "ผู้อนุมัติเบิกค่าใช้จ่าย";
            docEntity.WitnessName = claim.WitnessName;
            docEntity.WitnessPosition = claim.WitnessPosition;
            docEntity.PaymentDate = claim.ExpenseDate.Date;
            docEntity.UpdatedBy = "system:expense-claim:no-receipt";
            await _db.SaveChangesAsync();
        }

        claim.CertificateInLieuDocumentId = doc.Id;
        await _db.SaveChangesAsync();
    }

    public async Task<ExpenseClaimResponse> RejectAsync(Guid companyId, Guid claimId, Guid approverUserId, RejectExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Submitted)
            throw new InvalidOperationException("สามารถปฏิเสธได้เฉพาะใบเบิกที่ Submitted");

        claim.Status = ExpenseClaimStatus.Rejected;
        claim.ApprovedByUserId = approverUserId;
        claim.ApprovedAt = DateTime.UtcNow;
        claim.RejectionReason = request.Reason;
        await _db.SaveChangesAsync();

        await NotifyClaimEventAsync(companyId, NotificationEvents.ExpenseRejected, claim,
            actorUserId: approverUserId,
            title: $"ใบเบิก {claim.ClaimNumber} ถูกปฏิเสธ",
            message: $"เหตุผล: {request.Reason}");

        return await GetByIdAsync(companyId, claim.Id);
    }

    public async Task<ExpenseClaimResponse> MarkAsPaidAsync(Guid companyId, Guid claimId, PayExpenseClaimRequest request)
    {
        var claim = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status != ExpenseClaimStatus.Approved)
            throw new InvalidOperationException("สามารถจ่ายเงินได้เฉพาะใบเบิกที่ Approved");

        // Preferred path: generate a standard PaymentVoucher Document via
        // the central DocumentService, which auto-posts Dr Expense +
        // Dr VAT input / Cr Cash + Cr WHT payable through the same flow
        // every other payment uses (so claims show up as standard PVs
        // with the standard PDF/UI). No outer transaction here:
        // ApproveDocumentAsync owns its own execution-strategy transaction
        // and nesting would throw.
        if (_documentService != null)
        {
            if (claim.SubmittedByUser == null)
                throw new InvalidOperationException(
                    "ไม่พบข้อมูลผู้เบิก (อาจถูกลบไปแล้ว) — ไม่สามารถสร้างใบสำคัญจ่ายอัตโนมัติได้");
            var contactId = await EnsurePayeeContactAsync(
                companyId, claim.SubmittedByUserId, claim.SubmittedByUser);

            var docLines = claim.Lines.OrderBy(l => l.LineOrder).Select(l =>
                new DocumentLineRequest(
                    l.Description, 1m, null, l.Amount, 0m,
                    l.VatRate, l.WithholdingTaxRate, l.AccountId)).ToList();

            var createReq = new CreateDocumentRequest(
                DocumentType.PaymentVoucher,
                DateTime.UtcNow.Date,
                null,
                contactId,
                claim.ClaimNumber,
                $"เบิกค่าใช้จ่ายพนักงาน — {claim.Title}",
                docLines);

            var doc = await _documentService.CreateDocumentAsync(companyId, createReq, "system:expense-claim");
            await _documentService.ApproveDocumentAsync(companyId, doc.Id, "system:expense-claim");

            claim.PaymentVoucherDocumentId = doc.Id;
            claim.Status = ExpenseClaimStatus.Paid;
            claim.PaidAt = DateTime.UtcNow;
            claim.PaidMethod = request.PaymentMethod;
            claim.PaidReference = request.Reference;
            await _db.SaveChangesAsync();

            await NotifyClaimEventAsync(companyId, NotificationEvents.ExpensePaid, claim,
                actorUserId: null,
                title: $"จ่ายเงินใบเบิก {claim.ClaimNumber} แล้ว",
                message: $"{claim.Title} · {claim.TotalAmount:N2} บาท · ใบสำคัญจ่าย {doc.DocumentNumber}");

            return await GetByIdAsync(companyId, claim.Id);
        }

        // Legacy fallback when DocumentService is not wired (e.g. test
        // harness without DI): post the JE directly in an outer transaction.
        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            claim.Status = ExpenseClaimStatus.Paid;
            claim.PaidAt = DateTime.UtcNow;
            claim.PaidMethod = request.PaymentMethod;
            claim.PaidReference = request.Reference;
            if (_accountingService != null)
                await CreateExpenseClaimJournalAsync(companyId, claim);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch { await transaction.RollbackAsync(); throw; }

        return await GetByIdAsync(companyId, claim.Id);
    }

    /// <summary>Mirror an ExpenseClaim's submitter as a Contact so the
    /// generated PaymentVoucher treats them as a valid accounting payee.
    /// Prefers the Employee record linked to the User (and that employee's
    /// Contact); falls back to a Contact created directly from the user
    /// profile when no Employee record exists.</summary>
    private async Task<Guid> EnsurePayeeContactAsync(Guid companyId, Guid userId, User submittedBy)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.UserId == userId && !e.IsDeleted);
        if (employee != null)
        {
            if (employee.ContactId.HasValue)
            {
                var existing = await _db.Set<Contact>()
                    .AnyAsync(c => c.Id == employee.ContactId.Value && c.CompanyId == companyId);
                if (existing) return employee.ContactId.Value;
            }
            var fullName = $"{employee.TitleTh}{employee.FirstNameTh} {employee.LastNameTh}".Trim();
            var empContact = new Contact
            {
                CompanyId = companyId,
                Name = string.IsNullOrWhiteSpace(fullName) ? employee.EmployeeCode : fullName,
                TaxId = employee.TaxId ?? employee.CitizenId,
                ContactType = ContactType.Individual,
                IsSupplier = true,
                IsCustomer = false,
                Address = employee.Address,
                Phone = employee.Phone,
                Email = employee.Email,
                IsActive = true,
                CreatedBy = "system:expense-claim"
            };
            _db.Set<Contact>().Add(empContact);
            employee.ContactId = empContact.Id;
            await _db.SaveChangesAsync();
            return empContact.Id;
        }

        var userContact = new Contact
        {
            CompanyId = companyId,
            Name = !string.IsNullOrWhiteSpace(submittedBy.FullName)
                ? submittedBy.FullName
                : (submittedBy.Email ?? "พนักงาน"),
            ContactType = ContactType.Individual,
            IsSupplier = true,
            IsCustomer = false,
            Email = submittedBy.Email,
            IsActive = true,
            CreatedBy = "system:expense-claim"
        };
        _db.Set<Contact>().Add(userContact);
        await _db.SaveChangesAsync();
        return userContact.Id;
    }

    public async Task VoidAsync(Guid companyId, Guid claimId)
    {
        var claim = await _db.ExpenseClaims.FirstOrDefaultAsync(e => e.Id == claimId && e.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบใบเบิกค่าใช้จ่าย");

        if (claim.Status == ExpenseClaimStatus.Paid)
            throw new InvalidOperationException("ไม่สามารถยกเลิกใบเบิกที่จ่ายเงินแล้วได้");

        claim.Status = ExpenseClaimStatus.Voided;
        await _db.SaveChangesAsync();
    }

    public async Task<List<ExpenseClaimResponse>> GetMyClaimsAsync(Guid companyId, Guid userId)
    {
        var claims = await _db.ExpenseClaims
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .Include(e => e.SubmittedByUser)
            .Include(e => e.ApprovedByUser)
            .Where(e => e.CompanyId == companyId && e.SubmittedByUserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return claims.Select(MapToResponse).ToList();
    }

    // Expense policy compliance
    private static readonly Dictionary<string, decimal> CategoryLimits = new()
    {
        { "transportation", 5000m },
        { "meal", 2000m },
        { "accommodation", 10000m },
        { "entertainment", 5000m },
        { "supplies", 3000m }
    };
    private const decimal MaxSingleClaimAmount = 100000m;
    private const decimal MaxSingleLineAmount = 50000m;

    private static void ValidateExpensePolicy(ExpenseClaim claim)
    {
        var violations = new List<string>();

        if (claim.TotalAmount > MaxSingleClaimAmount)
            violations.Add($"ยอดรวมเกินวงเงินสูงสุด ({MaxSingleClaimAmount:N0} บาท)");

        foreach (var line in claim.Lines)
        {
            if (line.Amount > MaxSingleLineAmount)
                violations.Add($"รายการ '{line.Description}' เกินวงเงินต่อรายการ ({MaxSingleLineAmount:N0} บาท)");

            if (!string.IsNullOrEmpty(line.Category) &&
                CategoryLimits.TryGetValue(line.Category.ToLowerInvariant(), out var limit) &&
                line.Amount > limit)
            {
                violations.Add($"รายการ '{line.Description}' เกินวงเงินหมวด {line.Category} ({limit:N0} บาท)");
            }
        }

        if (claim.ExpenseDate > DateTime.UtcNow.AddDays(1))
            violations.Add("วันที่ค่าใช้จ่ายไม่สามารถเป็นวันในอนาคตได้");

        if (claim.ExpenseDate < DateTime.UtcNow.AddDays(-90))
            violations.Add("ค่าใช้จ่ายเก่าเกิน 90 วัน ไม่สามารถเบิกได้");

        if (violations.Count > 0)
            throw new InvalidOperationException($"ไม่ผ่านนโยบายค่าใช้จ่าย: {string.Join("; ", violations)}");
    }

    /// <summary>
    /// สร้างรายการบันทึกบัญชี PV สำหรับการจ่ายเงินค่าใช้จ่าย
    /// Dr: บัญชีค่าใช้จ่าย (ตาม line items) + VAT Input (ถ้ามี)
    /// Cr: เงินสด/ธนาคาร (111101) + WHT ค้างจ่าย (ถ้ามี)
    /// </summary>
    private async Task CreateExpenseClaimJournalAsync(Guid companyId, ExpenseClaim claim)
    {
        var lines = new List<JournalLineRequest>();

        // Dr: แต่ละรายการค่าใช้จ่าย
        foreach (var line in claim.Lines)
        {
            if (line.AccountId.HasValue)
            {
                lines.Add(new JournalLineRequest(
                    line.AccountId.Value, line.Amount, 0,
                    $"ค่าใช้จ่าย - {line.Description}"));
            }
            else
            {
                // ถ้าไม่ได้ระบุบัญชี ใช้บัญชีค่าใช้จ่ายทั่วไป (529xxx)
                var defaultExpAccount = await FindAccountAsync(companyId, "549");
                if (defaultExpAccount != null)
                    lines.Add(new JournalLineRequest(
                        defaultExpAccount.Id, line.Amount, 0,
                        $"ค่าใช้จ่าย - {line.Description}"));
            }

            // Dr: VAT Input (ภาษีซื้อ)
            if (line.VatAmount > 0)
            {
                var vatInputAccount = await FindAccountAsync(companyId, "116");
                if (vatInputAccount != null)
                    lines.Add(new JournalLineRequest(
                        vatInputAccount.Id, line.VatAmount, 0, "ภาษีซื้อ"));
            }
        }

        // Cr: WHT ค้างจ่าย (ถ้ามี)
        if (claim.WithholdingTaxAmount > 0)
        {
            var whtAccount = await FindAccountAsync(companyId, "21916")
                ?? await FindAccountAsync(companyId, "21917");
            if (whtAccount != null)
                lines.Add(new JournalLineRequest(
                    whtAccount.Id, 0, claim.WithholdingTaxAmount, "ภาษีหัก ณ ที่จ่ายค้างจ่าย"));
        }

        // Cr: เงินสด/ธนาคาร — ยอดที่จ่ายจริง (TotalAmount ซึ่งหัก WHT ไว้แล้ว)
        var cashAccount = await FindAccountAsync(companyId, "111");
        if (cashAccount != null)
        {
            lines.Add(new JournalLineRequest(
                cashAccount.Id, 0, claim.TotalAmount,
                $"จ่ายเงินเบิกค่าใช้จ่าย - {claim.ClaimNumber}"));
        }

        if (lines.Count < 2) return;

        var totalDebit = lines.Sum(l => l.DebitAmount);
        var totalCredit = lines.Sum(l => l.CreditAmount);
        if (totalDebit != totalCredit)
            throw new InvalidOperationException(
                $"Journal entry unbalanced: Dr={totalDebit:N2} Cr={totalCredit:N2}");

        var journalRequest = new CreateJournalEntryRequest(
            DateTime.UtcNow,
            $"เบิกค่าใช้จ่าย {claim.ClaimNumber} - {claim.Title}",
            claim.ClaimNumber,
            lines,
            JournalType.CashPayments);

        await _accountingService!.CreateJournalEntryAsync(companyId, journalRequest, "system");
    }

    /// <summary>
    /// ค้นหาบัญชีจากรหัส — รองรับทั้งรหัส 4 หลัก (prefix) และ 6 หลัก (exact)
    /// </summary>
    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
    }

    private static ExpenseClaimResponse MapToResponse(ExpenseClaim e) => new(
        e.Id, e.ClaimNumber, e.Title, e.Description, e.ExpenseDate, e.Status,
        e.SubTotal, e.VatAmount, e.WithholdingTaxAmount, e.TotalAmount,
        e.SubmittedByUser?.FullName, e.SubmittedByUserId,
        e.ApprovedAt, e.ApprovedByUser?.FullName,
        e.PaidAt, e.PaidReference,
        e.Lines.OrderBy(l => l.LineOrder).Select(l => new ExpenseClaimLineResponse(
            l.Id, l.Description, l.Amount, l.VatRate, l.VatAmount,
            l.WithholdingTaxRate, l.WithholdingTaxAmount, l.NetAmount,
            l.AccountId, l.Account?.AccountName, l.Category, l.Reference)).ToList(),
        e.CreatedAt,
        e.PaymentVoucherDocumentId,
        e.NoReceipt,
        e.NoReceiptReason,
        e.WitnessName,
        e.WitnessPosition,
        e.CertificateInLieuDocumentId,
        e.CertificateInLieuDocument?.DocumentNumber);
}
