using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวตัดสิน "ผู้ถูกหักอยู่แบบ ภ.ง.ด.3 / 53 / 54" — ต้องเป็น**ตัวเดียว**ของทุกหน้า
///
/// ที่มา (2026-09-16): ผู้ใช้พบว่า ภ.ง.ด.53 เดือน ก.ค. บนหน้า "รายงานภาษี" ถูกต้อง
/// แต่หน้า "นำส่งภาษี" แสดงคนละยอด. สาเหตุหนึ่งคือหน้านำส่ง + ปฏิทินยื่นแบ่ง 3/53
/// จาก <c>Contact.ContactType</c> **ดิบ ๆ** (default = Individual และมี 4 ทางเข้าที่
/// ไม่เคยตั้งค่า: API v1 / import CSV / OCR) ขณะที่ทะเบียน 50 ทวิ + รายงาน + ไฟล์
/// ยื่นใช้ตัวตัดสิน 3 สัญญาณมาตลอด
///
/// เทสต์ล็อก **สองทิศ**: เคสที่เคยพังต้องถูก และเคสธรรมดาต้องได้ผลเดิม
/// </summary>
public class WhtPayeeKindTests
{
    // ── ทิศที่เคยพัง: ContactType ไม่ตรงความจริง ──

    [Fact]
    public void เลขภาษีขึ้นต้นศูนย์_ชนะ_ContactType_ที่ตั้งผิด()
    {
        // "บริษัท ก จำกัด" ที่ถูกสร้างจาก OCR/import → ContactType ค้างที่ Individual
        Assert.Equal(TaxType.WithholdingTax53,
            WhtPayeeKind.ResolveForm(false, null, "0105558123456",
                ContactType.Individual, "บริษัท ก จำกัด"));
    }

    [Fact]
    public void ชื่อบ่งชี้นิติบุคคล_ชนะเมื่อไม่มีเลขภาษี()
    {
        Assert.Equal(TaxType.WithholdingTax53,
            WhtPayeeKind.ResolveForm(false, null, null, ContactType.Individual, "หจก. ข ขนส่ง"));
        Assert.Equal(TaxType.WithholdingTax53,
            WhtPayeeKind.ResolveForm(false, null, "", ContactType.Individual, "ACME Co.,Ltd."));
    }

    [Theory]
    [InlineData("1234567890123")]  // บุคคลธรรมดา — ขึ้นต้น 1
    [InlineData("8234567890123")]  // บุคคลธรรมดา — ขึ้นต้น 8
    public void เลขบัตรประชาชน_ชนะ_ContactType_ที่ตั้งเป็นนิติบุคคล(string taxId)
    {
        Assert.Equal(TaxType.WithholdingTax3,
            WhtPayeeKind.ResolveForm(false, null, taxId, ContactType.JuristicPerson, "สมชาย ใจดี"));
    }

    // ── ม.70: ต่างประเทศต้องอยู่ 54 เท่านั้น — **สองสัญญาณ** ──

    [Fact]
    public void ธงบนเอกสาร_พาไป54_แม้คู่ค้าไม่ได้กรอกประเทศ()
    {
        // เคส Booking.com: §83/6 ติ๊กไว้ แต่ Contact ไม่มี CountryCode
        Assert.Equal(TaxType.WithholdingTax54,
            WhtPayeeKind.ResolveForm(true, null, "0105558123456",
                ContactType.JuristicPerson, "Booking.com B.V."));
    }

    [Fact]
    public void ประเทศของคู่ค้า_พาไป54_แม้ไม่ได้ติ๊กบริการต่างประเทศ()
    {
        // ค่าสิทธิ/ดอกเบี้ย ม.70 ที่ไม่มี §83/6
        Assert.Equal(TaxType.WithholdingTax54,
            WhtPayeeKind.ResolveForm(false, "SG", null, ContactType.JuristicPerson, "Foo Pte Ltd"));
    }

    // ── ทิศที่ต้อง "ไม่เปลี่ยน": เคสปกติต้องได้ผลเดิม ──

    [Fact]
    public void บุคคลธรรมดาไทยปกติ_ยังอยู่_ภงด3()
    {
        Assert.Equal(TaxType.WithholdingTax3,
            WhtPayeeKind.ResolveForm(false, "TH", "1103700123456",
                ContactType.Individual, "สมหญิง รักดี"));
    }

    [Fact]
    public void นิติบุคคลไทยปกติ_ยังอยู่_ภงด53()
    {
        Assert.Equal(TaxType.WithholdingTax53,
            WhtPayeeKind.ResolveForm(false, "TH", "0105558123456",
                ContactType.JuristicPerson, "บริษัท ข จำกัด"));
    }

    [Fact]
    public void ประเทศไทยระบุเป็น_TH_ไม่ถือว่าต่างประเทศ()
    {
        Assert.False(WhtPayeeKind.IsForeignPayee(false, "TH"));
        Assert.False(WhtPayeeKind.IsForeignPayee(false, "th"));
        Assert.False(WhtPayeeKind.IsForeignPayee(false, ""));
        Assert.False(WhtPayeeKind.IsForeignPayee(false, null));
    }

    // ── "ไม่รู้" ต้องไม่ถูกอ่านเป็น "บุคคลธรรมดา" โดยอัตโนมัติ ──

    [Fact]
    public void ไม่มีสัญญาณชัด_Detect_คืน_null_แต่_ResolveForm_ต้องลงแบบใดแบบหนึ่งเสมอ()
    {
        // ContactType = 0 (ยังไม่เคยถูกตั้ง — enum นี้ไม่มีสมาชิกค่า 0) + ไม่มีเลข
        // ภาษี + ชื่อที่ไม่บ่งชี้อะไร ⇒ "ไม่รู้" ต้องไม่ถูกอ่านเป็นบุคคลธรรมดา
        var kind = WhtPayeeKind.Detect(null, (ContactType)0, "ร้านสมชาย");
        Assert.Null(kind);
        // แต่ห้ามหายจากทั้ง ภ.ง.ด.3 และ 53 — ต้องลงแบบใดแบบหนึ่งเสมอ
        var form = WhtPayeeKind.ResolveForm(false, null, null, (ContactType)0, "ร้านสมชาย");
        Assert.True(form is TaxType.WithholdingTax3 or TaxType.WithholdingTax53);
    }

    [Fact]
    public void เลขภาษีที่มีขีดคั่น_ยังอ่านได้()
    {
        Assert.Equal(TaxType.WithholdingTax53,
            WhtPayeeKind.ResolveForm(false, null, "0-1055-58123-45-6",
                ContactType.Individual, "ไม่ระบุ"));
    }
}
