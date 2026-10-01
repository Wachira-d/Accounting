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
    /// <summary>ระบบปลายทางผ่าน API v1 — คำตัดสินข้อ 12: [Σ-GAP] ยอดไม่ตรงกระดาษ ห้ามขัดจังหวะ API ⇒ ผ่านเฉพาะคำเตือนชุดนั้น แล้วคืนธงในคำตอบ ·
    /// คำเตือนชนิดอื่นยังหยุดตามเดิม · <b>คำเตือน VAT ไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้ = หยุด</b> (คำตัดสินรอบ 198 ข้อ 6 · รอบ 199 ฝ่ายค้าน B-1 —
    /// เดิมผ่านเพราะ <see cref="OcrApprovalGapWarning.IsGapWarning"/> ถูกขยายให้รวมชุด VAT แล้ว API อ้างข้อ 12 ซึ่งครอบแค่ [Σ-GAP])</summary>
    ApiClient = 3,
    /// <summary>รอบ 201 (คำตัดสินข้อ 110 · ฝ่ายค้าน TX RTX-1): ทางเข้าอัตโนมัติที่ไม่มีหน้าจอให้คนเห็นคำเตือน (ใบประจำ · ใบเบิก · เบิกล่วงหน้า · LINE ·
    /// OCR อนุมัติอัตโนมัติ · integration — ผู้เรียกรูปสามอาร์กิวเมนต์ของ <c>ApproveDocumentAsync</c>) — คำเตือนทั่วไปหยุดเหมือน <see cref="None"/>
    /// (พฤติกรรมเดิม) · <b>ข้อสังเกต §65 ตรีผ่าน</b> แล้วทิ้งร่องรอยบนเอกสาร + audit (<see cref="UnattendedRuleCode"/>) — ไม่ใช่ "คนรับทราบ"</summary>
    Unattended = 4,
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

    /// <summary>ทางเข้าอัตโนมัติส่งผ่านข้อสังเกต §65 ตรี (คำตัดสินข้อ 110) — ไม่มีผู้ใช้เห็นรายการ</summary>
    public const string UnattendedRuleCode = "APPROVE-UNATTENDED-PASSED-S65";

    /// <summary>คำเตือนที่ทางเข้าไม่มีคนส่งผ่านได้ (ไม่หยุด) — ชุด §65 ตรี (คำตัดสินข้อ 110) · ตัวตั้งอยู่ที่ <see cref="Section65TerApprovalWarnings.IsWarning"/></summary>
    private static bool PassesWithoutPerson(string warning) => Section65TerApprovalWarnings.IsWarning(warning);

    /// <summary>คำเตือนที่ยังไม่มีใครรับทราบตามแหล่ง — ว่าง = อนุมัติต่อได้ · ไม่ว่าง = ต้องหยุด (throw คำเตือนชุดนี้)</summary>
    public static IReadOnlyList<string> Unacknowledged(ApprovalAckSource source, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return Array.Empty<string>();
        return source switch
        {
            ApprovalAckSource.User => Array.Empty<string>(),
            ApprovalAckSource.SystemWorkflow => warnings.Where(OcrApprovalGapWarning.IsGapWarning).ToList(),
            // คำตัดสินข้อ 110: API ส่งผ่านข้อสังเกต §65 ตรี แล้วคืนธงในผลตอบ (เหมือน [Σ-GAP])
            ApprovalAckSource.ApiClient => warnings.Where(w => !OcrApprovalGapWarning.IsAmountGapWarning(w) && !PassesWithoutPerson(w)).ToList(),
            ApprovalAckSource.Unattended => warnings.Where(w => !PassesWithoutPerson(w)).ToList(),
            _ => warnings,
        };
    }

    /// <summary>รหัสที่ API v1 คืน (ให้เครื่องอ่าน) เมื่อ<b>ปฏิเสธ</b>การอนุมัติเพราะ VAT ของใบสแกนไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้
    /// (คำตัดสินรอบ 198 ข้อ 6) · สัญญา v1 — ห้ามเปลี่ยนค่า</summary>
    public const string ApiVatNotOnPaperCode = "APPROVE-SCAN-VAT-NOT-ON-PAPER";

    /// <summary>รหัสที่ API v1 คืนเมื่อปฏิเสธเพราะคำเตือนชนิดอื่นที่ต้องมีคนรับทราบ (เดิมหลุดเป็น 500 ข้อความกลาง ๆ) · สัญญา v1 — ห้ามเปลี่ยนค่า</summary>
    public const string ApiWarningsNeedAckCode = "APPROVE-WARNINGS-NEED-ACK";

    /// <summary>ป้ายหมายเหตุภายในที่ลงบนเอกสารครั้งแรกที่ API ถูกปฏิเสธ — ตัวกันลงซ้ำเมื่อระบบปลายทาง retry เป็นรอบ ๆ</summary>
    public const string ApiRefusalNoteTag = "[API-APPROVE-REFUSED]";

    /// <summary>
    /// **คำตัดสินของ <c>/api/v1/documents/{id}/approve</c> ต่อคำเตือนก่อนอนุมัติ** — null = อนุมัติต่อได้ (ไม่มีคำเตือน หรือเหลือแต่ [Σ-GAP]
    /// ที่คำตัดสินข้อ 12 ให้ผ่านแล้วคืนธง) · ไม่ null = <b>ปฏิเสธ</b> ต้องคืนก่อนเรียกอนุมัติ (เอกสารคงเป็นฉบับร่าง ไม่ออกเลข ไม่ลงบัญชี)
    ///
    /// <para>ที่มา: คำตัดสินเจ้าของรอบ 198 ข้อ 6 — ใบสแกนที่ VAT ไม่ได้พิมพ์บนกระดาษ (ระบบถอดจากยอดรวมเอง ⇒ ภาษีซื้อต้องห้าม ม.82/5(1)
    /// ถ้าใบไม่แยก VAT ตาม ม.86/4(6)) ต้องให้คนตรวจกับกระดาษแล้วกด "รับทราบ" บนเว็บ/มือถือ เหมือนทางเข้าที่มีคนเห็น · ทางเข้าที่ไม่มีคนเห็น
    /// ห้ามลงภาษีซื้อที่ระบบแต่ง (DECISION_DOCTRINE §2 write-gate · ราก R1/R5)</para>
    /// </summary>
    /// <param name="warnings">คำเตือนทั้งหมดของเอกสาร (<c>PreviewApprovalWarningsAsync</c>)</param>
    public static ApiApprovalRefusal? ApiRefusal(IReadOnlyList<string> warnings)
    {
        var left = Unacknowledged(ApprovalAckSource.ApiClient, warnings);
        return left.Count == 0 ? null : ApiRefusalOf(left);
    }

    /// <summary>คำตอบปฏิเสธจากรายการคำเตือนที่ยังไม่มีใครรับทราบ (ผู้เรียกกรองแล้ว — เช่น <c>DocumentApprovalWarningsException.Warnings</c>
    /// ที่ service โยนเป็นตาข่ายชั้นที่สอง) · ชุด VAT มาก่อนเสมอ (รหัส <see cref="ApiVatNotOnPaperCode"/>)</summary>
    public static ApiApprovalRefusal ApiRefusalOf(IReadOnlyList<string> unacknowledged)
    {
        var vatNotOnPaper = unacknowledged.Any(OcrApprovalGapWarning.IsVatDerivedWarning);
        var list = string.Join(" · ", unacknowledged);
        var message = vatNotOnPaper
            ? "ไม่อนุมัติผ่าน API — เอกสารนี้สร้างจากสแกนที่ VAT ไม่ได้พิมพ์บนกระดาษ (หรือไม่มีข้อความสแกนให้ตรวจ) "
              + "VAT ที่จะลงบัญชีระบบถอดจากยอดรวมเอง ถ้าใบไม่แยก VAT ตาม ม.86/4(6) จะเป็นภาษีซื้อต้องห้าม ม.82/5(1) — "
              + "ให้ผู้ใช้เปิดเอกสารบนหน้าเว็บ (หรือแอปมือถือ) ตรวจกับกระดาษ แก้ VAT ตามกระดาษ/ตั้ง VAT 0 หรือกด \"รับทราบ\" แล้วอนุมัติที่นั่น · "
              + "เอกสารยังเป็นฉบับร่าง ยังไม่ออกเลขและยังไม่ลงบัญชี"
            : $"ไม่อนุมัติผ่าน API — เอกสารมีคำเตือน {unacknowledged.Count} ข้อที่ต้องมีคนรับทราบก่อนอนุมัติ — "
              + "ให้ผู้ใช้เปิดเอกสารบนหน้าเว็บ (หรือแอปมือถือ) ตรวจแล้วกด \"รับทราบ\" และอนุมัติที่นั่น · "
              + "เอกสารยังเป็นฉบับร่าง ยังไม่ออกเลขและยังไม่ลงบัญชี";
        return new ApiApprovalRefusal(
            vatNotOnPaper ? ApiVatNotOnPaperCode : ApiWarningsNeedAckCode,
            list.Length == 0 ? message : message + " · คำเตือน: " + list,
            unacknowledged, vatNotOnPaper);
    }

    /// <summary>หมายเหตุภายในที่ต่อท้ายเอกสารเมื่อ API ถูกปฏิเสธ — null = ไม่ต้องเขียน (เคยลงแล้ว · กันซ้ำเมื่อระบบปลายทาง retry)
    /// · ให้คนที่เปิดเอกสารบนเว็บรู้ว่า "ใบนี้ค้างร่างเพราะ API ถูกปฏิเสธ รอคุณรับทราบ" (ล้มดังในที่ที่คนดู — F2 ข้อ 7)</summary>
    /// <param name="existing">InternalNotes ปัจจุบัน</param>
    /// <param name="refusal">คำตัดสินจาก <see cref="ApiRefusal"/></param>
    /// <param name="utcNow">เวลา UTC (แสดงเป็นเวลาไทย)</param>
    public static string? ApiRefusalNote(string? existing, ApiApprovalRefusal refusal, DateTime utcNow)
    {
        if (existing is not null && existing.Contains(ApiRefusalNoteTag, StringComparison.Ordinal)) return null;
        var at = utcNow.AddHours(7).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var note = $"{ApiRefusalNoteTag} {at} น. เวลาไทย — ระบบปลายทางขออนุมัติผ่าน API แต่ถูกปฏิเสธ ({refusal.Code}) · "
            + "ตรวจเอกสาร (และกระดาษต้นฉบับถ้าสร้างจากสแกน) แล้วกด \"อนุมัติ\" บนหน้านี้ ระบบจะถามให้รับทราบคำเตือน\n"
            + string.Join("\n", refusal.Warnings.Select(w => "• " + w));
        return string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + "\n\n" + note;
    }

    /// <summary>รหัสที่ <c>POST documents/bulk-approve</c> คืน (400) เมื่อผู้เรียกส่ง <c>acknowledgeWarnings: true</c> แบบเหมารวม · ห้ามเปลี่ยนค่า</summary>
    public const string BulkBlanketAckRefusedCode = "BULK-APPROVE-BLANKET-ACK-REFUSED";

    /// <summary>
    /// **คำตัดสินของ <c>POST documents/bulk-approve</c> ต่อธง <c>acknowledgeWarnings</c> ของผู้เรียก** — null = รับคำขอ (อนุมัติทีละใบแบบ
    /// "ไม่มีใครรับทราบ" = <see cref="ApprovalAckSource.None"/>) · ไม่ null = ข้อความปฏิเสธทั้งคำขอ (ยังไม่แตะเอกสารใบไหน)
    ///
    /// <para>ที่มา (รอบ 199 ทีม W · ค้างจากทีม H): endpoint นี้เคยส่งธงของผู้เรียกเข้า <c>ApproveDocumentAsync</c> ตรง ⇒ ธงเดียวประทับ
    /// "ผู้ใช้รับทราบ" ให้คำเตือนของเอกสาร 200 ใบที่ไม่มีใครเห็นสักข้อ (รวม [Σ-GAP] ยอดสแกนไม่ตรงกระดาษ และชุด VAT ไม่ได้พิมพ์บนกระดาษ ·
    /// คำตัดสิน #12 / รอบ 198 ข้อ 6 ให้คนรับทราบ) · ทางที่ง่ายและปลอดภัยที่สุด: <b>ไม่รับการรับทราบแบบเหมารวม</b> — ใบที่มีคำเตือนถูกคืนเป็น
    /// รายการ "ต้องเปิดรับทราบทีละใบ" พร้อมคำเตือนของใบนั้น (ไม่ใช่นับเป็นล้มเหลวเฉย ๆ)</para>
    /// <para>ปฏิเสธดัง ๆ (ไม่ใช่เพิกเฉยธงเงียบ ๆ) เพราะผู้เรียกที่ส่ง true คาดว่าใบที่มีคำเตือนจะผ่าน — เพิกเฉยแล้วตอบ 200 = silent no-op
    /// (กฎเหล็ก #4 A)</para>
    /// </summary>
    /// <param name="acknowledgeWarnings">ค่าที่ผู้เรียกส่งมา (null/false = ไม่ได้ขอรับทราบ)</param>
    public static string? BulkBlanketAckRefusal(bool? acknowledgeWarnings)
        => acknowledgeWarnings == true
            ? "อนุมัติหลายใบไม่รับ \"รับทราบคำเตือน\" แบบเหมารวม (" + BulkBlanketAckRefusedCode + ") — คำเตือนก่อนอนุมัติต้องมีคนเห็นทีละใบ · "
              + "ส่งคำขอใหม่โดยไม่ใส่ acknowledgeWarnings: ใบที่ไม่มีคำเตือนจะอนุมัติให้ ใบที่มีคำเตือนจะอยู่ในรายการ needsAcknowledgement "
              + "ให้เปิดเอกสารใบนั้นแล้วกด \"อนุมัติ\" (ระบบจะแสดงคำเตือนให้กดรับทราบ) · ยังไม่มีเอกสารใบไหนถูกอนุมัติจากคำขอนี้"
            : null;

    /// <summary>ข้อความสรุปผลอนุมัติหลายใบ — แยก "ต้องเปิดรับทราบทีละใบ" ออกจาก "ล้มเหลว" (คนละทางไปต่อ)</summary>
    /// <param name="total">จำนวนที่ขอ</param>
    /// <param name="approved">อนุมัติสำเร็จ</param>
    /// <param name="needsAcknowledgement">มีคำเตือน ยังไม่อนุมัติ รอคนรับทราบทีละใบ</param>
    /// <param name="failed">ล้มด้วยเหตุอื่น (สิทธิ์ · ไม่พบ · ด่านบังคับ)</param>
    public static string BulkSummary(int total, int approved, int needsAcknowledgement, int failed)
    {
        var msg = $"อนุมัติ {approved}/{total} ใบ";
        if (needsAcknowledgement > 0)
            msg += $" — มีคำเตือน {needsAcknowledgement} ใบ ยังไม่อนุมัติ: เปิดเอกสารแล้วกด \"อนุมัติ\" เพื่อรับทราบคำเตือนทีละใบ";
        if (failed > 0)
            msg += $" — ล้มเหลว {failed} ใบ";
        return msg;
    }

    /// <summary>รหัสกฎของร่องรอยเมื่ออนุมัติทั้งที่มีคำเตือน</summary>
    public static string RuleCode(ApprovalAckSource source) => source switch
    {
        ApprovalAckSource.SystemWorkflow => SystemRuleCode,
        ApprovalAckSource.ApiClient => ApiRuleCode,
        ApprovalAckSource.Unattended => UnattendedRuleCode,
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
            ApprovalAckSource.Unattended => "— ข้อสังเกตตอนอนุมัติ: ทางเข้าอัตโนมัติส่งผ่าน (ไม่มีผู้ใช้เห็นรายการนี้ก่อนอนุมัติ · คำตัดสินข้อ 110) — ตรวจก่อนปิดรอบ —",
            _ => "— รับทราบคำเตือนตอนอนุมัติ —",
        };
        var tail = source == ApprovalAckSource.User
            ? $"(ยืนยันโดย {approvedBy} เมื่อ {at} น. เวลาไทย)"
            : $"(อนุมัติโดย {approvedBy} เมื่อ {at} น. เวลาไทย — ไม่ใช่การรับทราบของผู้ใช้)";
        return head + "\n" + string.Join("\n", warnings.Select(w => "• " + w)) + "\n" + tail;
    }
}

/// <summary>คำตอบปฏิเสธการอนุมัติผ่าน API v1 — <c>Code</c> ให้เครื่องอ่าน · <c>Message</c> ให้คนอ่าน (บอกเหตุผล + ทางไปต่อ)</summary>
/// <param name="Code"><see cref="ApprovalAcknowledgement.ApiVatNotOnPaperCode"/> หรือ <see cref="ApprovalAcknowledgement.ApiWarningsNeedAckCode"/></param>
/// <param name="Message">ข้อความไทยพร้อมทางไปต่อ</param>
/// <param name="Warnings">คำเตือนที่ทำให้ปฏิเสธ</param>
/// <param name="ScanVatNotOnPaper">มีคำเตือนชุด VAT ไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้</param>
public sealed record ApiApprovalRefusal(string Code, string Message, IReadOnlyList<string> Warnings, bool ScanVatNotOnPaper);
