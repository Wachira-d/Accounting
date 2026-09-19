using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ประเภทผู้ติดต่อ = ตัวตัดสิน ภ.ง.ด.3 (บุคคล) vs ภ.ง.ด.53 (นิติบุคคล)**
/// (DECISION_AUDIT_2026-09-18 §3 D8-3)
///
/// <para>บั๊กจริง: <c>ContactsV1Controller</c> ตัดสินด้วย <c>taxId.Length == 13</c>
/// ทั้งที่เลขผู้เสียภาษีไทย**ทุกแบบ**ยาว 13 หลัก รวมเลขบัตรประชาชน ⇒ คู่ค้า
/// บุคคลธรรมดาที่พาร์ตเนอร์ sync เข้ามากลายเป็นนิติบุคคลทุกราย ⇒ ยื่นผิดแบบ
/// และอัตราหัก ณ ที่จ่ายของดอกเบี้ย ม.40(4)(ก) ผิด (บุคคล 15% · นิติบุคคล 1%)</para>
///
/// <para>ล็อกสองทิศ: เลขบุคคลต้อง**ไม่**กลายเป็นนิติบุคคล **และ** เลขนิติบุคคล
/// ที่เคยถูกอยู่แล้วต้องไม่ถูกแตะ/ลดระดับ</para>
/// </summary>
public class V1ContactTypeTests
{
    // เลขที่ผ่าน checksum จริง (mod-11 สำนักทะเบียนกลาง) — ประกอบจาก 12 หลักแรก
    // แล้วเติมหลักตรวจสอบด้วยสูตรเดียวกับ ThaiTaxId.HasValidChecksum
    private static string WithCheckDigit(string first12)
    {
        Assert.Equal(12, first12.Length);
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (13 - i);
        return first12 + (char)('0' + (11 - sum % 11) % 10);
    }

    private static readonly string Juristic = WithCheckDigit("010554013452");   // ขึ้นต้น 0
    private static readonly string Citizen1 = WithCheckDigit("110554013452");   // บัตรประชาชน ขึ้นต้น 1
    private static readonly string Citizen8 = WithCheckDigit("810554013452");   // ต่างด้าว ขึ้นต้น 8

    [Fact]
    public void เลขนิติบุคคลขึ้นต้นศูนย์และผ่าน_checksum_เป็นนิติบุคคล()
    {
        Assert.True(ThaiTaxId.IsValid(Juristic));
        Assert.Equal(ContactType.JuristicPerson, ContactTypeResolver.FromTaxId(Juristic));
    }

    // ═══ ทิศที่เคยพัง: เลข 13 หลักของบุคคลธรรมดา ═══

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void เลขบัตรประชาชน_13_หลัก_ขึ้นต้น_1ถึง8_ไม่ใช่นิติบุคคล(int firstDigit)
    {
        var id = WithCheckDigit($"{firstDigit}10554013452");
        Assert.True(ThaiTaxId.IsValid(id));
        Assert.Equal(ContactType.Individual, ContactTypeResolver.FromTaxId(id));
        // กติกาเดิม (Length == 13) จะตอบ JuristicPerson ทุกเคสข้างบน
        Assert.NotEqual(ContactType.JuristicPerson, ContactTypeResolver.FromTaxId(id));
    }

    [Fact]
    public void เลขบัตรประชาชนกับเลขต่างด้าวได้บุคคลธรรมดาเหมือนกัน()
    {
        Assert.Equal(ContactType.Individual, ContactTypeResolver.FromTaxId(Citizen1));
        Assert.Equal(ContactType.Individual, ContactTypeResolver.FromTaxId(Citizen8));
    }

    // ═══ "ไม่รู้" ต้องเป็นคำตอบได้ — ห้ามเดา ═══

    [Fact]
    public void เลขไม่ผ่าน_checksum_ตัดสินไม่ได้()
    {
        // พลิกหลักตรวจสอบให้ผิด
        var broken = Juristic[..12] + (char)('0' + (Juristic[12] - '0' + 1) % 10);
        Assert.False(ThaiTaxId.HasValidChecksum(broken));
        Assert.Null(ContactTypeResolver.FromTaxId(broken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0105540134")]        // ไม่ครบ 13 หลัก
    [InlineData("ไม่ใช่ตัวเลขเลย")]
    public void เลขว่างหรือไม่ครบ_ตัดสินไม่ได้(string? taxId)
    {
        Assert.Null(ContactTypeResolver.FromTaxId(taxId));
    }

    [Fact]
    public void หลักแรกเป็น_9_ไม่มีการออกจริง_ตัดสินไม่ได้()
    {
        var nine = WithCheckDigit("910554013452");
        Assert.True(ThaiTaxId.HasValidChecksum(nine));   // checksum ผ่านแต่ใช้ไม่ได้
        Assert.Null(ContactTypeResolver.FromTaxId(nine));
    }

    [Fact]
    public void ตัดสินไม่ได้แล้วต้องคงค่าเดิม_ไม่เขียนทับ()
    {
        // แถวที่ผู้ใช้ตั้งเป็นนิติบุคคลไว้ แล้ว sync ส่งเลขพังมา → ห้ามลดระดับ
        Assert.Equal(ContactType.JuristicPerson,
            ContactTypeResolver.ApplyToExisting(ContactType.JuristicPerson, null, "", null).Type);
        Assert.Equal(ContactType.GovernmentAgency,
            ContactTypeResolver.ApplyToExisting(ContactType.GovernmentAgency, null, "ไม่ใช่ตัวเลข", null).Type);
        Assert.Equal(ContactType.Individual,
            ContactTypeResolver.ApplyToExisting(ContactType.Individual, null, null, null).Type);
    }

    // ═══ ทิศตรงข้าม: ของที่เคยถูกอยู่แล้วต้องไม่ถูกแตะ ═══

    [Fact]
    public void คู่ค้านิติบุคคลที่เคยถูกอยู่แล้ว_ยังเป็นนิติบุคคลเหมือนเดิม()
    {
        Assert.Equal(ContactType.JuristicPerson,
            ContactTypeResolver.ApplyToExisting(ContactType.JuristicPerson, null, Juristic, null).Type);
    }

    [Fact]
    public void เลขบุคคลธรรมดาแก้แถวที่เคยถูกประทับผิดให้กลับมาถูก()
    {
        // นี่คือเคสที่ migration ต้องซ่อมย้อนหลัง: แถวเดิมเป็น JuristicPerson
        // เพราะกติกาเก่า — เมื่อ sync รอบถัดไปมาถึง ต้องถูกแก้กลับเป็นบุคคลธรรมดา
        Assert.Equal(ContactType.Individual,
            ContactTypeResolver.ApplyToExisting(ContactType.JuristicPerson, null, Citizen1, null).Type);
    }
}
