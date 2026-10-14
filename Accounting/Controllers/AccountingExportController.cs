using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ส่งออกรายงานบัญชีเป็น CSV / Excel (2026-10-14 ทีม X3) — ทุก endpoint อ่านอย่างเดียว ·
/// ตัวเลขมาจาก service ตัวเดียวกับหน้า (<see cref="AccountingReportExportService"/>) · ตัวกรองชื่อเดียวกับ endpoint ข้อมูลของหน้า ·
/// ด่าน: ล็อกอิน + เป็นสมาชิกบริษัท (tenant guard กลาง) + สิทธิ์ "ส่งออกรายงาน" (<see cref="PermissionKeys.ReportsExport"/>)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/accounting/exports")]
[Authorize]
[RequirePermission(PermissionKeys.ReportsExport)]
public class AccountingExportController : ControllerBase
{
    private readonly AccountingReportExportService _export;

    public AccountingExportController(AccountingReportExportService export) => _export = export;

    [HttpGet("trial-balance")]
    public async Task<IActionResult> TrialBalance(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? asOfDate = null, [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        var asOf = asOfDate ?? ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var from = fromDate ?? new DateTime(asOf.Year, asOf.Month, 1);
        return FileOf(await _export.TrialBalanceAsync(companyId, from, asOf, projectId, branchId, dimensionId, fmt));
    }

    [HttpGet("general-ledger")]
    public async Task<IActionResult> GeneralLedger(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] Guid? accountId = null, [FromQuery] string? accountIds = null, [FromQuery] string? accountType = null,
        [FromQuery] Guid? dimensionId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? projectId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        AccountType? type = null;
        if (!string.IsNullOrWhiteSpace(accountType))
        {
            if (!Enum.TryParse<AccountType>(accountType, true, out var t)) return BadRequest(new ApiResponse<object>(false, null, "ประเภทบัญชีไม่ถูกต้อง"));
            type = t;
        }
        return FileOf(await _export.GeneralLedgerAsync(companyId, fromDate, toDate, accountId, GuidList(accountIds), type,
            dimensionId, branchId, projectId, fmt));
    }

    [HttpGet("balance-sheet")]
    public async Task<IActionResult> BalanceSheet(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime? asOfDate = null,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.BalanceSheetAsync(companyId, asOfDate ?? ThaiDate.CalendarDateUtc(DateTime.UtcNow), projectId, branchId, dimensionId, fmt));
    }

    [HttpGet("profit-loss")]
    public async Task<IActionResult> ProfitLoss(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.ProfitLossAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId, fmt));
    }

    [HttpGet("cash-flow")]
    public async Task<IActionResult> CashFlow(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime fromDate, [FromQuery] DateTime toDate,
        [FromQuery] Guid? projectId = null, [FromQuery] Guid? branchId = null, [FromQuery] Guid? dimensionId = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.CashFlowAsync(companyId, fromDate, toDate, projectId, branchId, dimensionId, fmt));
    }

    /// <summary>สมุดรายวัน — ตัวกรองชุดเดียวกับ <c>GET accounting/journals</c> (ไม่มี page/pageSize: ส่งออกทุกหน้า) ·
    /// <c>entryIds</c> (คั่นด้วยจุลภาค) = เฉพาะใบที่เลือก</summary>
    [HttpGet("journals")]
    public async Task<IActionResult> Journals(Guid companyId, [FromQuery] string? format, [FromQuery] string? search = null,
        [FromQuery] string? status = null, [FromQuery] DateTime? fromDate = null, [FromQuery] DateTime? toDate = null,
        [FromQuery] string? journalType = null, [FromQuery] Guid? dimensionId = null, [FromQuery] Guid? branchId = null,
        [FromQuery] Guid? projectId = null, [FromQuery] string? tag = null, [FromQuery] string? entryIds = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        var userId = JwtHelper.GetUserIdFromClaims(User);
        return FileOf(await _export.JournalsAsync(companyId, userId, search, status, fromDate, toDate, journalType,
            dimensionId, branchId, projectId, tag, GuidList(entryIds), fmt));
    }

    /// <summary>อายุลูกหนี้ (<c>side=receivables</c>) / เจ้าหนี้ (<c>side=payables</c>) — ตัวกรองเดียวกับหน้า aging ·
    /// CSV: <c>level=document</c> = รายเอกสาร · ค่าอื่น = รายผู้ติดต่อ · Excel มีทั้งสองแผ่น</summary>
    [HttpGet("aging")]
    public async Task<IActionResult> Aging(Guid companyId, [FromQuery] string? format, [FromQuery] string? side,
        [FromQuery] DateTime? asOfDate = null, [FromQuery] Guid? contactId = null, [FromQuery] Guid? projectId = null, [FromQuery] string? level = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        var s = (side ?? "").Trim().ToLowerInvariant();
        if (s is not ("receivables" or "payables"))
            return BadRequest(new ApiResponse<object>(false, null, "side ต้องเป็น receivables หรือ payables"));
        return FileOf(await _export.AgingAsync(companyId, s == "payables", asOfDate, contactId, projectId,
            string.Equals(level, "document", StringComparison.OrdinalIgnoreCase), fmt));
    }

    /// <summary>บันทึกการใช้งาน — ตัวกรองเดียวกับ <c>GET audit/logs</c> (ทุกหน้า)</summary>
    [HttpGet("audit-logs")]
    public async Task<IActionResult> AuditLogs(Guid companyId, [FromQuery] string? format, [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null, [FromQuery] AuditAction? action = null, [FromQuery] string? entityType = null)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.AuditLogsAsync(companyId, fromDate, toDate, action, entityType, fmt));
    }

    [HttpGet("fixed-assets/register")]
    public async Task<IActionResult> AssetRegister(Guid companyId, [FromQuery] string? format)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.AssetRegisterAsync(companyId, fmt));
    }

    [HttpGet("fixed-assets/{assetId:guid}/depreciation-schedule")]
    public async Task<IActionResult> DepreciationSchedule(Guid companyId, Guid assetId, [FromQuery] string? format)
    {
        if (!TabularExport.TryParseFormat(format, out var fmt)) return BadFormat();
        return FileOf(await _export.DepreciationScheduleAsync(companyId, assetId, fmt));
    }

    private IActionResult FileOf(TabularFile f) => File(f.Content, f.ContentType, f.FileName);

    private IActionResult BadFormat()
        => BadRequest(new ApiResponse<object>(false, null, "รูปแบบไฟล์ต้องเป็น csv หรือ xlsx"));

    /// <summary>รายการ id คั่นด้วยจุลภาค · ว่าง = null (ไม่กรอง) · มีตัวที่อ่านไม่ออก = 400
    /// (ข้ามเงียบไม่ได้ — ถ้าทุกตัวพังแล้วกลายเป็น "ไม่กรอง" ผู้ใช้ที่เลือก 3 ใบจะได้ไฟล์ทั้งบริษัท)</summary>
    private static List<Guid>? GuidList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var ids = new List<Guid>();
        foreach (var s in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(s, out var g)) throw new BusinessRuleException("รายการที่เลือกมีรหัสที่อ่านไม่ออก — เลือกใหม่แล้วลองอีกครั้ง");
            ids.Add(g);
        }
        return ids.Count == 0 ? null : ids.Distinct().ToList();
    }
}
