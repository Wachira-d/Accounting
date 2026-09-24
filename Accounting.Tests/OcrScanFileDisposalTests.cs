using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ลบสแกนแล้วไฟล์ต้นฉบับต้องเป็นอย่างไร (ฝ่ายค้านรอบ 193 · S2-C1 → ทีม S2)
/// ครึ่งที่ 1: ไฟล์ของรายการอื่น (สลิปเงินเดือน · ใบเสร็จนำส่ง · ไฟล์ของเอกสารใบอื่น) ห้ามถูกลบเพราะลบสแกน · ไฟล์ที่สแกนอื่นยังใช้
/// ห้ามลบ · หลักฐานในระยะเก็บรักษาเก็บไฟล์จริงไว้ (AttachmentRetention)
/// ครึ่งที่ 2: เอกสารร่างที่ลบพร้อมสแกน (cascade) ยังลบไฟล์จริงได้เหมือนเดิม (ไม่ใช่รายการบัญชี)
/// </summary>
public class OcrScanFileDisposalTests
{
    private static readonly Guid Doc = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData("PayrollRun")]
    [InlineData("StatutoryRemittance")]
    [InlineData("ExpenseClaim")]
    [InlineData("ชนิดที่ไม่รู้จัก")]
    [InlineData(null)]
    public void ไฟล์ของรายการอื่น_ลบสแกนแล้วไม่แตะไฟล์(string? type)
        => Assert.Equal(ScanFileDisposalAction.Leave,
            OcrScanFileDisposal.Decide(type, Other, usedByOtherScan: false, cascadedDocumentId: null, cascadedDocumentStatus: null));

    [Fact]
    public void ไฟล์ของเอกสารใบอื่นที่ไม่ได้ลบพร้อมสแกน_ไม่แตะ()
        => Assert.Equal(ScanFileDisposalAction.Leave,
            OcrScanFileDisposal.Decide("Document", Other, false, Doc, DocumentStatus.Draft));

    [Fact]
    public void สแกนอื่นยังใช้ไฟล์นี้_ไม่แตะ()
        // retry สร้างแถวใหม่ที่ชี้ไฟล์เดิม — ลบแถวหนึ่งต้องไม่ทำให้อีกแถวเปิดรูปไม่ได้
        => Assert.Equal(ScanFileDisposalAction.Leave,
            OcrScanFileDisposal.Decide("OcrScan", Other, usedByOtherScan: true, null, null));

    [Theory]
    [InlineData(DocumentStatus.WaitingApproval)]
    [InlineData(DocumentStatus.Rejected)]
    public void เอกสารที่ไม่ใช่ร่างถูกลบพร้อมสแกน_เก็บไฟล์จริงไว้(DocumentStatus status)
        => Assert.Equal(ScanFileDisposalAction.SoftDeleteKeepBytes,
            OcrScanFileDisposal.Decide("Document", Doc, false, Doc, status));

    [Fact]
    public void ไฟล์ของสแกนเอง_ถอดแถวแต่เก็บไฟล์จริงตามระยะเก็บรักษา()
        => Assert.Equal(ScanFileDisposalAction.SoftDeleteKeepBytes,
            OcrScanFileDisposal.Decide("OcrScan", Other, false, null, null));

    // ── ครึ่งที่ 2 ──

    [Fact]
    public void เอกสารร่างที่ลบพร้อมสแกน_ลบไฟล์จริงได้เหมือนเดิม()
        => Assert.Equal(ScanFileDisposalAction.Remove,
            OcrScanFileDisposal.Decide("Document", Doc, false, Doc, DocumentStatus.Draft));
}
