using Accounting.Services.Ai;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่าน PDPA ก่อน prompt ออกไปหา provider (E-AI-06)
///
/// ═══ บั๊กจริงที่เทสต์ชุดนี้ล็อกไว้ ═══
/// <list type="number">
///   <item>doc ของคลาสอ้างว่าปิดบัง "อีเมล" มาตลอด แต่ <b>ไม่มี regex อีเมลเลย</b></item>
///   <item>สตริงที่เป็น<b>สมาชิกของ array</b> ไม่เคยถูกปิดบังเลยสักตัว — ตัววน
///         เรียกตัวเองต่อเฉพาะเมื่อสมาชิกเป็น object/array ⇒ คำอธิบายรายการจาก
///         OCR (<c>top_line_items</c>) ออกไปดิบทั้งหมด</item>
///   <item>เลขบัญชี 10 หลักที่ไม่ขึ้นต้น 0 ไม่เข้า <c>PhoneRegex</c> จึงหลุด</item>
///   <item><c>AllowTaxIdInPrompt=true</c> ปล่อย <b>เลขบัตรประชาชน</b> ของผู้ขาย
///         บุคคลธรรมดาออกไปด้วย ทั้งที่สิ่งที่ feature ต้องเทียบคือเลขนิติบุคคล</item>
/// </list>
///
/// หลักที่ต้องรักษาไว้: หน้ากากต้อง <b>deterministic</b> — ไม่งั้น feature ที่
/// "เทียบสองฝั่ง" (เบอร์ใน memo PromptPay · เลขบัญชีบนสเตทเมนต์) พังทันที
/// </summary>
public class AiPromptSanitizerTests
{
    private readonly AiPromptSanitizer _s = new();

    private string San(string json, bool keepTaxIds = false)
        => _s.Sanitize(json, stripPii: true, keepTaxIds: keepTaxIds);

    // ── อีเมล ──

    [Fact]
    public void อีเมลถูกปิดบัง_แต่ยังเทียบกันได้()
    {
        var outp = San("""{"contact":"somchai.j@example.co.th","memo":"โอนจาก somchai.j@example.co.th"}""");
        Assert.DoesNotContain("somchai.j", outp);
        Assert.DoesNotContain("example", outp);
        Assert.Contains("sxxx@xxx.th", outp);        // อักษรแรก + TLD พอให้เทียบ
        // ค่าเดียวกันสองที่ ⇒ หน้ากากเดียวกัน (ไม่งั้นโมเดลจับคู่ไม่ได้)
        Assert.Equal(2, outp.Split("sxxx@xxx.th").Length - 1);
    }

    [Fact]
    public void อีเมลถูกปิดบังแม้ตอน_keepTaxIds()
        => Assert.DoesNotContain("boss@acme.com", San("""{"x":"boss@acme.com"}""", keepTaxIds: true));

    // ── สตริงใน array (บั๊กที่เงียบที่สุด) ──

    [Fact]
    public void สตริงที่เป็นสมาชิกของ_array_ต้องถูกปิดบังด้วย()
    {
        // ★ เดิมออกไปดิบทั้งก้อน — ตัววนข้ามสมาชิกที่เป็นสตริง
        var outp = San("""{"top_line_items":["ค่าบริการ โทร 0812345678","ID 1234567890123"]}""");
        Assert.DoesNotContain("0812345678", outp);
        Assert.DoesNotContain("1234567890123", outp);
    }

    [Fact]
    public void สตริงใน_array_ซ้อน_object_ก็ต้องถูกปิดบัง()
        => Assert.DoesNotContain("0812345678",
            San("""{"rows":[{"tags":["โทร 0812345678"]}]}"""));

    // ── เลขบัญชีธนาคาร ──

    [Fact]
    public void ช่องที่ชื่อเป็นเลขบัญชี_ถูกปิดบังโดยไม่ต้องมีป้าย()
    {
        // ★ 1234567890 ไม่ขึ้นต้น 0 จึงไม่เข้า PhoneRegex ⇒ เดิมหลุดดิบ
        var outp = San("""{"contact_bank_acct":"1234567890","bank_account":"019-2-34567-8"}""");
        Assert.DoesNotContain("1234567890", outp);
        Assert.Contains("12xxxxxx90", outp);
        Assert.DoesNotContain("019234567", outp);
    }

    [Fact]
    public void bank_account_patterns_ที่เป็น_array_ก็ถูกปิดบัง()
        => Assert.DoesNotContain("1234567890", San("""{"bank_account_patterns":["1234567890"]}"""));

    [Fact]
    public void เลขบัญชีที่มีป้ายกำกับในข้อความอิสระ_ถูกปิดบัง_และคงป้ายไว้()
    {
        var outp = San("""{"memo":"โอนเข้าเลขที่บัญชี 123-4-56789-0 ยอด 5,000"}""");
        Assert.Contains("เลขที่บัญชี", outp);          // ป้ายต้องอยู่ โมเดลจะได้รู้ว่าคืออะไร
        Assert.DoesNotContain("123456789", outp);
        Assert.Contains("12xxxxxx90", outp);
    }

    [Fact]
    public void เลขลอย_10_หลักที่ไม่มีป้าย_ไม่ถูกเดาว่าเป็นเลขบัญชี()
    {
        // เลขที่เอกสาร/เลขอ้างอิงยาวเท่ากัน — เดาแล้วปิดบังผิดตัวคือทำลาย
        // ข้อมูลที่โมเดลต้องใช้ ("ไม่รู้ = บอกว่าไม่รู้" ไม่ใช่เดา)
        Assert.Contains("2026090001", San("""{"reference":"INV 2026090001"}"""));
    }

    // ── เลข 13 หลัก: นิติบุคคล vs บัตรประชาชน ──

    [Fact]
    public void keepTaxIds_คงเลขนิติบุคคล_ที่ขึ้นต้นศูนย์()
    {
        var outp = San("""{"our_company":"0105558000123"}""", keepTaxIds: true);
        Assert.Contains("0105558000123", outp);        // ต้องเทียบกับเลขบนกระดาษได้
    }

    [Fact]
    public void keepTaxIds_ยังปิดบังเลขบัตรประชาชนของบุคคลธรรมดา()
    {
        // ★ ผู้ขายรายย่อย/ฟรีแลนซ์ใช้เลขบัตรประชาชนเป็นเลขผู้เสียภาษี —
        // §26 ข้อมูลส่วนบุคคล ไม่มีเหตุผลใดต้องส่งออกไปให้ provider
        var outp = San("""{"vendor_tax_id":"1234567890123"}""", keepTaxIds: true);
        Assert.DoesNotContain("1234567890123", outp);
        Assert.Contains("1xxxxxxxxxx3", outp);
    }

    [Fact]
    public void ไม่_keepTaxIds_ปิดบังทั้งสองชนิด()
    {
        var outp = San("""{"a":"0105558000123","b":"1234567890123"}""");
        Assert.DoesNotContain("0105558000123", outp);
        Assert.DoesNotContain("1234567890123", outp);
    }

    [Fact]
    public void เลขนิติบุคคลแบบมีขีดคั่น_ก็คงรูปเดิมตอน_keepTaxIds()
        => Assert.Contains("0-1055-58000-12-3",
            San("""{"x":"0-1055-58000-12-3"}""", keepTaxIds: true));

    // ── ของเดิมที่ต้องไม่พัง ──

    [Fact]
    public void เบอร์โทรยังถูกปิดบังแบบเดิม_และเทียบกันได้()
    {
        var outp = San("""{"contact_phone":"0812345678","memo":"PromptPay 081-234-5678"}""");
        Assert.DoesNotContain("0812345678", outp);
        Assert.Equal(2, outp.Split("08xxxx78").Length - 1);   // สองฝั่งได้หน้ากากเดียวกัน
    }

    [Fact]
    public void ฟิลด์ลงท้าย_pii_ถูกแทนด้วย_hash_ที่คงที่()
    {
        var a = San("""{"name_pii":"สมชาย ใจดี"}""");
        var b = San("""{"name_pii":"สมชาย ใจดี"}""");
        Assert.DoesNotContain("สมชาย", a);
        Assert.Contains("h:", a);
        Assert.Equal(a, b);                                   // cache ต้องยังใช้ได้
    }

    [Fact]
    public void ปิดสวิตช์_stripPii_แล้วไม่แตะอะไรเลย()
    {
        const string raw = """{"x":"boss@acme.com","y":"0812345678"}""";
        Assert.Equal(raw, _s.Sanitize(raw, stripPii: false));
    }

    [Fact]
    public void JSON_พัง_ไม่โยน_แต่ยังปิดบังด้วย_regex()
    {
        var outp = _s.Sanitize("ไม่ใช่ json เลย boss@acme.com 0812345678", stripPii: true);
        Assert.DoesNotContain("boss@acme.com", outp);
        Assert.DoesNotContain("0812345678", outp);
    }

    [Fact]
    public void hash_ของ_prompt_เปลี่ยนตามโมเดล()
    {
        var h1 = _s.ComputePromptHash("{}", "sys", "deepseek-chat");
        var h2 = _s.ComputePromptHash("{}", "sys", "claude");
        Assert.NotEqual(h1, h2);
    }
}
