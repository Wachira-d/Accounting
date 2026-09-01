namespace Accounting.Helpers;

/// <summary>ผู้ใช้ผ่าน OAuth มาแล้วจริง แต่ระบบสร้าง/จับคู่บัญชีให้ไม่ได้เพราะ
/// provider ไม่ได้ให้อีเมลมา (LINE ที่ channel ยังไม่ได้รับสิทธิ์ email)
///
/// **ไม่ใช่ error ที่จบตรงนั้น** — เป็น "ต้องไปต่อที่หน้าสมัคร" พร้อมของที่ดึงมาได้
/// (ชื่อ/รูป) และ <see cref="Ticket"/> ที่เซ็นไว้เพื่อผูกบัญชีให้อัตโนมัติหลังสมัคร
/// เสร็จ — ตามกฎ "ทุกด่านที่ปฏิเสธ ต้องตอบให้ได้ว่าผู้ใช้ทำอะไรต่อได้"
///
/// สืบทอด <see cref="BusinessRuleException"/> เพื่อว่าถ้าหลุดไปถึง middleware
/// (มีคนเรียก service ตรงโดยไม่ได้ดักที่ controller) ผู้ใช้ยังได้ 400 + ข้อความไทย
/// ไม่ใช่ 500
/// </summary>
public class SsoSignupRequiredException : BusinessRuleException
{
    public string Provider { get; }
    public string Ticket { get; }
    public string? SuggestedName { get; }
    public string? PictureUrl { get; }

    public SsoSignupRequiredException(
        string provider, string ticket, string? suggestedName, string? pictureUrl)
        : base(SsoIdentityPolicy.SignupNeedsEmailMessage(provider), "SSO-NO-EMAIL")
    {
        Provider = provider;
        Ticket = ticket;
        SuggestedName = suggestedName;
        PictureUrl = pictureUrl;
    }
}
