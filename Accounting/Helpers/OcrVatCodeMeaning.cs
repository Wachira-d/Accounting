using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ความหมายของรหัส ภ.พ. หนึ่งรหัสมาจากไหน — ลำดับชั้นตาม DECISION_DOCTRINE §1 (ใกล้ของจริงชนะ)</summary>
public enum OcrVatCodeMeaningSource
{
    /// <summary>คำอธิบายรหัสที่พิมพ์บนกระดาษ (<see cref="OcrLineVatMarks.ReadGroups"/>) — ไม่ต้องตัดสิน</summary>
    Paper = 0,
    /// <summary>ตัวเลขของแถวพิสูจน์เอง: VAT = 7% ของฐาน ⇒ มี VAT 7% (ไม่ถาม AI)</summary>
    Arithmetic = 1,
    /// <summary>AI (provider จริง) ตอบ และผ่านด่านตัวเลข + เกณฑ์ความมั่นใจ</summary>
    Ai = 2,
    /// <summary>นักเรียน (<c>GenericFeedbackDistillationModel</c> ต่อผู้ขาย + รหัส) ตอบ และผ่านด่าน</summary>
    Student = 3,
    /// <summary>ไม่มีใครตอบได้ (ปิด AI · ไม่มีนักเรียน · คำตอบถูกทิ้ง) ⇒ ค่าตั้งต้น <see cref="OcrVatCodeMeaning.ColdStartKind"/> · ความมั่นใจต่ำ</summary>
    ColdStartDefault = 4,
}

/// <summary>ความหมายที่ตัดสินแล้วของรหัสหนึ่งรหัส — persist ที่ <c>OcrScanResult.VatCodeMeaningsJson</c></summary>
/// <param name="Code">รหัสบนกระดาษ ("1")</param>
/// <param name="Kind">ชนิดที่ใช้</param>
/// <param name="Source">ที่มา</param>
/// <param name="Confidence">ความมั่นใจ (0–1)</param>
/// <param name="FeedbackId">แถว <c>AiSuggestionFeedback</c> ของการถาม (ปิดลูปตอนอนุมัติ) · null = ไม่ได้ถาม</param>
/// <param name="AiAnswer">คำตอบดิบที่ AI/นักเรียนเสนอ (แม้ถูกทิ้ง — เทียบ acceptedAi ตอนผู้ใช้ยืนยัน) · null = ไม่มี</param>
public sealed record OcrVatCodeMeaningEntry(
    string Code, OcrVatGroupKind Kind, OcrVatCodeMeaningSource Source, decimal Confidence, Guid? FeedbackId, string? AiAnswer);

/// <summary>ผลของ <see cref="OcrVatCodeMeaning.Resolve"/> — ตารางที่รหัสถูกตัดสินความหมายแล้ว + ที่มาต่อรหัส</summary>
public sealed record OcrVatCodeResolution(OcrVatGroupTable Table, IReadOnlyList<OcrVatCodeMeaningEntry> Entries, string? Note);

/// <summary>
/// **ความหมายของรหัส ภ.พ. ที่กระดาษไม่บอก** (คำตัดสินเจ้าของ 2026-10-16 · DECISIONS ข้อ 153 ข้อ 2 "ให้โยนไปให้ AI เลย และเอามาสอนระบบอีกที") — pure
///
/// <para>ที่มา: ตารางรหัส ภ.พ. ที่ไม่มีคำอธิบาย/คำอธิบายอ่านเพี้ยน (Tesseract จริงของ Makro 2/2: "1«สินค้าได้รับการยกเว้น…") ⇒ รหัส = Unknown ⇒ เดิมไม่ใช้ตาราง
/// (บรรทัดสรุปใบเดียว + [Σ-GAP]) ทั้งที่ตัวเลขของตารางครบ</para>
///
/// ═══ ลำดับ (ตัวเลขก่อน AI · AI ก่อนค่าตั้งต้น) ═══
/// <list type="number">
/// <item>คำอธิบายบนกระดาษ (ตัวอ่านตาราง) — ไม่แตะ</item>
/// <item><b>ตัวเลขของแถวพิสูจน์</b>: VAT = 7% ของฐาน ⇒ 7% (<see cref="OcrVatCodeMeaningSource.Arithmetic"/> · ไม่ถาม) · VAT &gt; 0 ที่ไม่ใช่ 7% = ตัวเลขขัดกัน ⇒ คงไม่รู้ (ไม่มีคำตอบใดแก้ตัวเลขได้)</item>
/// <item>เหลือ<b>เฉพาะ VAT = 0</b> (ยกเว้น §81 หรือ 0% §80/1) ⇒ ถาม AI ผ่าน orchestrator (นักเรียนต่อผู้ขาย + รหัสตอบก่อน) · คำตอบต้องผ่าน <see cref="Guard"/>
///   (ขัดตัวเลข = ทิ้ง: "มี VAT 7%" บนแถว VAT 0) และความมั่นใจ ≥ <see cref="MinApplyConfidence"/> (write-gate · DECISION_DOCTRINE §2.3)</item>
/// <item>ไม่มีคำตอบที่ใช้ได้ ⇒ <see cref="ColdStartKind"/> (ยกเว้น) · ความมั่นใจ <see cref="ColdStartConfidence"/> (ไฮไลต์เหลือง · ป้าย "⚙️ ระบบแนะนำ") · ไม่บล็อก</item>
/// </list>
/// <para><b>ทำไมค่าตั้งต้น = ยกเว้น</b> (CLAUDE.md #2 B/D): ใบที่ห้างค้าปลีกในประเทศออกเป็น 0% ไม่ได้ (§80/1 = ส่งออก/บริการใช้ต่างประเทศ) · VAT ของแถวเป็น 0 ทั้งสองแบบ
/// ⇒ ยอดเงิน/ภาษีซื้อไม่ต่าง (ภาษีซื้อที่เคลมได้ = 0) · ยกเว้นไม่ถูกนับในรายงานภาษีซื้อ = ทิศที่ไม่เคลมเกิน · ตรงกับคำอธิบายห้างไทยทั่วไป "1 = ยกเว้น"</para>
/// </summary>
public static class OcrVatCodeMeaning
{
    /// <summary>ค่าตั้งต้นเมื่อไม่มีใครตอบได้</summary>
    public const OcrVatGroupKind ColdStartKind = OcrVatGroupKind.Exempt;
    /// <summary>ความมั่นใจของค่าตั้งต้น — ต่ำกว่า 0.85 (ไฮไลต์เหลือง · กฎเหล็ก #3 ข้อ 3)</summary>
    public const decimal ColdStartConfidence = 0.50m;
    /// <summary>เกณฑ์ใช้คำตอบ AI/นักเรียน (write-gate — คำตอบกลายเป็นประเภท VAT ของบรรทัดบัญชี · DECISION_DOCTRINE §2.3)</summary>
    public const decimal MinApplyConfidence = 0.70m;

    /// <summary>คำตอบที่รับได้ (ชื่อ enum — ชุดปิด)</summary>
    public static readonly IReadOnlyList<string> Answers = new[] { "Standard7", "ZeroRated", "Exempt" };

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>กลุ่มที่ต้องถามความหมาย: รหัสไม่รู้ความหมาย · VAT = 0 · ฐาน &gt; 0 (ตัวเลขบอกได้แค่ "ไม่มี VAT" — ยกเว้น หรือ 0%)</summary>
    public static IReadOnlyList<OcrVatGroup> Ambiguous(OcrVatGroupTable table)
        => !table.Found ? Array.Empty<OcrVatGroup>()
            : table.Groups.Where(g => g.Kind == OcrVatGroupKind.Unknown && g.Vat == 0m && g.Net > 0m).ToList();

    /// <summary>แปลงคำตอบเป็นชนิด · null = นอกชุดคำตอบ</summary>
    private static OcrVatGroupKind? ParseAnswer(string? answer) => (answer ?? "").Trim().ToLowerInvariant() switch
    {
        "standard7" or "standard" or "7" or "7%" => OcrVatGroupKind.Standard7,
        "zerorated" or "zero" or "0" or "0%" => OcrVatGroupKind.ZeroRated,
        "exempt" => OcrVatGroupKind.Exempt,
        _ => null,
    };

    public static string AnswerOf(OcrVatGroupKind kind) => kind switch
    {
        OcrVatGroupKind.Standard7 => "Standard7",
        OcrVatGroupKind.ZeroRated => "ZeroRated",
        OcrVatGroupKind.Exempt => "Exempt",
        _ => "",
    };

    /// <summary>
    /// **ด่านกันมั่ว (anti-hallucination ชนิดเลขคณิต · DECISION_DOCTRINE §2.4)** — คำตอบต้องสอดคล้องกับตัวเลขของแถวเอง:
    /// VAT ≈ 7% ของฐาน ⇒ ต้องเป็น Standard7 · VAT = 0 ⇒ ต้องเป็น ZeroRated/Exempt · นอกชุด/ขัดตัวเลข = null + เหตุผล
    /// </summary>
    public static OcrVatGroupKind? Guard(OcrVatGroup row, string? answer, out string reason)
    {
        var kind = ParseAnswer(answer);
        if (kind is null) { reason = $"คำตอบ '{answer}' ไม่อยู่ในชุด Standard7/ZeroRated/Exempt"; return null; }
        var seven = OcrLineVatMarks.IsSevenPercentRow(row.Net, row.Vat, row.ItemCount);
        if (kind == OcrVatGroupKind.Standard7 && !seven)
        {
            reason = $"คำตอบ 7% ขัดกับแถว (ฐาน {row.Net:N2} · VAT {row.Vat:N2} ไม่ใช่ 7%)";
            return null;
        }
        if (kind != OcrVatGroupKind.Standard7 && row.Vat != 0m)
        {
            reason = $"คำตอบ {AnswerOf(kind.Value)} ขัดกับแถวที่มี VAT {row.Vat:N2}";
            return null;
        }
        reason = "";
        return kind;
    }

    /// <summary>
    /// ตัดสินรหัสหนึ่งรหัสจากคำตอบของ orchestrator (หรือไม่มีคำตอบ) — ด่าน <see cref="Guard"/> + เกณฑ์ความมั่นใจ · ไม่ผ่าน ⇒ ค่าตั้งต้น
    /// (เก็บ <paramref name="feedbackId"/>/<paramref name="answer"/> ไว้เสมอ — คำตอบที่ถูกทิ้งก็คือข้อมูลสอน · DECISION_DOCTRINE §2.2)
    /// </summary>
    /// <param name="hasModelAnswer">AI หรือนักเรียนตอบจริง (<c>OcrAiAugmentationResult.HasModelAnswer</c>) — false = คำตอบดิบเป็นค่าที่เราส่งไปเอง</param>
    public static OcrVatCodeMeaningEntry Choose(
        OcrVatGroup row, string? answer, decimal? confidence, bool hasModelAnswer, bool usedAi, Guid? feedbackId)
    {
        if (hasModelAnswer && Guard(row, answer, out _) is OcrVatGroupKind k
            && confidence is decimal c && c >= MinApplyConfidence && c <= 1m)
            return new(row.PaperCode, k, usedAi ? OcrVatCodeMeaningSource.Ai : OcrVatCodeMeaningSource.Student, c, feedbackId, answer);
        return new(row.PaperCode, ColdStartKind, OcrVatCodeMeaningSource.ColdStartDefault, ColdStartConfidence, feedbackId,
            hasModelAnswer ? answer : null);
    }

    /// <summary>
    /// **ตารางที่รหัสถูกตัดสินความหมายแล้ว** — ตัวเดียวของตัวสร้างบรรทัด (service + replay harness) · คำอธิบายบนกระดาษคงเดิม ·
    /// รหัสไม่รู้ความหมาย: VAT = 7% ⇒ 7% · VAT = 0 ⇒ คำตอบที่ persist ไว้ (ผ่านด่านซ้ำ — ข้อมูลเก่าที่ผิดใช้ไม่ได้) หรือค่าตั้งต้น · VAT อื่น ⇒ คงไม่รู้
    /// </summary>
    /// <param name="learned">ความหมายที่ตัดสินตอนสแกน (<c>VatCodeMeaningsJson</c>) · null/ว่าง = สแกนเก่า ⇒ ค่าตั้งต้น</param>
    public static OcrVatCodeResolution Resolve(OcrVatGroupTable table, IReadOnlyList<OcrVatCodeMeaningEntry>? learned)
    {
        if (!table.Found || table.AllKnown) return new(table, Array.Empty<OcrVatCodeMeaningEntry>(), null);
        var entries = new List<OcrVatCodeMeaningEntry>();
        var groups = new List<OcrVatGroup>(table.Groups.Count);
        foreach (var g in table.Groups)
        {
            if (g.Kind != OcrVatGroupKind.Unknown) { groups.Add(g); continue; }
            if (OcrLineVatMarks.IsSevenPercentRow(g.Net, g.Vat, g.ItemCount))
            {
                entries.Add(new(g.PaperCode, OcrVatGroupKind.Standard7, OcrVatCodeMeaningSource.Arithmetic, 0.95m, null, null));
                groups.Add(g with { Kind = OcrVatGroupKind.Standard7, Evidence = g.Evidence + $" ⇒ VAT {g.Vat:N2} = 7% ของ {g.Net:N2} (ตัวเลขพิสูจน์)" });
                continue;
            }
            if (g.Vat != 0m) { groups.Add(g); continue; }      // ตัวเลขขัดกัน — ไม่มีคำตอบใดแก้ได้
            var known = learned?.LastOrDefault(e => e.Code == g.PaperCode);
            OcrVatCodeMeaningEntry entry;
            if (known is not null && known.Source is OcrVatCodeMeaningSource.Ai or OcrVatCodeMeaningSource.Student
                && Guard(g, AnswerOf(known.Kind), out _) is not null && known.Confidence >= MinApplyConfidence)
                entry = known;
            else
                entry = new(g.PaperCode, ColdStartKind, OcrVatCodeMeaningSource.ColdStartDefault, ColdStartConfidence,
                    known?.FeedbackId, known?.AiAnswer);
            entries.Add(entry);
            groups.Add(g with { Kind = entry.Kind, Evidence = g.Evidence + " ⇒ " + SourceText(entry) });
        }
        var resolved = table with { Groups = groups };
        var note = entries.Count == 0 ? null
            : "ความหมายรหัส ภ.พ. ที่กระดาษไม่บอก: " + string.Join(" · ", entries.Select(e => $"รหัส {e.Code} = {KindText(e.Kind)} ({SourceText(e)})"));
        return new(resolved, entries, note);
    }

    /// <summary>ป้ายซื่อสัตย์สำหรับหน้ารีวิว (กฎเหล็ก #1) — "🤖 AI แนะนำ" เฉพาะเมื่อ provider จริงตอบ · ไม่งั้น "⚙️ ระบบแนะนำ"</summary>
    public static string SourceText(OcrVatCodeMeaningEntry e) => e.Source switch
    {
        OcrVatCodeMeaningSource.Ai => $"🤖 AI แนะนำ · มั่นใจ {e.Confidence:P0}",
        OcrVatCodeMeaningSource.Student => $"⚙️ ระบบแนะนำ (เรียนจากใบก่อนของผู้ขายนี้ · มั่นใจ {e.Confidence:P0})",
        OcrVatCodeMeaningSource.Arithmetic => "⚙️ ระบบแนะนำ (VAT ของแถว = 7% ของฐาน)",
        OcrVatCodeMeaningSource.ColdStartDefault => "⚙️ ระบบแนะนำ (ค่าตั้งต้น · ความมั่นใจต่ำ — ตรวจคำอธิบายรหัสบนกระดาษ)",
        _ => "คำอธิบายบนกระดาษ",
    };

    /// <summary>ข้อความแสดงในหน้ารีวิว (เซิร์ฟเวอร์ประกอบ · หน้าเว็บแสดงอย่างเดียว) · null = ไม่มีรหัสที่ต้องตัดสิน</summary>
    public static string? ReviewNote(IReadOnlyList<OcrVatCodeMeaningEntry> entries)
        => entries.Count == 0 ? null
            : "ประเภท VAT ตามรหัส ภ.พ. ที่กระดาษไม่บอกความหมาย: "
              + string.Join(" · ", entries.Select(e => $"รหัส {e.Code} = {KindText(e.Kind)} — {SourceText(e)}"));

    /// <summary>
    /// **คำตอบจริงตอนอนุมัติ** (DECISION_DOCTRINE §3) — ชนิดของบรรทัดที่ไม่มี VAT ในเอกสารที่ผู้ใช้อนุมัติ: ทุกบรรทัด VAT ≤ 0 เป็นยกเว้น ⇒ Exempt ·
    /// เป็น 0% ⇒ ZeroRated · ปน/ไม่มี ⇒ null (ไม่รู้ว่าผู้ใช้ตอบอะไร — ไม่บันทึก · ห้ามสอนสิ่งที่ไม่รู้)
    /// </summary>
    public static OcrVatGroupKind? ChosenNoVatKind(IEnumerable<decimal> lineRates)
    {
        var kinds = lineRates.Where(r => r <= 0m)
            .Select(r => r == ThaiVatTypeRule.ExemptRate ? OcrVatGroupKind.Exempt : r == 0m ? OcrVatGroupKind.ZeroRated : OcrVatGroupKind.Unknown)
            .Distinct().ToList();
        return kinds.Count == 1 && kinds[0] != OcrVatGroupKind.Unknown ? kinds[0] : null;
    }

    /// <summary>บรรทัดคำอธิบายรหัส: "รหัส 1 สินค้า…" ที่ "=" อาจหาย/เพี้ยน ("1«สินค้า…" · "28สินคํา…") หรือบรรทัด "รหัส ภ.พ./VAT CODE" ที่มี "="
    /// (หัวตาราง "ราคาต่อหน่วย รหัส ภ.พ. จำนวนเงินรวม" ไม่ใช่คำอธิบาย)</summary>
    private static readonly Regex LegendLine = new(
        @"(?:^|[ \t(|·])\d{1,2}[ \t]*[=«:.\-]?[ \t]*สินค[้ํ]?า|(?:รหัส[ \t]*ภ\.?[ \t]*พ|vat[ \t]*code)[^\n]*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>บรรทัดคำอธิบายรหัสบนกระดาษ (อาจอ่านเพี้ยน) — ส่งให้ AI เป็นหลักฐานประกอบ · ≤ 3 บรรทัด · ≤ 300 ตัวอักษรต่อบรรทัด · ว่าง = ไม่มี</summary>
    public static string LegendText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return "";
        var lines = rawText.Replace("\r", "").Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && LegendLine.IsMatch(l) && l.Any(char.IsLetter))
            .Select(l => l.Length > 300 ? l[..300] : l).Distinct().Take(3);
        return string.Join("\n", lines);
    }

    public static string? ToJson(IReadOnlyList<OcrVatCodeMeaningEntry> entries)
        => entries.Count == 0 ? null : JsonSerializer.Serialize(entries, Json);

    /// <summary>อ่านกลับ · เสีย/ว่าง = ว่าง (สแกนเก่า ⇒ ค่าตั้งต้น — ไม่ throw)</summary>
    public static IReadOnlyList<OcrVatCodeMeaningEntry> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<OcrVatCodeMeaningEntry>();
        try { return JsonSerializer.Deserialize<List<OcrVatCodeMeaningEntry>>(json, Json) ?? new(); }
        catch (JsonException) { return Array.Empty<OcrVatCodeMeaningEntry>(); }
    }

    internal static string KindText(OcrVatGroupKind k) => k switch
    {
        OcrVatGroupKind.Standard7 => "มี VAT 7%",
        OcrVatGroupKind.ZeroRated => "อัตรา 0%",
        OcrVatGroupKind.Exempt => "ยกเว้น VAT",
        _ => "ไม่รู้ความหมาย",
    };
}
