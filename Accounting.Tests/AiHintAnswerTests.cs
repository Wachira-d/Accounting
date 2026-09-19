using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **"ไม่มีใครตอบ" ต้องไม่ถูกแปลงเป็นคำแนะนำ** (ผลตรวจ D1-11 รอบ 181)
///
/// <para>สองครึ่งตามกฎเหล็ก #4 H: ครึ่งที่พิสูจน์ว่าคำตอบที่ <b>ไม่มี</b> เดินทางถึง
/// หน้าจอในสภาพว่าง + ไม่มีรหัส feedback ให้บันทึก (kill-switch: ปิด provider ทุกตัว
/// และนักเรียนยังไม่มีคลัง) และครึ่งที่พิสูจน์ว่าคำตอบที่ <b>มีจริง</b> — รวมถึง
/// <c>"Acknowledge"</c> ที่โมเดลตอบเอง — ยังผ่านไปครบเหมือนเดิม</para>
/// </summary>
public class AiHintAnswerTests
{
    private static readonly Guid Fid = Guid.NewGuid();

    // ══════════ ครึ่งแรก: ไม่มีคำตอบ ══════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ไม่มีคำตอบ_ต้องส่งออกเป็นค่าว่างและไม่มีรหัสให้บันทึก(string? answer)
    {
        Assert.Equal("", AiHintAnswer.ForTransport(answer));
        Assert.Equal(0m, AiHintAnswer.ConfidenceFor(answer, 0.50m));   // ห้ามมีความมั่นใจที่แต่งขึ้น
        Assert.Null(AiHintAnswer.FeedbackIdFor(answer, Fid));
    }

    [Fact]
    public void ปิดโมเดลทั้งหมด_ต้องไม่มีแถวไหนถูกบันทึกว่าผู้ใช้เห็นด้วยกับ_AI()
    {
        // จำลองกติกาของหน้าจอ: บันทึกเฉพาะข้อที่มีทั้ง feedbackId และ primary
        // (documents.html `_showApprovalWarnings` → ปุ่ม "ยอมรับและอนุมัติต่อ")
        var hints = new[] { (string?)null, "", "   " }
            .Select(a => (Primary: AiHintAnswer.ForTransport(a),
                          FeedbackId: AiHintAnswer.FeedbackIdFor(a, Fid)))
            .ToList();

        var recorded = hints.Count(h => h.FeedbackId != null && h.Primary.Length > 0);
        Assert.Equal(0, recorded);
    }

    // ══════════ ครึ่งหลัง: มีคำตอบจริง ══════════

    [Fact]
    public void โมเดลตอบว่า_Acknowledge_เอง_ยังเป็นคำตอบที่ใช้ได้ครบ()
    {
        Assert.Equal("Acknowledge", AiHintAnswer.ForTransport("Acknowledge"));
        Assert.Equal(0.82m, AiHintAnswer.ConfidenceFor("Acknowledge", 0.82m));
        Assert.Equal(Fid, AiHintAnswer.FeedbackIdFor("Acknowledge", Fid));
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Block")]
    public void คำตอบอื่นผ่านครบเหมือนเดิม(string answer)
    {
        Assert.Equal(answer, AiHintAnswer.ForTransport(answer));
        Assert.Equal(Fid, AiHintAnswer.FeedbackIdFor(answer, Fid));
    }

    [Fact]
    public void มีคำตอบแต่ไม่มีความมั่นใจ_ต้องเป็นศูนย์ไม่ใช่ค่าที่แต่งขึ้น()
        => Assert.Equal(0m, AiHintAnswer.ConfidenceFor("Edit", null));

    [Fact]
    public void ช่องว่างหัวท้ายถูกตัด_แต่คำตอบไม่ถูกเปลี่ยน()
        => Assert.Equal("Block", AiHintAnswer.ForTransport("  Block  "));
}
