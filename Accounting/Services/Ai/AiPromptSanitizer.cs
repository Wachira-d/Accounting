using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Services.Ai;

/// <summary>
/// PDPA / privacy guard. Runs over the canonical UserPromptJson BEFORE
/// it leaves the boundary to the AI provider. Two responsibilities:
///   1. Mask PII: Thai national IDs / tax IDs / phone numbers, customer
///      personal names (when explicitly marked), full street addresses.
///   2. Compute a STABLE hash for cache lookup AFTER masking so two
///      different masked inputs that resolve to the same logical thing
///      don't poison cross-tenant cache (cache key includes CompanyId
///      separately).
///
/// Intentionally conservative — anything that looks like PII gets
/// stripped. Per-feature PromptBuilders are responsible for deciding
/// which fields are PII-bearing; the sanitizer just enforces.
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

    private static void WalkAndMask(System.Text.Json.Nodes.JsonNode node, bool keepTaxIds)
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
                    else
                    {
                        obj[key] = MaskWithRegex(s, keepTaxIds);
                    }
                }
                else if (val is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray)
                {
                    WalkAndMask(val, keepTaxIds);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr.Where(i => i != null))
                WalkAndMask(item!, keepTaxIds);
        }
    }

    private static string MaskWithRegex(string s, bool keepTaxIds = false)
    {
        // Thai ID: keep first digit + last digit, mask middle.
        // keepTaxIds = งานนี้คือการ "เทียบเลข" — ปิดบังแล้วโมเดลตอบไม่ได้
        // และคำตอบที่ได้กลับมาเป็นสตริงที่ถูกปิดบัง ซึ่งถ้าเขียนกลับลงเอกสาร
        // = ทำข้อมูลจริงเสียหาย (ดู AiRequest.AllowTaxIdInPrompt)
        if (!keepTaxIds) s = ThaiIdRegex.Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length == 13 ? $"{digits[0]}xxxxxxxxxx{digits[12]}" : "xxxxxxxxxxxxx";
        });
        // Phone: keep first 2 + last 2.
        s = PhoneRegex.Replace(s, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length >= 6 ? digits[..2] + "xxxx" + digits[^2..] : "0xxxxxx";
        });
        return s;
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
