using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ข้อความ "ทำไมเอกสารลับใบนี้ถูกซ่อน" ตัวเดียว** — ใช้ทั้งหน้าเอกสาร (ใบที่ถูกย่อเป็น stub ใน <c>DocumentService</c>)
/// และทางอ่านอื่นของเอกสารใบเดียวกัน (หัว/เนื้ออีเมล · ส่งอีเมล — ฝ่ายค้านรอบ 193 รอบสอง W2-P6)
///
/// <para>เดิมชั้นความลับบังคับใช้แค่ที่ JSON ของ GET document: <c>GET email-template</c> ส่งชื่อลูกค้า + ยอดรวมของใบลับให้ผู้ที่มีแค่
/// <c>Document.Create</c> และ <c>send-email</c> ส่ง PDF ทั้งใบไปยังอีเมลไหนก็ได้ ⇒ ทางอ่านทุกทางต้องเดินด่านเดียวกัน
/// (<c>ISensitivityService.CanViewAsync</c>) และบอกเหตุผลด้วยข้อความชุดเดียวกัน</para>
/// </summary>
public static class SensitivityAccess
{
    public const string RuleCode = "DOC-SENSITIVITY-HIDDEN";

    /// <summary>ต้องตรวจสิทธิ์ชั้นความลับไหม — <c>None</c> = เอกสารปกติ ไม่ต้องตรวจ</summary>
    public static bool NeedsCheck(SensitivityKind kind) => kind != SensitivityKind.None;

    public static string RedactReason(SensitivityKind kind) => kind switch
    {
        SensitivityKind.Payroll      => "ต้องมีสิทธิ์ดูข้อมูลเงินเดือน (perm:Payroll.View)",
        SensitivityKind.ExecutivePay => "ต้องมีสิทธิ์ดูข้อมูลค่าตอบแทนผู้บริหาร",
        SensitivityKind.HrPersonal   => "ต้องมีสิทธิ์ดูข้อมูลบุคลากร",
        SensitivityKind.Confidential => "ต้องมีสิทธิ์ดูเอกสารลับ (perm:SensitiveDocs.View)",
        _                            => "ต้องมีสิทธิ์เพิ่มเติม"
    };

    /// <summary>ข้อความ 403 ของทางอ่าน/ส่งออกเอกสารลับ (อีเมล ฯลฯ) — เหตุผลเดียวกับที่หน้าเอกสารแสดง + ทางไปต่อ</summary>
    public static string DeniedMessage(SensitivityKind kind, string verb)
        => $"{verb}เอกสารนี้ไม่ได้ — เป็นเอกสารชั้นความลับ ({RedactReason(kind)}) · "
           + "ขอให้เจ้าของบริษัทเปิดสิทธิ์ที่หน้า “สิทธิ์เอกสารลับ”";
}
