using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **จำข้อมูลผู้ลงนามด้วย LINE Login — ตัวตัดสินเดียว (pure)** (รอบ 267 แพ็ก P5 · คำตัดสินเจ้าของ 267-1-13 · 267-1-73…267-1-76 ·
/// ดีไซน์ §14 · DOCUMENT_FLOW §2.12n-P5)
///
/// <para>เจ้าของสั่ง: "กดล็อคอินไลน์บันทึกข้อมูลใช้ครั้งหน้าได้" ⇒ ผู้ลงนามเข้าด้วยช่อง LINE Login ของระบบ (ไม่ใช่ OA ของบริษัท — userId ต่าง Provider กัน
/// ใช้ผูกปลายทาง OA ไม่ได้ · §14.1) แล้ว<b>ติ๊กเอง</b>ว่าให้จำ ชื่อ · นามสกุล · ตำแหน่ง · อีเมล ไว้กับบัญชี LINE <b>สำหรับเอกสารของบริษัทผู้ส่งรายนี้</b>
/// (ต่อบริษัท — ไม่แชร์ข้ามบริษัท = จำกัดวัตถุประสงค์ PDPA ม.21) · ครั้งหน้าเข้าด้วย LINE ⇒ เติมฟอร์ม · <b>ติ๊กรับรองยังต้องกดเองทุกครั้ง</b>
/// (ค่าที่เติมไม่มีช่องรับรอง — ตัวตัดสินผู้ลงนาม <see cref="CustomerSignRule.DecideSigner"/> ไม่รู้จักโปรไฟล์)</para>
///
/// <para>ฐานกฎหมาย: ความยินยอม (PDPA ม.19) — แถว <c>PdpaConsentRecord</c> (Purpose <see cref="ConsentPurpose"/> · รุ่น <see cref="PolicyVersion"/> ·
/// EvidenceHash จาก canonical ของระบบ) · ถอนได้ทุกเมื่อ ⇒ ลบโปรไฟล์ทันที (แถวหลักฐานการลงนามคงอยู่ — คนละฐาน ม.24(3)/(5)) ·
/// ไม่ใช้ 24 เดือน ⇒ งานกลางคืนลบ (<c>PdpaRetentionPurgeJob</c> ใต้ <c>JobLock</c>) · RoPA (ม.39) แถว <see cref="RopaPurpose"/></para>
///
/// <para>ไม่มี state ข้ามคำขอในหน่วยความจำ (กฎ #4 D): <c>state</c> ของ OAuth และ "ตั๋วผู้ลงนาม" เป็นข้อความที่เซ็นด้วย HMAC (กุญแจแยกโดเมนจาก
/// ENCRYPTION_KEY — production ไม่มีคีย์ = boot ไม่ขึ้น) ผูกกับ token ของหน้าเอกสาร (hash) + บริษัท + เวลาหมดอายุ · ไม่เก็บในฐาน</para>
/// </summary>
public static class CustomerSignerProfileRule
{
    public const string RuleCode = "PDPA-19-SIGNER";
    /// <summary>วัตถุประสงค์ของความยินยอม (ค่าที่เก็บใน <c>PdpaConsentRecord.Purpose</c>)</summary>
    public const string ConsentPurpose = "customer-signer-prefill";
    /// <summary>รุ่นของข้อความยินยอม — แก้ <see cref="ConsentText"/> ต้องขยับ</summary>
    public const string PolicyVersion = "267-5";
    public const string Channel = "line-login";
    /// <summary>ไม่ได้ใช้โปรไฟล์ (เข้าด้วย LINE แล้วเติม/บันทึก) นานเท่านี้ ⇒ ลบ (ดีไซน์ §14.3)</summary>
    public const int RetentionMonths = 24;
    /// <summary>อายุของ state OAuth (นาที) — ผู้ลงนามต้องกลับจากหน้าอนุญาตของ LINE ภายในเวลานี้</summary>
    public const int StateTtlMinutes = 10;
    /// <summary>อายุของตั๋วผู้ลงนาม (นาที) — ใช้เติมฟอร์ม/บันทึก/ถอนบนหน้าเอกสารใบนี้เท่านั้น</summary>
    public const int TicketTtlMinutes = 30;
    public const string LineAuthorizeEndpoint = "https://access.line.me/oauth2/v2.1/authorize";

    private static byte[] _key = DeriveKey(EncryptionKeyResolver.DevKey);

    private static byte[] DeriveKey(string serverKey)
        => SHA256.HashData(Encoding.UTF8.GetBytes("nextacc/customer-signer-profile/v1|" + serverKey));

    /// <summary>ตั้งกุญแจ HMAC (ครั้งเดียวตอนบูต — <c>Program.cs</c>) · ว่าง = throw (ห้าม fallback เงียบ ๆ — กฎ #4 C)</summary>
    public static void ConfigureKey(string serverKey)
    {
        if (string.IsNullOrWhiteSpace(serverKey)) throw new InvalidOperationException("ต้องมีกุญแจฝั่งเซิร์ฟเวอร์สำหรับตั๋วผู้ลงนาม (ENCRYPTION_KEY)");
        _key = DeriveKey(serverKey);
    }

    private static string Mac(string message)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    private static bool MacEquals(string a, string b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private static DateTime? ParseStamp(string s)
        => DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    /// <summary>กุญแจเจ้าของข้อมูล = HMAC ของ (ช่อง LINE + userId) — ฐานไม่เก็บ LINE userId ตรง ๆ · ช่องเดียวกัน + คนเดียวกัน = กุญแจเดียวกันเสมอ</summary>
    public static string SubjectKey(string channelId, string lineUserId)
        => Mac($"subject|line|{channelId.Trim()}|{lineUserId.Trim()}");

    /// <summary>state ของ OAuth — ผูกบริษัท + token ของหน้าเอกสาร (hash) + เวลาหมดอายุ + nonce สุ่ม (กัน CSRF/ใช้ state ของใบอื่น)</summary>
    public static string NewState(Guid companyId, string pageTokenHash, DateTime nowUtc)
    {
        var exp = Stamp(nowUtc.AddMinutes(StateTtlMinutes));
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return $"{exp}.{nonce}.{Mac($"state|{companyId:N}|{pageTokenHash}|{exp}|{nonce}")}";
    }

    /// <summary>state ถูกต้อง (ของบริษัท/ใบนี้ · ยังไม่หมดอายุ · ลายเซ็นตรง) ไหม</summary>
    public static bool StateValid(string? state, Guid companyId, string pageTokenHash, DateTime nowUtc)
    {
        var parts = (state ?? "").Split('.');
        if (parts.Length != 3 || ParseStamp(parts[0]) is not DateTime exp || exp <= nowUtc) return false;
        return MacEquals(parts[2], Mac($"state|{companyId:N}|{pageTokenHash}|{parts[0]}|{parts[1]}"));
    }

    /// <summary>ตั๋วผู้ลงนาม (หลังเข้าด้วย LINE สำเร็จ) — ใช้ได้กับหน้าเอกสารใบนี้ของบริษัทนี้เท่านั้น · อายุ <see cref="TicketTtlMinutes"/> นาที</summary>
    public static string IssueTicket(Guid companyId, string pageTokenHash, string subjectKey, DateTime nowUtc)
    {
        var exp = Stamp(nowUtc.AddMinutes(TicketTtlMinutes));
        return $"{exp}.{subjectKey}.{Mac($"ticket|{companyId:N}|{pageTokenHash}|{subjectKey}|{exp}")}";
    }

    /// <summary>กุญแจเจ้าของข้อมูลจากตั๋ว · ตั๋วผิดใบ/ผิดบริษัท/หมดอายุ/ถูกแก้ = null</summary>
    public static string? ReadTicket(string? ticket, Guid companyId, string pageTokenHash, DateTime nowUtc)
    {
        var parts = (ticket ?? "").Split('.');
        if (parts.Length != 3 || parts[1].Length != 64 || ParseStamp(parts[0]) is not DateTime exp || exp <= nowUtc) return null;
        return MacEquals(parts[2], Mac($"ticket|{companyId:N}|{pageTokenHash}|{parts[1]}|{parts[0]}")) ? parts[1] : null;
    }

    /// <summary>URL หน้าอนุญาตของ LINE (scope openid เท่านั้น — ไม่ขอโปรไฟล์/อีเมลจาก LINE: ค่าที่เติมมาจากสิ่งที่ผู้ลงนามพิมพ์เองและยินยอมให้จำ · §14.1)</summary>
    public static string AuthorizeUrl(string channelId, string redirectUri, string state)
        => $"{LineAuthorizeEndpoint}?response_type=code&client_id={Uri.EscapeDataString(channelId)}"
           + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}&scope=openid";

    /// <summary>ที่อยู่กลับจาก LINE — หน้าเอกสาร (ต้องลงทะเบียนเป็น Callback URL ของช่อง LINE Login ของระบบ — 267-1-74)</summary>
    public static string RedirectUri(string origin) => origin.TrimEnd('/') + "/pages/pay-document.html";

    /// <summary>ข้อความที่ผู้ลงนามติ๊กเพื่อให้จำ (PDPA ม.19 — ชัดแจ้ง · แยกจากข้อความรับรองการลงนาม · ถอนได้)</summary>
    public static string ConsentText(bool en, string? sellerName)
    {
        var seller = string.IsNullOrWhiteSpace(sellerName) ? (en ? "this sender" : "ผู้ส่งเอกสารรายนี้") : sellerName.Trim();
        return en
            ? $"I agree that {seller} keeps my first name, last name, position and email, linked to my LINE account, to fill in the signing form of its documents next time. "
              + "I must still confirm and tick the declaration myself each time. I can withdraw this at any time on the document page; unused details are deleted after 24 months."
            : $"ข้าพเจ้ายินยอมให้ {seller} จำชื่อ นามสกุล ตำแหน่ง และอีเมลของข้าพเจ้าไว้กับบัญชี LINE เพื่อเติมฟอร์มลงนามเอกสารของ {seller} ครั้งต่อไป "
              + "โดยข้าพเจ้ายังต้องตรวจและติ๊กรับรองเองทุกครั้ง · ถอนความยินยอมได้ทุกเมื่อบนหน้าเอกสาร · ข้อมูลที่ไม่ได้ใช้ 24 เดือนจะถูกลบ";
    }

    /// <summary>EvidenceHash ของความยินยอม (canonical ของระบบ — ตรวจกลับได้ · EvidenceSource = "system")</summary>
    public static string ConsentEvidenceHash(Guid companyId, string subjectKey, string consentText, DateTime grantedAtUtc)
        => QuotationAcceptance.Sha256Hex(string.Join("|", "signer-prefill-consent", companyId.ToString("N"), subjectKey, ConsentPurpose, PolicyVersion,
            CustomerSignText.TextHash(consentText), grantedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))!;

    /// <summary>ไม่ได้ใช้ตั้งแต่ก่อนวันนี้ ⇒ ลบ</summary>
    public static DateTime PurgeCutoff(DateTime nowUtc) => nowUtc.AddMonths(-RetentionMonths);

    /// <summary>โปรไฟล์ต้องลบไหม (งานกลางคืน) — ไม่ได้ใช้เกิน 24 เดือน หรือความยินยอมถูกถอน/หาไม่พบ</summary>
    public static bool ShouldPurge(DateTime lastUsedAtUtc, bool consentActive, DateTime nowUtc)
        => !consentActive || lastUsedAtUtc < PurgeCutoff(nowUtc);

    /// <summary>ค่าที่เติมฟอร์ม — <b>ไม่มีช่องรับรอง</b> (ผู้ลงนามต้องติ๊กเองทุกครั้ง · S34/S35 · คำสั่งเจ้าของ)</summary>
    public sealed record Prefill(string FirstName, string LastName, string? Title, string? Email);

    // ── RoPA (PDPA ม.39) — แถว "จำข้อมูลผู้ลงนาม" ของบริษัท สร้างครั้งแรกที่มีโปรไฟล์ (ไม่ทับที่บริษัทแก้ไว้) ──
    public const string RopaPurpose = "จำข้อมูลผู้ลงนามเอกสารออนไลน์ด้วย LINE Login (เติมฟอร์มครั้งต่อไป)";
    public const string RopaLegalBasis = "Consent";
    public const string RopaDataCategories = "ชื่อ นามสกุล ตำแหน่ง อีเมล (ถ้าให้) ของผู้ลงนามฝั่งคู่ค้า · กุญแจอ้างอิงบัญชี LINE (HMAC — ไม่เก็บ LINE userId) · เวลาที่ใช้ล่าสุด";
    public const string RopaRetention = "จนกว่าจะถอนความยินยอม หรือไม่ได้ใช้ 24 เดือน (ลบอัตโนมัติ)";
    public const string RopaRecipients = "ไม่มีผู้รับภายนอก — ใช้เติมฟอร์มบนหน้าเอกสารของบริษัทนี้เท่านั้น";
    public const string RopaNote = "ฐาน: ความยินยอม PDPA ม.19 (แถว PdpaConsentRecord Purpose " + ConsentPurpose + ") · เข้ารหัสขณะจัดเก็บ (ม.37) · ถอนได้บนหน้าเอกสาร/ผ่าน DSR · "
        + "ไม่ใช่หลักฐานตัวตนของการลงนาม (แถวหลักฐานการลงนามเป็นคนละฐาน — ม.24(3)/(5))";
}
