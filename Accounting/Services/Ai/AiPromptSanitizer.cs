using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Services.Ai;

/// <summary>
/// ด่าน PDPA ก่อนข้อมูลออกจากระบบไปหา provider — ทำงานบน UserPromptJson
/// ที่ประกอบเสร็จแล้ว มีสองหน้าที่:
/// <list type="number">
///   <item>ปิดบัง PII: เลขบัตรประชาชน/เลขผู้เสียภาษี · เบอร์โทร ·
///         <b>อีเมล</b> · <b>เลขบัญชีธนาคาร</b> · ฟิลด์ที่ตั้งชื่อลงท้าย
///         <c>_pii</c>/<c>_personal</c> (แทนด้วย hash)</item>
///   <item>คำนวณ hash สำหรับ cache <b>หลัง</b>ปิดบัง</item>
/// </list>
///
/// <para>═══ หลักการปิดบัง: ต้อง "เทียบได้ แต่อ่านไม่ออก" ═══
/// หลาย feature ต้อง<b>เทียบ</b>ค่าสองฝั่ง (เบอร์ใน memo PromptPay เทียบเบอร์
/// ผู้ติดต่อ · เลขบัญชีบนสเตทเมนต์เทียบบัญชีที่บันทึกไว้) การปิดบังจึงต้อง
/// <b>deterministic</b> — ค่าเดียวกันได้หน้ากากเดียวกันเสมอ ⇒ โมเดลยังเทียบ
/// เท่ากันได้โดยไม่เห็นตัวเลขจริง. ถ้าเปลี่ยนไปสุ่ม/ตัดทิ้ง feature พวกนี้ตาย</para>
///
/// <para>═══ ที่มาของรอบแก้ (ผลตรวจ E-AI-06) ═══
/// (ก) doc ของคลาสนี้เขียนว่าปิดบัง "อีเมล" และ "ที่อยู่" มาตลอด แต่
/// <b>ไม่มี regex อีเมลเลย</b> (ข) เลขบัญชี 10 หลักที่ไม่ขึ้นต้น 0 ไม่เข้า
/// <c>PhoneRegex</c> จึงหลุดดิบ (ค) <c>AllowTaxIdInPrompt=true</c> ปล่อย
/// <b>เลขบัตรประชาชนของบุคคลธรรมดา</b> (§26 ข้อมูลอ่อนไหว) ออกไปด้วย ทั้งที่
/// สิ่งที่ feature ต้องเทียบคือเลขนิติบุคคล</para>
///
/// <para><b>ที่อยู่ยัง "ไม่" ปิดบัง — เป็นการตัดสินใจ ไม่ใช่ของตกหล่น:</b>
/// ที่อยู่ผู้ขาย/ผู้ซื้อเป็นรายการบังคับตาม §86/4 และงานของ
/// <c>OcrFullReview</c> คือ<b>แก้ที่อยู่ที่ OCR อ่านเพี้ยนให้ถูก</b> —
/// ปิดบังแล้วคืนค่าที่ปิดบังกลับมาเขียนทับ = ทำข้อมูลจริงเสียหาย
/// (ทางที่ถูกถ้าจะปิดบังคือให้ builder ตั้งชื่อฟิลด์เป็น <c>*_pii</c>
/// ซึ่งด่านนี้กับ <c>GenericFeedbackDistillationModel</c> รองรับอยู่แล้ว
/// — วันนี้ยังไม่มี builder ตัวไหนใช้ เพราะทุก feature ที่ส่งชื่อ/ที่อยู่
/// ต้องใช้ค่านั้นตอบคำถามพอดี)</para>
/// </summary>
public interface IAiPromptSanitizer
{
    string Sanitize(string userPromptJson, bool stripPii);

    /// <summary><paramref name="keepTaxIds"/> = ไม่ปิดบังเลขผู้เสียภาษี
    /// (เบอร์โทร/อีเมลยังปิดบังเสมอ) — ใช้เฉพาะ feature ที่คำถามคือการเทียบ
    /// เลขนั้นเอง ดู <c>AiRequest.AllowTaxIdInPrompt</c></summary>
    string Sanitize(string userPromptJson, bool stripPii, bool keepTaxIds);

    string ComputePromptHash(string sanitizedUserPromptJson, string systemPrompt, string model);
}

public class AiPromptSanitizer : IAiPromptSanitizer
{
    // Thai national ID / tax ID: 13 digits, often dash-separated as
    // 1-2345-67890-12-3. Match both formats.
    private static readonly System.Text.RegularExpressions.Regex ThaiIdRegex =
        new(@"\b\d{1}[- \t]?\d{4}[- \t]?\d{5}[- \t]?\d{2}[- \t]?\d{1}\b|\b\d{13}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Thai phone numbers: 0xx-xxx-xxxx or 0xxxxxxxxx (9-10 digits
    // starting with 0). Mobile and landline both covered.
    private static readonly System.Text.RegularExpressions.Regex PhoneRegex =
        new(@"\b0\d{1,2}[- \t]?\d{3}[- \t]?\d{4}\b|\b0\d{8,9}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // อีเมล — doc ของคลาสนี้อ้างมาตลอดว่าปิดบัง แต่ไม่เคยมี regex เลย
    // (ผลตรวจ E-AI-06 ก). ไม่มี feature ไหนต้องอ่านอีเมลเพื่อตอบคำถาม
    // จึงปิดบังเสมอ — รวมตอน keepTaxIds ด้วย
    private static readonly System.Text.RegularExpressions.Regex EmailRegex =
        new(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // เลขบัญชีธนาคาร **ที่มีป้ายกำกับ** — 10-15 หลัก
    // ⚠️ จงใจไม่จับเลข 10 หลักลอย ๆ: ในสเตทเมนต์/เอกสารมีเลขที่เอกสาร ·
    // เลขอ้างอิง · เลขที่ใบกำกับ ที่ยาวเท่ากัน — เดาแล้วปิดบังผิดตัวคือการ
    // ทำลายข้อมูลที่โมเดลต้องใช้ ("ไม่รู้ = บอกว่าไม่รู้" ไม่ใช่เดา)
    // เลขบัญชีที่มาแบบไม่มีป้ายถูกจับอีกทางด้วยกติกา "ชื่อฟิลด์" ข้างล่าง
    private static readonly System.Text.RegularExpressions.Regex LabelledBankAccountRegex =
        new(@"(?<label>เลขที่บัญชี|บัญชีเลขที่|เลขบัญชี|เลขที่บช\.|a/c\s*no\.?|account\s*(?:no\.?|number))"
            + @"(?<sep>[\s:\-]{0,4})(?<acct>\d(?:[- ]?\d){9,14})",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>ชื่อฟิลด์ที่ "เนื้อในเป็นเลขบัญชีเสมอ" — ปิดบังทั้งค่าโดยไม่ต้องรอป้าย
    /// (payload จริงที่ builder ส่ง: <c>bank_account</c> · <c>contact_bank_acct</c> ·
    /// <c>bank_account_patterns</c>)</summary>
    private static bool IsBankAccountKey(string key)
        => key.Contains("bank_acct", StringComparison.OrdinalIgnoreCase)
        || key.Contains("bank_account", StringComparison.OrdinalIgnoreCase);

    public string Sanitize(string userPromptJson, bool stripPii)
        => Sanitize(userPromptJson, stripPii, keepTaxIds: false);

    /// <param name="keepTaxIds">ไม่ปิดบังเลขผู้เสียภาษี — ใช้เฉพาะ feature ที่
    /// <b>คำถามคือการเทียบเลขนั้นเอง</b> (ดู <c>AiRequest.AllowTaxIdInPrompt</c>)
    /// เบอร์โทร/อีเมลยังถูกปิดบังตามเดิมเสมอ</param>
    public string Sanitize(string userPromptJson, bool stripPii, bool keepTaxIds)
    {
        if (!stripPii || string.IsNullOrWhiteSpace(userPromptJson))
            return userPromptJson;

        try
        {
            // Walk the JSON tree and rewrite any string values that
            // contain PII. Field semantics are encoded in the field name
            // (PromptBuilder convention) — anything ending in "_pii"
            // gets hash-replaced; anything containing tax-id / phone /
            // address gets mask-applied via regex.
            var node = System.Text.Json.Nodes.JsonNode.Parse(userPromptJson);
            if (node != null)
            {
                WalkAndMask(node, keepTaxIds);
                return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            }
        }
        catch
        {
            // Don't fail the whole call because sanitisation tripped on
            // an edge case. Fall back to regex-only on the raw string.
        }
        return MaskWithRegex(userPromptJson, keepTaxIds);
    }

    private static void WalkAndMask(
        System.Text.Json.Nodes.JsonNode node, bool keepTaxIds, bool inBankAccountField = false)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var val = obj[key];
                if (val is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var s) && s != null)
                {
                    // Convention: a field name ending in "_pii" or
                    // "_personal" gets HASHED entirely (untraceable but
                    // stable across calls so cache still works).
                    if (key.EndsWith("_pii", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("_personal", StringComparison.OrdinalIgnoreCase))
                    {
                        obj[key] = "h:" + ShortHash(s);
                    }
                    else if (IsBankAccountKey(key))
                    {
                        // ค่าทั้งช่องคือเลขบัญชี — ไม่ต้องรอป้ายกำกับ
                        obj[key] = MaskAccountDigits(s);
                    }
                    else
                    {
                        obj[key] = MaskWithRegex(s, keepTaxIds);
                    }
                }
                else if (val is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
                {
                    WalkAndMask(val, keepTaxIds, IsBankAccountKey(key));
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            // ⚠️ เดิมวนแล้วเรียกตัวเองต่อ ซึ่ง**ข้ามสตริงที่เป็นสมาชิกของ array ทั้งหมด**
            // (ตัวมันไม่ใช่ object/array จึงตกท้ายฟังก์ชันแล้วจบ) ⇒ `top_line_items`
            // (คำอธิบายรายการจาก OCR) · `bank_account_patterns` · ตัวอย่างค่าคอลัมน์
            // ตอน import ถูกส่งออกไป **ดิบทั้งหมด** ทั้งที่ผ่านด่านนี้แล้ว
            for (var i = 0; i < arr.Count; i++)
            {
                var item = arr[i];
                if (item is System.Text.Json.Nodes.JsonValue ajv
                    && ajv.TryGetValue<string>(out var astr) && astr != null)
                    arr[i] = inBankAccountField ? MaskAccountDigits(astr) : MaskWithRegex(astr, keepTaxIds);
                else if (item != null)
                    WalkAndMask(item, keepTaxIds, inBankAccountField);
            }
        }
    }

    private static string MaskWithRegex(string s, bool keepTaxIds = false)
    {
        // ── เลข 13 หลัก ──
        // keepTaxIds = งานนี้คือการ "เทียบเลข" — ปิดบังแล้วโมเดลตอบไม่ได้ และ
        // คำตอบที่ได้กลับมาเป็นสตริงที่ถูกปิดบัง ซึ่งถ้าเขียนกลับลงเอกสาร
        // = ทำข้อมูลจริงเสียหาย (ดู AiRequest.AllowTaxIdInPrompt)
        //
        // **แต่ "เลข 13 หลัก" มีสองชนิดที่กฎหมายมองคนละแบบ** (ผลตรวจ E-AI-06 ค):
        //   • นิติบุคคล  — ขึ้นต้น **0** — เป็นข้อมูลสาธารณะ (ค้นทะเบียน DBD ได้)
        //     และเป็นเลขที่ feature ต้องเทียบจริง ⇒ keepTaxIds ปล่อยผ่านได้
        //   • บุคคลธรรมดา — ขึ้นต้น 1-8 = **เลขบัตรประชาชน** ซึ่งเป็นข้อมูล
        //     ส่วนบุคคลตาม PDPA (ผู้ขายรายย่อย/ฟรีแลนซ์ใช้เลขนี้เป็นเลขภาษี)
        //     ⇒ ปิดบัง **เสมอ** แม้ keepTaxIds เพราะไม่มีเหตุผลทางธุรกิจใด
        //     ที่ต้องส่งเลขบัตรประชาชนออกไปให้ provider ภายนอก
        s = ThaiIdRegex.Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length != 13) return "xxxxxxxxxxxxx";
            var juristic = digits[0] == '0';
            if (keepTaxIds && juristic) return m.Value;      // คงรูปเดิม (มีขีดคั่นก็คงไว้)
            return $"{digits[0]}xxxxxxxxxx{digits[12]}";
        });
        // Phone: keep first 2 + last 2.
        s = PhoneRegex.Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length >= 6 ? digits[..2] + "xxxx" + digits[^2..] : "0xxxxxx";
        });
        // อีเมล: เก็บอักษรแรกของ local part + โดเมนระดับบนสุด พอให้เทียบ
        // "อีเมลเดียวกันไหม" ได้ (deterministic) แต่ติดต่อกลับไม่ได้
        s = EmailRegex.Replace(s, m =>
        {
            var v = m.Value;
            var at = v.IndexOf('@');
            var dot = v.LastIndexOf('.');
            var head = at > 0 ? v[0] : 'x';
            var tld = dot > at && dot < v.Length - 1 ? v[(dot + 1)..] : "xxx";
            return $"{head}xxx@xxx.{tld}";
        });
        // เลขบัญชีที่มีป้ายกำกับในข้อความอิสระ (memo/สเตทเมนต์)
        s = LabelledBankAccountRegex.Replace(s, m =>
            m.Groups["label"].Value + m.Groups["sep"].Value + MaskAccountDigits(m.Groups["acct"].Value));
        return s;
    }

    /// <summary>ปิดบังเลขบัญชี — เก็บ 2 ตัวแรก + 2 ตัวท้าย (deterministic ⇒
    /// ยังเทียบ "บัญชีเดียวกันไหม" ได้ แต่โอนเงินตามไม่ได้)</summary>
    private static string MaskAccountDigits(string s)
    {
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (digits.Length < 6) return s;                     // สั้นเกินกว่าจะเป็นเลขบัญชี — ไม่แตะ
        return digits[..2] + new string('x', digits.Length - 4) + digits[^2..];
    }

    private static string ShortHash(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public string ComputePromptHash(string sanitizedUserPromptJson, string systemPrompt, string model)
    {
        // Stable hash includes the model version so a provider/model
        // switch invalidates cache (prevents serving a deepseek answer
        // to an opus query).
        var combined = $"{model}{systemPrompt}{sanitizedUserPromptJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
