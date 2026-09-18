using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>
/// **กุญแจของ "อินพุตเดียวกัน" — ตัวเดียวของทั้งระบบ** (pure ไม่มี I/O)
///
/// ═══ ทำไมต้องมีที่เดียว (รอบ 178) ═══
/// คลังคำตอบที่เรียนไว้ (<c>AiSuggestionMemory</c>) ถูก**เขียน**ด้วยกุญแจแบบหนึ่ง
/// และถูก**อ่าน**ด้วยกุญแจอีกแบบหนึ่ง ⇒ แถวที่เส้น orchestrator เขียนไว้
/// **ไม่มีใครอ่านกลับได้เลย**:
/// <list type="bullet">
/// <item>เส้น orchestrator เขียน <c>PromptHash</c> = SHA(model + systemPrompt + userJson)
/// — ซึ่งมี <b>ชื่อรุ่นโมเดล</b> และ <b>บริบทบริษัทที่ถูกเติมเข้า system prompt</b>
/// ปนอยู่ ⇒ เปลี่ยนรุ่น/ผังบัญชีขยับ กุญแจก็เปลี่ยน = อินพุตเดิมไม่มีวันชนกุญแจเดิม</item>
/// <item>เส้นนักเรียน (<c>GenericFeedbackDistillationModel</c>) ใช้ fingerprint ของ
/// <b>userJson อย่างเดียว</b> หลังตัด token ที่เปลี่ยนทุกใบ (เลขผู้เสียภาษี · ยอดเงิน ·
/// วันที่ · เลขเอกสาร) ออก — อันนี้คือ "อินพุตเดียวกัน" ที่แท้จริง</item>
/// </list>
/// ⇒ ยุบเป็นฟังก์ชันเดียวที่นี่ แล้วให้ทั้งฝั่งเขียน (orchestrator) · ฝั่งเรียน
/// (งานกลางคืน) · ฝั่งอ่าน (นักเรียน) เรียกตัวเดียวกัน (หลักการข้อ 4 "ตัวตั้งตัวเดียว")
///
/// <para>⚠️ <b>ห้ามใส่ค่าที่เปลี่ยนทุกใบลงในกุญแจ</b> — ยอดเงิน/วันที่/เลขเอกสารถูก
/// แทนด้วย token คงที่โดยตั้งใจ: ใบของผู้ขายรายเดิม รายการเดิม แต่ยอดต่างกัน
/// ต้องได้คำตอบที่เรียนไว้เดียวกัน ไม่งั้นคลังจะโตแต่ไม่มีวันถูกใช้</para>
///
/// <para>ผลลัพธ์เป็นเลขฐานสิบหก 64 ตัว — พอดีกับคอลัมน์ <c>PromptHash varchar(80)</c></para>
/// </summary>
public static class AiMemoryKey
{
    /// <summary>กุญแจของ prompt JSON นี้ — <c>""</c> เมื่อไม่มีอะไรให้ทำกุญแจ
    /// (ผู้เรียกต้องถือว่า <c>""</c> = "ไม่มีกุญแจ" แล้วไม่เขียน/ไม่อ่านคลัง)</summary>
    public static string Of(string? promptJson)
    {
        if (string.IsNullOrEmpty(promptJson)) return "";
        var normalised = Normalise(promptJson);
        if (normalised.Length == 0) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    private static readonly System.Text.RegularExpressions.Regex _taxIdRe =
        new(@"\b\d{13}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    // ⚠️ รูปแบบ "หลัง mask" ของ AiPromptSanitizer ต้อง normalise ให้เป็น token
    // เดียวกับค่าดิบ — แถว feedback ถูกบันทึกด้วย prompt ที่ sanitize แล้ว
    // (AiStripPiiInPrompts=true) ขณะที่ตอนทำนายใช้ prompt ดิบ ถ้าไม่ทำให้ตรงกัน
    // fingerprint จะไม่มีวันชนกัน → exact-memory ของ student ตายสนิท
    private static readonly System.Text.RegularExpressions.Regex _taxIdMaskedRe =
        new(@"\b(?:\dx{10}\d|x{13})\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _phoneRe =
        new(@"\b0\d{1,2}[- \t]?\d{3}[- \t]?\d{4}\b|\b0\d{8,9}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _phoneMaskedRe =
        new(@"\b(?:\d{2}x{4}\d{2}|0x{6})\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _piiHashRe =
        new(@"""h:[0-9a-f]{6,}""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _docNumRe =
        new(@"\b(?:INV|TXN|REF|PV|RV|BIL|TAX|IV)[-_/]?\d{4,}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex _dateRe =
        new(@"\b\d{4}-\d{2}-\d{2}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _amountRe =
        new(@"\b[\d,]+\.?\d*\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex _wsRe =
        new(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Normalise(string s)
    {
        // Best-effort: re-serialise to canonical JSON so key ordering /
        // whitespace don't affect the fingerprint; fall back to raw on parse error.
        var t = s;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(s);
            if (node != null)
            {
                MaskPiiKeys(node);
                t = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            }
        }
        catch (JsonException) { /* not JSON — fingerprint the raw text */ }

        t = t.ToLowerInvariant();
        t = _piiHashRe.Replace(t, "\"<pii>\"");
        // masked ก่อน raw: ค่าที่ถูก mask แล้วมีตัวอักษร x ปน ถ้าปล่อยให้ _amountRe
        // จับก่อนจะได้ "<amt>xxxx<amt>" ซึ่งไม่ตรงกับค่าดิบที่ได้ "<phone>"
        t = _taxIdMaskedRe.Replace(t, "<taxid>");
        t = _taxIdRe.Replace(t, "<taxid>");
        t = _phoneMaskedRe.Replace(t, "<phone>");
        t = _phoneRe.Replace(t, "<phone>");
        t = _docNumRe.Replace(t, "<docnum>");
        t = _dateRe.Replace(t, "<date>");
        t = _amountRe.Replace(t, "<amt>");
        t = _wsRe.Replace(t, " ").Trim();
        return t;
    }

    /// <summary>แทนค่าของ field ที่เป็น PII ตาม convention ของ AiPromptSanitizer
    /// ("*_pii" / "*_personal") ด้วย token คงที่ — ฝั่งบันทึกเก็บเป็น "h:{hash}"
    /// ฝั่งทำนายเป็นค่าดิบ ถ้าไม่ทำให้เหมือนกัน fingerprint จะไม่ตรงกันตลอดไป</summary>
    private static void MaskPiiKeys(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var val = obj[key];
                if (key.EndsWith("_pii", StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("_personal", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = "<pii>";
                }
                else if (val is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
                {
                    MaskPiiKeys(val);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr.Where(i => i != null))
                MaskPiiKeys(item!);
        }
    }
}
