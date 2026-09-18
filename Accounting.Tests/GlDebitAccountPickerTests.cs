using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **บัญชีเดบิตของใบฝั่งซื้อ ต้องไม่ใช่บัญชีหนี้สิน** (ผู้ใช้รายงาน 2026-09-18)
///
/// เคสจริง: ใบซื้ออุปกรณ์แคมป์ปิ้งถูกเสนอบัญชีเดบิต <c>21513 "ค่าโทรศัพท์ค้างจ่าย"</c>
/// ซึ่งเป็น <b>หนี้สิน</b> — เกิดจากขั้นค้นด้วยคำในชื่อบัญชีที่ <c>OrderBy(AccountCode)</c>
/// โดยไม่กรองประเภท ⇒ รหัสขึ้นต้น "2" (หนี้สิน) ชนะ "5" (ค่าใช้จ่าย) <b>เสมอ</b>
/// = ผิดโดยโครงสร้าง ไม่ใช่โดยบังเอิญ
/// </summary>
public class GlDebitAccountPickerTests
{
    private static GlAccountCandidate Acc(string code, string name, AccountType t) => new(code, name, t);

    // เคสจริงจากผังบัญชีมาตรฐานของเรพนี้ (ChartOfAccountTemplates)
    private static readonly GlAccountCandidate AccruedPhone = Acc("21513", "ค่าโทรศัพท์ค้างจ่าย", AccountType.Liability);
    private static readonly GlAccountCandidate PhoneExpense = Acc("5304", "ค่าโทรศัพท์และอินเทอร์เน็ต", AccountType.Expense);

    [Fact]
    public void บัญชีค้างจ่ายห้ามถูกเลือกเป็นเดบิตของค่าใช้จ่าย()
    {
        // ลำดับในลิสต์จงใจให้หนี้สินมาก่อน เหมือนที่ OrderBy(AccountCode) เคยให้
        var pick = GlDebitAccountPicker.Pick(new[] { AccruedPhone, PhoneExpense }, seededCode: "5304");
        Assert.Equal("5304", pick?.Code);
    }

    [Fact]
    public void มีแต่บัญชีหนี้สิน_ต้องคืน_null_ไม่ใช่เลือกตัวที่ผิด()
        // ปล่อยว่างให้คนเลือก ดีกว่าเติมบัญชีที่ทำให้งบเพี้ยน
        => Assert.Null(GlDebitAccountPicker.Pick(new[] { AccruedPhone }, "5304"));

    [Theory]
    [InlineData(AccountType.Liability)]
    [InlineData(AccountType.Equity)]
    [InlineData(AccountType.Revenue)]
    public void ประเภทที่เป็นเดบิตค่าใช้จ่ายไม่ได้_ต้องถูกตัดทิ้ง(AccountType t)
        => Assert.Null(GlDebitAccountPicker.Pick(new[] { Acc("9001", "บัญชีทดสอบ", t) }, "5304"));

    [Theory]
    [InlineData(AccountType.Expense)]
    [InlineData(AccountType.Asset)]
    public void ค่าใช้จ่ายและสินทรัพย์ใช้เป็นเดบิตได้(AccountType t)
        // สินทรัพย์ต้องผ่าน — ซื้อของเข้าสต๊อก (1200) และสินทรัพย์ถาวร (12xxx)
        // เดบิตเข้าบัญชีสินทรัพย์เป็นเรื่องปกติ
        => Assert.Equal("9001",
            GlDebitAccountPicker.Pick(new[] { Acc("9001", "บัญชีทดสอบ", t) }, "5304")?.Code);

    [Fact]
    public void ค่าใช้จ่ายมาก่อนสินทรัพย์เมื่อเข้าข่ายทั้งคู่()
        => Assert.Equal("5304", GlDebitAccountPicker.Pick(new[]
        {
            Acc("1200", "สินค้าคงเหลือ", AccountType.Asset),
            PhoneExpense,
        }, seededCode: "5304")?.Code);

    [Fact]
    public void เลือกตัวที่ใกล้รหัสที่เสนอที่สุด_ไม่ใช่ตัวที่รหัสน้อยสุด()
    {
        // เดิมเรียงรหัสน้อยไปมากแล้วเอาตัวแรก ⇒ 51100 ชนะทั้งที่ชั้นก่อนหน้าเสนอ 5430
        var pick = GlDebitAccountPicker.Pick(new[]
        {
            Acc("51100", "ต้นทุนขาย", AccountType.Expense),
            Acc("54310", "ค่าไฟฟ้า", AccountType.Expense),
        }, seededCode: "5430");
        Assert.Equal("54310", pick?.Code);
    }

    [Fact]
    public void ไม่รู้รหัสที่เสนอ_ยังต้องเลือกได้และผลคงที่()
    {
        var set = new[]
        {
            Acc("54350", "ค่าสาธารณูปโภคอื่นๆ", AccountType.Expense),
            Acc("543", "ค่าสาธารณูปโภค", AccountType.Expense),
        };
        // รหัสสั้นกว่า = กลุ่มกว้างกว่า → ปลอดภัยกว่าเมื่อไม่มีอะไรชี้ชัด
        Assert.Equal("543", GlDebitAccountPicker.Pick(set, seededCode: null)?.Code);
        // สลับลำดับ input แล้วต้องได้ผลเดิม (ไม่ขึ้นกับลำดับแถวจากฐาน)
        Assert.Equal("543", GlDebitAccountPicker.Pick(set.Reverse().ToArray(), null)?.Code);
    }

    [Fact]
    public void ไม่มีผู้สมัครเลย_คืน_null()
    {
        Assert.Null(GlDebitAccountPicker.Pick(null, "5304"));
        Assert.Null(GlDebitAccountPicker.Pick(System.Array.Empty<GlAccountCandidate>(), "5304"));
    }

    [Fact]
    public void รหัสว่าง_ต้องไม่ถูกเลือก()
        => Assert.Null(GlDebitAccountPicker.Pick(new[] { Acc("  ", "ไม่มีรหัส", AccountType.Expense) }, "5304"));
}
