using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments;

/// <summary>
/// รอบ 235 (ข้อ 10 · คำตัดสิน 235-10) — <b>เส้นบันทึกบัตรตอนเริ่มชำระค่าบริการ NextAcc ตัวเดียว</b> ของทุกทางเข้า:
/// <c>PlatformPaymentController</c> (platform-pay — หน้าแพ็กเกจ/ส่วนเสริม/ใบแจ้งหนี้ในระบบ) และ <c>PublicCheckoutController</c> (หน้าซื้อแพ็กเกจสาธารณะ · ผู้ซื้อที่เพิ่งสมัคร)
/// <list type="bullet">
/// <item>ติ๊กบันทึกบัตร ⇒ <see cref="IPlatformSavedCardService.SaveFromTokenAsync"/> (ยินยอมบังคับ + ฉบับข้อความที่แสดง · นิติบุคคลติ๊กรับทราบภาษีหัก ณ ที่จ่ายแยก —
/// กติกา <c>PlatformSavedCardRules</c> ฉบับ autocharge-2) <b>ก่อน</b>เริ่มชำระ · บันทึกได้เฉพาะชำระด้วยบัตร</item>
/// <item>เริ่มชำระไม่สำเร็จหลังบันทึกบัตร ⇒ ทิ้งบัตร (<see cref="IPlatformSavedCardService.DiscardUnusedAsync"/>) แล้วโยนต่อ (ล้มดัง) · สำเร็จ ⇒ ผูกรายการแรก
/// (<see cref="IPlatformSavedCardService.BindFirstIntentAsync"/>) เฉพาะรายการที่สร้างในคำขอนี้</item>
/// <item>ข้อความยินยอมที่หน้าเว็บแสดงมาจาก <c>GET platform-pay/saved-card-offer</c> (<see cref="IPlatformSavedCardService.OfferAsync"/>) ทั้งสองหน้า</item>
/// </list>
/// ล็อกด้วย <c>tools/required_call_site_check.py</c> (ห้ามเรียก SaveFromTokenAsync ตรงจาก controller)
/// </summary>
public static class PlatformSavedCardStart
{
    /// <summary>ส่วนของคำขอเริ่มชำระที่เกี่ยวกับการบันทึกบัตร (ทั้งสองทางเข้าใช้ชื่อช่องเดียวกัน — PayWidget ส่ง saveCard/saveCardConsent/…)</summary>
    public sealed record Request(PaymentMethodKind Method, string? CardToken, bool SaveCard, bool SaveCardConsent, string? SaveCardConsentVersionShown,
        bool SaveCardWhtAcknowledged);

    /// <summary>ติ๊กบันทึกบัตร ⇒ บันทึกจากโทเคน · ไม่ติ๊ก ⇒ null (ชำระด้วยโทเคนตามเดิม)</summary>
    public static async Task<PlatformCardRefs?> SaveIfAskedAsync(IPlatformSavedCardService savedCards, Guid companyId, Request req, PlatformCardActor actor,
        CancellationToken ct)
    {
        if (!req.SaveCard) return null;
        if (req.Method != PaymentMethodKind.Card || string.IsNullOrWhiteSpace(req.CardToken))
            throw new BusinessRuleException("บันทึกบัตรได้เฉพาะการชำระด้วยบัตร", PlatformSavedCardRules.RuleSave);
        return await savedCards.SaveFromTokenAsync(companyId, req.CardToken, req.SaveCardConsent, req.SaveCardConsentVersionShown,
            req.SaveCardWhtAcknowledged, actor, ct);
    }

    /// <summary>เริ่มชำระ (ด้วยบัตรที่เพิ่งบันทึกถ้ามี) — ล้มหลังบันทึกบัตร ⇒ ทิ้งบัตรแล้วโยนต่อ · สำเร็จ ⇒ SaveChanges (หลักฐานการยอมรับ) + ผูกรายการแรก</summary>
    public static async Task<PaymentIntent> StartAsync(AccountingDbContext db, IPlatformSavedCardService savedCards, Guid companyId, Request req,
        PlatformCardActor actor, Func<PlatformCardRefs?, Task<PaymentIntent>> start, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var saved = await SaveIfAskedAsync(savedCards, companyId, req, actor, ct);
        PaymentIntent intent;
        try { intent = await start(saved); }
        catch (Exception) when (saved != null)
        {
            await savedCards.DiscardUnusedAsync(companyId, saved.SavedCardId, "เริ่มชำระไม่สำเร็จหลังบันทึกบัตร", CancellationToken.None);
            throw;
        }
        await db.SaveChangesAsync(ct);   // หลักฐานการยอมรับ (ใบเดิมที่ใช้ซ้ำไม่ผ่าน SaveChanges ของ StartAsync)
        if (saved != null) await savedCards.BindFirstIntentAsync(companyId, saved.SavedCardId, intent, startedAt, ct);
        return intent;
    }
}
