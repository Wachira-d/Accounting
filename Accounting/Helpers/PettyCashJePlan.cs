using System;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินตัวเดียวว่า "รายการเงินสดย่อยใบนี้ลงบัญชีอย่างไร"** (pure · OWNER file).
///
/// ═══ ของเดิมพังตรงไหน (`DECISION_AUDIT_2026-09-18.md` §3 D4-7, P1) ═══
/// 1. **`DisburseAsync` ลง JE เฉพาะเมื่อมีทั้งผังค่าใช้จ่ายและผังของกองทุน** —
///    `if (expenseAccountId.HasValue &amp;&amp; fund.LinkedAccountId.HasValue)` ไม่เข้า
///    เงื่อนไขก็ **บันทึกรายการโดยไม่มี JE เงียบ ๆ** ⇒ เงินออกจากลิ้นชักจริง
///    แต่บัญชีไม่รู้ (คอมเมนต์เดิมเขียนว่า "caller can backfill later" —
///    ไม่มี caller ไหนทำ: "มี ≠ ถูกเรียก")
/// 2. **`ReplenishAsync` ไม่มี JE เลย** — เงินโอนจากธนาคารเข้าถังเงินสดย่อย
///    ไม่ปรากฏใน GL เลยแม้แต่บรรทัดเดียว ⇒ ยอดธนาคารในงบไม่ลด และยอดเงินสดย่อย
///    ในงบไม่ขึ้น ทั้งที่ `PettyCashFund.CurrentBalance` ขึ้นไปแล้ว
///    = **สองความจริงที่ไม่มีวันตรงกัน**
/// 3. **ไม่มีด่าน §65 ตรี(9)** — จ่ายโดยไม่มีหลักฐาน ก็ยังเป็นค่าใช้จ่ายเต็ม
///
/// ═══ ทิศ (G5) ═══
/// "ลงบัญชีไม่ได้" ต้อง **ล้มดัง** พร้อมบอกว่าไปตั้งค่าที่ไหน — ความเสียหาย
/// มองเห็นและแก้ทันที. ทิศตรงข้าม (บันทึกไปก่อนแล้วค่อยตามเก็บ) คือ GL ที่
/// ขาดรายการโดยไม่มีอะไรฟ้อง ซึ่งจะถูกพบตอนปิดงบ — สายเกินแก้
///
/// ส่วน §65 ตรี(9) เลือกทิศ **"บันทึกได้ แต่ติดธงบวกกลับ"** ไม่ใช่บล็อก:
/// เงินออกจากลิ้นชักไปแล้วจริง ๆ การห้ามบันทึกทำให้ยอดเงินสดย่อยในระบบ
/// ไม่ตรงกับเงินในลิ้นชักถาวร (เสียหายมากกว่า) — ธงทำให้ ภ.ง.ด.50 เห็นเอง
/// </summary>
public static class PettyCashJePlan
{
    public const string RuleNoExpenseAccount = "PETTY-NO-EXPENSE-ACCOUNT";
    public const string RuleNoFundAccount = "PETTY-NO-FUND-ACCOUNT";
    public const string RuleNoSourceAccount = "PETTY-NO-SOURCE-ACCOUNT";
    public const string RuleBadAmount = "PETTY-BAD-AMOUNT";
    public const string RuleOk = "PETTY-JE-OK";

    /// <summary>รหัสกฎของ §65 ตรี(9) "รายจ่ายที่ไม่มีหลักฐานผู้รับ" —
    /// ใช้ตัวเดียวกันทั้งตอนติดธงและตอนทำ worksheet บวกกลับ</summary>
    public const string RuleNoReceipt = "RD-65TER-9";

    /// <param name="Ok">ลง JE ได้ไหม — false = ผู้เรียกต้อง throw ด้วย <paramref name="Error"/></param>
    /// <param name="DebitAccountId">ผังที่เดบิต</param>
    /// <param name="CreditAccountId">ผังที่เครดิต</param>
    /// <param name="NonDeductible">ติดธง "บวกกลับ" ตาม §65 ตรี(9) ไหม</param>
    public sealed record Plan(
        bool Ok,
        Guid DebitAccountId,
        Guid CreditAccountId,
        bool NonDeductible,
        string RuleCode,
        string? Error);

    private static Plan Fail(string ruleCode, string error)
        => new(false, Guid.Empty, Guid.Empty, false, ruleCode, error);

    /// <summary>
    /// จ่ายเงินสดย่อย — Dr ค่าใช้จ่าย / Cr ผังของกองทุนเงินสดย่อย.
    /// </summary>
    /// <param name="expenseAccountId">ผังค่าใช้จ่ายที่ผู้ใช้เลือก</param>
    /// <param name="fundAccountId">`PettyCashFund.LinkedAccountId`</param>
    /// <param name="receiptReference">เลขที่ใบเสร็จ/หลักฐาน (§65 ตรี(9))</param>
    /// <param name="amount">ยอดจ่าย</param>
    public static Plan ForDisbursement(
        Guid? expenseAccountId, Guid? fundAccountId, string? receiptReference, decimal amount)
    {
        if (amount <= 0m)
            return Fail(RuleBadAmount, "ยอดจ่ายต้องมากกว่า 0");
        if (!expenseAccountId.HasValue || expenseAccountId.Value == Guid.Empty)
            return Fail(RuleNoExpenseAccount,
                "ยังไม่ได้เลือกผังบัญชีค่าใช้จ่ายของรายการนี้ — เลือกบัญชีค่าใช้จ่ายก่อนบันทึก "
                + "(ถ้าบันทึกโดยไม่เลือก เงินจะออกจากลิ้นชักโดยบัญชีไม่รู้)");
        if (!fundAccountId.HasValue || fundAccountId.Value == Guid.Empty)
            return Fail(RuleNoFundAccount,
                "กองทุนเงินสดย่อยนี้ยังไม่ได้ผูกกับผังบัญชี — ไปที่ตั้งค่าเงินสดย่อย "
                + "แล้วเลือกผังบัญชี \"เงินสดย่อย\" ของกองทุนก่อน");

        var noReceipt = string.IsNullOrWhiteSpace(receiptReference);
        return new Plan(true, expenseAccountId.Value, fundAccountId.Value,
            NonDeductible: noReceipt,
            RuleCode: noReceipt ? RuleNoReceipt : RuleOk,
            Error: null);
    }

    /// <summary>
    /// เติมเงินสดย่อย — Dr ผังของกองทุน / Cr ผังแหล่งเงิน (ธนาคาร/เงินสด).
    /// </summary>
    /// <param name="fundAccountId">`PettyCashFund.LinkedAccountId`</param>
    /// <param name="sourceAccountId">ผังของบัญชีที่โอนเงินออกมา</param>
    /// <param name="amount">ยอดเติม</param>
    public static Plan ForReplenishment(Guid? fundAccountId, Guid? sourceAccountId, decimal amount)
    {
        if (amount <= 0m)
            return Fail(RuleBadAmount, "ยอดเติมต้องมากกว่า 0");
        if (!fundAccountId.HasValue || fundAccountId.Value == Guid.Empty)
            return Fail(RuleNoFundAccount,
                "กองทุนเงินสดย่อยนี้ยังไม่ได้ผูกกับผังบัญชี — ไปที่ตั้งค่าเงินสดย่อย "
                + "แล้วเลือกผังบัญชี \"เงินสดย่อย\" ของกองทุนก่อน");
        if (!sourceAccountId.HasValue || sourceAccountId.Value == Guid.Empty)
            return Fail(RuleNoSourceAccount,
                "ยังไม่รู้ว่าเงินที่เติมมาจากบัญชีไหน — เลือกบัญชีธนาคาร/เงินสดต้นทาง "
                + "ก่อนบันทึก (ไม่งั้นยอดธนาคารในงบจะไม่ลดตามเงินที่โอนออกจริง)");
        if (fundAccountId.Value == sourceAccountId.Value)
            return Fail(RuleNoSourceAccount,
                "บัญชีต้นทางกับผังของกองทุนเป็นบัญชีเดียวกัน — เลือกบัญชีต้นทางใหม่");

        return new Plan(true, fundAccountId.Value, sourceAccountId.Value, false, RuleOk, null);
    }
}
