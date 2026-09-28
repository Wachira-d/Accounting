using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>หนึ่งครั้งที่ "จะถูกบล็อก" ในโหมดเงา — ไม่มี PII (ไม่มีผู้ใช้ · ไม่มี URL เต็ม)</summary>
/// <param name="RouteKey">คีย์ใน <see cref="SubscriptionGatePolicy.RouteFeatureMap"/> ที่ตรง (เช่น <c>/payroll</c>) · null = ไม่เกี่ยวกับฟีเจอร์</param>
/// <param name="WouldBlock">true = เปิด Enforce แล้วคำขอนี้จะถูกบล็อก · false = config <c>Subscription:Enforcement:Mode</c>
/// เป็น LogOnly จึงแค่ log (บริษัทถูกระงับ/หมดอายุ — เปิดสวิตช์เว็บอย่างเดียวยังไม่บล็อก)</param>
public sealed record SubscriptionGateShadowHit(
    Guid CompanyId, SubscriptionGateReason Reason, string? Feature, string? Plan,
    string? RouteKey, string Method, bool WouldBlock);

/// <summary>แถวในรายงานแอดมิน (สะสมต่อ บริษัท × เหตุ × ฟีเจอร์)</summary>
public sealed record SubscriptionGateShadowRow(
    Guid CompanyId, string Reason, string Feature, string? Plan, string? RouteKey, string? LastMethod,
    bool WouldBlock, long HitCount, DateTime FirstSeenAt, DateTime LastSeenAt);

/// <summary>
/// ที่เก็บผลโหมดเงาของ gate แพ็กเกจ/ระงับบริษัท + ตัวอ่านสวิตช์แพลตฟอร์ม — รอบ 198 ข้อ 5 "รายงานก่อน แล้วค่อยเปิดบังคับ"
///
/// <para>เก็บลงตาราง <c>SubscriptionGateShadowHits</c> ด้วย upsert ในคำสั่งเดียว (<c>INSERT … ON CONFLICT DO UPDATE</c>)
/// แบบ <c>ChatRateLimiter</c> ⇒ นับตรงข้าม instance ไม่มี state ในหน่วยความจำ · fail-open: ตารางพัง ≠ เหตุผลที่จะปฏิเสธ
/// คำขอของลูกค้า (โหมดเงาไม่บล็อกอยู่แล้ว)</para>
/// </summary>
public interface ISubscriptionGateShadowLog
{
    /// <summary>สวิตช์ <c>SiteSettings.SubscriptionEnforcementMode</c> (แถวแรกตาม CreatedAt) · ไม่มีแถว/อ่านไม่ได้ = Shadow
    /// (ค่าตั้งต้น — ไม่บล็อกใครเพราะอ่านค่าไม่ได้ และไม่ปิดการบันทึกเงียบ ๆ)</summary>
    Task<SubscriptionEnforcementMode> GetWebModeAsync(CancellationToken ct = default);

    /// <summary>บวก 1 ให้แถว (บริษัท, เหตุ, ฟีเจอร์) · ไม่ throw</summary>
    Task RecordAsync(SubscriptionGateShadowHit hit, CancellationToken ct = default);

    /// <summary>แถวทั้งหมดเรียงตามเวลาล่าสุด (สูงสุด <paramref name="limit"/> แถว)</summary>
    Task<IReadOnlyList<SubscriptionGateShadowRow>> ListAsync(int limit, CancellationToken ct = default);

    /// <summary>ล้างผลทั้งหมด (แอดมินเริ่มนับใหม่หลังแก้แพ็กเกจ) — คืนจำนวนแถวที่ลบ</summary>
    Task<int> ClearAsync(CancellationToken ct = default);
}
