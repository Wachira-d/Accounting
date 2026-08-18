using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ai;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;

namespace Accounting.Controllers;

/// <summary>
/// **รายงานการใช้งาน AI ระดับแพลตฟอร์ม** — เฉพาะ SystemAdmin
///
/// ตอบคำถามที่ธุรกิจถามจริง: ลูกค้ารายไหนใช้ AI เท่าไร ผ่านหน้าเว็บหรือผ่าน
/// API เสียเงินไปเท่าไร คุ้มไหม และ local model โตพอจะลดการเรียกลงหรือยัง
///
/// <para><b>PDPA</b>: คืนเฉพาะ metadata (ฟีเจอร์/จำนวน/ต้นทุน/คำตอบที่เลือก)
/// — **ห้ามคืน PromptJson/ResponseJson** ซึ่งมีข้อมูลเอกสารของลูกค้าอยู่
/// (ยึดกติกาเดียวกับ MeteringController)</para>
/// </summary>
[ApiController]
[Route("api/admin/ai-usage")]
[Authorize(Roles = "SystemAdmin")]
public class AiUsageReportController : ControllerBase
{
    private readonly AiUsageReportService _report;

    public AiUsageReportController(AiUsageReportService report) => _report = report;

    /// <summary>สรุปทั้งแพลตฟอร์ม + ตารางลูกค้าทุกราย</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<AiUsageSummaryResponse>>> GetSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] AiUsageChannel? channel = null,
        [FromQuery] Guid? billingAccountId = null,
        [FromQuery] bool includeSandbox = false,
        [FromQuery] decimal usdToThb = 36.5m,
        CancellationToken ct = default)
    {
        var (f, t) = DefaultRange(from, to);
        var data = await _report.GetSummaryAsync(f, t, channel, billingAccountId,
            restrictToCompanyIds: null, includeSandbox, usdToThb, ct);
        return Ok(new ApiResponse<AiUsageSummaryResponse>(true, data));
    }

    /// <summary>เจาะลึกลูกค้ารายเดียว — ช่องทาง/ฟีเจอร์/คีย์ API/แนวโน้ม/ตัวอย่าง call</summary>
    [HttpGet("customers/{companyId:guid}")]
    public async Task<ActionResult<ApiResponse<AiUsageCustomerDetailResponse>>> GetCustomer(
        Guid companyId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool includeSandbox = false,
        [FromQuery] decimal usdToThb = 36.5m,
        [FromQuery] int recentCalls = 50,
        CancellationToken ct = default)
    {
        var (f, t) = DefaultRange(from, to);
        var data = await _report.GetCustomerDetailAsync(
            companyId, f, t, includeSandbox, usdToThb, recentCalls, ct);
        return Ok(new ApiResponse<AiUsageCustomerDetailResponse>(true, data));
    }

    /// <summary>ตารางลูกค้าเป็น Excel — ทีมบัญชี/ขายเอาไปทำบิลหรือคุยกับลูกค้าต่อ</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] AiUsageChannel? channel = null,
        [FromQuery] Guid? billingAccountId = null,
        [FromQuery] bool includeSandbox = false,
        [FromQuery] decimal usdToThb = 36.5m,
        CancellationToken ct = default)
    {
        var (f, t) = DefaultRange(from, to);
        var data = await _report.GetSummaryAsync(f, t, channel, billingAccountId,
            restrictToCompanyIds: null, includeSandbox, usdToThb, ct);

        var sheets = new Dictionary<string, object>
        {
            ["ลูกค้า"] = data.Customers.Select(c => new
            {
                บริษัท = c.CompanyName,
                เลขผู้เสียภาษี = c.TaxId ?? "",
                กลุ่มบิล = c.BillingAccountName ?? "",
                ประเภทการใช้งาน = c.CustomerKind,
                จำนวนคีย์API = c.ApiClientCount,
                เรียกทั้งหมด = c.Totals.CallsTotal,
                ถึงAIจริง = c.Totals.CallsAi,
                จากแคช = c.Totals.CallsCached,
                localตอบเอง = c.Totals.CallsLocalServed,
                ล้มเหลว = c.Totals.CallsFailed,
                ถูกงบสกัด = c.Totals.CallsBudgetBlocked,
                อัตราใช้AI = c.Totals.AiUsageRate,
                อัตราตอบเอง = c.Totals.SelfServedRate,
                อัตรายอมรับ = c.Totals.AcceptanceRate,
                โทเคนเข้า = c.Totals.InputTokens,
                โทเคนออก = c.Totals.OutputTokens,
                ต้นทุนUSD = c.Totals.CostUsd,
                ต้นทุนบาท = c.Totals.CostThb,
                หน่วงเฉลี่ยms = c.Totals.AvgLatencyMs,
                ฟีเจอร์ที่ใช้มากสุด = c.TopFeature ?? "",
                ใช้ล่าสุด = c.LastUsedAt?.ToString("yyyy-MM-dd") ?? "",
            }).ToList(),
            ["ช่องทาง"] = data.Channels.Select(x => new
            {
                ช่องทาง = x.ChannelLabel,
                เรียกทั้งหมด = x.Totals.CallsTotal,
                ถึงAIจริง = x.Totals.CallsAi,
                localตอบเอง = x.Totals.CallsLocalServed,
                ต้นทุนUSD = x.Totals.CostUsd,
                ต้นทุนบาท = x.Totals.CostThb,
            }).ToList(),
            ["ฟีเจอร์"] = data.Features.Select(x => new
            {
                ฟีเจอร์ = x.FeatureLabel,
                รหัส = x.FeatureKey,
                เรียกทั้งหมด = x.Totals.CallsTotal,
                ถึงAIจริง = x.Totals.CallsAi,
                อัตราใช้AI = x.Totals.AiUsageRate,
                อัตรายอมรับ = x.Totals.AcceptanceRate,
                ต้นทุนบาท = x.Totals.CostThb,
            }).ToList(),
            ["รายวัน"] = data.Daily.Select(x => new
            {
                วันที่ = x.Date.ToString("yyyy-MM-dd"),
                เรียกทั้งหมด = x.CallsTotal,
                ถึงAIจริง = x.CallsAi,
                localตอบเอง = x.CallsLocalServed,
                จากแคช = x.CallsCached,
                ต้นทุนบาท = x.CostThb,
            }).ToList(),
        };

        using var ms = new MemoryStream();
        ms.SaveAs(sheets);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"AiUsage_{f:yyyyMMdd}-{t:yyyyMMdd}.xlsx");
    }

    /// <summary>ช่วงเริ่มต้น = 30 วันล่าสุด เมื่อ UI ไม่ส่งมา</summary>
    internal static (DateTime From, DateTime To) DefaultRange(DateTime? from, DateTime? to)
    {
        var t = (to ?? DateTime.UtcNow).Date;
        var f = (from ?? t.AddDays(-29)).Date;
        return (f, t);
    }
}

/// <summary>
/// **ฝั่งลูกค้า — ดูการใช้ AI ของบริษัทตัวเอง**
///
/// แยก controller ออกจากฝั่งแอดมินโดยตั้งใจ: route ผูก <c>companyId</c> ทำให้
/// <c>TenantAccessMiddleware</c> กันข้ามบริษัทให้อัตโนมัติเหมือน endpoint อื่น
/// ถ้ายัดรวมกับฝั่งแอดมินแล้วสลับ policy ตาม role จะพลาดง่ายมาก
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/ai-usage")]
[Authorize]
public class CompanyAiUsageController : ControllerBase
{
    private readonly AiUsageReportService _report;
    private readonly AccountingDbContext _db;

    public CompanyAiUsageController(AiUsageReportService report, AccountingDbContext db)
    { _report = report; _db = db; }

    /// <summary>สรุปการใช้ AI ของบริษัทนี้ (ทุกช่องทาง รวมคีย์ API ของตัวเอง)</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AiUsageCustomerDetailResponse>>> Get(
        Guid companyId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool includeSandbox = true,
        [FromQuery] decimal usdToThb = 36.5m,
        CancellationToken ct = default)
    {
        var (f, t) = AiUsageReportController.DefaultRange(from, to);
        // ลูกค้าเห็นของตัวเองเท่านั้น — recentCalls จำกัดไว้ 20 เพราะฝั่งลูกค้า
        // ต้องการภาพรวม ไม่ใช่เครื่องมือสืบสวนแบบฝั่งแอดมิน
        var data = await _report.GetCustomerDetailAsync(
            companyId, f, t, includeSandbox, usdToThb, recentCallLimit: 20, ct);
        return Ok(new ApiResponse<AiUsageCustomerDetailResponse>(true, data));
    }

    /// <summary>ยอดรวมทั้ง "กลุ่มบิล" ของบริษัทนี้ — สำหรับเครือที่มีหลายนิติบุคคล
    /// ผู้ดูแลกลุ่มอยากเห็นภาพรวมทั้งเครือในหน้าเดียว</summary>
    [HttpGet("group")]
    public async Task<ActionResult<ApiResponse<AiUsageSummaryResponse>>> GetGroup(
        Guid companyId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool includeSandbox = true,
        [FromQuery] decimal usdToThb = 36.5m,
        CancellationToken ct = default)
    {
        var (f, t) = AiUsageReportController.DefaultRange(from, to);
        var billingAccountId = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId).Select(c => c.BillingAccountId)
            .FirstOrDefaultAsync(ct);

        // ไม่มีกลุ่ม = ยังไม่ได้ผูกกลุ่มบิล → คืนเฉพาะบริษัทตัวเอง (ไม่ใช่ทั้งฐาน)
        var scope = billingAccountId.HasValue
            ? await _db.Companies.AsNoTracking()
                .Where(c => c.BillingAccountId == billingAccountId.Value)
                .Select(c => c.Id).ToListAsync(ct)
            : new List<Guid> { companyId };

        var data = await _report.GetSummaryAsync(f, t,
            channel: null, billingAccountId: billingAccountId,
            restrictToCompanyIds: scope, includeSandbox, usdToThb, ct);
        return Ok(new ApiResponse<AiUsageSummaryResponse>(true, data));
    }
}
