using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 260-4 (ฝ่ายค้าน 260-4 L8 · คำตัดสิน 260-4-12) — <b>ผู้รับกระดิ่ง "ส่งถึงลูกค้าอัตโนมัติล้ม" ตัวเดียว</b> (กระดิ่ง LINE ล้มซ้ำ/Token เสีย/ถอดรหัสไม่ได้ ·
/// โควตาเดือนหมด · เข้าคิวส่งอัตโนมัติล้ม): เจ้าของที่ยังใช้งาน (<see cref="CompanyMembership.ActiveMemberIdsAsync"/>) ·
/// ไม่มีเจ้าของที่ใช้งาน ⇒ สมาชิกที่ใช้งานซึ่งมีสิทธิ์แก้ค่าตั้งบริษัท (<see cref="PermissionKeys.CompanySettingsEdit"/> — คนที่แก้ LINE/ตารางส่งได้จริง) ·
/// ทุก query ผูก CompanyId · ไม่มีใครเลย ⇒ รายการว่าง (ผู้เรียก LogError)
/// </summary>
public static class LineAlertRecipients
{
    private const int MaxFallbackCandidates = 50;

    public static async Task<IReadOnlyList<Guid>> ForCompanyAsync(AccountingDbContext db, IPermissionService? permissions, Guid companyId, CancellationToken ct)
    {
        var ownerIds = await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
        var activeOwners = await CompanyMembership.ActiveMemberIdsAsync(db, companyId, ownerIds, ct);
        var owners = ownerIds.Where(activeOwners.Contains).Distinct().ToList();
        if (owners.Count > 0 || permissions == null) return owners;
        var others = await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role != UserRole.Owner && cu.Role != UserRole.Viewer)
            .OrderBy(cu => cu.JoinedAt).Select(cu => cu.UserId).Take(MaxFallbackCandidates).ToListAsync(ct);
        var active = await CompanyMembership.ActiveMemberIdsAsync(db, companyId, others, ct);
        var result = new List<Guid>();
        foreach (var u in others.Where(active.Contains).Distinct())
            if (await permissions.HasPermissionAsync(companyId, u, PermissionKeys.CompanySettingsEdit)) result.Add(u);
        return result;
    }
}
