using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 270 (งานค้างจากรอบ 266/268/269 · คำตัดสิน 270-1-1…5) — ตัวตัดสิน pure ทั้งสองทิศ:
/// คืนมัดจำตามบัญชีของแต่ละงวด · แจ้งแชตหลังผูกสลิปต้องยังเป็นการผูกเดิม · จำแนกผล LINE ที่ไม่มีสถานะ HTTP · คิวใบลดหนี้ร่างของตัวช่วยใบซ้ำ ·
/// ใบกำกับ OCR ที่ VAT 0% ทั้งใบโดยกระดาษไม่บอกอัตราศูนย์ (กระดาษจริงจาก <see cref="OcrPaperSamples"/>)
/// </summary>
public class Round270Tests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000270");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000270");
    private static readonly Guid Doc = Guid.Parse("dddddddd-0000-0000-0000-000000000270");
    private static readonly Guid Intent = Guid.Parse("eeeeeeee-0000-0000-0000-000000000270");
    private static readonly IReadOnlyDictionary<Guid, decimal> None = new Dictionary<Guid, decimal>();

    // ═══ 270-1-1 คืนมัดจำตามบัญชีของแต่ละงวด ═══

    [Fact]
    public void L7_คืนเต็ม_สองงวดคนละบัญชี_แต่ละขาออกจากบัญชีของงวดนั้น_งวดล่าสุดก่อน()
    {
        var pools = new[] { new BookingDepositInstalmentChannel.RefundPool(B, 60m), new BookingDepositInstalmentChannel.RefundPool(A, 90m) };
        var full = BookingDepositInstalmentChannel.PlanRefund(pools, None, 0m, 150m);
        Assert.False(full.RequiresChoice);
        Assert.Equal(new[] { (A, 90m), (B, 60m) }, full.Legs.Select(l => (l.AccountId, l.Amount)).ToArray());
        // คืนบางส่วน ⇒ งวดล่าสุดก่อน
        var part = BookingDepositInstalmentChannel.PlanRefund(pools, None, 0m, 100m);
        Assert.Equal(new[] { (A, 90m), (B, 10m) }, part.Legs.Select(l => (l.AccountId, l.Amount)).ToArray());
        // คืนครั้งที่สอง: ครั้งแรกออกจาก A 90 แล้ว ⇒ ที่เหลืออยู่ B
        var second = BookingDepositInstalmentChannel.PlanRefund(pools, new Dictionary<Guid, decimal> { [A] = 90m }, 90m, 60m);
        Assert.Equal(new[] { (B, 60m) }, second.Legs.Select(l => (l.AccountId, l.Amount)).ToArray());
    }

    [Fact]
    public void L7_ทิศตรงข้าม_ช่องทางเดียว_ขาเดียว_และไม่รู้บัญชี_ต้องให้ผู้ใช้เลือก_ไม่เดา()
    {
        var one = BookingDepositInstalmentChannel.PlanRefund(new[] { new BookingDepositInstalmentChannel.RefundPool(A, 50m), new BookingDepositInstalmentChannel.RefundPool(A, 100m) }, None, 0m, 150m);
        Assert.Equal(new[] { (A, 150m) }, one.Legs.Select(l => (l.AccountId, l.Amount)).ToArray());
        Assert.False(one.RequiresChoice);
        // งวดก่อนรอบ 268 (ไม่รู้บัญชี) ⇒ ส่วนที่ตกงวดนั้นไม่เดา
        var unknown = BookingDepositInstalmentChannel.PlanRefund(new[] { new BookingDepositInstalmentChannel.RefundPool(null, 50m), new BookingDepositInstalmentChannel.RefundPool(A, 100m) }, None, 0m, 150m);
        Assert.True(unknown.RequiresChoice);
        Assert.Equal(50m, unknown.UnknownAmount);
        // คืนเฉพาะส่วนที่รู้ ⇒ ไม่ต้องถาม
        Assert.False(BookingDepositInstalmentChannel.PlanRefund(new[] { new BookingDepositInstalmentChannel.RefundPool(null, 50m), new BookingDepositInstalmentChannel.RefundPool(A, 100m) }, None, 0m, 100m).RequiresChoice);
        // คืนเกินยอดงวดที่บันทึกไว้ ⇒ ส่วนเกินไม่รู้บัญชี
        Assert.Equal(20m, BookingDepositInstalmentChannel.PlanRefund(new[] { new BookingDepositInstalmentChannel.RefundPool(A, 100m) }, None, 0m, 120m).UnknownAmount);
        // คืนครั้งก่อนจากบัญชีที่ผู้ใช้เลือก (ไม่ตรงงวดใด) ⇒ หักงวดที่ไม่รู้บัญชีก่อน
        var afterChosen = BookingDepositInstalmentChannel.PlanRefund(new[] { new BookingDepositInstalmentChannel.RefundPool(null, 50m), new BookingDepositInstalmentChannel.RefundPool(A, 100m) },
            new Dictionary<Guid, decimal> { [B] = 50m }, 50m, 100m);
        Assert.False(afterChosen.RequiresChoice);
        Assert.Equal(new[] { (A, 100m) }, afterChosen.Legs.Select(l => (l.AccountId, l.Amount)).ToArray());
        Assert.Contains("คืนจากบัญชี", BookingDepositInstalmentChannel.RefundChoiceMessage(50m));
    }

    // ═══ 270-1-2 แจ้งแชตหลังผูกสลิป: ถึงคิวแล้วต้องยังเป็นการผูกเดิม ═══

    [Fact]
    public void ตามต่อ268_การผูกเดิม_ส่งได้_แถวเก่าไม่มีกุญแจ_ตรวจสถานะอย่างเดียว()
    {
        var key = LineChatSlipRule.LinkKey(Doc, Intent);
        Assert.NotNull(key);
        Assert.Null(LineChatSlipRule.LinkKey(Doc, null));
        Assert.Null(LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Pending, Doc));
        Assert.Null(LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Succeeded, Doc));
        Assert.Null(LineChatSlipRule.LinkedNoticeStaleReason(null, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Pending, null));
    }

    [Fact]
    public void ตามต่อ268_ทิศตรงข้าม_ปิดแล้ว_ผูกใหม่_ปฏิเสธสลิป_ย้ายเอกสาร_ไม่ส่ง_มีเหตุ()
    {
        var key = LineChatSlipRule.LinkKey(Doc, Intent);
        Assert.Contains("ปิดสลิป", LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Dismissed, Doc, Intent, PaymentIntentStatus.Pending, Doc));
        Assert.Contains("ผูกใหม่", LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, A, Intent, PaymentIntentStatus.Pending, A));
        Assert.Contains("ผูกใหม่", LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, B, PaymentIntentStatus.Pending, Doc));
        Assert.Contains("ปฏิเสธ", LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Failed, Doc));
        Assert.NotNull(LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Expired, Doc));
        Assert.NotNull(LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, null, null));
        Assert.Contains("เอกสารอื่น", LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Assigned, Doc, Intent, PaymentIntentStatus.Pending, A));
        Assert.NotNull(LineChatSlipRule.LinkedNoticeStaleReason(key, null, null, null, null, null));
        Assert.NotNull(LineChatSlipRule.LinkedNoticeStaleReason(key, LineChatSlipRule.Statuses.Unmatched, null, null, null, null));
    }

    // ═══ 270-1-3 ผล LINE ที่ไม่มีสถานะ HTTP ═══

    [Fact]
    public void ตามต่อ268_ไม่มีสถานะHTTP_และไม่ใช่เครือข่ายล้ม_ถาวร_ไม่ลองซ้ำ_ทิศตรงข้าม_เครือข่ายล้ม_5xx_429_ลองซ้ำ_409ส่งแล้ว()
    {
        Assert.Equal(LinePushVerdict.Failed, LinePushOutcome.Classify(false, null, "ยังไม่ได้ตั้ง token ของ LINE OA", null, transportError: false).Verdict);
        Assert.Equal(LinePushVerdict.Failed, LinePushOutcome.Classify(false, null, "ปฏิเสธ", "LINE-ROUTE-DENIED", false).Verdict);
        Assert.Equal(LinePushVerdict.Failed, LinePushOutcome.Classify(false, 400, "Invalid to", null, false).Verdict);
        Assert.Equal(LinePushVerdict.Failed, LinePushOutcome.Classify(false, 429, "You have reached your monthly limit.", null, false).Verdict);
        Assert.Equal(LinePushVerdict.Retry, LinePushOutcome.Classify(false, null, "timeout", null, transportError: true).Verdict);
        Assert.Equal(LinePushVerdict.Retry, LinePushOutcome.Classify(false, 503, "busy", null, false).Verdict);
        Assert.Equal(LinePushVerdict.Retry, LinePushOutcome.Classify(false, 429, "rate", null, false).Verdict);
        Assert.Equal(LinePushVerdict.Sent, LinePushOutcome.Classify(false, 409, "dup", null, false).Verdict);
    }

    // ═══ 270-1-4 คิวใบลดหนี้ร่างของตัวช่วยใบซ้ำ ═══

    [Fact]
    public void L2_266C_แถวauditของเส้นใบลดหนี้_อ่านใบลดหนี้และเลขใบคู่_ทิศตรงข้าม_ขั้นอื่นหรือJSONเสีย_null()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            action = DuplicateSalePolicy.CreditNoteAuditAction, RuleCode = DuplicateSalePolicy.RuleCode, CashSale = "RC-0001", Invoice = "INV-0009",
            extra = new { creditNote = Doc, DocumentNumber = "CN-0001" },
        });
        var f = DuplicateSalePolicy.CreditNoteFromAudit(json);
        Assert.NotNull(f);
        Assert.Equal(Doc, f!.CreditNoteId);
        Assert.Equal("INV-0009", f.InvoiceNumber);
        Assert.Equal("RC-0001", f.CashSaleNumber);
        Assert.Null(DuplicateSalePolicy.CreditNoteFromAudit(System.Text.Json.JsonSerializer.Serialize(new { action = "VoidDuplicateInvoice", extra = new { creditNote = Doc } })));
        Assert.Null(DuplicateSalePolicy.CreditNoteFromAudit("{not json"));
        Assert.Null(DuplicateSalePolicy.CreditNoteFromAudit(null));
        Assert.True(DuplicateSalePolicy.CreditNoteStillPending(DocumentStatus.Draft));
        Assert.True(DuplicateSalePolicy.CreditNoteStillPending(DocumentStatus.WaitingApproval));
        Assert.False(DuplicateSalePolicy.CreditNoteStillPending(DocumentStatus.Approved));
        Assert.False(DuplicateSalePolicy.CreditNoteStillPending(DocumentStatus.Voided));
        Assert.Contains("ค้าง 3 วัน", DuplicateSalePolicy.CreditNoteDraftDetail("INV-0009", "RC-0001", new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc)));
    }

    // ═══ 270-1-5 ใบกำกับ OCR ที่ VAT 0% ทั้งใบ ═══

    /// <summary>ใบกำกับส่งออก (รูปแบบใบส่งออกไทยจริง — ใบกำกับภาษี/ใบขนสินค้า · พิมพ์อัตราศูนย์)</summary>
    internal const string ExportTaxInvoicePaper = ExportTaxInvoice;
    private const string ExportTaxInvoice =
        "บริษัท สยามเอ็กซ์ปอร์ต เทรดดิ้ง จำกัด (สำนักงานใหญ่)\n" +
        "เลขประจำตัวผู้เสียภาษี 0105548012345\n" +
        "ใบกำกับภาษี / TAX INVOICE (EXPORT)\n" +
        "เลขที่ EX6811-0021   วันที่ 05/11/2569\n" +
        "1  Dried Mango 10 kg x 100 cartons        100   1,000.00   100,000.00\n" +
        "รวมเงิน                                               100,000.00\n" +
        "ภาษีมูลค่าเพิ่ม 0% (ส่งออก มาตรา 80/1)                      0.00\n" +
        "จำนวนเงินรวมทั้งสิ้น                                  100,000.00";

    [Fact]
    public void R269ข้อ3_ใบกำกับจริงที่อ่านแถวVATไม่ได้_ทุกบรรทัด0_ติดแท็ก_หยุดอนุมัติเอง()
    {
        // ใบกำกับร้านวัสดุจริง (พิมพ์ "ภาษีมูลค่าเพิ่ม 7% 92.77") · engine อ่านแถว VAT ไม่ได้ ⇒ บรรทัดสรุป VatRate 0
        var note = OcrZeroVatTaxInvoiceGuard.Check(false, OcrPaperSamples.HardwareBillDiscount, null, new[] { 0m }, 0m);
        Assert.NotNull(note);
        Assert.StartsWith(OcrZeroVatTaxInvoiceGuard.Tag, note);
        Assert.False(OcrPostingReadiness.Evaluate("OCR อ่านแล้ว\n" + note, hasUsableDate: true).CanAutoApprove);
        // ฝั่งขาย: ชนิดเอกสาร = ใบกำกับ แม้คำบนกระดาษอ่านไม่ออก
        Assert.NotNull(OcrZeroVatTaxInvoiceGuard.Check(true, "บริษัท ก จำกัด\nค่าบริการ 1,000.00\nรวม 1,000.00", 0m, new[] { 0m, 0m }, 0m));
        // ผลของบรรทัดชุดนี้ — สร้างใหม่ (แก้อัตราแล้ว) ล้างแท็กเดิม
        Assert.DoesNotContain(OcrZeroVatTaxInvoiceGuard.Tag, OcrLineBuildNotes.StripRecomputed("a\n" + note + "\nb"));
    }

    /// <summary>ฝ่ายค้าน 270 M2: ใบกำกับ 7% ของผู้ขายที่ชื่อ/ที่อยู่มีคำว่า EXPORT/ส่งออก — กระดาษจริงร้านวัสดุ (HardwareBillDiscount) เปลี่ยนเฉพาะหัวผู้ขาย</summary>
    private static string ImportExportSeller => OcrPaperSamples.HardwareBillDiscount
        .Replace("บริษัท ไทยวัสดุภัณฑ์ก่อสร้าง จำกัด (สำนักงานใหญ่)", "บริษัท สยาม อิมพอร์ต-เอ็กซ์ปอร์ต จำกัด / SIAM IMPORT EXPORT CO.,LTD. (สำนักงานใหญ่)")
        .Replace("99/9 ถนนบางนา-ตราด แขวงบางนา เขตบางนา กรุงเทพฯ 10260", "88/8 นิคมอุตสาหกรรมเพื่อการส่งออก ลาดกระบัง กรุงเทพฯ 10520");

    [Fact]
    public void M2_270_ผู้ขายชื่อImportExport_ที่อยู่เขตส่งออก_ใบ7ที่อ่านVATไม่ได้_ยังติดแท็ก_ทิศตรงข้าม_ใบส่งออกจริงผ่าน()
    {
        Assert.Contains("IMPORT EXPORT", ImportExportSeller);
        Assert.Contains("เพื่อการส่งออก", ImportExportSeller);
        Assert.NotNull(OcrZeroVatTaxInvoiceGuard.Check(false, ImportExportSeller, null, new[] { 0m }, 0m));
        Assert.NotNull(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\nSIAM EXPORT TRADING CO.,LTD.\nค่าสินค้า 1,000.00\nรวม 1,000.00", 0m, new[] { 0m }, 0m));
        // ใบส่งออกจริง: VAT 0% บนบรรทัด VAT + ม.80/1 · ชนิดใบ "TAX INVOICE (EXPORT)" · อัตราศูนย์ · ส่งออกบนบรรทัด VAT
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, ExportTaxInvoice, 0m, new[] { 0m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี / TAX INVOICE (EXPORT)\nGoods 50,000.00\nTotal 50,000.00", 0m, new[] { 0m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\nบริการ 5,000.00\nภาษีมูลค่าเพิ่ม 0.00 (อัตราศูนย์)", 0m, new[] { 0m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\nสินค้า 5,000.00\nภาษีมูลค่าเพิ่ม (ส่งออก) 0.00", 0m, new[] { 0m }, 0m));
        // ส่วนลด 0% ที่ไม่ใช่บรรทัด VAT ไม่ใช่เครื่องหมาย
        Assert.NotNull(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\nส่วนลด 0% 0.00\nรวม 1,000.00", 0m, new[] { 0m }, 0m));
    }

    [Fact]
    public void M3_270_ทางแก้ของใบที่อนุมัติแล้ว_ฝั่งขายยกเลิกออกใหม่_ฝั่งซื้อบันทึกใหม่_งวดยื่นแล้วบอกยื่นเพิ่ม()
    {
        Assert.Contains("ออกใบกำกับใหม่", OcrZeroVatTaxInvoiceGuard.HowToFix(true, false));
        Assert.DoesNotContain("ยื่นแบบเพิ่มเติม", OcrZeroVatTaxInvoiceGuard.HowToFix(true, false));
        Assert.Contains("ภาษีซื้อ", OcrZeroVatTaxInvoiceGuard.HowToFix(false, true));
        Assert.Contains("ยื่นแบบเพิ่มเติม", OcrZeroVatTaxInvoiceGuard.HowToFix(false, true));
    }

    [Fact]
    public void H1_270_เงินของใบมัดจำ_ตัดที่งวดที่ครบมัดจำ_งวดที่ระบุชนะ_ยังไม่ครบได้ทุกแถว_ยอดงวดสุดท้ายไม่เกินยอดใบ()
    {
        var rows = new[] { (Id: A, Amt: 500m), (Id: B, Amt: 500m), (Id: Doc, Amt: 3_000m) };
        Assert.Equal(new[] { A, B }, BookingDepositInstalmentChannel.DepositRows(rows, r => r.Id, r => r.Amt, 1_000m, null).Select(r => r.Id).ToArray());
        Assert.Equal(new[] { A }, BookingDepositInstalmentChannel.DepositRows(rows, r => r.Id, r => r.Amt, 1_000m, A).Select(r => r.Id).ToArray());
        Assert.Equal(3, BookingDepositInstalmentChannel.DepositRows(rows, r => r.Id, r => r.Amt, 10_000m, null).Count);
        Assert.Equal(new[] { 500m, 500m }, BookingDepositInstalmentChannel.CapToDeposit(new[] { 500m, 3_500m }, 1_000m).ToArray());
        Assert.Equal(new[] { 60m, 90m }, BookingDepositInstalmentChannel.CapToDeposit(new[] { 60m, 90m }, 150m).ToArray());
    }

    [Fact]
    public void L5_270_retry_key_ผูกกับการผูก_การผูกเดิมคีย์เดิม_ผูกใหม่คีย์ใหม่_ไม่มีกุญแจใช้idแถว_L4ใบยกเลิกไม่ส่ง()
    {
        var k1 = LineChatSlipRule.LinkedNoticeRetryKey(Doc, LineChatSlipRule.LinkKey(A, Intent));
        Assert.Equal(k1, LineChatSlipRule.LinkedNoticeRetryKey(Doc, LineChatSlipRule.LinkKey(A, Intent)));
        Assert.NotEqual(k1, LineChatSlipRule.LinkedNoticeRetryKey(Doc, LineChatSlipRule.LinkKey(B, Intent)));
        Assert.Equal(Doc, LineChatSlipRule.LinkedNoticeRetryKey(Doc, null));
        Assert.Contains("ยกเลิก", LineChatSlipRule.LinkedNoticeStaleReason(LineChatSlipRule.LinkKey(Doc, Intent), LineChatSlipRule.Statuses.Assigned, Doc, Intent,
            PaymentIntentStatus.Pending, Doc, documentLive: false));
    }

    [Fact]
    public void L7_270_หัวเรื่องร่างไม่โชว์เลขDRAFT_หมายเหตุของตัวช่วยอ่านกลับได้_หมายเหตุอื่นไม่ใช่()
    {
        Assert.DoesNotContain("DRAFT-", DuplicateSalePolicy.CreditNoteDraftTitle("DRAFT-abc", "INV-1"));
        Assert.Contains("อ้าง INV-1", DuplicateSalePolicy.CreditNoteDraftTitle("DRAFT-abc", "INV-1"));
        Assert.Contains("CN-0001", DuplicateSalePolicy.CreditNoteDraftTitle("CN-0001", "INV-1"));
        var f = DuplicateSalePolicy.CreditNoteFromNote(Doc, DuplicateSalePolicy.CreditNoteNote("INV-9", "RC-3"));
        Assert.Equal(("INV-9", "RC-3"), (f!.InvoiceNumber, f.CashSaleNumber));
        Assert.Null(DuplicateSalePolicy.CreditNoteFromNote(Doc, "ลดหนี้เพราะสินค้าชำรุด"));
    }

    [Fact]
    public void M1_270_ล้มชั่วคราวถอยแล้วลองใหม่_ครบจำนวนครั้งจึงปิด()
    {
        var now = new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Utc);
        var (giveUp1, next1) = DeferredNotifyPolicy.AfterFailure(1, now);
        Assert.False(giveUp1);
        Assert.True(next1 > now);
        Assert.True(DeferredNotifyPolicy.AfterFailure(DeferredNotifyPolicy.MaxAttempts, now).GiveUp);
        Assert.Equal("LINE 503", new DeferredNotifyRetryException("LINE 503").Message);
    }

    [Fact]
    public void R269ข้อ3_ทิศตรงข้าม_ใบส่งออกจริง_ใบที่อ่านVATได้_ใบเสร็จไม่ใช่ใบกำกับ_บรรทัดยกเว้น_ไม่ติด()
    {
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, ExportTaxInvoice, 0m, new[] { 0m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, "INVOICE (EXPORT)\nGoods 100,000.00\nVAT 0% 0.00\nTotal 100,000.00", 0m, new[] { 0m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\nบริการใช้ในต่างประเทศ 5,000.00\nภาษีมูลค่าเพิ่มอัตราร้อยละ 0", 0m, new[] { 0m }, 0m));
        // ที่อยู่ "180/12" ไม่ใช่ "ม.80/1"
        Assert.NotNull(OcrZeroVatTaxInvoiceGuard.Check(true, "ใบกำกับภาษี\n180/12 ถนนสุขุมวิท\nค่าบริการ 1,000.00", 0m, new[] { 0m }, 0m));
        // อ่าน VAT ได้ (กระดาษจริงชุดเดิม)
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(false, OcrPaperSamples.HardwareBillDiscount, 92.77m, new[] { 7m, 7m, 7m }, 92.77m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(false, OcrPaperSamples.UptoyouShopee, 35.07m, new[] { 7m }, 35.07m));
        // ใบเสร็จของผู้ไม่จด VAT ที่พิมพ์ "ไม่ใช่ใบกำกับภาษี"
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(false, "ร้านป้าแดง\nใบเสร็จรับเงิน (ไม่ใช่ใบกำกับภาษี)\nข้าวสาร 500.00\nรวม 500.00", null, new[] { 0m }, 0m));
        // บรรทัดยกเว้น §81 (−1) ไม่ใช่ 0% ทั้งใบ · ไม่มีบรรทัด
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, OcrPaperSamples.HardwareBillDiscount, 0m, new[] { -1m }, 0m));
        Assert.Null(OcrZeroVatTaxInvoiceGuard.Check(true, OcrPaperSamples.HardwareBillDiscount, 0m, Array.Empty<decimal>(), 0m));
        Assert.True(OcrPostingReadiness.Evaluate("OCR อ่านแล้ว", true).CanAutoApprove);
    }
}
