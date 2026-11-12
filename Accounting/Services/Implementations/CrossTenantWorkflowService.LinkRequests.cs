using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 — คำขอเชื่อมคู่ค้าถึงปลายทางจริง (คำตัดสิน 264-1-1…264-1-8 · ตัวตัดสิน <see cref="PartnerLinkRequestRules"/>):
/// การแจ้งปลายทางจาก outbox (งานเบื้องหลัง) · การ์ดยอมรับของปลายทาง (<c>partners.html?request=</c>) · "คำขอที่เราส่ง" ของผู้ขอ (ไม่รั่วสมาชิกภาพ)
/// </summary>
public partial class CrossTenantWorkflowService
{
    /// <summary>จำนวนแถว outbox ต่อรอบงาน</summary>
    public const int LinkRequestNoticeBatch = 100;

    /// <summary>
    /// ส่ง "มีคำขอเชื่อมจากคู่ค้า" ของแถวที่ถึงเวลา (<c>InviteNoticeDueAt ≤ now</c>) ผ่านเครื่องแจ้งเตือนกลางของ<b>บริษัทปลายทาง</b>
    /// (คีย์ <see cref="Accounting.Models.Constants.NotificationEvents.PartnerLinkRequested"/> · ค่าตั้งต้น กระดิ่ง + อีเมล + LINE ถึงเจ้าของ/ผู้ดูแลตั้งค่าบริษัท) ·
    /// เพดานต่อปลายทางต่อวัน · หมดอายุ ⇒ ไม่แจ้ง · ล้ม ⇒ ลองใหม่ (เลิกหลัง 48 ชม.) · ผลประทับเป็นชื่อ enum บนแถว · ผู้เรียก: <c>AutopilotJob</c> ขั้น "PartnerLinkRequests" (JobLock)
    /// </summary>
    public async Task<int> DispatchPendingLinkRequestNoticesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var due = await _db.TradingPartnerships
            .Where(p => p.InviteNoticeDueAt != null && p.InviteNoticeDueAt <= nowUtc && p.InviteNotifiedAt == null
                && p.Status == TradingPartnershipStatus.Pending)
            .OrderBy(p => p.InviteNoticeDueAt)
            .Take(LinkRequestNoticeBatch)
            .ToListAsync(ct);
        var handled = 0;
        foreach (var p in due)
        {
            ct.ThrowIfCancellationRequested();
            var target = p.InvitedByCompanyId == p.CompanyAId ? p.CompanyBId : p.CompanyAId;
            var requestedAt = PartnerLinkRequestRules.RequestedAt(p.InvitedAt, p.CreatedAt);
            var since = nowUtc.AddHours(-24);
            // นับการแจ้งที่ปลายทางได้รับใน 24 ชม. (ทุกผู้ขอ · เฉพาะที่ส่งจริง) — tenant = ปลายทาง (แถวคู่ที่ปลายทางเป็นสมาชิกและไม่ใช่ผู้ขอ)
            var sentToTarget = await _db.TradingPartnerships.AsNoTracking()
                .CountAsync(x => (x.CompanyAId == target || x.CompanyBId == target) && x.InvitedByCompanyId != target
                    && x.InviteNotifiedAt != null && x.InviteNotifiedAt >= since
                    && x.InviteNoticeResult == nameof(Accounting.Services.Interfaces.NotificationDispatchOutcome.Delivered), ct);
            var action = PartnerLinkRequestRules.Decide(
                PartnerLinkRequestRules.IsExpired(p.Status, p.InvitedAt, p.CreatedAt, nowUtc), requestedAt, nowUtc, sentToTarget,
                previouslyFailed: p.InviteNoticeResult == nameof(Accounting.Services.Interfaces.NotificationDispatchOutcome.Failed));
            if (action != PartnerLinkRequestRules.NoticeAction.Send)
            {
                p.InviteNotifiedAt = nowUtc;
                p.InviteNoticeResult = action.ToString();
                p.InviteNoticeDueAt = null;
                await _db.SaveChangesAsync(ct);
                handled++;
                continue;
            }
            var requester = await _db.Companies.AsNoTracking().Where(c => c.Id == p.InvitedByCompanyId)
                .Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
            var outcome = Accounting.Services.Interfaces.NotificationDispatchOutcome.NoRecipient;
            if (_notify != null)
                outcome = await _notify.DispatchWithOutcomeAsync(target, Accounting.Models.Constants.NotificationEvents.PartnerLinkRequested,
                    new Accounting.Services.Interfaces.NotificationContext
                    {
                        Title = PartnerLinkRequestRules.NoticeTitle,
                        Message = PartnerLinkRequestRules.NoticeMessage(requester?.Name, requester?.TaxId, PartnerLinkRequestRules.ExpiryOf(p.InvitedAt, p.CreatedAt)),
                        ActionUrl = PartnerLinkRequestRules.AcceptUrl(target, p.Id),
                        EntityType = "TradingPartnership", EntityId = p.Id,
                    });
            p.InviteNoticeResult = outcome.ToString();
            if (outcome == Accounting.Services.Interfaces.NotificationDispatchOutcome.Failed)
            {
                // ล้มดัง: ผลบนแถว + log · ลองใหม่รอบหน้า (ไม่ประทับ "แจ้งแล้ว")
                p.InviteNoticeDueAt = nowUtc.AddMinutes(PartnerLinkRequestRules.NoticeRetryMinutes);
                _logger.LogError("แจ้งคำขอเชื่อมคู่ค้า {Partnership} ถึงบริษัท {Target} ไม่สำเร็จ — ลองใหม่ใน {Min} นาที", p.Id, target, PartnerLinkRequestRules.NoticeRetryMinutes);
            }
            else
            {
                // Delivered / NoRecipient / SuppressedByCompany / Unknown — ตัดสินแล้ว (คำขอยังอยู่ในรายการบนหน้าคู่ค้าของปลายทางเสมอ)
                p.InviteNotifiedAt = nowUtc;
                p.InviteNoticeDueAt = null;
            }
            await _db.SaveChangesAsync(ct);
            handled++;
        }
        return handled;
    }

    /// <summary>การ์ดยอมรับของปลายทาง (<c>partners.html?request=</c>) — เฉพาะบริษัทที่เป็นสมาชิกของคู่ <b>และไม่ใช่ผู้ขอ</b> · อื่น ๆ = null (ไม่พบ · ไม่บอกว่ามีแถว) ·
    /// ข้อมูลที่คืน = ของผู้ขอ (ผู้ขอเปิดเผยเองเมื่อส่ง) — ไม่มีข้อมูลของเราไหลไปผู้ขอจากการเปิดดู</summary>
    public async Task<object?> GetIncomingLinkRequestAsync(Guid companyId, Guid partnershipId, DateTime nowUtc)
    {
        var p = await _db.TradingPartnerships.AsNoTracking()
            .Where(x => x.Id == partnershipId && (x.CompanyAId == companyId || x.CompanyBId == companyId) && x.InvitedByCompanyId != companyId)
            .Select(x => new
            {
                x.Id, x.Status, x.InvitedAt, x.CreatedAt, x.InvitationMessage, x.AcceptedAt, x.RejectedAt, x.InvitedByCompanyId,
            })
            .FirstOrDefaultAsync();
        if (p == null) return null;
        var requester = await _db.Companies.AsNoTracking().Where(c => c.Id == p.InvitedByCompanyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync();
        var expired = PartnerLinkRequestRules.IsExpired(p.Status, p.InvitedAt, p.CreatedAt, nowUtc);
        // ทำไมกดตอบไม่ได้ (ทางไปต่ออยู่ในข้อความ) · ยอมรับแล้ว = ไม่ใช่ปัญหา
        var problem = p.Status == TradingPartnershipStatus.Accepted ? null
            : PartnerLinkRequestRules.AcceptProblem(p.Status, p.InvitedAt, p.CreatedAt, nowUtc);
        return new
        {
            p.Id,
            Status = p.Status.ToString(),
            StatusLabel = PartnerLinkRequestRules.PartnershipLabel(p.Status, invitedByUs: false, expired),
            CanRespond = PartnerLinkRequestRules.CanRespond(p.Status, invitedByUs: false, expired),
            Expired = expired,
            RequestedAt = PartnerLinkRequestRules.RequestedAt(p.InvitedAt, p.CreatedAt),
            ExpiresAt = PartnerLinkRequestRules.ExpiryOf(p.InvitedAt, p.CreatedAt),
            p.InvitationMessage,
            p.AcceptedAt,
            p.RejectedAt,
            Requester = new
            {
                Name = requester?.Name, TaxId = requester?.TaxId,
                BranchLabel = TaxBranchCode.Label(requester?.BranchCode),
            },
            ProblemMessage = problem,
        };
    }

    /// <summary>"คำขอที่เราส่ง" ของผู้ขอ — แถวของบริษัทผู้ขอเอง (tenant = <paramref name="companyId"/>) · สถานะจาก <see cref="PartnerLinkRequestRules.OutgoingStateOf"/>
    /// (ไม่มีบริษัทนี้ = ถูกปฏิเสธ = ยังไม่ตอบ ⇒ "รอคู่ค้าตอบรับ" จนหมดอายุ) · ล่าสุด 50 แถว</summary>
    public async Task<List<object>> ListOutgoingLinkRequestsAsync(Guid companyId, DateTime nowUtc)
    {
        var rows = await _db.PartnerLinkRequests.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Select(r => new { r.Id, r.PartnerTaxId, r.PartnerBranchCode, r.CreatedAt, r.ExpiresAt, r.AcceptedAt })
            .ToListAsync();
        return rows.Select(r =>
        {
            var state = PartnerLinkRequestRules.OutgoingStateOf(r.AcceptedAt, r.ExpiresAt, nowUtc);
            return (object)new
            {
                r.Id, r.PartnerTaxId,
                BranchLabel = r.PartnerBranchCode == null ? "ไม่ระบุสาขา" : TaxBranchCode.Label(r.PartnerBranchCode),
                SentAt = r.CreatedAt, r.ExpiresAt, r.AcceptedAt,
                State = state.ToString(), StateLabel = PartnerLinkRequestRules.OutgoingLabel(state),
            };
        }).ToList();
    }
}
