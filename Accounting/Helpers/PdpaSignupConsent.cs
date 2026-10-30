using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// นโยบาย/ข้อกำหนดที่ผู้สมัครต้องยอมรับ — **แหล่งความจริงเดียว** ของเวอร์ชัน
/// นโยบาย ใช้ร่วมกันทั้ง endpoint <c>/api/legal/policy</c> (หน้า terms/privacy
/// อ่านไปแสดง) และ <c>AuthService</c> (ตอนบันทึก <c>PdpaConsentRecord</c>)
///
/// ทำไมเวอร์ชันต้องอยู่ในโค้ด ไม่ใช่ในตารางที่แอดมินแก้ได้: เนื้อความนโยบาย
/// อยู่ในไฟล์ <c>wwwroot/terms.html</c> / <c>privacy.html</c> ซึ่งเปลี่ยนได้
/// ด้วยการ deploy เท่านั้น ถ้าเวอร์ชันแยกไปอยู่ที่อื่นจะ drift จากเนื้อความ
/// ที่ผู้ใช้เห็นจริง ⇒ หลักฐานความยินยอมชี้ไปที่นโยบายผิดฉบับ
///
/// ⚠️ แก้เนื้อความนโยบายเมื่อไร **ต้องขยับ <see cref="CurrentVersion"/> ด้วย**
/// และ <see cref="EffectiveDate"/> ให้ตรงวันที่ฉบับใหม่มีผล — ไม่งั้นแถวเก่ากับ
/// แถวใหม่จะอ้างเวอร์ชันเดียวกันทั้งที่คนละเนื้อความ
///
/// <para><b>รอบ 214 (คำตัดสินข้อ 173) — ความหมายเปลี่ยน</b>: เนื้อความย้ายไปอยู่ในตาราง <c>LegalDocumentVersions</c> ที่ SystemAdmin
/// เผยแพร่ได้ (append-only) · <see cref="CurrentVersion"/>/<see cref="EffectiveDate"/> เหลือเป็น<b>ฉบับตั้งต้นในโค้ด</b>
/// (<c>LegalDocumentDefaults</c> — ใช้เมื่อยังไม่มีฉบับเผยแพร่) · <b>ห้ามผู้เรียกใหม่อ่านค่าคงที่นี้เป็น "ฉบับปัจจุบัน"</b> —
/// ใช้ <c>ILegalDocumentService.GetConsentVersionAsync</c> ตัวเดียว (ล็อกด้วย <c>tools/required_call_site_check.py</c>) ·
/// เนื้อความเดิมใน terms.html/privacy.html ย้ายเข้า <c>LegalDocumentDefaults</c> แล้ว (หน้า HTML เหลือแค่โครงที่ดึงเนื้อความจาก API)</para>
/// </summary>
public static class PdpaPolicy
{
    /// <summary>เวอร์ชันของข้อกำหนดการใช้งาน + นโยบายความเป็นส่วนตัวฉบับปัจจุบัน</summary>
    public const string CurrentVersion = "1.0";

    /// <summary>วันที่ฉบับปัจจุบันมีผล (เก็บเป็น ค.ศ. แสดงผลเป็น พ.ศ. บนหน้าเว็บ)</summary>
    public static readonly DateOnly EffectiveDate = new(2026, 8, 25);

    /// <summary>วัตถุประสงค์ที่บันทึกลง <c>PdpaConsentRecord.Purpose</c> ตอนสมัคร
    /// (ม.19 — ต้องระบุวัตถุประสงค์ที่ขอความยินยอมให้ชัด)</summary>
    public const string SignupPurpose = "account-signup:terms-and-privacy";

    /// <summary>รอบ 214 (คำตัดสินข้อ 173): ผู้ใช้ที่มีบัญชีอยู่แล้วกด "รับทราบและยอมรับ" ฉบับใหม่บนแถบแจ้งในระบบ —
    /// วัตถุประสงค์เดียวกับตอนสมัคร (ยอมรับข้อกำหนด + นโยบาย) แต่แยกช่องทางให้ตรวจย้อนได้ว่ายอมรับตอนไหน</summary>
    public const string PolicyUpdatePurpose = "policy-update:terms-and-privacy";

    /// <summary>วัตถุประสงค์ที่นับเป็น "ยอมรับข้อกำหนด + นโยบายฉบับนั้นแล้ว" — ใช้หาฉบับที่ผู้ใช้ยอมรับล่าสุด</summary>
    public static readonly IReadOnlyList<string> TermsAcceptancePurposes = new[] { SignupPurpose, PolicyUpdatePurpose };

    /// <summary>ขอบเขตของ consent ตอนสมัคร = ระดับ **แพลตฟอร์ม** ไม่ใช่ระดับบริษัท
    /// (ผู้ควบคุมข้อมูลตอนสมัครคือผู้ให้บริการ ไม่ใช่ tenant ที่ยังไม่เกิด —
    /// ทางสมัครผ่านคำเชิญไม่สร้างบริษัทเลยด้วยซ้ำ). ใช้ค่าเดียวทุกทางสมัคร
    /// เพื่อให้ค้น "ผู้ใช้คนนี้ยอมรับนโยบายฉบับไหนแล้ว" ได้ด้วย query เดียว
    /// (แนวเดียวกับ <c>ChatbotService.PublicCompanyId</c> ที่มีอยู่แล้ว)</summary>
    /// <para>รอบ 214 หลัง CI: <b>ไม่ใช้กับแถวความยินยอมแล้ว</b> — PdpaConsentRecords มี FK → Companies (Guid.Empty ถูกปฏิเสธบนฐานที่สร้างจากโมเดล) ·
    /// หลักฐานยอมรับข้อกำหนด/นโยบายแพลตฟอร์มอยู่ที่ <c>PlatformPolicyAcceptances</c> (ระดับผู้ใช้) · ค่านี้เหลือใช้เป็น CompanyId ของแถว audit ระดับแพลตฟอร์ม</para>
    public static readonly Guid PlatformScopeCompanyId = Guid.Empty;
}

/// <summary>
/// Canonical form + SHA-256 ของ "หลักฐานการให้ความยินยอม" ตอนสมัครสมาชิก —
/// **ฟังก์ชันเดียวของทั้งระบบ** ใช้ร่วมทั้งฝั่งเขียน (<c>AuthService</c>) และ
/// ฝั่งตรวจ (<c>Verify</c> ด้านล่าง / เทสต์ round-trip)
///
/// ทำไมต้องเป็นฟังก์ชันเดียว: กฎเหล็ก #4 C ของโปรเจกต์ — audit hash chain เคย
/// พังเงียบ ๆ เพราะฝั่งเขียนกับฝั่งตรวจเขียน format string คนละที่แล้ว drift
/// ⇒ verify ไม่มีวันผ่าน = ไม่มี control จริง
///
/// hash นี้ตอบคำถามของ PDPC ว่า "ตอนกดยอมรับ ผู้ใช้เห็นอะไร": ผูก อีเมล +
/// วัตถุประสงค์ + เวอร์ชันนโยบายที่**หน้าเว็บแสดงจริง** + เวลา + IP + ช่องทาง
/// + user-agent เข้าด้วยกัน แก้ field ใดใน 7 ตัวนี้ภายหลัง hash จะไม่ตรงทันที
/// </summary>
public static class PdpaConsentEvidence
{
    // ═══ รอบ 214 ต่อ (คำตัดสินข้อ 173 ฎ) — สูตร v2 แบบเดียวกับ AuditHashChain v2 ═══
    // ที่มา: v1 ใช้ GrantedAt.ToString("O") ⇒ หลังเก็บใน PostgreSQL (timestamp without time zone · ไมโครวินาที) อ่านกลับได้ Kind Unspecified
    // และ ticks หลักที่ 7 หาย ⇒ verify ไม่มีวันผ่าน = ไม่มี control (กฎเหล็ก #4 G) · v2: เวลา UTC ตัดเหลือไมโครวินาที format ตายตัว ·
    // hash ขึ้นต้น "v2:" (แถวเก่าไม่ถูกเขียนทับ — รายงานเป็น "legacy" เมื่อตรวจไม่ได้ ไม่ใช่ "ผ่าน" เงียบ ๆ)

    public const string V2Prefix = "v2:";

    /// <summary>ผลตรวจหลักฐาน — <c>Valid</c> เท่านั้นที่นับว่าผ่าน</summary>
    public enum EvidenceCheck { Valid, Invalid, LegacyUnverifiable, Missing }

    /// <summary><b>ตัวเดียว</b>ของเวลาในหลักฐาน: UTC (Unspecified = เราเขียนเป็น UTC อยู่แล้ว) + ตัดเหลือไมโครวินาที (ความละเอียดของ PostgreSQL) ·
    /// ผู้เขียนเก็บค่านี้ลงคอลัมน์เวลา ⇒ ค่าที่อ่านกลับ = ค่าที่ hash</summary>
    public static DateTime NormalizeTimestamp(DateTime t)
    {
        var utc = t.Kind switch
        {
            DateTimeKind.Local => t.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc),
            _ => t,
        };
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    /// <summary>canonical v2 — ช่องเหมือน v1 แต่เวลาเป็น UTC ไมโครวินาทีรูปแบบตายตัว และขึ้นต้นด้วย "v2|"</summary>
    private static string CanonicalV2(
        string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => "v2|" + string.Join('|',
            (email ?? string.Empty).Trim().ToLowerInvariant(),
            purpose ?? string.Empty,
            policyVersionShown ?? string.Empty,
            NormalizeTimestamp(grantedAt).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            channel ?? string.Empty,
            ipAddress ?? string.Empty,
            userAgent ?? string.Empty);

    /// <summary>ลำดับ field ตายตัว:
    /// Email|Purpose|PolicyVersionShown|GrantedAt(ISO-O)|Channel|IpAddress|UserAgent
    /// <para>normalize: อีเมลเป็นตัวพิมพ์เล็ก/ตัดช่องว่าง (ให้ตรงกับที่เก็บใน
    /// <c>User.Email</c>), null ทุกตัวกลายเป็นสตริงว่าง — ไม่งั้น "ไม่มีค่า"
    /// กับ "ค่าว่าง" ให้ hash คนละตัวทั้งที่หมายถึงเรื่องเดียวกัน</para></summary>
    public static string Canonical(
        string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => string.Join('|',
            (email ?? string.Empty).Trim().ToLowerInvariant(),
            purpose ?? string.Empty,
            policyVersionShown ?? string.Empty,
            grantedAt.ToString("O"),
            channel ?? string.Empty,
            ipAddress ?? string.Empty,
            userAgent ?? string.Empty);

    /// <summary><b>ฝั่งเขียนตัวเดียว</b> — "v2:" + SHA-256 hex (ตัวพิมพ์ใหญ่) ของ <see cref="CanonicalV2"/> · ผู้เรียกต้องเก็บเวลา
    /// <see cref="NormalizeTimestamp"/> ลงคอลัมน์ (ค่าเดียวกับที่ hash)</summary>
    public static string ComputeHash(
        string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => V2Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            CanonicalV2(email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent))));

    /// <summary>hash v1 (ก่อนรอบ 214 ต่อ) — ใช้เฉพาะฝั่งตรวจแถวเก่า</summary>
    private static string ComputeHashV1(
        string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical(email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent))));

    /// <summary><b>ฝั่งตรวจตัวเดียว</b> — v2 ตรวจตรง (ไม่ตรง = Invalid) · v1 (ไม่มีป้าย) ลองเวลาแบบ UTC/Unspecified ·
    /// ตรงไม่ได้ = <c>LegacyUnverifiable</c> (เวลาเสียความละเอียดหลังผ่านฐาน — แยกไม่ออกจากการแก้ ⇒ ห้ามรายงานว่าผ่าน) ·
    /// ไม่มี hash = <c>Missing</c></summary>
    public static EvidenceCheck Check(
        string? expectedHash, string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return EvidenceCheck.Missing;
        if (expectedHash.StartsWith(V2Prefix, StringComparison.Ordinal))
            return string.Equals(expectedHash,
                ComputeHash(email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent),
                StringComparison.OrdinalIgnoreCase) ? EvidenceCheck.Valid : EvidenceCheck.Invalid;
        foreach (var kind in new[] { grantedAt.Kind, DateTimeKind.Utc, DateTimeKind.Unspecified })
            if (string.Equals(expectedHash,
                    ComputeHashV1(email, purpose, policyVersionShown, DateTime.SpecifyKind(grantedAt, kind), channel, ipAddress, userAgent),
                    StringComparison.OrdinalIgnoreCase))
                return EvidenceCheck.Valid;
        return EvidenceCheck.LegacyUnverifiable;
    }

    /// <summary>ฝั่งตรวจ — ใช้ canonical ตัวเดียวกับฝั่งเขียนเสมอ.
    /// คืน false เมื่อ field ใดถูกแก้ภายหลัง (tamper-evident)</summary>
    public static bool Verify(
        string? expectedHash, string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => Check(expectedHash, email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent) == EvidenceCheck.Valid;
}
