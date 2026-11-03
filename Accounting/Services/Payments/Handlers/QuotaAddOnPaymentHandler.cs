using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// รอบ 229 (QUOTA_ADDON §B.7) — เงินเข้าแล้วสำหรับ <b>การซื้อโควตาเสริม</b> (<c>PaymentSourceKind.QuotaAddOnPurchase</c> · <c>SourceId</c> = <c>CompanyQuotaAddOn.Id</c>)
/// <para>intent อยู่ใต้บริษัทผู้ให้บริการ (ตรวจแล้วใน <c>PaymentIntentService.DispatchSucceededAsync</c> ผ่าน <c>PlatformRevenueSource.EntitlementBlockReason</c> —
/// โหมดทดสอบ/ไม่รู้ผู้จ่าย ไม่มาถึงที่นี่) · แถวการซื้อเป็นของ<b>บริษัทผู้จ่าย</b> (<c>PayerCompanyId</c>) ไม่ใช่ <c>intent.CompanyId</c> (บทเรียน G-3)</para>
/// </summary>
public class QuotaAddOnPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly IQuotaAddOnPurchaseService _purchases;
    private readonly ILogger<QuotaAddOnPaymentHandler> _logger;

    public QuotaAddOnPaymentHandler(AccountingDbContext db, IQuotaAddOnPurchaseService purchases, ILogger<QuotaAddOnPaymentHandler> logger)
    { _db = db; _purchases = purchases; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.QuotaAddOnPurchase;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        // รอบ 232 (232-Omise-2): ด่านชั้นสอง — เงินโหมดทดสอบห้ามมีผลทางธุรกิจ (dispatcher กันชั้นแรก · ตัวตัดสินเดียว TestModePaymentPolicy)
        await Accounting.Helpers.TestModePaymentPolicy.GuardAsync(_db, intent, ct);
        var payer = intent.PayerCompanyId;
        var owned = payer is Guid payerId && Accounting.Helpers.PlatformRevenueSource.SourceBelongsToPayer(payer,
            await _db.CompanyQuotaAddOns.AsNoTracking().Where(p => p.Id == intent.SourceId && p.CompanyId == payerId)
                .Select(p => p.CompanyId).FirstOrDefaultAsync(ct));
        if (!owned)
        {
            // เงินเข้าแล้วแต่หารายการของบริษัทผู้จ่ายไม่เจอ = ต้องดัง (ผู้ส่งต่อบันทึกประวัติ ⚠️ ให้แอดมิน) — มีคนต้องคืนเงินหรือเปิดให้ด้วยมือ
            _logger.LogError("เงินค่าโควตาเสริมเข้าแล้วแต่หารายการของบริษัทผู้จ่ายไม่พบ — intent {Intent} ผู้จ่าย {Payer} source {Source} ยอด {Amount:N2}",
                intent.Id, intent.PayerCompanyId, intent.SourceId, intent.Amount);
            throw new Accounting.Helpers.BusinessRuleException("เงินเข้าแล้วแต่หารายการซื้อโควตาเสริมของบริษัทผู้จ่ายไม่พบ — ผู้ดูแลระบบต้องตรวจ", "QUOTA-PAID-NO-ROW");
        }
        await _purchases.MarkPaidByGatewayAsync(payer!.Value, intent.SourceId, intent.Id, intent.Amount, intent.ConfirmedBy ?? "payment-gateway", ct);
    }
}
