using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ปิดลูปการเรียนรู้ที่ "เอกสารที่ลงจริง" — ผู้ใช้แก้ Draft ก่อนอนุมัติแล้วนักเรียน
/// ต้องรู้ · แต่ห้ามลบค่าที่สแกนอ่านมาได้ด้วยช่องว่าง/ศูนย์ (สถาปัตยกรรมเป้าหมาย D3)
/// </summary>
public class OcrPostedTruthTests
{
    [Fact]
    public void ผู้ใช้แก้ชื่อผู้ขายและวันที่ก่อนอนุมัติ_ต้องถูกบันทึกกลับ()
    {
        var scan = new OcrPostedSnapshot(
            VendorName: "บจก. สยาม เทรดตึ้ง",           // OCR อ่านเพี้ยน
            VendorTaxId: "0105556123456",
            DocumentDate: new DateTime(2026, 8, 1),
            TotalAmount: 1070m);
        var posted = new OcrPostedSnapshot(
            VendorName: "บริษัท สยาม เทรดดิ้ง จำกัด",   // ผู้ใช้แก้ตอนเปิด Draft
            VendorTaxId: "0105556123456",
            DocumentDate: new DateTime(2026, 8, 15),
            TotalAmount: 1070m);

        var d = OcrPostedTruth.Diff(scan, posted);
        Assert.True(d.HasChanges);
        Assert.Equal("บริษัท สยาม เทรดดิ้ง จำกัด", d.VendorName);
        Assert.Equal(new DateTime(2026, 8, 15), d.DocumentDate);
        Assert.Null(d.VendorTaxId);     // ไม่ต่าง = ไม่แตะ
        Assert.Null(d.TotalAmount);
    }

    [Fact]
    public void เอกสารไม่ได้กรอกช่องนั้น_ต้องไม่ลบค่าที่สแกนอ่านมาได้()
    {
        // เลขใบกำกับยังไม่มา (ใบแจ้งหนี้ล่วงหน้า) ⇒ เอกสารไม่มีค่า
        // ห้ามไปล้างเลขที่สแกนอ่านได้ — "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ"
        // ใช้กับการลบด้วย ไม่ใช่แค่การเติม
        var scan = new OcrPostedSnapshot(
            VendorName: "บจก. ก", DocumentNumber: "INV-2026-0007",
            VendorBranchCode: "00003", SubTotal: 1000m);
        var posted = new OcrPostedSnapshot(
            VendorName: null, DocumentNumber: "  ",
            VendorBranchCode: null, SubTotal: null);

        var d = OcrPostedTruth.Diff(scan, posted);
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void อนุมัติซ้ำ_ต้องไม่มีอะไรเปลี่ยน()
    {
        // re-approve ต้องไม่สร้างแถวเรียนรู้ปลอมเพิ่มทุกครั้ง
        var same = new OcrPostedSnapshot(
            VendorName: "  บจก. ก  ", VendorTaxId: "0105556123456",
            VendorBranchCode: "00000", DocumentNumber: "TIV-1",
            DocumentDate: new DateTime(2026, 8, 15, 13, 45, 0),
            SubTotal: 1000m, VatAmount: 70m, TotalAmount: 1070m,
            TargetDocumentType: "PurchaseInvoice");
        var posted = same with
        {
            VendorName = "บจก. ก",
            // เวลาต่างกันแต่วันเดียวกัน = ไม่ต่าง
            DocumentDate = new DateTime(2026, 8, 15, 0, 0, 0),
            // ต่างกัน 1 สตางค์ = ปัดเศษ ไม่ใช่การแก้
            TotalAmount = 1070.01m,
        };
        Assert.False(OcrPostedTruth.Diff(same, posted).HasChanges);
    }

    [Fact]
    public void ชนิดเอกสารที่เกิดจริงต่างจากที่ระบบเดา_ต้องถูกบันทึกกลับ()
    {
        var scan = new OcrPostedSnapshot(TargetDocumentType: "Expense");
        var posted = new OcrPostedSnapshot(TargetDocumentType: "PurchaseInvoice");
        Assert.Equal("PurchaseInvoice", OcrPostedTruth.Diff(scan, posted).TargetDocumentType);
    }

    [Fact]
    public void สแกนยังว่าง_เอกสารมีค่า_ต้องเติมให้()
    {
        var scan = new OcrPostedSnapshot(VendorBranchCode: null);
        var posted = new OcrPostedSnapshot(VendorBranchCode: "00003");
        Assert.Equal("00003", OcrPostedTruth.Diff(scan, posted).VendorBranchCode);
    }

    [Fact]
    public void ยอดติดลบ_ต้องไม่ถูกเขียนกลับ()
    {
        var scan = new OcrPostedSnapshot(SubTotal: 1000m);
        var posted = new OcrPostedSnapshot(SubTotal: -5m);
        Assert.Null(OcrPostedTruth.Diff(scan, posted).SubTotal);
    }
}
