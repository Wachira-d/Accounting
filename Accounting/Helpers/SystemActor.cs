namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (B-L4 · คำตัดสิน 259-4-4) — ชื่อผู้กระทำของงานอัตโนมัติ (CreatedBy/PerformedBy/ReconciledBy ที่ไม่ใช่ผู้ใช้) <b>ตัวตั้งเดียว</b>
/// <para>เดิม 6 แบบ: <c>"System"</c> (รายการประจำ) · <c>"autopilot"</c> (ใบเสนอราคาแปลงอัตโนมัติ) · <c>"bank-auto-settle"</c> กับ <c>"system:bank-auto-settle"</c>
/// (เงินโอนปิดหนี้ — สองค่าในงานเดียว) · <c>"ระบบ (ส่งอัตโนมัติหลังรับชำระ)"</c> · <c>"email-schedule"</c> ⇒ กรอง audit "ระบบทำ" ด้วยเงื่อนไขเดียวไม่ได้</para>
/// <para>ตอนนี้: ค่าใหม่ทุกตัว = <c>"system:&lt;งาน&gt;"</c> (กรองด้วย <see cref="Prefix"/> ตัวเดียว · SQL <c>LIKE 'system:%'</c>) · แถวเก่ายังอ่านได้ผ่าน <see cref="IsSystem"/>
/// (ค่าเดิมใน <see cref="Legacy"/> — ไม่ย้อนแก้ข้อมูลที่บันทึกแล้ว: ร่องรอยเดิมคงตามที่เกิด) · ป้ายภาษาไทยสำหรับแสดงผล = <see cref="Display"/></para>
/// <para>ไม่รวม: ป้ายของเส้นที่มีตัวแยกช่องทางเฉพาะ (<c>system:settlement:*</c> · <c>system:expense-claim*</c> · <c>api:*</c> · <c>OCR-*</c> — ดู <c>DocumentCreationChannelRule</c>)</para>
/// </summary>
public static class SystemActor
{
    public const string Prefix = "system:";

    /// <summary>รอบอัตโนมัติของรายการประจำ (เดิม <c>"System"</c>)</summary>
    public const string Recurring = "system:recurring";
    /// <summary>ใบเสนอราคาที่ลูกค้ายอมรับ → แปลงอัตโนมัติ (เดิม <c>"autopilot"</c>)</summary>
    public const string QuotationAutopilot = "system:quotation-autopilot";
    /// <summary>เงินโอนเข้าปิดหนี้อัตโนมัติ (เดิม <c>"bank-auto-settle"</c> เป็นผู้บันทึกรับชำระ + <c>"system:bank-auto-settle"</c> เป็นผู้กระทบยอด)</summary>
    public const string BankAutoSettle = "system:bank-auto-settle";
    /// <summary>งานหลังรับชำระ (ออก/ส่งใบเสร็จ) (เดิม <c>"ระบบ (ส่งอัตโนมัติหลังรับชำระ)"</c>)</summary>
    public const string PostPayment = "system:post-payment";
    /// <summary>ตารางส่งเอกสาร (เดิม <c>"email-schedule"</c>)</summary>
    public const string EmailSchedule = "system:email-schedule";
    /// <summary>รอบ 264 P4 (คำตัดสิน 264-5-10): รายการเงินออกในรายการเดินบัญชีผูกกับการจ่ายที่ท่อคู่ค้าบันทึกจากหลักฐานบรรทัดนั้น (<c>BankPaymentLineEvidence.BindAsync</c>)</summary>
    public const string PartnerPayLine = "system:partner-pay-line";

    /// <summary>ค่าเดิมก่อนรอบ 259 ที่ยังอยู่ในฐาน — อ่านได้ ห้ามเขียนใหม่</summary>
    public static readonly IReadOnlySet<string> Legacy = new HashSet<string>(StringComparer.Ordinal)
    {
        "System", "autopilot", "bank-auto-settle", "ระบบ (ส่งอัตโนมัติหลังรับชำระ)", "email-schedule",
    };

    /// <summary>ผู้กระทำนี้คืองานอัตโนมัติของระบบไหม (ค่าใหม่ + ค่าเดิม)</summary>
    public static bool IsSystem(string? actor)
        => !string.IsNullOrEmpty(actor) && (actor.StartsWith(Prefix, StringComparison.Ordinal) || Legacy.Contains(actor));

    /// <summary>ป้ายภาษาไทยของผู้กระทำที่เป็นระบบ · ไม่ใช่ระบบ = คืนค่าเดิม</summary>
    public static string? Display(string? actor) => actor switch
    {
        Recurring or "System" => "ระบบ (รายการประจำ)",
        QuotationAutopilot or "autopilot" => "ระบบ (แปลงใบเสนอราคาอัตโนมัติ)",
        BankAutoSettle or "bank-auto-settle" => "ระบบ (เงินโอนปิดหนี้อัตโนมัติ)",
        PostPayment or "ระบบ (ส่งอัตโนมัติหลังรับชำระ)" => "ระบบ (ส่งอัตโนมัติหลังรับชำระ)",
        EmailSchedule or "email-schedule" => "ระบบ (ตารางส่งเอกสาร)",
        PartnerPayLine => "ระบบ (รายการเดินบัญชียืนยันการจ่ายคู่ค้า)",
        _ when IsSystem(actor) => "ระบบ",
        _ => actor,
    };
}
