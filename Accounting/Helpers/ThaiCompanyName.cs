using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ชื่อนิติบุคคลไทยบนกระดาษ (คำนำหน้า + ชื่อ + คำท้าย "จำกัด") — ตัวตั้งตัวเดียวของตัวอ่านชื่อจากข้อความ** (ทีม OCR 13 · 2026-10-16 · ฝ่ายค้าน OCR 10 P1-5)
///
/// <para>═══ ที่มา ═══ regex ชื่อบริษัท + ตรรกะต่อคำท้าย " จำกัด" เคยมี <b>6 สำเนา</b> (<c>SmartFieldExtractor.ExtractCompanyNames</c> ·
/// <c>SmartFieldExtractor.TryExtractVendorNameFromRawText</c> · <c>OcrService.ParseThaiDocument</c> · <c>DocumentZoneAnalyzer.ExtractCompanyName</c> ·
/// <c>DocumentZoneAnalyzer.ExtractCompanyNameFromMatch</c> · <c>FieldPatternLibrary.ExtractCompanyNameCandidates</c>) · ทีม OCR 10 แก้ "ห้างหุ้นส่วนจำกัด X จำกัด"
/// (หาคำว่าจำกัดตั้งแต่<b>คำนำหน้า</b> — คำนำหน้าเองมีคำว่าจำกัด) ได้สองสำเนา อีกสี่สำเนายังออกชื่อผิด (F2 #1/#4) · และสำเนาใน
/// <c>TryExtractVendorNameFromRawText</c> อ่านต่อถึงท้ายบรรทัด ⇒ e-Tax TXE05202609T004679 ได้ผู้ขาย "หจก. แอมแฮปปี้เนสเลขที่/ No. TXE05202609T004679"
/// (ชื่อเรา + เลขที่เอกสารคอลัมน์ขวา)</para>
///
/// <para>═══ กติกา ═══ คำนำหน้า (บริษัท · ห้างหุ้นส่วนจำกัด/สามัญ · หจก. · บจก. · บมจ. · ร้าน) → ชื่อ (สั้นสุด) → หยุดที่ "จำกัด (มหาชน)" · วงเล็บ ·
/// ป้ายถัดไป (เลข… · สาขา · ที่อยู่) · ตัวเลขรูปเลขผู้เสียภาษี · <b>ตัวคั่นคอลัมน์</b> (ช่องว่าง ≥ 2 ของข้อความ PDF ตามพิกัด — ข้อความที่ normalize แล้วไม่มี) ·
/// ท้ายบรรทัด · วงเล็บที่เป็นส่วนของชื่อจดทะเบียน ("(ประเทศไทย)" · "(Thailand)" · "(ไทยแลนด์)" · "(มหาชน)") ไม่ใช่จุดหยุด ·
/// คำท้าย " จำกัด" ต่อเมื่อคำว่าจำกัดอยู่<b>หลังชื่อ</b>ภายใน 30 ตัวอักษรหลังจุดจบ <b>บนช่องเดียวกัน</b> (หยุดที่ tab/ขึ้นบรรทัด) · ห้างหุ้นส่วนไม่ต่อคำท้ายเลย (ดู <see cref="Suffix"/>)</para>
///
/// <para>ห้ามเขียน regex ชื่อบริษัทหรือตรรกะคำท้ายที่อื่น (<c>tools/required_call_site_check.py</c> ล็อกผู้เรียกทุกตัว + ห้ามสำเนา)</para>
/// </summary>
public static class ThaiCompanyName
{
    /// <summary>กลุ่ม 1 = คำนำหน้า · กลุ่ม 2 = ชื่อ — ใช้ผ่าน <see cref="FindAll"/> / <see cref="First"/> เท่านั้น</summary>
    private static readonly Regex Name = new(
        @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|หจก\.?|บจก\.?|บมจ\.?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\((?!\s*(?:" + NameParentheses + @")\s*\))|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{1}[- \t]?\d{4}| {2,}|\t)|$)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>วงเล็บที่เป็น<b>ส่วนหนึ่งของชื่อจดทะเบียน</b> — "บริษัท โตโยต้า มอเตอร์ (ประเทศไทย) จำกัด" · "(Thailand)" · "(ไทยแลนด์)" · "(มหาชน)" ก่อนคำว่าจำกัด
    /// (ทีม OCR 15 · ฝ่ายค้าน OCR 13 P2: เดิมชื่อหยุดที่ "(" ⇒ "บริษัท โตโยต้า มอเตอร์ จำกัด" — ชื่อไม่ตรงทะเบียน · pattern เดิมของ FieldPatternLibrary
    /// <c>บริษัท\s+(.+?)\s*จำกัด</c> เก็บวงเล็บไว้ได้) · วงเล็บอื่น ("(สำนักงานใหญ่)" · "(สาขาศรีราชา)") ยังเป็นจุดหยุดของชื่อเหมือนเดิม</summary>
    private const string NameParentheses = "ประเทศไทย|ไทยแลนด์|Thailand|มหาชน";

    /// <summary>ระยะหลังจุดจบของชื่อที่ยอมให้คำว่า "จำกัด" อยู่ (ตัวอักษร)</summary>
    private const int SuffixWindow = 30;

    /// <summary>ชื่อนิติบุคคลทุกชื่อบนข้อความ ตามลำดับบนหน้า — ชื่อเต็ม (คำนำหน้า + ชื่อ + คำท้าย) และตำแหน่งคำนำหน้า</summary>
    public static List<(string FullName, int Position)> FindAll(string? text)
    {
        var results = new List<(string, int)>();
        if (string.IsNullOrEmpty(text)) return results;
        foreach (Match m in Name.Matches(text))
            if (Compose(m, text) is { } full) results.Add((full, m.Index));
        return results;
    }

    /// <summary>ชื่อแรกบนข้อความ · null = ไม่มี</summary>
    public static (string FullName, int Position)? First(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Match m in Name.Matches(text))
            if (Compose(m, text) is { } full) return (full, m.Index);
        return null;
    }

    /// <summary>คำที่ขึ้นต้น "ชื่อ" หลังคำว่าบริษัทแล้วแปลว่า<b>เป็นประโยค ไม่ใช่ชื่อ</b> — "บริษัทฯ จะถือว่า…" · "บริษัทขอสงวนสิทธิ์…" · "บริษัทจะไม่รับผิดชอบ…"
    /// (เงื่อนไขท้ายใบกำกับ CRC ไทวัสดุ / e-Tax Scommerce · ทีม OCR 13) · รายการปิด — ไม่ใส่คำสั้นที่ขึ้นต้นชื่อจริงได้ ("มี" ⇒ มีเดีย · "ขอ" ⇒ ขอนแก่น)</summary>
    private static readonly string[] ProseStarts =
    {
        "ฯ", "ขอสงวน", "ขอขอบคุณ", "ขอรับรอง", "ขอแสดง", "จะไม่", "จะถือ", "จะนำ", "จะทำ", "จะดำเนิน", "จะคืน",
        "ไม่รับ", "ไม่สามารถ", "ไม่มี", "ได้รับ", "ได้จัดทำ", "มีสิทธิ",
        // ทีม OCR 15 (ฝ่ายค้าน OCR 13 P2): เดิม "ยินดี" เปล่า ⇒ "บริษัท ยินดีมีสุข จำกัด" (ชื่อจริง) ถูกทิ้ง ⇒ ผู้ขายว่าง · เหลือเฉพาะวลีประโยค
        "ยินดีให้", "ยินดีต้อนรับ", "ยินดีคืน",
        // "…เจ้าหน้าที่บริษัทแต่อย่างใด" (CRC) · "(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)" (แบบ 50 ทวิ)
        "แต่อย่างใด", "สมาคม",
    };

    private static string? Compose(Match m, string text)
    {
        var prefix = m.Groups[1].Value;
        var name = m.Groups[2].Value.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return null;
        if (prefix == "บริษัท" && ProseStarts.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return null;
        var suffix = Suffix(text, prefix, m.Groups[2].Index + m.Groups[2].Length, m.Index + m.Length);
        return $"{prefix} {name}{suffix}".Trim();
    }

    /// <summary>ตัวจบหน้าต่างคำท้าย — ตัวคั่นคอลัมน์ (<see cref="PdfPositionalText.ColumnSeparator"/>) และขึ้นบรรทัด</summary>
    private static readonly char[] CellBreaks = { '\t', '\n' };

    /// <summary>คำนำหน้าห้างหุ้นส่วน (หจก. · ห้างหุ้นส่วนจำกัด/สามัญ) — คำนำหน้ามีคำว่าจำกัดอยู่แล้ว ไม่ต่อคำท้าย</summary>
    private static bool IsPartnership(string prefix)
        => prefix.StartsWith("ห้างหุ้นส่วน", StringComparison.Ordinal) || prefix.StartsWith("หจก", StringComparison.Ordinal);

    /// <summary>คำท้ายของชื่อ — " จำกัด (มหาชน)" / " จำกัด" / ""
    /// <list type="number">
    /// <item>คำนำหน้าห้างหุ้นส่วน (หจก. · ห้างหุ้นส่วนจำกัด/สามัญ) ⇒ "" เสมอ — คำนำหน้ามีคำว่าจำกัดอยู่แล้ว (ทีม OCR 15 · ฝ่ายค้าน OCR 13 P1-1:
    /// ใบ BS2026100001 "ในนาม หจก.แอม แฮปปี้เนส⇥ในนาม บริษัท บุญทรัพย์ ถาวร จำกัด" ⇒ เดิม "หจก. แอมแฮปปี้เนส จำกัด")</item>
    /// <item>ตัวจบของ regex เองมีคำว่าจำกัด ("…X จำกัด" · "…X\nจำกัด (มหาชน)") ⇒ ตามนั้น</item>
    /// <item>ไม่มี ⇒ หาต่อจากจุดจบไม่เกิน <see cref="SuffixWindow"/> ตัว แต่<b>หยุดที่ tab และขึ้นบรรทัด</b> — คำว่าจำกัดในคอลัมน์ขวา/บรรทัดถัดไปเป็นของชื่ออื่น
    /// (เดิม window ข้าม tab ⇒ "ร้าน X⇥บริษัท Y จำกัด" ⇒ "ร้าน X จำกัด")</item>
    /// </list></summary>
    internal static string Suffix(string text, string prefix, int nameEnd, int matchEnd)
    {
        if (IsPartnership(prefix)) return "";
        if (nameEnd < matchEnd)
        {
            var terminator = text.Substring(nameEnd, matchEnd - nameEnd);
            if (terminator.Contains("จำกัด", StringComparison.Ordinal))
                return terminator.Contains("มหาชน", StringComparison.Ordinal) ? " จำกัด (มหาชน)" : " จำกัด";
        }
        var end = Math.Min(matchEnd + SuffixWindow, text.Length);
        if (matchEnd >= end) return "";
        var after = text.Substring(matchEnd, end - matchEnd);
        var stop = after.IndexOfAny(CellBreaks);
        if (stop >= 0) after = after[..stop];
        if (!after.Contains("จำกัด", StringComparison.Ordinal)) return "";
        return after.Contains("มหาชน", StringComparison.Ordinal) ? " จำกัด (มหาชน)" : " จำกัด";
    }
}
