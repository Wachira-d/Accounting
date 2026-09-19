using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ชนิดผู้ติดต่อ = ตัวตัดสิน ภ.ง.ด.3 vs ภ.ง.ด.53 และ scheme ของ e-Tax (NIDN/TXID)**
/// — <c>Helpers/ContactTypeResolver</c> คือ OWNER file ตัวเดียวของกติกานี้
/// (DECISION_AUDIT_2026-09-18 §9.3 D-1)
///
/// <para><b>สองครึ่งตาม G7</b>:</para>
/// <list type="bullet">
/// <item><b>ครึ่งที่พัง→ถูก</b>: "ตัดสินไม่ได้" ต้องออกมาเป็น <c>Unknown</c>
///   ไม่ใช่ <c>Individual</c> (ของเดิมเดาเป็นบุคคลธรรมดาทุกครั้ง)</item>
/// <item><b>ครึ่งที่ถูกอยู่แล้ว→ห้ามแตะ</b>: คู่ค้าที่เลขภาษี/ชนิดถูกอยู่แล้ว
///   ต้องได้คำตอบเดิมทุกตัว และค่าที่คนเคยตั้งใจตั้งต้องไม่ถูกลดระดับ</item>
/// </list>
/// </summary>
public class ContactTypeResolverTests
{
    private static string WithCheckDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (13 - i);
        return first12 + (char)('0' + (11 - sum % 11) % 10);
    }

    private static readonly string Juristic = WithCheckDigit("010554013452");   // ขึ้นต้น 0
    private static readonly string Citizen = WithCheckDigit("110554013452");    // บัตรประชาชน

    // ═══ ครึ่งที่ 1 — "ไม่รู้" ต้องเป็นคำตอบที่เห็นได้ (G3) ═══

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData(null, "1234", "สมชาย")]                   // เลขไม่ครบ 13
    [InlineData(null, "N/A", "ร้านลุงหมี")]                // ไม่ใช่ตัวเลข
    [InlineData("ไม่รู้จักคำนี้", null, "ABC Trading")]      // ชนิดที่แปลงไม่ได้ + ชื่อไม่บ่งชี้
    public void ไม่มีหลักฐานเลย_ต้องได้_Unknown_ไม่ใช่บุคคลธรรมดา(
        string? declared, string? taxId, string? name)
    {
        var v = ContactTypeResolver.Resolve(declared, taxId, name);
        Assert.Equal(ContactType.Unknown, v.Type);
        Assert.Equal(ContactTypeEvidence.None, v.Evidence);
        Assert.False(v.IsResolved);
        Assert.NotEqual("", v.Reason);      // ต้องอธิบายได้ ไม่ใช่เงียบ
    }

    [Fact]
    public void ชนิดที่ส่งมาเป็น_Unknown_ถือว่าไม่ได้ส่ง_ไม่ใช่การประกาศ()
    {
        Assert.Null(ContactTypeResolver.ParseDeclared("Unknown"));
        Assert.Null(ContactTypeResolver.ParseDeclared("0"));
    }

    [Fact]
    public void เลขบาร์โค้ดที่ผ่าน_mod11_โดยบังเอิญยังตัดสินไม่ได้ถ้า_checksum_ไม่ผ่าน()
    {
        var broken = Juristic[..12] + (char)('0' + (Juristic[12] - '0' + 1) % 10);
        Assert.Null(ContactTypeResolver.FromTaxId(broken));
        Assert.Equal(ContactType.Unknown, ContactTypeResolver.Resolve(null, broken, "สมชาย ใจดี").Type);
    }

    // ═══ ครึ่งที่ 2 — ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ ═══

    [Fact]
    public void เลขนิติบุคคลที่ถูกอยู่แล้วยังเป็นนิติบุคคลเหมือนเดิม()
    {
        var v = ContactTypeResolver.Resolve(null, Juristic, "บริษัท ตัวอย่าง จำกัด");
        Assert.Equal(ContactType.JuristicPerson, v.Type);
        Assert.Equal(ContactTypeEvidence.TaxIdShape, v.Evidence);
    }

    [Fact]
    public void เลขบัตรประชาชนยังเป็นบุคคลธรรมดาเหมือนเดิม()
    {
        var v = ContactTypeResolver.Resolve(null, Citizen, "สมชาย ใจดี");
        Assert.Equal(ContactType.Individual, v.Type);
        Assert.Equal(ContactTypeEvidence.TaxIdShape, v.Evidence);
    }

    [Fact]
    public void ค่าที่ผู้ใช้เลือกชนะรูปเลขเสมอ_และต้องบันทึกตัวที่แพ้ไว้()
    {
        // ราชการถือเลขขึ้นต้น 0 เหมือนบริษัท ⇒ ถ้ารูปเลขชนะ ราชการจะถูกกดเป็นบริษัททุกรอบ
        var gov = ContactTypeResolver.Resolve("GovernmentAgency", Juristic, "กรมสรรพากร");
        Assert.Equal(ContactType.GovernmentAgency, gov.Type);
        Assert.DoesNotContain("⚠️", gov.Reason);   // ราชการ+เลข 0 ไม่ถือว่าขัดกัน

        // ขัดกันจริง: ประกาศว่าบุคคล แต่เลขเป็นทะเบียนนิติบุคคล → ใช้ค่าที่ประกาศ + เตือน
        var clash = ContactTypeResolver.Resolve("Individual", Juristic, "บริษัท ก จำกัด");
        Assert.Equal(ContactType.Individual, clash.Type);
        Assert.Contains("⚠️", clash.Reason);
        Assert.Contains("นิติบุคคล", clash.Reason);
    }

    [Fact]
    public void ชื่อที่มีคำบ่งชี้นิติบุคคลใช้ได้แค่ตอนไม่มีหลักฐานอื่น()
    {
        // ชั้นล่างสุดเป็น private โดยตั้งใจ ⇒ ทดสอบผ่าน Resolve เท่านั้น
        foreach (var n in new[] { "บริษัท ก จำกัด", "ABC Co.,Ltd.", "หจก. ข การช่าง" })
        {
            var j = ContactTypeResolver.Resolve(null, null, n);
            Assert.Equal(ContactType.JuristicPerson, j.Type);
            Assert.Equal(ContactTypeEvidence.NamePrefix, j.Evidence);
        }

        // "ไม่มีคำว่าบริษัท" ไม่ได้แปลว่าเป็นบุคคล — ต้องตอบ Unknown ไม่ใช่ Individual
        // (ชื่อแบรนด์ละตินบนโลโก้ของนิติบุคคลไทย — บั๊กจริง 2026-09-18)
        Assert.Equal(ContactType.Unknown, ContactTypeResolver.Resolve(null, null, "DECATHLON").Type);

        // ร้านค้าส่วนใหญ่ในไทยเป็นบุคคลธรรมดา — คำว่า "ร้าน" ห้ามยกเป็นนิติบุคคล
        Assert.Equal(ContactType.Unknown, ContactTypeResolver.Resolve(null, null, "ร้านลุงหมีการค้า").Type);

        // แต่เมื่อมีเลขภาษีของบุคคล เลขต้องชนะชื่อ
        Assert.Equal(ContactType.Individual,
            ContactTypeResolver.Resolve(null, Citizen, "บริษัท ก จำกัด").Type);
    }

    [Fact]
    public void ทะเบียนราชการยืนยันได้เมื่อเลขใช้ไม่ได้()
    {
        var v = ContactTypeResolver.Resolve(null, "ยังไม่มีเลข", "ก จำกัด", registryJuristic: true);
        Assert.Equal(ContactType.JuristicPerson, v.Type);
        Assert.Equal(ContactTypeEvidence.Registry, v.Evidence);
    }

    // ═══ การเขียนลงแถวเดิม — ค่าที่คนตั้งไว้ห้ามถูกลดระดับ ═══

    [Fact]
    public void รอบที่ไม่มีหลักฐานใหม่_ต้องคงค่าเดิม()
    {
        Assert.Equal(ContactType.JuristicPerson,
            ContactTypeResolver.ApplyToExisting(ContactType.JuristicPerson, null, "", null).Type);
        Assert.Equal(ContactType.GovernmentAgency,
            ContactTypeResolver.ApplyToExisting(ContactType.GovernmentAgency, null, "ไม่ใช่ตัวเลข", null).Type);
        Assert.Equal(ContactType.Individual,
            ContactTypeResolver.ApplyToExisting(ContactType.Individual, null, null, null).Type);
    }

    [Fact]
    public void เลขบุคคลธรรมดาซ่อมแถวที่เคยถูกประทับผิดให้กลับมาถูก()
    {
        // กติกาเก่า (Length == 13 ⇒ นิติบุคคล) ประทับผิดไว้ — sync รอบถัดไปต้องแก้กลับ
        Assert.Equal(ContactType.Individual,
            ContactTypeResolver.ApplyToExisting(ContactType.JuristicPerson, null, Citizen, null).Type);
    }

    [Fact]
    public void รูปเลขห้ามลดระดับหน่วยงานราชการเป็นบริษัท()
    {
        // ราชการกับบริษัทใช้เลขขึ้นต้น 0 เหมือนกัน — รูปเลขแยกไม่ได้ ⇒ ห้ามทับ
        var v = ContactTypeResolver.ApplyToExisting(ContactType.GovernmentAgency, null, Juristic, "กรมสรรพากร");
        Assert.Equal(ContactType.GovernmentAgency, v.Type);
    }

    [Fact]
    public void แถวที่ยังไม่รู้_เติมได้ด้วยผลอนุมาน_และยังเป็น_Unknown_ต่อได้()
    {
        Assert.Equal(ContactType.JuristicPerson,
            ContactTypeResolver.ApplyToExisting(ContactType.Unknown, null, Juristic, null).Type);
        Assert.Equal(ContactType.Unknown,
            ContactTypeResolver.ApplyToExisting(ContactType.Unknown, null, null, "ABC").Type);
    }

    // ═══ รหัสสาขา — §86/4(2) ผูกกับชนิด ═══

    [Fact]
    public void รหัสสาขาเก็บได้เฉพาะนิติบุคคลและราชการ()
    {
        Assert.Equal("00000", ContactTypeResolver.BranchCodeFor(ContactType.JuristicPerson, "00000"));
        Assert.Equal("00012", ContactTypeResolver.BranchCodeFor(ContactType.JuristicPerson, "12"));
        Assert.Equal("00000", ContactTypeResolver.BranchCodeFor(ContactType.GovernmentAgency, "0"));
    }

    [Fact]
    public void บุคคลธรรมดาไม่เก็บ_00000_แต่ยังเก็บสาขาย่อยจริงของผู้จด_VAT()
    {
        // "00000" ที่ตัวเติมฟอร์ม/OCR ใส่ให้เอง = ต้องไม่ติดท้ายเลขบัตรประชาชน
        // เป็น "(สำนักงานใหญ่)" บนใบกำกับ
        Assert.Null(ContactTypeResolver.BranchCodeFor(ContactType.Individual, "00000"));
        // แต่บุคคลจด VAT ที่มีสาขาย่อยจริงมีอยู่ — ห้ามลบทิ้ง
        Assert.Equal("00003", ContactTypeResolver.BranchCodeFor(ContactType.Individual, "00003"));
    }

    [Fact]
    public void ยังไม่รู้ชนิด_ห้ามแต่งรหัสสาขาให้()
    {
        Assert.Null(ContactTypeResolver.BranchCodeFor(ContactType.Unknown, "00000"));
        // และห้ามยกระดับด้วยรหัสศูนย์ล้วน — "00000" คือค่าที่ระบบเติมเอง ไม่ใช่หลักฐาน
        Assert.Null(ContactTypeResolver.BranchCodeFor(ContactType.Unknown, "0"));
    }

    [Fact]
    public void สาขาย่อยจริงยกระดับได้เฉพาะตอนยังไม่รู้_ห้ามล้มชั้นที่สูงกว่า()
    {
        // ยังไม่รู้ + มีสาขาย่อยจริง ⇒ นิติบุคคล (สาขาเป็นเรื่องของนิติบุคคล)
        var up = ContactTypeResolver.ResolveWithBranch(null, null, "00007", "ABC");
        Assert.Equal(ContactType.JuristicPerson, up.Type);
        Assert.Equal("00007", up.BranchCode);

        // แต่ "00000" ไม่ใช่หลักฐาน — เป็นค่า default ที่ระบบเติมเอง
        var noUp = ContactTypeResolver.ResolveWithBranch(null, null, "00000", "ABC");
        Assert.Equal(ContactType.Unknown, noUp.Type);
        Assert.Null(noUp.BranchCode);

        // เลขบัตรประชาชนชนะสาขา — สาขาย่อยห้ามพลิกคนให้เป็นบริษัท
        var person = ContactTypeResolver.ResolveWithBranch(null, Citizen, "00007", "สมชาย");
        Assert.Equal(ContactType.Individual, person.Type);
        Assert.Equal("00007", person.BranchCode);
    }

    [Fact]
    public void รหัสสาขาผิดรูปห้ามแต่งเป็นศูนย์()
    {
        Assert.Null(ContactTypeResolver.NormalizeBranchCode(null));
        Assert.Null(ContactTypeResolver.NormalizeBranchCode("   "));
        Assert.Null(ContactTypeResolver.NormalizeBranchCode("สาขา"));
        Assert.Null(ContactTypeResolver.NormalizeBranchCode("123456"));   // เกิน 5 หลัก
        Assert.Equal("00000", ContactTypeResolver.NormalizeBranchCode("0"));
    }

    // ═══ ป้ายไทย — ตัวแปลงตัวเดียว ═══

    [Fact]
    public void ป้ายไทยของทุกค่ารวมถึง_Unknown()
    {
        Assert.Equal("บุคคลธรรมดา", ContactTypeResolver.ThaiLabel(ContactType.Individual));
        Assert.Equal("นิติบุคคล", ContactTypeResolver.ThaiLabel(ContactType.JuristicPerson));
        Assert.Equal("หน่วยงานราชการ", ContactTypeResolver.ThaiLabel(ContactType.GovernmentAgency));
        Assert.Equal("ยังไม่ระบุ", ContactTypeResolver.ThaiLabel(ContactType.Unknown));
    }

    [Theory]
    [InlineData("JuristicPerson", ContactType.JuristicPerson)]
    [InlineData("juristic", ContactType.JuristicPerson)]
    [InlineData("company", ContactType.JuristicPerson)]
    [InlineData("นิติบุคคล", ContactType.JuristicPerson)]
    [InlineData("2", ContactType.JuristicPerson)]
    [InlineData("Individual", ContactType.Individual)]
    [InlineData("person", ContactType.Individual)]
    [InlineData("1", ContactType.Individual)]
    [InlineData("government", ContactType.GovernmentAgency)]
    [InlineData("3", ContactType.GovernmentAgency)]
    public void คำที่พาร์ตเนอร์ใช้จริงต้องแปลงได้(string sent, ContactType expected)
        => Assert.Equal(expected, ContactTypeResolver.ParseDeclared(sent));

    [Fact]
    public void ค่าเริ่มต้นของ_enum_ต้องเป็น_Unknown()
    {
        // `default(ContactType)` คือค่าที่ entity/DTO ที่ลืมตั้งค่าจะได้ —
        // ต้องเป็น "ยังไม่รู้" ไม่ใช่ "บุคคลธรรมดา"
        Assert.Equal(ContactType.Unknown, default(ContactType));
        Assert.Equal(0, (int)ContactType.Unknown);
        // เลขเดิมของ 3 ค่าต้องไม่ขยับ — ข้อมูลเก่าในฐานเก็บเป็น 1/2/3
        Assert.Equal(1, (int)ContactType.Individual);
        Assert.Equal(2, (int)ContactType.JuristicPerson);
        Assert.Equal(3, (int)ContactType.GovernmentAgency);
    }
}
