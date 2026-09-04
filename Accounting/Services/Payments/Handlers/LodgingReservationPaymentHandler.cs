using Accounting.Data;
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
            .Select(x => new { x.Id, x.ReservationNumber, x.Status, x.DepositPaid })
            .FirstOrDefaultAsync(ct);
        if (r == null)
        {
            _logger.LogError(
                "เงินเข้าแล้วแต่หาการจองไม่พบ — intent {Intent} การจอง {Res} บริษัท {Company} "
                + "ยอด {Amount:N2} · ต้องบันทึกรับเงินด้วยมือ",
                intent.Id, intent.SourceId, intent.CompanyId, intent.Amount);
            return;
        }

        // ยืนยันไปแล้วและมัดจำครบ = webhook ซ้ำ → no-op (ไม่ใช่ error)
        if (r.Status is LodgingReservationStatus.Confirmed or LodgingReservationStatus.CheckedIn
                or LodgingReservationStatus.CheckedOut
            && r.DepositPaid >= intent.Amount - 0.005m)
        {
            _logger.LogInformation("การจอง {Res} ยืนยันและรับมัดจำแล้ว — ข้าม intent {Intent}",
                r.ReservationNumber, intent.Id);
            return;
        }

        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);

        await _lodging.ConfirmAsync(intent.CompanyId, r.Id, new LodgingConfirmRequest(
            DepositAmount: intent.Amount,
            PaymentMethod: PaymentMethod.BankTransfer,
            PaymentReference: intent.ProviderRef,
            PaymentDate: intent.ConfirmedAt ?? DateTime.UtcNow,
            BankAccountId: null,
            Note: "ชำระมัดจำออนไลน์ผ่านระบบรับชำระเงิน"),
            intent.ConfirmedBy ?? "payment-gateway",
            moneyInAccountId: moneyIn);
    }
}
