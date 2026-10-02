using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของเอกสารหนึ่งใบที่ใช้ตัดสินว่า "ลงบัญชีย้อนหลังให้ใบที่อนุมัติแล้วแต่ไม่มี JE" ได้ไหม — ผู้เรียกอ่านจากฐานข้อมูล (tenant เดียว)</summary>
/// <param name="ReplacesWithoutPostings">ใบกำกับเต็มรูปที่ออก "แทน" ใบเดิมแบบไม่ถือผลบัญชีเอง (<c>ReplacesDocumentId</c> มี และ
/// <c>!ReplacementCarriesPostings</c>) — เงื่อนไขเดียวกับที่ <c>ApproveDocumentAsync</c> ข้าม AutoPost</param>
/// <param name="HasLiveJournal">มี JE ที่ยังมีผล (Posted · ไม่ใช่ตัวกลับ · ยังไม่ถูกกลับ) — predicate เดียวกับด่านกันลงซ้ำในการอนุมัติ</param>
/// <param name="LockedPeriodName">ชื่องวดบัญชีของวันที่เอกสารที่ปิด/ล็อกอยู่ · null = เปิดหรือไม่มีงวด</param>
/// <param name="Pp36Period">งวด ภ.พ.36 ของใบ (MM/YYYY) — ใช้ในข้อความเท่านั้น · null = ไม่ใช่บริการต่างประเทศที่มี VAT</param>
/// <param name="Pp36Remitted">งวด ภ.พ.36 ของใบนี้นำส่งแล้ว</param>
/// <param name="Pp36Recognized">ภาษีซื้อ ภ.พ.36 ของใบนี้ถูกรับรู้ (11640 → 11610) แล้ว</param>
public sealed record MissingJournalFacts(
    DocumentType Type, DocumentStatus Status, bool IsSettlementReceipt, bool ReplacesWithoutPostings,
    bool HasLiveJournal, string? LockedPeriodName,
    string? Pp36Period, bool Pp36Remitted, bool Pp36Recognized);

/// <summary>ผลตัดสิน — <paramref name="Missing"/> ใบนี้ควรมี JE แต่ไม่มี · <paramref name="CanRepair"/> ลงย้อนหลังด้วยเครื่องมือได้ ·
/// <paramref name="Message"/> ข้อความไทยพร้อมทางไปต่อ (null เมื่อไม่มีอะไรต้องบอก)</summary>
public sealed record MissingJournalDecision(bool Missing, bool CanRepair, string? Message);

/// <summary>
/// PP36_REVIEW "ซ่อมข้อมูลเดิม" (2026-10-02) — ตัวตัดสินตัวเดียวของเครื่องมือ "ลงบัญชีให้ใบที่อนุมัติแล้วแต่ไม่มี JE" ทั้งปุ่มบนหน้าเอกสาร
/// และหน้า 🩺 ตรวจโครงสร้าง JE (endpoint GET ถาม · POST ลงจริง ถามตัวเดียวกันภายใต้ล็อก)
/// <para>ใบที่ด่าน JE §83/6 เคยตีตกแต่สถานะค้างเป็น "อนุมัติแล้ว" (P0-1 + P0-2) แก้ด้วยการ "ยกเลิกแล้วอนุมัติใหม่" ไม่ได้: ยกเลิกใบที่ไม่มี JE
/// ไม่มีอะไรให้กลับ และเลขที่ออกแล้วต้องคงเดิม (§86/4) ⇒ ลงบัญชีด้วย AutoPost ตัวเดียวกับการอนุมัติ</para>
/// <para>ปฏิเสธ (พร้อมทางไปต่อ): ใบที่ไม่ควรมี JE · มี JE แล้ว · งวดปิด · ภ.พ.36 นำส่ง/รับรู้แล้ว (ลงตอนนี้ขยับยอดหนี้ 21912/11640 ของงวดที่
/// ยื่นแล้ว — ให้คนตัดสินพร้อมผู้ทำบัญชี ไม่ใช่ระบบ)</para>
/// </summary>
public static class MissingJournalRepair
{
    public const string RuleCode = "DOC-NO-JE-REPAIR";

    /// <summary>คำแนะนำของตัวสแกน DOC-NO-JE — ชี้เครื่องมือนี้ (เดิม "ยกเลิกแล้วอนุมัติใหม่" ซึ่งทำไม่ได้กับใบที่ออกเลขแล้ว)</summary>
    public const string ScannerFix =
        "เปิดเอกสาร → แผง 📒 รายการบัญชี → “🔧 ลงบัญชีให้ใบนี้” (ระบบลงด้วยกติกาเดียวกับตอนอนุมัติ · เลขเอกสารคงเดิม) · "
        + "ถ้าระบบปฏิเสธ (งวดปิด/ภ.พ.36 นำส่งแล้ว) ให้ทำตามข้อความ หรือคีย์ JE เองในหน้าสมุดรายวันอ้างเลขเอกสาร";

    public static MissingJournalDecision Decide(MissingJournalFacts f)
    {
        if (!DocumentJournalExpectation.ExpectsLiveJournal(f.Type, f.Status, f.IsSettlementReceipt, f.ReplacesWithoutPostings))
            return new(false, false, null);
        if (f.HasLiveJournal)
            return new(false, false, null);
        if (f.LockedPeriodName != null)
            return new(true, false,
                $"เอกสารนี้อนุมัติแล้วแต่ไม่มีรายการบัญชี และงวด {f.LockedPeriodName} ปิด/ล็อกอยู่ — เปิดงวดก่อน (เมนูปิดงวด) แล้วกดลงบัญชีอีกครั้ง "
                + "หรือคีย์ JE เองในงวดที่เปิดอ้างเลขเอกสาร");
        if (f.Pp36Recognized)
            return new(true, false,
                $"เอกสารนี้อนุมัติแล้วแต่ไม่มีรายการบัญชี และภาษีซื้อ ภ.พ.36 งวด {f.Pp36Period} ถูกรับรู้เข้า ภ.พ.30 แล้ว — ระบบไม่ลงย้อนหลังให้เอง "
                + "(ยอด 11640/11610 ของงวดที่ยื่นแล้วจะขยับ) · ปรึกษาผู้ทำบัญชีแล้วบันทึกด้วยใบสำคัญทั่วไปอ้างเลขเอกสาร");
        if (f.Pp36Remitted)
            return new(true, false,
                $"เอกสารนี้อนุมัติแล้วแต่ไม่มีรายการบัญชี และ ภ.พ.36 งวด {f.Pp36Period} นำส่งแล้ว — ระบบไม่ลงย้อนหลังให้เอง "
                + "(หนี้ 21912 ของงวดที่นำส่งแล้วจะขยับ) · ตรวจกับใบเสร็จกรมสรรพากรว่ายอดที่นำส่งรวมใบนี้หรือไม่ แล้วบันทึกด้วยใบสำคัญทั่วไปอ้างเลขเอกสาร");
        return new(true, true,
            "เอกสารนี้อนุมัติแล้วแต่ไม่มีรายการบัญชี — ยอดนี้หายจากงบ/สมุดรายวัน · กด “🔧 ลงบัญชีให้ใบนี้” "
            + "ระบบลงด้วยกติกาเดียวกับตอนอนุมัติ (เลขเอกสารคงเดิม)");
    }
}
