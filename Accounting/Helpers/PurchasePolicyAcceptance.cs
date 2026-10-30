using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านเดียวของ "ติ๊กยอมรับข้อกำหนด + นโยบายการยกเลิกและคืนเงินก่อนชำระเงิน"** — รอบ 219 ส่วน B · คำตัดสินข้อ 179
///
/// <para><b>ที่มา</b>: เจ้าของตัดสินว่าทุกหน้าชำระเงิน/ยืนยันการซื้อ (ค่าบริการ NextAcc · ออเดอร์/การจอง/ที่พักบนเว็บร้าน) ต้องมีช่องติ๊ก
/// "ยอมรับนโยบาย" แบบบังคับ และเก็บฉบับที่ยอมรับไว้ — ก่อนหน้านี้หน้าจ่ายของแพลตฟอร์มไม่แสดงนโยบายเลย และเช็กเอาต์ของร้านมีแค่ช่องยินยอม PDPA</para>
///
/// <para><b>กติกา</b> (เซิร์ฟเวอร์ตัดสิน — หน้าเว็บแสดงข้อความ/ฉบับ/ลิงก์ที่ได้จากเซิร์ฟเวอร์เท่านั้น):
/// <list type="bullet">
/// <item>ผู้ซื้อทำเอง (ลูกค้าบนเว็บร้าน · ผู้ใช้ที่ชำระค่าบริการ NextAcc) ⇒ <b>บังคับเสมอ</b> · ไม่ติ๊ก = ปฏิเสธก่อนบันทึก (<see cref="MissingMessage"/>)</item>
/// <item>ยกเว้น: พนักงานของร้านบันทึกแทนลูกค้า (ออเดอร์/การจองจากหลังบ้าน · walk-in/โทรศัพท์) — ยอมรับนอกระบบ ไม่แต่งหลักฐานให้</item>
/// <item>หน้าเว็บส่งฉบับที่แสดงมาด้วย แล้ว<b>ไม่ตรงฉบับปัจจุบัน</b> ⇒ ปฏิเสธ (<see cref="StaleMessage"/>) — ผู้ซื้อต้องเห็นฉบับที่ใช้จริง ·
/// ไม่ส่งฉบับมา (ผู้เรียกแบบเก่าที่ติ๊กแล้ว) ⇒ ยอมรับฉบับปัจจุบัน (<c>VersionShown = null</c> ในหลักฐาน)</item>
/// <item>ผ่าน ⇒ <see cref="Create"/> แถว <see cref="PolicyAcceptance"/> (append-only) พร้อม hash <see cref="PdpaConsentEvidence.ComputeHash"/> —
/// ผู้เรียกบันทึกพร้อมแถวต้นทางในคำสั่งเดียว</item>
/// </list></para>
/// </summary>
public static class PurchasePolicyAcceptance
{
    public const string RuleCode = "CONSUMER-PURCHASE-POLICY-ACCEPT";
    public const string LegalReference = "พ.ร.บ.คุ้มครองผู้บริโภค 2522 · พ.ร.บ.ว่าด้วยข้อสัญญาที่ไม่เป็นธรรม 2540 (ภาระพิสูจน์เงื่อนไขที่ผู้ซื้อยอมรับ)";
    /// <summary>วัตถุประสงค์ใน canonical ของหลักฐาน (แยกจากความยินยอม PDPA ทุกตัว)</summary>
    public const string Purpose = "purchase:terms-and-refund";

    public const string ScopeShop = "shop";
    public const string ScopePlatform = "platform";

    public const string ChannelSiteOrder = "site-order";
    public const string ChannelSiteBooking = "site-booking";
    public const string ChannelLodging = "lodging";
    public const string ChannelPlatformSubscription = "platform-subscription";
    public const string ChannelPlatformAddOn = "platform-addon";
    /// <summary>ใบแจ้งหนี้/บิลค่าบริการของแพลตฟอร์ม (ศูนย์ชำระค่าบริการของทีมเฟส 0 เรียกใช้ด้วยช่องนี้)</summary>
    public const string ChannelPlatformInvoice = "platform-invoice";
    public const string ChannelPlatformTopUp = "platform-topup";

    public const string MissingMessage =
        "กรุณาติ๊กยอมรับข้อกำหนดและนโยบายการยกเลิก/คืนเงินก่อนดำเนินการต่อ (อ่านนโยบายได้จากลิงก์ข้างช่องติ๊ก)";
    public const string StaleMessage =
        "นโยบายมีการปรับปรุงระหว่างที่ท่านเปิดหน้านี้ — กรุณาโหลดหน้าใหม่ อ่านนโยบายฉบับล่าสุด แล้วติ๊กยอมรับอีกครั้ง";

    /// <summary>ข้อความช่องติ๊กของแพลตฟอร์ม — อ้างชื่อเอกสาร + ฉบับที่เผยแพร่จริง (ฝ่ายค้าน: ไม่สรุปเนื้อหาเอง — แอดมินแก้นโยบายแล้วข้อความต้องไม่ขัด)</summary>
    public static string PlatformText(string version)
        => $"ข้าพเจ้าได้อ่านและยอมรับ{LegalDocumentDefaults.Title(LegalDocumentDefaults.KindTerms)} และ{LegalDocumentDefaults.Title(LegalDocumentDefaults.KindRefund)}ของ NextAcc (ฉบับ {version})";

    /// <summary>ข้อความช่องติ๊กของร้าน — ร้านตั้งเอง ⇒ ข้อความนั้น · ว่าง ⇒ ข้อความมาตรฐาน</summary>
    private static string ShopText(string? siteText, string? siteName)
        => !string.IsNullOrWhiteSpace(siteText) ? siteText.Trim()
            : $"ข้าพเจ้าได้อ่านและยอมรับข้อกำหนดการใช้บริการ และนโยบายการยกเลิกและคืนเงินของ {(string.IsNullOrWhiteSpace(siteName) ? "ร้าน" : siteName!.Trim())}";

    /// <summary>ด่าน — null = ผ่าน · ข้อความไทย = ปฏิเสธก่อนบันทึก (pure)</summary>
    /// <param name="exempt">พนักงานบันทึกแทนลูกค้า (ไม่ใช่ผู้ซื้อทำเอง)</param>
    public static string? Problem(bool exempt, bool accepted, string? versionShown, string currentVersion)
    {
        if (exempt) return null;
        if (!accepted) return MissingMessage;
        if (!string.IsNullOrWhiteSpace(versionShown) && versionShown.Trim() != currentVersion) return StaleMessage;
        return null;
    }

    /// <summary><b>ผู้เขียนแถวหลักฐานตัวเดียว</b> — เวลา normalize ก่อน hash และเก็บค่าเดียวกัน (ตรวจกลับหลังผ่าน PostgreSQL ได้ — บทเรียนรอบ 214 ฎ) ·
    /// ตัดความยาว IP/UA ก่อน hash (ค่าที่เก็บ = ค่าที่ hash)</summary>
    public static PolicyAcceptance Create(Guid companyId, string scope, string channel, Guid? sourceId, Guid? siteId,
        string policyVersion, string? versionShown, string? subject, Guid? userId, DateTime nowUtc, string? ip, string? userAgent)
    {
        var at = PdpaConsentEvidence.NormalizeTimestamp(nowUtc);
        var ipT = string.IsNullOrWhiteSpace(ip) ? null : (ip.Length > PlatformPolicyAcceptanceRules.MaxIp ? ip[..PlatformPolicyAcceptanceRules.MaxIp] : ip);
        var uaT = string.IsNullOrEmpty(userAgent) ? null
            : (userAgent.Length > PlatformPolicyAcceptanceRules.MaxUserAgent ? userAgent[..PlatformPolicyAcceptanceRules.MaxUserAgent] : userAgent);
        var shown = string.IsNullOrWhiteSpace(versionShown) ? null : versionShown.Trim();
        if (shown is { Length: > 100 }) shown = shown[..100];
        var subj = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        if (subj is { Length: > 256 }) subj = subj[..256];
        var row = new PolicyAcceptance
        {
            CompanyId = companyId,
            Scope = scope,
            Channel = channel,
            SourceId = sourceId,
            SiteId = siteId,
            PolicyVersion = policyVersion,
            VersionShown = shown,
            Subject = subj,
            UserId = userId,
            AcceptedAt = at,
            IpAddress = ipT,
            UserAgent = uaT,
            CreatedBy = userId?.ToString() ?? scope,
        };
        Seal(row);
        return row;
    }

    /// <summary>ฝ่ายค้าน S7: canonical ของช่องทางรวม <b>บริษัท · แถวต้นทาง · ฉบับที่เซิร์ฟเวอร์ใช้</b> — ย้ายหลักฐานไปผูกออเดอร์/บริษัทอื่น หรือแก้ฉบับ ⇒ ตรวจไม่ผ่าน</summary>
    private static string CanonicalChannel(PolicyAcceptance r)
        => $"{r.Scope}:{r.Channel}|{r.CompanyId:N}|{(r.SourceId is Guid s ? s.ToString("N") : "")}|{r.PolicyVersion}";

    private static void Seal(PolicyAcceptance r)
        => r.EvidenceHash = PdpaConsentEvidence.ComputeHash(r.Subject, Purpose, r.VersionShown ?? r.PolicyVersion, r.AcceptedAt,
            CanonicalChannel(r), r.IpAddress, r.UserAgent);

    /// <summary>ผูกแถวต้นทางที่เพิ่งได้ id (การจอง/การจองที่พัก) แล้วประทับ hash ใหม่ — ทางเดียวที่แก้ SourceId (ก่อนบันทึกครั้งแรกเท่านั้น)</summary>
    public static void BindSource(PolicyAcceptance? r, Guid sourceId)
    {
        if (r == null) return;
        r.SourceId = sourceId;
        Seal(r);
    }

    /// <summary>ผู้ส่งเป็นสมาชิกที่ยังทำหน้าที่ได้ของบริษัท (บันทึกแทนลูกค้า) — ตัวตัดสินเดียว (<see cref="CompanyMembership.IsActiveMemberAsync"/>)</summary>
    public static async Task<bool> IsStaffAsync(AccountingDbContext db, Guid companyId, string? actorUserId, CancellationToken ct = default)
        => Guid.TryParse(actorUserId, out var uid) && uid != Guid.Empty && await CompanyMembership.IsActiveMemberAsync(db, companyId, uid, ct);

    // ═══════════════ PDPA (ฝ่ายค้าน S4) ═══════════════

    /// <summary>เก็บกี่ปี — MAX(เอกสารบัญชี/รายงานภาษี 5 ปี · พ.ร.บ.การบัญชี ม.10 / §87/3 · อายุความสัญญาทั่วไป 10 ปี ป.พ.พ. ม.193/30) = 10 ปีนับแต่วันยอมรับ</summary>
    public const int RetentionYears = 10;

    /// <summary>แถวที่ยอมรับก่อนเวลานี้พ้นอายุเก็บแล้ว (ลบจริงได้)</summary>
    public static DateTime PurgeCutoff(DateTime nowUtc) => nowUtc.AddYears(-RetentionYears);

    /// <summary>ลบตามคำขอ (ม.33): หลักฐานการยอมรับเงื่อนไขซื้อขายเป็นภาระพิสูจน์ของผู้ขาย ⇒ legal hold — คงแถว (ฉบับ · เวลา · ช่องทาง · hash)
    /// แต่ทำผู้ยอมรับ/IP/UA ไม่ให้ระบุตัว · วันลบครั้งแรกไม่เลื่อน · ตรวจ hash ซ้ำไม่ได้แล้วโดยเจตนา</summary>
    public static void Pseudonymise(PolicyAcceptance r, string anonymisedSubject, DateTime nowUtc)
    {
        r.Subject = anonymisedSubject;
        r.IpAddress = null;
        r.UserAgent = null;
        r.PseudonymisedAt ??= nowUtc;
    }

    /// <summary>ป้ายผลตรวจหลักฐานรายแถว (รายงาน DSR) — แถวที่ทำไม่ให้ระบุตัวแล้วตรวจไม่ได้โดยเจตนา</summary>
    public static string CheckLabel(PolicyAcceptance r)
        => r.PseudonymisedAt != null ? "ทำไม่ให้ระบุตัวแล้ว (ลบตามคำขอ) — ตรวจซ้ำไม่ได้โดยเจตนา"
            : Verify(r) ? "หลักฐานตรงกับค่าที่เก็บ" : "หลักฐานไม่ตรง — แถวอาจถูกแก้";

    /// <summary>ตรวจหลักฐานกลับ (round-trip) — ใช้ canonical ตัวเดียวกับ <see cref="Create"/></summary>
    public static bool Verify(PolicyAcceptance row)
        => PdpaConsentEvidence.Check(row.EvidenceHash, row.Subject, Purpose, row.VersionShown ?? row.PolicyVersion, row.AcceptedAt,
               CanonicalChannel(row), row.IpAddress, row.UserAgent) == PdpaConsentEvidence.EvidenceCheck.Valid;

    // ═══════════════ ร้านค้า (เว็บ CMS) ═══════════════

    /// <summary>สิ่งที่หน้าเช็กเอาต์/จองของร้านต้องแสดง + ฉบับที่ด่านเทียบ — มาจากแถวเว็บ + ค่าตั้งมัดจำ (กรองบริษัท)</summary>
    public sealed record ShopPolicyFacts(string Version, string Text, CmsLegalPages.Link Terms, CmsLegalPages.Link Refund);

    /// <summary>คำนวณจากค่าที่โหลดแล้ว (pure — หน้า storefront และด่านใช้ตัวเดียวกัน)</summary>
    private static ShopPolicyFacts ShopFacts(string? siteName, string? acceptanceText, string? termsUrl, string? termsMarkdown,
        string? refundUrl, string? refundMarkdown, decimal? forfeitPercent, int? returnWindowDays, int? refundProcessingDays)
        => new(
            CmsLegalPages.ShopPolicyVersion(termsUrl, termsMarkdown, refundUrl, refundMarkdown, forfeitPercent, returnWindowDays, refundProcessingDays),
            ShopText(acceptanceText, siteName),
            CmsLegalPages.Resolve(LegalDocumentDefaults.KindTerms, termsUrl, termsMarkdown),
            CmsLegalPages.Resolve(LegalDocumentDefaults.KindRefund, refundUrl, refundMarkdown));

    /// <summary>โหลดข้อเท็จจริงนโยบายของเว็บ (CompanyId scope) — ไม่พบเว็บ ⇒ แม่แบบล้วน (การจองที่พักที่ไม่ผูกเว็บ)</summary>
    public static async Task<ShopPolicyFacts> LoadShopAsync(AccountingDbContext db, Guid companyId, Guid? siteId, CancellationToken ct = default)
    {
        if (siteId is not Guid sid)
            return ShopFacts(null, null, null, null, null, null, null, null, null);
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == sid && s.CompanyId == companyId)
            .Select(s => new
            {
                s.Name, s.PolicyAcceptanceText, s.TermsOfServiceUrl, s.TermsOfServiceMarkdown, s.RefundPolicyUrl, s.RefundPolicyMarkdown,
                s.RefundReturnWindowDays, s.RefundProcessingDays,
            })
            .FirstOrDefaultAsync(ct);
        var forfeit = await db.SiteCommerceConfigs.AsNoTracking()
            .Where(c => c.SiteId == sid && c.CompanyId == companyId)
            .Select(c => c.DepositForfeitOnCancelPercent).FirstOrDefaultAsync(ct);
        return site == null
            ? ShopFacts(null, null, null, null, null, null, forfeit, null, null)
            : ShopFacts(site.Name, site.PolicyAcceptanceText, site.TermsOfServiceUrl, site.TermsOfServiceMarkdown, site.RefundPolicyUrl,
                site.RefundPolicyMarkdown, forfeit, site.RefundReturnWindowDays, site.RefundProcessingDays);
    }

    /// <summary>
    /// <b>ด่านก่อนบันทึก</b>ของทางเข้าสั่งซื้อ/จองบนเว็บร้าน — ไม่ผ่าน ⇒ <see cref="BusinessRuleException"/> (ผู้เรียกยังไม่ได้บันทึกอะไร) ·
    /// ผ่าน (และไม่ใช่พนักงาน) ⇒ เพิ่มแถวหลักฐานใน context (บันทึกพร้อม SaveChanges ของผู้เรียก) แล้วคืนแถวนั้น
    /// </summary>
    /// <param name="exempt">ผู้ส่งเป็นพนักงานของบริษัท/บันทึกแทนลูกค้า</param>
    public static async Task<PolicyAcceptance?> EnforceShopAsync(AccountingDbContext db, Guid companyId, Guid? siteId, string channel,
        bool accepted, string? versionShown, bool exempt, string? email, string? phone, Guid? sourceId,
        string? ip = null, string? userAgent = null, CancellationToken ct = default)
    {
        if (exempt) return null;
        var facts = await LoadShopAsync(db, companyId, siteId, ct);
        if (Problem(false, accepted, versionShown, facts.Version) is string problem)
            throw new BusinessRuleException(problem, RuleCode);
        var subject = CmsContactResolver.NormalizeEmail(email) ?? CmsContactResolver.NormalizePhone(phone);
        var row = Create(companyId, ScopeShop, channel, sourceId, siteId, facts.Version, versionShown, subject, null, DateTime.UtcNow, ip, userAgent);
        db.Set<PolicyAcceptance>().Add(row);
        return row;
    }

    // ═══════════════ แพลตฟอร์ม (ค่าบริการ NextAcc) ═══════════════

    /// <summary>
    /// <b>ด่านก่อนเริ่มชำระค่าบริการ NextAcc</b> (แพ็กเกจ · ส่วนเสริม · แพ็ก · ใบแจ้งหนี้แพลตฟอร์ม) — บังคับเสมอ ·
    /// ผ่าน ⇒ เพิ่มแถวหลักฐาน (CompanyId = บริษัทผู้จ่าย) · ผู้เรียกบันทึก · <b>ทีมศูนย์ชำระค่าบริการเรียกตัวนี้</b>ด้วย <see cref="ChannelPlatformInvoice"/>
    /// <para>ผู้ซื้อยอมรับฉบับปัจจุบันไว้แล้วสำหรับรายการเดียวกัน (ติ๊กตอนขอแพ็กเกจ แล้วมาแนบสลิป/จ่ายออนไลน์ทีหลัง) ⇒ ไม่ต้องติ๊กซ้ำ
    /// (คืน null — ไม่เพิ่มแถว) · ฉบับเปลี่ยนระหว่างนั้น ⇒ ต้องติ๊กใหม่</para>
    /// </summary>
    public static async Task<PolicyAcceptance?> EnforcePlatformAsync(AccountingDbContext db, ILegalDocumentService legal,
        Guid payerCompanyId, Guid userId, string channel, Guid? sourceId, bool accepted, string? versionShown,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var current = await legal.GetPurchasePolicyVersionAsync(ct);
        if (!accepted && sourceId is Guid sid && await HasPlatformAcceptanceAsync(db, payerCompanyId, channel, sid, current.Version, ct))
            return null;
        if (Problem(false, accepted, versionShown, current.Version) is string problem)
            throw new BusinessRuleException(problem, RuleCode);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        var row = Create(payerCompanyId, ScopePlatform, channel, sourceId, null, current.Version, versionShown,
            email ?? userId.ToString(), userId, DateTime.UtcNow, ip, userAgent);
        db.Set<PolicyAcceptance>().Add(row);
        return row;
    }

    /// <summary>ฝ่ายค้าน S6: SystemAdmin ที่<b>ไม่ใช่สมาชิก</b>ของบริษัทผู้จ่าย (ทำแทนผู้เช่า — เข้าดูในนาม/ช่วยตั้งค่า) ไม่ใช่ผู้ซื้อ ⇒ ไม่บังคับติ๊ก และไม่แต่งหลักฐาน ·
    /// สมาชิกจริง (รวมแอดมินที่เป็นสมาชิก) ⇒ บังคับตามปกติ</summary>
    public static async Task<bool> IsPlatformAdminActingAsync(AccountingDbContext db, Guid payerCompanyId, Guid userId, bool isSystemAdmin,
        CancellationToken ct = default)
        => isSystemAdmin && !await CompanyMembership.IsActiveMemberAsync(db, payerCompanyId, userId, ct);

    /// <summary>ทางเข้าจาก controller ตัวเดียว: ผู้ใช้ในคำขอ → (แอดมินทำแทน = ข้าม · ไม่มีแถว) → <see cref="EnforcePlatformAsync"/></summary>
    public static async Task<PolicyAcceptance?> EnforcePlatformForUserAsync(AccountingDbContext db, ILegalDocumentService legal,
        Guid payerCompanyId, System.Security.Claims.ClaimsPrincipal user, string channel, Guid? sourceId, bool accepted, string? versionShown,
        string? ip, string? userAgent, CancellationToken ct = default)
    {
        var userId = JwtHelper.GetUserIdFromClaims(user);
        if (await IsPlatformAdminActingAsync(db, payerCompanyId, userId, user.IsInRole("SystemAdmin"), ct)) return null;
        return await EnforcePlatformAsync(db, legal, payerCompanyId, userId, channel, sourceId, accepted, versionShown, ip, userAgent, ct);
    }

    /// <summary>บริษัทผู้จ่ายยอมรับฉบับนี้ไว้แล้วสำหรับรายการนี้ (กรองบริษัท)</summary>
    private static Task<bool> HasPlatformAcceptanceAsync(AccountingDbContext db, Guid payerCompanyId, string channel, Guid sourceId,
        string version, CancellationToken ct = default)
        => db.Set<PolicyAcceptance>().AsNoTracking().AnyAsync(a => a.CompanyId == payerCompanyId && a.Scope == ScopePlatform
            && a.Channel == channel && a.SourceId == sourceId && a.PolicyVersion == version, ct);

    /// <summary>ข้อมูลที่หน้าชำระค่าบริการแสดง (ข้อความ · ฉบับ · ลิงก์) — ตัวเดียวของทุกหน้า (แพ็กเกจ · ส่วนเสริม · ศูนย์ชำระค่าบริการ)</summary>
    public sealed record PlatformPolicyInfo(string Text, string Version, string TermsUrl, string RefundUrl);

    public static PlatformPolicyInfo PlatformInfo(string version)
        => new(PlatformText(version), version, LegalDocumentDefaults.PlatformPath(LegalDocumentDefaults.KindTerms),
            LegalDocumentDefaults.PlatformPath(LegalDocumentDefaults.KindRefund));
}
