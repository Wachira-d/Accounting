using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// ข้อมูลเชิงกฎหมายที่หน้าเว็บสาธารณะต้องใช้ — เวอร์ชันนโยบายที่บังคับใช้อยู่
/// และ **ตัวตนของผู้ควบคุมข้อมูลส่วนบุคคล** (PDPA ม.23(1)(6): ต้องแจ้งชื่อและ
/// ช่องทางติดต่อของผู้ควบคุมข้อมูลให้เจ้าของข้อมูลทราบก่อน/ขณะเก็บข้อมูล)
///
/// ทำไมต้องเป็น endpoint ไม่ใช่พิมพ์ลงหน้า HTML ตรง ๆ: ชื่อ/ที่อยู่/เลขผู้เสียภาษี
/// ของผู้ให้บริการตั้งไว้ที่ <c>/admin/site-settings.html</c> อยู่แล้ว
/// (<c>SiteSettings.PlatformSeller*</c> — ชุดเดียวกับที่ใช้ออกใบกำกับค่าบริการ)
/// ถ้าหน้า terms/privacy ฝัง literal ไว้เอง วันที่ผู้ให้บริการย้ายที่อยู่/
/// เปลี่ยนชื่อนิติบุคคล เอกสารทางกฎหมายจะค้างของเก่าเงียบ ๆ
/// (defect class "สอง renderer ห้าม drift" — ที่เดียวคือแหล่งความจริง)
/// </summary>
[ApiController]
[Route("api/legal")]
[AllowAnonymous]
public class LegalController : ControllerBase
{
    private readonly AccountingDbContext _db;

    public LegalController(AccountingDbContext db) => _db = db;

    public sealed record PolicyInfo(
        string Version,
        string EffectiveDate,        // ISO yyyy-MM-dd (หน้าเว็บแปลงเป็น พ.ศ. เอง)
        string? ControllerName,
        string? ControllerTaxId,
        string? ControllerAddress,
        string? ControllerPhone,
        string? PrivacyContactEmail,
        string? SiteName,
        bool ControllerConfigured);  // false = แอดมินยังไม่ได้กรอก → หน้าเว็บเตือน

    /// <summary>เวอร์ชันนโยบาย + ตัวตนผู้ควบคุมข้อมูล (ไม่ต้อง login)</summary>
    [HttpGet("policy")]
    public async Task<ActionResult<ApiResponse<PolicyInfo>>> GetPolicy(CancellationToken ct)
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        // ชื่อผู้ควบคุมข้อมูล: ชื่อนิติบุคคลผู้ให้บริการ → ถ้ายังไม่ตั้ง ใช้ชื่อเว็บ
        // (ชื่อเว็บไม่ใช่ชื่อนิติบุคคล จึงยังนับว่า "ยังไม่ได้ตั้งค่า" ด้านล่าง)
        var name = FirstNonEmpty(s?.PlatformSellerName, s?.SiteName);
        // ช่องทางติดต่อเรื่องข้อมูลส่วนบุคคล: อีเมลผู้ขาย → อีเมลติดต่อทั่วไป
        var email = FirstNonEmpty(s?.PlatformSellerEmail, s?.ContactEmail);

        return Ok(new ApiResponse<PolicyInfo>(true, new PolicyInfo(
            Version: PdpaPolicy.CurrentVersion,
            EffectiveDate: PdpaPolicy.EffectiveDate.ToString("yyyy-MM-dd"),
            ControllerName: name,
            ControllerTaxId: s?.PlatformSellerTaxId,
            ControllerAddress: s?.PlatformSellerAddress,
            ControllerPhone: FirstNonEmpty(s?.PlatformSellerPhone, s?.ContactPhone),
            PrivacyContactEmail: email,
            SiteName: s?.SiteName,
            ControllerConfigured: !string.IsNullOrWhiteSpace(s?.PlatformSellerName)
                                  && !string.IsNullOrWhiteSpace(email))));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
