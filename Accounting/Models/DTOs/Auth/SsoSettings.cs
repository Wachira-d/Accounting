namespace Accounting.Services.Interfaces;

/// <summary>คีย์ SSO ที่ระบบใช้จริง ณ ขณะนั้น — ไม่มี secret อยู่ในนี้
/// (client id / app id / channel id เป็นค่าสาธารณะโดยธรรมชาติ ต้องส่งให้
/// หน้า login อยู่แล้ว ส่วน secret ไม่เคยออกจาก server)</summary>
public record SsoSettings(
    bool GoogleEnabled, string GoogleClientId,
    bool FacebookEnabled, string FacebookAppId,
    bool LineEnabled, string LineChannelId);
