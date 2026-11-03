using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Tax;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/stamp-duty")]
[Authorize]
public class StampDutyController : ControllerBase
{
    private readonly IStampDutyService _svc;
    private readonly IStatutoryRemittanceService _remit;

    public StampDutyController(IStampDutyService svc, IStatutoryRemittanceService remit) { _svc = svc; _remit = remit; }

    /// <summary>รอบ 228 — ลักษณะที่ระบบคิดอัตราได้ + ป้าย (เซิร์ฟเวอร์ส่ง หน้าไม่เขียนอัตราเอง)</summary>
    [HttpGet("schedules")]
    public ActionResult<ApiResponse<object>> Schedules()
        => Ok(new ApiResponse<object>(true, StampDutySchedule.KnownSchedules
            .Select(n => new { schedule = n, label = StampDutySchedule.Label(n) }).ToList()));

    [HttpGet("compute")]
    public ActionResult<ApiResponse<object>> Compute(Guid companyId,
        [FromQuery] int rdScheduleNumber, [FromQuery] decimal instrumentValue,
        [FromQuery] int? leaseYears)
    {
        var duty = _svc.Compute(rdScheduleNumber, instrumentValue, leaseYears);
        return Ok(new ApiResponse<object>(true, new
        {
            schedule = rdScheduleNumber,
            label = StampDutySchedule.Label(rdScheduleNumber),
            instrumentValue,
            leaseYears,
            duty,
            method = duty == 0 ? "Unknown" : "Computed",
        }));
    }

    public sealed record CreateRequest(string Reference, Guid? ContactId,
        int RdScheduleNumber, string InstrumentType, decimal InstrumentValue,
        DateTime InstrumentDate, int? LeaseYears, string PaymentMethod,
        Guid? ProjectId = null);

    [HttpPost]
    public async Task<ActionResult<ApiResponse<StampDutyRecord>>> Create(
        Guid companyId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        var rec = await _svc.CreateAsync(companyId, req.Reference, req.ContactId,
            req.RdScheduleNumber, req.InstrumentType, req.InstrumentValue,
            req.InstrumentDate, req.LeaseYears, req.PaymentMethod, req.ProjectId, ct);
        return Ok(new ApiResponse<StampDutyRecord>(true, rec,
            $"บันทึกอากรแสตมป์ {rec.DutyAmount:N0} บาท"));
    }

    /// <summary>รอบ 228 — บันทึกจ่าย: <c>PostJournal</c> = true (ค่าตั้งต้น) ⇒ เส้นนำส่ง (JE ค่าอากร + เงินเพิ่มอากร/ค่าปรับ §65 ตรี(6) · แถวนำส่ง · ยกเลิกได้) ·
    /// false ⇒ บันทึกเลขใบเสร็จอย่างเดียว (ลงค่าอากรด้วยเอกสารอื่นแล้ว — พฤติกรรมเดิม)</summary>
    public sealed record MarkPaidRequest(string RdReceiptNumber, DateTime PaidAt,
        bool PostJournal = true, Guid? BankAccountId = null, Guid? BankGlAccountId = null,
        decimal? DutyAmount = null, decimal? Surcharge = null, decimal? OtherFine = null, Guid? ExpenseAccountId = null);

    [HttpPost("{id:guid}/paid")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<object>>> MarkPaid(
        Guid companyId, Guid id, [FromBody] MarkPaidRequest req, CancellationToken ct)
    {
        try
        {
            if (!req.PostJournal)
            {
                var rec = await _svc.MarkPaidAsync(companyId, id, req.RdReceiptNumber, req.PaidAt, ct, JwtHelper.GetUserIdFromClaims(User).ToString());
                return Ok(new ApiResponse<object>(true, rec, "บันทึกเลขใบเสร็จอากรแสตมป์แล้ว (ไม่ลงบัญชี)"));
            }
            var res = await _remit.RemitStampDutyAsync(companyId, id, new StampDutyRemitRequest(
                req.PaidAt, req.RdReceiptNumber, req.BankAccountId, req.BankGlAccountId,
                req.DutyAmount, req.Surcharge, req.OtherFine, req.ExpenseAccountId), JwtHelper.GetUserIdFromClaims(User).ToString());   // N4 ผู้ทำ = user id (audit/JE)
            return Ok(new ApiResponse<object>(true, res, res.Message));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    /// <summary>รอบ 228 — ค้างชำระ + วันครบกำหนด + เงินเพิ่มอากรที่เสนอถ้าจ่ายวัน <paramref name="payDate"/> (ค่าตั้งต้น = วันนี้ตามเวลาไทย) ·
    /// เซิร์ฟเวอร์คิดทั้งหมด หน้าแสดง</summary>
    [HttpGet("unpaid")]
    public async Task<ActionResult<ApiResponse<object>>> Unpaid(
        Guid companyId, [FromQuery] DateTime? payDate, CancellationToken ct)
    {
        var rows = await _svc.ListUnpaidAsync(companyId, ct);
        var pay = (payDate ?? DateTime.UtcNow.AddHours(7)).Date;
        var items = rows.Select(r => Row(r, pay)).ToList();
        return Ok(new ApiResponse<object>(true, items, $"อากรค้างชำระ {items.Count} รายการ"));
    }

    /// <summary>รอบ 230 — รายการที่ "บันทึกเลขใบเสร็จอย่างเดียว" (มีวันจ่าย ไม่มี JE/แถวนำส่ง) ⇒ ปุ่ม "ลงบัญชี" บนหน้าเรียก <c>POST {id}/paid</c>
    /// (<c>PostJournal = true</c> ⇒ <c>RemitStampDutyAsync</c> เส้นเดียวกับรายการค้าง) · เงินเพิ่มอากรที่เสนอคิด ณ <paramref name="payDate"/> (ว่าง = วันจ่ายที่บันทึกไว้ ไม่ใช่วันนี้)</summary>
    [HttpGet("marked-only")]
    public async Task<ActionResult<ApiResponse<object>>> MarkedOnly(Guid companyId, [FromQuery] DateTime? payDate,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] bool includeConfirmed = false, CancellationToken ct = default)
    {
        // N4: แบ่งหน้า (เดิม Take(200) ตัดเงียบ) · ใบที่ยืนยันว่าลงบัญชีด้วยมือแล้วไม่แสดง
        var (rows, total) = await _svc.ListMarkedOnlyAsync(companyId, page, pageSize, includeConfirmed, ct);
        var items = rows.Select(r => Row(r, (payDate ?? r.PaidAt!.Value).Date)).ToList();
        return Ok(new ApiResponse<object>(true, new { items, total, page = Math.Max(1, page), pageSize = Math.Clamp(pageSize, 1, 200) },
            $"บันทึกเลขใบเสร็จแล้วแต่ยังไม่ลงบัญชีผ่านระบบ {total} รายการ"));
    }

    public sealed record ManualPostingRequest(bool Confirmed = true, string? Note = null);

    /// <summary>รอบ 230 N4 — "ลงบัญชีด้วยมือแล้ว — ซ่อน" (หรือยกเลิกการยืนยัน) · audit chain · ใบที่ยืนยันแล้วลงผ่านระบบไม่ได้ (กันค่าใช้จ่ายซ้ำ)</summary>
    [HttpPost("{id:guid}/manual-posting")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<object>>> ManualPosting(Guid companyId, Guid id, [FromBody] ManualPostingRequest req, CancellationToken ct)
    {
        var rec = await _svc.ConfirmManualPostingAsync(companyId, id, req.Confirmed, req.Note, JwtHelper.GetUserIdFromClaims(User).ToString(), ct);
        return Ok(new ApiResponse<object>(true, new { rec.Id, rec.ManualPostingConfirmedAt },
            req.Confirmed ? $"บันทึกว่าอากร {rec.Reference} ลงบัญชีด้วยมือแล้ว — ซ่อนจากรายการ" : $"ยกเลิกการยืนยันของอากร {rec.Reference} แล้ว"));
    }

    /// <summary>แถวที่หน้าแสดง — ชุดเดียวของรายการค้าง/บันทึกเลขใบเสร็จอย่างเดียว (เซิร์ฟเวอร์คิด หน้าแสดง)</summary>
    private static object Row(StampDutyRecord r, DateTime pay)
    {
        var due = StampDutySchedule.DueDate(r.InstrumentDate, r.PaymentMethod);
        var sug = StampDutySchedule.SuggestSurcharge(r.DutyAmount, due, pay);
        return new
        {
            r.Id, r.Reference, r.InstrumentType, r.InstrumentValue, r.DutyAmount, r.InstrumentDate,
            r.PaymentMethod, r.RdScheduleNumber,
            scheduleLabel = StampDutySchedule.Label(r.RdScheduleNumber),
            // ฝ่ายค้าน S9a: ค่าตั้งต้นช่อง "ลงบัญชี" (ติดแสตมป์ = ไม่ลง — มักลงค่าแสตมป์ไปแล้วตอนซื้อ) — เซิร์ฟเวอร์ตัดสิน หน้าแสดง
            defaultPostJournal = StampDutySchedule.DefaultPostJournal(r.PaymentMethod),
            paidMarkOnly = r.PaidAt != null,
            paidAt = r.PaidAt,
            manualPostingConfirmedAt = r.ManualPostingConfirmedAt,
            rdReceiptNumber = r.RdReceiptNumber,
            dueDate = due,
            isOverdue = pay > due,
            suggestedSurcharge = sug.Amount,
            surchargeNote = sug.Note,
        };
    }
}
