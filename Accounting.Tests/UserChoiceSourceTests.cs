using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"คำยืนยันนี้ตั้งใจแค่ไหน"** — ตัวแยกที่กันคลังคำตอบเอียงตามนิสัยการกด
///
/// ═══ ที่มา (ทีม T3 รอบ 177 §3.1) ═══
/// ตัวปิดลูปยิงตอนอนุมัติเอกสาร **ทุกบรรทัด** ที่มี feedbackId โดยไม่รู้ว่าผู้ใช้
/// มองค่านั้นหรือไม่ ⇒ ผู้ใช้ที่กด "ยอมรับและอนุมัติต่อ" เป็นนิสัยกลายเป็น
/// "ผู้ยืนยัน" วันละหลายสิบครั้ง ⇒ นักเรียนเรียนนิสัยการกด ไม่ใช่ความถูกต้อง
///
/// เทสต์นี้ล็อก**สัญญา**ของ enum: ค่าตั้งต้นต้องเป็นตัวที่อ่อนที่สุด
/// (เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน")
/// </summary>
public class UserChoiceSourceTests
{
    [Fact]
    public void ค่าตั้งต้นของ_enum_ต้องเป็น_Implicit()
        // `default(UserChoiceSource)` คือสิ่งที่ได้เมื่อไม่มีใครระบุ — ต้องเป็นตัวอ่อนสุด
        => Assert.Equal(UserChoiceSource.Implicit, default(UserChoiceSource));

    [Fact]
    public void Implicit_ต้องมีค่าเป็นศูนย์()
        // คอลัมน์ในฐานเป็น integer NULL — แถวเก่าอ่านเป็น null แล้วตีความเป็น Implicit
        => Assert.Equal(0, (int)UserChoiceSource.Implicit);

    [Fact]
    public void สามค่าต้องแยกจากกันจริง()
    {
        Assert.NotEqual(UserChoiceSource.Implicit, UserChoiceSource.Explicit);
        Assert.NotEqual(UserChoiceSource.Explicit, UserChoiceSource.BulkApprove);
        Assert.NotEqual(UserChoiceSource.Implicit, UserChoiceSource.BulkApprove);
    }

    [Fact]
    public void มีเพียง_Explicit_ตัวเดียวที่นับเป็นคำยืนยันที่ตั้งใจ()
    {
        // กติกาที่ฝั่งเขียนใช้ (`source == Explicit`) — ล็อกไว้กันคนเผลอเพิ่ม
        // ค่าใหม่แล้วนับรวมเข้าไปโดยไม่ได้ตั้งใจ
        var deliberate = Enum.GetValues<UserChoiceSource>()
            .Where(v => v == UserChoiceSource.Explicit).ToList();

        Assert.Single(deliberate);
        Assert.Equal(3, Enum.GetValues<UserChoiceSource>().Length);
    }
}
