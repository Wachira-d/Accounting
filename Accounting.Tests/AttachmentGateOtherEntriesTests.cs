using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทางเข้าอื่นที่แตะไฟล์ชุดเดียวกับไฟล์แนบ (ฝ่ายค้านรอบ 193 → ทีม S2)
/// ครึ่งที่ 1 (ช่องที่ปิด): รูปสแกนที่ย้ายไปเป็นไฟล์แนบของเอกสารแล้ว ต้องใช้ด่านของเอกสาร (C3) · ผู้ที่มองไม่เห็นฝั่ง/ชั้นความลับ
/// ของใบ อ่านไม่ได้ · ไฟล์ที่แนบก่อนบันทึก (ถังเจ้าของว่าง) เปิดได้เฉพาะผู้อัปโหลด (P7)
/// ครึ่งที่ 2 (ทิศตรงข้าม): สแกนที่ยังไม่ผูกเอกสาร / ผูกกับเอกสารที่ถูกลบไปแล้ว ยังใช้ด่าน OCR เดิม (ไม่ถูกปิดเพิ่ม) · ผู้ที่เห็น
/// ฝั่งนั้นและชั้นความลับนั้นยังอ่านได้ · ผู้อัปโหลดยังเปิดไฟล์ของตัวเองได้ · ชนิดที่มีเจ้าของแล้วไม่ถูกนับเป็นถังว่าง
/// (ด่านที่ controller เรียกจริงถูกล็อกด้วย tools/attachment_gate_check.py — ถอดการเรียกแล้วฟ้อง)
/// </summary>
public class AttachmentGateOtherEntriesTests
{
    private static readonly Guid DocA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RandomScanEntity = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── ครึ่งที่ 1: สแกนที่ผูกเอกสารแล้ว = ใบเดียวกับเอกสาร ──

    [Fact]
    public void ไฟล์ของสแกนที่ย้ายไปเป็นของเอกสารแล้ว_ใช้ด่านของเอกสารนั้น()
        // OcrService.RelinkScanFileToDocumentAsync: EntityType "OcrScan" → "Document", EntityId → เอกสาร
        => Assert.Equal(DocA, AttachmentPermissionScope.ScanFileOwner("Document", DocA, DocA, createdDocumentExists: true));

    [Fact]
    public void ไฟล์ย้ายไปเอกสารแล้วแต่ตัวชี้ของสแกนหาย_ยังใช้ด่านของเอกสารที่ไฟล์เป็นของ()
        => Assert.Equal(DocA, AttachmentPermissionScope.ScanFileOwner("document", DocA, null, createdDocumentExists: false));

    [Fact]
    public void relinkพลาด_ไฟล์ยังเป็นOcrScanแต่สแกนชี้เอกสารที่ยังอยู่_ใช้ด่านของเอกสาร()
        // เคสเดียวกับ fallback ใน FileAttachmentService.GetByEntityAsync — ประตูหลังของใบเดียวกันต้องล็อกเท่าประตูหน้า
        => Assert.Equal(DocB, AttachmentPermissionScope.ScanFileOwner("OcrScan", RandomScanEntity, DocB, createdDocumentExists: true));

    [Theory]
    [InlineData(DocumentType.Invoice)]          // ฝั่งรายรับ
    [InlineData(DocumentType.PaymentVoucher)]   // ฝั่งรายจ่าย
    public void ผู้ที่ไม่เห็นฝั่งของเอกสาร_อ่านรูปสแกนของใบนั้นไม่ได้(DocumentType type)
    {
        var onlyOtherSide = DocumentPermissionHelper.IsRevenue(type)
            ? new DocumentVisibility(Revenue: false, Purchase: true, Other: false)
            : new DocumentVisibility(Revenue: true, Purchase: false, Other: false);
        Assert.False(AttachmentPermissionScope.DocumentReadable(onlyOtherSide, type, SensitivityKind.None,
            new HashSet<SensitivityKind>()));
    }

    [Fact]
    public void เอกสารลับที่ผู้ใช้ไม่มีสิทธิ์ชั้นนั้น_อ่านไม่ได้แม้เห็นทุกฝั่ง()
        => Assert.False(AttachmentPermissionScope.DocumentReadable(DocumentVisibility.All, DocumentType.PaymentVoucher,
            SensitivityKind.ExecutivePay, new HashSet<SensitivityKind> { SensitivityKind.Payroll }));

    [Fact]
    public void ถังไฟล์ก่อนบันทึก_คนอื่นในบริษัทเปิดไม่ได้()
    {
        var uploader = Guid.NewGuid();
        Assert.False(AttachmentPermissionScope.UnsavedFileVisible(uploader, Guid.NewGuid()));
        Assert.False(AttachmentPermissionScope.UnsavedFileVisible(Guid.Empty, Guid.Empty));   // ไม่รู้ผู้อัปโหลด ≠ ผ่าน
        Assert.True(AttachmentPermissionScope.IsUnsavedBucket(AttachmentPermissionScope.Resolve("WhtCredit"), Guid.Empty));
        Assert.Contains("ผู้ที่อัปโหลด", AttachmentPermissionScope.UnsavedFileDeniedMessage);
        Assert.Contains("หน้ารายการ", AttachmentPermissionScope.UnsavedFileDeniedMessage);   // ทางไปต่อ
    }

    // ── ครึ่งที่ 2: ของที่ใช้ได้อยู่แล้วต้องยังใช้ได้ ──

    [Fact]
    public void สแกนที่ยังไม่ผูกเอกสาร_ใช้ด่านOCRเดิม()
        => Assert.Null(AttachmentPermissionScope.ScanFileOwner("OcrScan", RandomScanEntity, null, createdDocumentExists: false));

    [Fact]
    public void สแกนที่ชี้ไปเอกสารที่ถูกลบแล้ว_ไม่ถูกล็อกด้วยเอกสารที่ไม่มีอยู่()
        => Assert.Null(AttachmentPermissionScope.ScanFileOwner("OcrScan", RandomScanEntity, DocA, createdDocumentExists: false));

    [Fact]
    public void ไม่มีแถวไฟล์และไม่ผูกเอกสาร_ใช้ด่านOCR()
        => Assert.Null(AttachmentPermissionScope.ScanFileOwner(null, null, Guid.Empty, createdDocumentExists: true));

    [Theory]
    [InlineData(DocumentType.Invoice, SensitivityKind.None)]
    [InlineData(DocumentType.PaymentVoucher, SensitivityKind.Payroll)]
    public void ผู้ที่เห็นฝั่งนั้นและชั้นความลับนั้น_ยังอ่านได้(DocumentType type, SensitivityKind sens)
        => Assert.True(AttachmentPermissionScope.DocumentReadable(DocumentVisibility.All, type, sens,
            new HashSet<SensitivityKind> { SensitivityKind.Payroll }));

    [Fact]
    public void ผู้ใช้ที่ยังไม่ได้ตั้งสิทธิ์แยกฝั่ง_เห็นทุกฝั่งเหมือนลิสต์เอกสาร()
        // VisibleDirectionsAsync คืน All เมื่อไม่มีคีย์แยกฝั่งเลย (พฤติกรรมเดิมของลิสต์เอกสาร) — ด่านรูปสแกนต้องไม่เข้มกว่าลิสต์
        => Assert.True(AttachmentPermissionScope.DocumentReadable(DocumentVisibility.All, DocumentType.Receipt,
            SensitivityKind.None, new HashSet<SensitivityKind>()));

    [Fact]
    public void ผู้อัปโหลด_ยังเปิดไฟล์ที่ตัวเองแนบก่อนบันทึกได้()
    {
        var me = Guid.NewGuid();
        Assert.True(AttachmentPermissionScope.UnsavedFileVisible(me, me));
    }

    [Theory]
    [InlineData("WhtCredit", false)]      // มีเจ้าของแล้ว (บันทึกแล้ว) — ไม่ใช่ถังว่าง
    [InlineData("Contact", true)]         // ชนิดที่ไม่รับเจ้าของว่าง — id ว่างก็ไม่ใช่ถัง (ด่านเดิมของชนิดนั้นตัดสิน)
    [InlineData("Document", true)]
    public void ชนิดอื่นหรือรายการที่บันทึกแล้ว_ไม่ถูกนับเป็นถังไฟล์ก่อนบันทึก(string type, bool emptyId)
    {
        var id = emptyId ? Guid.Empty : Guid.NewGuid();
        Assert.False(AttachmentPermissionScope.IsUnsavedBucket(AttachmentPermissionScope.Resolve(type), id));
    }
}
