namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §5.4 · G1 · คำตัดสิน 264-5-10) — <b>ตัวตัดสินเดียว "รายการเงินออกในรายการเดินบัญชีของผู้ซื้อ ⇒ ยืนยันว่าเงินของการแจ้งคู่ค้าออกจากบัญชีนั้นจริงไหม"</b> ·
/// คู่สมมาตรของ <see cref="BankReceiptAutoSettle"/> (ฝั่งเงินเข้า) · pure · <b>ไม่เรียก AI</b> (ตัวเลขล้วน — F2 ข้อ 3 · kill-switch AI ไม่มีผล ⇒ ไม่มี AiFeatureKey)
/// <list type="bullet">
/// <item>บรรทัดที่นับ: <b>เงินออก</b> (ถอน/โอน) ที่ยังไม่จับคู่ของบัญชีผู้ซื้อ — บรรทัดเงินเข้าไม่ถูกแตะ (ไม่แย่งบรรทัดของ <see cref="BankReceiptAutoSettle"/>)</item>
/// <item><b>ตรงทุกข้อ</b>: ยอด = เงินของการแจ้ง ±0.00 (ไม่มีช่วงเผื่อ) · วันที่ (เวลาไทย) ห่างจากวันที่ผู้ขายรับเงินไม่เกิน <see cref="DateWindowDays"/> วัน ·
/// <b>เลขบัญชีปลายทาง</b> (บัญชีรับเงินของผู้ขาย) ปรากฏบนบรรทัด: เลขปิดบางที่หลักที่เห็น ≥ <see cref="MinVisibleDigits"/> ตรงตำแหน่งนับจากขวา หรือเลขเต็มตรงทั้งหมด
/// (ตัวเทียบหลักเดียวกับ <see cref="SlipAccountMatch"/> — ห้าม fuzzy) · ไม่รู้บัญชีรับเงินของผู้ขาย ⇒ ไม่ตัดสิน</item>
/// <item><b>หนึ่งต่อหนึ่ง</b>: การแจ้งนี้ตรงบรรทัดเดียว <b>และ</b> บรรทัดนั้นตรงการแจ้งนี้การแจ้งเดียว (การแจ้งที่ยังเปิดอื่นของบริษัทเดียวกัน) — มิฉะนั้นเป็นคำแนะนำ</item>
/// <item>แหล่งของบรรทัดตามกติกา H1 เดิม (ตัวเดียว <see cref="BankReceiptAutoSettle.EvidenceProblem"/>): ไฟล์ statement หน้าบัญชีธนาคารที่ผู้อัปโหลดมีสิทธิ์อนุมัติ = หลักฐาน ·
/// feed/ไฟล์ศูนย์นำเข้า/API/พิมพ์มือ = เสนอเท่านั้น</item>
/// </list>
/// ผล <see cref="Kind.Match"/> = ชั้น <c>BankLine</c> ของ <see cref="PartnerPaySourceAccount"/> (ชั้นเดียวที่ยกสลิปจาก preselect เป็นบันทึกเองได้ — <see cref="PartnerPaySourceAccount.SlipConfirmedByBankLine"/>)
/// </summary>
public static class BankPaymentLineMatch
{
    public const string RuleMatch = "BANK-PAY-LINE-MATCH";
    public const string RuleSuggest = "BANK-PAY-LINE-SUGGEST";
    public const string RuleNone = "BANK-PAY-LINE-NONE";

    /// <summary>กรอบวัน (team-264-a2b-design §5.4: ±3 วันจาก <c>PaidAt</c>) — โอนวันศุกร์ ธนาคารลงวันจันทร์</summary>
    public const int DateWindowDays = 3;

    /// <summary>หลักที่เห็นขั้นต่ำของเลขบัญชีปลายทางบนบรรทัด (สูงกว่าสลิป 1 หลัก — ข้อความ statement ไม่ใช่ OCR · statement ไทยแสดง 4 หลักท้าย)</summary>
    public const int MinVisibleDigits = 4;

    public enum Kind { None, Suggest, Match }

    /// <param name="IsOutflow">ถอน/โอนออก (ผู้เรียกแปลงจากชนิดบรรทัด — ค่าธรรมเนียม/ดอกเบี้ย/เงินเข้า = false)</param>
    /// <param name="Amount">ยอดบรรทัด (ค่าบวก)</param>
    /// <param name="Text">คำอธิบาย + เลขอ้างอิง + ผู้รับ ของบรรทัด (ข้อความที่ธนาคารส่งมา)</param>
    /// <param name="Unmatched">ยังไม่จับคู่/ไม่ถูกจองโดยกระทบยอดอื่น</param>
    /// <param name="EvidenceProblem">ผลของ <see cref="BankReceiptAutoSettle.EvidenceProblem"/> (null = แหล่งของบรรทัดเป็นหลักฐานได้)</param>
    public sealed record Line(Guid Id, Guid BankAccountId, DateTime TransactionDate, bool IsOutflow, decimal Amount, string? Text, bool Unmatched, string? EvidenceProblem);

    /// <param name="Cash">เงินที่ออกจากผู้ซื้อตามการแจ้ง</param>
    /// <param name="PaidAtUtc">วันที่ผู้ขายรับเงิน (UTC)</param>
    /// <param name="PayeeAccountDigits">เลขบัญชีรับเงินของผู้ขาย (ตัวเลขล้วน) · null = ไม่รู้ ⇒ ไม่ตัดสิน</param>
    public sealed record Target(Guid NoticeId, decimal Cash, DateTime PaidAtUtc, string? PayeeAccountDigits);

    /// <param name="LineId">มีค่าเมื่อ <see cref="Kind.Match"/> หรือคำแนะนำบรรทัดเดียว</param>
    public sealed record Result(Kind Kind, Guid? LineId, Guid? BankAccountId, DateTime? LineDate, string Reason, string RuleCode)
    {
        /// <summary>ไม่มีจุดเรียก AI</summary>
        public bool UsedAi => false;
        /// <summary>เป็นหลักฐานชั้นรายการเดินบัญชีได้ (ผู้เรียกส่งเข้า <see cref="PartnerPaySourceAccount.Facts.BankLine"/> เฉพาะเมื่อ true)</summary>
        public bool IsEvidence => Kind == Kind.Match && LineId != null && BankAccountId != null;
    }

    public const string NoPayeeReason = "ไม่รู้เลขบัญชีรับเงินของผู้ขาย — จับคู่รายการเดินบัญชีของเราไม่ได้ (ระบบไม่จับจากยอดอย่างเดียว)";
    public const string NoLineReason = "ยังไม่พบรายการเงินออกในรายการเดินบัญชีของเราที่ยอด วันที่ และบัญชีปลายทางตรงการแจ้งนี้";
    public const string ManyLinesReason = "มีรายการเงินออกที่ตรงหลายรายการ — ระบบไม่เลือกให้ (ยืนยันเองที่หน้าธนาคาร)";
    public const string SharedLineReason = "รายการเงินออกนี้ตรงการแจ้งของคู่ค้ามากกว่าหนึ่งรายการ — ระบบไม่เลือกให้";
    public const string MatchReason = "รายการเดินบัญชีของเรายืนยัน: ยอด วันที่ และบัญชีปลายทางตรงการแจ้งนี้รายการเดียว";

    public static Result Decide(Target t, IReadOnlyList<Target> otherOpen, IReadOnlyList<Line> lines)
    {
        if (!HasPayee(t.PayeeAccountDigits)) return new(Kind.None, null, null, null, NoPayeeReason, RuleNone);
        var hits = lines.Where(l => Fits(t, l)).ToList();
        if (hits.Count == 0) return new(Kind.None, null, null, null, NoLineReason, RuleNone);
        if (hits.Count > 1) return new(Kind.Suggest, null, null, null, ManyLinesReason, RuleSuggest);
        var line = hits[0];
        if (otherOpen.Any(o => o.NoticeId != t.NoticeId && Fits(o, line)))
            return new(Kind.Suggest, line.Id, line.BankAccountId, line.TransactionDate, SharedLineReason, RuleSuggest);
        if (line.EvidenceProblem is string problem)
            return new(Kind.Suggest, line.Id, line.BankAccountId, line.TransactionDate, problem, RuleSuggest);
        return new(Kind.Match, line.Id, line.BankAccountId, line.TransactionDate, MatchReason, RuleMatch);
    }

    /// <summary>บรรทัดนี้ตรงการแจ้งนี้ไหม (ทุกข้อ — ไม่รวมด่านแหล่งของบรรทัด)</summary>
    private static bool Fits(Target t, Line l)
    {
        return l.IsOutflow && l.Unmatched
            && Math.Abs(l.Amount) == Math.Round(t.Cash, 2, MidpointRounding.AwayFromZero)
            && Math.Abs((LocalDate(l.TransactionDate) - LocalDate(t.PaidAtUtc)).TotalDays) <= DateWindowDays
            && PayeeOnLine(l.Text, t.PayeeAccountDigits);
    }

    /// <summary>วันที่ตามเวลาไทย (+07:00) — รายการเดินบัญชีนำเข้าเป็นวันที่ (ไม่มีเวลา) ⇒ เทียบเป็นวัน</summary>
    private static DateTime LocalDate(DateTime d) => (d.TimeOfDay == TimeSpan.Zero ? d : d.AddHours(7)).Date;

    private static bool HasPayee(string? digits) => !string.IsNullOrEmpty(digits) && digits.Length >= MinVisibleDigits && digits.All(char.IsAsciiDigit);

    /// <summary>เลขบัญชีปลายทางปรากฏบนข้อความบรรทัดไหม — เลขเต็ม (≥ 10 หลัก) ต้องเท่ากันทั้งหมด · เลขปิดบาง (มีตัว x/X/*/•) ต้องมีหลักที่เห็น ≥ <see cref="MinVisibleDigits"/>
    /// และทุกหลักตรงตำแหน่งนับจากขวา (<see cref="SlipAccountMatch.DigitsFit"/>) · ตัวเลขล้วนสั้น (เช่น เลขอ้างอิง/วันที่) ไม่นับ</summary>
    private static bool PayeeOnLine(string? text, string? payeeDigits)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasPayee(payeeDigits)) return false;
        foreach (var token in Tokens(text))
        {
            var digits = new string(token.Where(char.IsAsciiDigit).ToArray());
            var masked = token.Any(IsMaskChar);
            if (!masked)
            {
                if (digits.Length >= 10 && digits == payeeDigits) return true;
                continue;
            }
            var visible = OcrSlipText.MaskDigits(new string(token.Select(c => IsMaskChar(c) ? 'x' : c).ToArray()));
            if (visible.Count >= MinVisibleDigits && SlipAccountMatch.DigitsFit(payeeDigits!, visible)) return true;
        }
        return false;
    }

    private static bool IsMaskChar(char c) => c is 'x' or 'X' or '*' or '•' or '●';

    /// <summary>ก้อนที่อาจเป็นเลขบัญชี: ตัวเลข/ตัวปิด/ขีด ติดกัน (ตัดขีดหัวท้าย)</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text + " ")
        {
            if (char.IsAsciiDigit(c) || IsMaskChar(c) || c == '-') { sb.Append(c); continue; }
            if (sb.Length > 0)
            {
                var tok = sb.ToString().Trim('-');
                if (tok.Count(char.IsAsciiDigit) >= MinVisibleDigits) yield return tok;
                sb.Clear();
            }
        }
    }
}
