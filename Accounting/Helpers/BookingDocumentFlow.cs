using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตาราง "ออกเอกสารอะไร ขั้นไหน" ของการจองหน้าเว็บ (รอบ 207 · คำขอเจ้าของข้อ 5) — <b>อธิบายอย่างเดียว ไม่ตัดสินเอง</b>:
/// ทุกช่องได้จากการถาม <see cref="BookingInvoiceTimingPolicy.Decide"/> ตัวเดียวกับเส้นจริง ⇒ หน้าตั้งค่าบอกตรงกับที่ระบบทำเสมอ
/// (ไม่มีสำเนาตารางใน JS · เปลี่ยนกติกาที่ policy แล้วตารางนี้ตามเอง)
/// </summary>
public static class BookingDocumentFlow
{
    public sealed record Option(string Value, string Label, string Description);

    public sealed record StageRow(string BookingType, string BookingTypeLabel, string AtBooking, string OnPayment, string OnComplete, string OnCancel);

    public static IReadOnlyList<Option> CompletionOptions() => new[]
    {
        new Option(nameof(BookingCompletionDocument.None), "ไม่ออกเอกสารเมื่อกดเสร็จโดยไม่รับชำระ (ค่าเริ่มต้น · เหมาะกับร้านที่เก็บเงินที่ POS)",
            "ใบยืนยันการจองคงเดิม · ถ้ากด \"เสร็จสิ้น + รับชำระ\" ระบบยังออกเอกสารรับเงินให้ (จด VAT ⇒ ใบกำกับภาษี · ไม่จด VAT รับครบ ⇒ ใบเสร็จรับเงิน)"),
        new Option(nameof(BookingCompletionDocument.Auto), "ออกเอกสารทุกครั้งที่ให้บริการเสร็จ",
            "แม้ยังไม่รับชำระ: จด VAT ⇒ ใบกำกับภาษี (ตั้งลูกหนี้) · ไม่จด VAT ⇒ ใบแจ้งหนี้ · รับเงินครบตอนเสร็จ (ไม่จด VAT) ⇒ ใบเสร็จรับเงิน"),
    };

    public static string TypeLabel(BookingType t) => t switch
    {
        BookingType.Lead => "ขอข้อมูล/ติดต่อกลับ (Lead)",
        BookingType.Appointment => "นัดหมาย (จ่ายหน้าร้าน)",
        BookingType.Guaranteed => "การันตี (ยืนยันแล้วเก็บเงิน)",
        BookingType.PrePayment => "ชำระมัดจำล่วงหน้า",
        _ => t.ToString(),
    };

    private static string DocName(DocumentType t) => t == DocumentType.Quotation ? "ใบยืนยันการจอง (ใบเสนอราคา · ไม่ลงบัญชี)" : DocumentTypeNames.Title(t, null);

    /// <summary>ป้ายเอกสารของคำตัดสินหนึ่งครั้ง — ถามจาก policy จริง</summary>
    public static string? Label(BookingInvoiceDecision d) => d.Action switch
    {
        BookingInvoiceAction.CreateAtBooking => d.DocumentType is DocumentType t ? DocName(t) : null,
        BookingInvoiceAction.CreateTaxInvoiceNow or BookingInvoiceAction.IssueTaxInvoiceFromConfirmation
            => d.DocumentType is DocumentType t2 ? DocName(t2) : null,
        BookingInvoiceAction.ApprovePendingTaxInvoice => "อนุมัติใบกำกับ/ใบแจ้งหนี้ที่รออยู่",
        _ => null,
    };

    /// <summary>ตารางต่อชนิดบริการตามค่าตั้งปัจจุบัน + สถานะ VAT ของบริษัท</summary>
    public static IReadOnlyList<StageRow> Describe(BookingInvoiceTiming guaranteedTiming, BookingCompletionDocument completion, bool vatRegistered)
    {
        var rows = new List<StageRow>();
        foreach (var type in new[] { BookingType.Appointment, BookingType.Guaranteed, BookingType.PrePayment, BookingType.Lead })
        {
            var timing = type == BookingType.Guaranteed ? guaranteedTiming : BookingInvoiceTiming.TaxInvoiceAtBooking;
            var booked = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(type, timing, BookingInvoiceEvent.Booked, null, null, false, vatRegistered));
            var atBooking = type switch
            {
                BookingType.Lead => "ไม่ออกเอกสาร (เก็บข้อมูลติดต่อ)",
                BookingType.PrePayment => "ใบเสร็จรับเงินมัดจำ — ร่างรอเงิน · อนุมัติเมื่อรับมัดจำครบ (ลงวันที่รับเงิน)",
                _ => Label(booked) ?? "ไม่ออกเอกสาร",
            };
            var onPayment = type switch
            {
                BookingType.Guaranteed => "ปุ่ม \"รับเงินแล้ว — ออกใบกำกับภาษี\" (ลงวันที่รับเงิน) หรือรับชำระตอนเสร็จ",
                BookingType.PrePayment => "รับมัดจำครบ = ยืนยันการจอง + อนุมัติใบเสร็จมัดจำ",
                _ => "รับชำระตอนกด \"เสร็จสิ้น\" (เลือกวิธี/บัญชีรับเงิน · ระบุยอด)",
            };
            string onComplete;
            if (type == BookingType.PrePayment)
                onComplete = "ราคาเต็มเกินมัดจำ ⇒ " + DocumentTypeNames.Title(BookingInvoiceTimingPolicy.TaxDocumentFor(vatRegistered), null)
                    + "ส่วนที่เหลือ (หักมัดจำ) + รับชำระได้ · เท่ามัดจำ ⇒ รับรู้รายได้จากมัดจำ";
            else if (type == BookingType.Lead)
                onComplete = "ไม่ออกเอกสาร";
            else
            {
                var linked = booked.DocumentType;
                var done = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(type, timing, BookingInvoiceEvent.ServiceCompleted,
                    linked, linked == null ? null : DocumentStatus.Approved, false, vatRegistered, Completion: completion));
                // "เสร็จสิ้น + รับชำระ" = ตั้งใจรับเงิน ⇒ ออกเอกสารรับเงินเสมอ (ตัวเดียวกับ LoadInvoiceFactsAsync collectingPayment)
                var paidNow = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(type, timing, BookingInvoiceEvent.ServiceCompleted,
                    linked, linked == null ? null : DocumentStatus.Approved, false, vatRegistered, Completion: BookingCompletionDocument.Auto, PaidInFullNow: true));
                var a = Label(done); var b = Label(paidNow);
                var noPay = a ?? (done.Action == BookingInvoiceAction.AlreadyIssued ? "ใช้เอกสารที่ออกไว้แล้ว" : "ไม่ออกเอกสาร");
                var withPay = b ?? (paidNow.Action == BookingInvoiceAction.AlreadyIssued ? "ใช้เอกสารที่ออกไว้แล้ว" : "ไม่ออกเอกสาร");
                onComplete = $"เสร็จ + รับชำระครบ ⇒ {withPay} · เสร็จโดยไม่รับชำระ ⇒ {noPay}";
            }
            var onCancel = type switch
            {
                BookingType.Lead => "—",
                BookingType.PrePayment => "ยกเลิกใบร่าง · ใบที่ออกแล้ว ⇒ ใบลดหนี้/คืนมัดจำตามนโยบายยกเลิก",
                _ => "ยกเลิกใบยืนยันการจอง · ใบกำกับที่ออกแล้ว ⇒ ใบลดหนี้",
            };
            rows.Add(new StageRow(type.ToString(), TypeLabel(type), atBooking, onPayment, onComplete, onCancel));
        }
        return rows;
    }
}
