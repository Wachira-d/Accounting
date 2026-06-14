using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Cash Advance (เบิก-เคลียร์เงินสดล่วงหน้า). พนง.ขอเบิก → manager อนุมัติ
/// → finance จ่ายเงิน → พนง.กลับมา clear ด้วยใบเสร็จ. ระบบ track วงเงิน
/// คงค้าง + ส่ง reminder อัตโนมัติเมื่อใกล้ครบกำหนด clear.
///
/// Workflow + JE timing:
///   Requested  → no JE
///   Approved   → no JE (อนุมัติเฉยๆ)
///   Disbursed  → Dr ลูกหนี้พนง.-เงินยืม (115xx) / Cr Cash/Bank
///   Cleared    → Dr Expense (per receipts) + Dr Cash (refund if any)
///                / Cr ลูกหนี้พนง.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/cash-advances")]
[Authorize]
public class CashAdvanceController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public CashAdvanceController(AccountingDbContext db) { _db = db; }

    public sealed record CreateRequest(Guid EmployeeId, decimal RequestedAmount, string Purpose, DateTime? RequestDate);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create(
        Guid companyId, [FromBody] CreateRequest req)
    {
        if (req.RequestedAmount <= 0)
            return BadRequest(new ApiResponse<object>(false, null, "จำนวนเงินต้องมากกว่า 0"));
        if (string.IsNullOrWhiteSpace(req.Purpose))
            return BadRequest(new ApiResponse<object>(false, null, "ระบุวัตถุประสงค์การเบิก"));

        var empExists = await _db.Set<Employee>().AnyAsync(e => e.Id == req.EmployeeId && e.CompanyId == companyId);
        if (!empExists) return NotFound(new ApiResponse<object>(false, null, "ไม่พบพนักงาน"));

        // Auto running number CA-YYYYMM-NNNN
        var prefix = $"CA-{DateTime.UtcNow:yyyyMM}-";
        var seq = await _db.CashAdvanceRequests
            .Where(c => c.CompanyId == companyId && c.RequestNumber.StartsWith(prefix))
            .CountAsync() + 1;

        var ca = new CashAdvanceRequest
        {
            CompanyId = companyId,
            RequestNumber = $"{prefix}{seq:D4}",
            EmployeeId = req.EmployeeId,
            RequestedAmount = req.RequestedAmount,
            Purpose = req.Purpose.Trim(),
            RequestDate = req.RequestDate ?? DateTime.UtcNow,
            Status = CashAdvanceStatus.Requested
        };
        _db.CashAdvanceRequests.Add(ca);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { ca.Id, ca.RequestNumber, ca.Status }, "ส่งคำขอเบิกแล้ว"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List(
        Guid companyId,
        [FromQuery] CashAdvanceStatus? status,
        [FromQuery] Guid? employeeId)
    {
        var query = _db.CashAdvanceRequests
            .Include(c => c.Employee)
            .Where(c => c.CompanyId == companyId && !c.IsDeleted);
        if (status.HasValue) query = query.Where(c => c.Status == status.Value);
        if (employeeId.HasValue) query = query.Where(c => c.EmployeeId == employeeId.Value);

        var rows = await query
            .OrderByDescending(c => c.RequestDate)
            .Take(500)
            .Select(c => new
            {
                c.Id, c.RequestNumber, c.Status,
                Employee = new { c.Employee.Id, c.Employee.EmployeeCode, c.Employee.FirstNameTh, c.Employee.LastNameTh },
                c.RequestedAmount, c.ApprovedAmount, c.DisbursedAmount, c.ClearedAmount, c.RefundAmount,
                c.RequestDate, c.ApprovedAt, c.DisbursedAt, c.ClearanceDueDate, c.ClearedAt,
                c.Purpose,
                Overdue = c.Status == CashAdvanceStatus.Disbursed
                          && c.ClearanceDueDate.HasValue
                          && c.ClearanceDueDate.Value < DateTime.UtcNow
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, rows));
    }

    public sealed record ApproveRequest(decimal ApprovedAmount, string? Note);

    [HttpPost("{caId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<object>>> Approve(
        Guid companyId, Guid caId, [FromBody] ApproveRequest req)
    {
        var ca = await _db.CashAdvanceRequests.FirstOrDefaultAsync(c => c.Id == caId && c.CompanyId == companyId);
        if (ca == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำขอ"));
        if (ca.Status != CashAdvanceStatus.Requested)
            return BadRequest(new ApiResponse<object>(false, null, $"สถานะปัจจุบัน {ca.Status} อนุมัติไม่ได้"));
        if (req.ApprovedAmount <= 0 || req.ApprovedAmount > ca.RequestedAmount * 1.2m)
            return BadRequest(new ApiResponse<object>(false, null, "จำนวนอนุมัติไม่สมเหตุสมผล (สูงสุด +20% จากที่ขอ)"));

        ca.Status = CashAdvanceStatus.Approved;
        ca.ApprovedAmount = req.ApprovedAmount;
        ca.ApprovedAt = DateTime.UtcNow;
        ca.ApproverNote = req.Note;
        ca.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { ca.Id, ca.Status, ca.ApprovedAmount }, "อนุมัติแล้ว"));
    }

    [HttpPost("{caId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<object>>> Reject(
        Guid companyId, Guid caId, [FromQuery] string reason)
    {
        var ca = await _db.CashAdvanceRequests.FirstOrDefaultAsync(c => c.Id == caId && c.CompanyId == companyId);
        if (ca == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำขอ"));
        if (ca.Status != CashAdvanceStatus.Requested)
            return BadRequest(new ApiResponse<object>(false, null, "ปฏิเสธได้เฉพาะคำขอที่รออนุมัติ"));
        ca.Status = CashAdvanceStatus.Rejected;
        ca.RejectionReason = reason;
        ca.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "ปฏิเสธแล้ว"));
    }

    public sealed record DisburseRequest(DateTime DisburseDate, int ClearanceDays, Guid? BankAccountId);

    /// <summary>Disburse — บันทึก Disbursement + ตั้ง ClearanceDueDate. JE
    /// posting ให้ admin ทำเองผ่าน manual journal entry หรือ PaymentVoucher
    /// ตามสภาพการใช้งานจริง (เลือกบัญชี 115xx ลูกหนี้พนง.-เงินยืม).</summary>
    [HttpPost("{caId:guid}/disburse")]
    public async Task<ActionResult<ApiResponse<object>>> Disburse(
        Guid companyId, Guid caId, [FromBody] DisburseRequest req)
    {
        var ca = await _db.CashAdvanceRequests.FirstOrDefaultAsync(c => c.Id == caId && c.CompanyId == companyId);
        if (ca == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำขอ"));
        if (ca.Status != CashAdvanceStatus.Approved)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องอนุมัติก่อนถึงจะจ่ายได้"));

        var days = req.ClearanceDays > 0 ? req.ClearanceDays : 14;  // default 14 วัน
        ca.Status = CashAdvanceStatus.Disbursed;
        ca.DisbursedAmount = ca.ApprovedAmount ?? ca.RequestedAmount;
        ca.DisbursedAt = req.DisburseDate;
        ca.ClearanceDueDate = req.DisburseDate.AddDays(days);
        ca.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            ca.Id, ca.Status, ca.DisbursedAmount, ca.ClearanceDueDate
        }, $"จ่ายเงินสดแล้ว — ต้องเคลียร์ภายใน {ca.ClearanceDueDate:dd/MM/yyyy}"));
    }

    public sealed record ClearRequest(decimal ClearedAmount, decimal RefundAmount, List<Guid>? DocumentIds);

    /// <summary>Clear — พนง.ส่งใบเสร็จ + คืนเงินเหลือ (ถ้ามี). บันทึก link ไป
    /// expense docs ที่ใช้เคลียร์ (เก็บเป็น JSON array of Guid).</summary>
    [HttpPost("{caId:guid}/clear")]
    public async Task<ActionResult<ApiResponse<object>>> Clear(
        Guid companyId, Guid caId, [FromBody] ClearRequest req)
    {
        var ca = await _db.CashAdvanceRequests.FirstOrDefaultAsync(c => c.Id == caId && c.CompanyId == companyId);
        if (ca == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบคำขอ"));
        if (ca.Status != CashAdvanceStatus.Disbursed && ca.Status != CashAdvanceStatus.PendingClearance)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องจ่ายเงินสดก่อนถึงจะเคลียร์ได้"));

        var totalSettled = req.ClearedAmount + req.RefundAmount;
        if (Math.Abs(totalSettled - ca.DisbursedAmount) > 0.01m)
            return BadRequest(new ApiResponse<object>(false, null,
                $"ผลรวม ใช้จ่าย + คืนเงิน ({totalSettled:N2}) ต้องเท่ากับที่จ่ายไป ({ca.DisbursedAmount:N2})"));

        ca.ClearedAmount = req.ClearedAmount;
        ca.RefundAmount = req.RefundAmount;
        ca.ClearedAt = DateTime.UtcNow;
        ca.Status = req.RefundAmount > 0 ? CashAdvanceStatus.Refunded : CashAdvanceStatus.Cleared;
        if (req.DocumentIds != null && req.DocumentIds.Count > 0)
            ca.ClearanceDocumentIdsJson = System.Text.Json.JsonSerializer.Serialize(req.DocumentIds);
        ca.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { ca.Id, ca.Status, ca.ClearedAmount, ca.RefundAmount },
            "เคลียร์สำเร็จ"));
    }

    /// <summary>List คำขอที่เลยกำหนด clear — Finance/HR ใช้เพื่อเตือน
    /// พนง.ที่ยังไม่ส่งใบเสร็จกลับ.</summary>
    [HttpGet("overdue")]
    public async Task<ActionResult<ApiResponse<object>>> Overdue(Guid companyId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.CashAdvanceRequests
            .Include(c => c.Employee)
            .Where(c => c.CompanyId == companyId && !c.IsDeleted
                && c.Status == CashAdvanceStatus.Disbursed
                && c.ClearanceDueDate.HasValue && c.ClearanceDueDate.Value < now)
            .Select(c => new
            {
                c.Id, c.RequestNumber,
                EmployeeName = $"{c.Employee.FirstNameTh} {c.Employee.LastNameTh}",
                c.DisbursedAmount, c.DisbursedAt, c.ClearanceDueDate,
                OverdueDays = (int)(now - c.ClearanceDueDate!.Value).TotalDays
            })
            .OrderByDescending(c => c.OverdueDays)
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new { count = rows.Count, totalAmount = rows.Sum(r => r.DisbursedAmount), rows }));
    }
}
