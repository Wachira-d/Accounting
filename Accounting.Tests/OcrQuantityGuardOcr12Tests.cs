using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม OCR 12 (ฝ่ายค้านรอบ 3 บน 02c30a2a): P1-5 น้ำประปาเป็น whitelist · P2-1 บรรทัดเศษเกินเพดานสอดคล้อง (round(จำนวน × ราคา) − ส่วนลด = ยอด) และ Update คิดซ้ำได้ยอดเดิม ·
/// P2-2 ตัวตรวจซ่อมใช้บริบทเดียวกับตัวเขียน (บิลมิเตอร์จริงไม่ถูกฟ้อง) · P1-4 ด่านอนุมัติโดยคนของหมายเหตุซ่อม (มีจริง + ทิศตรงข้าม) · P2-3 คีย์ตัวเรียนที่ถูกตัด 50 ตัว +
/// คำเตือนท้ายที่อาจเป็นขนาดแพ็ก · ทุกข้อสองทิศ
/// </summary>
public class OcrQuantityGuardOcr12Tests
{
    // ── P1-5: น้ำประปา = whitelist ──
    [Theory]
    [InlineData("ค่าน้ำ", true)]
    [InlineData("ค่าน้ำประปา", true)]
    [InlineData("น้ำประปา", true)]
    [InlineData("ค่าใช้น้ำ", true)]
    [InlineData("ค่าน้ำ ประจำเดือน 08/2569", true)]
    [InlineData("ค่าน้ำประจำเดือน ต.ค.", true)]
    [InlineData("ค่าน้ำ 25 ลบ.ม.", true)]
    [InlineData("ค่าน้ำค่าไฟ", true)]
    [InlineData("ค่าน้ำ ค่าไฟ", true)]
    [InlineData("ค่าน้ำ/ค่าไฟ", true)]
    [InlineData("มาตรวัดน้ำ เลขอ่านครั้งก่อน 1200 ครั้งหลัง 1225", true)]
    [InlineData("Water supply charge", true)]
    // ทิศตรงข้าม — ห้ามเป็นน้ำประปา (ขายเป็นขวด/ถัง/กระป๋อง/กก./ลิตร)
    [InlineData("ค่าน้ำขวด 600ml", false)]
    [InlineData("ค่าน้ำถัง 20 ลิตร", false)]
    [InlineData("ค่าน้ำกระป๋อง", false)]
    [InlineData("ค่าน้ำโค้ก", false)]
    [InlineData("ค่าน้ำเกลือ", false)]
    [InlineData("ค่าน้ำหนักเกิน", false)]
    [InlineData("ค่าน้ำดื่ม", false)]
    [InlineData("ค่าน้ำแข็ง", false)]
    [InlineData("ค่าน้ำมัน", false)]
    [InlineData("ค่าน้ำมันดีเซล", false)]
    [InlineData("น้ำดื่มถัง", false)]
    [InlineData("ท่อประปา PVC", false)]
    [InlineData("", false)]
    public void น้ำประปา_นับเฉพาะคำที่ยืนยันได้(string text, bool expected)
        => Assert.Equal(expected, UnitInferrer.IsWaterSupply(text));

    [Theory]
    [InlineData("ค่าน้ำ", "ลบ.ม.")]
    [InlineData("ค่าน้ำประปา", "ลบ.ม.")]
    [InlineData("ค่าน้ำมันดีเซล", "ลิตร")]          // ตรวจแล้วถูก (ห้ามถดถอย)
    [InlineData("ค่าน้ำขวด", null)]
    [InlineData("ค่าน้ำหนักเกิน", null)]
    [InlineData("ค่าน้ำโค้ก", null)]
    public void หน่วยที่อนุมาน(string desc, string? unit) => Assert.Equal(unit, UnitInferrer.Infer(desc));

    [Theory]
    [InlineData("ค่าน้ำขวด 600ml", 12, 299)]
    [InlineData("ค่าน้ำหนักเกิน", 3, 250)]
    [InlineData("ค่าน้ำถัง 20 ลิตร", 7, 100)]
    [InlineData("ค่าน้ำกระป๋อง", 7, 100)]
    [InlineData("ค่าน้ำโค้ก", 7, 100)]
    [InlineData("ค่าน้ำเกลือ", 7, 100)]
    public void สินค้าชื่อค่าน้ำ_คงจำนวน_ไม่กลายเป็น1คูณยอด_ลบม(string desc, int qty, int amt)
    {
        var d = OcrLineQuantityGuard.Decide(qty, null, amt, null, null, desc, null);
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Null(d.Quantity);
        Assert.DoesNotContain("ลบ.ม.", d.Description ?? "");
    }

    [Fact]
    public void ค่าน้ำประปาจริง_ยังเป็นบรรทัดมิเตอร์()
    {
        var d = OcrLineQuantityGuard.Decide(25m, null, 246.13m, null, null, "ค่าน้ำประปา", null);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        Assert.Equal("ค่าน้ำประปา (25 ลบ.ม.)", d.Description);
    }

    // ── ตรวจแล้วถูก (ฝ่ายค้านรอบ 3) — ล็อกไว้ไม่ให้ถดถอย ──
    [Fact]
    public void รายการตรวจแล้วถูก_ไม่ถดถอย()
    {
        // คอนกรีต 2.75 ลบ.ม. คงจำนวน
        var concrete = OcrLineQuantityGuard.Decide(2.75m, null, 1500m, null, OcrLineQuantityGuard.Read("คอนกรีตผสมเสร็จ 2.75 ลบ.ม. 1,500.00"),
            "คอนกรีตผสมเสร็จ", "ลบ.ม.");
        Assert.NotEqual(OcrLineQuantityAction.QuantityInDescription, concrete.Action);
        Assert.Null(concrete.Quantity);
        // PEA ⇒ (4,272 หน่วย)
        var pea = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, OcrLineQuantityGuard.Read("ค่าไฟฟ้า 4,272 หน่วย 19,381.50"),
            "ค่าไฟฟ้า ประจำเดือน 08/2569", null, OcrUtilityKind.Electricity);
        Assert.Equal("ค่าไฟฟ้า ประจำเดือน 08/2569 (4,272 หน่วย)", pea.Description);
        // Flash Drive (64 GB) ไม่ถูกตัด
        Assert.Equal("Flash Drive (64 GB)", OcrLineQuantityGuard.DescriptionForMatching("Flash Drive (64 GB)", 1m, OcrLineQuantityGuard.LumpSumUnit));
        // ดีเซล ⇒ ลิตร
        Assert.Equal("ลิตร", UnitInferrer.Infer("ค่าน้ำมันดีเซล"));
    }

    // ── P2-1: บรรทัดเศษเกินเพดานสอดคล้อง + Update คิดซ้ำได้ยอดเดิม ──
    [Theory]
    [InlineData(100000, 500.00, 0)]
    [InlineData(100000, 500.00, 7)]
    [InlineData(3611, 16351.48, 7)]
    [InlineData(4272, 19381.50, 0)]
    [InlineData(7, 100.00, 7)]          // ใต้เพดาน (ส่วนต่าง 3 สตางค์) — เส้นเดิม
    public void เศษปัด_บรรทัดสอดคล้อง_บันทึกซ้ำผ่านสูตรเอกสารได้ยอดกระดาษ(int qty, double amount, int vatRate)
    {
        var amt = (decimal)amount;
        var d = OcrLineQuantityGuard.Decide(qty, null, amt, null, null, "น็อต M6", "ตัว");
        Assert.Equal(OcrLineQuantityAction.RoundedUnitPrice, d.Action);
        var line = new DocumentLineRequest(Description: "น็อต M6", Quantity: qty, Unit: "ตัว", UnitPrice: d.UnitPrice!.Value, DiscountPercent: 0m,
            VatRate: vatRate, WithholdingTaxRate: 0m, AccountId: null, DiscountAmount: d.LineDiscount);
        // สร้าง (ComputeLineAmounts ตัวจริงผ่าน PreviewTotals) แล้ว "เปิดแก้แล้วบันทึก" ด้วยค่าที่เก็บไว้ = สูตรเดียวกันอีกรอบ ⇒ ยอดเดิมทุกสตางค์
        var created = DocumentService.PreviewTotals(new[] { line }, pricesIncludeVat: false, 0m);
        Assert.Equal(amt, created.Net);
        var stored = line with { UnitPrice = Math.Round(line.UnitPrice, 2, MidpointRounding.AwayFromZero),
                                 DiscountAmount = Math.Round(line.DiscountAmount ?? 0m, 2, MidpointRounding.AwayFromZero) };
        var updated = DocumentService.PreviewTotals(new[] { stored }, pricesIncludeVat: false, 0m);
        Assert.Equal((created.Net, created.Vat, created.Total), (updated.Net, updated.Vat, updated.Total));
        Assert.Equal(Math.Round(amt * vatRate / 100m, 2, MidpointRounding.AwayFromZero), updated.Vat);   // VAT จากฐานบนกระดาษ
    }

    // ── P2-2: ตัวตรวจซ่อมใช้บริบทเดียวกับตัวเขียน ──
    [Theory]
    [InlineData("ค่าพลังงาน", 4272, 19381.50, "จำนวนหน่วยที่ใช้ 4,272 หน่วย ค่าพลังงาน 19,381.50", OcrUtilityKind.None)]
    [InlineData("ค่าพลังงาน", 4272, 19381.50, "จำนวนหน่วยที่ใช้ 4,272 หน่วย ค่าพลังงาน 19,381.50", OcrUtilityKind.Electricity)]
    [InlineData("ค่า Ft", 4272, 893.50, "ค่า Ft 4,272 หน่วย 893.50", OcrUtilityKind.Electricity)]
    [InlineData("Water charge", 25, 246.13, "ปริมาณ 25 ลบ.ม. 246.13", OcrUtilityKind.Water)]
    public void บิลมิเตอร์จริงที่ตัวเขียนเขียนจำนวนN_ตัวตรวจซ่อมไม่ฟ้อง(string desc, int qty, double amt, string paper, OcrUtilityKind seller)
    {
        var p = OcrLineQuantityGuard.Read(paper);
        var d = OcrLineQuantityGuard.Decide(qty, null, (decimal)amt, null, p, desc, null, seller);
        Assert.Equal(OcrLineQuantityAction.QuantityInDescription, d.Action);
        // ตัวตรวจไม่มีบริบท (เดิม) ฟ้อง · มีบริบทของสแกน (ข้อความกระดาษ + ผู้ขาย) ไม่ฟ้อง
        // ★ ทีม OCR 14 (ตั้งใจพลิก): ตัวเขียนรู้จัก "ค่าพลังงาน"/"ค่า Ft" แล้ว ⇒ เขียน "(4,272 หน่วย)" (ท้ายมิเตอร์) ⇒ ไม่มีบริบทก็ไม่ฟ้อง · "Water charge" ยัง "(จำนวน N)"
        var energy = desc != "Water charge";
        Assert.Equal(energy ? $"{desc} ({qty:#,0} หน่วย)" : $"{desc} (จำนวน {qty:#,0})", d.Description);
        Assert.Equal(!energy, OcrLumpSumRepair.IsPollutedLine(d.Description, d.Quantity, d.Unit));
        Assert.False(OcrLumpSumRepair.IsPollutedLine(d.Description, d.Quantity, d.Unit, (decimal)amt, p, seller));
    }

    [Theory]
    [InlineData("เคส iPhone 16 (จำนวน 3)", 2775.70, "เคส iPhone 16 3 ชิ้น 2,775.70", OcrUtilityKind.Telecom)]   // ผู้ขาย AWN ขายสินค้า
    [InlineData("เคส iPhone 16 (จำนวน 3)", 2775.70, null, OcrUtilityKind.Telecom)]
    [InlineData("น้ำดื่ม 600ml (12 ขวด)", 300.00, "น้ำดื่ม 600ml 12 ขวด 300.00", OcrUtilityKind.None)]                 // รุ่น 6
    [InlineData("คอนกรีตผสมเสร็จ (2.75 ลบ.ม.)", 1500.00, "คอนกรีตผสมเสร็จ 2.75 ลบ.ม. 1,500.00", OcrUtilityKind.None)]
    [InlineData("ค่าน้ำขวด 600ml (12 ลบ.ม.)", 299.00, "ค่าน้ำขวด 600ml 12 299.00", OcrUtilityKind.None)]
    public void สินค้าที่จำนวนหาย_ยังถูกฟ้องเมื่อมีบริบท(string desc, double amt, string? paper, OcrUtilityKind seller)
        => Assert.True(OcrLumpSumRepair.IsPollutedLine(desc, 1m, OcrLineQuantityGuard.LumpSumUnit, (decimal)amt, OcrLineQuantityGuard.Read(paper), seller));

    // ── P1-4: ด่านอนุมัติโดยคนของหมายเหตุซ่อม ──
    private static OcrLiveLine Live(decimal q, decimal p, decimal amt, string desc, decimal disc = 0m)
        => new(q, p, 0m, disc, amt, 0m) { Description = desc };

    [Fact]
    public void หมายเหตุซ่อม_บล็อกการอนุมัติโดยคน_จนกว่าบรรทัดเลิกเป็น1คูณยอด()
    {
        var note = OcrLumpSumRepair.ScanNote(7, 1, "น้ำดื่ม 600ml (12 ขวด)", 300m);
        Assert.Equal("น้ำดื่ม 600ml (12 ขวด)", OcrLumpSumRepair.NoteDescription(note));
        var notes = "OCR อ่านแล้ว\n" + note;

        var blocked = OcrPostingReadiness.EvaluateHumanApproval(notes, new[] { Live(1m, 300m, 300m, "น้ำดื่ม 600ml (12 ขวด)"), Live(2m, 10m, 20m, "ปากกา") });
        Assert.False(blocked.CanAutoApprove);
        Assert.Contains("น้ำดื่ม 600ml (12 ขวด)", blocked.Reason);
        Assert.Contains("แกะใหม่", blocked.Reason);        // ทางไปต่อ

        // คนแก้เป็น 12 × 25 ⇒ ปลด (แม้บรรทัดอื่นในใบจะไม่ลงตัว — ด่านคนต้องมีทางไปต่อเสมอ)
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(notes,
            new[] { Live(12m, 25m, 300m, "น้ำดื่ม 600ml (12 ขวด)"), Live(2m, 10m, 19m, "ปากกา") }).CanAutoApprove);
        // บรรทัดอื่นที่บังเอิญ 1 × 300 ไม่ทำให้ติด เมื่อบรรทัดที่หมายเหตุอ้างถูกแก้แล้ว
        Assert.True(OcrPostingReadiness.EvaluateHumanApproval(notes,
            new[] { Live(12m, 25m, 300m, "น้ำดื่ม 600ml (12 ขวด)"), Live(1m, 300m, 300m, "ค่าขนส่ง") }).CanAutoApprove);
        // คนแก้คำอธิบายอย่างเดียว (ยัง 1 × 300) ⇒ ยังติด
        Assert.False(OcrPostingReadiness.EvaluateHumanApproval(notes, new[] { Live(1m, 300m, 300m, "น้ำดื่ม 600ml") }).CanAutoApprove);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OCR อ่านแล้ว")]
    // ทิศตรงข้าม: แท็ก [LINE-QTY] อื่น (หยุดเฉพาะอนุมัติเอง) ไม่บล็อกคน — ใบ OCR ปกติอนุมัติได้ตามเดิม
    [InlineData("[LINE-QTY] จำนวน 3 · ยอด 100.00 · ไม่มีราคาต่อหน่วย — ระบบคงจำนวน · ค่าที่ระบบตั้ง ⟦7×14.29⟧")]
    [InlineData("[Σ-GAP] ผลรวมรายการไม่ตรง")]
    public void ใบOCRปกติและใบที่ไม่ใช่OCR_ด่านคนไม่บล็อก(string? notes)
        => Assert.True(OcrPostingReadiness.EvaluateHumanApproval(notes, new[] { Live(7m, 14.29m, 100m, "สินค้า", 0.03m) }).CanAutoApprove);

    // ── P2-3: คีย์ตัวเรียนที่ถูกตัด 50 ตัว + คำเตือนขนาดแพ็ก ──
    [Fact]
    public void คีย์ตัวเรียนถูกตัด50ตัว_ซ่อมได้จากที่มาบรรทัดสแกน()
    {
        var line = "น้ำดื่มสิงห์ 600 มล. แพ็กหกขวด รุ่นพิเศษ ฉลาก (12 ขวด)";
        Assert.True(line.Length > OcrLumpSumRepair.LearnerKeyLength);
        var key = OcrLumpSumRepair.LearnerKey(line);
        Assert.DoesNotContain(")", key);
        Assert.Null(OcrLumpSumRepair.CleanKey(key));                               // เดิมหาไม่เจอ
        var fixedKey = OcrLumpSumRepair.CleanTruncatedLearnerKey(key, new[] { "อื่น", line });
        Assert.Equal(OcrLumpSumRepair.LearnerKey("น้ำดื่มสิงห์ 600 มล. แพ็กหกขวด รุ่นพิเศษ ฉลาก"), fixedKey);
        // ทิศตรงข้าม: ไม่มีที่มา / คีย์สั้น / คีย์ที่ตัดแล้วไม่มีท้ายเลย ⇒ ไม่แตะ
        Assert.Null(OcrLumpSumRepair.CleanTruncatedLearnerKey(key, new[] { "อื่น" }));
        Assert.Null(OcrLumpSumRepair.CleanTruncatedLearnerKey("น้ำดื่ม (12 ข", new[] { "น้ำดื่ม (12 ขวด)" }));
        var longBase = new string('ก', 60) + " (12 ขวด)";
        Assert.Null(OcrLumpSumRepair.CleanTruncatedLearnerKey(OcrLumpSumRepair.LearnerKey(longBase), new[] { longBase }));
    }

    [Theory]
    [InlineData("น้ำดื่มสิงห์ (12 ขวด)", false, true)]    // อาจเป็นแพ็กจริง ⇒ เตือน
    [InlineData("น้ำดื่มสิงห์ (12 ขวด)", true, false)]    // ตรงกับบรรทัดที่ตัวกันเขียน ⇒ ไม่เตือน
    [InlineData("ค่าบริการ (จำนวน 3)", false, false)]     // เครื่องหมายของตัวกันเสมอ
    [InlineData("ค่าไฟฟ้า (4,272 หน่วย)", false, false)]   // ท้ายมิเตอร์ของตัวกัน
    public void คำเตือนท้ายที่อาจเป็นขนาดแพ็ก(string text, bool fromGuard, bool warns)
        => Assert.Equal(warns, OcrLumpSumRepair.KeyWarning(text, fromGuard) is not null);
}
