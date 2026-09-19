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

    // ══════════ บล็อก local_model = "คำตอบที่เสนอ" ไม่ใช่ "คำถาม" (รอบ 181 · D7-2) ══════════
    //
    // บั๊กที่ล็อกไว้: `AiOrchestrator` เขียนทับบล็อก `local_model` ด้วยคำตอบของนักเรียน
    // **ก่อน** คิดกุญแจ (บรรทัด :177 → :310) ส่วนนักเรียนทำนายด้วย JSON **ก่อน** เขียนทับ
    // ⇒ กุญแจสองฝั่งต่างกันทันทีที่นักเรียนเริ่มตอบได้ = คลังที่เรียนไว้หาไม่เจอตลอดกาล
    // (นักเรียน generic หยุดโตหลังใบแรก · อาการเงียบสนิท ไม่มี error ไม่มี log)

    private const string PayloadHeuristic = """
        {"vendor":{"name":"ร้านวัสดุ ก","tax_id":"0105556123453"},
         "line":{"description":"ค่าซ่อมแอร์"},
         "local_model":{"pick":null,"confidence":0.0}}
        """;

    private const string PayloadStudentAnswered = """
        {"vendor":{"name":"ร้านวัสดุ ก","tax_id":"0105556123453"},
         "line":{"description":"ค่าซ่อมแอร์"},
         "local_model":{"pick":"5310","confidence":0.82,"source":"distilled-student"}}
        """;

    [Fact]
    public void บล็อก_local_model_ต่างกัน_ยังเป็นคำถามเดียวกัน()
        => Assert.Equal(AiMemoryKey.Of(PayloadHeuristic), AiMemoryKey.Of(PayloadStudentAnswered));

    [Fact]
    public void มีบล็อก_local_model_กับไม่มีเลย_ต้องได้กุญแจเดียวกัน()
        // prompt builder บางตัวไม่ใส่บล็อกนี้ (payload แบบ bulk) — คำถามเดียวกันต้องชนกัน
        => Assert.Equal(
            AiMemoryKey.Of("""{"line":{"description":"ค่าซ่อมแอร์"}}"""),
            AiMemoryKey.Of("""{"line":{"description":"ค่าซ่อมแอร์"},"local_model":{"pick":"5310"}}"""));

    [Fact]
    public void กุญแจฝั่งทำนายกับฝั่งเรียน_ต้องตรงกันตามลำดับจริงของ_orchestrator()
    {
        // ลำดับจริง: (1) นักเรียนทำนายด้วย UserPromptJson เดิม → (2) orchestrator เขียนทับ
        // บล็อก local_model ด้วยคำตอบนักเรียน → (3) คิด memoryKey จาก JSON ที่แก้แล้ว
        // เรียก **ฟังก์ชันจริง** ไม่ลอกตรรกะมาเขียนใหม่ (สำเนาที่สองจะ drift เงียบ ๆ)
        var predictKey = AiMemoryKey.Of(PayloadHeuristic);
        var rewritten = Accounting.Services.Ai.AiOrchestrator.ReplaceLocalModelBlock(
            PayloadHeuristic, "5310", 0.82m);
        var recordKey = AiMemoryKey.Of(rewritten);

        Assert.NotEqual(PayloadHeuristic, rewritten);      // เขียนทับจริง (ไม่งั้นเทสต์นี้ว่างเปล่า)
        Assert.Equal(predictKey, recordKey);
    }

    [Fact]
    public void ตัด_local_model_แล้ว_คนละคำถามยังต้องได้คนละกุญแจ()
        // ทิศตรงข้าม: การตัดบล็อกออกต้องไม่ทำให้ "อะไรก็ชนกันหมด" — ถ้าคำถามต่างกันจริง
        // (คนละรายการ) กุญแจต้องยังต่างกัน ไม่งั้นจะเสิร์ฟคำตอบของใบอื่นซึ่งแย่กว่าไม่ตอบ
        => Assert.NotEqual(
            AiMemoryKey.Of("""{"line":{"description":"ค่าซ่อมแอร์"},"local_model":{"pick":"5310"}}"""),
            AiMemoryKey.Of("""{"line":{"description":"ค่าเช่าสำนักงาน"},"local_model":{"pick":"5310"}}"""));

    [Fact]
    public void คีย์ที่ชื่อคล้าย_local_model_แต่เป็นอินพุตจริง_ห้ามถูกตัด()
        // ลิสต์ปิดโดยตั้งใจ: ถ้าใช้แพตเทิร์น `local*` / `*_model` คีย์อินพุตจริงจะหายไป
        // แล้วคำถามคนละคำถามจะได้กุญแจเดียวกัน (ญาติของ `__NEW__` ที่เป็นคำตอบจริง)
        => Assert.NotEqual(
            AiMemoryKey.Of("""{"local_branch":"สาขาลาดพร้าว","model_code":"เครื่องพิมพ์"}"""),
            AiMemoryKey.Of("""{"local_branch":"สาขาสีลม","model_code":"เครื่องพิมพ์"}"""));

    [Fact]
    public void กุญแจต้องยาวไม่เกินคอลัมน์ที่เก็บ()
        // PromptHash เป็น varchar(80) — SHA-256 hex = 64 ตัว
        => Assert.InRange(AiMemoryKey.Of("""{"a":"b"}""").Length, 1, 80);
}
