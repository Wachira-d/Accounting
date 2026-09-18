using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ระบบเงียบเรื่องหัก ณ ที่จ่าย" ต้องทิ้งร่องรอยได้ครั้งเดียวต่อคำตอบ**
///
/// ═══ บั๊กที่ล็อกไว้ (ฝ่ายค้านรอบ 177 ข้อ B3) ═══
/// `CollectApprovalWarningsAsync` ถูกเรียก**ทุกครั้งที่กดอนุมัติ** — รอบแรกจบด้วย 422
/// ให้ผู้ใช้ยืนยัน แล้วรอบสองรันซ้ำทั้งชุด ⇒ บรรทัด <c>[WHT-ADVICE]</c> เดิมถูกต่อท้าย
/// <c>InternalNotes</c> ซ้ำทุกรอบ ทั้งที่เป็นเหตุการณ์เดียวกัน (และช่องนี้มีด่านอื่น
/// อ่านธงจากมันอยู่ — <c>tools/flag_field_overwrite_check.py</c>)
///
/// ล็อกสองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่กันการทบซ้ำ และครึ่งที่พิสูจน์ว่า
/// **คำตอบที่เปลี่ยนไปยังต้องได้บรรทัดใหม่** (ด่านที่เงียบทุกกรณี = ไม่มีด่าน)
/// </summary>
public class WhtAdviceNoteTests
{
    // ══════════ ครึ่งแรก: กดอนุมัติซ้ำต้องไม่ทบบรรทัดเดิม ══════════

    [Fact]
    public void ShouldAppend_เป็นเท็จ_เมื่อบรรทัดเดียวกันมีอยู่แล้ว()
    {
        var note = WhtAdviceNote.Compose(usedAi: true, answer: "ค่าสินค้า", confidence: 0.92m);
        var notes = "— รับทราบคำเตือนตอนอนุมัติ —\n\n" + note;

        Assert.False(WhtAdviceNote.ShouldAppend(notes, note));
    }

    [Fact]
    public void ShouldAppend_เป็นเท็จ_แม้บันทึกเดิมมีช่องว่างท้ายบรรทัดต่างกัน()
    {
        var note = WhtAdviceNote.Compose(usedAi: false, answer: "ค่าสินค้า", confidence: 0.80m);

        Assert.False(WhtAdviceNote.ShouldAppend(note + "\n\n", note + "   "));
    }

    [Fact]
    public void Compose_ไม่มีเวลาในข้อความ_จึงเทียบซ้ำได้()
    {
        var a = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.9m);
        var b = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.9m);

        Assert.Equal(a, b);   // เรียกคนละเวลาแต่ต้องได้ข้อความเดียวกันเป๊ะ
    }

    // ══════════ ครึ่งหลัง: เหตุการณ์ใหม่ต้องยังได้บรรทัดใหม่ ══════════

    [Fact]
    public void ShouldAppend_เป็นจริง_เมื่อยังไม่เคยบันทึก()
    {
        var note = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.9m);

        Assert.True(WhtAdviceNote.ShouldAppend(null, note));
        Assert.True(WhtAdviceNote.ShouldAppend("   ", note));
        Assert.True(WhtAdviceNote.ShouldAppend("หมายเหตุอื่นที่ไม่เกี่ยวกัน", note));
    }

    [Fact]
    public void ShouldAppend_เป็นจริง_เมื่อคำตอบของโมเดลเปลี่ยน()
    {
        var เดิม = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.90m);
        var ใหม่ = WhtAdviceNote.Compose(true, "ค่าขนส่งสาธารณะ", 0.90m);

        Assert.True(WhtAdviceNote.ShouldAppend(เดิม, ใหม่));
    }

    [Fact]
    public void ShouldAppend_เป็นจริง_เมื่อความมั่นใจเปลี่ยน()
    {
        var เดิม = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.90m);
        var ใหม่ = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.72m);

        Assert.True(WhtAdviceNote.ShouldAppend(เดิม, ใหม่));
    }

    [Fact]
    public void ShouldAppend_เป็นจริง_เมื่อเปลี่ยนจากโมเดลในระบบเป็น_AI()
    {
        var นักเรียน = WhtAdviceNote.Compose(usedAi: false, answer: "ค่าสินค้า", confidence: 0.90m);
        var ครู = WhtAdviceNote.Compose(usedAi: true, answer: "ค่าสินค้า", confidence: 0.90m);

        Assert.NotEqual(นักเรียน, ครู);
        Assert.True(WhtAdviceNote.ShouldAppend(นักเรียน, ครู));
    }

    // ══════════ เนื้อความต้องตอบได้ว่า "ใครตอบ ตอบว่าอะไร มั่นใจแค่ไหน" ══════════

    [Fact]
    public void Compose_บอกผู้ตอบและคำตอบและความมั่นใจ()
    {
        var ai = WhtAdviceNote.Compose(true, "ค่าสินค้า", 0.86m);
        Assert.Contains("[WHT-ADVICE]", ai);
        Assert.Contains("AI", ai);
        Assert.Contains("ค่าสินค้า", ai);
        Assert.Contains("86", ai);

        var local = WhtAdviceNote.Compose(false, "ค่าสินค้า", 0.86m);
        Assert.Contains("โมเดลในระบบ", local);
    }

    [Fact]
    public void Compose_ไม่ทิ้งวงเล็บว่างเมื่อโมเดลไม่ระบุประเภท()
    {
        var note = WhtAdviceNote.Compose(true, null, null);

        Assert.DoesNotContain("()", note);
        Assert.Contains("ไม่ระบุประเภทเงินได้", note);
    }

    [Fact]
    public void ShouldAppend_เป็นเท็จ_เมื่อข้อความว่าง()
        => Assert.False(WhtAdviceNote.ShouldAppend("อะไรก็ได้", "   "));

    [Fact]
    public void รหัสกฎและมาตราต้องไม่ว่าง()
    {
        Assert.False(string.IsNullOrWhiteSpace(WhtAdviceNote.SilentRuleCode));
        Assert.Contains("54", WhtAdviceNote.SilentLegalReference);
    }
}
