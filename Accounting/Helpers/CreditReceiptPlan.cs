using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัด 1 รายการของใบเสร็จที่ออกจากเงินรอจัดสรร (ข้อเท็จจริงที่ผู้ใช้กรอก — ตัวตัดสินไม่อ่าน DB)</summary>
public sealed record CreditReceiptLineFact(string? Description, decimal Quantity, decimal UnitPrice, decimal VatRate, decimal WithholdingTaxRate);

/// <summary>ข้อเท็จจริงของรายการประจำที่ผู้ใช้เลือกให้ใบเสร็จนี้ "ปิดงวด" (กันออกใบงวดเดียวกันซ้ำ — คำตัดสิน 265-1-4)</summary>
/// <param name="Found">พบรายการประจำของบริษัทนี้ (ไม่ถูกลบ)</param>
/// <param name="ExistingDocumentNumber">มีเอกสารที่ถือกุญแจงวดนี้อยู่แล้ว (เลขที่) · null = งวดนี้ยังไม่ได้ออก</param>
public sealed record CreditReceiptRecurringFacts(bool Found, Guid? ContactId, RecurringStatus? Status, string? TemplateType, DocumentType? DocumentType,
    DateTime? NextRunDate, string? ExistingDocumentNumber);

/// <summary>
/// **ออกใบเสร็จรับเงิน/ใบกำกับภาษีจากเงินรอจัดสรรโดยไม่มีใบแจ้งหนี้ — ตัวตัดสินเดียว** (รอบ 265 · คำตัดสิน 265-1-1…6 ·
/// erp-review/2026-09-29/DECISIONS.md)
/// <para>เคสที่มา (เจ้าของ): ออกใบแจ้งหนี้ไว้ใบเดียว (INV-001 5,000 ของเดือน 1) ลูกค้าโอน 10,000 ปิดเดือน 1 + เดือน 2 — "เราไม่ได้แจ้งหนี้เขาแล้ว ไม่ต้องมาสร้างใบเพิ่ม"
/// ⇒ ส่วนเกิน (เงินรอจัดสรร เหตุผล "ค้างออกใบ") ออกเป็น<b>ใบเสร็จขายเงินสด</b>ได้ตรง ๆ: ขาเงินเข้า = ผังพักที่รับไว้จริง (Dr 21630) / Cr รายได้ + ภาษีขาย
/// (เส้นลงบัญชีใบเสร็จ standalone เดิมของ <c>DocumentService.AutoPostToJournalAsync</c> — ไม่มีสูตร JE ที่สอง) · รูปแบบกระดาษ (เต็มรูป/อย่างย่อ/ใบเสร็จเปล่า)
/// ตัดสินตอนอนุมัติโดย <see cref="ReceiptFormRule"/> เดิม</para>
/// <para><b>วันที่บนใบ</b> = วันรับเงิน เมื่อเงินเป็น "ค้างออกใบ" (ม.105 ใบรับลงวันที่รับเงิน · ม.78/1 · ม.86 — หลักเดียวกับ 260-5-1) · เหตุผลอื่น = วันนี้ (วันที่ตกลงหักกลบ)
/// — ตัวเดียว <see cref="LateReceiptTaxPoint.EffectiveReceiptDate"/> · <b>งวดภาษี</b> = <see cref="LateReceiptTaxPoint.Decide"/> (งวดรับเงิน หรืองวดปัจจุบัน + ธงล่าช้า
/// เมื่องวดรับเงินยื่น/ปิด/เลยกำหนด — 260-5-5 H1) ⇒ <see cref="ReportingTaxPoint"/></para>
/// <para>ผู้เรียก: <c>CustomerCreditService.IssueReceiptAsync</c> (ก่อนเขียนอะไร) · <c>DocumentService.DecideCreditReceiptTaxPointAsync</c> (ก่อนจอง — 265-1-8) —
/// <c>tools/required_call_site_check.py</c> กติกา R265 · หน้าเว็บไม่มีสำเนากติกา</para>
/// </summary>
public static class CreditReceiptPlan
{
    public const string RuleCode = CustomerCreditMovementRules.ToReceiptRuleCode;
    public const string LegalReference = "ป.รัษฎากร ม.105 · ม.78/1 · ม.86 · ม.86/4 · ม.86/13 · ม.87";
    public const int MaxLines = 50;
    /// <summary>ป้ายหมายเหตุภายในของใบที่ออกจากเงินรอจัดสรร (ค้นได้ · ไม่พิมพ์ถึงลูกค้า)</summary>
    public const string Marker = "[CC-RECEIPT]";

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);
    private static string Day(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    private static string Month(DateTime d) => d.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// ตรวจบรรทัดก่อนเขียนอะไร (null = ใช้ได้) — §86/4(5) ทุกบรรทัดต้องมีชื่อ/จำนวน/ราคา · กิจการไม่จด VAT ห้ามเรียกเก็บภาษี (ม.86/13) ·
    /// อัตรา VAT ต่อบรรทัด ∈ {0, อัตราบริษัท} (รองรับบรรทัดปน 7% / 0%) · หัก ณ ที่จ่ายต่อบรรทัด 0–15%
    /// </summary>
    public static string? LinesProblem(IReadOnlyList<CreditReceiptLineFact>? lines, bool vatRegistered, decimal companyVatRate)
    {
        if (lines == null || lines.Count == 0)
            return "ใส่รายการอย่างน้อย 1 บรรทัด (เช่น “ค่าบริการเดือน ต.ค.”) — ใบเสร็จ/ใบกำกับภาษีต้องระบุรายการที่รับเงิน (ม.86/4)";
        if (lines.Count > MaxLines)
            return $"รายการได้ไม่เกิน {MaxLines} บรรทัดต่อใบ";
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var n = i + 1;
            if (string.IsNullOrWhiteSpace(l.Description))
                return $"บรรทัดที่ {n}: ใส่ชื่อรายการ (ม.86/4 — ใบกำกับภาษีต้องระบุชื่อ ชนิด ประเภทของสินค้า/บริการ)";
            if (l.Quantity <= 0m)
                return $"บรรทัดที่ {n}: จำนวนต้องมากกว่า 0";
            if (l.UnitPrice < 0m)
                return $"บรรทัดที่ {n}: ราคาต่อหน่วยติดลบไม่ได้ — ส่วนลดให้ใส่เป็นราคาที่ลดแล้ว";
            if (decimal.Round(l.UnitPrice, 2) != l.UnitPrice || decimal.Round(l.Quantity, 4) != l.Quantity)
                return $"บรรทัดที่ {n}: ราคาทศนิยมได้ไม่เกิน 2 ตำแหน่ง · จำนวนไม่เกิน 4 ตำแหน่ง";
            if (!vatRegistered && l.VatRate != 0m)
                return $"บรรทัดที่ {n}: กิจการยังไม่จดทะเบียนภาษีมูลค่าเพิ่ม — เรียกเก็บ VAT ไม่ได้ (ม.86/13) · ตั้งอัตรา VAT เป็น 0";
            if (vatRegistered && l.VatRate != 0m && l.VatRate != companyVatRate)
                return $"บรรทัดที่ {n}: อัตรา VAT ได้เฉพาะ 0% หรือ {companyVatRate.ToString("0.##", CultureInfo.InvariantCulture)}% (อัตราของบริษัท)";
            if (l.WithholdingTaxRate < 0m || l.WithholdingTaxRate > 15m)
                return $"บรรทัดที่ {n}: อัตราหัก ณ ที่จ่ายต้องอยู่ระหว่าง 0–15%";
        }
        if (lines.All(l => l.UnitPrice == 0m))
            return "ยอดของใบต้องมากกว่า 0";
        return null;
    }

    /// <summary>วันที่บนใบ — วันรับเงิน (ค้างออกใบ) หรือวันนี้ (เหตุผลอื่น) · ตัวเดียวกับใบเสร็จของการจัดสรร</summary>
    public static DateTime PaperDate(string? creditReason, DateTime paymentDate, DateTime today)
        => LateReceiptTaxPoint.EffectiveReceiptDate(creditReason, paymentDate, today);

    /// <summary>ยอดของใบที่ระบบคำนวณ (หลัง VAT/หัก ณ ที่จ่าย = เงินที่รับจริง) ต้องเท่ายอดเงินรอจัดสรรที่ใช้ — ไม่ตรง = ข้อความปฏิเสธ (ไม่ปัดเศษให้เงียบ ๆ)</summary>
    public static string? TotalMismatch(decimal computedTotal, decimal amount)
        => Math.Abs(computedTotal - amount) <= UnappliedCreditBalance.Tolerance
            ? null
            : $"ยอดสุทธิของรายการที่กรอก ({Money(computedTotal)} หลังคิด VAT/หัก ณ ที่จ่าย) ไม่เท่ายอดที่ใช้จากเงินรอจัดสรร ({Money(amount)}) — "
              + "แก้ราคา/จำนวนให้ยอดสุทธิเท่ากัน หรือแก้ยอดที่ใช้ · ยังไม่มีอะไรถูกบันทึก";

    /// <summary>งวดบัญชีของวันที่บนใบปิดแล้ว ⇒ ลงวันที่รับเงินไม่ได้ (JE ของใบลงวันที่ใบ) — ทางไปต่อ</summary>
    public static string ClosedPeriodMessage(string closedReason, DateTime paperDate, bool receiptDrivesDate)
        => receiptDrivesDate
            ? $"ออกใบเสร็จลงวันที่รับเงิน {Day(paperDate)} ไม่ได้ — {closedReason} · ใบรับต้องลงวันที่ที่รับเงิน (ม.105) จึงไม่เลื่อนเป็นวันนี้ให้เอง: "
              + "ให้ผู้ดูแลเปิดงวดชั่วคราว หรือใช้ “ออกใบแจ้งหนี้ส่วนที่ขาด” แล้วจัดสรร (ปรึกษานักบัญชีเรื่องการยื่นเพิ่มเติม)"
            : $"ออกใบเสร็จลงวันที่ {Day(paperDate)} ไม่ได้ — {closedReason}";

    /// <summary>
    /// งวดรายงานที่ต้องประทับบนใบ (<c>TaxPointDate</c>) หลังอนุมัติ — null = ไม่ต้องแตะ (ไม่มี VAT · งวดของผลตัดสินเท่างวดของวันที่บนใบ)
    /// <para>ใบลงวันที่รับเงิน แต่งวดของวันรับเงินยื่น/ปิด/เลยกำหนดแล้ว ⇒ <see cref="LateReceiptTaxPoint.Decide"/> ให้งวดปัจจุบัน — ภ.พ.30 อ่านงวดจาก
    /// <c>TaxPointDate ?? DocumentDate</c> (OutputVatOwnership) ⇒ ไม่ประทับ = ภาษีหายเข้างวดที่ยื่นไปแล้ว (คลาสเดียวกับ 260-5-5 H1)</para>
    /// </summary>
    public static DateTime? ReportingTaxPoint(LateReceiptTaxPointDecision decision, DateTime paperDate, decimal vatAmount)
        => vatAmount <= UnappliedCreditBalance.Tolerance || DepositPolicyResolver.SameVatPeriod(decision.TaxPointDate, paperDate)
            ? null
            : decision.TaxPointDate.Date;

    /// <summary>หมายเหตุภายในเมื่องวดภาษีไม่ใช่งวดของวันที่บนใบ (ธงล่าช้า — ระบบไม่ย้ายงวดที่ยื่นแล้ว)</summary>
    public static string LateVatNote(DateTime paperDate, DateTime taxPointDate)
        => $"{DepositPolicyResolver.LateVatMarker} ใบนี้ลงวันที่รับเงิน {Day(paperDate)} (ม.105) แต่งวด ภ.พ.30 เดือน {Month(paperDate)} ยื่น/ปิด/เลยกำหนดแล้ว — "
           + $"ภาษีขายของใบนี้อยู่ในรายงานงวด {Month(taxPointDate)} · ภาษีถึงกำหนดตั้งแต่งวดรับเงิน (ม.78/1) ⇒ ตรวจกับนักบัญชีว่าต้องยื่นเพิ่มเติม/มีเงินเพิ่มมาตรา 89/1 หรือไม่";

    /// <summary>ใบที่ถือ VAT ออกหลังวันรับเงิน (ม.86 ให้ออก "ทันที") — หมายเหตุภายใน · null = ออกวันเดียวกับวันรับเงิน/ไม่มี VAT</summary>
    public static string? IssuedLateNote(DateTime paperDate, DateTime today, decimal vatAmount)
        => vatAmount <= UnappliedCreditBalance.Tolerance || paperDate.Date >= today.Date
            ? null
            : $"{LateReceiptTaxPoint.IssuedAfterMoneyMarker} ใบเสร็จ/ใบกำกับภาษีนี้ลงวันที่รับเงิน {Day(paperDate)} แต่ออกจริงวันที่ {Day(today)} — "
              + "ม.86 ให้ออกใบกำกับภาษีทันทีที่ความรับผิดเกิด · ครั้งหน้า: ออกใบ ณ วันรับเงิน · ปรึกษานักบัญชีเรื่องเบี้ยปรับ";

    /// <summary>หมายเหตุที่พิมพ์บนใบ (ข้อมูลของใบ ไม่ใช่ป้ายแม่แบบ) — บอกว่าเงินก้อนเดียวกันชำระใบไหนไปแล้ว (เคสใบรวม — คำตัดสิน 265-1-2)</summary>
    public static string ReceiptNotes(string paymentNumber, DateTime paymentDate, IReadOnlyList<string> settledDocumentNumbers)
    {
        var head = $"รับเงินตามการรับชำระ {paymentNumber} วันที่ {Day(paymentDate)}";
        return settledDocumentNumbers.Count == 0
            ? head
            : head + $" — เงินก้อนเดียวกันชำระ {string.Join(", ", settledDocumentNumbers)} แล้ว (ใบเสร็จของใบนั้นออกแยก) · ใบนี้สำหรับรายการที่ยังไม่ได้แจ้งหนี้";
    }

    /// <summary>หมายเหตุภายใน (ไม่พิมพ์)</summary>
    public static string InternalNote(string paymentNumber, decimal amount, string? recurringPeriodLabel)
        => $"{Marker} ออกจากเงินรอจัดสรร {paymentNumber} ยอด {Money(amount)} บาท — ยกเลิกใบนี้ ⇒ เงินกลับเป็นเงินรอจัดสรร"
           + (recurringPeriodLabel == null ? "" : $" · ปิดงวดรายการประจำ {recurringPeriodLabel} (รอบอัตโนมัติไม่ออกใบงวดนี้ซ้ำ)");

    /// <summary>"งวด dd/MM/พ.ศ." ของวันงวด</summary>
    public static string PeriodText(DateTime periodDate)
    {
        var d = ThaiDate.CalendarDateUtc(periodDate);
        return $"งวด {d:dd/MM}/{d.Year + 543}";
    }

    /// <summary>ป้ายงวดของรายการประจำ (พ.ศ. — ข้อความถึงผู้ใช้) · ฝ่ายค้าน M3: บอกชัดว่าเป็น "งวดที่กำลังจะถึง" (ระบบเสนอเฉพาะ NextRunDate)</summary>
    public static string PeriodLabel(string recurringName, DateTime periodDate)
        => $"“{recurringName}” งวดที่กำลังจะถึง ({PeriodText(periodDate)})";

    /// <summary>
    /// ฝ่ายค้าน L3: ด่านอนุมัติของบริษัทที่ใบเสร็จนี้ (ระบบอนุมัติให้ทันที) จะไม่ผ่าน — ตรวจก่อนจอง พร้อมทางที่มีอยู่จริง · null = ผ่าน
    /// (ผลของ <c>ApprovalControlPolicy.NeedsSignatureFlow/SelfApprovalBlocked</c> ตัวเดียวกับตอนอนุมัติ)
    /// </summary>
    public static string? ApprovalControlProblem(bool needsSignatureFlow, bool selfApprovalBlocked)
    {
        if (needsSignatureFlow)
            return "ยอดใบนี้เกินวงเงินอนุมัติตรงของบริษัท — ใบเสร็จที่ระบบออกให้ทันทีผ่านกระบวนการเซ็นหลายขั้นไม่ได้ · "
                   + "ใช้ “ออกใบแจ้งหนี้ส่วนที่ขาด” (ร่าง) แล้วส่งขออนุมัติตามขั้นตอน — อนุมัติแล้วระบบจัดสรรเงินก้อนนี้ให้เอง";
        if (selfApprovalBlocked)
            return "บริษัทเปิดการแบ่งแยกหน้าที่ (ผู้สร้างห้ามอนุมัติเอง) — ใบเสร็จนี้ระบบสร้างและอนุมัติในชื่อผู้กดคนเดียวกัน · "
                   + "ใช้ “ออกใบแจ้งหนี้ส่วนที่ขาด” (ร่าง) แล้วให้ผู้มีสิทธิ์อนุมัติอีกคนเป็นผู้อนุมัติ";
        return null;
    }

    /// <summary>
    /// ฝ่ายค้าน M3 (คำตัดสิน 265-1-11): ยกเลิกใบเสร็จที่ปิดงวดรายการประจำ ซึ่งรอบอัตโนมัติประทับผ่านไปแล้ว (งวดของกุญแจ &lt; NextRunDate) ⇒ ข้อความ
    /// บนแถวรายการประจำ + คำตอบของการยกเลิก — ระบบไม่ถอย NextRunDate เอง · null = งวดยังไม่ถูกประทับ (ปล่อยกุญแจแล้วรอบออกงวดนั้นตามปกติ)
    /// </summary>
    public static string? VoidedRecurringPeriodNotice(string documentNumber, string recurringName, DateTime periodDate, DateTime nextRunDate)
    {
        if (ThaiDate.CalendarDateUtc(periodDate).Date >= ThaiDate.CalendarDateUtc(nextRunDate).Date) return null;
        return $"ยกเลิกใบเสร็จ {documentNumber} ที่ปิด “{recurringName}” {PeriodText(periodDate)} — รอบอัตโนมัติผ่านงวดนั้นไปแล้ว จึงไม่มีใบของงวดนี้ · "
               + "ออกใบของงวดนั้นเอง (หน้าเอกสาร หรือ “ออกใบเสร็จ (ไม่มีใบแจ้งหนี้)” โดยไม่เลือกรายการประจำ) — ระบบไม่ถอยงวดของรายการประจำให้เอง";
    }

    /// <summary>
    /// รายการประจำที่เลือกให้ใบนี้ปิดงวด (คำตัดสิน 265-1-4) — null = ใช้ได้ · ต้องเป็นของลูกค้ารายเดียวกัน · ยังใช้งาน · แม่แบบเอกสารฝั่งขาย ·
    /// งวดที่กำลังจะถึง (<c>NextRunDate</c>) ยังไม่มีเอกสาร (มีแล้ว = ออกซ้ำ — ปฏิเสธพร้อมเลขใบเดิม)
    /// </summary>
    public static string? RecurringProblem(CreditReceiptRecurringFacts r, Guid? creditContactId)
    {
        if (!r.Found) return "ไม่พบรายการประจำที่เลือก (อาจถูกลบแล้ว)";
        if (r.ContactId == null || r.ContactId != creditContactId)
            return "รายการประจำที่เลือกเป็นของลูกค้าคนละรายกับเจ้าของเงิน — เลือกรายการของลูกค้ารายนี้";
        if (!string.Equals(r.TemplateType, "Document", StringComparison.OrdinalIgnoreCase)
            || r.DocumentType is not (DocumentType.Invoice or DocumentType.TaxInvoice or DocumentType.Receipt))
            return "รายการประจำที่เลือกไม่ได้ออกเอกสารขาย (ใบแจ้งหนี้/ใบกำกับ/ใบเสร็จ) — ใช้ปิดงวดกับใบเสร็จนี้ไม่ได้";
        if (r.Status != RecurringStatus.Active)
            return "รายการประจำที่เลือกไม่ได้เปิดใช้งาน — ไม่มีงวดที่จะออกซ้ำ (ไม่ต้องเลือก)";
        if (r.ExistingDocumentNumber != null)
        {
            var d = ThaiDate.CalendarDateUtc(r.NextRunDate ?? DateTime.UtcNow);
            return $"งวด {d:dd/MM}/{d.Year + 543} ของรายการประจำนี้มีเอกสารแล้ว ({r.ExistingDocumentNumber}) — ออกใบเสร็จซ้ำงวดเดียวกันไม่ได้ · "
                   + "ถ้าเงินนี้เป็นของงวดนั้น ให้จัดสรรให้ใบนั้นแทน";
        }
        return null;
    }

    /// <summary>ข้อความบนแถวรายการประจำเมื่อรอบอัตโนมัติพบว่างวดนี้ถูกออกเป็นใบเสร็จจากเงินรอจัดสรรแล้ว (ไม่ใช่ "ระบบหยุดระหว่างทาง")</summary>
    public static string RecurringBilledNote(string? documentNumber, DateTime periodDate)
    {
        var d = ThaiDate.CalendarDateUtc(periodDate);
        return $"งวด {d:dd/MM}/{d.Year + 543} ออกเป็นใบเสร็จจากเงินรอจัดสรรแล้ว ({documentNumber ?? "-"}) — ไม่ออกใบซ้ำ";
    }
}
