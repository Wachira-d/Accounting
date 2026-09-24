using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทางเข้าอื่นที่แตะไฟล์ชุดเดียวกับไฟล์แนบ (ฝ่ายค้านรอบ 193 → ทีม S2 · สองรอบ)
/// ครึ่งที่ 1 (ช่องที่ปิด): สแกนใช้ด่านของ<b>เจ้าของไฟล์</b>เสมอ — ไฟล์ของเอกสาร/สลิปเงินเดือน/ใบเบิกที่ถูกสแกนผ่าน
/// <c>POST ocr/scan/{fileId}</c> ไม่ตกเป็น "สแกนที่ยังไม่ผูก" (S2-C1) · สแกนที่ลงเป็น JE ใช้ด่าน JE (S2-P3) · ห้ามย้ายไฟล์ที่เป็นของ
/// รายการอื่นไปเป็นของเอกสารใบใหม่ (S2-C2) · ถังไฟล์ก่อนบันทึกลบได้เฉพาะผู้อัปโหลด (S2-P6)
/// ครึ่งที่ 2 (ทิศตรงข้าม): สแกนที่ยังไม่ผูก / ผูกกับรายการที่ถูกลบไปแล้ว ยังใช้ด่าน OCR เดิม · ไฟล์ของสแกนเองยังย้ายเข้าเอกสารได้
/// (รวมเรียกซ้ำ) · ผู้อัปโหลดยังเปิด/ลบไฟล์ของตัวเองได้ · ผู้ถือ Tax.File เปิดดูไฟล์ค้างในถังได้
/// (ด่านที่ controller เรียกจริงถูกล็อกด้วย tools/attachment_gate_check.py — ถอดการเรียกแล้วฟ้อง)
/// </summary>
public class AttachmentGateOtherEntriesTests
{
    private static readonly Guid DocA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RandomScanEntity = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Run = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Je = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // ── ครึ่งที่ 1: ด่านตามเจ้าของไฟล์ ──

    [Fact]
    public void ไฟล์ของสแกนที่ย้ายไปเป็นของเอกสารแล้ว_ใช้ด่านของเอกสารนั้น()
        => Assert.Equal(new ScanOwnerRef("Document", DocA),
            AttachmentPermissionScope.ScanOwner("Document", DocA, DocA, createdDocumentExists: true));

    [Fact]
    public void relinkพลาด_ไฟล์ยังเป็นOcrScanแต่สแกนชี้เอกสารที่ยังอยู่_ใช้ด่านของเอกสาร()
        // เคสเดียวกับ fallback ใน FileAttachmentService.GetByEntityAsync — ประตูหลังของใบเดียวกันต้องล็อกเท่าประตูหน้า
        => Assert.Equal(new ScanOwnerRef("Document", DocB),
            AttachmentPermissionScope.ScanOwner("OcrScan", RandomScanEntity, DocB, createdDocumentExists: true));

    [Theory]
    [InlineData("PayrollRun", "PayrollRun")]       // สลิปเงินเดือน (PDPA ม.26)
    [InlineData("ExpenseClaim", "ExpenseClaim")]   // ใบเสร็จในใบเบิกของคนอื่น
    [InlineData("LodgingReservation", "LodgingReservation")]
    [InlineData("payrollrun", "PayrollRun")]        // ตัวพิมพ์ต่าง → ชื่อมาตรฐาน
    public void สแกนที่สร้างจากไฟล์แนบของรายการอื่น_ได้ด่านของรายการนั้น_ไม่ตกเป็นสแกนยังไม่ผูก(string fileType, string expected)
        // S2-C1: POST ocr/scan/{fileId} กับไฟล์ของรายการอื่น — เดิม ScanFileOwner คืน null ⇒ ด่าน OCR ระดับสมาชิก
        => Assert.Equal(new ScanOwnerRef(expected, Run),
            AttachmentPermissionScope.ScanOwner(fileType, Run, null, createdDocumentExists: false));

    [Fact]
    public void ไฟล์เป็นของรายการอื่น_ชนะตัวชี้เอกสารของสแกน()
        // สแกนไฟล์สลิปเงินเดือนแล้วกดสร้างเอกสาร ⇒ ไฟล์ไม่ถูกย้าย (ScanFileRelinkable) และยังต้องใช้ด่านเงินเดือน
        => Assert.Equal(new ScanOwnerRef("PayrollRun", Run),
            AttachmentPermissionScope.ScanOwner("PayrollRun", Run, DocA, createdDocumentExists: true));

    [Fact]
    public void สแกนที่ลงเป็นJEตรง_ใช้ด่านของJE()
        // S2-P3: เดิมดูแค่ CreatedDocumentId ⇒ รูป/ผลอ่านของ JE ลับเปิดได้ระดับสมาชิก
        => Assert.Equal(new ScanOwnerRef("JournalEntry", Je),
            AttachmentPermissionScope.ScanOwner("OcrScan", RandomScanEntity, null, false, Je, createdJournalEntryExists: true));

    [Theory]
    [InlineData("PayrollRun")]
    [InlineData("ExpenseClaim")]
    [InlineData("StatutoryRemittance")]
    public void ไฟล์ของรายการอื่น_ย้ายไปเป็นของเอกสารไม่ได้(string fileType)
        => Assert.False(AttachmentPermissionScope.ScanFileRelinkable(fileType, Run, DocA));

    [Fact]
    public void ไฟล์ที่เป็นของเอกสารใบอื่นอยู่แล้ว_ย้ายไปใบใหม่ไม่ได้()
        // S2-C2: link-document ย้ายหลักฐานของใบ A (มองไม่เห็น) ไปใบ B (มองเห็น) แล้วดาวน์โหลดได้
        => Assert.False(AttachmentPermissionScope.ScanFileRelinkable("Document", DocA, DocB));

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
    public void ถังไฟล์ก่อนบันทึก_คนอื่นที่ไม่มีคีย์เปิดไม่ได้_และลบของคนอื่นไม่ได้แม้ถือคีย์()
    {
        var uploader = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.False(AttachmentPermissionScope.UnsavedFileVisible(uploader, other, holdsModuleWriteKey: false));
        Assert.False(AttachmentPermissionScope.UnsavedFileVisible(Guid.Empty, Guid.Empty, false));   // ไม่รู้ผู้อัปโหลด ≠ ผ่าน
        Assert.False(AttachmentPermissionScope.UnsavedFileRemovable(uploader, other));             // S2-P6
        Assert.False(AttachmentPermissionScope.UnsavedFileRemovable(Guid.Empty, Guid.Empty));
        Assert.True(AttachmentPermissionScope.IsUnsavedBucket(AttachmentPermissionScope.Resolve("WhtCredit"), Guid.Empty));
        Assert.Contains("ผู้ที่อัปโหลด", AttachmentPermissionScope.UnsavedFileDeniedMessage);
        Assert.Contains("หน้ารายการ", AttachmentPermissionScope.UnsavedFileDeniedMessage);   // ทางไปต่อ
        Assert.Contains("ผู้อัปโหลด", AttachmentPermissionScope.UnsavedFileNotRemovableMessage);
    }

    // ── ครึ่งที่ 2: ของที่ใช้ได้อยู่แล้วต้องยังใช้ได้ ──

    [Fact]
    public void สแกนที่ยังไม่ผูกเอกสาร_ใช้ด่านOCRเดิม()
        => Assert.Null(AttachmentPermissionScope.ScanOwner("OcrScan", RandomScanEntity, null, createdDocumentExists: false));

    [Fact]
    public void สแกนที่ชี้ไปเอกสารหรือJEที่ถูกลบแล้ว_ไม่ถูกล็อกด้วยรายการที่ไม่มีอยู่()
        => Assert.Null(AttachmentPermissionScope.ScanOwner("OcrScan", RandomScanEntity, DocA, false, Je, false));

    [Fact]
    public void ไม่มีแถวไฟล์และไม่ผูกเอกสาร_ใช้ด่านOCR()
        => Assert.Null(AttachmentPermissionScope.ScanOwner(null, null, Guid.Empty, createdDocumentExists: true));

    [Fact]
    public void ไฟล์ของสแกนเอง_ย้ายเข้าเอกสารได้_และเรียกซ้ำกับใบเดิมได้()
    {
        Assert.True(AttachmentPermissionScope.ScanFileRelinkable("OcrScan", RandomScanEntity, DocA));
        Assert.True(AttachmentPermissionScope.ScanFileRelinkable("Document", DocA, DocA));   // idempotent
    }

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
    public void ผู้อัปโหลด_ยังเปิดและลบไฟล์ที่ตัวเองแนบก่อนบันทึกได้()
    {
        var me = Guid.NewGuid();
        Assert.True(AttachmentPermissionScope.UnsavedFileVisible(me, me, holdsModuleWriteKey: false));
        Assert.True(AttachmentPermissionScope.UnsavedFileRemovable(me, me));
    }

    [Fact]
    public void ผู้ถือTaxFile_เปิดดูไฟล์ค้างในถังของคนอื่นได้()
        // S2-P6: ผู้อัปโหลดออกจากบริษัทไปแล้ว ไฟล์ค้างต้องมีคนเปิดตรวจได้ (Owner ผ่าน HasPermission อัตโนมัติ)
        => Assert.True(AttachmentPermissionScope.UnsavedFileVisible(Guid.NewGuid(), Guid.NewGuid(), holdsModuleWriteKey: true));

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
