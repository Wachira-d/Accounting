using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 267 แพ็ก P1 — <b>หน้าเอกสารสำหรับลูกค้า (ไม่ล็อกอิน)</b>: ดูหัวใบ/รายการ/ยอด · ดาวน์โหลด PDF · ขอลิงก์ใหม่เมื่อลิงก์หมดอายุ
/// (DOCUMENT_FLOW §2.12n · คำตัดสิน 267-1-1) — route เดิมของลิงก์ชำระ (<c>public-pay/documents/{token}</c>): ลิงก์ชำระที่ลูกค้าถืออยู่
/// กลายเป็นหน้าเอกสารทันที · เส้นจ่าย/สลิป (<c>intents</c> · <c>slip</c>) ยังอยู่ที่ <see cref="PublicPaymentController"/> ตามเดิมทุกตัวอักษร
///
/// <para>สิทธิ์ = token ของใบ (สุ่ม 256 บิต · ฐานเก็บ hash + ฉบับเข้ารหัส · ตรวจเวลาคงที่ · ตัดสินสดทุกครั้ง) ผ่าน
/// <see cref="IDocumentPayLinkService"/> ตัวเดียว · ปุ่ม/ข้อความ/เหตุที่ทำไม่ได้ทั้งหมดมาจาก <see cref="CustomerDocPage"/> ·
/// ข้อความล้มเดียวทุกเหตุ (<see cref="CustomerDocPage.NotFoundMessage"/>) · เพดานต่อ IP + ต่อ token (นับในฐาน — หลายเครื่อง) ·
/// <c>Cache-Control: private, no-store</c> · <c>Referrer-Policy: no-referrer</c> (token อยู่ใน URL)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/public-pay")]
[AllowAnonymous]
public partial class PublicDocumentPageController : ControllerBase
{
    private readonly IDocumentPayLinkService _links;
    private readonly IPdfGenerationService _pdf;
    private readonly IChatRateLimiter _limiter;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<PublicDocumentPageController> _logger;
    /// <summary>รอบ 267 ฝ่ายค้าน M7 (267-1-31 · PDPA ม.37): บันทึกการดาวน์โหลด PDF ทางลิงก์สาธารณะ · null (สร้างนอก DI) = ไม่บันทึก</summary>
    private readonly Accounting.Services.Implementations.Pdpa.IPdpaService? _pdpa;

    /// <summary>รอบ 267 P2: ผู้เขียนเดียวของการตอบรับ (ยอมรับ · ไม่รับ · ขอแก้ · เซ็นรับ) · null (สร้างนอก DI) = ไม่มีเส้นตอบรับ (ตอบ 404)</summary>
    private readonly Accounting.Services.Implementations.CustomerDocumentResponseService? _responses;

    public PublicDocumentPageController(IDocumentPayLinkService links, IPdfGenerationService pdf, IChatRateLimiter limiter,
        ILogger<PublicDocumentPageController> logger, INotificationEngine? notify = null,
        Accounting.Services.Implementations.Pdpa.IPdpaService? pdpa = null,
        Accounting.Services.Implementations.CustomerDocumentResponseService? responses = null)
    { _links = links; _pdf = pdf; _limiter = limiter; _logger = logger; _notify = notify; _pdpa = pdpa; _responses = responses; }

    private string Ip => HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private void NoStore()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private ObjectResult TooMany<T>()
    {
        Response.Headers.Append("Retry-After", "60");
        return StatusCode(429, new ApiResponse<T>(false, default, CustomerDocPage.RateMessage));
    }

    // ═══ เพดาน (ฝ่ายค้าน 267 M6 · 267-1-29) — สามถัง · ตัวเลขเดียวที่ CustomerDocPage.Limits ═══
    // (1) ต่อ IP อย่างเดียว (หลวม) — ก่อนแตะฐาน: token สุ่มทีละค่าไม่ได้ถังใหม่ และไม่สร้างแถวตัวนับต่อ token สุ่ม
    // (2) ต่อ (token + IP) — หลังยืนยันว่า token มีจริงเท่านั้น (ลูกค้าหลัง NAT เดียวกันไม่โดนรวม)
    // (3) ต่อ token อย่างเดียว — งานหนัก (เรนเดอร์ PDF) ที่หมุน IP หนีไม่ได้
    // failClosed: ตารางตัวนับใช้ไม่ได้ ⇒ ปฏิเสธไหม — เส้นดู = ไม่ (หน้านี้คือหน้าชำระเดิม) · PDF/ขอลิงก์ใหม่ = ปฏิเสธ

    private async Task<bool> OverIpAsync(string bucket, bool failClosed, CancellationToken ct)
        => !await _limiter.TryConsumeAsync(CustomerDocPage.RateKeyIp(bucket, Ip), CustomerDocPage.Limits.IpPerMinute, CustomerDocPage.Limits.IpPerDay, ct, failClosed: failClosed);

    private async Task<bool> OverTokenIpAsync(string bucket, string token, int perMinute, int perDay, bool failClosed, CancellationToken ct)
        => !await _limiter.TryConsumeAsync(CustomerDocPage.RateKey(bucket, token, Ip), perMinute, perDay, ct, failClosed: failClosed);

    private async Task<bool> OverTokenAsync(string bucket, string token, int perMinute, int perDay, CancellationToken ct)
        => !await _limiter.TryConsumeAsync(CustomerDocPage.RateKeyToken(bucket, token), perMinute, perDay, ct, failClosed: true);

    private static string BasePath(Guid companyId, string token)
        => $"/api/companies/{companyId:D}/public-pay/documents/{Uri.EscapeDataString(token)}";

    /// <summary>view model เดียวของหน้า — หัวใบ · บรรทัด · ยอด · ปุ่ม (<c>page.actions</c>) · ใบที่เกี่ยวข้อง + ช่องจ่ายเดิม (ชื่อเดิม) ·
    /// ลิงก์เพิกถอน/ไม่พบ ⇒ 404 ข้อความเดียว · หมดอายุไม่นาน ⇒ 404 ข้อความเดียว + <c>page.canRequestLink</c> (ไม่มีข้อมูลของใบ)</summary>
    [HttpGet("documents/{token}")]
    public async Task<ActionResult<ApiResponse<PublicDocumentPayView>>> View(Guid companyId, string token, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("view", failClosed: false, ct)) return TooMany<PublicDocumentPayView>();
        var view = await _links.PublicViewAsync(companyId, token, ct);
        if (view == null)
            return NotFound(new ApiResponse<PublicDocumentPayView>(false, null, CustomerDocPage.NotFoundMessage));
        if (await OverTokenIpAsync("view", token, CustomerDocPage.Limits.ViewPerMinute, CustomerDocPage.Limits.ViewPerDay, failClosed: false, ct))
            return TooMany<PublicDocumentPayView>();
        var basePath = BasePath(companyId, token);
        if (view.Page is not { Viewable: true })
        {
            // ลิงก์ใช้ไม่ได้: ไม่มีข้อมูลของใบ · ฝ่ายค้าน 267 L4: คำตอบเหมือน token ที่ไม่มีจริงทุกประการ (data = null — ไม่บอกสถานะเพิกถอน/ใช้ได้)
            // ยกเว้นกรณีเดียวที่มีทางไปต่อ: หมดอายุไม่นาน ⇒ data.page มีแค่ "ขอลิงก์ใหม่ได้" (ไม่ออก token ให้คนนอก)
            var stub = view.Page is { CanRequestLink: true } p ? view with { Page = p with { RequestLinkPath = basePath + "/request-link" } } : null;
            return NotFound(new ApiResponse<PublicDocumentPayView>(false, stub, CustomerDocPage.NotFoundMessage));
        }
        await _links.MarkOpenedAsync(companyId, token, ct);
        // รอบ 267 P5 (267-1-13): + เส้นเข้าด้วย LINE เพื่อเติมข้อมูลผู้ลงนาม (เฉพาะเมื่อมีฟอร์มตอบรับ + LINE Login ของระบบเปิด)
        var signerPage = await WithSignerPathsAsync(view.Page!, basePath, ct);
        return Ok(new ApiResponse<PublicDocumentPayView>(true, view with
        {
            // รอบ 267 P4: + ที่อยู่เส้นหลังจ่าย (ส่ง 50 ทวิ · เปิดหน้าของใบที่เกี่ยวข้อง) เฉพาะเมื่อหน้าเสนอจริง — WithAfterPayPaths ·
            // รอบ 267 P2: + ฉบับที่ลงนาม/รหัสอีเมล
            Page = WithAfterPayPaths(signerPage with
            {
                PdfPath = basePath + "/pdf", RelatedPdfBase = basePath + "/related/", RequestLinkPath = basePath + "/request-link",
                SignedCopyBase = basePath + "/signed/", OtpPath = basePath + "/respond/otp",
            }, basePath),
        }));
    }

    /// <summary>PDF ของใบนี้ — ไฟล์เดียวกับที่ผู้ขายดาวน์โหลด/ส่งอีเมล (renderer เดิม · ภาษาเอกสารผ่าน resolver กลาง · ใบยกเลิกพิมพ์ "ยกเลิก")</summary>
    [HttpGet("documents/{token}/pdf")]
    public Task<IActionResult> Pdf(Guid companyId, string token, CancellationToken ct) => PdfCoreAsync(companyId, token, null, ct);

    /// <summary>PDF ของใบที่เกี่ยวข้อง — <paramref name="relId"/> ต้องอยู่ในชุดที่หน้าแสดงของ token นี้ (ตัวโหลดเดียว · บริษัท/ผู้ติดต่อเดียวกัน ·
    /// ลูกค้าเห็นได้ · ออกแล้ว) · นอกชุด = 404 ข้อความเดียว</summary>
    [HttpGet("documents/{token}/related/{relId:guid}/pdf")]
    public Task<IActionResult> RelatedPdf(Guid companyId, string token, Guid relId, CancellationToken ct) => PdfCoreAsync(companyId, token, relId, ct);

    private async Task<IActionResult> PdfCoreAsync(Guid companyId, string token, Guid? relId, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("pdf", failClosed: true, ct)) return TooMany<object>();
        IActionResult Missing() => NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        var documentId = await _links.ResolvePdfDocumentAsync(companyId, token, relId, ct);
        if (documentId is not Guid id) return Missing();
        // token มีจริงแล้ว ⇒ เพดานเรนเดอร์: ต่อ (token + IP) และต่อ token รวมทุก IP (หมุน IP หนีไม่ได้)
        if (await OverTokenIpAsync("pdf", token, CustomerDocPage.Limits.PdfPerMinute, CustomerDocPage.Limits.PdfPerDay, failClosed: true, ct)
            || await OverTokenAsync("pdf", token, CustomerDocPage.Limits.PdfPerTokenPerMinute, CustomerDocPage.Limits.PdfPerTokenPerDay, ct))
            return TooMany<object>();
        var pdf = await _pdf.GenerateDocumentPdfAsync(companyId, new Models.DTOs.DocumentTemplate.GeneratePdfRequest(id, null, null, null, null));
        if (pdf?.PdfData is not { Length: > 0 }) return Missing();
        await LogPdfAccessAsync(companyId, id, relId != null, ct);
        Response.Headers.ContentDisposition = $"inline; filename=\"{SafeFileName(pdf.FileName)}\"";
        return File(pdf.PdfData, "application/pdf");
    }

    /// <summary>รอบ 267 ฝ่ายค้าน M7 (PDPA ม.37): PDF ของเอกสารมีชื่อ/ที่อยู่/เลขผู้เสียภาษีของคู่ค้า — ทุกการดาวน์โหลดทางลิงก์สาธารณะลงบันทึกการเข้าถึง
    /// (ใบไหน · เมื่อไร · hash ของ IP/เบราว์เซอร์ — ไม่เก็บ IP ตรงของคนนอก) · บันทึกล้ม = log ระดับ error (ไม่เงียบ) แต่ไม่กันลูกค้าจากเอกสารของตัวเอง</summary>
    private async Task LogPdfAccessAsync(Guid companyId, Guid documentId, bool related, CancellationToken ct, string? field = null)
    {
        if (_pdpa == null) return;
        try
        {
            var ua = HttpContext?.Request.Headers.UserAgent.ToString();
            await _pdpa.LogPiiAccessAsync(companyId, Guid.Empty, "ผู้ถือลิงก์เอกสาร (ไม่ได้ล็อกอิน)", "Document", documentId,
                field ?? (related ? "DocumentPdf.Related" : "DocumentPdf"), "PublicLinkDownload", "ลูกค้าดาวน์โหลดเอกสารผ่านลิงก์หน้าเอกสาร (" + CustomerDocPage.RuleCode + ")",
                "sha256:" + QuotationAcceptance.Sha256Hex(Ip)![..24], string.IsNullOrEmpty(ua) ? null : "sha256:" + QuotationAcceptance.Sha256Hex(ua)![..24], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "บันทึกการเข้าถึง PDF ทางลิงก์สาธารณะไม่สำเร็จ doc={DocId} company={CompanyId}", documentId, companyId);
        }
    }

    // ═══════════════ รอบ 267 P2 — ตอบรับบนหน้าเอกสาร (ผู้เขียนเดียว CustomerDocumentResponseService) ═══════════════

    private Accounting.Services.Implementations.CustomerDocumentResponseService.Caller Caller()
        => new(HttpContext?.Connection.RemoteIpAddress?.ToString(), HttpContext?.Request.Headers.UserAgent.ToString());

    private ActionResult<ApiResponse<object>> FromOutcome(Accounting.Services.Implementations.CustomerDocumentResponseService.Outcome r)
    {
        if (r.Status == 429) Response.Headers.Append("Retry-After", "60");
        return StatusCode(r.Status, new ApiResponse<object>(r.Success, r.Success ? r.Data : null, r.Message));
    }

    /// <summary>คำตอบของลูกค้าบนหน้าเอกสาร: ยอมรับ · ไม่รับ (เหตุผล) · ขอแก้ ใบเสนอราคา และเซ็นรับสินค้า — ผู้ลงนาม = ชื่อ + นามสกุล + ตำแหน่ง + ติ๊กรับรอง ·
    /// ทำได้เฉพาะเมื่อปุ่มนั้นเปิดอยู่ตาม <see cref="CustomerDocPage.Actions"/> ณ ตอนนี้ · เลข Rev + hash เนื้อหาที่หน้าแสดงต้องตรงกับใบ (ไม่ตรง = 409) ·
    /// หลักฐานลงตารางเดียว + audit chain ในธุรกรรมเดียว (ที่นี่ไม่มีตรรกะ)</summary>
    [HttpPost("documents/{token}/respond")]
    [RequestSizeLimit(512 * 1024)]
    public async Task<ActionResult<ApiResponse<object>>> Respond(Guid companyId, string token,
        [FromBody] Accounting.Services.Implementations.CustomerDocumentResponseService.RespondRequest? request, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("respond", failClosed: true, ct)) return TooMany<object>();
        if (_responses == null) return NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        // เพดานของการเขียน: ต่อ IP 10/นาที · 60/วัน ภายใน service (ก่อนแตะฐาน — token สุ่มไม่สร้างถังตัวนับใหม่ · 267-1-29) + ธุรกรรมจองแบบอะตอมมิก
        return FromOutcome(await _responses.RespondAsync(companyId, token, request, Caller()));
    }

    public sealed record OtpRequest(string? Email);

    /// <summary>ขอรหัสยืนยันอีเมลก่อนยอมรับใบเสนอราคา (เฉพาะเมื่อบริษัทตั้งยอดขั้นต่ำ — 267-1-6) · เพดานต่อ IP + ต่อ token</summary>
    [HttpPost("documents/{token}/respond/otp")]
    public async Task<ActionResult<ApiResponse<object>>> RespondOtp(Guid companyId, string token, [FromBody] OtpRequest? request, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("otp", failClosed: true, ct)) return TooMany<object>();
        if (_responses == null) return NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        // เพดานของรหัส: ต่อ IP 5/นาที · 30/วัน + หนึ่งรหัสต่อใบต่อนาที (แถวในฐาน — หลายเครื่อง) ภายใน service
        return FromOutcome(await _responses.RequestOtpAsync(companyId, token, request?.Email, Caller()));
    }

    /// <summary>ฉบับที่ลูกค้าเซ็น (ไฟล์ ณ ตอนลงนาม) ของคำตอบหนึ่งครั้งบนใบของ token นี้ — ตรวจ SHA-256 กับที่บันทึกก่อนส่งทุกครั้ง · ลงบันทึกการเข้าถึง (PDPA ม.37)</summary>
    [HttpGet("documents/{token}/signed/{actionId:guid}")]
    public async Task<IActionResult> SignedCopy(Guid companyId, string token, Guid actionId, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("pdf", failClosed: true, ct)) return TooMany<object>();
        IActionResult Missing() => NotFound(new ApiResponse<object>(false, null, CustomerDocPage.NotFoundMessage));
        if (_responses == null) return Missing();
        var file = await _responses.SignedCopyForTokenAsync(companyId, token, actionId, ct);
        if (file == null) return Missing();
        if (await OverTokenIpAsync("pdf", token, CustomerDocPage.Limits.PdfPerMinute, CustomerDocPage.Limits.PdfPerDay, failClosed: true, ct))
            return TooMany<object>();
        await LogPdfAccessAsync(companyId, file.Value.DocumentId, false, ct, "DocumentPdf.SignedCopy");
        Response.Headers.ContentDisposition = $"inline; filename=\"{SafeFileName(file.Value.FileName)}\"";
        return File(file.Value.Bytes, "application/pdf");
    }

    /// <summary>ผลของการขอลิงก์ — ไม่มีรหัสภายใน</summary>
    public sealed record RequestLinkResponse(bool Recorded, string Message);

    /// <summary>"ขอให้ผู้ขายส่งลิงก์ใหม่" — ใช้ได้กับลิงก์ที่หมดอายุไม่เกิน <see cref="CustomerDocPage.ExpiredRequestWindowDays"/> วัน
    /// (และลิงก์ที่ยังใช้ได้แต่ปุ่มตอบรับปิด) · <b>ไม่ออก token ให้คนนอก</b> — บันทึก audit แล้วแจ้งพนักงาน (กระดิ่ง) · ≤ 1 ครั้ง/ใบ/วัน</summary>
    [HttpPost("documents/{token}/request-link")]
    public async Task<ActionResult<ApiResponse<RequestLinkResponse>>> RequestLink(Guid companyId, string token, CancellationToken ct)
    {
        NoStore();
        if (await OverIpAsync("reqlink", failClosed: true, ct)) return TooMany<RequestLinkResponse>();
        var r = await _links.RequestNewLinkAsync(companyId, token, ct);
        if (!r.Found) return NotFound(new ApiResponse<RequestLinkResponse>(false, null, CustomerDocPage.NotFoundMessage));
        if (r.Recorded) await NotifyLinkRequestedAsync(_notify, _logger, companyId, r);
        return Ok(new ApiResponse<RequestLinkResponse>(true, new RequestLinkResponse(r.Recorded, r.Message), r.Message));
    }

    /// <summary>แจ้งพนักงานว่าลูกค้าขอลิงก์ใหม่ (กระดิ่ง — ค่าตั้งต้น <c>NotificationAudiencePolicy.BellOnlyKeys</c>) · ผู้เรียก: เส้นลิงก์ + เส้นการ์ด LINE ·
    /// คำขอถูกบันทึกลง audit chain แล้วก่อนถึงตรงนี้ ⇒ แจ้งล้ม = log ระดับ error (หลักฐานคำขอไม่หาย · ไม่ตอบลูกค้าว่าล้ม)</summary>
    internal static async Task NotifyLinkRequestedAsync(INotificationEngine? notify, ILogger logger, Guid companyId, CustomerLinkRequestResult r)
    {
        if (notify == null) return;
        try
        {
            await notify.DispatchAsync(companyId, NotificationEvents.DocumentLinkRequested, new NotificationContext
            {
                Title = $"🔗 ลูกค้าขอลิงก์เอกสารใหม่ — {r.DocumentNumber}",
                Message = $"ผู้ถือลิงก์ของเอกสาร {r.DocumentNumber} ขอให้ส่งลิงก์ใหม่ (ลิงก์เดิมหมดอายุหรือพ้นกำหนดทำรายการ) — เปิดเอกสารแล้วออก/ส่งลิงก์ให้ลูกค้า",
                ActionUrl = $"/pages/documents.html?id={r.DocumentId}",
                EntityType = nameof(Models.Entities.Document),
                EntityId = r.DocumentId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "แจ้งพนักงานเรื่องลูกค้าขอลิงก์เอกสารใหม่ไม่สำเร็จ doc={DocId} company={CompanyId}", r.DocumentId, companyId);
        }
    }

    internal static string SafeFileName(string? name)
    {
        var n = new string((name ?? "document.pdf").Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.').ToArray());
        return n.Length == 0 ? "document.pdf" : n;
    }
}
