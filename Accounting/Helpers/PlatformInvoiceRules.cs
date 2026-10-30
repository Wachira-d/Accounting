using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชนิดใบแจ้งหนี้ค่าบริการ NextAcc ที่ออกใน tenant ผู้ให้บริการ</summary>
public enum PlatformInvoiceKind
{
    /// <summary>ใบแจ้งหนี้ต่ออายุแพ็กเกจ (<c>RENEW-…</c> · <c>SaasBillingDocumentService.GenerateRenewalInvoiceAsync</c>)</summary>
    Renewal = 1,
    /// <summary>ใบแจ้งหนี้ค่าใช้งานตามจริงรายเดือน (<c>USAGE-…</c> · <c>UsageInvoicingJob</c>)</summary>
    Usage = 2,
    /// <summary>ใบที่แอดมินออกเอง (เรียกเก็บพิเศษ) — อ้างอิงอื่น</summary>
    Other = 3,
}

/// <summary>สถานะของใบแจ้งหนี้จากมุมผู้จ่าย</summary>
public enum PlatformInvoiceState
{
    Paid = 0,
    Open = 1,
    DueToday = 2,
    Overdue = 3,
    Voided = 4,
}

/// <summary>
/// รอบ 219 เฟส 2 (คำตัดสินข้อ 180) — กติกาใบแจ้งหนี้ค่าบริการ NextAcc ที่เดียว (pure): ชนิดจากอ้างอิง · "ยังค้าง" · วันเกินกำหนดนับวันปฏิทินไทย ·
/// ป้ายไทย — ใช้ร่วมโดยศูนย์บิลของผู้เช่า · dunning · หน้าแอดมิน · ลิงก์ชำระ (ห้ามมีสำเนาเงื่อนไข "ค้าง" ที่สองในหน้าเว็บ)
/// </summary>
public static class PlatformInvoiceRules
{
    /// <summary>คำนำหน้าอ้างอิงที่ผู้ออกใบตั้ง — ต้องตรงกับ <c>SaasBillingDocumentService</c> / <c>UsageInvoicingJob</c></summary>
    public const string RenewalReferencePrefix = "RENEW-";
    public const string UsageReferencePrefix = "USAGE-";

    /// <summary>ผู้ติดต่อของบริษัทลูกค้าใน tenant ผู้ให้บริการ (<c>PlatformBillingDocumentIssuer.EnsureContactAsync</c> ประทับ)</summary>
    public const string TenantContactSystem = "NextAccTenant";

    /// <summary>ยอดค้างที่ถือว่าเป็นศูนย์ (เศษสตางค์จากการปัด)</summary>
    public const decimal Epsilon = 0.005m;

    public static PlatformInvoiceKind KindOf(string? reference)
    {
        var r = reference?.Trim() ?? "";
        if (r.StartsWith(RenewalReferencePrefix, StringComparison.OrdinalIgnoreCase)) return PlatformInvoiceKind.Renewal;
        if (r.StartsWith(UsageReferencePrefix, StringComparison.OrdinalIgnoreCase)) return PlatformInvoiceKind.Usage;
        return PlatformInvoiceKind.Other;
    }

    public static string KindLabel(PlatformInvoiceKind k) => k switch
    {
        PlatformInvoiceKind.Renewal => "ต่ออายุแพ็กเกจ",
        PlatformInvoiceKind.Usage => "ค่าใช้งานรายเดือน",
        _ => "ค่าบริการอื่น",
    };

    /// <summary>สถานะที่ "ยังค้างได้" — ชุดเดียวที่ทั้ง query (EF แปลง <c>Contains</c> ได้) และ <see cref="IsOpen"/> ใช้</summary>
    public static readonly DocumentStatus[] OpenStatuses =
        { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };

    /// <summary>สถานะเอกสารที่ "ออกแล้ว" (ร่าง/รออนุมัติ/ถูกปฏิเสธ = ไม่ใช่ใบแจ้งหนี้ที่ลูกค้าต้องจ่าย)</summary>
    public static readonly DocumentStatus[] IssuedStatuses =
        { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue,
          DocumentStatus.Paid, DocumentStatus.Voided };

    public static bool IsIssued(DocumentStatus status) => IssuedStatuses.Contains(status);

    /// <summary>ยังค้างชำระ — ออกแล้ว · ไม่ถูกยกเลิก · ยอดค้าง &gt; 0</summary>
    public static bool IsOpen(DocumentStatus status, decimal balanceDue) =>
        balanceDue > Epsilon && OpenStatuses.Contains(status);

    /// <summary>รายการชำระค่าบริการนี้ "ตัดหนี้ใบแจ้งหนี้อย่างเดียว" (ผูกใบ · ไม่มีเดือนต่ออายุ) — อนุมัติแล้วห้ามแตะวันหมดอายุ/แพ็กเกจ ·
    /// ผูกใบต่ออายุ (เดือน &gt; 0) = ต่ออายุตามเส้นเดิมแล้วตัดชำระใบเดิม · ไม่ผูกใบ = รายการแบบเดิม</summary>
    public static bool IsInvoiceOnlyPayment(Guid? platformInvoiceDocumentId, int requestedPeriodMonths) =>
        platformInvoiceDocumentId != null && requestedPeriodMonths <= 0;

    /// <summary>ใบต่ออายุของรอบที่ผู้เช่ายกเลิกไว้ (<c>CancelAtPeriodEnd</c> · part B) — ห้ามจ่าย (จ่าย = ต่ออายุ ขัดกับการยกเลิก) · ทางไปต่อ = กดใช้ต่อ</summary>
    public static string? RenewalNotPayableReason(bool cancelAtPeriodEnd) => cancelAtPeriodEnd
        ? "ยกเลิกการต่ออายุไว้แล้ว — ถ้าต้องการใช้ต่อ กด \"ใช้ต่อ\" ที่หน้าแพ็กเกจ/ศูนย์บิลก่อนชำระใบต่ออายุ"
        : null;

    /// <summary>รอบ 219 เฟส 2 (S9): ใบต่ออายุที่ออกไว้ให้ "รอบที่กำลังจะถูกแทน" — การอนุมัติที่เริ่ม/ต่อรอบใหม่ (อัปเกรด · ต่ออายุแบบเดิม) โดยไม่ได้จ่ายใบนี้
    /// ⇒ ใบนี้ล้าสมัย (ค้างเป็นหนี้เท็จ/เก็บซ้ำ) · null = ไม่มีใบ/ใบออกให้รอบอื่น/การชำระนี้คือการจ่ายใบนั้นเอง</summary>
    public static Guid? StaleRenewalInvoice(Guid? renewalInvoiceDocumentId, DateTime? renewalInvoiceForEndUtc, DateTime currentEndUtc,
        Guid? paidInvoiceDocumentId) =>
        renewalInvoiceDocumentId is Guid id && renewalInvoiceForEndUtc == currentEndUtc && paidInvoiceDocumentId != id ? id : null;

    /// <summary>ฝ่ายค้าน N6: ใบต่ออายุ (อ้างอิง <c>RENEW-</c>) ที่ไม่ใช่ใบต่ออายุปัจจุบันของ subscription (ถูกแทนด้วยการอัปเกรด/ต่ออายุทางอื่น/รอบใหม่) —
    /// ไม่ใช่หนี้ที่ผู้เช่าต้องจ่ายแล้ว (คำตัดสินเจ้าของ ② ไม่เก็บย้อนหลัง) ⇒ ไม่รับชำระด้วยตัวเอง (จ่ายแล้วต่ออายุซ้อน/ตัดหนี้เงียบ) · ไม่ทวง ·
    /// ผู้ดูแลระบบยกเลิก/ออกใบลดหนี้ · null = ไม่ใช่ใบต่ออายุ หรือเป็นใบปัจจุบัน</summary>
    public static string? SupersededRenewalReason(string? reference, Guid invoiceId, Guid? currentRenewalInvoiceId) =>
        KindOf(reference) == PlatformInvoiceKind.Renewal && invoiceId != currentRenewalInvoiceId
            ? "ใบแจ้งหนี้ต่ออายุใบนี้ถูกแทนที่ด้วยรอบ/แพ็กเกจใหม่แล้ว — ไม่ต้องชำระ · ถ้าโอนไปแล้วกรุณาติดต่อผู้ดูแลระบบ NextAcc (จะยกเลิกใบหรือออกใบลดหนี้ให้)"
            : null;

    /// <summary>ฝ่ายค้าน S8: การยกเลิกเมื่อสิ้นรอบมีผล ⇒ ใบต่ออายุที่ออกให้ "รอบถัดไปของรอบที่ยกเลิก" (ForEndDate = วันสิ้นรอบปัจจุบัน) ต้องยกเลิก ·
    /// ใบของรอบอื่น/ไม่มีใบ ⇒ null (ไม่แตะ)</summary>
    public static Guid? RenewalVoidOnCancel(Guid? renewalInvoiceDocumentId, DateTime? renewalInvoiceForEndUtc, DateTime currentEndUtc) =>
        renewalInvoiceDocumentId is Guid id && renewalInvoiceForEndUtc == currentEndUtc ? id : null;

    /// <summary>ผลตรวจรายการชำระที่ผูกใบแจ้งหนี้ ก่อนอนุมัติ (ฝ่ายค้าน B2/N6)</summary>
    public enum LinkedReviewAction { Proceed = 0, InvoiceOnly = 1, Hold = 2, Refuse = 3 }

    /// <summary>
    /// ฝ่ายค้าน B2/N6 — <b>ตัวตัดสินเดียว</b>ว่ารายการชำระที่ผูกใบแจ้งหนี้อนุมัติได้แบบไหน (ห้ามต่ออายุซ้อน · ห้ามอนุมัติเงินที่ไม่มีใบให้ตัด):
    /// <list type="bullet">
    /// <item>ใบไม่มียอดค้างแล้ว (ถูกยกเลิก/ชำระครบโดยรายการอื่น): ระบบยืนยันเอง ⇒ <b>Hold</b> (พักให้แอดมิน) · แอดมินกด ⇒ <b>Refuse</b> + ทางไปต่อ
    /// (ปฏิเสธรายการนี้แล้ว "บันทึกรับเงิน" ด้วยมือ — กำหนดแพ็กเกจ/เดือนเองได้ คำตัดสินเจ้าของ ①)</item>
    /// <item>ใบต่ออายุที่ถูกแทนที่หลังแจ้งชำระ (รายการขอต่ออายุ แต่ใบไม่ใช่ใบปัจจุบันแล้ว): ระบบยืนยันเอง ⇒ Hold · แอดมินกด ⇒ <b>InvoiceOnly</b>
    /// (ตัดหนี้ใบนั้นด้วยเงินที่รับ · ไม่ต่ออายุซ้ำ — ส่วนที่ซ้ำแอดมินออกใบลดหนี้/คืนเงิน)</item>
    /// <item>อื่น ⇒ Proceed</item>
    /// </list>
    /// </summary>
    public static (LinkedReviewAction Action, string? Message) ReviewLinked(bool systemConfirmed, bool invoiceOpen, bool supersededRenewal,
        string? invoiceNumber)
    {
        var no = string.IsNullOrWhiteSpace(invoiceNumber) ? "ที่ผูกไว้" : invoiceNumber;
        if (!invoiceOpen)
            return systemConfirmed
                ? (LinkedReviewAction.Hold, $"เงินเข้าแล้วแต่ใบแจ้งหนี้ {no} ไม่มียอดค้าง (ยกเลิก/ชำระครบแล้ว) — ไม่อนุมัติอัตโนมัติ (กันต่ออายุซ้อน) · ผู้ดูแลระบบตรวจ: บันทึกรับเงินด้วยมือ หรือคืนเงิน")
                : (LinkedReviewAction.Refuse, $"ใบแจ้งหนี้ {no} ไม่มียอดค้างแล้ว (ยกเลิก/ชำระครบ) — อนุมัติรายการนี้ไม่ได้ (ไม่มีใบให้ตัด · กันต่ออายุซ้อน) · "
                    + "ให้ปฏิเสธรายการนี้แล้วใช้ \"บันทึกรับเงิน\" ของบริษัทนี้ (กำหนดแพ็กเกจ/จำนวนเดือน/ยอดเองได้) หรือคืนเงินให้ลูกค้า");
        if (supersededRenewal)
            return systemConfirmed
                ? (LinkedReviewAction.Hold, $"เงินเข้าสำหรับใบต่ออายุ {no} ที่ถูกแทนที่ด้วยรอบใหม่แล้ว — ไม่ต่ออายุซ้ำอัตโนมัติ · ผู้ดูแลระบบตรวจ (อนุมัติ = ตัดหนี้ใบนี้อย่างเดียว)")
                : (LinkedReviewAction.InvoiceOnly, $"ใบต่ออายุ {no} ถูกแทนที่ด้วยรอบใหม่แล้ว — ตัดหนี้ใบนี้อย่างเดียว ไม่ต่ออายุซ้ำ (ส่วนที่ซ้ำ: ออกใบลดหนี้/คืนเงิน)");
        return (LinkedReviewAction.Proceed, null);
    }

    /// <summary>ฝ่ายค้าน S3: ตัดชำระได้ไม่เกินยอดค้างของใบ — คืน (เงินสดที่ตัด · ภาษีหัก ณ ที่จ่ายที่ตัด · ส่วนเกินที่ไม่ตัด) · ภาษีหักตัดก่อน (ตามหนังสือรับรอง)
    /// เงินสดตัดส่วนที่เหลือ · ปัดสตางค์แบบ AwayFromZero</summary>
    public static (decimal Cash, decimal Wht, decimal Excess) CapToBalance(decimal cash, decimal wht, decimal balanceDue)
    {
        var bal = Math.Max(0m, balanceDue);
        var w = Math.Min(Math.Max(0m, wht), bal);
        var c = Math.Min(Math.Max(0m, cash), bal - w);
        var excess = Math.Max(0m, cash) - c;
        return (decimal.Round(c, 2, MidpointRounding.AwayFromZero), decimal.Round(w, 2, MidpointRounding.AwayFromZero),
            decimal.Round(excess, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>ฝ่ายค้าน S4: ภาษีหัก ณ ที่จ่ายที่ผู้เช่านิติบุคคลหักจากค่าบริการ — อัตรา ม.40(2) ค่าบริการ นิติบุคคล จาก <c>ThaiWhtRateTable</c> (ตารางเดียว) ·
    /// ไม่ระบุ/0 = ไม่หัก · ผู้จ่ายไม่ใช่นิติบุคคล = หักไม่ได้ · ต้อง &lt; ยอดค้าง · ไม่เกินอัตราคูณยอดค้าง (ยอดรวม VAT — ขอบบนที่หลวมกว่าฐานก่อน VAT)
    /// ⇒ เงินโอน + ภาษีหัก = ยอดค้าง · null = ใช้ได้</summary>
    public static string? WithholdingProblem(decimal? wht, decimal balanceDue, bool payerIsJuristic)
    {
        if (wht is not decimal w || w == 0m) return null;
        if (w < 0m) return "ภาษีหัก ณ ที่จ่ายติดลบไม่ได้";
        if (!payerIsJuristic) return "หักภาษี ณ ที่จ่ายได้เฉพาะผู้จ่ายที่เป็นนิติบุคคล (เลขผู้เสียภาษีของบริษัทต้องเป็นนิติบุคคล) — ถ้าไม่ได้หัก ให้เว้นว่าง";
        if (w >= balanceDue) return "ภาษีหัก ณ ที่จ่ายต้องน้อยกว่ายอดค้างของใบ";
        var rate = ThaiWhtRateTable.Find("40(2)")?.JuristicRate ?? 3m;
        var cap = decimal.Round(balanceDue * rate / 100m, 2, MidpointRounding.AwayFromZero);
        if (w > cap)
            return $"ภาษีหัก ณ ที่จ่าย {w:N2} บาท เกินอัตรา {rate:0.##}% ของค่าบริการ (ไม่เกิน {cap:N2} บาท · ม.40(2) ท.ป.4/2528) — ตรวจหนังสือรับรอง 50 ทวิ อีกครั้ง";
        return null;
    }

    /// <summary>วันที่ตามปฏิทินไทย (UTC+7) — ครบกำหนด/เกินกำหนดนับเป็นวัน ไม่ใช่ชั่วโมง</summary>
    public static DateTime ThaiDate(DateTime utc) => utc.AddHours(7).Date;

    /// <summary>เกินกำหนดกี่วัน (ลบ = อีกกี่วันจะครบ · 0 = ครบวันนี้) · ไม่มีวันครบกำหนด = null</summary>
    public static int? DaysPastDue(DateTime? dueUtc, DateTime nowUtc) =>
        dueUtc is DateTime d ? (int)(ThaiDate(nowUtc) - ThaiDate(d)).TotalDays : null;

    public static PlatformInvoiceState StateOf(DocumentStatus status, decimal balanceDue, DateTime? dueUtc, DateTime nowUtc)
    {
        if (status == DocumentStatus.Voided) return PlatformInvoiceState.Voided;
        if (!IsOpen(status, balanceDue)) return PlatformInvoiceState.Paid;
        var late = DaysPastDue(dueUtc, nowUtc);
        return late switch
        {
            > 0 => PlatformInvoiceState.Overdue,
            0 => PlatformInvoiceState.DueToday,
            _ => PlatformInvoiceState.Open,
        };
    }

    public static string StateLabel(PlatformInvoiceState s, int? daysPastDue) => s switch
    {
        PlatformInvoiceState.Paid => "ชำระแล้ว",
        PlatformInvoiceState.Voided => "ยกเลิกแล้ว",
        PlatformInvoiceState.DueToday => "ครบกำหนดวันนี้",
        PlatformInvoiceState.Overdue => $"เกินกำหนด {daysPastDue ?? 0} วัน",
        _ => daysPastDue is int d && d < 0 ? $"รอชำระ (อีก {-d} วันครบกำหนด)" : "รอชำระ",
    };
}
