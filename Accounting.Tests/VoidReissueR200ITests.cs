using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม V1I — แก้ผลตรวจฝ่ายค้านของงานทีม V1H (V1H-O1 · O2 · O5 · O7 · O3/O6 ล็อกด้วย checker) ·
/// ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ (F2 ข้อ 8) · ตัวเลขชุดเดียวกับ V1G/V1H (INV-1 1,000 + VAT 70 = 1,070 ·
/// เช็ค P1 เด้ง · เงินสด P2 1,070 · RC-1 ตอบรับที่กรมสรรพากรแล้ว) · จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py
/// (ทั้งเส้นใบเดียว/หลายใบเรียกตัวติดธงกลับ · VoidAsync ใช้สถานะรวม e-Tax by Email + เวลาส่ง · VoidPaymentAsync ล็อกเอกสารก่อนแถว Payment)
/// และด่านไฟล์แนบใต้เงื่อนไข "มีไฟล์" ล็อกด้วย tools/attachment_gate_check.py
/// </summary>
public class VoidReissueR200ITests
{
    private static readonly DateTime Feb10 = new(2026, 2, 10, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb12 = new(2026, 2, 12, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>หมายเหตุภายในของ RC-1 หลังปิดธงทาง (ค) — รูปเดียวกับที่ ResolveEtaxCancellationAsync เขียน</summary>
    private static readonly string KeptNotes =
        $"{EtaxReissueReview.KeptOriginalMarker} — ผู้ใช้ยืนยันว่าใบกำกับเดิมยังใช้ได้ · ยอดรับชำระที่มีผล 1,070.00 — ลูกค้าชำระเงินสดแทนเช็คที่เด้ง";

    // ═════════════ V1H-O1: ทาง (ค) แล้วการรับชำระใหม่ถูกยกเลิก ⇒ ติดธงกลับ (เดิมจบเงียบ) ═════════════

    [Fact]
    public void R200_V1I_O1_ลำดับ_ทางค_แล้วยกเลิกการรับชำระใหม่_ยอดไม่ครอบ_ติดธงกลับพร้อมทางไปต่อ_ภาษีไม่ถูกถอยเงียบ()
    {
        // 1) RC-1 ปิดธงด้วยทาง (ค) แล้ว — ป้ายครั้งล่าสุดคือทาง (ค)
        Assert.True(EtaxReissueReview.LastResolutionKeptOriginal(KeptNotes));
        // 2) ยกเลิก P2 (เงินสด 1,070) — ยอดที่ยังมีผล (ไม่นับ P2) = 0 ⇒ ต้องติดธงกลับ
        var flag = DocumentVoidPreconditions.KeptOriginalCoverageLost(lastResolutionKeptOriginal: true, flagged: false,
            receiptVatAmount: 70m, receiptTotalAmount: 1070m, livePaymentCoverage: 0m, "RC-1", "PAY-2");
        Assert.NotNull(flag);
        Assert.StartsWith("ต้องยกเลิกทาง e-Tax", flag);
        Assert.Contains("RC-1", flag);
        Assert.Contains("PAY-2", flag);
        Assert.Contains("0.00", flag);
        Assert.Contains("1,070.00", flag);
        Assert.Contains("ภ.พ.30", flag);
        Assert.Contains("ใบกำกับเดิมยังใช้ได้", flag);              // ทางไปต่อ: รับชำระใหม่ให้ครบแล้วยืนยันอีกครั้ง
        Assert.Contains("ออกใบลดหนี้", flag);
        // 3) ใบกำกับยังมีผล ⇒ ตัวถอยภาษีไม่ถอย (พฤติกรรมเดิมถูก — ที่ผิดคือไม่มีธง)
        Assert.False(DocumentVoidPreconditions.ShouldUndoOutputVatReclass(0m, outputVatDue: true, liveVatReceiptKeepsTaxPoint: true));
        // 4) ติดธงแล้วปิดใหม่ทาง (ค) ไม่ได้จนกว่ายอดที่มีผลจะครอบอีกครั้ง (ตัวตัดสินเดิม)
        var reresolve = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.OriginalStillValid, "ลูกค้าโอนใหม่", null, false, null, 70m, 0m,
            ReceiptTotalAmount: 1070m, LivePaymentCoverage: 0m));
        Assert.False(reresolve.Allowed);
        Assert.Contains("ยังไม่ครอบยอดใบเสร็จ", reresolve.Reason);
    }

    [Fact]
    public void R200_V1I_O1_ยกเลิกบางส่วน_ยอดที่ยังมีผลไม่ครอบ_ก็ติดธงกลับ()
    {
        // P2 600 + P3 470 ครอบ 1,070 ตอนปิดธง — ยกเลิก P3 ⇒ เหลือ 600 < 1,070
        var flag = DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m, 600m, "RC-1", "PAY-3");
        Assert.NotNull(flag);
        Assert.Contains("600.00", flag);
    }

    [Theory]
    [InlineData("covered")]        // ยกเลิกการรับชำระอื่น แต่ยอดที่เหลือยังครอบ (P3 ใหม่ 1,070)
    [InlineData("exact")]          // ครอบพอดี (ปัดเศษ 0.005)
    [InlineData("flagged")]        // ติดธงอยู่แล้ว — ไม่ประทับซ้ำ
    [InlineData("novat")]          // ใบรับไม่ถือภาษี
    [InlineData("notkept")]        // ปิดธงครั้งล่าสุดไม่ใช่ทาง (ค)
    public void R200_V1I_O1_ทิศตรงข้าม_ยอดยังครอบหรือไม่ใช่ทางค_ไม่แตะ(string kind)
    {
        var flag = kind switch
        {
            "covered" => DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m, 1070m, "RC-1", "PAY-9"),
            "exact" => DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m, 1069.996m, "RC-1", "PAY-9"),
            "flagged" => DocumentVoidPreconditions.KeptOriginalCoverageLost(true, true, 70m, 1070m, 0m, "RC-1", "PAY-9"),
            "novat" => DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 0m, 1070m, 0m, "RC-1", "PAY-9"),
            _ => DocumentVoidPreconditions.KeptOriginalCoverageLost(false, false, 70m, 1070m, 0m, "RC-1", "PAY-9"),
        };
        Assert.Null(flag);
    }

    [Fact]
    public void R200_V1I_O1_ป้ายทางค_ฝั่งเขียนฝั่งอ่านตัวเดียว_ปิดซ้ำด้วยทางอื่นไม่นับ()
    {
        // รูปที่ V1H เขียนลงฐานไว้แล้ว ($"{ResolvedMarker} ใบกำกับเดิมยังใช้ได้ — …") ต้องยังอ่านได้ — ไม่ต้อง migration
        Assert.Equal("[ETAX-CANCEL-RESOLVED] ใบกำกับเดิมยังใช้ได้", EtaxReissueReview.KeptOriginalMarker);
        Assert.True(EtaxReissueReview.LastResolutionKeptOriginal($"{EtaxReissueReview.ResolvedMarker} ใบกำกับเดิมยังใช้ได้ — x — y"));
        // ทาง (ค) แล้วภายหลังปิดด้วยใบลดหนี้ (ทาง ข) ⇒ ไม่ใช่ทาง (ค) แล้ว
        var thenCreditNote = KeptNotes + "\n\n" + $"{EtaxReissueReview.ResolvedMarker} ปิดธงด้วยใบลดหนี้ CN-1 — ใบลดหนี้ในระบบนี้ — ลดหนี้";
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal(thenCreditNote));
        // ใบลดหนี้ก่อน แล้วทาง (ค) ครั้งล่าสุด ⇒ นับ
        Assert.True(EtaxReissueReview.LastResolutionKeptOriginal(thenCreditNote + "\n\n" + KeptNotes));
        // ไม่มีป้าย / ทาง (ก) / ว่าง
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal(null));
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal(""));
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal("[VAT-UNDO-BLOCKED] เช็คเด้ง"));
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal($"{EtaxReissueReview.ResolvedMarker} ยกเลิกทาง e-Tax แล้ว — อ้างอิง X"));
        // ป้ายถูกตัดท้าย (หมายเหตุสั้นกว่าป้ายเต็ม) ⇒ ไม่นับ ไม่ throw
        Assert.False(EtaxReissueReview.LastResolutionKeptOriginal(EtaxReissueReview.ResolvedMarker + " ใบกำกับ"));
    }

    // ═════════════ V1H-O2: แถว Signed ที่ส่ง e-Tax by Email ประทับเวลาแล้ว = ถึงกรมสรรพากร ═════════════

    [Fact]
    public void R200_V1I_O2_แถวลงนามแล้วแต่ส่งอีเมลประทับเวลาแล้ว_ยกเลิกในระบบไม่ได้_ไม่เขียนว่าก่อนส่งถึงกรมสรรพากร()
    {
        var status = EtaxVoidPolicy.StatusForVoid(EtaxStatus.Signed, documentSentByEmailWithRdTimestamp: true);
        Assert.Equal(EtaxStatus.Accepted, status);
        var v = EtaxVoidPolicy.Decide(status, "ลูกค้าขอยกเลิก", evidenceFileAttached: true);
        Assert.False(v.Allowed);
        Assert.Contains("e-Tax by Email", v.Reason);
        Assert.Contains("ใบลดหนี้", v.Reason);                   // ทางไปต่อ
        Assert.Null(v.EvidenceLabel);                             // ไม่มีป้าย "ก่อนส่งถึงกรมสรรพากร" ลง audit
    }

    [Theory]
    [InlineData(EtaxStatus.Generated, false, EtaxStatus.Generated)]
    [InlineData(EtaxStatus.Signed, false, EtaxStatus.Signed)]
    [InlineData(EtaxStatus.Error, false, EtaxStatus.Error)]
    [InlineData(EtaxStatus.Submitted, false, EtaxStatus.Submitted)]
    [InlineData(EtaxStatus.Accepted, false, EtaxStatus.Accepted)]
    [InlineData(EtaxStatus.Voided, true, EtaxStatus.Voided)]       // ยกเลิกแล้วคงเป็นยกเลิก (ข้อความ "ไปแล้ว")
    public void R200_V1I_O2_ทิศตรงข้าม_ไม่มีอีเมลประทับเวลา_ใช้สถานะแถวเดิม(EtaxStatus row, bool emailed, EtaxStatus expect)
    {
        Assert.Equal(expect, EtaxVoidPolicy.StatusForVoid(row, emailed));
        if (row is EtaxStatus.Generated or EtaxStatus.Signed or EtaxStatus.Error)
        {
            var v = EtaxVoidPolicy.Decide(EtaxVoidPolicy.StatusForVoid(row, emailed), null, evidenceFileAttached: false);
            Assert.True(v.Allowed);                              // ยังยกเลิกได้โดยไม่ต้องแนบ (พฤติกรรมเดิม)
            Assert.Contains("ก่อนส่งถึงกรมสรรพากร", v.EvidenceLabel);
        }
    }

    // ═════════════ V1H-O5: ไฟล์หลักฐานต้องแนบหลังส่ง e-Tax ═════════════

    [Fact]
    public void R200_V1I_O5_หลักฐานต้องแนบหลังวันส่ง_ไฟล์ก่อนส่งไม่นับ_ข้อความบอกเหตุ()
    {
        var cutoff = EtaxVoidPolicy.EvidenceNotBefore(Feb10, rowCreatedAt: Feb10.AddDays(-1));
        Assert.Equal(Feb10, cutoff);
        var pdfBeforeSubmit = Feb10.AddHours(-2);       // PDF ใบเสร็จต้นฉบับที่แนบก่อนส่ง
        Assert.False(pdfBeforeSubmit > cutoff);
        Assert.True(Feb12 > cutoff);                    // ไฟล์ตอบกลับการยกเลิกที่แนบภายหลัง
        var refused = EtaxVoidPolicy.Decide(EtaxStatus.Submitted, "ยกเลิกแล้ว", evidenceFileAttached: false);
        Assert.Contains("หลังวันที่ส่ง e-Tax", refused.Reason);
    }

    [Fact]
    public void R200_V1I_O5_ทิศตรงข้าม_แถวเก่าไม่มีเวลาส่ง_ใช้เวลาสร้างแถว()
        => Assert.Equal(Feb10, EtaxVoidPolicy.EvidenceNotBefore(null, Feb10));

    // ═════════════ V1H-O7: ธงของใบที่ Submitted ไม่แนะนำทาง (ค) ที่จะถูกปฏิเสธ ═════════════

    [Theory]
    [InlineData(PaymentVoidCause.ChequeBounce)]
    [InlineData(PaymentVoidCause.SettlementUnpost)]
    public void R200_V1I_O7_ใบที่ส่งแล้วยังไม่รู้ผล_ไม่แนะนำทางค_ทิศตรงข้ามใบที่ตอบรับแล้วยังแนะนำ(PaymentVoidCause cause)
    {
        var submitted = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Submitted, "RC-1", cause);
        Assert.Equal(AutoReceiptEtaxAction.FlagEtaxCancellation, submitted.Action);
        Assert.DoesNotContain("ใบกำกับเดิมยังใช้ได้", submitted.Message);
        Assert.Contains("แนบไฟล์หลักฐาน", submitted.Message);      // ทางไปต่อของใบ Submitted ยังอยู่
        // ตัวตัดสินทาง (ค) ปฏิเสธ Submitted จริง — ข้อความต้องไม่ชี้ไปทางนั้น
        var c = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Submitted, false,
            EtaxCancellationPath.OriginalStillValid, "x", null, false, null, 70m, 1070m, ReceiptTotalAmount: 1070m, LivePaymentCoverage: 1070m));
        Assert.False(c.Allowed);
        // ทิศตรงข้าม: ตอบรับแล้ว ⇒ ยังแนะนำทาง (ค)
        var accepted = DocumentVoidPreconditions.AutoReceiptOnPaymentVoid(EtaxStatus.Accepted, "RC-1", cause);
        Assert.Contains("ใบกำกับเดิมยังใช้ได้", accepted.Message);
    }
}
