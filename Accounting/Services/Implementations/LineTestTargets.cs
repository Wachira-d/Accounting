using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 260-4 (team-A · คำตัดสิน 260-4-3 · ฝ่ายค้าน 260-4 H1 · 260-4-7) — <b>ปลายทางทดสอบส่ง LINE ที่ระบบรู้จัก: ตัวเดียวของ "รายการ" และ "หาใหม่ตอนส่ง"</b>
/// (<c>LineConfigController.TestTargets</c> / <c>Test</c>) — แทนการพิมพ์ U…/C… ดิบ
/// <list type="bullet">
/// <item>กลุ่ม LINE ของทีม (<c>CompanySettings.LineDefaultGroupId</c>)</item>
/// <item>ปลายทาง LINE ของผู้ติดต่อ<b>ในบริษัทนี้</b> (ผู้ติดต่อไม่ถูกลบ) ที่ผูกกับ <b>OA ของบริษัท</b> (ข้อความทดสอบส่งด้วย token ของบริษัท — id ใช้ได้เฉพาะ OA ที่ออก) และ
/// <b>ใช้ส่งได้ตามตัวตัดสินผู้รับตัวเดียว</b> <c>ContactRecipientRule.Status</c>
/// (ยกเลิก · บล็อก · ไม่มี/ถอนความยินยอม PDPA · กลุ่มยังไม่ยืนยัน/กลุ่มกลาง/เกินเพดาน · แถว legacy) — ห้ามเขียนเงื่อนไขซ้ำ</item>
/// </list>
/// LINE id ไม่ออกจากเซิร์ฟเวอร์ (key = <see cref="LineOaHealth.EndpointTargetKey"/>) · ทุก query ผูก CompanyId
/// </summary>
public static class LineTestTargets
{
    public const int MaxEndpoints = 200;

    private sealed record Candidate(ContactNotifyEndpoint Endpoint, string ContactName);

    /// <summary>ปลายทางที่ใช้ทดสอบได้ — ขอบเขต OA บริษัท + บอทปัจจุบัน (ตัวกรองแคบ) แล้วตัดสินด้วยตัวตัดสินผู้รับเดียว</summary>
    private static async Task<List<Candidate>> CandidatesAsync(AccountingDbContext db, ISecretProtector secrets, Guid companyId, string? oaDestination,
        IReadOnlyCollection<string> platformGroupIds, Guid? onlyEndpoint, CancellationToken ct)
    {
        var q = from e in db.ContactNotifyEndpoints.AsNoTracking()
                join c in db.Contacts.AsNoTracking() on e.ContactId equals c.Id
                where e.CompanyId == companyId && c.CompanyId == companyId && !e.IsDeleted && !c.IsDeleted
                      && (e.Kind == NotifyEndpointKind.LineUser || e.Kind == NotifyEndpointKind.LineGroup)
                      && e.OaScope == NotifyEndpointKind.ScopeCompany
                      && (oaDestination == null || e.OaDestination == null || e.OaDestination == oaDestination)
                select new { e, c.Name };
        if (onlyEndpoint is Guid only) q = q.Where(x => x.e.Id == only);
        var rows = await q.OrderBy(x => x.Name).ThenBy(x => x.e.Kind).ThenBy(x => x.e.LinkedAt).Take(MaxEndpoints).ToListAsync(ct);
        var consents = await ContactRecipientResolver.ConsentsAsync(db, companyId, rows.Select(r => r.e), ct);
        return rows.Where(r => ContactRecipientRule.Status(ContactRecipientResolver.FactsOf(r.e, consents, secrets), platformGroupIds,
                    ContactRecipientRule.LineOpenness.Unrestricted).Usable)
            .Select(r => new Candidate(r.e, r.Name)).ToList();
    }

    public static async Task<IReadOnlyList<LineTestTarget>> ListAsync(AccountingDbContext db, ISecretProtector secrets, Guid companyId,
        IReadOnlyCollection<string> platformGroupIds, CancellationToken ct)
    {
        var s = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineDefaultGroupId, x.LineOaDestination }).FirstOrDefaultAsync(ct);
        var list = new List<LineTestTarget>();
        if (!string.IsNullOrWhiteSpace(s?.LineDefaultGroupId))
            list.Add(new LineTestTarget(LineOaHealth.TeamGroupTargetKey, "กลุ่ม LINE ของทีม (ที่ตั้งไว้ด้านบน)"));
        foreach (var r in await CandidatesAsync(db, secrets, companyId, s?.LineOaDestination, platformGroupIds, null, ct))
            list.Add(new LineTestTarget(LineOaHealth.EndpointTargetKey(r.Endpoint.Id),
                LineOaHealth.EndpointTargetLabel(r.ContactName, r.Endpoint.Kind, r.Endpoint.GroupNameSnapshot, r.Endpoint.Label), ReachesContact: true));
        return list;
    }

    /// <summary>ผลหาใหม่ตอนส่ง — <paramref name="ContactId"/> ใช้ลง audit/PiiAccessLog (ม.37)</summary>
    public sealed record Resolved(string? To, string? Problem, Guid? ContactId);

    /// <summary>หาปลายทางของ key ใหม่ตอนส่ง (ไม่เชื่อรายการที่หน้าเว็บถือไว้) — เส้นเดียวกับรายการ (ตัวตัดสินผู้รับ · ผู้ติดต่อไม่ถูกลบ)</summary>
    public static async Task<Resolved> ResolveAsync(AccountingDbContext db, ISecretProtector secrets, Guid companyId, Guid endpointId,
        IReadOnlyCollection<string> platformGroupIds, CancellationToken ct)
    {
        var oa = await db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => x.LineOaDestination).FirstOrDefaultAsync(ct);
        var hit = (await CandidatesAsync(db, secrets, companyId, oa, platformGroupIds, endpointId, ct)).FirstOrDefault();
        if (hit == null)
            return new(null, "ไม่พบปลายทางที่เลือกในบริษัทนี้ หรือใช้ส่งไม่ได้แล้ว (ยกเลิก/บล็อก/ถอนความยินยอม/ผู้ติดต่อถูกลบ/ผูกกับ LINE OA อื่น) — โหลดหน้านี้ใหม่", null);
        var ep = hit.Endpoint;
        var to = secrets.Unprotect(ep.Kind == NotifyEndpointKind.LineGroup ? ep.LineGroupId : ep.LineUserId);
        return string.IsNullOrWhiteSpace(to)
            ? new(null, LineFailureAlert.DecryptAdvice, ep.ContactId)
            : new(to.Trim(), null, ep.ContactId);
    }
}
