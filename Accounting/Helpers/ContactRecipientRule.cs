namespace Accounting.Helpers;

/// <summary>
/// รอบ 245 L1 — <b>ตัวตัดสินเดียว</b> (pure) ว่า "เรื่องนี้ของผู้ติดต่อรายนี้ ไปถึงปลายทางใดได้บ้าง และปลายทางที่ไปไม่ได้ ไปไม่ได้เพราะอะไร"
/// (LINE_MULTICHANNEL_DESIGN §2 ข้อ 1 · §3.e · §8 L1) · ผู้อ่านข้อเท็จจริงจากฐานคือ <c>ContactRecipientResolver</c> ตัวเดียว
///
/// <para><b>ไม่ถดถอย</b>: ผู้ติดต่อที่ไม่มีแถวปลายทางเลย ⇒ ได้ <c>Contact.Email</c> (ทุกเรื่อง) เหมือนเดิม · อีเมลหลักอ่านสดจาก <c>Contact.Email</c> เสมอ</para>
/// <para><b>Tenant</b>: ผู้ติดต่อของบริษัทอื่น/ถูกลบ ⇒ ชุดว่าง · แถวที่ <c>CompanyId</c>/<c>ContactId</c> ไม่ตรง ⇒ ไม่ถูกนับ (ไม่ถึงแม้แต่การแสดงผล)</para>
/// <para><b>คำตัดสินเจ้าของ Q1(b) (2026-11-06)</b>: groupId กลางของระบบ — <b>ทุกแหล่ง</b> (หน้าแอดมิน <c>SiteSettings.LineMessagingGroupId</c> และ config
/// <c>Line:GroupId</c> แยกกัน · ฝ่ายค้าน S2) — <b>ห้าม</b>เป็นปลายทางของผู้ติดต่อ ⇒ <see cref="GroupProblem"/> ปฏิเสธเสมอ (ทั้งตอนส่งและตอนผูก) ·
/// groupId ที่อ่านไม่ได้ (ถอดรหัสไม่ออก/ว่าง) = ใช้ไม่ได้ (ไม่ผ่านด่านเพราะ "ไม่รู้ว่าเป็นกลุ่มอะไร")</para>
/// <para><b>Q2</b>: กลุ่มที่ผูกได้ ≤ <see cref="MaxGroupMembers"/> คน · <b>PDPA</b>: ปลายทาง LINE ต้องมีหลักฐานความยินยอมที่ยังไม่ถอน · อีเมลใช้ฐานสัญญา (ม.24(3))</para>
/// <para>L1: ส่งทาง LINE ยังไม่เปิด (<c>lineDeliveryOpen = false</c>) ⇒ ปลายทาง LINE แสดงได้ แต่ไม่อยู่ในชุดผู้รับ พร้อมเหตุ (ไม่ใช่หายเงียบ)</para>
/// <para>แถวอีเมลหลักหลายแถว (หลังรวมผู้ติดต่อ — ฝ่ายค้าน S4) ⇒ เลือกด้วย <see cref="PickPrimary"/> ตัวเดียว (ผูกก่อนชนะ · ไม่เลือกตาม Guid) ·
/// การรวมผู้ติดต่อยกเลิกแถวที่ซ้ำด้วย <see cref="MergeUnlinks"/></para>
/// </summary>
public static class ContactRecipientRule
{
    public const string RuleCodePlatformGroup = "NOTIFY-CONTACT-PLATFORM-GROUP";
    public const string RuleCodeGroupCap = "NOTIFY-CONTACT-GROUP-CAP";
    public const string RuleCodeGroupUnreadable = "NOTIFY-CONTACT-GROUP-UNREADABLE";
    public const string RuleCodeConsent = "PDPA-CONTACT-NOTIFY-CONSENT";
    /// <summary>คำตอบเจ้าของ Q2 — ส่งเข้ากลุ่มนับโควตาตามจำนวนสมาชิก ⇒ จำกัดกลุ่มที่ผูกได้</summary>
    public const int MaxGroupMembers = 200;
    /// <summary>อีเมลเพิ่มเติม (ไม่นับอีเมลหลัก) ต่อผู้ติดต่อ — กันใช้เป็นรายชื่อส่งจำนวนมาก</summary>
    public const int MaxEmailsPerContact = 10;
    public const int MaxLabelLength = 100;
    /// <summary>EntityType ของแถว audit การเปลี่ยนปลายทาง (EntityId = ContactId ⇒ อยู่ในรายงานสิทธิ์เข้าถึงของเจ้าของข้อมูล)</summary>
    public const string AuditEntityType = "ContactNotifyEndpoint.Change";

    /// <summary>ตรวจอีเมลที่จะเพิ่ม — ตัวแยก/ตรวจรูปแบบเดียวกับฝั่งส่ง (<c>SignEmailThrottle.ParseRecipients</c>) · ซ้ำอีเมลหลัก/ซ้ำแถวที่ใช้อยู่ = 409 · เกินเพดาน = 400</summary>
    public static (string? Email, string? Problem, int Status) ValidateNewEmail(string? raw, string? contactEmail, IEnumerable<string?> activeEmails)
    {
        var (valid, invalid) = SignEmailThrottle.ParseRecipients(new[] { raw });
        if (valid.Count != 1 || invalid.Count > 0) return (null, "กรอกอีเมลที่ถูกต้องหนึ่งอีเมลต่อครั้ง", 400);
        var email = valid[0];
        if (SignEmailThrottle.ParseRecipients(new[] { contactEmail }).Valid.Contains(email))
            return (null, "อีเมลนี้เป็นอีเมลหลักของผู้ติดต่ออยู่แล้ว — ตั้งเรื่องที่รับได้ที่แถวอีเมลหลัก", 409);
        var existing = activeEmails.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim().ToLowerInvariant()).ToList();
        if (existing.Contains(email)) return (null, "มีอีเมลนี้ในช่องทางแจ้งเตือนของผู้ติดต่อแล้ว", 409);
        if (existing.Count >= MaxEmailsPerContact) return (null, $"เพิ่มอีเมลได้ไม่เกิน {MaxEmailsPerContact} อีเมลต่อผู้ติดต่อ (ไม่นับอีเมลหลัก)", 400);
        return (email, null, 200);
    }

    /// <summary>รอบ 250 WP-08 (ฝ่ายค้าน LOW): แฮช SHA-256 ของอีเมล (ตัวเล็ก · ผูกบริษัท) สำหรับ audit append-only — ไม่ใช่ความลับ แต่ไม่เปิดเผยที่อยู่ตรง ๆ (PDPA ม.33)</summary>
    public static string EmailAuditHash(Guid companyId, string email)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{companyId:N}|{email.Trim().ToLowerInvariant()}"))).ToLowerInvariant();

    /// <summary>ป้ายของปลายทาง — ตัดช่องว่าง/อักขระควบคุม · ว่าง ⇒ null · ยาวเกิน ⇒ ปฏิเสธ (ไม่ตัดเงียบ)</summary>
    public static (string? Label, string? Problem) NormalizeLabel(string? raw)
    {
        var t = new string((raw ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (t.Length == 0) return (null, null);
        return t.Length > MaxLabelLength ? (null, $"ป้ายยาวได้ไม่เกิน {MaxLabelLength} ตัวอักษร") : (t, null);
    }

    public sealed record ContactFacts(Guid Id, Guid CompanyId, string? Email, bool IsDeleted);

    /// <summary>ข้อเท็จจริงของแถวปลายทางหนึ่งแถว (ผู้อ่านถอดรหัส groupId ให้เฉพาะ Kind=LineGroup — ใช้เทียบกับกลุ่มกลาง · ถอดไม่ออก = ค่าว่าง)</summary>
    public sealed record EndpointFacts(
        Guid Id, Guid CompanyId, Guid ContactId, string Kind, string? Label, string? Email,
        string? LineGroupIdPlain, string OaScope, string? GroupName, int? MemberCount,
        string? TopicsJson, bool ShowAmounts, bool IsPrimary, bool Verified, string Source,
        Guid? ConsentRecordId, DateTime? ConsentWithdrawnAt,
        DateTime? BlockedAt, DateTime? UnlinkedAt, string? UnlinkReason, bool IsDeleted, DateTime LinkedAt);

    /// <summary>สถานะของแถว (ไม่ขึ้นกับเรื่อง) — <paramref name="Code"/> เป็นค่าคงที่ให้เทสต์/หน้าใช้จัดสี · ป้าย/เหตุเป็นภาษาไทยที่หน้าแสดงตรงตัว</summary>
    public sealed record EndpointStatus(bool Usable, string Code, string Label, string? Reason, string? RuleCode);

    /// <summary>ปลายทางหนึ่งตัวสำหรับเรื่องหนึ่ง · <paramref name="EndpointId"/> null = อีเมลหลักที่ยังไม่มีแถวค่าตั้ง</summary>
    public sealed record RecipientOption(Guid? EndpointId, string Kind, string? Email, string Display, bool Available,
        string? Reason, string? RuleCode, bool ShowAmounts, bool IsPrimary);

    public sealed record RecipientSet(NotifyTopic Topic, IReadOnlyList<RecipientOption> Options)
    {
        /// <summary>อีเมลที่ส่งได้ (ไม่ซ้ำ · ตัวเล็ก)</summary>
        public IReadOnlyList<string> Emails => Options
            .Where(o => o.Available && o.Kind == NotifyEndpointKind.Email && o.Email != null)
            .Select(o => o.Email!.ToLowerInvariant()).Distinct().ToList();
        public IReadOnlyList<RecipientOption> LineUsers => Options.Where(o => o.Available && o.Kind == NotifyEndpointKind.LineUser).ToList();
        public IReadOnlyList<RecipientOption> LineGroups => Options.Where(o => o.Available && o.Kind == NotifyEndpointKind.LineGroup).ToList();
        /// <summary>รอบ 250 WP-08 ฝ่ายค้าน MED-2: ผู้ติดต่อของบริษัทนี้ถูกลบ (ชุดว่างโดยตั้งใจ) — ต่างจาก "ไม่มีผู้ติดต่อ/บริษัทอื่น" ที่ไม่มีเหตุให้บอก</summary>
        public bool ContactDeleted { get; init; }
    }

    /// <summary>รอบ 250 WP-08 ฝ่ายค้าน MED-2: เหตุไม่ส่งเมื่อผู้ติดต่อถูกลบ (ตัวเดียว — คิว/งานหลังรับชำระ/หน้ารายการประจำแสดงตรงตัว)</summary>
    public const string ContactDeletedReason = "ผู้ติดต่อนี้ถูกลบแล้ว — ระบบไม่ส่งอีเมลอัตโนมัติถึงผู้ติดต่อที่ลบ (กู้คืนผู้ติดต่อ หรือส่งเองจากหน้าเอกสาร)";

    /// <summary>รอบ 250 WP-08 (L1 ต่อสาย): ผล "ส่งอีเมลเรื่องนี้ถึงใคร" ของทุกเส้นส่ง (ตารางส่ง · ทวงชำระ · ใบแจ้งยอด · 50 ทวิ · ใบเสร็จหลังรับชำระ · ลิงก์เซ็น ·
    /// ความพร้อมของรายการประจำ) — <see cref="Emails"/> ว่าง + <see cref="OptOutReason"/> มีค่า = ไม่ส่งเพราะค่าตั้ง (ยกเลิกรับ/ค่าตั้งเสียหาย/รูปแบบผิด/ผู้ติดต่อถูกลบ)
    /// ⇒ ผู้ส่ง<b>ไม่ส่ง</b>และบันทึกเหตุ (ล้มดัง) · ว่างทั้งคู่ = ผู้ติดต่อไม่มีอีเมลเลย (พฤติกรรมเดิม: ไม่เข้าคิว) ·
    /// ลำดับ: <b>อีเมลหลักก่อนเสมอ</b> (<see cref="PrimaryEmails"/> แยกไว้ชัด — ไม่พึ่งลำดับของ Options) แล้วตามด้วยอีเมลเพิ่มเติม</summary>
    public sealed record EmailDelivery(IReadOnlyList<string> Emails, string? OptOutReason)
    {
        /// <summary>อีเมลหลัก (<c>Contact.Email</c>) ที่รับเรื่องนี้ — ว่าง = อีเมลหลักไม่รับ/ไม่มี · ผู้รับใบกำกับ e-Tax by Email (<see cref="ForChannel"/>)</summary>
        public IReadOnlyList<string> PrimaryEmails { get; init; } = Array.Empty<string>();
        /// <summary>อีเมลของผู้ติดต่อ (หลัก/เพิ่มเติม) ที่ค่าตั้ง<b>ปฏิเสธ</b>เรื่องนี้ (ไม่ได้เลือกรับ · ค่าตั้งเสียหาย · ถูกบล็อก/ถอด) — ตัวเล็ก ·
        /// ใช้แยก "ผู้รับที่ช่องทางของผู้ติดต่อบอกว่าไม่รับ" ออกจาก "ผู้รับที่ผู้ใช้พิมพ์เองซึ่งไม่อยู่ในช่องทางใดของผู้ติดต่อ" (<c>SignRequestService.SignAutomationRecipients</c>)</summary>
        public IReadOnlyList<string> DeclinedEmails { get; init; } = Array.Empty<string>();
        /// <summary>ผู้ติดต่อของบริษัทนี้ถูกลบ (<see cref="RecipientSet.ContactDeleted"/>) — งานอัตโนมัติไม่ส่งถึงใครเลย</summary>
        public bool ContactDeleted { get; init; }
        public bool Deliverable => Emails.Count > 0;
        /// <summary>ผู้รับทั้งหมดคั่นด้วย ", " (ตัวแยกเดียวกับ <c>SignEmailThrottle.ParseRecipients</c>) · null = ไม่มี</summary>
        public string? To => Emails.Count == 0 ? null : string.Join(", ", Emails);
        public string? First => Emails.Count == 0 ? null : Emails[0];
    }

    /// <summary>อีเมลที่ส่งได้ของชุดผู้รับ (อีเมลหลักก่อน) + เหตุเมื่อไม่ส่ง · LINE ไม่ถูกนับ (L1: ยังไม่เปิดส่ง · ไม่ใช่เหตุให้ไม่ส่งอีเมล)</summary>
    public static EmailDelivery ForEmail(RecipientSet set)
    {
        var primary = set.Options
            .Where(o => o.Available && o.IsPrimary && o.Kind == NotifyEndpointKind.Email && o.Email != null)
            .Select(o => o.Email!.ToLowerInvariant()).Distinct().ToList();
        var emails = primary.Concat(set.Emails.Where(e => !primary.Contains(e))).ToList();
        var declined = set.Options
            .Where(o => o.Kind == NotifyEndpointKind.Email && !o.Available)
            .Select(o => (o.Email ?? o.Display ?? "").Trim().ToLowerInvariant())
            .Where(e => e.Contains('@') && !emails.Contains(e)).Distinct().ToList();
        if (emails.Count > 0) return new EmailDelivery(emails, null) { PrimaryEmails = primary, DeclinedEmails = declined };
        if (set.ContactDeleted) return new EmailDelivery(emails, ContactDeletedReason) { ContactDeleted = true };
        var blocked = set.Options
            .Where(o => o.Kind == NotifyEndpointKind.Email && !o.Available && !string.IsNullOrWhiteSpace(o.Reason))
            .Select(o => $"{o.Display}: {o.Reason}").Distinct().ToList();
        return new EmailDelivery(emails, blocked.Count == 0 ? null
            : $"ไม่ส่งอีเมลเรื่อง \"{NotifyTopics.Label(set.Topic)}\" ตามช่องทางแจ้งเตือนของผู้ติดต่อ — {string.Join(" · ", blocked)} (แก้ที่หน้าผู้ติดต่อ แท็บช่องทางแจ้งเตือน)")
            { DeclinedEmails = declined };
    }

    /// <summary>รอบ 250 WP-08 ฝ่ายค้าน HIGH-1: ผู้รับตามเส้นการส่งจริง — <paramref name="To"/> null = ไม่ส่ง (<paramref name="Problem"/> = เหตุ) ·
    /// <paramref name="NotSent"/> = ปลายทางที่รับเรื่องนี้แต่เส้นนี้ส่งถึงไม่ได้ · <paramref name="Note"/> = ข้อความบนแถวคิว/งาน (คงไว้หลังส่ง)</summary>
    public sealed record ChannelRecipients(string? To, IReadOnlyList<string> NotSent, string? Problem, string? Note);

    /// <summary>หมายเหตุที่คงไว้บนแถวคิวหลังส่งสำเร็จ (ผู้ส่งคิวไม่ล้าง — <c>EmailScheduleService.AfterSentNote</c>)</summary>
    public const string EtaxPrimaryOnlyPrefix = "ส่งใบกำกับ e-Tax ถึงอีเมลหลักเท่านั้น: ";

    /// <summary>
    /// รอบ 250 WP-08 ฝ่ายค้าน HIGH-1 — <b>e-Tax by Email ส่งถึงผู้ซื้อ 1 อีเมล ไม่มีสำเนา</b> (คู่มือกรมสรรพากร · <c>EtaxByEmailFormat.Recipients</c>) ⇒
    /// เส้น e-Tax ส่งถึง<b>อีเมลหลักเท่านั้น</b> (ตรงกับข้อความ <c>EtaxByEmailFormat.ContactRecipientsNote</c>: "อีเมลเพิ่มเติมจะไม่ได้รับใบกำกับ") ·
    /// อีเมลเพิ่มเติมที่รับเรื่องนี้ ⇒ <b>ไม่ส่งสำเนาแบบอีเมลธรรมดา</b> (PDF/A-3 ที่ไม่ผ่าน ETDA = สำเนาใบกำกับที่ไม่ใช่ทางที่กฎหมายรับ · ตัวตรวจผู้รับปฏิเสธสำเนาอยู่แล้ว)
    /// แต่<b>บันทึกว่าไม่ได้ส่ง</b>บนแถว · อีเมลหลักไม่รับเรื่องนี้ ⇒ ไม่ส่งพร้อมเหตุ (ไม่ย้ายไปอีเมลเพิ่มเติม) · อีเมลหลักหลายอีเมลในช่องเดียว ⇒ ส่งต่อให้ตัวตรวจผู้รับปฏิเสธ (ตามข้อความเดิม) ·
    /// ไม่ใช่เส้น e-Tax ⇒ ทุกปลายทาง (ผลเดิม)
    /// </summary>
    public static ChannelRecipients ForChannel(EmailDelivery d, bool etaxByEmail)
    {
        if (!d.Deliverable) return new ChannelRecipients(null, Array.Empty<string>(), d.OptOutReason, null);
        if (!etaxByEmail) return new ChannelRecipients(d.To, Array.Empty<string>(), null, null);
        var rest = d.Emails.Where(e => !d.PrimaryEmails.Contains(e)).ToList();
        if (d.PrimaryEmails.Count == 0)
            return new ChannelRecipients(null, rest,
                "ไม่ส่งใบกำกับภาษี — บริษัทใช้ e-Tax by Email ซึ่งส่งถึงผู้ซื้อได้ 1 อีเมล (อีเมลหลัก) แต่อีเมลหลักของผู้ติดต่อไม่ได้เลือกรับเรื่องนี้ · "
                + $"อีเมลเพิ่มเติม ({string.Join(", ", rest)}) ไม่ได้รับใบกำกับ (ไม่มีสำเนา) — เปิดรับที่อีเมลหลัก หรือส่งเองจากหน้าเอกสาร", null);
        var to = string.Join(", ", d.PrimaryEmails);
        return new ChannelRecipients(to, rest, null, rest.Count == 0 ? null
            : EtaxPrimaryOnlyPrefix + $"ไม่ได้ส่งถึง {string.Join(", ", rest)} (e-Tax by Email ไม่มีสำเนา — ส่งต่ออีเมลที่ ETDA ประทับเวลาแล้วได้)");
    }

    /// <summary>รอบ 250 WP-08 ฝ่ายค้าน MED-3: เพดานผู้รับของอีเมลผ่าน NextAcc (To+Cc+Bcc ≤ <c>SignEmailThrottle.MaxRecipients</c>) และความยาวช่อง (≤ <paramref name="maxLength"/>) —
    /// ตัดตั้งแต่ตอนเข้าคิว (อีเมลหลักก่อน) พร้อมหมายเหตุ ไม่ใช่ปล่อยให้ผู้ส่งปฏิเสธทั้งฉบับ · ส่งผ่าน SMTP บริษัท ⇒ ไม่จำกัดจำนวน (จำกัดแค่ความยาว)</summary>
    public static (string To, string? Note) CapForSender(string to, bool platformSender, int otherRecipients, int maxRecipients, int maxLength)
    {
        var all = SignEmailThrottle.ParseRecipients(new[] { to }).Valid.ToList();
        if (all.Count == 0) return (to, null);
        var keep = new List<string>();
        var limit = platformSender ? Math.Max(1, maxRecipients - Math.Max(0, otherRecipients)) : int.MaxValue;
        foreach (var a in all)
        {
            if (keep.Count >= limit) break;
            if (keep.Count > 0 && string.Join(", ", keep.Append(a)).Length > maxLength) break;
            keep.Add(a);
        }
        var dropped = all.Skip(keep.Count).ToList();
        return (string.Join(", ", keep), dropped.Count == 0 ? null
            : $"{CappedPrefix}ไม่ได้ส่งถึง {string.Join(", ", dropped)} — "
              + (platformSender && all.Count > limit
                  ? $"อีเมลผ่านระบบ NextAcc ส่งได้ครั้งละไม่เกิน {maxRecipients} ผู้รับ (รวมสำเนา/สำเนาลับ) · ตั้งค่าอีเมลของบริษัทเพื่อส่งถึงทุกปลายทาง"
                  : "ช่องผู้รับยาวเกินที่ระบบเก็บได้ · ลดปลายทางที่รับเรื่องนี้"));
    }

    /// <summary>หมายเหตุ "ตัดผู้รับ" ที่คงไว้หลังส่ง</summary>
    public const string CappedPrefix = "ส่งไม่ครบทุกปลายทาง: ";

    /// <summary>groupId นี้เป็นกลุ่มกลางของระบบ (แหล่งใดแหล่งหนึ่ง) ไหม — เทียบตรงตัวหลังตัดช่องว่าง</summary>
    internal static bool IsPlatformCentralGroup(string? groupId, IEnumerable<string?> platformGroupIds)
    {
        var g = (groupId ?? "").Trim();
        return g.Length > 0 && platformGroupIds.Any(p => p != null && string.Equals(g, p.Trim(), StringComparison.Ordinal));
    }

    /// <summary>กลุ่มนี้ผูก/ส่งไม่ได้เพราะอะไร (null = ได้) — ตัวเดียวทั้งฝั่งส่ง (<see cref="Status"/>) และฝั่งผูก (เฟส L3 ต้องเรียกตัวนี้ ห้ามเขียนเงื่อนไขเอง)</summary>
    internal static (string Problem, string RuleCode)? GroupProblem(string? groupId, int? memberCount, IEnumerable<string?> platformGroupIds)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return ("อ่าน Group ID ของกลุ่มนี้ไม่ได้ (ถอดรหัสไม่ออกหรือไม่มีค่า) — ระบบไม่รู้ว่าเป็นกลุ่มใด จึงไม่ใช้ส่ง · ยกเลิกแล้วผูกกลุ่มใหม่", RuleCodeGroupUnreadable);
        if (IsPlatformCentralGroup(groupId, platformGroupIds))
            return ("กลุ่มนี้คือกลุ่ม LINE กลางของระบบ NextAcc (รับเฉพาะเรื่องของระบบ) — ใช้เป็นปลายทางของผู้ติดต่อไม่ได้ (คำตัดสินเจ้าของ Q1(b))", RuleCodePlatformGroup);
        if (memberCount is int n && n > MaxGroupMembers)
            return ($"กลุ่มมีสมาชิก {n} คน เกินเพดาน {MaxGroupMembers} คน (ส่งเข้ากลุ่มนับโควตาตามจำนวนสมาชิก — คำตัดสินเจ้าของ Q2)", RuleCodeGroupCap);
        return null;
    }

    /// <summary>สถานะของแถว — ลำดับ: ยกเลิก → กลุ่มอ่านไม่ได้/กลุ่มกลาง/เกินเพดาน → OA ไม่รู้ → บล็อก → ความยินยอม → ยังไม่ยืนยัน → LINE ยังไม่เปิด</summary>
    public static EndpointStatus Status(EndpointFacts e, IReadOnlyCollection<string> platformGroupIds, bool lineDeliveryOpen)
    {
        if (e.UnlinkedAt != null)
            return new(false, "unlinked", "ยกเลิกแล้ว", $"ยกเลิกการผูกแล้ว — {NotifyEndpointKind.UnlinkReasonLabel(e.UnlinkReason)}", null);
        if (e.Kind == NotifyEndpointKind.Email)
        {
            if (e.IsPrimary) return new(true, "ok", "ใช้งาน", null, null);
            return OnBehalfEmailSender.IsPlausibleEmail(e.Email)
                ? new(true, "ok", "ใช้งาน", null, null)
                : new(false, "invalid", "อีเมลไม่ถูกต้อง", "รูปแบบอีเมลไม่ถูกต้อง — แก้หรือยกเลิกปลายทางนี้", null);
        }
        if (!NotifyEndpointKind.IsLine(e.Kind))
            return new(false, "unknown-kind", "ไม่ทราบชนิด", $"ชนิดปลายทาง \"{e.Kind}\" ไม่รู้จัก — ไม่ใช้ส่ง", null);
        if (e.Kind == NotifyEndpointKind.LineGroup && GroupProblem(e.LineGroupIdPlain, e.MemberCount, platformGroupIds) is { } gp)
            return new(false, gp.RuleCode switch
            {
                RuleCodePlatformGroup => "platform-group",
                RuleCodeGroupUnreadable => "group-unreadable",
                _ => "group-cap",
            }, "ใช้ไม่ได้", gp.Problem, gp.RuleCode);
        if (e.OaScope != NotifyEndpointKind.ScopeNextAcc && e.OaScope != NotifyEndpointKind.ScopeCompany)
            return new(false, "legacy", "ต้องผูกใหม่",
                "LINE เดิมที่ไม่รู้ว่าผูกกับ OA ใด (LINE userId ใช้ได้เฉพาะ OA ที่ออกค่านั้น) — ไม่ใช้ส่ง · ให้ผู้รับผูกใหม่ (เฟส L5)", null);
        if (e.BlockedAt != null)
            return new(false, "blocked", "ผู้รับบล็อก", "ผู้รับบล็อกหรือเลิกเป็นเพื่อนกับ OA — เพิ่มเพื่อนกลับแล้วจะใช้ได้อีก", null);
        if (e.ConsentRecordId == null)
            return new(false, "no-consent", "ไม่มีความยินยอม", "ไม่มีหลักฐานความยินยอมรับข้อความทาง LINE (PDPA) — ส่งไม่ได้", RuleCodeConsent);
        if (e.ConsentWithdrawnAt != null)
            return new(false, "consent-withdrawn", "ถอนความยินยอม", "เจ้าของข้อมูลถอนความยินยอมแล้ว — ส่งไม่ได้", RuleCodeConsent);
        if (e.Kind == NotifyEndpointKind.LineGroup && !e.Verified)
            return new(false, "unverified", "ยังไม่ยืนยัน", "กลุ่มยังไม่ยืนยันด้วยรหัสผูก (#HeyNextAcc) — ส่งไม่ได้", null);
        if (!lineDeliveryOpen)
            return new(false, "line-not-open", "ยังไม่เปิดส่ง",
                "ระบบยังไม่ส่งข้อความทาง LINE ถึงผู้ติดต่อ (เปิดในเฟส L2/L3) — ปลายทางนี้ถูกเก็บไว้แล้ว", null);
        return new(true, "ok", "ใช้งาน", null, null);
    }

    /// <summary>ป้ายฐานกฎหมาย/ความยินยอมของแถว (หน้าแสดงตรงตัว)</summary>
    public static string ConsentLabel(EndpointFacts e)
    {
        if (e.Kind == NotifyEndpointKind.Email)
            return "ฐานสัญญา (PDPA ม.24(3)) — ส่งเอกสารตามสัญญากับผู้ติดต่อ ไม่ต้องขอความยินยอมแยก";
        if (e.ConsentRecordId == null) return "ไม่มีหลักฐานความยินยอม — ส่งทาง LINE ไม่ได้";
        if (e.ConsentWithdrawnAt != null) return "ถอนความยินยอมแล้ว";
        return e.Kind == NotifyEndpointKind.LineGroup
            ? "ผู้ดูแลบริษัทรับรองว่าสมาชิกกลุ่มมีสิทธิ์เห็นเอกสารของผู้ติดต่อ + ประกาศในกลุ่ม (PDPA ม.23)"
            : "ผู้รับยินยอมเองบนหน้าผูก LINE (PDPA ม.19)";
    }

    /// <summary>แถวค่าตั้งอีเมลหลักที่มีผล (ฝ่ายค้าน S4) — ผูกก่อนชนะ · เวลาผูกเท่ากันพอดี ⇒ สร้างก่อนชนะ · ห้ามเลือกตาม Guid (สุ่ม ⇒ ผลเปลี่ยนตามการรวม) ·
    /// ปกติเหลือแถวเดียวอยู่แล้ว (การรวมยกเลิกแถวของผู้ถูกรวม · <c>UpdatePrimaryAsync</c> ยกเลิกแถวเกิน)</summary>
    public static T? PickPrimary<T>(IEnumerable<T> rows, Func<T, DateTime> linkedAt, Func<T, DateTime> createdAt) where T : class
        => rows.OrderBy(linkedAt).ThenBy(createdAt).FirstOrDefault();

    /// <summary>แถวของผู้ติดต่อหลังรวม (ฝ่ายค้าน S4) — ข้อเท็จจริงที่ตัวตัดสินการรวมต้องใช้</summary>
    public sealed record MergeRow(Guid Id, string Kind, bool IsPrimary, string? Email, string? LineIdHash, bool FromLoser, DateTime LinkedAt);

    /// <summary>รวมผู้ติดต่อแล้วต้องยกเลิกแถวใด (เหตุ <c>merge</c>): แถวอีเมลหลักของผู้ถูกรวม (ผู้ติดต่อที่เก็บไว้มีอีเมลหลักของตัวเอง) ·
    /// อีเมลรองที่ซ้ำกับอีเมลหลักของผู้ที่เก็บไว้ · อีเมลรอง/LINE ที่ซ้ำกันเอง (คงแถวของผู้ที่เก็บไว้ก่อน แล้วผูกก่อน)</summary>
    public static IReadOnlyList<Guid> MergeUnlinks(IEnumerable<MergeRow> activeRows, string? keepContactEmail)
    {
        var rows = activeRows.ToList();
        var unlink = new List<Guid>();
        var keepPrimaries = rows.Where(r => r.IsPrimary && r.Kind == NotifyEndpointKind.Email && !r.FromLoser).OrderBy(r => r.LinkedAt).ToList();
        foreach (var r in rows.Where(r => r.IsPrimary && r.Kind == NotifyEndpointKind.Email))
            if (r.FromLoser || (keepPrimaries.Count > 0 && r.Id != keepPrimaries[0].Id)) unlink.Add(r.Id);
        // ผู้ที่เก็บไว้ไม่มีแถวหลัก ⇒ คงแถวหลักแรกของผู้ถูกรวม (เรื่องที่ผู้ใช้ตั้งไว้ไม่หาย · อีเมลหลักของผู้ที่เก็บไว้อาจรับมาจากผู้ถูกรวม)
        if (keepPrimaries.Count == 0)
        {
            var carry = rows.Where(r => r.IsPrimary && r.Kind == NotifyEndpointKind.Email).OrderBy(r => r.LinkedAt).FirstOrDefault();
            if (carry != null) unlink.Remove(carry.Id);
        }
        var primaryEmails = SignEmailThrottle.ParseRecipients(new[] { keepContactEmail }).Valid;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => !r.IsPrimary).OrderBy(r => r.FromLoser).ThenBy(r => r.LinkedAt))
        {
            string? key = r.Kind == NotifyEndpointKind.Email
                ? (r.Email ?? "").Trim().ToLowerInvariant()
                : string.IsNullOrEmpty(r.LineIdHash) ? null : $"{r.Kind}:{r.LineIdHash}";
            if (string.IsNullOrEmpty(key)) continue;
            if (r.Kind == NotifyEndpointKind.Email && primaryEmails.Contains(key)) { unlink.Add(r.Id); continue; }
            if (!seen.Add(key)) unlink.Add(r.Id);
        }
        return unlink;
    }

    /// <summary>ผู้รับของเรื่อง <paramref name="topic"/> — ทุกปลายทางที่เห็นได้ พร้อม Available/เหตุ · ใช้ส่งจริงอ่าน <see cref="RecipientSet.Emails"/> ฯลฯ</summary>
    public static RecipientSet Resolve(Guid companyId, ContactFacts? contact, IEnumerable<EndpointFacts> endpoints, NotifyTopic topic,
        IReadOnlyCollection<string> platformGroupIds, bool lineDeliveryOpen)
    {
        var options = new List<RecipientOption>();
        // ฝ่ายค้าน MED-2: ผู้ติดต่อของบริษัทนี้ที่ถูกลบ ⇒ ชุดว่างพร้อมธง (ForEmail บอกเหตุ) · บริษัทอื่น/ไม่พบ ⇒ ชุดว่างเฉย ๆ (ไม่เปิดเผยอะไร)
        if (contact != null && contact.CompanyId == companyId && contact.IsDeleted) return new RecipientSet(topic, options) { ContactDeleted = true };
        if (contact == null || contact.CompanyId != companyId || contact.IsDeleted) return new RecipientSet(topic, options);
        var rows = endpoints.Where(e => e.CompanyId == companyId && e.ContactId == contact.Id && !e.IsDeleted).ToList();
        var topicLabel = NotifyTopics.Label(topic);

        // ── อีเมลหลัก (Contact.Email · อ่านสด) — ไม่มีแถวค่าตั้ง = ทุกเรื่อง (พฤติกรรมเดิม) ──
        var primaryRow = PickPrimary(rows.Where(e => e.IsPrimary && e.Kind == NotifyEndpointKind.Email && e.UnlinkedAt == null), e => e.LinkedAt, e => e.LinkedAt);
        var primaryTopics = primaryRow == null ? NotifyTopics.All : NotifyTopics.Parse(primaryRow.TopicsJson);
        var (validPrimary, invalidPrimary) = SignEmailThrottle.ParseRecipients(new[] { contact.Email });
        foreach (var addr in validPrimary)
            options.Add(WithTopic(new RecipientOption(primaryRow?.Id, NotifyEndpointKind.Email, addr, addr, true, null, null, true, true),
                primaryTopics, topic, topicLabel));
        foreach (var bad in invalidPrimary)
            options.Add(new RecipientOption(primaryRow?.Id, NotifyEndpointKind.Email, null, bad, false,
                "อีเมลหลักของผู้ติดต่อรูปแบบไม่ถูกต้อง — แก้ที่แท็บข้อมูลทั่วไป", null, true, true));
        if (primaryRow != null && validPrimary.Count == 0 && invalidPrimary.Count == 0)
            options.Add(new RecipientOption(primaryRow.Id, NotifyEndpointKind.Email, null, "(ยังไม่มีอีเมลหลัก)", false,
                "ผู้ติดต่อยังไม่มีอีเมลหลัก — กรอกที่แท็บข้อมูลทั่วไป", null, true, true));

        // ── ปลายทางอื่น (แถวหลักที่ไม่ได้เลือก = ไม่ใช้) ──
        foreach (var e in rows.Where(e => !(e.IsPrimary && e.Kind == NotifyEndpointKind.Email)).OrderBy(e => e.Kind).ThenBy(e => e.LinkedAt))
        {
            var st = Status(e, platformGroupIds, lineDeliveryOpen);
            var display = e.Kind == NotifyEndpointKind.Email
                ? (e.Email ?? "").Trim()
                : !string.IsNullOrWhiteSpace(e.Label) ? e.Label!.Trim()
                : !string.IsNullOrWhiteSpace(e.GroupName) ? e.GroupName!.Trim()
                : NotifyEndpointKind.Label(e.Kind);
            var showAmounts = e.Kind != NotifyEndpointKind.LineGroup || e.ShowAmounts;
            var email = e.Kind == NotifyEndpointKind.Email && st.Usable ? (e.Email ?? "").Trim() : null;
            var opt = new RecipientOption(e.Id, e.Kind, email, display, st.Usable, st.Reason, st.RuleCode, showAmounts, false);
            options.Add(st.Usable ? WithTopic(opt, NotifyTopics.Parse(e.TopicsJson), topic, topicLabel) : opt);
        }
        return new RecipientSet(topic, options);
    }

    private static RecipientOption WithTopic(RecipientOption o, IReadOnlyList<NotifyTopic>? topics, NotifyTopic topic, string topicLabel)
    {
        if (topics == null)
            return o with { Available = false, Reason = "ค่าตั้งเรื่องที่รับเสียหาย — เปิดแก้แล้วบันทึกใหม่ (ระบบไม่เดาว่ารับทุกเรื่อง)" };
        return topics.Contains(topic) ? o : o with { Available = false, Reason = $"ไม่ได้เลือกรับเรื่อง \"{topicLabel}\"" };
    }
}
