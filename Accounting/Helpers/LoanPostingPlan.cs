namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินตัวเดียวของสินเชื่อ — คำขอสร้างใช้ได้ไหม · ลง JE รับเงินกู้ไหม · ผังที่เสนอเป็นค่าตั้งต้น** (pure · OWNER file ·
/// ทีม F1 2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 ข้อ 2)
///
/// <para>═══ ของเดิมพังตรงไหน ═══ (1) หน้า <c>loans.html</c> ส่งชื่อช่องไม่ตรงสัญญา ⇒ สร้างสินเชื่อไม่ได้เลย (400) ·
/// (2) <c>CreateAsync</c> ลง JE รับเงินกู้ "เมื่อมีผังครบ" ไม่มี else ⇒ มีครึ่งเดียวก็ข้าม JE เงียบ ๆ (เงินกู้เข้าแต่ GL ไม่รู้) ·
/// (3) <c>RepaymentFrequency</c> = Quarterly ถูกรับแล้วไม่มีผล (ตารางผ่อนเป็นรายเดือนเสมอ) = silent no-op</para>
///
/// <para>═══ ทิศ ═══ ลง JE ไม่ได้เพราะขาดผัง = ล้มดังพร้อมบอกช่องที่ต้องเลือก (ความถูกต้องของ GL · คำตัดสินข้อ 150) ·
/// สินเชื่อเดิมที่ยอดอยู่ในงบยกมาแล้วเลือก "ไม่ลงบัญชีรับเงินกู้" ได้ตรง ๆ (ทางไปต่อ ไม่ใช่การข้ามเงียบ)</para>
/// </summary>
public static class LoanPostingPlan
{
    public const string RuleInvalidRequest = "LOAN-INVALID-REQUEST";
    public const string RuleDisbursementAccounts = "LOAN-DISBURSEMENT-ACCOUNTS";
    public const string RuleNoMoneyAccount = "LOAN-NO-MONEY-ACCOUNT";
    public const string RuleNoLoanAccount = "LOAN-NO-LOAN-ACCOUNT";
    public const string RuleNoInterestAccount = "LOAN-NO-INTEREST-ACCOUNT";

    /// <summary>ชนิดดอกเบี้ยที่ตารางผ่อนคำนวณได้จริง (<c>LoanService.CalculateMonthlyPayment/GenerateScheduleAsync</c>)</summary>
    public static readonly string[] InterestTypes = { "Fixed", "Floating", "FlatRate" };

    /// <summary>ความถี่ที่ตารางผ่อนรองรับจริง — ตารางสร้างทีละเดือนเท่านั้น (รับค่าอื่นแล้วไม่มีผล = silent no-op)</summary>
    public const string MonthlyFrequency = "Monthly";

    /// <summary>ผังดอกเบี้ยจ่ายมาตรฐาน (<c>ChartOfAccountTemplates</c>: 55110 ดอกเบี้ยจ่าย - เงินกู้ยืม)</summary>
    public const string DefaultInterestExpenseCode = "55110";

    /// <summary>ผังเงินกู้ที่เสนอตามชนิด/ระยะ (ผังมาตรฐาน): วงเงินเบิกเกินบัญชี → 21100 · ไม่เกิน 12 เดือน → 21300 เงินกู้ยืมระยะสั้น ·
    /// เกิน 12 เดือน → 22100 เงินกู้ยืมระยะยาว · เป็นค่าตั้งต้นที่ผู้ใช้เปลี่ยนได้ ไม่ใช่การตัดสินแทน</summary>
    public static string SuggestLoanAccountCode(string? loanType, int termMonths)
        => string.Equals(loanType, "LineOfCredit", StringComparison.OrdinalIgnoreCase)
           || string.Equals(loanType, "Overdraft", StringComparison.OrdinalIgnoreCase)
            ? "21100"
            : termMonths > 0 && termMonths <= 12 ? "21300" : "22100";

    /// <summary>ตรวจคำขอสร้างสินเชื่อ — null = ใช้ได้ · ข้อความ = ช่องที่ผิด + ทางแก้</summary>
    public static string? ValidateCreate(string? name, decimal principal, decimal interestRate, int termMonths,
        string? interestType, string? repaymentFrequency, DateTime disbursementDate, DateTime? firstPaymentDate)
    {
        if (string.IsNullOrWhiteSpace(name)) return "กรอก “ชื่อสินเชื่อ” ก่อนบันทึก";
        if (principal <= 0) return "“เงินต้น” ต้องมากกว่า 0";
        if (interestRate < 0) return "“อัตราดอกเบี้ย” ติดลบไม่ได้";
        if (termMonths <= 0) return "“ระยะเวลา (เดือน)” ต้องมากกว่า 0";
        if (string.IsNullOrWhiteSpace(interestType) || !InterestTypes.Contains(interestType))
            return "“ประเภทดอกเบี้ย” ต้องเป็น คงที่ / ลอยตัว / flat rate";
        if (!string.IsNullOrWhiteSpace(repaymentFrequency) && repaymentFrequency != MonthlyFrequency)
            return "ตอนนี้ตารางผ่อนรองรับ “ความถี่ชำระ” รายเดือนเท่านั้น — เลือกรายเดือน (ถ้าผ่อนรายไตรมาส ให้บันทึกชำระตามงวดจริงที่หน้าบันทึกชำระ)";
        if (disbursementDate == default) return "กรอก “วันรับเงินกู้”";
        if (firstPaymentDate is DateTime f && f.Date < disbursementDate.Date)
            return "“วันชำระงวดแรก” ต้องไม่ก่อน “วันรับเงินกู้”";
        return null;
    }

    /// <summary>ผลการตัดสินว่าจะลง JE รับเงินกู้ไหม</summary>
    public sealed record Decision(bool Post, string? Error, string RuleCode);

    /// <param name="postRequested">true = ผู้ใช้ขอให้ลงบัญชี · false = ไม่ลง (ยอดยกมาแล้ว) · null = ผู้เรียก API แบบเดิม</param>
    public static Decision ForDisbursement(bool? postRequested, bool hasLoanAccount, bool hasMoneyAccount)
    {
        if (postRequested == false) return new Decision(false, null, "LOAN-DISBURSEMENT-SKIPPED");
        if (postRequested == null && !hasLoanAccount && !hasMoneyAccount)
            return new Decision(false, null, "LOAN-DISBURSEMENT-SKIPPED");
        if (!hasLoanAccount)
            return new Decision(false,
                "ลงบัญชีรับเงินกู้ไม่ได้ — ยังไม่ได้เลือก “ผังบัญชีเงินกู้” (หนี้สิน) · เลือกผังแล้วบันทึกใหม่ "
                + "หรือเอาเครื่องหมายออกจาก “ลงบัญชีรับเงินกู้” ถ้ายอดเงินกู้อยู่ในงบยกมาแล้ว", RuleDisbursementAccounts);
        if (!hasMoneyAccount)
            return new Decision(false,
                "ลงบัญชีรับเงินกู้ไม่ได้ — ยังไม่ได้เลือก “บัญชีรับ/จ่ายเงินกู้” (บัญชีธนาคารหรือเงินสด) · เลือกแล้วบันทึกใหม่ "
                + "หรือเอาเครื่องหมายออกจาก “ลงบัญชีรับเงินกู้” ถ้ายอดเงินกู้อยู่ในงบยกมาแล้ว", RuleDisbursementAccounts);
        return new Decision(true, null, "LOAN-DISBURSEMENT-POST");
    }

    /// <summary>บันทึกชำระแล้วไม่รู้ว่าจ่ายจากบัญชีไหน — ชี้ไปช่องในหน้าบันทึกชำระ / ปุ่มแก้ไขสินเชื่อ (ทั้งสองมีอยู่จริงบนหน้า)</summary>
    public const string NoMoneyAccountMessage =
        "ยังไม่รู้ว่าค่างวดนี้จ่ายจากบัญชีไหน — เลือก “จ่ายจากบัญชี” ในหน้าบันทึกชำระ "
        + "หรือกำหนด “บัญชีรับ/จ่ายเงินกู้” ที่ปุ่ม “แก้ไขสินเชื่อ / ผังบัญชี” ในรายละเอียดสินเชื่อ";

    public const string NoLoanAccountMessage =
        "สินเชื่อนี้ยังไม่ผูก “ผังบัญชีเงินกู้” — กด “แก้ไขสินเชื่อ / ผังบัญชี” ในรายละเอียดสินเชื่อ แล้วเลือกผังก่อนบันทึกชำระ "
        + "(ไม่งั้นรายการบัญชีจะไม่สมดุล)";

    public const string NoInterestAccountMessage =
        "สินเชื่อนี้ยังไม่ผูก “ผังบัญชีดอกเบี้ยจ่าย” — กด “แก้ไขสินเชื่อ / ผังบัญชี” ในรายละเอียดสินเชื่อ แล้วเลือกผังก่อนบันทึกชำระ";
}
