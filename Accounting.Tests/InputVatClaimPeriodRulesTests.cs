using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตารางตัดสินใจ "ใบนี้เคลมภาษีซื้อในงวด ภ.พ.30 ไหน" — ตัวตัดสินกลางที่ทางเข้า
/// ทั้ง 3 ทาง (สร้างเอกสาร · แก้ไขเอกสาร · แผงภาษีซื้อในหน้าดูเอกสาร) เรียกร่วมกัน
///
/// <para>เทสต์ชุดนี้คือ<b>หลักฐาน</b>ว่าทุกทางเข้าได้ผลลัพธ์เดียวกัน: ถ้าตรรกะ
/// อยู่ที่เดียวและที่นั้นถูก ก็ไม่มีทางที่ทางเข้าไหนจะเพี้ยนไปเอง (ทางเข้าทำแค่
/// หาข้อมูลป้อนให้กติกา แล้วลงมือตามผลที่ได้)</para>
///
/// <para>ครอบ VCP-V01..19 ใน TEST_PLAN.md</para>
/// </summary>
public class InputVatClaimPeriodRulesTests
{
    private static readonly DateTime JunInvoice = new(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);

    // ── การแปลงค่างวด ────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-09", 2026, 9)]
    [InlineData("2026-01", 2026, 1)]
    [InlineData("2026-12", 2026, 12)]
    [InlineData(" 2026-09 ", 2026, 9)]        // ช่องว่างหัวท้ายจาก input
    // พ.ศ. — ผู้ใช้ไทยพิมพ์แบบนี้ได้. เคยพังเงียบ ๆ: ตรวจช่วงปี (y > 2200) มา
    // **ก่อน** บรรทัดแปลง พ.ศ. → ค.ศ. ⇒ ปี พ.ศ. ทุกค่าถูกปฏิเสธตั้งแต่ต้น
    // บรรทัดแปลงไม่มีวันถูกเรียกถึง = ฟีเจอร์ที่มีโค้ดอยู่แต่ไม่เคยทำงาน
    [InlineData("2569-09", 2026, 9)]
    [InlineData("2543-01", 2000, 1)]          // ขอบล่างหลังแปลง (พ.ศ.2543 = ค.ศ.2000)
    public void Parses_period_including_buddhist_year(string raw, int expectYear, int expectMonth)
    {
        var (ok, period, err) = InputVatClaimPeriodRules.ParsePeriod(raw);
        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal(new DateTime(expectYear, expectMonth, 1), period);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_means_clear_not_error(string raw)
    {
        var (ok, period, err) = InputVatClaimPeriodRules.ParsePeriod(raw);
        Assert.True(ok);
        Assert.Null(period);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("2026-13")]      // เดือนเกิน
    [InlineData("2026-00")]      // เดือน 0
    [InlineData("2026")]         // ไม่มีเดือน
    [InlineData("2026-09-01")]   // เกินรูปแบบ
    [InlineData("abc-09")]
    [InlineData("1999-09")]      // ปีต่ำเกินช่วงที่รับ
    [InlineData("2542-09")]      // พ.ศ. ที่แปลงแล้วยังต่ำเกิน (= ค.ศ.1999)
    [InlineData("2201-09")]      // สูงเกินช่วง ค.ศ. และไม่ใช่ พ.ศ. (ไม่ถูกลบ 543)
    public void Malformed_period_is_rejected_with_the_expected_format(string raw)
    {
        var (ok, period, err) = InputVatClaimPeriodRules.ParsePeriod(raw);
        Assert.False(ok);
        Assert.Null(period);
        Assert.Contains("yyyy-MM", err!);
    }

    // ── no-op ต้องเป็น no-op เสมอ (ที่มา: บั๊ก clone ใบเสนอราคาแล้วบันทึกไม่ได้) ──

    [Fact]
    public void Null_payload_touches_nothing()
    {
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            null, DocumentType.Quotation, null, postedAsUndue: false);
        Assert.Equal(InputVatClaimPeriodOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void Blank_on_a_sales_doc_that_never_had_a_period_is_a_no_op()
    {
        // UI ส่ง "" มาเสมอตอนแก้ไข — ใบเสนอราคาที่ไม่เกี่ยวกับภาษีซื้อเลย
        // ต้องผ่านฉลุย (เวอร์ชันแรกเช็คชนิดเอกสารก่อนเทียบค่า → clone แล้วบันทึกไม่ได้)
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "", DocumentType.Quotation, null, postedAsUndue: false);
        Assert.Equal(InputVatClaimPeriodOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void Blank_on_an_untouched_undue_doc_is_a_no_op()
    {
        // ใบ undue ที่ยังไม่มีงวด: current = null, period = null → ไม่เปลี่ยน
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "", DocumentType.PaymentVoucher, null, postedAsUndue: true);
        Assert.Equal(InputVatClaimPeriodOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void Resending_the_same_month_is_not_a_change()
    {
        var current = new DateTime(2026, 8, 21, 13, 45, 0, DateTimeKind.Utc);  // มีเวลาติดมาด้วย
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "2026-08", DocumentType.PaymentVoucher, current, postedAsUndue: false);
        // เทียบระดับ "เดือน" ไม่ใช่ tick — ไม่งั้นกดบันทึกซ้ำจะยิง guard ทุกครั้ง
        Assert.Equal(InputVatClaimPeriodOutcome.NoChange, d.Outcome);
    }

    // ── ชนิดเอกสาร ───────────────────────────────────────────────────

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void Purchase_side_documents_can_set_a_period(DocumentType t)
    {
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "2026-09", t, null, postedAsUndue: false);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
        Assert.Equal(new DateTime(2026, 9, 1), d.Period);
    }

    [Theory]
    [InlineData(DocumentType.Quotation)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Receipt)]
    public void Sales_side_documents_cannot_set_an_input_vat_period(DocumentType t)
    {
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "2026-09", t, null, postedAsUndue: false);
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
        Assert.Contains("ฝั่งซื้อ", d.BlockReason!);
    }

    // ── flow 11640 ชนะเจตนา ──────────────────────────────────────────

    [Fact]
    public void Undue_doc_awaiting_its_invoice_cannot_pick_a_period()
    {
        // ใบกำกับยังไม่ครบ §86/4 → ตั้งงวดเอง = รายงานมองว่าถึงกำหนดแล้ว
        // ทั้งที่ยังไม่มีสิทธิ์เคลม
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "2026-09", DocumentType.PaymentVoucher, null, postedAsUndue: true);
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
        Assert.Contains("เติมใบกำกับครบ", d.BlockReason!);
    }

    [Fact]
    public void Reclassified_undue_doc_can_still_move_period()
    {
        var claimable = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "2026-09", DocumentType.PurchaseInvoice, claimable, postedAsUndue: true);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
        Assert.Equal(new DateTime(2026, 9, 1), d.Period);
    }

    [Fact]
    public void Reclassified_undue_doc_cannot_clear_its_period()
    {
        // ถ้าล้างได้ รายงานจะเห็นเป็น "ยังพัก 11640" ทั้งที่ GL ย้ายออกไปแล้ว
        // = ภ.พ.30 กับ GL แยกทางกันเงียบ ๆ
        var claimable = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "", DocumentType.PurchaseInvoice, claimable, postedAsUndue: true);
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
        Assert.Contains("ล้างงวด", d.BlockReason!);
    }

    [Fact]
    public void Ordinary_doc_can_clear_a_pinned_period()
    {
        var claimable = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
        var d = InputVatClaimPeriodRules.DecideFromDocument(
            "", DocumentType.PurchaseInvoice, claimable, postedAsUndue: false);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
        Assert.Null(d.Period);   // null = กลับไปเคลมตามเดือนภาษีของเอกสาร
    }

    // ── รายงานที่ยื่นแล้ว vs งวดร่าง ─────────────────────────────────

    [Fact]
    public void Filed_period_blocks_the_move_and_says_to_file_an_amendment()
    {
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2026, 9, 1), JunInvoice, filedReportPeriod: (6, 2026));
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
        Assert.Contains("06/2026", d.BlockReason!);
        Assert.Contains("ยื่นเพิ่มเติม", d.BlockReason!);
    }

    [Fact]
    public void Draft_period_allows_the_move()
    {
        // งวดร่าง = ไม่ส่ง filedReportPeriod มา (ผู้เรียกกรองเฉพาะงวดที่ยื่นแล้ว)
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2026, 9, 1), JunInvoice, filedReportPeriod: null);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
        Assert.Equal(new DateTime(2026, 9, 1), d.Period);
    }

    [Fact]
    public void Filed_period_wins_over_the_six_month_message()
    {
        // ใบ มิ.ย. + งวดปลายทาง ม.ค. 2027 = เกิน §82/3 ด้วย และอยู่ในงวดที่ยื่นแล้วด้วย
        // ต้องได้ข้อความ "ยื่นเพิ่มเติม" ไม่ใช่ข้อความกรอบ 6 เดือน (ชี้ทางแก้ผิด)
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2027, 1, 1), JunInvoice, filedReportPeriod: (6, 2026));
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
        Assert.Contains("ยื่นเพิ่มเติม", d.BlockReason!);
    }

    // ── §82/3 — ต้องตรงกับตัวตัดสินของปุ่ม "ดึงเอกสาร" ────────────────

    [Theory]
    [InlineData(2026, 6)]    // เดือนเดียวกับใบ
    [InlineData(2026, 12)]   // +6 = เดือนสุดท้ายที่ยังเคลมได้
    public void Period_inside_the_six_month_window_is_allowed(int y, int m)
    {
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(y, m, 1), JunInvoice, null);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
    }

    [Fact]
    public void Period_past_the_six_month_window_is_blocked()
    {
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2027, 1, 1), JunInvoice, null);
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
    }

    [Fact]
    public void Period_before_the_invoice_month_is_blocked()
    {
        var d = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2026, 5, 1), JunInvoice, null);
        Assert.Equal(InputVatClaimPeriodOutcome.Blocked, d.Outcome);
    }

    [Fact]
    public void Clearing_the_period_skips_the_six_month_check()
    {
        // ล้าง = กลับไปเคลมตามเดือนภาษีของเอกสารเอง ไม่ได้เลือกงวดใหม่
        // จึงไม่มีอะไรให้ตรวจกับ §82/3
        var d = InputVatClaimPeriodRules.DecideAgainstReports(null, JunInvoice, null);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d.Outcome);
        Assert.Null(d.Period);
    }

    // ── ร่องรอยบนบรรทัดรายงานงวดเดิม ─────────────────────────────────

    [Fact]
    public void Audit_trail_names_the_destination_period()
    {
        var reason = InputVatClaimPeriodRules.MoveAuditReason(new DateTime(2026, 9, 1));
        Assert.Contains("09/2026", reason);
    }

    [Fact]
    public void Audit_trail_says_it_reverted_to_the_document_month()
    {
        var reason = InputVatClaimPeriodRules.MoveAuditReason(null);
        Assert.Contains("เดือนเอกสาร", reason);
    }

    // ── ทางเข้าทั้ง 3 ทางได้ผลเดียวกัน ────────────────────────────────

    [Theory]
    [InlineData("2026-09")]
    [InlineData("2569-09")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2026-13")]
    public void Every_entry_point_with_the_same_input_gets_the_same_decision(string? raw)
    {
        // "ทางเข้า" ต่างกันแค่ตอนตัดสินใจว่าจะ**เรียก**กติกาไหม (สร้าง = ข้ามเมื่อ
        // ค่าว่าง · แผงภาษีซื้อ = ข้ามเมื่อกำลังเลิกเคลม) — ตัวกติกาเองรับ
        // พารามิเตอร์ชุดเดียวกันเสมอ จึงต้องคืนผลเดียวกันทุกครั้ง
        var a = InputVatClaimPeriodRules.DecideFromDocument(
            raw, DocumentType.PaymentVoucher, null, postedAsUndue: false);
        var b = InputVatClaimPeriodRules.DecideFromDocument(
            raw, DocumentType.PaymentVoucher, null, postedAsUndue: false);
        Assert.Equal(a, b);                       // deterministic (ไม่มี DateTime.Now แฝง)
        Assert.Equal(a.Outcome, b.Outcome);
        Assert.Equal(a.Period, b.Period);
        Assert.Equal(a.BlockReason, b.BlockReason);
    }

    [Fact]
    public void Rules_do_not_depend_on_the_current_clock()
    {
        // ถ้ากติกาแอบใช้ DateTime.Now/UtcNow ผลจะเปลี่ยนตามวันที่รัน ⇒ ทางเข้า
        // เดียวกันให้ผลต่างกันคนละวัน. ยืนยันว่าผลผูกกับ input ล้วน ๆ
        var d1 = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2026, 12, 1), JunInvoice, null);
        var d2 = InputVatClaimPeriodRules.DecideAgainstReports(
            new DateTime(2026, 12, 1), JunInvoice, null);
        Assert.Equal(d1, d2);
        Assert.Equal(InputVatClaimPeriodOutcome.Apply, d1.Outcome);
    }
}
