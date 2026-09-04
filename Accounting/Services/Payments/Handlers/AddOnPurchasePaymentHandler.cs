using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// เงินเข้าแล้วสำหรับ **การซื้อส่วนเสริม (add-on)** — <c>PaymentSourceKind.AddOnPurchase</c>
///
/// <para>ปิดวงจร "เปิด gateway แล้วลูกค้าจ่ายและใช้ได้เลย" ที่เจ้าของโปรเจกต์กำหนด:
/// เงินเข้า → <c>AddOnPurchaseService.MarkPaidByGatewayAsync</c> → สถานะเป็น
/// <c>Paid</c> และเปิดสิทธิ์ทันที ไม่ต้องรอคนตรวจสลิป</para>
///
/// <para><c>SourceId</c> ของ intent ชนิดนี้คือ <b><c>CompanyFeature.Id</c></b>
/// (ไม่ใช่ featureCode ที่เป็นสตริง) — <c>PaymentIntent.SourceId</c> เป็น
/// <c>Guid</c> จึงต้อง resolve กลับเป็นรหัสฟีเจอร์ที่นี่</para>
/// </summary>
public class AddOnPurchasePaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    private readonly IAddOnPurchaseService _addons;
    private readonly ILogger<AddOnPurchasePaymentHandler> _logger;

    public AddOnPurchasePaymentHandler(AccountingDbContext db, IAddOnPurchaseService addons,
        ILogger<AddOnPurchasePaymentHandler> logger)
    { _db = db; _addons = addons; _logger = logger; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.AddOnPurchase;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        var feature = await _db.CompanyFeatures.AsNoTracking()
            .Where(f => f.Id == intent.SourceId && f.CompanyId == intent.CompanyId)
            .Select(f => f.FeatureCode)
            .FirstOrDefaultAsync(ct);

        if (feature == null)
        {
            // เงินเข้าแล้วแต่หาสิทธิ์ไม่เจอ = ต้องดัง มีคนต้องคืนเงินหรือเปิดให้ด้วยมือ
            _logger.LogError(
                "เงินค่าส่วนเสริมเข้าแล้วแต่หาแถวสิทธิ์ไม่พบ — intent {Intent} บริษัท {Company} "
                + "source {Source} ยอด {Amount:N2}",
                intent.Id, intent.CompanyId, intent.SourceId, intent.Amount);
            return;
        }

        await _addons.MarkPaidByGatewayAsync(intent.CompanyId, feature, intent.Id,
            intent.Amount, intent.ConfirmedBy ?? "payment-gateway", ct);
    }
}
