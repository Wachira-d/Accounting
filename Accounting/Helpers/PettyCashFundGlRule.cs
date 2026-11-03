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
    /// <summary>มีแถวในทะเบียนกองทุนที่อ้างใบสำคัญซึ่งไม่อยู่ใน GL แล้ว (ถูกลบ / ยกเลิกแบบร่าง) — ยอดกองทุนยังนับแต่ GL ไม่นับ ⇒ ผลต่าง ·
    /// แก้ได้ด้วย “ย้อนผลต่อกองทุน” (2026-11-02 รอบ 2 · PETTY_CASH_AUDIT ส่วน D)</summary>
    OrphanedFundEntries = 3,
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

    /// <summary>ชนิดแถวทะเบียน "คืนเงินคงเหลือตอนปิดกองทุน" — เงินออกจากกองทุน (ลดยอด) · มี JE ของตัวเอง (Dr บัญชีที่รับคืน / Cr ผังกองทุน)</summary>
    public const string ReturnType = "Return";

    public const string RuleFundClosed = "PETTY-FUND-CLOSED";
    public const string RuleFundAlreadyOpen = "PETTY-FUND-ALREADY-OPEN";
    public const string RuleCloseOpenItems = "PETTY-CLOSE-OPEN-ITEMS";
    public const string RuleCloseNegative = "PETTY-CLOSE-NEGATIVE";
    public const string RuleOrphan = "PETTY-ORPHAN";

    /// <summary>กองทุนที่ปิดแล้ว — ทุกเส้นที่ขยับยอด (เบิก · เติม · ระบุกองทุน · ย้อนผลรายการค้าง) ปฏิเสธด้วยข้อความนี้ (ทางไปต่อ = เปิดกองทุนอีกครั้ง)</summary>
    public static string FundClosedMessage(string fundName)
        => $"กองทุน “{fundName}” ปิดแล้ว — ถ้าต้องใช้ต่อ กด “เปิดกองทุนอีกครั้ง” ที่การ์ดของกองทุน (ต้องมีสิทธิ์ “จัดการใบสำคัญ (JE)”) แล้วเติมเงินก่อน";

    /// <summary>ผลต่อยอดกองทุนของแถวทะเบียน (มีเครื่องหมาย) — ตัวเดียวของระบบ: เติม = +ยอด · เบิก/คืนเงิน = −ยอด · ปรับเฉพาะ GL = 0 ·
    /// ชนิดที่ไม่รู้จัก = null (ผู้เรียกต้องไม่เดา)</summary>
    public static decimal? FundEffect(string? type, decimal amount) => type switch
    {
        "Replenishment" => amount,
        "Disbursement" => -amount,
        ReturnType => -amount,
        GlOnlyType => 0m,
        _ => null,
    };

    /// <summary>ปิดกองทุนได้ไหม เมื่อดูจากกระทบยอดของผังกองทุน — null = ปิดได้ · มีค่า = ข้อความบอกรายการค้างและทางไปต่อ ·
    /// <paramref name="acknowledged"/> = ผู้ใช้ติ๊ก "รับทราบรายการค้าง ปิดต่อ" (ยืดหยุ่นตามคำสั่งเจ้าของ — audit เก็บจำนวนที่รับทราบ) ·
    /// <b>รับทราบไม่ครอบ</b> <paramref name="ownOrphaned"/> (แถวของกองทุนนี้เองที่อ้างใบสำคัญซึ่งหายจาก GL — ยอดที่จะคืนผิดแน่ ⇒ ต้องย้อนผลก่อน ·
    /// ฝ่ายค้านส่วน D ข้อ 3) · รับทราบยังครอบใบที่ยังไม่ระบุ/แถวของกองทุนอื่นบนผังร่วม/ผลต่าง</summary>
    public static string? CloseBlocker(int unattributed, int orphaned, int ownOrphaned, decimal difference, bool acknowledged)
    {
        if (ownOrphaned > 0)
            return $"ยังปิดกองทุนไม่ได้ — กองทุนนี้มีรายการ {ownOrphaned} รายการที่อ้างใบสำคัญซึ่งถูกลบ/ยกเลิกแล้ว (ยอดคงเหลือที่จะคืนจึงไม่ตรงบัญชี) · "
                + "กด “ย้อนผลต่อกองทุน” ที่ส่วน “กระทบยอดกับผังบัญชี” ก่อน (รับทราบแทนไม่ได้)";
        if (acknowledged || (unattributed == 0 && orphaned == 0 && difference == 0m)) return null;
        var parts = new List<string>();
        if (unattributed > 0) parts.Add($"ใบสำคัญจากทางอื่นที่ยังไม่ระบุกองทุน {unattributed} ใบ");
        if (orphaned > 0) parts.Add($"รายการกองทุนที่อ้างใบสำคัญซึ่งถูกลบ/ยกเลิก {orphaned} รายการ");
        if (difference != 0m) parts.Add($"ยอดตามผังบัญชีต่างจากยอดกองทุน {difference:N2} บาท");
        return "ยังปิดกองทุนไม่ได้ — ผังของกองทุนมี " + string.Join(" · ", parts)
            + " · จัดการที่ส่วน “กระทบยอดกับผังบัญชี” ท้ายหน้าก่อน (ระบุกองทุน / ไม่กระทบกองทุน / ย้อนผลต่อกองทุน) "
            + "หรือติ๊ก “รับทราบรายการค้าง ปิดกองทุนต่อ” ถ้าตรวจแล้วว่าไม่เกี่ยวกับเงินในกองทุนนี้";
    }

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
    public static PettyCashGlReconStatus Classify(decimal glBalance, decimal fundsTotal, int unattributedEntries, int orphanedEntries = 0)
    {
        // รายการค้างที่อ้างใบสำคัญซึ่งหายจาก GL มาก่อน — เป็นต้นเหตุของผลต่างที่แก้ได้ทันที (ย้อนผลต่อกองทุน)
        if (orphanedEntries > 0) return PettyCashGlReconStatus.OrphanedFundEntries;
        if (unattributedEntries > 0) return PettyCashGlReconStatus.Unattributed;
        return glBalance == fundsTotal ? PettyCashGlReconStatus.Matched : PettyCashGlReconStatus.Difference;
    }

    /// <summary>ป้ายไทยของสถานะ — ตัวเดียวของระบบ (หน้าเว็บแสดงตามนี้ ไม่แต่งเอง)</summary>
    public static string StatusLabel(PettyCashGlReconStatus s) => s switch
    {
        PettyCashGlReconStatus.Matched => "ตรงกัน",
        PettyCashGlReconStatus.Unattributed => "มีรายการจากทางอื่นที่ยังไม่ระบุกองทุน",
        PettyCashGlReconStatus.OrphanedFundEntries => "มีรายการกองทุนที่อ้างใบสำคัญซึ่งถูกลบ/ยกเลิกแล้ว — กด “ย้อนผลต่อกองทุน”",
        _ => "ยอดต่างกัน — ตรวจยอดยกมา/การแก้ยอดนอกระบบ",
    };

    /// <summary>คำอธิบายว่าทำไมแถวทะเบียนนี้ถูกนับว่า "ค้าง" — จากสถานะใบสำคัญที่อ้าง (อ่านแบบไม่กรองแถวลบ) ·
    /// <paramref name="found"/> = false ⇒ ไม่พบใบเลย</summary>
    public static string OrphanReason(bool found, bool isDeleted, string? status) =>
        !found ? "ไม่พบใบสำคัญที่อ้าง"
        : isDeleted ? "ใบสำคัญถูกลบ (ไม่อยู่ในบัญชีแล้ว)"
        : status == "Voided" ? "ใบสำคัญถูกยกเลิก (ไม่อยู่ในบัญชีแล้ว)"
        : status == "Draft" ? "ใบสำคัญเป็นแบบร่าง (ยังไม่ลงบัญชี)"
        : "ใบสำคัญไม่ถูกนับในบัญชี";
}
