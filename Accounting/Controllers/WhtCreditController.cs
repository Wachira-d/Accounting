using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// ทะเบียน "ภาษีที่เราถูกหัก ณ ที่จ่าย" → เครดิต ภ.ง.ด.51/50
/// (คนละชุดกับ <c>WithholdingTaxCertController</c> ซึ่งเป็นใบที่ <b>เราออกให้ผู้อื่น</b>)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/wht-credits")]
[Authorize]
public class WhtCreditController : ControllerBase
{
    private readonly WhtCreditService _service;
    public WhtCreditController(WhtCreditService service) => _service = service;

    public record UpsertBody(
        int TaxYear, string? CertificateNumber, DateTime? CertificateDate,
        Guid? PayerContactId, string? PayerName, string? PayerTaxId,
        WhtPayerFormType PayerFormType, string? IncomeTypeCode,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        Guid? DocumentId, Guid? AttachmentId, string? Notes);

    public record MarkReceivedBody(string CertificateNumber, DateTime? CertificateDate, Guid? AttachmentId);
    public record ExpireBody(string? Reason);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<WhtCreditService.WhtCreditRow>>>> List(
        Guid companyId, [FromQuery] int? taxYear = null, [FromQuery] WhtCreditStatus? status = null)
        => Ok(new ApiResponse<List<WhtCreditService.WhtCreditRow>>(true,
            await _service.ListAsync(companyId, taxYear, status)));

    /// <summary>สรุปยอดต่อปีภาษี + กระทบกับยอดบัญชี 11910 จริง</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<WhtCreditService.WhtCreditSummary>>> Summary(
        Guid companyId, [FromQuery] int taxYear)
        => Ok(new ApiResponse<WhtCreditService.WhtCreditSummary>(true,
            await _service.SummaryAsync(companyId, taxYear)));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create(Guid companyId, [FromBody] UpsertBody b)
    {
        var id = await _service.CreateAsync(companyId, Map(b));
        return StatusCode(201, new ApiResponse<Guid>(true, id, "บันทึกรายการภาษีถูกหักแล้ว"));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Update(Guid companyId, Guid id, [FromBody] UpsertBody b)
    {
        await _service.UpdateAsync(companyId, id, Map(b));
        return Ok(new ApiResponse<bool>(true, true, "แก้ไขแล้ว"));
    }

    /// <summary>ได้รับหนังสือรับรองแล้ว — ระบุเลขที่ + แนบสแกน → พร้อมใช้เครดิต</summary>
    [HttpPost("{id:guid}/mark-received")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkReceived(
        Guid companyId, Guid id, [FromBody] MarkReceivedBody b)
    {
        await _service.MarkReceivedAsync(companyId, id, b.CertificateNumber, b.CertificateDate, b.AttachmentId);
        return Ok(new ApiResponse<bool>(true, true, "บันทึกหนังสือรับรองแล้ว — ใช้เป็นเครดิตได้"));
    }

    [HttpPost("{id:guid}/expire")]
    public async Task<ActionResult<ApiResponse<bool>>> Expire(Guid companyId, Guid id, [FromBody] ExpireBody b)
    {
        await _service.ExpireAsync(companyId, id, b.Reason);
        return Ok(new ApiResponse<bool>(true, true, "ตัดรายการออกจากเครดิตแล้ว"));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid id)
    {
        await _service.DeleteAsync(companyId, id);
        return Ok(new ApiResponse<bool>(true, true, "ลบแล้ว"));
    }

    public record SettleBody(int TaxYear, decimal UsedAgainstCit, decimal RefundRequested,
        decimal WriteOff, string? Reason);

    /// <summary>ปิดปีภาษี — ลง JE ล้างยอดเครดิตออกจากบัญชี 11910 ตามที่ใช้จริง
    /// (ใช้หักภาษี / ขอคืน / ตัดสูญ) · ยกไปปีหน้าไม่ต้องเรียก (คงยอดไว้เฉย ๆ)</summary>
    [HttpPost("settle-year-end")]
    public async Task<ActionResult<ApiResponse<string>>> SettleYearEnd(
        Guid companyId, [FromBody] SettleBody b)
    {
        var actor = User?.Identity?.Name ?? "system";
        var je = await _service.SettleYearEndAsync(companyId,
            new WhtCreditService.SettleRequest(b.TaxYear, b.UsedAgainstCit, b.RefundRequested,
                b.WriteOff, b.Reason), actor);
        return Ok(new ApiResponse<string>(true, je, $"ลง JE ปิดปีแล้ว ({je})"));
    }

    private static WhtCreditService.UpsertRequest Map(UpsertBody b) => new(
        b.TaxYear, b.CertificateNumber, b.CertificateDate, b.PayerContactId, b.PayerName,
        b.PayerTaxId, b.PayerFormType, b.IncomeTypeCode, b.IncomeAmount, b.WhtRate,
        b.WhtAmount, b.DocumentId, b.AttachmentId, b.Notes);
}
