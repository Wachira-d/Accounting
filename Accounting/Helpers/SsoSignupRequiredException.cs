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
    /// <summary>อีเมลที่ provider ให้มา (null = LINE ที่ยังไม่ได้สิทธิ์ email) —
    /// หน้าสมัครใช้เติมช่องอีเมลให้ และใช้เป็นค่าเริ่มต้นของช่อง "ผูกกับบัญชีเดิม"</summary>
    public string? Email { get; }

    public SsoSignupRequiredException(
        string provider, string ticket, string? suggestedName, string? pictureUrl)
        : this(provider, ticket, suggestedName, pictureUrl, null,
               SsoIdentityPolicy.SignupNeedsEmailMessage(provider), "SSO-NO-EMAIL") { }

    /// <summary>กรณี provider ให้อีเมลมาแล้วแต่ **ยังไม่มีบัญชี** — เดิม throw ข้อความ
    /// ให้ผู้ใช้ไปกดปุ่ม "ไปหน้าสมัคร" เอง ตอนนี้พาไปเลยพร้อมชื่อ/อีเมล/รูปที่ดึงได้
    /// (ผู้ใช้กด SSO มาแล้ว ไม่ควรต้องกดซ้ำหรือพิมพ์ซ้ำ)</summary>
    public SsoSignupRequiredException(
        string provider, string ticket, string? suggestedName, string? pictureUrl,
        string? email, string message, string ruleCode)
        : base(message, ruleCode)
    {
        Provider = provider;
        Ticket = ticket;
        SuggestedName = suggestedName;
        PictureUrl = pictureUrl;
        Email = email;
    }
}
