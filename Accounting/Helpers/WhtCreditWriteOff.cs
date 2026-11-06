using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ตัดสูญเครดิตภาษีถูกหัก ณ ที่จ่าย (11910) — ตัวตัดสินเดียว</b> (รอบ 243a · คำตัดสิน 243-1…243-7 · erp-review/2026-11-06/AUTO_SEND_WHT_DESIGN.md G-9)
///
/// <para><b>ข้อเท็จจริงทางภาษี:</b> ภาษีที่ลูกค้าหักเราไว้ เป็น "ภาษีเงินได้ของเราที่จ่ายล่วงหน้า" — นำไปเครดิตกับภาษีที่ต้องชำระใน ภ.ง.ด.51/50 ได้
/// (ป.รัษฎากร ม.69 ทวิ วรรคท้าย — ถือเป็นเครดิตในการคำนวณภาษีที่ต้องชำระ) โดยต้องมี<b>หนังสือรับรอง 50 ทวิ</b> ที่ผู้จ่ายมีหน้าที่ออกให้ (ม.50 ทวิ) เป็นหลักฐาน ·
/// ไม่มีใบ (และไม่มีหลักฐาน e-Withholding Tax) = เครดิตไม่ได้ · ยอดที่ค้างใน 11910 จึงไม่ใช่สินทรัพย์ที่เรียกคืนได้อีกต่อไปเมื่อยืนยันว่าลูกค้าไม่ออกใบ</para>
///
/// <para><b>คำตัดสิน (243-1):</b> ลง Dr <see cref="ExpenseAccountCode"/> "ภาษีเงินได้ถูกหัก ณ ที่จ่ายที่ขอเครดิตไม่ได้" (กลุ่ม 580 ภาษีเงินได้) / Cr 11910 ·
/// เป็น<b>ภาษีเงินได้ของบริษัทเอง</b> ⇒ ไม่ใช่รายจ่ายหักได้ ตาม ม.65 ตรี (6 ทวิ) · ใช้กลไกเดียวของระบบที่ตัดผังกลุ่ม 580 ออกจากรายจ่ายใน
/// ภ.ง.ด.50/51/เครื่องคำนวณ CIT (<see cref="CitCalculation.ExceptCitExpense"/>) ⇒ "บวกกลับ" อัตโนมัติเต็มจำนวน <b>โดยไม่มีเส้นบวกกลับที่สอง</b>
/// (ถ้าใส่ใน <c>ComputeSection65TerAddBackAsync</c> ด้วย = นับซ้ำ) · ⚠️ ผู้ทำบัญชียืนยันผัง (DESIGN L-12)</para>
///
/// <para><b>กติกา:</b> ทำได้ทุกเวลาเมื่อผู้มีสิทธิ์ลงบัญชี (Journal.Manage) ยืนยันว่าลูกค้าไม่ออกใบ + ระบุเหตุผล ≥ <see cref="MinReasonLength"/> ตัวอักษร ·
/// วันที่ตัดสูญอยู่ในงวดที่เปิดและไม่เป็นวันในอนาคต · ตัดทั้งแถว (ไม่รองรับบางส่วน — สิทธิ์เครดิตผูกกับใบ 50 ทวิ ทั้งใบ) · ไม่ตัดอัตโนมัติ (หลักการ #3) —
/// สิ้นรอบระบบแค่ "เสนอรายการ" (<see cref="IsYearEndCandidate"/>) · ได้ใบภายหลัง ⇒ กลับรายการ JE + คืนสถานะ "ได้รับใบแล้ว" (<see cref="NeedsReversal"/>)</para>
/// </summary>
public static class WhtCreditWriteOff
{
    /// <summary>ผังค่าใช้จ่าย (ภาษีเงินได้) ที่ลงตอนตัดสูญ — แถวอยู่ใน <c>ChartOfAccountTemplates.GetCommonAccounts</c> ใต้กลุ่ม 580</summary>
    public const string ExpenseAccountCode = "58010";
    /// <summary>ผังสินทรัพย์ภาษีถูกหัก ณ ที่จ่าย</summary>
    public const string WhtAssetAccountCode = "11910";
    public const int MinReasonLength = 10;

    public const string RuleCode = "RD-65ter(6bis)-WHT-WRITEOFF";
    public const string LegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.69 ทวิ (เครดิตได้เมื่อมีหนังสือรับรอง) · ม.65 ตรี (6 ทวิ) (ภาษีเงินได้ของบริษัท — ไม่ใช่รายจ่าย)";
    public const string ReversalRuleCode = "RD-50bis-WHT-WRITEOFF-REVERSE";
    /// <summary>ต้นทางหายหลังตัดสูญ (ยกเลิกใบขาย · คืนเงินแพลตฟอร์ม) ⇒ กลับ JE ตัดสูญ (ฝ่ายค้าน S4)</summary>
    public const string UndoRuleCode = "RD-WHT-WRITEOFF-UNDO-SOURCE";
    public const string ReversalLegalReference = "ป.รัษฎากร ม.50 ทวิ · ม.69 ทวิ — ได้รับหนังสือรับรองภายหลัง เครดิตได้ (ขอคืน/ยื่นเพิ่มเติมภายใน 3 ปี ม.63)";

    /// <summary>คำนำหน้าเลขอ้างอิง JE ตัดสูญ — ฝั่งเขียน (<see cref="Reference"/>) และฝั่งอ่าน (กระทบยอดทะเบียน↔GL · <see cref="IsWriteOffReference"/>) ใช้ตัวเดียว</summary>
    public const string ReferencePrefix = "WHT-WO:";

    public static string Reference(Guid creditId) => ReferencePrefix + creditId.ToString("N");

    public static bool IsWriteOffReference(string? reference)
        => reference != null && reference.StartsWith(ReferencePrefix, StringComparison.Ordinal);

    public enum Outcome { Allowed, AlreadyWrittenOff, Blocked }

    public sealed record Decision(Outcome Outcome, string? Problem)
    {
        public bool Allowed => Outcome == Outcome.Allowed;
    }

    /// <summary>ตัดสินว่าตัดสูญแถวนี้ได้ไหม (pure) — ผู้เรียกส่งข้อเท็จจริงที่อ่านจากฐานแล้ว</summary>
    /// <param name="hasWriteOffJournal">แถวตัดสูญพร้อม JE ที่ยังมีผลอยู่ (= <see cref="NeedsReversal"/>)</param>
    /// <param name="closedPeriodReason">ข้อความงวดปิดของวันที่ตัดสูญ (<c>JournalEntryBuilder.ClosedPeriodReasonAsync</c>) — null = เปิด</param>
    /// <param name="glBalance">ยอด 11910 ที่ยังล้างได้<b>ของปีภาษีของแถว</b> (ฝ่ายค้าน S1 — ไม่ใช่ยอดสะสมทั้งบริษัท):
    /// Dr 11910 ของรอบนั้น − JE ปิดปีของปีนั้น − JE ตัดสูญของแถวปีนั้น (ลงวันไหนก็ตาม) · <c>WhtCreditService.TaxYearWhtAvailableAsync</c></param>
    /// <param name="checkReason">false = หน้ายืนยัน (ผู้ใช้ยังไม่ได้พิมพ์เหตุผล) · การลงบัญชีจริงตรวจเสมอ</param>
    public static Decision Decide(WhtCreditStatus status, bool hasWriteOffJournal, decimal amount, string? reason,
        DateTime writeOffDate, DateTime todayBangkok, string? closedPeriodReason, decimal glBalance, bool checkReason = true)
    {
        if (status == WhtCreditStatus.Expired && hasWriteOffJournal)
            return new(Outcome.AlreadyWrittenOff, null);
        if (status == WhtCreditStatus.Expired)
            return new(Outcome.Blocked, "รายการนี้ถูกตัดออกจากเครดิตไว้ก่อนแล้วโดยไม่มีรายการบัญชี (ข้อมูลก่อนรอบ 243a หรือคืนเงินอ้างใบลดหนี้) — "
                + "ระบบไม่ลงบัญชีซ้ำให้เพราะไม่รู้ว่ายอด 11910 ถูกล้างไปแล้วหรือยัง · ตรวจยอดบัญชี 11910 แล้วลงใบสำคัญทั่วไปเองถ้ายังค้าง");
        if (status == WhtCreditStatus.Claimed)
            return new(Outcome.Blocked, "รายการนี้ใช้เครดิตในแบบยื่นไปแล้ว — ตัดสูญไม่ได้ (ถ้าต้องแก้ ให้ยกเลิกการใช้เครดิตของแบบนั้นก่อน)");
        if (amount <= 0m)
            return new(Outcome.Blocked, "ยอดภาษีถูกหักของรายการนี้เป็น 0 — ไม่มีอะไรให้ตัดสูญ");
        if (checkReason && (reason ?? "").Trim().Length < MinReasonLength)
            return new(Outcome.Blocked, $"ระบุเหตุผลที่ตัดสูญอย่างน้อย {MinReasonLength} ตัวอักษร (เช่น ทวงแล้ว 3 ครั้ง ลูกค้าปิดกิจการ) — เก็บเป็นหลักฐานในประวัติ");
        if (writeOffDate.Date > todayBangkok.Date)
            return new(Outcome.Blocked, "วันที่ตัดสูญเป็นวันในอนาคตไม่ได้ — เลือกวันนี้หรือวันก่อนหน้าในงวดที่เปิดอยู่");
        if (closedPeriodReason != null)
            return new(Outcome.Blocked, closedPeriodReason);
        if (glBalance + 0.01m < amount)
            return new(Outcome.Blocked, $"ยอด 11910 ของปีภาษีนี้ที่ยังล้างได้ ({glBalance:N2}) น้อยกว่ายอดที่จะตัด ({amount:N2}) — "
                + "แปลว่ายอดนี้ไม่เคยลงบัญชี หรือถูกล้างไปแล้ว (ปิดปีภาษีแล้ว · ตัดสูญรายการอื่นของปีเดียวกัน) · ตรวจการกระทบยอดก่อน");
        return new(Outcome.Allowed, null);
    }

    /// <summary>แถว "ตัดสูญพร้อม JE ที่ยังมีผล" — ได้รับใบภายหลัง/ต้นทางถูกยกเลิก/คืนเงิน ⇒ ต้องกลับรายการ JE ตัดสูญก่อนเปลี่ยนแถว ·
    /// มีตัวกลับแล้ว (<paramref name="writeOffReversalJournalEntryId"/>) = ไม่มีผลแล้ว (แถวคงสถานะ Expired เมื่อเครดิตหมดไปด้วยเหตุอื่น)</summary>
    public static bool NeedsReversal(WhtCreditStatus status, Guid? writeOffJournalEntryId, Guid? writeOffReversalJournalEntryId)
        => status == WhtCreditStatus.Expired && writeOffJournalEntryId.HasValue && !writeOffReversalJournalEntryId.HasValue;

    /// <summary>ฝ่ายค้าน S3 (ตาข่ายชั้นสอง): JE ตัดสูญยังมีผลใน GL ไหม — ด่าน <see cref="WhtWriteOffJournalLock"/> กันเส้นทั่วไปแล้ว ·
    /// ตัวนี้กันกรณีหลุด (ข้อมูลเก่า/ทางที่ยังไม่รู้จัก) ไม่ให้ทะเบียนนับยอดตัดสูญที่ GL กลับไปแล้ว</summary>
    public static bool JournalStillLive(JournalEntryStatus status, Guid? reversedByEntryId, bool isDeleted)
        => status == JournalEntryStatus.Posted && !reversedByEntryId.HasValue && !isDeleted;

    /// <summary>แถวที่สิ้นรอบควรเสนอให้ผู้ทำบัญชีตัดสินใจ (ตามต่อ/ตัดสูญ) — ยังรอใบ และรอบบัญชีของแถวสิ้นสุดแล้ว · ไม่ตัดอัตโนมัติ</summary>
    /// <param name="missingAmount">ยอดที่ยังขาดใบ (<see cref="WhtCreditableAmount.Missing"/>) — ได้ใบครบแล้ว (0) ไม่ใช่ผู้สมัคร (ฝ่ายค้าน WP-09 M1)</param>
    public static bool IsYearEndCandidate(WhtCreditStatus status, decimal missingAmount, DateTime fiscalYearEnd, DateTime todayBangkok)
        => status == WhtCreditStatus.Pending && missingAmount > 0.005m && fiscalYearEnd.Date < todayBangkok.Date;

    /// <summary>ยอดที่ตัดสูญได้ของแถว (ฝ่ายค้าน WP-09 M1) — <b>เฉพาะส่วนที่ไม่มีใบ</b>: Pending = ยอดขาดใบ · Received ที่ได้ใบผ่านกล่องรับ = เศษที่ใบต่ำกว่า
    /// (≤ ฿1) · Received รุ่นเก่า (ReceivedAmount 0) = เต็มแถว (พฤติกรรมเดิม — ผู้ทำบัญชีเลือกเอง) · ส่วนที่มีใบแล้วไม่ถูกตัดสูญ (คงเป็นเครดิต)</summary>
    public static decimal AmountOf(WhtCreditStatus status, decimal whtAmount, decimal receivedAmount)
    {
        var wht = Math.Max(0m, whtAmount);
        var v = status switch
        {
            WhtCreditStatus.Pending => WhtCreditableAmount.Missing(status, whtAmount, receivedAmount),
            WhtCreditStatus.Received => receivedAmount > 0.005m ? Math.Max(0m, wht - Math.Max(0m, receivedAmount)) : wht,
            _ => wht,
        };
        return Math.Round(v, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ผังที่ใช้ตัดสูญต้องเป็นผังภาษีเงินได้ (กลุ่ม 580) — มิฉะนั้นรายจ่ายนี้ไหลเข้า ภ.ง.ด.50 เป็นรายจ่ายหักได้ (ผิด ม.65 ตรี (6 ทวิ))</summary>
    public static bool IsNonDeductibleAccount(string? accountCode, AccountType accountType, string? parentAccountCode)
        => CitCalculation.IsCitExpenseAccount(accountCode, accountType, parentAccountCode);

    /// <summary>ยอดที่ ภ.ง.ด.50 ไม่ให้หักเป็นรายจ่าย (ผลทางภาษีที่แสดงตอนยืนยัน) — ผังภาษีเงินได้ = เต็มจำนวน · ผังอื่น = 0 (ซึ่งผู้เรียกต้องปฏิเสธก่อนลงบัญชี)</summary>
    public static decimal NonDeductibleAmount(decimal amount, string? accountCode, AccountType accountType, string? parentAccountCode)
        => IsNonDeductibleAccount(accountCode, accountType, parentAccountCode) ? Math.Round(amount, 2, MidpointRounding.AwayFromZero) : 0m;

    /// <summary>ข้อความผลทางภาษีที่หน้าจอแสดงตอนยืนยัน (server computes · page displays)</summary>
    public static string TaxEffectText(decimal amount)
        => $"ไม่ใช่รายจ่ายหักได้ (ม.65 ตรี (6 ทวิ)) — ภ.ง.ด.50/51 ตัด {amount:N2} บาทออกจากรายจ่ายอัตโนมัติ · "
           + "เครดิตภาษีของปีนั้นลดลงเท่ากัน · ได้รับใบภายหลัง ⇒ กด \"ได้รับใบแล้ว\" ระบบกลับรายการและคืนเครดิตให้";
}
