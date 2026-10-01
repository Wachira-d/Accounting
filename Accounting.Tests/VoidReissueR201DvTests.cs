using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม DV — เอกสาร ยกเลิก/ออกใบแทน/e-Tax (BACKLOG A-DV1..A-DV6 · C-1 · คำตัดสินข้อ 62 · 65 · 66 · 67 · 68 · 74) ·
/// ทุกข้อมีสองครึ่ง: เคสที่พังกลับมาถูก + เคสที่ถูกอยู่แล้วไม่ถูกแตะ (F2 ข้อ 8) · ตัวเลขชุดเดียวกับ V1G/V1H/V1I (INV-1 1,000 + VAT 70 = 1,070) ·
/// จุดเรียกใน service ล็อกด้วย tools/required_call_site_check.py (บล็อก "รอบ 201 ทีม DV")
/// </summary>
public class VoidReissueR201DvTests
{
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid P3 = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly DateTime Feb10 = new(2026, 2, 10, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb12 = new(2026, 2, 12, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb15 = new(2026, 2, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly string KeptNotes =
        $"{EtaxReissueReview.KeptOriginalMarker} — ผู้ใช้ยืนยันว่าใบกำกับเดิมยังใช้ได้ · ยอดรับชำระที่มีผล 1,070.00 — ลูกค้าชำระเงินสดแทนเช็คที่เด้ง";

    // ═════════════ A-DV4 (ข้อ 68): ยอดครอบไม่นับทุกรายการที่ธุรกรรมนี้กำลังยกเลิก ═════════════

    [Fact]
    public void R201_DV4_cascadeยกเลิกสองรายการในลูปเดียว_ยอดครอบไม่นับทั้งคู่_ใบทางคติดธงกลับ()
    {
        // RC-1 ปิดธงทาง (ค) ด้วย P2 600 + P3 470 = 1,070 · cascade ยกเลิก P2 ก่อน แล้ว P3 (P2 ยังไม่ save — แถวในฐานยังมีผล)
        var rows = new[] { (P2, 600m), (P3, 470m) };
        // เดิม (ไม่นับแค่รายการเดียว): ตอนยกเลิก P3 ยอด = 600 (P2 ถูกนับว่ามีผล) — ยังไม่ครอบแต่ตัวเลขผิด · ถ้า P2 = 1,070 จะ "ครอบ" ทั้งที่ถูกยกเลิกแล้ว
        Assert.Equal(600m, DocumentVoidPreconditions.LivePaymentCoverage(rows, new[] { P3 }));
        var coverage = DocumentVoidPreconditions.LivePaymentCoverage(rows, new HashSet<Guid> { P2, P3 });
        Assert.Equal(0m, coverage);
        var flag = DocumentVoidPreconditions.KeptOriginalCoverageLost(EtaxReissueReview.KeptOriginal(null, KeptNotes), false, 70m, 1070m,
            coverage, "RC-1", "PAY-3");
        Assert.NotNull(flag);
        Assert.Contains("0.00", flag);

        // เคสที่เดิมพังจริง: P2 ครอบเต็ม 1,070 + P3 50 — ยกเลิกทั้งคู่ใน cascade · เดิมตอนยกเลิก P3 ยอด = 1,070 (P2 นับว่ามีผล) ⇒ ไม่ติดธง
        var rows2 = new[] { (P2, 1070m), (P3, 50m) };
        Assert.Null(DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m,
            DocumentVoidPreconditions.LivePaymentCoverage(rows2, new[] { P3 }), "RC-1", "PAY-3"));          // พฤติกรรมเดิม (ผิด)
        Assert.NotNull(DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m,
            DocumentVoidPreconditions.LivePaymentCoverage(rows2, new[] { P2, P3 }), "RC-1", "PAY-3"));      // ใหม่: ติดธงกลับ
    }

    [Fact]
    public void R201_DV4_ทิศตรงข้าม_รายการที่ไม่ได้ยกเลิกยังนับครบ_ชุดว่างนับทุกรายการ()
    {
        var rows = new[] { (P1, 500m), (P2, 600m), (P3, 470m) };
        Assert.Equal(1570m, DocumentVoidPreconditions.LivePaymentCoverage(rows, Array.Empty<Guid>()));
        Assert.Equal(1070m, DocumentVoidPreconditions.LivePaymentCoverage(rows, new[] { P1 }));
        // ยอดยังครอบหลังยกเลิก P1 ⇒ ไม่ติดธง (ใบที่ถูกอยู่แล้วไม่ถูกแตะ)
        Assert.Null(DocumentVoidPreconditions.KeptOriginalCoverageLost(true, false, 70m, 1070m,
            DocumentVoidPreconditions.LivePaymentCoverage(rows, new[] { P1 }), "RC-1", "PAY-1"));
        Assert.Equal(0m, DocumentVoidPreconditions.LivePaymentCoverage(Array.Empty<(Guid, decimal)>(), new[] { P1 }));
    }

    // ═════════════ A-DV2 (ข้อ 65): คอลัมน์ EtaxKeptOriginalAt เป็นหลัก · ป้ายเป็นทางสำรอง ═════════════

    [Fact]
    public void R201_DV2_คอลัมน์ตั้งแล้ว_นับเป็นทางค_แม้หมายเหตุว่างหรือถูกแก้()
    {
        Assert.True(EtaxReissueReview.KeptOriginal(Feb12, null));
        Assert.True(EtaxReissueReview.KeptOriginal(Feb12, ""));
        Assert.True(EtaxReissueReview.KeptOriginal(Feb12, "หมายเหตุถูกแก้จนป้ายหาย"));
        // ใบเก่าที่ migration ยังไม่เติม: อ่านป้าย (ทางสำรอง)
        Assert.True(EtaxReissueReview.KeptOriginal(null, KeptNotes));
    }

    [Fact]
    public void R201_DV2_ทิศตรงข้าม_ทางกหรือขล้างคอลัมน์_ป้ายตัวสุดท้ายไม่ใช่ทางค_ไม่นับ()
    {
        var thenCreditNote = KeptNotes + "\n\n" + $"{EtaxReissueReview.ResolvedMarker} ปิดธงด้วยใบลดหนี้ CN-1 — ใบลดหนี้ในระบบนี้ — ลดหนี้";
        Assert.False(EtaxReissueReview.KeptOriginal(null, thenCreditNote));
        Assert.False(EtaxReissueReview.KeptOriginal(null, null));
        Assert.False(EtaxReissueReview.KeptOriginal(null, $"{EtaxReissueReview.ResolvedMarker} ยกเลิกทาง e-Tax แล้ว — อ้างอิง X"));
    }

    /// <summary>สูตรตำแหน่ง "ป้ายตัวสุดท้าย" ของ SQL เติมคอลัมน์ (reverse + strpos) ต้องให้คำตอบเดียวกับ <see cref="EtaxReissueReview.LastResolutionKeptOriginal"/> —
    /// จำลองเลขคณิตของ SQL ด้วย C# (ตำแหน่งนับจาก 1 · อักษรไทยอยู่ใน BMP ⇒ ความยาวเท่ากับของ PostgreSQL)</summary>
    private static bool SqlLastIsKept(string notes)
    {
        var resolved = EtaxReissueReview.ResolvedMarker;
        if (!notes.Contains(EtaxReissueReview.KeptOriginalMarker, StringComparison.Ordinal)) return false;
        var rev = new string(notes.Reverse().ToArray());
        var p = rev.IndexOf(new string(resolved.Reverse().ToArray()), StringComparison.Ordinal) + 1;   // strpos (1-based)
        var start = notes.Length - p - resolved.Length + 2;                                               // substr start (1-based)
        return notes[(start - 1)..].StartsWith(EtaxReissueReview.KeptOriginalMarker, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kept")]
    [InlineData("kept-then-cn")]
    [InlineData("cn-then-kept")]
    [InlineData("cancelled")]
    [InlineData("prefix")]
    public void R201_DV2_SQLเติมคอลัมน์_ตำแหน่งป้ายตัวสุดท้ายตรงกับตัวอ่านเดียว(string kind)
    {
        var cn = $"{EtaxReissueReview.ResolvedMarker} ปิดธงด้วยใบลดหนี้ CN-1 — ใบลดหนี้ในระบบนี้ — ลดหนี้";
        var notes = kind switch
        {
            "kept" => KeptNotes,
            "kept-then-cn" => KeptNotes + "\n\n" + cn,
            "cn-then-kept" => cn + "\n\n" + KeptNotes,
            "cancelled" => $"[VAT-UNDO-BLOCKED] เช็คเด้ง\n{EtaxReissueReview.ResolvedMarker} ยกเลิกทาง e-Tax แล้ว — อ้างอิง X",
            _ => "บันทึกก่อนหน้า\n" + KeptNotes + " · ต่อท้าย",
        };
        Assert.Equal(EtaxReissueReview.LastResolutionKeptOriginal(notes), SqlLastIsKept(notes));
    }

    [Fact]
    public void R201_DV2_migration_เพิ่มคอลัมน์ก่อนเติม_ใช้ค่าคงที่ตัวเดียว_เติมเฉพาะแถวว่าง()
    {
        var all = DatabaseMigrationHelper.GetAlterStatements();
        var add = all.FindIndex(x => x.Contains("ADD COLUMN IF NOT EXISTS \"EtaxKeptOriginalAt\"", StringComparison.Ordinal));
        var fill = all.FindIndex(x => x.Contains("UPDATE \"Documents\" d SET \"EtaxKeptOriginalAt\"", StringComparison.Ordinal));
        Assert.True(add >= 0 && fill > add);
        var sql = DatabaseMigrationHelper.EtaxKeptOriginalBackfillSql();
        Assert.Contains(EtaxReissueReview.KeptOriginalMarker, sql);
        Assert.Contains($"reverse('{EtaxReissueReview.ResolvedMarker}')", sql);
        Assert.Contains("\"EtaxKeptOriginalAt\" IS NULL", sql);                 // ไม่ทับค่าที่เส้นใหม่เขียน
        Assert.Contains("RD-ETAX-ORIGINAL-STILL-VALID", sql);
        Assert.Contains("a.\"CompanyId\" = d.\"CompanyId\"", sql);                 // audit ของบริษัทเดียวกัน
        Assert.False(sql.Contains((char)0x7B));                                   // ไม่มีวงเล็บปีกกาเปิด (ExecuteSqlRaw ไม่ตีเป็น placeholder)
        Assert.DoesNotContain("'", EtaxReissueReview.KeptOriginalMarker);         // ต่อเข้า SQL literal ได้ตรง ๆ
    }

    [Fact]
    public void R201_DV2_คอลัมน์ใหม่ไม่ตามไปใบแทน()
        => Assert.Contains(nameof(Document.EtaxKeptOriginalAt), SettlementPaidReissue.DocumentNotCarriedFields);

    // ═════════════ A-DV3 (ข้อ 67): หลักฐานทาง (ก) ต้องแนบหลังเวลาที่ใบถึงกรมสรรพากร ═════════════

    [Fact]
    public void R201_DV3_เวลาอ้างอิง_ล่าสุดของแถวที่ส่งแล้วและอีเมลประทับเวลา()
    {
        // แถว e-Tax ส่ง 10 ก.พ. · อีเมลประทับเวลา 12 ก.พ. ⇒ ไฟล์ต้องแนบหลัง 12 ก.พ. (PDF ต้นฉบับที่แนบ 11 ก.พ. ไม่นับ)
        var t = DocumentVoidPreconditions.CancellationEvidenceNotBefore(
            new[] { (EtaxStatus.Accepted, (DateTime?)Feb10, Feb10.AddMinutes(-5)) },
            new[] { ((DateTime?)Feb12, Feb12.AddMinutes(-1)) });
        Assert.Equal(Feb12, t);
        // อีเมลอย่างเดียว (SME e-Tax by Email · แถว e-Tax แค่ลงนาม) ⇒ เวลาประทับของอีเมล
        Assert.Equal(Feb15, DocumentVoidPreconditions.CancellationEvidenceNotBefore(
            new[] { (EtaxStatus.Signed, (DateTime?)null, Feb10) }, new[] { ((DateTime?)Feb15, Feb10) }));
        // แถวเก่าไม่มีเวลาส่ง ⇒ เวลาสร้างแถว (กติกาเดียวกับ EtaxVoidPolicy.EvidenceNotBefore) · อีเมลไม่มีเวลาส่ง ⇒ เวลาสร้างบันทึก
        Assert.Equal(Feb10, DocumentVoidPreconditions.CancellationEvidenceNotBefore(
            new[] { (EtaxStatus.Submitted, (DateTime?)null, Feb10) }, Array.Empty<(DateTime?, DateTime)>()));
        Assert.Equal(Feb12, DocumentVoidPreconditions.CancellationEvidenceNotBefore(
            Array.Empty<(EtaxStatus, DateTime?, DateTime)>(), new[] { ((DateTime?)null, Feb12) }));
        // ข้อความปฏิเสธบอกเหตุ "แนบก่อนไม่นับ"
        var noFile = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Accepted, false,
            EtaxCancellationPath.CancelledAtRd, "ยกเลิกที่กรมสรรพากรแล้ว", "RD-CXL-001", false, null, 70m, 0m));
        Assert.False(noFile.Allowed);
        Assert.Contains("ไฟล์ต้องแนบหลังเวลาที่ใบนี้ถึงกรมสรรพากร", noFile.Reason);
    }

    [Fact]
    public void R201_DV3_ทิศตรงข้าม_ไม่มีอะไรถึงกรมสรรพากร_ไม่จำกัดเวลา()
    {
        Assert.Null(DocumentVoidPreconditions.CancellationEvidenceNotBefore(
            new[] { (EtaxStatus.Generated, (DateTime?)null, Feb10), (EtaxStatus.Signed, (DateTime?)null, Feb10),
                    (EtaxStatus.Error, (DateTime?)Feb10, Feb10), (EtaxStatus.Rejected, (DateTime?)Feb12, Feb10) },
            Array.Empty<(DateTime?, DateTime)>()));
        // ทางเดิมที่ไม่ถึงกรมสรรพากรยังผ่านโดยไม่ต้องใช้ไฟล์
        var notReached = DocumentVoidPreconditions.EtaxCancellationResolution(new EtaxCancellationClaim(true, EtaxStatus.Signed, false,
            EtaxCancellationPath.CancelledAtRd, "ยังไม่ส่ง", null, false, null, 70m, 0m));
        Assert.True(notReached.Allowed);
    }

    // ═════════════ C-1 (ข้อ 74): ออกใบแทนในเดือนที่ประกาศว่ายื่นแล้ว ═════════════

    [Fact]
    public void R201_C1_เดือนที่ประกาศว่ายื่น_บล็อกพร้อมทางไปต่อ()
    {
        var block = DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(new[]
        {
            new ReissueVatMonthFact("INV-1", Feb10, TaxReportStatus.Submitted),
        });
        Assert.NotNull(block);
        Assert.Contains("ประกาศว่ายื่นแล้ว", block);
        Assert.Contains("02/2569", block);                         // พ.ศ.
        Assert.Contains("INV-1", block);
        Assert.Contains("ปลดล็อก/กลับเป็นร่าง", block);            // ทางไปต่อ 1
        Assert.Contains("ใบลดหนี้", block);                         // ทางไปต่อ 2
        Assert.EndsWith("ระบบยังไม่ได้แตะอะไร", block);
        // ยื่นพร้อมเลขรับ
        Assert.Contains("มีเลขรับ", DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(new[]
        {
            new ReissueVatMonthFact("INV-1", Feb10, TaxReportStatus.Filed),
        }));
        // ใบเสร็จถือ VAT (เจ้าของแถว ภ.พ.30) อยู่เดือนที่ประกาศว่ายื่น แม้ใบเดิมอยู่เดือนร่าง
        var rc = DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(new[]
        {
            new ReissueVatMonthFact("INV-1", new DateTime(2026, 1, 20), null),
            new ReissueVatMonthFact("RC-1", Feb12, TaxReportStatus.Submitted),
        });
        Assert.NotNull(rc);
        Assert.Contains("RC-1", rc);
    }

    [Fact]
    public void R201_C1_ทิศตรงข้าม_ไม่มีรายงานหรือยังเป็นร่าง_ไม่บล็อก()
    {
        Assert.Null(DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(new[]
        {
            new ReissueVatMonthFact("INV-1", Feb10, null),
            new ReissueVatMonthFact("RC-1", Feb12, TaxReportStatus.Draft),
        }));
        Assert.Null(DocumentVoidPreconditions.ReissueDeclaredVatMonthBlock(Array.Empty<ReissueVatMonthFact>()));
    }

    // ═════════════ A-DV1 (ข้อ 62 · 66): 4 กลุ่มใหม่ของรายงานข้อ 44 (อ่านอย่างเดียว) ═════════════

    [Fact]
    public void R201_DV1_ภาษีขายค้างหลังยกเลิกการชำระหลายใบ_จำแนก()
        => Assert.True(EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.Approved, Feb10, 0m,
            liveVatReceipt: false, hadVoidedMultiDocAllocation: true));

    [Theory]
    [InlineData("paid")]          // ยังมีเงินรับที่มีผล
    [InlineData("receipt")]       // ใบเสร็จถือ VAT ยังถือจุดความรับผิด
    [InlineData("undone")]        // ภาษีถูกถอยแล้ว
    [InlineData("noalloc")]       // ไม่เคยมีการจัดสรรหลายใบที่ถูกยกเลิก
    [InlineData("voided")]        // ใบต้นทางยกเลิกแล้ว (JE ถูกกลับทั้งก้อน)
    [InlineData("taxinvoice")]    // ไม่ใช่ใบแจ้งหนี้ (ไม่มีภาษีขายพัก)
    public void R201_DV1_ทิศตรงข้าม_ภาษีขายไม่ค้าง_ไม่จำแนก(string kind)
    {
        var r = kind switch
        {
            "paid" => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.PartiallyPaid, Feb10, 500m, false, true),
            "receipt" => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.Approved, Feb10, 0m, true, true),
            "undone" => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.Approved, null, 0m, false, true),
            "noalloc" => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.Approved, Feb10, 0m, false, false),
            "voided" => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.Invoice, DocumentStatus.Voided, Feb10, 0m, false, true),
            _ => EtaxReissueReview.StuckOutputVatAfterPaymentVoid(DocumentType.TaxInvoice, DocumentStatus.Approved, Feb10, 0m, false, true),
        };
        Assert.False(r);
    }

    [Fact]
    public void R201_DV1_แถวeTaxที่ส่งแล้วถูกยกเลิกโดยไม่มีหลักฐาน_สองทิศ()
    {
        Assert.True(EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(EtaxStatus.Voided, Feb10, hasVoidDecisionAudit: false,
            documentCancellationRecorded: false));
        // ยกเลิกผ่านเส้นข้อ 51 (มี audit การตัดสิน) · เอกสารบันทึกยกเลิกทาง e-Tax พร้อมหลักฐาน · ไม่เคยส่ง · ยังไม่ถูกยกเลิก
        Assert.False(EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(EtaxStatus.Voided, Feb10, true, false));
        Assert.False(EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(EtaxStatus.Voided, Feb10, false, true));
        Assert.False(EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(EtaxStatus.Voided, null, false, false));
        Assert.False(EtaxReissueReview.SubmittedEtaxVoidedWithoutEvidence(EtaxStatus.Submitted, Feb10, false, false));
    }

    [Fact]
    public void R201_DV1_แถวeTaxของใบส่งอีเมลประทับเวลาถูกยกเลิกในระบบ_สองทิศ()
    {
        Assert.True(EtaxReissueReview.EmailedEtaxVoidedInSystem(EtaxStatus.Voided, documentEmailedWithRdTimestamp: true, documentCancellationRecorded: false));
        Assert.False(EtaxReissueReview.EmailedEtaxVoidedInSystem(EtaxStatus.Voided, false, false));
        Assert.False(EtaxReissueReview.EmailedEtaxVoidedInSystem(EtaxStatus.Voided, true, true));
        Assert.False(EtaxReissueReview.EmailedEtaxVoidedInSystem(EtaxStatus.Signed, true, false));
    }

    [Fact]
    public void R201_DV1_ใบทางคที่เสียยอดครอบ_ตัวตัดสินเดียวกับเส้นยกเลิกการชำระ_สองทิศ()
    {
        // ป้ายหรือคอลัมน์ทาง (ค) + ยอดที่ยังมีผลไม่ครอบ + ไม่มีธง ⇒ เข้ารายงาน
        Assert.NotNull(DocumentVoidPreconditions.KeptOriginalCoverageLost(EtaxReissueReview.KeptOriginal(Feb12, null), false, 70m, 1070m, 0m, "RC-1", null));
        // ยอดยังครอบ / ติดธงอยู่แล้ว / ไม่ใช่ทาง (ค) ⇒ ไม่เข้า
        Assert.Null(DocumentVoidPreconditions.KeptOriginalCoverageLost(EtaxReissueReview.KeptOriginal(Feb12, null), false, 70m, 1070m, 1070m, "RC-1", null));
        Assert.Null(DocumentVoidPreconditions.KeptOriginalCoverageLost(EtaxReissueReview.KeptOriginal(Feb12, null), true, 70m, 1070m, 0m, "RC-1", null));
        Assert.Null(DocumentVoidPreconditions.KeptOriginalCoverageLost(EtaxReissueReview.KeptOriginal(null, null), false, 70m, 1070m, 0m, "RC-1", null));
    }

    [Fact]
    public void R201_DV1_ตัวนับรวมทุกกลุ่ม()
    {
        var receipt = new EtaxReviewReceiptRow(Guid.NewGuid(), "RC-1", Feb10, 70m, null, null, null, "x");
        var etaxRow = new EtaxReviewEtaxRow(Guid.NewGuid(), Guid.NewGuid(), "RC-1", "ET-1", Feb10, Feb12, null, "x");
        var r = new EtaxReissueReviewReport(new[] { receipt }, Array.Empty<EtaxReviewReceiptRow>(), Array.Empty<EtaxReviewReplacementRow>(),
            Array.Empty<EtaxReviewReversalRow>(),
            new[] { new EtaxReviewDocumentRow(Guid.NewGuid(), "INV-1", Feb10, 70m, Feb10, "x") },
            new[] { etaxRow }, new[] { receipt }, new[] { etaxRow, etaxRow });
        Assert.Equal(6, r.Total);
    }
}
