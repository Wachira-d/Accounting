using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 14 — ฝ่ายค้านรอบ 5 บน OCR 12 (601225a5) ส่วน OCR: P1-1 บิลสาธารณูปโภคจริงถูกบล็อกแม้คนอนุมัติ · P2-a เอกสารลูกเลี่ยงด่าน ·
/// P2-b ด่านปลดจากบรรทัดที่หมายเหตุเขียนถึง · P2-c ตัวตั้งน้ำมิเตอร์ตัวเดียว (หน่วย + หมวด) · ล็อกรายการ "ตรวจแล้วถูก" ของฝ่ายค้านสองทิศ
/// </summary>
public class OcrQuantityGuardOcr14Tests
{
    private static OcrLinePaper Paper(string? text) => OcrLineQuantityGuard.Read(text);

    private static OcrLiveLine Live(decimal q, decimal p, decimal amt, string desc, decimal disc = 0m, string? unit = null)
        => new(q, p, 0m, disc, amt, 0m) { Description = desc, Unit = unit };

    // ── P1-1: บรรทัดค่ามิเตอร์ไม่ถูกบล็อกคน — 1 × ยอด + จำนวนในคำอธิบาย ───────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าพลังงาน", 4272, 19381.50, null, OcrUtilityKind.Electricity, "ค่าพลังงาน (4,272 หน่วย)")]
    [InlineData("ค่าพลังงาน", 4272, 19381.50, "การไฟฟ้าส่วนภูมิภาค\nค่าพลังงาน 4,272 19,381.50", OcrUtilityKind.Electricity, "ค่าพลังงาน (4,272 หน่วย)")]
    [InlineData("ค่า Ft", 4272, 893.50, "ค่า Ft 4,272 893.50", OcrUtilityKind.Electricity, "ค่า Ft (4,272 หน่วย)")]
    [InlineData("Water charge", 412, 7305.11, "การประปาส่วนภูมิภาค\nWater charge 412 7,305.11", OcrUtilityKind.Water, "Water charge (จำนวน 412)")]
    [InlineData("ค่าน้ำบาดาล", 1251, 8444.27, "ค่าน้ำบาดาล 1,251 8,444.27", OcrUtilityKind.None, "ค่าน้ำบาดาล (1,251 ลบ.ม.)")]
    [InlineData("ค่าน้ำตามมิเตอร์", 37, 512.37, "ค่าน้ำตามมิเตอร์ 37 512.37", OcrUtilityKind.None, "ค่าน้ำตามมิเตอร์ (37 ลบ.ม.)")]
    [InlineData("ค่าน้ำ ห้อง 101", 37, 512.37, "ค่าน้ำ ห้อง 101 37 512.37", OcrUtilityKind.None, "ค่าน้ำ ห้อง 101 (37 ลบ.ม.)")]
    [InlineData("ค่าบริการรายเดือน", 412, 7305.11, "การประปานครหลวง\nค่าบริการรายเดือน 412 7,305.11", OcrUtilityKind.Water, "ค่าบริการรายเดือน (จำนวน 412)")]
    public void บรรทัดค่ามิเตอร์_ไม่มีป้ายหน่วยที่ใช้_ลง1คูณยอด_ไม่บล็อกคน(string desc, int qty, double amount, string? paper, OcrUtilityKind seller, string expectedDesc)
    {
        var amt = (decimal)amount;
        var d = OcrLineQuantityGuard.Decide(qty, null, amt, null, Paper(paper), desc, null, seller);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Equal((1m, amt, (decimal?)null), (d.Quantity!.Value, d.UnitPrice!.Value, d.LineDiscount));   // ไม่มี "ส่วนลด" ที่ระบบแต่ง
        Assert.Equal(expectedDesc, d.Description);                       // "412 เดือน" ไม่ใช่หน่วยที่แต่ง ⇒ "(จำนวน 412)"
        Assert.Equal(OcrLineQuantityGuard.LumpSumUnit, d.Unit);
        Assert.False(d.Blocking);
        Assert.DoesNotContain(OcrLineQuantityGuard.RoundingOverCapCode, d.Note ?? "");
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { Live(1m, amt, amt, d.Description!, unit: d.Unit) }, seller).CanAutoApprove);
    }

    [Theory]
    // AWN/ทรูมูฟ เอช ขายสินค้า — ผู้ขายสาธารณูปโภคอย่างเดียวไม่ใช่หลักฐาน (OCR 11 P1-2) ⇒ คงจำนวน
    [InlineData("เคส iPhone 16", 3, 2775.70, "เคส iPhone 16 3 ชิ้น 2,775.70", OcrUtilityKind.Telecom)]
    [InlineData("เคส iPhone 16", 3, 2775.70, null, OcrUtilityKind.Telecom)]
    [InlineData("หูฟังบลูทูธ", 7, 100.00, null, OcrUtilityKind.Telecom)]
    [InlineData("สติกเกอร์ฉลาก", 4272, 19381.50, null, OcrUtilityKind.Electricity)]     // สินค้าบนบิลผู้ขายไฟฟ้า (คำอธิบายไม่ใช่ค่ามิเตอร์)
    [InlineData("สายไฟ", 12, 299.99, "สายไฟ 12 ม้วน 299.99", OcrUtilityKind.Telecom)]
    [InlineData("ค่าพลังงาน", 12, 299.99, "ค่าพลังงาน 12 ขวด 299.99", OcrUtilityKind.None)]  // หน่วยสินค้าบนกระดาษชนะคำอธิบาย
    public void สินค้าของผู้ขายสาธารณูปโภค_คงจำนวน_ไม่กลายเป็น1คูณยอด(string desc, int qty, double amount, string? paper, OcrUtilityKind seller)
    {
        var d = OcrLineQuantityGuard.Decide(qty, null, (decimal)amount, null, Paper(paper), desc, null, seller);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Null(d.Quantity);                                           // จำนวนตามกระดาษ
    }

    [Fact]
    public void ผลแกะรุ่นก่อน_บรรทัดมิเตอร์ราคาปัด_รันซ้ำตอนสร้างเอกสาร_ลง1คูณยอด_ล้างเศษ_สินค้าคงเดิม()
    {
        // สแกนรุ่น 12 ที่ persist ExtractedItemsJson: ค่าพลังงาน 4,272 × 4.54 − เศษ 13.38 (ธง UnitPriceRoundingResidual) · น็อต 100,000 × 0.01 − 500
        var data = new OcrExtractedData { TotalAmount = 19881.50m, VatAmount = 0m, VendorName = "การไฟฟ้าส่วนภูมิภาค", VendorTaxId = "0994000165501" };
        data.Items.Add(new OcrExtractedLineItem { Description = "ค่าพลังงาน", Quantity = 4272m, UnitPrice = 4.54m, LineDiscountAmount = 13.38m,
            UnitPriceRoundingResidual = 13.38m, Amount = 19381.50m });
        data.Items.Add(new OcrExtractedLineItem { Description = "น็อต M4", Quantity = 100000m, UnitPrice = 0.01m, LineDiscountAmount = 500m,
            UnitPriceRoundingResidual = 500m, Amount = 500m, Unit = "ตัว" });
        OcrService.SanitizeVatSplitArtifacts(data, false, null);
        var pea = data.Items.Single(i => i.Description!.StartsWith("ค่าพลังงาน", StringComparison.Ordinal));
        Assert.Equal(("ค่าพลังงาน (4,272 หน่วย)", 1m, 19381.50m, 19381.50m), (pea.Description, pea.Quantity!.Value, pea.UnitPrice!.Value, pea.Amount!.Value));
        Assert.Null(pea.LineDiscountAmount);
        Assert.Null(pea.UnitPriceRoundingResidual);
        var nut = data.Items.Single(i => i.Description == "น็อต M4");
        Assert.Equal((100000m, 0.01m, 500m, 500m, 500m), (nut.Quantity!.Value, nut.UnitPrice!.Value, nut.LineDiscountAmount!.Value, nut.UnitPriceRoundingResidual!.Value, nut.Amount!.Value));
        Assert.DoesNotContain(data.ReasoningTrace, t => t.Contains("ค่าพลังงาน") && t.StartsWith(OcrLineQuantityGuard.Tag + " ", StringComparison.Ordinal));
    }

    // ── ทางไปต่อตรงกับชนิดบรรทัด · บอกค่าที่ต้องพิมพ์ (ไม่มีปุ่มต่อบรรทัดในเรพ) ────────────────────────────────────────

    [Fact]
    public void เศษเกินเพดานของสินค้า_ทางไปต่อบอกค่าที่พิมพ์ทุกช่อง_ยอดเท่าเดิม_ไม่อ้างว่าแกะใหม่ได้ผลต่าง()
    {
        var d = OcrLineQuantityGuard.Decide(100000m, null, 500m, null, Paper(null), "น็อต M4", "ตัว");
        Assert.Contains(OcrLineQuantityGuard.RoundingOverCapCode, d.Note);
        Assert.Contains("จำนวน 10,000 · หน่วย \"แพ็ก 10\" · ราคา 0.05 · ส่วนลด 0", d.Note);
        Assert.Contains("จำนวน 1 · ราคา 500.00 · ส่วนลด 0", d.Note);
        Assert.Contains("\"แกะใหม่\" ได้ผลเดิม", d.Note);
        Assert.DoesNotContain("เปลี่ยนหน่วยนับเป็นแพ็ก เช่น", d.Note);    // ข้อความเดิมที่ไม่บอกว่าพิมพ์อะไร
        // ค่าที่เสนอลงตัวจริง: 10,000 × 0.05 = 500
        Assert.Equal(500m, Math.Round(10000m * 0.05m, 2, MidpointRounding.AwayFromZero));
        var human = OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { Live(100000m, 0.01m, 500m, "น็อต M4", 500m, "ตัว") });
        Assert.False(human.CanAutoApprove);
        Assert.Contains("\"น็อต M4\"", human.Reason);
        Assert.Contains("หน่วย \"แพ็ก 10\"", human.Reason);
        // ทิศตรงข้าม: พิมพ์ตามที่เสนอ ⇒ ปลด
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { Live(10000m, 0.05m, 500m, "น็อต M4", 0m, "แพ็ก 10") }).CanAutoApprove);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(d.Note, new[] { Live(1m, 500m, 500m, "น็อต M4 (จำนวน 100,000)") }).CanAutoApprove);
        // ไม่มีหน่วยแพ็กที่ลงตัว ⇒ ไม่เสนอแพ็ก (ไม่แต่ง)
        Assert.DoesNotContain("แพ็ก", OcrLineQuantityGuard.OverCapWayForward(4272m, 19381.50m));
    }

    [Fact]
    public void บรรทัดมิเตอร์ที่ผลแกะรุ่นก่อนเก็บเศษเกินเพดาน_ด่านบอกแกะใหม่ได้บรรทัดที่ถูก_หรือค่าที่ต้องพิมพ์()
    {
        // หมายเหตุรุ่น 12 (ร่องรอย ⟦4272×4.54⟧ ไม่ขึ้นกับคำอธิบาย)
        var v12 = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, Paper(null), "สติกเกอร์ฉลาก", "แผ่น").Note!;
        var r = OcrPostingReadiness.EvaluateHumanApproval(v12, new[] { Live(4272m, 4.54m, 19381.50m, "ค่าพลังงาน", 13.38m) }, OcrUtilityKind.Electricity);
        Assert.False(r.CanAutoApprove);                                    // เศษ 13.38 ยังอยู่ในช่องส่วนลดของ draft — ห้ามออกเป็นส่วนลด
        Assert.Contains("บรรทัดมิเตอร์", r.Reason);
        Assert.Contains("แกะใหม่", r.Reason);
        Assert.Contains("จำนวน 1 · ราคา 19,381.50 · ส่วนลด 0 · คำอธิบาย \"ค่าพลังงาน (4,272 หน่วย)\"", r.Reason);
        Assert.DoesNotContain("แพ็ก", r.Reason);
    }

    [Fact]
    public void หมายเหตุซ่อมบนบรรทัดมิเตอร์1คูณยอด_ไม่บล็อก_สินค้ายังบล็อก()
    {
        var pea = OcrLumpSumRepair.ScanNote(7, 1, "ค่าพลังงาน (จำนวน 4,272)", 19381.50m);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(pea, new[] { Live(1m, 19381.50m, 19381.50m, "ค่าพลังงาน (จำนวน 4,272)", unit: "รายการ") }).CanAutoApprove);
        var svc = OcrLumpSumRepair.ScanNote(7, 1, "ค่าบริการรายเดือน (จำนวน 412)", 7305.11m);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(svc, new[] { Live(1m, 7305.11m, 7305.11m, "ค่าบริการรายเดือน (จำนวน 412)", unit: "รายการ") },
            OcrUtilityKind.Water).CanAutoApprove);
        // ทิศตรงข้าม: ค่าบริการเดียวกันแต่ไม่ใช่บิลสาธารณูปโภค ⇒ ยังบล็อก · สินค้า ⇒ ยังบล็อก
        Assert.False(OcrPostingReadiness.EvaluateHumanApproval(svc, new[] { Live(1m, 7305.11m, 7305.11m, "ค่าบริการรายเดือน (จำนวน 412)", unit: "รายการ") }).CanAutoApprove);
        var goods = OcrLumpSumRepair.ScanNote(7, 1, "เคส iPhone 16 (จำนวน 3)", 2775.70m);
        Assert.False(OcrPostingReadiness.EvaluateHumanApproval(goods, new[] { Live(1m, 2775.70m, 2775.70m, "เคส iPhone 16 (จำนวน 3)", unit: "รายการ") },
            OcrUtilityKind.Telecom).CanAutoApprove);
    }

    // ── P2-b: ปลดจากบรรทัดที่หมายเหตุเขียนถึง · ข้อความตรงกับเงื่อนไขปลด ─────────────────────────────────────────────

    [Fact]
    public void ด่านดูบรรทัดที่หมายเหตุเขียนถึง_ไม่ใช่บรรทัดอื่นที่ค่าบังเอิญตรง()
    {
        var w = OcrLumpSumRepair.ScanNote(7, 1, "น้ำดื่ม 600ml (12 ขวด)", 300m);
        // ฝ่ายค้าน probe fixed-desc-renamed-plus-coincident: เปลี่ยนชื่อแล้วแก้เป็น 12 × 25 + "ค่าขนส่ง" 1 × 300 ⇒ ปลด (เดิมติด และข้อความอ้างบรรทัดที่ไม่มีแล้ว)
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(w, new[] { Live(12m, 25m, 300m, "น้ำดื่ม 600ml"), Live(1m, 300m, 300m, "ค่าขนส่ง") }).CanAutoApprove);
        // ตรวจแล้วถูก (ห้ามถดถอย): บรรทัดเดิมแก้แล้ว + บรรทัดอื่น 1 × 300
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(w, new[] { Live(12m, 25m, 300m, "น้ำดื่ม 600ml (12 ขวด)"), Live(1m, 300m, 300m, "ค่าขนส่ง") }).CanAutoApprove);
        // ทิศตรงข้าม: ตัดท้ายอย่างเดียวยัง 1 × 300 ⇒ ยังติด · ข้อความอ้างบรรทัดที่ยังอยู่จริง
        var still = OcrPostingReadiness.EvaluateHumanApproval(w, new[] { Live(1m, 300m, 300m, "น้ำดื่ม 600ml"), Live(1m, 300m, 300m, "ค่าขนส่ง") });
        Assert.False(still.CanAutoApprove);
        Assert.Contains("\"น้ำดื่ม 600ml\"", still.Reason);
        Assert.DoesNotContain("ค่าขนส่ง", still.Reason);
        // แก้แค่หน่วยไม่ใช่การแก้บรรทัด (จำนวนสินค้ายังหาย) ⇒ ยังติด — และข้อความไม่บอกให้ "แก้หน่วย"
        var unitOnly = OcrPostingReadiness.EvaluateHumanApproval(w, new[] { Live(1m, 300m, 300m, "น้ำดื่ม 600ml (12 ขวด)", unit: "แพ็ก") });
        Assert.False(unitOnly.CanAutoApprove);
        Assert.DoesNotContain("หน่วย", unitOnly.Reason);
        Assert.Contains("จำนวน/ราคา", unitOnly.Reason);
    }

    // ── P2-a: เอกสารลูก (คัดลอก/แปลง/รายการเกิดซ้ำ) ถือค่าที่ระบบตั้ง ⇒ บล็อกตามค่า ────────────────────────────────────

    [Fact]
    public void เอกสารลูกที่ก๊อปค่าที่ระบบตั้ง_บล็อกตามค่า_แก้แล้วปลด_ค่าบังเอิญไม่ครบไม่บล็อก()
    {
        var note = OcrLineQuantityGuard.Decide(100000m, null, 500m, null, Paper(null), "น็อต M4", "ตัว").Note!;
        Assert.Equal(500m, OcrLineQuantityGuard.NoteAmount(note));
        // DocumentCloneController ก๊อป Quantity/UnitPrice/DiscountAmount ⇒ บรรทัดลูกมีรูปเดียวกัน
        Assert.True(OcrLineQuantityGuard.MayCarryHumanBlock(100000m, 0.01m, 500m, 500m, "น็อต M4", "ตัว"));
        var child = OcrPostingReadiness.EvaluateHumanApproval(note, new[] { Live(100000m, 0.01m, 500m, "น็อต M4 (คัดลอก)", 500m, "ตัว") }, carried: true);
        Assert.False(child.CanAutoApprove);
        Assert.StartsWith(OcrPostingReadiness.CarriedLead, child.Reason);
        // แก้บรรทัดในใบลูก ⇒ ปลด
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(note, new[] { Live(10000m, 0.05m, 500m, "น็อต M4", 0m, "แพ็ก 10") }, carried: true).CanAutoApprove);
        // ทิศตรงข้าม: ใบที่บังเอิญ 100,000 × 0.01 แต่ยอด 1,000 (ไม่มีเศษ) ⇒ ไม่ใช่ค่าของหมายเหตุ
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(note, new[] { Live(100000m, 0.01m, 1000m, "น็อต M4") }, carried: true).CanAutoApprove);
        // หมายเหตุซ่อม: ลูกของบรรทัด 1 × 300 "(12 ขวด)"
        var lump = OcrLumpSumRepair.ScanNote(7, 1, "น้ำดื่ม 600ml (12 ขวด)", 300m);
        Assert.Equal(300m, OcrLineQuantityGuard.NoteAmount(lump));
        Assert.True(OcrLineQuantityGuard.MayCarryHumanBlock(1m, 300m, 0m, 300m, "น้ำดื่ม 600ml (12 ขวด)", OcrLineQuantityGuard.LumpSumUnit));
        Assert.False(OcrPostingReadiness.EvaluateHumanApproval(lump, new[] { Live(1m, 300m, 300m, "น้ำดื่ม 600ml (12 ขวด)", unit: "รายการ") }, carried: true).CanAutoApprove);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(lump, new[] { Live(1m, 300m, 300m, "ค่าขนส่ง") }, carried: true).CanAutoApprove);
    }

    [Theory]
    [InlineData(7, 14.29, 0.03, 100.00, "สินค้า", "ชิ้น")]           // เศษใต้เพดาน
    [InlineData(1, 1330.00, 293.00, 1037.00, "สินค้า", "ชิ้น")]       // ส่วนลดจริงบนกระดาษ
    [InlineData(10, 100.00, 100.00, 900.00, "สินค้า", "ชิ้น")]        // ส่วนลด 10% จริง (ราคาไม่ใช่ ยอด ÷ จำนวน)
    [InlineData(1, 19381.50, 0, 19381.50, "ค่าไฟฟ้า (4,272 หน่วย)", "รายการ")]   // บรรทัดมิเตอร์ของตัวกัน
    [InlineData(1, 300.00, 0, 300.00, "ค่าขนส่ง", null)]
    [InlineData(2, 10.00, 0, 20.00, "ปากกา", "ด้าม")]
    public void ใบทั่วไปไม่ผ่านตัวกรองเอกสารลูก_ไม่มีqueryเพิ่ม(int q, double p, double disc, double amt, string desc, string? unit)
        => Assert.False(OcrLineQuantityGuard.MayCarryHumanBlock(q, (decimal)p, (decimal)disc, (decimal)amt, desc, unit));

    // ── P2-c: ตัวตั้งน้ำมิเตอร์ตัวเดียว ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ค่าน้ำ", true)]
    [InlineData("ค่าน้ำประปา", true)]
    [InlineData("ค่าใช้น้ำ", true)]
    [InlineData("ค่าน้ำ ประจำเดือน ต.ค. 2569", true)]
    [InlineData("ค่าน้ำงวด 10/2569", true)]
    [InlineData("ค่าน้ำ-ไฟ", true)]
    [InlineData("ค่าน้ำตามมิเตอร์", true)]
    [InlineData("ค่าน้ำ ห้อง 101", true)]
    [InlineData("ค่าน้ำห้อง 101", true)]
    [InlineData("ค่าน้ำส่วนกลาง", true)]
    [InlineData("ค่าน้ำบาดาล", true)]
    [InlineData("ค่าน้ำดิบ", true)]
    [InlineData("ค่าน้ำเสีย", true)]
    [InlineData("ค่าบำบัดน้ำเสีย", true)]
    [InlineData("Water Charge (25 m3)", true)]
    [InlineData("Water supply charge", true)]
    // ไม่ใช่น้ำ (รายการเดิมของทีม OCR 12 — ห้ามถดถอย)
    [InlineData("ค่าน้ำขวด 600ml", false)]
    [InlineData("ค่าน้ำดื่ม", false)]
    [InlineData("ค่าน้ำแข็ง", false)]
    [InlineData("ค่าน้ำมัน", false)]
    [InlineData("ค่าน้ำมันดีเซล", false)]
    [InlineData("ค่าน้ำมันเครื่อง", false)]
    [InlineData("ค่าน้ำหนักเกิน", false)]
    [InlineData("ค่าน้ำถัง 20 ลิตร", false)]
    [InlineData("ค่าน้ำกระป๋อง", false)]
    [InlineData("ค่าน้ำโค้ก", false)]
    [InlineData("ค่าน้ำเกลือ", false)]
    [InlineData("น้ำดื่มถัง", false)]
    [InlineData("ท่อประปา PVC", false)]
    [InlineData("ค่ารักษามาตร", false)]
    [InlineData("Water charge", false)]               // ไม่มีผู้ขาย ⇒ ไม่รู้ = ไม่ใช่
    [InlineData("Drinking water 600ml", false)]
    public void น้ำมิเตอร์_ตัวตั้งเดียว(string text, bool expected)
    {
        Assert.Equal(expected, OcrMeteredWater.IsMeteredWater(text));
        Assert.Equal(expected, UnitInferrer.IsWaterSupply(text));          // UnitInferrer ส่งต่อตัวตั้ง (ไม่มีสำเนา)
        // ทางลบของกฎหมวด = คำต่อท้ายชุดเดียวกัน: ข้อความขึ้นต้น "ค่าน้ำ" ⇒ เป็นน้ำ ⇔ วลียกเว้นไม่ตัด
        if (text.StartsWith("ค่าน้ำ", StringComparison.Ordinal) && !text.StartsWith("ค่าน้ำประปา", StringComparison.Ordinal))
            Assert.Equal(expected, !Regex.IsMatch(text.ToLowerInvariant(), "^" + OcrMeteredWater.NotMeteredWaterPhrase));
    }

    [Theory]
    [InlineData("Water charge", OcrUtilityKind.Water, true)]
    [InlineData("Water usage 25", OcrUtilityKind.Water, true)]
    [InlineData("Drinking water 600ml", OcrUtilityKind.Water, false)]
    [InlineData("Water charge", OcrUtilityKind.Electricity, false)]
    public void น้ำมิเตอร์_คำอังกฤษนับเมื่อผู้ขายเป็นการประปา(string text, OcrUtilityKind seller, bool expected)
        => Assert.Equal(expected, OcrMeteredWater.IsMeteredWater(text, seller));

    [Theory]
    [InlineData("ค่าน้ำถัง 20 ลิตร", "ถัง")]           // 7 ถัง × 20 ลิตร — เดิม "ลิตร" (พิมพ์ "7 ลิตร" = หน่วยที่แต่ง)
    [InlineData("ค่าน้ำดื่มถัง 20 ลิตร", "ถัง")]
    [InlineData("น้ำดื่มถัง 20 ลิตร", "ถัง")]
    [InlineData("น้ำมันเครื่อง แกลลอน 4 ลิตร", "แกลลอน")]
    [InlineData("น้ำยาล้างจาน 3.8 ลิตร", null)]         // สเปกขนาด ⇒ ไม่รู้หน่วยนับ (ไม่แต่ง)
    [InlineData("ค่าน้ำมันดีเซล", "ลิตร")]             // ตรวจแล้วถูก
    [InlineData("ดีเซล 20 ลิตร", "ลิตร")]
    [InlineData("ค่าน้ำบาดาล", "ลบ.ม.")]
    [InlineData("ค่าน้ำ ห้อง 101", "ลบ.ม.")]
    [InlineData("ค่าพลังงาน", "หน่วย")]
    [InlineData("ค่า Ft", "หน่วย")]
    [InlineData("ค่าน้ำขวด", null)]
    public void หน่วยที่อนุมาน_ไม่แต่งหน่วย(string desc, string? unit) => Assert.Equal(unit, UnitInferrer.Infer(desc));

    [Fact]
    public void ค่าน้ำถัง_7ถัง_ไม่ได้หน่วยลิตร_ไม่เป็นบรรทัดมิเตอร์()
    {
        var d = OcrLineQuantityGuard.Decide(7m, null, 100m, null, Paper("ค่าน้ำถัง 20 ลิตร 7 100.00"), "ค่าน้ำถัง 20 ลิตร", null);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Null(d.Quantity);
        Assert.Equal("ถัง", UnitInferrer.Resolve(null, "ค่าน้ำถัง 20 ลิตร"));
        Assert.Equal("ถัง", UnitInferrer.Resolve("ชิ้น", "ค่าน้ำถัง 20 ลิตร"));
    }

    private static ExpenseCategoryResolver.CategoryResult? Category(string? vendor, string desc, decimal amount)
    {
        var raw = ThaiTextNormalizer.Normalize($"{vendor}\nใบเสร็จรับเงิน\nเลขที่ RC-001\n{desc} {amount:N2}\nรวมทั้งสิ้น {amount:N2}\n");
        return ExpenseCategoryResolver.Resolve(vendorName: vendor, headerDescription: null, lineDescriptions: new List<string?> { desc }, rawText: raw,
            zeroAmountLineDescriptions: new List<string?>(), pricedLines: new List<OcrPricedLine> { new(desc, amount) }, diagnostics: new List<string>(),
            minorityWhtParts: new List<OcrMinorityWhtPart>());
    }

    [Theory]
    [InlineData("บริษัท ขนส่งดี จำกัด", "ค่าน้ำหนักเกิน", false)]   // เดิม 54320 ค่าน้ำประปา (ฝ่ายค้านรอบ 5 P2-c)
    [InlineData("ร้านน้ำดื่ม", "ค่าน้ำขวด 600ml", false)]
    [InlineData("ร้านน้ำดื่ม", "ค่าน้ำถัง 20 ลิตร", false)]
    [InlineData("ร้านขายของ", "ค่าน้ำมันเครื่อง", false)]
    [InlineData("หอพักสุขใจ", "ค่าน้ำ ห้อง 101", true)]   // ทิศตรงข้าม: ค่าน้ำมิเตอร์ยังเป็นค่าน้ำประปา
    [InlineData("หอพักสุขใจ", "ค่าน้ำ", true)]
    [InlineData("การประปาส่วนภูมิภาค", "ค่าน้ำประปา", true)]
    [InlineData("หอพักสุขใจ", "ค่าน้ำตามมิเตอร์", true)]
    public void กฎหมวดค่าน้ำประปา_ใช้ตัวตั้งเดียวกับหน่วย(string vendor, string desc, bool water)
        => Assert.Equal(water, Category(vendor, desc, 1400m)?.AccountCode == "54320");

    // ── ตรวจแล้วถูก (ฝ่ายค้านรอบ 5) — ห้ามถดถอย ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void รายการตรวจแล้วถูก_รอบ5_ไม่ถดถอย()
    {
        // เกินเพดาน: 100,000 × 0.01 − 500 · ฐาน VAT/ยอด = 500 · ต้นทุนต่อหน่วย = Amount ÷ Qty = 0.005
        var d = OcrLineQuantityGuard.Decide(100000m, null, 500m, null, Paper(null), "น็อต M4", "ตัว");
        Assert.Equal((0.01m, 500m), (d.UnitPrice!.Value, d.LineDiscount!.Value));
        var line = new DocumentLineRequest(Description: "น็อต M4", Quantity: 100000m, Unit: "ตัว", UnitPrice: 0.01m, DiscountPercent: 0m,
            VatRate: 7m, WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: 500m);
        var t = DocumentService.PreviewTotals(new[] { line }, pricesIncludeVat: false, 0m);
        Assert.Equal((500m, 35m), (t.Net, t.Vat));
        Assert.Equal(0.005m, t.Net / 100000m);
        // ใบ OCR ปกติที่มี [LINE-QTY] ธรรมดา · ใบไม่ใช่ OCR ⇒ ด่านคนไม่บล็อก
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval("[LINE-QTY] จำนวน 7 · ยอด 100.00 · ค่าที่ระบบตั้ง ⟦7×14.29⟧", new[] { Live(7m, 14.29m, 100m, "สินค้า", 0.03m) }).CanAutoApprove);
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(null, new[] { Live(1m, 300m, 300m, "ค่าขนส่ง") }).CanAutoApprove);
        // น้ำ: PWA "ค่าน้ำประปา" 412 ⇒ บรรทัดมิเตอร์ · ค่าน้ำ/ค่าน้ำงวด ⇒ ลบ.ม. · ค่าน้ำขวด/ดื่ม/มัน ไม่ใช่น้ำ
        var pwa = OcrLineQuantityGuard.Decide(412m, null, 7305.11m, null, Paper("การประปาส่วนภูมิภาค\nค่าน้ำประปา 412 7,305.11"), "ค่าน้ำประปา", null, OcrUtilityKind.Water);
        Assert.Equal("ค่าน้ำประปา (412 ลบ.ม.)", pwa.Description);
        foreach (var w in new[] { "ค่าน้ำ", "ค่าน้ำประปา", "ค่าใช้น้ำ", "ค่าน้ำ ประจำเดือน", "ค่าน้ำงวด", "ค่าน้ำ-ไฟ" })
            Assert.Equal("ลบ.ม.", UnitInferrer.Infer(w));
        foreach (var nw in new[] { "ค่าน้ำขวด", "ค่าน้ำดื่ม", "ค่าน้ำมัน" })
            Assert.False(OcrMeteredWater.IsMeteredWater(nw));
        // PEA จริง (ป้าย "หน่วยที่ใช้") ยังเป็น 1 × 19,381.50
        var pea = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, Paper("ค่าไฟฟ้า ประจำเดือน 08/2569 หน่วยที่ใช้ 4272.00 19,381.50"),
            "ค่าไฟฟ้า ประจำเดือน 08/2569", null, OcrUtilityKind.Electricity);
        Assert.Equal(("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", 1m, 19381.50m), (pea.Description, pea.Quantity!.Value, pea.UnitPrice!.Value));
    }
}
