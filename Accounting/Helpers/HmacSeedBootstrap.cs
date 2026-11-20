using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก A ฝ่ายค้าน MED-4 — <b>เมล็ดกุญแจ HMAC ถาวร</b> ของแฮชเลขอ้างอิงสลิปภาษี (<see cref="TaxSlipRefHash"/>) และ OTP ผู้ลงนาม (<see cref="CustomerSignRule"/>)
///
/// <para><b>ที่มา:</b> กุญแจ HMAC เดิม derive จาก <c>ENCRYPTION_KEY</c> ตรง ๆ ⇒ หมุนคีย์แล้วแฮช Control Code/เลขรายการธนาคารที่เก็บไว้เทียบไม่ตรง ⇒
/// สลิปเดิมที่ส่งซ้ำหลังหมุน<b>ไม่ถูกกันซ้ำ</b> (นำส่งซ้ำ) และใบขอชำระที่ออกก่อนหมุนจับคู่สลิปไม่ได้ · OTP ที่ส่งไปก่อนหมุนใช้ไม่ได้</para>
/// <para><b>กติกา:</b> ครั้งแรกเก็บค่า <c>ENCRYPTION_KEY</c> ที่ใช้อยู่ (แฮชที่เก็บไว้ก่อนรอบนี้เทียบได้ต่อ) เป็นเมล็ด ลง <c>SiteSettings.HmacSeedProtected</c>
/// เข้ารหัสด้วย <see cref="ISecretProtector"/> (อยู่ในทะเบียนงานย้ายคีย์ ⇒ หมุนคีย์แล้ว<b>ห่อใหม่</b> แต่ค่าข้างในไม่เปลี่ยน) · บูตถัดไปอ่านเมล็ดเดิมเสมอ ·
/// ถอดไม่ได้ ⇒ LogError + ใช้คีย์ปัจจุบัน (ทางเดิม — ล้มดัง ไม่เงียบ) · ไม่มีแถวค่าตั้งระบบ ⇒ คีย์ปัจจุบัน (บูตถัดไปบันทึก)</para>
/// </summary>
public static class HmacSeedBootstrap
{
    public const string Label = "เมล็ดกุญแจ HMAC ของระบบ";

    /// <summary>คืนเมล็ดที่ใช้ตั้งกุญแจ HMAC · บันทึกครั้งแรกถ้ายังไม่มี</summary>
    public static async Task<string> EnsureAsync(AccountingDbContext db, ISecretProtector secrets, string currentKey, ILogger? logger = null,
        CancellationToken ct = default)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(ct);
        if (row == null) return currentKey;
        if (string.IsNullOrEmpty(row.HmacSeedProtected))
        {
            // สองเครื่องบูตพร้อมกันเขียนค่าเดียวกัน (คีย์ปัจจุบัน) — ไม่ชนกันในเชิงความหมาย
            row.HmacSeedProtected = secrets.Protect(currentKey);
            await db.SaveChangesAsync(ct);
            return currentKey;
        }
        var opened = SecretUse.Open(secrets, Label, row.HmacSeedProtected);
        if (opened.Ok) return opened.Value!;
        logger?.LogError("{Problem} — ใช้ ENCRYPTION_KEY ปัจจุบันเป็นกุญแจ HMAC ชั่วคราว: แฮชสลิปภาษี/OTP ที่ออกก่อนหน้าเทียบไม่ตรง (สลิปซ้ำอาจไม่ถูกกัน) · "
            + EncryptionHealthText.AdminKeyAdvice, opened.Problem);
        return currentKey;
    }

    /// <summary>ตั้งกุญแจ HMAC ทุกตัวที่ต้องไม่หมุนตามคีย์เข้ารหัส — ตัวเดียว (Program.cs เรียกหลัง migration)</summary>
    public static void Configure(string seed)
    {
        TaxSlipRefHash.ConfigureKey(seed);
        CustomerSignRule.ConfigureOtpKey(seed);
    }
}
