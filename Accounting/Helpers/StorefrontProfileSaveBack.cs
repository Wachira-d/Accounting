using Accounting.Services.Interfaces;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 216 (คำตัดสินข้อ 174) — "บันทึกเป็นข้อมูลของฉัน" <b>หลัง</b>จอง/สั่ง/จองที่พักสำเร็จ ในคำขอเดียวกัน (ทางเข้าเดียวของ 3 controller) ·
/// เจ้าของโปรไฟล์ = <see cref="StorefrontCustomerAuth.ProfileOwner"/> (โทเคนหน้าร้านของเว็บนี้เท่านั้น) ·
/// บันทึกล้ม <b>ไม่ทำให้รายการที่สำเร็จแล้วกลายเป็น "ไม่สำเร็จ"</b> (แขกจะกดซ้ำ ⇒ จองซ้อน) แต่ล้มดัง: log Error + ข้อความใน <c>Errors</c> ของคำตอบ
/// </summary>
public static class StorefrontProfileSaveBack
{
    public const string FailedNote = "บันทึกข้อมูลลงบัญชีของคุณไม่สำเร็จ (รายการสำเร็จแล้ว) — แก้ได้ที่หน้า \"บัญชีของฉัน › ข้อมูลส่วนตัว\"";

    /// <summary>คืนข้อความถึงผู้ใช้ (ใส่ใน <c>ApiResponse.Errors</c>) · ไม่ขอบันทึก/ไม่ใช่สมาชิกของเว็บนี้/ไม่มีอะไรเปลี่ยน ⇒ null</summary>
    public static async Task<List<string>?> RunAsync(IStorefrontCustomerService? profiles, ILogger? logger,
        Guid companyId, Guid siteId, Guid? owner, CheckoutProfileInput input)
    {
        if (owner is not Guid customerId || profiles == null) return null;
        try
        {
            var note = await profiles.SaveFromCheckoutAsync(companyId, siteId, customerId, input);
            return note == null ? null : new List<string> { note };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "บันทึกโปรไฟล์สมาชิก {CustomerId} หลังทำรายการไม่สำเร็จ — รายการสำเร็จแล้ว", customerId);
            return new List<string> { FailedNote };
        }
    }
}
