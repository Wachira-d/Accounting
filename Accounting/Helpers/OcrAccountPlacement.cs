using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **คู่บัญชี Dr/Cr ของใบฝั่งซื้อที่มาจากสแกน "วางถูกฝั่งไหม"** — ตัวตัดสินเดียว (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (สแกนจริง f1690d11 · 2026-10-09 · ซีอาร์ซี ไทวัสดุ → หจก.แอม แฮปปี้เนส โรงแรม) ═══
/// ผลสแกนเก็บ <c>SuggestedAccountsJson</c> = Dr <b>21230 เจ้าหนี้กรรมการ</b> (หนี้สิน) / Cr <b>51530 ต้นทุนซ่อมบำรุงห้องพัก</b>
/// (ค่าใช้จ่าย) ⇒ ใบสำคัญจ่ายที่สร้างได้ทุกบรรทัดลง 21230 และ "แหล่งเงิน" = 51530 — <b>กลับด้านทั้งคู่</b>:
/// JE ที่ได้คือ "ลดหนี้กรรมการ + ลดค่าใช้จ่าย" แทน "ค่าใช้จ่ายเพิ่ม + ค้างจ่ายกรรมการ" ·
/// ทั้งเส้นแก้ผลสแกน (<c>SubmitCorrectionAsync</c>) และเส้นอ่านไปสร้างเอกสาร
/// (<c>ResolveScanDebitAccountIdAsync</c> / แหล่งเงิน) รับรหัสทั้งสองฝั่ง<b>โดยไม่ดูประเภทบัญชีเลย</b> ·
/// ตัวเรียนรู้ (ประวัติผู้ขาย/ตัวเรียนหมวด/feedback ของ GL) ถูกสอนด้วย Dr ที่ผิดต่อ ⇒ ใบถัดไปของผู้ขายรายนี้ได้ 21230 เป็นเดบิตอีก</para>
///
/// <para>═══ กติกา (ประเภทบัญชีเป็น "ธงผังบัญชี" = ข้อเท็จจริง · DECISION_DOCTRINE G2) ═══
/// <list type="number">
/// <item><b>คู่สลับ</b> — Dr เป็นหนี้สิน/ทุน และ Cr เป็นค่าใช้จ่าย/รายได้ ขณะที่ "สลับกลับ" แล้วถูกทั้งสองฝั่ง ⇒ สลับ
///   (ไม่มีรายการซื้อ/จ่ายใดที่เดบิตหนี้สินพร้อมเครดิตค่าใช้จ่าย — นั่นคือรายการกลับบัญชี ซึ่งไม่ได้มาจากกระดาษใบซื้อ)</item>
/// <item><b>Cr เป็นค่าใช้จ่าย/รายได้</b> ⇒ ปฏิเสธ (แหล่งเงินต้องเป็นเงินสด/ธนาคาร/เจ้าหนี้/ทุน) — ทั้งค่าที่ระบบเสนอและที่ผู้ใช้เลือก</item>
/// <item><b>Dr เป็นหนี้สิน/ทุน/รายได้</b> ⇒ ปฏิเสธ<b>เฉพาะค่าที่ระบบเสนอ</b> (ตัวเรียนรู้/ประวัติ/AI) ·
///   ผู้ใช้เลือกเองยังได้ (ใบสำคัญจ่ายชำระหนี้เดิม Dr เจ้าหนี้ / Cr ธนาคาร เป็นรายการจริง) — ทางไปต่อของผู้ใช้ (F2 ข้อ 8)</item>
/// </list>
/// ใช้กับใบฝั่งซื้อที่เดบิตค่าใช้จ่าย (ใบสำคัญจ่าย · ใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย) เท่านั้น — ใบลดหนี้ฝั่งซื้อ (Dr เจ้าหนี้ / Cr
/// ต้นทุน) และฝั่งขาย ผู้เรียกต้องไม่ส่งมา (<see cref="AppliesTo"/>)</para>
/// </summary>
public static class OcrAccountPlacement
{
    public enum Verdict
    {
        /// <summary>วางถูกฝั่ง (หรือไม่รู้ประเภท — ไม่ตัดสินแทน)</summary>
        Ok = 0,
        /// <summary>Dr/Cr กลับด้านกันพอดี ⇒ สลับ</summary>
        Swapped = 1,
        /// <summary>Dr ใช้เป็นเดบิตของค่าใช้จ่ายไม่ได้ ⇒ ตัดทิ้ง (เฉพาะค่าที่ระบบเสนอ)</summary>
        DebitRejected = 2,
        /// <summary>Cr ใช้เป็นแหล่งเงินไม่ได้ ⇒ ตัดทิ้ง</summary>
        CreditRejected = 3,
        /// <summary>ผิดทั้งสองฝั่งแต่ไม่ใช่คู่สลับ ⇒ ตัดทั้งคู่</summary>
        BothRejected = 4,
    }

    /// <param name="DebitCode">รหัสเดบิตที่ควรใช้ (null = ตัดทิ้ง ให้ชั้นถัดไป/ผู้ใช้เลือก)</param>
    /// <param name="CreditCode">รหัสเครดิตที่ควรใช้ (null = ตัดทิ้ง)</param>
    /// <param name="Reason">เหตุผลภาษาไทยที่ลง trace/ProcessingNotes ได้ตรง ๆ (null เมื่อ Ok)</param>
    public readonly record struct Result(string? DebitCode, string? CreditCode, Verdict Verdict, string? Reason);

    /// <summary>แท็กใน ProcessingNotes — ผู้ใช้เห็นบนหน้าสแกน</summary>
    public const string Tag = "[ACCT-PLACEMENT]";

    /// <summary>ชนิดเอกสารเป้าหมายที่ "Dr = ค่าใช้จ่าย/สินทรัพย์ · Cr = แหล่งเงิน/เจ้าหนี้" — ที่เดียวของรายการนี้</summary>
    public static bool AppliesTo(string? targetDocumentType, bool isSalesSide)
        => !isSalesSide && targetDocumentType is "PaymentVoucher" or "Expense" or "PurchaseInvoice";

    private static bool ExpenseDebitOk(AccountType t) => t is AccountType.Expense or AccountType.Asset;

    private static bool PaymentSourceOk(AccountType t)
        => t is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    private static string Label(GlAccountCandidate a) => $"{a.Code} {a.Name}".Trim();

    /// <summary>ตรวจคู่ Dr/Cr — ประเภทไม่รู้ (null) = ไม่ตัดสินฝั่งนั้น</summary>
    /// <param name="debit">บัญชีเดบิต (ค่าใช้จ่าย/สินทรัพย์ที่ซื้อ) พร้อมประเภทจากผังของบริษัท</param>
    /// <param name="credit">บัญชีเครดิต (แหล่งเงิน/เจ้าหนี้)</param>
    /// <param name="debitIsSystemSuggested">true = เดบิตมาจากระบบ (ตัวเรียนรู้/ประวัติ/AI) · false = ผู้ใช้เลือกเอง</param>
    public static Result Check(GlAccountCandidate? debit, GlAccountCandidate? credit, bool debitIsSystemSuggested)
    {
        var dCode = debit?.Code;
        var cCode = credit?.Code;
        var debitBad = debit is { } d0 && !ExpenseDebitOk(d0.Type);
        var creditBad = credit is { } c0 && !PaymentSourceOk(c0.Type);

        if (debit is { } d && credit is { } c && debitBad && creditBad
            && ExpenseDebitOk(c.Type) && PaymentSourceOk(d.Type))
            return new(c.Code, d.Code, Verdict.Swapped,
                $"บัญชีเดบิต {Label(d)} ({TypeName(d.Type)}) กับบัญชีเครดิต {Label(c)} ({TypeName(c.Type)}) วางกลับด้าน — "
                + $"ใบซื้อ/จ่ายต้องเดบิตค่าใช้จ่าย เครดิตแหล่งเงิน ⇒ สลับเป็น Dr {c.Code} / Cr {d.Code} · ตรวจก่อนอนุมัติ");

        if (creditBad && debitBad && debitIsSystemSuggested)
            return new(null, null, Verdict.BothRejected,
                $"บัญชีเดบิต {Label(debit!.Value)} ({TypeName(debit.Value.Type)}) ใช้เป็นค่าใช้จ่ายไม่ได้ และบัญชีเครดิต "
                + $"{Label(credit!.Value)} ({TypeName(credit.Value.Type)}) ใช้เป็นแหล่งเงินไม่ได้ — เว้นว่างให้เลือกเอง");

        if (creditBad)
            return new(dCode, null, Verdict.CreditRejected,
                $"บัญชีเครดิต {Label(credit!.Value)} เป็น{TypeName(credit.Value.Type)} — ใช้เป็นแหล่งเงิน/เจ้าหนี้ไม่ได้ "
                + "(ต้องเป็นเงินสด/ธนาคาร/เจ้าหนี้/ทุน) — เว้นว่างให้เลือกเอง");

        if (debitBad && debitIsSystemSuggested)
            return new(null, cCode, Verdict.DebitRejected,
                $"บัญชีเดบิตที่ระบบเสนอ {Label(debit!.Value)} เป็น{TypeName(debit.Value.Type)} — ใบซื้อ/จ่ายต้องเดบิตค่าใช้จ่ายหรือสินทรัพย์ "
                + "(เดบิตหนี้สิน = ตัดหนี้ที่ไม่เคยตั้ง) — ไม่ใช้ค่านี้ เว้นว่างให้เลือกเอง");

        return new(dCode, cCode, Verdict.Ok, null);
    }

    private static string TypeName(AccountType t) => t switch
    {
        AccountType.Asset => "สินทรัพย์",
        AccountType.Liability => "หนี้สิน",
        AccountType.Equity => "ส่วนของเจ้าของ",
        AccountType.Revenue => "รายได้",
        AccountType.Expense => "ค่าใช้จ่าย",
        _ => t.ToString(),
    };
}
