using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัวตัดสินเดียว: รายการชำระนี้เป็นโหมดทดสอบไหม</b> (ฝ่ายค้านรอบ 219 S8 + รอบ 2 ข้อ 3)
///
/// <para><b>ที่มา:</b> รอบแรกเก็บ <c>PaymentIntent.IsTestMode</c> เป็น <c>bool</c> ไม่ว่าง ค่าตั้งต้น false ⇒ intent ใหม่ที่เป็นเงินจริง (false)
/// แยกไม่ออกจากแถวเดิมที่ "ไม่รู้" (false จาก DEFAULT) ⇒ ทางเลือกต้องอ่านโหมดปัจจุบันของ config ⇒ ถ้าแอดมินสลับ config เป็น Test ภายหลัง
/// เงินจริงถูกบล็อกไม่ให้สิทธิ์ · หน้าจ่ายเงินสองหน้าอ่าน config ตรง ๆ คนละสูตรกับด่านให้สิทธิ์</para>
///
/// <para><b>กติกา:</b> ค่าที่ประทับ ณ ตอนสร้าง (true/false) ชนะเสมอ · <c>null</c> = แถวก่อนมีคอลัมน์ ⇒ อ่านโหมดของ config (พฤติกรรมเดิม) ·
/// ไม่มี config = ไม่ใช่ทดสอบ</para>
/// </summary>
public static class PaymentIntentTestMode
{
    /// <summary>pure — <paramref name="configMode"/> ใช้เฉพาะเมื่อไม่ได้ประทับ</summary>
    public static bool Decide(bool? stamped, PaymentProviderMode? configMode)
        => stamped ?? configMode == PaymentProviderMode.Test;

    /// <summary>ค่าที่ประทับตอนสร้าง intent (ไม่มี config = null ⇒ ถามทีหลังได้ "ไม่ใช่ทดสอบ")</summary>
    public static bool? StampAtCreate(PaymentProviderConfig? config)
        => config == null ? null : config.Mode == PaymentProviderMode.Test;

    /// <summary>อ่านจาก intent — ถาม DB เฉพาะแถวเดิมที่ไม่ได้ประทับ</summary>
    public static async Task<bool> ResolveAsync(AccountingDbContext db, PaymentIntent intent, CancellationToken ct)
    {
        if (intent.IsTestMode is bool stamped) return stamped;
        if (intent.ProviderConfigId is not Guid cid) return false;
        var mode = await db.PaymentProviderConfigs.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == cid && c.CompanyId == intent.CompanyId)
            .Select(c => (PaymentProviderMode?)c.Mode).FirstOrDefaultAsync(ct);
        return Decide(null, mode);
    }
}
