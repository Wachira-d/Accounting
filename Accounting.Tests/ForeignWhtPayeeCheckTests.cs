using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม WF — ฝ่ายค้าน W-5/W-7: คำเตือนอัตรา WHT จ่ายต่างประเทศ (ตอนอนุมัติเอกสาร · ตอนออก 50 ทวิ ภ.ง.ด.54 ด้วยมือ) ·
/// สองทิศ: หักขาด/ไม่หักเลยกับนิติบุคคลต่างประเทศต้องดัง · บุคคลธรรมดา/ผู้มีเลขนิติบุคคลไทย/ไม่รู้ประเภท ห้ามประกาศว่า "หักขาด"
/// </summary>
public class ForeignWhtPayeeCheckTests
{
    private static readonly DateTime Pay = new(2026, 9, 20);
    private static ForeignWhtDecision Fee => ForeignWhtRateResolver.ResolveForIncomeCode("2", "SG", ResidenceCertificate.None, Pay);

    [Fact]
    public void ขอบเขตผู้รับ_จากข้อมูลผู้ติดต่อ()
    {
        Assert.Equal(ForeignPayeeWhtScope.Section70, ForeignWhtPayeeCheck.ScopeOf(null, ContactType.Unknown, "Agoda Company Pte. Ltd."));
        Assert.Equal(ForeignPayeeWhtScope.Section70, ForeignWhtPayeeCheck.ScopeOf(null, ContactType.JuristicPerson, "Google Asia Pacific"));
        Assert.Equal(ForeignPayeeWhtScope.Individual, ForeignWhtPayeeCheck.ScopeOf(null, ContactType.Individual, "John Smith"));
        Assert.Equal(ForeignPayeeWhtScope.KindUnknown, ForeignWhtPayeeCheck.ScopeOf(null, ContactType.Unknown, "John Smith"));
        // มีเลขนิติบุคคลไทย (checksum ถูก) — อาจเป็นสาขาในไทยหรือแค่จด VAT e-Service ⇒ ไม่รู้
        Assert.Equal(ForeignPayeeWhtScope.ThaiRegisteredUnknown,
            ForeignWhtPayeeCheck.ScopeOf("0105536000003", ContactType.JuristicPerson, "Foreign Co., Ltd."));
    }

    [Fact]
    public void นิติบุคคลต่างประเทศ_ไม่หักเลย_และหัก3_ดัง_หัก15เงียบ()
    {
        var none = ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.Section70, Fee, 0m);
        Assert.NotNull(none);
        Assert.Contains("§54", none);
        Assert.Contains("15", none);
        Assert.NotNull(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.Section70, Fee, 3m));
        Assert.Null(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.Section70, Fee, 15m));
    }

    [Fact]
    public void บุคคลธรรมดาต่างประเทศ_ไม่ใช่ม70_ไม่เตือน()
    {
        Assert.Null(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.Individual, Fee, 0m));
        Assert.Null(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.Individual, Fee, 3m));
    }

    [Fact]
    public void มีเลขนิติบุคคลไทยหรือไม่รู้ประเภท_หักต่ำ_เตือนแบบบอกว่าไม่รู้_ไม่ประกาศว่าหักขาด()
    {
        var pe = ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.ThaiRegisteredUnknown, Fee, 3m);
        Assert.NotNull(pe);
        Assert.Contains("ตัดสินไม่ได้", pe);
        Assert.Contains("สาขา", pe);
        Assert.DoesNotContain("ต่ำกว่าที่ต้องหัก", pe);            // ข้อความ "หักขาด" ของตัวตัดสินอัตราไม่ถูกใช้
        var kind = ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.KindUnknown, Fee, 0m);
        Assert.NotNull(kind);
        Assert.Contains("ประเภทผู้ติดต่อ", kind);
        // หักครบ/เกิน ⇒ ไม่มีอะไรต้องบอก · ประเภทนอก ม.70 ⇒ ไม่มีอัตราให้เทียบ
        Assert.Null(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.ThaiRegisteredUnknown, Fee, 15m));
        Assert.Null(ForeignWhtPayeeCheck.Warning(ForeignPayeeWhtScope.KindUnknown,
            ForeignWhtRateResolver.ResolveForIncomeCode("8", "SG", ResidenceCertificate.None, Pay), 3m));
    }

    [Fact]
    public void หนังสือรับรองภงด54ที่ออกเอง_เตือนรายแถว_แถวที่ถูกเงียบ()
    {
        var w = ForeignWhtPayeeCheck.CertificateWarnings(ForeignPayeeWhtScope.Section70, "SG", new List<(string?, decimal, DateTime)>
        {
            ("2", 15m, Pay),      // ถูก
            ("2", 3m, Pay),       // อัตราในประเทศ = หักขาด
            ("4b", 10m, Pay),     // ปันผล ม.70 10% ถูก
        });
        var only = Assert.Single(w);
        Assert.StartsWith("แถวที่ 2:", only);
        Assert.Empty(ForeignWhtPayeeCheck.CertificateWarnings(ForeignPayeeWhtScope.Individual, "SG",
            new List<(string?, decimal, DateTime)> { ("2", 3m, Pay) }));
    }
}
