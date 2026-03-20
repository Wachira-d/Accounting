using Accounting.Data;
using Accounting.Models.DTOs;
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
        var plan = new CommissionPlan
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            CalculationBasis = request.CalculationBasis,
            CalculationMethod = request.CalculationMethod,
            FlatRate = request.FlatRate,
            IsActive = true
        };

        _db.CommissionPlans.Add(plan);

        if (request.Tiers != null)
        {
            foreach (var tier in request.Tiers)
            {
                var commissionTier = new CommissionTier
                {
                    CompanyId = companyId,
                    CommissionPlanId = plan.Id,
                    FromAmount = tier.FromAmount,
                    ToAmount = tier.ToAmount,
                    Rate = tier.Rate
                };
                _db.CommissionTiers.Add(commissionTier);
            }
        }

        await _db.SaveChangesAsync();

        var tiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == plan.Id)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        return MapPlanToResponse(plan, tiers);
    }

    public async Task<List<CommissionPlanResponse>> GetPlansAsync(Guid companyId)
    {
        var plans = await _db.CommissionPlans
            .Where(p => p.CompanyId == companyId)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var planIds = plans.Select(p => p.Id).ToList();
        var allTiers = await _db.CommissionTiers
            .Where(t => planIds.Contains(t.CommissionPlanId))
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        var tiersByPlan = allTiers.GroupBy(t => t.CommissionPlanId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return plans.Select(p => MapPlanToResponse(p,
            tiersByPlan.GetValueOrDefault(p.Id, new List<CommissionTier>()))).ToList();
    }

    public async Task<CommissionPlanResponse> UpdatePlanAsync(Guid companyId, Guid planId, UpdateCommissionPlanRequest request)
    {
        var plan = await _db.CommissionPlans
            .FirstOrDefaultAsync(p => p.Id == planId && p.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบแผนค่าคอมมิชชัน");

        if (request.Name != null) plan.Name = request.Name;
        if (request.Description != null) plan.Description = request.Description;
        if (request.FlatRate.HasValue) plan.FlatRate = request.FlatRate.Value;
        if (request.IsActive.HasValue) plan.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        var tiers = await _db.CommissionTiers
            .Where(t => t.CommissionPlanId == planId)
            .OrderBy(t => t.FromAmount)
            .ToListAsync();

        return MapPlanToResponse(plan, tiers);
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
                // Sum revenue from paid/partially-paid invoices
                var invoiceQuery = _db.Documents
                    .Where(d => d.CompanyId == companyId
                        && d.DocumentDate >= periodStart
                        && d.DocumentDate <= periodEnd
                        && (d.Status == DocumentStatus.Paid || d.Status == DocumentStatus.PartiallyPaid));

                if (assignment.EmployeeId.HasValue)
                    invoiceQuery = invoiceQuery.Where(d => d.CreatedBy == assignment.EmployeeId.ToString());

                basisAmount = await invoiceQuery.SumAsync(d => d.TotalAmount);
            }
            else if (assignment.Plan.CalculationBasis == "CollectedAmount")
            {
                // Sum actual payments received
                var paymentQuery = _db.Payments
                    .Where(p => p.CompanyId == companyId
                        && p.PaymentDate >= periodStart
                        && p.PaymentDate <= periodEnd);

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
                return Math.Round(totalCommission, 2);

            default:
                return 0;
        }
    }

    // ===== Mappers =====

    private static CommissionPlanResponse MapPlanToResponse(CommissionPlan plan, List<CommissionTier> tiers) => new(
        plan.Id, plan.Name, plan.Description,
        plan.CalculationBasis, plan.CalculationMethod, plan.FlatRate, plan.IsActive,
        tiers.Select(t => new CommissionTierResponse(t.FromAmount, t.ToAmount, t.Rate)).ToList());
}
