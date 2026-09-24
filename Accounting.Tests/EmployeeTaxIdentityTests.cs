using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <b>D-01 (P0)</b> — เลขประจำตัวผู้เสียภาษีของพนักงานที่ใช้ออก 50 ทวิ ภ.ง.ด.1 / ไฟล์ยื่น
///
/// <para>เดิมด่านออก 50 ทวิ อ่าน <c>Employee.TaxId</c> เดี่ยว ๆ ซึ่งไม่มีจุดเขียนทั้งเรพ ⇒
/// ทุกพนักงานถูกข้าม ทุกงวด และนำส่ง ภ.ง.ด.1 ถูกบล็อก <c>WHT-CERT-UNISSUED</c> ตลอดกาล ·
/// และอีก 6 จุดเขียน fallback เองสองทิศ (<c>CitizenId ?? TaxId</c> vs <c>TaxId ?? CitizenId</c>)</para>
///
/// <para>สองครึ่ง: (1) พนักงานที่มีแต่เลขบัตรต้อง<b>ได้</b> 50 ทวิ (2) พนักงานที่ไม่มีทั้งคู่ต้อง
/// <b>ยังถูกข้ามพร้อมเตือน</b> (null — ห้ามแต่งเลข)</para>
/// </summary>
public class EmployeeTaxIdentityTests
{
    private const string Citizen = "1101700230708";   // checksum ผ่าน
    private const string ForeignTaxId = "8000000000014";

    [Fact]
    public void มีแต่เลขบัตรประชาชน_ได้เลขบัตรเป็นเลขผู้เสียภาษี_คือเคสที่เคยพัง()
        => Assert.Equal(Citizen, EmployeeTaxIdentity.Resolve(null, Citizen));

    [Fact]
    public void กรอกเลขผู้เสียภาษีเอง_ชนะเลขบัตร()
        => Assert.Equal(ForeignTaxId, EmployeeTaxIdentity.Resolve(ForeignTaxId, Citizen));

    [Fact]
    public void ต่างด้าวมีแต่เลขผู้เสียภาษี()
        => Assert.Equal(ForeignTaxId, EmployeeTaxIdentity.Resolve(ForeignTaxId, null));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData("-", " - ")]
    public void ไม่มีทั้งคู่_คืน_null_ให้ผู้เรียกข้ามพร้อมเตือน(string? taxId, string? citizenId)
        => Assert.Null(EmployeeTaxIdentity.Resolve(taxId, citizenId));

    [Fact]
    public void ตัดขีดและช่องว่าง_ให้ตรงรูป_Contact_TaxId_ที่ใช้จับคู่ใบ50ทวิ()
        => Assert.Equal(Citizen, EmployeeTaxIdentity.Resolve(null, "1-1017-00230-70-8"));

    [Fact]
    public void เลขผู้เสียภาษีว่างเปล่าไม่บังเลขบัตร()
        => Assert.Equal(Citizen, EmployeeTaxIdentity.Resolve("  ", Citizen));
}
