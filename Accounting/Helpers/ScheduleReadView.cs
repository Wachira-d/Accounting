using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 ฝ่ายค้าน R-L4/R-L5: สิ่งที่สมาชิก "ที่ไม่มีสิทธิ์จัดการตารางส่ง" (ไม่ถือ <c>CompanySettings.Edit</c>) เห็นจาก GET กฎ/คิวอีเมล — ตัวตั้งตัวเดียว · pure
///
/// <para>═══ ที่มา ═══ ด่านเขียนของหน้าตารางส่งปิดแล้ว (258-2-1) แต่ GET ยังส่งทุกอย่างให้สมาชิกทุกคน: ผู้รับสำเนา (<c>BccEmails</c>) · เนื้อเทมเพลต ·
/// ผู้รับ + หัวเรื่องของคิว "สลิปเงินเดือน/50 ทวิ" (ข้อมูลส่วนบุคคลของพนักงาน/ผู้ถูกหัก — PDPA ม.37)</para>
///
/// <para>═══ กติกา ═══ ผู้จัดการ (ถือสิทธิ์) เห็นครบ · คนอื่น: กฎไม่มี Bcc/เทมเพลต · แถวคิวชนิดส่วนบุคคลซ่อนผู้รับ (เหลือโดเมน) + หัวเรื่อง ·
/// แถวคิวของเอกสาร/ใบแจ้งยอด (คู่ค้า — สมาชิกเห็นในหน้าเอกสาร/ผู้ติดต่ออยู่แล้ว) คงเดิม · ปุ่มลองใหม่/ยกเลิก = ต้องเป็นผู้จัดการ</para>
/// </summary>
public static class ScheduleReadView
{
    /// <summary>ชนิดแถวคิวที่เป็นข้อมูลส่วนบุคคล (ค่า <c>EmailQueue.EntityType</c> ที่ <c>EmailScheduleService</c> เขียน)</summary>
    public static readonly IReadOnlySet<string> SensitiveQueueKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "PayrollDetail", "WithholdingTaxCert",
    };

    public const string HiddenSubject = "(ซ่อน — เอกสารส่วนบุคคล · ผู้ถือสิทธิ์ “แก้ไขการตั้งค่าบริษัท” เท่านั้นที่เห็น)";

    public const string ReadOnlyNotice =
        "คุณดูตารางส่งได้ แต่แก้กฎ/สั่งคิวไม่ได้ — ต้องมีสิทธิ์ “แก้ไขการตั้งค่าบริษัท” (CompanySettings.Edit) · ขอจากเจ้าของบริษัทที่หน้า “บทบาทและสิทธิ์” · "
        + "ผู้รับสำเนา (Bcc) · เนื้อเทมเพลต · ผู้รับของสลิปเงินเดือน/50 ทวิ ถูกซ่อนไว้";

    public static bool IsSensitiveKind(string? entityType) => entityType != null && SensitiveQueueKinds.Contains(entityType);

    /// <summary>อีเมลที่ซ่อนชื่อ (เหลือโดเมน) — หลายที่อยู่คั่น , หรือ ; ซ่อนทุกตัว · ว่าง = ว่าง</summary>
    public static string MaskEmails(string? emails)
    {
        if (string.IsNullOrWhiteSpace(emails)) return "";
        return string.Join(", ", emails.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => { var at = e.LastIndexOf('@'); return at <= 0 ? "•••" : "•••" + e[at..]; }));
    }

    public static string? Recipient(string? toEmail, string? entityType, bool canManage)
        => canManage || !IsSensitiveKind(entityType) ? toEmail : MaskEmails(toEmail);

    public static string? Subject(string? subject, string? entityType, bool canManage)
        => canManage || !IsSensitiveKind(entityType) ? subject : HiddenSubject;

    /// <summary>กฎที่จะส่งออกทาง GET — ไม่ใช่ผู้จัดการ ⇒ ล้าง Bcc + เทมเพลต (แก้บนตัวที่ส่งเข้ามา: ผู้เรียกต้องส่งแถวที่ไม่ถูกติดตาม) · คืนตัวเดิม</summary>
    public static EmailScheduleRule Rule(EmailScheduleRule rule, bool canManage)
    {
        if (canManage) return rule;
        rule.BccEmails = null;
        rule.SubjectTemplate = null;
        rule.BodyTemplate = null;
        return rule;
    }
}
