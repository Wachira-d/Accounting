using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลของ <see cref="OcrEtaxPageFill.ResolveBuyer"/> — ค่าที่ใช้ต่อช่อง + ที่มา + ข้อขัดแย้ง</summary>
/// <param name="BuyerName">ชื่อผู้ซื้อที่ใช้ (XML ก่อน · ว่างแล้วหน้ามีชื่อเรา ⇒ ชื่อบริษัทเรา)</param>
/// <param name="BuyerTaxId">เลขผู้ซื้อที่ใช้ (XML ก่อน · ว่าง/N/A แล้วหน้ามีเลขเราตรงตัว ⇒ เลขเรา)</param>
/// <param name="BuyerBranchCode">สาขาผู้ซื้อที่ใช้ (XML ก่อน · ว่างแล้วบรรทัดเลขเราบนหน้าบอกสาขา ⇒ ตามหน้า)</param>
/// <param name="FilledFromPage">ช่องที่มาจากหน้า PDF เพราะ XML ขาด (ชื่อภาษาไทยของช่อง)</param>
/// <param name="Conflicts">ช่องที่ XML มีค่าจริงแต่ขัดกับหน้า — คงค่า XML</param>
/// <param name="BlockingConflict">true = เลขผู้ซื้อ/ผู้ขายขัดกันจริง ⇒ หมายเหตุมี <see cref="OcrEtaxPageFill.ConflictTag"/> (ห้ามอนุมัติเอง)</param>
/// <param name="Note">หมายเหตุลง ProcessingNotes (null = ไม่มีอะไรต้องบอก)</param>
public sealed record OcrEtaxBuyerFill(
    string? BuyerName, string? BuyerTaxId, string? BuyerBranchCode,
    IReadOnlyList<string> FilledFromPage, IReadOnlyList<string> Conflicts, bool BlockingConflict, string? Note);

/// <summary>
/// **ใบ e-Tax: เลือกข้อมูลคู่สัญญาที่ "ครบและถูกต้องที่สุด" ระหว่าง XML กับข้อความบนหน้า PDF ของไฟล์เดียวกัน — ตัวตัดสินเดียว** (pure · ไม่ throw)
///
/// <para>═══ ที่มา ═══ ใบ SP-IN2026-10-00933 (Rapbizz Direct · 2026-10-26): XML ส่งเลขผู้ซื้อ <c>&lt;ram:ID schemeID="OTHR"&gt;N/A&lt;/ram:ID&gt;</c>
/// แต่หน้า PDF พิมพ์ "เลขที่ผู้เสียภาษี : 0203562005871 สาขาที่ : สำนักงานใหญ่" ⇒ เดิม §82/5(1) ปิดเคลมทั้งใบ ("VAT ต้องห้าม 140.70") ·
/// <b>คำตัดสินเจ้าของข้อ 158</b> (erp-review/2026-09-29/DECISIONS.md): "ให้ตรวจสอบข้อมูลบนกระดาษ ถ้าครบกว่า ก็เลือกใช้ข้อมูลที่ครบ ถูกต้องที่สุดไปเลย"</para>
///
/// <para><b>กติกา "ถูกต้อง":</b> (1) เลขผู้ซื้อจากหน้าใช้ได้เฉพาะเมื่อ <b>เท่ากับเลขผู้เสียภาษีของบริษัทเราตรงตัว</b> (ไม่ใช่เลขใดก็ได้ที่ checksum ผ่าน ·
/// ไม่ fuzzy — ตัวเลขล้วนไม่มีการสะกดผิด F2 ข้อ 3) · (2) ช่องที่ XML มีค่าจริง (ไม่ว่าง/ไม่ใช่ N/A) ชนะเสมอ — ขัดกับหน้า ⇒ คงค่า XML + เตือน
/// (ไม่หยุดอนุมัติ) ยกเว้น<b>เลขผู้ซื้อ/ผู้ขายขัดกันจริง</b> ⇒ <see cref="ConflictTag"/> (อยู่ใน <see cref="OcrPostingReadiness.BlockingTags"/>) ·
/// (3) ไม่ได้ตรวจลายเซ็นดิจิทัลของหน้า — เป็นข้อความที่อ่านได้จากไฟล์ (ระบุในหมายเหตุตรง ๆ)</para>
/// <para>ไม่เติม: ที่อยู่ผู้ซื้อ (ข้อความหน้ามักเพี้ยนจากฟอนต์ไทย — XML มีที่อยู่ ⇒ ใช้ XML · XML ว่าง ⇒ ชั้นถัดไปของไปป์ไลน์ตัดสิน)</para>
/// </summary>
public static class OcrEtaxPageFill
{
    /// <summary>หมายเหตุบอกที่มา (ไม่บล็อก) — ช่องไหนมาจากหน้า PDF เพราะ XML ขาด / ช่องไหนขัดกัน</summary>
    public const string FillTag = "[ETAX-PAGE-FILL]";

    /// <summary>เลขผู้ซื้อ/ผู้ขายใน XML ขัดกับหน้า PDF จริง — ห้ามอนุมัติเอง (เจ้าของคือ <see cref="OcrPostingReadiness.BlockingTags"/>)</summary>
    public const string ConflictTag = "[ETAX-PARTY-CONFLICT]";

    public const string RuleCode = "RD-86/4(3)-ETAX-PAGE";
    public const string LegalReference = "ป.รัษฎากร ม.86/4(3) · ม.82/5(1) · ประกาศอธิบดีฯ (ฉบับที่ 199) · ขมธอ.3-2560 · คำตัดสินเจ้าของข้อ 158";

    private static readonly Regex ThirteenDigits = new(@"(?<!\d)\d(?:[- \t]?\d){12}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex BranchDigits = new(@"สาขา[^\d\n]{0,20}?(?<code>\d{5})(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex HeadOffice = new(@"ใหญ่|head[ \t]*office", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="xmlBuyerName">ชื่อผู้ซื้อตาม XML</param>
    /// <param name="xmlBuyerTaxId">เลขผู้ซื้อตาม XML (null = ไม่ประกาศ/N/A — ตัวสกัดแปลงแล้ว)</param>
    /// <param name="xmlBuyerBranch">สาขาผู้ซื้อตาม XML</param>
    /// <param name="xmlSellerTaxId">เลขผู้ขายตาม XML</param>
    /// <param name="pageText">ข้อความบนหน้า PDF ของไฟล์ (normalize แล้ว) — null = อ่านไม่ได้ ⇒ ตาม XML ทุกช่อง</param>
    /// <param name="ourTaxId">เลขผู้เสียภาษีบริษัทเรา</param>
    /// <param name="ourName">ชื่อบริษัทเรา</param>
    public static OcrEtaxBuyerFill ResolveBuyer(string? xmlBuyerName, string? xmlBuyerTaxId, string? xmlBuyerBranch,
        string? xmlSellerTaxId, string? pageText, string? ourTaxId, string? ourName)
    {
        var filled = new List<string>();
        var conflicts = new List<string>();
        var blocking = new List<string>();
        string? name = xmlBuyerName, taxId = xmlBuyerTaxId, branch = xmlBuyerBranch;
        if (string.IsNullOrWhiteSpace(pageText))
            return new(name, taxId, branch, filled, conflicts, false, null);

        var lines = pageText!.Replace("\r", "").Split('\n');
        var ours = ThaiTaxId.Normalize(ourTaxId);
        var xmlBuyer = ThaiTaxId.Normalize(xmlBuyerTaxId);
        var xmlSeller = ThaiTaxId.Normalize(xmlSellerTaxId);
        var pageIds = ThirteenDigits.Matches(pageText).Select(m => ThaiTaxId.Normalize(m.Value)).Where(d => d.Length == 13).ToList();
        var oursOnPage = ours.Length == 13 && ours != xmlSeller && pageIds.Contains(ours);

        // ── เลขผู้ซื้อ ──
        if (xmlBuyer.Length == 13)
        {
            if (oursOnPage && xmlBuyer != ours && !pageIds.Contains(xmlBuyer))
                blocking.Add($"เลขผู้ซื้อใน XML {xmlBuyer} แต่หน้า PDF พิมพ์เลขบริษัทเรา {ours} (ไม่มีเลขใน XML บนหน้า) — คงค่า XML");
        }
        else if (oursOnPage)
        {
            taxId = ours;
            filled.Add("เลขประจำตัวผู้เสียภาษีของผู้ซื้อ");
        }

        // ── เลขผู้ขาย: XML มีเลขที่ถูกต้อง แต่หน้าไม่มีเลขนั้นเลย และมีเลขอื่นที่ถูกต้อง (ไม่ใช่เลขเรา) ──
        if (ThaiTaxId.IsValid(xmlSeller) && pageIds.Count > 0 && !pageIds.Contains(xmlSeller))
        {
            var other = pageIds.FirstOrDefault(d => d != ours && ThaiTaxId.IsValid(d));
            if (other is not null)
                blocking.Add($"เลขผู้ขายใน XML {xmlSeller} ไม่ปรากฏบนหน้า PDF แต่หน้าพิมพ์เลข {other} — คงค่า XML");
        }

        // ── สาขาผู้ซื้อ: อ่านจากบรรทัดที่มีเลขเรา (เมื่อผู้ซื้อคือเรา) ──
        var buyerIsUs = ours.Length == 13 && ThaiTaxId.Normalize(taxId) == ours;
        if (buyerIsUs)
        {
            var pageBranch = BranchOnOurLine(lines, ours);
            if (pageBranch is not null)
            {
                if (string.IsNullOrWhiteSpace(branch)) { branch = pageBranch; filled.Add("สาขาผู้ซื้อ"); }
                else if (branch!.Trim() != pageBranch) conflicts.Add($"สาขาผู้ซื้อ XML {branch.Trim()} · หน้า PDF {pageBranch} — คงค่า XML");
            }
        }

        // ── ชื่อผู้ซื้อ: XML ว่าง + หน้ามีชื่อเรา ⇒ ชื่อบริษัทเรา (ชื่อที่จดทะเบียน ไม่ใช่ข้อความเพี้ยนบนหน้า) ──
        if (string.IsNullOrWhiteSpace(name) && buyerIsUs && !string.IsNullOrWhiteSpace(ourName)
            && lines.Any(l => OcrSelfPartyGuard.NameOverlaps(l, ourName)))
        {
            name = ourName;
            filled.Add("ชื่อผู้ซื้อ");
        }

        var notes = new List<string>();
        if (filled.Count > 0 || conflicts.Count > 0)
            notes.Add($"{FillTag} ({RuleCode} · {LegalReference}) "
                + (filled.Count > 0
                    ? $"ข้อมูล e-Tax (XML) ที่ผู้ขายส่งกรมสรรพากรไม่มี{string.Join(" · ", filled)} — ใช้ค่าจากข้อความบนหน้า PDF ของไฟล์เดียวกัน "
                      + "(เลขผู้ซื้อตรงกับเลขบริษัทเราทุกหลัก · ระบบไม่ได้ตรวจลายเซ็นของหน้า) · แนะนำแจ้งผู้ขายแก้ข้อมูล e-Tax ให้ครบ"
                    : "")
                + (conflicts.Count > 0 ? (filled.Count > 0 ? " · " : "") + "ข้อมูลขัดกัน (คงค่า XML): " + string.Join(" · ", conflicts) : ""));
        foreach (var b in blocking)
            notes.Add($"{ConflictTag} ({RuleCode} · {LegalReference}) {b} — ตรวจกับกระดาษก่อนอนุมัติ");
        return new(name, taxId, branch, filled, conflicts, blocking.Count > 0, notes.Count == 0 ? null : string.Join("\n", notes));
    }

    /// <summary>สาขาบนบรรทัดที่มีเลขเรา (หรือบรรทัดถัดไป): "สาขาที่ 00012" ⇒ 00012 · "สำนักงานใหญ่/Head Office" ⇒ 00000 · ไม่พบ = null</summary>
    private static string? BranchOnOurLine(IReadOnlyList<string> lines, string ours)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!ThirteenDigits.Matches(lines[i]).Any(m => ThaiTaxId.Normalize(m.Value) == ours)) continue;
            var at = lines[i].IndexOf(ours[^4..], StringComparison.Ordinal);
            var tail = at >= 0 ? lines[i][at..] : lines[i];
            var digits = BranchDigits.Match(tail);
            if (digits.Success) return digits.Groups["code"].Value;
            if (tail.Contains("สาขา", StringComparison.Ordinal) && HeadOffice.IsMatch(tail)) return "00000";
            return null;
        }
        return null;
    }
}
