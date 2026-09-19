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
/// <para>⚠️ <b>บล็อก <c>local_model</c> ไม่ใช่อินพุต — ต้องถูกตัดออกจากกุญแจ</b>
/// (รอบ 181 · D7-2) prompt builder ทุกตัวที่เสนอคำตอบของนักเรียนให้ครูตรวจ
/// (<c>GlAccountPrompt</c> · <c>VendorCanonPrompt</c> · <c>WorkflowPrompts</c> ·
/// <c>AdvancedPrompts</c> · <c>BankAndAnalyticsPrompts</c>) ใส่บล็อกนี้ไว้ใน payload และ
/// <c>AiOrchestrator.ReplaceLocalModelBlock</c> **เขียนทับมันด้วยคำตอบของนักเรียนตัวจริง**
/// ก่อนคิดกุญแจ ⇒ ถ้านับบล็อกนี้เข้ากุญแจด้วย:
/// <list type="bullet">
/// <item>ฝั่ง <b>ทำนาย</b> (<c>GenericFeedbackDistillationModel.PredictAsync</c>) คิดจาก
/// JSON <b>ก่อน</b>เขียนทับ — บล็อกยังเป็น heuristic ของผู้เรียก</item>
/// <item>ฝั่ง <b>เขียน/เรียน</b> คิดจาก JSON <b>หลัง</b>เขียนทับ — บล็อกเป็นคำตอบนักเรียน
/// และมีคีย์ <c>source</c> งอกเพิ่มมาอีก</item>
/// </list>
/// ⇒ กุญแจสองฝั่งต่างกัน <b>ทันทีที่นักเรียนเริ่มตอบได้</b> = นักเรียนหยุดโตหลังใบแรก
/// (คำถามเดิมเป๊ะ ๆ ก็หาคลังไม่เจอ). บล็อกนี้คือ "คำตอบที่เสนอ" ไม่ใช่ "คำถาม" จึงไม่ควร
/// อยู่ในกุญแจของอินพุตตั้งแต่แรก</para>
///
/// <para>คีย์อื่นที่เป็น "คำตอบที่เสนอ" (ถ้ามีเพิ่มวันหลัง) ให้เติมใน
/// <c>ProposedAnswerKeys</c> — <b>ห้ามใช้แพตเทิร์นเดาชื่อ</b> เพราะคีย์ของอินพุตจริงอาจ
/// ชื่อคล้ายกัน (ญาติของ <c>__NEW__</c> ที่เป็น "คำตอบจริง" ไม่ใช่ sentinel)</para>
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

    /// <summary>คีย์ใน payload ที่เป็น <b>"คำตอบที่เสนอ"</b> ไม่ใช่ <b>"คำถาม"</b> —
    /// ตัดทิ้งก่อนทำกุญแจเสมอ (ดูเหตุผลเต็มที่ doc ของคลาส · รอบ 181 D7-2)
    ///
    /// <para>เป็น <b>ลิสต์ปิด</b> โดยตั้งใจ: ตรวจชื่อคีย์ตรง ๆ เท่านั้น ห้ามใช้แพตเทิร์น
    /// (<c>local*</c>/<c>*_model</c>) เพราะจะกินคีย์อินพุตจริงที่บังเอิญชื่อคล้ายกัน แล้ว
    /// คำถามคนละคำถามจะได้กุญแจเดียวกัน = เสิร์ฟคำตอบของใบอื่น ซึ่งแย่กว่าไม่ตอบ</para>
    ///
    /// <para>ปัจจุบันมีตัวเดียว — ยืนยันด้วย
    /// <c>grep -rn "local_model" Accounting/Services/Ai/Prompts/</c> (11 จุด 5 ไฟล์
    /// ทั้งหมดใช้ชื่อนี้ชื่อเดียว) และไม่มี prompt builder ตัวใดใช้ <c>local_prediction</c>
    /// หรือ <c>student_*</c> เลย</para></summary>
    private static readonly HashSet<string> ProposedAnswerKeys =
        new(StringComparer.OrdinalIgnoreCase) { "local_model" };

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
                t = Canonicalise(node).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
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

    /// <summary>คืน JSON ที่ <b>เรียงคีย์แล้ว</b> + แทนค่าของ field ที่เป็น PII
    /// ด้วย token คงที่
    ///
    /// <para><b>เรียงคีย์</b>: <c>JsonNode</c> คงลำดับตามที่ parse มา ⇒ payload
    /// เดียวกันที่ผู้เรียกคนละจุดประกอบคนละลำดับจะได้กุญแจคนละตัว (คอมเมนต์เดิมของ
    /// สำเนานี้เขียนว่า "canonical JSON" มาตลอดแต่ไม่เคยเรียงจริง — โค้ดเป็น
    /// ground truth จึงทำให้เป็นจริงตามที่เขียนไว้ · รอบ 178)</para>
    ///
    /// <para><b>PII</b>: convention ของ <c>AiPromptSanitizer</c> คือ "*_pii" / "*_personal"
    /// — ฝั่งบันทึกเก็บเป็น "h:{hash}" ฝั่งทำนายเป็นค่าดิบ ถ้าไม่ทำให้เหมือนกัน
    /// fingerprint จะไม่ตรงกันตลอดไป</para></summary>
    private static System.Text.Json.Nodes.JsonNode Canonicalise(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var result = new System.Text.Json.Nodes.JsonObject();
            foreach (var key in obj.Select(kvp => kvp.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                // "คำตอบที่เสนอ" ไม่ใช่อินพุต — ตัดทิ้งทุกชั้น (บล็อกอยู่ชั้นบนสุดในวันนี้
                // แต่ตัดทุกชั้นกันไว้ ถ้ามี prompt ห่อ payload ซ้อนวันหลัง)
                if (ProposedAnswerKeys.Contains(key)) continue;
                var val = obj[key];
                if (key.EndsWith("_pii", StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("_personal", StringComparison.OrdinalIgnoreCase))
                    result[key] = "<pii>";
                else
                    result[key] = val == null ? null : Canonicalise(val.DeepClone());
            }
            return result;
        }

        if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            // ลำดับของ array คือ**ข้อมูล** (บรรทัดที่ 1 ไม่ใช่บรรทัดที่ 2) — ห้ามเรียง
            var outArr = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in arr)
                outArr.Add(item == null ? null : Canonicalise(item.DeepClone()));
            return outArr;
        }

        return node;
    }
}
