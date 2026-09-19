using System.Globalization;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>ที่มาของ<b>ค่าที่อยู่ในฟอร์มจริง</b>ของช่องหนึ่ง</summary>
/// <param name="Field">ชื่อช่องตาม <see cref="OcrFieldKeys"/></param>
/// <param name="ActualValue">ค่าที่ไปป์ไลน์ตัดสินใช้จริง (<c>null</c> = ช่องยังว่าง)</param>
/// <param name="DecidedValue">ค่าที่ <see cref="OcrFieldArbiter"/> จะเลือกถ้าให้มันตัดสิน</param>
/// <param name="Source">ผู้เสนอที่<b>เสนอค่าตรงกับค่าจริง</b> — ไม่มีใครตรง ⇒
/// <see cref="OcrFieldSource.Unknown"/> (ห้ามเดาว่าเป็นของผู้ชนะ)</param>
/// <param name="Agrees"><c>true</c> = ตัวตัดสินเห็นด้วยกับค่าที่ใช้จริง ·
/// <c>null</c> = เทียบไม่ได้ (ช่องว่าง) ไม่ใช่ "ไม่เห็นด้วย"</param>
public sealed record OcrFieldOutcome(
    string Field, string? ActualValue, string? DecidedValue,
    OcrFieldSource Source, decimal Confidence, string? Evidence,
    bool? Agrees, IReadOnlyList<OcrFieldCandidate> Alternatives);

/// <summary>สรุปทั้งใบ — <c>Agreed</c>/<c>Compared</c> คือตัวชี้วัด <c>arbiterAgreed</c></summary>
public sealed record OcrProvenanceReport(
    IReadOnlyList<OcrFieldOutcome> Fields, int Agreed, int Compared)
{
    /// <summary>สัดส่วนที่ตัวตัดสินเห็นด้วยกับค่าที่ใช้จริง — <c>null</c> เมื่อไม่มีช่อง
    /// ให้เทียบเลย (<b>ไม่ใช่ 0</b>: "ยังไม่ได้ตรวจ" กับ "ไม่เห็นด้วยทุกช่อง" คนละเรื่อง)</summary>
    public decimal? ArbiterAgreed => Compared == 0
        ? null
        : Math.Round((decimal)Agreed / Compared, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// **สมุด "ค่านี้มาจากไหน" ที่ตรวจสอบตัวเองได้** (pure, ไม่มี I/O) — D-4 ขั้นที่ 1
///
/// ═══ ปัญหาที่แก้ (DECISION_DOCTRINE §4.2 V1) ═══
/// <para>เดิมหน้า review แสดงผลของ <see cref="OcrFieldArbiter.DecideAll"/> ตรง ๆ ภายใต้
/// หัวข้อ "🧾 ค่านี้มาจากไหน" ทั้งที่ <b>arbiter ไม่ได้ตัดสินอะไรเลย</b> — ค่าที่อยู่ใน
/// ฟอร์มมาจากลำดับบรรทัดในไปป์ไลน์ ⇒ <b>ป้ายที่มาขัดกับค่าที่ผู้ใช้เห็นได้โดยไม่มีอะไรฟ้อง</b>
/// (ผู้ใช้อ่านว่า "ชื่อผู้ขายมาจากทะเบียน" ขณะที่ช่องจริงเป็นค่าที่ engine อ่าน)</para>
///
/// <para>กติกาใหม่: สมุดรายงาน <b>ค่าที่อยู่ในฟอร์มจริง</b> ก่อน แล้วค่อยบอกว่า
/// <b>ผู้เสนอรายไหนเสนอค่านั้น</b> — ไม่มีใครเสนอตรง ⇒ <c>Unknown</c> ("ไม่ทราบที่มา")
/// <b>ห้ามยกผู้ชนะของ arbiter มาสวมเป็นที่มา</b> เพราะนั่นคือการแต่งคำตอบ (F2 ข้อ 3)</para>
///
/// ═══ ตัวชี้วัดที่ทำให้โกหกไม่ได้ ═══
/// <para><c>arbiterAgreed</c> = "ตัวตัดสินเห็นด้วยกับค่าที่ใช้จริงกี่ %" — ตัวเลขนี้
/// <b>ต่ำ</b> แปลว่า arbiter ยังไม่ได้ตัดสินจริง (ไปป์ไลน์เลือกคนละทาง) และเป็นเงื่อนไข
/// ที่ D-4 ตั้งไว้สำหรับขั้นที่ 2: ให้ arbiter ตัดสินทุกช่องได้เมื่อ <c>arbiterAgreed</c>
/// บนชุดกระดาษจริง = 100% เท่านั้น — ตอนนั้นการพลิกเป็น no-op ที่<b>พิสูจน์ได้</b></para>
/// </summary>
public static class OcrFieldProvenance
{
    /// <summary>เทียบ "ค่าเดียวกันไหม" แบบไม่ติดรูปแบบ — ตัวเลข <c>3</c> กับ <c>3.00</c>
    /// คือค่าเดียวกัน (ผู้เสนอบันทึกด้วย <c>"0.00"</c> · ฟอร์มถืออยู่เป็น <c>decimal</c>)
    /// แต่ข้อความเทียบแบบตัดช่องว่าง + ไม่สนตัวพิมพ์</summary>
    public static bool SameValue(string? a, string? b)
    {
        var x = (a ?? string.Empty).Trim();
        var y = (b ?? string.Empty).Trim();
        if (x.Length == 0 || y.Length == 0) return x.Length == y.Length;
        if (decimal.TryParse(x, NumberStyles.Number, CultureInfo.InvariantCulture, out var dx)
            && decimal.TryParse(y, NumberStyles.Number, CultureInfo.InvariantCulture, out var dy))
            return dx == dy;
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="candidates">สมุดผู้เสนอทั้งใบ (<c>OcrExtractedData.FieldCandidates</c>)</param>
    /// <param name="actualValues">ค่าที่อยู่ในฟอร์มจริงต่อช่อง — ผู้เรียกอ่านจาก
    /// <c>extractedData</c> ให้ (helper ตัวนี้ pure: ไม่รู้จักโครงของไปป์ไลน์)
    /// ช่องที่ไม่มีใน dictionary = "ยังไม่ได้ตรวจ" ⇒ ไม่นับเข้าตัวหาร</param>
    public static OcrProvenanceReport Build(
        IEnumerable<OcrFieldCandidate> candidates,
        IReadOnlyDictionary<string, string?> actualValues)
    {
        var all = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        var outcomes = new List<OcrFieldOutcome>();
        int agreed = 0, compared = 0;

        // ใช้ตัวตัดสินตัวเดิม (<see cref="OcrFieldArbiter.DecideAll"/>) ไม่เขียนลูป
        // "วนทุกช่องแล้วตัดสิน" ซ้ำที่นี่ — สองสำเนาจะ drift ทันทีที่ใครแก้กติกาข้างเดียว
        foreach (var decision in OcrFieldArbiter.DecideAll(all))
        {
            var field = decision.Field;

            actualValues.TryGetValue(field, out var actualRaw);
            var actual = string.IsNullOrWhiteSpace(actualRaw) ? null : actualRaw!.Trim();

            // ผู้เสนอที่เสนอ "ค่าที่ใช้จริง" — เลือกรายที่น่าเชื่อที่สุดในบรรดาที่ตรงกัน
            // (ไม่ใช่รายแรกที่เจอ: สองแหล่งเสนอค่าเดียวกันได้ และที่มาที่ควรโชว์คือชั้นบน)
            OcrFieldCandidate? matched = null;
            if (actual != null)
            {
                matched = all
                    .Where(c => string.Equals(c.Field, field, StringComparison.Ordinal)
                                && SameValue(c.Value, actual))
                    .OrderByDescending(c => OcrFieldArbiter.Rank(c.Source))
                    .ThenByDescending(c => c.Confidence)
                    .FirstOrDefault();
            }

            bool? agrees = actual == null ? null : SameValue(decision.Value, actual);
            if (agrees.HasValue)
            {
                compared++;
                if (agrees.Value) agreed++;
            }

            outcomes.Add(new OcrFieldOutcome(
                Field: field,
                ActualValue: actual,
                DecidedValue: decision.Value,
                // ไม่มีใครเสนอค่าที่ใช้จริง ⇒ Unknown. เคสนี้มีความหมาย: แปลว่ามีชั้นใน
                // ไปป์ไลน์ที่เขียนค่าโดย**ไม่ Note()** — คือจุดที่ต้องไปต่อสายถัดไป
                Source: matched?.Source ?? (actual == null ? decision.Source : OcrFieldSource.Unknown),
                Confidence: matched?.Confidence ?? (actual == null ? decision.Confidence : 0m),
                Evidence: matched?.Evidence ?? (actual == null ? decision.Evidence : null),
                Agrees: agrees,
                Alternatives: decision.Alternatives));
        }

        return new OcrProvenanceReport(outcomes, agreed, compared);
    }

    /// <summary>รูปที่ลง <c>OcrScanResult.FieldDecisionsJson</c> — <b>อ็อบเจกต์</b>
    /// (ของเดิมเป็น array) เพราะต้องพกตัวชี้วัดทั้งใบมาด้วย
    ///
    /// <para>หน้าเว็บต้องรับได้ทั้งสองรูป: แถวที่ persist ไว้ก่อนรอบ 184 ยังเป็น array
    /// และ<b>ไม่มี migration</b> (สมุดที่มาเป็นข้อมูลอธิบาย ไม่ใช่ข้อมูลบัญชี —
    /// คำนวณใหม่ทุกครั้งที่สแกนใหม่ ⇒ ปล่อยให้หมดไปเอง)</para>
    ///
    /// <para><c>sourceLabel</c> ถูกส่งมาจากเซิร์ฟเวอร์ด้วย เพื่อให้หน้าเว็บ<b>ไม่ต้องมี
    /// ตารางป้ายสำเนาที่สอง</b> (F2 ข้อ 5: server computes · page displays)</para></summary>
    public static string ToJson(OcrProvenanceReport report)
        => JsonSerializer.Serialize(new
        {
            arbiterAgreed = report.ArbiterAgreed,
            agreed = report.Agreed,
            compared = report.Compared,
            fields = report.Fields.Select(f => new
            {
                field = f.Field,
                // ชื่อ `value` คงไว้ตามสัญญาเดิม แต่ความหมายคมขึ้น: **ค่าที่อยู่ในฟอร์มจริง**
                value = f.ActualValue ?? f.DecidedValue,
                decided = f.DecidedValue,
                agrees = f.Agrees,
                source = f.Source.ToString(),
                sourceLabel = OcrFieldArbiter.SourceLabel(f.Source),
                confidence = f.Confidence,
                evidence = f.Evidence,
                alternatives = f.Alternatives.Select(a => new
                {
                    value = a.Value,
                    source = a.Source.ToString(),
                    sourceLabel = OcrFieldArbiter.SourceLabel(a.Source),
                    confidence = a.Confidence,
                }).ToList(),
            }).ToList(),
        });
}
