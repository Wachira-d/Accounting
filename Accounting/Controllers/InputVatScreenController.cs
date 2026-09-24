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

/// <summary>คำขอคัดกรอง — ส่งเป็น body (POST) เพราะคำอธิบายทุกบรรทัดของใบยาวเกิน URL ได้ (414 · ฝ่ายค้านรอบสอง)</summary>
/// <param name="Lines">คำอธิบายรายการ ลำดับเดียวกับที่ต้องการผล (ตัดที่ 200 บรรทัด)</param>
/// <param name="VendorName">ชื่อผู้ขาย — ใช้หา "ชื่อปั๊ม" เท่านั้น</param>
public record InputVatScreenRequest(List<string?>? Lines, string? VendorName);

/// <summary>
/// **คัดกรองภาษีซื้อต้องห้ามให้หน้าเว็บ — เซิร์ฟเวอร์ตัดสิน หน้าแสดง** (รอบ 193 ฝ่ายค้าน C-4)
///
/// <para>ที่มา: <c>documents.html</c> มีลิสต์คำรถ/ค่ารับรองของตัวเอง 2 ชุด (<c>suggestVatClaim</c> ปิดเคลมให้อัตโนมัติ ·
/// <c>toggleVatClaim</c> เตือนตอนติ๊กเคลม) ที่<b>ไม่รู้จัก <c>IsVehicleDealer</c></b> และมีคำเดี่ยว ("น้ำมัน" · "ค่าซ่อม" · "fuel")
/// ⇒ อู่/ผู้ขายรถที่คีย์มือ "อะไหล่รถ" ถูกปิดเคลมเหมือนก่อนแก้ S-05 · "น้ำมันพืช" ถูกปิดเคลม (F2 ข้อ 5 JS ห้ามมีสำเนากติกา)
/// ตอนนี้ทั้งสองจุดถามที่นี่ ซึ่งเรียก <see cref="ProhibitedInputVatScreener"/> ตัวเดียวกับ OCR และด่านเตือนตอนอนุมัติ</para>
///
/// <para>อ่านอย่างเดียว (POST ที่คำนวณแล้วคืนค่า ไม่เขียนอะไร — เดิม GET ส่งทุกบรรทัดใน query string เสี่ยง 414) ·
/// สมาชิกบริษัทผ่าน <c>TenantAccessMiddleware</c> · query กรอง <c>CompanyId</c> · หลังตัวคัดกรองกลาง ต่อด้วยกติกา
/// "บรรทัดที่พิมพ์เอง" <see cref="Accounting.Helpers.ManualInputVatLineRule"/> (ย้ายจาก JS — ไม่ใช้กับเส้น OCR)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/input-vat/screen")]
[Authorize]
public class InputVatScreenController : ControllerBase
{
    private const int MaxLines = 200;
    private readonly AccountingDbContext _db;

    public InputVatScreenController(AccountingDbContext db) => _db = db;

    /// <summary>คัดกรองทีละบรรทัด (ลำดับเดียวกับที่ส่งมา)</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<List<InputVatScreenResult>>>> Screen(
        Guid companyId, [FromBody] InputVatScreenRequest? request, CancellationToken ct = default)
    {
        var input = (request?.Lines ?? new List<string?>()).Take(MaxLines).Select(l => l ?? "").ToList();
        var vendorName = request?.VendorName;
        var isVehicleDealer = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => (bool?)s.IsVehicleDealer)
            .FirstOrDefaultAsync(ct) ?? false;
        var results = input
            .Select(line =>
            {
                var v = ProhibitedInputVatScreener.Screen(null, vendorName, new[] { line }, isVehicleDealer);
                if (v.Claimable != false && Accounting.Helpers.ManualInputVatLineRule.Judge(line) is { } manual)
                    return new InputVatScreenResult(false, manual.RuleCode, manual.Warning, v.Vehicle.ToString());
                return new InputVatScreenResult(v.Claimable, v.RuleCode, v.Warning, v.Vehicle.ToString());
            })
            .ToList();
        return Ok(new ApiResponse<List<InputVatScreenResult>>(true, results));
    }
}
