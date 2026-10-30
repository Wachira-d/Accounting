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
