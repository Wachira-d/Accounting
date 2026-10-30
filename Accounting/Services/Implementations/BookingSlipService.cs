using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public interface IBookingSlipService
{
    /// <summary>ลูกค้าแนบสลิปด้วยโทเคนการจอง · null = ไม่พบการจอง</summary>
    Task<BookingResponse?> UploadByTokenAsync(Guid companyId, Guid siteId, string token, IFormFile file, CancellationToken ct = default);
    /// <summary>ลูกค้าเปิดสลิปของตัวเอง (โทเคน) · null = ไม่มี</summary>
    Task<(byte[] Content, string ContentType)?> ReadSlipByTokenAsync(Guid companyId, Guid siteId, string token, CancellationToken ct = default);
    /// <summary>ร้านยืนยันสลิป ⇒ PaymentIntent สำเร็จ ⇒ handler บันทึกรับเงิน (เส้นเดียวกับ gateway)</summary>
    Task<BookingResponse> VerifyAsync(Guid companyId, Guid siteId, Guid bookingId, VerifyBookingSlipRequest request, string actor, CancellationToken ct = default);
    /// <summary>ร้านปฏิเสธสลิป (ต้องมีเหตุผล) ⇒ การจองล้างสลิป + แจ้งลูกค้า · PaymentIntent ⇒ Failed</summary>
    Task<BookingResponse> RejectAsync(Guid companyId, Guid siteId, Guid bookingId, RejectBookingSlipRequest request, string actor, CancellationToken ct = default);
}

/// <summary>
/// รอบ 218 (คำตัดสินข้อ 177) — <b>สลิปโอนเงินของการจองนัดหมาย</b>: ทุกทางเข้าของเงินผ่าน <c>PaymentIntent</c> + <c>IPaymentProvider</c> ตัวเดียว
/// (<see cref="ManualSlipPaymentProvider"/>) · แนบ ⇒ <c>StartAsync(manual-slip)</c> ยอดจากเซิร์ฟเวอร์ · ยืนยัน ⇒ <c>ApplyChargeAsync(Succeeded, Manual)</c> ⇒
/// <c>SiteBookingPaymentHandler</c> ⇒ <c>RecordDepositPaymentAsync</c> (ยืนยันการจอง + ใบรับมัดจำลงวันที่รับเงิน §78/1) · ปฏิเสธ ⇒ <c>Failed</c> ·
/// ไฟล์ผ่าน <c>IFileAttachmentService</c> (ชนิด <c>SiteBooking</c> · ด่านอ่าน CmsOrderManage — PII ผู้โอน) · สถานะบนการจองเขียนโดย
/// <c>CmsBookingService.Mark*</c> (ผู้เขียนแถวการจองคนเดียว · ใต้ล็อกเดียวกับเส้นสถานะ) ·
/// แยกจาก <c>CmsBookingService</c> เพราะต้องใช้ <c>IPaymentIntentService</c> (handler → booking service → intent service = วงกลม DI)
/// </summary>
public class BookingSlipService : IBookingSlipService
{
    private readonly AccountingDbContext _db;
    private readonly ICmsBookingService _bookings;
    private readonly IPaymentIntentService _intents;
    private readonly IFileAttachmentService _files;
    private readonly IImageProcessingService? _images;
    private readonly ILogger<BookingSlipService> _logger;

    public BookingSlipService(AccountingDbContext db, ICmsBookingService bookings, IPaymentIntentService intents, IFileAttachmentService files,
        ILogger<BookingSlipService> logger, IImageProcessingService? images = null)
    { _db = db; _bookings = bookings; _intents = intents; _files = files; _logger = logger; _images = images; }

    public async Task<BookingResponse?> UploadByTokenAsync(Guid companyId, Guid siteId, string token, IFormFile file, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var view = await _bookings.GetBookingByTokenAsync(companyId, siteId, token);
        if (view == null) return null;
        var pay = view.Payment;
        // ด่านเร็วก่อนเขียนไฟล์ (ตัดสินซ้ำใต้ล็อกใน MarkSlipUploadedAsync) — ยอด/ช่องทาง/สลิปค้าง จากตัวตัดสินเดียว
        if (BookingSlipRule.UploadProblem(view.Status, pay?.AmountDue ?? 0m, pay?.TransferAvailable == true, pay?.SlipPending == true, file?.Length ?? 0)
            is string problem)
            throw new BusinessRuleException(problem, BookingSlipRule.RuleCode);
        var due = pay!.AmountDue;

        var (content, storedName, storedType) = await ReadSlipBytesAsync(file!);
        var saved = await _files.UploadAnonymousBytesAsync(companyId, BookingSlipRule.AttachmentEntityType, view.Id, storedName, storedType, content);

        // ความจริงเรื่องเงิน = PaymentIntent (manual-slip) ยอดจากเซิร์ฟเวอร์ · เปิดอยู่ยอดเท่าเดิม ⇒ ใช้ใบเดิม (StartAsync ตัดสิน)
        var intent = await _intents.StartAsync(companyId, new StartPaymentRequest(
            PaymentSourceKind.SiteBooking, view.Id, due, PaymentMethodKind.ManualSlip,
            Description: $"สลิปชำระล่วงหน้าการจอง {view.BookingNumber}",
            CustomerEmail: view.GuestEmail ?? view.CustomerEmail, CustomerPhone: view.GuestPhone, SiteId: siteId),
            providerCode: ManualSlipPaymentProvider.Code, ct: ct);

        var after = await _bookings.MarkSlipUploadedAsync(companyId, siteId, view.Id, saved.Id, intent.Id, due);
        return await _bookings.GetBookingByTokenAsync(companyId, siteId, token) ?? after;
    }

    /// <summary>ไบต์ของสลิป — รูปผ่านตัวย่อ/ล้าง metadata (ImageProfile.Slip) · อื่น ๆ ตัดสินชนิดจากไบต์จริง (PDF) · นอก allow-list ⇒ ปฏิเสธ</summary>
    private async Task<(byte[] Content, string Name, string Type)> ReadSlipBytesAsync(IFormFile file)
    {
        if (_images != null && _images.IsProcessableImage(file.ContentType ?? ""))
        {
            await using var s = file.OpenReadStream();
            var processed = await _images.ProcessToBytesAsync(s, file.ContentType ?? "", file.FileName ?? "slip", ImageProfile.Slip);
            return (processed.Content, $"slip{processed.Extension}", processed.ContentType);
        }
        await using var raw = file.OpenReadStream();
        using var ms = new MemoryStream();
        await raw.CopyToAsync(ms);
        var bytes = ms.ToArray();
        var kind = UploadFileType.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, UploadFileType.HeaderBytes)))
            ?? throw new UnsupportedUploadException(UploadFileType.RejectMessage);
        return (bytes, $"slip{kind.Extension}", kind.ContentType);
    }

    public async Task<(byte[] Content, string ContentType)?> ReadSlipByTokenAsync(Guid companyId, Guid siteId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var attId = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
            .Select(b => b.SlipAttachmentId).FirstOrDefaultAsync(ct);
        if (attId is not Guid id) return null;
        var meta = await _files.GetByIdAsync(companyId, id);
        // ไฟล์ต้องเป็นของการจองนี้จริง (ชนิด SiteBooking) — กันชี้ไปไฟล์อื่นของบริษัท
        if (meta == null || meta.EntityType != BookingSlipRule.AttachmentEntityType) return null;
        var bytes = await _files.ReadBytesAsync(companyId, id, ct);
        return bytes == null ? null : (bytes, meta.ContentType);
    }

    public async Task<BookingResponse> VerifyAsync(Guid companyId, Guid siteId, Guid bookingId, VerifyBookingSlipRequest request, string actor,
        CancellationToken ct = default)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.SlipIntentId, x.SlipUploadedAt, x.BookingNumber })
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Booking not found.");
        if (BookingSlipRule.DecideProblem(b.SlipIntentId, b.SlipUploadedAt) is string problem)
            throw new BusinessRuleException(problem, BookingSlipRule.RuleCode, 409);
        var intent = await _intents.FindAsync(companyId, b.SlipIntentId!.Value, ct);
        if (intent == null || intent.SourceKind != PaymentSourceKind.SiteBooking || intent.SourceId != bookingId
            || intent.ProviderCode != ManualSlipPaymentProvider.Code)
            throw new BusinessRuleException("รายการชำระของสลิปไม่ตรงกับการจองนี้ — ใช้ปุ่ม “บันทึกรับเงิน” แทน", BookingSlipRule.RuleCode, 409);

        var note = string.IsNullOrWhiteSpace(request.Note) ? "ตรวจสลิปกับยอดในบัญชีธนาคารแล้ว" : request.Note.Trim();
        // เส้นเดียวกับ webhook/ยืนยันด้วยมือ ⇒ handler ของการจองบันทึกรับเงิน (กันซ้ำด้วยเลขอ้างอิง) — ไม่มีสำเนาเส้นเงินที่นี่
        await _intents.ApplyChargeAsync(intent.Id,
            new ProviderCharge(intent.ProviderRef ?? $"slip:{intent.Id:N}", PaymentIntentStatus.Succeeded, "slip_verified", intent.Amount),
            PaymentEventSource.Manual, $"manual:{actor} · {note}", ct);

        var result = await _bookings.GetBookingAsync(companyId, siteId, bookingId) ?? throw new KeyNotFoundException("Booking not found.");
        // ล้มดัง: เงินยืนยันแล้วแต่ handler บันทึกบนการจองไม่ได้ ⇒ บอกผู้กด + ทางไปต่อ (ไม่ประกาศว่าสำเร็จ)
        if (result.Payment?.SlipPending == true)
        {
            _logger.LogError("ยืนยันสลิปของการจอง {Booking} แล้วแต่ยอดยังไม่ถูกบันทึกบนการจอง — intent {Intent}", b.BookingNumber, intent.Id);
            result.ErpNotices.Add("ยืนยันรับเงินในรายการชำระแล้ว แต่บันทึกบนการจองไม่สำเร็จ — เปิดเมนู “รายการรับชำระเงินออนไลน์” แล้วกด “ส่งต่อใหม่” ของรายการนี้");
        }
        return result;
    }

    public async Task<BookingResponse> RejectAsync(Guid companyId, Guid siteId, Guid bookingId, RejectBookingSlipRequest request, string actor,
        CancellationToken ct = default)
    {
        var (reason, reasonErr) = BookingSlipRule.NormalizeRejectReason(request.Reason);
        if (reasonErr != null) throw new BusinessRuleException(reasonErr, BookingSlipRule.RuleCode);
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.SlipIntentId, x.SlipUploadedAt })
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Booking not found.");
        if (BookingSlipRule.DecideProblem(b.SlipIntentId, b.SlipUploadedAt) is string problem)
            throw new BusinessRuleException(problem, BookingSlipRule.RuleCode, 409);
        var intentId = b.SlipIntentId!.Value;
        var result = await _bookings.MarkSlipRejectedAsync(companyId, siteId, bookingId, intentId, reason!, actor);
        var intent = await _intents.FindAsync(companyId, intentId, ct);
        if (intent != null && intent.SourceId == bookingId && PaymentIntentPolicy.IsOpen(intent.Status))
            await _intents.ApplyChargeAsync(intentId,
                new ProviderCharge(intent.ProviderRef ?? $"slip:{intentId:N}", PaymentIntentStatus.Failed, "slip_rejected", intent.Amount),
                PaymentEventSource.Manual, $"manual:{actor} · ปฏิเสธสลิป: {reason}", ct);
        return result;
    }
}
