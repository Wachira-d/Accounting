using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **กติกาการลงนามบนหน้าเอกสารสำหรับลูกค้า — ตัวตัดสินเดียว** (รอบ 267 แพ็ก P2 · คำตัดสิน 267-1-4…267-1-6 · 267-1-11 · 267-1-14 ·
/// DOCUMENT_FLOW §2.12n-P2) · pure — ผู้เรียกเดียวฝั่งเขียน: <c>CustomerDocumentResponseService</c> · ฝั่งปุ่ม: <see cref="CustomerDocPage.Actions"/>
///
/// <para>(1) ผู้ลงนามผ่านลิงก์หน้าเอกสาร = ชื่อ + นามสกุล + ตำแหน่ง + ติ๊กรับรองข้อความรุ่นปัจจุบัน (ขาดข้อใด = ปฏิเสธ — ไม่ยอมรับแบบไม่ครบ) ·
/// (2) หนึ่งคำตอบต่อ Rev: ตอบแล้ว (ยอมรับ/ปฏิเสธ/ขอแก้) ⇒ ปุ่มตอบรับปิดจนผู้ขายส่งให้ตอบรับใหม่หรือออก Rev ใหม่ ·
/// (3) รหัสยืนยันอีเมลใช้เฉพาะการยอมรับใบเสนอราคาที่ยอดถึงเกณฑ์ที่บริษัทตั้ง (ค่าตั้งต้น = ไม่ใช้) ·
/// (4) ฉบับที่ลูกค้าเซ็นเก็บนอก allow-list <c>/uploads</c> + SHA-256 ตรวจทุกครั้งที่ส่งไฟล์ออก</para>
/// </summary>
public static class CustomerSignRule
{
    public const string RuleCode = "DOCSIGN-267";

    /// <summary>ชนิดของแถวหลักฐาน — ชื่อที่เก็บ/ส่งออก = ชื่อ enum</summary>
    /// <remarks>รอบ 267 P5: ผู้ขาย (ผู้รับใบสั่งซื้อ) รับทราบ/ไม่รับใบสั่งซื้อ — แถวหลักฐานชุดเดียวกัน (267-1-70)</remarks>
    public enum Kind { Accepted = 0, Declined = 1, ChangeRequested = 2, DeliverySigned = 3, PoAcknowledged = 4, PoDeclined = 5 }

    public const string ChannelLink = "Link";
    public const string ChannelLegacyLink = "LegacyLink";
    /// <summary>รอบ 267 P5 (267-1-43 ข้อ 2 · 267-1-71): ระบบภายนอกบันทึกการอนุมัติของลูกค้าผ่าน <c>ExternalApprovalController</c> — แถวหลักฐานชุดเดียวกัน</summary>
    public const string ChannelApi = "Api";
    public const string IdentityFull = "Full";
    public const string IdentityNameOnly = "NameOnly";
    /// <summary>รอบ 267 P5: ชื่อ/ตำแหน่งที่ระบบภายนอกส่งมา — ผู้ลงนามไม่ได้ติ๊กรับรองบนหน้าของเรา (ไม่แต่งว่า "ครบ" — F2 ข้อ 3)</summary>
    public const string IdentityApiReported = "ApiReported";

    /// <summary>ชนิดคำตอบของผู้ขายต่อใบสั่งซื้อ</summary>
    public static bool IsPoKind(Kind kind) => kind is Kind.PoAcknowledged or Kind.PoDeclined;

    /// <summary>คำตอบที่ "ลงนาม" (มีฉบับที่ลงนาม + พิมพ์บนกระดาษ) — ไม่รับ/ขอแก้ ไม่ใช่</summary>
    public static bool IsSigningKind(Kind kind) => kind is Kind.Accepted or Kind.DeliverySigned or Kind.PoAcknowledged;

    public const int NamePartMax = 100;
    public const int TitleMax = 100;
    public const int MessageMax = 1000;
    public const int EmailMax = 254;

    /// <summary>รหัส action ของหน้า (<c>CustomerDocPage.Action*</c>) → ชนิดหลักฐาน · ไม่รู้จัก = null · ชนิดเอกสารไม่ตรง = null</summary>
    public static Kind? KindFor(string? actionId, DocumentType type) => (actionId, type) switch
    {
        (CustomerDocPage.ActionAccept, DocumentType.Quotation) => Kind.Accepted,
        (CustomerDocPage.ActionDecline, DocumentType.Quotation) => Kind.Declined,
        (CustomerDocPage.ActionRequestChange, DocumentType.Quotation) => Kind.ChangeRequested,
        (CustomerDocPage.ActionSignDelivery, DocumentType.DeliveryNote) => Kind.DeliverySigned,
        (CustomerDocPage.ActionPoAcknowledge, DocumentType.PurchaseOrder) => Kind.PoAcknowledged,
        (CustomerDocPage.ActionPoDecline, DocumentType.PurchaseOrder) => Kind.PoDeclined,
        _ => null,
    };

    /// <summary>
    /// รอบ 267 P5 (S31 · 267-1-70): วันส่งของที่ผู้ขายยืนยันตอนรับทราบใบสั่งซื้อ (ไม่บังคับ) — ต้องไม่ก่อนวันที่ใบสั่งซื้อ ·
    /// ว่าง = (null, null) · เก็บเป็นวันปฏิทิน (ไม่มีเวลา) · ผิด = ข้อความ (ไม่บันทึกอะไร)
    /// </summary>
    public static (DateTime? Date, string? Error) DecidePromisedDate(DateTime? promised, DateTime poDate, bool en)
    {
        if (promised == null) return (null, null);
        var d = DateTime.SpecifyKind(promised.Value.Date, DateTimeKind.Utc);
        if (d < poDate.Date)
            return (null, T(en, $"วันส่งที่ยืนยันต้องไม่ก่อนวันที่ใบสั่งซื้อ ({ThaiDate.ToThaiDisplayString(poDate)})",
                $"The confirmed delivery date cannot be before the purchase order date ({poDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)})"));
        if (d > poDate.Date.AddYears(3))
            return (null, T(en, "วันส่งที่ยืนยันไกลเกินไป — กรุณาตรวจปีอีกครั้ง", "The confirmed delivery date is too far ahead — please check the year"));
        return (d, null);
    }

    /// <summary>สิ่งที่ผู้ลงนามกรอก (ดิบจากคำขอ)</summary>
    public sealed record SignerInput(string? FirstName = null, string? LastName = null, string? Title = null,
        bool AttestationChecked = false, string? AttestationVersion = null, string? Email = null);

    /// <summary>ผู้ลงนามที่ผ่านด่านแล้ว — <paramref name="FullName"/> = ชื่อที่ลงภาพฉายเดิมบนใบ/พิมพ์บนกระดาษ</summary>
    public sealed record Signer(string FirstName, string LastName, string Title, string FullName, string? Email);

    private static string T(bool en, string th, string enText) => en ? enText : th;

    private static readonly Regex EmailRx = new(@"^[^@\s<>""',;:\\()\[\]]+@[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\.[A-Za-z]{2,}$", RegexOptions.Compiled);

    /// <summary>อีเมลที่ทำความสะอาดแล้ว · ว่าง = (null, null) · รูปแบบผิด = ข้อความ</summary>
    public static (string? Email, string? Error) CleanEmail(string? raw, bool en)
    {
        var e = QuotationAcceptance.CleanText(raw, EmailMax + 50);
        if (e == null) return (null, null);
        if (e.Length > EmailMax || e.Contains("..") || !EmailRx.IsMatch(e))
            return (null, T(en, "รูปแบบอีเมลไม่ถูกต้อง", "The email address is not valid"));
        return (e.ToLowerInvariant(), null);
    }

    /// <summary>
    /// ด่านผู้ลงนามของลิงก์หน้าเอกสาร: ชื่อ · นามสกุล · ตำแหน่ง (บุคคลธรรมดาเว้นได้ ⇒ "ผู้ซื้อ (บุคคลธรรมดา)") · ติ๊กรับรอง · รุ่นข้อความตรงกับที่ระบบแสดง ·
    /// อีเมล (ไม่บังคับ เว้น <paramref name="emailRequired"/>) — ขาดข้อใด = ข้อความบอกช่องที่ขาด (ไม่ยอมรับแบบไม่ครบ)
    /// </summary>
    public static (Signer? Signer, string? Error) DecideSigner(SignerInput? input, bool buyerIsJuristic, bool emailRequired, bool en)
    {
        if (input == null)
            return (null, T(en, "กรุณากรอกชื่อ นามสกุล ตำแหน่ง และติ๊กรับรองก่อนยืนยัน", "Please enter your first name, last name and position, and tick the confirmation"));
        var first = QuotationAcceptance.CleanText(input.FirstName, NamePartMax);
        var last = QuotationAcceptance.CleanText(input.LastName, NamePartMax);
        var title = QuotationAcceptance.CleanText(input.Title, TitleMax);
        if (first == null) return (null, T(en, "กรุณากรอกชื่อผู้ลงนาม", "Please enter the signer's first name"));
        if (last == null) return (null, T(en, "กรุณากรอกนามสกุลผู้ลงนาม", "Please enter the signer's last name"));
        if (title == null)
        {
            if (buyerIsJuristic) return (null, T(en, "กรุณากรอกตำแหน่งของผู้ลงนาม", "Please enter the signer's position"));
            title = CustomerSignText.IndividualTitle(en);
        }
        if (!input.AttestationChecked)
            return (null, T(en, "กรุณาติ๊กรับรองว่าท่านเป็นบุคคลของผู้ซื้อและมีอำนาจทำรายการนี้", "Please tick the confirmation that you act for the buyer"));
        if (!string.Equals(input.AttestationVersion, CustomerSignText.Version, StringComparison.Ordinal))
            return (null, T(en, "ข้อความรับรองถูกปรับปรุง — กรุณาเปิดหน้านี้ใหม่แล้วติ๊กรับรองอีกครั้ง", "The confirmation text was updated — please reload this page and confirm again"));
        var (email, emailError) = CleanEmail(input.Email, en);
        if (emailError != null) return (null, emailError);
        if (emailRequired && email == null)
            return (null, T(en, "กรุณากรอกอีเมลเพื่อรับรหัสยืนยัน", "Please enter your email to receive the verification code"));
        return (new Signer(first, last, title, first + " " + last, email), null);
    }

    /// <summary>เหตุผล/ข้อความของ "ปฏิเสธ" และ "ขอแก้" — ปฏิเสธ: รหัสต้องอยู่ในรายการปิด ("อื่น ๆ" ต้องมีข้อความ) · ขอแก้: ต้องมีข้อความ</summary>
    public static (string? ReasonCode, string? Message, string? Error) DecideReason(Kind kind, string? reasonCode, string? message, bool en)
    {
        var msg = QuotationAcceptance.CleanText(message, MessageMax);
        if (kind == Kind.Declined)
        {
            var code = (reasonCode ?? "").Trim();
            if (CustomerSignText.ReasonLabel(en, code) == null)
                return (null, null, T(en, "กรุณาเลือกเหตุผลที่ไม่รับใบเสนอราคา", "Please choose a reason for declining"));
            if (code == CustomerSignText.OtherReasonCode && msg == null)
                return (null, null, T(en, "กรุณาระบุรายละเอียดของเหตุผล", "Please describe the reason"));
            return (code, msg, null);
        }
        if (kind is Kind.ChangeRequested or Kind.PoDeclined)
            return msg == null && kind == Kind.PoDeclined
                ? (null, null, T(en, "กรุณาระบุเหตุที่ไม่รับใบสั่งซื้อ", "Please describe why the purchase order is declined"))
                : msg == null
                ? (null, null, T(en, "กรุณาระบุสิ่งที่ต้องการให้แก้ไข", "Please describe what should be changed"))
                : (null, msg, null);
        return (null, msg, null);
    }

    /// <summary>
    /// 267-1-11 หนึ่งคำตอบต่อ Rev: ลูกค้าตอบไปแล้ว (ยอมรับ/ปฏิเสธ/ขอแก้) ⇒ เหตุที่ตอบซ้ำไม่ได้ (null = ยังไม่เคยตอบ) ·
    /// ผู้ขายส่งให้ตอบรับใหม่ (<c>SignRequestKinds.MarkIssued</c>) หรือออก Rev ใหม่ = ล้างคำตอบที่ไม่ใช่ "ยอมรับ" ⇒ เปิดอีกครั้ง
    /// </summary>
    public static string? PriorResponseBlock(bool en, string? customerResponse) => customerResponse switch
    {
        null or "" => null,
        nameof(Kind.Accepted) => T(en, "ยอมรับใบเสนอราคานี้แล้ว", "This quotation has been accepted"),
        nameof(Kind.PoAcknowledged) => T(en, "รับทราบใบสั่งซื้อนี้แล้ว", "This purchase order has been acknowledged"),
        nameof(Kind.PoDeclined) => T(en, "ท่านแจ้งไม่รับใบสั่งซื้อนี้แล้ว — หากเปลี่ยนใจ กรุณาติดต่อผู้ซื้อให้ส่งลิงก์ใหม่",
            "You declined this purchase order — to change your answer, ask the buyer to reopen it for a response"),
        nameof(Kind.Declined) => T(en, "ท่านแจ้งไม่รับใบเสนอราคานี้แล้ว — หากเปลี่ยนใจ กรุณาติดต่อผู้ขายให้ส่งลิงก์ตอบรับใหม่",
            "You declined this quotation — to change your answer, ask the seller to reopen it for a response"),
        _ => T(en, "ท่านส่งคำขอแก้ไขแล้ว — รอผู้ขายส่งใบเสนอราคาฉบับแก้ไข", "Your change request was sent — please wait for the seller's revised quotation"),
    };

    // ═══════════════ รหัสยืนยันอีเมล (267-1-6 · ค่าตั้งต้น = ไม่ใช้) ═══════════════

    public const int OtpTtlMinutes = 10;
    public const int OtpMaxAttempts = 5;
    /// <summary>ขอรหัสใหม่ได้ไม่ถี่กว่า (วินาที) ต่อใบ</summary>
    public const int OtpResendSeconds = 60;
    /// <summary>ฝ่ายค้าน 267-P2 L7: รหัสต่อใบต่อ 24 ชั่วโมง</summary>
    public const int OtpMaxPerDocumentPerDay = 5;

    /// <summary>ต้องยืนยันอีเมลด้วยรหัสไหม — เฉพาะ "ยอมรับ" ใบเสนอราคาที่ยอดรวมถึงเกณฑ์ของบริษัท (ไม่ตั้งเกณฑ์ = ไม่ใช้)</summary>
    public static bool OtpRequired(decimal? companyMinAmount, decimal documentTotal, Kind kind)
        => kind == Kind.Accepted && companyMinAmount is decimal min && min > 0m && documentTotal >= min;

    /// <summary>ฝ่ายค้าน 267-P2 L6: อีเมลผู้ลงนามสำหรับผู้ไม่มีสิทธิ์ Pii.View — "s***@buyer.co.th" (คงโดเมนให้รู้ว่าเป็นคนของบริษัทไหน) · null/ไม่มี @ = null</summary>
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        if (at <= 0) return null;
        return email[..1] + "***" + email[at..];
    }

    /// <summary>ฝ่ายค้าน 267-P2 M2: ลิงก์ยอมรับรุ่นก่อนรอบ 267 (พิมพ์ชื่ออย่างเดียว) ปิดเมื่อ (ก) ลูกค้าตอบใบ/Rev นี้ไปแล้ว (หนึ่งคำตอบต่อ Rev — 267-1-11)
    /// (ข) ยอดถึงเกณฑ์รหัสยืนยันอีเมล (ลิงก์รุ่นก่อนยืนยันอีเมลไม่ได้) · ข้อความบอกทางไปต่อเสมอ · null = เปิด</summary>
    public static string? LegacyAcceptClosedReason(string? customerResponse, bool otpThresholdReached)
    {
        if (!string.IsNullOrEmpty(customerResponse) && customerResponse != nameof(Kind.Accepted))
            return "ท่านแจ้งคำตอบของใบเสนอราคาฉบับนี้ไปแล้ว — หากต้องการเปลี่ยนคำตอบ กรุณาติดต่อผู้ขายให้เปิดให้ตอบใหม่หรือส่งฉบับแก้ไข";
        if (otpThresholdReached)
            return "ใบเสนอราคานี้ต้องยืนยันอีเมลก่อนยอมรับ — กรุณาเปิดลิงก์หน้าเอกสารล่าสุดจากผู้ขาย (หรือขอลิงก์ใหม่จากผู้ขาย)";
        return null;
    }

    /// <summary>รหัส 6 หลักจากตัวสุ่มเชิงเข้ารหัส</summary>
    public static string NewOtpCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    // ฝ่ายค้าน 267-P2 M1: รหัส 6 หลักมีแค่ 10⁶ ค่า ⇒ SHA-256 เปล่ากู้รหัสได้ทันทีจาก hash ⇒ HMAC-SHA256 ด้วยกุญแจฝั่งเซิร์ฟเวอร์ ·
    // กุญแจ = แยกโดเมนจาก ENCRYPTION_KEY (แหล่งเดียวกับ SecretProtector — production fail-fast ใน Program.cs ผ่าน EncryptionKeyResolver.RequireUsable)
    private static byte[] _otpKey = DeriveOtpKey(EncryptionKeyResolver.DevKey);

    private static byte[] DeriveOtpKey(string serverKey)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("nextacc/customer-sign-otp/v1|" + serverKey));

    /// <summary>ตั้งกุญแจของ HMAC รหัสยืนยัน (เรียกครั้งเดียวตอนบูต — <c>Program.cs</c>) · ว่าง = throw (ห้าม fallback เงียบ ๆ — กฎ #4 C)</summary>
    public static void ConfigureOtpKey(string serverKey)
    {
        if (string.IsNullOrWhiteSpace(serverKey)) throw new InvalidOperationException("ต้องมีกุญแจฝั่งเซิร์ฟเวอร์สำหรับรหัสยืนยันอีเมล (ENCRYPTION_KEY)");
        _otpKey = DeriveOtpKey(serverKey);
    }

    /// <summary>HMAC ของรหัส ผูกกับใบ + อีเมล (รหัสของใบหนึ่ง/อีเมลหนึ่งใช้กับอีกใบ/อีกอีเมลไม่ได้) — ตัวเดียวทั้งฝั่งออกและฝั่งตรวจ</summary>
    public static string OtpHash(Guid documentId, string emailLower, string code)
        => Convert.ToHexString(HMACSHA256.HashData(_otpKey,
            System.Text.Encoding.UTF8.GetBytes($"{documentId:N}|{emailLower}|{(code ?? "").Trim()}"))).ToLowerInvariant();

    public static string EmailHash(string emailLower) => QuotationAcceptance.Sha256Hex(emailLower)!;

    public enum OtpOutcome { Ok, Missing, Expired, TooManyAttempts, Wrong }

    /// <summary>ตรวจรหัสกับแถวล่าสุดของ (ใบ + อีเมล) — เวลาคงที่ · หมดอายุ/ใช้แล้ว/เกินจำนวนครั้ง = ไม่ผ่าน</summary>
    public static OtpOutcome VerifyOtp(string? storedHash, DateTime? expiresAt, int attempts, DateTime? consumedAt, Guid documentId, string emailLower, string? code, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(storedHash) || consumedAt != null || expiresAt == null) return OtpOutcome.Missing;
        if (expiresAt <= nowUtc) return OtpOutcome.Expired;
        if (attempts >= OtpMaxAttempts) return OtpOutcome.TooManyAttempts;
        var given = System.Text.Encoding.ASCII.GetBytes(OtpHash(documentId, emailLower, code ?? ""));
        var want = System.Text.Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(given, want) ? OtpOutcome.Ok : OtpOutcome.Wrong;
    }

    public static string OtpMessage(bool en, OtpOutcome o) => o switch
    {
        OtpOutcome.Missing => T(en, "กรุณากดส่งรหัสยืนยันไปที่อีเมลก่อน แล้วกรอกรหัสที่ได้รับ", "Please request a verification code first, then enter it"),
        OtpOutcome.Expired => T(en, "รหัสยืนยันหมดอายุ — กดส่งรหัสใหม่", "The verification code has expired — request a new one"),
        OtpOutcome.TooManyAttempts => T(en, "กรอกรหัสผิดหลายครั้ง — กดส่งรหัสใหม่", "Too many wrong attempts — request a new code"),
        OtpOutcome.Wrong => T(en, "รหัสยืนยันไม่ถูกต้อง", "The verification code is not correct"),
        _ => "",
    };

    // ═══════════════ หลังยอมรับ (267-1-14 — เตือนเท่านั้น) ═══════════════

    /// <summary>ควรเตือนพนักงานเรื่องอากรแสตมป์ไหม — ลูกค้า "ยอมรับ" ใบเสนอราคาที่มีบรรทัดงานบริการ (ระบบไม่ตัดสินว่าเป็นตราสาร)</summary>
    public static bool StampDutyWarn(DocumentType type, Kind kind, bool hasServiceLine)
        => type == DocumentType.Quotation && kind == Kind.Accepted && hasServiceLine;

    // ═══════════════ ฉบับที่ลูกค้าเซ็น (ไฟล์ + SHA-256) ═══════════════

    /// <summary>โฟลเดอร์ย่อยใต้ <c>FileStorageLocator.AttachmentsRoot</c> — ห้ามเพิ่มเข้า publicUploadPrefixes ของ Program.cs</summary>
    public const string SignedCopyFolder = "customer-signed";

    public static string SignedCopyLocation(string attachmentsRoot, Guid companyId, Guid documentId, Guid actionId)
        => Path.Combine(attachmentsRoot, SignedCopyFolder, companyId.ToString("N"), $"{documentId:N}-{actionId:N}.pdf");

    /// <summary>ตำแหน่งที่เก็บไว้เป็นของบริษัท/เอกสาร/แถวหลักฐานนี้จริงไหม (กันแถวที่ชี้ไฟล์ของบริษัทอื่น) — พาธเต็มต้องตรงกับที่ <see cref="SignedCopyLocation"/> คืน</summary>
    public static bool IsOwnSignedCopyLocation(string? location, string attachmentsRoot, Guid companyId, Guid documentId, Guid actionId)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(attachmentsRoot)) return false;
        try
        {
            var full = Path.GetFullPath(location).Replace('\\', '/');
            var want = Path.GetFullPath(SignedCopyLocation(attachmentsRoot, companyId, documentId, actionId)).Replace('\\', '/');
            return string.Equals(full, want, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>SHA-256 hex ของไฟล์ — ตัวเดียวทั้งตอนบันทึกและตอนตรวจก่อนส่งไฟล์ออก</summary>
    public static string FileSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>ไฟล์ที่อ่านได้ตรงกับที่บันทึกไว้ไหม (ไฟล์ถูกแก้/เสีย = ไม่ส่งออก)</summary>
    public static bool SignedCopyIntact(byte[]? bytes, string? storedSha256)
        => bytes is { Length: > 4 } && !string.IsNullOrWhiteSpace(storedSha256)
           && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46   // %PDF
           && string.Equals(FileSha256(bytes), storedSha256, StringComparison.Ordinal);

    /// <summary>รหัสอ้างอิงหลักฐานที่พิมพ์บนกระดาษ (8 ตัวแรกของ hash เนื้อหาที่ลงนาม — ไม่ใช่ IP/ข้อมูลบุคคล · 267-1-17)</summary>
    public static string? EvidenceRef(string? contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;
        var hex = contentHash.Contains(':') ? contentHash[(contentHash.IndexOf(':') + 1)..] : contentHash;
        return hex.Length >= 8 ? hex[..8].ToUpperInvariant() : null;
    }

    /// <summary>สร้างฉบับที่ลูกค้าเซ็นได้ไหม — เฉพาะเมื่อใบยังเป็น Rev และเนื้อหาเดียวกับตอนลงนาม (ไม่อย่างนั้นไฟล์ที่ได้ไม่ใช่ฉบับที่ลูกค้าเห็น)</summary>
    public static bool SnapshotStillValid(int actionRevision, string actionContentHash, int currentRevision, string currentContentHash)
        => actionRevision == currentRevision && string.Equals(actionContentHash, currentContentHash, StringComparison.Ordinal);
}
