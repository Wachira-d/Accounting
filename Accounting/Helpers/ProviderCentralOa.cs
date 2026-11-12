namespace Accounting.Helpers;

/// <summary>
/// รอบ 262 (คำตัดสิน 262-1-1…262-1-8 · <c>erp-review/2026-11-12/team-262-provider-line-billing.md</c>) — <b>ตัวตัดสินเดียว (pure)</b> ของ
/// "บริษัทผู้ให้บริการ + LINE OA กลาง = LINE OA ของบริษัท สำหรับผู้ติดต่อ"
/// <para>═══ ที่มา ═══ เจ้าของ: "ใช้ LINE OA เรียกเก็บเงินค่าบริการบัญชี เงินเข้าบัญชีของระบบ ควรต้องใช้ได้" — บริษัทผู้ให้บริการ (<c>SiteSettings.PlatformCompanyId</c>)
/// ตั้ง OA ของตัวเองไม่ได้ (257-1-7 — OA ของมันคือ OA กลาง ซึ่ง webhook ต้องชี้ <c>/api/line-webhook</c>) แต่ด่าน LINE ผู้ใช้ 1:1 ของผู้ติดต่อเรียก "OA ของบริษัท" เท่านั้น ⇒
/// ไม่มีทางผูก/ส่งเอกสารถึงลูกค้า (ผู้เช่า) ทาง LINE เลย และแท็บ LINE บอกให้ไปตั้งที่ส่วนที่ถูกซ่อน (ทางตัน · กฎ #4A)</para>
/// <list type="bullet">
/// <item><b>ขอบเขต</b>: เฉพาะบริษัทผู้ให้บริการ (ด่านเดียว <see cref="LineGroupBindRule.CentralScopeAllowed"/> — อ่านใหม่ทุกครั้ง · เปลี่ยนบริษัทผู้ให้บริการแล้วปลายทางเดิมหยุดส่ง) ·
/// บริษัทอื่นคง N8 (OA ของบริษัทเท่านั้น)</item>
/// <item><b>ช่องเดียว webhook เดียว</b>: ผูก 1:1 = คำเชิญ scope <c>nextacc</c> → ผู้รับส่งรหัส <c>ผูก LN-…</c> ในแชต OA กลาง → webhook กลาง (<c>/api/line-webhook</c>) ค้นรหัส
/// <b>เฉพาะคำเชิญของบริษัทผู้ให้บริการ</b> · คำเชิญ scope company ไถ่ผ่าน OA กลางไม่ได้ และกลับกัน (userId ของ LINE ใช้ได้เฉพาะ OA ที่ออก)</item>
/// <item>กลุ่มกลางของแอดมินยังห้ามเสมอ (Q1(b) · <c>ContactRecipientRule.GroupProblem</c>) — ไม่แตะ</item>
/// </list>
/// </summary>
public static class ProviderCentralOa
{
    public const string RuleCode = "NOTIFY-PROVIDER-CENTRAL-OA";

    /// <summary>บริษัทนี้ใช้ OA กลางเป็น OA ของบริษัทสำหรับผู้ติดต่อไหม (ตัวเดียวกับกลุ่มรอบ 256)</summary>
    public static bool Allowed(Guid companyId, Guid? platformCompanyId) => LineGroupBindRule.CentralScopeAllowed(companyId, platformCompanyId);

    /// <summary>ขอบเขต OA ของคำเชิญ 1:1 ที่บริษัทนี้ออก</summary>
    public static string InviteScope(bool isProvider) => isProvider ? NotifyEndpointKind.ScopeNextAcc : NotifyEndpointKind.ScopeCompany;

    public const string CentralNoSecret =
        "LINE OA กลางยังไม่มี Channel Secret — webhook กลางตรวจลายเซ็นไม่ได้ จึงรับรหัสผูก LINE ของลูกค้าไม่ได้ (ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง)";

    /// <summary>ช่อง OA กลางพร้อมผูก/ส่ง 1:1 ไหม — null = พร้อม · <paramref name="configProblem"/> = เหตุจากตัวอ่านค่าตั้ง OA กลาง (ถอดรหัสไม่ได้ ฯลฯ)</summary>
    public static string? CentralProblem(bool hasToken, bool hasSecret, string? configProblem)
    {
        if (!hasToken) return string.IsNullOrWhiteSpace(configProblem) ? LineGroupBindRule.Replies.CentralOaNotConfigured : configProblem!.Trim();
        if (!hasSecret) return CentralNoSecret;
        return null;
    }

    /// <summary>คำเชิญนี้ไถ่ผ่าน OA ที่รับข้อความได้ไหม (ขอบเขตต้องตรงกัน · คำเชิญก่อนรอบนี้ไม่มีค่า = company)</summary>
    public static bool InviteMatchesChannel(string? inviteScope, string webhookScope)
        => string.Equals(string.IsNullOrWhiteSpace(inviteScope) ? NotifyEndpointKind.ScopeCompany : inviteScope!.Trim(), webhookScope, StringComparison.Ordinal);

    /// <summary>ประโยคบนแท็บ LINE หน้า "ตั้งค่า" บรรทัดส่งเอกสารถึงผู้ติดต่อ (server computes · page displays) — บริษัทผู้ให้บริการไม่อ้างส่วนที่ถูกซ่อน</summary>
    public static string ContactSendNotice(bool isProvider, string? centralBasicId)
        => isProvider
            ? "ส่งเอกสารถึงผู้ติดต่อทาง LINE (ใบแจ้งหนี้ค่าบริการ · ทวงชำระ · ใบเสร็จ · ตารางส่ง) — บริษัทนี้เป็นบริษัทผู้ให้บริการ จึงส่งผ่าน LINE OA กลางของ NextAcc"
              + (string.IsNullOrWhiteSpace(centralBasicId) ? "" : $" ({centralBasicId!.Trim()})")
              + " ซึ่งเป็น OA ของบริษัทนี้อยู่แล้ว ไม่ต้องตั้ง OA เพิ่ม · ลูกค้าผูก LINE ได้ 2 ทาง: (1) หน้าผู้ติดต่อ → แท็บช่องทางแจ้งเตือน → \"ลิงก์/QR ผูก LINE\" แล้วส่งลิงก์ให้ลูกค้า "
              + "(2) ลูกค้ากดเองที่หน้า \"ศูนย์บิลค่าบริการ\" ของบริษัทเขา → \"รับใบแจ้งหนี้ทาง LINE\" — ลูกค้ากดยินยอมบนมือถือของตัวเองแล้วส่งรหัสในแชต OA กลาง"
            : "ส่งเอกสารถึงผู้ติดต่อทาง LINE (ใบแจ้งหนี้ · ทวงชำระ · รายการประจำ ตามตารางส่ง) — ต้องใช้ LINE OA ของร้านเอง (ตั้งที่ \"ตั้งค่าขั้นสูง\" ด้านล่าง)";

    /// <summary>ข้อเท็จจริงของแถวความพร้อม (ผู้อ่าน: <c>LineConfigController</c>)</summary>
    public sealed record ReadinessFacts(bool CentralHasToken, bool CentralHasSecret, string? CentralConfigProblem, string? CentralBasicId,
        int BoundContacts, int ContactsTotal, bool BillingLineEnabled, bool BillingEmailEnabled)
    {
        /// <summary>รอบ 262 ฝ่ายค้าน L2: Bot User ID ที่ตั้งไว้ในหน้าแอดมิน (ใช้เทียบตอนผูก) · ค่าที่ LINE ตอบจาก token (null = ถาม LINE ไม่ได้/ไม่มี token)</summary>
        public string? CentralBotConfigured { get; init; }
        public string? CentralBotFetched { get; init; }
    }

    /// <summary>แถวความพร้อม "ส่งเอกสารถึงผู้ติดต่อผ่าน LINE OA กลาง" ของบริษัทผู้ให้บริการ (server computes · หน้าแสดงตรงตัว) · บริษัทอื่น ⇒ ว่าง</summary>
    public static IReadOnlyList<LineOaHealth.Row> ReadinessRows(bool isProvider, ReadinessFacts f)
    {
        if (!isProvider) return Array.Empty<LineOaHealth.Row>();
        var rows = new List<LineOaHealth.Row>();
        var problem = CentralProblem(f.CentralHasToken, f.CentralHasSecret, f.CentralConfigProblem);
        rows.Add(problem == null
            ? new LineOaHealth.Row("central-oa", LineOaHealth.LevelOk, "LINE OA กลางพร้อม (Channel Access Token + Channel Secret)", null)
            : new LineOaHealth.Row("central-oa", LineOaHealth.LevelError, "LINE OA กลางยังไม่พร้อม — ลูกค้าผูก LINE/รับเอกสารทาง LINE ไม่ได้", problem));
        rows.Add(string.IsNullOrWhiteSpace(f.CentralBasicId)
            ? new LineOaHealth.Row("central-basic-id", LineOaHealth.LevelWarn, "ยังไม่ได้ใส่ Basic ID ของ LINE OA กลาง",
                "หน้าผูก LINE ของลูกค้าจะไม่มีปุ่ม \"เพิ่มเพื่อน\"/\"ส่งรหัส\" — ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง → Basic ID")
            : new LineOaHealth.Row("central-basic-id", LineOaHealth.LevelOk, "Basic ID ของ LINE OA กลาง: " + f.CentralBasicId!.Trim(), null));
        rows.Add(BotRow(f.CentralBotConfigured, f.CentralBotFetched, f.CentralHasToken));
        rows.Add(new LineOaHealth.Row("central-webhook", LineOaHealth.LevelOk, "Webhook ของ OA กลางต้องชี้ /api/line-webhook",
            "การผูก LINE ของลูกค้าเดินผ่าน webhook กลางตัวเดียว — ห้ามเปลี่ยน Webhook ของ OA กลางใน LINE Developers (257-1-7)"));
        rows.Add(f.BoundContacts > 0
            ? new LineOaHealth.Row("bound", LineOaHealth.LevelOk, $"ลูกค้าที่ผูก LINE แล้ว {f.BoundContacts:N0} ราย (จากผู้ติดต่อ {f.ContactsTotal:N0} ราย)", null)
            : new LineOaHealth.Row("bound", LineOaHealth.LevelWarn, "ยังไม่มีลูกค้าผูก LINE กับ OA กลาง",
                "หน้าผู้ติดต่อ → แท็บช่องทางแจ้งเตือน → \"ลิงก์/QR ผูก LINE\" หรือให้ลูกค้ากด \"รับใบแจ้งหนี้ทาง LINE\" ที่ศูนย์บิลค่าบริการของเขา"));
        rows.Add(f.BillingLineEnabled
            ? new LineOaHealth.Row("billing-line", LineOaHealth.LevelOk, "แจ้งใบแจ้งหนี้/ทวง/ใบเสร็จค่าบริการทาง LINE: เปิด"
                + (f.BillingEmailEnabled ? " (อีเมลเปิดด้วย — ลูกค้าได้ทั้งสองช่องตามค่าตั้ง)" : ""), null)
            : new LineOaHealth.Row("billing-line", LineOaHealth.LevelWarn, "แจ้งใบแจ้งหนี้/ทวง/ใบเสร็จค่าบริการทาง LINE: ปิดอยู่",
                "เปิดที่ หน้าแอดมิน → เรียกเก็บค่าบริการ → ค่าตั้งการเรียกเก็บค่าบริการ → \"ส่งใบแจ้งหนี้/แจ้งเตือนทาง LINE\""));
        return rows;
    }

    /// <summary>รอบ 262 ฝ่ายค้าน L2 (คำตัดสิน 262-1-14): Bot User ID ที่ตั้งไว้ต้องตรงบอทของ token — ตั้งผิด ⇒ ผูกทุกครั้งตอบ "เปลี่ยนบัญชี" + ปลายทาง nextacc ทั้งหมด "ต้องผูกใหม่"</summary>
    internal static LineOaHealth.Row BotRow(string? configured, string? fetched, bool hasToken)
    {
        var c = (configured ?? "").Trim();
        var f = (fetched ?? "").Trim();
        if (c.Length == 0)
            return new("central-bot", LineOaHealth.LevelWarn, "ยังไม่ได้ใส่ Bot User ID ของ LINE OA กลาง",
                "ระบบไม่ตรวจว่าบอทของ OA กลางเปลี่ยนหรือไม่ — ใส่ที่ ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง → Bot User ID (ค่าที่ขึ้นต้นด้วย U)");
        if (!hasToken || f.Length == 0)
            return new("central-bot", LineOaHealth.LevelWarn, "ตรวจ Bot User ID กับ LINE ไม่ได้ตอนนี้", "LINE ไม่ตอบ หรือยังไม่มี token ของ OA กลาง — ลองโหลดหน้านี้ใหม่");
        return string.Equals(c, f, StringComparison.Ordinal)
            ? new("central-bot", LineOaHealth.LevelOk, "Bot User ID ของ LINE OA กลางตรงกับบอทของ token", null)
            : new("central-bot", LineOaHealth.LevelError, "Bot User ID ของ LINE OA กลางไม่ตรงกับบอทของ token",
                $"ค่าที่ตั้ง {c} ≠ บอทจริง {f} — ลูกค้าผูก LINE ทุกครั้งจะได้ \"บริษัทเปลี่ยนบัญชี LINE\" · แก้ Bot User ID ที่ ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์ → LINE OA กลาง");
    }

    /// <summary>แจ้งเตือนค่าบริการ (ชนิดจาก <see cref="PlatformDunningPolicy.KindOfKey"/>) → trigger ของการ์ดเอกสารในคิว LINE ของผู้ติดต่อ ·
    /// null = ไม่ใช่เรื่องของใบ (ผลตัดบัตร ฯลฯ — ส่งตรงถึงเจ้าของเท่านั้น)</summary>
    public static string? TriggerForKind(string? kind) => kind switch
    {
        PlatformDunningPolicy.KindIssued => "DocumentApproved",
        PlatformDunningPolicy.KindBefore or PlatformDunningPolicy.KindDue => "DocumentDueSoon",
        PlatformDunningPolicy.KindAfter => "DocumentOverdue",
        _ => null,
    };

    /// <summary>กุญแจเหตุการณ์ของการ์ดค่าบริการ (ต่อใบ + ต่อกุญแจข้อความ ⇒ ขั้นทวงแต่ละขั้นคนละการ์ด · ขั้นเดิมซ้ำไม่เข้าคิว)</summary>
    public static string QueueBaseKey(Guid documentId, string trigger, string messageKey) => QueueBasePrefix(documentId, trigger) + messageKey;

    /// <summary>รอบ 262 ฝ่ายค้าน M3/L1: ส่วนต้นของกุญแจการ์ดค่าบริการ — แยก "การ์ดของงานนี้" ออกจาก "การ์ดของกฎตารางส่ง" ในตัวกันซ้ำทั้งสองทาง</summary>
    public static string QueueBasePrefix(Guid documentId, string trigger) => $"doc:{documentId}:{trigger}:platform:";

    /// <summary>ปลายทางเดียวกันได้การ์ด trigger เดียวกันของใบนี้จากทางอื่นแล้ว (เช่นกฎตารางส่งของบริษัทผู้ให้บริการ) ภายในช่วงนี้ ⇒ ไม่เข้าคิวซ้ำ ·
    /// ใบใหม่/ใบเสร็จ = ครั้งเดียวต่อใบตลอดไป (null) · ทวงชำระ = ภายใน 20 ชม. (ขั้นทวงคนละวันยังได้การ์ดของขั้นนั้น)</summary>
    public static TimeSpan? DuplicateWindow(string trigger) => trigger is "DocumentDueSoon" or "DocumentOverdue" ? TimeSpan.FromHours(20) : null;

    /// <summary>กุญแจข้อความของใบเสร็จค่าบริการ (หนึ่งครั้งต่อใบ)</summary>
    public static string ReceiptMessageKey(Guid receiptDocumentId) => $"rcpt:{receiptDocumentId:N}";

    /// <summary>เจ้าของที่ LINE id ตรงปลายทางที่ได้การ์ดทางคิวแล้ว ⇒ ไม่ส่งข้อความตรงซ้ำ (ช่องเดียว — 262-1-5) · เทียบตรงตัวหลังตัดช่องว่าง</summary>
    public static IReadOnlyList<T> OwnersNotCovered<T>(IEnumerable<T> owners, Func<T, string?> lineId, IReadOnlyCollection<string> coveredLineIds)
        => owners.Where(o => !(lineId(o) is string id && id.Trim().Length > 0 && coveredLineIds.Contains(id.Trim()))).ToList();

    /// <summary>ปุ่มชำระของการ์ดใบแจ้งหนี้ค่าบริการ — ไป <b>ศูนย์บิลค่าบริการ</b> ของผู้เช่าเท่านั้น (เส้นชำระของแพลตฟอร์ม: เงินเข้าบัญชีของระบบ ·
    /// ต่ออายุแพ็กเกจ/ตัดชำระผ่าน <c>SubscriptionPayment</c>) ไม่ใช่ลิงก์ชำระเอกสารทั่วไปของบริษัท · ผู้ใช้ไม่ติ๊กลิงก์ชำระ (ส่งเอง) ⇒ ไม่มีปุ่ม</summary>
    public static string? BillingPayUrl(bool? includePayLink, string? billingCenterUrl)
        => includePayLink == false || string.IsNullOrWhiteSpace(billingCenterUrl) ? null : billingCenterUrl;

    /// <summary>รอบ 262 ฝ่ายค้าน L4: ทางศูนย์บิล<b>ของบริษัทผู้ซื้อ</b> (<c>?company=</c> — layout.js สลับบริษัทตามลิงก์) · ผู้ใช้หลายบริษัทไม่เจอศูนย์บิลของบริษัทที่เปิดค้างไว้</summary>
    public static string BillingPath(Guid buyerCompanyId) => $"/pages/billing.html?company={buyerCompanyId:D}";

    /// <summary>รอบ 262 ฝ่ายค้าน L3 (คำตัดสิน 262-1-15): คำตอบ "หยุด" บน OA กลาง — บอกว่าหยุดการรับของผู้ติดต่อใดบ้าง (หยุดทุกรายที่ LINE นี้ผูกไว้กับบริษัทผู้ให้บริการ) + ทางกลับมารับ</summary>
    public static string StopScopeNote(IReadOnlyCollection<string> contactNames)
        => contactNames.Count == 0 ? ""
            : $"\nหยุดการรับของ: {string.Join(" · ", contactNames.Take(10))}{(contactNames.Count > 10 ? $" และอีก {contactNames.Count - 10} ราย" : "")}"
              + " — ต้องการรับของบางรายต่อ ให้ผู้ส่งเอกสาร (หรือศูนย์บิลค่าบริการของบริษัทนั้น) ส่งลิงก์ผูกใหม่";

    /// <summary>รอบ 262 ฝ่ายค้าน M1: ป้ายปลายทางบนศูนย์บิลของผู้เช่า — ชื่อโปรไฟล์ LINE แบบปิดบัง (ตัวเดียวกับ audit) · ไม่มีชื่อ ⇒ "บัญชี LINE"</summary>
    public static string MaskedEndpointLabel(string? label) => ContactLineBindRule.MaskName(label) ?? "บัญชี LINE";

    /// <summary>รอบ 262 ฝ่ายค้าน L5: เพดานสร้างลิงก์ผูก LINE จากศูนย์บิลต่อบริษัทผู้เช่า (แต่ละครั้งเขียน audit ในบริษัทผู้ให้บริการ + เพิกถอนลิงก์เดิม)</summary>
    public const int BillingInvitePerMinute = 2, BillingInvitePerDay = 10;
    public static string BillingInviteRateKey(Guid buyerCompanyId) => $"billing-line-invite:{buyerCompanyId:N}";

    /// <summary>รอบ 262 ฝ่ายค้าน H1 (คำตัดสิน 262-1-9 · F2 ข้อ 9): ลิงก์ชำระทั่วไปที่ออกให้ใบค่าบริการของผู้เช่าไว้ก่อนรอบนี้ ⇒ เพิกถอน (idempotent · รันทุกบูต ·
    /// ตอนใช้งานถูกปฏิเสธด้วย <c>DocumentPayLinkPolicy.PlatformTenantRefusal</c> อยู่แล้ว — นี่คือการล้างของค้างให้สถานะบนหน้าเอกสารตรงจริง)</summary>
    public const string RevokeTenantPayLinksSql = """
        UPDATE "Documents" d SET "PayLinkRevokedAt" = (now() at time zone 'utc')
        WHERE d."PayLinkTokenHash" IS NOT NULL AND d."PayLinkRevokedAt" IS NULL
          AND d."CompanyId" = (SELECT s."PlatformCompanyId" FROM "SiteSettings" s ORDER BY s."CreatedAt" LIMIT 1)
          AND EXISTS (SELECT 1 FROM "Contacts" c WHERE c."Id" = d."ContactId" AND c."CompanyId" = d."CompanyId"
                      AND c."ExternalSystem" = 'NextAccTenant' AND c."ExternalId" IS NOT NULL);
        """;

    /// <summary>สถานะกล่อง "รับใบแจ้งหนี้ทาง LINE" บนศูนย์บิลของผู้เช่า (server computes)</summary>
    public sealed record BillingLineView(bool Available, string? Reason, int BoundCount, string Note)
    {
        /// <summary>รอบ 262 ฝ่ายค้าน M1: รายการปลายทางที่ผูกอยู่ (ยกเลิกได้จากศูนย์บิล)</summary>
        public IReadOnlyList<BillingLineEndpoint> Endpoints { get; init; } = Array.Empty<BillingLineEndpoint>();
    }

    /// <summary>รอบ 262 ฝ่ายค้าน M1: ปลายทางหนึ่งตัวบนศูนย์บิล (ป้ายปิดบัง · ไม่มี LINE id)</summary>
    public sealed record BillingLineEndpoint(Guid Id, string Label, DateTime LinkedAt, DateTime? LastSentAt, string Status);

    public static BillingLineView BillingView(bool providerSet, bool contactExists, string? centralProblem, int boundCount, bool canManage)
    {
        const string note = "รับใบแจ้งหนี้ ทวงชำระ และใบเสร็จค่าบริการ NextAcc ทาง LINE (ผ่าน LINE OA กลางของ NextAcc) — กดสร้างลิงก์แล้วเปิดบนมือถือที่ใช้ LINE · กดยินยอม · "
                            + "ส่งรหัสในแชต · ยกเลิกได้ทุกเมื่อโดยพิมพ์ \"หยุด\" ในแชตนั้น";
        if (!providerSet) return new(false, "ระบบยังไม่ได้ตั้งบริษัทผู้ให้บริการ — ยังส่งใบแจ้งหนี้ค่าบริการทาง LINE ไม่ได้", boundCount, note);
        if (!contactExists) return new(false, "ยังไม่มีใบแจ้งหนี้ค่าบริการของบริษัทนี้ — ผูก LINE ได้หลังออกใบแจ้งหนี้ใบแรก", boundCount, note);
        if (centralProblem != null) return new(false, centralProblem, boundCount, note);
        if (!canManage) return new(false, "ต้องมีสิทธิ์ \"จัดการค่าใช้จ่าย/ส่วนเสริม\" จากเจ้าของบริษัท", boundCount, note);
        return new(true, null, boundCount, note);
    }
}
