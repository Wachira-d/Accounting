using System.Text.Json;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ผังบัญชี "รายบรรทัด" ต้องเดินทางถึงเอกสารจริง** (ผลตรวจ 2026-09-18 · D3-1)
///
/// <para>บั๊ก: ไปป์ไลน์ serialize บรรทัดลง <c>ExtractedItemsJson</c> ด้วย projection 8 ช่อง
/// และทำ **ก่อน** <c>ApplyProductCrossReferenceAsync</c> (ตัวเขียน <c>SuggestedAccountCode</c>
/// จากสินค้าใน master) กับตัวเรียนรายบรรทัด ⇒ เส้นสร้างเอกสารอ่านกลับมาได้ <c>null</c>
/// ทุกบรรทัด แล้วตกไปใช้ผังบัญชีระดับหัวใบ ⇒ ชั้นหลักฐานที่แข็งที่สุดไม่เคยถึงเอกสาร
/// ขณะที่ trace บนหน้าจอเขียนว่า "[Product] รายการ → บัญชี" ไปแล้ว</para>
///
/// <para>เทสต์ชุดนี้มี **สองครึ่ง** ตามกฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่าใบที่พังกลับมาถูก
/// (สองบรรทัดคนละบัญชีต้องได้บัญชีของตัวเอง) และครึ่งที่พิสูจน์ว่าใบที่เคยถูกอยู่แล้ว
/// **ไม่ถูกแตะ** (บรรทัดที่ไม่มีบัญชีของตัวเองยังตกไปใช้บัญชีหัวใบเหมือนเดิม ·
/// บรรทัดที่ผูกใบสั่งซื้อยังชนะทุกชั้นเหมือนเดิม)</para>
/// </summary>
public class OcrLineAccountFlowTests
{
    private static readonly JsonSerializerOptions SameAsPipelineRead = new();

    private static List<OcrExtractedLineItem> RoundTrip(List<OcrExtractedLineItem> items)
    {
        // รูปเดียวกับที่เส้นสร้างเอกสารอ่านกลับ (CreateDocumentFromScanAsync / Preview /
        // Repopulate ใช้ JsonSerializer.Deserialize<List<OcrExtractedLineItem>> ตรง ๆ)
        var json = OcrService.SerializeExtractedItems(items);
        return JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(json, SameAsPipelineRead)!;
    }

    // ── ครึ่งที่ 1: ใบที่พังต้องกลับมาถูก ──────────────────────────────────

    [Fact]
    public void สองบรรทัดคนละบัญชี_ต้องได้บัญชีของตัวเองไม่ใช่บัญชีหัวใบ()
    {
        // กระดาษใบเดียวมีค่าน้ำมัน (54310) กับค่าเครื่องเขียน (54120) — ตัวเทียบสินค้า
        // ใน master เขียนรหัสให้ทั้งสองบรรทัดแล้ว ส่วนตัวจัดหมวดของทั้งใบเดาเป็น 54900
        var scanned = new List<OcrExtractedLineItem>
        {
            new() { Description = "น้ำมันดีเซล", Quantity = 30m, UnitPrice = 32m, Amount = 960m, SuggestedAccountCode = "54310" },
            new() { Description = "กระดาษ A4", Quantity = 2m, UnitPrice = 120m, Amount = 240m, SuggestedAccountCode = "54120" },
        };

        var afterPersist = RoundTrip(scanned);

        // ค่าที่ชั้น Product master เขียนไว้ต้องรอดข้ามการบันทึกลงฐาน
        Assert.Equal("54310", afterPersist[0].SuggestedAccountCode);
        Assert.Equal("54120", afterPersist[1].SuggestedAccountCode);

        // และตอนสร้างบรรทัดเอกสาร ต้องชนะบัญชีระดับหัวใบ
        var coa = new Dictionary<string, Guid>
        {
            ["54310"] = Guid.NewGuid(),
            ["54120"] = Guid.NewGuid(),
        };
        var headerAccount = Guid.NewGuid();

        var picks = afterPersist
            .Select(i => OcrLineAccountSource.Resolve(
                purchaseOrderLineAccountId: null,
                scannedLineAccountId: i.SuggestedAccountCode != null && coa.TryGetValue(i.SuggestedAccountCode, out var a)
                    ? a : (Guid?)null,
                scanHeaderAccountId: headerAccount))
            .ToList();

        Assert.Equal(coa["54310"], picks[0].AccountId);
        Assert.Equal(coa["54120"], picks[1].AccountId);
        Assert.All(picks, p => Assert.Equal(OcrLineAccountOrigin.ScannedLine, p.From));
        Assert.DoesNotContain(picks, p => p.AccountId == headerAccount);
    }

    [Fact]
    public void ช่องที่_projection_เดิมตกหล่น_ต้องอยู่ครบหลังบันทึก()
    {
        // projection 8 ช่องเดิมไม่มี VatRate/VatAmount/ProjectAiFeedbackId/AiSuggestedProjectId
        // ⇒ ด่าน "ผู้ใช้/AI ตัดสินอัตรา VAT มาแล้ว ห้ามทับ" (it.VatRate.HasValue) เป็นจริง
        // เฉพาะหลังผู้ใช้แก้บรรทัด · และลูปสอน MatchLineProject ปิดไม่ได้เพราะ feedbackId หาย
        var feedback = Guid.NewGuid();
        var project = Guid.NewGuid();
        var items = new List<OcrExtractedLineItem>
        {
            new()
            {
                Description = "ค่าบริการยกเว้น §81", Amount = 300m, Unit = "งาน",
                VatRate = -1m, VatAmount = 0m,
                ProjectId = project, ProjectName = "โครงการ A",
                ProjectAiFeedbackId = feedback, AiSuggestedProjectId = project,
            },
        };

        var back = RoundTrip(items)[0];

        Assert.Equal(-1m, back.VatRate);
        Assert.Equal(0m, back.VatAmount);
        Assert.Equal(feedback, back.ProjectAiFeedbackId);
        Assert.Equal(project, back.AiSuggestedProjectId);
        Assert.Equal(project, back.ProjectId);
        Assert.Equal("งาน", back.Unit);
    }

    [Fact]
    public void รูปแบบเดิมที่บันทึกไว้ก่อนแก้_ยังอ่านได้_ไม่_throw()
    {
        // แถวเก่าในฐานถูกเขียนด้วย projection 8 ช่อง — ต้องอ่านได้ต่อ (VatRate = null
        // แปลว่า "ยังไม่ได้ตัดสิน" ซึ่งเป็นความหมายที่ถูกต้องของค่านั้น)
        const string legacy = """
        [{"Description":"สินค้า A","Quantity":1,"UnitPrice":100,"Amount":100,
          "SuggestedAccountCode":"54120","ProjectId":null,"ProjectName":null,"Unit":"ชิ้น"}]
        """;
        var back = JsonSerializer.Deserialize<List<OcrExtractedLineItem>>(legacy, SameAsPipelineRead)!;
        Assert.Single(back);
        Assert.Equal("54120", back[0].SuggestedAccountCode);
        Assert.Null(back[0].VatRate);
    }

    // ── ครึ่งที่ 2: ใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ ─────────────────────────

    [Fact]
    public void บรรทัดที่ไม่มีบัญชีของตัวเอง_ยังตกไปใช้บัญชีหัวใบเหมือนเดิม()
    {
        // ใบ Makro/ใบค้าปลีกที่ไม่มีสินค้าใน master — ตัวจัดหมวดของทั้งใบเป็นคำตอบเดียว
        // ที่มี ⇒ ต้องยังเติมให้ทุกบรรทัด (กฎเหล็ก #3: ห้ามปล่อยช่องว่าง)
        var header = Guid.NewGuid();
        var pick = OcrLineAccountSource.Resolve(null, null, header);
        Assert.Equal(header, pick.AccountId);
        Assert.Equal(OcrLineAccountOrigin.ScanHeader, pick.From);
    }

    [Fact]
    public void บรรทัดที่ผูกใบสั่งซื้อ_ชนะทุกชั้นเหมือนเดิม()
    {
        var po = Guid.NewGuid();
        var line = Guid.NewGuid();
        var header = Guid.NewGuid();
        var pick = OcrLineAccountSource.Resolve(po, line, header);
        Assert.Equal(po, pick.AccountId);
        Assert.Equal(OcrLineAccountOrigin.PurchaseOrderLine, pick.From);
    }

    [Fact]
    public void ไม่มีชั้นไหนตอบ_ต้องคืนไม่รู้_ห้ามแต่งบัญชีขึ้นมา()
    {
        var pick = OcrLineAccountSource.Resolve(null, null, null);
        Assert.Null(pick.AccountId);
        Assert.Equal(OcrLineAccountOrigin.None, pick.From);
        Assert.Contains("เลือกเอง", OcrLineAccountSource.Explain(pick.From));
    }

    [Fact]
    public void ทุกชั้นมีคำอธิบายของตัวเอง_ห้ามให้หน้าจอแต่งคำเอง()
    {
        foreach (var origin in Enum.GetValues<OcrLineAccountOrigin>())
            Assert.False(string.IsNullOrWhiteSpace(OcrLineAccountSource.Explain(origin)));
    }
}
