using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ด่านผู้ส่งของ e-Tax by Email — ตัวตัดสินตัวเดียว** (รอบ 244 · ETAX_BY_EMAIL_AUDIT E3/E4/E12)
///
/// <para>R4: อีเมล e-Tax by Email ต้องส่ง<b>จากอีเมลที่ลงทะเบียนกับกรมสรรพากร</b> · R10: ไม่พบกฎที่ให้แพลตฟอร์มส่งแทนจากอีเมลของตัวเอง ⇒
/// ทางนี้ใช้ได้เฉพาะเมื่อ (1) บริษัทเปิดโหมด by Email (2) ติ๊กว่าลงทะเบียนกับสรรพากรแล้ว (3) ตั้งอีเมลผู้ส่งที่ลงทะเบียน (4) ตั้งผู้ส่งของบริษัทเองครบ
/// (<c>EmailSenderFactory.HasTenantSender</c>) <b>และทดสอบผ่าน</b> (<c>EmailConfigured</c>) (5) กล่องที่ผู้ให้บริการส่งออกจริง (<see cref="ActualMailbox"/>)
/// ตรงกับอีเมลที่ลงทะเบียน (ไม่สนตัวพิมพ์)</para>
/// <para>ไม่ผ่าน ⇒ ปฏิเสธพร้อมทางไปต่อ · <b>ห้ามถอยไป SMTP ของแพลตฟอร์ม</b> (เดิม <c>GetSenderAsync</c> → <c>GetGlobalFallbackSender</c> ใส่ From ของบริษัท =
/// ปลอมโดเมน + ไม่ได้ส่งจากอีเมลที่ลงทะเบียน) — ล็อกด้วย <c>tools/required_call_site_check.py</c> · G6: pure</para>
/// </summary>
public static class EtaxByEmailSenderGate
{
    public const string RuleCode = "ETAX-EMAIL-SENDER-UNVERIFIED";

    /// <summary>ข้อเท็จจริงของบริษัท ณ ตอนส่ง</summary>
    public sealed record Input(EtaxMode Mode, bool RdRegistered, string? RegisteredSenderEmail, bool HasTenantSender, bool EmailConfigured,
        string? ActualMailbox);

    /// <summary>กล่องที่ผู้ให้บริการอีเมลของบริษัทส่งออกจริง — Graph ส่งในนาม UPN · Gmail ส่งจากที่อยู่ที่ตั้ง · SMTP ใช้ From ที่ตั้ง (ไม่มี = ชื่อผู้ใช้ SMTP) ·
    /// ไม่มีข้อมูล = null (ไม่ใช้ค่าสำรองของระบบ <c>noreply@…</c> — ด่านจะปฏิเสธ)</summary>
    private static string? ActualMailbox(CompanySettings s) => s.EmailProvider switch
    {
        EmailProvider.MicrosoftGraph => Clean(s.EmailMsSenderUpn),
        EmailProvider.GmailApi => Clean(s.EmailFromAddress),
        EmailProvider.Smtp => Clean(s.EmailFromAddress) ?? Clean(s.EmailSmtpUsername),
        _ => null,
    };

    /// <summary>ข้อเท็จจริงจากค่าตั้งของบริษัท — <paramref name="hasTenantSender"/> ต้องมาจาก <c>EmailSenderFactory.HasTenantSender</c> (ตัวตัดสินเดียวกับตัวสร้างผู้ส่ง)</summary>
    public static Input From(CompanySettings s, bool hasTenantSender)
        => new(s.EtaxMode, s.EtaxByEmailRdRegistered, s.EtaxByEmailSenderEmail, hasTenantSender, s.EmailConfigured, ActualMailbox(s));

    /// <summary>เหตุที่ส่งไม่ได้ (ข้อความไทยพร้อมทางไปต่อ) · null = ส่งจาก SMTP/บัญชีอีเมลของบริษัทได้</summary>
    public static string? Refusal(Input i)
    {
        const string Tail = " · ระบบไม่ส่งแทนจากอีเมลของ NextAcc (ต้องส่งจากอีเมลที่ลงทะเบียนกับกรมสรรพากร) · ยังไม่ส่งอีเมล";
        if (i.Mode is not (EtaxMode.ByEmail or EtaxMode.Both))
            return "บริษัทไม่ได้เปิดโหมด e-Tax by Email — ตั้งค่าที่ ตั้งค่า > e-Tax ก่อน (หรือส่งเป็นอีเมลเอกสารธรรมดา)";
        if (!i.RdRegistered)
            return "บริษัทยังไม่ได้ยืนยันว่าลงทะเบียน e-Tax by Email กับกรมสรรพากรแล้ว — ติ๊ก “ลงทะเบียนกับสรรพากรเรียบร้อย” ที่ ตั้งค่า > e-Tax หลังได้รับอนุมัติ" + Tail;
        var registered = Clean(i.RegisteredSenderEmail);
        if (registered == null)
            return "ยังไม่ได้ตั้ง “อีเมลผู้ส่งที่ลงทะเบียนกับสรรพากร” ที่ ตั้งค่า > e-Tax" + Tail;
        if (!i.HasTenantSender)
            return $"บริษัทยังไม่ได้ตั้งค่าการส่งอีเมลของตัวเอง (SMTP / Microsoft 365 / Gmail) — ตั้งที่ ตั้งค่า > อีเมล ให้ส่งออกจาก {registered} แล้วกดทดสอบ" + Tail;
        if (!i.EmailConfigured)
            return $"ค่าตั้งอีเมลของบริษัทยังไม่ผ่านการทดสอบ — กด “ทดสอบการส่ง” ที่ ตั้งค่า > อีเมล ให้ผ่านก่อน (ผู้ส่งต้องเป็น {registered})" + Tail;
        var actual = Clean(i.ActualMailbox);
        if (actual == null || !string.Equals(actual, registered, StringComparison.OrdinalIgnoreCase))
            return $"อีเมลที่ระบบอีเมลของบริษัทส่งออกจริง ({actual ?? "ไม่ทราบ"}) ไม่ตรงกับอีเมลที่ลงทะเบียนกับสรรพากร ({registered}) — "
                + "แก้ “อีเมลผู้ส่ง” ที่ ตั้งค่า > อีเมล (Microsoft 365 = บัญชีผู้ส่ง UPN) ให้ตรง แล้วทดสอบใหม่" + Tail;
        return null;
    }

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
