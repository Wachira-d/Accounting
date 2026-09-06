using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝั่งเขียนกับฝั่งอ่านของตัวเรียนรู้ต้องใช้คีย์<b>ชุดเดียวกัน</b> — ไม่งั้นความรู้ที่
/// สะสมมาไม่เคยถูกอ่านเลย (defect class "ชื่อ key คนละชุดระหว่างฝั่งเขียนกับฝั่งอ่าน"
/// ที่เคยเกิดกับ FieldConfidence มาแล้ว · ผลตรวจ 2026-09-06 · T3-09)
/// </summary>
public class VendorLearningKeyTests
{
    [Fact]
    public void เลขภาษี13หลักชนะชื่อเสมอ()
    {
        Assert.Equal("tax:0105556123456",
            VendorLearningKey.For("0105556123456", "บจก. อะไรก็ได้"));
        // กระดาษพิมพ์มีขีด/เว้นวรรค — ต้องได้คีย์เดียวกัน
        Assert.Equal("tax:0105556123456",
            VendorLearningKey.For("0-1055-56123-45-6", "บจก. อะไรก็ได้"));
    }

    [Fact]
    public void เลขไม่ครบ13หลัก_ต้องถือว่าไม่มีเลข_ไม่ใช่มีเลขบางส่วน()
    {
        // ★ นี่คือบั๊กจริง: ฝั่ง**อ่าน**ของตัวเรียนรู้ข้ามผู้เช่าเคยสร้าง "tax:0105556"
        // ทันทีที่มีเลขอะไรก็ตาม ส่วนฝั่ง**เขียน**ตกไปใช้ "name:" ⇒ คีย์ไม่มีวันชนกัน
        // ⇒ ใบที่ OCR อ่านเลขไม่ครบ (ตัวเลขติดตราประทับ/เส้นตาราง) ไม่เคยได้ประโยชน์
        // จากความรู้ที่ผู้เช่าคนอื่นสะสมไว้เลย
        Assert.Equal("name:บจก. ก", VendorLearningKey.For("0105556", "บจก. ก"));
        Assert.Equal("name:บจก. ก", VendorLearningKey.For("01055561234567", "บจก. ก"));
        Assert.Equal("name:บจก. ก", VendorLearningKey.For(null, "บจก. ก"));
        Assert.Equal("name:บจก. ก", VendorLearningKey.For("ไม่ใช่ตัวเลข", "บจก. ก"));
    }

    [Fact]
    public void ชื่อถูกตัดช่องว่างและแปลงเป็นตัวพิมพ์เล็ก()
    {
        Assert.Equal("name:makro", VendorLearningKey.For(null, "  MAKRO  "));
        Assert.Equal(VendorLearningKey.For(null, "Makro"), VendorLearningKey.For(null, "makro"));
    }

    [Fact]
    public void ไม่มีทั้งเลขและชื่อ_ต้องคืนค่าว่าง_เพื่อให้ผู้เรียกข้ามไป()
    {
        Assert.Equal("", VendorLearningKey.For(null, null));
        Assert.Equal("", VendorLearningKey.For("123", "   "));
        Assert.Equal("", VendorLearningKey.ForName(null));
    }

    [Fact]
    public void คีย์จากชื่ออย่างเดียว_ต้องตรงกับเส้นปกติที่ไม่มีเลขภาษี()
    {
        // ตัวป้อน seed ใช้ ForName — ต้องได้คีย์เดียวกับที่เส้นสแกนสร้างเมื่อไม่มีเลข
        Assert.Equal(VendorLearningKey.For(null, "บจก. ข"), VendorLearningKey.ForName("บจก. ข"));
    }
}
