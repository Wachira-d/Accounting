using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **ค่าบริการระบบ (SaaS) ของตัวแพลตฟอร์มเอง**
/// (<c>PaymentSourceKind.SubscriptionPayment</c>)
///
/// <para>เรียก <c>ISubscriptionService.ReviewPaymentAsync(..., systemConfirmed: true)</c> —
/// ตัวนั้นทำครบ (ต่ออายุแพ็กเกจ · ปรับ limit ตาม template · ออกใบเสร็จของแพลตฟอร์ม)</para>
///
/// ═══ จุดที่ต่างจาก handler ตัวอื่นโดยสิ้นเชิง ═══
/// นี่คือ **รายได้ของแพลตฟอร์ม ไม่ใช่ของผู้เช่า** ⇒ เงินเข้าบัญชีของเรา ไม่ใช่ของลูกค้า
/// จึง<b>ไม่มีคำถามเรื่องบัญชีพัก 11340 ของผู้เช่า</b>เลย (จงใจไม่เรียก
/// <c>IGatewayAccountResolver</c>) · การกระทบยอดรอบโอนของเงินก้อนนี้เป็นเรื่อง
/// ของสมุดบัญชีแพลตฟอร์ม ไม่ใช่ของ tenant
///
/// <para>และ<b>ไม่มี "ผู้ตรวจสอบ" ที่เป็นคน</b> — ปกติแอดมินเป็นคนดูสลิปแล้วกดอนุมัติ
/// แต่เส้นนี้เงินเข้าจริงยืนยันโดย provider ⇒ ส่ง <c>systemConfirmed: true</c>
/// เพื่อให้ <c>ReviewedByUserId</c> เป็น null แทนการแต่ง user id ปลอมให้ช่องไม่ว่าง</para>
/// </summary>
public class SubscriptionPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subs;
    private readonly ILogger<SubscriptionPaymentHandler> _logger;

    public SubscriptionPaymentHandler(AccountingDbContext db, ISubscriptionService subs,
        ILogger<SubscriptionPaymentHandler> logger)
    { _db = db; _subs = subs; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.SubscriptionPayment;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        // รอบ 219 เฟส 0 (G-3): เดิมหาด้วย Id อย่างเดียว ⇒ intent ของบริษัทหนึ่งอนุมัติรายการของอีกบริษัทได้ · ตอนนี้แถวต้องเป็นของบริษัทผู้จ่าย
        // (PayerCompanyId — intent อยู่ใต้บริษัทผู้ให้บริการ ตรวจแล้วใน DispatchSucceededAsync ด้วย PlatformRevenueSource)
        var pay = await _db.SubscriptionPayments.AsNoTracking()
            .Where(p => p.Id == intent.SourceId)
            .Select(p => new { p.Id, p.PaymentNumber, p.Status, p.Amount, p.Subscription.CompanyId, p.RequestedPlan, p.RequestedBillingCycle })
            .FirstOrDefaultAsync(ct);
        if (pay != null && !Accounting.Helpers.PlatformRevenueSource.SourceBelongsToPayer(intent.PayerCompanyId, pay.CompanyId))
        {
            _logger.LogError("รายการชำระค่าบริการ {No} ไม่ใช่ของบริษัทผู้จ่ายของ intent {Intent} (ผู้จ่าย {Payer}) — ไม่อนุมัติ",
                pay.PaymentNumber, intent.Id, intent.PayerCompanyId);
            throw new BusinessRuleException("รายการชำระค่าบริการนี้ไม่ใช่ของบริษัทผู้จ่าย — ไม่อนุมัติอัตโนมัติ");
        }
        if (pay == null)
        {
            _logger.LogError(
                "เงินค่าบริการเข้าแล้วแต่หารายการชำระไม่พบ — intent {Intent} รายการ {Pay} "
                + "ยอด {Amount:N2} · ต้องตรวจและอนุมัติด้วยมือในหน้าแอดมิน",
                intent.Id, intent.SourceId, intent.Amount);
            return;
        }

        // อนุมัติไปแล้ว = webhook ซ้ำ → no-op (อนุมัติซ้ำจะต่ออายุแพ็กเกจสองรอบ)
        if (pay.Status is not (SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview))
        {
            _logger.LogInformation("รายการค่าบริการ {No} อยู่ในสถานะ {Status} แล้ว — ข้าม intent {Intent}",
                pay.PaymentNumber, pay.Status, intent.Id);
            return;
        }

        // ยอดไม่ตรงต้องดัง: อนุมัติเต็มแพ็กเกจทั้งที่ได้เงินไม่ครบ = ให้บริการฟรีบางส่วน
        if (Math.Abs(intent.Amount - pay.Amount) > 0.01m)
        {
            _logger.LogError(
                "ยอดเงินที่เข้า ({Paid:N2}) ไม่ตรงกับยอดของรายการ {No} ({Due:N2}) — "
                + "ไม่อนุมัติอัตโนมัติ ให้แอดมินตรวจเอง (intent {Intent})",
                intent.Amount, pay.PaymentNumber, pay.Amount, intent.Id);
            return;
        }

        // รอบ 219 เฟส 0 (G-4): ยอดของรายการเคยมาจาก body ของผู้เช่า — ยืนยันอัตโนมัติได้เฉพาะเมื่อยอดตรงราคาแพ็กเกจที่เซิร์ฟเวอร์คำนวณ
        // (ตัวตั้งเดียว PlatformPlanPricing · ยอดที่มีส่วนลดโปรโมชัน = ไม่ตรงราคาเต็ม ⇒ แอดมินตรวจเอง — ไม่เดาว่าส่วนลดถูกต้อง)
        var template = await _db.PlanTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Plan == pay.RequestedPlan && t.IsActive, ct);
        var listPrice = template == null ? 0m : Accounting.Helpers.PlatformPlanPricing.AmountDue(template, pay.RequestedBillingCycle);
        if (template == null || listPrice <= 0m || !Accounting.Helpers.PlatformPlanPricing.ClaimedAmountAcceptable(pay.Amount, listPrice) || pay.Amount <= 0m)
        {
            _logger.LogError("ยอดรายการค่าบริการ {No} ({Amount:N2}) ไม่สอดคล้องกับราคาแพ็กเกจ ({List:N2}) — ไม่อนุมัติอัตโนมัติ (intent {Intent})",
                pay.PaymentNumber, pay.Amount, listPrice, intent.Id);
            throw new BusinessRuleException("ยอดของรายการชำระไม่สอดคล้องกับราคาแพ็กเกจ — ให้ผู้ดูแลระบบตรวจและอนุมัติเอง");
        }

        await _subs.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(
            Approve: true,
            ReviewNotes: $"ยืนยันอัตโนมัติจากการชำระเงินออนไลน์ · อ้างอิง {intent.ProviderRef ?? intent.Id.ToString("N")}",
            RejectionReason: null),
            performedBy: intent.ConfirmedBy ?? "payment-gateway",
            systemConfirmed: true,
            gatewayPaidAtUtc: intent.PaidAt);   // ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 5): วันรับเงิน = เวลาเงินเข้าจริง ไม่ใช่วันเลือกแพ็กเกจ
    }
}
