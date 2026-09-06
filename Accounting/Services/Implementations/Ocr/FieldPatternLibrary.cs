using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Strict pattern definitions for each extracted field type.
/// Knows the format, length, validation rules, and context keywords for each
/// field — so a phone number will never be mistaken for a Tax ID, and a
/// postal code will never be mistaken for an amount.
/// </summary>
public static class FieldPatternLibrary
{
    public enum FieldType
    {
        TaxId,              // Thai 13-digit national/juristic ID with checksum
        BranchCode,         // 5-digit Thai branch code (00000 = HQ)
        PhoneNumber,        // Thai phone 9-10 digits, starts with 0 or +66
        PostalCode,         // Thai 5-digit postal code
        Email,
        DocumentNumber,     // Alphanumeric document identifier
        Date,               // Various Thai/Gregorian formats
        Amount,             // Decimal money amount
        CompanyName,        // Thai legal-entity name (บริษัท/ห้างหุ้นส่วน/ร้าน)
        BankAccount,        // Thai bank account number
        Url,
    }

    // === Public API ===

    /// <summary>
    /// Extract all candidate values for a given field type from text.
    /// Each candidate is validated against pattern rules; only valid matches are returned.
    /// </summary>
    public static List<FieldCandidate> ExtractCandidates(string text, FieldType type)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        return type switch
        {
            FieldType.TaxId => ExtractTaxIdCandidates(text),
            FieldType.BranchCode => ExtractBranchCodeCandidates(text),
            FieldType.PhoneNumber => ExtractPhoneCandidates(text),
            FieldType.PostalCode => ExtractPostalCodeCandidates(text),
            FieldType.Email => ExtractEmailCandidates(text),
            FieldType.DocumentNumber => ExtractDocNumberCandidates(text),
            FieldType.Date => ExtractDateCandidates(text),
            FieldType.Amount => ExtractAmountCandidates(text),
            FieldType.CompanyName => ExtractCompanyNameCandidates(text),
            FieldType.BankAccount => ExtractBankAccountCandidates(text),
            FieldType.Url => ExtractUrlCandidates(text),
            _ => new()
        };
    }

    public static bool Validate(string value, FieldType type) => type switch
    {
        FieldType.TaxId => ValidateThaiTaxId(value),
        FieldType.BranchCode => ValidateBranchCode(value),
        FieldType.PhoneNumber => ValidateThaiPhone(value),
        FieldType.PostalCode => ValidateThaiPostalCode(value),
        FieldType.Email => Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"),
        FieldType.Date => ValidateDate(value),
        FieldType.Amount => decimal.TryParse(value.Replace(",", ""), out var v) && v >= 0,
        _ => !string.IsNullOrWhiteSpace(value)
    };

    public static string[] GetContextKeywords(FieldType type) => type switch
    {
        FieldType.TaxId => new[] {
            "เลขประจำตัวผู้เสียภาษี", "เลขผู้เสียภาษี", "เลขประจำตัวภาษี",
            "เลขประจำตัว", "TaxID", "Tax ID", "Tax No", "TIN", "VAT No",
            "เลขที่ผู้เสียภาษี", "เลขที่ภาษี"
        },
        FieldType.BranchCode => new[] {
            "สาขา", "รหัสสาขา", "Branch", "สำนักงานใหญ่", "Head Office", "HQ"
        },
        FieldType.PhoneNumber => new[] {
            "โทร", "โทรศัพท์", "เบอร์โทร", "เบอร์", "Tel", "Phone", "Mobile",
            "Hotline", "Fax", "แฟกซ์"
        },
        FieldType.PostalCode => new[] {
            "รหัสไปรษณีย์", "ไปรษณีย์", "Postal", "Zip", "ที่อยู่"
        },
        FieldType.Email => new[] {
            "อีเมล", "อีเมล์", "Email", "E-mail", "@"
        },
        FieldType.DocumentNumber => new[] {
            "เลขที่", "เอกสารเลขที่", "เลขที่เอกสาร", "หมายเลข", "เลขที่ใบ",
            "No.", "No", "Document No", "Invoice No", "Receipt No", "Ref",
            "Reference"
        },
        FieldType.Date => new[] {
            "วันที่", "วันออก", "ลงวันที่", "Date", "Issue Date", "Doc Date"
        },
        FieldType.Amount => new[] {
            "รวมทั้งสิ้น", "ยอดรวมสุทธิ", "ยอดรวม", "จำนวนเงินรวม", "รวมเงิน",
            "GRAND TOTAL", "NET TOTAL", "TOTAL", "AMOUNT"
        },
        FieldType.CompanyName => new[] {
            "บริษัท", "ห้างหุ้นส่วน", "ร้าน", "หจก", "บจก",
            "Company", "Co., Ltd", "Co.,Ltd"
        },
        FieldType.BankAccount => new[] {
            "เลขบัญชี", "เลขที่บัญชี", "Account No", "A/C No", "Bank Account"
        },
        _ => Array.Empty<string>()
    };

    // === TaxId ===

    static List<FieldCandidate> ExtractTaxIdCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Match 13 digits with optional separators
        var pattern = Accounting.Helpers.ThaiTaxId.Pattern;
        foreach (Match m in Regex.Matches(text, pattern))
        {
            var raw = m.Groups[1].Value;
            var normalized = Regex.Replace(raw, @"[-\s]", "");
            if (normalized.Length != 13) continue;
            var checksumOk = ValidateThaiTaxId(normalized);
            var c = new FieldCandidate
            {
                Value = raw,
                NormalizedValue = normalized,
                Position = m.Index,
                Length = m.Length,
                IsChecksumValid = checksumOk,
                FieldType = FieldType.TaxId,
            };
            // Boost score for checksum-valid IDs; non-checksum IDs still allowed
            // (some scanners may have 1-digit OCR errors)
            c.Score = checksumOk ? 1.0 : 0.4;
            c.ScoreReasons.Add(checksumOk ? "checksum:valid" : "checksum:invalid");
            results.Add(c);
        }
        return results;
    }

    /// <summary>Thai national/juristic ID checksum: mod-11 of weighted sum of first 12 digits.</summary>
    public static bool ValidateThaiTaxId(string id)
    {
        if (id == null || id.Length != 13 || !id.All(char.IsDigit)) return false;
        int sum = 0;
        for (int i = 0; i < 12; i++)
            sum += (id[i] - '0') * (13 - i);
        int check = (11 - (sum % 11)) % 10;
        return check == (id[12] - '0');
    }

    // === BranchCode ===

    static List<FieldCandidate> ExtractBranchCodeCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Look for "สาขา" followed by 5 digits OR "00000"/"สำนักงานใหญ่"
        foreach (Match m in Regex.Matches(text,
            @"(?:สาขา(?:ที่|เลขที่)?|Branch)\s*[:：]?\s*(\d{5}|00000|สำนักงานใหญ่|HQ|Head\s*Office)",
            RegexOptions.IgnoreCase))
        {
            var v = m.Groups[1].Value.Trim();
            if (v.Contains("สำนัก", StringComparison.OrdinalIgnoreCase) ||
                v.Contains("HQ", StringComparison.OrdinalIgnoreCase) ||
                v.Contains("Head", StringComparison.OrdinalIgnoreCase))
                v = "00000";
            results.Add(new FieldCandidate
            {
                Value = v,
                NormalizedValue = v,
                Position = m.Index,
                Length = m.Length,
                Score = 1.0,
                FieldType = FieldType.BranchCode,
                ScoreReasons = { "explicit:branch-keyword" }
            });
        }
        // Also: standalone "สำนักงานใหญ่"
        foreach (Match m in Regex.Matches(text, @"สำนักงานใหญ่"))
        {
            results.Add(new FieldCandidate
            {
                Value = "สำนักงานใหญ่",
                NormalizedValue = "00000",
                Position = m.Index,
                Length = m.Length,
                Score = 0.9,
                FieldType = FieldType.BranchCode,
                ScoreReasons = { "explicit:hq-keyword" }
            });
        }
        return results;
    }

    public static bool ValidateBranchCode(string value)
        => Regex.IsMatch(value ?? "", @"^\d{5}$");

    // === Phone ===

    static List<FieldCandidate> ExtractPhoneCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Thai phones: 02-XXX-XXXX or 0XX-XXX-XXXX or +66 X XXXX XXXX
        var patterns = new[]
        {
            @"\b(0\d{1,2}[- \t]?\d{3}[- \t]?\d{4})\b",        // 02-XXX-XXXX or 0XX-XXX-XXXX
            @"\b(0\d{8,9})\b",                                // 0XXXXXXXXX (no separators)
            @"\b(\+66[- \t]?\d{1,2}[- \t]?\d{3}[- \t]?\d{4})\b", // +66 format
        };
        foreach (var p in patterns)
        {
            foreach (Match m in Regex.Matches(text, p))
            {
                var v = m.Groups[1].Value;
                var normalized = Regex.Replace(v, @"[-\s+]", "");
                if (normalized.StartsWith("66")) normalized = "0" + normalized[2..];
                if (normalized.Length is 9 or 10 && normalized.StartsWith("0"))
                {
                    results.Add(new FieldCandidate
                    {
                        Value = v,
                        NormalizedValue = normalized,
                        Position = m.Index,
                        Length = m.Length,
                        Score = 1.0,
                        FieldType = FieldType.PhoneNumber,
                        ScoreReasons = { "format:thai-phone" }
                    });
                }
            }
        }
        return results;
    }

    public static bool ValidateThaiPhone(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var digits = Regex.Replace(value, @"\D", "");
        if (digits.StartsWith("66")) digits = "0" + digits[2..];
        return digits.Length is 9 or 10 && digits.StartsWith("0");
    }

    // === Postal Code ===

    static List<FieldCandidate> ExtractPostalCodeCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Postal code: exactly 5 digits, usually after province name or "รหัสไปรษณีย์"
        // Avoid matching parts of longer numbers (TaxId, account numbers)
        foreach (Match m in Regex.Matches(text, @"(?<![\d])(\d{5})(?![\d])"))
        {
            var v = m.Groups[1].Value;
            // Heuristic: postal codes start with 1-9 (no leading zero), valid range 10000-99999
            if (v[0] == '0') continue;
            // Score boosted if preceded by "ไปรษณีย์" within 30 chars
            var beforeCtx = text.Substring(Math.Max(0, m.Index - 50), Math.Min(50, m.Index));
            double score = beforeCtx.Contains("ไปรษณีย์") || beforeCtx.Contains("Postal", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.5;
            results.Add(new FieldCandidate
            {
                Value = v,
                NormalizedValue = v,
                Position = m.Index,
                Length = 5,
                Score = score,
                FieldType = FieldType.PostalCode,
                ScoreReasons = { score > 0.8 ? "explicit:postal-keyword" : "format:5-digit" }
            });
        }
        return results;
    }

    public static bool ValidateThaiPostalCode(string value)
        => Regex.IsMatch(value ?? "", @"^[1-9]\d{4}$");

    // === Email ===

    static List<FieldCandidate> ExtractEmailCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        foreach (Match m in Regex.Matches(text, @"\b[\w._%+-]+@[\w.-]+\.[A-Za-z]{2,}\b"))
        {
            results.Add(new FieldCandidate
            {
                Value = m.Value,
                NormalizedValue = m.Value.ToLowerInvariant(),
                Position = m.Index,
                Length = m.Length,
                Score = 1.0,
                FieldType = FieldType.Email,
                ScoreReasons = { "format:email" }
            });
        }
        return results;
    }

    // === URL ===

    static List<FieldCandidate> ExtractUrlCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        foreach (Match m in Regex.Matches(text, @"\bhttps?://[\w.-]+(?:/[\w./-]*)?\b", RegexOptions.IgnoreCase))
        {
            results.Add(new FieldCandidate
            {
                Value = m.Value, NormalizedValue = m.Value.ToLowerInvariant(),
                Position = m.Index, Length = m.Length,
                Score = 1.0, FieldType = FieldType.Url
            });
        }
        return results;
    }

    // === Document Number ===

    static List<FieldCandidate> ExtractDocNumberCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Strong patterns with explicit keyword
        var keywordPatterns = new[]
        {
            (@"(?:เลขที่|เลขที่เอกสาร|เอกสารเลขที่)\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,30})", 1.0),
            (@"(?:Document\s*No|Doc\s*No|Invoice\s*No|Receipt\s*No|No)\.?\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,30})", 0.95),
            (@"(?:Ref|Reference)\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,30})", 0.85),
        };
        foreach (var (pat, score) in keywordPatterns)
        {
            foreach (Match m in Regex.Matches(text, pat, RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value.Trim();
                // Reject pure-digit tax-id-like numbers
                if (Regex.IsMatch(v, @"^\d{13}$")) continue;
                // Reject phone-like
                if (Regex.IsMatch(v, @"^0\d{8,9}$")) continue;
                results.Add(new FieldCandidate
                {
                    Value = v, NormalizedValue = v,
                    Position = m.Index, Length = m.Length,
                    Score = score,
                    FieldType = FieldType.DocumentNumber,
                    ScoreReasons = { "keyword:explicit" }
                });
            }
        }
        // Pattern-based: prefixed codes (INV, TX, IV, REC, PO, CN, DN)
        foreach (Match m in Regex.Matches(text,
            @"\b((?:INV|TX|IV|REC|RC|PO|CN|DN|TAX)[\-/]?[A-Z0-9]+\d+[A-Z0-9\-/]*)\b",
            RegexOptions.IgnoreCase))
        {
            var v = m.Value.Trim();
            results.Add(new FieldCandidate
            {
                Value = v, NormalizedValue = v.ToUpperInvariant(),
                Position = m.Index, Length = m.Length,
                Score = 0.7,
                FieldType = FieldType.DocumentNumber,
                ScoreReasons = { "pattern:prefixed-code" }
            });
        }
        return results;
    }

    // === Date ===

    static List<FieldCandidate> ExtractDateCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Pattern 1: dd/mm/yyyy or dd-mm-yyyy or dd.mm.yyyy (year 4 or 2 digits)
        foreach (Match m in Regex.Matches(text,
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2,4})"))
        {
            int day = int.Parse(m.Groups[1].Value);
            int month = int.Parse(m.Groups[2].Value);
            int year = int.Parse(m.Groups[3].Value);
            if (year > 2400) year -= 543;          // Buddhist → Gregorian
            if (year < 100) year += 2000;          // 2-digit year
            if (TryBuildDate(year, month, day, out var d))
            {
                var v = m.Value;
                var ctx = SafeSubstring(text, m.Index - 30, 30);
                double score = ctx.Contains("วันที่") || ctx.Contains("Date", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.6;
                results.Add(new FieldCandidate
                {
                    Value = v, NormalizedValue = d.ToString("yyyy-MM-dd"),
                    Position = m.Index, Length = m.Length,
                    Score = score,
                    FieldType = FieldType.Date,
                    ScoreReasons = { score > 0.8 ? "keyword:explicit-date" : "format:numeric-date" }
                });
            }
        }
        // Pattern 2: dd <Thai-month> yyyy
        foreach (Match m in Regex.Matches(text,
            @"(\d{1,2})\s+([฀-๿\.]+)\s+(\d{4})"))
        {
            int day = int.Parse(m.Groups[1].Value);
            string monthStr = m.Groups[2].Value;
            int year = int.Parse(m.Groups[3].Value);
            if (year > 2400) year -= 543;
            // ตารางชื่อเดือนอยู่ที่ Helpers/ThaiMonthName ตัวเดียว — เดิมไฟล์นี้มี
            // สำเนาของตัวเอง ซึ่งรู้จักชื่อเต็มแต่เส้นทางหลักไม่รู้จัก ⇒ ใบเดียวกัน
            // อ่านวันที่ได้/ไม่ได้ต่างกันตาม engine ที่ใช้ (ผลตรวจ 2026-09-06 · T2-13)
            var month = Accounting.Helpers.ThaiMonthName.TryParse(monthStr) ?? 0;
            if (month > 0 && TryBuildDate(year, month, day, out var d))
            {
                results.Add(new FieldCandidate
                {
                    Value = m.Value, NormalizedValue = d.ToString("yyyy-MM-dd"),
                    Position = m.Index, Length = m.Length,
                    Score = 0.95,
                    FieldType = FieldType.Date,
                    ScoreReasons = { "format:thai-month-name" }
                });
            }
        }
        return results;
    }

    static bool TryBuildDate(int year, int month, int day, out DateTime d)
    {
        d = default;
        if (day < 1 || day > 31 || month < 1 || month > 12 || year < 1900 || year > 2100) return false;
        try { d = new DateTime(year, month, day); return true; }
        catch { return false; }
    }

    public static bool ValidateDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // === Amount ===

    static List<FieldCandidate> ExtractAmountCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Match number with optional thousands separators and 2-decimal point
        // Reject 5-digit no-decimal (likely postal code) and 13-digit (taxid)
        foreach (Match m in Regex.Matches(text, @"(?<!\d)([\d,]+\.\d{2})(?!\d)"))
        {
            var v = m.Groups[1].Value;
            var normalized = v.Replace(",", "");
            if (decimal.TryParse(normalized, out var amt) && amt > 0 && amt < 1_000_000_000)
            {
                var ctx = SafeSubstring(text, m.Index - 60, 60);
                double score = 0.5;
                if (Regex.IsMatch(ctx, @"รวม|Total|TOTAL|สุทธิ|Net", RegexOptions.IgnoreCase)) score = 1.0;
                else if (Regex.IsMatch(ctx, @"ภาษี|VAT", RegexOptions.IgnoreCase)) score = 0.85;
                else if (Regex.IsMatch(ctx, @"ก่อนภาษี|Sub", RegexOptions.IgnoreCase)) score = 0.85;
                results.Add(new FieldCandidate
                {
                    Value = v, NormalizedValue = normalized,
                    Position = m.Index, Length = m.Length,
                    Score = score,
                    FieldType = FieldType.Amount,
                    NumericValue = amt,
                    ScoreReasons = { $"context-score:{score:F2}" }
                });
            }
        }
        // Also: integer amounts with commas, e.g. "10,500"
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{1,3}(?:,\d{3})+)(?!\d|\.)"))
        {
            var v = m.Groups[1].Value;
            var normalized = v.Replace(",", "");
            if (decimal.TryParse(normalized, out var amt) && amt > 0 && amt < 1_000_000_000)
            {
                results.Add(new FieldCandidate
                {
                    Value = v, NormalizedValue = normalized,
                    Position = m.Index, Length = m.Length,
                    Score = 0.4,
                    NumericValue = amt,
                    FieldType = FieldType.Amount,
                    ScoreReasons = { "format:integer-with-commas" }
                });
            }
        }
        return results;
    }

    // === Company Name ===

    static List<FieldCandidate> ExtractCompanyNameCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        // Thai company name patterns
        var patterns = new[]
        {
            @"(บริษัท)\s+(.+?)\s*จำกัด\s*\(มหาชน\)",
            @"(บริษัท)\s+(.+?)\s*จำกัด",
            @"(ห้างหุ้นส่วนจำกัด|หจก\.?)\s+(.+?)(?=\s*(?:เลข|สาขา|ที่อยู่|\d{1}-?\d{4}|\n|$))",
            @"(ห้างหุ้นส่วนสามัญ)\s+(.+?)(?=\s*(?:เลข|สาขา|ที่อยู่|\n|$))",
            @"(ร้าน)\s+(.+?)(?=\s*(?:เลข|สาขา|ที่อยู่|\n|$))",
        };
        foreach (var p in patterns)
        {
            foreach (Match m in Regex.Matches(text, p))
            {
                var prefix = m.Groups[1].Value.Trim();
                var name = m.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 100) continue;
                var full = p.Contains("มหาชน") ? $"{prefix} {name} จำกัด (มหาชน)"
                         : p.Contains("จำกัด") ? $"{prefix} {name} จำกัด"
                         : $"{prefix} {name}";
                results.Add(new FieldCandidate
                {
                    Value = full, NormalizedValue = full,
                    Position = m.Index, Length = m.Length,
                    Score = 1.0,
                    FieldType = FieldType.CompanyName,
                    ScoreReasons = { "format:thai-legal-entity" }
                });
            }
        }
        // English: Co., Ltd / Public Co., Ltd
        foreach (Match m in Regex.Matches(text,
            @"([A-Z][A-Za-z0-9 &\.\-]+?)\s+(?:Co\.?\s*,?\s*Ltd\.?(?:\s*\(?Public\)?)?|Limited|PLC)",
            RegexOptions.IgnoreCase))
        {
            var name = m.Value.Trim();
            if (name.Length is < 5 or > 120) continue;
            results.Add(new FieldCandidate
            {
                Value = name, NormalizedValue = name,
                Position = m.Index, Length = m.Length,
                Score = 0.85,
                FieldType = FieldType.CompanyName,
                ScoreReasons = { "format:english-legal-entity" }
            });
        }
        return results;
    }

    // === Bank Account ===

    static List<FieldCandidate> ExtractBankAccountCandidates(string text)
    {
        var results = new List<FieldCandidate>();
        foreach (Match m in Regex.Matches(text,
            @"(?:เลขบัญชี|เลขที่บัญชี|Account\s*No\.?|A/C\s*No\.?)\s*[:：]?\s*(\d{1,3}[- \t]?\d{1,3}[- \t]?\d{1,4}[- \t]?\d{1,4})",
            RegexOptions.IgnoreCase))
        {
            var raw = m.Groups[1].Value;
            var normalized = Regex.Replace(raw, @"[-\s]", "");
            if (normalized.Length is >= 10 and <= 14)
            {
                results.Add(new FieldCandidate
                {
                    Value = raw, NormalizedValue = normalized,
                    Position = m.Index, Length = m.Length,
                    Score = 1.0,
                    FieldType = FieldType.BankAccount,
                    ScoreReasons = { "keyword:bank-account" }
                });
            }
        }
        return results;
    }

    // === Helpers ===

    static string SafeSubstring(string text, int start, int length)
    {
        start = Math.Max(0, start);
        length = Math.Min(length, text.Length - start);
        return length > 0 ? text.Substring(start, length) : "";
    }
}

/// <summary>One candidate value extracted for a field, with scoring/validation metadata.</summary>
public class FieldCandidate
{
    public string Value { get; set; } = "";
    public string NormalizedValue { get; set; } = "";
    public int Position { get; set; }
    public int Length { get; set; }
    public double Score { get; set; }
    public FieldPatternLibrary.FieldType FieldType { get; set; }
    public bool IsChecksumValid { get; set; }
    public decimal? NumericValue { get; set; }
    public List<string> ScoreReasons { get; set; } = new();

    public override string ToString() =>
        $"{FieldType}={NormalizedValue} score={Score:F2} reasons=[{string.Join(",", ScoreReasons)}]";
}
