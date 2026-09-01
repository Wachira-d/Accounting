using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>"บัญชีนี้เข้าสู่ระบบได้ไหม" — ด่านเดียวของทุกทางเข้า
/// (รหัสผ่าน · SSO · refresh token)
///
/// ⚠️ ที่มา: `User.Status` มีมาตั้งแต่ต้นและถูกเซ็ตเป็น Inactive จาก **4 ที่**
/// (แอดมินปิดบัญชี · `PdpaService` erasure · และ `PayrollService` ที่ตั้งให้
/// อัตโนมัติเมื่อ **พนักงานลาออก/ถูกลบ**) — แต่ `LoginAsync`/`SsoLoginAsync`
/// ไม่เคยอ่านค่านี้เลยสักบรรทัด ⇒ **พนักงานที่ลาออกแล้วยังล็อกอินเข้าระบบได้**
/// เป็นกรณีคลาสสิกของ "control ที่ไม่มีใครเรียก = ไม่มี control"
///
/// คืน **เหตุผลเป็นข้อความ** ไม่ใช่ bool เปล่า ๆ — ไม่งั้นแต่ละหน้าจอจะไปแต่ง
/// คำเอง (= สำเนามือชุดที่สอง รอ drift)
/// </summary>
public static class UserLoginPolicy
{
    /// <param name="status">สถานะบัญชีปัจจุบัน</param>
    /// <param name="viaVerifiedSso">เข้ามาด้วย SSO ที่ provider ยืนยันอีเมลแล้ว —
    /// เท่ากับพิสูจน์การเข้าถึงอีเมล จึงใช้แทนลิงก์ยืนยันได้ (ดู <see cref="ActivatesPendingVerification"/>)</param>
    public static (bool Can, string? Reason) Evaluate(UserStatus status, bool viaVerifiedSso)
    {
        switch (status)
        {
            case UserStatus.Active:
                return (true, null);

            case UserStatus.Inactive:
                return (false,
                    "บัญชีนี้ถูกปิดการใช้งานแล้ว — หากคิดว่าผิดพลาด กรุณาติดต่อผู้ดูแลระบบของบริษัท");

            case UserStatus.Suspended:
                return (false,
                    "บัญชีนี้ถูกระงับการใช้งานชั่วคราว — กรุณาติดต่อผู้ดูแลระบบ");

            case UserStatus.PendingVerification:
                // SSO ที่ยืนยันอีเมลแล้ว = พิสูจน์ว่าเข้าถึงอีเมลได้จริง ซึ่งเป็น
                // สิ่งเดียวกับที่ลิงก์ตั้งรหัสผ่านต้องการ → ให้ผ่านแล้วเลื่อนเป็น Active
                if (viaVerifiedSso) return (true, null);
                // บัญชีที่แอดมินสร้างให้ได้รหัสผ่าน**สุ่มที่ไม่มีใครรู้** อยู่แล้ว
                // (AdminController) ⇒ ด่านนี้ไม่ได้กันใครที่เคยเข้าได้ออกไป
                // แต่เปลี่ยน "รหัสผ่านไม่ถูกต้อง" ที่ชวนงงเป็นคำแนะนำที่ทำตามได้
                return (false,
                    "บัญชีนี้ยังไม่ได้ยืนยันอีเมล — กรุณากด \"ลืมรหัสผ่าน\" เพื่อรับลิงก์ตั้งรหัสผ่านครั้งแรก");

            default:
                return (false, "บัญชีนี้ยังไม่พร้อมใช้งาน — กรุณาติดต่อผู้ดูแลระบบ");
        }
    }

    /// <summary>เข้าด้วย SSO ที่ยืนยันอีเมลแล้ว + บัญชียัง PendingVerification
    /// → เลื่อนเป็น Active (ผู้เรียกต้องเซ็ต <c>EmailVerified = true</c> ด้วย)</summary>
    public static bool ActivatesPendingVerification(UserStatus status, bool viaVerifiedSso)
        => status == UserStatus.PendingVerification && viaVerifiedSso;

    /// <summary>บัญชีที่สร้างผ่าน SSO มี <c>PasswordHash = ""</c> — ส่งเข้า
    /// <c>BCrypt.Verify</c> จะโยน <c>SaltParseException</c> ซึ่ง middleware แปลง
    /// เป็น **500 "เกิดข้อผิดพลาดภายในระบบ"** (ไม่ใช่ 401) ⇒ ผู้ใช้ไม่รู้ว่าต้อง
    /// กดปุ่ม provider แทน และ ErrorLogs รกด้วยรายการที่ไม่ใช่บั๊ก</summary>
    public static string PasswordLoginUnavailableMessage(string? linkedProvider)
        => string.IsNullOrWhiteSpace(linkedProvider)
            ? "บัญชีนี้ยังไม่ได้ตั้งรหัสผ่าน — กรุณากด \"ลืมรหัสผ่าน\" เพื่อตั้งรหัสผ่านครั้งแรก"
            : $"บัญชีนี้เข้าสู่ระบบด้วย {SsoIdentityPolicy.DisplayName(linkedProvider)} — "
              + $"กรุณากดปุ่ม {SsoIdentityPolicy.DisplayName(linkedProvider)} "
              + "หรือกด \"ลืมรหัสผ่าน\" เพื่อตั้งรหัสผ่านสำหรับเข้าด้วยอีเมล";
}
