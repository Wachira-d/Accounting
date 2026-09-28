using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (ฝ่ายค้าน C5/C6) — <see cref="ApprovalAcknowledgement"/>: ห้ามระบบ/workflow/API ประทับ "ผู้ใช้รับทราบคำเตือน" แทนคน
///
/// <para><b>ครึ่งที่ 1</b> (ที่เคยผิด): workflow/ลายเซ็นส่ง ack แทนผู้ใช้ ⇒ ตอนนี้ [Σ-GAP] ระบบส่งผ่านไม่ได้ · API ผ่านได้เฉพาะ [Σ-GAP]
/// (คำตัดสินข้อ 12) คำเตือนอื่นยังหยุด · หมายเหตุ/รหัสกฎบอกว่า "ระบบ/API ส่งผ่าน" ไม่ใช่ "ยืนยันโดย"</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): ผู้ใช้กดรับทราบเอง = ผ่านทุกข้อ รหัสกฎเดิม APPROVE-ACK-WARNINGS · ไม่มีใครรับทราบ = หยุดทุกข้อ ·
/// ไม่มีคำเตือน = ไม่มีอะไรหยุด</para>
/// <para><b>รอบ 199 (คำตัดสินเจ้าของรอบ 198 ข้อ 6 · ฝ่ายค้าน B-1)</b>: คำเตือน "VAT จากสแกนไม่ได้พิมพ์บนกระดาษ/ตรวจกับกระดาษไม่ได้" API ต้อง
/// <b>ปฏิเสธ</b> (รหัส <c>APPROVE-SCAN-VAT-NOT-ON-PAPER</c>) · ทิศตรงข้าม: [Σ-GAP] ยอดอย่างเดียว/ไม่มีคำเตือน = อนุมัติต่อเหมือนเดิม (ข้อ 12)</para>
/// </summary>
public class ApprovalAcknowledgementTests
{
    private static readonly string Gap = OcrApprovalGapWarning.Prefix + ": ผลรวมรายการ 24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25";
    private const string Other = "ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล";
    private static readonly System.DateTime At = new(2026, 9, 24, 3, 0, 0, System.DateTimeKind.Utc);

    [Fact]
    public void ระบบworkflowส่งผ่านคำเตือนยอดสแกนไม่ได้_ต้องมีคนรับทราบ()
    {
        var left = ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { Gap, Other });
        Assert.Equal(new[] { Gap }, left);
    }

    [Fact]
    public void APIผ่านได้เฉพาะคำเตือนยอดสแกน_คำเตือนอื่นยังหยุด()
    {
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { Gap }));
        Assert.Equal(new[] { Other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { Gap, Other }));
    }

    [Fact]
    public void ร่องรอยของระบบและAPI_ไม่อ้างว่าผู้ใช้รับทราบ()
    {
        var sys = ApprovalAcknowledgement.Note(ApprovalAckSource.SystemWorkflow, new[] { Other }, "external:คุณสมชาย", At);
        Assert.Contains("ไม่มีผู้ใช้เห็น", sys);
        Assert.DoesNotContain("ยืนยันโดย", sys);
        Assert.Equal(ApprovalAcknowledgement.SystemRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.SystemWorkflow));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.SystemWorkflow));

        var api = ApprovalAcknowledgement.Note(ApprovalAckSource.ApiClient, new[] { Gap }, "api:v1", At);
        Assert.Contains("API", api);
        Assert.DoesNotContain("ยืนยันโดย", api);
        Assert.Equal(ApprovalAcknowledgement.ApiRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.ApiClient));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.ApiClient));
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void ผู้ใช้กดรับทราบเอง_ผ่านทุกข้อ_รหัสกฎเดิม()
    {
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, new[] { Gap, Other }));
        Assert.Equal("APPROVE-ACK-WARNINGS", ApprovalAcknowledgement.RuleCode(ApprovalAckSource.User));
        Assert.True(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.User));
        var note = ApprovalAcknowledgement.Note(ApprovalAckSource.User, new[] { Other }, "u-1", At);
        Assert.Contains("รับทราบคำเตือนตอนอนุมัติ", note);
        Assert.Contains("ยืนยันโดย u-1 เมื่อ 2026-09-24 10:00", note);
    }

    [Fact]
    public void ไม่มีใครรับทราบ_หยุดทุกข้อ_ไม่มีคำเตือนไม่มีอะไรหยุด()
    {
        Assert.Equal(new[] { Gap, Other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, new[] { Gap, Other }));
        foreach (var src in new[] { ApprovalAckSource.None, ApprovalAckSource.SystemWorkflow, ApprovalAckSource.ApiClient })
            Assert.Empty(ApprovalAcknowledgement.Unacknowledged(src, System.Array.Empty<string>()));
    }

    // ── รอบ 199: API v1 ปฏิเสธใบสแกนที่ VAT ไม่ได้พิมพ์บนกระดาษ (คำตัดสินรอบ 198 ข้อ 6) ─────────────────────────────────────

    private static string VatNotOnPaper() => Assert.Single(OcrApprovalGapWarning.Build("[Tier] Tesseract", 1070.00m, 1070.00m,
        linesVat: 70.00m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NotOnPaper));

    private static string VatUnchecked() => Assert.Single(OcrApprovalGapWarning.Build(null, 1070.00m, 1070.00m,
        linesVat: 70.00m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NoTextToCheck));

    [Fact]
    public void APIปฏิเสธ_VATไม่ได้พิมพ์บนกระดาษ_รหัสเครื่องอ่าน_ข้อความบอกทางไปต่อ()
    {
        var vat = VatNotOnPaper();
        Assert.Equal(new[] { vat }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { vat }));
        var r = ApprovalAcknowledgement.ApiRefusal(new[] { vat });
        Assert.NotNull(r);
        Assert.Equal("APPROVE-SCAN-VAT-NOT-ON-PAPER", r!.Code);
        Assert.Equal(ApprovalAcknowledgement.ApiVatNotOnPaperCode, r.Code);
        Assert.True(r.ScanVatNotOnPaper);
        Assert.Equal(new[] { vat }, r.Warnings);
        Assert.Contains("ม.82/5(1)", r.Message);
        Assert.Contains("ฉบับร่าง", r.Message);
        Assert.Contains("หน้าเว็บ", r.Message);
        Assert.Contains(vat, r.Message);
    }

    [Fact]
    public void APIปฏิเสธ_ไม่มีข้อความสแกนให้ตรวจVAT_รหัสเดียวกัน()
    {
        var r = ApprovalAcknowledgement.ApiRefusal(new[] { VatUnchecked() });
        Assert.NotNull(r);
        Assert.Equal(ApprovalAcknowledgement.ApiVatNotOnPaperCode, r!.Code);
        Assert.True(r.ScanVatNotOnPaper);
    }

    [Fact]
    public void APIปฏิเสธ_VATพร้อมSigmaGap_บล็อกด้วยข้อVAT_ข้อยอดไม่อยู่ในรายการบล็อก()
    {
        var vat = VatNotOnPaper();
        var r = ApprovalAcknowledgement.ApiRefusal(new[] { Gap, vat });
        Assert.NotNull(r);
        Assert.Equal(ApprovalAcknowledgement.ApiVatNotOnPaperCode, r!.Code);
        Assert.Equal(new[] { vat }, r.Warnings);
        // VAT + คำเตือนชนิดอื่น ⇒ รหัส VAT มาก่อน · รายการครบทั้งสองข้อ
        var both = ApprovalAcknowledgement.ApiRefusal(new[] { Other, vat });
        Assert.Equal(ApprovalAcknowledgement.ApiVatNotOnPaperCode, both!.Code);
        Assert.Equal(new[] { Other, vat }, both.Warnings);
    }

    [Fact]
    public void ทิศตรงข้าม_SigmaGapยอดอย่างเดียว_หรือไม่มีคำเตือน_APIอนุมัติต่อ_คำตัดสินข้อ12ไม่เปลี่ยน()
    {
        Assert.Null(ApprovalAcknowledgement.ApiRefusal(new[] { Gap }));
        Assert.Null(ApprovalAcknowledgement.ApiRefusal(System.Array.Empty<string>()));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { Gap }));
        Assert.Equal(ApprovalAcknowledgement.ApiRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.ApiClient));
        // VAT พิมพ์บนกระดาษ ⇒ ไม่มีคำเตือน VAT ⇒ ไม่มีอะไรให้ปฏิเสธ
        Assert.Null(ApprovalAcknowledgement.ApiRefusal(OcrApprovalGapWarning.Build("[Tier] Azure DI", 1070.00m, 1070.00m,
            linesVat: 70.00m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.Labelled)));
    }

    [Fact]
    public void APIปฏิเสธคำเตือนชนิดอื่นด้วยรหัสทั่วไป_ไม่ใช่รหัสVAT()
    {
        var r = ApprovalAcknowledgement.ApiRefusal(new[] { Gap, Other });
        Assert.NotNull(r);
        Assert.Equal(ApprovalAcknowledgement.ApiWarningsNeedAckCode, r!.Code);
        Assert.False(r.ScanVatNotOnPaper);
        Assert.Equal(new[] { Other }, r.Warnings);
        Assert.Equal(ApprovalAcknowledgement.ApiWarningsNeedAckCode, ApprovalAcknowledgement.ApiRefusalOf(new[] { Other }).Code);
    }

    [Fact]
    public void ตัวแยกชุดยอด_ไม่รวมชุดVAT()
    {
        Assert.True(OcrApprovalGapWarning.IsAmountGapWarning(Gap));
        Assert.False(OcrApprovalGapWarning.IsAmountGapWarning(VatNotOnPaper()));
        Assert.False(OcrApprovalGapWarning.IsAmountGapWarning(VatUnchecked()));
        Assert.False(OcrApprovalGapWarning.IsAmountGapWarning(Other));
        Assert.False(OcrApprovalGapWarning.IsAmountGapWarning(null));
        // ชุดที่ต้องมีคนรับทราบของ workflow ไม่เปลี่ยน (ยังรวมชุด VAT) · ผู้ใช้กดรับทราบเอง = ผ่านทุกข้อ
        Assert.Equal(new[] { Gap, VatNotOnPaper() },
            ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { Gap, VatNotOnPaper(), Other }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, new[] { Gap, VatNotOnPaper() }));
    }

    [Fact]
    public void หมายเหตุปฏิเสธบนเอกสาร_ลงครั้งเดียว_ไม่ทับของเดิม()
    {
        var r = ApprovalAcknowledgement.ApiRefusal(new[] { VatNotOnPaper() })!;
        var first = ApprovalAcknowledgement.ApiRefusalNote("หมายเหตุเดิม", r, At);
        Assert.NotNull(first);
        Assert.StartsWith("หมายเหตุเดิม", first);
        Assert.Contains(ApprovalAcknowledgement.ApiRefusalNoteTag, first);
        Assert.Contains("2026-09-24 10:00", first);
        Assert.Contains(ApprovalAcknowledgement.ApiVatNotOnPaperCode, first);
        Assert.Null(ApprovalAcknowledgement.ApiRefusalNote(first, r, At));     // retry ของระบบปลายทาง ⇒ ไม่ต่อซ้ำ
        Assert.StartsWith(ApprovalAcknowledgement.ApiRefusalNoteTag, ApprovalAcknowledgement.ApiRefusalNote(null, r, At));
    }
}
