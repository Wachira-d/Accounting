using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Ocr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>ผลคัดกรองภาษีซื้อต้องห้าม §82/5(4)(6) ของบรรทัดหนึ่ง</summary>
/// <param name="Claimable"><c>false</c> = ควรตั้ง "ไม่เคลม" ไว้ก่อน · <c>null</c> = ไม่ปิดเคลม (อาจยังมีคำเตือน)</param>
/// <param name="RuleCode">อ้างมาตรา เช่น <c>RD-82/5(6)</c> · null = ไม่เข้าข่าย</param>
/// <param name="Warning">ข้อความถึงผู้ใช้ (ไทย) · null = ไม่ต้องเตือน</param>
/// <param name="Vehicle">ชื่อ enum <c>VehicleVatVerdict</c> (ส่งเป็นชื่อเสมอ)</param>
public record InputVatScreenResult(bool? Claimable, string? RuleCode, string? Warning, string Vehicle);

/// <summary>
/// **คัดกรองภาษีซื้อต้องห้ามให้หน้าเว็บ — เซิร์ฟเวอร์ตัดสิน หน้าแสดง** (รอบ 193 ฝ่ายค้าน C-4)
///
/// <para>ที่มา: <c>documents.html</c> มีลิสต์คำรถ/ค่ารับรองของตัวเอง 2 ชุด (<c>suggestVatClaim</c> ปิดเคลมให้อัตโนมัติ ·
/// <c>toggleVatClaim</c> เตือนตอนติ๊กเคลม) ที่<b>ไม่รู้จัก <c>IsVehicleDealer</c></b> และมีคำเดี่ยว ("น้ำมัน" · "ค่าซ่อม" · "fuel")
/// ⇒ อู่/ผู้ขายรถที่คีย์มือ "อะไหล่รถ" ถูกปิดเคลมเหมือนก่อนแก้ S-05 · "น้ำมันพืช" ถูกปิดเคลม (F2 ข้อ 5 JS ห้ามมีสำเนากติกา)
/// ตอนนี้ทั้งสองจุดถามที่นี่ ซึ่งเรียก <see cref="ProhibitedInputVatScreener"/> ตัวเดียวกับ OCR และด่านเตือนตอนอนุมัติ</para>
///
/// <para>อ่านอย่างเดียว (GET) · สมาชิกบริษัทผ่าน <c>TenantAccessMiddleware</c> · query กรอง <c>CompanyId</c></para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/input-vat/screen")]
[Authorize]
public class InputVatScreenController : ControllerBase
{
    private const int MaxLines = 200;
    private readonly AccountingDbContext _db;

    public InputVatScreenController(AccountingDbContext db) => _db = db;

    /// <summary>คัดกรองทีละบรรทัด (ลำดับเดียวกับที่ส่งมา) — <paramref name="vendorName"/> ใช้หา "ชื่อปั๊ม" เท่านั้น</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<InputVatScreenResult>>>> Get(
        Guid companyId, [FromQuery] string? vendorName, [FromQuery] List<string>? lines, CancellationToken ct = default)
    {
        var input = (lines ?? new List<string>()).Take(MaxLines).ToList();
        var isVehicleDealer = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => (bool?)s.IsVehicleDealer)
            .FirstOrDefaultAsync(ct) ?? false;
        var results = input
            .Select(line =>
            {
                var v = ProhibitedInputVatScreener.Screen(null, vendorName, new[] { line }, isVehicleDealer);
                return new InputVatScreenResult(v.Claimable, v.RuleCode, v.Warning, v.Vehicle.ToString());
            })
            .ToList();
        return Ok(new ApiResponse<List<InputVatScreenResult>>(true, results));
    }
}
