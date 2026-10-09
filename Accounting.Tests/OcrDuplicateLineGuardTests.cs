using Accounting.Helpers;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 2026-10-09 — ผู้ใช้รายงาน: ใบแจ้งหนี้/ใบกำกับภาษี BS2026100001 (บริษัท บุญทรัพย์ ถาวร จำกัด · สายไฟ 5 บรรทัด) สแกนแล้ว
/// **ทุกบรรทัดจำนวน ×2** (200/100/200/200/200 · Σ 42,010 = 2 × 21,005) ราคาต่อหน่วย/คำอธิบายถูก · ต้นเหตุ: PDF หน้า 1 ต้นฉบับ +
/// หน้า 2 สำเนา (ข้อความจริงใน <see cref="OcrPaperSamples.BoonsapOriginalCopyPages"/>) ⇒ engine คืน 5 แถวสองรอบ ⇒ ขั้นยุบบรรทัด
/// ชื่อ+ราคาเท่ากันของ <c>OcrService.SanitizeVatSplitArtifacts</c> บวกจำนวน/ยอด
///
/// <para>สองทิศทาง (กฎเหล็ก #4 §H): ใบที่พังกลับมาถูก (ต้นฉบับ+สำเนา ⇒ 100/50/100/100/100) · ใบที่ถูกอยู่แล้วไม่ถูกแตะ (กระดาษพิมพ์
/// "น้ำดื่ม 10 ขวด 5.00 50.00" สองบรรทัด หัวใบ 100 ⇒ คงสองบรรทัดและขั้นยุบเดิมรวมเป็น 20 ขวดเหมือนเดิม)</para>
/// </summary>
public class OcrDuplicateLineGuardTests
{
    // ── กระดาษจริง: 5 บรรทัดตามที่หน้า review แสดง (คำอธิบายจาก engine · ตัวเลขจากกระดาษ) ─────────────────────────────

    private static readonly (string Desc, decimal Qty, decimal Price, decimal Amount)[] Boonsap =
    {
        ("สายไฟ FD-CV 0.6/1KV 1*16 mm2 YAZAKI ดำ", 100m, 103.16m, 10316.00m),
        ("สายไฟ FD-0.6/1K.V-CV 1x10 SQ.mm ยาซากิ (ดำ)", 50m, 69.26m, 3463.00m),
        ("สายไฟ IEC 01 THW 1 x 6 SQ.MM YAZAKI สีดำ", 100m, 36.18m, 3618.00m),
        ("สายไฟ IEC 01 THW 1 x 4 SQ.MM YAZAKI ดำ", 100m, 22.08m, 2208.00m),
        ("สายไฟ IEC 01 THW 1 x 2.5 SQ.MM YAZAKI สีดำ", 100m, 14.00m, 1400.00m),
    };

    private const decimal BoonsapSub = 21005.00m, BoonsapVat = 1470.35m, BoonsapTotal = 22475.35m;

    private static List<OcrCandidateRow> BoonsapRows(int copies)
        => Enumerable.Range(0, copies)
            .SelectMany(_ => Boonsap.Select(l => new OcrCandidateRow(l.Desc, l.Qty, l.Price, l.Amount)))
            .ToList();

    private static OcrExtractedLineItem Item(string desc, decimal qty, decimal price, decimal amount, bool etax = false)
        => new() { Description = desc, Quantity = qty, UnitPrice = price, Amount = amount, Unit = "เมตร", QuantityFromEtaxXml = etax };

    private static OcrExtractedData BoonsapScan(int copies, bool etax = false)
    {
        var d = new OcrExtractedData { SubTotal = BoonsapSub, VatAmount = BoonsapVat, TotalAmount = BoonsapTotal };
        for (var c = 0; c < copies; c++)
            foreach (var l in Boonsap) d.Items.Add(Item(l.Desc, l.Qty, l.Price, l.Amount, etax));
        return d;
    }

    // ── ตัวตัดสิน (pure) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ต้นฉบับบวกสำเนา_สิบแถว_Σทุกแถว42010ไม่ตรงหัวใบ_Σหลังนับครั้งเดียว21005ตรง_ตัดเหลือห้าแถวแรก()
    {
        var d = OcrDuplicateLineGuard.Decide(BoonsapRows(2), BoonsapSub, BoonsapVat, BoonsapTotal);
        Assert.True(d.Deduped);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, d.KeepIndexes.ToArray());
        Assert.Equal(5, d.DroppedCount);
        Assert.Equal(42010.00m, d.SumAll);
        Assert.Equal(21005.00m, d.SumDistinct);
        Assert.StartsWith(OcrDuplicateLineGuard.Tag, d.Reason);
        Assert.Contains("ต้นฉบับ/สำเนา", d.Reason);
    }

    [Fact]
    public void กระดาษพิมพ์รายการซ้ำจริง_น้ำดื่มสองบรรทัดหัวใบ100_Σทุกแถวตรงหัวใบ_คงทุกแถว()
    {
        var rows = new List<OcrCandidateRow> { new("น้ำดื่ม", 10m, 5.00m, 50.00m), new("น้ำดื่ม", 10m, 5.00m, 50.00m) };
        var d = OcrDuplicateLineGuard.Decide(rows, 100m, 7m, 107m);
        Assert.False(d.Deduped);
        Assert.Equal(new[] { 0, 1 }, d.KeepIndexes.ToArray());
        Assert.Equal(0, d.DroppedCount);
        Assert.Contains("ซ้ำจริง", d.Reason);
    }

    [Fact]
    public void ไม่มียอดหัวใบ_แถวเหมือนกันสองแถว_ไม่เดา_คงทุกแถว()
    {
        var rows = new List<OcrCandidateRow> { new("น้ำดื่ม", 10m, 5.00m, 50.00m), new("น้ำดื่ม", 10m, 5.00m, 50.00m) };
        var d = OcrDuplicateLineGuard.Decide(rows, null, null, null);
        Assert.False(d.Deduped);
        Assert.Contains("ไม่มียอดหัวใบ", d.Reason);
        Assert.False(OcrDuplicateLineGuard.Decide(rows, 0m, 0m, 0m).Deduped);
    }

    [Fact]
    public void Σไม่ตรงทั้งก่อนและหลังตัด_ไม่เดา_คงทุกแถวให้ด่านΣฟ้อง()
    {
        var d = OcrDuplicateLineGuard.Decide(BoonsapRows(2), 30000m, 2100m, 32100m);
        Assert.False(d.Deduped);
        Assert.Equal(10, d.KeepIndexes.Count);
        Assert.Contains("ไม่เดา", d.Reason);
    }

    [Fact]
    public void ชื่อและราคาเท่ากันแต่จำนวนต่าง_ไม่ใช่แถวซ้ำ_ไม่แตะ()
    {
        // "ค่าแรง 2 × 500" + "ค่าแรง 3 × 500" คือสองรายการจริง — ขั้นยุบเดิม (ชื่อ+ราคา) เป็นคนรวม ไม่ใช่ตัวนี้
        var rows = new List<OcrCandidateRow> { new("ค่าแรง", 2m, 500m, 1000m), new("ค่าแรง", 3m, 500m, 1500m) };
        var d = OcrDuplicateLineGuard.Decide(rows, 1000m, 70m, 1070m);   // หัวใบตรงแค่แถวแรก — ก็ยังไม่ใช่ "แถวซ้ำ"
        Assert.False(d.Deduped);
        Assert.Equal(0, d.DroppedCount);
    }

    [Fact]
    public void หน้าสองยกมาแค่บางแถว_ซ้ำสามแถว_Σหลังตัดตรงหัวใบ_ตัดเฉพาะแถวที่ซ้ำ()
    {
        var rows = BoonsapRows(1);
        rows.AddRange(BoonsapRows(1).Take(3));     // หน้า 2 พิมพ์ซ้ำ 3 แถวแรก (ยกมา)
        var d = OcrDuplicateLineGuard.Decide(rows, BoonsapSub, BoonsapVat, BoonsapTotal);
        Assert.True(d.Deduped);
        Assert.Equal(3, d.DroppedCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, d.KeepIndexes.ToArray());
    }

    [Fact]
    public void ราคารวมVAT_Σหลังตัดตรงยอดรวม_ก็ถือว่าตรงหัวใบ()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("กาแฟ", 2m, 53.50m, 107.00m), new("ขนม", 1m, 107.00m, 107.00m),
            new("กาแฟ", 2m, 53.50m, 107.00m), new("ขนม", 1m, 107.00m, 107.00m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 200m, 14m, 214m);
        Assert.True(d.Deduped);
        Assert.Equal(new[] { 0, 1 }, d.KeepIndexes.ToArray());
        Assert.Contains("ยอดรวม", d.Reason);
    }

    [Fact]
    public void คำอธิบายต่างแค่ช่องว่างและตัวพิมพ์_ถือว่าแถวเดียวกัน_แต่แถวไม่มีคำอธิบายไม่ถือว่าซ้ำกับใคร()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("สาย ไฟ  THW 1x6", 100m, 36.18m, 3618m), new("สายไฟ thw 1X6", 100m, 36.18m, 3618m),
            new("", 1m, 100m, 100m), new(null, 1m, 100m, 100m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 3818m, 267.26m, 4085.26m);
        Assert.True(d.Deduped);
        Assert.Equal(new[] { 0, 2, 3 }, d.KeepIndexes.ToArray());
        Assert.Equal(1, d.DroppedCount);
    }

    // ── ด่านจริง: SanitizeVatSplitArtifacts (จุดที่เคยรวมจำนวน ×2) ───────────────────────────────────────────────────

    [Fact]
    public void Sanitize_ต้นฉบับบวกสำเนา_ได้ห้าบรรทัดจำนวนตามกระดาษ_ไม่ใช่สองเท่า()
    {
        // ก่อนแก้ (ตรงที่ผู้ใช้รายงาน): 5 บรรทัด จำนวน 200/100/200/200/200 · ยอด 20,632/6,926/7,236/4,416/2,800 · Σ 42,010 · ราคาคงเดิม
        var d = BoonsapScan(copies: 2);
        OcrService.SanitizeVatSplitArtifacts(d);

        Assert.Equal(5, d.Items.Count);
        Assert.Equal(new[] { 100m, 50m, 100m, 100m, 100m }, d.Items.Select(i => i.Quantity!.Value).ToArray());
        Assert.Equal(new[] { 103.16m, 69.26m, 36.18m, 22.08m, 14.00m }, d.Items.Select(i => i.UnitPrice!.Value).ToArray());
        Assert.Equal(new[] { 10316m, 3463m, 3618m, 2208m, 1400m }, d.Items.Select(i => i.Amount!.Value).ToArray());
        Assert.Equal(21005.00m, d.Items.Sum(i => i.Amount!.Value));
        Assert.Equal(Boonsap.Select(l => l.Desc).ToArray(), d.Items.Select(i => i.Description).ToArray());
        Assert.Contains(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
    }

    [Fact]
    public void Sanitize_ใบที่ไม่ซ้ำ_ห้าบรรทัดเดิม_ไม่ถูกแตะและไม่มีtrace()
    {
        var d = BoonsapScan(copies: 1);
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(5, d.Items.Count);
        Assert.Equal(new[] { 100m, 50m, 100m, 100m, 100m }, d.Items.Select(i => i.Quantity!.Value).ToArray());
        Assert.DoesNotContain(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
    }

    [Fact]
    public void Sanitize_กระดาษพิมพ์น้ำดื่มสองบรรทัดจริง_หัวใบ100_คงพฤติกรรมเดิม_รวมเป็น20ขวด100บาท()
    {
        var d = new OcrExtractedData { SubTotal = 100m, VatAmount = 7m, TotalAmount = 107m };
        d.Items.Add(new() { Description = "น้ำดื่ม", Quantity = 10m, UnitPrice = 5.00m, Amount = 50.00m });
        d.Items.Add(new() { Description = "น้ำดื่ม", Quantity = 10m, UnitPrice = 5.00m, Amount = 50.00m });
        OcrService.SanitizeVatSplitArtifacts(d);
        var it = Assert.Single(d.Items);
        Assert.Equal(20m, it.Quantity);
        Assert.Equal(5.00m, it.UnitPrice);
        Assert.Equal(100.00m, it.Amount);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
    }

    [Fact]
    public void Sanitize_บรรทัดจากeTaxXML_ไม่เดินด่านแถวซ้ำและไม่ถูกยุบ()
    {
        // เอกสารลงนามไม่มี "ถูกอ่านสองรอบ" — บรรทัดชื่อซ้ำใน XML เป็นบรรทัดจริง (ทีม A: OcrEtaxLineNormalizer) ⇒ คง 10 บรรทัด
        var d = BoonsapScan(copies: 2, etax: true);
        OcrService.SanitizeVatSplitArtifacts(d, quantitiesFromSignedXml: true);
        Assert.Equal(10, d.Items.Count);
        Assert.All(d.Items, i => Assert.True(i.Quantity is 100m or 50m));
        Assert.DoesNotContain(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
    }

    // ── ด่านจริงที่สอง: บรรทัดจากข้อความ (OcrLineSplitGuard) — ข้อความต้นฉบับ+สำเนาให้บรรทัดสองรอบเช่นกัน ──────────────

    private static string SplitJson(int copies)
    {
        var lines = Enumerable.Range(0, copies).SelectMany(_ => Boonsap).Select(l =>
            $"{{\"description\":\"{l.Desc}\",\"quantity\":{l.Qty},\"unit\":\"เมตร\",\"unit_price\":{l.Price},\"amount\":{l.Amount}}}");
        return "{\"lines\":[" + string.Join(",", lines) + "]}";
    }

    [Fact]
    public void LineSplitGuard_บรรทัดสองรอบ_Σ42010_เดิมทิ้งทั้งชุด_ตอนนี้ตัดซ้ำแล้วรับห้าบรรทัด()
    {
        var r = OcrLineSplitGuard.Evaluate(SplitJson(2), subTotal: BoonsapSub, totalAmount: BoonsapTotal);
        Assert.True(r.Accepted);
        Assert.Equal(5, r.Lines.Count);
        Assert.Equal(21005.00m, r.Sum);
        Assert.Equal(new[] { 100m, 50m, 100m, 100m, 100m }, r.Lines.Select(l => l.Quantity).ToArray());
        Assert.NotNull(r.DedupeNote);
        Assert.StartsWith(OcrDuplicateLineGuard.Tag, r.DedupeNote!);
    }

    [Fact]
    public void LineSplitGuard_บรรทัดรอบเดียว_รับเหมือนเดิม_ไม่มีหมายเหตุตัดซ้ำ()
    {
        var r = OcrLineSplitGuard.Evaluate(SplitJson(1), subTotal: BoonsapSub, totalAmount: BoonsapTotal);
        Assert.True(r.Accepted);
        Assert.Equal(5, r.Lines.Count);
        Assert.Null(r.DedupeNote);
    }

    [Fact]
    public void LineSplitGuard_น้ำดื่มสองบรรทัดหัวใบ100_รับทั้งสองบรรทัด_ไม่ตัด()
    {
        const string json = """
        {"lines":[
          {"description":"น้ำดื่ม","quantity":10,"unit":"ขวด","unit_price":5,"amount":50},
          {"description":"น้ำดื่ม","quantity":10,"unit":"ขวด","unit_price":5,"amount":50}
        ]}
        """;
        var r = OcrLineSplitGuard.Evaluate(json, subTotal: 100m, totalAmount: 107m);
        Assert.True(r.Accepted);
        Assert.Equal(2, r.Lines.Count);
        Assert.Null(r.DedupeNote);
    }
}
