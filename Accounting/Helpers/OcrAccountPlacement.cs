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
/// ใช้กับใบฝั่งซื้อที่เดบิตค่าใช้จ่าย (ใบสำคัญจ่าย · ใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย · ใบรับรองแทนใบเสร็จ) เท่านั้น — ใบลดหนี้ฝั่งซื้อ (Dr เจ้าหนี้ / Cr
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

    /// <summary>ชนิดเอกสารที่ "Dr = ค่าใช้จ่าย/สินทรัพย์ · Cr = แหล่งเงิน/เจ้าหนี้" — ที่เดียวของรายการนี้
    /// (ใบรับรองแทนใบเสร็จ = ใบจ่ายค่าใช้จ่ายรูปแบบหนึ่ง · ฝ่ายค้านรอบ f1690d11)</summary>
    private static readonly HashSet<DocumentType> ExpenseShaped = new()
    {
        DocumentType.PaymentVoucher, DocumentType.Expense,
        DocumentType.PurchaseInvoice, DocumentType.CertificateInLieu,
    };

    /// <summary>ด่านนี้ใช้กับเอกสาร<b>ชนิดที่จะสร้างจริง</b>ไหม — ฝั่งตัดสินด้วย
    /// <see cref="DocumentSide.IsSales"/> ตัวเดียวกับเส้นสร้างเอกสาร</summary>
    public static bool AppliesTo(DocumentType type, string? ourRole)
        => ExpenseShaped.Contains(type) && !DocumentSide.IsSales(type, ourRole);

    /// <summary>ตัวตัดสิน "ใช้ด่านไหม" ของ<b>แถวสแกน</b> (ไปป์ไลน์ · แก้ผลสแกน · การ์ดบนหน้าจอ · ลงทะเบียนสินทรัพย์) —
    /// ชนิดเอกสารมาจาก <see cref="OcrTargetDocumentType.Resolve"/> ตัวเดียวกับเส้นสร้างเอกสาร ⇒ เป้าหมายว่าง/บทบาทไม่รู้
    /// ได้คำตอบเดียวกันทุกทาง (เดิมไปป์ไลน์ดู <c>OurRole == "Seller"</c> + ชื่อเป้าหมายตรง ๆ ส่วนเส้นอ่านดูชนิดที่ resolve แล้ว
    /// ⇒ เป้าหมายว่างได้สองคำตอบ)</summary>
    public static bool AppliesToScan(
        string? scanTargetDocumentType, string? scannedPaperType, bool hasLinkedPurchaseOrder, string? ourRole)
    {
        var resolved = OcrTargetDocumentType.Resolve(null, scanTargetDocumentType, scannedPaperType, hasLinkedPurchaseOrder);
        return !resolved.IsDeposit && AppliesTo(resolved.Type, ourRole);
    }

    /// <summary>บัญชีประเภทนี้สอนตัวเรียนรู้ (ประวัติผู้ขาย · ตัวเรียนหมวด · feedback ผังบัญชี) เป็น "ผังของบรรทัด" ได้ไหม —
    /// หนี้สิน/ทุน ไม่ใช่คำตอบของ "จ่ายค่าอะไร" ทั้งฝั่งซื้อและขาย (สอนไป = ใบถัดไปของผู้ขายได้เดบิต 21230 อีก) ·
    /// ไม่รู้ประเภท (null) = สอนตามเดิม</summary>
    public static bool IsLearnableLineAccount(AccountType? type)
        => type is not (AccountType.Liability or AccountType.Equity);

    /// <summary>ป้ายของผังเดบิตที่ "เรียนไว้" เมื่อเป็นหนี้สิน/ทุน — ข้อความเดียวของหน้า/endpoint ที่แสดงความรู้ของผู้ขาย
    /// (ฝ่ายค้านรอบสาม f1690d11: ห้ามแสดง 21230 ในฐานะ "ผังค่าใช้จ่ายที่ระบบแนะนำ") · null = ผังค่าใช้จ่าย/สินทรัพย์/ไม่รู้ประเภท</summary>
    public static string? LearnedDebitLabel(AccountType? type)
        => IsLearnableLineAccount(type) ? null
            : $"{TypeName(type!.Value)} — ไม่ใช่ผังค่าใช้จ่าย: สแกนของผู้ขายรายนี้จะไม่ถูกเติมบัญชีนี้เอง "
              + "(ระบบเติมผังตามหมวดพร้อมไฮไลต์เหลืองและเหตุผล ให้ผู้ใช้เลือกบัญชีนี้เองเมื่อเป็นการชำระหนี้/ถอนใช้ส่วนตัว)";

    /// <summary>คู่ที่<b>เก็บไว้แล้ว</b> (อาจเป็นค่าที่ผู้ใช้เลือก) — ตัวเดียวของ "เส้นสร้างเอกสาร" และ "การ์ดบนหน้าจอ"
    /// เพื่อให้สิ่งที่ผู้ใช้เห็น = สิ่งที่ลงบัญชีจริง (คู่สลับ ⇒ สลับ · แหล่งเงินเป็นค่าใช้จ่าย/รายได้ ⇒ ตัด · เดบิตหนี้สินลำพังคงไว้)</summary>
    public static Result ResolveStored(GlAccountCandidate? debit, GlAccountCandidate? credit)
        => Check(debit, credit, debitIsSystemSuggested: false);

    public enum CorrectionAction
    {
        /// <summary>เก็บตามที่ผู้ใช้ส่ง</summary>
        Keep = 0,
        /// <summary>ผู้ใช้ส่ง<b>ทั้งคู่</b>มากลับด้าน ⇒ สลับ (ระบบสลับให้ ≠ ผู้ใช้เลือก — ห้ามบันทึกเป็นคำตอบแบบ Explicit)</summary>
        Swap = 1,
        /// <summary>แหล่งเงินที่ผู้ใช้เลือกเป็นค่าใช้จ่าย/รายได้ ⇒ ปฏิเสธคำแก้พร้อมเหตุผล (ห้ามเก็บเงียบ)</summary>
        RejectCredit = 2,
    }

    /// <summary>ผลตัดสินของ "คำแก้ผลสแกน" ที่มีผังบัญชี</summary>
    public readonly record struct CorrectionResult(CorrectionAction Action, string? DebitCode, string? CreditCode, string? Reason);

    /// <summary>
    /// **คำแก้จากหน้าตรวจ** — ตัดสินเฉพาะฝั่งที่ผู้ใช้ส่งมา (ฝ่ายค้านรอบ f1690d11: ห้ามเติม/ย้ายฝั่งที่ผู้ใช้ไม่ได้ส่ง)
    /// <list type="bullet">
    /// <item>ส่งทั้งคู่และกลับด้านพอดี ⇒ <see cref="CorrectionAction.Swap"/></item>
    /// <item>ส่งแหล่งเงินที่เป็นค่าใช้จ่าย/รายได้ (และไม่ใช่คู่สลับที่ส่งมาทั้งคู่) ⇒ <see cref="CorrectionAction.RejectCredit"/></item>
    /// <item>อื่น ๆ (รวมเดบิตหนี้สินที่ผู้ใช้เลือกเอง = ชำระหนี้เดิม) ⇒ <see cref="CorrectionAction.Keep"/></item>
    /// </list>
    /// </summary>
    /// <param name="debit">เดบิตหลังรวมกับค่าที่เก็บไว้</param>
    /// <param name="credit">เครดิตหลังรวมกับค่าที่เก็บไว้</param>
    /// <param name="ourRole">บทบาทเราบนแถวสแกน — ใช้เติมคำแนะนำ "เปลี่ยนเอกสารที่จะสร้าง" เมื่อเราเป็นผู้ขายแต่เป้าหมายเป็นใบสำคัญจ่าย</param>
    /// <param name="targetDocumentType">เป้าหมายบนแถวสแกน</param>
    public static CorrectionResult DecideCorrection(
        GlAccountCandidate? debit, GlAccountCandidate? credit, bool userSentDebit, bool userSentCredit,
        string? ourRole = null, string? targetDocumentType = null)
    {
        var check = Check(debit, credit, debitIsSystemSuggested: false);
        if (check.Verdict == Verdict.Swapped && userSentDebit && userSentCredit)
            return new(CorrectionAction.Swap, check.DebitCode, check.CreditCode, check.Reason);
        if (userSentCredit && credit is { } c && !PaymentSourceOk(c.Type))
            return new(CorrectionAction.RejectCredit, debit?.Code, c.Code,
                $"บัญชีเครดิต (แหล่งเงิน) {Label(c)} เป็น{TypeName(c.Type)} — ใบซื้อ/จ่ายต้องเครดิตเงินสด/ธนาคาร/เจ้าหนี้/ทุน "
                + "ถ้าตั้งใจให้เป็นบัญชีค่าใช้จ่าย ให้เลือกไว้ที่ \"บัญชีเดบิต\" แทน"
                + (SellerOnPaymentVoucher(ourRole, targetDocumentType)
                    ? " · ใบนี้ระบบอ่านว่าเราเป็นผู้ขาย แต่ \"เอกสารที่จะสร้าง\" เป็นใบสำคัญจ่าย — ถ้าเป็นใบที่เรารับเงิน ให้เปลี่ยน \"เอกสารที่จะสร้าง\" (เช่น ใบเสร็จรับเงิน) แทนการเลือกบัญชี"
                    : "")
                + " — ยังไม่บันทึกคำแก้นี้");
        return new(CorrectionAction.Keep, debit?.Code, credit?.Code, null);
    }

    private static bool SellerOnPaymentVoucher(string? ourRole, string? targetDocumentType)
        => string.Equals(ourRole, "Seller", StringComparison.OrdinalIgnoreCase)
           && string.Equals(targetDocumentType, nameof(DocumentType.PaymentVoucher), StringComparison.OrdinalIgnoreCase);

    /// <summary>ผู้ใช้ส่งเดบิต "ค่าที่จอแสดง" กลับมาโดยไม่เปลี่ยน และค่านั้น<b>ระบบเป็นคนวางให้</b> (จอสลับ/แก้คู่ที่เก็บไว้) ⇒ ไม่ใช่คำตอบของผู้ใช้
    /// — ห้ามสอนเป็น Explicit (DOCTRINE §3 ห้ามระบบยืนยันตัวเอง · ฝ่ายค้านรอบสาม f1690d11)</summary>
    /// <param name="submittedDebit">เดบิตที่หน้าเว็บส่งมา</param>
    /// <param name="shownDebit">เดบิตที่จอแสดง (หลัง <see cref="ResolveStored"/>)</param>
    /// <param name="storedDebit">เดบิตที่เก็บไว้ในแถวก่อนแก้</param>
    public static bool IsSystemPlacedEcho(string? submittedDebit, string? shownDebit, string? storedDebit)
        => !string.IsNullOrWhiteSpace(submittedDebit)
           && !OcrCorrectedFieldList.AccountChanged(submittedDebit, shownDebit)
           && OcrCorrectedFieldList.AccountChanged(shownDebit, storedDebit);

    /// <summary>
    /// **ประวัติของผู้ขายรายนี้ (คำตอบของผู้ใช้) ลงเดบิตเป็นหนี้สิน/ทุน แต่ใบนี้ได้ผังค่าใช้จ่าย** — ฝ่ายค้านรอบสาม f1690d11:
    /// ผู้ขาย "คืนเงินกรรมการ" (Dr 21230 / Cr ธนาคาร) หรือ "ถอนใช้ส่วนตัว" (ทุน) ห้ามได้ผังค่าใช้จ่ายแบบมั่นใจ — กดผ่านหนึ่งคลิก = ค่าใช้จ่ายหักภาษีผิด ·
    /// คืนเหตุผล (ผู้เรียกกดความมั่นใจช่องเดบิต ≤ <see cref="HistoryConflictConfidenceCap"/> + ลง ProcessingNotes) · null = ไม่ขัดกัน
    /// </summary>
    /// <param name="learned">ผังเดบิตที่ประวัติ/ตัวเรียนรู้ของผู้ขายเสนอ (พร้อมประเภท)</param>
    /// <param name="finalDebitCode">ผังเดบิตที่ใบนี้จะได้หลังทุกชั้น</param>
    public static string? HistoryConflict(GlAccountCandidate? learned, string? finalDebitCode)
    {
        if (learned is not { } l || ExpenseDebitOk(l.Type)) return null;
        if (string.Equals(l.Code, finalDebitCode, StringComparison.Ordinal)) return null;
        return $"ประวัติของผู้ขายรายนี้ผู้ใช้เคยลงเดบิต {Label(l)} ({TypeName(l.Type)}) — ใบนี้ระบบเติมผัง"
            + (string.IsNullOrEmpty(finalDebitCode) ? "ว่างไว้" : $" {finalDebitCode}")
            + " ให้แทน · ถ้าใบนี้เป็นการชำระหนี้เดิม/ถอนใช้ส่วนตัว ให้เลือก " + l.Code + " เอง (ลงเป็นค่าใช้จ่ายผิด = หักภาษีเกิน)";
    }

    /// <summary>ความมั่นใจสูงสุดของช่องเดบิตเมื่อ <see cref="HistoryConflict"/> ไม่ว่าง — ต่ำกว่าเกณฑ์ไฮไลต์ 0.85</summary>
    public const double HistoryConflictConfidenceCap = 0.5;

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
                $"บัญชีเดบิตที่ระบบเสนอ {Label(debit!.Value)} เป็น{TypeName(debit.Value.Type)} — ระบบไม่เติมให้เป็นผังค่าใช้จ่ายเอง "
                + "ถ้าใบนี้คือการชำระหนี้เดิม/ถอนใช้ส่วนตัว (เช่น คืนเงินกรรมการ) ให้เลือกบัญชีนี้ที่ \"บัญชีเดบิต\" เอง");

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
