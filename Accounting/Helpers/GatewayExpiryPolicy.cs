namespace Accounting.Helpers;

/// <summary>
/// รอบ 227 ฝ่ายค้าน B1 — วันหมดอายุของรายการชำระที่ผู้ให้บริการรายงาน (<c>PaymentIntent.QrExpiresAt</c>) ใช้อย่างไร · pure · ตัวเดียวของทุกผู้อ่าน
/// <para><b>ที่มา:</b> ก่อนรอบ 227 adapter อ่านวันหมดอายุไม่ได้ (null เสมอ) ⇒ ทุกด่านใช้ "ค่าตั้งต้นของตัวเอง" (ที่พักถือห้อง 30 นาที · การจองรอเงิน 30 นาที ·
/// ไม่ปิดใบเอง) · พออ่านได้ (charge ระดับ <c>expires_at</c> — พร้อมเพย์อาจยาวเป็นวัน) จะ (ก) ปิดใบเป็น "หมดอายุ" เองโดยไม่ถามผู้ให้บริการ (สถานะปลายทางที่ระบบประทับเอง —
/// เงินอาจเพิ่งเข้า) และ (ข) ยืดการถือห้อง/รอเงินของร้านยาวกว่าเดิมเงียบ ๆ</para>
/// <list type="bullet">
/// <item>ปิดใบเอง: มีเลขอ้างอิงผู้ให้บริการ ⇒ <b>ถามผู้ให้บริการก่อนเสมอ</b> แล้วใช้สถานะของเขา · ปิดเองได้เฉพาะเมื่อถามไม่ได้ <b>และ</b> พ้นวันหมดอายุ + <see cref="LocalExpiryGrace"/> ·
///   ไม่มีเลขอ้างอิง (ไม่เคยสร้าง charge สำเร็จ) ⇒ ปิดเองได้ทันที (พฤติกรรมเดิม)</item>
/// <item>หน้าต่างถือ/รอ: <c>min(วันหมดอายุของผู้ให้บริการ, ค่าตั้งต้นเดิมของร้าน)</c> — ค่าตั้งต้นเดิมเป็นเพดาน (ไม่ยาวกว่าที่ร้านเคยได้)</item>
/// </list>
/// </summary>
public static class GatewayExpiryPolicy
{
    /// <summary>ระยะผ่อนหลังวันหมดอายุก่อนยอมปิดใบเองเมื่อถามผู้ให้บริการไม่ได้</summary>
    public static readonly TimeSpan LocalExpiryGrace = TimeSpan.FromMinutes(15);

    /// <summary>ปิดใบเป็น "หมดอายุ" เองโดยไม่ถามผู้ให้บริการได้ไหม (ยังไม่ได้ถาม) — เฉพาะใบที่ไม่มีเลขอ้างอิงผู้ให้บริการ</summary>
    public static bool ExpireWithoutAsking(DateTime? expiresAtUtc, bool hasProviderRef, DateTime nowUtc)
        => !hasProviderRef && expiresAtUtc is DateTime e && e <= nowUtc;

    /// <summary>ถามผู้ให้บริการไม่สำเร็จ — ปิดเองได้ไหม (พ้นวันหมดอายุ + ระยะผ่อนแล้วเท่านั้น)</summary>
    public static bool ExpireAfterProviderUnreachable(DateTime? expiresAtUtc, DateTime nowUtc)
        => expiresAtUtc is DateTime e && e + LocalExpiryGrace <= nowUtc;

    /// <summary>สิ้นสุดหน้าต่างถือ/รอ = min(วันหมดอายุของผู้ให้บริการ (ถ้ายังไม่ผ่าน · ถ้ามี), <paramref name="start"/> + <paramref name="defaultWindow"/>)</summary>
    public static DateTime WindowEnd(DateTime? providerExpiresAtUtc, DateTime start, TimeSpan defaultWindow)
    {
        var fallback = start + defaultWindow;
        return providerExpiresAtUtc is DateTime e && e < fallback ? e : fallback;
    }
}
