using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินเจ้าของข้อ 155 (2026-10-22 · BOOKING_REVENUE_AUDIT B-2) — จังหวะออกใบกำกับภาษีของการจองแบบ Guaranteed เลือกได้ต่อไซต์ ·
/// ตัวตัดสินเดียว <see cref="BookingInvoiceTimingPolicy"/> · ล็อกทั้งสองทิศ: ค่าเริ่มต้น = ผลเดิมทุกเคส · โหมด (ข) ไม่ออกใบกำกับตอนจอง
/// แต่ออกครบเมื่อรับเงิน/ให้บริการ (แล้วแต่อย่างไหนก่อน · §78/1) · ไม่ออกซ้ำ · ยกเลิก/ไม่มา ถูกในแต่ละโหมด · สลับค่าตั้งกลางทาง tax point ไม่หลุด
/// </summary>
public class BookingInvoiceTimingPolicyTests
{
    private const BookingInvoiceTiming AtBooking = BookingInvoiceTiming.TaxInvoiceAtBooking;
    private const BookingInvoiceTiming Deferred = BookingInvoiceTiming.TaxInvoiceOnPaymentOrService;

    private static BookingInvoiceDecision D(BookingType type, BookingInvoiceTiming timing, BookingInvoiceEvent ev,
        DocumentType? linked = null, DocumentStatus? status = null, bool issuedChild = false, bool vat = true)
        => BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(type, timing, ev, linked, status, issuedChild, vat));

    // ───────────── ค่าเริ่มต้น (ก) = ผลเดิมทุกตัว ─────────────

    [Fact]
    public void ค่าเริ่มต้นของ_enum_คือใบกำกับตอนจอง()
        => Assert.Equal(0, (int)default(BookingInvoiceTiming));

    [Theory]
    [InlineData(BookingType.Lead, DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder)]
    [InlineData(BookingType.Appointment, DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder)]
    [InlineData(BookingType.Guaranteed, DocumentType.TaxInvoice, ErpBookingDocumentType.TaxInvoice)]
    [InlineData(BookingType.PrePayment, DocumentType.Receipt, ErpBookingDocumentType.AdvanceReceipt)]
    public void ค่าเริ่มต้น_ชนิดเอกสารตอนจองตรงกับ_switch_เดิมทุกชนิด(BookingType type, DocumentType doc, ErpBookingDocumentType erp)
    {
        var d = D(type, AtBooking, BookingInvoiceEvent.Booked);
        Assert.Equal(BookingInvoiceAction.CreateAtBooking, d.Action);
        Assert.Equal(doc, d.DocumentType);
        Assert.Equal(erp, d.ErpType);
    }

    [Theory]
    [InlineData(BookingInvoiceEvent.PaymentReceived)]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    [InlineData(BookingInvoiceEvent.NoShow)]
    public void ค่าเริ่มต้น_มีใบกำกับอนุมัติแล้ว_หลังจองไม่ออกเพิ่ม(BookingInvoiceEvent ev)
    {
        var d = D(BookingType.Guaranteed, AtBooking, ev, DocumentType.TaxInvoice, DocumentStatus.Approved);
        Assert.NotEqual(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, d.Action);
        Assert.NotEqual(BookingInvoiceAction.ApprovePendingTaxInvoice, d.Action);
    }

    [Fact]
    public void ค่าเริ่มต้น_ใบกำกับยังเป็นร่างตอนให้บริการเสร็จ_ไม่แตะเหมือนเดิม()
        => Assert.Equal(BookingInvoiceAction.None,
            D(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.ServiceCompleted, DocumentType.TaxInvoice, DocumentStatus.Draft).Action);

    [Theory]
    [InlineData(BookingType.Lead)]
    [InlineData(BookingType.Appointment)]
    [InlineData(BookingType.PrePayment)]
    public void ชนิดอื่นนอกจาก_Guaranteed_ไม่ถูกแตะหลังจองทั้งสองโหมด(BookingType type)
    {
        foreach (var timing in new[] { AtBooking, Deferred })
        foreach (var ev in new[] { BookingInvoiceEvent.PaymentReceived, BookingInvoiceEvent.ServiceCompleted, BookingInvoiceEvent.NoShow })
            Assert.Equal(BookingInvoiceAction.None, D(type, timing, ev, DocumentType.Quotation, DocumentStatus.Approved).Action);
    }

    [Theory]
    [InlineData(BookingType.Lead)]
    [InlineData(BookingType.Appointment)]
    [InlineData(BookingType.PrePayment)]
    public void โหมด_ข_ไม่เปลี่ยนชนิดเอกสารตอนจองของชนิดอื่น(BookingType type)
        => Assert.Equal(D(type, AtBooking, BookingInvoiceEvent.Booked), D(type, Deferred, BookingInvoiceEvent.Booked));

    // ───────────── โหมด (ข) ─────────────

    [Fact]
    public void โหมด_ข_ตอนจองออกใบยืนยันการจอง_ไม่ใช่ใบกำกับ()
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.Booked);
        Assert.Equal(BookingInvoiceAction.CreateAtBooking, d.Action);
        Assert.Equal(DocumentType.Quotation, d.DocumentType);
        Assert.Equal(ErpBookingDocumentType.Quotation, d.ErpType);
        Assert.NotEqual(DocumentType.TaxInvoice, d.DocumentType);
    }

    [Theory]
    [InlineData(BookingInvoiceEvent.PaymentReceived)]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    public void โหมด_ข_รับเงินหรือให้บริการ_ออกใบกำกับจากใบยืนยันการจอง(BookingInvoiceEvent ev)
    {
        var d = D(BookingType.Guaranteed, Deferred, ev, DocumentType.Quotation, DocumentStatus.Approved);
        Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, d.Action);
        Assert.Equal(DocumentType.TaxInvoice, d.DocumentType);
        Assert.Equal(ErpBookingDocumentType.TaxInvoice, d.ErpType);
    }

    [Fact]
    public void โหมด_ข_บริษัทไม่จด_VAT_ออกใบแจ้งหนี้แทนใบกำกับ()
        => Assert.Equal(DocumentType.Invoice,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved, vat: false).DocumentType);

    [Fact]
    public void โหมด_ข_รับเงินก่อนแล้วให้บริการทีหลัง_ออกครั้งเดียว()
    {
        // รับเงิน ⇒ ออกใบกำกับ (การจองผูกใบกำกับที่อนุมัติแล้ว) ⇒ ให้บริการเสร็จ ⇒ ไม่ออกซ้ำ
        Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved).Action);
        Assert.Equal(BookingInvoiceAction.AlreadyIssued,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.TaxInvoice, DocumentStatus.Approved).Action);
    }

    [Theory]
    [InlineData(BookingInvoiceEvent.PaymentReceived)]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    public void พนักงานแปลงใบยืนยันเป็นใบกำกับเองแล้ว_ไม่ออกซ้ำ(BookingInvoiceEvent ev)
        => Assert.Equal(BookingInvoiceAction.AlreadyIssued,
            D(BookingType.Guaranteed, Deferred, ev, DocumentType.Quotation, DocumentStatus.Approved, issuedChild: true).Action);

    [Theory]
    [InlineData(DocumentType.TaxInvoice, DocumentStatus.Paid)]
    [InlineData(DocumentType.Invoice, DocumentStatus.Approved)]
    [InlineData(DocumentType.Receipt, DocumentStatus.Approved)]
    public void ใบภาษีที่ผูกอยู่ออกแล้ว_กดรับเงินซ้ำไม่ออกใหม่(DocumentType type, DocumentStatus status)
        => Assert.Equal(BookingInvoiceAction.AlreadyIssued,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, type, status).Action);

    [Theory]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.WaitingApproval)]
    public void ใบกำกับที่ผูกยังเป็นร่าง_อนุมัติใบนั้น_ไม่แปลงใหม่(DocumentStatus status)
    {
        Assert.Equal(BookingInvoiceAction.ApprovePendingTaxInvoice,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.TaxInvoice, status).Action);
        Assert.Equal(BookingInvoiceAction.ApprovePendingTaxInvoice,
            D(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.PaymentReceived, DocumentType.TaxInvoice, status).Action);
    }

    [Fact]
    public void โหมด_ข_ไม่มา_ไม่ออกใบกำกับ_แต่บอกทางไปต่อ()
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.NoShow, DocumentType.Quotation, DocumentStatus.Approved);
        Assert.Equal(BookingInvoiceAction.None, d.Action);
        Assert.Contains("รับเงินแล้ว — ออกใบกำกับภาษี", d.Notice);
    }

    [Fact]
    public void ค่าเริ่มต้น_ไม่มา_ใบกำกับคงอยู่_ไม่มีข้อความ()
    {
        var d = D(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.NoShow, DocumentType.TaxInvoice, DocumentStatus.Approved);
        Assert.Equal(BookingInvoiceAction.AlreadyIssued, d.Action);
        Assert.Null(d.Notice);
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.TaxInvoice)]
    public void เอกสารที่ผูกถูกยกเลิก_ไม่ออกเอง_ไม่อ้างว่ามีใบแล้ว_บอกให้ออกมือ(DocumentType linked)
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, linked, DocumentStatus.Voided);
        Assert.Equal(BookingInvoiceAction.None, d.Action);
        Assert.Contains("§78/1", d.Notice);
    }

    [Theory]
    [InlineData(BookingInvoiceTiming.TaxInvoiceAtBooking)]
    [InlineData(BookingInvoiceTiming.TaxInvoiceOnPaymentOrService)]
    public void ยังไม่มีเอกสารผูก_ขั้นERPตอนจองล้ม_กดรับเงิน_สร้างใบกำกับตรง(BookingInvoiceTiming timing)
    {
        var d = D(BookingType.Guaranteed, timing, BookingInvoiceEvent.PaymentReceived);
        Assert.Equal(BookingInvoiceAction.CreateTaxInvoiceNow, d.Action);
        Assert.Equal(DocumentType.TaxInvoice, d.DocumentType);
        Assert.Equal(DocumentType.Invoice, D(BookingType.Guaranteed, timing, BookingInvoiceEvent.PaymentReceived, vat: false).DocumentType);
    }

    [Fact]
    public void ยังไม่มีเอกสารผูก_ให้บริการเสร็จ_โหมด_ข_สร้างใบกำกับ_โหมด_ก_คงเดิม()
    {
        Assert.Equal(BookingInvoiceAction.CreateTaxInvoiceNow, D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted).Action);
        Assert.Equal(BookingInvoiceAction.None, D(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.ServiceCompleted).Action);
        Assert.Equal(BookingInvoiceAction.None, D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.NoShow).Action);
    }

    // ───────────── tax point ห้ามหลุดเพราะค่าตั้ง ─────────────

    [Theory]
    [InlineData(BookingInvoiceEvent.PaymentReceived)]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    public void จองตอนโหมด_ข_แล้วเจ้าของสลับกลับ_ก_ยังออกใบกำกับเมื่อรับเงินหรือให้บริการ(BookingInvoiceEvent ev)
        => Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation,
            D(BookingType.Guaranteed, AtBooking, ev, DocumentType.Quotation, DocumentStatus.Approved).Action);

    [Fact]
    public void ทุกค่าตั้ง_รับเงินแล้วต้องจบที่มีใบภาษีเสมอ()
    {
        // ทุกสภาพเอกสารที่ผูกได้ (ยังไม่ถูกยกเลิก) — รับเงินแล้วต้อง "ออก/อนุมัติ" หรือ "มีอยู่แล้ว" เท่านั้น
        var states = new (DocumentType Type, DocumentStatus Status, bool Child)[]
        {
            (DocumentType.Quotation, DocumentStatus.Draft, false), (DocumentType.Quotation, DocumentStatus.Approved, false),
            (DocumentType.Quotation, DocumentStatus.Approved, true), (DocumentType.TaxInvoice, DocumentStatus.Draft, false),
            (DocumentType.TaxInvoice, DocumentStatus.Approved, false), (DocumentType.Invoice, DocumentStatus.Paid, false),
        };
        foreach (var timing in Enum.GetValues<BookingInvoiceTiming>())
        {
            foreach (var (type, status, child) in states)
            {
                var a = D(BookingType.Guaranteed, timing, BookingInvoiceEvent.PaymentReceived, type, status, child).Action;
                Assert.True(a is BookingInvoiceAction.IssueTaxInvoiceFromConfirmation or BookingInvoiceAction.ApprovePendingTaxInvoice
                    or BookingInvoiceAction.AlreadyIssued, $"{timing} {type} {status} {child} ⇒ {a}");
            }
            // ไม่มีเอกสารผูกเลย ⇒ สร้างใบกำกับตรง
            Assert.Equal(BookingInvoiceAction.CreateTaxInvoiceNow, D(BookingType.Guaranteed, timing, BookingInvoiceEvent.PaymentReceived).Action);
        }
    }

    // ───────────── ยกเลิก: เส้นเดิม (CmsBookingCancelPolicy) ถูกในทั้งสองโหมด ─────────────

    [Fact]
    public void ยกเลิกการจอง_ใบที่ผูก_ไม่ใช่มัดจำ_ถูก_void_ทั้งสองโหมด()
    {
        // โหมด (ข) ก่อนรับเงิน: ใบที่ผูก = ใบยืนยันการจอง (ใบเสนอราคา · ไม่มีผลภาษี) · โหมด (ก) / (ข) หลังออกใบกำกับ: ใบที่ผูก = ใบกำกับ — เส้นเดิมตัดสินเหมือนกัน
        Assert.Equal(CmsBookingCancelAction.VoidDocument, CmsBookingCancelPolicy.DecideOnCancel(isDeposit: false, DocumentStatus.Approved));
        // ยกเลิกแล้ว ⇒ ไม่ทำซ้ำ
        Assert.Equal(CmsBookingCancelAction.NothingToDo, CmsBookingCancelPolicy.DecideOnCancel(isDeposit: false, DocumentStatus.Voided));
    }

    // ───────────── หน้าตั้งค่า ─────────────

    [Fact]
    public void ตัวเลือกครบทุกค่า_enum_และมีผลทางภาษีภาษาไทย()
    {
        var opts = BookingInvoiceTimingPolicy.Options();
        Assert.Equal(Enum.GetNames<BookingInvoiceTiming>().OrderBy(x => x), opts.Select(o => o.Value).OrderBy(x => x));
        Assert.All(opts, o => Assert.False(string.IsNullOrWhiteSpace(o.TaxEffect)));
        Assert.Equal(nameof(BookingInvoiceTiming.TaxInvoiceAtBooking), opts[0].Value);
    }
}
