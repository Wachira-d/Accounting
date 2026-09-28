using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **webhook ที่ยืนยันลายเซ็นผ่านแล้ว เป็นของรายการชำระเงินนี้จริงไหม** — ตัวตัดสินตัวเดียว (รอบ 198 ฝ่ายค้าน R-E1 · P0)
///
/// <para>═══ ที่มา ═══ <c>PaymentWebhookController.Receive</c> ลองยืนยัน event กับ config ทุกบริษัทของผู้ให้บริการเดียวกัน
/// แล้วเรียก <c>ApplyChargeAsync(verified.IntentId, …)</c> โดยรหัสรายการมาจาก <c>metadata</c> ของ charge ที่ดึงด้วยคีย์ของ config
/// ที่ยืนยันผ่าน · <c>ApplyChargeAsync</c> โหลดรายการด้วย <c>Id</c> อย่างเดียว ⇒ ใครก็ได้ที่มีบัญชี Omise (คีย์ทดสอบฟรี) +
/// tenant ของตัวเอง สร้าง charge ที่ใส่รหัสรายการของร้านอื่น (เห็นได้จากหน้าชำระเงินสาธารณะ) แล้วยิง webhook ⇒ ใบแจ้งหนี้/
/// การจองของร้านอื่นกลายเป็น "ชำระแล้ว" + Dr 11340 ด้วยเงินที่ไม่มีวันมา</para>
///
/// <para>═══ กติกา ═══ ต้องครบทุกข้อ: รายการอยู่ในบริษัทเดียวกับ config ที่ยืนยันผ่าน · ผู้ให้บริการตรงกัน · ถ้ารายการผูก config
/// ไว้ ต้องเป็นตัวเดียวกัน · event "สำเร็จ" ต้องมียอดเท่ารายการ (กันเงิน 0 บาทปิดหนี้ 10,000) · ไม่พบ = ไม่ใช่ของเรา (ไม่ใช่ "ผ่าน")</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class PaymentWebhookOwnership
{
    /// <summary>ข้อเท็จจริงของรายการชำระเงินที่ webhook อ้างถึง (อ่านจากฐานข้อมูล<b>ในบริษัทของ config</b> เท่านั้น)</summary>
    public sealed record IntentFacts(Guid CompanyId, Guid? ProviderConfigId, string ProviderCode, decimal Amount);

    /// <summary>เศษที่ยอมให้ (ครึ่งสตางค์)</summary>
    public const decimal AmountTolerance = 0.005m;

    /// <summary>เหตุผลที่ต้องปฏิเสธ — <c>null</c> = เป็นของรายการนี้จริง ให้เดินต่อได้</summary>
    public static string? RejectReason(IntentFacts? intent, Guid configCompanyId, Guid configId,
        string providerCode, PaymentIntentStatus chargeStatus, decimal chargeAmount)
    {
        if (intent == null)
            return "ไม่พบรายการชำระเงินที่อ้างถึงในบริษัทของคีย์ที่ยืนยันผ่าน";
        if (intent.CompanyId != configCompanyId)
            return "รายการชำระเงินเป็นของบริษัทอื่น — ไม่ใช่บริษัทของคีย์ที่ยืนยันลายเซ็นผ่าน";
        if (!string.Equals(intent.ProviderCode, providerCode, StringComparison.OrdinalIgnoreCase))
            return $"รายการนี้ใช้ช่องทาง \"{intent.ProviderCode}\" ไม่ใช่ \"{providerCode}\"";
        if (intent.ProviderConfigId is Guid bound && bound != configId)
            return "รายการนี้ผูกกับการตั้งค่าผู้ให้บริการอีกชุดหนึ่ง";
        if (chargeStatus == PaymentIntentStatus.Succeeded
            && Math.Abs(chargeAmount - intent.Amount) > AmountTolerance)
            return $"ยอดที่ผู้ให้บริการแจ้ง ({chargeAmount:N2}) ไม่เท่ายอดของรายการ ({intent.Amount:N2})";
        return null;
    }
}
