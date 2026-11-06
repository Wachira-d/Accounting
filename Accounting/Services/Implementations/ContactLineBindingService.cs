using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IContactLineBindingService"/> — รอบ 254 WP-11 L2 · ตัวตั้ง <see cref="ContactLineBindRule"/> · รูปแบบรหัส <see cref="CustomerLineLinkRule"/> ·
/// ทุก query มี <c>CompanyId == companyId</c> · LINE userId เข้ารหัสด้วย <see cref="ISecretProtector"/> (ช่อง <c>ContactNotifyEndpoint.LineUserId</c> อยู่ในทะเบียน SecretReprotectJob) ·
/// log ไม่มี userId เต็ม (<see cref="CustomerLineLinkRule.Mask"/>)</summary>
public class ContactLineBindingService : IContactLineBindingService
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IContactRecipientResolver _recipients;
    private readonly IChatRateLimiter _limiter;
    private readonly IConfiguration _config;
    private readonly ILogger<ContactLineBindingService> _logger;
    /// <summary>ฝ่ายค้าน M4: ชื่อแสดงจากโปรไฟล์ LINE (OA บริษัท) ตอนผูก · null (นอก DI/เทสต์) ⇒ ไม่มีชื่อ (ไม่ล้มการผูก)</summary>
    private readonly ILineNotifyService? _line;

    /// <summary>กดยินยอม/ขอรหัสต่อ IP (DB-backed · หลายเครื่อง)</summary>
    private const int ConsentPerMinute = 5;
    private const int ConsentPerDay = 40;

    public ContactLineBindingService(AccountingDbContext db, ISecretProtector secrets, IContactRecipientResolver recipients,
        IChatRateLimiter limiter, IConfiguration config, ILogger<ContactLineBindingService> logger, ILineNotifyService? line = null)
    {
        _db = db; _secrets = secrets; _recipients = recipients; _limiter = limiter; _config = config; _logger = logger; _line = line;
    }

    /// <summary>เปิดหน้าเชิญต่อ IP (ฝ่ายค้าน L3)</summary>
    private const int ViewPerMinute = 20;
    private const int ViewPerDay = 300;
    /// <summary>ส่งรหัสผูกต่อ LINE user (ฝ่ายค้าน L1 — ก่อนค้นรหัส)</summary>
    private const int RedeemPerMinute = 3;
    private const int RedeemPerDay = 10;

    public async Task<ContactLineInviteResponse> CreateInviteAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail,
        CancellationToken ct = default)
    {
        var contact = await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new { c.Id, c.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
        // OA ของบริษัทเท่านั้น (คำตัดสิน N8) — ตัวตัดสินความพร้อมเดียวกับสถานะปลายทาง/ด่านบันทึกกฎ
        var line = await _recipients.LineChannelAsync(companyId, ct);
        if (!line.Open || !line.CompanyOaReady)
            throw new BusinessRuleException((line.CompanyOaProblem ?? "LINE OA ของบริษัทยังไม่พร้อม")
                + " — ผูก LINE ของผู้ติดต่อได้ผ่าน LINE OA ของบริษัทเท่านั้น (ตั้งที่ ตั้งค่า → แจ้งเตือนทาง LINE)", ContactLineBindRule.RuleCode, 409);

        var token = ContactLineBindRule.NewToken();
        var now = DateTime.UtcNow;
        // ฝ่ายค้าน M4: ลิงก์ใหม่ ⇒ ลิงก์เดิมที่ยังเปิดของผู้ติดต่อนี้ใช้ไม่ได้ (ลิงก์ที่หลุดไปไม่ค้างใช้ได้ 7 วัน)
        var revoked = await _db.ContactLineInvites
            .Where(i => i.CompanyId == companyId && i.ContactId == contact.Id && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, (DateTime?)now).SetProperty(i => i.UpdatedAt, (DateTime?)now), ct);
        await EnsureRopaAsync(companyId, actorEmail, ct);
        var invite = new ContactLineInvite
        {
            CompanyId = companyId, ContactId = contact.Id, TokenHash = ContactLineBindRule.TokenHash(token),
            ExpiresAt = now + ContactLineBindRule.InviteLifetime, OaDestination = line.CompanyOaDestination,
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser)),
            CreatedByUserId = actorUserId, CreatedBy = actorEmail,
        };
        _db.ContactLineInvites.Add(invite);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId, UserEmail = actorEmail, Action = AuditAction.Create,
            EntityType = ContactRecipientRule.AuditEntityType, EntityId = contact.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "line-invite", inviteId = invite.Id, expiresAt = invite.ExpiresAt, revokedOpen = revoked, rule = ContactLineBindRule.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);

        var path = ContactLineBindRule.JoinPath(companyId, token);
        var url = NotificationLink.Absolute(await SystemBaseUrl.ResolveAsync(_db, _config, ct), path);
        return new ContactLineInviteResponse(path, url, url == null ? null : QrDataUri(url), invite.ExpiresAt,
            $"ส่งลิงก์นี้ให้ผู้รับของ \"{contact.Name}\" (หรือให้สแกน QR ต่อหน้า) — ผู้รับกดยินยอมบนมือถือของตัวเอง แล้วส่งรหัสในแชต LINE OA ของบริษัท · "
            + $"ลิงก์ใช้ได้ครั้งเดียว หมดอายุใน {ContactLineBindRule.InviteLifetime.TotalDays:0} วัน"
            + (url == null ? " · ยังไม่ได้ตั้ง URL ของระบบ (ผู้ดูแลระบบ) — หน้าเว็บประกอบลิงก์จากโดเมนที่เปิดอยู่" : ""));
    }

    private static string QrDataUri(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private sealed record InviteFacts(ContactLineInvite Invite, string CompanyName, string ContactName);

    /// <summary>คำเชิญของบริษัทนี้จาก token (tracked) — ไม่พบ/รูปแบบผิด ⇒ null</summary>
    private async Task<InviteFacts?> LoadInviteAsync(Guid companyId, string? token, CancellationToken ct)
    {
        if (!ContactLineBindRule.LooksLikeToken(token)) return null;
        var hash = ContactLineBindRule.TokenHash(token);
        var invite = await _db.ContactLineInvites.FirstOrDefaultAsync(i => i.TokenHash == hash && i.CompanyId == companyId, ct);
        if (invite == null) return null;
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var contact = await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == invite.ContactId && c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.Name).FirstOrDefaultAsync(ct);
        if (contact == null) return null;   // ผู้ติดต่อถูกลบ ⇒ ลิงก์ใช้ไม่ได้
        return new InviteFacts(invite, company, contact);
    }

    public async Task<ContactLineJoinView> GetJoinViewAsync(Guid companyId, string? token, string? ip, CancellationToken ct = default)
    {
        // ฝ่ายค้าน L3: หน้าสาธารณะ ⇒ จำกัดความถี่ต่อ IP (DB) ก่อนค้น token
        if (!await _limiter.TryConsumeAsync("contact-line-view:" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip), ViewPerMinute, ViewPerDay, ct, failClosed: true))
            return new ContactLineJoinView(null, null, null, null, "เปิดลิงก์บ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่");
        var f = await LoadInviteAsync(companyId, token, ct);
        var problem = ContactLineBindRule.InviteProblem(f != null, f?.Invite.ExpiresAt ?? DateTime.MinValue, f?.Invite.UsedAt, f?.Invite.RevokedAt, DateTime.UtcNow);
        if (problem != null || f == null) return new ContactLineJoinView(null, null, null, null, problem);
        var text = ContactLineBindRule.ConsentText(f.CompanyName, f.ContactName);
        return new ContactLineJoinView(f.CompanyName, f.ContactName, text, CmsDataConsent.Version(text), null);
    }

    public async Task<ContactLineJoinConsentResponse> ConsentAsync(Guid companyId, string? token, string? ip, string? userAgent, CancellationToken ct = default)
    {
        var key = "contact-line-join:" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip);
        if (!await _limiter.TryConsumeAsync(key, ConsentPerMinute, ConsentPerDay, ct, failClosed: true))
            throw new BusinessRuleException("ลองบ่อยเกินไป — กรุณารอสักครู่แล้วลองใหม่", ContactLineBindRule.RuleCode, 429);
        var f = await LoadInviteAsync(companyId, token, ct);
        var now = DateTime.UtcNow;
        var problem = ContactLineBindRule.InviteProblem(f != null, f?.Invite.ExpiresAt ?? DateTime.MinValue, f?.Invite.UsedAt, f?.Invite.RevokedAt, now);
        if (problem != null || f == null) throw new BusinessRuleException(problem ?? "ลิงก์ใช้ไม่ได้", ContactLineBindRule.RuleCode, 404);
        var invite = f.Invite;

        // บริษัทเปลี่ยน/ปิด OA หลังออกลิงก์ ⇒ userId ที่จะได้ใช้กับ OA ที่ส่งจริงไม่ได้
        var line = await _recipients.LineChannelAsync(companyId, ct);
        if (!line.Open || !line.CompanyOaReady || ContactRecipientRule.OaChanged(invite.OaDestination, line.CompanyOaDestination))
            throw new BusinessRuleException(ContactLineBindRule.Replies.WrongChannel, ContactLineBindRule.RuleCode, 409);
        var basicId = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => s.LineOaBasicId).FirstOrDefaultAsync(ct);

        if (invite.ConsentRecordId == null)
        {
            // PDPA ม.19: ผู้รับกดยินยอมเองบนหน้าที่แสดงข้อความนี้ — หลักฐาน hash ผ่านตัวเดียวของระบบ (PdpaConsentEvidence) · เวลาที่เก็บ = เวลาที่ hash
            var text = ContactLineBindRule.ConsentText(f.CompanyName, f.ContactName);
            var version = CmsDataConsent.Version(text);
            var granted = PdpaConsentEvidence.NormalizeTimestamp(now);
            var channel = $"{ContactLineBindRule.ConsentChannel}:{invite.Id:N}";
            var subject = $"contact:{invite.ContactId:N}";
            var ua = string.IsNullOrEmpty(userAgent) ? null : (userAgent.Length > 500 ? userAgent[..500] : userAgent);
            var consent = new PdpaConsentRecord
            {
                CompanyId = companyId, SubjectContactId = invite.ContactId, SubjectContact = subject, Purpose = ContactLineBindRule.ConsentPurpose,
                PolicyVersion = version, GrantedAt = granted, Channel = channel, IpAddress = ip, UserAgent = ua,
                EvidenceHash = PdpaConsentEvidence.ComputeHash(subject, ContactLineBindRule.ConsentPurpose, version, granted, channel, ip, ua),
                EvidenceSource = PdpaConsentEvidence.SourceSystem, CreatedBy = "contact-line-join",
            };
            _db.PdpaConsentRecords.Add(consent);
            invite.ConsentRecordId = consent.Id;
            invite.ConsentedAt = granted;
        }
        // รหัสใหม่ทุกครั้งที่ขอ (รหัสเดิมใช้ไม่ได้ทันที — hash ถูกแทน) · อายุไม่เกินอายุลิงก์
        var code = CustomerLineLinkRule.NewCode();
        invite.CodeHash = CustomerLineLinkRule.Hash(code);
        var codeExpires = now + CustomerLineLinkRule.CodeLifetime;
        invite.CodeExpiresAt = codeExpires < invite.ExpiresAt ? codeExpires : invite.ExpiresAt;
        invite.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return new ContactLineJoinConsentResponse(CustomerLineLinkRule.MessageText(code), CustomerLineLinkRule.AddFriendUrl(basicId),
            CustomerLineLinkRule.SendCodeUrl(basicId, code), invite.CodeExpiresAt.Value);
    }

    public async Task<string?> TryRedeemAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId) || !CustomerLineLinkRule.IsLinkAttempt(text)) return null;
        var code = CustomerLineLinkRule.TryParse(text);
        if (code == null) return null;   // รูปแบบผิด ⇒ เส้นลูกค้าเว็บตอบ (จำกัดความถี่ที่นั่น)
        // ฝ่ายค้าน L1: จำกัดความถี่ต่อ LINE user ก่อนค้นรหัส (กันเดารหัสผ่านเส้นนี้ก่อนถึงตัวจำกัดของเส้นลูกค้าเว็บ)
        if (!await _limiter.TryConsumeAsync("contactline:" + lineUserId.Trim(), RedeemPerMinute, RedeemPerDay, ct, failClosed: true))
            return CustomerLineLinkRule.Replies.Throttled;
        var hash = CustomerLineLinkRule.Hash(code);
        var invite = await _db.ContactLineInvites.AsNoTracking()
            .FirstOrDefaultAsync(i => i.CompanyId == companyId && i.CodeHash == hash, ct);
        if (invite == null) return null;   // ไม่ใช่รหัสของคำเชิญผู้ติดต่อ ⇒ ส่งต่อเส้นลูกค้าเว็บ
        var now = DateTime.UtcNow;
        if (ContactLineBindRule.InviteProblem(true, invite.ExpiresAt, invite.UsedAt, invite.RevokedAt, now) != null
            || invite.CodeExpiresAt == null || invite.CodeExpiresAt <= now || invite.ConsentRecordId == null)
            return ContactLineBindRule.Replies.Invalid;
        // userId ของ LINE เป็นค่าต่อ OA ⇒ บอทที่รับรหัสต้องเป็นบอทเดียวกับตอนออกลิงก์ (บริษัทเปลี่ยน OA ระหว่างทาง ⇒ ไม่ผูก)
        if (ContactRecipientRule.OaChanged(invite.OaDestination, destination))
            return ContactLineBindRule.Replies.WrongChannel;
        var consentLive = await _db.PdpaConsentRecords.AsNoTracking()
            .AnyAsync(r => r.Id == invite.ConsentRecordId && r.CompanyId == companyId && r.WithdrawnAt == null, ct);
        if (!consentLive) return ContactLineBindRule.Replies.Invalid;

        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var uid = lineUserId.Trim();
        // ฝ่ายค้าน M4: ชื่อแสดงจากโปรไฟล์ LINE ให้พนักงานเห็นว่าใครผูก (ป้ายของปลายทาง · audit เก็บแบบปิดบัง) — อ่านไม่ได้ ⇒ ไม่มีชื่อ
        var profileName = _line == null ? null : await _line.TryGetCompanyOaProfileNameAsync(companyId, uid);
        var label = ContactLineBindRule.ProfileLabel(profileName);
        var dest = string.IsNullOrWhiteSpace(destination) ? null : destination.Trim();
        var idHash = NotifyEndpointKind.LineIdHash(dest, uid);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // ล็อกต่อผู้ติดต่อ — ตัวเดียวกับผู้เขียนปลายทางอื่น (ContactNotifyEndpointService · การรวมผู้ติดต่อ)
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.ContactNotifyEndpoint, invite.ContactId.ToString()) }, ct);
        // ใช้ครั้งเดียว — อัปเดตแบบมีเงื่อนไข (สองเครื่องไถ่พร้อมกัน ได้แถวเดียว)
        var claimed = await _db.ContactLineInvites
            .Where(i => i.Id == invite.Id && i.CompanyId == companyId && i.UsedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedAt, (DateTime?)now).SetProperty(i => i.UpdatedAt, (DateTime?)now), ct);
        if (claimed != 1) return ContactLineBindRule.Replies.Invalid;

        // LINE เดียวกันผูกกับผู้ติดต่อนี้อยู่แล้ว ⇒ แถวเดิมยกเลิก (ผูกใหม่ = แถวใหม่ · ความยินยอมใหม่)
        var previous = await _db.ContactNotifyEndpoints
            .Where(e => e.CompanyId == companyId && e.ContactId == invite.ContactId && e.LineIdHash == idHash && e.UnlinkedAt == null)
            .ToListAsync(ct);
        foreach (var p in previous)
        {
            p.UnlinkedAt = now;
            p.UnlinkReason = "relinked";
            p.LineUserId = null;
            p.UpdatedAt = now;
        }
        var endpoint = new ContactNotifyEndpoint
        {
            CompanyId = companyId, ContactId = invite.ContactId, Kind = NotifyEndpointKind.LineUser,
            LineUserId = _secrets.Protect(uid), LineIdHash = idHash,
            OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = dest,
            TopicsJson = NotifyTopics.Parse(invite.TopicsJson) != null ? invite.TopicsJson : NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser)),
            Label = label, ShowAmounts = true, Verified = true, Source = ContactLineBindRule.SourceLineInvite,
            ConsentRecordId = invite.ConsentRecordId, BoundByUserId = invite.CreatedByUserId, LinkedAt = now, CreatedBy = "line-webhook",
        };
        _db.ContactNotifyEndpoints.Add(endpoint);
        await _db.ContactLineInvites.Where(i => i.Id == invite.Id && i.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.EndpointId, (Guid?)endpoint.Id), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = "line-webhook", Action = AuditAction.Create,
            EntityType = ContactRecipientRule.AuditEntityType, EntityId = invite.ContactId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                op = "line-bind", endpointId = endpoint.Id, inviteId = invite.Id, lineUser = CustomerLineLinkRule.Mask(uid),
                lineName = ContactLineBindRule.MaskName(label),
                relinked = previous.Count, consent = invite.ConsentRecordId, rule = ContactLineBindRule.RuleCode,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("ผูก LINE ผู้ติดต่อ {Contact} ของบริษัท {Company} (LINE {Masked})", invite.ContactId, companyId, CustomerLineLinkRule.Mask(uid));
        return ContactLineBindRule.Replies.Linked(companyName);
    }

    public async Task<string?> TryStopAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId) || !ContactLineBindRule.IsStop(text)) return null;
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), lineUserId.Trim());
        var rows = await _db.ContactNotifyEndpoints
            .Where(e => e.CompanyId == companyId && e.Kind == NotifyEndpointKind.LineUser && e.LineIdHash == idHash && e.UnlinkedAt == null)
            .ToListAsync(ct);
        if (rows.Count == 0) return null;   // ไม่ได้ผูกกับผู้ติดต่อใด ⇒ ไม่ตอบ (แชตของร้าน)
        var now = DateTime.UtcNow;
        var consentIds = rows.Where(r => r.ConsentRecordId != null).Select(r => r.ConsentRecordId!.Value).Distinct().ToList();
        foreach (var r in rows)
        {
            r.UnlinkedAt = now;
            r.UnlinkReason = ContactLineBindRule.UnlinkUser;
            r.LineUserId = null;   // PDPA ม.22: ยกเลิกแล้วไม่ใช้ id อีก (คงแฮชเป็นร่องรอย)
            r.UpdatedAt = now;
        }
        if (consentIds.Count > 0)
            await _db.PdpaConsentRecords.Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.WithdrawnAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, "line-stop"), ct);
        foreach (var contactId in rows.Select(r => r.ContactId).Distinct())
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserEmail = "line-webhook", Action = AuditAction.Delete,
                EntityType = ContactRecipientRule.AuditEntityType, EntityId = contactId.ToString(),
                NewValues = JsonSerializer.Serialize(new { op = "line-stop", reason = ContactLineBindRule.UnlinkUser, lineUser = CustomerLineLinkRule.Mask(lineUserId) }),
                Timestamp = now,
            });
        await _db.SaveChangesAsync(ct);
        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        return ContactLineBindRule.Replies.Stopped(companyName);
    }

    public async Task<int> SetBlockedAsync(Guid companyId, string? destination, string lineUserId, bool blocked, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId)) return 0;
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), lineUserId.Trim());
        var q = _db.ContactNotifyEndpoints.Where(e => e.CompanyId == companyId && e.Kind == NotifyEndpointKind.LineUser
            && e.LineIdHash == idHash && e.UnlinkedAt == null);
        var now = DateTime.UtcNow;
        return blocked
            ? await q.Where(e => e.BlockedAt == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.BlockedAt, (DateTime?)now).SetProperty(e => e.UpdatedAt, (DateTime?)now), ct)
            : await q.Where(e => e.BlockedAt != null).ExecuteUpdateAsync(s => s.SetProperty(e => e.BlockedAt, (DateTime?)null).SetProperty(e => e.UpdatedAt, (DateTime?)now), ct);
    }

    public async Task<int> RevokeInvitesAsync(Guid companyId, Guid contactId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default)
    {
        var exists = await _db.Contacts.AsNoTracking().AnyAsync(c => c.Id == contactId && c.CompanyId == companyId, ct);
        if (!exists) throw new BusinessRuleException("ไม่พบผู้ติดต่อ", "NOTIFY-CONTACT-NOT-FOUND", 404);
        var now = DateTime.UtcNow;
        var n = await _db.ContactLineInvites
            .Where(i => i.CompanyId == companyId && i.ContactId == contactId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, (DateTime?)now).SetProperty(i => i.UpdatedAt, (DateTime?)now), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId, UserEmail = actorEmail, Action = AuditAction.Update,
            EntityType = ContactRecipientRule.AuditEntityType, EntityId = contactId.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "line-invite-revoke", revoked = n, rule = ContactLineBindRule.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>ฝ่ายค้าน L7 (PDPA ม.39 · DESIGN §6.3): บริษัทเริ่มส่งข้อมูลผู้ติดต่อให้ LINE ⇒ มีแถว RoPA ของกิจกรรมนี้ (ครั้งเดียวต่อบริษัท · ไม่ทับที่บริษัทแก้ไว้)</summary>
    private async Task EnsureRopaAsync(Guid companyId, string? actorEmail, CancellationToken ct)
    {
        var has = await _db.PdpaProcessingActivities.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.Purpose == ContactLineBindRule.RopaPurpose, ct);
        if (has) return;
        _db.PdpaProcessingActivities.Add(new PdpaProcessingActivity
        {
            CompanyId = companyId, Purpose = ContactLineBindRule.RopaPurpose, LegalBasis = "Consent",
            DataCategories = ContactLineBindRule.RopaDataCategories, RetentionPeriod = ContactLineBindRule.RopaRetention,
            Recipients = ContactLineBindRule.RopaRecipients, TransfersOutsideThailand = true,
            TransferSafeguards = ContactLineBindRule.RopaTransferNote, Notes = ContactLineBindRule.RopaNote,
            CreatedBy = actorEmail ?? "system", LastReviewedBy = "system",
        });
    }
}
