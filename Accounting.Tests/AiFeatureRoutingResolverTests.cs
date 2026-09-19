using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **พื้นขั้นต่ำของอัตราสุ่มถามครู — ต้องบังคับทั้งฝั่งเขียนและฝั่งอ่าน** (รอบ 181 · D7-4)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// รอบ 178 ใส่พื้น <c>MinSamplingRate</c> ไว้ใน <c>SetAsync</c> อย่างเดียว ⇒ แถวที่แอดมิน
/// เคยตั้ง <c>ProviderSamplingRate = 0</c> ไว้**ก่อน**รอบนั้นยังเป็น 0 อยู่ในฐาน และฝั่งอ่าน
/// (<c>EnsureCacheFreshAsync</c>) ส่งต่อให้ orchestrator ตรง ๆ ⇒ <b>ครูถูกปิดถาวรโดยไม่มี
/// ใครรู้</b>: นักเรียนไม่ได้ตัวอย่างใหม่อีกเลย และตัววัด "นักเรียนแม่นแค่ไหน" ก็ไม่มีวัน
/// เปลี่ยน = หน้า ai-health รายงาน "สุขภาพดี" ตลอดกาลทั้งที่กำลังแย่ลง
///
/// ═══ ครึ่งทิศตรงข้าม ═══
/// <see cref="AiFeatureRoutingMode.LocalOnly"/> คือ**เจตนาปิดครูที่มองเห็นได้** — 0 ของ
/// โหมดนั้นต้องอยู่เป็น 0 ห้ามยกพื้นให้ ไม่งั้นการแก้บั๊กนี้จะกลายเป็น "ยิง provider ทั้งที่
/// แอดมินสั่งปิด" = kill-switch ระดับ feature ใช้ไม่ได้จริง (กฎเหล็ก #1 ข้อ 5)
///
/// <para>เทสต์เรียก <see cref="AiFeatureRoutingResolver.ClampSamplingRate"/> ซึ่งเป็น
/// <b>ฟังก์ชันเดียวกับที่ทั้งสองเส้นเรียกจริง</b> (SetAsync + EnsureCacheFreshAsync) —
/// ไม่ได้ลอกสูตรมาเขียนใหม่ในเทสต์ ซึ่งจะกลายเป็นด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ</para>
/// </summary>
public class AiFeatureRoutingResolverTests
{
    // ══════════ ครึ่งแรก: ศูนย์ที่ไม่ได้ตั้งใจ ต้องถูกยกขึ้นพื้น ══════════

    [Theory]
    [InlineData(AiFeatureRoutingMode.Hybrid)]
    [InlineData(AiFeatureRoutingMode.AlwaysTeach)]
    [InlineData(AiFeatureRoutingMode.ProviderOnly)]
    public void แถวที่เก็บศูนย์ไว้_โหมดที่ยังใช้ครู_ต้องได้พื้นขั้นต่ำ(AiFeatureRoutingMode mode)
        => Assert.Equal(AiFeatureRoutingResolver.MinSamplingRate,
            AiFeatureRoutingResolver.ClampSamplingRate(mode, 0m));

    [Fact]
    public void ค่าที่ต่ำกว่าพื้นแต่ไม่ใช่ศูนย์_ก็ต้องถูกยกขึ้นพื้น()
        => Assert.Equal(AiFeatureRoutingResolver.MinSamplingRate,
            AiFeatureRoutingResolver.ClampSamplingRate(AiFeatureRoutingMode.Hybrid, 0.001m));

    // ══════════ ครึ่งหลัง (ทิศตรงข้าม): เจตนาที่ประกาศชัด ห้ามถูกแก้ ══════════

    [Fact]
    public void โหมด_LocalOnly_ตั้งศูนย์ไว้_ต้องยังเป็นศูนย์()
        => Assert.Equal(0m,
            AiFeatureRoutingResolver.ClampSamplingRate(AiFeatureRoutingMode.LocalOnly, 0m));

    [Fact]
    public void ค่าปกติที่สูงกว่าพื้น_ต้องไม่ถูกแตะ()
        => Assert.Equal(0.25m,
            AiFeatureRoutingResolver.ClampSamplingRate(AiFeatureRoutingMode.Hybrid, 0.25m));

    [Fact]
    public void ค่าเกินหนึ่ง_ต้องถูกตัดลงเหลือหนึ่ง()
        // แถวเก่า/ค่าที่เขียนตรงเข้า DB อาจเกิน 1 — สุ่มเกิน 100% ไม่มีความหมาย
        => Assert.Equal(1m,
            AiFeatureRoutingResolver.ClampSamplingRate(AiFeatureRoutingMode.Hybrid, 1.5m));

    [Fact]
    public void พื้นขั้นต่ำต้องไม่เป็นศูนย์()
        // ถ้าใครลดค่าคงที่นี้เหลือ 0 ด่านทั้งอันจะกลายเป็น no-op โดยที่เทสต์อื่นยังเขียวหมด
        => Assert.True(AiFeatureRoutingResolver.MinSamplingRate > 0m);
}
