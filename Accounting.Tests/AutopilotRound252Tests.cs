using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 252 WP-12A (erp-review/2026-11-06/AUDIT_249_FOR_OPUS.md §WP-12 · AUTOPILOT_BILLING_DESIGN §5/§7/§10/§11.4 · คำตัดสิน §2 #36) — ตัวตัดสิน pure ของ
/// Autopilot ส่วน A: เงินโอนปิดหนี้ (<see cref="BankReceiptAutoSettle"/>) · ใบเสนอราคาแปลงอัตโนมัติ (<see cref="QuotationAcceptAutopilot"/>) ·
/// อีเมลสถานะ/สรุปประจำวัน (<see cref="AutopilotNotice"/>) · ขั้นของใบ (<see cref="AutopilotStage"/>) · สลิปกันซ้ำ (<see cref="SlipEvidenceRule"/>) — ทุกด่านมีทิศตรงข้าม
/// </summary>
public class AutopilotRound252Tests
{
    private static readonly DateTime Day = new(2026, 11, 5);

    private static BankReceiptAutoSettle.Line Line(decimal amount, string? source = BankReceiptAutoSettle.SourceStatement, bool credit = true, bool matched = false)
        => new(amount, Day, credit, source, matched);

    private static BankReceiptAutoSettle.Candidate Cand(string number, decimal net, decimal? gross = null, bool cites = false, bool name = false,
        bool slip = false, bool pendingOnline = false, bool contactOff = false, DateTime? date = null, bool creatorOk = true, bool supplier = false)
        => new(Guid.NewGuid(), number, date ?? Day.AddDays(-10), net, gross ?? net, cites, name, slip, pendingOnline, contactOff, creatorOk, supplier);

    // ═══ BankReceiptAutoSettle — ปิดหนี้เองเมื่อครบทุกข้อเท่านั้น ═══

    [Fact]
    public void ยอดสุทธิตรงเป๊ะ_อ้างเลขเอกสาร_มาจากstatement_บริษัทเปิด_ปิดหนี้เอง()
    {
        var c = Cand("INV-2569110012", 10_400m, 10_700m, cites: true);
        var r = BankReceiptAutoSettle.Decide(Line(10_400m), new[] { c }, companyEnabled: true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle, r.Kind);
        Assert.Equal(c.DocumentId, r.DocumentId);
        Assert.Equal(BankReceiptAutoSettle.RuleAuto, r.RuleCode);
        Assert.True(r.WouldAutoSettleIfEnabled);
    }

    [Fact]
    public void ทิศตรงข้าม_บริษัทยังไม่เปิด_เข้าเกณฑ์ก็แค่เสนอ_ไม่ปิดเอง()
    {
        var c = Cand("INV-2569110012", 10_400m, cites: true);
        var r = BankReceiptAutoSettle.Decide(Line(10_400m), new[] { c }, companyEnabled: false);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Equal(c.DocumentId, r.DocumentId);
        Assert.True(r.WouldAutoSettleIfEnabled);
        Assert.Contains("บริษัทยังไม่เปิด", r.Reason);
    }

    [Theory]
    [InlineData(BankReceiptAutoSettle.SourceManual)]
    [InlineData(null)]
    [InlineData("Slip")]
    [InlineData(BankReceiptAutoSettle.SourceUserFile)]   // ฝ่ายค้าน H1: ไฟล์ศูนย์นำเข้า/นำเข้า-ส่งออก/Open Banking (ไฟล์)
    [InlineData(BankReceiptAutoSettle.SourceApi)]        // ฝ่ายค้าน H1: API v1
    public void รายการพิมพ์มือ_ไฟล์หรือAPIที่ผู้ใช้ส่งเอง_หรือไม่รู้แหล่ง_ไม่ปิดเอง_แม้ผู้ส่งมีสิทธิ์อนุมัติ(string? source)
    {
        var r = BankReceiptAutoSettle.Decide(Line(1_040m, source), new[] { Cand("INV-2569000001", 1_040m, cites: true) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.False(r.WouldAutoSettleIfEnabled);
        Assert.Contains("ไม่ได้มาจากธนาคาร", r.Reason);
    }

    [Theory]
    [InlineData(BankReceiptAutoSettle.SourceStatement)]
    [InlineData(BankReceiptAutoSettle.SourceFeed)]
    [InlineData(BankReceiptAutoSettle.SourceFeedCustom)]
    public void ทิศตรงข้าม_ผู้นำเข้ามีสิทธิ์อนุมัติ_statementหน้าธนาคาร_และfeed_ปิดเองได้(string source)
        => Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle,
            BankReceiptAutoSettle.Decide(Line(1_040m, source), new[] { Cand("INV-2569000001", 1_040m, cites: true) }, true).Kind);

    [Theory]
    [InlineData(BankReceiptAutoSettle.SourceStatement)]
    [InlineData(BankReceiptAutoSettle.SourceFeedCustom)]
    public void ฝ่ายค้านH1_ผู้นำเข้าไม่มีสิทธิ์อนุมัติหรือไม่ทราบ_เสนอเท่านั้นพร้อมเหตุ(string source)
    {
        var r = BankReceiptAutoSettle.Decide(Line(1_040m, source), new[] { Cand("INV-2569000001", 1_040m, cites: true, creatorOk: false) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Contains("ไม่มีสิทธิ์อนุมัติ", r.Reason);
    }

    [Fact]
    public void feedปลายทางที่ระบบรู้จัก_ไม่ต้องตรวจสิทธิ์ผู้สั่งดึง()
        => Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle,
            BankReceiptAutoSettle.Decide(Line(1_040m, BankReceiptAutoSettle.SourceFeed),
                new[] { Cand("INV-2569000001", 1_040m, cites: true, creatorOk: false) }, true).Kind);

    [Fact]
    public void ฝ่ายค้านM3_ใบลงวันหลังเงินเข้า_เสนอพร้อมเหตุtaxpoint_ใบลงวันเดียวกัน_ปิดเองได้()
    {
        var later = BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, cites: true, date: Day.AddDays(1)) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, later.Kind);
        Assert.Contains("tax point", later.Reason);
        Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle,
            BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, cites: true, date: Day) }, true).Kind);
    }

    [Fact]
    public void ผู้จ่ายเป็นผู้ขายของเราด้วย_เสนอเท่านั้น()
        => Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest,
            BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, cites: true, supplier: true) }, true).Kind);

    [Fact]
    public void จับคู่มือห้ามแตะบรรทัดที่ระบบกำลังบันทึก()
    {
        Assert.NotNull(BankReceiptAutoSettle.ManualMatchBlock(BankReceiptAutoSettle.Stamps.Applying));
        Assert.Null(BankReceiptAutoSettle.ManualMatchBlock(BankReceiptAutoSettle.Stamps.Suggest));
        Assert.Null(BankReceiptAutoSettle.ManualMatchBlock(null));
    }

    [Fact]
    public void ยอดตรงแต่ไม่มีตัวระบุตัว_เสนอ_ไม่ปิดเอง()
    {
        var r = BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Contains("ไม่มีตัวระบุตัว", r.Reason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ชื่อผู้โอนตรง_หรือ_เลขอ้างอิงตรงสลิป_นับเป็นตัวระบุตัว(bool name, bool slip)
    {
        var r = BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, name: name, slip: slip) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle, r.Kind);
    }

    [Fact]
    public void ยอดเท่ากันสองใบ_ไม่อ้างเลขเอกสาร_ให้คนเลือก_ไม่ปิดเอง_แม้ชื่อตรง()
    {
        var a = Cand("INV-2569000001", 10_400m, name: true);
        var b = Cand("INV-2569000002", 10_400m, name: true);
        var r = BankReceiptAutoSettle.Decide(Line(10_400m), new[] { a, b }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Null(r.DocumentId);
        Assert.Contains("2 ใบ", r.Reason);
    }

    [Fact]
    public void ยอดเท่ากันสองใบ_อ้างเลขเอกสารใบเดียว_ปิดใบนั้น()
    {
        var a = Cand("INV-2569000001", 10_400m);
        var b = Cand("INV-2569000002", 10_400m, cites: true);
        var r = BankReceiptAutoSettle.Decide(Line(10_400m), new[] { a, b }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.AutoSettle, r.Kind);
        Assert.Equal(b.DocumentId, r.DocumentId);
    }

    [Fact]
    public void ลูกค้าไม่หัก_ณ_ที่จ่าย_ยอดเต็ม_เสนอ_ไม่เดาแทน()
    {
        var c = Cand("INV-2569000001", 10_400m, 10_700m, cites: true);
        var r = BankReceiptAutoSettle.Decide(Line(10_700m), new[] { c }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Equal(c.DocumentId, r.DocumentId);
        Assert.Contains("ก่อนหัก ณ ที่จ่าย", r.Reason);
    }

    [Theory]
    [InlineData(10_200, "ขาด 200.00")]
    [InlineData(10_430, "เกิน 30.00")]
    public void ขาดหรือเกินเล็กน้อยพร้อมตัวระบุตัว_เสนอพร้อมส่วนต่าง_ไม่ปิดเอง(int amount, string expected)
    {
        var c = Cand("INV-2569000001", 10_400m, name: true);
        var r = BankReceiptAutoSettle.Decide(Line(amount), new[] { c }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Contains(expected, r.Reason);
    }

    [Fact]
    public void ขาดมากเกินเกณฑ์_ไม่เสนอ()
        => Assert.Equal(BankReceiptAutoSettle.Verdict.None,
            BankReceiptAutoSettle.Decide(Line(9_000m), new[] { Cand("INV-2569000001", 10_400m, name: true) }, true).Kind);

    [Fact]
    public void ใบมีรายการชำระออนไลน์ค้าง_ไม่ปิดเอง_กันนับซ้ำกับgateway()
    {
        var r = BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, cites: true, pendingOnline: true) }, true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, r.Kind);
        Assert.Contains("ชำระออนไลน์ค้าง", r.Reason);
    }

    [Fact]
    public void ผู้ติดต่อปิดการปิดหนี้อัตโนมัติ_เสนอเท่านั้น()
        => Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest,
            BankReceiptAutoSettle.Decide(Line(1_040m), new[] { Cand("INV-2569000001", 1_040m, cites: true, contactOff: true) }, true).Kind);

    [Fact]
    public void เงินออก_หรือจับคู่แล้ว_หรือใบลงวันหลังเงินเข้าเกิน7วัน_ไม่ทำอะไร()
    {
        var c = Cand("INV-2569000001", 1_040m, cites: true);
        Assert.Equal(BankReceiptAutoSettle.Verdict.None, BankReceiptAutoSettle.Decide(Line(1_040m, credit: false), new[] { c }, true).Kind);
        Assert.Equal(BankReceiptAutoSettle.Verdict.None, BankReceiptAutoSettle.Decide(Line(1_040m, matched: true), new[] { c }, true).Kind);
        var late = Cand("INV-2569000002", 1_040m, cites: true, date: Day.AddDays(8));
        Assert.Equal(BankReceiptAutoSettle.Verdict.None, BankReceiptAutoSettle.Decide(Line(1_040m), new[] { late }, true).Kind);
        // ฝ่ายค้าน M3: ภายใน 7 วันหลังเงินเข้ายังเป็นผู้สมัคร แต่เสนอเท่านั้น
        var within = Cand("INV-2569000003", 1_040m, cites: true, date: Day.AddDays(7));
        Assert.Equal(BankReceiptAutoSettle.Verdict.Suggest, BankReceiptAutoSettle.Decide(Line(1_040m), new[] { within }, true).Kind);
    }

    [Theory]
    [InlineData("TRF FR บจก.พี INV2569110012", "INV-2569110012", true)]
    [InlineData("inv 2569/110012 ค่าบริการ", "INV-2569110012", true)]
    [InlineData("TRF 2569110013", "INV-2569110012", false)]
    [InlineData("TRF IV-12 จ่าย", "IV-12", false)]   // เลขสั้นกว่า 6 ตัว — ไม่นับ (กันชนเลขทั่วไป)
    [InlineData("TRF INV25691100123", "INV-2569110012", false)]   // ฝ่ายค้าน LOW: ตามขอบคำ — เลขยาวกว่าไม่ใช่ใบนี้
    [InlineData("XINV2569110012 TRF", "INV-2569110012", false)]
    public void อ้างเลขเอกสาร_เทียบแบบตัดสัญลักษณ์และไม่สนตัวพิมพ์(string lineText, string number, bool expected)
        => Assert.Equal(expected, BankReceiptAutoSettle.CitesDocumentNumber(lineText, number));

    [Fact]
    public void ผลจำลอง_นับปิดเองได้_เสนอ_ไม่พบ_แยกกัน()
    {
        var s = BankReceiptAutoSettle.Summarize(new[]
        {
            new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.AutoSettle, Guid.NewGuid(), BankReceiptAutoSettle.RuleAuto, "", true),
            new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.Suggest, Guid.NewGuid(), BankReceiptAutoSettle.RuleSuggest, "", true),
            new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.Suggest, null, BankReceiptAutoSettle.RuleSuggest, "", false),
            new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.None, null, BankReceiptAutoSettle.RuleNone, "", false),
        });
        Assert.Equal(4, s.Lines);
        Assert.Equal(2, s.WouldAutoSettle);
        Assert.Equal(1, s.WouldSuggest);
        Assert.Equal(1, s.NoMatch);
    }

    [Fact]
    public void งานหลังรับชำระ_ทางเข้าเงินโอนปิดหนี้_เดินเต็มเหมือนgateway()
    {
        var on = new PostPaymentAutomationPolicy.Settings(EmailReceipt: true, IncludePartial: false);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.Pending, PostPaymentAutomationPolicy.ForEnqueue(on, PaymentEntrySource.BankAutoSettle).Kind);
        Assert.Equal(PaymentEntrySource.BankAutoSettle, PostPaymentAutomationPolicy.ParseSource(nameof(PaymentEntrySource.BankAutoSettle)));
        // ทิศตรงข้าม: ปิดค่าตั้ง ⇒ ไม่สร้างแถว (เหมือนทุกทางเข้า)
        var off = new PostPaymentAutomationPolicy.Settings(EmailReceipt: false, IncludePartial: false);
        Assert.Equal(PostPaymentAutomationPolicy.EnqueueKind.None, PostPaymentAutomationPolicy.ForEnqueue(off, PaymentEntrySource.BankAutoSettle).Kind);
    }

    // ═══ QuotationAcceptAutopilot — ค่าตั้งต้นไม่แปลง · ตั้งต่อใบ ═══

    private static QuotationAcceptAutopilot.Facts Qt(DocumentStatus status = DocumentStatus.Sent, bool accepted = true, bool? matches = true,
        bool child = false, bool vat = true, DocumentType type = DocumentType.Quotation)
        => new(type, status, accepted ? Day : null, matches, child, vat);

    [Theory]
    [InlineData(null, null, QuotationAcceptMode.Off)]
    [InlineData(null, "ToInvoice", QuotationAcceptMode.ToInvoice)]
    [InlineData("Off", "ToInvoice", QuotationAcceptMode.Off)]          // ใบตั้ง "ไม่แปลง" ชนะค่าบริษัท
    [InlineData("ToTaxInvoice", null, QuotationAcceptMode.ToTaxInvoice)]
    [InlineData("Inherit", "ToTaxInvoice", QuotationAcceptMode.ToTaxInvoice)]
    [InlineData("garbage", null, QuotationAcceptMode.Off)]              // อ่านไม่ออก = ไม่เดา
    public void ค่าที่มีผล_ใบ_ชนะ_บริษัท_ชนะ_ปิด(string? doc, string? company, QuotationAcceptMode expected)
        => Assert.Equal(expected, QuotationAcceptAutopilot.Effective(doc, company));

    [Fact]
    public void ค่าที่ส่งมาตั้ง_อ่านไม่ออก_ปฏิเสธพร้อมเหตุ_ว่าง_ล้าง()
    {
        Assert.NotNull(QuotationAcceptAutopilot.Normalize("ToRecurring").Problem);
        Assert.Equal(((string?)null, (string?)null), QuotationAcceptAutopilot.Normalize(""));
        Assert.Equal(("ToInvoice", (string?)null), QuotationAcceptAutopilot.Normalize("toinvoice"));
    }

    [Fact]
    public void ลูกค้ายอมรับ_โหมดแปลง_แปลงเป็นใบแจ้งหนี้()
    {
        var d = QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt());
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Convert, d.Kind);
        Assert.Equal(DocumentType.Invoice, d.Target);
        // ใบก่อนรอบ 239 (ไม่มี hash) = ไม่รู้ ≠ ไม่ตรง ⇒ แปลงได้
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Convert, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(matches: null)).Kind);
    }

    [Fact]
    public void ทิศตรงข้าม_โหมดปิด_หรือยังไม่ยอมรับ_หรือไม่ใช่ใบเสนอราคา_ไม่ทำอะไร()
    {
        Assert.Equal(QuotationAcceptAutopilot.Outcome.NotApplicable, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.Off, Qt()).Kind);
        Assert.Equal(QuotationAcceptAutopilot.Outcome.NotApplicable, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(accepted: false)).Kind);
        Assert.Equal(QuotationAcceptAutopilot.Outcome.NotApplicable,
            QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(type: DocumentType.Invoice)).Kind);
    }

    [Fact]
    public void เนื้อหาเปลี่ยนหลังลูกค้ายอมรับ_หยุดพร้อมเหตุ_ไม่แปลง()
    {
        var d = QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(matches: false));
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Held, d.Kind);
        Assert.Equal(QuotationAcceptAutopilot.ContentChangedReason, d.Reason);
    }

    [Fact]
    public void มีใบลูกแล้ว_ไม่แปลงซ้ำ_ยกเลิกแล้ว_หยุด_ไม่จดVAT_ใบกำกับไม่ได้()
    {
        Assert.Equal(QuotationAcceptAutopilot.Outcome.AlreadyConverted, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(child: true)).Kind);
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Held, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(DocumentStatus.Voided)).Kind);
        var noVat = QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToTaxInvoice, Qt(vat: false));
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Held, noVat.Kind);
        Assert.Equal(QuotationAcceptAutopilot.NotVatRegisteredReason, noVat.Reason);
        // ทิศตรงข้าม: ไม่จด VAT แต่ตั้งใบแจ้งหนี้ ⇒ แปลงได้
        Assert.Equal(QuotationAcceptAutopilot.Outcome.Convert, QuotationAcceptAutopilot.Decide(QuotationAcceptMode.ToInvoice, Qt(vat: false)).Kind);
    }

    // ═══ AutopilotNotice — ล้ม = ทันที · สำเร็จ = สรุป 08:40 ═══

    [Fact]
    public void สรุปประจำวัน_ส่งเมื่อเลยเวลาไทยที่ตั้ง_วันละครั้ง()
    {
        var before = new DateTime(2026, 11, 6, 1, 30, 0, DateTimeKind.Utc);   // 08:30 ไทย
        var after = new DateTime(2026, 11, 6, 1, 45, 0, DateTimeKind.Utc);    // 08:45 ไทย
        Assert.False(AutopilotNotice.DigestDue(before, null, null));
        Assert.True(AutopilotNotice.DigestDue(after, null, null));
        Assert.False(AutopilotNotice.DigestDue(after, null, new DateTime(2026, 11, 6)));   // ส่งแล้ววันนี้
        Assert.True(AutopilotNotice.DigestDue(after, null, new DateTime(2026, 11, 5)));
        Assert.False(AutopilotNotice.DigestDue(after, "09:00", null));                      // เวลาที่บริษัทตั้ง
        Assert.True(AutopilotNotice.DigestDue(after, "bad", null));                         // อ่านไม่ออก = 08:40
    }

    [Fact]
    public void วันไทย_ข้ามเที่ยงคืนUTC_ถูกวัน()
    {
        Assert.Equal(new DateTime(2026, 11, 7), AutopilotNotice.BangkokDate(new DateTime(2026, 11, 6, 17, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 11, 6), AutopilotNotice.BangkokDate(new DateTime(2026, 11, 6, 16, 59, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void เวลาสรุปที่ตั้ง_อ่านไม่ออก_ปฏิเสธ_ไม่clampเงียบ()
    {
        Assert.Equal(("07:15", (string?)null), AutopilotNotice.NormalizeDigestTime("07:15"));
        Assert.Equal(((string?)null, (string?)null), AutopilotNotice.NormalizeDigestTime(" "));
        Assert.NotNull(AutopilotNotice.NormalizeDigestTime("25:99").Problem);
    }

    private static AutopilotNotice.DigestFacts Facts(int emailed = 0, int issues = 0, int waiting = 0)
        => new(emailed, 0, 0m, 0, 0, 0, waiting, issues, issues > 0 ? new[] { "ใบ X ติด" } : Array.Empty<string>());

    [Fact]
    public void สรุป_ไม่มีอะไร_ไม่ส่งอีเมลว่าง_มีเรื่องค้างอย่างเดียวก็ส่ง()
    {
        Assert.Null(AutopilotNotice.ComposeDigest(Day, Facts()));
        var onlyIssue = AutopilotNotice.ComposeDigest(Day, Facts(issues: 1));
        Assert.NotNull(onlyIssue);
        Assert.Contains("ติด 1 เรื่อง", onlyIssue!.Value.Title);
        Assert.Contains("ใบ X ติด", onlyIssue.Value.Message);
        var ok = AutopilotNotice.ComposeDigest(Day, Facts(emailed: 3));
        Assert.Contains("ส่งเอกสารถึงลูกค้า 3 ฉบับ", ok!.Value.Message);
        Assert.Contains("05/11/2026", ok.Value.Title);   // ค.ศ. ไม่ขึ้นกับ culture ของเครื่อง
        Assert.DoesNotContain("❗", ok.Value.Title);
    }

    [Fact]
    public void แค็ตตาล็อก_ทุกเหตุการณ์ที่แจ้งทันทีและต่อสายแล้ว_มีคีย์ในตารางแจ้งเตือน_และค่าตั้งต้นส่งอีเมลถึงเจ้าของ()
    {
        var keys = NotificationEvents.All.Select(e => e.Key).ToHashSet();
        foreach (var e in AutopilotNotice.Catalogue.Where(e => e.Wired && e.Delivery == AutopilotDelivery.Immediate))
            Assert.Contains(e.EventKey!, keys);
        foreach (var k in NotificationAudiencePolicy.EmailDefaultKeys)
        {
            Assert.Contains(k, keys);
            var row = Assert.Single(NotificationAudiencePolicy.DefaultMatrix(k, companyLineGroupConfigured: false));
            Assert.Equal("Owner", row.RecipientRole);
            Assert.True(row.EnableSystem);
            Assert.True(row.EnableEmail);
            Assert.False(row.EnableLineGroup);
            Assert.True(Assert.Single(NotificationAudiencePolicy.DefaultMatrix(k, companyLineGroupConfigured: true)).EnableLineGroup);
            Assert.Equal(NotificationAudience.TenantBusiness, NotificationAudiencePolicy.Classify(k));
        }
        // ทิศตรงข้าม: คีย์ที่ย้ายมาเดิมยังเป็นกระดิ่งอย่างเดียว (ไม่ถูกเปิดอีเมลตาม)
        Assert.False(Assert.Single(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.PaymentReceived, false)).EnableEmail);
        // ส่วน B ยังไม่มีผู้ส่ง ⇒ ไม่แต่งว่าส่งแล้ว
        Assert.All(AutopilotNotice.Catalogue.Where(e => e.Code is "E-09" or "E-10" or "E-07"), e => Assert.False(e.Wired));
    }

    [Fact]
    public void ข้อความแจ้งทันที_ไม่ว่าง_และไม่อ้างมาตรา()
    {
        var q = AutopilotNotice.QuotationBlocked("QT-001", "บจก.พี", QuotationAcceptAutopilot.ContentChangedReason);
        var s = AutopilotNotice.SlipSubmitted("INV-001", 10_400m, "ABC123456");
        var b = AutopilotNotice.BankSettleFailed(Day, 10_400m, "INV-001", "งวดปิดแล้ว");
        foreach (var (title, message) in new[] { q, s, b })
        {
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.DoesNotContain("มาตรา", message);
            Assert.DoesNotContain("§", message);
        }
        Assert.Contains("10,400.00", s.Message);
        Assert.Contains("ยังไม่ใช่การรับชำระ", s.Message);
        Assert.Contains("05/11/2026", b.Message);
    }

    // ═══ AutopilotStage — ใบนี้อยู่ขั้นไหน ═══

    private static AutopilotStage.DocFacts DF(DocumentStatus status = DocumentStatus.Sent, bool emailed = true, bool failed = false, decimal balance = 0m,
        bool anyPay = true, string? via = "Gateway", string? run = PostPaymentAutomationPolicy.StatusDone, string? msg = null, string? rec = "RT-1",
        bool bankWait = false)
        => new(status, emailed, failed, balance, anyPay, via, run, msg, rec, bankWait);

    [Fact]
    public void ชำระครบและส่งใบเสร็จแล้ว_จบ()
    {
        var r = AutopilotStage.Of(DF());
        Assert.Equal(AutopilotStage.OverallDone, r.Overall);
        Assert.Equal("RT-1", r.Steps.Single(s => s.Key == "receipt").Note);
        Assert.Equal("ออนไลน์", r.Steps.Single(s => s.Key == "paid").Note);
    }

    [Fact]
    public void งานหลังรับชำระหยุด_ติด_พร้อมเหตุ()
    {
        var r = AutopilotStage.Of(DF(run: PostPaymentAutomationPolicy.StatusBlocked, msg: "ผู้ซื้อขาดสาขา", rec: null));
        Assert.Equal(AutopilotStage.OverallStuck, r.Overall);
        Assert.Contains("ผู้ซื้อขาดสาขา", r.Problem);
    }

    [Fact]
    public void ร่าง_รออนุมัติ_กำลังเดิน_ไม่ใช่ติด()
    {
        var r = AutopilotStage.Of(DF(DocumentStatus.Draft, emailed: false, balance: 1_040m, anyPay: false, via: null, run: null, rec: null));
        Assert.Equal(AutopilotStage.OverallRunning, r.Overall);
        Assert.Equal(AutopilotStage.StepState.Waiting, r.Steps.Single(s => s.Key == "approved").State);
        Assert.Equal(AutopilotStage.StepState.NotApplicable, r.Steps.Single(s => s.Key == "sent").State);
    }

    [Fact]
    public void ส่งอีเมลล้ม_ติด_เงินโอนรอยืนยัน_รอ_ยกเลิก_ยกเลิก()
    {
        Assert.Equal(AutopilotStage.OverallStuck, AutopilotStage.Of(DF(emailed: false, failed: true, balance: 1_040m, anyPay: false, via: null, run: null, rec: null)).Overall);
        var wait = AutopilotStage.Of(DF(balance: 1_040m, anyPay: false, via: null, run: null, rec: null, bankWait: true));
        Assert.Equal("เงินโอนเข้ารอยืนยัน", wait.Steps.Single(s => s.Key == "paid").Note);
        Assert.Equal(AutopilotStage.OverallVoided, AutopilotStage.Of(DF(DocumentStatus.Voided)).Overall);
    }

    [Fact]
    public void ชำระครบ_ไม่ได้ตั้งออกใบเสร็จอัตโนมัติ_ถือว่าจบ_ไม่ใช่ติด()
    {
        var r = AutopilotStage.Of(DF(run: null, rec: null, via: "Bank"));
        Assert.Equal(AutopilotStage.OverallDone, r.Overall);
        Assert.Equal(AutopilotStage.StepState.NotApplicable, r.Steps.Single(s => s.Key == "receipt").State);
        Assert.Equal("เงินโอน (ระบบจับคู่)", r.Steps.Single(s => s.Key == "paid").Note);
    }

    // ═══ SlipEvidenceRule — สลิปเป็นข้อบ่งชี้ · กันซ้ำ ═══

    [Fact]
    public void สลิป_ไฟล์เดิมแฮชเดิม_เลขอ้างอิงปรับรูป_ตรงกับรายการเดินบัญชี()
    {
        var a = SlipEvidenceRule.Sha256Hex(new byte[] { 1, 2, 3 });
        Assert.Equal(a, SlipEvidenceRule.Sha256Hex(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(a, SlipEvidenceRule.Sha256Hex(new byte[] { 1, 2, 4 }));
        Assert.Equal(64, a.Length);
        Assert.Equal("ABC123456", SlipEvidenceRule.NormalizeRef(" abc-123 456 "));
        Assert.Null(SlipEvidenceRule.NormalizeRef("12-3"));        // สั้น = ไม่ใช้
        Assert.Null(SlipEvidenceRule.NormalizeRef("ABCDEFGHIJ"));  // ฝ่ายค้าน M6: ไม่มีตัวเลข = ไม่ใช้
        Assert.Null(SlipEvidenceRule.NormalizeRef("AB12345"));     // ฝ่ายค้าน M6: สั้นกว่า 8
        Assert.True(SlipEvidenceRule.RefMatchesLine("ABC123456", "abc-123 456"));        // ทั้งช่องเท่ากัน
        Assert.True(SlipEvidenceRule.RefMatchesLine("ABC123456", "TRF ABC123456 KBANK")); // คำในช่องเท่ากัน
        Assert.False(SlipEvidenceRule.RefMatchesLine("ABC123456", "XABC1234567"));       // ฝ่ายค้าน M6: ไม่ใช่ substring
        Assert.False(SlipEvidenceRule.RefMatchesLine("ABC123456", "REF 999"));
        Assert.False(SlipEvidenceRule.RefMatchesLine(null, "ABC123456"));
    }

    [Fact]
    public void สลิปซ้ำ_ปฏิเสธพร้อมทางไปต่อ_ไม่ซ้ำ_ผ่าน()
    {
        Assert.NotNull(SlipEvidenceRule.DuplicateProblem(true, false));
        Assert.NotNull(SlipEvidenceRule.DuplicateProblem(false, true));
        Assert.Null(SlipEvidenceRule.DuplicateProblem(false, false));
    }
}
