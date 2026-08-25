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

    /// <summary>ขอบเขตของ consent ตอนสมัคร = ระดับ **แพลตฟอร์ม** ไม่ใช่ระดับบริษัท
    /// (ผู้ควบคุมข้อมูลตอนสมัครคือผู้ให้บริการ ไม่ใช่ tenant ที่ยังไม่เกิด —
    /// ทางสมัครผ่านคำเชิญไม่สร้างบริษัทเลยด้วยซ้ำ). ใช้ค่าเดียวทุกทางสมัคร
    /// เพื่อให้ค้น "ผู้ใช้คนนี้ยอมรับนโยบายฉบับไหนแล้ว" ได้ด้วย query เดียว
    /// (แนวเดียวกับ <c>ChatbotService.PublicCompanyId</c> ที่มีอยู่แล้ว)</summary>
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

    /// <summary>SHA-256 hex (ตัวพิมพ์ใหญ่) ของ canonical form</summary>
    public static string ComputeHash(
        string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Canonical(email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent))));

    /// <summary>ฝั่งตรวจ — ใช้ canonical ตัวเดียวกับฝั่งเขียนเสมอ.
    /// คืน false เมื่อ field ใดถูกแก้ภายหลัง (tamper-evident)</summary>
    public static bool Verify(
        string? expectedHash, string? email, string purpose, string? policyVersionShown,
        DateTime grantedAt, string? channel, string? ipAddress, string? userAgent)
        => !string.IsNullOrWhiteSpace(expectedHash)
           && string.Equals(expectedHash,
               ComputeHash(email, purpose, policyVersionShown, grantedAt, channel, ipAddress, userAgent),
               StringComparison.OrdinalIgnoreCase);
}
