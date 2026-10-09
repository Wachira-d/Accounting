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
    public void กระดาษพิมพ์รายการซ้ำจริง_น้ำดื่มสองบรรทัดหัวใบ100_คงทุกแถว()
    {
        // ชุดเดียว × 2 ไม่เข้ารูป "สำเนาทั้งหน้า" (ฝ่ายค้านข้อ 2) ⇒ ไม่แตะตั้งแต่ขั้นรูปแบบ — ขั้นยุบเดิมรวมเป็น 20 ขวดเหมือนเดิม
        var rows = new List<OcrCandidateRow> { new("น้ำดื่ม", 10m, 5.00m, 50.00m), new("น้ำดื่ม", 10m, 5.00m, 50.00m) };
        var d = OcrDuplicateLineGuard.Decide(rows, 100m, 7m, 107m);
        Assert.False(d.Deduped);
        Assert.Equal(new[] { 0, 1 }, d.KeepIndexes.ToArray());
        Assert.Equal(0, d.DroppedCount);
        Assert.Contains("ไม่มีแถวอื่นเทียบ", d.Reason);
    }

    [Fact]
    public void กระดาษพิมพ์สองรายการซ้ำจริงทั้งคู่_Σทุกแถวตรงหัวใบ_คงทุกแถว()
    {
        // น้ำดื่ม 50 + ขนม 30 พิมพ์สองรอบจริง (ซื้อสองชุด) · หัวใบ 160 = Σ ทุกแถว ⇒ ซ้ำจริง (เข้ารูปสำเนาแต่หัวใบบอกว่าไม่ใช่)
        var rows = new List<OcrCandidateRow>
        {
            new("น้ำดื่ม", 10m, 5m, 50m), new("ขนม", 1m, 30m, 30m), new("น้ำดื่ม", 10m, 5m, 50m), new("ขนม", 1m, 30m, 30m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 160m, 11.20m, 171.20m);
        Assert.False(d.Deduped);
        Assert.Equal(4, d.KeepIndexes.Count);
        Assert.Contains("ซ้ำจริง", d.Reason);
    }

    [Fact]
    public void ไม่มียอดหัวใบ_เข้ารูปสำเนาทั้งหน้า_แต่ไม่เดา_คงทุกแถว()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("น้ำดื่ม", 10m, 5m, 50m), new("ขนม", 1m, 30m, 30m), new("น้ำดื่ม", 10m, 5m, 50m), new("ขนม", 1m, 30m, 30m),
        };
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
    public void หน้าสองยกมาแค่บางแถว_ซ้ำไม่เท่ากันทุกชุด_ไม่ใช่รูปแบบสำเนาทั้งหน้า_ไม่แตะ()
    {
        // ฝ่ายค้านข้อ 2/3: 5 แถว + ยกมา 3 แถวแรก ⇒ ชุดซ้ำ ×2 สามชุด · ชุดเดี่ยวสองชุด — นอกรูป "สำเนาทั้งหน้า" ⇒ คงทุกแถว
        // (ขั้นยุบเดิมยังรวมคู่ที่เหมือนกัน ⇒ ×2 บางบรรทัด แล้ว [Σ-GAP] ฟ้อง — ยอมรับ · จดในตารางครอบคลุมข้อ 9)
        var rows = BoonsapRows(1);
        rows.AddRange(BoonsapRows(1).Take(3));
        var d = OcrDuplicateLineGuard.Decide(rows, BoonsapSub, BoonsapVat, BoonsapTotal);
        Assert.False(d.Deduped);
        Assert.Equal(8, d.KeepIndexes.Count);
        Assert.Contains("ไม่ใช่รูปแบบสำเนาทั้งหน้า", d.Reason);
    }

    [Fact]
    public void หัวใบอ่านเพี้ยนเป็นยอดบรรทัดเดียว_น้ำดื่ม20บาทสามแถว_หัวใบ20_ไม่มีแถวอื่นเทียบ_ไม่แตะ()
    {
        // ฝ่ายค้านข้อ 2: ถ้าตัด จะเหลือ 1 แถว Σ 20 = หัวใบ(ที่อ่านผิด) แล้วเอกสารดู "สะอาด" — รูปแบบ 1 ชุด × k ไม่ใช่สำเนาทั้งหน้า ⇒ ให้คนดู
        var rows = Enumerable.Repeat(new OcrCandidateRow("น้ำดื่ม", 1m, 20m, 20m), 3).ToList();
        var d = OcrDuplicateLineGuard.Decide(rows, 20m, 0m, 20m);
        Assert.False(d.Deduped);
        Assert.Equal(3, d.KeepIndexes.Count);
        Assert.Contains("ไม่มีแถวอื่นเทียบ", d.Reason);
    }

    [Fact]
    public void สองแถวซ้ำบวกหนึ่งแถวเดี่ยว_หัวใบอ่านเพี้ยนเป็น100_ซ้ำไม่เท่ากัน_ไม่แตะ()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("น้ำดื่ม", 1m, 50m, 50m), new("น้ำดื่ม", 1m, 50m, 50m), new("ข้าว", 1m, 50m, 50m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 100m, 0m, 100m);   // หัวใบจริง 150 แต่อ่านได้ 100
        Assert.False(d.Deduped);
        Assert.Equal(3, d.KeepIndexes.Count);
    }

    // ── ฝ่ายค้านข้อ 1: ใบที่พิมพ์ส่วนลดท้ายบิล — Σ บรรทัดก่อนลด = หัวใบ + ส่วนลด ──────────────────────────────────────────

    [Fact]
    public void ต้นฉบับบวกสำเนา_มีส่วนลดท้ายบิล1005_ฐาน20000_ไม่ส่งส่วนลดจะไม่ตรงทั้งสองทาง_ส่งส่วนลดแล้วตัดได้()
    {
        // กระดาษ: Σ บรรทัด 21,005 · ส่วนลด 1,005 · ก่อน VAT 20,000 · VAT 1,400 · รวม 21,400 — engine คืนสองรอบ (42,010)
        var without = OcrDuplicateLineGuard.Decide(BoonsapRows(2), 20000m, 1400m, 21400m);
        Assert.False(without.Deduped);                    // ไม่รู้ส่วนลด ⇒ ไม่เดา (คงเดิม — แล้ว [Σ-GAP] ฟ้อง)
        var with = OcrDuplicateLineGuard.Decide(BoonsapRows(2), 20000m, 1400m, 21400m, headerDiscount: 1005m);
        Assert.True(with.Deduped);
        Assert.Equal(5, with.DroppedCount);
        Assert.Contains("ยอดก่อน VAT + ส่วนลด", with.Reason);
    }

    [Fact]
    public void ใบมีส่วนลด_กระดาษพิมพ์แถวเหมือนกันสองแถวจริง_Σทุกแถวลบส่วนลดเท่ากับฐาน_คงทุกแถว()
    {
        // A 600 × 2 แถว + B 300 ×2 แถว (Σ 1,800) · ส่วนลด 100 · ก่อน VAT 1,700 ⇒ Σ ทุกแถว = ฐาน + ส่วนลด ⇒ ซ้ำจริง
        var rows = new List<OcrCandidateRow>
        {
            new("A", 1m, 600m, 600m), new("B", 1m, 300m, 300m), new("A", 1m, 600m, 600m), new("B", 1m, 300m, 300m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 1700m, 119m, 1819m, headerDiscount: 100m);
        Assert.False(d.Deduped);
        Assert.Contains("ซ้ำจริง", d.Reason);
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
    public void คำอธิบายต่างแค่ช่องว่างและตัวพิมพ์_ถือว่าแถวเดียวกัน()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("สายไฟ  THW   1x6 ", 100m, 36.18m, 3618m), new("ปูน", 1m, 100m, 100m),
            new("สายไฟ thw 1X6", 100m, 36.18m, 3618m), new("ปูน", 1m, 100m, 100m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 3718m, 260.26m, 3978.26m);
        Assert.True(d.Deduped);
        Assert.Equal(new[] { 0, 1 }, d.KeepIndexes.ToArray());
        Assert.Equal(2, d.DroppedCount);
    }

    [Fact]
    public void แถวไม่มีคำอธิบาย_ไม่ถือว่าซ้ำกับใคร_ทำให้ไม่เข้ารูปสำเนาทั้งหน้า_ไม่แตะ()
    {
        var rows = new List<OcrCandidateRow>
        {
            new("A", 1m, 100m, 100m), new("", 1m, 100m, 100m), new("A", 1m, 100m, 100m), new(null, 1m, 100m, 100m),
        };
        var d = OcrDuplicateLineGuard.Decide(rows, 300m, 21m, 321m);
        Assert.False(d.Deduped);
        Assert.Equal(4, d.KeepIndexes.Count);
    }

    [Fact]
    public void EffectiveAmount_สูตรเดียวกับEffAmtของSanitize_ยอด0ใช้ราคาคูณจำนวน()
    {
        Assert.Equal(300m, OcrDuplicateLineGuard.EffectiveAmount(300m, 1m, 999m));
        Assert.Equal(250m, OcrDuplicateLineGuard.EffectiveAmount(0m, 25m, 10m));
        Assert.Equal(250m, OcrDuplicateLineGuard.EffectiveAmount(null, 25m, 10m));
        Assert.Equal(25m, OcrDuplicateLineGuard.EffectiveAmount(null, 25m, null));
    }

    [Fact]
    public void แท็กDUP_ROWS_อยู่ในแท็กห้ามอนุมัติเอง_และหยุดอนุมัติเองจริง()
    {
        // ฝ่ายค้านข้อ 2: ระบบตัดแถวเองแล้ว Σ ตรง ⇒ ไม่มี [Σ-GAP]/[MATH] — ต้องมีแท็กของตัวเองหยุดการอนุมัติ
        Assert.Contains(OcrPostingReadiness.BlockingTags, t => t.Tag == OcrDuplicateLineGuard.Tag);
        var v = OcrPostingReadiness.Evaluate("[Tier] Azure DI\n" + OcrDuplicateLineGuard.Tag + " ตัดแถวที่ถูกอ่านซ้ำ 5 แถว", hasUsableDate: true);
        Assert.False(v.CanAutoApprove);
        Assert.Contains(OcrScanSnapshot.DecisionNoteTags, t => t == OcrDuplicateLineGuard.Tag);
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
    public void Sanitize_น้ำดื่ม20บาทสามแถว_หัวใบอ่านเพี้ยนเป็น20_ไม่ตัด_ขั้นยุบเดิมรวมเป็น60_ให้ด่านΣฟ้อง()
    {
        var d = new OcrExtractedData { SubTotal = 20m, VatAmount = 0m, TotalAmount = 20m };
        for (var i = 0; i < 3; i++) d.Items.Add(new() { Description = "น้ำดื่ม", Quantity = 1m, UnitPrice = 20m, Amount = 20m });
        OcrService.SanitizeVatSplitArtifacts(d);
        var it = Assert.Single(d.Items);
        Assert.Equal(3m, it.Quantity);
        Assert.Equal(60m, it.Amount);
        Assert.DoesNotContain(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
    }

    [Fact]
    public void Sanitize_ต้นฉบับบวกสำเนาที่มีส่วนลดท้ายบิล_ใช้DiscountAmountของสแกน_ตัดได้()
    {
        var d = BoonsapScan(copies: 2);
        d.SubTotal = 20000m; d.VatAmount = 1400m; d.TotalAmount = 21400m; d.DiscountAmount = 1005m;
        OcrService.SanitizeVatSplitArtifacts(d);
        Assert.Equal(5, d.Items.Count);
        Assert.Equal(21005.00m, d.Items.Sum(i => i.Amount!.Value));
        Assert.Contains(d.ReasoningTrace, t => t.StartsWith(OcrDuplicateLineGuard.Tag));
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
