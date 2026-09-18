using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **บัญชี "ภาษีหัก ณ ที่จ่ายค้างจ่าย" ต้องมาจากที่เดียว** (ผลตรวจรอบ 180)
///
/// ═══ บั๊กที่ล็อกไว้ ═══ การเลือก 21916/21917/21918 ถูกตัดสินจาก **4 ที่** ด้วยกติกา
/// คนละชุด และ **3 ใน 4 ไม่รู้จัก 21918 (ภ.ง.ด.54) เลย**:
/// · `DocumentService` ผ่าน `WhtPayeeKind` ✅ · `PdfGenerationService` (พรีวิว GL) ใช้
/// `ContactType` ดิบ ⇒ **พรีวิวโชว์บัญชีหนึ่ง JE จริงลงอีกบัญชี** · `IntegrationService`
/// สองจุดเดาเอง (`ContactType` ดิบ / `TaxId.StartsWith("0")`)
///
/// ล็อกสองครึ่ง: ครึ่งที่พิสูจน์ว่าเลือกถูกตามชนิดผู้รับ และครึ่งที่พิสูจน์ว่า
/// **ยังมีตาข่ายรองเมื่อผังบัญชีตั้งไม่ครบ** (ลำดับรอง ไม่ใช่คืนค่าว่าง)
/// </summary>
public class WhtPayableAccountTests
{
    private const string JuristicTaxId = "0105556123453";   // ขึ้นต้น 0 = นิติบุคคล
    private const string PersonalId = "1103700123458";      // ขึ้นต้น 1 = บุคคลธรรมดา

    private static IReadOnlyList<string> Chain(
        bool foreignSvc = false, string? country = null, string? taxId = null,
        ContactType type = ContactType.Individual, string? name = null)
        => WhtPayableAccount.CodeChain(foreignSvc, country, taxId, type, name);

    // ══════════ ครึ่งแรก: เลือกถูกตามชนิดผู้รับ ══════════

    [Fact]
    public void นิติบุคคลไทย_ต้องได้_21917_ก่อน()
        => Assert.Equal(WhtPayableAccount.Pnd53Code, Chain(taxId: JuristicTaxId)[0]);

    [Fact]
    public void บุคคลธรรมดา_ต้องได้_21916_ก่อน()
        => Assert.Equal(WhtPayableAccount.Pnd3Code, Chain(taxId: PersonalId)[0]);

    [Fact]
    public void จ่ายต่างประเทศ_ต้องได้_21918_ก่อน()
    {
        // ทั้งสองสัญญาณต้องพาไป ภ.ง.ด.54 — ติ๊ก §83/6 หรือกรอกประเทศ
        Assert.Equal(WhtPayableAccount.Pnd54Code, Chain(foreignSvc: true, taxId: JuristicTaxId)[0]);
        Assert.Equal(WhtPayableAccount.Pnd54Code, Chain(country: "SG", name: "Foo Pte Ltd")[0]);
    }

    [Fact]
    public void ชื่อที่มีคำบ่งชี้นิติบุคคล_ชนะ_ContactType_ที่เป็นค่าตั้งต้น()
        // เคสจริง: contact ที่สร้างจากทางเข้าที่ไม่เคยตั้ง ContactType (default = Individual)
        // แต่ชื่อบอกชัดว่าเป็นนิติบุคคล ⇒ ต้องลง 21917 ไม่ใช่ 21916
        => Assert.Equal(WhtPayableAccount.Pnd53Code,
            Chain(type: ContactType.Individual, name: "บริษัท ทดสอบ จำกัด")[0]);

    [Fact]
    public void ป้ายชื่อบัญชีต้องตรงกับรหัสตัวแรกเสมอ()
    {
        // พรีวิวกับ JE จริงต้องพูดตรงกันคำต่อคำ
        Assert.Contains("54", WhtPayableAccount.PreferredLabel(true, null, JuristicTaxId, ContactType.JuristicPerson, null));
        Assert.Contains("53", WhtPayableAccount.PreferredLabel(false, null, JuristicTaxId, ContactType.JuristicPerson, null));
        Assert.Contains("3", WhtPayableAccount.PreferredLabel(false, null, PersonalId, ContactType.Individual, null));
    }

    // ══════════ ครึ่งหลัง: ผังไม่ครบต้องยังลงบัญชีได้ ══════════

    [Fact]
    public void ทุกกรณีต้องมีตัวสำรองอย่างน้อยหนึ่งตัว()
    {
        // ผังบัญชีที่ตั้งไม่ครบต้องยังลง JE ได้ ไม่ใช่ทำให้เอกสารลงบัญชีไม่ได้เลย
        Assert.True(Chain(taxId: JuristicTaxId).Count >= 2);
        Assert.True(Chain(taxId: PersonalId).Count >= 2);
        Assert.Equal(3, Chain(foreignSvc: true).Count);
    }

    [Fact]
    public void ลำดับห้ามมีรหัสซ้ำ()
    {
        var chain = Chain(foreignSvc: true, taxId: JuristicTaxId);
        Assert.Equal(chain.Count, chain.Distinct().Count());
    }

    [Fact]
    public void ไม่มีข้อมูลเลย_ต้องยังคืนลำดับที่ใช้งานได้()
        // "ไม่รู้" ต้องไม่กลายเป็นรายการว่าง — ไม่งั้น WHT ไม่ลง GL เลยแล้ว JE ไม่ balance
        => Assert.NotEmpty(Chain());
}
