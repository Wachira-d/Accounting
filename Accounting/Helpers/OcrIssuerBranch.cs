using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ประโยคบนกระดาษที่<b>ประกาศตรง ๆ</b> ว่าใบนี้ออกโดยสาขาไหน</summary>
/// <param name="Code">รหัสสาขา 5 หลักตามประกาศอธิบดีฯ 199 (เช่น <c>00008</c>)</param>
/// <param name="Evidence">ข้อความที่พบบนกระดาษ (ไว้โชว์/ลง ReasoningTrace)</param>
/// <param name="Address">ที่อยู่ของสาขาที่ออกใบ ที่พิมพ์ต่อจากประโยคนั้น — <c>null</c> เมื่อหาไม่ได้
/// (ภาษาไทยก่อน เมื่อกระดาษพิมพ์ทั้งสองภาษา)</param>
public sealed record OcrIssuerBranchStatement(string Code, string Evidence, string? Address);

/// <summary>ประโยคประกาศสาขาทำอะไรกับรหัสสาขาผู้ขายที่ถืออยู่</summary>
public enum OcrIssuerBranchAction
{
    /// <summary>รหัสที่ถืออยู่ตรงกับประโยคแล้ว — ไม่ต้องแก้</summary>
    Keep = 0,
    /// <summary>ถืออยู่ว่าง/00000 (มักได้จากคำว่า “สำนักงานใหญ่” บนหัวกระดาษ) — แทนด้วยรหัสจากประโยค</summary>
    Replace = 1,
    /// <summary>ถืออยู่เป็นสาขาอื่นที่ไม่ใช่ 00000 — ขัดกัน ไม่แทน ให้ไฮไลต์ (G4)</summary>
    Conflict = 2,
}

/// <summary>
/// **“สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8” — หลักฐานชั้นสูงสุดเรื่องสาขาผู้ออกใบ**
///
/// <para>═══ ที่มา (สแกนจริง 2026-09-24 · ใบ B Radisson Hua Hin) ═══ หัวกระดาษสองภาษาพิมพ์
/// ทั้ง “Head Office / สำนักงานใหญ่” (ที่อยู่จดทะเบียนที่นนทบุรี) <b>และ</b>
/// “Branch Tax Invoice is Issued no. 8 / สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8” (ที่ชะอำ) ·
/// ระบบได้สาขาผู้ขาย = 00000 และที่อยู่ผู้ขาย = ที่อยู่สำนักงานใหญ่ปนอังกฤษ+ไทย ทั้งที่ผู้ออกใบคือ
/// สาขาที่ 8 ⇒ รายงานภาษีซื้อ §87 ผูกผิดสถานประกอบการ + Contact ที่สร้างผิดสาขา</para>
///
/// <para>═══ ลำดับชั้น (DECISION_DOCTRINE G1/G2) ═══ ประโยคนี้เป็น<b>ข้อเท็จจริงที่มีป้ายกำกับ
/// บนกระดาษ</b> ซึ่งบอก “บทบาท” ของรหัส (ผู้ออกใบ) ตรง ๆ ⇒ ชนะคำว่า “สำนักงานใหญ่/Head Office”
/// ที่อยู่ในหัวกระดาษเดียวกัน (คำนั้นบอกแค่ว่าบริษัทมีสำนักงานใหญ่ ไม่ได้บอกว่าใครออกใบ) และชนะ
/// “สาขาที่ N” เปล่า ๆ (ซึ่งอาจเป็นรายชื่อสาขาอื่นบนหัวกระดาษ) · <b>ไม่เดา</b>: ไม่พบประโยค = คืน
/// <c>null</c> ให้ตัวอ่านสาขาทั่วไป (<c>BranchCodeExtractor</c>) ทำงานตามเดิม</para>
///
/// <para>☑ “สำนักงานใหญ่” ในบล็อกผู้ซื้อ (ช่องติ๊ก) เป็นของ<b>ผู้ซื้อ</b> — ตัวนี้อ่านเฉพาะประโยค
/// “ออกโดย/ออกใบกำกับ” จึงไม่ไปปนกับช่องติ๊กนั้น</para>
/// </summary>
public static class OcrIssuerBranch
{
    // หมายเหตุ regex: ตัวคั่นทุกตัวเป็น [ \t] ไม่ใช่ \s — ประโยคประกาศสาขาอยู่ในบรรทัดเดียวเสมอ
    // และ \s ครอบ \n ⇒ เลขต้นบรรทัดถัดไป (เลขบ้าน “854/2”) จะถูกกลืนเป็นรหัสสาขา
    // (บทเรียน regex_line_span_check)

    /// <summary>ไทย: “สาขาที่ออกใบกำกับภาษี(คือ) สาขาที่ 8” · “สาขาที่ออกใบกำกับภาษี 00008” ·
    /// “ออกโดยสาขาที่ 3” · “ออกใบกำกับภาษีโดย สาขาที่ 3” — “ที่” ยอมตกวรรณยุกต์เป็น “ที”
    /// (OCR ไทยทำหล่นบ่อย)</summary>
    private static readonly Regex ThaiStatementRx = new(
        @"(?:สาขา(?:ที่|ที)?[ \t]*(?:ผู้)?ออก(?:ใบกำกับ(?:ภาษี)?|ใบเสร็จ(?:รับเงิน)?|เอกสาร)?[ \t]*(?:นี้)?[ \t]*(?:คือ|ได้แก่|:|：)?[ \t]*(?:สาขา(?:ที่|ที)?[ \t]*)?(?:เลขที่[ \t]*)?"
        + @"|ออก(?:ใบกำกับ(?:ภาษี)?)?[ \t]*(?:โดย|ที่|ณ)[ \t]*สาขา(?:ที่|ที)?[ \t]*(?:เลขที่[ \t]*)?)"
        + @"(\d{1,5})(?![\d/])",
        RegexOptions.Compiled);

    /// <summary>อังกฤษ: “Branch Tax Invoice is Issued no. 8” · “Tax invoice issued by branch no. 3” ·
    /// “Issuing Branch: 00003” · “Issued at Branch 00005”</summary>
    private static readonly Regex EnglishStatementRx = new(
        @"(?:branch[ \t]+(?:tax[ \t]+invoice[ \t]+)?(?:is[ \t]+)?issued(?:[ \t]+(?:at|by|from))?"
        + @"|issu(?:ed|ing)[ \t]+(?:at[ \t]+|by[ \t]+|from[ \t]+)?branch)"
        + @"[ \t]*(?:no\.?|number|#)?[ \t]*[:：.]?[ \t]*(\d{1,5})(?![\d/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ท้ายบรรทัดที่เป็นเบอร์โทร/แฟกซ์/อีเมล — ตัดทิ้งจากที่อยู่</summary>
    private static readonly Regex ContactTailRx = new(
        @"(?:\bTel\b|\bFax\b|\bE-?mail\b|โทร|แฟกซ์|โทรสาร|อีเมล)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ต้นบรรทัดที่อยู่: เลขบ้าน “854/2” / “99” หรือ “เลขที่ 99”</summary>
    private static readonly Regex AddressStartRx = new(
        @"^(?:เลขที่[ \t]*)?\d{1,5}(?:/\d{1,5})?(?![\d])",
        RegexOptions.Compiled);

    /// <summary>ที่อยู่ “ครบ” ต้องมีจังหวัด/รหัสไปรษณีย์ (กติกาเดียวกับ <c>OcrPartyAddress.SplitGlued</c>)</summary>
    private static readonly Regex AddressEndRx = new(
        @"(จ\.|จังหวัด|กรุงเทพ|กทม\.?|(?<!\d)\d{5}(?!\d))",
        RegexOptions.Compiled);

    /// <summary>หาประโยคประกาศสาขาผู้ออกใบบนกระดาษ — คืน <c>null</c> เมื่อไม่พบ หรือพบแต่รหัส
    /// <b>ขัดกันเอง</b> (ไทยบอก 8 อังกฤษบอก 9 = ไม่รู้ · G3 ห้ามเลือกเอง)</summary>
    public static OcrIssuerBranchStatement? Detect(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var text = rawText.Replace("\r", "");

        var hits = new List<(string Code, string Evidence, string? Address)>();
        foreach (var rx in new[] { ThaiStatementRx, EnglishStatementRx })
            foreach (Match m in rx.Matches(text))
            {
                var code = m.Groups[1].Value.PadLeft(5, '0');
                if (!code.All(c => c is >= '0' and <= '9')) continue;   // เลขไทย ๘ ฯลฯ — ไม่เดาแปลง
                hits.Add((code, m.Value.Trim(), AddressAfter(text, m.Index + m.Length)));
            }
        if (hits.Count == 0) return null;
        if (hits.Select(h => h.Code).Distinct().Count() > 1) return null;

        // ที่อยู่: ภาษาไทยก่อน (เอกสารภาษีไทย · ทะเบียน RD/DBD เป็นไทย) — ไม่มีไทยจึงใช้อังกฤษ
        var addrs = hits.Select(h => h.Address).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var address = addrs.FirstOrDefault(IsMostlyThai) ?? addrs.FirstOrDefault();
        return new OcrIssuerBranchStatement(hits[0].Code, hits[0].Evidence, address);
    }

    /// <summary>ประโยคประกาศสาขาควรทำอะไรกับรหัสสาขาผู้ขายที่ถืออยู่ + ควรใช้ที่อยู่ของสาขานั้นไหม
    ///
    /// <para>ประโยคประกาศ (หลักฐานมีป้ายกำกับ · G1) ชนะ <c>00000</c>/ว่าง — ค่านั้นมักมาจากคำว่า
    /// “สำนักงานใหญ่” บนหัวกระดาษเดียวกัน ซึ่งตัวเติมแบบ “เติมเฉพาะช่องว่าง” ซ่อมไม่ได้ (บทเรียน §H)
    /// · <b>ไม่ทับ</b>รหัสสาขาอื่นที่ไม่ใช่ 00000 (ขัดกัน = <see cref="OcrIssuerBranchAction.Conflict"/>)
    /// · ใช้ที่อยู่เฉพาะเมื่อรหัสสุดท้ายตรงกับประโยค และประโยคไม่ได้บอกสำนักงานใหญ่</para></summary>
    public static (OcrIssuerBranchAction Action, bool UseAddress) Reconcile(
        string? currentBranch, OcrIssuerBranchStatement statement)
    {
        var action = string.IsNullOrWhiteSpace(currentBranch) || TaxBranchCode.IsHeadOffice(currentBranch)
            ? (string.Equals(currentBranch?.Trim(), statement.Code, StringComparison.Ordinal)
                ? OcrIssuerBranchAction.Keep : OcrIssuerBranchAction.Replace)
            : (TaxBranchCode.Normalize(currentBranch) == statement.Code
                ? OcrIssuerBranchAction.Keep : OcrIssuerBranchAction.Conflict);
        var finalBranch = action == OcrIssuerBranchAction.Replace ? statement.Code : currentBranch;
        var useAddress = statement.Address != null
            && !TaxBranchCode.IsHeadOffice(statement.Code)
            && TaxBranchCode.Normalize(finalBranch) == statement.Code;
        return (action, useAddress);
    }

    /// <summary>ที่อยู่ที่พิมพ์<b>ต่อจาก</b>ประโยคประกาศสาขา: ส่วนที่เหลือของบรรทัดเดียวกันก่อน
    /// ถ้าไม่ใช่ที่อยู่ค่อยดูบรรทัดถัดไป (บรรทัดเดียว) · ต้องขึ้นต้นด้วยเลขบ้านและจบด้วย
    /// จังหวัด/รหัสไปรษณีย์ — ไม่ครบ = ไม่ใช่ที่อยู่ คืน null (ไม่แต่ง)</summary>
    private static string? AddressAfter(string text, int from)
    {
        if (from >= text.Length) return null;
        var eol = text.IndexOf('\n', from);
        var rest = (eol < 0 ? text[from..] : text[from..eol]);
        var sameLine = CleanAddress(rest);
        if (sameLine != null) return sameLine;
        if (eol < 0) return null;
        var nextEnd = text.IndexOf('\n', eol + 1);
        var next = nextEnd < 0 ? text[(eol + 1)..] : text[(eol + 1)..nextEnd];
        return CleanAddress(next);
    }

    private static string? CleanAddress(string s)
    {
        var t = s.Trim(' ', '\t', ',', ':', '：', '-');
        var tail = ContactTailRx.Match(t);
        if (tail.Success) t = t[..tail.Index];
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[ \t]{2,}", " ").Trim(' ', '\t', ',', '-');
        if (t.Length < 10) return null;
        if (!AddressStartRx.IsMatch(t)) return null;
        if (!AddressEndRx.IsMatch(t)) return null;
        return t;
    }

    private static bool IsMostlyThai(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int thai = 0, latin = 0;
        foreach (var c in s)
        {
            if (c >= '฀' && c <= '๿') thai++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }
        return thai > latin;
    }

    /// <summary>
    /// <b>ที่อยู่ที่ควรเขียนลง Contact ของผู้ขาย</b> — ทะเบียน (DBD/RD) เก็บที่ตั้ง<b>สำนักงานใหญ่</b>
    /// เท่านั้น ⇒ ห้ามเอาไปใส่ Contact ของสาขาอื่น และห้ามเอาที่อยู่สาขาจากกระดาษไปใส่ Contact
    /// สำนักงานใหญ่
    ///
    /// <para>═══ ที่มา (ใบ B · เจ้าของข้อ 5) ═══ “ใบ B เป็นสาขาที่ 8 ต้องดึงเลขสาขาไปสร้างข้อมูลให้ถูก”
    /// — เดิม Contact ที่ OCR สร้าง/เติม ใช้ “DBD ก่อนเสมอ” (<c>EnrichContactAddress</c>) ⇒ Contact
    /// สาขาที่ 8 ได้ที่อยู่นนทบุรี (สำนักงานใหญ่) ซึ่งเป็นที่อยู่ที่ใบกำกับของสาขานั้นไม่เคยพิมพ์</para>
    ///
    /// <para>กติกา: Contact สำนักงานใหญ่ → DBD ก่อน (พฤติกรรมเดิม) · กระดาษได้เฉพาะเมื่อใบนี้เป็นของ
    /// สำนักงานใหญ่/ไม่รู้สาขา (ที่อยู่ของสาขาอื่นห้ามไปทับ Contact สำนักงานใหญ่) · Contact สาขา N →
    /// ที่อยู่ที่กระดาษประกาศไว้ตามประโยคสาขาผู้ออกใบก่อน เมื่อใบนี้ออกโดยสาขา N · ไม่มีจึงถอยไปใช้ทะเบียน
    /// (= ที่อยู่ของนิติบุคคล ซึ่งยังใช้ได้กับ 50 ทวิ — พฤติกรรมเดิม ไม่ปล่อยว่างให้แย่ลง ·
    /// คำถามเจ้าของ Q-P3 ในรายงานทีม P ว่าควรปล่อยว่างแทนไหม)</para>
    /// </summary>
    /// <param name="contactBranch">รหัสสาขาของ Contact ที่จะเขียน (ว่าง = สำนักงานใหญ่)</param>
    /// <param name="scanBranch">รหัสสาขาผู้ขายที่อ่านได้จากใบนี้ (ว่าง = ไม่รู้)</param>
    /// <param name="dbdAddress">ที่อยู่จากทะเบียน — ใช้ได้เฉพาะเมื่อ <paramref name="dbdMatched"/></param>
    /// <param name="paperAddress">ที่อยู่ผู้ขายที่อ่านจากใบนี้</param>
    /// <param name="paperIsIssuerBranchAddress">ที่อยู่จากกระดาษมาจาก<b>ประโยคประกาศสาขาผู้ออกใบ</b>
    /// (<see cref="Detect"/>) — ที่อยู่ที่ engine อ่านเองอาจเป็นที่อยู่สำนักงานใหญ่ปนสองภาษา (ใบ B)
    /// จึงไม่ชนะทะเบียน</param>
    /// <returns><c>(Address, FromRegistry)</c> — <c>Address = null</c> = ไม่ต้องเขียนที่อยู่</returns>
    public static (string? Address, bool FromRegistry) ContactAddress(
        string? contactBranch, string? scanBranch, bool dbdMatched, string? dbdAddress, string? paperAddress,
        bool paperIsIssuerBranchAddress)
    {
        var hasDbd = dbdMatched && !string.IsNullOrWhiteSpace(dbdAddress);
        var paper = string.IsNullOrWhiteSpace(paperAddress) ? null : paperAddress;
        var scanKnown = !string.IsNullOrWhiteSpace(scanBranch);
        var scanIsHq = !scanKnown || TaxBranchCode.IsHeadOffice(scanBranch);

        if (TaxBranchCode.IsHeadOffice(contactBranch))
        {
            if (hasDbd) return (dbdAddress, true);
            return scanIsHq ? (paper, false) : (null, false);
        }
        // Contact ของสาขา — ที่อยู่ที่ใบของสาขานั้นพิมพ์เองชนะทะเบียน (ทะเบียน = สำนักงานใหญ่)
        var sameBranch = scanKnown
            && TaxBranchCode.Normalize(scanBranch) == TaxBranchCode.Normalize(contactBranch);
        if (sameBranch && paper != null && paperIsIssuerBranchAddress) return (paper, false);
        if (hasDbd) return (dbdAddress, true);
        return sameBranch ? (paper, false) : (null, false);
    }
}
