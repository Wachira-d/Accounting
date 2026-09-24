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

    /// <summary>ด่านสำหรับข้อมูล "รายคน": ของตัวเองดูได้เสมอ · ของคนอื่นต้องมีสิทธิ์ HR
    ///
    /// <para>ที่มา (ผลตรวจ D-A2): endpoint อ่าน 7 ตัวไม่มีด่านเลย — รวม
    /// <c>sso</c> (และ <c>pnd3</c> ที่ลบไปรอบ 170) ที่คืน<b>ชื่อ + ค่าจ้าง + ภาษีของพนักงานทุกคน</b>
    /// และ <c>leaves</c>/<c>leaves/balance</c> ที่รับ <c>employeeId</c> อะไรก็ได้
    /// ⇒ สมาชิกคนไหนของบริษัทก็อ่านข้อมูลเงินเดือน/วันลาของเพื่อนร่วมงานได้</para></summary>
    private async Task<ActionResult?> RequireOwnOrPayrollAsync(Guid companyId, Guid employeeId)
    {
        var actorUserId = JwtHelper.GetUserIdFromClaims(User);
        if (actorUserId != Guid.Empty && employeeId != Guid.Empty
            && await _service.IsEmployeeOfUserAsync(companyId, employeeId, actorUserId))
            return null;
        return await CheckPayrollAccessAsync(companyId);
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

    /// <summary>
    /// **ด่านของสิทธิ์ระดับ "การกระทำ" — คืน `null` = ผ่าน**
    ///
    /// <para>═══ ที่มา (ผลตรวจ D-A1 / D-A2) ═══ endpoint ที่เขียนข้อมูลเงินเดือน
    /// ทั้งหมดเคยมีด่านแค่ <see cref="CheckPayrollAccessAsync"/> ซึ่งถาม
    /// **"ดูข้อมูลเงินเดือนได้ไหม" (PayrollView)** เท่านั้น — และอีก 20 กว่า
    /// endpoint (สร้างรอบ · import · คำนวณ · **อนุมัติ** · **จ่าย** · นำส่ง ปกส. ·
    /// แก้ยอดรายคน · ตั้งค่าอัตรา ปกส./ภาษี) ไม่มีด่านอะไรเลย ⇒ สมาชิกที่ได้สิทธิ์
    /// "ดูเงินเดือน" (หรือแม้แต่ไม่ได้อะไรเลยในกลุ่มหลัง) **กดจ่ายเงินเดือนจริง
    /// ลง JE + ออก ภ.ง.ด.1 + สปส.1-10 ได้**</para>
    ///
    /// <para>คีย์ `PayrollRun` / `PayrollApprove` / `PayrollPay` มีอยู่ใน
    /// <c>PermissionKeys</c> มาตลอดแต่ไม่เคยมีใครเรียก — "ของที่สร้างไว้แล้ว
    /// ไม่ได้ถูกเรียกใช้" (CLAUDE.md กฎเหล็ก #4)</para>
    ///
    /// <para>Owner/SystemAdmin/Accountant ผ่านอัตโนมัติที่ <c>PermissionService</c>
    /// (คีย์ทั้งสามอยู่ใน <c>AccountantDefaultKeys</c>) ⇒ ผู้ที่ทำเงินเดือนอยู่แล้ว
    /// ไม่กระทบ · กระทบเฉพาะ role ที่ต้อง grant อยู่แล้วตามการออกแบบ</para>
    /// </summary>
    private async Task<ActionResult?> RequireAnyAsync(Guid companyId, string verb, params string[] anyOfKeys)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        foreach (var k in anyOfKeys)
            if (await _permissions.HasPermissionAsync(companyId, userId, k)) return null;
        var need = string.Join(" หรือ ", anyOfKeys.Select(k => k.Replace("perm:", "")));
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            redacted = false,
            kind = "Payroll",
            requiredPermission = need,
        }, $"ไม่มีสิทธิ์{verb} (ต้องการ {need})"));
    }

    /// <summary>ด่านของ endpoint ที่ **เขียน** ข้อมูลเงินเดือน — ต้องผ่านทั้ง
    /// (ก) ด่านความอ่อนไหว Payroll และ (ข) สิทธิ์ระดับการกระทำ</summary>
    private async Task<ActionResult?> RequirePayrollWriteAsync(Guid companyId, string verb, params string[] anyOfKeys)
        => await CheckPayrollAccessAsync(companyId) ?? await RequireAnyAsync(companyId, verb, anyOfKeys);

    // Employees
    [HttpPost("employees")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> CreateEmployee(Guid companyId, [FromBody] CreateEmployeeRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "เพิ่มพนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        // ★ เดิมคืนแถวพนักงาน **ดิบ** ไม่ผ่านการปิดบัง PII/เงินเดือน ต่างจาก
        // `GetEmployee`/`GetEmployees` ที่ส่ง CanViewPii/CanViewPayroll เข้าไป
        // ⇒ ใครก็ได้ที่รู้ (ExternalSystem, ExternalId) อ่านเลขบัตรประชาชนและ
        // เงินเดือนได้ครบโดยไม่ต้องมีสิทธิ์อะไรเลย (ผลตรวจ D-A2)
        // → หา id ก่อน แล้วเดินผ่านเส้นเดียวกับ GetEmployee เพื่อให้กติกาปิดบัง
        //   เป็นชุดเดียวกัน (ไม่สร้างกติกาสำเนาที่สอง)
        var lookup = await _service.GetEmployeeByExternalAsync(companyId, externalSystem, externalId);
        if (lookup == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบพนักงาน"));
        var emp = await _service.GetEmployeeAsync(companyId, lookup.Id,
            await CanViewPiiAsync(companyId, lookup.Id), await CanViewPayrollAsync(companyId));
        return Ok(new ApiResponse<EmployeeResponse>(true, emp));
    }

    [HttpDelete("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteEmployee(Guid companyId, Guid employeeId)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ลบพนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        try { await _service.DeleteEmployeeAsync(companyId, employeeId); return Ok(new ApiResponse<bool>(true, true, "ลบพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPost("employees/{employeeId:guid}/restore")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> RestoreEmployee(Guid companyId, Guid employeeId)
    {
        var block = await RequirePayrollWriteAsync(companyId, "กู้คืนพนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        try { return Ok(new ApiResponse<EmployeeResponse>(true, await _service.RestoreEmployeeAsync(companyId, employeeId), "กู้คืนพนักงานแล้ว")); }
        catch (KeyNotFoundException ex) { return NotFound(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpPut("employees/{employeeId:guid}")]
    public async Task<ActionResult<ApiResponse<EmployeeResponse>>> UpdateEmployee(Guid companyId, Guid employeeId, [FromBody] UpdateEmployeeRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "แก้ไขข้อมูลพนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return Ok(new ApiResponse<EmployeeResponse>(true, await _service.UpdateEmployeeAsync(companyId, employeeId, request)));
    }

    [HttpPost("employees/sync")]
    public async Task<ActionResult<ApiResponse<SyncEmployeesResponse>>> SyncEmployees(
        Guid companyId, [FromBody] SyncEmployeesRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "sync ข้อมูลพนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        var r = await _service.SyncEmployeesAsync(companyId, request);
        return Ok(new ApiResponse<SyncEmployeesResponse>(true, r,
            $"sync เสร็จ — เพิ่ม {r.Inserted} · อัปเดต {r.Updated} · ข้าม {r.Skipped}"));
    }

    [HttpPost("employees/{employeeId:guid}/terminate")]
    public async Task<ActionResult<ApiResponse<bool>>> Terminate(Guid companyId, Guid employeeId, [FromQuery] DateTime endDate)
    {
        var block = await RequirePayrollWriteAsync(companyId, "แจ้งพนักงานลาออก", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        await _service.TerminateEmployeeAsync(companyId, employeeId, endDate); return Ok(new ApiResponse<bool>(true, true));
    }

    /// <summary>คำนวณค่าชดเชยตามมาตรา 118 (preview เท่านั้น) — ใช้แสดงตัวเลขให้ HR
    /// ดูก่อนออกใบเงินเดือนสุดท้ายหรือบันทึก Expense voucher; ไม่บันทึก GL.</summary>
    [HttpPost("employees/{employeeId:guid}/severance-preview")]
    public async Task<ActionResult<ApiResponse<SeverancePreviewResponse>>> PreviewSeverance(
        Guid companyId, Guid employeeId, [FromBody] SeverancePreviewRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ดูตัวอย่างค่าชดเชย", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return Ok(new ApiResponse<SeverancePreviewResponse>(true,
            await _service.PreviewSeverancePayAsync(companyId, employeeId, request)));
    }

    /// <summary>ปิดปี: คำนวณวันลาคงเหลือทุกพนักงานของปี ที่ระบุ →
    /// upsert EmployeeLeaveBalance ของปีถัดไป (carry-forward).</summary>
    [HttpPost("leaves/carry-forward/{year:int}")]
    public async Task<ActionResult<ApiResponse<object>>> RunLeaveCarryForward(Guid companyId, int year)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ทำ carry-forward วันลา", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        var block = await RequirePayrollWriteAsync(companyId, "โพสต์เงินชดเชยเข้า GL", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
        var jeId = await _service.PostSeveranceAsync(companyId, employeeId,
            req.Amount, req.PayDate, JwtHelper.GetUserIdFromClaims(User).ToString());
        return Ok(new ApiResponse<object>(true, new { journalEntryId = jeId }, "โพสต์เงินชดเชยเข้า GL เรียบร้อย"));
    }

    // Payroll Items
    [HttpPost("items")]
    public async Task<ActionResult<ApiResponse<PayrollItemResponse>>> CreateItem(Guid companyId, [FromBody] CreatePayrollItemRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "สร้างรายการเงินเดือน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return StatusCode(201, new ApiResponse<PayrollItemResponse>(true, await _service.CreatePayrollItemAsync(companyId, request)));
    }

    /// <summary>แก้ไขรายการเงินเดือน — รวมช่อง "ลักษณะเงินได้" (D6-3) ที่
    /// migration เติมให้แถวเก่าจากกฎรหัสเดิม ผู้ใช้ต้องแก้ให้ถูกได้เอง</summary>
    [HttpPut("items/{itemId:guid}")]
    public async Task<ActionResult<ApiResponse<PayrollItemResponse>>> UpdateItem(
        Guid companyId, Guid itemId, [FromBody] UpdatePayrollItemRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "แก้ไขรายการเงินเดือน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return Ok(new ApiResponse<PayrollItemResponse>(true, await _service.UpdatePayrollItemAsync(companyId, itemId, request)));
    }

    [HttpGet("items")]
    public async Task<ActionResult<ApiResponse<List<PayrollItemResponse>>>> GetItems(Guid companyId)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<List<PayrollItemResponse>>(true, await _service.GetPayrollItemsAsync(companyId)));
    }

    // Payroll Runs
    [HttpPost("runs")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> CreateRun(Guid companyId, [FromBody] CreatePayrollRunRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "สร้างรอบเงินเดือน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return StatusCode(201, new ApiResponse<PayrollRunResponse>(true, await _service.CreatePayrollRunAsync(companyId, request, User.Identity?.Name ?? "")));
    }

    /// <summary>Import payroll run จากระบบนอก (TakeTime) — รับยอดสำเร็จรูป
    /// ต่อพนักงาน สร้าง run สถานะ Calculated ทันที (ไม่คำนวณใหม่). จากนั้น
    /// approve → pay → ออก GL + ภงด.1 + สปส.1-10 + 50ทวิ + payslip จากยอด
    /// ที่ส่งมา. Idempotent ผ่าน externalRunRef. CreatedBy = X-Acting-User
    /// (ผ่าน NameIdentifier ของ int_/acc_ key).</summary>
    [HttpPost("runs/import")]
    public async Task<ActionResult<ApiResponse<ImportPayrollRunResult>>> ImportRun(
        Guid companyId, [FromBody] ImportPayrollRunRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "นำเข้ารอบเงินเดือน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
    {
        var block = await RequirePayrollWriteAsync(companyId, "คำนวณรอบเงินเดือน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return Ok(new ApiResponse<PayrollRunResponse>(true, await _service.CalculatePayrollAsync(companyId, runId)));
    }

    [HttpPost("runs/{runId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Approve(Guid companyId, Guid runId)
    {
        var block = await RequirePayrollWriteAsync(companyId, "อนุมัติรอบเงินเดือน", Models.Constants.PermissionKeys.PayrollApprove); if (block != null) return block;
        return Ok(new ApiResponse<PayrollRunResponse>(true, await _service.ApprovePayrollAsync(companyId, runId, User.Identity?.Name ?? "")));
    }

    [HttpPost("runs/{runId:guid}/pay")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> Pay(Guid companyId, Guid runId)
    {
        var block = await RequirePayrollWriteAsync(companyId, "จ่ายเงินเดือน", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
        try
        {
            var res = await _service.ProcessPaymentAsync(companyId, runId, User.Identity?.Name ?? "");
            // ซ่อมยอดให้ก่อนลง JE = ต้องบอก ห้ามเปลี่ยนตัวเลขเงียบ ๆ
            var ssoFixed = _service.LastPaySsoAdjustedCount;
            var ssoConflicts = _service.LastSsoConflicts;
            var msg = "จ่ายเงินเดือนสำเร็จ";
            if (ssoFixed > 0)
                msg += $" · ปรับยอดประกันสังคมฝั่งนายจ้างให้ตรงกับฝั่งลูกจ้าง {ssoFixed} คน "
                     + "ก่อนลงบัญชี (ม.33 ใช้ฐานค่าจ้างเดียวกันทั้งสองฝั่ง — "
                     + "ยอดที่ระบบต้นทางส่งมาไม่สอดคล้องกัน)";
            // แถวที่ระบบตัดสินแทนไม่ได้ ต้องดังตรงนี้ ไม่ใช่ปล่อยไปตายที่ด่าน
            // ตอนนำส่ง สปส. โดยผู้ใช้ไม่รู้ว่าต้นเหตุอยู่ที่ใคร
            if (ssoConflicts.Count > 0)
                msg += $" · ⚠️ ยอดประกันสังคมของ {ssoConflicts.Count} คนขัดกันจนระบบปรับให้ไม่ได้ "
                     + "(คงค่าเดิมไว้) — ต้องแก้ก่อนนำส่ง สปส.: "
                     + string.Join(" · ", ssoConflicts.Take(3));
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
        var block = await RequirePayrollWriteAsync(companyId, "กลับรายการนำส่งประกันสังคม", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
        try
        {
            var res = await _service.ReverseSsoSettlementAsync(companyId, runId,
                request.Reason, User.Identity?.Name ?? "");
            var revJe = _service.LastReversedJournalNumber;
            // ⚠️ ขอบเขตของปุ่มนี้คือ **JE ของการนำส่ง** เท่านั้น (Dr 21815 / Cr ธนาคาร)
            // ไม่ได้แตะ JE ของการจ่ายเงินเดือน (ที่มีบรรทัด 54120 ประกันสังคมนายจ้าง)
            // — ผู้ใช้เข้าใจสลับกันแล้วรายงานว่า "กดกลับรายการแล้วยอดยังผิด"
            var msg = "กลับรายการนำส่งประกันสังคมแล้ว"
                + (string.IsNullOrWhiteSpace(revJe) ? "" : $" (ใบสำคัญ {revJe})")
                + " — ปุ่มนี้กลับเฉพาะรายการ \"นำส่ง\" เท่านั้น "
                + "ถ้ายอดประกันสังคมในใบจ่ายเงินเดือนผิด ต้องกด \"กลับรายการจ่าย\" อีกทีหนึ่ง "
                + "แล้วตรวจยอด → จ่ายใหม่ → นำส่งใหม่";
            return Ok(new ApiResponse<PayrollRunResponse>(true, res, msg));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<PayrollRunResponse>(false, null, ex.Message));
        }
    }

    [HttpPost("runs/{runId:guid}/settle-sso")]
    public async Task<ActionResult<ApiResponse<PayrollRunResponse>>> SettleSso(
        Guid companyId, Guid runId, [FromBody] SettleSsoRequest request)
    {
        var block = await RequirePayrollWriteAsync(companyId, "นำส่งประกันสังคม", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
        return Ok(new ApiResponse<PayrollRunResponse>(true,
            await _service.SettleSocialSecurityAsync(companyId, runId,
                request.PayDate, request.BankAccountId, request.BankGlAccountId, request.FilingNumber,
                User.Identity?.Name ?? "")));
    }

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
        var block = await RequirePayrollWriteAsync(companyId, "แก้แหล่งจ่ายรายคน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        var block = await RequirePayrollWriteAsync(companyId, "แก้ยอดรายคนในรอบ", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        // ── พนักงานเปิดสลิป**ของตัวเอง**ได้เสมอ (ผลตรวจ D-U3) ──
        //
        // เดิมต้องมีสิทธิ์ "ดูข้อมูลเงินเดือน" ซึ่งเป็นสิทธิ์ระดับ HR ⇒ พนักงาน
        // ทั่วไปเปิดสลิปตัวเองไม่ได้เลย ทางเดียวคือรอ HR กดส่งทาง LINE —
        // สลิปเป็นเอกสารที่ลูกจ้างมีสิทธิ์ได้รับตามกฎหมายแรงงาน ไม่ใช่ข้อมูลลับ
        // จากเขา · ส่วนสลิป**ของคนอื่น** ยังต้องมีสิทธิ์ HR เหมือนเดิม
        var actorUserId = JwtHelper.GetUserIdFromClaims(User);
        var isOwnPayslip = actorUserId != Guid.Empty
            && await _service.IsEmployeeOfUserAsync(companyId, employeeId, actorUserId);
        if (!isOwnPayslip)
        {
            var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        }
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
        var block = await RequirePayrollWriteAsync(companyId, "ออกรหัสผูก LINE ให้พนักงาน", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        var block = await RequirePayrollWriteAsync(companyId, "ส่งสลิปทาง LINE", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
        var block = await RequirePayrollWriteAsync(companyId, "ส่งสลิปทาง LINE ทั้งงวด", Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
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
    {
        // ฟอร์มนี้ยื่นใบลา **แทนพนักงานคนใดก็ได้** (request มี EmployeeId) จึงเป็น
        // งานของ HR ไม่ใช่ self-service — เดิมไม่มีด่านเลย
        var block = await RequireAnyAsync(companyId, "ยื่นใบลาแทนพนักงาน", Models.Constants.PermissionKeys.HrAdmin, Models.Constants.PermissionKeys.LeaveApprove, Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return StatusCode(201, new ApiResponse<LeaveResponse>(true, await _service.CreateLeaveAsync(companyId, request)));
    }

    [HttpGet("leaves/{leaveId:guid}")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> GetLeave(Guid companyId, Guid leaveId)
    {
        // ใบลาเฉพาะใบ — ผู้ใช้ได้ id มาจากลิสต์ที่ผ่านด่านแล้ว จึงใช้ด่าน HR ตรง ๆ
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.GetLeaveAsync(companyId, leaveId)));
    }

    [HttpPost("leaves/{leaveId:guid}/approve")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> ApproveLeave(Guid companyId, Guid leaveId)
    {
        var block = await RequireAnyAsync(companyId, "อนุมัติใบลา", Models.Constants.PermissionKeys.LeaveApprove, Models.Constants.PermissionKeys.HrAdmin); if (block != null) return block;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.ApproveLeaveAsync(companyId, leaveId, userId, name)));
    }

    [HttpPost("leaves/{leaveId:guid}/reject")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> RejectLeave(Guid companyId, Guid leaveId, [FromBody] RejectLeaveRequest request)
    {
        var block = await RequireAnyAsync(companyId, "ปฏิเสธใบลา", Models.Constants.PermissionKeys.LeaveReject, Models.Constants.PermissionKeys.LeaveApprove, Models.Constants.PermissionKeys.HrAdmin); if (block != null) return block;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var name = User.Identity?.Name ?? "";
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.RejectLeaveAsync(companyId, leaveId, userId, name, request)));
    }

    [HttpPost("leaves/{leaveId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<LeaveResponse>>> CancelLeave(Guid companyId, Guid leaveId)
    {
        var block = await RequireAnyAsync(companyId, "ยกเลิกใบลา", Models.Constants.PermissionKeys.LeaveApprove, Models.Constants.PermissionKeys.HrAdmin, Models.Constants.PermissionKeys.PayrollRun); if (block != null) return block;
        return Ok(new ApiResponse<LeaveResponse>(true, await _service.CancelLeaveAsync(companyId, leaveId)));
    }

    [HttpGet("leaves")]
    public async Task<ActionResult<ApiResponse<List<LeaveResponse>>>> GetLeaves(Guid companyId, [FromQuery] Guid? employeeId, [FromQuery] int? year)
    {
        // ระบุพนักงาน → ของตัวเองดูได้ · ไม่ระบุ (ดูของทุกคน) → ต้องมีสิทธิ์ HR
        // เดิมไม่มีด่านเลย ⇒ สมาชิกคนไหนก็อ่านวันลาของเพื่อนร่วมงานได้ (D-A2)
        var block = employeeId is { } eid
            ? await RequireOwnOrPayrollAsync(companyId, eid)
            : await CheckPayrollAccessAsync(companyId);
        if (block != null) return block;
        return Ok(new ApiResponse<List<LeaveResponse>>(true, await _service.GetLeavesAsync(companyId, employeeId, year)));
    }

    [HttpGet("leaves/balance")]
    public async Task<ActionResult<ApiResponse<LeaveBalanceResponse>>> GetLeaveBalance(
        Guid companyId, [FromQuery] Guid employeeId, [FromQuery] int? year)
    {
        var block = await RequireOwnOrPayrollAsync(companyId, employeeId); if (block != null) return block;
        return Ok(new ApiResponse<LeaveBalanceResponse>(true,
            await _service.GetLeaveBalanceAsync(companyId, employeeId, year ?? DateTime.UtcNow.Year)));
    }

    [HttpPost("runs/{runId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> VoidRun(Guid companyId, Guid runId)
    {
        // ยกเลิกรอบ = กลับ JE + คืนเงินทดรอง — ทำลายข้อมูลมากกว่าการ "อ่าน"
        // รายละเอียดรอบเสียอีก แต่เดิม**ไม่มีด่านสิทธิ์เลย** ขณะที่ GetRun /
        // UpdateDetail มี (บทเรียน "ด่านที่อ่อนกว่าแต่ทำได้มากกว่า คือช่องที่
        // ใหญ่ที่สุด" — เวลาเพิ่ม endpoint ให้ถามว่าหน้าอื่นที่แตะข้อมูลชุด
        // เดียวกันใช้ด่านอะไร แล้วใช้อย่างน้อยเท่ากัน)
        var block = await RequirePayrollWriteAsync(companyId, "ยกเลิกรอบเงินเดือน", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
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
        var block = await RequirePayrollWriteAsync(companyId, "กลับรายการจ่ายเงินเดือน", Models.Constants.PermissionKeys.PayrollPay); if (block != null) return block;
        try
        {
            var res = await _service.ReopenPaidRunAsync(companyId, runId,
                request.Reason, User.Identity?.Name ?? "");
            // แก้อะไรให้ต้องบอก — ห้ามเปลี่ยนตัวเลขเงียบ ๆ
            var ssoFixed = _service.LastReopenSsoAdjustedCount;
            var msg = "กลับรายการจ่ายแล้ว — รอบกลับไปสถานะ \"อนุมัติแล้ว\" แก้ยอดได้ จากนั้นกด \"จ่าย\" ใหม่";
            // บอกเลขใบที่กลับ — การกลับรายการ **ไม่แก้ใบเดิม** แต่สร้างใบตรงข้าม
            // ⇒ ใบเดิมยังโชว์ยอดเท่าเดิมตลอดไป (เปลี่ยนแค่สถานะเป็น "กลับรายการแล้ว")
            // ถ้าไม่บอก ผู้ใช้จะเปิดใบเดิมแล้วคิดว่ากดปุ่มไปแล้วไม่มีอะไรเกิดขึ้น
            var revJe = _service.LastReversedJournalNumber;
            if (!string.IsNullOrWhiteSpace(revJe))
                msg += $" · กลับรายการบัญชี {revJe} แล้ว (ใบเดิมยังแสดงยอดเท่าเดิม "
                     + "แต่สถานะเปลี่ยนเป็น \"กลับรายการแล้ว\" และมีใบตรงข้ามหักล้างยอดในงบ)";
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

    // `pnd3/{year}/{month}` ถูกลบรอบ 170 — เป็นสูตรที่ 3 ของยอด ภ.ง.ด.3 (นับจาก Documents ไม่กรองฝั่งซื้อ ·
    // ไม่แยก 3/53 · ไม่ตัด PV ซ้ำ) ที่ **ไม่มี UI เรียก** (REGRESSION_ROOT_CAUSE §4 #2) — ตัวตั้งคือ
    // รายงานภาษี (TaxService.GenerateWhtReport จาก 50 ทวิ ที่ออกแล้ว) ที่ /api/tax/reports

    [HttpGet("sso/{year:int}/{month:int}")]
    public async Task<ActionResult<ApiResponse<object>>> GetSso(Guid companyId, int year, int month)
    {
        var block = await CheckPayrollAccessAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<object>(true, await _service.GenerateSsoReportAsync(companyId, year, month)));
    }

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

    /// <remarks><c>EffectiveFromMonth</c>/<c>EffectiveToMonth</c> = ช่วงเดือนที่
    /// อัตรานี้มีผล (ไม่ส่ง = ทั้งปี) — ประกาศลดอัตราสมทบของไทยออกเป็นช่วงเดือน
    /// ⇒ ปีเดียวมีได้หลายแถว แต่ช่วงต้องไม่ทับกัน</remarks>
    public sealed record SsoYearConfigRequest(int Year, decimal WageCeiling,
        decimal RatePercent = 5m, decimal EmployerRatePercent = 5m, string? Notes = null,
        int? EffectiveFromMonth = null, int? EffectiveToMonth = null);

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
        // หนึ่งปีมีได้หลายแถว (ประกาศลดอัตราเป็นช่วงเดือน) ⇒ คืนทุกช่วง
        // ปีที่ไม่มี override เลย คืนแถวเดียว 1–12 = ค่าตามกฎหมาย
        var rows = Enumerable.Range(start, end - start + 1).SelectMany(y =>
        {
            var (defCeiling, defRate) = SsoRateSchedule.GetDefault(y);
            var ovs = overrides.Where(o => o.Year == y)
                .OrderBy(o => o.EffectiveFromMonth).ToList();
            if (ovs.Count == 0)
                return new[] { new
                {
                    Year = y,
                    WageCeiling = defCeiling,
                    RatePercent = defRate * 100m,
                    EmployerRatePercent = defRate * 100m,
                    MaxMonthlyContribution = Math.Round(defCeiling * defRate, 2),
                    EffectiveFromMonth = 1,
                    EffectiveToMonth = 12,
                    IsOverride = false,
                    Notes = (string?)null,
                } };
            return ovs.Select(ov =>
            {
                var rate = ov.RatePercent / 100m;
                var (f, t) = SsoRateSchedule.NormalizeRange(ov.EffectiveFromMonth, ov.EffectiveToMonth);
                return new
                {
                    Year = y,
                    WageCeiling = ov.WageCeiling,
                    RatePercent = rate * 100m,
                    EmployerRatePercent = ov.EmployerRatePercent,
                    MaxMonthlyContribution = Math.Round(ov.WageCeiling * rate, 2),
                    EffectiveFromMonth = f,
                    EffectiveToMonth = t,
                    IsOverride = true,
                    ov.Notes,
                };
            }).ToArray();
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    /// <summary>Upsert the SSO parameters for one year (ปี ค.ศ. — พ.ศ. ถูก
    /// normalize ให้). ใช้เมื่อประกาศ/พรฎ. ฉบับใหม่เปลี่ยนเพดานหรืออัตรา
    /// (รวมกรณีลดอัตราชั่วคราว) โดยไม่ต้องรออัปเดตระบบ.</summary>
    [HttpPut("sso-config")]
    [Accounting.Filters.RejectApiKey("ตั้งค่าอัตราประกันสังคม")]   // W2-C6: ตารางกฎหมายชุดเดียวกับ tax-rule-config
    public async Task<ActionResult<ApiResponse<object>>> UpsertSsoConfig(
        Guid companyId, [FromBody] SsoYearConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ตั้งค่าอัตราประกันสังคม", Models.Constants.PermissionKeys.PayrollApprove); if (block != null) return block;
        var year = req.Year > 2400 ? req.Year - 543 : req.Year;
        if (year < 2000 || year > 2100)
            return BadRequest(new ApiResponse<object>(false, null, "ปีไม่ถูกต้อง"));
        if (req.WageCeiling <= 0 || req.RatePercent <= 0 || req.RatePercent > 30)
            return BadRequest(new ApiResponse<object>(false, null, "เพดานค่าจ้าง/อัตราสมทบไม่ถูกต้อง"));

        var (fromM, toM) = SsoRateSchedule.NormalizeRange(req.EffectiveFromMonth, req.EffectiveToMonth);

        var yearRows = await db.SsoYearConfigs
            .Where(c => c.CompanyId == companyId && c.Year == year && !c.IsDeleted)
            .ToListAsync();
        // แถวเดิมของ "ช่วงเดียวกันเป๊ะ" = แก้ไข · ช่วงใหม่ = เพิ่มแถว
        var existing = yearRows.FirstOrDefault(c =>
            c.EffectiveFromMonth == fromM && c.EffectiveToMonth == toM);
        if (existing == null)
        {
            // ช่วงที่ซ้อนอยู่ข้างในช่วงกว้างกว่าเป็นเรื่องปกติ (อัตราทั้งปี +
            // ประกาศลดชั่วคราวบางเดือน) — ตัวอ่านเลือก "ช่วงที่แคบกว่า" เสมอ
            // ⛔ ที่รับไม่ได้คือทับกันโดย**กว้างเท่ากัน** (เช่น 1–6 กับ 4–9)
            // เพราะไม่มีเกณฑ์ตัดสิน ⇒ ผลจะขึ้นกับลำดับแถว
            var clash = yearRows.FirstOrDefault(c => SsoRateSchedule.RangesAmbiguous(
                c.EffectiveFromMonth, c.EffectiveToMonth, fromM, toM));
            if (clash != null)
                return BadRequest(new ApiResponse<object>(false, null,
                    $"ช่วงเดือน {fromM}–{toM} ทับกับค่าที่ตั้งไว้แล้วแบบตัดสินไม่ได้ "
                    + $"(เดือน {clash.EffectiveFromMonth}–{clash.EffectiveToMonth} ปี {year} กว้างเท่ากัน) — "
                    + "แก้ช่วงเดิมก่อน หรือเลือกช่วงที่ไม่ทับกัน"));

            existing = new Models.Entities.SsoYearConfig
            {
                CompanyId = companyId, Year = year,
                EffectiveFromMonth = fromM, EffectiveToMonth = toM,
            };
            db.SsoYearConfigs.Add(existing);
        }
        existing.WageCeiling = req.WageCeiling;
        existing.RatePercent = req.RatePercent;
        existing.EmployerRatePercent = req.EmployerRatePercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var scope = fromM == 1 && toM == 12 ? "ทั้งปี" : $"เดือน {fromM}–{toM}";
        return Ok(new ApiResponse<object>(true, new
        {
            existing.Year,
            existing.WageCeiling,
            existing.RatePercent,
            existing.EffectiveFromMonth,
            existing.EffectiveToMonth,
            MaxMonthlyContribution = Math.Round(existing.WageCeiling * existing.RatePercent / 100m, 2),
        }, $"บันทึกค่าประกันสังคมปี {year} ({scope}) แล้ว — สมทบสูงสุด {existing.WageCeiling * existing.RatePercent / 100m:N2} บาท/เดือน"));
    }

    /// <summary>Remove a year override — the statutory default takes over.</summary>
    [HttpDelete("sso-config/{year:int}")]
    [Accounting.Filters.RejectApiKey("ลบค่าอัตราประกันสังคม")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSsoConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db,
        [FromQuery] int? fromMonth = null, [FromQuery] int? toMonth = null)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ลบค่าอัตราประกันสังคม", Models.Constants.PermissionKeys.PayrollApprove); if (block != null) return block;
        var y = year > 2400 ? year - 543 : year;
        // ปีเดียวมีได้หลายช่วงเดือน — ระบุช่วงเพื่อลบเฉพาะช่วงนั้น
        // ไม่ระบุ = ลบทั้งปี (พฤติกรรมเดิมตอนที่หนึ่งปีมีได้แถวเดียว)
        var rows = await db.SsoYearConfigs
            .Where(c => c.CompanyId == companyId && c.Year == y && !c.IsDeleted)
            .ToListAsync();
        if (fromMonth.HasValue || toMonth.HasValue)
        {
            var (f, t) = SsoRateSchedule.NormalizeRange(fromMonth, toMonth);
            rows = rows.Where(c => c.EffectiveFromMonth == f && c.EffectiveToMonth == t).ToList();
        }
        if (rows.Count == 0)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบค่าตั้งของปีนี้"));
        foreach (var r in rows)
        {
            r.IsDeleted = true;
            r.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null,
            $"ลบค่าตั้งปี {y} จำนวน {rows.Count} ช่วง แล้ว — กลับไปใช้ตารางตามกฎหมาย"));
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
        // null = ไม่แก้ (คงค่าเดิม · แถวใหม่ = ค่าตามกฎหมาย) — ฝ่ายค้านรอบ 193 W-C6: หน้า payroll ไม่มีช่องของสามค่านี้แต่
        // เคยส่งตัวเลขตายตัว (100,000 / 25,000 / 100,000) ทุกครั้งที่กดบันทึกแถว ⇒ ค่าที่ตั้งผ่าน API ถูกทับเงียบ ๆ ·
        // HealthInsuranceCap/MortgageInterestCap ยังไม่มีผู้อ่าน (ตัวคำนวณ ภ.ง.ด.1 ไม่หักสองรายการนี้ — S-16)
        decimal? Section42TwiCap,
        decimal LifeInsuranceCap,
        decimal? HealthInsuranceCap,
        decimal PvdCap,
        decimal? MortgageInterestCap,
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
        // ตารางขั้น §48(1) + ค่าลดหย่อน §47 อ่านจาก `Helpers/ThaiPitCalculator`
        // ที่เดียว — เดิมพิมพ์ซ้ำที่นี่ (สำเนาที่ 2 ของตารางกฎหมาย) ⇒ หน้าตั้งค่า
        // โชว์เลขหนึ่ง เครื่องคิดภาษีใช้อีกเลขหนึ่งได้โดยไม่มีอะไรฟ้อง
        var defaultBrackets = Accounting.Helpers.ThaiPitCalculator.DefaultBracketsJson();
        var rows = Enumerable.Range(start, end - start + 1).Select(y =>
        {
            var ov = overrides.FirstOrDefault(o => o.FiscalYear == y);
            return new
            {
                FiscalYear = y,
                BracketsJson = ov?.BracketsJson ?? defaultBrackets,
                PersonalAllowance = ov?.PersonalAllowance ?? Accounting.Helpers.ThaiPitCalculator.DefaultPersonalAllowance,
                SpouseAllowance = ov?.SpouseAllowance ?? Accounting.Helpers.ThaiPitCalculator.DefaultSpouseAllowance,
                ChildAllowance = ov?.ChildAllowance ?? Accounting.Helpers.ThaiPitCalculator.DefaultChildAllowance,
                ChildAllowancePost2561 = ov?.ChildAllowancePost2561 ?? Accounting.Helpers.ThaiPitCalculator.DefaultChildAllowancePost2561,
                ParentAllowance = ov?.ParentAllowance ?? Accounting.Helpers.ThaiPitCalculator.DefaultParentAllowance,
                Section42TwiCap = ov?.Section42TwiCap ?? Accounting.Helpers.ThaiPitCalculator.DefaultExpenseCap,
                LifeInsuranceCap = ov?.LifeInsuranceCap ?? Accounting.Helpers.ThaiPitCalculator.DefaultLifeInsuranceCap,
                HealthInsuranceCap = ov?.HealthInsuranceCap ?? Accounting.Helpers.ThaiPitCalculator.DefaultHealthInsuranceCap,
                PvdCap = ov?.PvdCap ?? Accounting.Helpers.ThaiPitCalculator.DefaultPvdCap,
                MortgageInterestCap = ov?.MortgageInterestCap ?? Accounting.Helpers.ThaiPitCalculator.DefaultMortgageInterestCap,
                DonationCapPercent = ov?.DonationCapPercent ?? Accounting.Helpers.ThaiPitCalculator.DefaultDonationCapPercent,
                IsOverride = ov != null,
                ov?.Notes,
            };
        }).ToList();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPut("tax-rule-config")]
    [Accounting.Filters.RejectApiKey("ตั้งค่ากฎภาษีเงินได้")]
    public async Task<ActionResult<ApiResponse<object>>> UpsertTaxRuleConfig(
        Guid companyId, [FromBody] TaxRuleConfigRequest req,
        [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ตั้งค่ากฎภาษีเงินได้", Models.Constants.PermissionKeys.PayrollApprove); if (block != null) return block;
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
        if (req.Section42TwiCap.HasValue) existing.Section42TwiCap = req.Section42TwiCap.Value;
        existing.LifeInsuranceCap = req.LifeInsuranceCap;
        if (req.HealthInsuranceCap.HasValue) existing.HealthInsuranceCap = req.HealthInsuranceCap.Value;
        existing.PvdCap = req.PvdCap;
        if (req.MortgageInterestCap.HasValue) existing.MortgageInterestCap = req.MortgageInterestCap.Value;
        existing.DonationCapPercent = req.DonationCapPercent;
        existing.Notes = req.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { existing.FiscalYear, existing.PersonalAllowance, existing.PvdCap },
            $"บันทึกกฎภาษี ปี {year} แล้ว"));
    }

    [HttpDelete("tax-rule-config/{year:int}")]
    [Accounting.Filters.RejectApiKey("ลบกฎภาษีเงินได้")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTaxRuleConfig(
        Guid companyId, int year, [FromServices] Accounting.Data.AccountingDbContext db)
    {
        var block = await RequirePayrollWriteAsync(companyId, "ลบกฎภาษีเงินได้", Models.Constants.PermissionKeys.PayrollApprove); if (block != null) return block;
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
