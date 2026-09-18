using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"พิสูจน์ได้ไหมว่าผู้รับเป็นบุคคลธรรมดา"** — predicate สำหรับ<b>ตั้งด่าน</b>
/// ซึ่งต้องการหลักฐานเชิงบวก ต่างจาก <c>Detect</c> ที่ต้องเลือกข้างเสมอ
///
/// ═══ ที่มา (ทีมตรวจรอบ 180) ═══ `WhtPayeeKind.Detect` คืน `false` (= บุคคลธรรมดา)
/// ให้ทุก contact ที่ `ContactType` ยังเป็นค่า default — และ default คือ `Individual`
/// ⇒ คู่ค้าที่คีย์ชื่อมาเปล่า ๆ = "บุคคลธรรมดาที่พิสูจน์แล้ว" ⇒ เอามาลดเกณฑ์ด่าน
/// ตรง ๆ = เตือนถี่ขึ้นทั้งฐาน = กลับไปเป็นบั๊กที่ผู้ใช้รายงานมาตั้งแต่ต้น
/// </summary>
public class WhtProvenIndividualTests
{
    // ══════════ ครึ่งแรก: มีหลักฐานเชิงบวก ⇒ จริง ══════════

    [Theory]
    [InlineData("1103700123458")]   // เลขบัตรประชาชนขึ้นต้น 1
    [InlineData("3101200123453")]   // ขึ้นต้น 3
    public void เลขบัตรประชาชน_13_หลัก_คือหลักฐาน(string nid)
        => Assert.True(WhtPayeeKind.IsProvenIndividual(nid, "สมชาย ใจดี", null));

    [Theory]
    [InlineData("นาย")]
    [InlineData("นาง")]
    [InlineData("นางสาว")]
    [InlineData("น.ส.")]
    public void คำนำหน้าชื่อที่มนุษย์กรอกเอง_คือการประกาศ(string title)
        => Assert.True(WhtPayeeKind.IsProvenIndividual(null, "สมชาย ใจดี", title));

    // ══════════ ครึ่งหลัง: ไม่มีหลักฐาน ⇒ เท็จ (ห้ามเดา) ══════════

    [Fact]
    public void คู่ค้าที่คีย์ชื่อมาเปล่าๆ_ต้องไม่นับว่าเป็นบุคคลธรรมดา()
        // นี่คือเคสที่ทำให้ Detect ใช้แทนกันไม่ได้
        => Assert.False(WhtPayeeKind.IsProvenIndividual(null, "ABC Trading", null));

    [Fact]
    public void ไม่มีอะไรเลย_ต้องเป็นเท็จ()
        => Assert.False(WhtPayeeKind.IsProvenIndividual(null, null, null));

    [Fact]
    public void เลข_13_หลักที่_checksum_ไม่ผ่าน_ไม่ใช่หลักฐาน()
        // OCR อ่านเพี้ยนหนึ่งหลักไม่ควรกลายเป็น "พิสูจน์แล้ว"
        => Assert.False(WhtPayeeKind.IsProvenIndividual("1103700123450", "สมชาย ใจดี", null));

    [Fact]
    public void เลขนิติบุคคล_ต้องเป็นเท็จ()
        => Assert.False(WhtPayeeKind.IsProvenIndividual("0105556123453", "บริษัท ก จำกัด", null));

    [Theory]
    [InlineData("บริษัท สมชาย จำกัด")]
    [InlineData("หจก. สมชายการช่าง")]
    [InlineData("Somchai Co.,Ltd.")]
    public void ชื่อที่บ่งชี้นิติบุคคล_ชนะแม้จะมีคำนำหน้าชื่อบุคคล(string name)
        // หลักฐานค้านตรง ๆ ต้องชนะ — ไม่งั้นการกรอก TitleTh ผิดหนึ่งครั้งพาไปผิดทั้งใบ
        => Assert.False(WhtPayeeKind.IsProvenIndividual(null, name, "นาย"));

    [Fact]
    public void เลขบัตรประชาชนแต่ชื่อเป็นนิติบุคคล_ต้องไม่นับ()
        // ข้อมูลขัดกันเอง = ยังพิสูจน์ไม่ได้ ⇒ ใช้กติกาเดิม
        => Assert.False(WhtPayeeKind.IsProvenIndividual("1103700123458", "บริษัท ก จำกัด", null));

    // ══════════ ต้องไม่ไปทำให้ Detect/ResolveForm เปลี่ยนพฤติกรรม ══════════

    [Fact]
    public void ตัวเดิมยังต้องเลือกข้างเสมอเหมือนเดิม()
        // ResolveForm ต้องไม่คืน "ไม่รู้" มิฉะนั้นผู้รับหายจากทั้ง ภ.ง.ด.3 และ 53
        => Assert.Equal(TaxType.WithholdingTax3,
            WhtPayeeKind.ResolveForm(false, null, null, ContactType.Individual, "ABC Trading"));
}
