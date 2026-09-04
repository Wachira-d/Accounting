using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

public interface IGatewayAccountResolver
{
    /// <summary>ผังบัญชีที่ขา "เงินเข้า" ของ intent นี้ต้องลง — <b>ตัวตัดสินตัวเดียวของระบบ</b>
    ///
    /// <para><c>null</c> = ลงธนาคาร/เงินสดตามปกติ (เส้นสลิป: ลูกค้าโอนเข้าบัญชีเราแล้ว)<br/>
    /// มีค่า = ลงบัญชีพัก <b>11340 ลูกหนี้ผู้ให้บริการรับชำระเงิน</b> เพราะเงินยังอยู่กับ
    /// ผู้ให้บริการ จะเข้าธนาคาร T+n หลังหักค่าธรรมเนียม</para></summary>
    Task<Guid?> ResolveMoneyInAccountAsync(PaymentIntent intent, CancellationToken ct = default);
}

/// <summary>
/// **ผังบัญชีขา "เงินเข้า" ของการรับชำระผ่าน gateway — ที่เดียวของระบบ**
///
/// ═══ ทำไมต้องแยกเป็นบริการเล็ก ๆ ของตัวเอง ═══
/// ทางเข้าทั้ง 5 (เว็บขายของ · portal · มัดจำที่พัก · SaaS · POS) ต้องถามคำถามเดียวกัน
/// ว่า "เงินก้อนนี้เข้าธนาคารแล้วหรือยัง" · ถ้าให้แต่ละ handler ไปหาผังเอง จะได้กติกา
/// <b>5 ชุดที่ drift แน่นอน</b> ซึ่งเป็น defect class ที่เรพนี้เจอซ้ำที่สุด
///
/// <para>และ<b>ต้องไม่ผูกอยู่ใน <c>PaymentIntentService</c></b>: ตัวนั้นรับ
/// <c>IEnumerable&lt;IPaymentCompletionHandler&gt;</c> ส่วน handler ต้องเรียกตัวนี้ ⇒
/// ถ้ารวมกันจะเป็น <b>วงกลม DI</b> (handler → intent service → handler) ที่พังตอน
/// รันจริง ไม่ใช่ตอนคอมไพล์ — <c>tools/di_cycle_check.py</c> มีไว้จับคลาสนี้พอดี</para>
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item>ตัวตัดสินว่า "เข้าธนาคารทันทีไหม" คือ <c>IPaymentProvider.SettlesDirectlyToBank</c>
///   ที่ adapter ประกาศเอง — <b>ไม่มีลิสต์ชื่อเจ้าตรงกลาง</b> (เกณฑ์ผ่านเฟส 6)</item>
/// <item>ไม่มีผัง 11340 → คืน null (พฤติกรรมเดิม) แต่ <b>log ระดับ Error</b> —
///   เงียบไม่ได้เพราะยอดธนาคารจะสูงเกินจริงตั้งแต่วันนั้นเป็นต้นไป</item>
/// </list>
/// </summary>
public class GatewayAccountResolver : IGatewayAccountResolver
{
    /// <summary>ผังมาตรฐาน "ลูกหนี้ผู้ให้บริการรับชำระเงิน" (ChartOfAccountTemplates)</summary>
    public const string StandardClearingCode = "11340";

    private readonly AccountingDbContext _db;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly ILogger<GatewayAccountResolver> _logger;

    public GatewayAccountResolver(AccountingDbContext db, IEnumerable<IPaymentProvider> providers,
        ILogger<GatewayAccountResolver> logger)
    { _db = db; _providers = providers; _logger = logger; }

    public async Task<Guid?> ResolveMoneyInAccountAsync(PaymentIntent intent,
        CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == intent.ProviderCode);
        if (provider == null)
        {
            // ไม่รู้จัก provider = ไม่รู้ว่าเงินอยู่ไหน · "ไม่รู้ต้องบอกว่าไม่รู้" —
            // คงพฤติกรรมเดิม (ธนาคาร) แล้วดังไว้ ดีกว่าเดาแล้วลงผังผิดเงียบ ๆ
            _logger.LogWarning(
                "ไม่รู้จักช่องทางชำระเงิน \"{Code}\" ตอนหาผังบัญชีขาเงินเข้า — ใช้ผังธนาคาร/เงินสดตามเดิม",
                intent.ProviderCode);
            return null;
        }
        if (provider.SettlesDirectlyToBank) return null;

        var config = intent.ProviderConfigId is Guid cid
            ? await _db.PaymentProviderConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == cid && c.CompanyId == intent.CompanyId, ct)
            : await _db.PaymentProviderConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CompanyId == intent.CompanyId
                    && c.ProviderCode == intent.ProviderCode && !c.IsDeleted, ct);

        if (config?.ClearingAccountId is Guid chosen) return chosen;

        // ยังไม่ได้ตั้งค่า → ใช้ผังมาตรฐาน · **ไม่ตกไปลงธนาคาร** เพราะเงินยังไม่เข้าจริง
        // (ลงธนาคารเกินไว้ = กระทบยอดพังเงียบตลอดไป ซึ่งกู้ยากกว่าการมียอดค้างใน
        // บัญชีพักที่มองเห็นได้และล้างได้ตอน settlement)
        var standard = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == intent.CompanyId
                && a.AccountCode == StandardClearingCode && !a.IsDeleted)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

        if (standard == null)
            _logger.LogError(
                "บริษัท {Company} ไม่มีผังบัญชี {Code} (ลูกหนี้ผู้ให้บริการรับชำระเงิน) — "
                + "เงินที่รับผ่าน {Provider} จะลงผังธนาคารทั้งที่ยังไม่เข้าบัญชีจริง "
                + "⇒ ยอดธนาคารในระบบสูงเกินจริงและกระทบยอดไม่ได้ · เพิ่มผังนี้ในผังบัญชี "
                + "แล้วตั้งที่หน้า \"ตั้งค่าการรับชำระเงินออนไลน์\"",
                intent.CompanyId, StandardClearingCode, intent.ProviderCode);

        return standard;
    }
}
