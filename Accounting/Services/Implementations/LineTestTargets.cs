using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Line;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 260-4 (team-A · คำตัดสิน 260-4-3) — <b>ปลายทางทดสอบส่ง LINE ที่ระบบรู้จัก: ตัวเดียวของ "รายการ" และ "หาใหม่ตอนส่ง"</b>
/// (<c>LineConfigController.TestTargets</c> / <c>Test</c>) — แทนการพิมพ์ U…/C… ดิบ
/// <list type="bullet">
/// <item>กลุ่ม LINE ของทีม (<c>CompanySettings.LineDefaultGroupId</c>)</item>
/// <item>ปลายทาง LINE ของผู้ติดต่อ<b>ในบริษัทนี้</b>ที่ผูกกับ <b>OA ของบริษัท</b> (ข้อความทดสอบส่งผ่าน token ของบริษัท — userId/groupId ใช้ได้เฉพาะ OA ที่ออก ⇒
/// ปลายทางผ่าน OA กลาง/แถว legacy/บอทเดิมก่อนเปลี่ยน OA ไม่อยู่ในรายการ) · ยังผูกอยู่ · ไม่ถูกบล็อก</item>
/// </list>
/// LINE id ไม่ออกจากเซิร์ฟเวอร์ (key = <see cref="LineOaHealth.EndpointTargetKey"/>) · ทุก query ผูก CompanyId (raw ไม่มี — LINQ + เงื่อนไขชัด)
/// </summary>
public static class LineTestTargets
{
    public const int MaxEndpoints = 200;

    private static IQueryable<Models.Entities.ContactNotifyEndpoint> Usable(AccountingDbContext db, Guid companyId, string? oaDestination)
    {
        return db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted
                && (e.Kind == NotifyEndpointKind.LineUser || e.Kind == NotifyEndpointKind.LineGroup)
                && e.OaScope == NotifyEndpointKind.ScopeCompany && e.UnlinkedAt == null && e.BlockedAt == null
                && (oaDestination == null || e.OaDestination == null || e.OaDestination == oaDestination));
    }

    public static async Task<IReadOnlyList<LineTestTarget>> ListAsync(AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var s = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineDefaultGroupId, x.LineOaDestination }).FirstOrDefaultAsync(ct);
        var list = new List<LineTestTarget>();
        if (!string.IsNullOrWhiteSpace(s?.LineDefaultGroupId))
            list.Add(new LineTestTarget(LineOaHealth.TeamGroupTargetKey, "กลุ่ม LINE ของทีม (ที่ตั้งไว้ด้านบน)"));
        var rows = await (from e in Usable(db, companyId, s?.LineOaDestination)
                          join c in db.Contacts.AsNoTracking() on e.ContactId equals c.Id
                          where c.CompanyId == companyId && !c.IsDeleted
                          orderby c.Name, e.Kind, e.LinkedAt
                          select new { e.Id, e.Kind, e.GroupNameSnapshot, e.Label, ContactName = c.Name })
            .Take(MaxEndpoints).ToListAsync(ct);
        list.AddRange(rows.Select(r => new LineTestTarget(LineOaHealth.EndpointTargetKey(r.Id),
            LineOaHealth.EndpointTargetLabel(r.ContactName, r.Kind, r.GroupNameSnapshot, r.Label), ReachesContact: true)));
        return list;
    }

    /// <summary>หาปลายทางของ key ใหม่ตอนส่ง (ไม่เชื่อรายการที่หน้าเว็บถือไว้) — คืน (id ที่ถอดรหัสแล้ว, null) หรือ (null, เหตุ)</summary>
    public static async Task<(string? To, string? Problem)> ResolveAsync(AccountingDbContext db, ISecretProtector secrets, Guid companyId, Guid endpointId,
        CancellationToken ct)
    {
        var oa = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => x.LineOaDestination).FirstOrDefaultAsync(ct);
        var ep = await Usable(db, companyId, oa).Where(e => e.Id == endpointId)
            .Select(e => new { e.Kind, e.LineUserId, e.LineGroupId }).FirstOrDefaultAsync(ct);
        if (ep == null) return (null, "ไม่พบปลายทางที่เลือกในบริษัทนี้ (ยกเลิกการผูก/ถูกบล็อก/ผูกกับ LINE OA อื่น) — โหลดหน้านี้ใหม่");
        var to = secrets.Unprotect(ep.Kind == NotifyEndpointKind.LineGroup ? ep.LineGroupId : ep.LineUserId);
        return string.IsNullOrWhiteSpace(to)
            ? (null, "ถอดรหัส LINE id ของปลายทางนี้ไม่ได้ (คีย์เปลี่ยน/ค่าเสียหาย) — ให้ผู้รับผูกใหม่")
            : (to.Trim(), null);
    }
}
