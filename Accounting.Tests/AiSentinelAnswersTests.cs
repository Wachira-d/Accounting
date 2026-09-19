using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ธงของ UI ≠ คำตอบที่เรียนได้** (รอบ 181 · D7-1)
///
/// ═══ บั๊กที่ล็อกไว้ ═══
/// ปุ่ม "ใช้ของเดิม" ยิง <c>chosenAnswer = "__USER_KEPT_EXISTING__"</c> ไปที่
/// <c>/ai-feedback/record</c> แต่ฝั่งเซิร์ฟเวอร์ไม่รู้จักค่านี้เลย ⇒ มันไหลเข้า
/// <c>AiSuggestionMemory.LearnedAnswer</c> แล้วต่อไปเป็น
/// <c>OcrCategoryMapping.AccountCode = "__USER_KEPT_EXISTING__"</c> =
/// <b>รหัสผังบัญชีปลอมที่ระบบนับว่า "ผู้ใช้ยืนยันแล้ว"</b> แล้วเสนอให้คนถัดไปลงบัญชีด้วย
///
/// ═══ ครึ่งที่สำคัญกว่า: ทิศตรงข้าม ═══
/// ด่านนี้พลาดไปทางเข้มเกินได้ง่ายมาก — <c>__NEW__</c> ("ไม่มีตัวไหนตรง ให้สร้างใหม่")
/// เป็น <b>คำตอบจริง</b> ของ VendorCanonicalization/BankStatementMatch ถ้าเผลอกันมันด้วย
/// (เช่นใช้แพตเทิร์น <c>^__[A-Z_]+__$</c>) การเรียนรู้ของสองฟีเจอร์นั้นจะตายเงียบทั้งเส้น
/// — "ด่านที่ฟ้องผิด = ด่านที่พัง"
/// </summary>
public class AiSentinelAnswersTests
{
    // ══════════ ครึ่งแรก: ธงต้องถูกจับได้ ══════════

    [Fact]
    public void ธง_ใช้ของเดิม_ต้องถูกนับเป็น_sentinel()
        => Assert.True(AiSentinelAnswers.IsSentinel("__USER_KEPT_EXISTING__"));

    [Fact]
    public void ธงที่มีช่องว่างหัวท้าย_ยังเป็น_sentinel()
        => Assert.True(AiSentinelAnswers.IsSentinel("  __USER_KEPT_EXISTING__  "));

    [Fact]
    public void ค่าคงที่ที่ประกาศไว้_ต้องตรงกับสตริงที่หน้าเว็บส่งมาจริง()
        // ถ้ามีใครแก้ค่าคงที่นี้โดยไม่แก้ `wwwroot/js/ai-suggestion.js` ด่านจะเปิดเงียบ ๆ
        => Assert.Equal("__USER_KEPT_EXISTING__", AiSentinelAnswers.UserKeptExisting);

    // ══════════ ครึ่งหลัง (ทิศตรงข้าม): คำตอบจริงต้องไม่ถูกกัน ══════════

    [Theory]
    // คำตอบจริงของ VendorCanonicalization / BankStatementMatch — **ห้าม**เป็น sentinel
    [InlineData("__NEW__")]
    // รหัสผังบัญชี · GUID · ชื่อ enum · ข้อความไทย · คำที่บังเอิญมีขีดล่าง
    [InlineData("5310")]
    [InlineData("11640")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("PurchaseInvoice")]
    [InlineData("ค่าซ่อมแอร์")]
    [InlineData("ACC__2000")]
    [InlineData("user_kept_existing")]
    [InlineData("__USER_KEPT_EXISTING")]
    [InlineData("USER_KEPT_EXISTING__")]
    public void คำตอบปกติ_ต้องเรียนได้ตามเดิม(string answer)
        => Assert.False(AiSentinelAnswers.IsSentinel(answer));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ค่าว่าง_ไม่ใช่_sentinel(string? answer)
        // ค่าว่างมีด่านของตัวเองอยู่แล้ว (LearnInlineAsync ตัดทิ้งก่อน) — ตัวนี้ต้องตอบ
        // "ไม่ใช่ธง" ไม่ใช่ "ใช่ธง" เพื่อไม่ให้สองด่านทับหน้าที่กันจนอ่านเจตนาไม่ออก
        => Assert.False(AiSentinelAnswers.IsSentinel(answer));
}
