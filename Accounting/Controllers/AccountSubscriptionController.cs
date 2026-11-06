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
/// Account-level subscription endpoints — the paying user manages their plan
/// here, then attaches or detaches Companies (subject to MaxCompanies on the
/// plan). All endpoints scope to the calling User; admins / system staff use
/// the admin endpoints to override.
/// </summary>
[ApiController]
[Route("api/account-subscription")]
[Authorize]
public class AccountSubscriptionController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly Services.Interfaces.ISubscriptionService _subSvc;
    public AccountSubscriptionController(AccountingDbContext db, Services.Interfaces.ISubscriptionService subSvc)
    {
        _db = db; _subSvc = subSvc;
    }

    public record AccountSubDto(
        Guid Id, Guid OwnerUserId, Guid PlanTemplateId, string PlanName,
        SubscriptionStatus Status, DateTime StartDate, DateTime EndDate,
        int MaxCompanies, int CompaniesUsed, int MaxUsersPerCompany,
        int MaxDocumentsPerMonth, int MaxJournalEntriesPerMonth,
        long MaxStorageBytes, int MaxOcrPagesPerMonth,
        long EnabledFeaturesValue, decimal MonthlyPrice, decimal AnnualPrice,
        BillingCycle BillingCycle, int GracePeriodDays);

    /// <summary>Current user's account plan + the list of Companies attached.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetMine()
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var acct = await _db.AccountSubscriptions
            .Include(a => a.PlanTemplate)
            .Where(a => a.OwnerUserId == userId && !a.IsDeleted
                && a.Status != SubscriptionStatus.Cancelled
                && a.Status != SubscriptionStatus.Expired)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
        // รอบ 238 (236-3 ข้อ 6): License หมดอายุ (พ้นผ่อนผัน) ที่ยังมีบริษัทผูกอยู่ ⇒ ผู้ถือยังต้องเห็นหน้านี้เพื่อถอดบริษัทออก (เดิมกรองทิ้ง ⇒ ถอดไม่ได้)
        acct ??= await _db.AccountSubscriptions
            .Include(a => a.PlanTemplate)
            .Where(a => a.OwnerUserId == userId && !a.IsDeleted && a.Status == SubscriptionStatus.Expired
                && _db.Subscriptions.Any(s => s.AccountSubscriptionId == a.Id && !s.IsDeleted))
            .OrderByDescending(a => a.EndDate)
            .FirstOrDefaultAsync();

        if (acct == null)
        {
            // Surface the companies the user owns so the UI can prompt
            // "start an Account Plan to cover these in one bill".
            var owned = await _db.CompanyUsers
                .Where(cu => cu.UserId == userId && cu.Role == UserRole.Owner)
                .Include(cu => cu.Company)
                .Select(cu => new { cu.Company.Id, cu.Company.Name })
                .ToListAsync();
            return Ok(new ApiResponse<object>(true, new
            {
                hasAccountPlan = false,
                ownedCompanies = owned,
            }, "ยังไม่มี Account Plan — แต่ละบริษัทใช้ subscription ของตัวเอง"));
        }

        var usedCount = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted);
        var attachedRaw = await _db.Subscriptions
            .Where(s => s.AccountSubscriptionId == acct.Id && !s.IsDeleted)
            .Include(s => s.Company)
            .Select(s => new
            {
                s.CompanyId, CompanyName = s.Company.Name, s.Status,
                // Per-company breakdown for the "X used in Co A · Y used in
                // Co B" gauge — sum of these = the aggregate displayed below.
                s.CurrentMonthDocuments, s.CurrentMonthJournalEntries,
                s.CurrentMonthOcrPages,
            })
            .ToListAsync();
        // พื้นที่รายบริษัทคิดจากของจริง (Σ ไฟล์แนบ + สื่อ CMS) — คอลัมน์ Subscription.CurrentStorageUsed ไม่มีใครเขียน
        // ⇒ เดิมหน้านี้โชว์ 0 ทุกบริษัท (รอบ 193 ข้อ 30) · ชื่อช่องเดิม (currentStorageUsed) คงไว้ให้หน้าเว็บอ่านได้เหมือนเดิม
        var storageByCompany = await _subSvc.GetStorageBytesByCompanyAsync(attachedRaw.Select(x => x.CompanyId).ToList());
        var attached = attachedRaw.Select(x => new
        {
            x.CompanyId, x.CompanyName, x.Status,
            x.CurrentMonthDocuments, x.CurrentMonthJournalEntries,
            CurrentStorageUsed = storageByCompany.TryGetValue(x.CompanyId, out var companyBytes) ? companyBytes : 0L,
            x.CurrentMonthOcrPages,
        }).ToList();

        // Owner-of-but-not-yet-attached so the UI can prompt "ผูกบริษัท X ?"
        var ownedNotAttached = await (from cu in _db.CompanyUsers
                                      join s in _db.Subscriptions on cu.CompanyId equals s.CompanyId
                                      where cu.UserId == userId && cu.Role == UserRole.Owner && s.AccountSubscriptionId == null
                                      select new { Id = cu.Company.Id, Name = cu.Company.Name })
                                     .ToListAsync();

        var dto = new AccountSubDto(acct.Id, acct.OwnerUserId, acct.PlanTemplateId, acct.PlanTemplate.Name,
            acct.Status, acct.StartDate, acct.EndDate, acct.MaxCompanies, usedCount,
            acct.MaxUsersPerCompany, acct.MaxDocumentsPerMonth, acct.MaxJournalEntriesPerMonth,
            acct.MaxStorageBytes, acct.MaxOcrPagesPerMonth,
            (long)acct.EnabledFeatures, acct.MonthlyPrice, acct.AnnualPrice,
            acct.BillingCycle, acct.GracePeriodDays);
        var agg = await _subSvc.GetAggregateUsageAsync(acct.Id);

        return Ok(new ApiResponse<object>(true, new
        {
            hasAccountPlan = true,
            plan = dto,
            // ป้ายสถานะภาษาไทยจากตารางเดียว (WP-13 J · D §10 — หน้าเว็บเคยแสดง enum ดิบ "Active"/"Trial")
            planStatusLabel = PlanIdentityResolver.StatusLabel(acct.Status),
            attachedCompanies = attached,
            ownedCompanies = ownedNotAttached,
            usage = agg,
        }));
    }

    public record StartTrialRequest(Guid PlanTemplateId);

    /// <summary>Start the account-level trial. Picks limits from PlanTemplate
    /// at create time so admin plan changes don't silently shrink an
    /// established account.</summary>
    [HttpPost("start-trial")]
    [Accounting.Filters.RejectApiKey("เริ่มทดลองใช้ Account Plan")]
    public async Task<ActionResult<ApiResponse<AccountSubDto>>> StartTrial([FromBody] StartTrialRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var existing = await _db.AccountSubscriptions
            .FirstOrDefaultAsync(a => a.OwnerUserId == userId && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active || a.Status == SubscriptionStatus.PastDue || a.Status == SubscriptionStatus.Suspended));
        if (existing != null)
            return BadRequest(new ApiResponse<AccountSubDto>(false, null!, "มี Account Plan ที่ active อยู่แล้ว"));

        var tpl = await _db.PlanTemplates.FirstOrDefaultAsync(p => p.Id == req.PlanTemplateId && p.IsActive);
        if (tpl == null) return BadRequest(new ApiResponse<AccountSubDto>(false, null!, "ไม่พบ plan template"));

        // ฝ่ายค้านรอบ 219 S4: ทดลองใช้ระดับบัญชีได้ครั้งเดียวต่อเจ้าของต่อระดับแพ็กเกจ — เดิมหมดอายุ/ยกเลิกแล้วเริ่มใหม่ได้ไม่จำกัด (ใช้ฟรีตลอดกาล) ·
        // นับแถวที่ถูกลบด้วย (ลบแล้วเริ่มใหม่ = ทางเลี่ยงเดิม)
        var previousPlans = await _db.AccountSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OwnerUserId == userId)
            .Join(_db.PlanTemplates.IgnoreQueryFilters().AsNoTracking(), a => a.PlanTemplateId, t => t.Id, (a, t) => t.Plan)
            .ToListAsync();
        if (!Accounting.Helpers.PlatformPlanPricing.MayStartAccountTrial(previousPlans, tpl.Plan))
            return BadRequest(new ApiResponse<AccountSubDto>(false, null!,
                "บัญชีนี้เคยทดลองใช้แพ็กเกจระดับนี้แล้ว — เลือกแพ็กเกจจ่ายเงิน หรือติดต่อผู้ดูแลระบบ NextAcc"));

        var acct = new AccountSubscription
        {
            OwnerUserId = userId,
            PlanTemplateId = tpl.Id,
            Status = SubscriptionStatus.Trial,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(tpl.TrialDurationDays > 0 ? tpl.TrialDurationDays : 14),
            MaxCompanies = Math.Max(1, tpl.MaxCompanies),
            MaxUsersPerCompany = tpl.MaxUsers,
            MaxDocumentsPerMonth = tpl.MaxDocumentsPerMonth,
            MaxJournalEntriesPerMonth = tpl.MaxJournalEntriesPerMonth,
            MaxStorageBytes = tpl.MaxStorageBytes,
            MaxOcrPagesPerMonth = tpl.MaxOcrPagesPerMonth,
            AzureOcrPagesPerMonth = tpl.AzureOcrPagesPerMonth,
            LocalOcrPagesPerMonth = tpl.LocalOcrPagesPerMonth,
            EnabledFeatures = tpl.EnabledFeatures,
            MonthlyPrice = tpl.MonthlyPrice,
            AnnualPrice = tpl.AnnualPrice,
            BillingCycle = BillingCycle.Monthly,
            GracePeriodDays = 7,
        };
        _db.AccountSubscriptions.Add(acct);
        await _db.SaveChangesAsync();

        return StatusCode(201, new ApiResponse<AccountSubDto>(true,
            new AccountSubDto(acct.Id, acct.OwnerUserId, tpl.Id, tpl.Name,
                acct.Status, acct.StartDate, acct.EndDate, acct.MaxCompanies, 0,
                acct.MaxUsersPerCompany, acct.MaxDocumentsPerMonth, acct.MaxJournalEntriesPerMonth,
                acct.MaxStorageBytes, acct.MaxOcrPagesPerMonth,
                (long)acct.EnabledFeatures, acct.MonthlyPrice, acct.AnnualPrice,
                acct.BillingCycle, acct.GracePeriodDays),
            "เริ่มทดลองใช้ Account Plan สำเร็จ"));
    }

    public record AttachRequest(Guid CompanyId);

    /// <summary>Attach a Company under the user's Account Plan. The user must
    /// be Owner of the Company. Reduces the company to the account's quota
    /// (account features win). Respects MaxCompanies.</summary>
    // ฝ่ายค้านรอบ 193 W-C3: CompanyId มาจาก body — คีย์ของบริษัท A (ตัวตนเจ้าของ) เคยผูก/ถอดบริษัท B ของเจ้าของคนเดียวกันได้
    // (TenantAccessMiddleware ดูแค่ route) ⇒ คีย์ห้าม · คนที่ล็อกอินยังต้องเป็น Owner ของบริษัทใน body (ตรวจด้านล่าง)
    [HttpPost("attach")]
    [Accounting.Filters.RejectApiKey("ผูกบริษัทเข้า Account Plan")]
    public async Task<ActionResult<ApiResponse<string>>> Attach([FromBody] AttachRequest req)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var acct = await _db.AccountSubscriptions
            .FirstOrDefaultAsync(a => a.OwnerUserId == userId && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active || a.Status == SubscriptionStatus.PastDue));
        if (acct == null) return BadRequest(new ApiResponse<string>(false, null, "ไม่มี Account Plan ที่ใช้งานได้"));

        // Ownership check — only Owner of the Company can attach it.
        var isOwner = await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == req.CompanyId
            && cu.Role == UserRole.Owner);
        if (!isOwner)
            return StatusCode(403, new ApiResponse<string>(false, null,
                "ทำได้เฉพาะเจ้าของบริษัทนั้น — ผูก/ถอดได้เฉพาะบริษัทที่คุณเป็นเจ้าของ (Owner)"));

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == req.CompanyId && !s.IsDeleted);
        if (sub == null) return BadRequest(new ApiResponse<string>(false, null, "บริษัทยังไม่มี Subscription"));
        if (sub.AccountSubscriptionId == acct.Id)
            return Ok(new ApiResponse<string>(true, null, "ผูกกับ Account Plan อยู่แล้ว"));

        // Quota check — count companies currently attached (excluding the one
        // being attached if it's already counted).
        var used = await _db.Subscriptions.CountAsync(s => s.AccountSubscriptionId == acct.Id
            && s.CompanyId != req.CompanyId && !s.IsDeleted);
        if (used >= acct.MaxCompanies)
            return BadRequest(new ApiResponse<string>(false, null,
                $"Account Plan ครอบได้สูงสุด {acct.MaxCompanies} บริษัท — ปัจจุบันใช้ {used} บริษัท · อัพเกรดเพื่อเพิ่ม"));

        // รอบ 238 (คำตัดสิน 236-9 · ค่าตั้งต้นรอเจ้าของยืนยัน): เวลาที่บริษัทจ่ายแพ็กเกจเองไว้เดินต่อ ไม่คืนเงิน ⇒ บอกในผลตอบ (หน้าเว็บถาม attach-preview ก่อนกด) ·
        // ฝ่ายค้าน S2: License พ้นวันหมดอายุแล้ว (ช่วงผ่อนผัน) ⇒ เตือนด้วย
        var paidWarning = AttachWarnings(sub, acct, DateTime.UtcNow);
        sub.AccountSubscriptionId = acct.Id;
        sub.UpdatedBy = userId.ToString();
        sub.UpdatedAt = DateTime.UtcNow;
        // รอบ 236 (ฝ่ายค้าน S1): รายการค่าแพ็กเกจระดับบริษัทที่ยังรอชำระ ⇒ ยกเลิก (บันทึกพร้อมการผูก)
        await _subSvc.CancelPendingPlanPaymentsForLicenseAsync(sub.Id, sub.CompanyId, userId.ToString());
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<string>(true, paidWarning,
            paidWarning == null ? "ผูกบริษัทเข้า Account Plan สำเร็จ" : $"ผูกบริษัทเข้า Account Plan สำเร็จ · ⚠️ {paidWarning}"));
    }

    /// <summary>รอบ 238 (236-9): ก่อนผูก — คำเตือน "เวลาที่จ่ายไว้ X วันยังเดินอยู่ ไม่คืนเงิน" (เซิร์ฟเวอร์คิด · หน้าเว็บแสดงในกล่องยืนยัน) · เฉพาะเจ้าของบริษัท</summary>
    [HttpGet("attach-preview/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> AttachPreview(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isOwner = await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == companyId && cu.Role == UserRole.Owner);
        var sub = isOwner ? await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted) : null;
        // ฝ่ายค้าน S3: ไม่มีบริษัท / ไม่มีสิทธิ์ ⇒ 404 เหมือนกัน (ไม่บอกว่ามีบริษัทนี้อยู่)
        if (sub == null) return NotFound(new ApiResponse<object>(false, null!, NotFoundText));
        var acct = await _db.AccountSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerUserId == userId && !a.IsDeleted
                && (a.Status == SubscriptionStatus.Trial || a.Status == SubscriptionStatus.Active || a.Status == SubscriptionStatus.PastDue));
        return Ok(new ApiResponse<object>(true, new { paidTimeWarning = AttachWarnings(sub, acct, DateTime.UtcNow) }));
    }

    /// <summary>คำเตือนตอนผูก (เซิร์ฟเวอร์คิด · ข้อความเดียวของ attach-preview/Attach): 236-9 เวลาที่จ่ายไว้เดินต่อ + ฝ่ายค้าน S2 License อยู่ในช่วงผ่อนผัน</summary>
    private static string? AttachWarnings(Subscription sub, AccountSubscription? acct, DateTime now)
    {
        var parts = new[]
        {
            PlanIdentityResolver.AttachPaidTimeWarning(sub.Kind, sub.IsPermanentFree, sub.Status, sub.EndDate, sub.LastPaymentDate, now),
            acct == null ? null : PlanIdentityResolver.AttachLicenseGraceWarning(acct.EndDate, acct.GracePeriodDays, now),
        }.Where(p => p != null).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>ฝ่ายค้าน S3: ข้อความ 404 เดียว (ไม่มีบริษัท = ไม่มีสิทธิ์ — ไม่บอกว่ามีบริษัทอยู่)</summary>
    private const string NotFoundText = "ไม่พบบริษัท หรือคุณไม่มีสิทธิ์ดูบริษัทนี้";

    /// <summary>ฝ่ายค้าน S3: ผู้ใช้เกี่ยวข้องกับบริษัทนี้ไหม — สมาชิกบริษัท (ทุกบทบาท) หรือผู้ถือ License ที่บริษัทผูกอยู่ · ไม่ใช่ ⇒ 404</summary>
    private async Task<bool> MayViewCompanyPlanAsync(Guid userId, Guid companyId)
        => await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == companyId)
           || await _db.Subscriptions.AnyAsync(s => s.CompanyId == companyId && !s.IsDeleted && s.AccountSubscriptionId != null
               && _db.AccountSubscriptions.Any(a => a.Id == s.AccountSubscriptionId && !a.IsDeleted && a.OwnerUserId == userId));

    /// <summary>สิทธิ์ถอด (รอบ 238 · 236-3 ข้อ 6): เจ้าของบริษัท หรือผู้ถือ License ที่บริษัทผูกอยู่ — กติกาเดียว <see cref="PlanIdentityResolver.MayDetach"/></summary>
    private async Task<(bool IsOwner, bool IsHolder)> DetachRightsAsync(Guid userId, Subscription sub)
    {
        var isOwner = await _db.CompanyUsers.AnyAsync(cu => cu.UserId == userId && cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner);
        var isHolder = sub.AccountSubscriptionId is Guid aid
            && await _db.AccountSubscriptions.AnyAsync(a => a.Id == aid && !a.IsDeleted && a.OwnerUserId == userId);
        return (isOwner, isHolder);
    }

    /// <summary>รอบ 238 (236-3 ข้อ 6): "หลังถอดจะใช้ {แพ็กเกจ} ถึง {วันที่} / ซื้อได้ทันที" — คิดจากแถวบริษัทผ่าน <see cref="PlanIdentityResolver.DetachPreview"/> ·
    /// เฉพาะผู้ที่ถอดได้ (เจ้าของบริษัท/ผู้ถือ License)</summary>
    [HttpGet("detach-preview/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DetachPreview(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted);
        var (isOwner, isHolder) = sub == null ? (false, false) : await DetachRightsAsync(userId, sub);
        // ฝ่ายค้าน S3: ไม่มีบริษัท / ไม่มีสิทธิ์ถอด ⇒ 404 เหมือนกัน
        if (sub == null || (!isOwner && !isHolder)) return NotFound(new ApiResponse<object>(false, null!, NotFoundText));
        var viaLicense = sub.AccountSubscriptionId != null;
        var p = await Accounting.Services.Platform.PlanIdentityReader.DetachPreviewAsync(_db, sub);
        return Ok(new ApiResponse<object>(true, new
        {
            viaLicense,
            canDetach = PlanIdentityResolver.MayDetach(viaLicense, isOwner, isHolder),
            label = p.Label,
            usableUntil = p.UsableUntilUtc,
            resumes = p.Resumes,
            canBuyNow = p.CanBuyNow,
            text = p.Text,
        }));
    }

    /// <summary>Detach the Company — falls back to the per-company Subscription
    /// row's own limits (which may be the original FreeTrial or an older paid
    /// plan that's still on file). Useful when selling a company or when a
    /// holding decides to split bills.
    /// <para>รอบ 238 (คำตัดสิน 236-3 ข้อ 6): เจ้าของบริษัท <b>หรือ</b>ผู้ถือ License ถอดได้ (<see cref="PlanIdentityResolver.MayDetach"/>) · บันทึก audit chain +
    /// ประวัติ · แจ้งอีกฝ่าย (ผู้ถือ ⇄ เจ้าของบริษัท) · ผลตอบบอกว่าหลังถอดใช้แพ็กเกจอะไร (ตัวเดียวกับ detach-preview)</para></summary>
    [HttpPost("detach")]
    [Accounting.Filters.RejectApiKey("ถอดบริษัทออกจาก Account Plan")]
    public async Task<ActionResult<ApiResponse<string>>> Detach([FromBody] AttachRequest req,
        [FromServices] Services.Interfaces.IEmailService? email = null)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == req.CompanyId && !s.IsDeleted);
        // ฝ่ายค้าน S3: ไม่มีบริษัท / ผู้ใช้ไม่เกี่ยวกับบริษัทนี้เลย ⇒ 404 (ไม่บอกว่ามีบริษัทอยู่) · สมาชิกที่ไม่ใช่เจ้าของ ⇒ 403 พร้อมเหตุ
        if (sub == null || !await MayViewCompanyPlanAsync(userId, req.CompanyId))
            return NotFound(new ApiResponse<string>(false, null, NotFoundText));
        var (isOwner, isHolder) = await DetachRightsAsync(userId, sub);
        if (!isOwner && !isHolder)
            return StatusCode(403, new ApiResponse<string>(false, null, PlanIdentityResolver.DetachForbiddenText));
        if (sub.AccountSubscriptionId is not Guid licenseId)
            return Ok(new ApiResponse<string>(true, null, "บริษัทนี้ไม่ได้ผูกกับ License — ใช้แพ็กเกจของตัวเองอยู่แล้ว"));

        var license = await _db.AccountSubscriptions.AsNoTracking().Include(a => a.Owner).Include(a => a.PlanTemplate)
            .FirstOrDefaultAsync(a => a.Id == licenseId);
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == sub.CompanyId).Select(c => c.Name).FirstOrDefaultAsync() ?? "";
        var preview = await Accounting.Services.Platform.PlanIdentityReader.DetachPreviewAsync(_db, sub);
        var now = DateTime.UtcNow;
        var by = isHolder && !isOwner ? "LicenseHolder" : "CompanyOwner";

        // ฝ่ายค้าน B2: แถวที่พักไว้ใต้ License อาจยัง Active/PastDue ทั้งที่เลยวันหมดอายุนานแล้ว ⇒ ตั้งสถานะปลายทางที่ตรงจริงจาก helper เดียวกับพรีวิว
        // (เลยวันหมดอายุ ⇒ Expired = อ่านอย่างเดียว/ซื้อได้ทันที) — ไม่ปล่อยให้งานหมดอายุรอบถัดไปเดิน Active→PastDue→Suspended (403 เงียบ ๆ)
        var fromStatus = sub.Status;
        var settled = PlanIdentityResolver.CompanyStatusAfterDetach(sub.Status, sub.EndDate, sub.IsPermanentFree, now);
        sub.AccountSubscriptionId = null;
        sub.UpdatedBy = userId.ToString();
        sub.UpdatedAt = now;
        _db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id,
            AccountSubscriptionId = licenseId,
            Action = "DetachedFromAccountPlan",
            Notes = $"ถอดบริษัทออกจาก License โดย{(by == "LicenseHolder" ? "ผู้ถือ License" : "เจ้าของบริษัท")} · {preview.Text}",
            PerformedBy = userId.ToString(),
        });
        if (settled != fromStatus)
        {
            sub.Status = settled;
            _db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = sub.Id,
                AccountSubscriptionId = licenseId,
                Action = "DetachStatusSettled",
                FromStatus = fromStatus,
                ToStatus = settled,
                Notes = $"ถอดออกจาก License — แพ็กเกจของบริษัทหมดอายุแล้วตั้งแต่ {sub.EndDate:yyyy-MM-dd} ⇒ {settled} (อ่านอย่างเดียว · ซื้อ/ต่ออายุได้ทันที)",
                PerformedBy = userId.ToString(),
            });
        }
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = sub.CompanyId,
            UserId = userId,
            UserEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            Action = AuditAction.Update,
            EntityType = nameof(Subscription),
            EntityId = sub.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { accountSubscriptionId = licenseId }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "DetachedFromLicense", ruleCode = "PLATFORM-LICENSE-DETACH", accountSubscriptionId = (Guid?)null,
                by, afterDetach = preview.Text, resumes = preview.Resumes,
                fromStatus = fromStatus.ToString(), status = settled.ToString(),
            }),
            IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            Timestamp = now,
        });
        await _db.SaveChangesAsync();

        // แจ้งอีกฝ่าย (ล้มไม่ย้อนการถอด — ถอดสำเร็จแล้ว · ไม่ส่งซ้ำถึงผู้ทำรายการเอง) · ฝ่ายค้าน N2: ผลการส่งติดไปกับผลตอบ (ล้มต้องเห็น)
        int notified = 0, notifyFailed = 0;
        if (email != null)
        {
            var recipients = new List<string>();
            if (by == "LicenseHolder")
                recipients.AddRange(await _db.CompanyUsers.AsNoTracking()
                    .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner && cu.UserId != userId)
                    .Select(cu => cu.User.Email).ToListAsync());
            else if (license?.Owner != null && license.OwnerUserId != userId)
                recipients.Add(license.Owner.Email);
            var planName = license?.PlanTemplate?.Name ?? "License";
            var subject = $"บริษัท {companyName} ถูกถอดออกจาก License {planName}";
            var body = "<p>" + System.Net.WebUtility.HtmlEncode(
                           $"บริษัท {companyName} ถูกถอดออกจาก License {planName} โดย{(by == "LicenseHolder" ? "ผู้ถือ License" : "เจ้าของบริษัท")}") + "</p>"
                       + "<p>" + System.Net.WebUtility.HtmlEncode(preview.Text) + "</p>";
            foreach (var to in recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool sent;
                try { sent = await email.TrySendAsync(to, subject, body); }
                catch { sent = false; }
                if (sent) notified++; else notifyFailed++;
            }
        }
        var notifyText = notified + notifyFailed == 0 ? ""
            : notifyFailed == 0 ? $" · แจ้ง{(by == "LicenseHolder" ? "เจ้าของบริษัท" : "ผู้ถือ License")}ทางอีเมลแล้ว {notified} ราย"
            : $" · ⚠️ ส่งอีเมลแจ้งอีกฝ่ายไม่สำเร็จ {notifyFailed} จาก {notified + notifyFailed} ราย — โปรดแจ้งเอง";
        return Ok(new ApiResponse<string>(true, preview.Text, $"ถอดบริษัทออกจาก License แล้ว — {preview.Text}{notifyText}"));
    }

    /// <summary>Effective plan resolution for a Company — useful for the UI to
    /// show which plan is currently driving the limits (account vs company).</summary>
    [HttpGet("effective/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Effective(Guid companyId, [FromServices] Services.Interfaces.ISubscriptionService subSvc)
    {
        // ฝ่ายค้าน S3 (IDOR): เดิมใครล็อกอินก็อ่านแพ็กเกจ/ผู้ถือ License ของบริษัทใดก็ได้ (เส้นนี้ไม่อยู่ใต้ /api/companies/{id} ⇒ ไม่ผ่านด่านสมาชิก) ·
        // ต้องเป็นสมาชิกบริษัทหรือผู้ถือ License ของบริษัทนั้น ไม่งั้น 404 (ตรวจก่อน GetEffectivePlanAsync ซึ่งอาจเขียนฐาน)
        if (!await MayViewCompanyPlanAsync(JwtHelper.GetUserIdFromClaims(User), companyId))
            return NotFound(new ApiResponse<object>(false, null!, NotFoundText));
        var eff = await subSvc.GetEffectivePlanAsync(companyId);
        if (eff == null) return Ok(new ApiResponse<object>(true, new { hasPlan = false }));
        // รอบ 236 (ฝ่ายค้าน ข้อ 4): ป้าย/ผู้ถือ/สถานะไทยจากตัวตัดสินเดียว — แบนเนอร์หน้า subscription ใช้คำว่า "License ของคุณ" เฉพาะผู้ถือ
        var identity = await Accounting.Services.Platform.PlanIdentityReader.ForCompanyAsync(_db, companyId);
        var viewer = Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var vid) ? vid : (Guid?)null;
        // รอบ 238 (236-3 ข้อ 6): วันสิ้นระยะผ่อนผันของ License (สูตรเดียว LicenseGraceEndsAt) + ผู้ดูถอดบริษัทเองได้ไหม (MayDetach) — หน้าเว็บแสดงอย่างเดียว
        var viewerIsOwner = viewer is Guid vo && await _db.CompanyUsers.AnyAsync(cu => cu.UserId == vo && cu.CompanyId == companyId && cu.Role == UserRole.Owner);
        var viewerIsHolder = identity != null && Accounting.Services.Platform.PlanIdentityReader.IsHolder(identity, viewer);
        DateTime? graceEndsAt = identity?.License is { } lic ? PlanIdentityResolver.LicenseGraceEndsAt(lic.EndDate, lic.GracePeriodDays) : null;
        return Ok(new ApiResponse<object>(true, new
        {
            hasPlan = true,
            graceEndsAt,
            lapseNote = identity == null ? null : PlanIdentityResolver.LicenseLapseNote(identity, DateTime.UtcNow),
            canSelfDetach = identity != null && PlanIdentityResolver.MayDetach(identity.ViaLicense, viewerIsOwner, viewerIsHolder),
            label = identity == null ? null : PlanIdentityResolver.Label(identity),
            statusLabel = identity == null ? null : PlanIdentityResolver.StatusLabel(identity.Status),
            licenseOwner = identity?.License?.OwnerName ?? identity?.License?.OwnerEmail,
            viewerIsLicenseHolder = identity != null && Accounting.Services.Platform.PlanIdentityReader.IsHolder(identity, viewer),
            source = eff.Source,
            isActive = eff.IsActive,
            inGrace = eff.InGrace,
            status = eff.Status.ToString(),
            endDate = eff.EndDate,
            maxUsers = eff.MaxUsers,
            maxCompanies = eff.MaxCompanies,
            maxDocumentsPerMonth = eff.MaxDocumentsPerMonth,
            maxJournalEntriesPerMonth = eff.MaxJournalEntriesPerMonth,
            maxStorageBytes = eff.MaxStorageBytes,
            maxOcrPagesPerMonth = eff.MaxOcrPagesPerMonth,
            azureOcrPagesPerMonth = eff.AzureOcrPagesPerMonth,
            localOcrPagesPerMonth = eff.LocalOcrPagesPerMonth,
            enabledFeatures = (long)eff.EnabledFeatures,
        }));
    }
}
