namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวของ "ยกเลิกการนำส่ง" (คำตัดสินข้อ 113 · ทีมตรวจงานค้าง 2026-10-08) และของ "กลับรายการนำส่งประกันสังคมรายรอบ"
///
/// <para>ที่มา: ข้อ 113 ให้มีปุ่มยกเลิกการนำส่งเป็นทางปลดชั่วคราว แต่ไม่เคยมี (ด่าน 50 ทวิ/แก้รอบเงินเดือนบอกผู้ใช้ว่า "ยังไม่มีปุ่ม") ·
/// และพบบั๊กเงิน: นำส่ง สปส.1-10 ทั้งเดือนประทับ JE เดียวกันลงทุกรอบที่จ่ายแล้ว แต่ "กลับรายการรายรอบ" กลับ JE ทั้งก้อนแล้วปลดธงแค่รอบเดียว
/// ⇒ รอบอื่นยังขึ้น "นำส่งแล้ว" ทั้งที่ JE ถูกกลับ · กลับรอบที่สองของเดือนเดียวกัน = กลับ JE ซ้ำสองครั้ง</para>
/// </summary>
public static class RemittanceVoidPolicy
{
    public const string RuleReasonRequired = "REMIT-VOID-REASON";
    public const string RulePp36Recognized = "REMIT-VOID-PP36-RECOGNIZED";
    public const string RuleNoJournal = "REMIT-VOID-NO-JE";
    public const string RuleSsoShared = "SSO-REVERSE-SHARED-JE";

    /// <summary>null = ยกเลิกได้ · ข้อความ = เหตุผล (แสดงผู้ใช้ตรง ๆ)</summary>
    public static (string Message, string RuleCode)? BlockReason(string? reason, bool hasJournal, bool hasRecognizedPp36Docs)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return ("ต้องระบุเหตุผลการยกเลิกการนำส่ง (บันทึกลง audit)", RuleReasonRequired);
        if (!hasJournal)
            return ("รายการนำส่งนี้ไม่มีรายการบัญชี (JE) ผูกอยู่ — ตรวจในสมุดรายวันแล้วกลับรายการด้วยตนเอง", RuleNoJournal);
        if (hasRecognizedPp36Docs)
            // คำตัดสินข้อ 134: ภาษีซื้อของ ภ.พ.36 รับรู้ได้หลังนำส่งแล้วเท่านั้น — ยกเลิกการนำส่งทั้งที่รับรู้แล้ว = ภาษีซื้อที่ไม่มีการนำส่งรองรับ
            return ("ใบ ภ.พ.36 ในรายการนำส่งนี้รับรู้ภาษีซื้อแล้ว (11640 → 11610) — ยกเลิกการรับรู้ภาษีซื้อก่อน แล้วค่อยยกเลิกการนำส่ง",
                RulePp36Recognized);
        return null;
    }

    /// <summary>กลับรายการนำส่งประกันสังคม "รายรอบ" ได้ไหม — JE ของการนำส่งที่ใช้ร่วมกับรอบอื่น/รายการนำส่งทั้งเดือน ห้ามกลับรายรอบ
    /// (กลับ JE ทั้งก้อนแต่ปลดธงรอบเดียว) ⇒ ใช้ "ยกเลิกการนำส่ง" ที่หน้านำส่งแทน</summary>
    public static (string Message, string RuleCode)? SsoPerRunReverseBlock(string payrollNumber, int otherRunsSharingJe, bool monthlyRemittanceOwnsJe)
    {
        if (otherRunsSharingJe <= 0 && !monthlyRemittanceOwnsJe) return null;
        return ($"รอบ {payrollNumber} นำส่งประกันสังคมรวมทั้งเดือน (รายการบัญชีเดียวกับรอบอื่น {otherRunsSharingJe} รอบ) — "
                + "กลับรายการรายรอบไม่ได้ · ยกเลิกที่หน้า “นำส่งภาษี/ประกันสังคม” → ประวัติ → ยกเลิกการนำส่ง (ปลดทุกรอบของเดือนพร้อมกัน)",
            RuleSsoShared);
    }
}
