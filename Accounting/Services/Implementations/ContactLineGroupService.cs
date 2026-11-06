using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IContactLineGroupService"/> — รอบ 254 WP-11 L3 · ตัวตั้ง <see cref="LineGroupBindRule"/> · ด่านกลุ่ม <see cref="ContactRecipientRule.GroupProblem"/>
/// (กลุ่มกลางของแอดมิน · > 200 คน) ตัวเดียวกับฝั่งส่ง · ทุก query ของปลายทางมี <c>CompanyId == companyId</c> · groupId เข้ารหัสทุกที่ที่เก็บ</summary>
public class ContactLineGroupService : IContactLineGroupService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ILineNotifyService _line;
    private readonly IContactRecipientResolver _recipients;
    private readonly INextAccLineConfig _nextAcc;
    private readonly IChatRateLimiter _limiter;
    private readonly ILogger<ContactLineGroupService> _logger;

    public ContactLineGroupService(AccountingDbContext db, ISecretProtector secrets, ILineNotifyService line, IContactRecipientResolver recipients,
        INextAccLineConfig nextAcc, IChatRateLimiter limiter, ILogger<ContactLineGroupService> logger)
    {
        _db = db; _secrets = secrets; _line = line; _recipients = recipients; _nextAcc = nextAcc; _limiter = limiter; _logger = logger;
    }

    // ───────────── webhook ─────────────

    public async Task<bool> MarkEventSeenAsync(string? webhookEventId, CancellationToken ct = default)
    {
        var id = (webhookEventId ?? "").Trim();
        if (id.Length == 0 || id.Length > 100) return true;
        var n = await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"LineWebhookEventsSeen\" (\"WebhookEventId\", \"SeenAt\") VALUES ({0}, {1}) ON CONFLICT DO NOTHING",
            new object[] { id, DateTime.UtcNow }, ct);
        return n == 1;
    }

    public async Task<LineGroupEventOutcome> HandleCompanyGroupEventAsync(Guid companyId, string? destination, JsonElement ev, CancellationToken ct = default)
    {
        var type = ev.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!ev.TryGetProperty("source", out var src)) return LineGroupEventOutcome.Nothing;
        var srcType = src.TryGetProperty("type", out var st) ? st.GetString() : null;
        var text = type == "message" && ev.TryGetProperty("message", out var msg) && msg.TryGetProperty("type", out var mt) && mt.GetString() == "text"
            && msg.TryGetProperty("text", out var tx) ? tx.GetString() : null;
        if (srcType == "room")
            return LineGroupBindRule.Parse(text) == LineGroupCommand.None ? LineGroupEventOutcome.Nothing
                : new LineGroupEventOutcome("รองรับเฉพาะกลุ่ม LINE (ห้องแชตหลายคนแบบเก่าผูกไม่ได้) — สร้างกลุ่มแล้วเชิญบอทเข้ากลุ่มแทน", false);
        if (srcType != "group") return LineGroupEventOutcome.Nothing;
        var groupId = src.TryGetProperty("groupId", out var g) ? (g.GetString() ?? "").Trim() : "";
        if (groupId.Length == 0) return LineGroupEventOutcome.Nothing;
        var dest = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();
        var idHash = NotifyEndpointKind.LineIdHash(dest, groupId);

        switch (type)
        {
            case "join":
                return new LineGroupEventOutcome(LineGroupBindRule.Replies.Join, false);
            case "leave":
                await UnbindGroupAsync(companyId, idHash, LineGroupBindRule.UnlinkBotRemoved, ct);
                return LineGroupEventOutcome.Nothing;
            case "memberJoined":
            case "memberLeft":
                await RefreshMemberCountAsync(companyId, groupId, idHash, ct);
                return LineGroupEventOutcome.Nothing;
            case "message":
                switch (LineGroupBindRule.Parse(text))
                {
                    case LineGroupCommand.HeyNextAcc:
                        return new LineGroupEventOutcome(await IssueCodeAsync(companyId, dest, groupId, idHash, ct), false);
                    case LineGroupCommand.ByeNextAcc:
                        var n = await UnbindGroupAsync(companyId, idHash, LineGroupBindRule.UnlinkGroupBye, ct);
                        return n > 0 ? new LineGroupEventOutcome(LineGroupBindRule.Replies.Bye, true)
                            : new LineGroupEventOutcome(LineGroupBindRule.Replies.ByeNothing, false);
                }
                // ข้อความอื่นในกลุ่ม: ไม่ประมวล ไม่เก็บ (PDPA ม.22)
                return LineGroupEventOutcome.Nothing;
        }
        return LineGroupEventOutcome.Nothing;
    }

    /// <summary>#HeyNextAcc ⇒ รหัสผูก (ตอบด้วย reply · ฟรี) · กลุ่มกลางของแอดมิน/เกิน 200 คน/อ่านจำนวนไม่ได้ ⇒ ปฏิเสธพร้อมเหตุ</summary>
    private async Task<string> IssueCodeAsync(Guid companyId, string? dest, string groupId, string idHash, CancellationToken ct)
    {
        if (!await _limiter.TryConsumeAsync("linegroup:" + idHash, 2, 10, ct, failClosed: true)) return LineGroupBindRule.Replies.Throttled;
        var platform = await _nextAcc.PlatformGroupIdsAsync(ct);
        if (ContactRecipientRule.GroupProblem(groupId, null, platform) is { } early) return LineGroupBindRule.Replies.Refused(early.Problem);
        var summary = await _line.TryGetCompanyGroupSummaryAsync(companyId, groupId);
        if (summary?.MemberCount == null)
            return LineGroupBindRule.Replies.Refused("อ่านจำนวนสมาชิกของกลุ่มไม่ได้ (ตรวจว่า OA เปิด \"Allow bot to join group chats\" และ token ยังใช้ได้) — ลองใหม่ภายหลัง");
        if (ContactRecipientRule.GroupProblem(groupId, summary.MemberCount, platform) is { } gp) return LineGroupBindRule.Replies.Refused(gp.Problem);
        var code = LineGroupBindRule.NewCode();
        var now = DateTime.UtcNow;
        var row = new LineGroupBindCode
        {
            CodeHash = LineGroupBindRule.Hash(code), GroupIdProtected = _secrets.Protect(groupId), GroupIdHash = idHash,
            OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = dest, CompanyIdHint = companyId,
            GroupName = LineDocumentCard.Clean(summary.Name, 200), MemberCount = summary.MemberCount, ExpiresAt = now + LineGroupBindRule.CodeLifetime,
            CreatedBy = "line-webhook",
        };
        _db.LineGroupBindCodes.Add(row);
        await _db.SaveChangesAsync(ct);
        return LineGroupBindRule.Replies.Code(code, row.GroupName, row.ExpiresAt);
    }

    /// <summary>ยกเลิกทุกปลายทางกลุ่มนี้ของบริษัทนี้ (บอทถูกเตะ/#ByeNextAcc) + แถวคิวที่ค้าง ⇒ ข้ามพร้อมเหตุ · audit · คืนจำนวนปลายทาง</summary>
    private async Task<int> UnbindGroupAsync(Guid companyId, string idHash, string reason, CancellationToken ct)
    {
        var rows = await _db.ContactNotifyEndpoints
            .Where(e => e.CompanyId == companyId && e.Kind == NotifyEndpointKind.LineGroup && e.LineIdHash == idHash && e.UnlinkedAt == null)
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            r.UnlinkedAt = now;
            r.UnlinkReason = reason;
            r.LineGroupId = null;   // PDPA ม.22: เลิกใช้แล้วไม่เก็บ id (คงแฮชเป็นร่องรอย)
            r.UpdatedAt = now;
        }
        var ids = rows.Select(r => (Guid?)r.Id).ToList();
        var why = "ไม่ส่ง — " + NotifyEndpointKind.UnlinkReasonLabel(reason);
        await _db.LineQueues.Where(q => q.CompanyId == companyId && ids.Contains(q.EndpointId) && q.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, ContactLineChannel.StatusSkipped).SetProperty(q => q.ErrorMessage, why)
                .SetProperty(q => q.UpdatedAt, (DateTime?)now), ct);
        foreach (var contactId in rows.Select(r => r.ContactId).Distinct())
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserEmail = "line-webhook", Action = AuditAction.Delete,
                EntityType = ContactRecipientRule.AuditEntityType, EntityId = contactId.ToString(),
                NewValues = JsonSerializer.Serialize(new { op = "line-group-unbind", reason, rule = LineGroupBindRule.RuleCode }),
                Timestamp = now,
            });
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task RefreshMemberCountAsync(Guid companyId, string groupId, string idHash, CancellationToken ct)
    {
        var has = await _db.ContactNotifyEndpoints.AsNoTracking()
            .AnyAsync(e => e.CompanyId == companyId && e.LineIdHash == idHash && e.UnlinkedAt == null, ct);
        if (!has) return;
        var summary = await _line.TryGetCompanyGroupSummaryAsync(companyId, groupId);
        if (summary?.MemberCount is not int count) return;
        // เกิน 200 คนหลังผูก ⇒ ตัวตัดสินฝั่งส่ง (GroupProblem) หยุดส่งเองจากจำนวนนี้ (Q2) — ไม่เก็บ userId ของสมาชิก
        await _db.ContactNotifyEndpoints.Where(e => e.CompanyId == companyId && e.LineIdHash == idHash && e.UnlinkedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.MemberCountSnapshot, (int?)count).SetProperty(e => e.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
    }

    public async Task LeaveGroupIfUnboundAsync(Guid companyId, string? destination, string groupId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), groupId.Trim());
        var still = await _db.ContactNotifyEndpoints.AsNoTracking()
            .AnyAsync(e => e.CompanyId == companyId && e.Kind == NotifyEndpointKind.LineGroup && e.LineIdHash == idHash && e.UnlinkedAt == null, ct);
        if (!still) await _line.TryLeaveCompanyGroupAsync(companyId, groupId.Trim());
    }

    public async Task LeaveIfUnboundAsync(Guid companyId, string? protectedGroupId, string? lineIdHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineIdHash) || string.IsNullOrWhiteSpace(protectedGroupId)) return;
        var still = await _db.ContactNotifyEndpoints.AsNoTracking()
            .AnyAsync(e => e.CompanyId == companyId && e.Kind == NotifyEndpointKind.LineGroup && e.LineIdHash == lineIdHash && e.UnlinkedAt == null, ct);
        if (still) return;
        var plain = _secrets.Unprotect(protectedGroupId);
        if (!string.IsNullOrWhiteSpace(plain)) await _line.TryLeaveCompanyGroupAsync(companyId, plain);
    }

    // ───────────── ผู้ดูแลบริษัท ─────────────

    private sealed record Checked(LineGroupBindCode? Row, string? GroupId, string ContactName, string? Problem);

    /// <summary>ตรวจรหัส + ผู้ติดต่อ + OA + กลุ่ม — ตัวเดียวของ preview/confirm</summary>
    private async Task<Checked> CheckAsync(Guid companyId, Guid contactId, string? code, CancellationToken ct)
    {
        var contactName = await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted).Select(c => c.Name).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
        var parsed = LineGroupBindRule.TryParseCode(code);
        if (parsed == null) return new(null, null, contactName, "รหัสไม่ถูกต้อง — ใส่รหัส GB-XXXXXXXX ที่บอทตอบในกลุ่ม (ใช้ Group ID อย่างเดียวผูกไม่ได้)");
        var hash = LineGroupBindRule.Hash(parsed);
        var row = await _db.LineGroupBindCodes.AsNoTracking().FirstOrDefaultAsync(r => r.CodeHash == hash, ct);
        var now = DateTime.UtcNow;
        if (row == null || !LineGroupBindRule.IsRedeemable(row.ExpiresAt, row.UsedAt, now))
            return new(null, null, contactName, "รหัสไม่ถูกต้อง หมดอายุ หรือใช้ไปแล้ว — พิมพ์ #HeyNextAcc ในกลุ่มเพื่อรับรหัสใหม่");
        if (row.CompanyIdHint == null)
            return new(null, null, contactName, "รหัสนี้มาจาก LINE OA กลางของ NextAcc — การส่งเข้ากลุ่มผ่าน OA กลางยังไม่เปิด · เชิญ LINE OA ของบริษัทเข้ากลุ่มแทนแล้วพิมพ์ #HeyNextAcc ใหม่");
        if (row.CompanyIdHint != companyId)
            return new(null, null, contactName, "รหัสไม่ถูกต้อง หมดอายุ หรือใช้ไปแล้ว — พิมพ์ #HeyNextAcc ในกลุ่มเพื่อรับรหัสใหม่");   // ไม่บอกว่าเป็นของบริษัทอื่น
        var line = await _recipients.LineChannelAsync(companyId, ct);
        if (!line.Open || !line.CompanyOaReady)
            return new(null, null, contactName, (line.CompanyOaProblem ?? "LINE OA ของบริษัทยังไม่พร้อม") + " — แก้ที่ ตั้งค่า → แจ้งเตือนทาง LINE");
        if (ContactRecipientRule.OaChanged(row.OaDestination, line.CompanyOaDestination))
            return new(null, null, contactName, "บริษัทเปลี่ยน LINE OA หลังออกรหัส — เชิญ OA ปัจจุบันเข้ากลุ่มแล้วพิมพ์ #HeyNextAcc ใหม่");
        var groupId = _secrets.Unprotect(row.GroupIdProtected);
        if (ContactRecipientRule.GroupProblem(groupId, row.MemberCount, await _nextAcc.PlatformGroupIdsAsync(ct)) is { } gp)
            return new(null, null, contactName, gp.Problem);
        return new(row, groupId, contactName, null);
    }

    public async Task<ContactLineGroupPreview> PreviewAsync(Guid companyId, Guid contactId, string? code, Guid? actorUserId, CancellationToken ct = default)
    {
        if (!await _limiter.TryConsumeAsync("linegroup-code:" + (actorUserId?.ToString("N") ?? "anon"), 5, 50, ct, failClosed: true))
            throw new BusinessRuleException("ตรวจรหัสบ่อยเกินไป — กรุณารอสักครู่", LineGroupBindRule.RuleCode, 429);
        var c = await CheckAsync(companyId, contactId, code, ct);
        if (c.Problem != null || c.Row == null) return new ContactLineGroupPreview(null, null, c.ContactName, null, null, c.Problem);
        var attest = LineGroupBindRule.AttestText(c.ContactName, c.Row.GroupName);
        return new ContactLineGroupPreview(c.Row.GroupName, c.Row.MemberCount, c.ContactName, attest, CmsDataConsent.Version(attest), null);
    }

    public async Task<ContactNotifyOverviewResponse> ConfirmAsync(Guid companyId, Guid contactId, ConfirmContactLineGroupRequest request,
        ContactNotifyActor actor, CancellationToken ct = default)
    {
        if (request == null || !request.AcceptResponsibility)
            throw new BusinessRuleException("ติ๊กยืนยันว่าสมาชิกทุกคนในกลุ่มมีสิทธิ์เห็นเอกสารของผู้ติดต่อนี้ก่อนผูก (PDPA)", LineGroupBindRule.RuleCode, 400);
        if (!await _limiter.TryConsumeAsync("linegroup-code:" + (actor.UserId?.ToString("N") ?? "anon"), 5, 50, ct, failClosed: true))
            throw new BusinessRuleException("ตรวจรหัสบ่อยเกินไป — กรุณารอสักครู่", LineGroupBindRule.RuleCode, 429);
        IReadOnlyList<NotifyTopic> topics;
        if (request.Topics == null) topics = NotifyTopics.DefaultsFor(NotifyEndpointKind.LineGroup);
        else
        {
            var (parsed, problem) = NotifyTopics.FromRequest(request.Topics, allowEmpty: false);
            if (problem != null) throw new BusinessRuleException(problem, "NOTIFY-CONTACT-TOPIC", 400);
            topics = parsed!;
        }
        var c = await CheckAsync(companyId, contactId, request.Code, ct);
        if (c.Problem != null || c.Row == null || string.IsNullOrWhiteSpace(c.GroupId))
            throw new BusinessRuleException(c.Problem ?? "ผูกกลุ่มไม่ได้", LineGroupBindRule.RuleCode, 409);
        var row = c.Row;
        var groupId = c.GroupId!;
        var now = DateTime.UtcNow;
        ContactNotifyEndpoint endpoint;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.ContactNotifyEndpoint, contactId.ToString()) }, ct);
            // ใช้ครั้งเดียว — สองคำขอพร้อมกันได้แถวเดียว · รหัสของบริษัทนี้เท่านั้น
            var claimed = await _db.LineGroupBindCodes
                .Where(r => r.Id == row.Id && r.UsedAt == null && r.CompanyIdHint == companyId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, (DateTime?)now).SetProperty(r => r.UsedByCompanyId, (Guid?)companyId), ct);
            if (claimed != 1)
                throw new BusinessRuleException("รหัสนี้ถูกใช้ไปแล้ว — พิมพ์ #HeyNextAcc ในกลุ่มเพื่อรับรหัสใหม่", LineGroupBindRule.RuleCode, 409);

            var previous = await _db.ContactNotifyEndpoints
                .Where(e => e.CompanyId == companyId && e.ContactId == contactId && e.Kind == NotifyEndpointKind.LineGroup
                    && e.LineIdHash == row.GroupIdHash && e.UnlinkedAt == null)
                .ToListAsync(ct);
            foreach (var p in previous) { p.UnlinkedAt = now; p.UnlinkReason = "relinked"; p.LineGroupId = null; p.UpdatedAt = now; }

            // PDPA ม.23 (DESIGN §6.3): ผู้ดูแลบริษัทรับรองสิทธิ์ของสมาชิก — หลักฐาน hash ผ่านตัวเดียวของระบบ · ประกาศในกลุ่มคือการแจ้งสมาชิก
            var attest = LineGroupBindRule.AttestText(c.ContactName, row.GroupName);
            var version = CmsDataConsent.Version(attest);
            var granted = PdpaConsentEvidence.NormalizeTimestamp(now);
            var channel = $"staff-attest:{(actor.Email ?? "unknown").Trim()}";
            var subject = $"line-group:{row.GroupIdHash[..16]}";
            var consent = new PdpaConsentRecord
            {
                CompanyId = companyId, SubjectContactId = contactId, SubjectContact = subject, Purpose = LineGroupBindRule.AttestPurpose,
                PolicyVersion = version, GrantedAt = granted, Channel = channel, IpAddress = actor.IpAddress,
                EvidenceHash = PdpaConsentEvidence.ComputeHash(subject, LineGroupBindRule.AttestPurpose, version, granted, channel, actor.IpAddress, null),
                EvidenceSource = PdpaConsentEvidence.SourceSystem, CreatedBy = actor.Email,
            };
            _db.PdpaConsentRecords.Add(consent);
            endpoint = new ContactNotifyEndpoint
            {
                CompanyId = companyId, ContactId = contactId, Kind = NotifyEndpointKind.LineGroup,
                LineGroupId = _secrets.Protect(groupId), LineIdHash = row.GroupIdHash,
                OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = row.OaDestination,
                GroupNameSnapshot = row.GroupName, MemberCountSnapshot = row.MemberCount,
                TopicsJson = NotifyTopics.Serialize(topics), ShowAmounts = request.ShowAmounts ?? true,   // Q5: แสดงยอดเป็นค่าตั้งต้น
                Verified = true, Source = LineGroupBindRule.SourceGroupCode, ConsentRecordId = consent.Id,
                BoundByUserId = actor.UserId, LinkedAt = now, CreatedBy = actor.Email,
            };
            _db.ContactNotifyEndpoints.Add(endpoint);
            await _db.LineGroupBindCodes.Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.EndpointId, (Guid?)endpoint.Id), ct);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = actor.UserId, UserEmail = actor.Email, Action = AuditAction.Create,
                EntityType = ContactRecipientRule.AuditEntityType, EntityId = contactId.ToString(), IpAddress = actor.IpAddress,
                NewValues = JsonSerializer.Serialize(new
                {
                    op = "line-group-bind", endpointId = endpoint.Id, members = row.MemberCount, showAmounts = endpoint.ShowAmounts,
                    topics = endpoint.TopicsJson, relinked = previous.Count, rule = LineGroupBindRule.RuleCode,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        // ประกาศในกลุ่มครั้งเดียว (ข้อความระบบ · ผ่าน OA ของบริษัท) — ล้มไม่ยกเลิกการผูก แต่ log ดัง
        var companyName = await _db.Companies.AsNoTracking().Where(x => x.Id == companyId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        var announce = await _line.TryPushContactTextAsync(companyId, NotifyEndpointKind.ScopeCompany, LineTargetKind.ContactGroup, groupId,
            LineGroupBindRule.Replies.Bound(companyName, c.ContactName, topics.Select(NotifyTopics.Label)), Guid.NewGuid());
        if (!announce.Ok) _logger.LogWarning("ประกาศผูกกลุ่ม LINE ไม่สำเร็จ (company {Company}): {Error}", companyId, announce.Error);

        return await _recipients.GetOverviewAsync(companyId, contactId, canEdit: true, ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
    }
}
