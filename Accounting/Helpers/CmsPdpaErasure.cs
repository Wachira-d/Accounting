using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// **DSR ม.33 (สิทธิลบ) บนข้อมูลเว็บ — ช่องไหนล้าง ช่องไหนคงไว้ตามกฎหมายบัญชี/ภาษี** — รอบ 209 P6 · คำตัดสินข้อ 168 ฑ · R-209-D6
///
/// <para><b>ที่มา</b>: <c>PdpaService</c> แตะเฉพาะ Users/Contacts/Documents/สแกน — ลูกค้าเว็บที่ขอลบยังค้นเจอตัวเองเต็ม ๆ ใน
/// <c>SiteCustomer</c>/<c>SiteOrder</c>/<c>SiteBooking</c>/<c>CmsLead</c>/<c>SiteFormSubmission</c> (ชื่อ · โทร · อีเมล · ที่อยู่ · ข้อความ)</para>
///
/// <para><b>กติกา (MAX retention · legal_hold — CLAUDE.md กฎ M/J)</b>: แถวที่ผูกเอกสารบัญชีซึ่งยังอยู่ในงวดเก็บ 5 ปี
/// (พ.ร.บ.การบัญชี ม.10 · ป.รัษฎากร §87/3 — เกณฑ์เดียวกับ <c>PdpaService.ApplyErasureAsync</c> ของผู้ติดต่อ) <b>คงช่องผู้ซื้อที่ใบกำกับบังคับ</b>
/// (ชื่อ · ที่อยู่ · เลขผู้เสียภาษี · สาขา — §86/4) · ช่องติดต่อ (อีเมล · โทร · ที่อยู่จัดส่ง · ข้อความ) <b>ล้างเสมอ</b> ·
/// ไม่ผูกเอกสาร/พ้นงวด ⇒ ล้างทุกช่องส่วนบุคคล · ยอดเงิน/สถานะ/เลขออเดอร์คงไว้ (ไม่ใช่ข้อมูลส่วนบุคคล — รายงานยอดขายต้องไม่เพี้ยน)</para>
/// </summary>
public static class CmsPdpaErasure
{
    private static readonly System.Text.RegularExpressions.Regex EmailToken =
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PhoneToken =
        new(@"\+?\d[\d\-\s]{7,}\d", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>คำตอบฟอร์มนี้เป็นของเจ้าของข้อมูลไหม — เทียบ<b>ทั้งโทเคน</b> (อีเมล normalize เท่ากันพอดี · เบอร์ normalize เท่ากันพอดี) ·
    /// ฝ่ายค้านรอบ 209 (security ข้อ 5): เดิม substring ⇒ bob@x.com ตรง jimbob@x.com ⇒ ลบ/ส่งออกคำตอบของบุคคลอื่น (ม.33 ย้อนไม่ได้)</summary>
    public static bool SubmissionMentions(string? json, string? normalizedEmail, string? normalizedPhone)
    {
        if (string.IsNullOrEmpty(json)) return false;
        if (normalizedEmail != null && EmailToken.Matches(json).Any(m => CmsContactResolver.NormalizeEmail(m.Value) == normalizedEmail)) return true;
        return normalizedPhone != null && PhoneToken.Matches(json).Any(m => CmsContactResolver.NormalizePhone(m.Value) == normalizedPhone);
    }

    public const string RuleCode = "PDPA-33-CMS-ERASE";
    public const string Anonymized = "[ANONYMIZED]";
    public const int RetentionYears = 5;

    /// <summary>อยู่ใต้ legal hold ไหม — มีเอกสารบัญชีผูกอยู่ที่ยังไม่พ้น 5 ปีนับจากวันที่เอกสาร</summary>
    public static bool IsUnderLegalHold(DateTime? linkedDocumentDate, DateTime nowUtc)
        => linkedDocumentDate is DateTime d && d.AddYears(RetentionYears) > nowUtc;

    /// <summary>ล้างออเดอร์ · คืนจำนวนช่องที่เปลี่ยน</summary>
    public static int AnonymizeOrder(SiteOrder o, bool legalHold)
    {
        var n = 0;
        n += Clear(o.ShippingName, v => o.ShippingName = v, legalHold && string.IsNullOrWhiteSpace(o.BillingName) ? o.ShippingName : null);
        n += Clear(o.ShippingAddress, v => o.ShippingAddress = v, null);
        n += Clear(o.ShippingPhone, v => o.ShippingPhone = v, null);
        n += Clear(o.ShippingEmail, v => o.ShippingEmail = v, null);
        n += Clear(o.ShippingPostalCode, v => o.ShippingPostalCode = v, null);
        n += Clear(o.CustomerNotes, v => o.CustomerNotes = v, null);
        if (!legalHold)
        {
            n += Clear(o.BillingName, v => o.BillingName = v, null);
            n += Clear(o.BillingAddress, v => o.BillingAddress = v, null);
            n += Clear(o.BillingTaxId, v => o.BillingTaxId = v, null);
            n += Clear(o.BillingBranchCode, v => o.BillingBranchCode = v, null);
        }
        return n;
    }

    /// <summary>ล้างการจอง — legal hold คงชื่อผู้จอง (ผู้รับบริการบนใบ) · ช่องติดต่อ/ข้อความล้างเสมอ</summary>
    public static int AnonymizeBooking(SiteBooking b, bool legalHold)
    {
        var n = 0;
        if (!legalHold) n += Clear(b.GuestName, v => b.GuestName = v, null);
        n += Clear(b.GuestEmail, v => b.GuestEmail = v, null);
        n += Clear(b.GuestPhone, v => b.GuestPhone = v, null);
        n += Clear(b.CustomerNotes, v => b.CustomerNotes = v, null);
        return n;
    }

    /// <summary>ล้าง lead — legal hold (แปลงเป็นเอกสารแล้วยังไม่พ้นงวด) คงชื่อ/บริษัท/เลขภาษี · ช่องติดต่อ/ข้อความ/ช่องเพิ่มเติมล้างเสมอ</summary>
    public static int AnonymizeLead(CmsLead l, bool legalHold)
    {
        var n = 0;
        if (!legalHold)
        {
            n += Clear(l.CustomerName, v => l.CustomerName = v, null);
            n += Clear(l.CustomerCompany, v => l.CustomerCompany = v, null);
            n += Clear(l.CustomerTaxId, v => l.CustomerTaxId = v, null);
        }
        n += Clear(l.CustomerEmail, v => l.CustomerEmail = v, null);
        n += Clear(l.CustomerPhone, v => l.CustomerPhone = v, null);
        n += Clear(l.Message, v => l.Message = v, null);
        n += Clear(l.DataJson, v => l.DataJson = v, null);
        return n;
    }

    /// <summary>ล้างคำตอบฟอร์ม — ไม่มีเอกสารบัญชีผูก (lead ที่สร้างจากฟอร์มถือช่องที่ต้องคงไว้แทน) ⇒ ล้างทั้งก้อน</summary>
    public static int AnonymizeSubmission(SiteFormSubmission s)
    {
        var n = 0;
        if (s.DataJson != "{}") { s.DataJson = "{}"; n++; }
        n += Clear(s.IpAddress, v => s.IpAddress = v, null);
        n += Clear(s.UserAgent, v => s.UserAgent = v, null);
        n += Clear(s.Notes, v => s.Notes = v, null);
        return n;
    }

    /// <summary>ล้างบัญชีลูกค้าเว็บ (ตัวเดียวกับ <c>CmsCustomerService.ProcessDataDeletionAsync</c>) — บัญชีไม่ใช่เอกสารบัญชี ⇒ ไม่มี legal hold ·
    /// ปิดบัญชี + ถอดรหัสผ่าน/โทเคน (ทางเข้าที่ยังเปิดอยู่ = การลบที่ยังไม่จบ)</summary>
    /// <summary>รอบ 211: ลิงก์ LINE ที่ลูกค้าผูกเอง — ปิดการเชื่อมต่อ (reason "erase") + ล้าง LINE userId · คืนจำนวนช่องที่เปลี่ยน (0 = ล้างไปแล้ว)</summary>
    public static int EraseLineLink(CustomerLineLink l, DateTime nowUtc)
    {
        var n = 0;
        if (l.LineUserId.Length > 0) { l.LineUserId = ""; n++; }
        if (l.UnlinkedAt == null) { l.UnlinkedAt = nowUtc; l.UnlinkReason = "erase"; n++; }
        return n;
    }

    public static int AnonymizeCustomer(SiteCustomer c, DateTime nowUtc)
    {
        c.RightToBeforgotten = true;
        c.DataDeletionRequestedAt ??= nowUtc;
        c.Email = $"deleted_{c.Id:N}@anonymized.local";
        c.FullName = "Anonymized User";
        c.Phone = null;
        c.TaxId = null;
        c.BranchCode = null;
        c.CompanyName = null;
        c.PasswordHash = null;
        c.AuthProvider = null;
        c.AuthProviderId = null;
        c.PasswordResetToken = null;
        c.PasswordResetTokenExpiry = null;
        c.EmailVerificationToken = null;
        c.AcceptMarketing = false;
        c.IsActive = false;
        c.Tags = null;
        c.CustomerGroup = null;
        return 1;
    }

    private static int Clear(string? current, Action<string?> set, string? keep)
    {
        if (string.IsNullOrEmpty(current) || current == keep) return 0;
        set(keep);
        return 1;
    }
}
