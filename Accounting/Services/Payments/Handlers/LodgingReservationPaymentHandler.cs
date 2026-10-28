using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **มัดจำการจองที่พัก** (<c>PaymentSourceKind.LodgingReservation</c>)
///
/// <para>เรียก <c>ILodgingService.ConfirmAsync</c> ที่มีอยู่เดิม — ตัวนั้นทำครบ
/// (ตรวจห้องว่างซ้ำ · ออกใบเสร็จมัดจำ <c>Receipt IsDeposit</c> ที่ Cr ขายรอรับรู้ 217xx ·
/// เลื่อนสถานะเป็น Confirmed · ปลด hold · แจ้งแขก) และเคารพ <c>AccountingMode.Off</c>
/// ของที่พักที่ไม่ออกเอกสารบัญชี</para>
///
/// <para><b>ตรวจห้องว่างอีกครั้งเป็นเรื่องสำคัญ</b>: ระหว่างที่แขกสแกนจ่าย ห้องอาจถูกจอง
/// ไปแล้ว · <c>ConfirmAsync</c> จะ throw <c>LODGING-OVERSOLD</c> ⇒ เงินเข้าแล้วแต่ยืนยัน
/// ไม่ได้ · เราปล่อยให้ throw ขึ้นไป ให้ <c>PaymentIntentService</c> บันทึกลงประวัติของ
/// intent แล้วดังใน log — <b>ห้ามกลืน</b> เพราะต้องมีคนคืนเงินหรือหาห้องให้แขก</para>
/// </summary>
public class LodgingReservationPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly ILodgingService _lodging;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<LodgingReservationPaymentHandler> _logger;

    public LodgingReservationPaymentHandler(AccountingDbContext db, ILodgingService lodging,
        IGatewayAccountResolver accounts, ILogger<LodgingReservationPaymentHandler> logger)
    { _db = db; _lodging = lodging; _accounts = accounts; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.LodgingReservation;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        var r = await _db.LodgingReservations.AsNoTracking()
            .Where(x => x.Id == intent.SourceId && x.CompanyId == intent.CompanyId)
            .Select(x => new { x.Id, x.ReservationNumber, x.Status, x.DepositPaid, x.PaymentReference })
            .FirstOrDefaultAsync(ct);
        if (r == null)
        {
            _logger.LogError(
                "เงินเข้าแล้วแต่หาการจองไม่พบ — intent {Intent} การจอง {Res} บริษัท {Company} "
                + "ยอด {Amount:N2} · ต้องบันทึกรับเงินด้วยมือ",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            // รอบ 205 ทีม E: โยนแทน return เงียบ — PaymentIntentService บันทึกลงประวัติของรายการชำระ + แจ้งเจ้าของ
            throw new BusinessRuleException(GatewayPaymentRecord.SourceMissingMessage("การจอง", intent.Id, intent.Amount),
                Accounting.Helpers.GatewayPaymentRecord.SourceMissingRuleCode);
        }

        // กันซ้ำด้วย "เลขอ้างอิงของเงินก้อนนี้" เท่านั้น (รอบ 205 ทีม E · BOOKING_REVENUE_AUDIT L-1) — เดิมข้ามเมื่อ DepositPaid ≥ ยอดรายการ
        // ⇒ มัดจำ 1,500 แล้วแขกจ่ายยอดคงเหลือ 1,500 ออนไลน์ = ถูกตีเป็น webhook ซ้ำ ⇒ เงินเข้าแต่ไม่มีใบ/JE/ธง ·
        // webhook/poll ซ้ำของรายการเดียวกันถูกกันที่ PaymentIntentService (Succeeded→Succeeded ไม่ส่งต่อ) อยู่แล้ว · ชั้นนี้คือตาข่ายที่สอง
        // (ConfirmAsync ใช้ตัวตัดสินเดียวกันใต้ล็อกการจอง)
        var reference = GatewayPaymentRecord.ReservationReference(intent.Id, intent.ProviderRef);
        if (GatewayPaymentRecord.AlreadyRecordedOnReservation(r.PaymentReference, r.DepositPaid, reference))
        {
            _logger.LogInformation("การจอง {Res} บันทึกรับเงินของรายการ {Ref} แล้ว — ข้าม intent {Intent}",
                r.ReservationNumber, reference, intent.Id);
            return;
        }

        // ฝ่ายค้าน W1 ข้อ 2 (รอบ 205 ทีม W1): พนักงานบันทึกรับเงินยอดนี้ที่การจองเองแล้ว (หลังเงินเข้า · เลขอ้างอิงไม่ใช่ของ gateway) ⇒ ส่งต่อใหม่ต้องไม่รับซ้ำ
        // ตัวตัดสินเดียวกับปุ่ม "ส่งต่อใหม่" (GatewaySourceEvidence → PaymentRedispatchPolicy.SourceRecorded)
        if (await GatewaySourceEvidence.AlreadyRecordedBySomeoneElseAsync(_db, intent, ct))
        {
            _logger.LogInformation("การจอง {Res} มีบันทึกรับเงินยอด {Amount:N2} หลังเงินเข้าอยู่แล้ว (บันทึกมือ) — ข้าม intent {Intent}",
                r.ReservationNumber, intent.Amount, intent.Id);
            return;
        }

        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);

        try
        {
            await _lodging.ConfirmAsync(intent.CompanyId, r.Id, new LodgingConfirmRequest(
                DepositAmount: intent.Amount,
                PaymentMethod: PaymentMethod.BankTransfer,
                PaymentReference: reference,
                PaymentDate: intent.ConfirmedAt ?? DateTime.UtcNow,
                BankAccountId: null,
                Note: "ชำระมัดจำออนไลน์ผ่านระบบรับชำระเงิน"),
                intent.ConfirmedBy ?? "payment-gateway",
                moneyInAccountId: moneyIn,
                fromOnlinePayment: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // รอบ 202 (O-P1-4 · คำตัดสินข้อ 127): เงินเข้าแล้วแต่ยืนยันไม่ได้ — เดิมเห็นแค่ใน event ของรายการชำระ ⇒ ติดธงบนตัวการจองด้วย
            // (ตัวกรอง "เงินเข้าแต่ยืนยันไม่ได้" + หน้าแขก "ได้รับเงินแล้ว" + อีเมลแจ้งที่พัก) แล้ว**โยนต่อ** ให้ PaymentIntentService บันทึกลงประวัติ
            // ของรายการชำระ (ล้มดัง 3 ที่) · ไม่คืนเงินอัตโนมัติ — ที่พักตัดสิน · ธงล้มเองก็ต้องไม่กลบ error เดิม
            try
            {
                await _lodging.FlagPaymentProblemAsync(intent.CompanyId, r.Id,
                    $"รายการชำระออนไลน์ {intent.Id:N} ยอด {intent.Amount:N2} เข้าแล้ว แต่ยืนยันการจองไม่ได้: {ex.Message}", "online-payment",
                    paymentIntentId: intent.Id);
            }
            catch (Exception flagError) when (flagError is not OperationCanceledException)
            {
                _logger.LogError(flagError, "ติดธงเงินเข้าแต่ยืนยันไม่ได้ของการจอง {Res} ไม่สำเร็จ (intent {Intent})", r.ReservationNumber, intent.Id);
            }
            throw;
        }
    }
}
