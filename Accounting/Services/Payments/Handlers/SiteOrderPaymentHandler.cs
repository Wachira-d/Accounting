using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **คำสั่งซื้อหน้าเว็บขายของ**
///
/// <para>เรียก <c>ConfirmPaymentAsync</c> ที่มีอยู่เดิม — ตัวนั้นทำครบอยู่แล้ว
/// (idempotent · sync เข้า ERP · ลง JE · ตัดสต็อก · ออก e-Tax) และผ่านการใช้งานจริง
/// มาแล้ว · <b>หน้าที่ของ handler คือเรียกของที่มี ไม่ใช่เขียนใหม่</b></para>
/// </summary>
public class SiteOrderPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly ICmsCommerceService _commerce;
    private readonly IGatewayAccountResolver _accounts;
    private readonly ILogger<SiteOrderPaymentHandler> _logger;

    public SiteOrderPaymentHandler(AccountingDbContext db, ICmsCommerceService commerce,
        IGatewayAccountResolver accounts, ILogger<SiteOrderPaymentHandler> logger)
    { _db = db; _commerce = commerce; _accounts = accounts; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.SiteOrder;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        // siteId อาจไม่ได้ส่งมาตอนสร้าง intent — หาจากตัวออเดอร์เอง
        // (เดาไม่ได้ และ "ค่า default ที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")
        var siteId = intent.SiteId ?? await _db.SiteOrders.AsNoTracking()
            .Where(o => o.Id == intent.SourceId && o.CompanyId == intent.CompanyId)
            .Select(o => (Guid?)o.SiteId)
            .FirstOrDefaultAsync(ct);

        if (siteId is not Guid sid)
        {
            // ห้ามเงียบ: เงินเข้าแล้วแต่ออเดอร์ไม่ถูกยืนยัน = ลูกค้าจ่ายแล้วของไม่ออก ·
            // รอบ 205 ทีม E: โยนแทน LogError+return (เดิมประวัติของรายการชำระไม่มีร่องรอย) ⇒ PaymentIntentService บันทึกลงประวัติ + แจ้งเจ้าของ
            _logger.LogError(
                "เงินเข้าแล้วแต่หาคำสั่งซื้อไม่พบ — intent {Intent} order {Order} บริษัท {Company}",
                intent.Id, intent.SourceId, intent.CompanyId);
            throw new BusinessRuleException(GatewayPaymentRecord.SourceMissingMessage("คำสั่งซื้อ", intent.Id, intent.Amount),
                Accounting.Helpers.GatewayPaymentRecord.SourceMissingRuleCode);
        }

        // รอบ 205 ทีม E (BOOKING_REVENUE_AUDIT S-1): แถวรับเงินของรายการชำระนี้ — เดิมส่ง paymentId: null ⇒ ConfirmPaymentAsync หยิบแถว
        // SiteOrderPayment ล่าสุด ซึ่งเกิดจากการแนบสลิปเท่านั้น ⇒ ออเดอร์ที่จ่ายผ่าน gateway โดยไม่แนบสลิป = "ไม่พบรายการชำระเงินที่จะยืนยัน"
        // (เงินเข้าแต่ไม่มีเอกสาร/JE/ตัดสต็อก) · มีสลิปค้าง = ยืนยันแถวสลิปแทนเงินก้อนจริง (ยอด/เลขอ้างอิงผิดก้อน)
        // คีย์ = GatewayPaymentRecord.Reference ⇒ ทำซ้ำได้ (ConfirmPaymentAsync กันบันทึกรับชำระ ERP ซ้ำด้วย Reference+Amount เดิม)
        var reference = GatewayPaymentRecord.Reference(intent.Id);
        var pay = await _db.SiteOrderPayments
            .FirstOrDefaultAsync(p => p.CompanyId == intent.CompanyId && p.OrderId == intent.SourceId
                && p.Reference == reference && !p.IsDeleted, ct);
        if (pay == null)
        {
            pay = new SiteOrderPayment
            {
                CompanyId = intent.CompanyId,
                OrderId = intent.SourceId,
                Amount = intent.Amount,
                Currency = intent.Currency,
                PaymentMethod = GatewayPaymentRecord.MethodOf(intent.MethodKind),
                GatewayTransactionId = intent.ProviderRef,
                Reference = reference,
                Status = SitePaymentStatus.Pending,
                PaidAt = intent.ConfirmedAt ?? DateTime.UtcNow,
                CreatedBy = "payment-gateway",
            };
            _db.SiteOrderPayments.Add(pay);
            await _db.SaveChangesAsync(ct);
        }

        // เงินที่รับผ่าน gateway ยังไม่เข้าธนาคาร (T+n หลังหักค่าธรรมเนียม) ⇒ ขาเงินเข้า
        // ต้องลงบัญชีพัก 11340 ไม่ใช่ธนาคาร · ตัวตัดสินอยู่ที่ resolver ตัวเดียวของระบบ
        // (ห้าม handler ไปหาผังเอง — 5 ทางเข้าจะได้กติกา 5 ชุดที่ drift แน่นอน)
        var moneyIn = await _accounts.ResolveMoneyInAccountAsync(intent, ct);

        var ok = await _commerce.ConfirmPaymentAsync(intent.CompanyId, sid, intent.SourceId,
            paymentId: pay.Id, actor: intent.ConfirmedBy ?? "payment-gateway",
            moneyInAccountId: moneyIn);
        if (!ok)
            throw new BusinessRuleException(GatewayPaymentRecord.SourceMissingMessage("คำสั่งซื้อ", intent.Id, intent.Amount),
                Accounting.Helpers.GatewayPaymentRecord.SourceMissingRuleCode);
    }
}
