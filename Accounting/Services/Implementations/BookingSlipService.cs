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
        // ฝ่ายค้านรอบ 218: มีรายการชำระที่ยืนยันแล้ว ⇒ StartAsync จะปฏิเสธ — บอกก่อนเขียนไฟล์ (ข้อความไทยเฉพาะ ไม่ใช่ข้อผิดพลาดกลาง)
        if (await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.SiteBooking
                && i.SourceId == view.Id && (i.Status == PaymentIntentStatus.Succeeded || i.Status == PaymentIntentStatus.PartiallyRefunded
                    || i.Status == PaymentIntentStatus.Refunded), ct))
            throw new BusinessRuleException(BookingSlipRule.AlreadyPaidMessage, BookingSlipRule.RuleCode, 409);

        var (content, storedName, storedType) = await ReadSlipBytesAsync(file!);
        var saved = await _files.UploadAnonymousBytesAsync(companyId, BookingSlipRule.AttachmentEntityType, view.Id, storedName, storedType, content);

        // ความจริงเรื่องเงิน = PaymentIntent (manual-slip) ยอดจากเซิร์ฟเวอร์ · เปิดอยู่ยอดเท่าเดิม ⇒ ใช้ใบเดิม (StartAsync ตัดสิน)
        var intent = await _intents.StartAsync(companyId, new StartPaymentRequest(
            PaymentSourceKind.SiteBooking, view.Id, due, PaymentMethodKind.ManualSlip,
            Description: $"สลิปชำระล่วงหน้าการจอง {view.BookingNumber}",
            CustomerEmail: view.GuestEmail ?? view.CustomerEmail, CustomerPhone: view.GuestPhone, SiteId: siteId),
            providerCode: ManualSlipPaymentProvider.Code, ct: ct);

        BookingResponse after;
        try
        {
            after = await _bookings.MarkSlipUploadedAsync(companyId, siteId, view.Id, saved.Id, intent.Id, due);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ฝ่ายค้านรอบ 218 (S4): ขั้นใต้ล็อกปฏิเสธ/ล้ม ⇒ ปิดรายการชำระที่เพิ่งเปิด (ไม่ให้ค้างเป็น "รอสลิป") · ไฟล์ลบ (รอบ 2 F2) ·
            // ลูกค้าได้ข้อความเฉพาะ ไม่ใช่ข้อผิดพลาดกลาง
            _logger.LogWarning(ex, "แนบสลิปการจอง {Booking} ไม่สำเร็จหลังเก็บไฟล์ {File} — ปิดรายการชำระ {Intent}", view.BookingNumber, saved.Id, intent.Id);
            await FailIntentQuietlyAsync(companyId, intent.Id, BookingSlipRule.AttachFailedRaw, ex.Message, "storefront-slip", ct);
            // ฝ่ายค้านรอบ 218 รอบ 2 (F2): สลิปนี้ไม่ถูกบันทึกบนการจอง (พนักงานมองไม่เห็น) ⇒ ลบไฟล์ (ไม่เก็บข้อมูลผู้โอนกำพร้า) · ลูกค้าได้ข้อความ "แนบอีกครั้ง"
            try { await _files.DeleteAsync(companyId, saved.Id); }
            catch (Exception delEx) when (delEx is not OperationCanceledException)
            {
                _logger.LogError(delEx, "ลบไฟล์สลิปที่แนบไม่สำเร็จ {File} ของการจอง {Booking} ไม่ได้", saved.Id, view.BookingNumber);
            }
            if (ex is BusinessRuleException bre) throw new BusinessRuleException(bre.Message, BookingSlipRule.RuleCode, 409);
            throw new BusinessRuleException(BookingSlipRule.AttachFailedMessage, BookingSlipRule.RuleCode, 409);
        }
        return await _bookings.GetBookingByTokenAsync(companyId, siteId, token) ?? after;
    }

    private async Task FailIntentQuietlyAsync(Guid companyId, Guid intentId, string raw, string reason, string actor, CancellationToken ct)
    {
        try
        {
            var i = await _intents.FindAsync(companyId, intentId, ct);
            if (i == null || !PaymentIntentPolicy.IsOpen(i.Status)) return;
            await _intents.ApplyChargeAsync(intentId,
                new ProviderCharge(i.ProviderRef ?? $"slip:{intentId:N}", PaymentIntentStatus.Failed, raw, i.Amount, FailureCode: raw,
                    FailureMessage: reason.Length > 300 ? reason[..300] : reason),
                PaymentEventSource.Manual, actor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ปิดรายการชำระ {Intent} ไม่สำเร็จ", intentId);
        }
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
        var row = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.PublicToken == token)
            .Select(b => new { b.Id, b.SlipAttachmentId }).FirstOrDefaultAsync(ct);
        if (row?.SlipAttachmentId is not Guid id) return null;
        var meta = await _files.GetByIdAsync(companyId, id);
        // ไฟล์ต้องเป็นสลิปของการจองนี้จริง (ชนิด SiteBooking + EntityId = การจองนี้ · ฝ่ายค้านรอบ 218) — กันชี้ไปไฟล์อื่นของบริษัท
        if (meta == null || meta.EntityType != BookingSlipRule.AttachmentEntityType || meta.EntityId != row.Id) return null;
        var bytes = await _files.ReadBytesAsync(companyId, id, ct);
        return bytes == null ? null : (bytes, meta.ContentType);
    }

    public async Task<BookingResponse> VerifyAsync(Guid companyId, Guid siteId, Guid bookingId, VerifyBookingSlipRequest request, string actor,
        CancellationToken ct = default)
    {
        var exists = await _db.SiteBookings.AsNoTracking()
            .AnyAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId, ct);
        if (!exists) throw new KeyNotFoundException("Booking not found.");
        var note = BookingSlipRule.NormalizeVerifyNote(request.Note) ?? "ตรวจสลิปกับยอดในบัญชีธนาคารแล้ว";
        // ฝ่ายค้านรอบ 218: ยืนยัน/ปฏิเสธ/ยกเลิก ใช้ล็อกเดียวกัน (session · ล็อกซ้อนได้ — handler ถือคีย์เดียวกันแบบ xact บนการเชื่อมต่อเดิม) ⇒
        // ปฏิเสธที่มาพร้อมกันเห็นสลิปที่ยืนยันแล้ว (ไม่แจ้งลูกค้าว่าไม่ผ่าน) · ยกเลิกพร้อมกันไม่ได้รับเงินเข้าการจองที่ปิด
        var (bookingNumber, intentId) = await _bookings.RunUnderBookingLockAsync(companyId, bookingId, async () =>
        {
            var b = await _db.SiteBookings.AsNoTracking()
                .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
                .Select(x => new { x.SlipIntentId, x.SlipUploadedAt, x.BookingNumber, x.Status, x.CreatedAt })
                .FirstAsync(ct);
            if (BookingSlipRule.DecideProblem(b.SlipIntentId, b.SlipUploadedAt, b.Status) is string problem)
                throw new BusinessRuleException(problem, BookingSlipRule.RuleCode, 409);
            var intent = await _intents.FindAsync(companyId, b.SlipIntentId!.Value, ct);
            if (intent == null || intent.SourceKind != PaymentSourceKind.SiteBooking || intent.SourceId != bookingId
                || intent.ProviderCode != ManualSlipPaymentProvider.Code)
                throw new BusinessRuleException("รายการชำระของสลิปไม่ตรงกับการจองนี้ — ใช้ปุ่ม “บันทึกรับเงิน” แทน", BookingSlipRule.RuleCode, 409);
            // ฝ่ายค้านรอบ 218 (B2): วันรับเงิน = วันที่โอนตามสลิป (tax point §78/1 ของใบรับมัดจำ) ไม่ใช่วันที่กดยืนยัน
            var (paidAtUtc, dateErr) = BookingSlipRule.ResolvePaidAt(request.TransferDate, b.SlipUploadedAt!.Value, b.CreatedAt, DateTime.UtcNow);
            if (dateErr != null) throw new BusinessRuleException(dateErr, BookingSlipRule.RuleCode);
            // ฝ่ายค้านรอบ 218 รอบ 2 (F4): วันที่โอนอยู่ในงวดบัญชีที่ปิด ⇒ บอกก่อนรับเงิน (ไม่ใช่ปล่อยให้ใบรับมัดจำล้มหลังเงินยืนยันแล้ว) ·
            // กติกาเดียวกับ AccountingService.ValidateFiscalPeriodOpenForDateAsync (งวดครอบวันนั้นและไม่ใช่ Open)
            var transferDay = paidAtUtc.AddHours(7).Date;
            var closedPeriod = await _db.FiscalPeriods.AsNoTracking()
                .Where(f => f.CompanyId == companyId && f.StartDate <= transferDay && f.EndDate >= transferDay && f.Status != FiscalPeriodStatus.Open)
                .Select(f => f.Name).FirstOrDefaultAsync(ct);
            if (closedPeriod != null)
                throw new BusinessRuleException(BookingSlipRule.ClosedPeriodMessage(transferDay, closedPeriod), BookingSlipRule.RuleCode, 409);
            // เส้นเดียวกับ webhook ⇒ handler ของการจองบันทึกรับเงิน (กันซ้ำด้วยเลขอ้างอิง) — ไม่มีสำเนาเส้นเงินที่นี่
            await _intents.ApplyChargeAsync(intent.Id,
                new ProviderCharge(intent.ProviderRef ?? $"slip:{intent.Id:N}", PaymentIntentStatus.Succeeded, "slip_verified", intent.Amount,
                    PaidAtUtc: paidAtUtc),
                PaymentEventSource.Manual, $"manual:{actor}", ct);
            // ฝ่ายค้านรอบ 218 (S3) + รอบ 2: บันทึกของผู้ยืนยันลงหมายเหตุ <b>หลัง</b>รับเงินสำเร็จ (ล้ม = ไม่มีหมายเหตุ "ยืนยันแล้ว" ค้าง) · ConfirmedBy = ผู้กดเท่านั้น
            await _bookings.AppendBookingNoteAsync(companyId, siteId, bookingId,
                $"[{BookingSlipRule.RuleCode}] ยืนยันสลิปโดย {actor} · วันที่โอน {paidAtUtc.AddHours(7):dd/MM/yyyy} · {note}", actor);
            return (b.BookingNumber, intent.Id);
        });

        var result = await _bookings.GetBookingAsync(companyId, siteId, bookingId) ?? throw new KeyNotFoundException("Booking not found.");
        // ล้มดัง: เงินยืนยันแล้วแต่ handler บันทึกบนการจองไม่ได้ ⇒ บอกผู้กด + ทางไปต่อ (ไม่ประกาศว่าสำเร็จ)
        if (result.Payment?.SlipPending == true)
        {
            _logger.LogError("ยืนยันสลิปของการจอง {Booking} แล้วแต่ยอดยังไม่ถูกบันทึกบนการจอง — intent {Intent}", bookingNumber, intentId);
            result.ErpNotices.Add("ยืนยันรับเงินในรายการชำระแล้ว แต่บันทึกบนการจองไม่สำเร็จ — เปิดเมนู “รายการรับชำระเงินออนไลน์” แล้วกด “ส่งต่อใหม่” ของรายการนี้");
        }
        return result;
    }

    public async Task<BookingResponse> RejectAsync(Guid companyId, Guid siteId, Guid bookingId, RejectBookingSlipRequest request, string actor,
        CancellationToken ct = default)
    {
        var (reason, reasonErr) = BookingSlipRule.NormalizeRejectReason(request.Reason);
        if (reasonErr != null) throw new BusinessRuleException(reasonErr, BookingSlipRule.RuleCode);
        var exists = await _db.SiteBookings.AsNoTracking()
            .AnyAsync(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId, ct);
        if (!exists) throw new KeyNotFoundException("Booking not found.");
        // ล็อกเดียวกับยืนยัน — ยืนยันชนะก่อน ⇒ ที่นี่เห็นว่าไม่มีสลิปรอตรวจ (409) และไม่แจ้งลูกค้าว่าไม่ผ่าน
        return await _bookings.RunUnderBookingLockAsync(companyId, bookingId, async () =>
        {
            var b = await _db.SiteBookings.AsNoTracking()
                .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
                .Select(x => new { x.SlipIntentId, x.SlipUploadedAt, x.Status })
                .FirstAsync(ct);
            if (BookingSlipRule.DecideProblem(b.SlipIntentId, b.SlipUploadedAt, b.Status) is string problem)
                throw new BusinessRuleException(problem, BookingSlipRule.RuleCode, 409);
            var intentId = b.SlipIntentId!.Value;
            var result = await _bookings.MarkSlipRejectedAsync(companyId, siteId, bookingId, intentId, reason!, actor);
            var intent = await _intents.FindAsync(companyId, intentId, ct);
            if (intent != null && intent.SourceId == bookingId && PaymentIntentPolicy.IsOpen(intent.Status))
                await _intents.ApplyChargeAsync(intentId,
                    new ProviderCharge(intent.ProviderRef ?? $"slip:{intentId:N}", PaymentIntentStatus.Failed, "slip_rejected", intent.Amount,
                        FailureCode: "slip_rejected", FailureMessage: reason),
                    PaymentEventSource.Manual, $"manual:{actor}", ct);
            return result;
        });
    }
}
