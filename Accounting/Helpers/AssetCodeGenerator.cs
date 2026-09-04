using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รหัสสินทรัพย์ถาวร <c>FA-yyyyMM-####</c> — **ตัวออกรหัสตัวเดียวของระบบ**
///
/// ═══ ที่มา (ผลตรวจ F-08) ═══
/// เดิมมีเมธอด <c>GenerateAssetCodeAsync</c> <b>สองชุดที่เหมือนกันคำต่อคำ</b>
/// (<c>FixedAssetService</c> และ <c>DocumentService</c> ตอนขึ้นทะเบียนสินทรัพย์
/// จากใบซื้อ) — ทั้งคู่ไม่มีล็อกและเรียงแบบ<b>ข้อความ</b> ⇒
/// <list type="bullet">
///   <item>ขึ้นทะเบียนจากใบซื้อพร้อมกับที่ผู้ใช้กดสร้างเองในหน้าทะเบียน =
///     ได้รหัสเดียวกันทั้งคู่</item>
///   <item>พอเลขทะลุ <c>9999</c> การเรียงแบบข้อความทำให้ <c>"...9999"</c> ยัง
///     ชนะ <c>"...10000"</c> ⇒ รหัสวนกลับไปทับของเดิม</item>
/// </list>
/// รหัสสินทรัพย์เป็นคีย์ที่ผู้ตรวจสอบใช้อ้างในทะเบียนทรัพย์สินและงบการเงิน —
/// ซ้ำแล้วตามรอยค่าเสื่อมกลับไม่ได้
/// </summary>
public static class AssetCodeGenerator
{
    public static async Task<string> NextAsync(
        AccountingDbContext db, Guid companyId, DateTime? asOf = null, CancellationToken ct = default)
    {
        var prefix = $"FA-{(asOf ?? DateTime.UtcNow):yyyyMM}-";
        return await SequenceNumber.NextAsync(
            db, companyId, AdvisoryLockKey.AssetSequence, prefix,
            db.FixedAssets.IgnoreQueryFilters()
                .Where(a => a.CompanyId == companyId && a.AssetCode.StartsWith(prefix))
                .OrderByDescending(a => a.CreatedAt)
                .Select(a => a.AssetCode),
            db.FixedAssets.Local.Select(a => a.AssetCode),
            ct: ct);
    }
}
