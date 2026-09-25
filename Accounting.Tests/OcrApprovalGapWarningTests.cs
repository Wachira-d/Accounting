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
            "ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ 328.67 = 7% × 4,695.33)");
        Assert.Equal(2, w.Count);                                                      // VAT (รวม) + บรรทัดติดลบ
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
}
