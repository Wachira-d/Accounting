using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>ปลายทางสาธารณะสำหรับดาวน์โหลดสลิปเงินเดือนผ่าน token ที่ส่งทาง LINE.
/// ไม่ต้องล็อกอิน (พนักงานส่วนใหญ่ไม่มีบัญชี) แต่ token สุ่มเดาไม่ได้ + หมดอายุ +
/// เพิกถอนได้ + บันทึกการเข้าถึงตาม PDPA ม.37. หาก token ผิด/หมดอายุ → 404 เงียบ ๆ.</summary>
[ApiController]
[Route("api/public/payslip")]
[AllowAnonymous]
public class PayslipPublicController : ControllerBase
{
    private readonly IPayslipLineDeliveryService _payslipLine;

    public PayslipPublicController(IPayslipLineDeliveryService payslipLine)
        => _payslipLine = payslipLine;

    [HttpGet("{token}")]
    public async Task<ActionResult> Download(string token, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = Request.Headers.UserAgent.ToString();
        var result = await _payslipLine.ResolvePublicPayslipAsync(token, ip, ua, ct);
        if (result == null)
            return NotFound("ลิงก์ไม่ถูกต้องหรือหมดอายุแล้ว — กรุณาขอลิงก์ใหม่จากฝ่ายบุคคล");

        // แสดง inline ในเบราว์เซอร์ (LINE in-app browser) — ผู้ใช้กดบันทึกเองได้
        Response.Headers["Content-Disposition"] =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(result.Value.FileName)}";
        return File(result.Value.Pdf, "application/pdf");
    }
}
