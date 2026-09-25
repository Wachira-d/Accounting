using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 12) — <see cref="OcrApprovalGapWarning"/>: [Σ-GAP] ตอนอนุมัติด้วยมือ = คำเตือนที่ต้องกดรับทราบ
///
/// <para><b>ครึ่งที่ 1</b>: สแกนที่มี [Σ-GAP] ⇒ ได้คำเตือนหนึ่งข้อต่อหนึ่งบรรทัด [Σ-GAP] พร้อมตัวเลข "ตอนนี้ vs กระดาษ" ·
/// คำเตือนแยกได้ด้วย <see cref="OcrApprovalGapWarning.IsGapWarning"/> (API ใช้ตัดสินว่าไม่ขัดจังหวะ)</para>
/// <para><b>ครึ่งที่ 2</b>: สแกนที่ไม่มี [Σ-GAP] / ไม่มีหมายเหตุ ⇒ ไม่เตือน (ไม่ฟ้องใบถูกทุกใบ — F2 ข้อ 8) ·
/// คำเตือนชุดอื่น (เช่น §82/3) ไม่ถูกนับเป็นชุดนี้ · ผู้ใช้แก้รายการจนตรงแล้ว ⇒ ข้อความบอกว่า "ตรงกันแล้ว" ไม่ใช่ข้อความเก่า</para>
/// </summary>
public class OcrApprovalGapWarningTests
{
    private const string GapNotes =
        "[Tier] Azure DI สำเร็จ\n" +
        "[Σ-GAP] ผลรวมรายการ 24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25 (ต่าง 297.75)\n" +
        "[Buyer] หจก.แอม แฮปปี้เนส";

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void สแกนมีSigmaGap_เตือนพร้อมยอดตอนนี้และกระดาษ()
    {
        var w = Assert.Single(OcrApprovalGapWarning.Build(GapNotes, 23812.25m, 24110.00m));
        Assert.StartsWith(OcrApprovalGapWarning.Prefix, w);
        Assert.Contains("24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25", w);
        Assert.Contains("ตอนนี้รายการในเอกสารรวม 24,110.00 · กระดาษ 23,812.25", w);
        Assert.Contains("+297.75", w);
        Assert.True(OcrApprovalGapWarning.IsGapWarning(w));
    }

    [Fact]
    public void SigmaGapซ้ำกันสองบรรทัด_เตือนครั้งเดียว_คนละเรื่องเตือนสองข้อ()
    {
        var dup = GapNotes + "\n[Σ-GAP] ผลรวมรายการ 24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25 (ต่าง 297.75)";
        Assert.Single(OcrApprovalGapWarning.Build(dup, 23812.25m, 24110.00m));
        var two = GapNotes + "\n[Σ-GAP] VAT รายบรรทัดรวม 1,150.00 ≠ VAT หัวใบ 1,148.28";
        Assert.Equal(2, OcrApprovalGapWarning.Build(two, 23812.25m, 24110.00m).Count);
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void ไม่มีSigmaGap_ไม่เตือน()
    {
        Assert.Empty(OcrApprovalGapWarning.Build(null, 536.00m, 536.00m));
        Assert.Empty(OcrApprovalGapWarning.Build("[Tier] Azure DI สำเร็จ\n[PAY≠TOTAL] ยอดตามใบกำกับ 536.00", 536.00m, 536.00m));
        Assert.Empty(OcrApprovalGapWarning.Build("[Σ] กระทบยอดผ่าน · [Σ-GAP]ไม่ใช่ขึ้นต้นบรรทัด", 536.00m, 536.00m));
    }

    [Fact]
    public void แก้รายการจนตรงกระดาษแล้ว_ข้อความบอกว่าตรงกันแล้ว()
    {
        var w = Assert.Single(OcrApprovalGapWarning.Build(GapNotes, 23812.25m, 23812.25m));
        Assert.Contains("(ตรงกันแล้ว)", w);
    }

    [Fact]
    public void ไม่รู้ยอดกระดาษ_ไม่แต่งตัวเลขเปรียบเทียบ()
    {
        var w = Assert.Single(OcrApprovalGapWarning.Build(GapNotes, null, 24110.00m));
        Assert.DoesNotContain("ตอนนี้รายการ", w);
    }

    [Fact]
    public void คำเตือนชุดอื่น_ไม่ใช่ชุดนี้()
    {
        Assert.False(OcrApprovalGapWarning.IsGapWarning("ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล"));
        Assert.False(OcrApprovalGapWarning.IsGapWarning(null));
    }

    // ── รอบ 195 (ใบ Scommerce): ท่อน "ตอนนี้…" ข้อเดียว · คำแนะนำเป็นตัวเลขรวมข้อที่พูดเรื่องเดียวกัน ─────────────

    private static string ScommerceGapNotes()
    {
        var r = OcrAmountIntegrity.Check(
            new[] { new OcrPlannedLine(4695.33m, ThaiVatTypeRule.ExemptRate, 0m), new OcrPlannedLine(0m, 7m, 0m) }, 328.67m, 5024.00m);
        return "[Tier] Azure DI สำเร็จ\n" + string.Join("\n", r.Problems.Select(p => "[Σ-GAP] " + p.Message))
            + "\n[Σ-GAP] บรรทัดที่ 3 ยอดติดลบ (-5.00) — ส่วนลด/คืนของต้องลงช่องส่วนลด";
    }

    [Fact]
    public void ใบScommerce_มีคำแนะนำ_รวมข้อเรื่องVATเป็นข้อเดียว_ข้อคนละเรื่องยังแยก()
    {
        var w = OcrApprovalGapWarning.Build(ScommerceGapNotes(), 5024.00m, 4695.33m,
            "ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 328.67 = 7% × 4,695.33)", linesVat: 0m, paperVat: 328.67m);
        Assert.Equal(2, w.Count);                                                      // VAT+ยอดรวม (รวม) + บรรทัดติดลบ
        Assert.Contains("ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 328.67 = 7% × 4,695.33)", w[0]);
        Assert.Contains("รวม 2 ข้อ", w[0]);
        Assert.Contains("ยอดติดลบ", w[1]);
        Assert.All(w, x => Assert.True(OcrApprovalGapWarning.IsGapWarning(x)));        // ยังต้องกดรับทราบ (ไม่ถอดด่าน)
        Assert.Single(w, x => x.Contains("ตอนนี้รายการในเอกสารรวม"));                  // ท่อน "ตอนนี้…" ข้อเดียว
    }

    [Fact]
    public void ไม่มีคำแนะนำ_ทุกข้อแยกเหมือนเดิม_แต่ท่อนตอนนี้อยู่ข้อแรกข้อเดียว()
    {
        var w = OcrApprovalGapWarning.Build(ScommerceGapNotes(), 5024.00m, 4695.33m);
        Assert.Equal(3, w.Count);
        Assert.Contains("ตอนนี้รายการในเอกสารรวม", w[0]);
        Assert.DoesNotContain("ตอนนี้รายการในเอกสารรวม", w[1]);
        Assert.DoesNotContain("ตอนนี้รายการในเอกสารรวม", w[2]);
    }

    // ── รอบ 195 ฝ่ายค้าน P1 (ข้อ 4): รวมข้อยอดรวมเข้าคำแนะนำเฉพาะเมื่อ "ตั้ง 7% แล้วยอดรวมตรงกระดาษ" ─────────────────────

    private const string Advice1000 = "ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 70.00 = 7% × 1,000.00)";

    private static string FeeGapNotes()
    {
        // ฐาน 1,000 (บรรทัดถูกตั้งเป็นยกเว้น) · VAT 70 · ยอดรวมบนกระดาษ 1,120 (ค่าธรรมเนียมหลัง VAT 50 ที่อ่านไม่ได้)
        var r = OcrAmountIntegrity.Check(new[] { new OcrPlannedLine(1000m, ThaiVatTypeRule.ExemptRate, 0m) }, 70m, 1120m);
        return string.Join("\n", r.Problems.Select(p => "[Σ-GAP] " + p.Message));
    }

    [Fact]
    public void ค่าธรรมเนียม50ที่อ่านไม่ได้_ข้อยอดรวมไม่ถูกรวม_บอกส่วนต่างที่เหลือ()
    {
        var w = OcrApprovalGapWarning.Build(FeeGapNotes(), 1120m, 1000m, Advice1000, linesVat: 0m, paperVat: 70m);
        Assert.Equal(2, w.Count);                                   // คำแนะนำอัตรา (ข้อ VAT) + ข้อยอดรวมแยก
        Assert.Contains(Advice1000, w[0]);
        Assert.DoesNotContain("รวม 2 ข้อ", w[0]);                  // ข้อยอดรวมไม่ใช่ "เรื่องเดียวกัน"
        Assert.Contains("ยอดรวมจากรายการ", w[1]);
        Assert.Contains("−50.00", w[1]);                             // 1,000 + 70 − 1,120 = ส่วนที่ไม่ใช่เรื่องอัตรา VAT
        Assert.All(w, x => Assert.True(OcrApprovalGapWarning.IsGapWarning(x)));
    }

    [Fact]
    public void ไม่รู้VATของบรรทัด_ไม่รวมข้อยอดรวม_ทิศปลอดภัย()
    {
        var w = OcrApprovalGapWarning.Build(ScommerceGapNotes(), 5024.00m, 4695.33m,
            "ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 328.67 = 7% × 4,695.33)");
        Assert.Equal(3, w.Count);                                   // คำแนะนำ (VAT) · ยอดรวมแยก · บรรทัดติดลบ
        Assert.Single(w, x => x.Contains("ยอดรวมจากรายการ"));
    }

    // ── รอบ 195 ฝ่ายค้านรอบสอง R2-3: VAT ที่ไม่ได้พิมพ์บนกระดาษ = คำเตือนตอนอนุมัติทุกทางเข้า ─────────────────────────────

    [Fact]
    public void VATไม่อยู่บนกระดาษ_ไม่มีSigmaGap_ยังเตือนหนึ่งข้อ_เป็นชุดที่ต้องรับทราบ()
    {
        var w = Assert.Single(OcrApprovalGapWarning.Build("[Tier] Tesseract", 1070.00m, 1070.00m,
            linesVat: 70.00m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NotOnPaper));
        Assert.StartsWith(OcrApprovalGapWarning.VatDerivedPrefix, w);
        Assert.Contains("ม.86/4(6)", w);
        Assert.Contains("ม.82/5(1)", w);
        Assert.Contains("ตอนนี้ VAT ในเอกสาร 70.00", w);
        Assert.True(OcrApprovalGapWarning.IsGapWarning(w));          // workflow ส่งผ่านเองไม่ได้ · API ไม่ขัดจังหวะ
        Assert.True(OcrApprovalGapWarning.IsVatDerivedWarning(w));
        // สแกนเก่าที่ไม่มีหมายเหตุเลยก็ยังเตือน (ตัดสินสดจากกระดาษ ไม่พึ่งแท็ก)
        Assert.Single(OcrApprovalGapWarning.Build(null, 1070.00m, 1070.00m,
            linesVat: 70.00m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NotOnPaper));
    }

    [Fact]
    public void VATไม่อยู่บนกระดาษ_พร้อมSigmaGap_ได้ทั้งสองข้อ_ข้อVATอยู่ท้าย()
    {
        var ws = OcrApprovalGapWarning.Build(GapNotes, 23812.25m, 24110.00m,
            linesVat: 1577.29m, paperVat: 1577.29m, headerVatSource: OcrHeaderVatSource.NotOnPaper);
        Assert.Equal(2, ws.Count);
        Assert.StartsWith(OcrApprovalGapWarning.Prefix, ws[0]);
        Assert.False(OcrApprovalGapWarning.IsVatDerivedWarning(ws[0]));
        Assert.StartsWith(OcrApprovalGapWarning.VatDerivedPrefix, ws[1]);
    }

    [Theory]
    [InlineData(OcrHeaderVatSource.Labelled)]
    [InlineData(OcrHeaderVatSource.PrintedUnlabelled)]
    [InlineData(OcrHeaderVatSource.NoVat)]
    public void ทิศตรงข้าม_VATพิมพ์บนกระดาษหรือไม่มีVAT_ไม่เตือน(OcrHeaderVatSource src)
        => Assert.Empty(OcrApprovalGapWarning.Build("[Tier] Azure DI", 5024.00m, 5024.00m,
            linesVat: 328.67m, paperVat: 328.67m, headerVatSource: src));

    [Fact]
    public void ทิศตรงข้าม_ผู้ใช้ตั้งVATเป็น0ตามทางเลือกแล้ว_ไม่เตือน()
    {
        Assert.Empty(OcrApprovalGapWarning.Build(null, 1070.00m, 1070.00m,
            linesVat: 0m, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NotOnPaper));
        // ไม่รู้ VAT ของบรรทัด = เตือน (ไม่รู้ ≠ ปลอดภัย)
        Assert.Single(OcrApprovalGapWarning.Build(null, 1070.00m, 1070.00m,
            linesVat: null, paperVat: 70.00m, headerVatSource: OcrHeaderVatSource.NotOnPaper));
    }

    [Fact]
    public void คำเตือนVATไม่อยู่บนกระดาษ_workflowต้องหยุด_APIผ่านแต่คืนในคำตอบ()
    {
        var w = OcrApprovalGapWarning.VatDerivedWarning(OcrHeaderVatSource.NotOnPaper, 70.00m, 70.00m)!;
        Assert.Equal(new[] { w }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { w }));
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, new[] { w }));
    }
}
