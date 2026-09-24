using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>ใคร "ผ่าน" คำเตือนก่อนอนุมัติ — ต้องบอกความจริงใน audit ว่าเป็นคนหรือระบบ (DECISION_AUDIT R1: สถานะปลายทางประทับเอง)</summary>
public enum ApprovalAckSource
{
    /// <summary>ยังไม่มีใครรับทราบ — มีคำเตือน = หยุดและคืนรายการให้ผู้เรียก</summary>
    None = 0,
    /// <summary>ผู้ใช้เห็นรายการคำเตือนแล้วกด "รับทราบ" เอง (เว็บหน้าเอกสาร · เว็บ workflow ขั้นสุดท้าย · มือถือ)</summary>
    User = 1,
    /// <summary>ระบบ/workflow อนุมัติต่อโดยไม่มีคนเห็นคำเตือน (ลายเซ็นครบ · ใบสำคัญจ่ายเงินสดอัตโนมัติ · ใบแทน) —
    /// ผ่านได้เฉพาะคำเตือนทั่วไป · คำเตือน "ยอดจากสแกนไม่ตรงกระดาษ" ต้องมีคนรับทราบเสมอ (คำตัดสินเจ้าของข้อ 12)</summary>
    SystemWorkflow = 2,
    /// <summary>ระบบปลายทางผ่าน API v1 — คำตัดสินข้อ 12: [Σ-GAP] ห้ามขัดจังหวะ API ⇒ ผ่านเฉพาะคำเตือนชุดนั้น แล้วคืนธงในคำตอบ ·
    /// คำเตือนชนิดอื่นยังหยุดตามเดิม</summary>
    ApiClient = 3,
}

/// <summary>
/// **ตัวตัดสินตัวเดียวว่า "คำเตือนก่อนอนุมัติข้อไหนยังไม่มีใครรับทราบ" และข้อความ/รหัสกฎที่ต้องลงร่องรอย** (pure · ไม่ throw)
///
/// ═══ ที่มา (ฝ่ายค้าน C5/C6 รอบ 193) ═══
/// <para>workflow อนุมัติหลายขั้นบนเว็บ (<c>ApprovalService</c>) และเส้นลายเซ็น (<c>SignatureApprovalService</c>) ส่ง
/// <c>acknowledgeWarnings: true</c> ⇒ audit <c>APPROVE-ACK-WARNINGS</c> + หมายเหตุ "ยืนยันโดย {userId}" ทั้งที่ไม่มีใครเห็นข้อความ
/// <c>[Σ-GAP]</c> เลย (ระบบประทับ "รับทราบ" แทนคน) · ขณะที่ workflow เดียวกันผ่านมือถือหยุดให้กดรับทราบ ⇒ สองช่องทางไม่เหมือนกัน ·
/// และ API v1 เรียกอนุมัติแบบไม่รับทราบก่อน (เรียก AI 8 วินาทีเพื่อเสริมคำเตือน) แล้วโยนคำตอบ AI ทิ้ง (กฎเหล็ก #1)</para>
/// <para>ตอนนี้: ผู้เรียกบอก<b>แหล่ง</b>ของการรับทราบ (<see cref="ApprovalAckSource"/>) · ตัวนี้บอกว่าข้อไหนยังต้องหยุด ·
/// หมายเหตุ/รหัสกฎแยก "คนรับทราบ" ออกจาก "ระบบส่งผ่าน" ชัดเจน</para>
/// </summary>
public static class ApprovalAcknowledgement
{
    /// <summary>ผู้ใช้เห็นรายการแล้วกดรับทราบ (รหัสเดิม — รายงาน/ตัวค้นหาเดิมยังใช้ได้)</summary>
    public const string UserRuleCode = "APPROVE-ACK-WARNINGS";

    /// <summary>ระบบ/workflow ส่งผ่านคำเตือนโดยไม่มีผู้ใช้เห็น</summary>
    public const string SystemRuleCode = "APPROVE-SYSTEM-PASSED-WARNINGS";

    /// <summary>API ส่งผ่านคำเตือน [Σ-GAP] และคืนรายการให้ระบบปลายทางในคำตอบ</summary>
    public const string ApiRuleCode = "APPROVE-API-RETURNED-WARNINGS";

    /// <summary>คำเตือนที่ยังไม่มีใครรับทราบตามแหล่ง — ว่าง = อนุมัติต่อได้ · ไม่ว่าง = ต้องหยุด (throw คำเตือนชุดนี้)</summary>
    public static IReadOnlyList<string> Unacknowledged(ApprovalAckSource source, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return Array.Empty<string>();
        return source switch
        {
            ApprovalAckSource.User => Array.Empty<string>(),
            ApprovalAckSource.SystemWorkflow => warnings.Where(OcrApprovalGapWarning.IsGapWarning).ToList(),
            ApprovalAckSource.ApiClient => warnings.Where(w => !OcrApprovalGapWarning.IsGapWarning(w)).ToList(),
            _ => warnings,
        };
    }

    /// <summary>รหัสกฎของร่องรอยเมื่ออนุมัติทั้งที่มีคำเตือน</summary>
    public static string RuleCode(ApprovalAckSource source) => source switch
    {
        ApprovalAckSource.SystemWorkflow => SystemRuleCode,
        ApprovalAckSource.ApiClient => ApiRuleCode,
        _ => UserRuleCode,
    };

    /// <summary>คนเป็นผู้รับทราบจริงไหม (ลงใน audit เป็นช่องแยก — ห้ามให้ผู้อ่านเดาจากชื่อผู้อนุมัติ)</summary>
    public static bool AcknowledgedByPerson(ApprovalAckSource source) => source == ApprovalAckSource.User;

    /// <summary>หมายเหตุภายในบนเอกสาร (InternalNotes — ไม่พิมพ์ลงกระดาษ)</summary>
    /// <param name="source">แหล่งของการรับทราบ</param>
    /// <param name="warnings">คำเตือนทั้งหมดที่ผ่าน</param>
    /// <param name="approvedBy">ผู้อนุมัติ (id ผู้ใช้ หรือป้ายระบบ)</param>
    /// <param name="utcNow">เวลา UTC (แสดงเป็นเวลาไทย)</param>
    public static string Note(ApprovalAckSource source, IReadOnlyList<string> warnings, string approvedBy, DateTime utcNow)
    {
        var at = utcNow.AddHours(7).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var head = source switch
        {
            ApprovalAckSource.SystemWorkflow => "— คำเตือนตอนอนุมัติ: ระบบ workflow ส่งผ่าน (ไม่มีผู้ใช้เห็นรายการนี้ก่อนอนุมัติ) —",
            ApprovalAckSource.ApiClient => "— คำเตือนตอนอนุมัติผ่าน API: คืนรายการให้ระบบปลายทางในคำตอบ (ไม่มีผู้ใช้กดรับทราบ) —",
            _ => "— รับทราบคำเตือนตอนอนุมัติ —",
        };
        var tail = source == ApprovalAckSource.User
            ? $"(ยืนยันโดย {approvedBy} เมื่อ {at} น. เวลาไทย)"
            : $"(อนุมัติโดย {approvedBy} เมื่อ {at} น. เวลาไทย — ไม่ใช่การรับทราบของผู้ใช้)";
        return head + "\n" + string.Join("\n", warnings.Select(w => "• " + w)) + "\n" + tail;
    }
}
