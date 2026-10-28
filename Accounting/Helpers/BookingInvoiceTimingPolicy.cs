using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุการณ์ของการจองนัดหมาย (CMS booking) ที่อาจต้องออก/ไม่ออกเอกสาร ERP</summary>
public enum BookingInvoiceEvent
{
    /// <summary>สร้างการจอง (หน้าเว็บ/พนักงาน) · เลื่อนขึ้นจากคิวสำรอง · กด "ส่งเข้า ERP"</summary>
    Booked,
    /// <summary>พนักงานยืนยันว่า "รับเงินแล้ว" พร้อมวันที่รับเงิน (เส้นรับเงินของการจองนัดหมายเส้นเดียวตอนนี้ — ยังไม่มีชำระออนไลน์ BOOKING_REVENUE_AUDIT B-4)</summary>
    PaymentReceived,
    /// <summary>ให้บริการเสร็จ (สถานะ Completed)</summary>
    ServiceCompleted,
    /// <summary>ลูกค้าไม่มา (สถานะ NoShow)</summary>
    NoShow,
}

/// <summary>บิล POS ที่ผูกกับการจอง (ทีม K · <c>PosOrder.SourceBookingId</c>) — POS เป็นเส้นรายได้อีกเส้นของการจองเดียวกัน</summary>
public enum BookingPosBillState
{
    None,
    /// <summary>มีบิลเปิดอยู่ (ยังไม่เก็บเงิน) — เงิน/รายได้จะเกิดที่ POS</summary>
    Open,
    /// <summary>บิลปิดแล้ว (Completed/Refunded) — รายได้ + ภาษีขายรับรู้ที่บิล POS แล้ว</summary>
    Closed,
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
    /// <summary>ใบกำกับ/ใบแจ้งหนี้ที่ผูกอยู่ (หรือที่พนักงานแปลงจากใบยืนยันเอง) ยังเป็นร่าง ⇒ อนุมัติใบนั้น (ไม่สร้างใบใหม่) ·
    /// <see cref="BookingInvoiceDecision.UseIssuedChild"/> = ใบลูกที่แปลงเอง (ผูกใบนั้นกับการจองก่อนอนุมัติ)</summary>
    ApprovePendingTaxInvoice,
    /// <summary>ออกใบกำกับ/ใบรับเงินไปแล้ว — ไม่ออกซ้ำ (<see cref="BookingInvoiceDecision.UseIssuedChild"/> ⇒ ผูกใบลูกนั้นกับการจอง)</summary>
    AlreadyIssued,
    /// <summary>บิล POS ของการจองปิดแล้ว = รายได้/ภาษีขายอยู่ที่ POS ⇒ ไม่ออกใบกำกับ · <see cref="BookingInvoiceDecision.VoidConfirmation"/> ⇒ ยกเลิกใบยืนยันการจอง</summary>
    SettledByPosBill,
    /// <summary>กด "รับเงินแล้ว" ไม่ได้ในสภาพนี้ (สถานะการจอง/บิล POS เปิดอยู่/ไม่มีอะไรต้องออก) — ผู้เรียกปฏิเสธพร้อม <see cref="BookingInvoiceDecision.Notice"/></summary>
    Rejected,
}

/// <param name="Type">ชนิดการจองของบริการ</param>
/// <param name="Timing">ค่าตั้งของไซต์ (มีผลกับ <see cref="BookingInvoiceEvent.Booked"/> + ปุ่มรับเงิน — การออกใบหลังจองตัดสินจาก "ข้อเท็จจริงของเอกสาร" ⇒ เปลี่ยนค่าตั้งกลางทาง tax point ไม่หลุด)</param>
/// <param name="LinkedDocumentType">ชนิดเอกสารที่ผูกกับการจองอยู่ (<c>SiteBooking.ErpDocumentId</c>) · null = ยังไม่มี</param>
/// <param name="LinkedStatus">สถานะของเอกสารที่ผูก</param>
/// <param name="HasIssuedTaxDocument">มีใบกำกับ/ใบแจ้งหนี้/ใบเสร็จที่แปลงจาก<b>ใบยืนยันการจอง</b>ที่ผูก (พนักงานแปลงเอง) และยังไม่ถูกยกเลิก — ผู้ประกอบข้อเท็จจริงหาเฉพาะเมื่อใบที่ผูกเป็นใบเสนอราคา</param>
/// <param name="VatRegistered">บริษัทจด VAT (<c>CompanyVatStatus</c>) — ไม่จด ⇒ ใบแจ้งหนี้แทนใบกำกับ (§90/2)</param>
/// <param name="IssuedChildStatus">สถานะของใบลูกนั้น (ร่าง = ยังไม่ออก)</param>
/// <param name="PosBill">บิล POS ที่ผูกกับการจอง</param>
/// <param name="BookingStatus">สถานะการจองปัจจุบัน</param>
public readonly record struct BookingInvoiceFacts(
    BookingType Type,
    BookingInvoiceTiming Timing,
    BookingInvoiceEvent Event,
    DocumentType? LinkedDocumentType,
    DocumentStatus? LinkedStatus,
    bool HasIssuedTaxDocument,
    bool VatRegistered,
    DocumentStatus? IssuedChildStatus = null,
    BookingPosBillState PosBill = BookingPosBillState.None,
    BookingStatus BookingStatus = BookingStatus.Confirmed,
    BookingCompletionDocument Completion = BookingCompletionDocument.None,
    bool PaidInFullNow = false);

public sealed record BookingInvoiceDecision(
    BookingInvoiceAction Action,
    DocumentType? DocumentType = null,
    ErpBookingDocumentType? ErpType = null,
    string? Notice = null,
    bool UseIssuedChild = false,
    bool VoidConfirmation = false);

/// <summary>เอกสารที่เกี่ยวกับการจอง (ใบที่ผูก หรือใบลูกที่แปลงจากใบนั้น) — ข้อเท็จจริงของ <see cref="BookingInvoiceTimingPolicy.RevenueDocumentForPos"/></summary>
public readonly record struct BookingDocRef(DocumentType Type, DocumentStatus Status, string? Number, bool IsDeposit = false);

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
/// <para>กติกา: (1) หลังจองตัดสินจาก "เอกสารที่ผูกจริง" ไม่ใช่ค่าตั้งปัจจุบัน (สลับค่าตั้งกลางทาง tax point ไม่หลุด) ·
/// (2) ใบที่ออกแล้ว (ระบบหรือพนักงานแปลงเอง) ⇒ ไม่ออกซ้ำ · (3) <b>บิล POS ของการจองปิดแล้ว ⇒ ไม่ออกใบกำกับ</b> (รายได้/ภาษีขายอยู่ที่ POS ·
/// ฝ่ายค้านทีม N ข้อ 1) — ยกเลิกใบยืนยันการจองแทน · บิล POS เปิดอยู่ ⇒ ไม่ออก (เก็บเงินที่ POS) ·
/// (4) โหมด (ก) ใบกำกับที่ผูกแล้วไม่ถูกแตะ (ฝ่ายค้าน ข้อ 2) · ยกเลิก = <c>CmsBookingCancelPolicy.DecideOnCancel</c> เดิม</para>
/// </summary>
public static class BookingInvoiceTimingPolicy
{
    /// <summary>ชนิดเอกสารที่นับว่า "ออกเอกสารภาษี/รับเงินแล้ว" ของการจอง — ใบที่แปลงจากใบยืนยันการจองเป็นชนิดเหล่านี้ ⇒ ไม่ออกซ้ำ</summary>
    public static readonly IReadOnlyList<DocumentType> IssuedTaxDocumentTypes = new[]
    {
        DocumentType.TaxInvoice, DocumentType.Invoice, DocumentType.Receipt,
    };

    private static bool IsIssuedTaxDocument(DocumentType? type) => type is DocumentType t && IssuedTaxDocumentTypes.Contains(t);
    private static bool IsPending(DocumentStatus? s) => s is DocumentStatus.Draft or DocumentStatus.WaitingApproval;
    private static bool IsDead(DocumentStatus? s) => s is DocumentStatus.Voided or DocumentStatus.Rejected;

    /// <summary>ต้องหาใบลูกที่แปลงจากใบที่ผูกไหม — เฉพาะเมื่อใบที่ผูกเป็นใบยืนยันการจอง (ใบเสนอราคา) · ใบกำกับที่ผูก (โหมด ก) มีใบเสร็จลูก
    /// ได้ตามปกติ — ห้ามนับเป็น "ออกแล้ว" แล้วย้ายการผูกไปที่ใบเสร็จ (ฝ่ายค้านทีม N ข้อ 2)</summary>
    public static bool ShouldLookForIssuedChild(DocumentType? linkedType) => linkedType == DocumentType.Quotation;

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

    /// <summary>รอบ 207: ไม่จด VAT + รับเงินครบตอนนั้น ⇒ ใบเสร็จรับเงิน (ขายสด) · ไม่จด VAT ยังไม่รับครบ ⇒ ใบแจ้งหนี้ · จด VAT ⇒ ใบกำกับภาษีเสมอ (§86/4 —
    /// รับเงินแล้วหัวเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" ตามโหมดใบเสร็จของบริษัท)</summary>
    public static DocumentType TaxDocumentFor(bool vatRegistered, bool paidInFullNow = false)
        => vatRegistered ? DocumentType.TaxInvoice : paidInFullNow ? DocumentType.Receipt : DocumentType.Invoice;

    private static BookingInvoiceDecision Issue(BookingInvoiceAction action, bool vatRegistered, bool paidInFullNow = false)
    {
        var t = TaxDocumentFor(vatRegistered, paidInFullNow);
        return new(action, t, CmsBookingErpPolicy.ErpTypeOf(t));
    }

    /// <summary>สถานะการจองที่กด "รับเงินแล้ว — ออกใบกำกับภาษี" ไม่ได้ (server ปฏิเสธ ไม่ใช่แค่ซ่อนปุ่ม · ฝ่ายค้าน ข้อ 3)</summary>
    private static string? PaymentBlockedByStatus(BookingStatus s) => s switch
    {
        BookingStatus.Cancelled => "การจองนี้ถูกยกเลิกแล้ว — ออกใบกำกับจากการจองไม่ได้ · ถ้ารับเงินค่ายกเลิก ออกเอกสารเองที่หน้าเอกสาร (อ้างเลขการจอง)",
        BookingStatus.Waitlisted => "การจองนี้อยู่ในคิวสำรอง — ยืนยันการจองก่อน แล้วค่อยบันทึกรับเงิน",
        BookingStatus.NoShow => "การจองนี้บันทึกว่าลูกค้าไม่มาแล้ว — ถ้าเรียกเก็บค่าการันตี ออกใบกำกับเองที่หน้าเอกสาร (แปลงใบยืนยันการจอง) ลงวันที่รับเงิน",
        _ => null,
    };

    public static BookingInvoiceDecision Decide(BookingInvoiceFacts f)
    {
        if (f.Event == BookingInvoiceEvent.Booked)
        {
            // มีเอกสารผูกอยู่แล้ว ⇒ เส้นเดิมคืนใบเดิม (ไม่สร้างใหม่)
            if (f.LinkedDocumentType != null) return new(BookingInvoiceAction.None);
            var (doc, erp) = DocumentAtBooking(f.Type, f.Timing);
            return new(BookingInvoiceAction.CreateAtBooking, doc, erp);
        }

        // รอบ 207 (คำขอเจ้าของข้อ 5): บริการแบบนัดหมาย + ค่าตั้งไซต์ "ออกเอกสารตอนเสร็จอัตโนมัติ" ⇒ ตอนให้บริการเสร็จเดินเส้นเดียวกับ
        // Guaranteed โหมด "ตอนรับเงิน/ให้บริการ" (แปลงใบยืนยันการจอง → ใบกำกับ/ใบเสร็จ · ไม่มีใบ ⇒ สร้างใหม่) · เหตุการณ์อื่นของนัดหมายคงเดิม
        if (f.Type == BookingType.Appointment && f.Event == BookingInvoiceEvent.ServiceCompleted && f.Completion == BookingCompletionDocument.Auto)
            f = f with { Type = BookingType.Guaranteed, Timing = BookingInvoiceTiming.TaxInvoiceOnPaymentOrService };
        // จังหวะออกใบภายหลังมีเฉพาะ Guaranteed — ชนิดอื่นคงเส้นเดิม (มัดจำ = CmsBookingCancelPolicy.DecideOnComplete · ใบเสนอราคานัดหมาย = พนักงานแปลงเอง)
        if (f.Type != BookingType.Guaranteed)
            return f.Event == BookingInvoiceEvent.PaymentReceived
                ? new(BookingInvoiceAction.Rejected, Notice: "ปุ่มนี้ใช้กับบริการแบบการันตี (Guaranteed) เท่านั้น — ออกเอกสารเองที่หน้าเอกสาร")
                : new(BookingInvoiceAction.None);

        var isPayment = f.Event == BookingInvoiceEvent.PaymentReceived;
        if (isPayment && PaymentBlockedByStatus(f.BookingStatus) is string blocked)
            return new(BookingInvoiceAction.Rejected, Notice: blocked);

        var confirmationAlive = f.LinkedDocumentType == DocumentType.Quotation && !IsDead(f.LinkedStatus);

        // ── บิล POS (ฝ่ายค้านทีม N ข้อ 1) — รายได้ของการจองเดียวกันห้ามเกิดสองเส้น ──
        if (f.PosBill == BookingPosBillState.Closed && !IsIssuedTaxDocument(f.LinkedDocumentType) && !f.HasIssuedTaxDocument)
        {
            const string posNote = "การจองนี้ปิดบิล POS แล้ว — รายได้และภาษีขายอยู่ที่บิล POS · ไม่ออกใบกำกับจากการจองซ้ำ";
            return isPayment
                ? new(BookingInvoiceAction.Rejected, Notice: posNote + " (รับเงินที่ POS ไปแล้ว)")
                : new(BookingInvoiceAction.SettledByPosBill, Notice: confirmationAlive ? posNote + " · ยกเลิกใบยืนยันการจองแล้ว" : posNote,
                    VoidConfirmation: confirmationAlive);
        }
        if (f.PosBill == BookingPosBillState.Open && !IsIssuedTaxDocument(f.LinkedDocumentType) && !f.HasIssuedTaxDocument)
        {
            const string openNote = "การจองนี้มีบิล POS เปิดอยู่ — เก็บเงินและออกเอกสารที่ POS (ปิดบิล) · ไม่ออกใบกำกับจากการจองซ้อน";
            return isPayment ? new(BookingInvoiceAction.Rejected, Notice: openNote) : new(BookingInvoiceAction.None, Notice: openNote);
        }

        if (f.LinkedDocumentType is not DocumentType linked)
        {
            // ไม่มีเอกสารผูก (ขั้น ERP ตอนจองล้ม — เช่น โควตาใบเสนอราคาเต็ม B-3) แต่ถึง tax point แล้ว ⇒ ออกใบกำกับตรงด้วยเส้นสร้างเอกสารเดิม ·
            // ให้บริการเสร็จในโหมด (ก) คงพฤติกรรมเดิม (หมายเหตุ "ออกเอกสารเองที่หน้าเอกสาร" จากตอนจองยังอยู่บนการจอง)
            if (isPayment || (f.Event == BookingInvoiceEvent.ServiceCompleted && f.Timing == BookingInvoiceTiming.TaxInvoiceOnPaymentOrService))
                return Issue(BookingInvoiceAction.CreateTaxInvoiceNow, f.VatRegistered, f.PaidInFullNow);
            return new(BookingInvoiceAction.None);
        }

        // ใบที่พนักงานแปลงจากใบยืนยันเอง — ร่าง = ยังไม่ออก (ฝ่ายค้าน ข้อ 4): ให้บริการเสร็จ/รับเงิน ⇒ อนุมัติใบนั้น · ออกแล้ว ⇒ ไม่ออกซ้ำ
        if (f.HasIssuedTaxDocument)
        {
            if (IsPending(f.IssuedChildStatus) && f.Event != BookingInvoiceEvent.NoShow)
                return new(BookingInvoiceAction.ApprovePendingTaxInvoice, UseIssuedChild: true);
            return new(BookingInvoiceAction.AlreadyIssued, UseIssuedChild: true,
                Notice: IsPending(f.IssuedChildStatus) ? "มีใบกำกับที่แปลงจากใบยืนยันการจองแต่ยังเป็นร่าง — ถ้าเรียกเก็บเงิน เปิดใบนั้นแล้วกด “อนุมัติ”" : null);
        }

        // ใบที่ผูกถูกยกเลิก/ปฏิเสธ (ใบยืนยันหรือใบกำกับ) ⇒ ไม่มีใบให้แปลง/อ้าง — ไม่ออกเอง (ไม่รู้ว่าทำไมถูกยกเลิก) แต่บอกทางไปต่อ
        if (IsDead(f.LinkedStatus))
            return isPayment
                ? new(BookingInvoiceAction.Rejected, Notice: "เอกสารที่ผูกกับการจองถูกยกเลิกแล้ว — ระบบออกใบกำกับให้อัตโนมัติไม่ได้ · ออกเองที่หน้าเอกสาร (อ้างเลขการจอง) ลงวันที่รับเงิน (§78/1)")
                : new(BookingInvoiceAction.None, Notice: f.Event == BookingInvoiceEvent.NoShow ? null
                    : "เอกสารที่ผูกกับการจองถูกยกเลิกแล้ว — ระบบออกใบกำกับภาษีให้อัตโนมัติไม่ได้ · ออกเองที่หน้าเอกสาร (อ้างเลขการจอง) ภายในวันที่ให้บริการ (§78/1)");

        if (IsIssuedTaxDocument(linked))
        {
            if (!IsPending(f.LinkedStatus))
                return isPayment ? new(BookingInvoiceAction.Rejected, Notice: "การจองนี้มีใบกำกับ/เอกสารรับเงินที่อนุมัติแล้ว — บันทึกรับชำระที่หน้าเอกสารนั้น (ไม่ออกใบซ้ำ)")
                    : new(BookingInvoiceAction.AlreadyIssued);
            // ใบที่ผูกยังเป็นร่าง (อนุมัติอัตโนมัติครั้งก่อนล้ม) — กด "รับเงินแล้ว" = อนุมัติใบนั้น · ให้บริการเสร็จ: โหมด (ข) อนุมัติให้ ·
            // โหมด (ก) คงพฤติกรรมเดิม (ไม่แตะ — หมายเหตุ "เปิดเอกสารแล้วกดอนุมัติ" จากตอนจองยังอยู่บนการจอง)
            return isPayment || (f.Event == BookingInvoiceEvent.ServiceCompleted && f.Timing == BookingInvoiceTiming.TaxInvoiceOnPaymentOrService)
                ? new(BookingInvoiceAction.ApprovePendingTaxInvoice)
                : new(BookingInvoiceAction.None);
        }

        if (linked != DocumentType.Quotation)
            return isPayment ? new(BookingInvoiceAction.Rejected, Notice: "เอกสารที่ผูกกับการจองไม่ใช่ใบยืนยันการจอง — ออกใบกำกับเองที่หน้าเอกสาร")
                : new(BookingInvoiceAction.None);

        if (f.Event == BookingInvoiceEvent.NoShow)
            // ไม่ได้รับเงิน · ไม่ได้ใช้บริการ · ยังไม่ออกใบกำกับ ⇒ ยังไม่มี tax point — ไม่ออกเอกสารภาษีเอง (ค่าตั้งใดก็ตาม)
            return new(BookingInvoiceAction.None, Notice:
                "ลูกค้าไม่มาใช้บริการ — ยังไม่ได้ออกใบกำกับภาษี (ยังไม่รับเงิน/ไม่ได้ให้บริการ = ยังไม่มีจุดความรับผิด §78/1) · "
                + "ถ้าเรียกเก็บค่าการันตี ออกใบกำกับเองที่หน้าเอกสาร (แปลงใบยืนยันการจอง) ลงวันที่รับเงิน");

        return Issue(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, f.VatRegistered, f.PaidInFullNow);
    }

    /// <summary><b>เอกสารรายได้ของการจองที่ POS ต้องนับ</b> (เปิดบิล POS ซ้ำ = รายได้/ภาษีขายซ้ำ) — ตัวตัดสินเดียวที่ <c>PosService.Bookings</c> เรียก ·
    /// ใบที่ผูก (ไม่ใช่ใบยืนยันการจอง · ไม่ใช่มัดจำ · ยังไม่ยกเลิก) = นับ · ใบที่ผูกเป็นใบยืนยันการจอง ⇒ นับ<b>ใบลูก</b>ที่แปลงจากใบนั้น
    /// (<see cref="IssuedTaxDocumentTypes"/> ที่ยังไม่ Voided/Rejected — พนักงานแปลงเองที่หน้าเอกสารโดยการจองยังผูกใบยืนยันอยู่ · ฝ่ายค้านทีม N) ·
    /// ร่างก็นับ (อนุมัติทีหลัง = รายได้ซ้ำ) · คืนเลขเอกสาร ("(ร่าง)" เมื่อยังไม่มีเลข) หรือ null = ไม่มี</summary>
    public static string? RevenueDocumentForPos(BookingDocRef? linked, IEnumerable<BookingDocRef> childrenOfLinked)
    {
        if (linked is not BookingDocRef l || IsDead(l.Status) || l.IsDeposit) return null;
        static string Label(string? no) => string.IsNullOrWhiteSpace(no) ? "(ร่าง)" : no;
        if (l.Type != DocumentType.Quotation) return Label(l.Number);
        foreach (var c in childrenOfLinked)
            if (IsIssuedTaxDocument(c.Type) && !IsDead(c.Status)) return Label(c.Number);
        return null;
    }

    /// <summary>แสดงปุ่ม "รับเงินแล้ว — ออกใบกำกับภาษี" ไหม — ตัวตัดสินเดียวกับตอนกด (<see cref="Decide"/> ไม่ปฏิเสธ) และเฉพาะการจองของโหมด (ข)
    /// (ใบที่ผูกเป็นใบยืนยันการจอง หรือไซต์ตั้งโหมด ข) ⇒ โหมด (ก) หน้าจอเหมือนเดิม (ฝ่ายค้าน ข้อ 6)</summary>
    public static bool ShowPaymentButton(BookingInvoiceFacts f)
    {
        if (f.Type != BookingType.Guaranteed) return false;
        if (f.Timing != BookingInvoiceTiming.TaxInvoiceOnPaymentOrService && f.LinkedDocumentType != DocumentType.Quotation) return false;
        var a = Decide(f with { Event = BookingInvoiceEvent.PaymentReceived }).Action;
        return a is BookingInvoiceAction.IssueTaxInvoiceFromConfirmation or BookingInvoiceAction.ApprovePendingTaxInvoice
            or BookingInvoiceAction.CreateTaxInvoiceNow;
    }

    /// <summary>วันที่รับเงินที่ผู้ใช้ระบุ → วันที่ใบกำกับ (ปฏิทินไทย 00:00 UTC) · ว่าง = วันนี้ · ห้ามอนาคต (ฝ่ายค้าน ข้อ 5)</summary>
    /// <returns>(วันที่, ข้อความปฏิเสธ) — ข้อความไม่ว่าง = ปฏิเสธ</returns>
    public static (DateTime Date, string? Error) PaymentDate(DateTime? received, DateTime utcNow)
    {
        var today = ThaiDate.CalendarDateUtc(utcNow);
        if (received is not DateTime r) return (today, null);
        var d = ThaiDate.CalendarDateUtc(r);
        return d > today
            ? (today, "วันที่รับเงินเป็นวันในอนาคตไม่ได้ — ใบกำกับต้องลงวันที่ที่ได้รับเงินจริง (§78/1)")
            : (d, null);
    }

    /// <summary>ข้อความหลังออกใบสำเร็จ (ตอบผู้กด + ประทับบนการจอง) — บอกเฉพาะสิ่งที่ระบบทำจริง (ยังไม่ได้บันทึกรับชำระ)</summary>
    public static string IssuedNotice(BookingInvoiceEvent ev, string documentNumber, DocumentType type, DateTime documentDate)
        => $"ออก{(type == DocumentType.TaxInvoice ? "ใบกำกับภาษี" : "ใบแจ้งหนี้")} {documentNumber} ลงวันที่ {documentDate:dd/MM/yyyy} "
           + (ev == BookingInvoiceEvent.PaymentReceived ? "(วันที่รับเงินที่ระบุ)" : "(วันที่บันทึกให้บริการเสร็จ)")
           + " · ยังไม่ได้บันทึกรับชำระ — บันทึกรับชำระที่หน้าเอกสารนั้นเมื่อได้รับเงิน";

    /// <summary>หมายเหตุบนการจอง/ใบยืนยันการจอง เมื่อโหมด (ข) ออกใบเสนอราคาแทนใบกำกับตอนจอง</summary>
    public const string DeferredNote = "ออกใบยืนยันการจอง (ใบเสนอราคา · ยังไม่ลงบัญชี/ภาษีขาย) — ใบกำกับภาษีออกอัตโนมัติเมื่อให้บริการเสร็จ · "
        + "ถ้ารับเงินก่อนวันบริการ กด “รับเงินแล้ว — ออกใบกำกับภาษี” แล้วระบุวันที่รับเงิน (§78/1)";

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
            + "เก็บเงินที่ POS ⇒ ใบกำกับออกที่ POS (ไม่ออกซ้ำจากการจอง) · นับโควตาเอกสาร 2 ใบต่อการจอง (ใบยืนยัน + ใบกำกับ)"),
    };
}
