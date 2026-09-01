using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Payroll;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/payroll")]
[Authorize]
public class PayrollController : ControllerBase
{
    private readonly IPayrollService _service;
    private readonly ISensitivityService _sensitivity;
    private readonly IPermissionService _permissions;
    private readonly IPayslipLineDeliveryService _payslipLine;
    private readonly Services.Implementations.Pdpa.IPdpaService _pdpa;
    public PayrollController(IPayrollService service, ISensitivityService sensitivity,
        IPermissionService permissions, IPayslipLineDeliveryService payslipLine,
        Services.Implementations.Pdpa.IPdpaService pdpa)
    {
        _service = service;
        _sensitivity = sensitivity;
        _permissions = permissions;
        _payslipLine = payslipLine;
        _pdpa = pdpa;
    }

    private string? ActorEmail() => User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

    /// <summary>PDPA ม.26 — เปิดดู PII (CitizenId/Phone/Email) แบบ raw เฉพาะ
    /// user ที่มี permission Pii.View. คนอื่น ๆ ได้ค่า mask ตาม PiiMask helper.</summary>
    private async Task<bool> CanViewPiiAsync(Guid companyId, Guid? subjectEmployeeId = null)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var can = await _permissions.HasPermissionAsync(companyId, userId,
            Models.Constants.PermissionKeys.PiiView);
        // ม.37(4) — เปิดดูแบบ raw ต้องมีร่องรอยว่าใครดูของใครเมื่อไร
        //
        // ⚠️ doc-comment ของ PermissionKeys.PiiView ระบุข้อนี้ไว้ตั้งแต่ต้นและ
        // PdpaService.LogPiiAccessAsync ก็เขียนไว้ครบ — แต่**ไม่มี call site เลย
        // ทั้งเรพ** ⇒ ตารางว่างเปล่าตลอด = ไม่มี control จริง
        // (doc-comment ที่บอกว่า "ป้องกันแล้วที่ X" เป็นเจตนา ไม่ใช่หลักฐาน)
        //
        // log เฉพาะตอน "ได้ดูจริง" — คนที่ถูก mask ไม่ได้เข้าถึง PII จึงไม่ต้องบันทึก
        if (can)
        {
            await _pdpa.LogPiiAccessAsync(companyId, userId,
                User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
                subjectType: "Employee", subjectId: subjectEmployeeId ?? Guid.Empty,
                fieldName: subjectEmployeeId.HasValue ? "*" : "* (รายการพนักงาน)",
                operation: "Read", purpose: "ดูข้อมูลพนักงานแบบไม่ปกปิด (HR/Payroll)",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());
        }
        return can;
    }

    /// <summary>เงินเดือน (BaseSalary) = ข้อมูลอ่อนไหว payroll — คืน true ถ้า user
    /// มีสิทธิ์ดู. ใช้กับ read ของพนักงานเพื่อ mask ฐานเงินเดือน (แต่ยังคืน row
    /// ให้ dropdown/org chart ได้), ต่างจาก CheckPayrollAccessAsync ที่บล็อกทั้ง endpoint.</summary>
    private async Task<bool> CanViewPayrollAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return await _sensitivity.CanViewAsync(companyId, userId, Models.Enums.SensitivityKind.Payroll);
    }

    /// <summary>Gate every payroll endpoint behind the Payroll sensitivity rule.
    /// Returns 403 with a structured body so integrations distinguish "no access"
    /// from "no such record". Owner / SystemAdmin pass through.</summary>
    private async Task<ActionResult?> CheckPayrollAccessAsync(Guid companyId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _sensitivity.CanViewAsync(companyId, userId, Models.Enums.SensitivityKind.Payroll))
            return StatusCode(403, new ApiResponse<object>(false, new
            {
                redacted = true,
                kind = "Payroll",
                requiredPermission = Models.Constants.PermissionKeys.PayrollView,
            }, "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน"));
        return null;
    }

    // Employees
    [HttpPost("employees")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> CreateEmployee(Guid companyId, [FromBody] CreateEmployeeRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return StatusCode(201, new ApiResponse<EmployeeResponse>(true, await _service.CreateEmployeeAsync(companyId, request)));
    }

    [HttpGet("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> GetEmployee(Guid companyId, Guid employeeId)
        => Ok(new ApiResponse<EmployeeResponse>(true,
            await _service.GetEmployeeAsync(companyId, employeeId, await CanViewPiiAsync(companyId, employeeId),
                await CanViewPayrollAsync(companyId))));

    [HttpGet("employees")]
    public async Task<ActionResult<ApiResponse<PagedResponse<EmployeeResponse>>>> GetEmployees(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null)
        => Ok(new ApiResponse<PagedResponse<EmployeeResponse>>(true,
            await _service.GetEmployeesAsync(companyId, new PagedRequest(page, pageSize, search),
                await CanViewPiiAsync(companyId), await CanViewPayrollAsync(companyId))));

    /// <summary>Lookup-by-external for partner ERPs/HRIS — caller passes
    /// its own (ExternalSystem, ExternalId) and gets back our employee
    /// row without needing to remember our GUID.</summary>
    [HttpGet("employees/by-external/{externalSystem}/{externalId}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> GetByExternal(
        Guid companyId, string externalSystem, string externalId)
    {
        var emp = await _service.GetEmployeeByExternalAsync(companyId, externalSystem, externalId);
        return emp == null
            ? NotFound(new ApiResponse<object>(false, null, "ไม่พบพนักงาน"))
            : Ok(new ApiResponse<EmployeeResponse>(true, emp));
    }

    [HttpDelete("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteEmployee(Guid companyId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try { await _service.DeleteEmployeeAsync(companyId, employeeId); return Ok(new ApiResponse<bool>(true, true, "ลบพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("employees/{employeeId:guid}/restore")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> RestoreEmployee(Guid companyId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try { return Ok(new ApiResponse<EmployeeResponse>(true, await _service.RestoreEmployeeAsync(companyId, employeeId), "กู้คืนพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPut("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> UpdateEmployee(Guid companyId, Guid employeeId, [FromBody] UpdateEmployeeRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<EmployeeResponse>(true, await _service.UpdateEmployeeAsync(companyId, employeeId, request)));
    }

    [HttpPost("employees/sync")]
    public async Task<ActionResult<ApiResponse<SyncEmployeesResponse>>> SyncEmployees(
        Guid companyId, [FromBody] SyncEmployeesRequest request)
    {
        var r = await _service.SyncEmployeesAsync(companyId, request);
        return Ok(new ApiResponse<SyncEmployeesResponse>(true, r,
            $"sync เสร็จ — เพิ่ม {r.Inserted} · อัปเดต {r.Updated} · ข้าม {r.Skipped}"));
    }

    [HttpPost("employees/{employeeId:guid}/terminate")]
    public async Task<ActionResult<ApiResponse<bool>>> Terminate(Guid companyId, Guid employeeId, [FromQuery] DateTime endDate)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        await _service.TerminateEmployeeAsync(companyId, employeeId, endDate); return Ok(new ApiResponse<bool>(true, true));
    }

    /// <summary>คำนวณค่าชดเชยตามมาตรา 118 (preview เท่านั้น) — ใช้แสดงตัวเลขให้ HR
    /// ดูก่อนออกใบเงินเดือนสุดท้ายหรือบันทึก Expense voucher; ไม่บันทึก GL.</summary>
    [HttpPost("employees/{employeeId:guid}/severance-preview")]
    public async Task<ActionResult<ApiResponse<SeverancePreviewResponse>>> PreviewSeverance(
        Guid companyId, Guid employeeId, [FromBody] SeverancePreviewRequest request)
        => Ok(new ApiResponse<SeverancePreviewResponse>(true,
            await _service.PreviewSeverancePayAsync(companyId, employeeId, request)));

    /// <summary>ปิดปี: คำนวณวันลาคงเหลือทุกพนักงานของปี ที่ระบุ →
    /// upsert EmployeeLeaveBalance ของปีถัดไป (carry-forward).</summary>
    [HttpPost("leaves/carry-forward/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> RunLeaveCarryForward(Guid companyId, int year)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var actor = JwtHelper.GetUserIdFromClaims(User).ToString();
        var count = await _service.RunYearEndLeaveCarryForwardAsync(companyId, year, actor);
        return Ok(new ApiResponse<object>(true, new { upserts = count, targetYear = year + 1 },
            $"ทำ carry-forward วันลาเข้าปี {year + 1} เรียบร้อย ({count} รายการ)"));
    }

    public sealed record PostSeveranceRequest(decimal Amount, DateTime PayDate);

    /// <summary>โพสต์ JE เงินชดเชยเลิกจ้าง §118 — Dr Severance / Cr Cash.
    /// ใช้คู่กับ severance-preview: HR กดยืนยันยอดที่คำนวณแล้วโพสต์เข้า GL.</summary>
    [HttpPost("employees/{employeeId:guid}/severance/post")]
    public async Task<ActionResult<ApiResponse<object>>> PostSeverance(
        Guid companyId, Guid employeeId, [FromBody] PostSeveranceRequest req)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var jeId = await _service.PostSeveranceAsync(companyId, employeeId,
            req.Amount, req.PayDate, JwtHelper.GetUserIdFromClaims(User).ToString());
        return Ok(new ApiResponse<object>(true, new { journalEntryId = jeId }, "โพสต์เงินชดเชยเข้า GL เรียบร้อย"));
    }

    // Payroll Items
    [HttpPost("items")]
    public async Task<ActionResult<ApiResponse<PayrollItemResponse>>> CreateItem(Guid companyId, [FromBody] CreatePayrollItemRequest request)
        => StatusCode(201, new ApiResponse<PayrollItemResponse>(true, await _service.CreatePayrollItemAsync(companyId, request)));

    [HttpGet("items")]
    public async Task<ActionResult<ApiResponse<List<PayrollItemResponse>>>> GetItems(Guid companyId)
        => Ok(new ApiResponse<List<PayrollItemResponse>>(true, await _service.GetPayrollItemsAsync(companyId)));

    // Payroll Runs
    [HttpPost("runs")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> CreateRun(Guid companyId, [FromBody] CreatePayrollRunRequest request)
        => StatusCode(201, new ApiResponse<PayrollRunResponse>(true, await _service.CreatePayrollRunAsync(companyId, request, User.Identity?.Name ?? "")));

    /// <summary>Import payroll run จากระบบนอก (TakeTime) — รับยอดสำเร็จรูป
    /// ต่อพนักงาน สร้าง run สถานะ Calculated ทันที (ไม่คำนวณใหม่). จากนั้น
    /// approve → pay → ออก GL + ภงด.1 + สปส.1-10 + 50ทวิ + payslip จากยอด
    /// ที่ส่งมา. Idempotent ผ่าน externalRunRef. CreatedBy = X-Acting-User
    /// (ผ่าน NameIdentifier ของ int_/acc_ key).</summary>
    [HttpPost("runs/import")]
    public async Task<ActionResult<ApiResponse<ImportPayrollRunResult>>> ImportRun(
        Guid companyId, [FromBody] ImportPayrollRunRequest request)
    {
        var createdBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _service.ImportPayrollRunAsync(companyId, request, createdBy);
        return StatusCode(result.WasExisting ? 200 : 201,
            new ApiResponse<ImportPayrollRunResult>(true, result,
                result.WasExisting
                    ? "พบ run เดิม (idempotent) — คืน run ที่มีอยู่"
                    : $"import สำเร็จ — run {result.PayrollNumber} สถานะ Calculated ({result.EmployeeCount} คน). " +
                      "เรียก /approve → /pay เพื่อออก GL + เอกสารตามกฎหมาย"));
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> GetRun(Guid companyId, Guid runId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PayrollRunResponse>(true, await _service.GetPayrollRunAsync(companyId, runId)));
    }

    [HttpGet("runs")]
    public async Task<ActionResult<ApiResponse<PagedResponse<PayrollRunResponse>>>> GetRuns(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PagedResponse<PayrollRunResponse>>(true, await _service.GetPayrollRunsAsync(companyId, new PagedRequest(page, pageSize))));
    }

    [HttpPost("runs/{runId:guid}/calculate")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Calculate(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.CalculatePayrollAsync(companyId, runId)));

    [HttpPost("runs/{runId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Approve(Guid companyId, Guid runId)
        => Ok(new ApiResponse<PayrollRunResponse>(true, await _service.ApprovePayrollAsync(companyId, runId, User.Identity?.Name ?? "")));

    [HttpPost("runs/{runId:guid}/pay")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Pay(Guid companyId, Guid runId)
    {
        try
        {
            var res = await _service.ProcessPaymentAsync(companyId, runId, User.Identity?.Name ?? "");
            // ซ่อมยอดให้ก่อนลง JE = ต้องบอก ห้ามเปลี่ยนตัวเลขเงียบ ๆ
            var ssoFixed = _service.LastPaySsoAdjustedCount;
            var msg = "จ่ายเงินเดือนสำเร็จ";
            if (ssoFixed > 0)
                msg += $" · ปรับยอดประกันสังคมฝั่งนายจ้างให้ตรงกับฝั่งลูกจ้าง {ssoFixed} คน "
                     + "ก่อนลงบัญชี (ม.33 ใช้ฐานค่าจ้างเดียวกันทั้งสองฝั่ง — "
                     + "ยอดที่ระบบต้นทางส่งมาไม่สอดคล้องกัน)";
            return Ok(new ApiResponse<PayrollRunResponse>(true, res, msg));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    /// <summary>นำส่งประกันสังคมให้ สปส. (สปส.1-10) — post JE คู่ที่สอง
    /// Dr 21815 / Cr Bank. คำนวณเงินเพิ่ม §49 อัตโนมัติ. ใช้กับรอบ Paid +
    /// ยังไม่นำส่ง.</summary>
    /// <summary>กลับรายการนำส่งประกันสังคม (เช่นนำส่งผิดยอด/ผิดวัน) — กลับ JE
    /// ก้อนที่สองลงวันเดียวกับที่นำส่งเดิม แล้วปลดล็อกให้นำส่งใหม่</summary>
    [HttpPost("runs/{runId:guid}/reverse-sso")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> ReverseSso(
        Guid companyId, Guid runId, [FromBody] ReopenPayrollRunRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            var res = await _service.ReverseSsoSettlementAsync(companyId, runId,
                request.Reason, User.Identity?.Name ?? "");
            return Ok(new ApiResponse<PayrollRunResponse>(true, res,
                "กลับรายการนำส่งประกันสังคมแล้ว — แก้ยอดในรอบเงินเดือนแล้วนำส่งใหม่ได้"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    [HttpPost("runs/{runId:guid}/settle-sso")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> SettleSso(
        Guid companyId, Guid runId, [FromBody] SettleSsoRequest request)
        => Ok(new ApiResponse<PayrollRunResponse>(true,
            await _service.SettleSocialSecurityAsync(companyId, runId,
                request.PayDate, request.BankAccountId, request.BankGlAccountId, request.FilingNumber,
                User.Identity?.Name ?? "")));

    [HttpGet("runs/{runId:guid}/employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<PayrollDetailResponse>>> GetDetail(Guid companyId, Guid runId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<PayrollDetailResponse>(true, await _service.GetPayrollDetailAsync(companyId, runId, employeeId)));
    }

    /// <summary>ตั้งแหล่งจ่ายเงินสุทธิรายคน (ก่อนจ่าย). accountCode=null/ว่าง =
    /// กลับไปใช้ค่าระดับ run/default.</summary>
    [HttpPut("runs/{runId:guid}/employees/{employeeId:guid}/payment-account")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> SetEmployeePaymentAccount(
        Guid companyId, Guid runId, Guid employeeId, [FromBody] SetPaymentAccountRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            var res = await _service.SetEmployeePaymentAccountAsync(companyId, runId, employeeId, request.AccountCode);
            return Ok(new ApiResponse<PayrollRunResponse>(true, res, "อัปเดตแหล่งจ่ายรายคนแล้ว"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    /// <summary>แก้ยอดรายคนในรอบ (ก่อนจ่าย) — อัปเดตเฉพาะ field ที่ส่งมา +
    /// รวม Gross/หัก/สุทธิ และ run totals ใหม่.</summary>
    [HttpPut("runs/{runId:guid}/employees/{employeeId:guid}/detail")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> UpdateDetail(
        Guid companyId, Guid runId, Guid employeeId, [FromBody] UpdatePayrollDetailRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            var res = await _service.UpdatePayrollDetailAsync(companyId, runId, employeeId, request, User.Identity?.Name ?? "");
            return Ok(new ApiResponse<PayrollRunResponse>(true, res, "แก้ยอดรายคนแล้ว"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    /// <summary>สลิปเงินเดือน PDF. download=false (ค่าเริ่มต้น) → แสดง inline ใน
    /// iframe; download=true → แนบไฟล์ให้โหลด (ชื่อไฟล์มีชื่อพนักงาน).</summary>
    [HttpGet("runs/{runId:guid}/employees/{employeeId:guid}/payslip")]
    public async Task<ActionResult> GetPayslip(Guid companyId, Guid runId, Guid employeeId,
        [FromQuery] bool download = false)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var slip = await _service.GeneratePayslipAsync(companyId, runId, employeeId);
        if (download)
            // attachment + ชื่อไฟล์ไทย (File() เข้ารหัส filename* UTF-8 ให้เอง)
            return File(slip.PdfData, "application/pdf", slip.FileName);
        // inline — ให้เบราว์เซอร์ render ใน iframe แทนการดาวน์โหลด
        Response.Headers["Content-Disposition"] = "inline";
        return File(slip.PdfData, "application/pdf");
    }

    // ===== ส่งสลิปทาง LINE =====

    /// <summary>สร้างรหัสผูก LINE 6 หลักให้พนักงาน — HR แสดงรหัส/QR ให้พนักงาน
    /// เพิ่มเพื่อน LINE OA บริษัทแล้วส่ง "สลิป {รหัส}" เพื่อรับสลิปทาง LINE.</summary>
    [HttpPost("employees/{employeeId:guid}/line-bind-code")]
    public async Task<ActionResult> IssueLineBindCode(Guid companyId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var (code, addFriendUrl) = await _payslipLine.IssueBindCodeAsync(companyId, employeeId, ActorEmail());
        return Ok(new ApiResponse<object>(true, new
        {
            code,
            addFriendUrl,
            instruction = $"ให้พนักงานเพิ่มเพื่อน LINE OA บริษัท แล้วส่งข้อความ: สลิป {code}",
            expiresInHours = 24,
        }));
    }

    /// <summary>สถานะผูก LINE ของพนักงาน (ผูกแล้ว/ยังไม่ผูก).</summary>
    [HttpGet("employees/{employeeId:guid}/line-status")]
    public async Task<ActionResult> GetLineStatus(Guid companyId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var (bound, masked) = await _payslipLine.GetLineStatusAsync(companyId, employeeId);
        return Ok(new ApiResponse<object>(true, new { bound, maskedLineUserId = masked }));
    }

    /// <summary>ส่งสลิปงวดนี้ให้พนักงานคนเดียวทาง LINE.</summary>
    [HttpPost("runs/{runId:guid}/employees/{employeeId:guid}/payslip/send-line")]
    public async Task<ActionResult> SendPayslipLine(Guid companyId, Guid runId, Guid employeeId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var result = await _payslipLine.SendPayslipAsync(companyId, runId, employeeId, ActorEmail());
        var (ok, msg) = result switch
        {
            PayslipLineSendResult.Sent => (true, "ส่งสลิปทาง LINE สำเร็จ"),
            PayslipLineSendResult.NotBound => (false, "พนักงานยังไม่ได้ผูก LINE — สร้างรหัสให้พนักงานผูกก่อน"),
            PayslipLineSendResult.NoPayrollDetail => (false, "ไม่พบรายละเอียดเงินเดือนของพนักงานในงวดนี้"),
            PayslipLineSendResult.LineNotConfigured => (false, "ยังไม่ได้ตั้งค่า LINE Messaging API ของบริษัท"),
            _ => (false, "ส่งไม่สำเร็จ กรุณาลองใหม่"),
        };
        return Ok(new ApiResponse<object>(ok, new { result = result.ToString(), message = msg }));
    }

    /// <summary>ส่งสลิปทั้งงวดให้พนักงานทุกคนที่ผูก LINE แล้ว.</summary>
    [HttpPost("runs/{runId:guid}/payslip/send-line-all")]
    public async Task<ActionResult> SendPayslipLineAll(Guid companyId, Guid runId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var r = await _payslipLine.SendPayslipForRunAsync(companyId, runId, ActorEmail());
        return Ok(new ApiResponse<object>(true, new
        {
            total = r.Total, sent = r.Sent, notBound = r.NotBound, failed = r.Failed,
            message = $"ส่งสำเร็จ {r.Sent}/{r.Total} คน" +
                      (r.NotBound > 0 ? $" · ยังไม่ผูก LINE {r.NotBound} คน" : "") +
                      (r.Failed > 0 ? $" · ล้มเหลว {r.Failed} คน" : ""),
        }));
    }

    // Leave
    [HttpPost("leaves")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CreateLeave(Guid companyId, [FromBody] CreateLeaveRequest request)
        => StatusCode(201, new ApiResponse<LeaveResponse>(true, await _service.CreateLeaveAsync(companyId, request)));

    [HttpGet("leaves/{leaveId:guid}")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> GetLeave(Guid companyId, Guid leaveId)
        => Ok(new ApiResponse<LeaveResponse>(true, await _service.GetLeaveAsync(companyId, leaveId)));

    [HttpPost("leaves/{leaveId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> ApproveLeave(Guid companyId, Guid leaveId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.ApproveLeaveAsync(companyId, leaveId, userId, name)));
    }

    [HttpPost("leaves/{leaveId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> RejectLeave(Guid companyId, Guid leaveId, [FromBody] RejectLeaveRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.RejectLeaveAsync(companyId, leaveId, userId, name, request)));
    }

    [HttpPost("leaves/{leaveId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CancelLeave(Guid companyId, Guid leaveId)
        => Ok(new ApiResponse<LeaveResponse>(true, await _service.CancelLeaveAsync(companyId, leaveId)));

    [HttpGet("leaves")]
    public async Task<ActionResult<ApiResponse<List<LeaveResponse>>>> GetLeaves(Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] int? year)
        => Ok(new ApiResponse<List<LeaveResponse>>(true, await _service.GetLeavesAsync(companyId, employeeId, year)));

    [HttpGet("leaves/balance")]
    public async Task<ActionResult<ApiResponse<LeaveBalanceResponse>>> GetLeaveBalance(
        Guid companyId, [FromQuery] Guid employeeId, [FromQuery] int? year)
        => Ok(new ApiResponse<LeaveBalanceResponse>(true,
            await _service.GetLeaveBalanceAsync(companyId, employeeId, year ?? DateTime.UtcNow.Year)));

    [HttpPost("runs/{runId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> VoidRun(Guid companyId, Guid runId)
    {
        // ยกเลิกรอบ = กลับ JE + คืนเงินทดรอง — ทำลายข้อมูลมากกว่าการ "อ่าน"
        // รายละเอียดรอบเสียอีก แต่เดิม**ไม่มีด่านสิทธิ์เลย** ขณะที่ GetRun /
        // UpdateDetail มี (บทเรียน "ด่านที่อ่อนกว่าแต่ทำได้มากกว่า คือช่องที่
        // ใหญ่ที่สุด" — เวลาเพิ่ม endpoint ให้ถามว่าหน้าอื่นที่แตะข้อมูลชุด
        // เดียวกันใช้ด่านอะไร แล้วใช้อย่างน้อยเท่ากัน)
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            await _service.VoidPayrollAsync(companyId, runId);
            return Ok(new ApiResponse<bool>(true, true));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<bool>(false, false, ex.Message));
        }
    }

    /// <summary>กลับรายการจ่ายเงินเดือน (Paid → Approved) เพื่อแก้ยอดย้อนหลัง
    /// แล้วกด "จ่าย" ใหม่ — กลับ JE ลงวันเดียวกับ PayDate + คืนเงินทดรอง +
    /// บันทึกเหตุผลลง AuditLog. บล็อกเมื่อนำส่ง สปส. แล้ว/งวดบัญชีปิดแล้ว.</summary>
    [HttpPost("runs/{runId:guid}/reopen")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> ReopenRun(
        Guid companyId, Guid runId, [FromBody] ReopenPayrollRunRequest request)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        try
        {
            var res = await _service.ReopenPaidRunAsync(companyId, runId,
                request.Reason, User.Identity?.Name ?? "");
            // แก้อะไรให้ต้องบอก — ห้ามเปลี่ยนตัวเลขเงียบ ๆ
            var ssoFixed = _service.LastReopenSsoAdjustedCount;
            var msg = "กลับรายการจ่ายแล้ว — รอบกลับไปสถานะ \"อนุมัติแล้ว\" แก้ยอดได้ จากนั้นกด \"จ่าย\" ใหม่";
            if (ssoFixed > 0)
                msg += $" · ปรับยอดประกันสังคมฝั่งนายจ้างให้ตรงกับฝั่งลูกจ้าง {ssoFixed} คน "
                     + "(ม.33 ใช้ฐานค่าจ้างเดียวกันทั้งสองฝั่ง) — ตรวจยอดก่อนกดจ่าย";
            return Ok(new ApiResponse<PayrollRunResponse>(true, res, msg));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    // Reports — ภงด.1 exposes individual employee salary/WHT and is sensitive
    // payroll data, so it sits behind the same Payroll permission gate. ภงด.3
    // (vendor / freelancer WHT) stays open since it's part of the regular AP flow.
    [HttpGet("pnd1/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd1(Guid companyId, int year, int month)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<object>(true, await _service.GeneratePnd1Async(companyId, year, month)));
    }

    [HttpGet("pnd3/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetPnd3(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GeneratePnd3Async(companyId, year, month)));

    [HttpGet("sso/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetSso(Guid companyId, int year, int month)
        => Ok(new ApiResponse<object>(true, await _service.GenerateSsoReportAsync(companyId, year, month)));

    /// <summary>ออกใบ 50 ทวิรายปีให้พนักงาน (ภงด.1 §40(1) เงินเดือน).
    /// employeeId=null → คืน Zip รวมทุกคน, ระบุ → คืน PDF เดียวคน.
    /// อิงเฉพาะ PayrollRun ที่ Status=Paid เท่านั้น.</summary>
    [HttpGet("wht-cert/annual/{year:int}")]
    public async Task<IActionResult> GetAnnualWhtCerts(Guid companyId, int year, [FromQuery] Guid? employeeId = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var requestedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        var (fileName, bytes) = await _service.GenerateAnnualEmployeeWhtCertsAsync(companyId, year, employeeId, requestedBy);
        var contentType = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? "application/zip" : "application/pdf";
        return File(bytes, contentType, fileName);
    }

    // ===== SSO year-config (เพดานค่าจ้าง/อัตราสมทบ ปรับได้รายปี) =====

    public sealed record SsoYearConfigRequest(int Year, decimal WageCeiling,
        decimal RatePercent = 5m, decimal EmployerRatePercent = 5m, string? Notes = null);

    /// <summary>Effective SSO parameters per year: company overrides merged
    /// over the statutory default schedule (15,000 → 17,500 ปี 2026 →
    /// 20,000 ปี 2572 → 23,000 ปี 2575). UI renders this as the editable
    /// year table.</summary>
    [HttpGet("sso-config")]
    public async Task<ActionResult<ApiResponse<object>>> GetSsoConfig(
        Guid companyId, [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int? fromYear = null, [FromQuery] int? toYear = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var start = fromYear ?? DateTime.UtcNow.Year - 1;
        var end = toYear ?? DateTime.UtcNow.Year + 6;
        var overrides = await db.SsoYearConfigs
            .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.Year >= start && c.Year <= end)
            .ToListAsync();
        var rows = Enumerable.Range(start, end - start + 1).Select(y =>
        {
            var ov = overrides.FirstOrDefault(o => o.Year == y);
            var (defCeiling, defRate) = SsoRateSchedule.GetDefault(y);
            var ceiling = ov?.WageCeiling ?? defCeiling;
            var rate = ov != null ? ov.RatePercent / 100m : defRate;
            return new
            {
                Year = y,
                WageCeiling = ceiling,
                RatePercent = rate * 100m,
                EmployerRatePercent = ov?.EmployerRatePercent ?? defRate * 100m,
                MaxMonthlyContribution = Math.Round(ceiling * rate, 2),
                IsOverride = ov != null,
                ov?.Notes,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>Upsert the SSO parameters for one year (ปี ค.ศ. — พ.ศ. ถูก
    /// normalize ให้). ใช้เมื่อประกาศ/พรฎ. ฉบับใหม่เปลี่ยนเพดานหรืออัตรา
    /// (รวมกรณีลดอัตราชั่วคราว) โดยไม่ต้องรออัปเดตระบบ.</summary>
    [HttpPut("sso-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertSsoConfig(
        Guid companyId, [FromBody] SsoYearConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var year = req.Year > 2400 ? req.Year - 543 : req.Year;
        if (year < 2000 || year > 2100)
            return BadRequest(new ApiResponse<object>(false, null, "ปีไม่ถูกต้อง"));
        if (req.WageCeiling <= 0 || req.RatePercent <= 0 || req.RatePercent > 30)
            return BadRequest(new ApiResponse<object>(false, null, "เพดานค่าจ้าง/อัตราสมทบไม่ถูกต้อง"));

        var existing = await db.SsoYearConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == year && !c.IsDeleted);
        if (existing == null)
        {
            existing = new Models.Entities.SsoYearConfig { CompanyId = companyId, Year = year };
            db.SsoYearConfigs.Add(existing);
        }
        existing.WageCeiling = req.WageCeiling;
        existing.RatePercent = req.RatePercent;
        existing.EmployerRatePercent = req.EmployerRatePercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            existing.Year,
            existing.WageCeiling,
            existing.RatePercent,
            MaxMonthlyContribution = Math.Round(existing.WageCeiling * existing.RatePercent / 100m, 2),
        }, $"บันทึกค่าประกันสังคมปี {year} แล้ว — สมทบสูงสุด {existing.WageCeiling * existing.RatePercent / 100m:N2} บาท/เดือน"));
    }

    /// <summary>Remove a year override — the statutory default takes over.</summary>
    [HttpDelete("sso-config/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSsoConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var y = year > 2400 ? year - 543 : year;
        var existing = await db.SsoYearConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Year == y && !c.IsDeleted);
        if (existing == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบค่าตั้งของปีนี้"));
        existing.IsDeleted = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, $"ลบค่าตั้งปี {y} แล้ว — กลับไปใช้ตารางตามกฎหมาย"));
    }

    // ===== Tax-rule config (PIT brackets + allowances รายปี) =====

    public sealed record TaxRuleConfigRequest(
        int FiscalYear,
        string? BracketsJson,
        decimal PersonalAllowance,
        decimal SpouseAllowance,
        decimal ChildAllowance,
        decimal ChildAllowancePost2561,
        decimal ParentAllowance,
        decimal Section42TwiCap,
        decimal LifeInsuranceCap,
        decimal HealthInsuranceCap,
        decimal PvdCap,
        decimal MortgageInterestCap,
        decimal DonationCapPercent,
        string? Notes);

    /// <summary>คืน TaxRuleConfig ของบริษัท × ช่วงปี (default 6 ปีย้อนหลัง +
    /// ล่วงหน้า). Row ที่ไม่มีใน DB คืน statutory default + IsOverride=false
    /// เพื่อให้ UI render ตารางครบทุกปีในช่วง.</summary>
    [HttpGet("tax-rule-config")]
    public async Task<ActionResult<ApiResponse<object>>> GetTaxRuleConfig(
        Guid companyId, [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int? fromYear = null, [FromQuery] int? toYear = null)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var start = fromYear ?? DateTime.UtcNow.Year - 1;
        var end = toYear ?? DateTime.UtcNow.Year + 4;
        var overrides = await db.TaxRuleConfigs
            .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.FiscalYear >= start && c.FiscalYear <= end)
            .ToListAsync();
        var defaultBrackets = "[{\"upperBound\":150000,\"rate\":0},{\"upperBound\":300000,\"rate\":0.05},{\"upperBound\":500000,\"rate\":0.10},{\"upperBound\":750000,\"rate\":0.15},{\"upperBound\":1000000,\"rate\":0.20},{\"upperBound\":2000000,\"rate\":0.25},{\"upperBound\":5000000,\"rate\":0.30},{\"upperBound\":0,\"rate\":0.35}]";
        var rows = Enumerable.Range(start, end - start + 1).Select(y =>
        {
            var ov = overrides.FirstOrDefault(o => o.FiscalYear == y);
            return new
            {
                FiscalYear = y,
                BracketsJson = ov?.BracketsJson ?? defaultBrackets,
                PersonalAllowance = ov?.PersonalAllowance ?? 60_000m,
                SpouseAllowance = ov?.SpouseAllowance ?? 60_000m,
                ChildAllowance = ov?.ChildAllowance ?? 30_000m,
                ChildAllowancePost2561 = ov?.ChildAllowancePost2561 ?? 60_000m,
                ParentAllowance = ov?.ParentAllowance ?? 30_000m,
                Section42TwiCap = ov?.Section42TwiCap ?? 100_000m,
                LifeInsuranceCap = ov?.LifeInsuranceCap ?? 100_000m,
                HealthInsuranceCap = ov?.HealthInsuranceCap ?? 25_000m,
                PvdCap = ov?.PvdCap ?? 500_000m,
                MortgageInterestCap = ov?.MortgageInterestCap ?? 100_000m,
                DonationCapPercent = ov?.DonationCapPercent ?? 10m,
                IsOverride = ov != null,
                ov?.Notes,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPut("tax-rule-config")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertTaxRuleConfig(
        Guid companyId, [FromBody] TaxRuleConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var year = req.FiscalYear > 2400 ? req.FiscalYear - 543 : req.FiscalYear;
        if (year < 2000 || year > 2100)
            return BadRequest(new ApiResponse<object>(false, null, "ปีไม่ถูกต้อง"));
        if (req.PersonalAllowance < 0 || req.SpouseAllowance < 0 || req.ChildAllowance < 0
            || req.ParentAllowance < 0 || req.PvdCap < 0 || req.DonationCapPercent < 0 || req.DonationCapPercent > 100)
            return BadRequest(new ApiResponse<object>(false, null, "ค่าลดหย่อนติดลบ หรือ donationCap > 100% ไม่ได้"));

        var existing = await db.TaxRuleConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.FiscalYear == year && !c.IsDeleted);
        if (existing == null)
        {
            existing = new Models.Entities.TaxRuleConfig { CompanyId = companyId, FiscalYear = year };
            db.TaxRuleConfigs.Add(existing);
        }
        existing.BracketsJson = req.BracketsJson ?? "";
        existing.PersonalAllowance = req.PersonalAllowance;
        existing.SpouseAllowance = req.SpouseAllowance;
        existing.ChildAllowance = req.ChildAllowance;
        existing.ChildAllowancePost2561 = req.ChildAllowancePost2561;
        existing.ParentAllowance = req.ParentAllowance;
        existing.Section42TwiCap = req.Section42TwiCap;
        existing.LifeInsuranceCap = req.LifeInsuranceCap;
        existing.HealthInsuranceCap = req.HealthInsuranceCap;
        existing.PvdCap = req.PvdCap;
        existing.MortgageInterestCap = req.MortgageInterestCap;
        existing.DonationCapPercent = req.DonationCapPercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { existing.FiscalYear, existing.PersonalAllowance, existing.PvdCap },
            $"บันทึกกฎภาษี ปี {year} แล้ว"));
    }

    [HttpDelete("tax-rule-config/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaxRuleConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        var y = year > 2400 ? year - 543 : year;
        var existing = await db.TaxRuleConfigs
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.FiscalYear == y && !c.IsDeleted);
        if (existing == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบกฎภาษีของปีนี้"));
        existing.IsDeleted = true;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, $"ลบกฎภาษีปี {y} แล้ว — กลับไปใช้ default"));
    }
}
