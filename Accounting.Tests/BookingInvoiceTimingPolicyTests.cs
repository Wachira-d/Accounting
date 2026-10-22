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

    private static BookingInvoiceFacts F(BookingType type, BookingInvoiceTiming timing, BookingInvoiceEvent ev,
        DocumentType? linked = null, DocumentStatus? status = null, bool issuedChild = false, bool vat = true,
        DocumentStatus? childStatus = null, BookingPosBillState pos = BookingPosBillState.None, BookingStatus bs = BookingStatus.Confirmed)
        => new(type, timing, ev, linked, status, issuedChild, vat, childStatus, pos, bs);

    private static BookingInvoiceDecision D(BookingType type, BookingInvoiceTiming timing, BookingInvoiceEvent ev,
        DocumentType? linked = null, DocumentStatus? status = null, bool issuedChild = false, bool vat = true,
        DocumentStatus? childStatus = null, BookingPosBillState pos = BookingPosBillState.None, BookingStatus bs = BookingStatus.Confirmed)
        => BookingInvoiceTimingPolicy.Decide(F(type, timing, ev, linked, status, issuedChild, vat, childStatus, pos, bs));

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
        {
            foreach (var ev in new[] { BookingInvoiceEvent.ServiceCompleted, BookingInvoiceEvent.NoShow })
                Assert.Equal(BookingInvoiceAction.None, D(type, timing, ev, DocumentType.Quotation, DocumentStatus.Approved).Action);
            // ปุ่มรับเงินใช้กับ Guaranteed เท่านั้น — server ปฏิเสธ
            Assert.Equal(BookingInvoiceAction.Rejected, D(type, timing, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved).Action);
        }
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
    public void ใบภาษีที่ผูกอยู่ออกแล้ว_กดรับเงินซ้ำ_ปฏิเสธไม่ออกใหม่(DocumentType type, DocumentStatus status)
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, type, status);
        Assert.Equal(BookingInvoiceAction.Rejected, d.Action);
        Assert.Contains("ไม่ออกใบซ้ำ", d.Notice);
        // ให้บริการเสร็จหลังมีใบแล้ว ⇒ เงียบ ไม่ออกเพิ่ม
        Assert.Equal(BookingInvoiceAction.AlreadyIssued, D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, type, status).Action);
    }

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
        Assert.Contains("ออกใบกำกับเองที่หน้าเอกสาร", d.Notice);
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
        Assert.Equal(BookingInvoiceAction.Rejected, d.Action);
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
                // ออก/อนุมัติ หรือ มีใบภาษีอยู่แล้ว (ไม่ออกซ้ำ — ปฏิเสธพร้อมบอกให้รับชำระที่ใบนั้น)
                Assert.True(a is BookingInvoiceAction.IssueTaxInvoiceFromConfirmation or BookingInvoiceAction.ApprovePendingTaxInvoice
                    or BookingInvoiceAction.AlreadyIssued or BookingInvoiceAction.Rejected, $"{timing} {type} {status} {child} ⇒ {a}");
                if (a == BookingInvoiceAction.Rejected)
                    Assert.True(type != DocumentType.Quotation && status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval), $"ปฏิเสธทั้งที่ยังไม่มีใบภาษี: {type} {status}");
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

    // ───────────── ฝ่ายค้านทีม N ข้อ 1: บิล POS ของการจองเดียวกัน — รายได้ห้ามเกิดสองเส้น ─────────────

    [Fact]
    public void โหมด_ข_ปิดบิลPOSแล้ว_ให้บริการเสร็จ_ไม่ออกใบกำกับ_ยกเลิกใบยืนยัน()
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved,
            pos: BookingPosBillState.Closed);
        Assert.Equal(BookingInvoiceAction.SettledByPosBill, d.Action);
        Assert.True(d.VoidConfirmation);
        Assert.Null(d.DocumentType);
        Assert.Contains("POS", d.Notice);
    }

    [Theory]
    [InlineData(BookingPosBillState.Closed)]
    [InlineData(BookingPosBillState.Open)]
    public void มีบิลPOS_กดรับเงินที่การจอง_ปฏิเสธ(BookingPosBillState pos)
    {
        Assert.Equal(BookingInvoiceAction.Rejected,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved, pos: pos).Action);
        Assert.Equal(BookingInvoiceAction.Rejected,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, pos: pos).Action);
        Assert.False(BookingInvoiceTimingPolicy.ShowPaymentButton(
            F(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved, pos: pos)));
    }

    [Fact]
    public void บิลPOSเปิดอยู่_ให้บริการเสร็จ_ไม่ออกใบกำกับ_ไม่ยกเลิกใบยืนยัน()
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved,
            pos: BookingPosBillState.Open);
        Assert.Equal(BookingInvoiceAction.None, d.Action);
        Assert.False(d.VoidConfirmation);
    }

    [Fact]
    public void ไม่มีบิลPOS_โหมด_ข_ยังออกใบกำกับตามเดิม()
        => Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved,
                pos: BookingPosBillState.None).Action);

    [Fact]
    public void ปิดบิลPOSแล้ว_ไม่มีเอกสารผูก_ไม่สร้างใบกำกับตรง()
        => Assert.Equal(BookingInvoiceAction.SettledByPosBill,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, pos: BookingPosBillState.Closed).Action);

    // ───────────── ฝ่ายค้าน ข้อ 2: โหมด (ก) ไม่แตะใบกำกับที่ผูก (ใบเสร็จลูกไม่ใช่ "ออกแล้ว") ─────────────

    [Theory]
    [InlineData(DocumentType.TaxInvoice, false)]
    [InlineData(DocumentType.Receipt, false)]
    [InlineData(DocumentType.Invoice, false)]
    [InlineData(null, false)]
    [InlineData(DocumentType.Quotation, true)]
    public void หาใบลูกเฉพาะเมื่อใบที่ผูกเป็นใบยืนยันการจอง(DocumentType? linked, bool expected)
        => Assert.Equal(expected, BookingInvoiceTimingPolicy.ShouldLookForIssuedChild(linked));

    [Theory]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    [InlineData(BookingInvoiceEvent.NoShow)]
    public void โหมด_ก_ใบกำกับอนุมัติแล้ว_ไม่ย้ายการผูก(BookingInvoiceEvent ev)
    {
        var d = D(BookingType.Guaranteed, AtBooking, ev, DocumentType.TaxInvoice, DocumentStatus.Paid);
        Assert.False(d.UseIssuedChild);
        Assert.NotEqual(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation, d.Action);
        Assert.NotEqual(BookingInvoiceAction.ApprovePendingTaxInvoice, d.Action);
    }

    // ───────────── ฝ่ายค้าน ข้อ 3: สถานะการจองที่กดรับเงินไม่ได้ (server ปฏิเสธ) ─────────────

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Waitlisted)]
    [InlineData(BookingStatus.NoShow)]
    public void สถานะยกเลิก_คิวสำรอง_ไม่มา_กดรับเงินถูกปฏิเสธ(BookingStatus bs)
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved, bs: bs);
        Assert.Equal(BookingInvoiceAction.Rejected, d.Action);
        Assert.False(string.IsNullOrWhiteSpace(d.Notice));
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.InProgress)]
    [InlineData(BookingStatus.Completed)]
    public void สถานะปกติ_กดรับเงินได้(BookingStatus bs)
        => Assert.Equal(BookingInvoiceAction.IssueTaxInvoiceFromConfirmation,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved, bs: bs).Action);

    // ───────────── ฝ่ายค้าน ข้อ 4: ใบที่แปลงเองยังเป็นร่าง ≠ ออกแล้ว ─────────────

    [Theory]
    [InlineData(BookingInvoiceEvent.ServiceCompleted)]
    [InlineData(BookingInvoiceEvent.PaymentReceived)]
    public void ใบที่พนักงานแปลงเองยังเป็นร่าง_อนุมัติใบนั้น(BookingInvoiceEvent ev)
    {
        var d = D(BookingType.Guaranteed, Deferred, ev, DocumentType.Quotation, DocumentStatus.Approved, issuedChild: true, childStatus: DocumentStatus.Draft);
        Assert.Equal(BookingInvoiceAction.ApprovePendingTaxInvoice, d.Action);
        Assert.True(d.UseIssuedChild);
    }

    [Fact]
    public void ใบที่พนักงานแปลงเองอนุมัติแล้ว_ไม่ออกซ้ำ_ผูกใบนั้น()
    {
        var d = D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved,
            issuedChild: true, childStatus: DocumentStatus.Approved);
        Assert.Equal(BookingInvoiceAction.AlreadyIssued, d.Action);
        Assert.True(d.UseIssuedChild);
    }

    // ───────────── ฝ่ายค้าน ข้อ 5: วันที่รับเงิน ─────────────

    [Fact]
    public void วันที่รับเงินว่าง_ใช้วันนี้ตามเวลาไทย()
    {
        var now = new DateTime(2026, 10, 22, 18, 30, 0, DateTimeKind.Utc); // = 23 ต.ค. 01:30 เวลาไทย
        var (d, err) = BookingInvoiceTimingPolicy.PaymentDate(null, now);
        Assert.Null(err);
        Assert.Equal(new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc), d);
    }

    [Fact]
    public void วันที่รับเงินย้อนหลังได้_อนาคตไม่ได้()
    {
        var now = new DateTime(2026, 10, 22, 3, 0, 0, DateTimeKind.Utc);
        Assert.Null(BookingInvoiceTimingPolicy.PaymentDate(new DateTime(2026, 10, 20), now).Error);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), BookingInvoiceTimingPolicy.PaymentDate(new DateTime(2026, 10, 20), now).Date);
        Assert.Null(BookingInvoiceTimingPolicy.PaymentDate(new DateTime(2026, 10, 22), now).Error);
        Assert.NotNull(BookingInvoiceTimingPolicy.PaymentDate(new DateTime(2026, 10, 23), now).Error);
    }

    [Fact]
    public void ข้อความออกใบ_ไม่อ้างว่าบันทึกรับชำระแล้ว()
    {
        var n = BookingInvoiceTimingPolicy.IssuedNotice(BookingInvoiceEvent.PaymentReceived, "TIV-001", DocumentType.TaxInvoice, new DateTime(2026, 10, 20));
        Assert.Contains("20/10/2026", n);
        Assert.Contains("ยังไม่ได้บันทึกรับชำระ", n);
    }

    // ───────────── ฝ่ายค้าน ข้อ 6: ป้ายบริษัทไม่จด VAT · ปุ่มเฉพาะโหมด (ข) ─────────────

    [Fact]
    public void บริษัทไม่จดVAT_ป้ายบนการจองเป็นใบแจ้งหนี้()
        => Assert.Equal(ErpBookingDocumentType.Invoice,
            D(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.ServiceCompleted, DocumentType.Quotation, DocumentStatus.Approved, vat: false).ErpType);

    [Fact]
    public void ปุ่มรับเงิน_โหมด_ก_ไม่แสดง_แม้ไม่มีเอกสารผูก()
    {
        Assert.False(BookingInvoiceTimingPolicy.ShowPaymentButton(F(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.PaymentReceived)));
        Assert.False(BookingInvoiceTimingPolicy.ShowPaymentButton(
            F(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.PaymentReceived, DocumentType.TaxInvoice, DocumentStatus.Approved)));
        Assert.True(BookingInvoiceTimingPolicy.ShowPaymentButton(
            F(BookingType.Guaranteed, Deferred, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved)));
        // จองตอน (ข) แล้วสลับกลับ (ก) — ยังมีใบยืนยันผูกอยู่ ⇒ ปุ่มยังแสดง (tax point ไม่หลุด)
        Assert.True(BookingInvoiceTimingPolicy.ShowPaymentButton(
            F(BookingType.Guaranteed, AtBooking, BookingInvoiceEvent.PaymentReceived, DocumentType.Quotation, DocumentStatus.Approved)));
    }
}
