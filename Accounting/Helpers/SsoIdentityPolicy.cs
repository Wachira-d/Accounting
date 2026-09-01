namespace Accounting.Helpers;

/// <summary>ตัวตนที่ provider คืนมาหลัง verify token
///
/// <para><b>Email อาจว่างได้</b> — LINE คืนอีเมลเฉพาะเมื่อ channel ผ่านการอนุมัติ
/// สิทธิ์ email (ต้องยื่นเอกสาร) ซึ่งส่วนใหญ่ยังไม่ผ่าน. เดิมระบบ throw ทิ้งทันที
/// เมื่อไม่มีอีเมล ⇒ ปุ่ม LINE ใช้ไม่ได้เลยแม้แต่กับคนที่ผูกบัญชีไว้แล้ว
/// ทั้งที่ <c>Id</c> (LINE userId) เป็นตัวระบุตัวตนที่มั่นคงกว่าอีเมลด้วยซ้ำ
/// (ไม่เปลี่ยนแม้ผู้ใช้เปลี่ยนอีเมล)</para>
/// </summary>
public sealed record SsoIdentity(
    string Provider,
    string Id,
    string? Email,
    string? Name,
    string? PictureUrl,
    bool EmailVerified);

/// <summary>กติกาเดียวของระบบว่า "ตัวตนจาก SSO เชื่อได้แค่ไหน"
///
/// ⚠️ ที่มา: <c>SsoLoginAsync</c> เดิม **ผูก SSO เข้าบัญชีเดิมด้วยอีเมลอย่างเดียว**
/// (เจอ user ที่อีเมลตรง → เซ็ต AuthProvider/AuthProviderId แล้วล็อกอินให้เลย)
/// โดยไม่เคยถามว่า provider ยืนยันอีเมลนั้นแล้วหรือยัง ⇒ ใครก็ตามที่สร้างบัญชี
/// ฝั่ง provider ให้อีเมลตรงกับผู้ใช้ของเรา จะเข้าถึงข้อมูลทั้ง tenant ได้
/// (CWE-287 account pre-hijacking)
///
/// หลักการ: **SSO = ทางเข้าที่พิสูจน์ความเป็นเจ้าของอีเมล** จะผูกเข้าบัญชีที่มี
/// อยู่แล้วได้ก็ต่อเมื่อ provider ยืนยันอีเมลนั้นจริง — ไม่ยืนยัน ≠ ปฏิเสธทิ้ง
/// แต่ต้องเดินเส้น "ส่งลิงก์ยืนยันไปที่อีเมล" แทน (ห้ามตันเฉย ๆ)
/// </summary>
public static class SsoIdentityPolicy
{
    public const string Google = "Google";
    public const string Facebook = "Facebook";
    public const string Line = "Line";

    public static bool IsSupported(string? provider)
        => provider == Google || provider == Facebook || provider == Line;

    /// <summary>ชื่อที่เอาไปโชว์ผู้ใช้ (LINE เขียนตัวใหญ่ทั้งคำตามแบรนด์)</summary>
    public static string DisplayName(string provider)
        => provider == Line ? "LINE" : provider;

    /// <summary>provider ตัวนี้ให้ "สัญญาณยืนยันอีเมล" มาด้วยไหม
    ///
    /// - Google: tokeninfo คืน <c>email_verified</c> → เชื่อค่าที่ได้มา
    /// - LINE: verify endpoint คืน email เฉพาะเมื่อ channel ได้สิทธิ์ email ซึ่ง
    ///   LINE ยืนยันกับเจ้าของแล้ว → มี email = ยืนยันแล้ว
    /// - Facebook: graph <c>me?fields=email</c> **ไม่มีสัญญาณใด ๆ** → ถือว่าไม่ยืนยัน
    ///   (ไม่ได้แปลว่าใช้ไม่ได้ — แปลว่าต้องยืนยันผ่านอีเมลก่อนผูกกับบัญชีเดิม)
    /// </summary>
    public static bool ProvidesEmailVerification(string provider)
        => provider == Google || provider == Line;

    /// <summary>ผูกเข้า **บัญชีที่มีอยู่แล้ว** ได้ทันทีหรือไม่
    /// false = ต้องส่งลิงก์ยืนยันไปที่อีเมลก่อน (ดู <see cref="LinkNeedsConfirmationMessage"/>)</summary>
    public static bool CanAutoLink(string provider, bool providerSaysEmailVerified)
        => ProvidesEmailVerification(provider) && providerSaysEmailVerified;

    /// <summary>สร้างผู้ใช้ **ใหม่** ด้วยอีเมลจาก provider ที่ยืนยันไม่ได้ ทำได้
    /// (ไม่มีบัญชีเดิมให้ยึด) แต่ห้ามตั้ง <c>EmailVerified = true</c> ให้ฟรี ๆ —
    /// ไม่งั้นครั้งหน้าจะกลายเป็นบัญชีที่ "ยืนยันแล้ว" ทั้งที่ไม่มีใครยืนยัน</summary>
    public static bool MarksEmailVerifiedOnSignup(string provider, bool providerSaysEmailVerified)
        => CanAutoLink(provider, providerSaysEmailVerified);

    public static string LinkNeedsConfirmationMessage(string provider, string email)
        => $"อีเมล {email} มีบัญชีอยู่แล้วในระบบ — เราส่งลิงก์ยืนยันการผูกบัญชี "
           + $"{DisplayName(provider)} ไปที่อีเมลนี้แล้ว กรุณาเปิดอีเมลแล้วกดยืนยัน "
           + "(ลิงก์มีอายุ 1 ชั่วโมง) จากนั้นกดเข้าสู่ระบบด้วยปุ่มเดิมอีกครั้ง";

    /// <summary>ไม่มีอีเมลจาก provider (LINE ที่ยังไม่ได้สิทธิ์ email) และยังไม่เคย
    /// ผูกบัญชี — ไปกรอกอีเมลที่หน้าสมัคร โดยพา**ตัวตนที่ยืนยันแล้ว**ไปด้วย
    /// (ticket) เพื่อผูกให้อัตโนมัติหลังสมัครเสร็จ ผู้ใช้จะได้ไม่ต้องกด SSO ซ้ำ</summary>
    public static string SignupNeedsEmailMessage(string provider)
        => $"{DisplayName(provider)} ไม่ได้ให้อีเมลมา (channel ยังไม่ได้รับสิทธิ์ email) — "
           + "พาไปหน้าสมัครสมาชิกเพื่อกรอกอีเมล แล้วระบบจะผูกบัญชี "
           + $"{DisplayName(provider)} ให้อัตโนมัติ";

    /// <summary>ผู้ใช้ใหม่ที่กด SSO ที่หน้า login (ไม่มีช่องติ๊กยินยอม) — ส่งกลับไป
    /// หน้าสมัคร ซึ่งเป็นที่เดียวที่มีข้อความให้อ่านจริง (PDPA ม.19)</summary>
    public const string NoAccountMessage =
        "ยังไม่มีบัญชีสำหรับอีเมลนี้ — กรุณาสมัครสมาชิกที่หน้าสมัคร "
        + "เพื่ออ่านและยอมรับข้อกำหนดการใช้งานและนโยบายความเป็นส่วนตัวก่อน "
        + "(ถ้าคุณเคยสมัครด้วยอีเมลอื่น ให้เข้าสู่ระบบด้วยอีเมลนั้นก่อน "
        + "แล้วผูกบัญชีที่หน้าโปรไฟล์ — จะได้ไม่มีสองบัญชี)";
}
