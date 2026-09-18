namespace Accounting.Helpers;

/// <summary>
/// **บันทึก "การเงียบ" เรื่องหัก ณ ที่จ่าย** — ข้อความบนเอกสาร + รหัสกฎสำหรับ audit
///
/// ═══ ทำไมต้องมีไฟล์นี้ ═══
/// เมื่อชั้นเรียนรู้ (นักเรียน/AI) ตอบว่า "ใบนี้ไม่เข้าข่าย" ระบบจะ **ไม่เตือนเลย**
/// ตามคำตัดสินเจ้าของโปรเจกต์ (2026-09-18) — นั่นคือ **สถานะปลายทางที่ระบบประทับเอง**
/// (หลักการข้อ 3 ใน กฎเหล็ก #4 F2) ⇒ ต้องตามรอยได้ว่าใครตอบ · ตอบว่าอะไร · มั่นใจแค่ไหน
/// ไม่งั้นผู้สอบบัญชี/สรรพากรถามว่า "ทำไมใบนี้ไม่มีคำเตือน" แล้วไม่มีอะไรตอบได้
/// (§54 ผู้จ่ายเป็นผู้รับผิดในภาษีที่ไม่ได้หัก — ความเสี่ยงตกที่ลูกค้าเรา)
///
/// ═══ ทำไม Compose ไม่มีเวลา ═══
/// ด่านคำเตือนถูกเรียก**ทุกครั้งที่กดอนุมัติ** (`CollectApprovalWarningsAsync` รันก่อน
/// throw 422 และรันซ้ำเมื่อผู้ใช้กดยืนยัน) ⇒ ถ้าข้อความมี timestamp จะต่อท้าย
/// <c>InternalNotes</c> ใหม่ทุกรอบจนบวม และ "ช่องข้อความที่เป็นที่สะสม" นี้มีด่านอื่น
/// อ่านธงจากมันอยู่ (<c>tools/flag_field_overwrite_check.py</c>)
/// ⇒ **เวลา/ผู้กระทำอยู่ใน AuditLog** (append-only + hash chain) ส่วน
/// <c>InternalNotes</c> เก็บแค่ "สถานะปัจจุบัน" ที่ผู้ใช้เปิดดูเห็น — แยกหน้าที่กันชัด
/// </summary>
public static class WhtAdviceNote
{
    /// <summary>ป้ายนำหน้าบรรทัด — ใช้ค้นว่าเคยบันทึกไว้แล้วหรือยัง</summary>
    private const string Marker = "[WHT-ADVICE]";

    /// <summary>รหัสกฎสำหรับ audit (กฎเหล็ก #2 ข้อ M — ทุก validation rule ต้อง log
    /// <c>RuleCode</c> + <c>LegalReference</c>)</summary>
    public const string SilentRuleCode = "WHT-ADVICE-SILENT";

    /// <summary>รหัสกฎของการเงียบเพราะ<b>ไม่มีเหตุให้สงสัย</b> (คำตัดสินเจ้าของ รอบ 179:
    /// "มีเหตุให้สงสัยว่าเป็นค่าจ้าง หรือ ค่าบริการ ค่อยขึ้นเตือนหัก")
    ///
    /// <para>แยกจาก <see cref="SilentRuleCode"/> เพราะเป็นคนละเหตุผล: อันนั้นคือ
    /// "ถามแล้วโมเดลว่าไม่เข้าข่าย" อันนี้คือ "ไม่มีอะไรให้ต้องถามตั้งแต่ต้น" —
    /// ผู้สอบบัญชีที่ไล่ดูต้องแยกสองกรณีนี้ออกจากกันได้</para></summary>
    public const string NoSuspicionRuleCode = "WHT-NO-SUSPICION";

    /// <summary>ข้อความที่ต่อลง <c>Document.InternalNotes</c> เมื่อระบบเงียบเพราะ
    /// <b>ไม่มีเหตุ</b> — ไม่มีเวลาในตัวด้วยเหตุผลเดียวกับ <see cref="Compose"/></summary>
    public static string ComposeNoSuspicion(string reason)
        => $"{Marker} ไม่เตือนเรื่องหัก ณ ที่จ่าย — {reason}";

    /// <summary>มาตราที่การตัดสินใจนี้อ้างอิง — ท.ป.4/2528 ข้อ 12 (เกณฑ์ ฿1,000 สะสม
    /// ต่อคู่สัญญา) + §54 (ผู้จ่ายรับผิดในภาษีที่ไม่ได้หัก)</summary>
    public const string SilentLegalReference = "RD-TP4/2528-12 / RD-54";

    /// <summary>ข้อความที่ต่อลง <c>Document.InternalNotes</c> เมื่อระบบเลือกเงียบ
    /// — **ห้ามใส่เวลา/ผู้กระทำ** (ดูเหตุผลใน doc ของคลาส)</summary>
    public static string Compose(bool usedAi, string? answer, decimal? confidence)
    {
        var who = usedAi ? "AI" : "โมเดลในระบบ";
        var what = string.IsNullOrWhiteSpace(answer) ? "ไม่ระบุประเภทเงินได้" : answer.Trim();
        var sure = confidence.HasValue
            ? $" ความมั่นใจ {confidence.Value:P0}"
            : string.Empty;
        return $"{Marker} ไม่เตือนเรื่องหัก ณ ที่จ่าย — {who} ประเมินว่าไม่เข้าข่าย ({what}){sure}";
    }

    /// <summary>ควรต่อข้อความนี้ลงบันทึกภายในหรือไม่ — <c>false</c> เมื่อบรรทัด
    /// **เดียวกันเป๊ะ** มีอยู่แล้ว (กดอนุมัติซ้ำ) · <c>true</c> เมื่อคำตอบเปลี่ยน
    /// (คนละคำตอบ = เหตุการณ์ใหม่ ต้องเห็นทั้งสองบรรทัด ห้ามทับของเดิม)</summary>
    public static bool ShouldAppend(string? internalNotes, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return false;
        if (string.IsNullOrWhiteSpace(internalNotes)) return true;
        return !internalNotes.Contains(note.Trim(), StringComparison.Ordinal);
    }
}
