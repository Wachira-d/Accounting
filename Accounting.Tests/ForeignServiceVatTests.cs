using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>§83/6 reverse charge — การแยกขาเครดิตของใบซื้อบริการต่างประเทศ
///
/// ที่มา (ผู้ใช้รายงาน 2026-09-13): ใบสำคัญจ่าย Booking.com สองใบ — ใบที่อนุมัติแล้ว
/// แสดง JE ถูกต้อง (Cr 21912 ภ.พ.36 + Cr ธนาคารเฉพาะฐาน) แต่ใบใหม่ที่ยังไม่อนุมัติ
/// แสดง "ประมาณการ" ที่ **ไม่มีบรรทัด ภ.พ.36 เลย** และเครดิตเจ้าหนี้ด้วยยอดรวม VAT
/// ⇒ ดูเหมือนระบบเปลี่ยนไปบันทึกผิด ทั้งที่ posting จริงไม่ได้เปลี่ยน —
/// พรีวิวเป็น renderer ตัวที่สามที่ไม่เคยรู้จักกฎนี้ (`git log -S"21912"` บน
/// PdfGenerationService ว่างเปล่า). เทสต์นี้ล็อกกติกาให้ทุก renderer ใช้ร่วมกัน</summary>
public class ForeignServiceVatTests
{
    // เลขจริงจากใบที่ผู้ใช้ส่งมา (PV-20260801-0003 · Booking.com B.V.)
    private const decimal Base = 10_074.75m;
    private const decimal Vat = 705.23m;
    private const decimal Gross = 10_779.98m;

    [Fact]
    public void บริการต่างประเทศ_ผู้รับเงินได้เฉพาะฐาน_VAT_ไปเป็นหนี้_ภพ36()
    {
        var split = ForeignServiceVat.SplitCredit(isForeignService: true, Gross, Vat);

        // ผู้ขาย ตปท. ไม่เก็บ VAT ไทย ⇒ จ่าย/ตั้งหนี้เขาแค่ฐาน
        Assert.Equal(Base, split.PayeeCredit);
        // VAT ที่ประเมินเองเป็นหนี้ต่อสรรพากร ไม่ใช่ต่อผู้ขาย
        Assert.Equal(Vat, split.Pp36Credit);
        // ขาเครดิตรวมต้องเท่าเดิมเสมอ — JE ต้องยังบาลานซ์
        Assert.Equal(Gross, split.PayeeCredit + split.Pp36Credit);
    }

    [Fact]
    public void ใบในประเทศ_ต้องไม่ถูกแตะ_ขาเครดิตเป็นยอดรวมเหมือนเดิม()
    {
        // ทิศตรงข้าม — ถ้าเทสต์มีแต่เคสต่างประเทศ การ "แยกทุกใบ" ก็ผ่านได้
        var split = ForeignServiceVat.SplitCredit(isForeignService: false, Gross, Vat);

        Assert.Equal(Gross, split.PayeeCredit);
        Assert.Equal(0m, split.Pp36Credit);
    }

    [Theory]
    // ใบต่างประเทศที่ไม่มี VAT (ผู้ขายที่ไม่อยู่ในบังคับ / บริการใช้นอกไทย)
    // ⇒ ไม่มีหนี้ ภ.พ.36 ให้ตั้ง — ห้ามสร้างบรรทัด 21912 ยอด 0
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    // ค่าติดลบ (ใบลดหนี้) ต้องไม่ถูกตีเป็นหนี้ ภ.พ.36 — เส้นนั้นมีกติกาของตัวเอง
    [InlineData(true, -100)]
    public void ไม่มี_VAT_หรือ_VAT_ติดลบ_ต้องไม่ตั้งหนี้_ภพ36(bool foreign, int vatWhole)
    {
        // รับเป็น int แล้วแปลงเอง — InlineData กับพารามิเตอร์ decimal ต้องพึ่ง
        // การแปลงชนิดของ xUnit ซึ่งไม่คุ้มจะเสี่ยงในเทสต์ที่คอมไพล์ที่นี่ไม่ได้
        decimal vat = vatWhole;

        Assert.Equal(0m, ForeignServiceVat.SelfAssessedVat(foreign, vat));

        var split = ForeignServiceVat.SplitCredit(foreign, Gross, vat);
        Assert.Equal(Gross, split.PayeeCredit);
        Assert.Equal(0m, split.Pp36Credit);
    }

    [Fact]
    public void ยอดเครดิตที่ส่งเข้ามาเป็นของผู้เรียก_WHT_คนละเรื่องกับ_มาตรา83_6()
    {
        // สาย accrual (cash basis) ตัดเจ้าหนี้ gross = ยอดจ่าย + WHT ก่อนแยก ภ.พ.36
        // — กติกา WHT อยู่นอกฟังก์ชันนี้ ตัวมันรู้แค่ "ก้อนที่ส่งมา ลบ VAT ออก"
        const decimal wht = 302.24m;
        var split = ForeignServiceVat.SplitCredit(true, Gross + wht, Vat);

        Assert.Equal(Base + wht, split.PayeeCredit);
        Assert.Equal(Vat, split.Pp36Credit);
    }

    [Fact]
    public void ผังบัญชีที่กติกาอ้าง_ต้องเป็นชุดเดียวกับที่_JE_จริงใช้()
    {
        // คอนสแตนต์สองตัวนี้คือสิ่งที่ทำให้ posting จริงกับพรีวิวพูดตรงกัน —
        // เปลี่ยนเมื่อไรต้องเปลี่ยนพร้อมกันทั้งสองฝั่ง
        Assert.Equal("21912", ForeignServiceVat.Pp36PayableCode);
        Assert.Equal("11640", ForeignServiceVat.Pp36InputVatCode);
    }
}
