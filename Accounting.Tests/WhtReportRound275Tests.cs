using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 275 แพ็ก B (T2 H1 · H2 · M3 · M4 · M5 · L2) — กติกาบรรทัดรายงาน ภ.ง.ด. + ลายเซ็นร่างล้าสมัยต่อใบ 50 ทวิ (pure)</summary>
public class WhtReportRound275Tests
{
    private const string Tin = "0105556000017";

    // ═════ WhtReportLineRule ═════

    [Theory]
    [InlineData(WhtUnissuedCertGate.UnissuedCertMarker + " — ค่าบริการ", WhtReportLineKind.Unissued)]
    [InlineData(WhtUnissuedCertGate.DraftCertMarker + " — WT-2 (ออกใบก่อนยื่น)", WhtReportLineKind.DraftCert)]
    [InlineData(WhtUnissuedCertGate.JournalOnlyPnd2Marker + " — JV-1", WhtReportLineKind.JournalOnly)]
    [InlineData(EWithholdingScope.ReportMarker + " — WT-9 — ค่าบริการ", WhtReportLineKind.EWithholding)]
    [InlineData(WhtReportLineRule.VoidedDocumentPrefix + "WT-1 — ค่าบริการ", WhtReportLineKind.VoidedDocument)]
    [InlineData("WT-1 — ค่าบริการ", WhtReportLineKind.Counted)]
    [InlineData(null, WhtReportLineKind.Counted)]
    public void R275B_จำแนกชนิดบรรทัดจากคำนำหน้าของระบบ(string? d, WhtReportLineKind kind)
        => Assert.Equal(kind, WhtReportLineRule.KindOf(d));

    [Fact]
    public void R275B_H1_คืนติ๊กเฉพาะแถวระบบที่ผู้ใช้ติ๊กเข้า_ไม่ใช่ทุกแถวของเอกสารเดียวกัน()
    {
        var doc = Guid.NewGuid();
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        // ใบงวด 1 ออกแล้ว (นับ) — ไม่ใช่แถวที่ต้องคืน
        Assert.Null(WhtReportLineRule.RestoreKey(TaxType.WithholdingTax53, doc, c1, "WT-1 — ค่าบริการ", isExcluded: false));
        // ใบงวด 2 ร่าง ติ๊กออกอยู่ (ไม่มีใครติ๊กเข้า) — ไม่คืน
        Assert.Null(WhtReportLineRule.RestoreKey(TaxType.WithholdingTax53, doc, c2,
            WhtUnissuedCertGate.DraftCertMarker + " — WT-2 (ออกใบก่อนยื่น)", isExcluded: true));
        // ผู้ใช้ติ๊กแถวเตือนเข้าเอง — คืน (คีย์มีคำอธิบาย ⇒ ไม่ไปติดแถวอื่นของเอกสารเดียวกัน)
        var k = WhtReportLineRule.RestoreKey(TaxType.WithholdingTax53, doc, null,
            WhtUnissuedCertGate.UnissuedCertMarker + " — ค่าขนส่ง", isExcluded: false);
        Assert.NotNull(k);
        Assert.Equal(doc, k!.Value.DocumentId);
        Assert.Null(k.Value.WhtCertId);
    }

    [Fact]
    public void R275B_M5_ล็อกคำนำหน้าแถวเตือน_และยอดของบรรทัดจาก50ทวิ_เฉพาะแบบภงด()
    {
        var cert = Guid.NewGuid();
        var warn = WhtUnissuedCertGate.UnissuedCertMarker + " — ค่าบริการ";
        Assert.True(WhtReportLineRule.DescriptionLocked(TaxType.WithholdingTax3, warn, null));
        Assert.False(WhtReportLineRule.AmountsLocked(TaxType.WithholdingTax3, null));
        Assert.True(WhtReportLineRule.IncludeNeedsReason(TaxType.WithholdingTax3, warn));
        Assert.True(WhtReportLineRule.AmountsLocked(TaxType.WithholdingTax54, cert));
        Assert.True(WhtReportLineRule.DescriptionLocked(TaxType.WithholdingTax2, "WT-1 — ดอกเบี้ย", cert));
        Assert.False(WhtReportLineRule.IncludeNeedsReason(TaxType.WithholdingTax53, "WT-1 — ค่าบริการ"));   // ติ๊กเข้า/ออกบรรทัดใบที่ออกแล้วไม่ต้องมีเหตุผล
        Assert.NotNull(WhtReportLineRule.LockReason(TaxType.WithholdingTax53, "WT-1", cert));
        // ทิศตรงข้าม: ภ.พ.30/ภ.พ.36/ภ.ง.ด.50 ไม่ถูกแตะ · JE ล้วนของ ภ.ง.ด.3/53 แก้ยอดได้ (ยอดฐานเป็นค่าประมาณ)
        Assert.False(WhtReportLineRule.DescriptionLocked(TaxType.VAT, warn, null));
        Assert.False(WhtReportLineRule.IncludeNeedsReason(TaxType.VatPp36, warn));
        Assert.False(WhtReportLineRule.AmountsLocked(TaxType.CorporateIncomeTax, cert));
        Assert.Null(WhtReportLineRule.LockReason(TaxType.WithholdingTax53, "PV-001", null));
    }

    [Fact]
    public void R275B_M5_ข้อความด่านยังไม่ออกใบ_ไม่บอกให้ติ๊กออก_บอกทางที่ทำได้จริง()
    {
        var rows = WhtUnissuedCertGate.Evaluate(new[] { ((string?)(WhtUnissuedCertGate.UnissuedCertMarker + " — PV-1 (x)"), true, 179m) });
        var msg = WhtUnissuedCertGate.BlockMessage("ภ.ง.ด.53", 10, 2026, rows);
        Assert.DoesNotContain("ติ๊กรายการที่ไม่ใช่การจ่ายจริงออก", msg);
        Assert.Contains("ออกใบ", msg);
        Assert.Contains("ระบุเหตุผล", msg);
        Assert.Contains("แก้ยอดหัก ณ ที่จ่ายที่เอกสาร", msg);
    }

    // ═════ TaxReportDraftFreshness.WhtSignature ═════

    private static TaxReportDraftFreshness.WhtLine Line(Guid? cert, Guid? doc, string desc, decimal inc, decimal tax,
        string? tin = Tin, string? branch = null, string name = "บริษัท ก จำกัด", string code = "40(8)")
        => new(cert, doc, desc, code, inc, tax, tin, branch, name);

    private static TaxReportDraftFreshness.WhtVerdict Cmp(IEnumerable<TaxReportDraftFreshness.WhtLine> stored, IEnumerable<TaxReportDraftFreshness.WhtLine> fresh)
        => TaxReportDraftFreshness.CompareWht(TaxReportDraftFreshness.WhtSignature(stored), TaxReportDraftFreshness.WhtSignature(fresh));

    [Fact]
    public void R275B_H2_ยกเลิกใบมือ_ไม่มีเอกสาร_ลายเซ็นเห็น()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var stored = new[] { Line(a, null, "WT-A — ค่าบริการ", 100_000m, 3_000m), Line(b, null, "WT-B — ค่าขนส่ง", 17_865m, 178.65m) };
        var fresh = new[] { Line(b, null, "WT-B — ค่าขนส่ง", 17_865m, 178.65m) };
        var v = Cmp(stored, fresh);
        Assert.True(v.Stale);
        Assert.Equal(1, v.Removed);
        // ทิศกลับ: ออกใบมือเพิ่มหลังสร้างร่าง (ยื่นขาด)
        var w = Cmp(fresh, stored);
        Assert.True(w.Stale);
        Assert.Equal(1, w.Added);
    }

    [Fact]
    public void R275B_H2_ร่างเป็นออกแล้ว_ยอดเท่าเดิม_ลายเซ็นเห็น()
    {
        var c = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var stored = new[] { Line(c, doc, WhtUnissuedCertGate.DraftCertMarker + " — WT-2 (ออกใบก่อนยื่น)", 5_000m, 150m) };
        var fresh = new[] { Line(c, doc, "WT-2 — ค่าบริการ", 5_000m, 150m) };
        var v = Cmp(stored, fresh);
        Assert.True(v.Stale);
        Assert.Equal(1, v.Changed);
    }

    [Fact]
    public void R275B_H2_เปลี่ยนการผูก_A_เป็น_B_ยอดเท่ากัน_ลายเซ็นเห็น()
    {
        var doc = Guid.NewGuid();
        var v = Cmp(new[] { Line(Guid.NewGuid(), doc, "WT-A — ค่าบริการ", 5_000m, 150m) },
            new[] { Line(Guid.NewGuid(), doc, "WT-B — ค่าบริการ", 5_000m, 150m) });
        Assert.True(v.Stale);
        Assert.Equal((1, 1), (v.Added, v.Removed));
    }

    [Theory]
    [InlineData("0105556000025", null, "บริษัท ก จำกัด")]   // แก้เลขผู้เสียภาษี
    [InlineData(Tin, "00001", "บริษัท ก จำกัด")]           // แก้สาขา
    [InlineData(Tin, null, "บริษัท ก จำกัด (มหาชน)")]      // แก้ชื่อ
    public void R275B_M4_ตัวตนผู้ถูกหักเปลี่ยนหลังสร้างร่าง_ลายเซ็นเห็น(string tin, string? branch, string name)
    {
        var c = Guid.NewGuid();
        var v = Cmp(new[] { Line(c, null, "WT-1", 1_000m, 30m) }, new[] { Line(c, null, "WT-1", 1_000m, 30m, tin, branch, name) });
        Assert.True(v.Stale);
        Assert.Equal(1, v.Changed);
    }

    [Fact]
    public void R275B_ทิศตรงข้าม_ติ๊กของผู้ใช้_แถวสรุป_JEล้วน_สาขาว่างเทียบ00000_ไม่ทำให้ล้าสมัย()
    {
        var c = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var stored = new[]
        {
            Line(c, doc, "WT-1 — ค่าบริการ", 1_000m, 30m, branch: "00000"),
            Line(null, doc, WhtUnissuedCertGate.UnissuedCertMarker + " — ค่าขนส่ง", 500m, 5m),
            Line(null, null, "JV-1", 999m, 29.97m),                                   // JE ล้วน (ผู้ทำบัญชีแก้ฐานได้)
            new(null, null, "[สรุป] บริษัท ก จำกัด", VatReportLineKind.Summary, 1_500m, 35m, Tin, null, "บริษัท ก จำกัด"),
        };
        var fresh = new[]
        {
            Line(c, doc, "WT-1 — ค่าบริการ", 1_000m, 30m, branch: null),
            Line(null, doc, WhtUnissuedCertGate.UnissuedCertMarker + " — ค่าขนส่ง", 500m, 5m),
            Line(null, null, "JV-1", 1_000m, 30m),
        };
        var v = Cmp(stored, fresh);
        Assert.False(v.Stale);
        Assert.Null(TaxReportDraftFreshness.WhtMessage(v, "ภ.ง.ด.53", "10/2569"));
    }

    [Fact]
    public void R275B_ร่างก่อนรอบ275_ไม่มีตัวอ้างใบ_ขึ้นแถบหนึ่งครั้งพร้อมบอกเหตุ()
    {
        var doc = Guid.NewGuid();
        var v = Cmp(new[] { Line(null, doc, "WT-1 — ค่าบริการ", 1_000m, 30m) }, new[] { Line(Guid.NewGuid(), doc, "WT-1 — ค่าบริการ", 1_000m, 30m) });
        Assert.True(v.Stale);
        Assert.Equal(1, v.LegacyLines);
        Assert.Contains("ก่อนรอบ 275", TaxReportDraftFreshness.WhtMessage(v, "ภ.ง.ด.53", "10/2569"));
    }

    [Fact]
    public void R275B_M3_ชุดตรวจครอบทุกแบบ_แยกจากชุดสร้างอัตโนมัติ()
    {
        foreach (var t in new[] { TaxType.VAT, TaxType.WithholdingTax1, TaxType.WithholdingTax2, TaxType.WithholdingTax3,
                     TaxType.WithholdingTax53, TaxType.WithholdingTax54, TaxType.VatPp36 })
            Assert.Contains(t, TaxReportDraftFreshness.CheckedTaxTypes);
        Assert.DoesNotContain(TaxType.CorporateIncomeTax, TaxReportDraftFreshness.CheckedTaxTypes);
        Assert.True(TaxReportDraftFreshness.UsesWhtSignature(TaxType.WithholdingTax54));
        Assert.False(TaxReportDraftFreshness.UsesWhtSignature(TaxType.VatPp36));   // ภ.พ.36 ต่อเอกสาร
    }

    // ═════ WhtReportTotals — แถว [สรุป] ต่อสาขา (L2) ═════

    [Fact]
    public void R275B_L2_แถวสรุปแยกสาขา_แถวสรุปรุ่นเก่าไม่มีสาขาเทียบเลขอย่างเดียว()
    {
        TaxReportLine L(decimal tax, string? branch) => new() { TaxPayerId = Tin, TaxPayerBranchCode = branch, IncomeAmount = tax * 10, TaxAmount = tax, IncomeTypeCode = "40(8)" };
        TaxReportLine S(string? branch) => new() { TaxPayerId = Tin, TaxPayerBranchCode = branch, IncomeTypeCode = VatReportLineKind.Summary };
        var hq = S("00000");
        var b1 = S("00001");
        var legacy = S(null);
        var r = new TaxReport { TaxType = TaxType.WithholdingTax53 };
        foreach (var l in new[] { L(10m, null), L(20m, "00000"), L(30m, "00001"), L(40m, "1"), hq, b1, legacy }) r.Lines.Add(l);
        WhtReportTotals.Apply(r);
        Assert.Equal(30m, hq.TaxAmount);        // สาขาว่าง + 00000 = สำนักงานใหญ่
        Assert.Equal(70m, b1.TaxAmount);        // "1" = 00001
        Assert.Equal(100m, legacy.TaxAmount);   // รุ่นเก่า: เลขอย่างเดียว (พฤติกรรมเดิม)
        Assert.Equal(100m, r.TotalTaxWithheld); // ยอดหัวไม่รวมแถวสรุป
    }

    // ═════ ชุดแก้ฝ่ายค้านรอบ 275 แพ็ก B ═════

    [Fact]
    public void R275BF_MED4_คืนติ๊กออก_จับคู่เอกสารใบประเภทคำอธิบาย_บรรทัดรุ่นเก่าไม่มีใบจับคู่ได้()
    {
        var doc = Guid.NewGuid();
        var cert = Guid.NewGuid();
        var excluded = new[] { new RegenerateUntick.UntickKey(doc, cert, "40(8)", "WT-1 — ค่าบริการ"), new RegenerateUntick.UntickKey(null, null, "40(8)", "JV-9") };
        Assert.True(RegenerateUntick.Matches(excluded, new(doc, cert, "40(8)", "WT-1 — ค่าบริการ")));
        Assert.True(RegenerateUntick.Matches(excluded, new(null, null, "40(8)", "JV-9")));
        // ทิศตรงข้าม: ใบอื่น · ประเภทอื่น · คำอธิบายอื่น (ร่าง → ออกแล้ว) ⇒ ไม่ติ๊กออกตาม
        Assert.False(RegenerateUntick.Matches(excluded, new(doc, Guid.NewGuid(), "40(8)", "WT-1 — ค่าบริการ")));
        Assert.False(RegenerateUntick.Matches(excluded, new(doc, cert, "40(2)", "WT-1 — ค่าบริการ")));
        Assert.False(RegenerateUntick.Matches(excluded, new(doc, cert, "40(8)", "WT-2 — ค่าบริการ")));
        // บรรทัดเดิมไม่มีตัวอ้างใบ (ก่อนรอบ 275) ⇒ จับคู่โดยไม่ดูใบ
        var legacy = new[] { new RegenerateUntick.UntickKey(doc, null, "40(8)", "WT-1 — ค่าบริการ") };
        Assert.True(RegenerateUntick.Matches(legacy, new(doc, cert, "40(8)", "WT-1 — ค่าบริการ")));
    }

    [Fact]
    public void R275BF_MED1_MED2_ยอดของบรรทัดในลายเซ็นล็อก_JEล้วนแก้ได้_ภพ30ไม่แตะ()
    {
        var doc = Guid.NewGuid();
        Assert.True(WhtReportLineRule.AmountsLocked(TaxType.VatPp36, null, doc));                // ภ.พ.36 จากเอกสาร
        Assert.True(WhtReportLineRule.AmountsLocked(TaxType.WithholdingTax53, null, doc));       // แถว "ยังไม่ออกหนังสือรับรอง"
        Assert.False(WhtReportLineRule.AmountsLocked(TaxType.WithholdingTax53, null, null));     // JE ล้วน (ไม่อยู่ในลายเซ็น)
        Assert.False(WhtReportLineRule.AmountsLocked(TaxType.VatPp36, null, null));
        Assert.False(WhtReportLineRule.AmountsLocked(TaxType.VAT, null, doc));
        Assert.Contains("แก้ยอดที่เอกสาร", WhtReportLineRule.LockReason(TaxType.VatPp36, "PV-1", null, doc));
        Assert.Contains("ระบุเหตุผล", WhtReportLineRule.LockReason(TaxType.WithholdingTax3, WhtUnissuedCertGate.UnissuedCertMarker + " — x", null, doc));
    }

    [Fact]
    public void R275BF_LOW_ใบเดิมเปลี่ยนเอกสารที่ผูก_ยอดเท่าเดิม_ลายเซ็นเห็น()
    {
        var c = Guid.NewGuid();
        var v = Cmp(new[] { Line(c, Guid.NewGuid(), "WT-1 — ค่าบริการ", 1_000m, 30m) }, new[] { Line(c, Guid.NewGuid(), "WT-1 — ค่าบริการ", 1_000m, 30m) });
        Assert.True(v.Stale);
        Assert.Equal(1, v.Changed);
    }

    [Fact]
    public void R275BF_MED3_ด่านเซิร์ฟเวอร์บล็อกเฉพาะกติกาที่ตั้งใจ()
    {
        var codes = Accounting.Services.Implementations.Tax.TaxComplianceChecker.ServerBlockingCodes;
        foreach (var c in new[] { "RD-WHT-002", "RD-WHT-005", "RD-PND1-002", "RD-PP36-001" }) Assert.Contains(c, codes);
        foreach (var c in new[] { "RD-VAT-001", "RD-VAT-002", "RD-PP36-002", "RD-GEN-001", "RD-DEADLINE" }) Assert.DoesNotContain(c, codes);
    }

    [Fact]
    public void R275BF_HIGH1_ยื่นแล้วทะเบียนขยับ_เตือนยื่นเพิ่มเติม_ไม่ใช่ให้สร้างใหม่()
    {
        var note = TaxReportDraftFreshness.FiledDriftNote("x");
        Assert.Contains("ยื่นแล้ว", note);
        Assert.Contains("ยื่นแบบเพิ่มเติม", note);
    }

    [Fact]
    public void R275BF_B6ก_ใบผูกด้วยมือหลุดการผูก_ใบที่ระบบสร้างยกเลิกตาม()
    {
        Assert.True(WhtCertVoidCascade.UnlinkInsteadOfVoid(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(WhtCertVoidCascade.UnlinkInsteadOfVoid(null));
        Assert.Contains("ยังไม่ผูก", WhtCertVoidCascade.UnlinkedNotice("WT-1", "PV-1"));
    }

    [Fact]
    public void R275BF_B6ข_สำเนาผู้ถูกหักณวันออก_ชนะผู้ติดต่อสด_ไม่มีสำเนาใช้สด_สำเนาเสียใช้สด()
    {
        var at = new Contact { Name = "บริษัท เดิม จำกัด", TaxId = Tin, BranchCode = "00000", TitleTh = "บริษัท", Province = "กรุงเทพมหานคร" };
        var cert = new WithholdingTaxCert { PayeeContactId = Guid.NewGuid(), PayeeSnapshotJson = WhtPayeeSnapshot.Serialize(at) };
        cert.PayeeContact = new Contact { Name = "บริษัท ใหม่ จำกัด", TaxId = "0105556000033", BranchCode = "00002", CountryCode = "TH" };
        var p = WhtPayeeSnapshot.Of(cert)!;
        Assert.Equal("บริษัท เดิม จำกัด", p.Name);
        Assert.Equal(Tin, p.TaxId);
        Assert.Equal("00000", p.BranchCode);
        Assert.Equal("กรุงเทพมหานคร", p.Province);
        Assert.NotSame(cert.PayeeContact, p);   // ไม่ผูกกับ navigation
        cert.PayeeSnapshotJson = null;
        Assert.Same(cert.PayeeContact, WhtPayeeSnapshot.Of(cert));
        cert.PayeeSnapshotJson = "{not json";
        Assert.Same(cert.PayeeContact, WhtPayeeSnapshot.Of(cert));
        Assert.True(WhtPayeeSnapshot.ShouldStamp(Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Issued));
        Assert.False(WhtPayeeSnapshot.ShouldStamp(Accounting.Models.DTOs.Tax.WithholdingTaxCertStatus.Draft));
    }
}
