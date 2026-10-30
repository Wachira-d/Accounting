using Accounting.Helpers;

namespace Accounting.Services.Interfaces;

/// <summary>รอบ 212 แพ็ก D — ผู้อ่านค่าตั้ง LINE OA กลางของ NextAcc (Messaging API) <b>ตัวเดียวของระบบ</b>:
/// ค่าในหน้าแอดมิน (<c>SiteSettings.LineMessaging*</c>) ก่อน · ไม่มี token ⇒ config <c>Line:*</c> (กติกา <see cref="NextAccLineConfigRule.Merge"/>) ·
/// ห้ามอ่าน <c>_config["Line:…"]</c> ที่อื่น (<c>tools/required_call_site_check.py</c> LINE_CONFIG_FORBID) ·
/// scoped — โหลดครั้งเดียวต่อคำขอ/ขอบเขต (ไม่มี cache ข้ามคำขอ ⇒ บันทึกจากหน้าแอดมินมีผลทันทีทุกเครื่อง)</summary>
public interface INextAccLineConfig
{
    Task<NextAccLineSettings> GetAsync(CancellationToken ct = default);
}
