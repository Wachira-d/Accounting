using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุการณ์ของการจองนัดหมาย (CMS booking) ที่อาจต้องออก/ไม่ออกเอกสาร ERP</summary>
public enum BookingInvoiceEvent
{
    /// <summary>สร้างการจอง (หน้าเว็บ/พนักงาน) · เลื่อนขึ้นจากคิวสำรอง · กด "ส่งเข้า ERP"</summary>
    Booked,
    /// <summary>พนักงานยืนยันว่า "รับเงินแล้ว" (เส้นรับเงินของการจองนัดหมายเส้นเดียวตอนนี้ — ยังไม่มีชำระออนไลน์ BOOKING_REVENUE_AUDIT B-4)</summary>
    PaymentReceived,
    /// <summary>ให้บริการเสร็จ (สถานะ Completed)</summary>
    ServiceCompleted,
    /// <summary>ลูกค้าไม่มา (สถานะ NoShow)</summary>
    NoShow,
}

public enum BookingInvoiceAction
{
    /// <summary>ไม่ต้องทำอะไรกับเอกสาร (อาจมีข้อความใน <see cref="BookingInvoiceDecision.Notice"/>)</summary>
    None,
    /// <summary>สร้างเอกสารของการจองตอนจอง — ชนิดใน <see cref="BookingInvoiceDecision.DocumentType"/> (เส้นเดิม SyncBookingToErpAsync)</summary>
    CreateAtBooking,
    /// <summary>แปลงใบยืนยันการจอง (ใบเสนอราคา) ที่ผูกอยู่ → ใบกำกับภาษี (บริษัทไม่จด VAT = ใบแจ้งหนี้) แล้วอนุมัติ — เส้นแปลงเอกสารเดิม</summary>
    IssueTaxInvoiceFromConfirmation,
    /// <summary>ยังไม่มีเอกสารผูกเลย (ขั้น ERP ตอนจองล้ม) แต่ถึง tax point แล้ว ⇒ สร้างใบกำกับ (ไม่จด VAT = ใบแจ้งหนี้) ด้วยเส้นสร้างเอกสารของการจองเดิม + อนุมัติ</summary>
    CreateTaxInvoiceNow,
    /// <summary>มีใบกำกับ/ใบแจ้งหนี้ที่ผูกอยู่แต่ยังเป็นร่าง (ขั้นอนุมัติครั้งก่อนล้ม) ⇒ อนุมัติใบนั้น (ไม่สร้างใบใหม่)</summary>
    ApprovePendingTaxInvoice,
    /// <summary>ออกใบกำกับ/ใบรับเงินไปแล้ว — ไม่ออกซ้ำ (ผูกใบนั้นกับการจองถ้ายังไม่ผูก)</summary>
    AlreadyIssued,
}

/// <param name="Type">ชนิดการจองของบริการ</param>
/// <param name="Timing">ค่าตั้งของไซต์ (มีผลกับ <see cref="BookingInvoiceEvent.Booked"/> เท่านั้น — เหตุการณ์อื่นตัดสินจาก "ข้อเท็จจริงของเอกสาร" ⇒ เปลี่ยนค่าตั้งกลางทาง tax point ไม่หลุด)</param>
/// <param name="LinkedDocumentType">ชนิดเอกสารที่ผูกกับการจองอยู่ (<c>SiteBooking.ErpDocumentId</c>) · null = ยังไม่มี</param>
/// <param name="LinkedStatus">สถานะของเอกสารที่ผูก</param>
/// <param name="HasIssuedTaxDocument">มีใบกำกับ/ใบแจ้งหนี้/ใบเสร็จที่แปลงจากใบที่ผูก (พนักงานแปลงเองที่หน้าเอกสาร) และยังไม่ถูกยกเลิก</param>
/// <param name="VatRegistered">บริษัทจด VAT (<c>CompanyVatStatus</c>) — ไม่จด ⇒ ใบแจ้งหนี้แทนใบกำกับ (§90/2)</param>
public readonly record struct BookingInvoiceFacts(
    BookingType Type,
    BookingInvoiceTiming Timing,
    BookingInvoiceEvent Event,
    DocumentType? LinkedDocumentType,
    DocumentStatus? LinkedStatus,
    bool HasIssuedTaxDocument,
    bool VatRegistered);

public sealed record BookingInvoiceDecision(
    BookingInvoiceAction Action,
    DocumentType? DocumentType = null,
    ErpBookingDocumentType? ErpType = null,
    string? Notice = null);

/// <summary>ตัวเลือกบนหน้าตั้งค่า (server ประกอบ · หน้าเว็บแสดงตรง ๆ)</summary>
public sealed record BookingInvoiceTimingOption(string Value, string Label, string TaxEffect);

/// <summary>
/// <b>จังหวะออกเอกสาร ERP ของการจองนัดหมาย (CMS booking) — ตัวตัดสินเดียว</b> (คำตัดสินเจ้าของ 2026-10-22 ข้อ 155 · BOOKING_REVENUE_AUDIT B-2) · pure
///
/// <para>ที่มา: บริการแบบ <c>Guaranteed</c> อนุมัติ "ใบกำกับภาษี" ทันทีตอนแขกจองบนเว็บ (ก่อนรับเงิน/ก่อนใช้บริการ) — tax point = วันออกใบ (§78/1 ถูกตามตัวบท)
/// แต่ภาษีขายเข้า ภ.พ.30 ก่อนได้เงิน + รายได้ก่อนให้บริการ (TFRS NPAEs บทที่ 6) + ใบเลขจริงจากการจองที่ใครก็สร้างได้ ·
/// เจ้าของสั่ง "ต้องเปิดให้ตั้งค่าได้ว่าจะเป็นแบบไหนก็ได้" ⇒ ค่าตั้งระดับไซต์ <see cref="BookingInvoiceTiming"/>:</para>
/// <list type="bullet">
/// <item>(ก) <see cref="BookingInvoiceTiming.TaxInvoiceAtBooking"/> = เดิมทุกตัวอักษร (ค่าเริ่มต้น)</item>
/// <item>(ข) <see cref="BookingInvoiceTiming.TaxInvoiceOnPaymentOrService"/> = ตอนจองออก "ใบยืนยันการจอง" (ใบเสนอราคา · ไม่ลงบัญชี ไม่มีภาษีขาย) ·
/// ออกใบกำกับเมื่อ <b>รับเงิน</b> หรือ <b>ให้บริการเสร็จ</b> แล้วแต่อย่างไหนเกิดก่อน = MIN(รับเงิน, ออกใบกำกับ, ใช้บริการ) ของ §78/1</item>
/// </list>
/// <para>กติกากัน tax point หลุด: รับเงิน/ให้บริการเสร็จ ตัดสินจาก "เอกสารที่ผูกจริง" ไม่ใช่ค่าตั้งปัจจุบัน ⇒ การจองที่ทำตอนโหมด (ข) แล้วเจ้าของสลับกลับ (ก)
/// ยังได้ใบกำกับเมื่อรับเงิน/ให้บริการ · ใบที่ออกแล้ว (ระบบหรือพนักงานแปลงเอง) ⇒ <see cref="BookingInvoiceAction.AlreadyIssued"/> ไม่ออกซ้ำ ·
/// ยกเลิก = <c>CmsBookingCancelPolicy.DecideOnCancel</c> เดิม (ใบยืนยันการจอง/ใบกำกับที่ผูก ⇒ void)</para>
/// </summary>
public static class BookingInvoiceTimingPolicy
{
    /// <summary>ชนิดเอกสารที่นับว่า "ออกเอกสารภาษี/รับเงินแล้ว" ของการจอง — ใบที่แปลงจากใบยืนยันการจองเป็นชนิดเหล่านี้ ⇒ ไม่ออกซ้ำ</summary>
    public static readonly IReadOnlyList<DocumentType> IssuedTaxDocumentTypes = new[]
    {
        DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt,
    };

    private static bool IsIssuedTaxDocument(DocumentType? type) => type is DocumentType t && IssuedTaxDocumentTypes.Contains(t);

    /// <summary>ชนิดเอกสารตอนจอง (เดิมเป็น switch ใน <c>SyncBookingToErpAsync</c> — ย้ายมาที่นี่ทั้งชุด · ชนิดอื่นนอกจาก Guaranteed ไม่เปลี่ยน)</summary>
    private static (DocumentType DocType, ErpBookingDocumentType ErpType) DocumentAtBooking(BookingType type, BookingInvoiceTiming timing) => type switch
    {
        BookingType.Lead => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder),
        BookingType.Appointment => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder),
        BookingType.Guaranteed => timing == BookingInvoiceTiming.TaxInvoiceOnPaymentOrService
            ? (DocumentType.Quotation, ErpBookingDocumentType.Quotation)
            : (DocumentType.TaxInvoice, ErpBookingDocumentType.TaxInvoice),
        BookingType.PrePayment => (DocumentType.Receipt, ErpBookingDocumentType.AdvanceReceipt),
        _ => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder),
    };

    /// <summary>ชนิดใบกำกับเมื่อถึง tax point — ไม่จด VAT ⇒ ใบแจ้งหนี้ (ออกใบกำกับไม่ได้ §90/2 · ด่านเดียวกับเส้นแปลง)</summary>
    private static DocumentType TaxDocumentFor(bool vatRegistered) => vatRegistered ? DocumentType.TaxInvoice : DocumentType.Invoice;

    public static BookingInvoiceDecision Decide(BookingInvoiceFacts f)
    {
        if (f.Event == BookingInvoiceEvent.Booked)
        {
            // มีเอกสารผูกอยู่แล้ว ⇒ เส้นเดิมคืนใบเดิม (ไม่สร้างใหม่)
            if (f.LinkedDocumentType != null) return new(BookingInvoiceAction.None);
            var (doc, erp) = DocumentAtBooking(f.Type, f.Timing);
            return new(BookingInvoiceAction.CreateAtBooking, doc, erp);
        }

        // จังหวะออกใบภายหลังมีเฉพาะ Guaranteed — ชนิดอื่นคงเส้นเดิม (มัดจำ = CmsBookingCancelPolicy.DecideOnComplete · ใบเสนอราคานัดหมาย = พนักงานแปลงเอง)
        if (f.Type != BookingType.Guaranteed) return new(BookingInvoiceAction.None);

        if (f.LinkedDocumentType is not DocumentType linked)
        {
            // ไม่มีเอกสารผูก (ขั้น ERP ตอนจองล้ม — เช่น โควตาใบเสนอราคาเต็ม B-3) แต่ถึง tax point แล้ว ⇒ ออกใบกำกับตรงด้วยเส้นสร้างเอกสารเดิม ·
            // ให้บริการเสร็จในโหมด (ก) คงพฤติกรรมเดิม (หมายเหตุ "ออกเอกสารเองที่หน้าเอกสาร" จากตอนจองยังอยู่บนการจอง)
            if (f.Event == BookingInvoiceEvent.PaymentReceived
                || (f.Event == BookingInvoiceEvent.ServiceCompleted && f.Timing == BookingInvoiceTiming.TaxInvoiceOnPaymentOrService))
            {
                var direct = TaxDocumentFor(f.VatRegistered);
                return new(BookingInvoiceAction.CreateTaxInvoiceNow, direct, CmsBookingErpPolicy.ErpTypeOf(direct));
            }
            return new(BookingInvoiceAction.None);
        }

        if (f.HasIssuedTaxDocument) return new(BookingInvoiceAction.AlreadyIssued);

        // ใบที่ผูกถูกยกเลิก/ปฏิเสธ (ใบยืนยันหรือใบกำกับ) ⇒ ไม่มีใบให้แปลง/อ้าง — ไม่ออกเอง (ไม่รู้ว่าทำไมถูกยกเลิก) แต่บอกทางไปต่อ
        if (f.LinkedStatus is DocumentStatus.Voided or DocumentStatus.Rejected)
            return new(BookingInvoiceAction.None, Notice: f.Event == BookingInvoiceEvent.NoShow ? null
                : "เอกสารที่ผูกกับการจองถูกยกเลิกแล้ว — ระบบออกใบกำกับภาษีให้อัตโนมัติไม่ได้ · ออกเองที่หน้าเอกสาร (อ้างเลขการจอง) ภายในวันที่รับเงิน/ให้บริการ (§78/1)");

        if (IsIssuedTaxDocument(linked))
        {
            if (f.LinkedStatus is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval))
                return new(BookingInvoiceAction.AlreadyIssued);
            // ใบที่ผูกยังเป็นร่าง (อนุมัติอัตโนมัติครั้งก่อนล้ม) — กด "รับเงินแล้ว" = อนุมัติใบนั้น · ให้บริการเสร็จ: โหมด (ข) อนุมัติให้ ·
            // โหมด (ก) คงพฤติกรรมเดิม (ไม่แตะ — หมายเหตุ "เปิดเอกสารแล้วกดอนุมัติ" จากตอนจองยังอยู่บนการจอง)
            return f.Event == BookingInvoiceEvent.PaymentReceived
                   || (f.Event == BookingInvoiceEvent.ServiceCompleted && f.Timing == BookingInvoiceTiming.TaxInvoiceOnPaymentOrService)
                ? new(BookingInvoiceAction.ApprovePendingTaxInvoice)
                : new(BookingInvoiceAction.None);
        }

        if (linked != DocumentType.Quotation) return new(BookingInvoiceAction.None);

        if (f.Event == BookingInvoiceEvent.NoShow)
            // ไม่ได้รับเงิน · ไม่ได้ใช้บริการ · ยังไม่ออกใบกำกับ ⇒ ยังไม่มี tax point — ไม่ออกเอกสารภาษีเอง (ค่าตั้งใดก็ตาม)
            return new(BookingInvoiceAction.None, Notice:
                "ลูกค้าไม่มาใช้บริการ — ยังไม่ได้ออกใบกำกับภาษี (ยังไม่รับเงิน/ไม่ได้ให้บริการ = ยังไม่มีจุดความรับผิด §78/1) · "
                + "ถ้าเรียกเก็บค่าการันตี กด “รับเงินแล้ว — ออกใบกำกับภาษี” ในวันที่ได้รับเงิน");

        var target = TaxDocumentFor(f.VatRegistered);
        return new(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, target, CmsBookingErpPolicy.ErpTypeOf(target));
    }

    /// <summary>ข้อความหลังออกใบสำเร็จ (ตอบผู้กด + ประทับบนการจอง)</summary>
    public static string IssuedNotice(BookingInvoiceEvent ev, string documentNumber, DocumentType type)
        => $"ออก{(type == DocumentType.TaxInvoice ? "ใบกำกับภาษี" : "ใบแจ้งหนี้")} {documentNumber} แล้ว "
           + (ev == BookingInvoiceEvent.PaymentReceived ? "(วันที่รับเงิน)" : "(วันที่ให้บริการเสร็จ)")
           + " — บันทึกรับชำระที่หน้าเอกสารนั้น";

    /// <summary>หมายเหตุบนการจอง/ใบยืนยันการจอง เมื่อโหมด (ข) ออกใบเสนอราคาแทนใบกำกับตอนจอง</summary>
    public const string DeferredNote = "ออกใบยืนยันการจอง (ใบเสนอราคา · ยังไม่ลงบัญชี/ภาษีขาย) — ใบกำกับภาษีออกอัตโนมัติเมื่อให้บริการเสร็จ · "
        + "ถ้ารับเงินก่อนวันบริการ กด “รับเงินแล้ว — ออกใบกำกับภาษี” ในวันที่รับเงิน (§78/1)";

    public static string AlreadyIssuedNotice(string documentNumber)
        => $"การจองนี้มีเอกสาร {documentNumber} แล้ว — ไม่ออกใบกำกับซ้ำ";

    /// <summary>ตัวเลือกบนหน้าตั้งค่า + ผลทางภาษีสั้น ๆ (ภาษาไทย)</summary>
    public static IReadOnlyList<BookingInvoiceTimingOption> Options() => new[]
    {
        new BookingInvoiceTimingOption(nameof(BookingInvoiceTiming.TaxInvoiceAtBooking),
            "ออกใบกำกับภาษีทันทีตอนจอง (ค่าเริ่มต้น)",
            "ภาษีขายเข้า ภ.พ.30 เดือนที่จอง แม้ยังไม่ได้รับเงิน · ลูกค้ายกเลิก = ต้องยกเลิกใบกำกับเลขจริง"),
        new BookingInvoiceTimingOption(nameof(BookingInvoiceTiming.TaxInvoiceOnPaymentOrService),
            "ตอนจองออกใบยืนยันการจอง · ออกใบกำกับเมื่อรับเงินหรือให้บริการเสร็จ (แล้วแต่อย่างไหนก่อน)",
            "ตอนจองไม่ลงบัญชี/ไม่มีภาษีขาย · ภาษีขายเข้าเดือนที่รับเงินหรือให้บริการ (§78/1) · รับเงินก่อนวันบริการต้องกด “รับเงินแล้ว — ออกใบกำกับภาษี” ทันที · "
            + "นับโควตาเอกสาร 2 ใบต่อการจอง (ใบยืนยัน + ใบกำกับ)"),
    };
}
