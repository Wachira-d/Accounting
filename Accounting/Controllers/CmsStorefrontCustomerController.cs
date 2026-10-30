using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 209 · P1 — ทางเข้าฝั่ง<b>ลูกค้าหน้าร้าน</b> (อ่านอย่างเดียว):
/// <list type="bullet">
/// <item>หน้าสถานะออเดอร์ของแขก — พิสูจน์สิทธิ์ด้วย <c>SiteOrder.PublicToken</c> (สุ่ม · ไม่ใช่ GUID) · DTO จำกัดฟิลด์ (คำตัดสินข้อ 168 ข)</item>
/// <item>QR พร้อมเพย์สร้างที่เซิร์ฟเวอร์ (ไม่ส่งเลขพร้อมเพย์/ยอดไปบริการภายนอก — ข้อ 168 ฑ)</item>
/// <item>"บัญชีของฉัน" — โทเคนลูกค้าหน้าร้านใน header <c>X-Storefront-Token</c> (<see cref="StorefrontCustomerAuth"/>) · อ่านเฉพาะข้อมูลของ id ในโทเคน</item>
/// </list>
/// <c>[AllowAnonymous]</c> ทั้งคลาส เพราะโทเคนหน้าร้านถูก scheme ของ ERP ปฏิเสธโดยตั้งใจ (A-02) — ด่านจริงคือ <see cref="Me"/> ที่ทุก action "ของฉัน" เรียก
/// </summary>
[ApiController]
[AllowAnonymous]
public class CmsStorefrontCustomerController : ControllerBase
{
    private readonly IStorefrontCustomerService _svc;
    private readonly IConfiguration _config;

    public CmsStorefrontCustomerController(IStorefrontCustomerService svc, IConfiguration config)
    {
        _svc = svc;
        _config = config;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/orders/public/{token}")]
    public async Task<ActionResult<ApiResponse<PublicOrderResponse>>> GetPublicOrder(
        Guid companyId, Guid siteId, string token, [FromServices] IChatRateLimiter limiter)
    {
        if (!await limiter.TryConsumeAsync($"cms-order-public:{siteId}:{Ip}", perMinute: 30, perDay: 600))
            return StatusCode(429, new ApiResponse<PublicOrderResponse>(false, null, "เปิดดูถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var result = await _svc.GetPublicOrderAsync(companyId, siteId, token);
        if (result == null)
            return NotFound(new ApiResponse<PublicOrderResponse>(false, null, "ไม่พบคำสั่งซื้อ — ตรวจลิงก์อีกครั้ง หรือดูจากหน้า \"บัญชีของฉัน\""));
        return Ok(new ApiResponse<PublicOrderResponse>(true, result));
    }

    /// <summary>รูป QR พร้อมเพย์ของเว็บ (PNG) — ยอดที่หน้าเว็บได้จากเซิร์ฟเวอร์ (ยอดค้างของออเดอร์/ยอดโอนของการจอง) · ไม่มีพร้อมเพย์/ยอดผิด ⇒ 404</summary>
    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/commerce/promptpay-qr")]
    public async Task<IActionResult> PromptPayQr(Guid companyId, Guid siteId, [FromQuery] decimal? amount, [FromServices] IChatRateLimiter limiter)
    {
        if (!await limiter.TryConsumeAsync($"cms-promptpay-qr:{siteId}:{Ip}", perMinute: 30, perDay: 600))
            return StatusCode(429);
        var png = await _svc.PromptPayQrPngAsync(companyId, siteId, amount);
        if (png == null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=300";
        return File(png, "image/png");
    }

    // ===== รอบ 210 (คำตัดสินข้อ 169): แขกค้นการจอง/คำสั่งซื้อด้วยเลขที่ + เบอร์โทรหรืออีเมล =====
    // ตัวตัดสิน = Helpers/GuestRecordLookup (ตรงตัวหลัง normalize · ไม่ fuzzy) · สำเร็จคืนเฉพาะโทเคน · ไม่ตรง = ข้อความกลางเดียว (ไม่บอกว่าผิดที่ไหน) ·
    // จำกัดความถี่ต่อ IP และต่อเลขที่ ด้วย IChatRateLimiter (DB-backed · หลาย instance — กฎ #4 D) — นับก่อนค้นทุกครั้ง

    [HttpPost("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront/lookup/booking")]
    public async Task<ActionResult<ApiResponse<GuestLookupResponse>>> LookupBooking(
        Guid companyId, Guid siteId, [FromBody] GuestBookingLookupRequest? request, [FromServices] IChatRateLimiter limiter)
    {
        if (await LookupThrottledAsync(limiter, siteId, "booking", request?.BookingNumber, request?.Contact) is ActionResult limited) return limited;
        if (!GuestRecordLookup.IsValidRequest(request?.BookingNumber, request?.Contact))
            return BadRequest(new ApiResponse<GuestLookupResponse>(false, null, GuestRecordLookup.InputMessage));
        var token = await _svc.LookupBookingTokenAsync(companyId, siteId, request!.BookingNumber, request.Contact);
        if (token == null) return NotFound(new ApiResponse<GuestLookupResponse>(false, null, GuestRecordLookup.NotFoundMessage));
        return Ok(new ApiResponse<GuestLookupResponse>(true, new GuestLookupResponse(token, "booking")));
    }

    [HttpPost("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront/lookup/order")]
    public async Task<ActionResult<ApiResponse<GuestLookupResponse>>> LookupOrder(
        Guid companyId, Guid siteId, [FromBody] GuestOrderLookupRequest? request, [FromServices] IChatRateLimiter limiter)
    {
        if (await LookupThrottledAsync(limiter, siteId, "order", request?.OrderNumber, request?.Contact) is ActionResult limited) return limited;
        if (!GuestRecordLookup.IsValidRequest(request?.OrderNumber, request?.Contact))
            return BadRequest(new ApiResponse<GuestLookupResponse>(false, null, GuestRecordLookup.InputMessage));
        var token = await _svc.LookupOrderTokenAsync(companyId, siteId, request!.OrderNumber, request.Contact);
        if (token == null) return NotFound(new ApiResponse<GuestLookupResponse>(false, null, GuestRecordLookup.NotFoundMessage));
        return Ok(new ApiResponse<GuestLookupResponse>(true, new GuestLookupResponse(token, "order")));
    }

    /// <summary>นับโควตาก่อนค้น (fail-closed ทั้งหมด): ต่อ IP ของเว็บนี้ (IPv6 รวม /64 · 10/นาที · 100/วัน) · ต่อเลขที่ (5/นาที · 20/วัน — กันเดาเบอร์ของเลขเดียวจากหลาย IP) ·
    /// ต่อผู้ติดต่อ (5/นาที · 20/วัน — ฝ่ายค้านรอบ 210: กันรู้เบอร์/อีเมลเหยื่อแล้วไล่เลขที่ตามลำดับ) · เกิน ⇒ 429</summary>
    private async Task<ActionResult?> LookupThrottledAsync(IChatRateLimiter limiter, Guid siteId, string kind, string? number, string? contact)
    {
        var tooMany = StatusCode(429, new ApiResponse<GuestLookupResponse>(false, null, GuestRecordLookup.RateLimitedMessage));
        // คีย์สั้นพอสำหรับ BucketKey varchar(120): siteId แบบ N (32) + IP (≤45) · เลขที่/ผู้ติดต่อ = แฮช 16 ตัว
        var ip = GuestRecordLookup.IpBucket(HttpContext.Connection.RemoteIpAddress);
        if (!await limiter.TryConsumeAsync($"cms-lookup:{siteId:N}:{ip}", perMinute: 10, perDay: 100, failClosed: true)) return tooMany;
        if (GuestRecordLookup.NumberBucket(GuestRecordLookup.NormalizeNumber(number)) is string n
            && !await limiter.TryConsumeAsync($"cms-lookup-num:{siteId:N}:{kind}:{n}", perMinute: 5, perDay: 20, failClosed: true)) return tooMany;
        if (GuestRecordLookup.ContactBucket(contact) is string c
            && !await limiter.TryConsumeAsync($"cms-lookup-c:{siteId:N}:{c}", perMinute: 5, perDay: 20, failClosed: true)) return tooMany;
        return null;
    }

    // ===== บัญชีของฉัน (โทเคนลูกค้าหน้าร้าน) =====

    /// <summary>ด่านเดียวของทุก action "ของฉัน": โทเคนถูกต้อง + ออกโดยเว็บนี้ + ลูกค้ายังมีอยู่/ใช้งานในบริษัท+เว็บนี้ · ไม่ผ่าน ⇒ (null, 401/403)</summary>
    private async Task<(StorefrontMeResponse? Me, ActionResult? Fail)> Me(Guid companyId, Guid siteId)
    {
        var id = StorefrontCustomerAuth.FromRequest(Request, _config);
        if (id == null)
            return (null, StatusCode(401, new ApiResponse<object>(false, null, "กรุณาเข้าสู่ระบบสมาชิก (เซสชันหมดอายุหรือยังไม่ได้เข้าสู่ระบบ)")));
        if (StorefrontCustomerAuth.ForSite(id, siteId) == null)
            return (null, StatusCode(403, new ApiResponse<object>(false, null, "บัญชีสมาชิกนี้เป็นของเว็บไซต์อื่น — เข้าสู่ระบบที่เว็บนี้ใหม่")));
        var me = await _svc.GetMeAsync(companyId, siteId, id.CustomerId);
        if (me == null)
            return (null, StatusCode(401, new ApiResponse<object>(false, null, "ไม่พบบัญชีสมาชิก หรือบัญชีถูกปิด — เข้าสู่ระบบใหม่")));
        return (me, null);
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me")]
    public async Task<ActionResult> GetMe(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<StorefrontMeResponse>(true, me!));
    }

    /// <summary>รอบ 216 (คำตัดสินข้อ 174): สมาชิกแก้ "ข้อมูลส่วนตัว" ของตัวเอง — ลูกค้า = id ในโทเคนหน้าร้านของเว็บนี้ (ด่าน <see cref="Me"/>) ·
    /// อีเมลเข้าระบบแก้ไม่ได้ · ตัวตรวจ <see cref="StorefrontMemberProfile.Validate"/> · ไม่แตะผู้ติดต่อ ERP · จำกัดความถี่ต่อสมาชิก</summary>
    [HttpPut("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/profile")]
    public async Task<ActionResult> UpdateMyProfile(Guid companyId, Guid siteId, [FromBody] UpdateStorefrontProfileRequest? request,
        [FromServices] IChatRateLimiter limiter)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        if (!await limiter.TryConsumeAsync($"cms-profile:{me!.CustomerId:N}", perMinute: 10, perDay: 100))
            return StatusCode(429, new ApiResponse<object>(false, null, "บันทึกถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var updated = await _svc.UpdateProfileAsync(companyId, siteId, me.CustomerId, request ?? new UpdateStorefrontProfileRequest());
        if (updated == null)
            return StatusCode(401, new ApiResponse<object>(false, null, "ไม่พบบัญชีสมาชิก หรือบัญชีถูกปิด — เข้าสู่ระบบใหม่"));
        return Ok(new ApiResponse<StorefrontMeResponse>(true, updated, "บันทึกข้อมูลส่วนตัวแล้ว"));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/orders")]
    public async Task<ActionResult> MyOrders(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyOrderItem>>(true, await _svc.MyOrdersAsync(companyId, siteId, me!.CustomerId)));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/bookings")]
    public async Task<ActionResult> MyBookings(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyBookingItem>>(true, await _svc.MyBookingsAsync(companyId, siteId, me!.CustomerId)));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/credits")]
    public async Task<ActionResult> MyCredits(Guid companyId, Guid siteId)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<List<StorefrontMyCreditItem>>(true, await _svc.MyCreditsAsync(companyId, siteId, me!.CustomerId)));
    }

    // ===== รอบ 211 (คำตัดสินข้อ 170): รับแจ้งเตือนทาง LINE — สมาชิก (โทเคนหน้าร้าน) + แขก (โทเคนสาธารณะของการจอง/ออเดอร์) =====
    // เซิร์ฟเวอร์ตัดสินทุกอย่าง (ผูกได้ไหม · OA ไหน · ลิงก์/QR) ผ่าน ICustomerLineLinkService · หน้าเว็บแสดงอย่างเดียว · ออกรหัสนับโควตาก่อน (fail-closed)

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/line")]
    public async Task<ActionResult> MyLineStatus(Guid companyId, Guid siteId, [FromServices] ICustomerLineLinkService links)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        return Ok(new ApiResponse<CustomerLineStatusResponse>(true,
            await links.GetStatusAsync(companyId, siteId, CustomerLineLinkRule.KindMember, me!.CustomerId, me.CustomerId)));
    }

    [HttpPost("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/line/start")]
    public async Task<ActionResult> MyLineStart(Guid companyId, Guid siteId, [FromServices] ICustomerLineLinkService links, [FromServices] IChatRateLimiter limiter)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        if (await LineStartThrottledAsync(limiter, siteId, me!.CustomerId) is ActionResult limited) return limited;
        return Ok(new ApiResponse<CustomerLineStartResponse>(true,
            await links.StartAsync(companyId, siteId, CustomerLineLinkRule.KindMember, me.CustomerId)));
    }

    [HttpDelete("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/portal/me/line")]
    public async Task<ActionResult> MyLineUnlink(Guid companyId, Guid siteId, [FromServices] ICustomerLineLinkService links)
    {
        var (me, fail) = await Me(companyId, siteId);
        if (fail != null) return fail;
        var n = await links.UnlinkAsync(companyId, siteId, CustomerLineLinkRule.KindMember, me!.CustomerId, "user");
        return Ok(new ApiResponse<object>(true, new { unlinked = n }, n > 0 ? "ยกเลิกการเชื่อมต่อ LINE แล้ว" : "ไม่มีการเชื่อมต่อ LINE ของบัญชีนี้"));
    }

    [HttpGet("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront/line/{kind}/{token}")]
    public async Task<ActionResult> GuestLineStatus(Guid companyId, Guid siteId, string kind, string token,
        [FromServices] ICustomerLineLinkService links, [FromServices] IChatRateLimiter limiter)
    {
        if (!await limiter.TryConsumeAsync($"cms-line-status:{siteId:N}:{GuestRecordLookup.IpBucket(HttpContext.Connection.RemoteIpAddress)}", perMinute: 30, perDay: 600))
            return StatusCode(429, new ApiResponse<object>(false, null, "เปิดดูถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var rec = await GuestRecordAsync(links, companyId, siteId, kind, token);
        if (rec == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ — ตรวจลิงก์อีกครั้ง"));
        return Ok(new ApiResponse<CustomerLineStatusResponse>(true, await links.GetStatusAsync(companyId, siteId, kind, rec.Id, rec.CustomerId)));
    }

    [HttpPost("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront/line/{kind}/{token}/start")]
    public async Task<ActionResult> GuestLineStart(Guid companyId, Guid siteId, string kind, string token,
        [FromServices] ICustomerLineLinkService links, [FromServices] IChatRateLimiter limiter)
    {
        var rec = await GuestRecordAsync(links, companyId, siteId, kind, token);
        if (rec == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ — ตรวจลิงก์อีกครั้ง"));
        if (await LineStartThrottledAsync(limiter, siteId, rec.Id) is ActionResult limited) return limited;
        return Ok(new ApiResponse<CustomerLineStartResponse>(true, await links.StartAsync(companyId, siteId, kind, rec.Id)));
    }

    [HttpDelete("api/companies/{companyId:guid}/cms/sites/{siteId:guid}/storefront/line/{kind}/{token}")]
    public async Task<ActionResult> GuestLineUnlink(Guid companyId, Guid siteId, string kind, string token, [FromServices] ICustomerLineLinkService links)
    {
        var rec = await GuestRecordAsync(links, companyId, siteId, kind, token);
        if (rec == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายการ — ตรวจลิงก์อีกครั้ง"));
        var n = await links.UnlinkAsync(companyId, siteId, kind, rec.Id, "user");
        return Ok(new ApiResponse<object>(true, new { unlinked = n }, n > 0
            ? "ยกเลิกการเชื่อมต่อ LINE ของรายการนี้แล้ว"
            : "รายการนี้ไม่ได้เชื่อมต่อ LINE โดยตรง (ถ้าเชื่อมไว้ที่บัญชีสมาชิก ยกเลิกได้ที่ \"บัญชีของฉัน\")"));
    }

    /// <summary>แขก: เฉพาะการจอง/ออเดอร์ (สมาชิกใช้ portal/me/line) · โทเคนสาธารณะของรายการคือหลักฐานสิทธิ์ (เส้นเดียวกับหน้า my-booking/track-order)</summary>
    private static async Task<CustomerLineRecordRef?> GuestRecordAsync(ICustomerLineLinkService links, Guid companyId, Guid siteId, string kind, string token)
        => kind is CustomerLineLinkRule.KindBooking or CustomerLineLinkRule.KindOrder
            ? await links.ResolveRecordByTokenAsync(companyId, siteId, kind, token)
            : null;

    /// <summary>ออกรหัสผูก: ต่อ IP ของเว็บ (10/นาที · 60/วัน) + ต่อเป้าหมาย (5/นาที · 20/วัน) · ตารางนับพัง ⇒ ปฏิเสธ (fail-closed)</summary>
    private async Task<ActionResult?> LineStartThrottledAsync(IChatRateLimiter limiter, Guid siteId, Guid targetId)
    {
        var tooMany = StatusCode(429, new ApiResponse<object>(false, null, "ขอรหัสถี่เกินไป — รอสักครู่แล้วลองใหม่"));
        var ip = GuestRecordLookup.IpBucket(HttpContext.Connection.RemoteIpAddress);
        if (!await limiter.TryConsumeAsync($"cms-line-start:{siteId:N}:{ip}", perMinute: 10, perDay: 60, failClosed: true)) return tooMany;
        if (!await limiter.TryConsumeAsync($"cms-line-start-t:{targetId:N}", perMinute: 5, perDay: 20, failClosed: true)) return tooMany;
        return null;
    }
}
