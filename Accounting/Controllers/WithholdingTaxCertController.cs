using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/withholding-tax-certs")]
[Authorize]
public class WithholdingTaxCertController : ControllerBase
{
    private readonly IWithholdingTaxCertService _whtService;
    private readonly AccountingDbContext _db;

    public WithholdingTaxCertController(IWithholdingTaxCertService whtService, AccountingDbContext db)
    {
        _whtService = whtService;
        _db = db;
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> Create(
        Guid companyId, [FromBody] CreateWithholdingTaxCertRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _whtService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<WithholdingTaxCertResponse>(true, result, "สร้างหนังสือรับรองหัก ณ ที่จ่ายสำเร็จ"));
    }

    [HttpGet("{certId:guid}")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> GetById(Guid companyId, Guid certId)
    {
        var result = await _whtService.GetByIdAsync(companyId, certId);
        return Ok(new ApiResponse<WithholdingTaxCertResponse>(true, result));
    }

    /// <summary>แก้ไขหนังสือรับรอง — เฉพาะ Draft + สร้างเอง (ไม่อ้างอิง
    /// ใบสำคัญจ่าย/payroll). Service throw ถ้าไม่เข้าเงื่อนไข.</summary>
    [HttpPut("{certId:guid}")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> Update(
        Guid companyId, Guid certId, [FromBody] CreateWithholdingTaxCertRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _whtService.UpdateAsync(companyId, certId, request, userId);
        return Ok(new ApiResponse<WithholdingTaxCertResponse>(true, result, "แก้ไขหนังสือรับรองสำเร็จ"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<WithholdingTaxCertResponse>>>> GetAll(
        Guid companyId,
        [FromQuery] TaxType? taxFormType, [FromQuery] int? year, [FromQuery] int? month,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _whtService.GetAllAsync(companyId, taxFormType, year, month, new PagedRequest(page, pageSize));
        return Ok(new ApiResponse<PagedResponse<WithholdingTaxCertResponse>>(true, result));
    }

    [HttpPost("{certId:guid}/issue")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> Issue(Guid companyId, Guid certId)
    {
        var result = await _whtService.IssueAsync(companyId, certId);
        return Ok(new ApiResponse<WithholdingTaxCertResponse>(true, result, "ออกหนังสือรับรองสำเร็จ"));
    }

    [HttpPost("{certId:guid}/void")]
    public async Task<ActionResult<ApiResponse<bool>>> Void(Guid companyId, Guid certId)
    {
        await _whtService.VoidAsync(companyId, certId);
        return Ok(new ApiResponse<bool>(true, true, "ยกเลิกสำเร็จ"));
    }

    // ฝ่ายค้านรอบ 193 W-C1: ลบ 50 ทวิถาวร (หลักฐานนำส่งภาษี) — งานของคน ไม่ใช่คีย์
    [HttpDelete("{certId:guid}")]
    [Accounting.Filters.RejectApiKey("ลบหนังสือรับรองหัก ณ ที่จ่ายถาวร")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid certId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var isSystemAdmin = User.IsInRole("SystemAdmin");
        if (!isSystemAdmin)
        {
            var role = await _db.CompanyUsers
                .Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
                .Select(cu => cu.Role)
                .FirstOrDefaultAsync();
            if (role != UserRole.Owner)
                return StatusCode(403, new ApiResponse<string>(false, null, "เฉพาะเจ้าของบริษัท (Owner) เท่านั้นที่สามารถลบหนังสือรับรองถาวรได้"));
        }

        await _whtService.DeleteAsync(companyId, certId, userId);
        return Ok(new ApiResponse<string>(true, null, "ลบหนังสือรับรองหัก ณ ที่จ่ายสำเร็จ"));
    }

    [HttpGet("contacts/{contactId:guid}")]
    public async Task<ActionResult<ApiResponse<List<WithholdingTaxCertResponse>>>> GetByContact(
        Guid companyId, Guid contactId, [FromQuery] int? year)
    {
        var result = await _whtService.GetByContactAsync(companyId, contactId, year);
        return Ok(new ApiResponse<List<WithholdingTaxCertResponse>>(true, result));
    }

    /// <summary>Auto-generate WHT cert from a document that has withholding tax</summary>
    [HttpPost("auto-generate")]
    public async Task<ActionResult<ApiResponse<WithholdingTaxCertResponse>>> AutoGenerate(
        Guid companyId, [FromBody] AutoGenerateWhtRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _whtService.AutoGenerateFromDocumentAsync(companyId, request.DocumentId, request.AutoIssue, userId);
        return StatusCode(201, new ApiResponse<WithholdingTaxCertResponse>(true, result, "สร้างหนังสือรับรองหัก ณ ที่จ่ายจากเอกสารสำเร็จ"));
    }

    /// <summary>Get documents with WHT that don't have certs yet</summary>
    [HttpGet("pending")]
    public async Task<ActionResult<ApiResponse<List<PendingWhtDocumentResponse>>>> GetPending(
        Guid companyId, [FromQuery] int? year, [FromQuery] int? month)
    {
        var result = await _whtService.GetPendingDocumentsAsync(companyId, year, month);
        return Ok(new ApiResponse<List<PendingWhtDocumentResponse>>(true, result));
    }

    /// <summary>Dismiss a document from the "waiting to issue cert" list — the
    /// source document is left exactly as-is, we just stop prompting the user
    /// to issue a cert for it. POST /pending/{docId}/dismiss to skip;
    /// /pending/{docId}/restore to bring it back.</summary>
    [HttpPost("pending/{documentId:guid}/dismiss")]
    public async Task<ActionResult<ApiResponse<object>>> DismissPending(Guid companyId, Guid documentId)
    {
        await _whtService.DismissPendingAsync(companyId, documentId, dismiss: true);
        return Ok(new ApiResponse<object>(true, null, "ข้ามเอกสารนี้แล้ว — จะไม่แสดงในรายการรอออกอีก"));
    }
    [HttpPost("pending/{documentId:guid}/restore")]
    public async Task<ActionResult<ApiResponse<object>>> RestorePending(Guid companyId, Guid documentId)
    {
        await _whtService.DismissPendingAsync(companyId, documentId, dismiss: false);
        return Ok(new ApiResponse<object>(true, null, "คืนเอกสารสู่รายการรอออก"));
    }

    /// <summary>Bulk generate WHT certs for all pending documents in a period</summary>
    [HttpPost("bulk-generate")]
    public async Task<ActionResult<ApiResponse<BulkGenerateWhtResponse>>> BulkGenerate(
        Guid companyId, [FromBody] BulkGenerateWhtRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _whtService.BulkGenerateAsync(companyId, request, userId);
        return Ok(new ApiResponse<BulkGenerateWhtResponse>(true, result,
            $"สร้างสำเร็จ {result.Generated} รายการ" + (result.Skipped > 0 ? $", ข้าม {result.Skipped} รายการ" : "")));
    }

    /// <summary>ข้อมูลอ้างอิง: คำนำหน้าชื่อ — ให้ทุกหน้าสร้าง dropdown จากที่นี่
    /// ไม่ใช่พิมพ์ตัวเลือกซ้ำในแต่ละหน้า (เดิม employees.html มี "ดร." แต่
    /// payroll.html ไม่มี ทั้งที่เป็นช่องเดียวกันของข้อมูลชุดเดียวกัน)</summary>
    [HttpGet("~/api/reference/titles")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<object>> GetTitles()
    {
        var titles = Accounting.Helpers.ThaiTitleHelper.All.Select(t => new
        {
            t.Thai,
            t.RdCode,
            t.IsJuristic,
            // ใช้ยื่น สปส. ได้ไหม (สปส. e-Service ปฏิเสธทั้งแถวถ้าคำนำหน้าไม่อยู่ในชุด)
            ValidForSso = Accounting.Helpers.ThaiTitleHelper.IsValidForSso(t.Thai),
        });
        return Ok(new ApiResponse<object>(true, titles));
    }

    /// <summary>ข้อมูลอ้างอิง: ประเภทเงินได้ + อัตราหัก ณ ที่จ่ายตามกฎหมาย</summary>
    [HttpGet("~/api/reference/income-types")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<object>> GetIncomeTypes()
    {
        // ⚠️ เดิมตารางนี้ถูกพิมพ์ไว้ตรงนี้ตัวหนึ่ง และหน้า wht-credit.html พิมพ์ไว้
        // อีกตัวหนึ่ง — สองตารางไม่ตรงกันเอง และไม่ตรงกับ ท.ป.4/2528 ทั้งคู่
        // (40(3) ค่าสิทธิ ที่นี่ = 5% ควรเป็น 3% · 40(1) เงินเดือนใส่ 3% คงที่
        // ทั้งที่กฎหมายเป็นอัตราขั้นบันได · ฝั่งหน้าเว็บยุบดอกเบี้ย+ปันผลเป็น 1%)
        // ยุบเป็น Helpers/ThaiWhtRateTable ตัวเดียว แล้วให้หน้าเว็บสร้าง dropdown
        // จาก endpoint นี้ — drift เป็นศูนย์โดยโครงสร้าง
        var incomeTypes = Accounting.Helpers.ThaiWhtRateTable.All.Select(t => new
        {
            t.Code,
            t.Name,
            t.TaxSection,
            IndividualRate = t.IndividualRate,
            JuristicRate = t.JuristicRate,
            // ค่าที่ฟอร์มควรเติมให้ก่อน (เคสปกติของบริษัท = ผู้รับเป็นนิติบุคคล)
            // null = กฎหมายไม่มีอัตราคงที่ ⇒ ห้ามเติมตัวเลขปลอมให้ช่องไม่ว่าง
            DefaultRate = t.JuristicRate ?? t.IndividualRate,
            // แถวบนแบบ 50 ทวิ — ส่งมาให้หน้าเว็บ **แสดง** อย่างเดียว ห้ามคิดเอง
            // (เดิม wht.html ถือ allow-list ของรหัสที่พิมพ์มือ ซึ่งตกรหัสที่ระบบ
            //  เองสร้าง `8ad`/`8tr` ⇒ ช่องประเภทว่างแต่ยอดรวมเต็ม — D-03/F-02)
            CertificateRow = Accounting.Helpers.ThaiWhtRateTable.CertificateRow(t.Code),
            t.ApplicableForms,
            t.Note,
        });

        return Ok(new ApiResponse<object>(true, incomeTypes));
    }
}
