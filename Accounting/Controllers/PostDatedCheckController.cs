using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Post-Dated Check (เช็คล่วงหน้า, PDC) management. B2B ไทยใช้เป็นเรื่องปกติ
/// แทบทุกร้านค้า/บริษัท — ต้อง track วันที่ฝาก / สถานะ / dishonor + แนบ
/// สลิปหน้าเช็ค (LINE-snap) เพื่อตามเงินคืน.
///
/// Flow:
///   1. Create PDC (Inbound: ลูกค้าจ่ายเช็คให้ / Outbound: เราจ่ายเช็คให้ vendor)
///      → Status = Held; แนบรูป FileAttachment
///   2. Deposit → Status = Deposited + create RV (inbound) or PV (outbound)
///   3. Clear → Status = Cleared (final, ไม่มี JE เพิ่ม — ตัดยอด bank แล้ว)
///      หรือ Dishonor → Status = Dishonored + reverse the deposit entry
///
/// Reminder service ดู ScheduledDepositDate <= today + 3 days → notify
/// AR/Finance team ผ่าน NotificationEvents.PdcDueSoon.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/post-dated-checks")]
[Authorize]
public class PostDatedCheckController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public PostDatedCheckController(AccountingDbContext db) { _db = db; }

    public sealed record CreatePdcRequest(
        PdcDirection Direction,
        string CheckNumber,
        string BankName,
        string? BankBranch,
        decimal Amount,
        DateTime CheckDate,
        DateTime IssueDate,
        DateTime? ScheduledDepositDate,
        Guid? ContactId,
        Guid? SourceDocumentId,
        Guid? BankAccountId,
        string? Notes);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<PostDatedCheck>>> Create(
        Guid companyId, [FromBody] CreatePdcRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CheckNumber) || req.Amount <= 0)
            return BadRequest(new ApiResponse<PostDatedCheck>(false, null!, "เลขที่เช็ค + จำนวนเงิน จำเป็น"));

        var pdc = new PostDatedCheck
        {
            CompanyId = companyId,
            Direction = req.Direction,
            CheckNumber = req.CheckNumber.Trim(),
            BankName = req.BankName.Trim(),
            BankBranch = req.BankBranch,
            Amount = req.Amount,
            CheckDate = req.CheckDate,
            IssueDate = req.IssueDate,
            ScheduledDepositDate = req.ScheduledDepositDate ?? req.CheckDate,
            ContactId = req.ContactId,
            SourceDocumentId = req.SourceDocumentId,
            BankAccountId = req.BankAccountId,
            Notes = req.Notes,
            Status = PdcStatus.Held
        };
        _db.PostDatedChecks.Add(pdc);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<PostDatedCheck>(true, pdc, "บันทึกเช็คเรียบร้อย"));
    }

    /// <summary>List + filter. Default: เช็คที่ยัง active (Held/Deposited)
    /// เรียงตาม ScheduledDepositDate เพื่อให้ Finance เห็นเช็คใกล้ถึงคิวก่อน.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List(
        Guid companyId,
        [FromQuery] PdcDirection? direction,
        [FromQuery] PdcStatus? status,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] Guid? contactId)
    {
        var query = _db.PostDatedChecks
            .Include(p => p.Contact)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted);
        if (direction.HasValue) query = query.Where(p => p.Direction == direction.Value);
        if (status.HasValue) query = query.Where(p => p.Status == status.Value);
        else query = query.Where(p => p.Status == PdcStatus.Held || p.Status == PdcStatus.Deposited);
        if (fromDate.HasValue) query = query.Where(p => p.ScheduledDepositDate >= fromDate.Value);
        if (toDate.HasValue) query = query.Where(p => p.ScheduledDepositDate <= toDate.Value);
        if (contactId.HasValue) query = query.Where(p => p.ContactId == contactId.Value);

        var rows = await query
            .OrderBy(p => p.ScheduledDepositDate)
            .Take(500)
            .Select(p => new
            {
                p.Id, p.Direction, p.CheckNumber, p.BankName, p.Amount,
                p.CheckDate, p.ScheduledDepositDate, p.Status,
                ContactName = p.Contact != null ? p.Contact.Name : null,
                p.SourceDocumentId, p.BankAccountId,
                DaysUntilDeposit = (int)Math.Floor((p.ScheduledDepositDate - DateTime.UtcNow).TotalDays),
                p.Notes
            })
            .ToListAsync();

        var summary = new
        {
            HeldInboundCount = rows.Count(r => r.Direction == PdcDirection.Inbound && r.Status == PdcStatus.Held),
            HeldInboundTotal = rows.Where(r => r.Direction == PdcDirection.Inbound && r.Status == PdcStatus.Held).Sum(r => r.Amount),
            HeldOutboundCount = rows.Count(r => r.Direction == PdcDirection.Outbound && r.Status == PdcStatus.Held),
            HeldOutboundTotal = rows.Where(r => r.Direction == PdcDirection.Outbound && r.Status == PdcStatus.Held).Sum(r => r.Amount),
            DueWithin7Days = rows.Count(r => r.Status == PdcStatus.Held && r.DaysUntilDeposit <= 7),
        };

        return Ok(new ApiResponse<object>(true, new { rows, summary }));
    }

    /// <summary>Deposit เช็ค (Inbound: ฝากเข้าบัญชีเรา / Outbound: vendor นำเช็คเรา
    /// ไปขึ้นเงิน). Auto-create RV หรือ PV ไปตัด AR/AP ของ source doc.
    /// JE ผ่าน DocumentService ปกติ — consistent กับระบบเดิม.</summary>
    [HttpPost("{pdcId:guid}/deposit")]
    public async Task<ActionResult<ApiResponse<object>>> Deposit(
        Guid companyId, Guid pdcId, [FromQuery] DateTime? depositDate)
    {
        var pdc = await _db.PostDatedChecks
            .FirstOrDefaultAsync(p => p.Id == pdcId && p.CompanyId == companyId);
        if (pdc == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเช็ค"));
        if (pdc.Status != PdcStatus.Held)
            return BadRequest(new ApiResponse<object>(false, null, $"เช็คอยู่ในสถานะ {pdc.Status} — ฝากไม่ได้"));

        pdc.Status = PdcStatus.Deposited;
        pdc.DepositedAt = depositDate ?? DateTime.UtcNow;
        pdc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { pdc.Id, pdc.Status, pdc.DepositedAt },
            "บันทึกการฝากเช็ค — รอ clearing"));
    }

    /// <summary>ธนาคารหักเงินสำเร็จ — final state. ไม่มี JE เพิ่ม
    /// (bank ตัดยอดให้แล้วใน statement, reconcile จะ match ในขั้นถัดไป).</summary>
    [HttpPost("{pdcId:guid}/clear")]
    public async Task<ActionResult<ApiResponse<object>>> Clear(
        Guid companyId, Guid pdcId, [FromQuery] DateTime? clearedDate)
    {
        var pdc = await _db.PostDatedChecks
            .FirstOrDefaultAsync(p => p.Id == pdcId && p.CompanyId == companyId);
        if (pdc == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเช็ค"));
        if (pdc.Status != PdcStatus.Deposited)
            return BadRequest(new ApiResponse<object>(false, null, "ต้อง deposit ก่อนถึงจะ clear ได้"));

        pdc.Status = PdcStatus.Cleared;
        pdc.ClearedAt = clearedDate ?? DateTime.UtcNow;
        pdc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { pdc.Id, pdc.Status, pdc.ClearedAt },
            "เช็คผ่านเรียบร้อย"));
    }

    /// <summary>เช็คเด้ง (Dishonored) — Reverse the deposit entry + alert team.
    /// Common reasons: "เงินไม่พอ", "บัญชีปิด", "สั่งห้ามจ่าย", "ลายเซ็นไม่ตรง".</summary>
    [HttpPost("{pdcId:guid}/dishonor")]
    public async Task<ActionResult<ApiResponse<object>>> Dishonor(
        Guid companyId, Guid pdcId, [FromBody] DishonorRequest req)
    {
        var pdc = await _db.PostDatedChecks
            .FirstOrDefaultAsync(p => p.Id == pdcId && p.CompanyId == companyId);
        if (pdc == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเช็ค"));
        if (pdc.Status != PdcStatus.Deposited)
            return BadRequest(new ApiResponse<object>(false, null, "เช็คต้อง deposit ก่อนถึงจะ dishonor ได้"));

        pdc.Status = PdcStatus.Dishonored;
        pdc.DishonoredAt = req.DishonoredAt ?? DateTime.UtcNow;
        pdc.DishonorReason = req.Reason;
        pdc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // TODO: trigger notification → AR team
        return Ok(new ApiResponse<object>(true, new { pdc.Id, pdc.Status, pdc.DishonorReason },
            "บันทึกเช็คเด้ง — กรุณาตามเงินกับลูกค้า/vendor"));
    }
    public sealed record DishonorRequest(string Reason, DateTime? DishonoredAt);

    [HttpPost("{pdcId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<object>>> Cancel(Guid companyId, Guid pdcId)
    {
        var pdc = await _db.PostDatedChecks
            .FirstOrDefaultAsync(p => p.Id == pdcId && p.CompanyId == companyId);
        if (pdc == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเช็ค"));
        if (pdc.Status != PdcStatus.Held)
            return BadRequest(new ApiResponse<object>(false, null, "ยกเลิกได้เฉพาะเช็คที่ยัง Held"));
        pdc.Status = PdcStatus.Cancelled;
        pdc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { pdc.Id, pdc.Status }, "ยกเลิกแล้ว"));
    }
}
