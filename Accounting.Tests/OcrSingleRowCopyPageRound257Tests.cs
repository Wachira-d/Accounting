using System.Collections.Generic;
using System.Linq;
using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 257 — ผู้ใช้รายงาน 2026-11-10: BS2026100012 หจก.แอม แฮปปี้เนส · PDF 2 หน้า (ต้นฉบับ+สำเนา) · รายการเดียว
/// "สายไฟ FD-CV 0.6/1KV 1×35 mm2 Yazaki ดำ" 54 เมตร × 210.42 = 11,362.68 ⇒ engine คืนแถวเดิมสองรอบ ⇒ เดิมยุบเป็น 108 (Σ 22,725.36)</summary>
public class OcrSingleRowCopyPageRound257Tests
{
    private const string Desc = "สายไฟ FD-CV 0.6/1KV 1×35 mm2 Yazaki ดำ";
    private static readonly OcrCandidateRow Row = new(Desc, 54m, 210.42m, 11362.68m);
    private const string PaperOriginalPlusCopy =
        "ใบกำกับภาษี ต้นฉบับ เลขประจำตัวผู้เสียภาษี 0203562005871 1 " + Desc + " 54 เมตร 210.42 11,362.68 รวมเป็นเงิน 11,362.68 ภาษีมูลค่าเพิ่ม 7% 795.39 จำนวนเงินรวมทั้งสิ้น 12,158.07\n"
        + "ใบกำกับภาษี สำเนา เลขประจำตัวผู้เสียภาษี 0203562005871 1 " + Desc + " 54 เมตร 210.42 11,362.68 รวมเป็นเงิน 11,362.68 ภาษีมูลค่าเพิ่ม 7% 795.39 จำนวนเงินรวมทั้งสิ้น 12,158.07";

    [Fact]
    public void ใบจริง_BS2026100012_รายการเดียวต้นฉบับบวกสำเนา_มีหลักฐานหน้าสำเนา_ตัดเหลือแถวเดียว_จำนวน54()
    {
        Assert.True(OcrDuplicateLineGuard.PageCopyEvidence(PaperOriginalPlusCopy, Desc, 2));
        var d = OcrDuplicateLineGuard.Decide(new List<OcrCandidateRow> { Row, Row }, 11362.68m, 795.39m, 12158.07m, 0m, pageCopyEvidence: true);
        Assert.True(d.Deduped);
        Assert.Equal(new[] { 0 }, d.KeepIndexes.ToArray());
        Assert.Equal(22725.36m, d.SumAll);
        Assert.Equal(11362.68m, d.SumDistinct);
        Assert.StartsWith(OcrDuplicateLineGuard.Tag, d.Reason);   // ห้ามอนุมัติเอง — คนตรวจก่อน
    }

    [Fact]
    public void ทิศตรงข้าม_ไม่มีหลักฐานหน้าสำเนา_คงพฤติกรรมเดิม_ไม่ตัด()
        => Assert.False(OcrDuplicateLineGuard.Decide(new List<OcrCandidateRow> { Row, Row }, 11362.68m, 795.39m, 12158.07m).Deduped);

    [Fact]
    public void ทิศตรงข้าม_กระดาษพิมพ์รายการซ้ำจริง_Σทุกแถวตรงหัวใบ_ไม่ตัดแม้มีหลักฐาน()
    {
        // น้ำดื่ม 10 ขวดสองบรรทัดจริง หัวใบ 100 — Σ ทุกแถวตรง ⇒ คงทุกแถว
        var rows = new List<OcrCandidateRow> { new("น้ำดื่ม", 10m, 5m, 50m), new("น้ำดื่ม", 10m, 5m, 50m) };
        Assert.False(OcrDuplicateLineGuard.Decide(rows, 100m, 7m, 107m, 0m, pageCopyEvidence: true).Deduped);
    }

    [Fact]
    public void หลักฐานหน้าสำเนา_ต้องเห็นคำอธิบายครบจำนวนครั้ง_และคำว่าสำเนาหรือเลขภาษีซ้ำ()
    {
        Assert.False(OcrDuplicateLineGuard.PageCopyEvidence("ใบกำกับภาษี " + Desc + " 54 210.42", Desc, 2));            // เห็นครั้งเดียว
        Assert.False(OcrDuplicateLineGuard.PageCopyEvidence(Desc + " 54 " + Desc + " 54", Desc, 2));                      // ไม่มีสำเนา/เลขภาษีซ้ำ
        Assert.True(OcrDuplicateLineGuard.PageCopyEvidence("0203562005871 " + Desc + " 0203562005871 " + Desc, Desc, 2)); // หัวใบพิมพ์ซ้ำ
        Assert.True(OcrDuplicateLineGuard.PageCopyEvidence("COPY " + Desc + " " + Desc, Desc, 2));
        Assert.False(OcrDuplicateLineGuard.PageCopyEvidence(null, Desc, 2));
        Assert.False(OcrDuplicateLineGuard.PageCopyEvidence(PaperOriginalPlusCopy, Desc, 1));
    }
}
