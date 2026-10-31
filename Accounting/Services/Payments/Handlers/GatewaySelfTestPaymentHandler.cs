using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments.Handlers;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179): <b>ทดลองจ่าย ฿20 โหมดทดสอบ</b> (<c>PaymentSourceKind.GatewaySelfTest</c>) สำเร็จ —
/// ประทับ <c>PaymentProviderConfig.LastSelfTestPaidAt</c> อย่างเดียว · <b>ไม่ลงบัญชี ไม่ออกเอกสาร ไม่ให้สิทธิ์</b> (ไม่มีเงินจริง ·
/// รอบโอนก็ไม่หยิบ — <c>GatewaySettlementService</c>) · ต้องมีตัวจัดการ ไม่งั้น <c>DispatchSucceededAsync</c> จะแจ้ง "เงินเข้าแต่ไม่มีผู้รับ" ทุกครั้งที่ทดสอบ
/// </summary>
public class GatewaySelfTestPaymentHandler : IPaymentCompletionHandler
{
    private readonly AccountingDbContext _db;
    public GatewaySelfTestPaymentHandler(AccountingDbContext db) { _db = db; }

    public PaymentSourceKind SourceKind => PaymentSourceKind.GatewaySelfTest;

    public async Task HandleSucceededAsync(PaymentIntent intent, CancellationToken ct = default)
    {
        if (intent.ProviderConfigId is not Guid cfgId) return;
        var cfg = await _db.PaymentProviderConfigs
            .FirstOrDefaultAsync(c => c.Id == cfgId && c.CompanyId == intent.CompanyId, ct);
        if (cfg == null) return;
        cfg.LastSelfTestPaidAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
