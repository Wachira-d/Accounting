using System.Security.Cryptography;
using System.Text;
using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ความยินยอม PDPA บนฟอร์มหน้าเว็บ (เช็กเอาต์ · จอง · ติดต่อ/lead · ฟอร์มกำหนดเอง)** — รอบ 209 P6 · คำตัดสินข้อ 168 ฑ · R-209-D6/E13/A11
///
/// <para><b>ที่มา</b>: ฟอร์มหน้าเว็บเก็บชื่อ/โทร/อีเมล/ที่อยู่โดยไม่มีช่องยินยอมหรือลิงก์นโยบายเลย (grep consent ใน storefront = 0) ·
/// <c>PdpaConsentRecord</c> มีอยู่แล้ว (ม.19/22) แต่ไม่มีทางเข้าจากเว็บ</para>
///
/// <para><b>กติกา</b> (ตัวตัดสินเดียว — หน้าเว็บแสดงตามค่าที่เซิร์ฟเวอร์ส่ง · ด่านจริงอยู่ที่ service ก่อนบันทึก):</para>
/// <list type="bullet">
/// <item>เว็บตั้ง <c>RequireDataConsent</c> ⇒ ไม่ติ๊ก = ปฏิเสธก่อนบันทึก (<see cref="Problem"/>) · ไม่ตั้ง (ค่าเริ่มต้น) = พฤติกรรมเดิม</item>
/// <item>ยกเว้น: พนักงานของบริษัทบันทึกแทนลูกค้า (ได้ความยินยอมนอกระบบ) · ลูกค้าเว็บที่ล็อกอินและให้ความยินยอมไว้แล้ว (<c>SiteCustomer.ConsentGiven</c>)</item>
/// <item>ติ๊กแล้ว (บังคับหรือไม่ก็ตาม) ⇒ บันทึก <c>PdpaConsentRecord</c> พร้อม hash หลักฐานผ่าน <see cref="PdpaConsentEvidence"/> ตัวเดียวของระบบ ·
///   เวอร์ชัน = hash ของข้อความที่แสดงจริง (<see cref="Version"/>) ⇒ ร้านแก้ข้อความแล้วแถวเก่ายังชี้ฉบับที่ลูกค้าเห็น</item>
/// </list>
/// </summary>
public static class CmsDataConsent
{
    public const string RuleCode = "PDPA-19-CMS-CONSENT";
    public const string LegalReference = "พ.ร.บ.คุ้มครองข้อมูลส่วนบุคคล 2562 ม.19";

    public const string OrderPurpose = "cms-order:personal-data";
    public const string BookingPurpose = "cms-booking:personal-data";
    public const string LeadPurpose = "cms-lead:personal-data";
    public const string FormPurpose = "cms-form:personal-data";
    /// <summary>รอบ 211: ลูกค้าผูก LINE เพื่อรับแจ้งเตือน (ถอนเมื่อยกเลิกการเชื่อมต่อ · <c>CustomerLineLinkService</c>)</summary>
    public const string LineNotifyPurpose = "cms-line-notify:messaging";
    public const string Channel = "web-form";

    public const string MissingMessage = "กรุณาติ๊กยอมรับการเก็บและใช้ข้อมูลส่วนบุคคลตามนโยบายความเป็นส่วนตัวก่อนส่งข้อมูล";

    /// <summary>ข้อความยินยอมที่แสดง — ร้านตั้งเอง ⇒ ข้อความนั้น · ว่าง ⇒ ข้อความมาตรฐาน (ชื่อเว็บแทนในข้อความ)</summary>
    public static string ResolveText(string? siteText, string? siteName)
        => !string.IsNullOrWhiteSpace(siteText) ? siteText.Trim()
            : $"ข้าพเจ้ายินยอมให้ {(string.IsNullOrWhiteSpace(siteName) ? "ร้าน" : siteName!.Trim())} เก็บ ใช้ และเปิดเผยข้อมูลส่วนบุคคลที่กรอกนี้ "
              + "เพื่อดำเนินการตามคำสั่งซื้อ/การจอง/การติดต่อ และการออกเอกสารทางบัญชีภาษีที่กฎหมายกำหนด ตามนโยบายความเป็นส่วนตัวของร้าน";

    /// <summary>เวอร์ชันของข้อความยินยอม = "site-" + 12 hex แรกของ SHA-256 ของข้อความที่แสดงจริง (ข้อความเดียวกัน = เวอร์ชันเดียวกันเสมอ)</summary>
    public static string Version(string shownText)
        => "site-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shownText ?? ""))).Substring(0, 12).ToLowerInvariant();

    /// <summary>ด่าน — null = ผ่าน · ข้อความไทย = ปฏิเสธก่อนบันทึก</summary>
    /// <param name="siteRequiresConsent"><c>Site.RequireDataConsent</c></param>
    /// <param name="consentGiven">ลูกค้าติ๊กบนฟอร์ม</param>
    /// <param name="isCompanyStaff">ผู้ส่งเป็นพนักงานของบริษัท (บันทึกแทนลูกค้า)</param>
    /// <param name="customerAlreadyConsented">ลูกค้าเว็บที่ล็อกอินของเว็บนี้และให้ความยินยอมไว้แล้ว</param>
    public static string? Problem(bool siteRequiresConsent, bool consentGiven, bool isCompanyStaff, bool customerAlreadyConsented)
        => siteRequiresConsent && !consentGiven && !isCompanyStaff && !customerAlreadyConsented ? MissingMessage : null;

    /// <summary>ต้องบันทึกแถวความยินยอมไหม — เฉพาะเมื่อลูกค้าติ๊กเอง (ไม่แต่งหลักฐานให้คนที่ไม่ได้ติ๊ก · F2 #3)</summary>
    internal static bool ShouldRecord(bool consentGiven) => consentGiven;

    /// <summary>
    /// เตรียมแถว <c>PdpaConsentRecord</c> (Add เท่านั้น — ผู้เรียกบันทึกพร้อมออเดอร์/การจอง/lead ในคำสั่งเดียว ⇒ ไม่มีแถวยินยอมลอยเมื่อบันทึกล้ม) ·
    /// ไม่มีทั้งอีเมลและเบอร์ ⇒ ไม่บันทึก (ไม่มีตัวตนให้ผูก) — คืน null
    /// </summary>
    public static PdpaConsentRecord? Stage(AccountingDbContext db, Guid companyId, string purpose, string shownText,
        string? email, string? phone, string reference, DateTime grantedAtUtc)
    {
        var subject = CmsContactResolver.NormalizeEmail(email) ?? CmsContactResolver.NormalizePhone(phone);
        if (subject == null) return null;
        return StageForSubject(db, companyId, purpose, shownText, subject, reference, grantedAtUtc);
    }

    /// <summary>รอบ 211 ฝ่ายค้าน: เตรียมแถวยินยอมด้วยตัวระบุที่ผู้เรียกให้มาเอง (เช่น <c>CustomerLineLinkRule.ConsentSubject</c> เมื่อเป้าหมายไม่มีอีเมล/เบอร์) ·
    /// หลักฐานใช้สูตรเดียวกับ <see cref="Stage"/> (<see cref="PdpaConsentEvidence"/>)</summary>
    public static PdpaConsentRecord StageForSubject(AccountingDbContext db, Guid companyId, string purpose, string shownText,
        string subject, string reference, DateTime grantedAtUtc)
    {
        var version = Version(shownText);
        var channel = $"{Channel}:{reference}";
        var row = new PdpaConsentRecord
        {
            CompanyId = companyId,
            SubjectContact = subject,
            Purpose = purpose,
            PolicyVersion = version,
            GrantedAt = grantedAtUtc,
            Channel = channel,
            EvidenceHash = PdpaConsentEvidence.ComputeHash(subject, purpose, version, grantedAtUtc, channel, null, null),
            CreatedBy = "storefront",
        };
        db.Set<PdpaConsentRecord>().Add(row);
        return row;
    }

    /// <summary>
    /// **ด่านก่อนบันทึก** ของทุกทางเข้าแบบฟอร์มหน้าเว็บ — อ่านค่าตั้งของเว็บ (กรองบริษัท) · ตัดสินด้วย <see cref="Problem"/> ·
    /// ไม่ผ่าน ⇒ <see cref="BusinessRuleException"/> (ผู้เรียกยังไม่ได้บันทึกอะไร) · ติ๊กแล้ว ⇒ <see cref="Stage"/> แถวความยินยอม
    /// (บันทึกพร้อม SaveChanges ของผู้เรียก)
    /// </summary>
    /// <param name="actorUserId">ผู้ส่ง (JWT) — เป็นสมาชิกบริษัท = พนักงานบันทึกแทนลูกค้า (ยกเว้นด่าน)</param>
    /// <param name="customerId">ลูกค้าเว็บที่ล็อกอิน (จาก body — ยอมรับเฉพาะลูกค้าของเว็บนี้ที่ให้ความยินยอมไว้แล้ว)</param>
    public static async Task EnforceAsync(AccountingDbContext db, Guid companyId, Guid siteId, string purpose, bool consentGiven,
        string? actorUserId, Guid? customerId, string? email, string? phone, string reference, CancellationToken ct = default)
    {
        var site = await db.Sites.AsNoTracking()
            .Where(s => s.Id == siteId && s.CompanyId == companyId)
            .Select(s => new { s.RequireDataConsent, s.DataConsentText, s.Name })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Site not found.");
        if (site.RequireDataConsent && !consentGiven)
        {
            var isStaff = Guid.TryParse(actorUserId, out var uid) && uid != Guid.Empty
                && await db.CompanyUsers.AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == uid, ct);
            var customerConsented = customerId is Guid cid
                && await db.SiteCustomers.AnyAsync(c => c.Id == cid && c.CompanyId == companyId && c.SiteId == siteId && c.ConsentGiven, ct);
            if (Problem(site.RequireDataConsent, consentGiven, isStaff, customerConsented) is string problem)
                throw new BusinessRuleException(problem, RuleCode);
        }
        if (ShouldRecord(consentGiven))
            Stage(db, companyId, purpose, ResolveText(site.DataConsentText, site.Name), email, phone, reference, DateTime.UtcNow);
    }
}

/// <summary>
/// **อีเมลกู้ตะกร้าค้าง — ส่งได้ไหม** — รอบ 209 P6 · คำตัดสินข้อ 168 ช (PDPA ม.19/24: อีเมลชวนกลับมาซื้อ = การตลาด ต้องได้ความยินยอม)
/// <para>เดิม <c>AbandonedCartService</c> ส่งทุกเว็บทุกบริษัทหลัง 3 ชม. ตายตัว — ไม่ดู <c>EnableAbandonedCartRecovery</c>/<c>AbandonedCartHours</c>
/// และไม่ดู <c>AcceptMarketing</c></para>
/// </summary>
public static class AbandonedCartEligibility
{
    public const string RuleCode = "PDPA-24-CART-MARKETING";
    public const int DefaultHours = 3;

    /// <summary>ชั่วโมงที่ถือว่าค้าง — ค่าตั้งของเว็บ (1–720) · ไม่ตั้ง/นอกช่วง ⇒ 3 (เดิม)</summary>
    public static int Hours(int? configured) => configured is int h && h >= 1 && h <= 720 ? h : DefaultHours;

    /// <summary>คำตัดสิน + เหตุผล (log ด้วย <see cref="RuleCode"/>) · <c>Send=false</c> ยังทำเครื่องหมาย "ค้าง" ตามเดิม (ล้างตะกร้าแขกเก่า)</summary>
    public static (bool Send, string Reason) Decide(bool recoveryEnabled, bool hasCustomer, string? email,
        bool acceptMarketing, bool consentWithdrawn)
    {
        if (!recoveryEnabled) return (false, "ร้านปิดอีเมลกู้ตะกร้า");
        if (!hasCustomer || CmsContactResolver.NormalizeEmail(email) == null) return (false, "ไม่มีบัญชีลูกค้า/อีเมล");
        if (consentWithdrawn) return (false, "ลูกค้าถอนความยินยอมแล้ว");
        if (!acceptMarketing) return (false, "ลูกค้าไม่ได้ยินยอมรับข่าวสารการตลาด");
        return (true, "ยินยอมรับข่าวสาร + ร้านเปิดอีเมลกู้ตะกร้า");
    }
}
