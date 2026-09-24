using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// <b>A05 / D-07 (P0)</b> — แก้ข้อมูลพนักงานแล้ว "สำเร็จ" แต่ค่าเดิมกลับมา + กับดัก
/// "ค่าที่ถูกปิดบังย้อนกลับมาเขียนทับของจริง"
///
/// <para>ครึ่งที่ 1 — ช่องที่เคยเงียบต้องถูกเขียนจริง (เลขบัตรที่พิมพ์ผิดแก้ได้ · ชื่อแก้ได้ ·
/// "" ล้างค่าได้) · ครึ่งที่ 2 — ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ: ไม่ส่ง = ไม่แตะ · ส่งค่าปิดบัง
/// ของเดิมกลับมา (ผู้ไม่มีสิทธิ์ pii:view) = ไม่แตะ · เลขเดิมแค่ต่างรูปแบบ = ไม่แตะ</para>
/// </summary>
public class EmployeeRecordEditTests
{
    private const string Citizen = "1101700230708";
    private const string Citizen2 = "3100500123458";

    // ═══════════ ครึ่งที่ 1 — ของที่เคยเงียบต้องถูกเขียน ═══════════

    [Fact]
    public void แก้เลขบัตรที่พิมพ์ผิด_ถูกเขียนเป็นเลขใหม่()
    {
        var r = EmployeeRecordEdit.ThaiIdNumber(Citizen2, Citizen, "เลขบัตรประชาชน");
        Assert.Null(r.Error);
        Assert.True(r.Changes);
        Assert.Equal(Citizen2, r.Value);
    }

    [Fact]
    public void เลขมีขีด_เก็บเป็นตัวเลขล้วน()
    {
        var r = EmployeeRecordEdit.ThaiIdNumber("3-1005-00123-45-8", null, "เลขบัตรประชาชน");
        Assert.True(r.Changes);
        Assert.Equal(Citizen2, r.Value);
    }

    [Fact]
    public void สตริงว่าง_ล้างค่า()
    {
        var r = EmployeeRecordEdit.ThaiIdNumber("", Citizen, "เลขประจำตัวผู้เสียภาษี");
        Assert.True(r.Changes);
        Assert.Null(r.Value);
    }

    [Theory]
    [InlineData("1101700230709")]   // check digit ผิด
    [InlineData("110170023070")]    // 12 หลัก
    [InlineData("9101700230708")]   // หลักแรก 9 ไม่มีการออก
    public void เลขผิด_ปฏิเสธพร้อมชื่อช่องภาษาไทย(string bad)
    {
        var r = EmployeeRecordEdit.ThaiIdNumber(bad, Citizen, "เลขบัตรประชาชน");
        Assert.False(r.Changes);
        Assert.StartsWith("เลขบัตรประชาชน:", r.Error);
    }

    [Fact]
    public void ค่าปิดบังที่ไม่ใช่ของเดิม_ปฏิเสธ_ห้ามเก็บ_X_ลงฐาน()
    {
        // ปิดบังของคนอื่น (เลขเดิมคือ Citizen2 = 3-XXXX…-8) หรือสร้างใหม่ด้วยค่าปิดบัง
        var r = EmployeeRecordEdit.ThaiIdNumber("1-XXXX-XXXXX-XX-8", Citizen2, "เลขบัตรประชาชน");
        Assert.NotNull(r.Error);
        Assert.NotNull(EmployeeRecordEdit.ThaiIdNumber("1-XXXX-XXXXX-XX-8", null, "เลขบัตรประชาชน").Error);
    }

    [Fact]
    public void ชื่อแก้ได้_และตัดช่องว่าง()
    {
        var r = EmployeeRecordEdit.RequiredText("  สมหญิง ", "ชื่อ (ไทย)");
        Assert.True(r.Changes);
        Assert.Equal("สมหญิง", r.Value);
    }

    [Fact]
    public void ชื่อว่าง_ปฏิเสธ()
        => Assert.Equal("ชื่อ (ไทย)ห้ามว่าง", EmployeeRecordEdit.RequiredText("  ", "ชื่อ (ไทย)").Error);

    [Fact]
    public void ช่องไม่บังคับ_สตริงว่างล้างเป็น_null()
    {
        var r = EmployeeRecordEdit.OptionalText("");
        Assert.True(r.Changes);
        Assert.Null(r.Value);
    }

    [Fact]
    public void รหัสพนักงาน_ยังไม่มีประวัติเงินเดือน_แก้ได้()
    {
        var r = EmployeeRecordEdit.EmployeeCode(" EMP002 ", "EMP001", hasPayrollHistory: false);
        Assert.True(r.Changes);
        Assert.Equal("EMP002", r.Value);
        Assert.Null(EmployeeRecordEdit.CodeLockReason(false));
    }

    [Fact]
    public void รหัสพนักงาน_มีประวัติเงินเดือนแล้ว_ปฏิเสธพร้อมเหตุผลเดียวกับที่หน้าจอแสดง()
    {
        var r = EmployeeRecordEdit.EmployeeCode("EMP002", "EMP001", hasPayrollHistory: true);
        Assert.False(r.Changes);
        Assert.Equal(EmployeeRecordEdit.CodeLockReason(true), r.Error);
        Assert.Contains("50 ทวิ", r.Error);
    }

    [Fact]
    public void วันเริ่มงานเกินหนึ่งปีข้างหน้า_ปฏิเสธ()
    {
        var now = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(EmployeeRecordEdit.StartDateError(now.AddYears(1).AddDays(1), now));
        Assert.Null(EmployeeRecordEdit.StartDateError(now.AddMonths(3), now));
    }

    // ═══════════ ครึ่งที่ 2 — ของที่ถูกอยู่แล้วต้องไม่ถูกแตะ ═══════════

    [Fact]
    public void ไม่ส่งช่อง_ไม่แตะ()
    {
        Assert.False(EmployeeRecordEdit.ThaiIdNumber(null, Citizen, "x").Changes);
        Assert.False(EmployeeRecordEdit.RequiredText(null, "x").Changes);
        Assert.False(EmployeeRecordEdit.OptionalText(null).Changes);
        Assert.False(EmployeeRecordEdit.EmployeeCode(null, "EMP001", true).Changes);
    }

    [Fact]
    public void ผู้ไม่มีสิทธิ์ดูPII_ส่งค่าปิดบังของเดิมกลับมา_ไม่แตะเลขจริง()
    {
        // ฟอร์มถูก hydrate ด้วย PiiMask.CitizenId(ของเดิม) แล้วกดบันทึกโดยไม่แตะช่องนี้
        var echoed = PiiMask.CitizenId(Citizen)!;
        var r = EmployeeRecordEdit.ThaiIdNumber(echoed, Citizen, "เลขบัตรประชาชน");
        Assert.Null(r.Error);
        Assert.False(r.Changes);
    }

    [Fact]
    public void ค่าปิดบังของเบอร์โทร_อีเมล_เลขบัญชี_ถูกจำได้ว่าเป็นค่าเดิม()
    {
        Assert.True(EmployeeRecordEdit.IsMaskedEcho(PiiMask.Phone("0812345678"), PiiMask.Phone("0812345678")));
        Assert.True(EmployeeRecordEdit.IsMaskedEcho(" " + PiiMask.Email("somchai@example.com") + " ",
            PiiMask.Email("somchai@example.com")));
        Assert.True(EmployeeRecordEdit.IsMaskedEcho(PiiMask.BankAccountNo("1234567890"),
            PiiMask.BankAccountNo("1234567890")));
        // ทิศตรงข้าม: พิมพ์ค่าใหม่จริง ต้องไม่ถูกมองเป็นค่าปิดบัง
        Assert.False(EmployeeRecordEdit.IsMaskedEcho("0899999999", PiiMask.Phone("0812345678")));
        Assert.False(EmployeeRecordEdit.IsMaskedEcho("0812345678", null));
    }

    [Fact]
    public void เลขเดิมแค่ต่างรูปแบบ_ไม่นับว่าแก้()
        => Assert.False(EmployeeRecordEdit.ThaiIdNumber("1-1017-00230-70-8", Citizen, "x").Changes);

    [Fact]
    public void เลขเก่าที่checksumไม่ผ่านแต่ไม่ได้แก้_ไม่บล็อกการแก้ช่องอื่น()
    {
        // เข้าระบบสมัยตรวจแค่ regex 13 หลัก / HRIS sync / นำเข้า CSV — ฟอร์มส่งค่าเดิมกลับมา
        // ทุกครั้งที่ HR แก้ช่องอื่น ⇒ ต้องไม่ถูกปฏิเสธ (มิฉะนั้นแก้ชื่อ/บัญชีไม่ได้เลย)
        var r = EmployeeRecordEdit.ThaiIdNumber("1101700230709", "1101700230709", "เลขบัตรประชาชน");
        Assert.Null(r.Error);
        Assert.False(r.Changes);
    }

    [Fact]
    public void สตริงว่างบนช่องที่ว่างอยู่แล้ว_ไม่แตะ()
        => Assert.False(EmployeeRecordEdit.ThaiIdNumber("", null, "x").Changes);

    [Fact]
    public void รหัสเท่าเดิม_ไม่ถูกด่านประวัติเงินเดือนกัน()
    {
        // ฟอร์มส่งรหัสเดิมทุกครั้ง (ช่องถูกล็อก) — ต้องไม่ถูกปฏิเสธเพราะมีประวัติเงินเดือน
        var r = EmployeeRecordEdit.EmployeeCode("EMP001", "EMP001", hasPayrollHistory: true);
        Assert.Null(r.Error);
        Assert.False(r.Changes);
    }
}
