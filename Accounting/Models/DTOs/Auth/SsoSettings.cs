namespace Accounting.Services.Interfaces;

/// <summary>คีย์ SSO ที่ระบบใช้จริง ณ ขณะนั้น — ไม่มี secret อยู่ในนี้
/// (client id / app id / channel id เป็นค่าสาธารณะโดยธรรมชาติ ต้องส่งให้
/// หน้า login อยู่แล้ว ส่วน secret ไม่เคยออกจาก server)</summary>
public record SsoSettings(
    bool GoogleEnabled, string GoogleClientId,
    bool FacebookEnabled, string FacebookAppId,
    bool LineEnabled, string LineChannelId,
    /// <summary>Callback URL ที่ **เซิร์ฟเวอร์จะใช้ตอนแลก authorization code**
    /// (AppBaseUrl + /login.html) — หน้า login ต้องส่งค่าเดียวกันนี้ตอนพาไป
    /// LINE มิฉะนั้น LINE ตอบ invalid_grant ("แลก code ไม่สำเร็จ")
    ///
    /// เดิมหน้าเว็บคำนวณเอง (<c>location.origin + '/login.html'</c>) ⇒ เปิดด้วย
    /// www. แต่ AppBaseUrl ไม่มี www (หรือกลับกัน) = คนละค่า ⇒ ล็อกอินพังโดยที่
    /// ทั้งสองฝั่ง "ดูถูก" — ตัวเลขคู่ที่ต้องตรงกันต้องมาจากแหล่งเดียว
    /// ว่าง = ยังไม่ได้ตั้ง AppBaseUrl (ผู้ดูแลต้องตั้งก่อนถึงจะใช้ LINE Login ได้)</summary>
    string LoginCallbackUrl = "");
