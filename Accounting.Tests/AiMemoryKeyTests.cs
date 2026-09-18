using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **กุญแจของ "อินพุตเดียวกัน"** — ตัวที่ตัดสินว่าสิ่งที่ผู้ใช้เพิ่งสอน จะถูกหยิบมาใช้
/// ตอบคำถามเดิมได้หรือไม่
///
/// ═══ บั๊กที่ล็อกไว้ (รอบ 178) ═══
/// คลัง <c>AiSuggestionMemory</c> ถูก**เขียน**ด้วย SHA(รุ่นโมเดล + system prompt +
/// userJson) แต่ถูก**อ่าน**ด้วย fingerprint ของ userJson อย่างเดียว ⇒ แถวที่เส้น
/// orchestrator เขียนไว้ไม่มีใครอ่านกลับได้เลย = จ่าย token แล้วเรียนทิ้ง
///
/// ล็อกสองครึ่ง: ครึ่งที่พิสูจน์ว่า "อินพุตเดียวกันได้กุญแจเดียวกัน" (ไม่งั้นคลังโต
/// แต่ไม่มีวันถูกใช้) และครึ่งที่พิสูจน์ว่า **คนละคำถามต้องได้คนละกุญแจ**
/// (กุญแจที่ชนกันหมด = เสิร์ฟคำตอบของคำถามอื่น ซึ่งแย่กว่าไม่ตอบ)
/// </summary>
public class AiMemoryKeyTests
{
    // ══════════ ครึ่งแรก: อินพุตเดียวกัน = กุญแจเดียวกัน ══════════

    [Fact]
    public void ลำดับคีย์ใน_JSON_ต่างกัน_ต้องได้กุญแจเดียวกัน()
        => Assert.Equal(
            AiMemoryKey.Of("""{"vendor":"ร้านค้า","desc":"ค่าบริการ"}"""),
            AiMemoryKey.Of("""{"desc":"ค่าบริการ","vendor":"ร้านค้า"}"""));

    [Fact]
    public void ช่องว่างและตัวพิมพ์ต่างกัน_ต้องได้กุญแจเดียวกัน()
        => Assert.Equal(
            AiMemoryKey.Of("""{"desc":"Consulting Fee"}"""),
            AiMemoryKey.Of("""{ "desc" :  "consulting fee" }"""));

    [Fact]
    public void ยอดเงินและวันที่ต่างกัน_ยังเป็นคำถามเดียวกัน()
        // ใบของผู้ขายรายเดิม รายการเดิม แต่คนละงวด/คนละยอด ⇒ ต้องได้คำตอบที่เรียนไว้
        // ถ้าไม่ตัดค่าเหล่านี้ออก คลังจะมีแถวใหม่ทุกใบและไม่มีวันถูกใช้ซ้ำ
        => Assert.Equal(
            AiMemoryKey.Of("""{"vendor":"ร้านค้า","amount":1500.00,"date":"2026-01-31"}"""),
            AiMemoryKey.Of("""{"vendor":"ร้านค้า","amount":98765.25,"date":"2026-09-18"}"""));

    [Fact]
    public void เลขผู้เสียภาษีที่ถูกปิดบังกับค่าดิบ_ต้องชนกุญแจเดียวกัน()
        // แถว feedback ถูกบันทึกด้วย prompt ที่ sanitize แล้ว ส่วนตอนทำนายใช้ค่าดิบ
        // ถ้าไม่ทำให้ตรงกัน exact-memory จะไม่มีวันชนกันเลยสักครั้ง
        => Assert.Equal(
            AiMemoryKey.Of("""{"tax":"0105556123453"}"""),
            AiMemoryKey.Of("""{"tax":"0xxxxxxxxxx3"}"""));

    [Fact]
    public void ข้อความที่ไม่ใช่_JSON_ก็ยังทำกุญแจได้()
        => Assert.Equal(AiMemoryKey.Of("ค่าที่ปรึกษา"), AiMemoryKey.Of("ค่าที่ปรึกษา"));

    // ══════════ ครึ่งหลัง: คนละคำถาม = คนละกุญแจ ══════════

    [Fact]
    public void คำอธิบายคนละเรื่อง_ต้องได้คนละกุญแจ()
        => Assert.NotEqual(
            AiMemoryKey.Of("""{"desc":"ค่าที่ปรึกษากฎหมาย"}"""),
            AiMemoryKey.Of("""{"desc":"ซื้อกระดาษ A4"}"""));

    [Fact]
    public void คนละผู้ขาย_ต้องได้คนละกุญแจ()
        => Assert.NotEqual(
            AiMemoryKey.Of("""{"vendor":"บริษัท ก จำกัด","desc":"ค่าบริการ"}"""),
            AiMemoryKey.Of("""{"vendor":"บริษัท ข จำกัด","desc":"ค่าบริการ"}"""));

    // ══════════ "ไม่มีกุญแจ" ต้องบอกว่าไม่มี ห้ามแต่งขึ้น ══════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ไม่มีอินพุต_ต้องคืนค่าว่าง(string? input)
        => Assert.Equal("", AiMemoryKey.Of(input));

    [Fact]
    public void กุญแจต้องยาวไม่เกินคอลัมน์ที่เก็บ()
        // PromptHash เป็น varchar(80) — SHA-256 hex = 64 ตัว
        => Assert.InRange(AiMemoryKey.Of("""{"a":"b"}""").Length, 1, 80);
}
