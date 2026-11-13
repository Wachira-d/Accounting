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
    /// <summary>รอบ 262 (คำตัดสิน 262-1-1): ค่าตั้ง OA กลาง (Basic ID บนหน้ายินยอม — ผู้อ่านตัวเดียว) · null (นอก DI/เทสต์) ⇒ ไม่มีปุ่มเพิ่มเพื่อน OA กลาง</summary>
    private readonly INextAccLineConfig? _nextAcc;

    /// <summary>กดยินยอม/ขอรหัสต่อ IP (DB-backed · หลายเครื่อง)</summary>
    private const int ConsentPerMinute = 5;
    private const int ConsentPerDay = 40;

    public ContactLineBindingService(AccountingDbContext db, ISecretProtector secrets, IContactRecipientResolver recipients,
        IChatRateLimiter limiter, IConfiguration config, ILogger<ContactLineBindingService> logger, ILineNotifyService? line = null,
        INextAccLineConfig? nextAcc = null)
    {
        _db = db; _secrets = secrets; _recipients = recipients; _limiter = limiter; _config = config; _logger = logger; _line = line; _nextAcc = nextAcc;
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
        // OA ของบริษัทเท่านั้น (คำตัดสิน N8) — ตัวตัดสินความพร้อมเดียวกับสถานะปลายทาง/ด่านบันทึกกฎ ·
        // รอบ 262 (คำตัดสิน 262-1-1): บริษัทผู้ให้บริการ ⇒ OA กลางคือ OA ของบริษัท (คำเชิญ scope nextacc · ไถ่ได้เฉพาะ webhook กลาง)
        var line = await _recipients.LineChannelAsync(companyId, ct);
        var viaCentral = line.Open && line.NextAccUserOpen;
        if (viaCentral)
        {
            if (await _recipients.CentralUserProblemAsync(companyId, ct) is string central)
                throw new BusinessRuleException(central + " — บริษัทผู้ให้บริการผูก LINE ของผู้ติดต่อผ่าน LINE OA กลางของ NextAcc", ProviderCentralOa.RuleCode, 409);
        }
        else if (!line.Open || !line.CompanyOaReady)
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
            ExpiresAt = now + ContactLineBindRule.InviteLifetime, OaDestination = viaCentral ? line.CentralOaDestination : line.CompanyOaDestination,
            OaScope = ProviderCentralOa.InviteScope(viaCentral),
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser)),
            CreatedByUserId = actorUserId, CreatedBy = actorEmail,
        };
        _db.ContactLineInvites.Add(invite);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = actorUserId, UserEmail = actorEmail, Action = AuditAction.Create,
            EntityType = ContactRecipientRule.AuditEntityType, EntityId = contact.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "line-invite", inviteId = invite.Id, expiresAt = invite.ExpiresAt, revokedOpen = revoked, oaScope = invite.OaScope,
                rule = ContactLineBindRule.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);

        var path = ContactLineBindRule.JoinPath(companyId, token);
        var url = NotificationLink.Absolute(await SystemBaseUrl.ResolveAsync(_db, _config, ct), path);
        return new ContactLineInviteResponse(path, url, url == null ? null : QrDataUri(url), invite.ExpiresAt,
            $"ส่งลิงก์นี้ให้ผู้รับของ \"{contact.Name}\" (หรือให้สแกน QR ต่อหน้า) — ผู้รับกดยินยอมบนมือถือของตัวเอง แล้วส่งรหัสในแชต "
            + (viaCentral ? "LINE OA กลางของ NextAcc (OA ของบริษัทนี้)" : "LINE OA ของบริษัท") + " · "
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
        string? basicId;
        if (invite.OaScope == NotifyEndpointKind.ScopeNextAcc)
        {
            // รอบ 262 (คำตัดสิน 262-1-2): คำเชิญผ่าน OA กลาง ⇒ ยังเป็นบริษัทผู้ให้บริการ + OA กลางพร้อม + บอทกลางไม่เปลี่ยน · ปุ่มเพิ่มเพื่อน/ส่งรหัส = OA กลาง
            if (!line.Open || !line.NextAccUserOpen || await _recipients.CentralUserProblemAsync(companyId, ct) != null
                || ContactRecipientRule.OaChanged(invite.OaDestination, line.CentralOaDestination))
                throw new BusinessRuleException(ContactLineBindRule.Replies.WrongChannel, ContactLineBindRule.RuleCode, 409);
            basicId = _nextAcc == null ? null : (await _nextAcc.GetAsync(ct)).BasicId;
        }
        else
        {
            if (!line.Open || !line.CompanyOaReady || ContactRecipientRule.OaChanged(invite.OaDestination, line.CompanyOaDestination))
                throw new BusinessRuleException(ContactLineBindRule.Replies.WrongChannel, ContactLineBindRule.RuleCode, 409);
            basicId = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId).Select(s => s.LineOaBasicId).FirstOrDefaultAsync(ct);
        }

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

    public async Task<string?> TryRedeemAsync(Guid companyId, string? destination, string lineUserId, string text, CancellationToken ct = default,
        string oaScope = NotifyEndpointKind.ScopeCompany)
    {
        if (string.IsNullOrWhiteSpace(lineUserId) || !CustomerLineLinkRule.IsLinkAttempt(text)) return null;
        // รอบ 262 (คำตัดสิน 262-1-2): webhook ของ OA ใด ⇒ ขอบเขตนั้น (OA ของบริษัท = company · webhook กลาง = nextacc เฉพาะบริษัทผู้ให้บริการ)
        if (oaScope != NotifyEndpointKind.ScopeCompany && oaScope != NotifyEndpointKind.ScopeNextAcc) return null;
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
        // คำเชิญของ OA หนึ่งไถ่ผ่าน OA อื่นไม่ได้ (userId ใช้ได้เฉพาะ OA ที่ออก) — คำเชิญ company ผ่าน webhook กลาง/คำเชิญ nextacc ผ่าน webhook บริษัท ⇒ ไม่ผูก
        if (!ProviderCentralOa.InviteMatchesChannel(invite.OaScope, oaScope))
            return ContactLineBindRule.Replies.WrongChannel;
        var consentLive = await _db.PdpaConsentRecords.AsNoTracking()
            .AnyAsync(r => r.Id == invite.ConsentRecordId && r.CompanyId == companyId && r.WithdrawnAt == null, ct);
        if (!consentLive) return ContactLineBindRule.Replies.Invalid;

        var companyName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var uid = lineUserId.Trim();
        // ฝ่ายค้าน M4: ชื่อแสดงจากโปรไฟล์ LINE ให้พนักงานเห็นว่าใครผูก (ป้ายของปลายทาง · audit เก็บแบบปิดบัง) — อ่านไม่ได้ ⇒ ไม่มีชื่อ
        var profileName = _line == null ? null
            : oaScope == NotifyEndpointKind.ScopeNextAcc ? await _line.TryGetNextAccProfileNameAsync(uid) : await _line.TryGetCompanyOaProfileNameAsync(companyId, uid);
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
        foreach (var p in previous) NotifyEndpointKind.MarkUnlinked(p, "relinked", now);   // ฝ่ายค้าน fix2 M3: ตัวยกเลิกเดียว (ล้างป้ายจาก LINE ด้วย)
        var endpoint = new ContactNotifyEndpoint
        {
            CompanyId = companyId, ContactId = invite.ContactId, Kind = NotifyEndpointKind.LineUser,
            LineUserId = _secrets.Protect(uid), LineIdHash = idHash,
            OaScope = oaScope, OaDestination = dest,
            // รอบ 259 (A-M4): คำเชิญที่ออกก่อนรอบนี้อาจเก็บ "ลิงก์เซ็น" ไว้ ⇒ ตัดเรื่องที่ LINE ไม่มีเส้นส่ง (ตัวเดียว NotifyTopics.SupportedFor)
            TopicsJson = NotifyTopics.Parse(invite.TopicsJson) is { } invited
                ? NotifyTopics.Serialize(NotifyTopics.SupportedFor(NotifyEndpointKind.LineUser, invited))
                : NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser)),
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
                lineName = ContactLineBindRule.MaskName(label), oaScope,
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
        foreach (var r in rows) NotifyEndpointKind.MarkUnlinked(r, ContactLineBindRule.UnlinkUser, now);   // PDPA ม.22 · ฝ่ายค้าน fix2 M3: ตัวยกเลิกเดียว
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

    // ═══════════ รอบ 262 (คำตัดสิน 262-1-1/262-1-2/262-1-3): บริษัทผู้ให้บริการ — OA กลางคือ OA ของบริษัทสำหรับผู้ติดต่อ ═══════════

    /// <summary>บริษัทผู้ให้บริการที่ตั้งไว้ (อ่านใหม่ทุกครั้ง) — null = ยังไม่ตั้ง ⇒ webhook กลางไม่มีคำเชิญผู้ติดต่อให้ไถ่</summary>
    private async Task<Guid?> ProviderIdAsync(CancellationToken ct)
    {
        var id = await PlatformCompanyLookup.IdAsync(_db, ct);
        return id is Guid p && ProviderCentralOa.Allowed(p, id) ? p : null;
    }

    public async Task<string?> TryRedeemCentralAsync(string? destination, string lineUserId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId) || !CustomerLineLinkRule.IsLinkAttempt(text)) return null;   // ข้อความอื่นไม่แตะฐาน
        // ค้นรหัสเฉพาะคำเชิญของบริษัทผู้ให้บริการ (scope nextacc) — คำเชิญของบริษัทอื่นไม่มีทางถูกไถ่ผ่าน OA กลาง (ห้ามค้นข้ามบริษัท)
        if (await ProviderIdAsync(ct) is not Guid providerId) return null;
        // ฝ่ายค้าน L6 (262-1-16): รหัสที่ไม่ใช่คำเชิญผู้ติดต่อของบริษัทผู้ให้บริการ ⇒ ส่งต่อเส้นลิงก์ลูกค้าเว็บบน OA กลางโดย<b>ไม่กินตัวจำกัดของเส้นผู้ติดต่อ</b>
        // (เส้นลูกค้าเว็บจำกัดความถี่ของตัวเอง — ลูกค้าหน้าร้านของผู้เช่าไม่ได้ Throttled จากเส้นนี้)
        var code = CustomerLineLinkRule.TryParse(text);
        if (code == null) return null;
        var hash = CustomerLineLinkRule.Hash(code);
        if (!await _db.ContactLineInvites.AsNoTracking().AnyAsync(i => i.CompanyId == providerId && i.CodeHash == hash, ct)) return null;
        return await TryRedeemAsync(providerId, destination, lineUserId, text, ct, NotifyEndpointKind.ScopeNextAcc);
    }

    public async Task<string?> TryStopCentralAsync(string? destination, string lineUserId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lineUserId) || !ContactLineBindRule.IsStop(text)) return null;
        // ปลดเฉพาะปลายทางของบริษัทผู้ให้บริการที่ผูกกับบอทกลางนี้ (LineIdHash รวมบอท) — ไม่มี ⇒ null (ข้อความไปบอท ERP ตามเดิม)
        if (await ProviderIdAsync(ct) is not Guid providerId) return null;
        // ฝ่ายค้าน L3 (คำตัดสิน 262-1-15): "หยุด" = หยุดรับทาง LINE จากผู้ส่งรายนี้ทั้งหมด (PDPA ม.19 ถอนได้ง่ายเท่าให้) — คำตอบบอกว่าหยุดการรับของผู้ติดต่อใดบ้าง
        var idHash = NotifyEndpointKind.LineIdHash(string.IsNullOrWhiteSpace(destination) ? null : destination.Trim(), lineUserId.Trim());
        var names = await (from e in _db.ContactNotifyEndpoints.AsNoTracking()
                           join c in _db.Contacts.AsNoTracking().IgnoreQueryFilters() on e.ContactId equals c.Id
                           where e.CompanyId == providerId && c.CompanyId == providerId && e.Kind == NotifyEndpointKind.LineUser
                                 && e.LineIdHash == idHash && e.UnlinkedAt == null
                           select c.Name).Distinct().ToListAsync(ct);
        var reply = await TryStopAsync(providerId, destination, lineUserId, text, ct);
        return reply == null ? null : reply + ProviderCentralOa.StopScopeNote(names);
    }

    public async Task<int> SetBlockedCentralAsync(string? destination, string lineUserId, bool blocked, CancellationToken ct = default)
        => await ProviderIdAsync(ct) is Guid providerId ? await SetBlockedAsync(providerId, destination, lineUserId, blocked, ct) : 0;

    /// <summary>ผู้ติดต่อของบริษัทผู้เช่านี้ในบริษัทผู้ให้บริการที่คำเชิญผูกเองลง (ผู้ติดต่อของใบล่าสุด — <see cref="PlatformInvoiceQueries.BuyerContactIdAsync"/>)</summary>
    private async Task<(Guid? ProviderId, Guid? ContactId)> BuyerContactAsync(Guid buyerCompanyId, CancellationToken ct)
    {
        var providerId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (providerId is not Guid pid) return (null, null);
        return (pid, await PlatformInvoiceQueries.BuyerContactIdAsync(_db, pid, buyerCompanyId, ct));
    }

    public async Task<ProviderCentralOa.BillingLineView> BillingLineViewAsync(Guid buyerCompanyId, bool canManage, CancellationToken ct = default)
    {
        var (providerId, contactId) = await BuyerContactAsync(buyerCompanyId, ct);
        string? central = null;
        var endpoints = new List<ProviderCentralOa.BillingLineEndpoint>();
        if (providerId is Guid pid && contactId is Guid)
        {
            // ฝ่ายค้าน M1/M2: ปลายทาง LINE ผู้ใช้ผ่าน OA กลางของ<b>ทุกแถว</b>ผู้ติดต่อของผู้เช่า — ป้ายปิดบัง · ไม่มี LINE id · ยกเลิกได้จากที่นี่
            var contactIds = await PlatformInvoiceQueries.BuyerContactIdsAsync(_db, pid, buyerCompanyId, ct);
            var rows = await _db.ContactNotifyEndpoints.AsNoTracking()
                .Where(e => e.CompanyId == pid && contactIds.Contains(e.ContactId) && e.OaScope == NotifyEndpointKind.ScopeNextAcc
                    && e.Kind == NotifyEndpointKind.LineUser && e.UnlinkedAt == null && !e.IsDeleted)
                .OrderBy(e => e.LinkedAt).Select(e => new { e.Id, e.Label, e.LinkedAt, e.LastSentAt, e.BlockedAt }).ToListAsync(ct);
            endpoints = rows.Select(r => new ProviderCentralOa.BillingLineEndpoint(r.Id, ProviderCentralOa.MaskedEndpointLabel(r.Label), r.LinkedAt, r.LastSentAt,
                r.BlockedAt != null ? "ผู้รับบล็อก LINE OA กลาง" : "ใช้งาน")).ToList();
            central = await _recipients.CentralUserProblemAsync(pid, ct);
        }
        return ProviderCentralOa.BillingView(providerId != null, contactId != null, central, endpoints.Count, canManage) with { Endpoints = endpoints };
    }

    public async Task<ProviderCentralOa.BillingLineView> UnlinkBillingEndpointAsync(Guid buyerCompanyId, Guid endpointId, Guid? actorUserId, string? actorEmail,
        CancellationToken ct = default)
    {
        var providerId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct)
            ?? throw new BusinessRuleException("ไม่พบปลายทาง LINE นี้", ProviderCentralOa.RuleCode, 404);
        // ปลายทางต้องเป็นของผู้ติดต่อ (แถวใดก็ได้) ของผู้เช่ารายนี้ในบริษัทผู้ให้บริการ — ของบริษัทอื่น/ของลูกค้าทั่วไป = ไม่พบ (ไม่บอกว่ามีอยู่)
        var contactIds = await PlatformInvoiceQueries.BuyerContactIdsAsync(_db, providerId, buyerCompanyId, ct);
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var ep = await _db.ContactNotifyEndpoints.FirstOrDefaultAsync(e => e.Id == endpointId && e.CompanyId == providerId && contactIds.Contains(e.ContactId)
            && e.OaScope == NotifyEndpointKind.ScopeNextAcc && e.Kind == NotifyEndpointKind.LineUser && e.UnlinkedAt == null && !e.IsDeleted, ct)
            ?? throw new BusinessRuleException("ไม่พบปลายทาง LINE นี้ (อาจถูกยกเลิกไปแล้ว)", ProviderCentralOa.RuleCode, 404);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(providerId, AdvisoryLockKey.ContactNotifyEndpoint, ep.ContactId.ToString()) }, ct);
        var consentId = ep.ConsentRecordId;
        NotifyEndpointKind.MarkUnlinked(ep, NotifyEndpointKind.UnlinkTenant, now);   // ตัวยกเลิกเดียว (ล้าง LINE id + ป้ายจาก LINE · PDPA ม.22)
        if (consentId is Guid cid)
            await _db.PdpaConsentRecords.Where(c => c.Id == cid && c.CompanyId == providerId && c.WithdrawnAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, NotifyEndpointKind.UnlinkTenant), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = providerId, UserId = actorUserId, UserEmail = actorEmail, Action = AuditAction.Delete,
            EntityType = ContactRecipientRule.AuditEntityType, EntityId = ep.ContactId.ToString(),
            NewValues = JsonSerializer.Serialize(new { op = "line-unlink-by-tenant", endpointId = ep.Id, buyerCompanyId, consent = consentId, rule = ProviderCentralOa.RuleCode }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await BillingLineViewAsync(buyerCompanyId, canManage: true, ct);
    }

    /// <summary>
    /// รอบ 268 (คำตัดสิน 268-1-5 · backlog 262): ผู้ใช้ถูกนำออกจากบริษัทผู้เช่า ⇒ ปลายทาง LINE รับใบค่าบริการ (OA กลาง · ผู้ติดต่อของผู้เช่าในบริษัทผู้ให้บริการ)
    /// <b>ที่ผู้ใช้คนนั้นสร้างคำเชิญผูก</b> (<c>BoundByUserId</c>) ถูกยกเลิก + ถอนความยินยอม + audit ในบริษัทผู้ให้บริการ — ตัวยกเลิกเดียว <see cref="NotifyEndpointKind.MarkUnlinked"/> ·
    /// ปลายทางที่คนอื่นผูกไม่ถูกแตะ (ผู้ติดต่อ = บริษัทผู้เช่าเป็นเจ้าของปลายทาง · ผู้รับยินยอมเอง) · ไม่มีบริษัทผู้ให้บริการ/ผู้ติดต่อ ⇒ 0 ·
    /// ผู้เรียก = <c>CompanyService.RemoveUserAsync</c> (ธุรกรรมเดียวกับการนำออก — ล้ม = ไม่นำออก)
    /// </summary>
    public static async Task<int> UnlinkBillingEndpointsBoundByAsync(AccountingDbContext db, Guid buyerCompanyId, Guid removedUserId, Guid? actorUserId,
        CancellationToken ct = default)
    {
        if (await PlatformInvoiceQueries.PlatformTenantIdAsync(db, ct) is not Guid providerId || providerId == buyerCompanyId) return 0;
        var contactIds = await PlatformInvoiceQueries.BuyerContactIdsAsync(db, providerId, buyerCompanyId, ct);
        if (contactIds.Count == 0) return 0;
        var rows = await db.ContactNotifyEndpoints
            .Where(e => e.CompanyId == providerId && contactIds.Contains(e.ContactId) && e.OaScope == NotifyEndpointKind.ScopeNextAcc
                && (e.Kind == NotifyEndpointKind.LineUser || e.Kind == NotifyEndpointKind.LineGroup) && e.BoundByUserId == removedUserId && e.UnlinkedAt == null && !e.IsDeleted)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var ep in rows)
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(providerId, AdvisoryLockKey.ContactNotifyEndpoint, ep.ContactId.ToString()) }, ct);
            var consentId = ep.ConsentRecordId;
            NotifyEndpointKind.MarkUnlinked(ep, NotifyEndpointKind.UnlinkMemberRemoved, now);
            if (consentId is Guid cid)
                await db.PdpaConsentRecords.Where(c => c.Id == cid && c.CompanyId == providerId && c.WithdrawnAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.WithdrawnAt, (DateTime?)now).SetProperty(c => c.WithdrawnReason, NotifyEndpointKind.UnlinkMemberRemoved), ct);
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = providerId, UserId = actorUserId, Action = AuditAction.Delete,
                EntityType = ContactRecipientRule.AuditEntityType, EntityId = ep.ContactId.ToString(),
                NewValues = JsonSerializer.Serialize(new { op = "line-unlink-member-removed", endpointId = ep.Id, buyerCompanyId, removedUserId, consent = consentId,
                    rule = ProviderCentralOa.RuleCode }),
                Timestamp = now,
            });
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<ContactLineInviteResponse> CreateBillingInviteAsync(Guid buyerCompanyId, Guid? actorUserId, string? actorEmail, CancellationToken ct = default)
    {
        // ฝ่ายค้าน L5 (262-1-17): จำกัดความถี่ต่อบริษัทผู้เช่า (DB · หลายเครื่อง) — แต่ละครั้งเขียน audit ในบริษัทผู้ให้บริการ + เพิกถอนลิงก์เดิมของผู้ติดต่อ
        if (!await _limiter.TryConsumeAsync(ProviderCentralOa.BillingInviteRateKey(buyerCompanyId), ProviderCentralOa.BillingInvitePerMinute,
                ProviderCentralOa.BillingInvitePerDay, ct, failClosed: true))
            throw new BusinessRuleException("สร้างลิงก์บ่อยเกินไป — ใช้ลิงก์ล่าสุดที่สร้างไว้ หรือรอสักครู่แล้วลองใหม่", ProviderCentralOa.RuleCode, 429);
        var (providerId, contactId) = await BuyerContactAsync(buyerCompanyId, ct);
        var view = ProviderCentralOa.BillingView(providerId != null, contactId != null,
            providerId is Guid p ? await _recipients.CentralUserProblemAsync(p, ct) : null, 0, canManage: true);
        if (!view.Available || providerId is not Guid pid || contactId is not Guid cid)
            throw new BusinessRuleException(view.Reason ?? "ยังผูก LINE รับใบแจ้งหนี้ค่าบริการไม่ได้", ProviderCentralOa.RuleCode, 409);
        // คำเชิญอยู่ในบริษัทผู้ให้บริการ (ผู้ควบคุมข้อมูลของการส่งใบแจ้งหนี้) · ผู้ขอ = ผู้ใช้ของบริษัทผู้เช่า (audit ระบุตัวตน) · ความยินยอมผู้รับกดเองบนมือถือ (PDPA ม.19)
        return await CreateInviteAsync(pid, cid, actorUserId, actorEmail, ct);
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
