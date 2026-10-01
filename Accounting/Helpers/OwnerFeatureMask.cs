using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ผลของด่าน "เจ้าของบริษัทปิดฟีเจอร์นี้เอง" ที่ระดับ service</summary>
/// <param name="Allowed">ให้ผ่านไหม</param>
/// <param name="ShadowHit">true = เจ้าของปิดฟีเจอร์นี้แต่ยังอยู่ในโหมดเงา ⇒ ผ่าน + บันทึก "จะถูกปิด" ลงรายงานแอดมิน</param>
public readonly record struct OwnerFeatureMaskVerdict(bool Allowed, bool ShadowHit);

/// <summary>
/// ตัวตัดสินตัวเดียวของ <c>CompanySettings.OwnerDisabledFeatures</c> ที่ด่าน <b>service</b> (<c>SubscriptionService.CheckFeatureAccessAsync</c> —
/// ด่านสร้างเอกสาร · <c>EntitlementService</c>) · รอบ 201 ทีม PL (C-3 · คำตัดสินข้อ 76 · team-S2 F-S2-1)
///
/// <para>═══ ที่มา ═══ เจ้าของบริษัทปิดฟีเจอร์ที่หน้า "ตั้งค่าเจ้าของ" แล้ว mask มีผลแค่ overlay หน้าเว็บ/gate (<c>ResolveGateOverlayAsync</c>) ⇒
/// ด่าน service ยังให้ผ่าน = สวิตช์ของเจ้าของเป็น silent no-op สำหรับทางเข้าที่ไม่ผ่านหน้าเว็บ (API/LINE/job) · คอมเมนต์ใน EntitlementService
/// เขียนว่า "ครอบแล้ว" ซึ่งไม่จริง</para>
///
/// <para>═══ กติกา (ทิศที่มองเห็นและย้อนได้ — ข้อ 76) ═══</para>
/// <list type="bullet">
/// <item>แพ็กเกจไม่มีฟีเจอร์ ⇒ ไม่ผ่าน (พฤติกรรมเดิม · ด่านนี้ไม่เพิ่มสิทธิ์)</item>
/// <item>แพ็กเกจมี + เจ้าของไม่ได้ปิด ⇒ ผ่าน (เดิม)</item>
/// <item>แพ็กเกจมี + เจ้าของปิด (บิตใดบิตหนึ่งของฟีเจอร์ — ตรงกับ overlay <c>features &amp; ~ownerDisabled</c>) + <b>ยังไม่กดบังคับ</b> ⇒
/// ผ่าน + <see cref="OwnerFeatureMaskVerdict.ShadowHit"/> (บันทึก "จะถูกปิด" — แอดมินดูผลกระทบก่อน)</item>
/// <item>… + <b>กดบังคับแล้ว</b> (<c>SiteSettings.OwnerFeatureMaskEnforced</c>) ⇒ ไม่ผ่าน</item>
/// </list>
/// </summary>
public static class OwnerFeatureMask
{
    public static OwnerFeatureMaskVerdict Decide(FeatureFlags planFeatures, FeatureFlags ownerDisabled, FeatureFlags feature, bool enforced)
    {
        if (!planFeatures.HasFlag(feature)) return new OwnerFeatureMaskVerdict(false, false);
        if (!IsOwnerDisabled(ownerDisabled, feature)) return new OwnerFeatureMaskVerdict(true, false);
        return enforced ? new OwnerFeatureMaskVerdict(false, false) : new OwnerFeatureMaskVerdict(true, true);
    }

    /// <summary>เจ้าของปิดบิตใดบิตหนึ่งของฟีเจอร์นี้ (เกณฑ์เดียวกับ overlay หน้าเว็บ) · ฟีเจอร์ว่าง (None) = ไม่ถือว่าปิด</summary>
    public static bool IsOwnerDisabled(FeatureFlags ownerDisabled, FeatureFlags feature)
        => feature != FeatureFlags.None && (ownerDisabled & feature) != FeatureFlags.None;
}
