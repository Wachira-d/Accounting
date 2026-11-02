namespace Accounting.Helpers;

/// <summary>สถานะการกระทบยอด "ผังเงินสดย่อย (GL) ↔ ยอดคงเหลือของกองทุนที่ผูกผังนั้น"</summary>
public enum PettyCashGlReconStatus
{
    /// <summary>ยอดตรง และไม่มีรายการจากทางอื่นค้างระบุกองทุน</summary>
    Matched = 0,
    /// <summary>มีรายการลงผังนี้จากทางอื่น (ใบสำคัญจ่าย/ใบเบิก/สมุดรายวัน/กลับรายการ …) ที่ยังไม่ระบุว่าเป็นของกองทุนไหน</summary>
    Unattributed = 1,
    /// <summary>ไม่มีรายการค้าง แต่ยอดยังต่าง (เช่น ยอดยกมาก่อนเปิดกองทุน · แก้ยอดกองทุนนอกระบบ) — ต้องให้คนตรวจ</summary>
    Difference = 2,
}

/// <summary>
/// <b>กติกา "ผังบัญชีของกองทุนเงินสดย่อย" ตัวเดียว</b> (2026-11-02 · erp-review/2026-11-01/PETTY_CASH_AUDIT.md)
///
/// <para>═══ ที่มา ═══ เจ้าของถาม: "ถ้ามีหลายกองทุนผูกผัง 11112 เดียวกัน ตอนทำใบสำคัญจ่ายแล้วเลือกผังนี้ จะรู้ได้ไงว่าหักกองทุนไหน" ·
/// ตรวจแล้ว: (1) เปิดกองทุนโดยไม่เลือกผัง = ได้ 11112 ทุกกองทุน ⇒ "ผังร่วม" เป็นค่าตั้งต้นโดยไม่ตั้งใจ · (2) ใบสำคัญจ่าย/ใบเบิก/เงินทดรอง/
/// สินเชื่อ/สมุดรายวัน เลือก 11112 เป็นช่องทางเงินได้ (กลุ่ม "เงินสด / ช่องทางอื่น") แต่<b>ไม่มีทางไหนลดยอดกองทุน</b> ⇒ ยอดกองทุนกับ GL
/// เพี้ยนกันเงียบ ๆ และการเบิกที่หน้าเงินสดย่อยยังเชื่อยอดเก่า</para>
///
/// <para>═══ คำตัดสิน ═══ (ก) ผัง 1 ผัง ↔ กองทุน 1 กองทุน เป็นค่าตั้งต้น — ใช้ผังร่วมได้เมื่อผู้ใช้ติ๊ก "ยอมใช้ผังร่วม" เอง (ยืดหยุ่นตามที่เจ้าของขอ) ·
/// (ข) ไม่บังคับตัวเลือกกองทุนในทุกทางเข้า (6+ หน้า + สมุดรายวัน + กลับรายการ — ทางที่ลืมจะรั่วอีก) แต่<b>กระทบยอดจาก GL ที่เดียว</b>:
/// ทุกใบสำคัญที่แตะผังกองทุนแต่ไม่ได้มาจากหน้าเงินสดย่อย = "รายการจากทางอื่น" ให้ระบุกองทุน (ผังที่มีกองทุนเดียว = ระบบเสนอให้เอง
/// แต่คนกดยืนยัน — ไม่ประทับเองตอนอ่าน) ⇒ ครอบทุกทางเข้า รวมทางที่ยังไม่เกิด</para>
/// </summary>
public static class PettyCashFundGlRule
{
    public const string RuleSharedAccount = "PETTY-FUND-SHARED-GL";
    public const string RuleAttribute = "PETTY-ATTRIBUTE";

    /// <summary>ชนิดแถวทะเบียน "ใบสำคัญนี้ไม่กระทบกองทุน (ปรับเฉพาะ GL)" — <c>Amount</c> = ยอดสุทธิที่ใบลงผัง (เดบิต − เครดิต · มีเครื่องหมาย) ·
    /// ไม่ขยับ <c>CurrentBalance</c> · ยกเลิกได้ (soft-delete + audit) · กระทบยอดนับยอดนี้เป็น "ส่วนที่ GL ต่างจากกองทุนโดยตั้งใจ"</summary>
    public const string GlOnlyType = "GlOnly";

    /// <summary>ปัญหาของการผูกผังที่กองทุนอื่นใช้อยู่ — null = ผูกได้ · <paramref name="otherFundNames"/> = กองทุนอื่น (ไม่ถูกลบ) ที่ผูกผังนี้</summary>
    public static string? SharedAccountProblem(string accountLabel, IReadOnlyCollection<string> otherFundNames, bool allowShared)
    {
        if (otherFundNames.Count == 0 || allowShared) return null;
        return $"ผัง {accountLabel} ผูกกับกองทุน {string.Join(", ", otherFundNames.Select(n => $"“{n}”"))} อยู่แล้ว — "
            + "กองทุนที่ใช้ผังร่วมกัน ระบบแยกไม่ได้ว่าใบสำคัญจ่าย/ใบเบิก/สมุดรายวันที่เลือกผังนี้หักกองทุนไหน · "
            + "แนะนำ: เพิ่มผังย่อยแยกต่อกองทุน (เช่น 11112-01, 11112-02) ที่หน้า “ผังบัญชี” แล้วเลือกผังนั้น · "
            + "ถ้าตั้งใจใช้ผังร่วม ติ๊ก “ใช้ผังร่วมกับกองทุนอื่น” (รายการจากทางอื่นต้องระบุกองทุนเองที่ส่วน “กระทบยอดกับผังบัญชี”)";
    }

    /// <summary>ชนิดรายการในทะเบียนกองทุนของ "ยอดสุทธิที่ใบสำคัญหนึ่งลงผังกองทุน" (เดบิต − เครดิต) — บวก = เงินเข้า (เติม) ·
    /// ลบ = เงินออก (เบิกจ่าย) · 0 = ไม่มีผลต่อยอด (null)</summary>
    public static (string Type, decimal Amount)? ExternalMovement(decimal netDebit)
    {
        if (netDebit > 0m) return ("Replenishment", netDebit);
        if (netDebit < 0m) return ("Disbursement", -netDebit);
        return null;
    }

    /// <summary>สถานะของผังหนึ่ง — ยอด GL เทียบผลรวมยอดกองทุนที่ผูกผังนั้น</summary>
    public static PettyCashGlReconStatus Classify(decimal glBalance, decimal fundsTotal, int unattributedEntries)
    {
        if (unattributedEntries > 0) return PettyCashGlReconStatus.Unattributed;
        return glBalance == fundsTotal ? PettyCashGlReconStatus.Matched : PettyCashGlReconStatus.Difference;
    }

    /// <summary>ป้ายไทยของสถานะ — ตัวเดียวของระบบ (หน้าเว็บแสดงตามนี้ ไม่แต่งเอง)</summary>
    public static string StatusLabel(PettyCashGlReconStatus s) => s switch
    {
        PettyCashGlReconStatus.Matched => "ตรงกัน",
        PettyCashGlReconStatus.Unattributed => "มีรายการจากทางอื่นที่ยังไม่ระบุกองทุน",
        _ => "ยอดต่างกัน — ตรวจยอดยกมา/การแก้ยอดนอกระบบ",
    };
}
