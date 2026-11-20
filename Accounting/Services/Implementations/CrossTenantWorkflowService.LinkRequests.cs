using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
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
            var windowQ = _db.TradingPartnerships.AsNoTracking()
                .Where(x => (x.CompanyAId == target || x.CompanyBId == target) && x.InvitedByCompanyId != target
                    && x.InviteNotifiedAt != null && x.InviteNotifiedAt >= since
                    && x.InviteNoticeResult == nameof(Accounting.Services.Interfaces.NotificationDispatchOutcome.Delivered));
            var sentToTarget = await windowQ.CountAsync(ct);
            var action = PartnerLinkRequestRules.Decide(
                PartnerLinkRequestRules.IsExpired(p.Status, p.InvitedAt, p.CreatedAt, nowUtc), requestedAt, nowUtc, sentToTarget,
                previouslyFailed: p.InviteNoticeResult == nameof(Accounting.Services.Interfaces.NotificationDispatchOutcome.Failed));
            if (action == PartnerLinkRequestRules.NoticeAction.SkipCapped)
            {
                // ฝ่ายค้าน M3 (คำตัดสิน 264-1-10): เกินเพดาน ⇒ เลื่อนไปเมื่อหน้าต่าง 24 ชม. ว่าง — ไม่ประทับว่าแจ้งแล้ว (เดิมเงียบถาวร ⇒ ผู้โจมตีปิดเสียงคำขอจริงได้)
                p.InviteNoticeResult = action.ToString();
                p.InviteNoticeDueAt = PartnerLinkRequestRules.CapRetryAt(await windowQ.MinAsync(x => x.InviteNotifiedAt, ct), nowUtc);
                await _db.SaveChangesAsync(ct);
                handled++;
                continue;
            }
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
                .Select(c => new { c.TaxId, c.BranchCode }).FirstOrDefaultAsync(ct);
            var outcome = Accounting.Services.Interfaces.NotificationDispatchOutcome.NoRecipient;
            if (_notify != null)
                outcome = await _notify.DispatchWithOutcomeAsync(target, Accounting.Models.Constants.NotificationEvents.PartnerLinkRequested,
                    new Accounting.Services.Interfaces.NotificationContext
                    {
                        Title = PartnerLinkRequestRules.NoticeTitle,
                        Message = PartnerLinkRequestRules.NoticeMessage(requester?.TaxId, requester?.BranchCode, PartnerLinkRequestRules.RequestExpiryUtc(p.InvitedAt, p.CreatedAt)),
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

    /// <summary>
    /// รอบ 280 (คำตัดสิน 280-5 · แก้ตามฝ่ายค้าน MED-1 = 280-10) — คำขอที่ยังรอถึงเลขภาษีของ <paramref name="companyId"/> ซึ่งยังไม่มีแถวคู่ ⇒ เปิดแถว "รอเรายอมรับ"
    /// (ตัวสร้างเดียว <see cref="OfferLinkRequestAsync"/>) · <b>เฉพาะคำขอที่ส่งหลัง <c>Company.TaxIdSetAt</c></b> — ระบบยังไม่มีขั้นยืนยันเลขภาษี ⇒ สมัคร/เปลี่ยนเลขเป็นของคนอื่น
    /// แล้วเก็บคำขอย้อนหลังไม่ได้ (คำขอก่อนหน้า ⇒ ผู้ขอกด "ส่งแจ้งอีกครั้ง" = เสนอใหม่) · ผู้เรียก: <c>POST …/trading-partners/requests/incoming/refresh</c> (สิทธิ์ตั้งค่าบริษัท)
    /// </summary>
    public async Task<int> MaterializeIncomingLinkRequestsAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var me = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId && !c.IsDeleted)
            .Select(c => new { c.TaxId, c.TaxIdSetAt }).FirstOrDefaultAsync(ct);
        var tax = Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(me?.TaxId);
        if (tax == null || me!.TaxIdSetAt is not DateTime setAt) return 0;
        var open = await _db.PartnerLinkRequests.AsNoTracking()
            .Where(r => r.PartnerTaxId == tax && r.AcceptedAt == null && r.ExpiresAt > nowUtc && r.CompanyId != companyId && r.CreatedAt >= setAt)
            .OrderByDescending(r => r.CreatedAt).Take(50)
            .ToListAsync(ct);
        var created = 0;
        foreach (var r in open.GroupBy(x => x.CompanyId).Select(g => g.First()))   // คำขอล่าสุดต่อผู้ขอ
            created += await OfferLinkRequestAsync(_db, r, nowUtc, onlyCompanyId: companyId, ct);
        return created;
    }

    /// <summary>
    /// รอบ 280 — ตัวสร้างแถวคู่ "รอยอมรับ" จากคำขอที่มีอยู่ (ตัวเดียว: เปิดเมื่อปลายทางเปิดหน้า · ผู้ขอ "ส่งแจ้งอีกครั้ง") · ตัวจับคู่เดียวกับตอนส่ง
    /// (<see cref="Accounting.Helpers.PartnerCompanyMatch.InviteTargets"/>) · ล็อกต่อคู่เดียวกับ <see cref="InvitePartnerAsync"/> · คู่ที่มีแถวแล้ว (ทุกสถานะ — ปฏิเสธ/บล็อก/ระงับ) ไม่ถูกแตะ ·
    /// อายุนับจากวันที่ผู้ขอส่ง · แจ้งในระบบผ่าน outbox เดิม · <paramref name="onlyCompanyId"/> = จำกัดปลายทาง (null = ทุกบริษัทที่ตรง) · คืนจำนวนแถวที่สร้าง (ผู้เรียกห้ามส่งต่อถึงผู้ขอ)
    /// </summary>
    internal static async Task<int> OfferLinkRequestAsync(AccountingDbContext db, PartnerLinkRequest r, DateTime nowUtc, Guid? onlyCompanyId, CancellationToken ct)
    {
        if (!await db.Companies.AsNoTracking().AnyAsync(c => c.Id == r.CompanyId && !c.IsDeleted, ct)) return 0;
        var candidates = await db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted && c.Id != r.CompanyId && c.TaxId.Replace("-", "").Replace(" ", "") == r.PartnerTaxId)
            .Select(c => new Accounting.Helpers.PartnerCompanyCandidate(c.Id, c.BranchCode)).ToListAsync(ct);
        var targets = Accounting.Helpers.PartnerCompanyMatch.InviteTargets(candidates, r.PartnerBranchCode)
            .Where(t => onlyCompanyId == null || t == onlyCompanyId).ToList();
        var created = 0;
        foreach (var target in targets)
        {
            var (a, b) = OrderPair(r.CompanyId, target);
            var ownTx = db.Database.CurrentTransaction == null ? await db.Database.BeginTransactionAsync(ct) : null;
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    new object[] { Accounting.Helpers.AdvisoryLockKey.For(a, Accounting.Helpers.AdvisoryLockKey.TradingPartnerInvite, "invite:" + b.ToString("N")) }, ct);
                if (!await db.TradingPartnerships.AnyAsync(p => p.CompanyAId == a && p.CompanyBId == b, ct))
                {
                    db.TradingPartnerships.Add(new TradingPartnership
                    {
                        CompanyAId = a, CompanyBId = b, Status = TradingPartnershipStatus.Pending,
                        InvitedByCompanyId = r.CompanyId, InvitedByUserId = r.RequestedByUserId, InvitationMessage = r.Message,
                        CreatedBy = r.RequestedByUserId.ToString(),
                        InvitedAt = r.CreatedAt, InviteNoticeDueAt = nowUtc, LinkRequestId = r.Id,
                    });
                    await db.SaveChangesAsync(ct);
                    created++;
                }
                if (ownTx != null) await ownTx.CommitAsync(ct);
            }
            finally
            {
                if (ownTx != null) await ownTx.DisposeAsync();
            }
        }
        return created;
    }

    /// <summary>ฝ่ายค้าน MED-1 (ค) — ข้อความบนแถวคู่ที่ถูกยกเลิกเพราะบริษัทปลายทางเปลี่ยนเลขภาษี (ผู้ขอไม่เห็น — สถานะของผู้ขอยังเป็น "รอตอบ" จนหมดอายุ)</summary>
    public const string TaxIdChangedReason = "ยกเลิกอัตโนมัติ — บริษัทปลายทางเปลี่ยนเลขผู้เสียภาษี (คำขอนี้ส่งถึงเลขเดิม)";

    /// <summary>
    /// ฝ่ายค้าน MED-1 (ค · คำตัดสิน 280-10): บริษัทเปลี่ยนเลขภาษี ⇒ ยกเลิกคำขอที่ "รอเรายอมรับ" ซึ่งส่งถึง<b>เลขเดิม</b> (สถานะ Rejected + เหตุ · ล้าง outbox) + audit chain ·
    /// คู่ที่เชื่อมแล้วไม่ถูกแตะ (เชื่อมด้วยความยินยอมแล้ว — ระงับเองได้) · ผู้เรียก: <c>CompanyService.UpdateAsync</c> ก่อน SaveChanges (ธุรกรรมเดียวกัน) · คืนจำนวนแถว
    /// </summary>
    internal static async Task<int> VoidIncomingRequestsForOldTaxIdAsync(AccountingDbContext db, Guid companyId, string? oldTaxId, string? newTaxId, Guid actorUserId,
        DateTime nowUtc, CancellationToken ct = default)
    {
        var oldTax = Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(oldTaxId);
        if (oldTax == null || oldTax == Accounting.Helpers.PartnerCompanyMatch.NormalizeTaxId(newTaxId)) return 0;
        var oldRequestIds = db.PartnerLinkRequests.Where(r => r.PartnerTaxId == oldTax).Select(r => r.Id);
        var rows = await db.TradingPartnerships
            .Where(p => (p.CompanyAId == companyId || p.CompanyBId == companyId) && p.InvitedByCompanyId != companyId
                && p.Status == TradingPartnershipStatus.Pending && p.LinkRequestId != null && oldRequestIds.Contains(p.LinkRequestId!.Value))
            .ToListAsync(ct);
        foreach (var p in rows)
        {
            p.Status = TradingPartnershipStatus.Rejected;
            p.RejectedAt = nowUtc;
            p.RejectionReason = TaxIdChangedReason;
            p.InviteNoticeDueAt = null;
            p.UpdatedAt = nowUtc;
            p.UpdatedBy = actorUserId.ToString();
        }
        if (rows.Count > 0)
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = actorUserId, Action = AuditAction.Update, EntityType = "TradingPartnerRequestsVoidedByTaxIdChange",
                EntityId = companyId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new { partnerships = rows.Select(x => x.Id), reason = TaxIdChangedReason, ruleCode = PartnerLinkRequestChannels.RuleCode }),
                Timestamp = nowUtc,
            });
        return rows.Count;
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
            ExpiresAt = PartnerLinkRequestRules.RequestExpiryUtc(p.InvitedAt, p.CreatedAt),
            p.InvitationMessage,
            p.AcceptedAt,
            p.RejectedAt,
            Requester = new
            {
                // ฝ่ายค้าน M2/L5: ชื่อ = ค่าที่ผู้ขอตั้งเอง (ไม่มีการยืนยันทะเบียน) — ป้ายจากเซิร์ฟเวอร์ แสดงคู่กับชื่อเสมอ
                Name = requester?.Name, NameNote = PartnerLinkRequestRules.NameUnverifiedLabel, TaxId = requester?.TaxId,
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
            .Select(r => new { r.Id, r.PartnerTaxId, r.PartnerBranchCode, r.CreatedAt, r.ExpiresAt, r.AcceptedAt, r.ContactId })
            .ToListAsync();
        // รอบ 280: แจ้งไปช่องไหนแล้ว + เวลา + ผล (ล่าสุดต่อช่อง · ช่องในระบบ = "เข้าคิว" เสมอ — ไม่มีผลที่บอกสมาชิกภาพ) · tenant = ผู้ขอ
        var ids = rows.Select(r => r.Id).ToList();
        var notices = await _db.PartnerLinkRequestNotices.AsNoTracking()
            .Where(n => n.CompanyId == companyId && ids.Contains(n.LinkRequestId))
            .Select(n => new { n.LinkRequestId, n.Channel, n.Status, n.RecipientLabel, n.Reason, n.AttemptedAt, n.CompletedAt })
            .ToListAsync();
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var contactNames = await _db.Contacts.AsNoTracking().Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name);
        return rows.Select(r =>
        {
            var state = PartnerLinkRequestRules.OutgoingStateOf(r.AcceptedAt, r.ExpiresAt, nowUtc);
            var mine = notices.Where(n => n.LinkRequestId == r.Id).ToList();
            return (object)new
            {
                r.Id, r.PartnerTaxId,
                BranchLabel = r.PartnerBranchCode == null ? "ไม่ระบุสาขา" : TaxBranchCode.Label(r.PartnerBranchCode),
                SentAt = r.CreatedAt, r.ExpiresAt, r.AcceptedAt,
                State = state.ToString(), StateLabel = PartnerLinkRequestRules.OutgoingLabel(state),
                r.ContactId,
                ContactName = r.ContactId is Guid cid && contactNames.TryGetValue(cid, out var nm) ? nm : null,
                Notices = mine.GroupBy(n => n.Channel)
                    .Select(g => g.OrderByDescending(n => n.AttemptedAt).First())
                    .OrderBy(n => n.Channel == PartnerLinkRequestChannels.InApp ? 0 : n.Channel == PartnerLinkRequestChannels.LineGroup ? 1 : 2)
                    .Select(n => new
                    {
                        n.Channel, ChannelLabel = PartnerLinkRequestChannels.ChannelLabel(n.Channel),
                        n.Status, StatusLabel = PartnerLinkRequestChannels.StatusLabel(n.Status),
                        Recipient = n.RecipientLabel, n.Reason, At = n.CompletedAt ?? n.AttemptedAt,
                        Attempts = mine.Count(x => x.Channel == n.Channel && PartnerLinkRequestChannels.CountsAsAttempt(x.Status)),
                    }).ToList(),
                // ปุ่ม "ส่งแจ้งอีกครั้ง" — เฉพาะคำขอที่รอตอบ (สถานะที่ผู้ขอเห็นอยู่แล้ว) · กันถี่ตัดสินตอนกดต่อช่อง
                CanNotify = state == PartnerLinkRequestRules.OutgoingState.Awaiting,
                NotifyProblem = PartnerLinkRequestChannels.RequestProblem(r.AcceptedAt, r.ExpiresAt, nowUtc),
            };
        }).ToList();
    }
}
