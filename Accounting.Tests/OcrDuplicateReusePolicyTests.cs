using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ล็อกด่านไฟล์ซ้ำของ <c>OcrService.ScanAsync</c> — "ไฟล์ซ้ำ" (ธงเตือน) แยกจาก "ใช้ผลอ่านเดิมซ้ำ"
///
/// <para>ที่มา (ผู้ใช้รายงาน 2026-10-09): e-Tax ซีอาร์ซี ไทวัสดุ สแกนแล้วบรรทัดผิด → "ลบทั้งคู่" → อัปไฟล์เดิม (ไบต์เดียวกัน) ใหม่ ⇒
/// ตัวเลขผิดชุดเดิม · ด่านเดิมคัดลอกผลของต้นฉบับที่ hash ตรงโดยไม่ดูรุ่นตัวแกะ ⇒ การแก้ตัวแกะไม่มีผลกับไฟล์ที่เคยสแกนแล้ว</para>
///
/// <para>ทั้งสองทิศ: (ก) ใบที่ควรใช้ผลเดิมยังใช้ผลเดิม (พฤติกรรมเดิม — ประหยัดโควตา) (ข) ใบที่ผลเดิมเก่า/ถูกลบ/เป็น e-Tax อ่านใหม่
/// แต่ธงไฟล์ซ้ำยังอยู่ (§86/4 — คำเตือนบันทึกใบเดียวกันสองครั้ง)</para>
/// </summary>
public class OcrDuplicateReusePolicyTests
{
    private static readonly Guid PriorId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static OcrPriorScanFacts Prior(
        string? engine = "AzureDI", int? version = OcrExtractionVersion.Current,
        bool isDeleted = false, bool isDuplicate = false, string? status = "Completed") =>
        new(PriorId, isDeleted, status, isDuplicate, engine, version);

    [Fact]
    public void ไฟล์เดิม_รุ่นเดียวกัน_ไม่ถูกลบ_ใช้ผลอ่านเดิม_พฤติกรรมเดิม()
    {
        var v = OcrDuplicateReusePolicy.Decide(forceRescan: false, Prior());
        Assert.Equal(OcrDuplicateReuseAction.ReuseExtraction, v.Action);
        Assert.True(v.IsDuplicate);
        Assert.True(v.ReuseExtraction);
        Assert.Equal(PriorId, v.DuplicateOfScanId);
        var note = OcrDuplicateReusePolicy.Note(v);
        Assert.NotNull(note);
        Assert.StartsWith(OcrDuplicateReusePolicy.ReusedTag, note);
        Assert.Contains("ไม่ได้อ่านไฟล์ใหม่", note);
    }

    [Fact]
    public void สแกนเดิมถูกลบ_อ่านใหม่และไม่ถือเป็นไฟล์ซ้ำ()
    {
        var v = OcrDuplicateReusePolicy.Decide(false, Prior(isDeleted: true));
        Assert.Equal(OcrDuplicateReuseAction.NotDuplicate, v.Action);
        Assert.Equal(OcrDuplicateReExtractReason.PriorDeleted, v.Reason);
        Assert.False(v.IsDuplicate);
        Assert.False(v.ReuseExtraction);
        Assert.Null(v.DuplicateOfScanId);
        Assert.Null(OcrDuplicateReusePolicy.Note(v));
    }

    [Theory]
    [InlineData(null)]                              // แถวก่อนมีคอลัมน์ (NULL = รุ่นไม่ทราบ)
    [InlineData(OcrExtractionVersion.Current - 1)]  // รุ่นก่อน
    [InlineData(OcrExtractionVersion.Current + 1)]  // รุ่นที่ใหม่กว่าโค้ดที่รันอยู่ (ถอย deploy) — ไม่ตรงก็ไม่ใช้
    public void ผลอ่านจากตัวแกะรุ่นอื่น_อ่านใหม่แต่ยังติดธงไฟล์ซ้ำ(int? version)
    {
        var v = OcrDuplicateReusePolicy.Decide(false, Prior(version: version));
        Assert.Equal(OcrDuplicateReuseAction.ReExtract, v.Action);
        Assert.Equal(OcrDuplicateReExtractReason.OtherExtractionVersion, v.Reason);
        Assert.True(v.IsDuplicate);
        Assert.False(v.ReuseExtraction);
        Assert.Equal(PriorId, v.DuplicateOfScanId);
        var note = OcrDuplicateReusePolicy.Note(v)!;
        Assert.StartsWith(OcrDuplicateReusePolicy.ReExtractedTag, note);
        Assert.Contains($"รุ่นปัจจุบัน {OcrExtractionVersion.Current}", note);
        Assert.Contains("ยังนับเป็นไฟล์ซ้ำ", note);
    }

    [Fact]
    public void eTax_XML_อ่านใหม่เสมอ_แม้รุ่นตรง_แต่ธงไฟล์ซ้ำยังอยู่()
    {
        // เคสของผู้ใช้: ไฟล์ PDF/A-3 ที่ฝัง XML — อ่านซ้ำได้แน่นอนโดยไม่ใช้โควตา ⇒ ไม่ต้องเสี่ยงคัดลอกผลของตัวแปลงรุ่นเก่า
        var v = OcrDuplicateReusePolicy.Decide(false, Prior(engine: OcrDuplicateReusePolicy.EtaxXmlEngine));
        Assert.Equal(OcrDuplicateReuseAction.ReExtract, v.Action);
        Assert.Equal(OcrDuplicateReExtractReason.EtaxXmlDeterministic, v.Reason);
        Assert.True(v.IsDuplicate);
        Assert.Equal(PriorId, v.DuplicateOfScanId);
        Assert.Contains("e-Tax XML", OcrDuplicateReusePolicy.Note(v));
    }

    [Fact]
    public void กดแกะใหม่_ไม่เปลี่ยน_ข้ามด่านไฟล์ซ้ำทั้งด่าน()
    {
        foreach (var prior in new OcrPriorScanFacts?[] { null, Prior(), Prior(version: null), Prior(engine: "EtaxXml") })
        {
            var v = OcrDuplicateReusePolicy.Decide(forceRescan: true, prior);
            Assert.Equal(OcrDuplicateReuseAction.NotDuplicate, v.Action);
            Assert.Equal(OcrDuplicateReExtractReason.ForceRescan, v.Reason);
            Assert.False(v.IsDuplicate);
            Assert.Null(v.DuplicateOfScanId);
        }
    }

    [Fact]
    public void ไม่มีสแกนเดิม_หรือสแกนเดิมยังไม่เสร็จ_ไม่ใช่ไฟล์ซ้ำ()
    {
        Assert.Equal(OcrDuplicateReuseAction.NotDuplicate, OcrDuplicateReusePolicy.Decide(false, null).Action);
        var failed = OcrDuplicateReusePolicy.Decide(false, Prior(status: "Failed"));
        Assert.Equal(OcrDuplicateReuseAction.NotDuplicate, failed.Action);
        Assert.Equal(OcrDuplicateReExtractReason.PriorNotCompleted, failed.Reason);
    }

    [Fact]
    public void สำเนาCached_ไม่เป็นต้นฉบับ_กันสำเนาของสำเนา()
    {
        var v = OcrDuplicateReusePolicy.Decide(false, Prior(engine: OcrDuplicateReusePolicy.CachedEngine, isDuplicate: true));
        Assert.Equal(OcrDuplicateReuseAction.ReExtract, v.Action);
        Assert.Equal(OcrDuplicateReExtractReason.PriorIsCopy, v.Reason);
        Assert.True(v.IsDuplicate);
        // แถวเก่าที่ติดธงซ้ำโดยไม่บันทึก engine ถือเป็นสำเนาเช่นกัน
        Assert.Equal(OcrDuplicateReExtractReason.PriorIsCopy,
            OcrDuplicateReusePolicy.Decide(false, Prior(engine: null, isDuplicate: true)).Reason);
    }

    [Fact]
    public void แถวที่ติดธงซ้ำแต่อ่านไฟล์จริงรุ่นปัจจุบัน_ใช้เป็นต้นฉบับได้()
    {
        // อัปครั้งที่ 2 หลังเพิ่มรุ่น = อ่านใหม่ (ติดธงซ้ำ · engine จริง) ⇒ ครั้งที่ 3 ใช้ผลนั้นได้ ไม่ต้องเรียก engine ซ้ำทุกครั้ง
        // ต้นฉบับไม่ติดธงซ้ำ แม้ไม่มีชื่อ engine (แถวเก่ามาก) ก็เป็นผลอ่านจริง — ตัดสินต่อด้วยรุ่น
        Assert.Equal(OcrDuplicateReuseAction.ReuseExtraction,
            OcrDuplicateReusePolicy.Decide(false, Prior(engine: null, isDuplicate: false)).Action);
        var v = OcrDuplicateReusePolicy.Decide(false, Prior(engine: "AzureDI", isDuplicate: true));
        Assert.Equal(OcrDuplicateReuseAction.ReuseExtraction, v.Action);
    }

    [Fact]
    public void ExtractionVersion_ติดไปกับสำเนาเพราะเป็นคุณสมบัติของผลอ่าน()
    {
        // OcrScanSnapshot คัดลอกทุกช่องยกเว้นตัวตนของแถว — รุ่นของตัวแกะต้องตามผลอ่านไป (ไม่อยู่ใน RowIdentityFields)
        Assert.DoesNotContain(nameof(OcrScanResult.ExtractionVersion), OcrScanSnapshot.RowIdentityFields);
        var src = new OcrScanResult { ExtractionVersion = OcrExtractionVersion.Current };
        var dst = new OcrScanResult();
        OcrScanSnapshot.CopyExtractionFrom(src, dst);
        Assert.Equal(OcrExtractionVersion.Current, dst.ExtractionVersion);
    }
}
