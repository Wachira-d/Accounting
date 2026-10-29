using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.CourseCredit;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Journal;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// คอร์ส / Class Credits / Hour Pass (ทีม L) — ดู <c>docs/COURSE_CREDIT_DESIGN.md</c>
///
/// <para><b>เส้นเงิน</b> (TFRS for NPAEs บทที่ 6):
/// <list type="number">
/// <item>ขาย ⇒ <see cref="IDocumentService"/> ออกใบกำกับภาษี/ใบเสร็จ (ขายเงินสด) บรรทัดลงผัง "ค่าบริการรับล่วงหน้า" (21713) ⇒ AutoPost เดิม:
///   Dr เงินสด/ธนาคาร · Cr 21713 (สุทธิ) · Cr ภาษีขาย (§78/1 จุดความรับผิด = วันรับเงิน) — <b>ไม่มี posting layer ใหม่</b></item>
/// <item>ใช้เครดิต (เข้าเรียน · no-show · ยกเลิกช้า) ⇒ <see cref="JournalEntryBuilder"/>: Dr 21713 · Cr รายได้ค่าบริการ (42000)
///   ตาม <see cref="CourseCreditMath.RecognizeOnUse"/></item>
/// <item>หมดอายุ ⇒ Dr 21713 · Cr รายได้ (breakage = ส่วนคงเหลือทั้งหมด)</item>
/// </list></para>
/// <para>ทุกการขยับยอดล็อกแถวบัตร (<c>FOR UPDATE</c>) ในธุรกรรมเดียวกับ JE + สมุดเครดิต + audit</para>
/// </summary>
public class CourseCreditService : ICourseCreditService, ICreditBookingLedger
{
    private const string Origin = "CourseCredit";
    private const string UnearnedCode = "21713";
    private const string RevenueCode = "42000";

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docService;
    private readonly ILogger<CourseCreditService> _logger;

    public CourseCreditService(AccountingDbContext db, IDocumentService docService, ILogger<CourseCreditService> logger)
    {
        _db = db;
        _docService = docService;
        _logger = logger;
    }

    // ===================================================================== แพ็กเกจ

    public async Task<List<CreditPackageResponse>> GetPackagesAsync(Guid companyId, bool includeInactive, CancellationToken ct = default)
    {
        var rows = await _db.CreditPackages.AsNoTracking()
            .Where(p => p.CompanyId == companyId && (includeInactive || p.IsActive))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var activeCounts = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && ids.Contains(p.PackageId) && p.Status == CreditPassStatus.Active)
            .GroupBy(p => p.PackageId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var labels = await AccountLabelsAsync(companyId,
            rows.SelectMany(r => new[] { r.UnearnedAccountId, r.RevenueAccountId }), ct);
        return rows.Select(r => MapPackage(r, labels, activeCounts.TryGetValue(r.Id, out var n) ? n : 0)).ToList();
    }

    public async Task<CreditPackageResponse> CreatePackageAsync(Guid companyId, SaveCreditPackageRequest request, string actor, CancellationToken ct = default)
    {
        await ValidatePackageAsync(companyId, null, request, ct);
        var p = new CreditPackage { CompanyId = companyId, CreatedBy = actor };
        ApplyPackage(p, request);
        _db.CreditPackages.Add(p);
        _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditPackage", p.Id, new { action = "CreatePackage", request }));
        await _db.SaveChangesAsync(ct);
        return (await GetPackagesAsync(companyId, true, ct)).First(x => x.Id == p.Id);
    }

    public async Task<CreditPackageResponse> UpdatePackageAsync(Guid companyId, Guid packageId, SaveCreditPackageRequest request, string actor, CancellationToken ct = default)
    {
        var p = await _db.CreditPackages.FirstOrDefaultAsync(x => x.Id == packageId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบแพ็กเกจ");
        await ValidatePackageAsync(companyId, packageId, request, ct);
        var before = MapPackage(p, new Dictionary<Guid, string>(), 0);
        ApplyPackage(p, request);
        p.UpdatedAt = DateTime.UtcNow; p.UpdatedBy = actor;
        // บัตรที่ขายแล้วตรึงค่าสัญญาไว้ที่ตัวบัตร (จำนวน/อายุ/นโยบาย/ผัง) — แก้แพ็กเกจไม่ย้อนเปลี่ยนสัญญาเดิม
        _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditPackage", p.Id, new { action = "UpdatePackage", before, request }));
        await _db.SaveChangesAsync(ct);
        return (await GetPackagesAsync(companyId, true, ct)).First(x => x.Id == p.Id);
    }

    private async Task ValidatePackageAsync(Guid companyId, Guid? selfId, SaveCreditPackageRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new BusinessRuleException("ระบุชื่อแพ็กเกจ", CourseCreditMath.RuleCode);
        if (string.IsNullOrWhiteSpace(r.Code)) throw new BusinessRuleException("ระบุรหัสแพ็กเกจ", CourseCreditMath.RuleCode);
        if (r.CreditQuantity <= 0m) throw new BusinessRuleException("จำนวนครั้ง/ชั่วโมงต้องมากกว่า 0", CourseCreditMath.RuleCode);
        if (r.Unit == CreditUnit.Session && r.CreditQuantity != Math.Floor(r.CreditQuantity))
            throw new BusinessRuleException("แพ็กเกจแบบ \"ครั้ง\" ต้องเป็นจำนวนเต็ม", CourseCreditMath.RuleCode);
        if (r.Price <= 0m) throw new BusinessRuleException("ราคาต้องมากกว่า 0", CourseCreditMath.RuleCode);
        if (r.ValidityDays < 0) throw new BusinessRuleException("อายุใช้งานต้องไม่ติดลบ (0 = ไม่หมดอายุ)", CourseCreditMath.RuleCode);
        if (r.FreeCancelHours < 0) throw new BusinessRuleException("ชั่วโมงยกเลิกฟรีต้องไม่ติดลบ", CourseCreditMath.RuleCode);
        if (r.VatRate != 7m && r.VatRate != 0m && r.VatRate != -1m)
            throw new BusinessRuleException("อัตรา VAT ต้องเป็น 7, 0 (§80/1) หรือ -1 (ยกเว้น §81)", CourseCreditMath.RuleCode);
        var code = r.Code.Trim();
        if (await _db.CreditPackages.AnyAsync(p => p.CompanyId == companyId && p.Code == code && (selfId == null || p.Id != selfId), ct))
            throw new BusinessRuleException($"รหัสแพ็กเกจ \"{code}\" มีอยู่แล้ว", CourseCreditMath.RuleCode);
        if (r.UnearnedAccountId is Guid u) await EnsureAccountTypeAsync(companyId, u, AccountType.Liability, "ผังค่าบริการรับล่วงหน้า", ct);
        if (r.RevenueAccountId is Guid v) await EnsureAccountTypeAsync(companyId, v, AccountType.Revenue, "ผังรายได้ค่าบริการ", ct);
        var trainerIds = r.TrainerEmployeeIds ?? new List<Guid>();
        if (trainerIds.Count > 0)
        {
            var found = await _db.Employees.AsNoTracking().CountAsync(e => e.CompanyId == companyId && trainerIds.Contains(e.Id), ct);
            if (found != trainerIds.Distinct().Count()) throw new BusinessRuleException("ครูฝึกบางคนไม่พบในทะเบียนพนักงานของบริษัท", CourseCreditMath.RuleCode);
        }
        var svcIds = r.AllowedServiceIds ?? new List<Guid>();
        if (svcIds.Count > 0)
        {
            var found = await _db.SiteBookingServices.AsNoTracking().CountAsync(s => s.CompanyId == companyId && svcIds.Contains(s.Id), ct);
            if (found != svcIds.Distinct().Count()) throw new BusinessRuleException("บริการที่ใช้ได้บางรายการไม่พบในบริษัทนี้", CourseCreditMath.RuleCode);
        }
    }

    private static void ApplyPackage(CreditPackage p, SaveCreditPackageRequest r)
    {
        p.Code = r.Code.Trim();
        p.Name = r.Name.Trim();
        p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.Unit = r.Unit;
        p.CreditQuantity = Math.Round(r.CreditQuantity, 2, MidpointRounding.AwayFromZero);
        p.ValidityDays = r.ValidityDays;
        p.Price = Math.Round(r.Price, 2, MidpointRounding.AwayFromZero);
        p.PriceIncludesVat = r.PriceIncludesVat;
        p.VatRate = r.VatRate;
        p.ClassFormat = r.ClassFormat;
        p.TrainerMode = r.TrainerMode;
        p.AllowedServiceIds = JoinIds(r.AllowedServiceIds);
        p.TrainerEmployeeIds = JoinIds(r.TrainerEmployeeIds);
        p.FreeCancelHours = r.FreeCancelHours;
        p.UnearnedAccountId = r.UnearnedAccountId;
        p.RevenueAccountId = r.RevenueAccountId;
        p.IsActive = r.IsActive;
        p.SortOrder = r.SortOrder;
    }

    // ===================================================================== ขาย

    /// <summary>ขายคอร์ส — ฝ่ายค้านข้อ 4: กันกดซ้ำด้วย <c>ClientRequestId</c> (session lock ต่อ key + ใบเดิมที่ประทับ key ใน Reference ⇒ คืนบัตรเดิม/
    /// เดินต่อจากใบเดิม ไม่ออกใบใหม่) · อนุมัติล้ม ⇒ ลบใบร่าง (ลบไม่ได้ ⇒ บอกเลขใบร่าง)</summary>
    public async Task<CreditPassResponse> SellAsync(Guid companyId, SellCreditPassRequest request, string actor, CancellationToken ct = default)
    {
        if (request.ClientRequestId is not Guid key || key == Guid.Empty)
            return await SellCoreAsync(companyId, request, null, actor, ct);
        CreditPassResponse? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.CreditSale, key.ToString("N"),
            async () => result = await SellCoreAsync(companyId, request, key, actor, ct), _logger, companyId, ct);
        if (!ran)
            throw new BusinessRuleException("กำลังบันทึกการขายรายการนี้อยู่ (กดซ้ำ) — รอสักครู่แล้วดูที่รายการบัตร", CourseCreditMath.RuleCode, 409);
        return result!;
    }

    private static string SaleReference(string packageCode, Guid? key)
        => key is Guid k ? $"{packageCode} REQ-{k.ToString("N")[..12]}" : packageCode;

    private async Task<CreditPassResponse> SellCoreAsync(Guid companyId, SellCreditPassRequest request, Guid? key, string actor, CancellationToken ct)
    {
        var pkg = await _db.CreditPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PackageId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบแพ็กเกจ");
        if (!pkg.IsActive) throw new BusinessRuleException("แพ็กเกจนี้ปิดการขายแล้ว", CourseCreditMath.RuleCode);
        var contactOk = await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == request.ContactId && c.CompanyId == companyId, ct);
        if (!contactOk) throw new BusinessRuleException("ไม่พบลูกค้าในบริษัทนี้ — เลือกลูกค้าใหม่", CourseCreditMath.RuleCode);

        var unearned = await ResolveAccountAsync(companyId, pkg.UnearnedAccountId, UnearnedCode, "217", AccountType.Liability, "ค่าบริการรับล่วงหน้า", ct);
        var revenue = await ResolveAccountAsync(companyId, pkg.RevenueAccountId, RevenueCode, "42", AccountType.Revenue, "รายได้ค่าบริการ", ct);
        var reference = SaleReference(pkg.Code, key);

        // คำขอเดิม (กดซ้ำ/เครือข่ายหลุดแล้วส่งใหม่) — เดินต่อจากใบเดิม ไม่ออกใบใหม่
        if (key != null)
        {
            var prior = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.OriginModule == Origin && d.Reference == reference && d.ContactId == request.ContactId)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => new { d.Id, d.Status, d.DocumentNumber }).FirstOrDefaultAsync(ct);
            if (prior != null)
            {
                var priorPass = await _db.CreditPasses.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && p.SaleDocumentId == prior.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
                if (priorPass is Guid pp) return (await GetPassAsync(companyId, pp, ct)).Pass;
                if (prior.Status == DocumentStatus.Voided || prior.Status == DocumentStatus.Rejected)
                    throw new BusinessRuleException($"คำขอขายนี้เคยออกเอกสาร {prior.DocumentNumber} ซึ่งถูกยกเลิกแล้ว — เปิดหน้าขายใหม่แล้วทำรายการใหม่",
                        CourseCreditMath.RuleCode, 409);
                var priorId = prior.Id;
                if (prior.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                    priorId = (await ApproveSaleOrExplainAsync(companyId, prior.Id, prior.DocumentNumber, deleteOnFail: false, actor)).Id;
                return await OpenPassAfterSaleAsync(companyId, pkg, priorId, unearned.Id, revenue.Id, request.Note, actor, ct);
            }
        }

        var saleDate = ThaiDate.CalendarDateUtc(request.SaleDate ?? DateTime.UtcNow);
        var vatRegistered = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync(ct);
        var shape = CourseCreditMath.SaleDocumentShape(vatRegistered, pkg.VatRate);
        var expiresOn = CourseCreditMath.ExpiresOn(saleDate, pkg.ValidityDays);
        var desc = $"{pkg.Name} — {pkg.CreditQuantity:0.##} {UnitTh(pkg.Unit)}"
            + (expiresOn is DateTime e ? $" · ใช้ได้ถึง {ThaiDate.ToThaiDisplayString(e)}" : " · ไม่มีวันหมดอายุ")
            + " (ค่าบริการรับล่วงหน้า — รับรู้รายได้เมื่อใช้บริการ)";

        var created = await _docService.CreateDocumentAsync(companyId, new CreateDocumentRequest(
            DocumentType: shape.Type,
            DocumentDate: saleDate,
            DueDate: null,
            ContactId: request.ContactId,
            Reference: reference,
            Notes: string.IsNullOrWhiteSpace(request.Note) ? $"ขายคอร์ส {pkg.Name}" : request.Note.Trim(),
            Lines: new List<DocumentLineRequest>
            {
                new(Description: desc, Quantity: 1, Unit: "คอร์ส", UnitPrice: pkg.Price, DiscountPercent: 0,
                    VatRate: shape.LineVatRate, WithholdingTaxRate: 0, AccountId: unearned.Id),
            },
            BankAccountId: request.BankAccountId,
            PaymentAccountId: request.PaymentAccountId,
            IssuedAsCashReceipt: shape.IssuedAsCashReceipt ? true : null,
            PaymentDate: saleDate,
            PricesIncludeVat: pkg.PriceIncludesVat), actor, Origin);
        var approved = await ApproveSaleOrExplainAsync(companyId, created.Id, created.DocumentNumber, deleteOnFail: true, actor);
        return await OpenPassAfterSaleAsync(companyId, pkg, approved.Id, unearned.Id, revenue.Id, request.Note, actor, ct);
    }

    /// <summary>อนุมัติใบขาย — ล้ม ⇒ (ใบใหม่) ลบใบร่างทิ้งแล้วบอกเหตุ · ลบไม่ได้/ใบเดิม ⇒ บอกเลขใบร่างให้ไปจัดการที่หน้าเอกสาร (ไม่ปล่อยใบค้างเงียบ)</summary>
    private async Task<DocumentResponse> ApproveSaleOrExplainAsync(Guid companyId, Guid documentId, string draftNumber, bool deleteOnFail, string actor)
    {
        try
        {
            return await _docService.ApproveDocumentAsync(companyId, documentId, actor, ApprovalAckSource.SystemWorkflow, withAiHints: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            var cleaned = false;
            if (deleteOnFail)
            {
                try { await _docService.DeleteDocumentAsync(companyId, documentId); cleaned = true; }
                catch (Exception delEx) when (delEx is not OperationCanceledException)
                {
                    _logger.LogError(delEx, "ลบใบร่างขายคอร์ส {Draft} หลังอนุมัติล้มไม่สำเร็จ", draftNumber);
                }
            }
            throw new BusinessRuleException(
                $"อนุมัติเอกสารขายไม่สำเร็จ: {ex.Message} — "
                + (cleaned ? "ระบบลบใบร่างแล้ว ยังไม่มีการขาย · แก้สาเหตุแล้วขายใหม่"
                           : $"ใบร่าง {draftNumber} ยังค้างอยู่ที่หน้าเอกสาร — แก้สาเหตุแล้วกดขายซ้ำ (คำขอเดิมจะใช้ใบนี้ต่อ) หรือลบใบร่างนั้น"),
                CourseCreditMath.RuleCode);
        }
    }

    private async Task<CreditPassResponse> OpenPassAfterSaleAsync(Guid companyId, CreditPackage pkg, Guid documentId,
        Guid unearnedId, Guid revenueId, string? note, string actor, CancellationToken ct)
    {
        var approved = await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => new { d.Id, d.DocumentNumber }).FirstAsync(ct);
        try
        {
            return await OpenPassFromDocumentAsync(companyId, pkg, approved.Id, unearnedId, revenueId, CreditSaleChannel.BackOffice, note, actor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ใบขายออกแล้ว (เงิน/VAT ลงบัญชีแล้ว) แต่เปิดบัตรไม่สำเร็จ — ล้มดังพร้อมทางไปต่อ (ไม่ออกใบซ้ำ)
            _logger.LogError(ex, "เปิดบัตรคอร์สหลังออกใบ {Doc} ไม่สำเร็จ (บริษัท {Cid})", approved.DocumentNumber, companyId);
            throw new BusinessRuleException(
                $"ออกเอกสารขาย {approved.DocumentNumber} แล้ว แต่เปิดบัตรคอร์สไม่สำเร็จ ({ex.Message}) — "
                + "อย่าขายซ้ำ · ใช้ปุ่ม \"เปิดบัตรจากเอกสารขาย\" กับใบนี้", CourseCreditMath.RuleCode);
        }
    }

    public async Task<CreditPassResponse> ActivateFromDocumentAsync(Guid companyId, ActivateCreditPassRequest request, string actor, CancellationToken ct = default)
    {
        var pkg = await _db.CreditPackages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PackageId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบแพ็กเกจ");
        var revenue = await ResolveAccountAsync(companyId, pkg.RevenueAccountId, RevenueCode, "42", AccountType.Revenue, "รายได้ค่าบริการ", ct);
        // ผังรายได้รับล่วงหน้าที่ JE ของใบลงเครดิตจริง (ฝ่ายค้านข้อ 2 — ไม่อ่านจากบรรทัด: ใบเสร็จที่ตัดลูกหนี้มีบรรทัด 21713 ได้แต่ JE ไม่ Cr 21713) ·
        // แพ็กเกจระบุผังไว้ ⇒ ใช้ผังนั้น · ไม่ระบุ ⇒ ผังกลุ่ม 217 (รายได้รับล่วงหน้า) ที่ JE ใบนี้ Cr สุทธิ > 0 ต้องมีผังเดียว
        var credits = await JournalLiabilityCreditsAsync(companyId, request.DocumentId, ct);
        var accountIds = credits.Where(c => c.Value > 0m && c.Code.StartsWith("217")).Select(c => c.Key).ToList();
        Guid unearnedId;
        if (pkg.UnearnedAccountId is Guid fixedId) unearnedId = fixedId;
        else if (accountIds.Count == 1) unearnedId = accountIds[0];
        else if (accountIds.Count == 0)
            throw new BusinessRuleException("เอกสารนี้ไม่มีบรรทัดที่ลงผังหนี้สิน \"รายได้รับล่วงหน้า\" — ยอดขายลงรายได้ไปแล้ว เปิดบัตรจากใบนี้ไม่ได้ "
                + "(เปิดได้จะรับรู้รายได้ซ้ำ) · ออกใบลดหนี้/ใบใหม่ที่บรรทัดลงผัง 21713 แทน", CourseCreditMath.RuleCode);
        else
            throw new BusinessRuleException("เอกสารนี้มีบรรทัดหนี้สินหลายผัง — ระบุผังค่าบริการรับล่วงหน้าในแพ็กเกจก่อน", CourseCreditMath.RuleCode);
        return await OpenPassFromDocumentAsync(companyId, pkg, request.DocumentId, unearnedId, revenue.Id,
            request.Channel ?? CreditSaleChannel.ExistingDocument, request.Note, actor, ct);
    }

    private static readonly DocumentType[] SaleTypes =
        { DocumentType.TaxInvoice, DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.Invoice };
    private static readonly DocumentStatus[] IssuedStatuses =
        { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Paid, DocumentStatus.Overdue };

    private async Task<CreditPassResponse> OpenPassFromDocumentAsync(Guid companyId, CreditPackage pkg, Guid documentId,
        Guid unearnedAccountId, Guid revenueAccountId, CreditSaleChannel channel, string? note, string actor, CancellationToken ct)
    {
        var passId = await InTxAsync(async () =>
        {
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", documentId, companyId);
            var doc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == documentId && d.CompanyId == companyId)
                .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status, d.ContactId, d.DocumentDate, d.RelatedDocumentId })
                .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("ไม่พบเอกสารขาย");
            if (!SaleTypes.Contains(doc.DocumentType))
                throw new BusinessRuleException("เปิดบัตรได้จากใบกำกับภาษี/ใบเสร็จ/ใบแจ้งหนี้ฝั่งขายเท่านั้น", CourseCreditMath.RuleCode);
            if (!IssuedStatuses.Contains(doc.Status))
                throw new BusinessRuleException($"เอกสาร {doc.DocumentNumber} ยังไม่อนุมัติหรือถูกยกเลิก — เปิดบัตรไม่ได้", CourseCreditMath.RuleCode);
            // สายเอกสารเดียวกัน = ใบนี้ · ใบต้นทาง · ใบลูก · ใบพี่น้องที่อ้างต้นทางเดียวกัน — มีบัตรอยู่แล้วใบใดใบหนึ่ง ⇒ ห้ามเปิดซ้ำ (เงินก้อนเดียว)
            var chain = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && (d.Id == doc.Id || d.RelatedDocumentId == doc.Id
                    || (doc.RelatedDocumentId != null && (d.Id == doc.RelatedDocumentId || d.RelatedDocumentId == doc.RelatedDocumentId))))
                .Select(d => d.Id).ToListAsync(ct);
            var chainPass = await _db.CreditPasses.IgnoreQueryFilters()
                .Where(p => p.CompanyId == companyId && p.SaleDocumentId != null && chain.Contains(p.SaleDocumentId.Value))
                .Select(p => p.PassNumber).FirstOrDefaultAsync(ct);
            // ยอดสัญญา = เครดิตสุทธิของ JE ที่มีผลบนผังรายได้รับล่วงหน้า (บาท — ใบต่างสกุลได้ยอดที่ลง GL จริง)
            var contract = (await JournalLiabilityCreditsAsync(companyId, doc.Id, ct))
                .Where(c => c.Key == unearnedAccountId).Sum(c => c.Value);
            if (CreditPassDocumentRule.ActivateProblem(doc.DocumentNumber, contract, chainPass) is string activateProblem)
                throw new BusinessRuleException(activateProblem, CreditPassDocumentRule.RuleCode);
            await _db.EnsurePostingAccountsAsync(companyId, new (Guid?, string)[]
            {
                (unearnedAccountId, "ผังค่าบริการรับล่วงหน้า"), (revenueAccountId, "ผังรายได้ค่าบริการ"),
            }, ct);

            var prefix = $"CP-{ThaiDate.CalendarDateUtc(doc.DocumentDate):yyyyMM}-";
            var number = await SequenceNumber.NextAsync(_db, companyId, AdvisoryLockKey.CreditPassSequence, prefix,
                _db.CreditPasses.IgnoreQueryFilters().Where(p => p.CompanyId == companyId && p.PassNumber.StartsWith(prefix)).Select(p => p.PassNumber),
                _db.CreditPasses.Local.Select(p => p.PassNumber), ct: ct);
            var pass = new CreditPass
            {
                CompanyId = companyId, CreatedBy = actor,
                PassNumber = number, PackageId = pkg.Id, ContactId = doc.ContactId, PackageName = pkg.Name,
                Unit = pkg.Unit, ClassFormat = pkg.ClassFormat, AllowedServiceIds = pkg.AllowedServiceIds,
                FreeCancelHours = pkg.FreeCancelHours,
                PurchasedAt = doc.DocumentDate,
                ExpiresOn = CourseCreditMath.ExpiresOn(doc.DocumentDate, pkg.ValidityDays),
                CreditsTotal = pkg.CreditQuantity,
                ContractAmount = contract,
                UnearnedAccountId = unearnedAccountId, RevenueAccountId = revenueAccountId,
                SaleChannel = channel, SaleDocumentId = doc.Id, SaleDocumentNumber = doc.DocumentNumber,
                Notes = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            };
            _db.CreditPasses.Add(pass);
            _db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                CompanyId = companyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Purchase,
                Credits = pass.CreditsTotal, Note = $"ซื้อ {pkg.Name} — {doc.DocumentNumber} · ค่าบริการรับล่วงหน้า {contract:N2}",
            });
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditPass", pass.Id, new
            {
                action = "OpenPass", pass = number, document = doc.DocumentNumber, contract, credits = pass.CreditsTotal,
                expiresOn = pass.ExpiresOn, channel = channel.ToString(), ruleCode = CourseCreditMath.RuleCode,
                legalReference = CourseCreditMath.LegalReference,
            }));
            await _db.SaveChangesAsync(ct);
            return pass.Id;
        }, ct);
        return (await GetPassAsync(companyId, passId, ct)).Pass;
    }

    // ===================================================================== คืนเงินจากบัตร (R-38)

    /// <summary>ปุ่ม "ออกใบลดหนี้คืนเงิน (ร่าง)" บนหน้าบัตร — สร้าง<b>ใบร่าง</b>ใบลดหนี้อ้างใบขายของบัตร บรรทัดเดียวลงผังค่าบริการรับล่วงหน้าของบัตร
    /// (ยอดสุทธิก่อน VAT · อัตรา VAT ตามบรรทัดใบขาย) · <b>ไม่อนุมัติเอง</b> — ผู้มีสิทธิ์ตรวจแล้วอนุมัติที่หน้าเอกสาร ⇒ ด่าน <see cref="CreditPassDocumentHook"/>
    /// ลดเครดิต/ปิดบัตรตาม <see cref="CreditPassDocumentRule.RefundPreview"/> (ตัวตัดสินเดียวกับที่ใช้คำนวณตัวอย่างตรงนี้) · ระบุยอดบาท หรือจำนวนเครดิต (แปลงเป็นบาทด้วยราคาต่อเครดิต)</summary>
    public async Task<CreditRefundDraftResponse> DraftRefundCreditNoteAsync(Guid companyId, Guid passId, DraftCreditRefundRequest request, string actor, CancellationToken ct = default)
    {
        var pass = await _db.CreditPasses.AsNoTracking().FirstOrDefaultAsync(p => p.Id == passId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรคอร์ส");
        if (pass.Status != CreditPassStatus.Active)
            throw new BusinessRuleException($"บัตรอยู่ในสถานะ {CourseCreditMath.StatusTh(pass.Status)} — คืนเงินจากบัตรไม่ได้", CreditPassDocumentRule.RuleCode);
        if (pass.SaleDocumentId is not Guid saleId)
            throw new BusinessRuleException("บัตรนี้ไม่ผูกใบขายในระบบ — ออกใบลดหนี้จากหน้าเอกสารเอง", CreditPassDocumentRule.RuleCode);
        if (await SaleDocumentProblemAsync(pass, ct) is string problem)
            throw new BusinessRuleException(problem, CreditPassDocumentRule.RuleCode);
        var facts = CreditPassDocumentHook.Facts(pass);
        CreditPassDocumentRule.RefundPlan plan;
        if (request.AmountNet is decimal a && a > 0m)
            plan = CreditPassDocumentRule.RefundPreview(facts, Math.Round(a, 2, MidpointRounding.AwayFromZero));
        else if (request.Credits is decimal c && c > 0m)
            // รอบ 208 (ฝ่ายค้าน W6 ข้อ 4): คืนตามจำนวนเครดิต ⇒ ส่งเครดิตเข้าตัวตัดสินตรง ๆ (เดิมแปลงเป็นบาทปัดสตางค์แล้วหารกลับ ⇒ บัตรชั่วโมงหาย 0.25) ·
            // เครดิตที่ยังไม่ใช้ทั้งหมด ⇒ ยอดเท่าส่วนที่เหลือ (ปิดบัตร · ไม่มีเศษสตางค์ค้าง) — ตัดสินใน RefundPreview ตัวเดียว
            plan = CreditPassDocumentRule.RefundPreview(facts, 0m, credits: c);
        else throw new BusinessRuleException("ระบุยอดคืน (บาท สุทธิก่อน VAT) หรือจำนวนเครดิตที่คืน", CreditPassDocumentRule.RuleCode);

        if (plan.Problem != null) throw new BusinessRuleException(plan.Problem, CreditPassDocumentRule.RuleCode);

        var saleLine = await _db.DocumentLines.AsNoTracking()
            .Where(l => l.DocumentId == saleId && !l.IsDeleted && l.Document.CompanyId == companyId)
            .OrderByDescending(l => l.AccountId == pass.UnearnedAccountId)
            .Select(l => new { l.VatRate })
            .FirstOrDefaultAsync(ct) ?? throw new BusinessRuleException("ใบขายของบัตรไม่มีบรรทัด — ออกใบลดหนี้จากหน้าเอกสารเอง", CreditPassDocumentRule.RuleCode);
        // รอบ 208 (ฝ่ายค้าน W6 หมายเหตุ): ใบลดหนี้ §86/10 เฉพาะผู้จด VAT ที่ออกใบกำกับไว้ — ไม่จด VAT / บริการยกเว้น ⇒ ไม่สร้างใบผิดชนิด (ตัวตัดสินเดียว)
        var companyVatRegistered = await _db.Companies.AsNoTracking().Where(co => co.Id == companyId).Select(co => co.IsVatRegistered).FirstOrDefaultAsync(ct);
        if (CreditPassDocumentRule.RefundDocumentProblem(companyVatRegistered, saleLine.VatRate) is string refundDocProblem)
            throw new BusinessRuleException(refundDocProblem, CreditPassDocumentRule.RuleCode);

        var what = plan.ClosesPass
            ? $"คืนเงินส่วนที่ยังไม่ใช้ทั้งหมด {plan.Net:N2} — ปิดบัตร"
            : $"คืนเงินบางส่วน {plan.Net:N2} — ลดเครดิต {plan.CreditsToRemove:0.##} {UnitTh(pass.Unit)} (คงเหลือ {plan.CreditsRemaining:0.##})";
        var created = await _docService.CreateDocumentAsync(companyId, new CreateDocumentRequest(
            DocumentType: DocumentType.CreditNote,
            DocumentDate: ThaiDate.CalendarDateUtc(DateTime.UtcNow),
            DueDate: null,
            ContactId: pass.ContactId,
            Reference: pass.PassNumber,
            Notes: string.IsNullOrWhiteSpace(request.Note) ? $"คืนเงินคอร์ส {pass.PackageName} บัตร {pass.PassNumber} — {what}" : request.Note.Trim(),
            Lines: new List<DocumentLineRequest>
            {
                new(Description: $"{pass.PackageName} — บัตร {pass.PassNumber} · {what} (ค่าบริการรับล่วงหน้า)", Quantity: 1, Unit: "คอร์ส",
                    UnitPrice: plan.Net, DiscountPercent: 0, VatRate: saleLine.VatRate, WithholdingTaxRate: 0, AccountId: pass.UnearnedAccountId),
            },
            RelatedDocumentId: saleId,
            CreditNoteReason: CreditNoteReason.Adjustment,
            PricesIncludeVat: false), actor, Origin);
        _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditPass", pass.Id, new
        {
            action = "DraftRefundCreditNote", pass = pass.PassNumber, document = created.DocumentNumber, net = plan.Net,
            creditsToRemove = plan.CreditsToRemove, closesPass = plan.ClosesPass, ruleCode = CreditPassDocumentRule.RuleCode,
        }));
        await _db.SaveChangesAsync(ct);
        return new CreditRefundDraftResponse(created.Id, created.DocumentNumber, plan.Net, plan.CreditsToRemove, plan.CreditsRemaining, plan.ClosesPass,
            $"สร้างใบร่างใบลดหนี้ {created.DocumentNumber} แล้ว ({what}) — ตรวจแล้วอนุมัติที่หน้าเอกสาร ระบบจึง{(plan.ClosesPass ? "ปิดบัตร" : "ลดเครดิต")}");
    }

    /// <summary>ตัวอย่างผลการคืนเงิน (ไม่สร้างเอกสาร) — ให้หน้าบัตรแสดงก่อนกดสร้างใบร่าง</summary>
    public async Task<CreditRefundPreviewResponse> PreviewRefundAsync(Guid companyId, Guid passId, CancellationToken ct = default)
    {
        var pass = await _db.CreditPasses.AsNoTracking().FirstOrDefaultAsync(p => p.Id == passId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรคอร์ส");
        var facts = CreditPassDocumentHook.Facts(pass);
        return new CreditRefundPreviewResponse(facts.Deferred, facts.PricePerCredit, facts.MaxRemovableCredits,
            CreditPassDocumentRule.MaxPartialAmount(facts), pass.CreditsReserved > 0m);
    }

    // ===================================================================== อ่าน

    public async Task<PagedResponse<CreditPassResponse>> GetPassesAsync(Guid companyId, Guid? contactId, string? status, string? search,
        int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var q = _db.CreditPasses.AsNoTracking().Where(p => p.CompanyId == companyId);
        if (contactId is Guid cid) q = q.Where(p => p.ContactId == cid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<CreditPassStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            var contactIds = _db.Contacts.Where(c => c.CompanyId == companyId && c.Name.Contains(s)).Select(c => c.Id);
            q = q.Where(p => p.PassNumber.Contains(s) || p.PackageName.Contains(s) || contactIds.Contains(p.ContactId)
                || (p.SaleDocumentNumber != null && p.SaleDocumentNumber.Contains(s)));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(p => p.PurchasedAt).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await ContactNamesAsync(companyId, rows.Select(r => r.ContactId), ct);
        var now = DateTime.UtcNow;
        return new PagedResponse<CreditPassResponse>(rows.Select(r => MapPass(r, names, now)).ToList(), total, page, pageSize,
            (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<CreditPassDetailResponse> GetPassAsync(Guid companyId, Guid passId, CancellationToken ct = default)
    {
        var pass = await _db.CreditPasses.AsNoTracking().FirstOrDefaultAsync(p => p.Id == passId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรคอร์ส");
        var names = await ContactNamesAsync(companyId, new[] { pass.ContactId }, ct);
        var ledger = await _db.CreditLedgerEntries.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.PassId == passId)
            .OrderBy(l => l.OccurredAt).ThenBy(l => l.CreatedAt).ToListAsync(ct);
        var jeIds = ledger.Where(l => l.JournalEntryId.HasValue).Select(l => l.JournalEntryId!.Value).ToList();
        var jeNumbers = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && jeIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.EntryNumber, ct);
        var bookings = await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.PassId == passId)
            .OrderByDescending(b => b.StartAt).ToListAsync(ct);
        return new CreditPassDetailResponse(
            MapPass(pass, names, DateTime.UtcNow),
            ledger.Select(l => new CreditLedgerEntryResponse(l.Id, l.Kind, KindTh(l.Kind), l.Credits, l.RecognizedAmount,
                l.BookingId, l.JournalEntryId, l.JournalEntryId is Guid j && jeNumbers.TryGetValue(j, out var no) ? no : null,
                l.OccurredAt, l.Note, l.CreatedBy)).ToList(),
            await MapBookingsAsync(companyId, bookings, ct));
    }

    public async Task<CreditSummaryResponse> GetSummaryAsync(Guid companyId, CancellationToken ct = default)
    {
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var active = _db.CreditPasses.AsNoTracking().Where(p => p.CompanyId == companyId && p.Status == CreditPassStatus.Active);
        var count = await active.CountAsync(ct);
        var deferred = await active.SumAsync(p => (decimal?)(p.ContractAmount - p.RecognizedAmount - p.RefundedAmount), ct) ?? 0m;
        var soon = today.AddDays(7);
        var expiring = await active.CountAsync(p => p.ExpiresOn != null && p.ExpiresOn >= today && p.ExpiresOn <= soon, ct);
        var pending = await active.CountAsync(p => p.ExpiresOn != null && p.ExpiresOn < today, ct);
        return new CreditSummaryResponse(count, deferred, expiring, pending);
    }

    // ===================================================================== รอบเรียน

    public async Task<List<CreditSessionResponse>> GetSessionsAsync(Guid companyId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var f = from ?? DateTime.UtcNow.AddDays(-7);
        var t = to ?? DateTime.UtcNow.AddDays(30);
        var rows = await _db.CreditClassSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.StartAt >= f && s.StartAt <= t)
            .OrderBy(s => s.StartAt).Take(500).ToListAsync(ct);
        return await MapSessionsAsync(companyId, rows, ct);
    }

    public async Task<CreditSessionResponse> CreateSessionAsync(Guid companyId, SaveCreditSessionRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new BusinessRuleException("ระบุชื่อคลาส", CourseCreditMath.RuleCode);
        if (request.EndAt <= request.StartAt) throw new BusinessRuleException("เวลาเลิกต้องหลังเวลาเริ่ม", CourseCreditMath.RuleCode);
        var capacity = request.Format == CreditClassFormat.OneOnOne ? 1 : request.Capacity;
        if (capacity < 1) throw new BusinessRuleException("จำนวนที่รับต่อรอบต้องอย่างน้อย 1", CourseCreditMath.RuleCode);
        if (request.CreditCost <= 0m) throw new BusinessRuleException("เครดิตต่อคนต้องมากกว่า 0", CourseCreditMath.RuleCode);
        if (request.BookingServiceId is Guid sid
            && !await _db.SiteBookingServices.AsNoTracking().AnyAsync(s => s.Id == sid && s.CompanyId == companyId, ct))
            throw new BusinessRuleException("ไม่พบบริการที่เลือกในบริษัทนี้", CourseCreditMath.RuleCode);
        var s = await InTxAsync(async () =>
        {
        if (request.TrainerEmployeeId is Guid tid)
        {
            if (!await _db.Employees.AsNoTracking().AnyAsync(e => e.Id == tid && e.CompanyId == companyId, ct))
                throw new BusinessRuleException("ไม่พบครูฝึกในทะเบียนพนักงาน", CourseCreditMath.RuleCode);
            await LockTrainerDaysAsync(companyId, new[] { tid }, request.StartAt, ct);
            if (await TrainerBusyAsync(companyId, tid, request.StartAt, request.EndAt, null, ct))
                throw new BusinessRuleException("ครูฝึกคนนี้มีรอบสอนซ้อนเวลานี้อยู่แล้ว", CourseCreditMath.RuleCode);
        }
        var row = new CreditClassSession
        {
            CompanyId = companyId, CreatedBy = actor, Title = request.Title.Trim(), BookingServiceId = request.BookingServiceId,
            Format = request.Format, StartAt = request.StartAt, EndAt = request.EndAt, Capacity = capacity,
            TrainerEmployeeId = request.TrainerEmployeeId,
            CreditCost = Math.Round(request.CreditCost, 2, MidpointRounding.AwayFromZero),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        _db.CreditClassSessions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
        }, ct);
        return (await MapSessionsAsync(companyId, new List<CreditClassSession> { s }, ct))[0];
    }

    public async Task<CreditSessionResponse> CancelSessionAsync(Guid companyId, Guid sessionId, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"CreditClassSessions\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", sessionId, companyId);
            var s = await _db.CreditClassSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.CompanyId == companyId, ct)
                ?? throw new KeyNotFoundException("ไม่พบรอบเรียน");
            if (s.Status != CreditSessionStatus.Scheduled) throw new BusinessRuleException("รอบนี้ปิด/ยกเลิกไปแล้ว", CourseCreditMath.RuleCode);
            // ลำดับล็อกเดียวกับปิด/ยกเลิกรายคน (การจอง → บัตร) — กัน deadlock กับคนที่กด "มาเรียน" พร้อมกัน
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"CreditClassBookings\" WHERE \"SessionId\" = {0} AND \"CompanyId\" = {1} AND \"Status\" = 0 FOR UPDATE",
                sessionId, companyId);
            var open = await _db.CreditClassBookings
                .Where(b => b.CompanyId == companyId && b.SessionId == sessionId && b.Status == CreditBookingStatus.Booked)
                .OrderBy(b => b.PassId).ToListAsync(ct);
            foreach (var b in open)
            {
                var pass = await LockPassAsync(companyId, b.PassId, ct);
                Release(pass, b, actor, "ร้านยกเลิกรอบเรียน — คืนเครดิต");
            }
            s.Status = CreditSessionStatus.Cancelled; s.UpdatedAt = DateTime.UtcNow; s.UpdatedBy = actor;
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Update, "CreditClassSession", s.Id,
                new { action = "CancelSession", refunded = open.Count }));
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        var row = await _db.CreditClassSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId && x.CompanyId == companyId, ct);
        return (await MapSessionsAsync(companyId, new List<CreditClassSession> { row }, ct))[0];
    }

    public async Task<List<CreditBookingResponse>> GetSessionBookingsAsync(Guid companyId, Guid sessionId, CancellationToken ct = default)
    {
        var rows = await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SessionId == sessionId)
            .OrderBy(b => b.BookedAt).ToListAsync(ct);
        return await MapBookingsAsync(companyId, rows, ct);
    }

    public async Task<List<CreditTrainerOption>> GetTrainersAsync(Guid companyId, CancellationToken ct = default)
    {
        // ชื่อเล่น/ชื่อเท่านั้น (ไม่มีข้อมูลส่วนบุคคลอื่น — หน้าจองไม่ต้องใช้)
        var rows = await _db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.IsActive)
            .OrderBy(e => e.FirstNameTh).Take(500)
            .Select(e => new { e.Id, e.NickName, e.FirstNameTh, e.LastNameTh })
            .ToListAsync(ct);
        return rows.Select(e => new CreditTrainerOption(e.Id, TrainerName(e.NickName, e.FirstNameTh, e.LastNameTh))).ToList();
    }

    public async Task<List<CreditTrainerOption>> GetBookableServicesAsync(Guid companyId, CancellationToken ct = default)
        => await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name).Take(500)
            .Select(s => new CreditTrainerOption(s.Id, s.Name))
            .ToListAsync(ct);

    // ===================================================================== จอง / ปิด / ยกเลิก

    public async Task<CreditBookingResponse> BookAsync(Guid companyId, BookWithCreditRequest request, string actor, CancellationToken ct = default)
    {
        var bookingId = await InTxAsync(async () =>
        {
            CreditClassSession session;
            if (request.SessionId is Guid sid)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"CreditClassSessions\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", sid, companyId);
                session = await _db.CreditClassSessions.FirstOrDefaultAsync(s => s.Id == sid && s.CompanyId == companyId, ct)
                    ?? throw new KeyNotFoundException("ไม่พบรอบเรียน");
                if (session.Status != CreditSessionStatus.Scheduled)
                    throw new BusinessRuleException("รอบนี้ปิด/ยกเลิกแล้ว — จองไม่ได้", CourseCreditMath.RuleCode);
                var taken = await _db.CreditClassBookings.CountAsync(b => b.CompanyId == companyId && b.SessionId == sid
                    && b.Status != CreditBookingStatus.Cancelled, ct);
                if (taken >= session.Capacity)
                    throw new BusinessRuleException($"รอบนี้เต็มแล้ว ({taken}/{session.Capacity})", CourseCreditMath.RuleCode);
                if (await _db.CreditClassBookings.AnyAsync(b => b.CompanyId == companyId && b.SessionId == sid
                        && b.PassId == request.PassId && b.Status != CreditBookingStatus.Cancelled, ct))
                    throw new BusinessRuleException("บัตรนี้จองรอบนี้ไว้แล้ว", CourseCreditMath.RuleCode);
            }
            else
            {
                // ตัวต่อตัว — สร้างรอบ capacity 1 ให้ (ครูฝึก: ลูกค้าเลือก / ระบบกำหนดตามโหมดแพ็กเกจ)
                if (request.StartAt is not DateTime start || request.EndAt is not DateTime end || end <= start)
                    throw new BusinessRuleException("ระบุรอบเรียน หรือเวลาเริ่ม/เลิกของคลาสตัวต่อตัว", CourseCreditMath.RuleCode);
                session = await NewOneOnOneSessionAsync(companyId, request.PassId, start, end, request.BookingServiceId,
                    request.TrainerEmployeeId, actor, ct);
            }

            var pass = await LockPassAsync(companyId, request.PassId, ct);
            await EnsureSaleDocumentLiveAsync(pass, ct);
            var credits = CourseCreditMath.CreditsForSession(pass.Unit, (int)(session.EndAt - session.StartAt).TotalMinutes, session.CreditCost);
            var reason = CourseCreditMath.CannotBookReason(Facts(pass), credits, session.StartAt, session.BookingServiceId,
                session.Format, DateTime.UtcNow);
            if (reason != null) throw new BusinessRuleException(reason, CourseCreditMath.RuleCode);
            var booking = Reserve(pass, credits, session.StartAt, session.Id, null, session.TrainerEmployeeId, request.Note, actor);
            _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditClassBooking", booking.Id,
                new { action = "Book", pass = pass.PassNumber, session = session.Id, credits }));
            await _db.SaveChangesAsync(ct);
            return booking.Id;
        }, ct);
        return await GetBookingAsync(companyId, bookingId, ct);
    }

    public async Task<CreditBookingResponse> SettleBookingAsync(Guid companyId, Guid bookingId, SettleCreditBookingRequest request, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            var booking = await LockBookingAsync(companyId, bookingId, ct);
            if (!request.Attended && booking.StartAt > DateTime.UtcNow)
                throw new BusinessRuleException("ยังไม่ถึงเวลาเรียน — บันทึก no-show ไม่ได้ (ถ้าลูกค้าแจ้งยกเลิก ให้กด \"ยกเลิก\")", CourseCreditMath.RuleCode);
            var pass = await LockPassAsync(companyId, booking.PassId, ct);
            await ConsumeAsync(pass, booking, request.Attended ? CreditBookingStatus.Attended : CreditBookingStatus.NoShow, request.Note, actor, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetBookingAsync(companyId, bookingId, ct);
    }

    public async Task<CreditBookingResponse> CancelBookingAsync(Guid companyId, Guid bookingId, CancelCreditBookingRequest request, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            var booking = await LockBookingAsync(companyId, bookingId, ct);
            var pass = await LockPassAsync(companyId, booking.PassId, ct);
            var outcome = request.ByBusiness
                ? CreditCancelOutcome.Refund
                : CourseCreditMath.CancelOutcome(booking.StartAt, DateTime.UtcNow, pass.FreeCancelHours);
            if (outcome == CreditCancelOutcome.Refund)
                Release(pass, booking, actor, (request.ByBusiness ? "ร้านยกเลิก" : $"ลูกค้ายกเลิกก่อน {pass.FreeCancelHours} ชม.") + " — คืนเครดิต"
                    + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · {request.Note.Trim()}"));
            else
                await ConsumeAsync(pass, booking, CreditBookingStatus.LateCancelled,
                    $"ยกเลิกน้อยกว่า {pass.FreeCancelHours} ชม. ก่อนเรียน — ริบเครดิตตามนโยบาย" + (string.IsNullOrWhiteSpace(request.Note) ? "" : $" · {request.Note.Trim()}"),
                    actor, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetBookingAsync(companyId, bookingId, ct);
    }

    // ===================================================================== หมดอายุ

    public async Task<CreditPassResponse> ExpirePassAsync(Guid companyId, Guid passId, string actor, CancellationToken ct = default)
    {
        await InTxAsync(async () =>
        {
            var pass = await LockPassAsync(companyId, passId, ct);
            if (pass.Status != CreditPassStatus.Active) throw new BusinessRuleException($"บัตรอยู่ในสถานะ {CourseCreditMath.StatusTh(pass.Status)} แล้ว", CourseCreditMath.RuleCode);
            if (!CourseCreditMath.IsExpired(pass.ExpiresOn, DateTime.UtcNow))
                throw new BusinessRuleException("บัตรยังไม่หมดอายุ", CourseCreditMath.RuleCode);
            if (pass.CreditsReserved > 0m)
                throw new BusinessRuleException($"มีการจองค้าง {pass.CreditsReserved:0.##} เครดิต — ปิดการจอง (มาเรียน/no-show/ยกเลิก) ก่อนปิดบัตร", CourseCreditMath.RuleCode);
            await ExpireCoreAsync(pass, actor, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return (await GetPassAsync(companyId, passId, ct)).Pass;
    }

    public async Task<CreditExpiryRunResult> ExpireDueAsync(Guid companyId, string actor, CancellationToken ct = default)
    {
        var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var due = await _db.CreditPasses.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.Status == CreditPassStatus.Active && p.ExpiresOn != null && p.ExpiresOn < today)
            .OrderBy(p => p.ExpiresOn).Select(p => new { p.Id, p.PassNumber, p.CreditsReserved, Remaining = p.ContractAmount - p.RecognizedAmount })
            .Take(500).ToListAsync(ct);
        int expired = 0, skipped = 0, failed = 0; decimal breakage = 0m; var messages = new List<string>();
        foreach (var d in due)
        {
            if (d.CreditsReserved > 0m) { skipped++; messages.Add($"{d.PassNumber}: มีการจองค้าง — รอปิดการจองก่อน"); continue; }
            try
            {
                await ExpirePassAsync(companyId, d.Id, actor, ct);
                expired++; breakage += Math.Max(0m, d.Remaining);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ฝ่ายค้านข้อ 6: ใบเดียวล้ม (ด่านธุรกิจ/ฐานข้อมูล) ไม่หยุดทั้งรอบ — ล้มดังในผลลัพธ์ + log Error · บัตรคงสถานะเดิม ไม่ประทับว่าหมดอายุ
                failed++; messages.Add($"{d.PassNumber}: {ex.Message}");
                _logger.LogError(ex, "ปิดบัตรคอร์สหมดอายุ {Pass} (บริษัท {Cid}) ไม่สำเร็จ", d.PassNumber, companyId);
            }
        }
        return new CreditExpiryRunResult(expired, skipped, failed, breakage, messages);
    }

    private async Task ExpireCoreAsync(CreditPass pass, string actor, CancellationToken ct)
    {
        await EnsureSaleDocumentLiveAsync(pass, ct);
        // สัญญาที่มีผล = สัญญา − คืนเงินบางส่วนแล้ว (R-38) — บัตรที่ไม่เคยคืน = สัญญาเดิม
        var amount = CourseCreditMath.BreakageAmount(CourseCreditMath.EffectiveContract(pass.ContractAmount, pass.RefundedAmount), pass.RecognizedAmount);
        var remainingCredits = Math.Max(0m, pass.CreditsTotal - pass.CreditsUsed);
        Guid? jeId = null; string? dateNote = null;
        if (amount > 0m)
        {
            var je = await PostRecognitionAsync(pass, amount, pass.ExpiresOn ?? DateTime.UtcNow,
                $"หมดอายุ — เครดิตคงเหลือ {remainingCredits:0.##} (breakage)", actor, ct);
            jeId = je.Id; dateNote = je.DateNote;
        }
        pass.RecognizedAmount += amount;
        pass.Status = CreditPassStatus.Expired;
        pass.ClosedAt = DateTime.UtcNow; pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Expire,
            Credits = remainingCredits, RecognizedAmount = amount, JournalEntryId = jeId,
            Note = "หมดอายุ — รับรู้ส่วนคงเหลือเป็นรายได้" + (dateNote != null ? $" · {dateNote}" : ""),
        });
        _db.AddChainedAuditLog(Audit(pass.CompanyId, actor, AuditAction.Update, "CreditPass", pass.Id, new
        {
            action = "ExpirePass", pass = pass.PassNumber, breakage = amount, remainingCredits, journalEntryId = jeId,
            ruleCode = CourseCreditMath.RuleCode, legalReference = CourseCreditMath.LegalReference,
        }));
    }

    // ===================================================================== ICreditBookingLedger (เครื่องจองภายนอก)

    public async Task<CreditHoldResult> HoldAsync(Guid companyId, CreditHoldRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalRef)) throw new BusinessRuleException("ต้องมีเลขอ้างอิงการจอง", CourseCreditMath.RuleCode);
        var extRef = request.ExternalRef.Trim();
        var bookingId = await InTxAsync(async () =>
        {
            // ฝ่ายค้านข้อ 3: สองคำขอ ref เดียวกันพร้อมกัน ⇒ รอกันที่นี่ (ตาข่ายสุดท้าย = unique index การจองที่มีผลต่อ ref)
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                AdvisoryLockKey.For(companyId, AdvisoryLockKey.CreditHold, extRef));
            var existing = await _db.CreditClassBookings.AsNoTracking()
                .FirstOrDefaultAsync(b => b.CompanyId == companyId && b.ExternalRef == extRef && b.Status != CreditBookingStatus.Cancelled, ct);
            if (existing != null) return existing.Id;   // idempotent — เรียกซ้ำได้ผลเดิม

            var candidates = request.PassId is Guid pid
                ? new List<Guid> { pid }
                : await _db.CreditPasses.AsNoTracking()
                    .Where(p => p.CompanyId == companyId && p.ContactId == request.ContactId && p.Status == CreditPassStatus.Active)
                    .OrderBy(p => p.ExpiresOn == null).ThenBy(p => p.ExpiresOn).ThenBy(p => p.PurchasedAt)   // FEFO
                    .Select(p => p.Id).Take(50).ToListAsync(ct);
            string? lastReason = "ลูกค้าไม่มีคอร์สที่ใช้งานได้";
            foreach (var id in candidates)
            {
                var pass = await LockPassAsync(companyId, id, ct);
                if (pass.ContactId != request.ContactId) { lastReason = "บัตรนี้ไม่ใช่ของลูกค้าคนนี้"; continue; }
                if (await SaleDocumentProblemAsync(pass, ct) is string docProblem) { lastReason = docProblem; continue; }
                // ตรวจซ้ำหลังได้ล็อกบัตร — ห้ามกันเครดิตซ้ำให้ ref ที่มีการจองที่มีผลอยู่แล้ว
                var dup = await _db.CreditClassBookings.AsNoTracking()
                    .Where(b => b.CompanyId == companyId && b.ExternalRef == extRef && b.Status != CreditBookingStatus.Cancelled)
                    .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
                if (dup is Guid dupId) return dupId;
                var credits = request.Credits is decimal c && c > 0m
                    ? c
                    : CourseCreditMath.CreditsForBooking(pass.Unit, request.DurationMinutes, 1m, request.Seats);
                lastReason = CourseCreditMath.CannotBookReason(Facts(pass), credits, request.StartAt, request.BookingServiceId,
                    request.Format, DateTime.UtcNow);
                if (lastReason != null) continue;
                var booking = Reserve(pass, credits, request.StartAt, null, extRef, null, null, actor);
                _db.AddChainedAuditLog(Audit(companyId, actor, AuditAction.Create, "CreditClassBooking", booking.Id,
                    new { action = "Hold", pass = pass.PassNumber, externalRef = extRef, credits }));
                await _db.SaveChangesAsync(ct);
                return booking.Id;
            }
            throw new BusinessRuleException(lastReason ?? "ใช้เครดิตจองไม่ได้", CourseCreditMath.RuleCode);
        }, ct);
        var b = await _db.CreditClassBookings.AsNoTracking().FirstAsync(x => x.Id == bookingId && x.CompanyId == companyId, ct);
        var p2 = await _db.CreditPasses.AsNoTracking().FirstAsync(x => x.Id == b.PassId && x.CompanyId == companyId, ct);
        return new CreditHoldResult(b.Id, p2.Id, p2.PassNumber, b.Credits,
            CourseCreditMath.Available(p2.CreditsTotal, p2.CreditsUsed, p2.CreditsReserved));
    }

    public async Task<CreditBookingResponse> ReleaseAsync(Guid companyId, string externalRef, bool byBusiness, string actor, CancellationToken ct = default)
        => await CancelBookingAsync(companyId, await BookingIdByRefAsync(companyId, externalRef, ct),
            new CancelCreditBookingRequest(byBusiness, null), actor, ct);

    public async Task<CreditBookingResponse> SettleAsync(Guid companyId, string externalRef, bool attended, string actor, CancellationToken ct = default)
        => await SettleBookingAsync(companyId, await BookingIdByRefAsync(companyId, externalRef, ct),
            new SettleCreditBookingRequest(attended, null), actor, ct);

    private async Task<Guid> BookingIdByRefAsync(Guid companyId, string externalRef, CancellationToken ct)
    {
        var r = (externalRef ?? "").Trim();
        return await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ExternalRef == r && b.Status == CreditBookingStatus.Booked)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"ไม่พบการจองด้วยเครดิตที่ค้างอยู่ของ {r}");
    }

    // ===================================================================== แกนการขยับยอด (เรียกภายใต้ล็อกบัตรเท่านั้น)

    private CreditClassBooking Reserve(CreditPass pass, decimal credits, DateTime startAt, Guid? sessionId, string? externalRef,
        Guid? trainerId, string? note, string actor)
    {
        var booking = new CreditClassBooking
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, SessionId = sessionId, ExternalRef = externalRef,
            PassId = pass.Id, ContactId = pass.ContactId, TrainerEmployeeId = trainerId, StartAt = startAt,
            Credits = credits, Status = CreditBookingStatus.Booked,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        _db.CreditClassBookings.Add(booking);
        pass.CreditsReserved += credits;
        pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Reserve,
            Credits = credits, BookingId = booking.Id, Note = $"จองเรียน {ThaiDate.ToThaiDisplayString(startAt)}",
        });
        return booking;
    }

    private void Release(CreditPass pass, CreditClassBooking booking, string actor, string note)
    {
        if (booking.Status != CreditBookingStatus.Booked)
            throw new BusinessRuleException("การจองนี้ปิด/ยกเลิกไปแล้ว", CourseCreditMath.RuleCode);
        booking.Status = CreditBookingStatus.Cancelled;
        booking.SettledAt = DateTime.UtcNow; booking.UpdatedAt = DateTime.UtcNow; booking.UpdatedBy = actor;
        pass.CreditsReserved = Math.Max(0m, pass.CreditsReserved - booking.Credits);
        pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Release,
            Credits = booking.Credits, BookingId = booking.Id, Note = note,
        });
        _db.AddChainedAuditLog(Audit(pass.CompanyId, actor, AuditAction.Update, "CreditClassBooking", booking.Id,
            new { action = "Release", pass = pass.PassNumber, credits = booking.Credits, note }));
    }

    private async Task ConsumeAsync(CreditPass pass, CreditClassBooking booking, CreditBookingStatus outcome, string? note, string actor, CancellationToken ct)
    {
        if (booking.Status != CreditBookingStatus.Booked)
            throw new BusinessRuleException("การจองนี้ปิด/ยกเลิกไปแล้ว", CourseCreditMath.RuleCode);
        if (pass.Status != CreditPassStatus.Active)
            throw new BusinessRuleException($"บัตรอยู่ในสถานะ {CourseCreditMath.StatusTh(pass.Status)} — ตัดเครดิตไม่ได้", CourseCreditMath.RuleCode);
        await EnsureSaleDocumentLiveAsync(pass, ct);
        var amount = CourseCreditMath.RecognizeOnUse(CourseCreditMath.EffectiveContract(pass.ContractAmount, pass.RefundedAmount),
            pass.CreditsTotal, pass.CreditsUsed, pass.RecognizedAmount, booking.Credits);
        Guid? jeId = null; string? dateNote = null;
        if (amount > 0m)
        {
            // วันที่รับรู้ = วันที่ให้บริการ (วันเรียน) — ไม่ใช่วันที่กดบันทึก
            var serviceDate = booking.StartAt <= DateTime.UtcNow ? booking.StartAt : DateTime.UtcNow;
            var je = await PostRecognitionAsync(pass, amount, serviceDate, $"{BookingStatusTh(outcome)} {booking.Credits:0.##} {UnitTh(pass.Unit)}", actor, ct);
            jeId = je.Id; dateNote = je.DateNote;
        }
        booking.Status = outcome;
        booking.RecognizedAmount = amount;
        booking.SettledAt = DateTime.UtcNow; booking.UpdatedAt = DateTime.UtcNow; booking.UpdatedBy = actor;
        if (!string.IsNullOrWhiteSpace(note)) booking.Note = string.IsNullOrWhiteSpace(booking.Note) ? note.Trim() : $"{booking.Note} · {note.Trim()}";
        pass.CreditsReserved = Math.Max(0m, pass.CreditsReserved - booking.Credits);
        pass.CreditsUsed += booking.Credits;
        pass.RecognizedAmount += amount;
        if (pass.CreditsUsed >= pass.CreditsTotal)
        {
            pass.Status = CreditPassStatus.Exhausted;
            pass.ClosedAt = DateTime.UtcNow;
        }
        pass.UpdatedAt = DateTime.UtcNow; pass.UpdatedBy = actor;
        _db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            CompanyId = pass.CompanyId, CreatedBy = actor, PassId = pass.Id, Kind = CreditLedgerKind.Consume,
            Credits = booking.Credits, RecognizedAmount = amount, BookingId = booking.Id, JournalEntryId = jeId,
            Note = BookingStatusTh(outcome) + (dateNote != null ? $" · {dateNote}" : ""),
        });
        _db.AddChainedAuditLog(Audit(pass.CompanyId, actor, AuditAction.Update, "CreditClassBooking", booking.Id, new
        {
            action = "Consume", outcome = outcome.ToString(), pass = pass.PassNumber, credits = booking.Credits, recognized = amount,
            journalEntryId = jeId, ruleCode = CourseCreditMath.RuleCode, legalReference = CourseCreditMath.LegalReference,
        }));
    }

    /// <summary>JE รับรู้รายได้ผ่าน <see cref="JournalEntryBuilder"/> (ตัวสร้าง JE กลาง · ด่านงวดปิดตัวเดียวกัน) — วันที่บริการอยู่ในงวดปิด ⇒
    /// ลงวันนี้แทน + บอกในหมายเหตุ (ไม่เงียบ) · วันนี้ก็ปิด ⇒ ล้มดัง</summary>
    private async Task<(Guid Id, string Number, string? DateNote)> PostRecognitionAsync(CreditPass pass, decimal amount, DateTime serviceDateUtc,
        string what, string actor, CancellationToken ct)
    {
        var date = ThaiDate.CalendarDateUtc(serviceDateUtc);
        string? dateNote = null;
        if (await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, pass.CompanyId, date, ct) is string closed)
        {
            var today = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
            if (today != date && await JournalEntryBuilder.ClosedPeriodReasonAsync(_db, pass.CompanyId, today, ct) is null)
            {
                dateNote = $"ลงบัญชีวันที่ {ThaiDate.ToThaiDisplayString(today)} แทน {ThaiDate.ToThaiDisplayString(date)} (งวดของวันให้บริการปิดแล้ว)";
                date = today;
            }
            else throw new BusinessRuleException(closed, CourseCreditMath.RuleCode);
        }
        var je = await JournalEntryBuilder.For(_db, pass.CompanyId, date)
            .Reference(pass.PassNumber)
            .Description($"รับรู้รายได้คอร์ส {pass.PassNumber} ({pass.PackageName}) — {what}")
            .Debit(pass.UnearnedAccountId, amount, $"ตัดค่าบริการรับล่วงหน้า {pass.PassNumber}")
            .Credit(pass.RevenueAccountId, amount, $"รายได้ค่าบริการ {pass.PassNumber}")
            .PostAsync(actor, ct);
        return (je.Id, je.EntryNumber, dateNote);
    }

    private async Task<CreditClassSession> NewOneOnOneSessionAsync(Guid companyId, Guid passId, DateTime start, DateTime end,
        Guid? bookingServiceId, Guid? requestedTrainer, string actor, CancellationToken ct)
    {
        var pkg = await (from p in _db.CreditPasses.AsNoTracking()
                         join k in _db.CreditPackages.AsNoTracking() on p.PackageId equals k.Id
                         where p.Id == passId && p.CompanyId == companyId && k.CompanyId == companyId
                         select new { k.Name, k.TrainerMode, k.TrainerEmployeeIds }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรคอร์ส");
        var eligible = ParseIds(pkg.TrainerEmployeeIds);
        Guid? trainer = null;
        if (requestedTrainer is Guid want)
        {
            if (eligible.Count > 0 && !eligible.Contains(want))
                throw new BusinessRuleException("ครูฝึกที่เลือกไม่ได้สอนแพ็กเกจนี้", CourseCreditMath.RuleCode);
            if (!await _db.Employees.AsNoTracking().AnyAsync(e => e.Id == want && e.CompanyId == companyId, ct))
                throw new BusinessRuleException("ไม่พบครูฝึกในทะเบียนพนักงาน", CourseCreditMath.RuleCode);
            await LockTrainerDaysAsync(companyId, new[] { want }, start, ct);
            if (await TrainerBusyAsync(companyId, want, start, end, null, ct))
                throw new BusinessRuleException("ครูฝึกที่เลือกติดสอนรอบอื่นในเวลานี้ — เลือกเวลาหรือครูคนอื่น", CourseCreditMath.RuleCode);
            trainer = want;
        }
        else if (eligible.Count > 0)
        {
            if (pkg.TrainerMode == TrainerAssignmentMode.CustomerChooses)
                throw new BusinessRuleException("แพ็กเกจนี้ให้ลูกค้าเลือกครูฝึก — เลือกครูก่อนจอง", CourseCreditMath.RuleCode);
            await LockTrainerDaysAsync(companyId, eligible, start, ct);
            var overlapping = await _db.CreditClassSessions.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.Status == CreditSessionStatus.Scheduled && s.TrainerEmployeeId != null
                    && eligible.Contains(s.TrainerEmployeeId.Value) && s.StartAt < end && s.EndAt > start)
                .Select(s => s.TrainerEmployeeId!.Value).ToListAsync(ct);
            var dayFrom = start.AddHours(-12); var dayTo = start.AddHours(12);
            var load = await _db.CreditClassSessions.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.Status == CreditSessionStatus.Scheduled && s.TrainerEmployeeId != null
                    && eligible.Contains(s.TrainerEmployeeId.Value) && s.StartAt >= dayFrom && s.StartAt < dayTo)
                .GroupBy(s => s.TrainerEmployeeId!.Value).Select(g => new { g.Key, N = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.N, ct);
            trainer = CourseCreditMath.PickTrainer(eligible, load, overlapping.ToHashSet())
                ?? throw new BusinessRuleException("ไม่มีครูฝึกของแพ็กเกจนี้ว่างในเวลานี้ — เลือกเวลาอื่น", CourseCreditMath.RuleCode);
        }
        var s = new CreditClassSession
        {
            CompanyId = companyId, CreatedBy = actor, Title = $"{pkg.Name} (ตัวต่อตัว)", BookingServiceId = bookingServiceId,
            Format = CreditClassFormat.OneOnOne, StartAt = start, EndAt = end, Capacity = 1, TrainerEmployeeId = trainer, CreditCost = 1m,
        };
        _db.CreditClassSessions.Add(s);
        return s;
    }

    /// <summary>ใบขายของบัตรยังมีผลไหม — ใบถูกยกเลิก/ลบ หรือมีใบลดหนี้อ้างใบนี้ ⇒ ยอดหนี้สินในบัตรไม่ตรงสมุดแล้ว
    /// (รับรู้ต่อ = 21713 ติดลบ/รายได้เกินจริง) ⇒ ห้ามจอง/ตัดเครดิต/ปิดหมดอายุ จนกว่าผู้มีสิทธิ์จัดการบัตร (backlog: ยกเลิกบัตร+คืนเงิน)</summary>
    private async Task<string?> SaleDocumentProblemAsync(CreditPass pass, CancellationToken ct)
    {
        if (pass.SaleDocumentId is not Guid docId) return null;
        var doc = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.Id == docId && d.CompanyId == pass.CompanyId)
            .Select(d => new { d.Status, d.IsDeleted, d.DocumentNumber }).FirstOrDefaultAsync(ct);
        if (doc == null || doc.IsDeleted || doc.Status == DocumentStatus.Voided)
            return $"เอกสารขาย {pass.SaleDocumentNumber} ของบัตรนี้ถูกยกเลิกแล้ว — ใช้บัตรไม่ได้ (ยอดรับล่วงหน้าถูกกลับรายการแล้ว)";
        var hasCreditNote = await _db.Documents.AsNoTracking()
            .AnyAsync(d => d.CompanyId == pass.CompanyId && d.RelatedDocumentId == docId && d.DocumentType == DocumentType.CreditNote
                && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Rejected, ct);
        if (hasCreditNote)
            return $"เอกสารขาย {doc.DocumentNumber} มีใบลดหนี้อ้างอยู่ — ยอดสัญญาในบัตรไม่ตรงแล้ว ใช้บัตรต่อไม่ได้จนกว่าจะปรับบัตร (ติดต่อผู้ดูแลบัญชี)";
        return null;
    }

    private async Task EnsureSaleDocumentLiveAsync(CreditPass pass, CancellationToken ct)
    {
        if (await SaleDocumentProblemAsync(pass, ct) is string problem)
            throw new BusinessRuleException(problem, CourseCreditMath.RuleCode);
    }

    /// <summary>Σ(Cr − Dr) ต่อผังหนี้สิน ของ JE ที่มีผล (Posted · ไม่ถูกกลับ) ของเอกสารนี้ — บาทตาม GL</summary>
    private async Task<List<(Guid Key, string Code, decimal Value)>> JournalLiabilityCreditsAsync(Guid companyId, Guid documentId, CancellationToken ct)
    {
        var rows = await (from l in _db.JournalEntryLines.AsNoTracking()
                          join j in _db.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                          join a in _db.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                          where j.CompanyId == companyId && j.SourceDocumentId == documentId && !j.IsDeleted && !l.IsDeleted
                                && j.Status == JournalEntryStatus.Posted && j.ReversedByEntryId == null
                                && a.CompanyId == companyId && a.AccountType == AccountType.Liability
                          select new { a.Id, a.AccountCode, l.CreditAmount, l.DebitAmount })
            .ToListAsync(ct);
        return rows.GroupBy(r => new { r.Id, r.AccountCode })
            .Select(g => (Key: g.Key.Id, Code: g.Key.AccountCode, Value: g.Sum(x => x.CreditAmount - x.DebitAmount))).ToList();
    }

    /// <summary>ล็อกตารางสอนของครูต่อวัน (วันไทย) — ตรวจว่าง→สร้างรอบ ของสองคำขอพร้อมกันต้องรอกัน (ฝ่ายค้านข้อ 5) · เรียงคีย์ก่อนล็อก (กัน deadlock)</summary>
    private async Task LockTrainerDaysAsync(Guid companyId, IEnumerable<Guid> trainerIds, DateTime startUtc, CancellationToken ct)
    {
        var day = ThaiDate.YyyyMmDd(startUtc);
        foreach (var t in trainerIds.Distinct().OrderBy(x => x))
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                AdvisoryLockKey.For(companyId, AdvisoryLockKey.CreditTrainerDay, $"{t}:{day}"));
    }

    private Task<bool> TrainerBusyAsync(Guid companyId, Guid trainerId, DateTime start, DateTime end, Guid? exceptSessionId, CancellationToken ct)
        => _db.CreditClassSessions.AsNoTracking().AnyAsync(s => s.CompanyId == companyId && s.TrainerEmployeeId == trainerId
            && s.Status == CreditSessionStatus.Scheduled && s.StartAt < end && s.EndAt > start
            && (exceptSessionId == null || s.Id != exceptSessionId), ct);

    private async Task<CreditPass> LockPassAsync(Guid companyId, Guid passId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"CreditPasses\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", passId, companyId);
        var pass = await _db.CreditPasses.FirstOrDefaultAsync(p => p.Id == passId && p.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบบัตรคอร์ส");
        // ค่าหลังได้ล็อก — แถวที่ติดตามอยู่แล้วแต่ยังไม่แก้ในงานนี้อาจเป็นค่าก่อนล็อก ⇒ โหลดใหม่ · แถวที่แก้ค้างในงานนี้ห้ามโหลดทับ
        if (_db.Entry(pass).State == EntityState.Unchanged) await _db.Entry(pass).ReloadAsync(ct);
        return pass;
    }

    private async Task<CreditClassBooking> LockBookingAsync(Guid companyId, Guid bookingId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"CreditClassBookings\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", bookingId, companyId);
        var b = await _db.CreditClassBookings.FirstOrDefaultAsync(x => x.Id == bookingId && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบการจอง");
        if (_db.Entry(b).State == EntityState.Unchanged) await _db.Entry(b).ReloadAsync(ct);
        return b;
    }

    private async Task<T> InTxAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction != null) return await work();
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work();
                await tx.CommitAsync(ct);
                return result;
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();   // ถอดของที่ค้าง (บัตร/JE/สมุด) — ไม่ให้ไหลไปกับ SaveChanges ถัดไป
                throw;
            }
        });
    }

    // ===================================================================== map / ตัวช่วย

    private async Task<CreditBookingResponse> GetBookingAsync(Guid companyId, Guid bookingId, CancellationToken ct)
    {
        var b = await _db.CreditClassBookings.AsNoTracking().FirstAsync(x => x.Id == bookingId && x.CompanyId == companyId, ct);
        return (await MapBookingsAsync(companyId, new List<CreditClassBooking> { b }, ct))[0];
    }

    private async Task<List<CreditBookingResponse>> MapBookingsAsync(Guid companyId, List<CreditClassBooking> rows, CancellationToken ct)
    {
        var names = await ContactNamesAsync(companyId, rows.Select(r => r.ContactId), ct);
        var passIds = rows.Select(r => r.PassId).Distinct().ToList();
        var passNos = await _db.CreditPasses.AsNoTracking().Where(p => p.CompanyId == companyId && passIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.PassNumber, ct);
        var sessionIds = rows.Where(r => r.SessionId.HasValue).Select(r => r.SessionId!.Value).Distinct().ToList();
        var sessions = await _db.CreditClassSessions.AsNoTracking().Where(s => s.CompanyId == companyId && sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new { s.Title, s.TrainerEmployeeId }, ct);
        var trainerIds = rows.Select(r => r.TrainerEmployeeId)
            .Concat(sessions.Values.Select(s => s.TrainerEmployeeId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var trainers = await TrainerNamesAsync(companyId, trainerIds, ct);
        return rows.Select(b =>
        {
            var sess = b.SessionId is Guid sid && sessions.TryGetValue(sid, out var sv) ? sv : null;
            var tid = b.TrainerEmployeeId ?? sess?.TrainerEmployeeId;
            return new CreditBookingResponse(b.Id, b.SessionId, sess?.Title, b.ExternalRef, b.PassId,
                passNos.TryGetValue(b.PassId, out var pn) ? pn : null, b.ContactId, names.TryGetValue(b.ContactId, out var cn) ? cn : null,
                tid, tid is Guid t && trainers.TryGetValue(t, out var tn) ? tn : null,
                b.StartAt, b.Credits, b.Status, BookingStatusTh(b.Status), b.RecognizedAmount, b.SettledAt, b.Note);
        }).ToList();
    }

    private async Task<List<CreditSessionResponse>> MapSessionsAsync(Guid companyId, List<CreditClassSession> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await _db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SessionId != null && ids.Contains(b.SessionId.Value) && b.Status != CreditBookingStatus.Cancelled)
            .GroupBy(b => b.SessionId!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var trainers = await TrainerNamesAsync(companyId, rows.Where(r => r.TrainerEmployeeId.HasValue).Select(r => r.TrainerEmployeeId!.Value), ct);
        return rows.Select(s => new CreditSessionResponse(s.Id, s.Title, s.BookingServiceId, s.Format, CourseCreditMath.FormatTh(s.Format),
            s.StartAt, s.EndAt, s.Capacity, counts.TryGetValue(s.Id, out var n) ? n : 0,
            s.TrainerEmployeeId, s.TrainerEmployeeId is Guid t && trainers.TryGetValue(t, out var tn) ? tn : null,
            s.CreditCost, s.Status, SessionStatusTh(s.Status), s.Notes)).ToList();
    }

    private static CreditPassResponse MapPass(CreditPass p, IReadOnlyDictionary<Guid, string> names, DateTime nowUtc)
    {
        string? warning = null;
        if (p.Status == CreditPassStatus.Active && CourseCreditMath.IsExpired(p.ExpiresOn, nowUtc))
            warning = p.CreditsReserved > 0m
                ? $"หมดอายุแล้ว — มีการจองค้าง {p.CreditsReserved:0.##} เครดิต ปิดการจองก่อนระบบจึงปิดบัตรได้"
                : "หมดอายุแล้ว — รอระบบปิดบัตรรอบกลางคืน (หรือกด \"ปิดบัตรหมดอายุ\")";
        return new CreditPassResponse(p.Id, p.PassNumber, p.PackageId, p.PackageName, p.ContactId,
            names.TryGetValue(p.ContactId, out var n) ? n : null, p.Unit, UnitTh(p.Unit), p.PurchasedAt, p.ExpiresOn,
            p.CreditsTotal, p.CreditsUsed, p.CreditsReserved, CourseCreditMath.Available(p.CreditsTotal, p.CreditsUsed, p.CreditsReserved),
            p.ContractAmount, p.RecognizedAmount, p.ContractAmount - p.RecognizedAmount - p.RefundedAmount,
            CourseCreditMath.PricePerCredit(CourseCreditMath.EffectiveContract(p.ContractAmount, p.RefundedAmount), p.CreditsTotal),
            p.Status, CourseCreditMath.StatusTh(p.Status), p.SaleDocumentId, p.SaleDocumentNumber, warning);
    }

    private static CreditPackageResponse MapPackage(CreditPackage p, IReadOnlyDictionary<Guid, string> labels, int activePasses)
        => new(p.Id, p.Code, p.Name, p.Description, p.Unit, UnitTh(p.Unit), p.CreditQuantity, p.ValidityDays,
            p.Price, p.PriceIncludesVat, p.VatRate, VatTh(p.VatRate), p.ClassFormat, CourseCreditMath.FormatTh(p.ClassFormat),
            p.TrainerMode, p.TrainerMode == TrainerAssignmentMode.SystemAssigns ? "ระบบกำหนดครูให้" : "ลูกค้าเลือกครูเอง",
            ParseIds(p.AllowedServiceIds), ParseIds(p.TrainerEmployeeIds), p.FreeCancelHours,
            p.UnearnedAccountId, p.UnearnedAccountId is Guid u && labels.TryGetValue(u, out var ul) ? ul : null,
            p.RevenueAccountId, p.RevenueAccountId is Guid r && labels.TryGetValue(r, out var rl) ? rl : null,
            p.IsActive, p.SortOrder, activePasses);

    private static CourseCreditMath.PassFacts Facts(CreditPass p)
        => new(p.Status, p.ExpiresOn, p.CreditsTotal, p.CreditsUsed, p.CreditsReserved, p.ClassFormat, ParseIds(p.AllowedServiceIds));

    private async Task<ChartOfAccount> ResolveAccountAsync(Guid companyId, Guid? configured, string code, string prefix,
        AccountType type, string label, CancellationToken ct)
    {
        if (configured is Guid id)
        {
            await EnsureAccountTypeAsync(companyId, id, type, label, ct);
            return await _db.ChartOfAccounts.AsNoTracking().FirstAsync(a => a.Id == id && a.CompanyId == companyId, ct);
        }
        var acc = await _db.ChartOfAccounts.AsNoTracking()
                      .Where(a => a.CompanyId == companyId && a.IsActive && a.AccountCode == code)
                      .DefaultPostingCandidates(_db).FirstOrDefaultAsync(ct)
                  ?? await _db.ChartOfAccounts.AsNoTracking()
                      .Where(a => a.CompanyId == companyId && a.IsActive && a.AccountType == type && a.AccountCode.StartsWith(prefix))
                      .DefaultFallbackCandidates(_db).FirstOrDefaultAsync(ct);
        if (acc == null)
            throw new BusinessRuleException($"ไม่พบผังบัญชี \"{label}\" ({code}) — เพิ่มผังบัญชี หรือเลือกผังในแพ็กเกจก่อนขาย", CourseCreditMath.RuleCode);
        if (acc.AccountType != type)
            throw new BusinessRuleException($"ผัง {acc.AccountCode} ไม่ใช่หมวด{(type == AccountType.Liability ? "หนี้สิน" : "รายได้")} — เลือกผัง \"{label}\" ในแพ็กเกจ", CourseCreditMath.RuleCode);
        return acc;
    }

    private async Task EnsureAccountTypeAsync(Guid companyId, Guid accountId, AccountType type, string label, CancellationToken ct)
    {
        await _db.EnsurePostingAccountAsync(companyId, accountId, label, ct);
        var t = await _db.ChartOfAccounts.AsNoTracking().Where(a => a.Id == accountId && a.CompanyId == companyId)
            .Select(a => a.AccountType).FirstAsync(ct);
        if (t != type)
            throw new BusinessRuleException($"{label}: ต้องเป็นผังหมวด{(type == AccountType.Liability ? "หนี้สิน" : "รายได้")}", CourseCreditMath.RuleCode);
    }

    private async Task<Dictionary<Guid, string>> AccountLabelsAsync(Guid companyId, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await _db.ChartOfAccounts.AsNoTracking().Where(a => a.CompanyId == companyId && list.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.AccountCode + " " + a.AccountName, ct);
    }

    private async Task<Dictionary<Guid, string>> ContactNamesAsync(Guid companyId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && list.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    private async Task<Dictionary<Guid, string>> TrainerNamesAsync(Guid companyId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        var rows = await _db.Employees.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.CompanyId == companyId && list.Contains(e.Id))
            .Select(e => new { e.Id, e.NickName, e.FirstNameTh, e.LastNameTh }).ToListAsync(ct);
        return rows.ToDictionary(e => e.Id, e => TrainerName(e.NickName, e.FirstNameTh, e.LastNameTh));
    }

    private static string TrainerName(string? nick, string first, string last)
        => string.IsNullOrWhiteSpace(nick) ? $"{first} {last}".Trim() : $"{nick} ({first})";

    private static string? JoinIds(List<Guid>? ids)
        => ids == null || ids.Count == 0 ? null : string.Join(",", ids.Distinct());

    private static List<Guid> ParseIds(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new List<Guid>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();

    private static string UnitTh(CreditUnit u) => u == CreditUnit.Hour ? "ชั่วโมง" : "ครั้ง";

    private static string VatTh(decimal r) => r switch
    {
        7m => "VAT 7%",
        0m => "VAT 0% (§80/1)",
        _ => "ยกเว้น VAT (§81)",
    };

    private static string KindTh(CreditLedgerKind k) => k switch
    {
        CreditLedgerKind.Purchase => "ซื้อคอร์ส",
        CreditLedgerKind.Reserve => "จอง (กันเครดิต)",
        CreditLedgerKind.Release => "คืนเครดิต",
        CreditLedgerKind.Consume => "ใช้เครดิต",
        CreditLedgerKind.Expire => "หมดอายุ",
        CreditLedgerKind.Cancel => "ยกเลิกบัตร",
        CreditLedgerKind.Adjust => "ลดเครดิต (คืนเงินบางส่วน)",
        CreditLedgerKind.AdjustReversal => "คืนเครดิต (ยกเลิกใบลดหนี้)",
        _ => k.ToString(),
    };

    private static string BookingStatusTh(CreditBookingStatus s) => s switch
    {
        CreditBookingStatus.Booked => "จองแล้ว",
        CreditBookingStatus.Attended => "มาเรียน",
        CreditBookingStatus.NoShow => "ไม่มา (no-show)",
        CreditBookingStatus.Cancelled => "ยกเลิก (คืนเครดิต)",
        CreditBookingStatus.LateCancelled => "ยกเลิกช้า (ริบเครดิต)",
        _ => s.ToString(),
    };

    private static string SessionStatusTh(CreditSessionStatus s) => s switch
    {
        CreditSessionStatus.Scheduled => "เปิดจอง",
        CreditSessionStatus.Cancelled => "ยกเลิก",
        CreditSessionStatus.Completed => "จบแล้ว",
        _ => s.ToString(),
    };

    private static AuditLog Audit(Guid companyId, string actor, AuditAction action, string entityType, Guid entityId, object payload)
        => new()
        {
            CompanyId = companyId,
            UserEmail = actor,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            NewValues = JsonSerializer.Serialize(payload),
        };
}
