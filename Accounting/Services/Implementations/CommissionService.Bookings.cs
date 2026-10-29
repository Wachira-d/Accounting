using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs.Commission;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P5 (R-209-C6 · R-209-D8 · คำตัดสินข้อ 168 ฏ) — คอมมิชชัน/ค่าตอบแทนจากการจอง CMS
/// <para>ฐานใหม่สองตัว (<see cref="CommissionPlanRules"/>): <b>BookingServices</b> = มูลค่าการจองที่เสร็จ<b>นอก POS</b> × % งาน (ดีดซ้ำกับฐาน "ยอดที่ช่างทำบน POS"
/// ด้วย <c>PosOrder.SourceBookingId</c>) · <b>InstructorPay</b> = ค่าตอบแทนตามกติกาต่อผู้ให้บริการ (<see cref="InstructorPayRule"/>) · ผู้รับ = ผู้ให้บริการที่
/// <c>EmployeeId</c> ตรงพนักงาน หรือ <c>StaffUserId</c> ตรงผู้ใช้ของแผน · มูลค่าต่อการจองผ่าน <see cref="BookingServiceValue"/> ตัวเดียว (บัตรคอร์ส = รายได้ที่รับรู้)</para>
/// </summary>
public partial class CommissionService
{
    private async Task<decimal> CompanyVatRateAsync(Guid companyId)
        => await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.IsVatRegistered).FirstOrDefaultAsync() ? 7m : 0m;

    /// <summary>ฐานคอมมิชชันจากการจองของผู้รับในแผน — คืน (ฐาน, หมายเหตุเมื่อฐาน 0 เพราะตั้งค่าไม่ครบ · ไม่เงียบ)</summary>
    private async Task<(decimal Basis, string? Note)> BookingBasisAsync(Guid companyId, CommissionAssignment assignment, DateTime periodStart, string basis)
    {
        var userId = assignment.UserId;
        if (userId == null && assignment.EmployeeId.HasValue)
            userId = await _db.Set<Employee>().AsNoTracking()
                .Where(e => e.Id == assignment.EmployeeId.Value && e.CompanyId == companyId).Select(e => e.UserId).FirstOrDefaultAsync();
        var empId = assignment.EmployeeId;
        var resources = await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted
                && ((empId != null && r.EmployeeId == empId) || (userId != null && r.StaffUserId == userId)))
            .Select(r => new { r.Id, r.Name, r.EmployeeId, r.PayMode, r.PayRate, r.PayMinimumPerClass })
            .ToListAsync();
        if (resources.Count == 0)
            return (0m, "ฐาน 0 เพราะยังไม่มีผู้ให้บริการ (ระบบจอง) ที่ผูกพนักงาน/ผู้ใช้นี้ — ผูกที่หน้า \"สรุปงานผู้ให้บริการ\" → ค่าตอบแทน แล้วคำนวณใหม่");
        var vat = await CompanyVatRateAsync(companyId);
        var work = await BookingWorkLoader.LoadAsync(_db, companyId, resources.Select(r => r.Id).ToList(), periodStart, periodStart.AddMonths(1), null, vat);
        if (basis == CommissionPlanRules.BookingServicesBasis)
            return (BookingServiceValue.BookingSalesBasis(work.Select(w => w.Work)), null);
        decimal total = 0m;
        foreach (var r in resources)
            total += InstructorPayRule.Compute(r.Id, r.Name, r.EmployeeId, new InstructorPaySettings(r.PayMode, r.PayRate, r.PayMinimumPerClass),
                work.Where(w => w.ResourceId == r.Id).Select(w => w.Work).ToList()).Pay;
        return (total, resources.All(r => r.PayMode == InstructorPayMode.None)
            ? "ฐาน 0 เพราะผู้ให้บริการที่ผูกยังไม่ตั้งกติกาค่าตอบแทน (ต่อคลาส/ต่อหัว/%) — ตั้งที่หน้า \"สรุปงานผู้ให้บริการ\" → ค่าตอบแทน"
            : null);
    }

    // ===================================================================== ค่าตั้งค่าตอบแทนต่อผู้ให้บริการ

    public async Task<ResourcePaySettingsResponse> GetResourcePayAsync(Guid companyId, Guid siteId)
    {
        var rows = await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.IsActive, r.StaffUserId, r.EmployeeId, r.PayMode, r.PayRate, r.PayMinimumPerClass })
            .ToListAsync();
        var emps = await _db.Set<Employee>().AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.IsActive)
            .OrderBy(e => e.FirstNameTh).Take(500)
            .Select(e => new { e.Id, e.NickName, e.FirstNameTh, e.LastNameTh }).ToListAsync();
        var names = emps.ToDictionary(e => e.Id, e => EmployeeLabel(e.NickName, e.FirstNameTh, e.LastNameTh));
        var missing = rows.Where(r => r.EmployeeId != null && !names.ContainsKey(r.EmployeeId.Value)).Select(r => r.EmployeeId!.Value).ToList();
        if (missing.Count > 0)
            foreach (var e in await _db.Set<Employee>().AsNoTracking().IgnoreQueryFilters()
                         .Where(e => e.CompanyId == companyId && missing.Contains(e.Id))
                         .Select(e => new { e.Id, e.NickName, e.FirstNameTh, e.LastNameTh }).ToListAsync())
                names[e.Id] = EmployeeLabel(e.NickName, e.FirstNameTh, e.LastNameTh) + " (ไม่ใช้งาน)";
        return new ResourcePaySettingsResponse(
            rows.Select(r => new ResourcePayResponse(r.Id, r.Name, r.IsActive, r.StaffUserId, r.EmployeeId,
                r.EmployeeId is Guid eid && names.TryGetValue(eid, out var n) ? n : null, r.PayMode, r.PayRate, r.PayMinimumPerClass)).ToList(),
            emps.Select(e => new PayEmployeeOption(e.Id, EmployeeLabel(e.NickName, e.FirstNameTh, e.LastNameTh))).ToList());
    }

    public async Task<ResourcePayResponse> SaveResourcePayAsync(Guid companyId, Guid siteId, Guid resourceId, SaveResourcePayRequest request, string actor)
    {
        if (InstructorPayRule.SettingsProblem(request.Mode, request.Rate, request.MinimumPerClass) is string problem)
            throw new BusinessRuleException(problem, InstructorPayRule.RuleCode);
        if (request.EmployeeId is Guid eid && !await _db.Set<Employee>().AsNoTracking().AnyAsync(e => e.Id == eid && e.CompanyId == companyId))
            throw new BusinessRuleException("ไม่พบพนักงานที่เลือกในบริษัทนี้", InstructorPayRule.RuleCode);
        var res = await _db.SiteBookingResources
            .FirstOrDefaultAsync(r => r.Id == resourceId && r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบผู้ให้บริการ");
        var before = new { res.EmployeeId, res.PayMode, res.PayRate, res.PayMinimumPerClass };
        res.EmployeeId = request.EmployeeId;
        res.PayMode = request.Mode;
        res.PayRate = Math.Round(request.Rate, 2, MidpointRounding.AwayFromZero);
        res.PayMinimumPerClass = request.Mode == InstructorPayMode.PerHead ? Math.Round(request.MinimumPerClass, 2, MidpointRounding.AwayFromZero) : 0m;
        res.UpdatedAt = DateTime.UtcNow; res.UpdatedBy = actor;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = "SiteBookingResource", EntityId = res.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "SaveInstructorPay", before, after = new { res.EmployeeId, res.PayMode, res.PayRate, res.PayMinimumPerClass },
                ruleCode = InstructorPayRule.RuleCode,
            }),
        });
        await _db.SaveChangesAsync();
        return (await GetResourcePayAsync(companyId, siteId)).Resources.First(r => r.ResourceId == resourceId);
    }

    /// <summary>ค่าตอบแทนผู้ให้บริการของไซต์ในช่วงวันที่ (รวมหัวท้าย) — ตัวคิดเดียวกับฐานคอมมิชชัน InstructorPay</summary>
    public async Task<List<InstructorPayLine>> GetInstructorPayAsync(Guid companyId, Guid siteId, DateTime from, DateTime to)
    {
        var f = from.Date; var t = to.Date;
        if (t < f) throw new BusinessRuleException("วันที่สิ้นสุดต้องไม่ก่อนวันที่เริ่ม", InstructorPayRule.RuleCode);
        if ((t - f).TotalDays >= 366) throw new BusinessRuleException("ช่วงรายงานได้ไม่เกิน 366 วัน", InstructorPayRule.RuleCode);
        var resources = await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.EmployeeId, r.PayMode, r.PayRate, r.PayMinimumPerClass })
            .ToListAsync();
        var vat = await CompanyVatRateAsync(companyId);
        var work = await BookingWorkLoader.LoadAsync(_db, companyId, resources.Select(r => r.Id).ToList(), f, t.AddDays(1), siteId, vat);
        return resources.Select(r => InstructorPayRule.Compute(r.Id, r.Name, r.EmployeeId, new InstructorPaySettings(r.PayMode, r.PayRate, r.PayMinimumPerClass),
            work.Where(w => w.ResourceId == r.Id).Select(w => w.Work).ToList())).ToList();
    }

    private static string EmployeeLabel(string? nick, string first, string last)
        => string.IsNullOrWhiteSpace(nick) ? $"{first} {last}".Trim() : $"{nick} ({first})";
}
