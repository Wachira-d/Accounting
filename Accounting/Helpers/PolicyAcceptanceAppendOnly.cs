using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S4) — หลักฐานการยอมรับนโยบาย (<see cref="PolicyAcceptance"/>) เป็น <b>append-only</b>:
/// แก้ได้เฉพาะการทำไม่ให้ระบุตัวตามคำขอลบ (ม.33 — Subject/IP/UA/PseudonymisedAt) และการผูกแถวต้นทางก่อนบันทึกครั้งแรก ·
/// ห้ามลบผ่าน ChangeTracker และห้าม soft delete (ลบจริงเฉพาะงานล้างตามอายุ <c>PdpaRetentionPurgeJob</c> ที่ไม่ผ่าน ChangeTracker) ·
/// ผู้เรียก: <c>AccountingDbContext.SaveChanges/SaveChangesAsync</c> (ล้มดังก่อนเขียน)
/// </summary>
public static class PolicyAcceptanceAppendOnly
{
    private static readonly HashSet<string> Mutable = new(StringComparer.Ordinal)
    {
        nameof(PolicyAcceptance.Subject), nameof(PolicyAcceptance.IpAddress), nameof(PolicyAcceptance.UserAgent),
        nameof(PolicyAcceptance.PseudonymisedAt), nameof(PolicyAcceptance.UpdatedAt), nameof(PolicyAcceptance.UpdatedBy),
    };

    /// <summary>ชื่อช่องที่ถูกแก้แต่แก้ไม่ได้ (ว่าง = ผ่าน) — pure ต่อรายการชื่อช่อง</summary>
    public static IReadOnlyList<string> ForbiddenChanges(IEnumerable<string> modifiedProperties)
        => modifiedProperties.Where(p => !Mutable.Contains(p)).ToList();

    public static string? Violation(ChangeTracker tracker)
    {
        foreach (var e in tracker.Entries<PolicyAcceptance>())
        {
            if (e.State == EntityState.Deleted)
                return "หลักฐานการยอมรับนโยบายลบไม่ได้ (append-only · ลบจริงเฉพาะเมื่อพ้นอายุเก็บ)";
            if (e.State != EntityState.Modified) continue;
            var bad = ForbiddenChanges(e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name));
            if (bad.Count > 0)
                return $"หลักฐานการยอมรับนโยบายแก้ไม่ได้ (append-only) — ช่อง {string.Join(", ", bad)}";
        }
        return null;
    }
}
