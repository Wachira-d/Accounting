using System;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ประกาศว่ายื่น" ≠ "ระบบรู้ว่ายื่นสำเร็จ"** (ผลตรวจรอบ 181 · D2-B1a/b · ราก R1)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// <para><c>FileTaxReportAsync</c> ประทับ <c>Status = Filed</c> + <c>FiledDate</c>
/// + <b><c>FilingLockedAt</c></b> จาก<b>การกดปุ่มอย่างเดียว</b> แล้ว controller ตอบ
/// "ยื่นรายงานภาษีสำเร็จ" ⇒ ล็อกเอกสาร/JE ทั้งงวดด้วยเหตุการณ์ที่ระบบไม่รู้ว่า
/// เกิดจริง · และ <c>ComplianceService.SubmitFilingAsync</c> คอมเมนต์ตัวเองว่า
/// "Simulate submission" แล้ว<b>แต่งเลขอ้างอิงจาก GUID</b> พร้อมคิดเงินเพิ่ม
/// 1.5%/เดือน <b>ไม่มีเพดาน</b> (ม.27 วรรคสามกำหนดเพดาน = จำนวนภาษี)</para>
///
/// <para>ครึ่งหลังของแต่ละหมวดคือ <b>ทิศตรงข้าม</b> — เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ
/// (แถวเก่าที่ Filed ห้ามถูกลดชั้น · แถว excluded ที่ผู้ใช้ติ๊กออกเองห้ามทำให้ยื่นไม่ได้ ·
/// ยื่นทันกำหนดห้ามมีเงินเพิ่ม)</para>
/// </summary>
public class TaxFilingDeclarationTests
{
    // ════════ 1. ระดับหลักฐาน + การล็อกงวด ════════

    [Fact]
    public void กดยื่นโดยไม่มีเลขรับ_ได้สถานะประกาศ_และต้องไม่ล็อกงวด()
    {
        var j = TaxFilingLockPolicy.Judge(null);

        Assert.Equal(TaxReportStatus.Submitted, j.Status);
        Assert.Equal(TaxFilingEvidence.DeclaredByUser, j.Evidence);
        Assert.False(j.LockPeriod);            // ★ หัวใจของ D2-B1a
        Assert.True(j.NeedsFilingNumber);
        Assert.False(string.IsNullOrWhiteSpace(j.NextStep));   // ต้องมีทางไปต่อเสมอ
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void เลขรับที่เป็นช่องว่างไม่ใช่เลขรับ(string blank)
    {
        Assert.False(TaxFilingLockPolicy.HasFilingNumber(blank));
        Assert.Equal(TaxReportStatus.Submitted, TaxFilingLockPolicy.Judge(blank).Status);
    }

    [Fact]
    public void มีเลขรับจากกรมสรรพากร_จึงถือว่ายื่นจริง_และล็อกงวด()
    {
        var j = TaxFilingLockPolicy.Judge("0105566001234");

        Assert.Equal(TaxReportStatus.Filed, j.Status);
        Assert.Equal(TaxFilingEvidence.ConfirmedByFilingNumber, j.Evidence);
        Assert.True(j.LockPeriod);
        Assert.False(j.NeedsFilingNumber);
        Assert.Contains("0105566001234", j.Detail);
    }

    [Fact]
    public void แถวเก่าที่ประทับ_Filed_ไว้ก่อนแก้_ห้ามถูกลดชั้น_แต่ต้องติดธงตามเก็บเลขรับ()
    {
        // migration คงสถานะเดิมไว้ (ห้ามลดชั้นของที่ประกาศว่ายื่นไปแล้ว)
        var d = TaxFilingLockPolicy.Describe(TaxReportStatus.Filed, filingNumber: null);

        Assert.Equal(TaxReportStatus.Filed, d.Status);
        Assert.True(d.LockPeriod);              // ยังล็อกอยู่เหมือนเดิม
        Assert.True(d.NeedsFilingNumber);       // แต่ต้องตามเก็บเลขรับ
        Assert.NotNull(d.NextStep);
    }

    [Fact]
    public void รายงานร่าง_ไม่ถือว่ายื่น_และไม่มีอะไรต้องทำ()
    {
        var d = TaxFilingLockPolicy.Describe(TaxReportStatus.Draft, null);

        Assert.Equal(TaxFilingEvidence.NotFiled, d.Evidence);
        Assert.False(d.LockPeriod);
        Assert.False(d.NeedsFilingNumber);
        Assert.Null(d.NextStep);
    }

    [Fact]
    public void ชุดสถานะที่แปลว่ายื่นแล้ว_ต้องครอบทั้งสองชั้น_และไม่ครอบร่าง()
    {
        Assert.True(TaxFilingLockPolicy.DeclaredOrFiled(TaxReportStatus.Filed));
        Assert.True(TaxFilingLockPolicy.DeclaredOrFiled(TaxReportStatus.Submitted));
        Assert.False(TaxFilingLockPolicy.DeclaredOrFiled(TaxReportStatus.Draft));

        // array ที่ EF ใช้แปลเป็น IN (...) ต้องตรงกับ predicate เสมอ
        foreach (TaxReportStatus s in Enum.GetValues<TaxReportStatus>())
            Assert.Equal(
                TaxFilingLockPolicy.DeclaredOrFiled(s),
                Array.IndexOf(TaxFilingLockPolicy.DeclaredOrFiledStatuses, s) >= 0);
    }

    // ════════ 2. ด่าน 50 ทวิ ยังไม่ออก — golden ของโจทย์ ════════

    [Fact]
    public void รายงานที่มีหนังสือรับรองร่าง_1_ใบ_ต้องยื่นไม่ได้_และบอกว่าใบไหน()
    {
        var rows = WhtUnissuedCertGate.Evaluate(new (string?, bool, decimal)[]
        {
            ("WHT-2569-0001 — ค่าบริการออกแบบ", false, 300m),   // ใบที่ออกแล้ว
            ($"{WhtUnissuedCertGate.DraftCertMarker} — WHT-2569-0002 (ออกใบก่อนยื่น)", true, 150m),
        });

        Assert.True(rows.Any);
        Assert.Equal(1, rows.Count);
        Assert.Equal(150m, rows.TaxAmount);
        Assert.Contains("WHT-2569-0002", Assert.Single(rows.Samples));

        var msg = WhtUnissuedCertGate.BlockMessage("ภ.ง.ด.53", 5, 2026, rows);
        Assert.Contains("WHT-2569-0002", msg);
        Assert.Contains("ภ.ง.ด.53", msg);
        Assert.Contains("หนังสือรับรองหัก ณ ที่จ่าย", msg);   // ทางไปต่อ
    }

    [Fact]
    public void เอกสารหัก_WHT_ที่ไม่มีใบรับรองเลย_ก็ติดด่านเดียวกัน()
    {
        var rows = WhtUnissuedCertGate.Evaluate(new (string?, bool, decimal)[]
        {
            ($"{WhtUnissuedCertGate.UnissuedCertMarker} — PV-2569-0007 (ออกใบที่หน้า …)", true, 90m),
        });

        Assert.Equal(1, rows.Count);
        Assert.Equal(90m, rows.TaxAmount);
        Assert.Contains("PV-2569-0007", rows.Samples[0]);
    }

    // ── ทิศตรงข้าม: รายงานที่ถูกอยู่แล้วต้องยังยื่นได้ ──

    [Fact]
    public void แถวที่ผู้ทำบัญชีติ๊กออกเอง_ไม่ใช่เรื่องใบ_50ทวิ_ต้องยื่นได้ตามเดิม()
    {
        var rows = WhtUnissuedCertGate.Evaluate(new (string?, bool, decimal)[]
        {
            ("ยกเลิกเอกสาร PV-2569-0003", true, 120m),          // ติ๊กออกเอง
            ("[สรุป] บริษัท ก จำกัด", true, 0m),
        });

        Assert.False(rows.Any);
        Assert.Equal(0, rows.Count);
        Assert.Equal("", rows.ReviewNote);
    }

    [Fact]
    public void แถวเตือนที่ถูกติ๊กกลับเข้ามาใช้แล้ว_ไม่นับเป็นด่านอีก()
    {
        // IsExcluded=false = นักบัญชีจงใจนับเข้ายอดแล้ว — ด่านต้องไม่ขวาง
        var rows = WhtUnissuedCertGate.Evaluate(new (string?, bool, decimal)[]
        {
            ($"{WhtUnissuedCertGate.DraftCertMarker} — WHT-2569-0002", false, 150m),
        });

        Assert.False(rows.Any);
    }

    [Fact]
    public void รายงานที่ไม่มีแถวเตือนเลย_ReviewNote_ต้องว่าง_ไม่ใช่ข้อความเปล่า()
    {
        var rows = WhtUnissuedCertGate.Evaluate(Array.Empty<(string?, bool, decimal)>());
        Assert.Equal("", rows.ReviewNote);
        Assert.False(rows.Any);
    }

    [Fact]
    public void แผนที่รหัสแบบยื่น_ต้องรู้จักเฉพาะแบบ_ภงด_เท่านั้น()
    {
        Assert.Equal(TaxType.WithholdingTax3, WhtUnissuedCertGate.TaxTypeForPndForm("PND.3"));
        Assert.Equal(TaxType.WithholdingTax53, WhtUnissuedCertGate.TaxTypeForPndForm("pnd.53"));
        Assert.Null(WhtUnissuedCertGate.TaxTypeForPndForm("PP.30"));
        Assert.Null(WhtUnissuedCertGate.TaxTypeForPndForm(null));
    }

    // ════════ 3. เงินเพิ่ม ม.27 — ต้องมีเพดาน ════════

    [Fact]
    public void ยื่นทันกำหนด_ไม่มีเงินเพิ่ม()
    {
        Assert.Equal(0m, RevenueCodeSurcharge.Compute(
            100_000m, new DateTime(2026, 6, 15), new DateTime(2026, 6, 15)));
        Assert.Equal(0m, RevenueCodeSurcharge.Compute(
            100_000m, new DateTime(2026, 6, 15), new DateTime(2026, 6, 1)));
    }

    [Fact]
    public void ช้า_1_วัน_นับเป็น_1_เดือน_ได้_1จุด5_เปอร์เซ็นต์()
    {
        Assert.Equal(1_500m, RevenueCodeSurcharge.Compute(
            100_000m, new DateTime(2026, 6, 15), new DateTime(2026, 6, 16)));
    }

    [Fact]
    public void ช้า_3_เดือนเต็ม_ได้_4จุด5_เปอร์เซ็นต์()
    {
        Assert.Equal(4_500m, RevenueCodeSurcharge.Compute(
            100_000m, new DateTime(2026, 1, 15), new DateTime(2026, 4, 15)));
    }

    [Fact]
    public void เงินเพิ่มต้องไม่เกินจำนวนภาษี_ตาม_ม27_วรรคสาม()
    {
        // ช้า 10 ปี: สูตรดิบ = 100,000 × 1.5% × 120 = 180,000 (เกินภาษี)
        var v = RevenueCodeSurcharge.Compute(
            100_000m, new DateTime(2016, 6, 15), new DateTime(2026, 6, 20));

        Assert.Equal(100_000m, v);              // ★ ชนเพดาน
        Assert.True(RevenueCodeSurcharge.IsCapped(
            100_000m, new DateTime(2016, 6, 15), new DateTime(2026, 6, 20)));
    }

    [Fact]
    public void ยังไม่ชนเพดาน_ต้องไม่ถูกติดธงชนเพดาน()
    {
        Assert.False(RevenueCodeSurcharge.IsCapped(
            100_000m, new DateTime(2026, 1, 15), new DateTime(2026, 4, 15)));
    }

    [Fact]
    public void ไม่มียอดภาษี_ไม่มีเงินเพิ่ม_และไม่ติดธงชนเพดาน()
    {
        Assert.Equal(0m, RevenueCodeSurcharge.Compute(
            0m, new DateTime(2020, 1, 15), new DateTime(2026, 6, 20)));
        Assert.False(RevenueCodeSurcharge.IsCapped(
            0m, new DateTime(2020, 1, 15), new DateTime(2026, 6, 20)));
    }

    [Fact]
    public void ปัดเศษแบบ_AwayFromZero_ไม่ใช่_bankers()
    {
        // 1,234.50 × 1.5% × 1 = 18.5175 → 18.52
        Assert.Equal(18.52m, RevenueCodeSurcharge.Compute(
            1_234.50m, new DateTime(2026, 6, 15), new DateTime(2026, 6, 16)));
    }
}
