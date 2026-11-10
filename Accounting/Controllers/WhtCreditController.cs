using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
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
    private readonly IPermissionService _permissions;
    public WhtCreditController(WhtCreditService service, IPermissionService permissions)
    { _service = service; _permissions = permissions; }

    /// <summary>ผู้บันทึก + ถือ Tax.File ไหม — service ใช้ตัดสินการผูกไฟล์ในถังก่อนบันทึก (ฝ่ายค้านรอบ 193 · S2-P7)</summary>
    private async Task<WhtCreditService.AttachmentActor> ActorAsync(Guid companyId)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        return new(uid, await _permissions.HasPermissionAsync(companyId, uid, PermissionKeys.TaxFile));
    }

    public record UpsertBody(
        int TaxYear, string? CertificateNumber, DateTime? CertificateDate,
        Guid? PayerContactId, string? PayerName, string? PayerTaxId,
        WhtPayerFormType PayerFormType, string? IncomeTypeCode,
        decimal IncomeAmount, decimal WhtRate, decimal WhtAmount,
        Guid? DocumentId, Guid? AttachmentId, string? Notes);

    public record MarkReceivedBody(string CertificateNumber, DateTime? CertificateDate, Guid? AttachmentId);
    /// <summary>รอบ 243a: ตัดสูญ = ลงบัญชี (Dr 58010 / Cr 11910) — เหตุผล ≥ 10 ตัวอักษร · วันที่ (ว่าง = วันนี้) ต้องอยู่ในงวดที่เปิด</summary>
    public record ExpireBody(string? Reason, DateTime? WriteOffDate = null);

    /// <summary>ผู้ลงบัญชี — ชื่อ/อีเมลจาก JWT (CreatedBy ของ JE + audit)</summary>
    private WhtCreditService.WriteOffActor JournalActor()
        => new(JwtHelper.GetUserIdFromClaims(User), User?.Identity?.Name ?? "user");

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<WhtCreditService.WhtCreditRow>>>> List(
        Guid companyId, [FromQuery] int? taxYear = null, [FromQuery] WhtCreditStatus? status = null)
        => Ok(new ApiResponse<List<WhtCreditService.WhtCreditRow>>(true,
            await _service.ListAsync(companyId, taxYear, status)));

    /// <summary>ตรวจย้อนหลัง: ใบที่ปิดยอดแล้วแต่ WHT บันทึกไม่ครบ → เหลือลูกหนี้
    /// ค้างใน GL + เสียเครดิตภาษี (เกิดจากใบเสร็จตัดลูกหนี้ด้วยยอดเงินที่รับจริง
    /// แทนยอดก่อนหักภาษี — มี guard กันตอนอนุมัติแล้ว แต่ใบเก่าต้องตามเก็บ)</summary>
    [HttpGet("stranded-ar")]
    public async Task<ActionResult<ApiResponse<List<WhtCreditService.StrandedArRow>>>> StrandedAr(Guid companyId)
        => Ok(new ApiResponse<List<WhtCreditService.StrandedArRow>>(true,
            await _service.FindStrandedArFromMissingWhtAsync(companyId)));

    /// <summary>สรุปยอดต่อปีภาษี + กระทบกับยอดบัญชี 11910 จริง</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<WhtCreditService.WhtCreditSummary>>> Summary(
        Guid companyId, [FromQuery] int taxYear)
        => Ok(new ApiResponse<WhtCreditService.WhtCreditSummary>(true,
            await _service.SummaryAsync(companyId, taxYear)));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> Create(Guid companyId, [FromBody] UpsertBody b)
    {
        var id = await _service.CreateAsync(companyId, Map(b), await ActorAsync(companyId));
        return StatusCode(201, new ApiResponse<Guid>(true, id, "บันทึกรายการภาษีถูกหักแล้ว"));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Update(Guid companyId, Guid id, [FromBody] UpsertBody b)
    {
        await _service.UpdateAsync(companyId, id, Map(b), await ActorAsync(companyId));
        return Ok(new ApiResponse<bool>(true, true, "แก้ไขแล้ว"));
    }

    /// <summary>ได้รับหนังสือรับรองแล้ว — ระบุเลขที่ + แนบสแกน → พร้อมใช้เครดิต</summary>
    [HttpPost("{id:guid}/mark-received")]
    public async Task<ActionResult<ApiResponse<bool>>> MarkReceived(
        Guid companyId, Guid id, [FromBody] MarkReceivedBody b)
    {
        // รอบ 243a: แถวที่ตัดสูญพร้อม JE ⇒ service กลับรายการ JE ให้ — ส่งผู้ลงบัญชีเฉพาะผู้ถือ Journal.Manage (ไม่ถือ = service ปฏิเสธ 403 พร้อมเหตุ)
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var journalActor = await _permissions.HasPermissionAsync(companyId, uid, PermissionKeys.JournalManage) ? JournalActor() : null;
        await _service.MarkReceivedAsync(companyId, id, b.CertificateNumber, b.CertificateDate, b.AttachmentId,
            await ActorAsync(companyId), journalActor);
        return Ok(new ApiResponse<bool>(true, true, "บันทึกหนังสือรับรองแล้ว — ใช้เป็นเครดิตได้"));
    }

    /// <summary>รอบ 257 ชุด 2 (B-M3): ผู้จ่ายหักผ่าน e-WHT · รอหลักฐาน (true) / ยกเลิกการแจ้ง (false) — ไม่เพิ่มเครดิต (ป้าย + ตัวกรอง "e-WHT")</summary>
    public record EWhtBody(bool EWithholding);

    [HttpPost("{id:guid}/ewht-pending")]
    [RequirePermission(PermissionKeys.TaxFile)]
    public async Task<ActionResult<ApiResponse<bool>>> EWhtPending(Guid companyId, Guid id, [FromBody] EWhtBody b)
    {
        await _service.SetEWhtPendingAsync(companyId, id, b.EWithholding, JwtHelper.GetUserIdFromClaims(User), User?.Identity?.Name ?? "user");
        return Ok(new ApiResponse<bool>(true, true, b.EWithholding
            ? "บันทึกแล้ว — ผู้จ่ายหักผ่าน e-WHT (รอหลักฐาน · ยังไม่เป็นเครดิตจนกว่าจะแนบหลักฐานเข้ากล่องรับ)"
            : "ยกเลิกการแจ้ง e-WHT แล้ว — รายการกลับไปรอ 50 ทวิ"));
    }

    /// <summary>ตัดสูญ (รอบ 243a · DESIGN G-9) — ลง JE Dr 58010 / Cr 11910 ทั้งยอดของแถว · สิทธิ์ลงใบสำคัญ (Journal.Manage) · กดซ้ำ = ผลเดิม</summary>
    [HttpPost("{id:guid}/expire")]
    [RequirePermission(PermissionKeys.JournalManage)]
    public async Task<ActionResult<ApiResponse<WhtCreditService.WriteOffResult>>> Expire(Guid companyId, Guid id, [FromBody] ExpireBody b)
    {
        var r = await _service.ExpireAsync(companyId, id, b.Reason, b.WriteOffDate, JournalActor());
        return Ok(new ApiResponse<WhtCreditService.WriteOffResult>(true, r,
            r.AlreadyWrittenOff ? $"รายการนี้ตัดสูญไว้แล้ว ({r.EntryNumber})" : $"ตัดสูญแล้ว — ลงบัญชี {r.EntryNumber} ยอด {r.Amount:N2}"));
    }

    /// <summary>ข้อมูลหน้ายืนยันตัดสูญ: ยอด · ผัง Dr/Cr · ผลทางภาษี · เหตุที่ทำไม่ได้ (อ่านอย่างเดียว)</summary>
    [HttpGet("{id:guid}/write-off-preview")]
    public async Task<ActionResult<ApiResponse<WhtCreditService.WriteOffPreview>>> WriteOffPreview(
        Guid companyId, Guid id, [FromQuery] DateTime? date = null)
        => Ok(new ApiResponse<WhtCreditService.WriteOffPreview>(true, await _service.WriteOffPreviewAsync(companyId, id, date)));

    /// <summary>คำเตือนผัง/JE ที่กระทบ ภ.ง.ด.50 (อ่านอย่างเดียว · รอบ 243a N1/N2): ผัง 58000–58099 นอกกลุ่ม 580 · ใบปิดปีรุ่นเก่าที่ตัดสูญลงผังหักได้</summary>
    [HttpGet("account-warnings")]
    public async Task<ActionResult<ApiResponse<List<WhtCreditService.AccountWarning>>>> AccountWarnings(Guid companyId)
        => Ok(new ApiResponse<List<WhtCreditService.AccountWarning>>(true, await _service.AccountWarningsAsync(companyId)));

    /// <summary>สิ้นรอบ: รายการที่ยังรอใบในรอบบัญชีที่สิ้นสุดแล้ว — ให้ผู้ทำบัญชีเลือกตามต่อหรือตัดสูญ (ไม่ตัดอัตโนมัติ)</summary>
    [HttpGet("write-off-candidates")]
    public async Task<ActionResult<ApiResponse<List<WhtCreditService.WriteOffCandidate>>>> WriteOffCandidates(Guid companyId)
        => Ok(new ApiResponse<List<WhtCreditService.WriteOffCandidate>>(true, await _service.WriteOffCandidatesAsync(companyId)));

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid companyId, Guid id)
    {
        await _service.DeleteAsync(companyId, id);
        return Ok(new ApiResponse<bool>(true, true, "ลบแล้ว"));
    }

    /// <summary>รอบ 243a (ฝ่ายค้าน S1): ช่องตัดสูญต้องระบุรายการ (<c>WriteOffCreditIds</c>) — แต่ละรายการเดินเส้นตัดสูญรายแถว</summary>
    public record SettleBody(int TaxYear, decimal UsedAgainstCit, decimal RefundRequested,
        decimal WriteOff, string? Reason, List<Guid>? WriteOffCreditIds = null);

    /// <summary>ปิดปีภาษี — ลง JE ล้างยอดเครดิตออกจากบัญชี 11910 ตามที่ใช้จริง
    /// (ใช้หักภาษี / ขอคืน / ตัดสูญ) · ยกไปปีหน้าไม่ต้องเรียก (คงยอดไว้เฉย ๆ)</summary>
    [HttpPost("settle-year-end")]
    [RequirePermission(PermissionKeys.JournalManage)]   // รอบ 243a (ฝ่ายค้าน S2): ลง JE ล้าง 11910 = สิทธิ์ลงใบสำคัญ
    public async Task<ActionResult<ApiResponse<string>>> SettleYearEnd(
        Guid companyId, [FromBody] SettleBody b)
    {
        var who = JournalActor();
        var je = await _service.SettleYearEndAsync(companyId,
            new WhtCreditService.SettleRequest(b.TaxYear, b.UsedAgainstCit, b.RefundRequested,
                b.WriteOff, b.Reason, b.WriteOffCreditIds), who.Name, who.UserId);
        return Ok(new ApiResponse<string>(true, je, $"ลง JE ปิดปีแล้ว ({je})"));
    }

    private static WhtCreditService.UpsertRequest Map(UpsertBody b) => new(
        b.TaxYear, b.CertificateNumber, b.CertificateDate, b.PayerContactId, b.PayerName,
        b.PayerTaxId, b.PayerFormType, b.IncomeTypeCode, b.IncomeAmount, b.WhtRate,
        b.WhtAmount, b.DocumentId, b.AttachmentId, b.Notes);
}
