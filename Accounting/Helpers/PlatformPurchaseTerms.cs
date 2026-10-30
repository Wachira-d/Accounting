namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินเจ้าของข้อ 178 ⑥⑦⑧ · ข้อ 180) — <b>เงื่อนไขการซื้อค่าบริการ NextAcc + ด่าน "ติ๊กยอมรับ" ตัวเดียว</b>
/// ของทุกหน้าจ่าย (ศูนย์บิล · ลิงก์ชำระ · ซื้อส่วนเสริม) · ข้อความต้องตรงกับพฤติกรรมระบบจริง:
/// ไม่คืนเงิน (⑦) · ยกเลิก = ใช้ได้ถึงสิ้นรอบ ไม่คิดตามสัดส่วน (⑥) · ส่วนเสริมเลือกจ่ายล่วงหน้า/รวมใบรายเดือนต่อการซื้อ (⑤)
/// <para><b>ชั่วคราว</b> จนตัวช่วยยอมรับนโยบายของทีม part B (เอกสารชนิด refund บนโครงสร้างรอบ 214) ลง — เปลี่ยนข้อความ = เปลี่ยน <see cref="Version"/>
/// เสมอ (หลักฐานผูกกับฉบับ) · หน้าเว็บส่ง <c>termsVersionShown</c> กลับมา ฉบับไม่ตรง = 409 ให้โหลดหน้าใหม่ (ไม่ถือว่ายอมรับฉบับที่ไม่ได้เห็น)</para>
/// </summary>
public static class PlatformPurchaseTerms
{
    /// <summary>ฉบับของเงื่อนไขการซื้อ — เปลี่ยนเมื่อแก้ <see cref="Lines"/></summary>
    public const string Version = "2026-10-30.1";

    /// <summary>purpose ใน canonical ของ <c>PdpaConsentEvidence.ComputeHash</c></summary>
    public const string Purpose = "platform-purchase-terms";

    public const string KindInvoicePayment = "invoice-payment";
    public const string KindPayLinkPayment = "paylink-payment";
    public const string KindAddOn = "addon";

    public const string NotAcceptedRuleCode = "PLATFORM-TERMS-NOT-ACCEPTED";
    public const string VersionChangedRuleCode = "PLATFORM-TERMS-VERSION-CHANGED";

    /// <summary>ลิงก์ข้อกำหนดการใช้งานของแพลตฟอร์ม (รอบ 214) — นโยบายคืนเงินฉบับเต็มเป็นงานของ part B</summary>
    public const string TermsUrl = "/terms.html";

    public static IReadOnlyList<string> Lines { get; } = new[]
    {
        "ค่าบริการที่ชำระแล้วไม่สามารถขอคืนเงินได้ — หากถูกเรียกเก็บซ้ำหรือผิดพลาดจากระบบ โปรดติดต่อผู้ดูแลระบบ NextAcc เพื่อตรวจสอบ",
        "ยกเลิกแพ็กเกจได้ทุกเมื่อ — ใช้งานต่อได้จนสิ้นรอบที่ชำระแล้ว และไม่มีการคิดหรือคืนเงินตามสัดส่วนวันที่เหลือ",
        "ส่วนเสริมรายเดือน: เลือกได้ทุกครั้งที่ซื้อว่าจะชำระล่วงหน้า หรือรวมในใบแจ้งหนี้รายเดือน · ปิดส่วนเสริมได้ทุกเมื่อ (ไม่คืนค่างวดที่ชำระแล้ว)",
        "ค่าใช้งานตามจริงออกใบแจ้งหนี้รายเดือน · ค้างชำระเกินกำหนดอาจถูกระงับการใช้งานจนกว่าจะชำระ",
    };

    /// <summary>ด่านเดียว: ไม่ติ๊ก ⇒ 400 · ติ๊กแต่ฉบับที่เห็นไม่ใช่ฉบับปัจจุบัน ⇒ 409 (ให้โหลดหน้าใหม่) · null = ผ่าน</summary>
    public static BusinessRuleException? Refusal(bool accepted, string? versionShown)
    {
        if (!accepted)
            return new BusinessRuleException("กรุณาอ่านและติ๊กยอมรับเงื่อนไขการชำระค่าบริการก่อนชำระ", NotAcceptedRuleCode, 400);
        if (!string.Equals(versionShown?.Trim(), Version, StringComparison.Ordinal))
            return new BusinessRuleException("เงื่อนไขการชำระมีการปรับปรุง — กรุณาโหลดหน้าใหม่ อ่านแล้วติ๊กยอมรับอีกครั้ง", VersionChangedRuleCode, 409);
        return null;
    }
}
