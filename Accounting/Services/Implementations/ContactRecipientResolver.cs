using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ดู <see cref="IContactRecipientResolver"/> — อ่านข้อเท็จจริง (ผู้ติดต่อ · ปลายทาง · ความยินยอม · กลุ่มกลางของระบบ) แล้วส่งให้ตัวตัดสินเดียว
/// <see cref="ContactRecipientRule"/> · ทุก query มี <c>CompanyId == companyId</c> · ไม่มี cache ข้ามคำขอ</summary>
public class ContactRecipientResolver : IContactRecipientResolver
{
    /// <summary>รอบ 254 WP-11 L2: ระบบส่ง LINE ถึงผู้ติดต่อแล้ว (คิว <c>LineQueues</c> + การ์ด) — ความพร้อมต่อปลายทางตัดสินที่
    /// <see cref="ContactRecipientRule.LineGate"/> จากข้อเท็จจริงของ OA บริษัท (<see cref="LoadLineAsync"/>) · ตัวหาผู้รับแบบอีเมลอย่างเดียว (<see cref="EmailOnly"/>) = ปิด</summary>
    public const bool LineDeliveryOpen = true;

    /// <summary>คำตัดสิน N8: ส่งถึงผู้ติดต่อผ่าน OA กลางยังไม่เปิดใน L2 (รอโควตา LINE แยกช่อง L4 + สวิตช์แอดมิน) — ห้ามเปิดก่อนมีการจองโควตา</summary>
    public const bool NextAccTenantDeliveryOpen = false;

    private readonly AccountingDbContext _db;
    private readonly ISecretProtector? _secrets;
    private readonly INextAccLineConfig? _nextAccLine;

    public ContactRecipientResolver(AccountingDbContext db, ISecretProtector secrets, INextAccLineConfig nextAccLine)
    {
        _db = db; _secrets = secrets; _nextAccLine = nextAccLine;
    }

    private ContactRecipientResolver(AccountingDbContext db)
    {
        _db = db; _secrets = null; _nextAccLine = null;
    }

    /// <summary>รอบ 250 WP-08: ตัวหาผู้รับสำหรับบริการที่ถูกสร้างนอก DI (เทสต์/ผู้เรียกเก่าที่ไม่ส่ง resolver) — ตัวตัดสินเดียวกัน ·
    /// ถอด groupId ไม่ได้ ⇒ ปลายทางกลุ่ม "อ่านไม่ได้ = ใช้ไม่ได้" (ปิดทางปลอดภัย · L1 LINE ยังไม่เปิดส่งอยู่แล้ว) · อีเมลได้ผลเท่าตัวเต็ม</summary>
    internal static ContactRecipientResolver EmailOnly(AccountingDbContext db) => new(db);

    public async Task<ContactRecipientRule.RecipientSet> ResolveAsync(Guid companyId, Guid contactId, NotifyTopic topic, CancellationToken ct = default)
    {
        var facts = await LoadFactsAsync(companyId, contactId, ct);
        return ContactRecipientRule.Resolve(companyId, facts.Contact, facts.Endpoints, topic, facts.PlatformGroupIds, facts.Line);
    }

    public Task<ContactRecipientRule.LineOpenness> LineChannelAsync(Guid companyId, CancellationToken ct = default) => LoadLineAsync(companyId, ct);

    public async Task<ContactNotifyOverviewResponse?> GetOverviewAsync(Guid companyId, Guid contactId, bool canEdit, CancellationToken ct = default)
    {
        var facts = await LoadFactsAsync(companyId, contactId, ct);
        if (facts.Contact == null || facts.Contact.IsDeleted) return null;   // ผู้ติดต่อที่ลบ ⇒ ไม่มีแท็บ (404 เหมือนเดิม)
        var contact = facts.Contact;
        var rows = facts.Endpoints;
        var list = new List<ContactNotifyEndpointResponse>();

        // ── อีเมลหลัก: แถวค่าตั้ง (ถ้ามี · ตัวเลือกเดียวกับชุดผู้รับ) หรือแถวเสมือน (รับทุกเรื่อง — พฤติกรรมเดิม) ──
        var primaryRow = ContactRecipientRule.PickPrimary(rows.Where(e => e.IsPrimary && e.Kind == NotifyEndpointKind.Email && e.UnlinkedAt == null),
            e => e.LinkedAt, e => e.LinkedAt);
        var primaryFacts = primaryRow ?? new ContactRecipientRule.EndpointFacts(Guid.Empty, companyId, contact.Id, NotifyEndpointKind.Email, null, null, null,
            "", null, null, NotifyTopics.Serialize(NotifyTopics.All), true, true, false, NotifyEndpointKind.SourceContactEmail, null, null, null, null, null, false,
            DateTime.MinValue);
        var (validPrimary, invalidPrimary) = SignEmailThrottle.ParseRecipients(new[] { contact.Email });
        var primaryTopics = NotifyTopics.Parse(primaryFacts.TopicsJson);
        var (pUsable, pCode, pLabel, pReason) = validPrimary.Count > 0 && invalidPrimary.Count == 0
            ? (true, "ok", "ใช้งาน", (string?)null)
            : validPrimary.Count == 0 && invalidPrimary.Count == 0
                ? (false, "no-email", "ยังไม่มีอีเมล", "ผู้ติดต่อยังไม่มีอีเมลหลัก — กรอกที่แท็บข้อมูลทั่วไป")
                : (validPrimary.Count > 0, "invalid", "อีเมลไม่ถูกต้องบางส่วน", $"อีเมลหลักที่รูปแบบไม่ถูกต้องจะไม่ถูกใช้: {string.Join(", ", invalidPrimary)}");
        if (primaryTopics == null) (pCode, pReason) = ("topics-corrupt", "ค่าตั้งเรื่องที่รับเสียหาย — เลือกเรื่องแล้วบันทึกใหม่");
        list.Add(new ContactNotifyEndpointResponse(
            primaryRow?.Id, NotifyEndpointKind.Email, NotifyEndpointKind.Label(NotifyEndpointKind.Email), "อีเมลหลัก",
            string.IsNullOrWhiteSpace(contact.Email) ? null : contact.Email!.Trim(),
            validPrimary.Count > 0 ? string.Join(", ", validPrimary) : "(ยังไม่มีอีเมลหลัก)",
            true, Names(primaryTopics), Labels(primaryTopics), true, false,
            pUsable, pCode, pLabel, pReason, ContactRecipientRule.ConsentLabel(primaryFacts),
            NotifyEndpointKind.SourceContactEmail, NotifyEndpointKind.SourceLabel(NotifyEndpointKind.SourceContactEmail),
            null, null, null, null, null, null, null,
            CanEditTopics: canEdit, CanEditLabel: false, CanUnlink: false));

        // ── ปลายทางอื่น (ใช้งานก่อน · ยกเลิกแล้วท้าย) ──
        var meta = facts.Meta;
        foreach (var e in rows.Where(e => !(e.IsPrimary && e.Kind == NotifyEndpointKind.Email))
                     .OrderBy(e => e.UnlinkedAt != null).ThenBy(e => e.Kind).ThenBy(e => meta[e.Id].LinkedAt))
        {
            var st = ContactRecipientRule.Status(e, facts.PlatformGroupIds, facts.Line);
            var topics = NotifyTopics.Parse(e.TopicsJson);
            var reason = topics == null && st.Usable ? "ค่าตั้งเรื่องที่รับเสียหาย — เลือกเรื่องแล้วบันทึกใหม่" : st.Reason;
            var active = e.UnlinkedAt == null && canEdit;   // ฝ่ายค้าน N3: ปุ่มที่กดได้ตามสิทธิ์จริง (Contact.Edit)
            var m = meta[e.Id];
            list.Add(new ContactNotifyEndpointResponse(
                e.Id, e.Kind, NotifyEndpointKind.Label(e.Kind), e.Label, e.Kind == NotifyEndpointKind.Email ? e.Email : null,
                e.Kind == NotifyEndpointKind.Email ? (e.Email ?? "")
                    : !string.IsNullOrWhiteSpace(e.Label) ? e.Label! : !string.IsNullOrWhiteSpace(e.GroupName) ? e.GroupName! : NotifyEndpointKind.Label(e.Kind),
                false, Names(topics), Labels(topics), e.ShowAmounts, e.Kind == NotifyEndpointKind.LineGroup,
                st.Usable && topics != null, topics == null && st.Usable ? "topics-corrupt" : st.Code, st.Label, reason, ContactRecipientRule.ConsentLabel(e),
                e.Source, NotifyEndpointKind.SourceLabel(e.Source), OaLabel(e), e.GroupName, e.MemberCount,
                m.LinkedAt, e.UnlinkedAt, m.LastSentAt, m.LastFailure,
                CanEditTopics: active, CanEditLabel: active, CanUnlink: active));
        }

        // รอบ 251 (WP-07 · F-06): บริษัทโหมด e-Tax by Email ⇒ บอกกติกาผู้รับใบกำกับ 1 อีเมล (ตัวตัดสินโหมดเดียว EtaxDeliveryRouter.ByEmailActive)
        var etaxMode = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.EtaxEnabled, x.EtaxMode }).FirstOrDefaultAsync(ct);
        var etaxNote = etaxMode == null ? null : EtaxByEmailFormat.ContactRecipientsNote(etaxMode.EtaxEnabled, etaxMode.EtaxMode);

        return new ContactNotifyOverviewResponse(contact.Id, facts.ContactName, string.IsNullOrWhiteSpace(contact.Email) ? null : contact.Email!.Trim(), list,
            NotifyTopics.All.Select(t => new NotifyTopicOption(t.ToString(), NotifyTopics.Label(t),
                NotifyTopics.LineUnsupportedReason(t, NotifyEndpointKind.LineUser), NotifyTopics.LineUnsupportedReason(t, NotifyEndpointKind.LineGroup))).ToList(),
            _secrets != null && LineDeliveryOpen,
            new List<ContactNotifyComingSoon>
            {
                new(NotifyEndpointKind.LineUser, "เชิญผู้ใช้ให้ผูก LINE (สมัครผู้ใช้พอร์ทัลก่อน)",
                    "เร็ว ๆ นี้ (เฟส L5) — ทางลัด \"ลิงก์/QR ผูก LINE\" ใช้ได้แล้ว (ผู้รับกดยินยอมบนมือถือของตัวเอง · ผ่าน LINE OA ของบริษัท · คำตอบเจ้าของ Q6)"),
            },
            DeliveryNoteFor(LineGroupBindRule.CentralScopeAllowed(companyId, await PlatformCompanyLookup.IdAsync(_db, ct))),
            canEdit,
            canEdit ? null : $"ดูได้อย่างเดียว — แก้ช่องทางแจ้งเตือนต้องมีสิทธิ์ {Accounting.Models.Constants.PermissionKeys.ContactEdit.Replace("perm:", "")}",
            EtaxByEmailNote: etaxNote,
            LineBind: await LineBindOfferAsync(companyId, contact.Id, canEdit, ct),
            LineGroupBind: await LineGroupBindOfferAsync(companyId, canEdit, ct));
    }

    public async Task<string?> CentralGroupProblemAsync(Guid companyId, CancellationToken ct = default)
    {
        if (!LineGroupBindRule.CentralScopeAllowed(companyId, await PlatformCompanyLookup.IdAsync(_db, ct)))
            return ContactRecipientRule.CentralGroupNotPlatformReason;
        if (_nextAccLine == null) return LineGroupBindRule.Replies.CentralOaNotConfigured;
        var nx = await _nextAccLine.GetAsync(ct);
        return nx.HasToken ? null : nx.Problem ?? LineGroupBindRule.Replies.CentralOaNotConfigured;
    }

    /// <summary>รอบ 262 (คำตัดสิน 262-1-1): LINE ผู้ใช้ 1:1 ผ่าน OA กลางพร้อมไหม — null = พร้อม · ไม่ใช่บริษัทผู้ให้บริการ ⇒ เหตุ N8 ·
    /// พร้อม = token + secret ของ OA กลาง (webhook กลางต้องตรวจลายเซ็นได้จึงรับรหัสผูกได้) — ตัวเดียวของปุ่มลิงก์ผูก · สร้างคำเชิญ · ยินยอม · ศูนย์บิล</summary>
    public async Task<string?> CentralUserProblemAsync(Guid companyId, CancellationToken ct = default)
    {
        if (!ProviderCentralOa.Allowed(companyId, await PlatformCompanyLookup.IdAsync(_db, ct)))
            return ContactRecipientRule.NextAccNotOpenReason;
        if (_nextAccLine == null) return LineGroupBindRule.Replies.CentralOaNotConfigured;
        var nx = await _nextAccLine.GetAsync(ct);
        return ProviderCentralOa.CentralProblem(nx.HasToken, nx.HasSecret, nx.Problem);
    }

    /// <summary>รอบ 256 (คำตัดสิน 256-2): กล่อง "เพิ่มกลุ่ม LINE" — server computes · page displays ·
    /// บริษัทผู้ให้บริการ ⇒ ผ่าน LINE OA กลาง (ข้อความบอก Basic ID ของ OA กลางจากค่าตั้ง) · บริษัทอื่น ⇒ OA ของบริษัทเท่านั้น (N8 คงเดิม)</summary>
    private async Task<ContactLineGroupBindOffer> LineGroupBindOfferAsync(Guid companyId, bool canEdit, CancellationToken ct)
    {
        var line = await LoadLineAsync(companyId, ct);
        if (line.NextAccGroupOpen && _nextAccLine != null)
        {
            var nx = await _nextAccLine.GetAsync(ct);
            var oa = string.IsNullOrWhiteSpace(nx.BasicId) ? "LINE OA กลางของ NextAcc" : $"LINE OA กลางของ NextAcc ({nx.BasicId!.Trim()})";
            var instruction = $"เพิ่มกลุ่ม LINE: เชิญ {oa} เข้ากลุ่ม → พิมพ์ #HeyNextAcc ในกลุ่ม → ใส่รหัสที่บอทตอบ (ไม่ต้องหา Group ID เอง)";
            if (!canEdit) return new ContactLineGroupBindOffer(false, "ต้องมีสิทธิ์แก้ผู้ติดต่อ", instruction, true);
            var problem = await CentralGroupProblemAsync(companyId, ct);
            return problem == null
                ? new ContactLineGroupBindOffer(true, null, instruction, true)
                : new ContactLineGroupBindOffer(false, problem, instruction, true);
        }
        const string own = "เพิ่มกลุ่ม LINE: เชิญ LINE OA ของบริษัทเข้ากลุ่ม → พิมพ์ #HeyNextAcc ในกลุ่ม → ใส่รหัสที่บอทตอบ (ไม่ต้องหา Group ID เอง)";
        if (!canEdit) return new ContactLineGroupBindOffer(false, "ต้องมีสิทธิ์แก้ผู้ติดต่อ", own, false);
        return line.CompanyOaReady
            ? new ContactLineGroupBindOffer(true, null, own, false)
            : new ContactLineGroupBindOffer(false, line.CompanyOaProblem ?? "LINE OA ของบริษัทยังไม่พร้อม", own, false);
    }

    /// <summary>รอบ 254 WP-11 L2 (คำตอบเจ้าของ Q6 ทางลัด): ปุ่ม "ลิงก์/QR ผูก LINE" — กดได้เมื่อมีสิทธิ์แก้ + OA ของบริษัทพร้อม (ตัวตัดสินเดียว CompanyLineReadiness) ·
    /// ไม่พร้อม ⇒ ปุ่มปิดพร้อมเหตุ (server computes · page displays)</summary>
    private async Task<ContactLineBindOffer> LineBindOfferAsync(Guid companyId, Guid contactId, bool canEdit, CancellationToken ct)
    {
        var line = await LoadLineAsync(companyId, ct);
        if (!canEdit) return new ContactLineBindOffer(false, "ต้องมีสิทธิ์แก้ผู้ติดต่อ");
        // ฝ่ายค้าน M4: ลิงก์ที่ยังเปิด (ปุ่มยกเลิกบนหน้า)
        var now = DateTime.UtcNow;
        var open = await _db.ContactLineInvites.AsNoTracking()
            .CountAsync(i => i.CompanyId == companyId && i.ContactId == contactId && i.UsedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        // รอบ 262 (คำตัดสิน 262-1-1): บริษัทผู้ให้บริการ ⇒ ผูกผ่าน LINE OA กลาง (OA ของบริษัทนี้) — พร้อม = token + secret ของ OA กลาง (ตัวตัดสินเดียว CentralProblemAsync)
        if (line.NextAccUserOpen)
        {
            var central = await CentralUserProblemAsync(companyId, ct);
            return central == null
                ? new ContactLineBindOffer(true, null, open, ViaCentralOa: true)
                : new ContactLineBindOffer(false, central, open, ViaCentralOa: true);
        }
        if (!line.CompanyOaReady)
            return new ContactLineBindOffer(false, (line.CompanyOaProblem ?? "LINE OA ของบริษัทยังไม่พร้อม")
                + " — ผูก LINE ของผู้ติดต่อได้ผ่าน LINE OA ของบริษัทเท่านั้น (OA กลางของ NextAcc ใช้ได้เฉพาะบริษัทผู้ให้บริการ)", open);
        return new ContactLineBindOffer(true, null, open);
    }

    /// <summary>รอบ 250 WP-08: ประโยคบนแท็บ (เซิร์ฟเวอร์ส่ง · หน้าแสดงตรงตัว) — เส้นส่งที่อ่านค่าตั้งนี้แล้วจริง (ล็อกด้วย required_call_site_check R250L1)
    /// · รอบ 257 ชุด 4 (C-M5): ประโยค LINE ขึ้นกับ <c>NextAccGroupOpen</c> (บริษัทผู้ให้บริการ ⇒ กลุ่มผ่าน OA กลางเปิดแล้ว ตั้งแต่รอบ 256) — เดิมบอก "ยังไม่เปิด" กับทุกบริษัท</summary>
    internal static string DeliveryNoteFor(bool nextAccGroupOpen) => DeliveryNoteFor(nextAccGroupOpen, nextAccGroupOpen);

    /// <summary>รอบ 262: บริษัทผู้ให้บริการ ⇒ LINE ผู้ใช้ 1:1 ผ่าน OA กลางเปิดด้วย (ตัวเดียวกับกลุ่ม)</summary>
    internal static string DeliveryNoteFor(bool nextAccGroupOpen, bool nextAccUserOpen) =>
        "ปลายทางที่ตั้งที่นี่มีผลกับการส่งอีเมลถึงผู้ติดต่อรายนี้แล้ว (ตัวหาผู้รับตัวเดียว): ตารางส่งอีเมล (เมื่ออนุมัติ/รายการประจำ) · ทวงชำระ/ใบแจ้งยอด · "
        + "ส่ง 50 ทวิ · ใบเสร็จหลังรับชำระ · ลิงก์เซ็น — เรื่องที่ไม่ได้เลือกจะไม่ถูกส่งไปปลายทางนั้น และถ้าไม่มีปลายทางใดรับเรื่องนั้นเลย ระบบไม่ส่งพร้อมบันทึกเหตุบนคิว · "
        + "ช่องผู้รับที่ผู้ใช้กรอกเองตอนส่ง (เช่นลิงก์เซ็นครั้งก่อน) ยังชนะ · อีเมลหลักแก้ที่แท็บข้อมูลทั่วไป · "
        + "LINE (รอบ 254): ส่งการ์ดเอกสารถึงผู้ใช้ LINE ที่ผูกผ่าน LINE OA ของบริษัทเมื่อกฎในตารางส่งติ๊กช่อง LINE (เมื่ออนุมัติ · ใกล้/เกินกำหนด · รายการประจำ) · "
        + ContactLineChannel.CentralOaSentence(nextAccGroupOpen, nextAccUserOpen);

    private sealed record RowMeta(DateTime LinkedAt, DateTime? LastSentAt, string? LastFailure);

    private sealed record Facts(ContactRecipientRule.ContactFacts? Contact, string ContactName,
        List<ContactRecipientRule.EndpointFacts> Endpoints, Dictionary<Guid, RowMeta> Meta, IReadOnlyList<string> PlatformGroupIds,
        ContactRecipientRule.LineOpenness Line);

    private async Task<Facts> LoadFactsAsync(Guid companyId, Guid contactId, CancellationToken ct)
    {
        // ฝ่ายค้าน MED-2: อ่านผู้ติดต่อที่ถูกลบด้วย (ใบเก่ายังอ้าง) ⇒ ตัวตัดสินบอกเหตุ "ผู้ติดต่อถูกลบ" แทนการหายเงียบ · tenant ยังกรองเอง
        var c = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Id == contactId && x.CompanyId == companyId)
            .Select(x => new { x.Id, x.CompanyId, x.Email, x.Name, x.IsDeleted })
            .FirstOrDefaultAsync(ct);
        if (c == null) return new Facts(null, "", new(), new(), Array.Empty<string>(), ContactRecipientRule.LineOpenness.Closed);

        var rows = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.ContactId == contactId)
            .ToListAsync(ct);
        var consents = await ConsentsAsync(_db, companyId, rows, ct);
        // กลุ่มกลางของระบบ (คำตอบเจ้าของ Q1(b) · ฝ่ายค้าน S2) — ทั้งค่าหน้าแอดมินและ config แยกกัน (ไม่ใช่ผล Merge) · อ่านเฉพาะเมื่อมีปลายทางกลุ่ม
        IReadOnlyList<string> platformGroups = Array.Empty<string>();
        if (rows.Any(r => r.Kind == NotifyEndpointKind.LineGroup) && _nextAccLine != null)
            platformGroups = await _nextAccLine.PlatformGroupIdsAsync(ct);

        var facts = rows.Select(r => ToFacts(r, consents)).ToList();
        var meta = rows.ToDictionary(r => r.Id, r => new RowMeta(r.LinkedAt, r.LastSentAt, r.LastFailure));
        // รอบ 254 WP-11 L2: ความพร้อมของช่อง LINE (อ่านเฉพาะเมื่อมีปลายทาง LINE · ไม่มี cache ข้ามคำขอ)
        var line = rows.Any(r => NotifyEndpointKind.IsLine(r.Kind)) ? await LoadLineAsync(companyId, ct) : ContactRecipientRule.LineOpenness.Closed;
        return new Facts(new ContactRecipientRule.ContactFacts(c.Id, c.CompanyId, c.Email, c.IsDeleted), c.Name, facts, meta, platformGroups, line);
    }

    /// <summary>รอบ 254 WP-11 L2: ข้อเท็จจริงของ OA บริษัท (เปิด + token/secret ถอดได้ + บอทที่จำไว้) — ตัวหาผู้รับแบบอีเมลอย่างเดียว (ไม่มีตัวถอด) ⇒ ปิด ·
    /// เกณฑ์ "พร้อม" ตัวเดียวกับ <see cref="CompanyLineReadiness"/> (ด่านบันทึกกฎ/ปุ่มผูก LINE ใช้ตัวเดียวกัน)</summary>
    internal async Task<ContactRecipientRule.LineOpenness> LoadLineAsync(Guid companyId, CancellationToken ct)
    {
        if (_secrets == null) return ContactRecipientRule.LineOpenness.Closed;
        var s = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineEnabled, x.LineChannelAccessToken, x.LineChannelSecret, x.LineOaDestination })
            .FirstOrDefaultAsync(ct);
        var ready = CompanyLineReadiness.Evaluate(s?.LineEnabled ?? false,
            s != null && _secrets.IsUsable(s.LineChannelAccessToken), s != null && _secrets.IsUsable(s.LineChannelSecret));
        // รอบ 256 (คำตัดสิน 256-2): กลุ่มของผู้ติดต่อผ่าน OA กลาง ⇒ เฉพาะบริษัทผู้ให้บริการ (ตัวตัดสินเดียว LineGroupBindRule.CentralScopeAllowed · อ่านใหม่ทุกครั้ง)
        var centralGroups = LineGroupBindRule.CentralScopeAllowed(companyId, await PlatformCompanyLookup.IdAsync(_db, ct));
        // รอบ 262 (คำตัดสิน 262-1-1): บริษัทผู้ให้บริการ ⇒ OA กลางคือ OA ของบริษัทสำหรับ LINE ผู้ใช้ 1:1 ด้วย (ตัวตัดสินเดียวกับกลุ่ม) · บอทของ OA กลางตอนนี้ (เทียบบอทที่ผูก)
        var centralUsers = ProviderCentralOa.Allowed(companyId, await PlatformCompanyLookup.IdAsync(_db, ct));
        var centralBot = centralUsers && _nextAccLine != null ? (await _nextAccLine.GetAsync(ct)).BotUserId : null;
        return new ContactRecipientRule.LineOpenness(LineDeliveryOpen, ready.Ready, s?.LineOaDestination, ready.Problem, NextAccTenantDeliveryOpen,
            NextAccGroupOpen: centralGroups, NextAccUserOpen: centralUsers, CentralOaDestination: centralBot);
    }

    /// <summary>รอบ 260-4 (ฝ่ายค้าน H1): ความยินยอมของแถวปลายทาง (id → เวลาถอน) — ตัวเดียวของตัวหาผู้รับและรายการปลายทางทดสอบ LINE</summary>
    internal static async Task<IReadOnlyDictionary<Guid, DateTime?>> ConsentsAsync(AccountingDbContext db, Guid companyId,
        IEnumerable<ContactNotifyEndpoint> rows, CancellationToken ct)
    {
        var consentIds = rows.Where(r => r.ConsentRecordId != null).Select(r => r.ConsentRecordId!.Value).Distinct().ToList();
        return consentIds.Count == 0
            ? new Dictionary<Guid, DateTime?>()
            : await db.PdpaConsentRecords.AsNoTracking()
                .Where(r => r.CompanyId == companyId && consentIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.WithdrawnAt, ct);
    }

    private ContactRecipientRule.EndpointFacts ToFacts(ContactNotifyEndpoint r, IReadOnlyDictionary<Guid, DateTime?> consents) => FactsOf(r, consents, _secrets);

    /// <summary>รอบ 260-4 (ฝ่ายค้าน H1): แถว → ข้อเท็จจริงของตัวตัดสิน <c>ContactRecipientRule.Status</c> — ตัวเดียว</summary>
    internal static ContactRecipientRule.EndpointFacts FactsOf(ContactNotifyEndpoint r, IReadOnlyDictionary<Guid, DateTime?> consents, ISecretProtector? secrets)
    {
        // ความยินยอมต้องเป็นแถวของบริษัทนี้จริง — อ้าง id ที่ไม่พบ = ไม่มีความยินยอม
        Guid? consentId = r.ConsentRecordId is Guid id && consents.ContainsKey(id) ? id : null;
        var withdrawn = consentId is Guid cid ? consents[cid] : null;
        // ฝ่ายค้าน S2: ถอดไม่ออก ⇒ Unprotect คืน "" ⇒ ตัวตัดสินถือว่า "อ่านไม่ได้ = ใช้ไม่ได้" (ไม่ผ่านด่านกลุ่มกลางเพราะเทียบไม่ได้)
        var groupPlain = r.Kind == NotifyEndpointKind.LineGroup ? secrets?.Unprotect(r.LineGroupId) : null;
        return new ContactRecipientRule.EndpointFacts(r.Id, r.CompanyId, r.ContactId, r.Kind, r.Label, r.Email, groupPlain, r.OaScope,
            r.GroupNameSnapshot, r.MemberCountSnapshot, r.TopicsJson, r.ShowAmounts, r.IsPrimary, r.Verified, r.Source,
            consentId, withdrawn, r.BlockedAt, r.UnlinkedAt, r.UnlinkReason, r.IsDeleted, r.LinkedAt) { OaDestination = r.OaDestination };
    }

    private static string? OaLabel(ContactRecipientRule.EndpointFacts e) => !NotifyEndpointKind.IsLine(e.Kind) ? null : e.OaScope switch
    {
        NotifyEndpointKind.ScopeNextAcc => "OA กลางของ NextAcc (ในนามบริษัท)",
        NotifyEndpointKind.ScopeCompany => "OA ของบริษัท",
        _ => "ไม่ทราบ OA",
    };

    private static List<string> Names(IReadOnlyList<NotifyTopic>? topics) => (topics ?? Array.Empty<NotifyTopic>()).Select(t => t.ToString()).ToList();
    private static List<string> Labels(IReadOnlyList<NotifyTopic>? topics) => (topics ?? Array.Empty<NotifyTopic>()).Select(NotifyTopics.Label).ToList();
}
