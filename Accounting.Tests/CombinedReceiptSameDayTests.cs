using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ใบกำกับ "ยกหัวเป็นใบเสร็จรับเงินในตัว" ได้เฉพาะ<b>รับเงินวันเดียวกับวันที่บนใบ</b>
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-10) ═══
/// กด "บันทึกชำระเงิน" บนใบ <c>TIV-20260805-0005</c> (ลงวันที่ 5 ส.ค. 2569) โดยเงินเข้า
/// จริงคนละวัน แล้ว<b>ใบเดิม</b>เปลี่ยนป้าย/หัวเป็น "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน"
/// ⇒ กระดาษที่ลงวันที่ 5 ส.ค. ประกาศว่ารับเงินวันที่ 5 ส.ค. = <b>ใบรับลงวันที่เท็จ</b>
/// (ป.รัษฎากร ม.105 ใบรับต้องออกทันทีที่รับเงินและลงวันที่ที่รับจริง) และไม่ตรงกับ
/// 50 ทวิ ที่ผู้จ่ายออกตามวันจ่ายจริง
///
/// สิ่งที่ผู้ใช้บอกว่าควรเป็น (และเป็นสิ่งที่แก้): "ทำหน้าที่เหมือนแปลงเอกสารเป็น
/// ใบเสร็จรับเงิน" = ออก REC <b>แยก</b> ลงวันที่รับเงิน
///
/// <para><b>เทสต์ล็อกสองทิศ</b> ตามกฎเหล็ก #4 H — ทิศที่พัง (คนละวัน ⇒ ต้องไม่ยกหัว
/// และต้องออกใบเสร็จแยก) <b>และ</b> ทิศที่เคยถูกอยู่แล้ว (ขายสดวันเดียวกัน ⇒ ยังยกหัว
/// ใบเดียวจบ ไม่มีกระดาษเกิน). เทสต์ที่มีแต่ทิศแรกผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดด่านทิ้ง"</para>
/// </summary>
public class CombinedReceiptSameDayTests
{
    private static readonly DateTime IssuedOn = new(2026, 8, 5);

    // ── ตัวกติกาจริง (pure) ──────────────────────────────────────────────
    [Fact]
    public void ขายสดรับเงินวันเดียวกัน_ยกหัวเป็นใบเสร็จได้()
    {
        Assert.True(ReceiptIssuePolicy.SettledSameDay(IssuedOn, IssuedOn));
        // เวลาในวันไม่เกี่ยว — เทียบเฉพาะวัน (payment ที่บันทึกตอนบ่ายยังเป็นวันเดียวกัน)
        Assert.True(ReceiptIssuePolicy.SettledSameDay(IssuedOn, IssuedOn.AddHours(15)));
        Assert.Null(ReceiptIssuePolicy.WhyNotCombined(ReceiptIssueMode.Combined, IssuedOn, IssuedOn));
    }

    [Fact]
    public void รับเงินคนละวัน_ห้ามยกหัว_และต้องบอกเหตุผลที่เอาไปโชว์ได้()
    {
        var paidOn = IssuedOn.AddDays(9);                 // เคสจริง: ใบ 5 ส.ค. เงินเข้าภายหลัง
        Assert.False(ReceiptIssuePolicy.SettledSameDay(IssuedOn, paidOn));
        var why = ReceiptIssuePolicy.WhyNotCombined(ReceiptIssueMode.Combined, IssuedOn, paidOn);
        Assert.NotNull(why);
        Assert.Contains("ม.105", why!);
        Assert.Contains("ใบเสร็จรับเงินแยก", why!);
    }

    [Fact]
    public void แม้รับเงินก่อนวันที่บนใบ_ก็ยังคนละวัน()
    {
        // จ่ายล่วงหน้าแล้วออกใบทีหลัง — ใบเสร็จต้องลงวันที่รับเงิน ไม่ใช่วันที่ใบ
        Assert.False(ReceiptIssuePolicy.SettledSameDay(IssuedOn, IssuedOn.AddDays(-1)));
    }

    [Fact]
    public void ไม่รู้วันรับเงิน_ต้องไม่ยกหัว_และบอกว่าไม่รู้()
    {
        // ยอดถูกปิดด้วยการหักใบมัดจำ / ข้อมูลก่อนย้ายระบบ — "ไม่รู้ = บอกว่าไม่รู้"
        // การพิมพ์ใบเสร็จเกินอันตรายกว่าการไม่พิมพ์
        Assert.False(ReceiptIssuePolicy.SettledSameDay(IssuedOn, null));
        var why = ReceiptIssuePolicy.WhyNotCombined(ReceiptIssueMode.Combined, IssuedOn, null);
        Assert.NotNull(why);
        Assert.Contains("มัดจำ", why!);
    }

    [Fact]
    public void โหมดแยกใบชนะเสมอ_แม้รับเงินวันเดียวกัน()
    {
        var why = ReceiptIssuePolicy.WhyNotCombined(ReceiptIssueMode.SeparateReceipt, IssuedOn, IssuedOn);
        Assert.NotNull(why);
        Assert.Equal(ReceiptIssuePolicy.Describe(ReceiptIssueMode.SeparateReceipt), why);
    }

    [Fact]
    public void วันที่ในข้อความต้องเป็น_ค_ศ_เสมอ_แม้_process_ตั้ง_culture_ไทย()
    {
        // DateTime.ToString("dd/MM/yyyy") บน th-TH ใช้ปฏิทินพุทธ ⇒ 2026 กลายเป็น 2569
        // เงียบ ๆ ในข้อความที่ไม่ใช่แบบยื่นภาษี (บทเรียนเดิมของเรพนี้)
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var why = ReceiptIssuePolicy.WhyNotCombined(
                ReceiptIssueMode.Combined, IssuedOn, IssuedOn.AddDays(9));
            Assert.Contains("05/08/2026", why!);
            Assert.Contains("14/08/2026", why!);
            Assert.DoesNotContain("2569", why!);
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // ── mirror ของ DocumentService.ComputeServedAsReceipt (internal) ─────
    private static bool Served(string status, bool paidOnIssue, decimal balanceDue,
        decimal paidAmount, bool hasSeparateReceipt, DateTime? settledOn,
        ReceiptIssueMode mode = ReceiptIssueMode.Combined)
    {
        if (!ReceiptIssuePolicy.AllowsCombinedReceiptHeader(mode)) return false;
        if (status == "Draft") return paidOnIssue && !hasSeparateReceipt;
        if (balanceDue > 0.01m || paidAmount <= 0.005m) return false;
        if (status is "Voided" or "Rejected" or "WaitingApproval") return false;
        if (!ReceiptIssuePolicy.SettledSameDay(IssuedOn, settledOn)) return false;
        return !hasSeparateReceipt;
    }

    [Fact]
    public void ใบที่พัง_รับเงินคนละวัน_ป้ายต้องกลับเป็นใบกำกับภาษีเฉย_ๆ()
    {
        // TIV-20260805-0005: 111,800 ชำระครบแล้ว แต่เงินเข้าคนละวัน
        Assert.False(Served("Paid", false, 0m, 111_800m, false, IssuedOn.AddDays(9)));
    }

    [Fact]
    public void ทิศตรงข้าม_ขายสดวันเดียวกัน_ต้องยังยกหัวเหมือนเดิม()
    {
        Assert.True(Served("Paid", false, 0m, 111_800m, false, IssuedOn));
        // ใบร่างที่เลือกโหมด "รับเงินครบแล้ว" ยังพิมพ์ตัวอย่างหัวรวมตามเจตนา
        // (ยังไม่มีรายการรับชำระให้เทียบวัน — Draft ไม่ผ่านด่านวันที่)
        Assert.True(Served("Draft", true, 111_800m, 0m, false, null));
    }

    [Fact]
    public void มีใบเสร็จแยกแล้ว_ต้องไม่ยกหัวซ้ำ_แม้วันเดียวกัน()
        => Assert.False(Served("Paid", false, 0m, 111_800m, hasSeparateReceipt: true, settledOn: IssuedOn));

    // ── mirror ของด่านใน CreatePaymentAsync (ออกใบเสร็จแยกหรือไม่) ───────
    private static bool CombinedSelfReceipt(ReceiptIssueMode mode, DateTime? paidOn,
        bool combinedInvoiceTaxInvoice, bool singleShotFull)
        => ReceiptIssuePolicy.WhyNotCombined(mode, IssuedOn, paidOn) == null
            && combinedInvoiceTaxInvoice && singleShotFull;

    [Fact]
    public void รับเงินคนละวัน_ต้องออกใบเสร็จแยก_เหมือนการแปลงเอกสาร()
    {
        // combinedSelfReceipt = false ⇒ wantReceipt เดินต่อ ⇒ CreateSettlementReceiptAsync
        // สร้าง REC ลงวันที่ payment.PaymentDate (เหมือนแปลงใบกำกับ → ใบเสร็จรับเงิน)
        Assert.False(CombinedSelfReceipt(ReceiptIssueMode.Combined, IssuedOn.AddDays(9), true, true));
    }

    [Fact]
    public void ทิศตรงข้าม_ขายสดใบเดียวจบ_ต้องไม่มีกระดาษเกิน()
    {
        // ฟอร์มขายสดส่ง paymentDate = วันที่บนใบ → ยังเป็นใบเดียวจบเหมือนเดิม
        Assert.True(CombinedSelfReceipt(ReceiptIssueMode.Combined, IssuedOn, true, true));
        // ผ่อนหลายงวด → ออกใบเสร็จต่องวดตามเดิม (ไม่เกี่ยวกับวันที่)
        Assert.False(CombinedSelfReceipt(ReceiptIssueMode.Combined, IssuedOn, true, singleShotFull: false));
    }

    // ── ปุ่ม "ออกใบเสร็จ" บนประวัติการชำระ (ผู้ใช้รายงาน 2026-09-11) ──────
    // TIV-20260805-0007 หัวเป็น "ใบกำกับภาษี/ใบเสร็จรับเงิน" อยู่แล้ว แต่ปุ่มยังโผล่
    // ⇒ กดแล้วได้กระดาษใบรับใบที่สองของเงินก้อนเดิม
    [Fact]
    public void เคสจริง_รับเงินวันเดียวกับวันที่ใบ_ถือว่าใบต้นทางเป็นใบรับของงวดนั้นแล้ว()
        => Assert.True(ReceiptIssuePolicy.CoversPayment(
            documentServesAsReceipt: true, IssuedOn, IssuedOn));

    [Fact]
    public void งวดที่รับเงินคนละวัน_ยังต้องออกใบเสร็จได้_แม้ใบจะยกหัวแล้ว()
        // ใบผ่อน 2 งวด: งวดแรก 27 ก.ค. · งวดปิด 5 ส.ค. (= วันที่บนใบ) ⇒ ธงระดับ
        // เอกสารเป็นจริง แต่งวดแรกยังไม่มีกระดาษใบรับ — ห้ามปิดปุ่มทั้งแถว
        => Assert.False(ReceiptIssuePolicy.CoversPayment(
            documentServesAsReceipt: true, IssuedOn, IssuedOn.AddDays(-9)));

    [Fact]
    public void ใบที่ไม่ได้ยกหัวเป็นใบเสร็จ_ไม่คุมงวดไหนเลย_แม้วันตรงกัน()
        => Assert.False(ReceiptIssuePolicy.CoversPayment(
            documentServesAsReceipt: false, IssuedOn, IssuedOn));

    [Fact]
    public void เทียบเฉพาะวัน_ไม่เอาเวลามาตัดสิน()
        // PaymentDate มักมีส่วนเวลาติดมาจากฟอร์ม/DB — ห้ามทำให้ "วันเดียวกัน" กลายเป็นเท็จ
        => Assert.True(ReceiptIssuePolicy.CoversPayment(
            documentServesAsReceipt: true, IssuedOn, IssuedOn.AddHours(15).AddMinutes(42)));
}
