using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ <b>มัดจำการจองนัดหมาย</b> (<c>PaymentSourceKind.SiteBooking</c> · รอบ 205 แพ็ก W2 R-08)
///
/// <para>เรียก <c>ICmsBookingService.RecordDepositPaymentAsync</c> ตัวเดียวกับที่พนักงานใช้บันทึกรับเงิน — ตัวนั้นทำครบ
/// (PaidAmount · ยืนยันการจองเมื่อครบมัดจำ · อนุมัติใบเสร็จมัดจำลงวันที่รับเงิน · กันซ้ำด้วยเลขอ้างอิง) · ล้มดังผ่าน <c>PaymentIntentService</c>
/// (ประวัติรายการชำระ + log) ไม่กลืน — เงินเข้าแล้วแต่ยืนยันไม่ได้ ต้องมีคนเห็น</para>
/// </summary>
public class SiteBookingPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly ICmsBookingService _bookings;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<SiteBookingPaymentHandler> _logger;

    public SiteBookingPaymentHandler(AccountingDbContext db, ICmsBookingService bookings,
        IGatewayAccountResolver accounts, ILogger<SiteBookingPaymentHandler> logger)
    { _db = db; _bookings = bookings; _accounts = accounts; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.SiteBooking;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == intent.SourceId && x.CompanyId == intent.CompanyId)
            .Select(x => new { x.Id, x.SiteId, x.BookingNumber, x.PaymentReference })
            .FirstOrDefaultAsync(ct);
        if (b == null)
        {
            _logger.LogError("เงินเข้าแล้วแต่หาการจองนัดหมายไม่พบ — intent {Intent} การจอง {Booking} บริษัท {Company} ยอด {Amount:N2}",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            throw new BusinessRuleException(GatewayPaymentRecord.SourceMissingMessage("การจองนัดหมาย", intent.Id, intent.Amount),
                GatewayPaymentRecord.SourceMissingRuleCode);
        }

        // กันซ้ำด้วยเลขอ้างอิงของเงินก้อนนี้ (webhook/poll ซ้ำ) — ตัวเดียวกับที่ RecordDepositPaymentAsync ตรวจซ้ำใต้ล็อก
        var reference = GatewayPaymentRecord.Reference(intent.Id);
        if (CmsBookingOnlinePayment.AlreadyRecorded(b.PaymentReference, reference))
        {
            _logger.LogInformation("การจอง {Booking} บันทึกรับเงินของรายการ {Ref} แล้ว — ข้าม intent {Intent}", b.BookingNumber, reference, intent.Id);
            return;
        }
        // พนักงานบันทึกรับเงินยอดนี้ไว้เองหลังเงินเข้า (ปุ่ม "ส่งต่อใหม่" ใช้ตัวตัดสินเดียวกัน — W1)
        if (await GatewaySourceEvidence.AlreadyRecordedBySomeoneElseAsync(_db, intent, ct))
        {
            _logger.LogInformation("การจอง {Booking} มีบันทึกรับเงินยอด {Amount:N2} หลังเงินเข้าอยู่แล้ว (บันทึกมือ) — ข้าม intent {Intent}",
                b.BookingNumber, intent.Amount, intent.Id);
            return;
        }

        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);
        // ฝ่ายค้านรอบ 218 (B2): วันรับเงิน = เวลาที่เงินเข้าตามหลักฐาน (สลิป: วันที่โอนที่ร้านยืนยัน) ไม่ใช่วันที่กดยืนยัน — tax point §78/1 ของใบรับมัดจำ
        var isSlip = intent.ProviderCode == Accounting.Services.Payments.Providers.ManualSlipPaymentProvider.Code;
        await _bookings.RecordDepositPaymentAsync(intent.CompanyId, b.SiteId, b.Id,
            new RecordBookingPaymentRequest(intent.Amount, intent.PaidAt ?? intent.ConfirmedAt ?? DateTime.UtcNow, reference, null,
                isSlip ? "โอนเงิน (ตรวจสลิป)" : "ชำระมัดจำออนไลน์ผ่านระบบรับชำระเงิน"),
            intent.ConfirmedBy ?? "payment-gateway", moneyInAccountId: moneyIn, fromOnlinePayment: true, ct: ct);
    }
}
