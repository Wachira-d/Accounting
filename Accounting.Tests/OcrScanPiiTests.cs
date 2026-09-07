using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// "ช่องไหนของผลสแกนเก็บข้อมูลส่วนบุคคล" (<see cref="OcrScanPii"/>)
///
/// <para>ที่มา (ผลตรวจ OCR 2026-09-06 · T5): <c>PdpaService.ApplyErasureAsync</c>
/// (ม.33) anonymise <c>Users</c> + <c>Contacts</c> ครบ แต่<b>ไม่เคยแตะ
/// <c>OcrScanResult</c></b> ทั้งที่แถวนั้นเก็บ <c>RawTextContent</c> =
/// ข้อความทั้งหน้ากระดาษ ⇒ ผู้ที่ใช้สิทธิขอลบยังค้นเจอตัวเองได้เต็ม ๆ</para>
/// </summary>
public class OcrScanPiiTests
{
    [Fact]
    public void ทุกช่องข้อความของแถวสแกน_ต้องถูกตัดสินแล้วว่าเป็นข้อมูลส่วนบุคคลหรือไม่()
    {
        // นี่คือกลไกที่กันตัวถัดไป: ช่องข้อความใหม่ที่ใครเพิ่มทีหลังจะทำให้
        // เทสต์นี้แดง บังคับให้ต้องตัดสินว่าต้องล้างตอนใช้สิทธิขอลบไหม
        // (deny-list เฉย ๆ จะปล่อยช่องใหม่หลุดเงียบ — บทเรียนเดียวกับ OcrScanSnapshot)
        var undecided = OcrScanPii.UndecidedTextFields(typeof(OcrScanResult));
        Assert.True(undecided.Count == 0,
            "ช่องข้อความที่ยังไม่ถูกตัดสิน: " + string.Join(", ", undecided)
            + " — เพิ่มลง OcrScanPii.PersonalDataFields หรือ NotPersonalDataFields");
    }

    [Fact]
    public void ล้างแล้วต้องไม่เหลือข้อความบนกระดาษ_แต่โครงบัญชีต้องอยู่ครบ()
    {
        var row = new OcrScanResult
        {
            OriginalFileName = "ใบกำกับ-นายสมชาย.pdf",
            RawTextContent = "นายสมชาย ใจดี 99/1 ถนนสุขุมวิท เลขประจำตัวผู้เสียภาษี 1234567890123",
            ExtractedVendorName = "นายสมชาย ใจดี",
            ExtractedVendorTaxId = "1234567890123",
            VendorAddress = "99/1 ถนนสุขุมวิท",
            BuyerName = "บริษัท ทดสอบ จำกัด",
            ProcessingNotes = "[Buyer] บริษัท ทดสอบ จำกัด TaxID:0105558123456",
            // โครงบัญชีที่ต้องคงไว้ตาม พ.ร.บ.การบัญชี ม.10
            ExtractedDocumentNumber = "INV-2020-0001",
            ExtractedTotalAmount = 1070m,
            ScanStatus = "Completed",
            ExpenseCategory = "ค่าวัสดุสำนักงาน",
        };

        var cleared = OcrScanPii.Anonymize(row);

        Assert.True(cleared >= 6);
        Assert.Null(row.RawTextContent);
        Assert.Null(row.ExtractedVendorName);
        Assert.Null(row.ExtractedVendorTaxId);
        Assert.Null(row.VendorAddress);
        Assert.Null(row.BuyerName);
        Assert.Null(row.ProcessingNotes);
        // ชื่อไฟล์เป็น non-nullable — ต้องเป็นข้อความแทน ไม่ใช่ null
        Assert.Equal("[ANONYMIZED]", row.OriginalFileName);
        // โครงบัญชีอยู่ครบ
        Assert.Equal("INV-2020-0001", row.ExtractedDocumentNumber);
        Assert.Equal(1070m, row.ExtractedTotalAmount);
        Assert.Equal("Completed", row.ScanStatus);
        Assert.Equal("ค่าวัสดุสำนักงาน", row.ExpenseCategory);
    }

    [Fact]
    public void แถวที่ล้างไปแล้ว_ล้างซ้ำต้องไม่นับเพิ่ม()
    {
        var row = new OcrScanResult { RawTextContent = "ข้อความ" };
        Assert.Equal(1, OcrScanPii.Anonymize(row));
        Assert.Equal(0, OcrScanPii.Anonymize(row));
    }

    [Fact]
    public void ช่องที่ประกาศว่าไม่ใช่ข้อมูลส่วนบุคคล_ต้องไม่ทับกับช่องที่ต้องล้าง()
    {
        Assert.Empty(OcrScanPii.PersonalDataFields.Intersect(OcrScanPii.NotPersonalDataFields));
    }
}
