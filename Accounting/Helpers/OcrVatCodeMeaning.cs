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
    /// <param name="zeroRatedOnPaper">กระดาษมีหลักฐานอัตรา 0% (<see cref="ZeroRatedOnPaper"/>) — false ⇒ คำตอบ ZeroRated ถูกทิ้ง
    /// (ฝ่ายค้าน f7c29c2c P2-3: ใบขายปลีกในประเทศไม่มี 0% · §80/1 = ส่งออก/บริการใช้ต่างประเทศ · คำอธิบายที่ถูกฉีด/อ่านเพี้ยน หรือ AI มั่ว
    /// พลิกบรรทัดเป็น 0% ⇒ ยอดย้ายจากคอลัมน์ยกเว้นไปคอลัมน์ 0% ของรายงานภาษี) · ผู้ใช้ยังตั้ง 0% เองบนบรรทัดได้เสมอ</param>
    public static OcrVatGroupKind? Guard(OcrVatGroup row, string? answer, out string reason, bool zeroRatedOnPaper)
    {
        var kind = ParseAnswer(answer);
        if (kind is null) { reason = $"คำตอบ '{answer}' ไม่อยู่ในชุด Standard7/ZeroRated/Exempt"; return null; }
        if (kind == OcrVatGroupKind.ZeroRated && !zeroRatedOnPaper)
        {
            reason = "คำตอบ 0% ไม่มีหลักฐานบนกระดาษ (ไม่มีคำว่า 0% / อัตราศูนย์ / zero rate / ส่งออก) — ใบซื้อในประเทศ 0% มีแต่ §80/1";
            return null;
        }
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
    /// <param name="zeroRatedOnPaper">หลักฐาน 0% บนกระดาษ (<see cref="ZeroRatedOnPaper"/>)</param>
    public static OcrVatCodeMeaningEntry Choose(
        OcrVatGroup row, string? answer, decimal? confidence, bool hasModelAnswer, bool usedAi, Guid? feedbackId, bool zeroRatedOnPaper)
    {
        if (hasModelAnswer && Guard(row, answer, out _, zeroRatedOnPaper) is OcrVatGroupKind k
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
    /// <param name="zeroRatedOnPaper">หลักฐาน 0% บนกระดาษ — คำตอบ ZeroRated ที่ persist ไว้โดยไม่มีหลักฐาน ⇒ ค่าตั้งต้น (ข้อมูลเก่าที่ผิดใช้ไม่ได้ · F2 ข้อ 9)</param>
    public static OcrVatCodeResolution Resolve(OcrVatGroupTable table, IReadOnlyList<OcrVatCodeMeaningEntry>? learned, bool zeroRatedOnPaper)
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
                && Guard(g, AnswerOf(known.Kind), out _, zeroRatedOnPaper) is not null && known.Confidence >= MinApplyConfidence)
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

    /// <summary>
    /// **ตารางรหัส ภ.พ. ที่ตัดสินความหมายแล้วของสแกนหนึ่งใบ — ตัวเดียวของผู้อ่านทุกตัว** (ฝ่ายค้าน f7c29c2c P1-1): ตัวสร้างบรรทัด · ด่านตรวจสดตอนอนุมัติ ·
    /// ปุ่มอนุมัติ LINE · ยอดยกเว้นบนกระดาษ (<c>OcrLineVatPlanner.PaperExemptAmount</c>) · ตัวปิดลูปตอนอนุมัติ · replay harness
    /// <para>ที่มา: ตอนสร้างอ่านตารางที่ตัดสินแล้ว แต่ตอนอนุมัติ/LINE อ่านตารางดิบ (<see cref="OcrLineVatMarks.ReadGroups"/>) ⇒ Makro 2/2 Tesseract ที่คำอธิบายรหัส 1
    /// อ่านออก ("1=") แต่รหัส 2 เพี้ยน: สร้างสะอาด · อนุมัติฟ้อง "รหัส 2 ไม่บอกความหมาย" · LINE ปฏิเสธ — ผู้อ่านคำนวณตารางเองคนละสูตร (CLAUDE.md #4 A "Resolver กลาง")</para>
    /// </summary>
    /// <param name="normalizedRawText">ข้อความดิบที่ผ่าน <c>ThaiTextNormalizer.Normalize</c> แล้ว</param>
    /// <param name="vatCodeMeaningsJson"><c>OcrScanResult.VatCodeMeaningsJson</c> · null = สแกนเก่า/ไม่มีชุดคำตอบ ⇒ ตัวเลขของแถว + ค่าตั้งต้น</param>
    public static OcrVatCodeResolution ResolvedTable(string? normalizedRawText, string? vatCodeMeaningsJson)
        => Resolve(OcrLineVatMarks.ReadGroups(normalizedRawText), FromJson(vatCodeMeaningsJson), ZeroRatedOnPaper(normalizedRawText));

    /// <summary>วลีที่บอกอัตรา 0% (§80/1) บนกระดาษ — "อัตราศูนย์/อัตรา 0%" · "VAT 0%" · "zero rate(d)" · "ส่งออก/export"</summary>
    private static readonly Regex ZeroRatedPhrase = new(
        @"อัตรา(?:ภาษี)?[ \t]*(?:ร้อยละ[ \t]*)?(?:ศูนย์|0(?:\.0+)?[ \t]*%)|(?:vat|ภาษีมูลค[่]?าเพิ่ม)[ \t]*0(?:\.0+)?[ \t]*%|zero[ \t-]*rat|ส[่]?งออก|\bexport",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"0%" ตรง ๆ ในบรรทัดคำอธิบายรหัส ("3=สินค้าอัตรา 0%")</summary>
    private static readonly Regex ZeroPercent = new(@"(?<![\d.,])0(?:\.0+)?[ \t]*%", RegexOptions.CultureInvariant);

    /// <summary>
    /// **กระดาษมีหลักฐานอัตรา 0% ไหม** (ฝ่ายค้าน f7c29c2c P2-3) — คำตอบ ZeroRated (AI · นักเรียน · ที่ persist) ใช้ได้เฉพาะเมื่อ true ·
    /// ไม่มี ⇒ ยกเว้น (ค่าตั้งต้น · ใบซื้อในประเทศ 0% มีแต่ §80/1) · ค้นทั้งใบด้วยวลีเฉพาะ + "0%" เฉพาะในบรรทัดคำอธิบายรหัส ("ส่วนลด 0%" ที่อื่นไม่นับ)
    /// </summary>
    public static bool ZeroRatedOnPaper(string? normalizedRawText)
    {
        if (string.IsNullOrWhiteSpace(normalizedRawText)) return false;
        if (ZeroRatedPhrase.IsMatch(normalizedRawText)) return true;
        return LegendText(normalizedRawText).Split('\n').Any(l => ZeroPercent.IsMatch(l));
    }

    /// <summary>เพดานความยาวของหลักฐานข้อความ OCR ที่ส่งครู (ต่อช่อง)</summary>
    public const int MaxEvidenceChars = 300;

    /// <summary>บรรทัดที่หน้าตาเหมือน "คำสั่ง" ในข้อความ OCR (prompt injection) — ตัดทิ้งก่อนส่งครู</summary>
    private static readonly Regex InstructionLike = new(
        @"ignore|disregard|instruction|system|prompt|assistant|respond|answer|output|json|primary|confidence|you[ \t]+are|\bmust\b|คำสั่ง|ให้ตอบ|ตอบว่า|จงตอบ|ละเว้น|ไม่ต้องสนใจ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// **ทำความสะอาดข้อความ OCR ก่อนส่งครู** (ฝ่ายค้าน f7c29c2c P2-3 — ข้อความบนกระดาษเป็น<b>ข้อมูล</b> ไม่ใช่คำสั่ง): ตัดบรรทัดที่หน้าตาเหมือนคำสั่ง ·
    /// ตัดอักขระควบคุม/วงเล็บปีกกา/backtick/วงเล็บแหลม · ยุบช่องว่าง · เพดาน <see cref="MaxEvidenceChars"/> ตัวอักษร · ว่าง ⇒ null
    /// </summary>
    public static string? SanitizeEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => new string(l.Where(c => !char.IsControl(c) && c is not ('{' or '}' or '`' or '<' or '>')).ToArray()))
            .Select(l => Regex.Replace(l, @"[ \t]{2,}", " ").Trim())
            .Where(l => l.Length > 0 && !InstructionLike.IsMatch(l));
        var joined = string.Join("\n", lines);
        if (joined.Length > MaxEvidenceChars) joined = joined[..MaxEvidenceChars];
        return joined.Length == 0 ? null : joined;
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
    /// **ตอนอนุมัติ รหัสนี้สอนนักเรียนได้ไหม** (ฝ่ายค้าน f7c29c2c P2-4) — ผู้ใช้<b>แก้</b>ชนิด (≠ ที่ระบบเติม) ⇒ สอน · คง<b>คำตอบ AI</b> ⇒ สอน ·
    /// คงค่าตั้งต้น ⇒ ไม่สอน (การกดอนุมัติผ่านไม่ใช่หลักฐาน — เดิมอนุมัติ ~10 ใบ ค่าตั้งต้นไต่ถึง Wilson ≥ 0.70 แล้วถูกติดป้าย "เรียนจากใบก่อนของผู้ขายนี้") ·
    /// คงคำตอบนักเรียน ⇒ ไม่สอน (ยืนยันตัวเอง · DECISION_DOCTRINE §3)
    /// </summary>
    public static bool Teaches(OcrVatCodeMeaningEntry entry, OcrVatGroupKind chosen)
        => entry.Kind != chosen || entry.Source == OcrVatCodeMeaningSource.Ai;

    /// <summary>เพดานรหัสที่ตัดสินต่อรหัสด้วยผลรวม (2^k)</summary>
    private const int MaxChosenCodes = 8;

    /// <summary>
    /// **คำตอบจริงตอนอนุมัติ ต่อรหัส** (DECISION_DOCTRINE §3 · ฝ่ายค้าน f7c29c2c P2-6) — ชนิดของบรรทัดที่ไม่มี VAT ในเอกสารที่ผู้ใช้อนุมัติ จับคู่กับรหัสที่ถาม:
    /// <list type="bullet">
    /// <item>บรรทัด VAT ≤ 0 เป็นชนิดเดียว (ทั้งหมดยกเว้น หรือทั้งหมด 0%) ⇒ ทุกรหัสที่ถามได้ชนิดนั้น (สูตรเดิม)</item>
    /// <item>ปนกัน (ยกเว้น + 0%) ⇒ จับคู่ด้วย<b>ยอด</b>: Σ บรรทัดยกเว้น/Σ บรรทัด 0% ต้องเท่า Σ ฐานของกลุ่มที่ตกแต่ละชนิด (กลุ่มที่กระดาษบอกความหมายคงชนิดเดิม ·
    ///   รหัสที่ถามเลือกได้สองทาง) · รหัสที่ทุกทางที่ลงตัวตอบตรงกัน ⇒ บันทึก · ไม่ลงตัว/หลายทางขัดกัน ⇒ ไม่บันทึกรหัสนั้น (ห้ามสอนสิ่งที่ไม่รู้)</item>
    /// <item>ไม่มีบรรทัดไม่มี VAT (ผู้ใช้รวมเป็น 7% ทั้งใบ) ⇒ ว่าง (ไม่ใช่คำตอบของคำถามนี้)</item>
    /// </list>
    /// เดิม (<c>ChosenNoVatKind</c>) ปน ⇒ null ทั้งใบ ⇒ ใบที่มีทั้งยกเว้นและ 0% ไม่เคยสอนนักเรียนเลย
    /// </summary>
    /// <param name="resolvedTable">ตารางของสแกน (<see cref="ResolvedTable"/>)</param>
    /// <param name="askedCodes">รหัสที่ถาม (มี FeedbackId)</param>
    /// <param name="lines">(ยอดก่อน VAT · อัตรา) ของบรรทัดที่อนุมัติ</param>
    public static IReadOnlyDictionary<string, OcrVatGroupKind> ChosenKindsByCode(
        OcrVatGroupTable resolvedTable, IReadOnlyCollection<string> askedCodes, IEnumerable<(decimal Amount, decimal Rate)> lines)
    {
        var result = new Dictionary<string, OcrVatGroupKind>(StringComparer.Ordinal);
        static OcrVatGroupKind KindOfLine(decimal r)
            => r == ThaiVatTypeRule.ExemptRate ? OcrVatGroupKind.Exempt : r == 0m ? OcrVatGroupKind.ZeroRated : OcrVatGroupKind.Unknown;
        var noVat = lines.Where(l => l.Rate <= 0m).ToList();
        var kinds = noVat.Select(l => KindOfLine(l.Rate)).Distinct().ToList();
        if (askedCodes.Count == 0 || kinds.Count == 0 || kinds.Contains(OcrVatGroupKind.Unknown)) return result;
        if (kinds.Count == 1)
        {
            foreach (var c in askedCodes) result[c] = kinds[0];
            return result;
        }
        // ปน: จับคู่ด้วยยอด — กลุ่มไม่มี VAT ของตาราง (รหัสที่ถามเลือกได้ · ที่เหลือคงชนิดตามกระดาษ)
        var groups = resolvedTable.Found ? resolvedTable.Groups.Where(g => g.Vat == 0m && g.Net > 0m).ToList() : new List<OcrVatGroup>();
        var free = groups.Where(g => askedCodes.Contains(g.PaperCode)).ToList();
        var fixedGroups = groups.Where(g => !askedCodes.Contains(g.PaperCode)).ToList();
        if (free.Count == 0 || free.Count > MaxChosenCodes
            || fixedGroups.Any(g => g.Kind is not (OcrVatGroupKind.Exempt or OcrVatGroupKind.ZeroRated)))
            return result;
        var exemptLines = noVat.Where(l => l.Rate == ThaiVatTypeRule.ExemptRate).Sum(l => l.Amount);
        var zeroLines = noVat.Where(l => l.Rate == 0m).Sum(l => l.Amount);
        var fixedExempt = fixedGroups.Where(g => g.Kind == OcrVatGroupKind.Exempt).Sum(g => g.Net);
        var fixedZero = fixedGroups.Where(g => g.Kind == OcrVatGroupKind.ZeroRated).Sum(g => g.Net);
        var tol = Math.Max(0.02m, 0.01m * noVat.Count);
        var solutions = new List<int>();
        for (var mask = 0; mask < 1 << free.Count; mask++)
        {
            var e = fixedExempt + free.Where((_, i) => (mask & (1 << i)) != 0).Sum(g => g.Net);
            var z = fixedZero + free.Where((_, i) => (mask & (1 << i)) == 0).Sum(g => g.Net);
            if (Math.Abs(e - exemptLines) <= tol && Math.Abs(z - zeroLines) <= tol) solutions.Add(mask);
        }
        if (solutions.Count == 0) return result;
        for (var i = 0; i < free.Count; i++)
        {
            var bit = 1 << i;
            var asExempt = solutions.All(m => (m & bit) != 0);
            var asZero = solutions.All(m => (m & bit) == 0);
            if (asExempt) result[free[i].PaperCode] = OcrVatGroupKind.Exempt;
            else if (asZero) result[free[i].PaperCode] = OcrVatGroupKind.ZeroRated;
        }
        return result;
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
