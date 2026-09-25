using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ชื่อนิติบุคคลที่<b>พิมพ์อยู่บนกระดาษ</b>เหนือเลขผู้เสียภาษีของผู้ขาย</summary>
/// <param name="Name">ชื่อ (ตัดป้ายสาขา/เบอร์/ป้ายเลขภาษีท้ายบรรทัดออกแล้ว)</param>
/// <param name="Position">ตำแหน่งต้นบรรทัดในข้อความ</param>
/// <param name="Evidence">เหตุผลสำหรับ trace</param>
public sealed record OcrPrintedLegalName(string Name, int Position, string Evidence);

/// <summary>
/// **ชื่อผู้ขายเป็น "โลโก้/แบรนด์" แต่กระดาษพิมพ์ชื่อนิติบุคคลไว้ชัด — ใช้ชื่อที่พิมพ์** (รอบ 197 ทีม K · ใบ Makro 3/3)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-25) ═══ ใบ Makro หัวซ้ายพิมพ์ "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน) · สำนักงานใหญ่ โทร. …
/// · เลขประจำตัวผู้เสียภาษีอากร 0 10 7 567 00041 4" และมีโลโก้ "makro" ตัวใหญ่ · engine หยิบโลโก้ที่อ่านแตกเป็นชื่อผู้ขาย
/// <b>"ma ro"</b> · ทุกชั้นถัดไปไม่ซ่อม: <c>OcrPartyName.ExpandTruncated</c> ขยายได้เฉพาะ superstring ("ma ro" ไม่ใช่ส่วนหนึ่งของชื่อไทย) ·
/// <c>VendorKnownGoodCorrector</c> แทนได้เฉพาะค่าที่<b>คล้าย</b> ≥ 0.80 · และทะเบียน (<c>DbdIdentityGuard</c>) ตัดสินว่า "คนละบริษัท"
/// เพราะกุญแจพิสูจน์ไม่ได้ (เลขพิมพ์แบ่งกลุ่ม 1-2-1-3-5-1 ซึ่ง <see cref="ThaiTaxId.Pattern"/> ไม่รู้จัก — แก้คู่กันใน
/// <see cref="ThaiTaxId.LooseGroupingPattern"/>) ⇒ ชื่อโลโก้ค้างเป็นชื่อผู้ขาย แล้วลาม: หน้าตรวจ "จับคู่ผู้ติดต่อ: ma ro"</para>
///
/// <para>═══ กติกา (ทิศปลอดภัย · DOCTRINE G1) ═══ แทนได้เฉพาะเมื่อครบทุกข้อ:
/// <list type="number">
/// <item>ชื่อปัจจุบัน<b>ไม่มีรูปนิติบุคคล</b> (<see cref="LacksLegalForm"/>) — ชื่อที่เป็นนิติบุคคลอยู่แล้วไม่ถูกแตะเลย</item>
/// <item>เลขผู้ขายเป็นเลข<b>นิติบุคคล</b> 13 หลักที่ผ่าน checksum (ขึ้นต้น 0) — ผู้ขายบุคคลธรรมดาใช้ชื่อร้านได้ตามปกติ</item>
/// <item>รู้ตำแหน่งเลขผู้ขายบนกระดาษ และเลขนั้น<b>ไม่อยู่ในบล็อกผู้ซื้อ</b></item>
/// <item>บรรทัดชื่อนิติบุคคลอยู่<b>เหนือ</b>เลข (≤ <see cref="MaxLinesAbove"/> บรรทัด · หรือหน้าป้ายเลขในบรรทัดเดียวกัน) ·
///   ไม่อยู่ในบล็อกผู้ซื้อ · ไม่ใช่ชื่อบริษัทเรา</item>
/// </list>
/// ไม่ครบ = คืน null (ชั้นถัดไป — ทะเบียน/ประวัติผู้ขาย/คน — ตัดสิน) · ห้ามเดาชื่อจากบรรทัดใต้เลขหรือไกลกว่านั้น</para>
/// </summary>
public static class OcrVendorLegalName
{
    /// <summary>คะแนนของชื่อที่ได้จากบรรทัดพิมพ์ — เท่าเกณฑ์ไม่ไฮไลต์พอดี (ป้ายเลขภาษีกำกับ + มีรูปนิติบุคคล)</summary>
    public const double PrintedConfidence = 0.85;

    /// <summary>คะแนนของชื่อที่ได้จากผู้ติดต่อ/คลังที่รู้จัก (เลขเดียวกัน) — ต่ำกว่าเกณฑ์ ⇒ ไฮไลต์ให้ตรวจ</summary>
    public const double KnownNameConfidence = 0.70;

    /// <summary>มองขึ้นไปกี่บรรทัดจากบรรทัดเลขผู้เสียภาษี (หัวใบไทย: ชื่อ → ที่อยู่/สำนักงานใหญ่ โทร → เลขภาษี · ใบ Makro มีโลโก้ +
    /// กล่อง "ต้นฉบับลูกค้า / For Customer" แทรกระหว่างชื่อกับเลขอีก 3 บรรทัดเมื่อ engine อ่านสองคอลัมน์สลับกัน) · หยุดที่บรรทัด
    /// ชื่อนิติบุคคลบรรทัดแรกเสมอ ไม่ข้ามไปหาบรรทัดที่ไกลกว่า</summary>
    public const int MaxLinesAbove = 6;

    /// <summary>คำบอกรูปนิติบุคคล (ไทย/อังกฤษ) — มีคำใดคำหนึ่ง = ชื่อนิติบุคคล</summary>
    private static readonly Regex LegalForm = new(
        @"บริษัท|บจก|บ\.จ\.ก|บมจ|หจก|ห้างหุ้นส่วน|จำกัด|จํากัด|มหาชน"
        + @"|\b(?:co\.?\s*,?\s*ltd|company\s+limited|limited|ltd|pcl|plc|public\s+company|inc|corp(?:oration)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ส่วนท้ายบรรทัดที่ไม่ใช่ชื่อ: ป้ายเลขภาษี · โทร · สำนักงานใหญ่/สาขา · Tax ID</summary>
    private static readonly Regex TailNoise = new(
        @"(?:เลขประจำตัว|เลขประจําตัว|เลขผู้เสียภาษี|ผู้เสียภาษี|\bTax\s*ID\b|โทร|\bTel\b|แฟกซ์|\bFax\b|สำนักงานใหญ่|\bHead\s*Office\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ชื่อนี้ไม่มีรูปนิติบุคคลเลย (ว่าง/โลโก้/แบรนด์/ชื่อร้าน) — ตัวเดียวที่ตัดสินว่า "แทนได้"</summary>
    public static bool LacksLegalForm(string? name)
        => string.IsNullOrWhiteSpace(name) || !LegalForm.IsMatch(name);

    /// <summary>เลขนิติบุคคลไทย 13 หลัก (ขึ้นต้น 0 · checksum ผ่าน)</summary>
    public static bool IsJuristicTaxId(string? taxId)
    {
        var d = ThaiTaxId.Normalize(taxId);
        return d.Length == 13 && d[0] == '0' && ThaiTaxId.IsValid(d);
    }

    /// <summary>หาชื่อนิติบุคคลที่พิมพ์เหนือเลขผู้ขาย — null เมื่อไม่ครบกติกา (ดูหัวคลาส)</summary>
    /// <param name="rawText">ข้อความทั้งหน้า (ก้อนเดียวกับที่ใช้หาตำแหน่งเลข/ป้าย)</param>
    /// <param name="vendorTaxId">เลขผู้ขาย</param>
    /// <param name="taxIdPosition">ตำแหน่งเลขผู้ขายในข้อความ (-1 = ไม่รู้ ⇒ null)</param>
    /// <param name="buyerLabelPositions">ตำแหน่งป้ายฝั่งผู้ซื้อทุกตัว (<see cref="OcrPartyLabels.FindAll"/>)</param>
    /// <param name="sellerLabelPositions">ตำแหน่งป้ายฝั่งผู้ขายทุกตัว</param>
    /// <param name="ourNames">ชื่อบริษัทเรา (ไทย/อังกฤษ) — บรรทัดที่เป็นชื่อเราไม่ใช่ผู้ขาย</param>
    public static OcrPrintedLegalName? FindAboveTaxId(string? rawText, string? vendorTaxId, int taxIdPosition,
        IReadOnlyList<int>? buyerLabelPositions, IReadOnlyList<int>? sellerLabelPositions, IEnumerable<string?>? ourNames)
    {
        if (string.IsNullOrEmpty(rawText) || taxIdPosition < 0 || taxIdPosition >= rawText.Length) return null;
        if (!IsJuristicTaxId(vendorTaxId)) return null;
        if (InBuyerBlock(taxIdPosition, buyerLabelPositions, sellerLabelPositions)) return null;
        var ours = (ourNames ?? Array.Empty<string?>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        // บรรทัดของเลข: ส่วนหน้าป้ายเลขในบรรทัดเดียวกันก่อน ("บริษัท X จำกัด เลขประจำตัวผู้เสียภาษี 010…")
        var lineStart = rawText.LastIndexOf('\n', Math.Max(0, taxIdPosition - 1)) + 1;
        var sameLine = rawText[lineStart..taxIdPosition];
        var cut = TailNoise.Match(sameLine);
        var head = cut.Success ? sameLine[..cut.Index] : sameLine;
        var pick = Candidate(head, lineStart, "บรรทัดเดียวกับเลขผู้เสียภาษี", buyerLabelPositions, sellerLabelPositions, ours);
        if (pick != null) return pick;

        // บรรทัดเหนือเลข (ใกล้สุดก่อน) — หยุดที่บรรทัดแรกที่เป็นชื่อนิติบุคคล ไม่ข้ามไปหาบรรทัดที่ไกลกว่า
        var end = lineStart - 1;   // '\n' ของบรรทัดก่อนหน้า
        for (var up = 1; up <= MaxLinesAbove && end > 0; up++)
        {
            var start = rawText.LastIndexOf('\n', end - 1) + 1;
            var line = rawText[start..end];
            var lineCut = TailNoise.Match(line);
            var text = lineCut.Success ? line[..lineCut.Index] : line;
            if (LegalForm.IsMatch(text))
                return Candidate(text, start, $"บรรทัดที่ {up} เหนือเลขผู้เสียภาษี", buyerLabelPositions, sellerLabelPositions, ours);
            end = start - 1;
        }
        return null;
    }

    /// <summary>
    /// ชื่อนิติบุคคลที่<b>รู้จักแล้ว</b>ของเลขเดียวกัน (ผู้ติดต่อทุกสาขา · คลังค่าที่ยืนยันแล้ว) — ใช้เมื่อชื่อบนสแกนเป็นโลโก้
    /// และหาบรรทัดพิมพ์ไม่ได้/ทะเบียนไม่ยืนยัน · รับเฉพาะชื่อที่<b>มีรูปนิติบุคคล</b> (ชื่อโลโก้ที่เคยหลุดเข้าผู้ติดต่อไม่นับ) ·
    /// ลำดับ = ลำดับที่ผู้เรียกส่งมา (ผู้เรียกเรียงแหล่งที่เชื่อได้ก่อน) · ตัดป้ายสาขาท้ายชื่อออก
    /// </summary>
    public static string? PickKnownLegalName(string? vendorTaxId, IEnumerable<string?>? knownNames)
    {
        if (!IsJuristicTaxId(vendorTaxId) || knownNames == null) return null;
        foreach (var n in knownNames)
        {
            if (LacksLegalForm(n)) continue;
            var (stripped, _) = OcrPartyName.StripBranchSuffix(n);
            if (!string.IsNullOrWhiteSpace(stripped)) return stripped.Trim();
        }
        return null;
    }

    /// <summary>คำปิดท้ายชื่อนิติบุคคล — ตัดสิ่งที่ตามหลัง (โลโก้/แบรนด์ที่ engine ต่อมาในบรรทัดเดียวกัน)</summary>
    private static readonly Regex LegalSuffix = new(
        @"(?:จำกัด|จํากัด)(?:[ \t]*\([ \t]*มหาชน[ \t]*\))?|\b(?:public[ \t]+company[ \t]+limited|company[ \t]+limited|co\.?[ \t]*,?[ \t]*ltd\.?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static OcrPrintedLegalName? Candidate(string text, int position, string where,
        IReadOnlyList<int>? buyerLabels, IReadOnlyList<int>? sellerLabels, List<string?> ours)
    {
        var name = Regex.Replace(text, @"[ \t]{2,}", " ").Trim(' ', '\t', '\r', ',', ':', '：', '-', '·', '|', '(');
        // ตัดหลังคำปิดท้ายตัวแรกที่ไม่ใช่ส่วนของคำนำหน้า "ห้างหุ้นส่วนจำกัด"
        foreach (Match m in LegalSuffix.Matches(name))
        {
            if (m.Index >= "ห้างหุ้นส่วน".Length
                && name.Substring(m.Index - "ห้างหุ้นส่วน".Length, "ห้างหุ้นส่วน".Length) == "ห้างหุ้นส่วน") continue;
            name = name[..(m.Index + m.Length)];
            break;
        }
        if (name.Length < 6 || name.Length > 120) return null;
        if (!LegalForm.IsMatch(name)) return null;
        var (stripped, _) = OcrPartyName.StripBranchSuffix(name);
        name = (stripped ?? name).Trim();
        if (name.Length < 6) return null;
        if (InBuyerBlock(position, buyerLabels, sellerLabels)) return null;
        if (ours.Any(o => OcrSelfPartyGuard.IsSelf(name, o))) return null;
        return new OcrPrintedLegalName(name, position, where);
    }

    /// <summary>ป้ายฝั่งผู้ซื้ออยู่เหนือจุดนี้ใกล้กว่าป้ายฝั่งผู้ขาย (กติกาเดียวกับ <c>OcrVendorKeyEvidence</c>)</summary>
    private static bool InBuyerBlock(int position, IReadOnlyList<int>? buyerLabels, IReadOnlyList<int>? sellerLabels)
        => NearestAbove(position, buyerLabels) > NearestAbove(position, sellerLabels);

    private static int NearestAbove(int position, IReadOnlyList<int>? positions)
    {
        var best = -1;
        if (positions == null) return best;
        foreach (var p in positions)
            if (p < position && p > best) best = p;
        return best;
    }
}
