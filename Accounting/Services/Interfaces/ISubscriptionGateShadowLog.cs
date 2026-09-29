using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>หนึ่งครั้งที่ "จะถูกบล็อก" (โหมดเงา) หรือ "ถูกบล็อกจริง" (หน้าเว็บที่เปิดบังคับแล้ว) — ไม่มี PII (ไม่มีผู้ใช้ · ไม่มี URL เต็ม/id)</summary>
/// <param name="RouteKey">คีย์ใน <see cref="SubscriptionGatePolicy.RouteFeatureMap"/> ที่ตรง (เช่น <c>/payroll</c>) · null = ไม่เกี่ยวกับฟีเจอร์</param>
/// <param name="WouldBlock">true = เปิดบังคับแล้วคำขอนี้จะถูกบล็อก (รอบ 200: โหมดเงาตัดสินด้วยด่านเขียนแบบบังคับเสมอ ⇒ true ทุกแถวใหม่ ·
/// false มีได้เฉพาะแถวเก่าก่อนรอบ 200 ที่ config ด่านเขียนเป็น LogOnly)</param>
/// <param name="Endpoint">คีย์ endpoint (<see cref="SubscriptionGatePolicy.EndpointKey"/> — route template ไม่มี id) · ส่วนหนึ่งของคีย์แถว</param>
/// <param name="SubscriptionStatus">ชื่อสถานะ subscription (Trial/Active/…) — ใช้แยก "ลูกค้าทดลองใช้" ในรายงาน</param>
/// <param name="Enforced">true = ถูกบล็อกจริง (นับใน BlockedCount) · false = โหมดเงา (นับใน HitCount)</param>
/// <param name="Partner">รอบ 200 S200-3: ผู้เรียกเป็น partner (ส่ง <c>X-Company-Id</c>) หรือคีย์ API ของ <c>/api/v1</c> ⇒ นับใน
/// <c>PartnerHitCount</c>/<c>PartnerBlockedCount</c> แทน (<see cref="SubscriptionGatePolicy.IsPartnerCaller"/>)</param>
public sealed record SubscriptionGateShadowHit(
    Guid CompanyId, SubscriptionGateReason Reason, string? Feature, string? Plan,
    string? RouteKey, string Method, bool WouldBlock,
    string Endpoint, string? SubscriptionStatus, bool Enforced, bool Partner = false);

/// <summary>แถวในรายงานแอดมิน (สะสมต่อ บริษัท × เหตุ × ฟีเจอร์ × endpoint)</summary>
/// <param name="HitCount">ครั้งที่ "จะถูกบล็อก" ในโหมดเงา</param>
/// <param name="BlockedCount">ครั้งที่ถูกบล็อกจริง (หน้าเว็บ · โหมดที่มีผลจริง = บังคับ)</param>
/// <param name="PartnerHitCount">ครั้งที่ "จะถูกบล็อก" ของคำขอ partner/คีย์ API (คีย์ใหม่ระหว่างโหมดเงา · ด่านเขียนที่ยัง log อย่างเดียว · /api/v1 โหมดเงา)</param>
/// <param name="PartnerBlockedCount">ครั้งที่คำขอ partner/คีย์ API ถูกบล็อกจริง</param>
public sealed record SubscriptionGateShadowRow(
    Guid CompanyId, string Reason, string Feature, string Endpoint, string? Plan, string? SubscriptionStatus,
    string? RouteKey, string? LastMethod, bool WouldBlock, long HitCount, long BlockedCount,
    DateTime FirstSeenAt, DateTime LastSeenAt, long PartnerHitCount = 0, long PartnerBlockedCount = 0);

/// <summary>
/// ที่เก็บผลโหมดเงาของ gate แพ็กเกจ/ระงับบริษัท + ตัวอ่านสวิตช์แพลตฟอร์ม — รอบ 198 ข้อ 5 "รายงานก่อน แล้วค่อยเปิดบังคับ" ·
/// รอบ 200 ข้อ 14: คีย์แถวรวม endpoint · นับ "ถูกบล็อกจริง" แยกจาก "จะถูกบล็อก" · ตัดแถวเก่า
///
/// <para>เก็บลงตาราง <c>SubscriptionGateShadowHits</c> ด้วย upsert ในคำสั่งเดียว (<c>INSERT … ON CONFLICT DO UPDATE</c>)
/// แบบ <c>ChatRateLimiter</c> ⇒ นับตรงข้าม instance ไม่มี state ในหน่วยความจำ · fail-open: ตารางพัง ≠ เหตุผลที่จะปฏิเสธ
/// คำขอของลูกค้า (โหมดเงาไม่บล็อกอยู่แล้ว)</para>
/// </summary>
public interface ISubscriptionGateShadowLog
{
    /// <summary>อ่านสวิตช์แอดมิน <c>SiteSettings.SubscriptionEnforcementMode</c> (แถวแรกตาม CreatedAt) พร้อมบอกว่าอ่านได้จากไหน ·
    /// ไม่มีแถว/อ่านไม่ได้ = Shadow (ไม่บล็อกใครเพราะอ่านค่าไม่ได้ และไม่ปิดการบันทึกเงียบ ๆ) · โหมดที่มีผลจริงต้องผ่าน
    /// <see cref="SubscriptionEnforcementResolver.Resolve(SubscriptionAdminSwitchRead, Microsoft.Extensions.Configuration.IConfiguration)"/>
    /// (override ฉุกเฉินใน config) เสมอ — ห้ามใช้ค่านี้ตัดสินตรง ๆ</summary>
    Task<SubscriptionAdminSwitchRead> ReadAdminSwitchAsync(CancellationToken ct = default);

    /// <summary>อ่านสวิตช์แอดมินสดจากฐานข้อมูล (ไม่ผ่านแคช <see cref="SubscriptionAdminSwitchCache"/>) — หน้าแอดมินใช้ตัวนี้ ให้กรอบ "โหมดที่มีผลจริง"
    /// ตรงฐานข้อมูลเสมอ · middleware ใช้ <see cref="ReadAdminSwitchAsync"/> (แคชสั้น S200-8)</summary>
    Task<SubscriptionAdminSwitchRead> ReadAdminSwitchFreshAsync(CancellationToken ct = default);

    /// <summary>ล้างแคชสวิตช์ของเครื่องนี้ (หลังแอดมินกดเปลี่ยน) — เครื่องอื่นตามทันภายใน <see cref="SubscriptionAdminSwitchCache.Ttl"/></summary>
    void InvalidateAdminSwitchCache();

    /// <summary>บวก 1 ให้แถว (บริษัท, เหตุ, ฟีเจอร์) · ไม่ throw</summary>
    Task RecordAsync(SubscriptionGateShadowHit hit, CancellationToken ct = default);

    /// <summary>แถวทั้งหมดเรียงตามเวลาล่าสุด (สูงสุด <paramref name="limit"/> แถว)</summary>
    Task<IReadOnlyList<SubscriptionGateShadowRow>> ListAsync(int limit, CancellationToken ct = default);

    /// <summary>ล้างผลทั้งหมด (แอดมินเริ่มนับใหม่หลังแก้แพ็กเกจ) — คืนจำนวนแถวที่ลบ</summary>
    Task<int> ClearAsync(CancellationToken ct = default);

    /// <summary>ตัดแถวที่ไม่ถูกพบซ้ำนานกว่า <paramref name="olderThanDays"/> วัน (<see cref="SubscriptionGatePolicy.ShadowRetentionDays"/>) —
    /// DELETE ตามเวลา ทำซ้ำข้าม instance ได้ปลอดภัย · คืนจำนวนแถวที่ลบ · ไม่ throw (อ่านไม่ได้ = 0)</summary>
    Task<int> PruneAsync(int olderThanDays, CancellationToken ct = default);
}
