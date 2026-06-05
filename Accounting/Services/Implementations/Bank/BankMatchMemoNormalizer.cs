using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// Shared helpers that turn a raw Thai-bank memo string into the
/// signals the 1:1 sweep and CalibrateConfidence reason about: account
/// tails, Thai TINs (13-digit), phone numbers, masked payer names, and
/// the title-stripped lowercase token set used for fuzzy contact match.
///
/// Why this is its own file: every Thai bank quotes these fields slightly
/// differently (KBank "จาก X3349 …", SCB "นาย …", KTB "Ref ABC123",
/// PromptPay "พร้อมเพย์ 081xxxxxxx") and previously each matcher rolled
/// its own ad-hoc extractor. Centralising removes drift and makes adding
/// a new bank's quirks a one-line change instead of N call-site edits.
/// </summary>
public static class BankMatchMemoNormalizer
{
    // ── Thai titles / legal-entity prefixes that destroy fuzzy match if
    // ── left in the contact name when comparing to a memo. "บจก. สมชาย"
    // ── vs memo "สมชาย" should match — but contains("สมชาย") works only
    // ── after the prefix is gone. Order doesn't matter — set lookup.
    private static readonly HashSet<string> _stripTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        // Personal honorifics
        "นาย", "นาง", "นางสาว", "น.ส.", "น.ส", "ดร.", "ดร", "พล.",
        "mr", "mr.", "mrs", "mrs.", "ms", "ms.", "miss", "dr", "dr.", "prof", "prof.",
        // Thai company legal-entity prefixes
        "บจก.", "บจก", "บมจ.", "บมจ", "บลจ.", "บลจ",
        "หจก.", "หจก", "หสน.", "หสน", "มจ.", "มจ",
        // English suffixes that aren't names but are part of company strings
        "co.", "co", "ltd.", "ltd", "company", "corporation", "corp.", "corp",
        "inc.", "inc", "plc.", "plc", "limited",
        // Connective words
        "บริษัท", "ห้างหุ้นส่วน", "จำกัด", "มหาชน",
    };

    // 13-digit Thai TIN, may appear bare or formatted "1-2345-67890-12-3".
    private static readonly Regex _tinRx =
        new(@"(?<!\d)([0-9](?:[-\s]?[0-9]){12})(?!\d)", RegexOptions.Compiled);

    // Thai mobile (08x/09x/06x) or landline (02x/0xx); accept dashes/space.
    private static readonly Regex _phoneRx =
        new(@"(?<!\d)(0(?:[689])[-\s]?[0-9](?:[-\s]?[0-9]){7,8}|0[2-7][-\s]?[0-9](?:[-\s]?[0-9]){6,7})(?!\d)",
            RegexOptions.Compiled);

    // Bank-account tail tokens that mean "last N digits of payer's account",
    // e.g. KBank's "X3349", SCB's "x-1234", KTB's "XXX3349".
    private static readonly Regex _acctTailRx =
        new(@"\bX{1,4}[-\s]?(\d{3,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Strip Thai/English titles and legal-entity prefixes from a
    /// contact name. "บจก. สมชาย โรเซิร์ฟ" → "สมชาย โรเซิร์ฟ" so the
    /// fuzzy contact-vs-memo check works when the bank memo dropped them.</summary>
    public static string StripTitles(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
        var tokens = s.Split(new[] { ' ', '\t', '.', ',', '(', ')' },
                             StringSplitOptions.RemoveEmptyEntries);
        var kept = tokens.Where(t => !_stripTitles.Contains(t.Trim('.', ',')));
        return string.Join(" ", kept).Trim();
    }

    /// <summary>Digits-only Thai TINs found in the memo (10 digits or more so
    /// short refs like an invoice number don't pollute the set).</summary>
    public static HashSet<string> ExtractTaxIds(string memo)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(memo)) return set;
        foreach (Match m in _tinRx.Matches(memo))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length >= 10) set.Add(digits);
        }
        return set;
    }

    /// <summary>Digits-only phone numbers in the memo (08/09/06 mobile or
    /// 02-07 landline). Used to cross-check a candidate contact's Phone.</summary>
    public static HashSet<string> ExtractPhones(string memo)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(memo)) return set;
        foreach (Match m in _phoneRx.Matches(memo))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length >= 9 && digits.Length <= 10) set.Add(digits);
        }
        return set;
    }

    /// <summary>Account-tail tokens like "X3349" / "xxx7634" → just the
    /// 3-5 trailing digits ("3349", "7634"). Cross-check the candidate
    /// contact's bank-account number's tail to confirm the payer.</summary>
    public static HashSet<string> ExtractAccountTails(string memo)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(memo)) return set;
        foreach (Match m in _acctTailRx.Matches(memo))
            if (m.Groups[1].Success) set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>True if any candidate phone matches a phone in the memo.
    /// Both sides are reduced to digits-only; we compare equality of the
    /// last 9 digits so a "+66 81 234 5678" memo matches a "0812345678"
    /// stored contact phone.</summary>
    public static bool PhoneInMemo(string? contactPhone, HashSet<string> memoPhones)
    {
        if (string.IsNullOrWhiteSpace(contactPhone) || memoPhones.Count == 0) return false;
        var digits = new string(contactPhone!.Where(char.IsDigit).ToArray());
        if (digits.Length < 9) return false;
        var tail9 = digits.Substring(digits.Length - 9);
        return memoPhones.Any(p => p.EndsWith(tail9) || tail9.EndsWith(p));
    }

    /// <summary>True if any account-tail in the memo matches the tail of
    /// the candidate contact's stored bank-account number.</summary>
    public static bool AccountTailInMemo(string? acct, HashSet<string> memoTails)
    {
        if (string.IsNullOrWhiteSpace(acct) || memoTails.Count == 0) return false;
        var digits = new string(acct!.Where(char.IsDigit).ToArray());
        if (digits.Length < 3) return false;
        var tail4 = digits.Substring(digits.Length - Math.Min(4, digits.Length));
        return memoTails.Any(t => t.EndsWith(tail4) || tail4.EndsWith(t));
    }
}
