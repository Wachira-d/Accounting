using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Commission;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CommissionService : ICommissionService
{
    private readonly AccountingDbContext _db;

    public CommissionService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Plans =====

    public async Task<CommissionPlanResponse> CreatePlanAsync(Guid companyId, CreateCommissionPlanRequest request)
    {
        // ตัวตรวจตัวเดียวของสร้าง+แก้ไข (รอบ 193 · A04) — ผิดกติกา = ข้อความไทยก่อนแตะ DB
        var spec = CommissionPlanRules.Validate(request.Name, request.CalculationBasis,
            request.CalculationMethod, request.FlatRate, ToTierSpecs(request.Tiers));

        var plan = new CommissionPlan
        {
            CompanyId = companyId,
            Name = spec.Name,
            Description = request.Description,
            CalculationBasis = spec.CalculationBasis,
            CalculationMethod = spec.CalculationMethod,
            FlatRate = spec.FlatRate,
            // เดิมตั้ง true ตายตัว ทั้งที่สัญญารับ IsActive มา (default true) — ค่าที่ส่งมาต้องมีผล
            IsActive = request.IsActive
        };

        _db.CommissionPlans.Add(plan);
        AddTiers(companyId, plan.Id, spec.Tiers);

        await _db.SaveChangesAsync();

        var tiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == plan.Id && t.CompanyId == companyId)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        return MapPlanToResponse(plan, tiers, 0);
    }

    private static List<CommissionTierSpec>? ToTierSpecs(List<CommissionTierRequest>? tiers)
        => tiers?.Select(t => new CommissionTierSpec(t.FromAmount, t.ToAmount, t.Rate)).ToList();

    private void AddTiers(Guid companyId, Guid planId, IReadOnlyList<CommissionTierSpec> tiers)
    {
        foreach (var tier in tiers)
        {
            _db.CommissionTiers.Add(new CommissionTier
            {
                CompanyId = companyId,
                CommissionPlanId = planId,
                FromAmount = tier.FromAmount,
                ToAmount = tier.ToAmount,
                Rate = tier.Rate
            });
        }
    }

    /// <summary>จำนวนการกำหนดแผนที่ยังมีผล (ยังไม่สิ้นสุด) ต่อแผน</summary>
    private async Task<Dictionary<Guid, int>> CountActiveAssignmentsAsync(Guid companyId, IEnumerable<Guid> planIds)
    {
        var ids = planIds.ToList();
        var now = DateTime.UtcNow;
        return await _db.CommissionAssignments
            .Where(a => a.CompanyId == companyId && ids.Contains(a.CommissionPlanId)
                && (a.EndDate == null || a.EndDate >= now))
            .GroupBy(a => a.CommissionPlanId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<CommissionPlanResponse> GetPlanByIdAsync(Guid companyId, Guid planId)
    {
        var plan = await _db.CommissionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนค่าคอมมิชชัน");

        var tiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == planId && t.CompanyId == companyId)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        var counts = await CountActiveAssignmentsAsync(companyId, new[] { planId });
        return MapPlanToResponse(plan, tiers, counts.GetValueOrDefault(planId));
    }

    public async Task DeletePlanAsync(Guid companyId, Guid planId)
    {
        var plan = await _db.CommissionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนค่าคอมมิชชัน");

        var hasActiveAssignments = await _db.CommissionAssignments
            .AnyAsync(a => a.CommissionPlanId == planId && a.CompanyId == companyId
                && (a.EndDate == null || a.EndDate >= DateTime.UtcNow));

        if (hasActiveAssignments)
            throw new InvalidOperationException("ไม่สามารถลบแผนที่มีการกำหนดอยู่");

        _db.CommissionPlans.Remove(plan);
        await _db.SaveChangesAsync();
    }

    public async Task<List<CommissionPlanResponse>> GetPlansAsync(Guid companyId)
    {
        var plans = await _db.CommissionPlans
            .Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var planIds = plans.Select(p => p.Id).ToList();
        var allTiers = await _db.CommissionTiers
            .Where(t => t.CompanyId == companyId && planIds.Contains(t.CommissionPlanId))
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        var tiersByPlan = allTiers.GroupBy(t => t.CommissionPlanId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var counts = await CountActiveAssignmentsAsync(companyId, planIds);

        return plans.Select(p => MapPlanToResponse(p,
            tiersByPlan.GetValueOrDefault(p.Id, new List<CommissionTier>()),
            counts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<CommissionPlanResponse> UpdatePlanAsync(Guid companyId, Guid planId, UpdateCommissionPlanRequest request)
    {
        var plan = await _db.CommissionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนค่าคอมมิชชัน");

        var existingTiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == planId && t.CompanyId == companyId)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        // ผสาน "ที่ส่งมา" กับ "ค่าเดิม" แล้วตรวจทั้งก้อนด้วยตัวตรวจเดียวกับตอนสร้าง — แก้ประเภทได้จริง
        // (เดิมรับแค่ชื่อ/อัตรา/สถานะ ขณะที่ฟอร์มเปิดให้เปลี่ยนประเภท = silent no-op)
        var spec = CommissionPlanRules.Validate(
            request.Name ?? plan.Name,
            request.CalculationBasis ?? plan.CalculationBasis,
            request.CalculationMethod ?? plan.CalculationMethod,
            request.FlatRate ?? plan.FlatRate,
            ToTierSpecs(request.Tiers)
                ?? existingTiers.Select(t => new CommissionTierSpec(t.FromAmount, t.ToAmount, t.Rate)).ToList());

        plan.Name = spec.Name;
        if (request.Description != null) plan.Description = request.Description;
        plan.CalculationBasis = spec.CalculationBasis;
        plan.CalculationMethod = spec.CalculationMethod;
        plan.FlatRate = spec.FlatRate;
        if (request.IsActive.HasValue) plan.IsActive = request.IsActive.Value;

        // ขั้นชุดใหม่แทนที่ชุดเดิมทั้งชุด (รวมกรณีเปลี่ยนจากขั้นบันไดเป็นแบบอื่น ⇒ spec.Tiers ว่าง)
        var tiersChanged = existingTiers.Count != spec.Tiers.Count
            || existingTiers.Zip(spec.Tiers).Any(p => p.First.FromAmount != p.Second.FromAmount
                || p.First.ToAmount != p.Second.ToAmount || p.First.Rate != p.Second.Rate);
        if (tiersChanged)
        {
            _db.CommissionTiers.RemoveRange(existingTiers);
            AddTiers(companyId, planId, spec.Tiers);
        }

        await _db.SaveChangesAsync();

        var tiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == planId && t.CompanyId == companyId)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        var counts = await CountActiveAssignmentsAsync(companyId, new[] { planId });
        return MapPlanToResponse(plan, tiers, counts.GetValueOrDefault(planId));
    }

    // ===== Assignments =====

    public async Task AssignPlanAsync(Guid companyId, Guid planId, AssignCommissionRequest request)
    {
        var plan = await _db.CommissionPlans
            .AnyAsync(p => p.Id == planId && p.CompanyId == companyId);
        if (!plan)
            throw new KeyNotFoundException("ไม่พบแผนค่าคอมมิชชัน");

        var existingAssignment = await _db.CommissionAssignments
            .AnyAsync(a => a.CommissionPlanId == planId
                && a.EmployeeId == request.EmployeeId
                && a.UserId == request.UserId
                && a.CompanyId == companyId
                && (a.EndDate == null || a.EndDate >= request.StartDate));

        if (existingAssignment)
            throw new InvalidOperationException("มีการกำหนดแผนค่าคอมมิชชันให้พนักงานคนนี้อยู่แล้ว");

        var assignment = new CommissionAssignment
        {
            CompanyId = companyId,
            CommissionPlanId = planId,
            EmployeeId = request.EmployeeId,
            UserId = request.UserId,
            StartDate = request.StartDate,
            EndDate = request.EndDate
        };

        _db.CommissionAssignments.Add(assignment);
        await _db.SaveChangesAsync();
    }

    public async Task UnassignPlanAsync(Guid companyId, Guid assignmentId)
    {
        var assignment = await _db.CommissionAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบการกำหนดแผนค่าคอมมิชชัน");

        assignment.EndDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ===== Calculation =====

    public async Task<List<CommissionCalcResponse>> CalculateAsync(Guid companyId, int year, int month)
    {
        // Get all active assignments for the period
        var periodStart = new DateTime(year, month, 1);
        var periodEnd = periodStart.AddMonths(1).AddDays(-1);

        var assignments = await _db.CommissionAssignments
            .Include(a => a.Plan)
                .ThenInclude(p => p.Tiers)
            .Where(a => a.CompanyId == companyId
                && a.Plan.IsActive
                && a.StartDate <= periodEnd
                && (a.EndDate == null || a.EndDate >= periodStart))
            .ToListAsync();

        // Remove existing calculations for this period
        var existingCalcs = await _db.CommissionCalculations
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month
                && c.Status == "Calculated")
            .ToListAsync();
        _db.CommissionCalculations.RemoveRange(existingCalcs);

        var results = new List<CommissionCalcResponse>();

        foreach (var assignment in assignments)
        {
            // Calculate basis amount based on documents (invoices) for the period
            decimal basisAmount = 0;

            if (assignment.Plan.CalculationBasis == "Revenue")
            {
                // Sum revenue from paid/partially-paid invoices.
                // ต้องกรอง DocumentType ให้เป็น "เอกสารรับรู้รายได้" เท่านั้น
                // (ใบแจ้งหนี้/ใบกำกับ + ใบเสร็จขายสด standalone) — เดิมไม่กรอง type
                // เลย → รวมทุกเอกสาร (ใบเสร็จตัดชำระ, ใบซื้อ, ใบลดหนี้) ทำให้ฐาน
                // คอมมิชชันเบิ้ล/ผิด = จ่ายคอมเกินจริง.
                var revenueTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice,
                    DocumentType.Receipt, DocumentType.ReceiptVoucher };
                var invoiceQuery = _db.Documents
                    .Where(d => d.CompanyId == companyId
                        && d.DocumentDate >= periodStart
                        && d.DocumentDate <= periodEnd
                        && revenueTypes.Contains(d.DocumentType)
                        // ใบเสร็จตัดชำระใบแจ้งหนี้ (RelatedDocumentId) = ไม่ใช่รายได้ใหม่
                        && (d.DocumentType == DocumentType.Invoice || d.DocumentType == DocumentType.TaxInvoice
                            || d.RelatedDocumentId == null)
                        && (d.Status == DocumentStatus.Paid || d.Status == DocumentStatus.PartiallyPaid));

                if (assignment.EmployeeId.HasValue)
                    invoiceQuery = invoiceQuery.Where(d => d.CreatedBy == assignment.EmployeeId.ToString());

                basisAmount = await invoiceQuery.SumAsync(d => d.TotalAmount);
            }
            else if (assignment.Plan.CalculationBasis == "CollectedAmount")
            {
                // Sum actual payments received, filtered by employee if assigned
                var paymentQuery = _db.Payments
                    .Include(p => p.Document)
                    .Where(p => p.CompanyId == companyId
                        && p.PaymentDate >= periodStart
                        && p.PaymentDate <= periodEnd);

                if (assignment.EmployeeId.HasValue)
                    paymentQuery = paymentQuery.Where(p => p.Document != null && p.Document.CreatedBy == assignment.EmployeeId.ToString());

                basisAmount = await paymentQuery.SumAsync(p => p.Amount);
            }
            else if (assignment.Plan.CalculationBasis == "Profit")
            {
                // Sum revenue minus cost
                var revenue = await _db.Documents
                    .Where(d => d.CompanyId == companyId
                        && d.DocumentDate >= periodStart
                        && d.DocumentDate <= periodEnd
                        && d.DocumentType == DocumentType.Invoice
                        && d.Status == DocumentStatus.Paid)
                    .SumAsync(d => d.TotalAmount);

                var costs = await _db.Documents
                    .Where(d => d.CompanyId == companyId
                        && d.DocumentDate >= periodStart
                        && d.DocumentDate <= periodEnd
                        && d.DocumentType == DocumentType.PurchaseInvoice
                        && d.Status == DocumentStatus.Paid)
                    .SumAsync(d => d.TotalAmount);

                basisAmount = revenue - costs;
            }

            // Calculate commission amount
            decimal commissionAmount = CalculateCommissionAmount(assignment.Plan, basisAmount);

            // Get employee name
            string? employeeName = null;
            if (assignment.EmployeeId.HasValue)
            {
                var employee = await _db.Set<Employee>()
                    .FirstOrDefaultAsync(e => e.Id == assignment.EmployeeId.Value);
                employeeName = employee != null
                    ? $"{employee.FirstNameTh} {employee.LastNameTh}"
                    : null;
            }

            var calculation = new CommissionCalculation
            {
                CompanyId = companyId,
                CommissionPlanId = assignment.CommissionPlanId,
                EmployeeId = assignment.EmployeeId,
                UserId = assignment.UserId,
                Year = year,
                Month = month,
                BasisAmount = basisAmount,
                CommissionAmount = commissionAmount,
                Status = "Calculated"
            };

            _db.CommissionCalculations.Add(calculation);

            results.Add(new CommissionCalcResponse(
                assignment.EmployeeId, employeeName,
                year, month, basisAmount, commissionAmount, "Calculated"));
        }

        await _db.SaveChangesAsync();
        return results;
    }

    public async Task<List<CommissionCalcResponse>> GetCalculationsAsync(Guid companyId, int year, int month)
    {
        var calculations = await _db.CommissionCalculations
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month)
            .ToListAsync();

        var results = new List<CommissionCalcResponse>();
        foreach (var calc in calculations)
        {
            string? employeeName = null;
            if (calc.EmployeeId.HasValue)
            {
                var employee = await _db.Set<Employee>()
                    .FirstOrDefaultAsync(e => e.Id == calc.EmployeeId.Value);
                employeeName = employee != null
                    ? $"{employee.FirstNameTh} {employee.LastNameTh}"
                    : null;
            }

            results.Add(new CommissionCalcResponse(
                calc.EmployeeId, employeeName,
                calc.Year, calc.Month, calc.BasisAmount, calc.CommissionAmount, calc.Status));
        }

        return results;
    }

    public async Task ApproveCalculationsAsync(Guid companyId, int year, int month, string approvedBy)
    {
        var calculations = await _db.CommissionCalculations
            .Where(c => c.CompanyId == companyId && c.Year == year && c.Month == month && c.Status == "Calculated")
            .ToListAsync();

        if (!calculations.Any())
            throw new InvalidOperationException("ไม่พบรายการค่าคอมมิชชันที่รอการอนุมัติ");

        foreach (var calc in calculations)
        {
            calc.Status = "Approved";
        }

        await _db.SaveChangesAsync();
    }

    // ===== Helpers =====

    private static decimal CalculateCommissionAmount(CommissionPlan plan, decimal basisAmount)
    {
        if (basisAmount <= 0) return 0;

        switch (plan.CalculationMethod)
        {
            case "Percentage":
                return basisAmount * (plan.FlatRate ?? 0) / 100m;

            case "Fixed":
                return plan.FlatRate ?? 0;

            case "Tiered":
                decimal totalCommission = 0;
                var sortedTiers = plan.Tiers.OrderBy(t => t.FromAmount).ToList();

                foreach (var tier in sortedTiers)
                {
                    if (basisAmount <= tier.FromAmount)
                        break;

                    var tierMax = tier.ToAmount ?? decimal.MaxValue;
                    var applicableAmount = Math.Min(basisAmount, tierMax) - tier.FromAmount;

                    if (applicableAmount > 0)
                        totalCommission += applicableAmount * tier.Rate / 100m;
                }
                return Math.Round(totalCommission, 2, MidpointRounding.AwayFromZero);

            default:
                return 0;
        }
    }

    // ===== Mappers =====

    private static CommissionPlanResponse MapPlanToResponse(CommissionPlan plan, List<CommissionTier> tiers, int assignedCount) => new(
        plan.Id, plan.Name, plan.Description,
        plan.CalculationBasis, plan.CalculationMethod, plan.FlatRate, plan.IsActive,
        tiers.Select(t => new CommissionTierResponse(t.FromAmount, t.ToAmount, t.Rate)).ToList(),
        assignedCount);
}
