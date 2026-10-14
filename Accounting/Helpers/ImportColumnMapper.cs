using System.Text.RegularExpressions;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Import;

namespace Accounting.Helpers;

/// <summary>หนึ่งคอลัมน์ของไฟล์ที่จะจับคู่ — <see cref="SampleValues"/> = ค่าตัวอย่างดิบ (≤ 20 แถวแรกที่ไม่ว่างพอ)</summary>
public sealed record ImportColumnInput(int SourceIndex, string Header, IReadOnlyList<string> SampleValues);

/// <summary>คู่ "หัวคอลัมน์ → field" ที่บริษัทนี้ยืนยันแล้ว (จากแม่แบบ + การยืนยันการจับคู่) — <see cref="Header"/> normalize แล้ว
/// (<see cref="ImportHeaderSignature.Normalize"/>) · <see cref="EntityType"/> = คีย์ normalize ของชนิดข้อมูล</summary>
public sealed record ImportLearnedHeader(string EntityType, string Header, string TargetField, int Weight);

/// <summary>คำตอบของชั้น AI ต่อคอลัมน์ (ครู หรือนักเรียนเมื่อ AI ไม่ถูกเรียก) — <see cref="UsedAi"/> = เรียก AI จริง (ป้าย 🤖 เฉพาะ true)</summary>
public sealed record ImportColumnAiAnswer(int SourceIndex, string? TargetField, decimal Confidence, string? Reasoning, bool UsedAi);

/// <summary>คำขอจับคู่คอลัมน์ — ทุกชั้นเป็นข้อมูลที่ผู้เรียกโหลดมาแล้ว (helper นี้ pure)</summary>
/// <param name="TemplateColumns">📌 แม่แบบของบริษัทที่ signature ตรง (header normalize → field + transform) · null = ไม่มี</param>
/// <param name="Learned">🧠 คู่ที่เรียนไว้ (ทุกชนิดข้อมูลก็ได้ — กรองตาม <paramref name="EntityType"/> ให้เอง)</param>
/// <param name="AiAnswers">🤖 คำตอบชั้น AI (ผ่านด่าน <see cref="ImportColumnMapper.GuardAiAnswers"/> ก่อนใช้เสมอ)</param>
/// <param name="UserAssignments">✋ ผู้ใช้เลือกเอง — ชนะทุกชั้น (TargetField null = ไม่ใช้คอลัมน์นี้)</param>
/// <param name="SourceSystem">โปรแกรมต้นทางที่ผู้ใช้เลือก · null = เดาจากหัวตาราง (<see cref="ImportSourceSeed.Detect"/>)</param>
public sealed record ImportColumnMapRequest(
    string EntityType,
    IReadOnlyList<ImportFieldDto> Targets,
    IReadOnlyList<ImportColumnInput> Columns,
    IReadOnlyList<ImportTemplateColumnDto>? TemplateColumns = null,
    IReadOnlyList<ImportLearnedHeader>? Learned = null,
    IReadOnlyList<ImportColumnAiAnswer>? AiAnswers = null,
    IReadOnlyList<ImportColumnAssignment>? UserAssignments = null,
    string? SourceSystem = null);

/// <summary>ผลจับคู่ทั้งไฟล์</summary>
/// <param name="Columns">แผนต่อคอลัมน์ (ลำดับตามไฟล์) — รูปสัญญา <see cref="ImportColumnPlanDto"/> ส่งหน้าเว็บได้ตรง</param>
/// <param name="UsedAi">มีอย่างน้อยหนึ่งคอลัมน์ที่ AI ตอบจริงและถูกใช้</param>
/// <param name="MissingRequired">field บังคับที่ยังไม่มีคอลัมน์ (target เติมค่าแทนได้ หรือผู้ใช้เลือก)</param>
/// <param name="Uncertain">คอลัมน์ที่ควรถามชั้น AI = ยังไม่จับคู่ (ชั้น AI เติมได้เฉพาะช่องว่าง — ไม่ทับชั้นสูงกว่า) · ไม่รวมที่ผู้ใช้/แม่แบบ/โปรไฟล์บอกว่า "ไม่ใช้"</param>
/// <param name="SourceProfile">โปรไฟล์โปรแกรมต้นทางที่ตรง (ใช้เป็นชั้นคำพ้อง) · null = ไม่ตรงโปรไฟล์ใด</param>
/// <param name="RejectedAiAnswers">คำตอบ AI ที่ถูกด่านปัดตก (field นอก schema · มั่นใจ &lt; 0.70 · คอลัมน์ไม่มีจริง)</param>
public sealed record ImportColumnPlan(
    IReadOnlyList<ImportColumnPlanDto> Columns,
    bool UsedAi,
    IReadOnlyList<ImportFieldDto> MissingRequired,
    IReadOnlyList<ImportColumnInput> Uncertain,
    ImportSourceMatch? SourceProfile,
    int RejectedAiAnswers);

/// <summary>ผลเดาชนิดข้อมูลของไฟล์ (เรียงคะแนนมาก → น้อย)</summary>
public sealed record ImportEntityGuess(string EntityType, string Label, decimal Score, int RequiredMapped, int RequiredTotal, int ColumnsMapped,
    string? SourceSystem);

/// <summary>
/// **ตัวจับคู่คอลัมน์ไฟล์นำเข้า → field ปลายทาง — owner เดียว** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ชั้น (สูง → ต่ำ · ชั้นสูงกว่าชนะเสมอ): ✋ ผู้ใช้ → 📌 แม่แบบบริษัท → ชื่อตรง (field/ป้ายไทย) → 📚 คำพ้อง (<see cref="ImportFieldSynonymSeed"/> +
/// โปรไฟล์โปรแกรมต้นทาง <see cref="ImportSourceSeed"/>) → 🧠 คู่ที่บริษัทยืนยันแล้ว → รูปข้อมูล (เลข 13 หลักผ่าน checksum = เลขภาษี ฯลฯ ·
/// เฉพาะเมื่อไม่กำกวม) → 🤖 AI (ด่าน: field ∈ schema · มั่นใจ ≥ 0.70 · ไม่ทับชั้นสูงกว่า) → ผู้ใช้เลือกเอง</para>
///
/// <para><b>Greedy unique</b>: แต่ละชั้นเรียงคะแนนมาก → น้อย แล้วจับคู่เฉพาะคอลัมน์ที่ยังว่าง <b>และ</b> field ที่ยังว่าง ⇒ สองคอลัมน์ไม่มีทางได้ field เดียวกัน ·
/// คะแนนคำพ้อง/ชื่อตรงถูกปรับด้วย "ค่าในคอลัมน์เข้ากับชนิด field ไหม" (คอลัมน์ "ลูกค้า" ที่มีแต่ Y/N ⇒ เป็นลูกค้า ไม่ใช่ชื่อ)</para>
///
/// <para>แทน <c>ImportExportService.AnalyzeColumnMatch</c> (ทีม B มอบมาที่นี่) และ schema ส่วนตัวของ <c>AiSuggestionController.SuggestImportMapping</c>
/// (มอบแล้ว) · ชั้น AI เรียกผ่าน <c>ImportColumnMatchService</c> → <c>ImportAiAugmenter</c> → orchestrator (กฎเหล็ก #1) — helper นี้ pure:
/// ไม่มี I/O · ไม่ throw · ผลไม่ขึ้นกับลำดับ response (ชั้นตัดสิน ไม่ใช่ใครมาก่อน)</para>
/// </summary>
public static class ImportColumnMapper
{
    /// <summary>ด่านความมั่นใจของคำตอบชั้น AI (กฎเหล็ก #1 — ต่ำกว่านี้ไม่ใช้)</summary>
    public const decimal AiAcceptFloor = 0.70m;
    private const decimal AssignFloor = 0.60m;
    private const int MaxSamples = 5;

    private enum Tier { User = 0, Template = 1, Exact = 2, Synonym = 3, Learned = 4, DataPattern = 5, Ai = 6 }

    private sealed record Cand(int Col, ImportFieldDto Field, decimal Score, Tier Tier, string Reason, string? Detail = null, bool UsedAi = false);

    /// <summary>จับคู่ทั้งไฟล์ตามชั้น — ดูคำอธิบายคลาส</summary>
    public static ImportColumnPlan Resolve(ImportColumnMapRequest req)
    {
        var targets = req.Targets ?? Array.Empty<ImportFieldDto>();
        var byField = targets.GroupBy(t => t.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // คอลัมน์ละหนึ่ง index (index ซ้ำ = ข้อมูลผู้เรียกเสีย — ใช้ตัวแรก ไม่ throw)
        var cols = (req.Columns ?? Array.Empty<ImportColumnInput>()).GroupBy(c => c.SourceIndex).Select(g => g.First()).ToList();
        var entityNorm = ImportExportPermissionScope.NormalizeEntityType(req.EntityType);
        var norm = cols.ToDictionary(c => c.SourceIndex, c => ImportHeaderSignature.Normalize(c.Header));
        var fit = new Dictionary<(int, string), decimal>();
        decimal Fit(ImportColumnInput c, ImportFieldDto f)
        {
            if (!fit.TryGetValue((c.SourceIndex, f.Field), out var v)) fit[(c.SourceIndex, f.Field)] = v = TypeFit(f, c.SampleValues);
            return v;
        }
        decimal Adjust(ImportColumnInput c, ImportFieldDto f, decimal score) => Math.Round(score * (0.5m + 0.5m * Fit(c, f)), 4, MidpointRounding.AwayFromZero);

        var cands = new List<Cand>();
        var decidedIgnore = new Dictionary<int, (Tier Tier, string Reason)>();

        // ✋ ผู้ใช้
        var userCols = new HashSet<int>();
        foreach (var a in req.UserAssignments ?? Array.Empty<ImportColumnAssignment>())
        {
            if (!norm.ContainsKey(a.SourceIndex) || !userCols.Add(a.SourceIndex)) continue;
            if (a.TargetField == null) { decidedIgnore[a.SourceIndex] = (Tier.User, "ผู้ใช้เลือกไม่ใช้คอลัมน์นี้"); continue; }
            if (byField.TryGetValue(a.TargetField, out var f)) cands.Add(new Cand(a.SourceIndex, f, 1m, Tier.User, "ผู้ใช้เลือกเอง"));
        }

        // 📌 แม่แบบ
        var tmpl = (req.TemplateColumns ?? Array.Empty<ImportTemplateColumnDto>())
            .GroupBy(t => ImportHeaderSignature.Normalize(t.Header)).Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var c in cols)
        {
            if (userCols.Contains(c.SourceIndex) || !tmpl.TryGetValue(norm[c.SourceIndex], out var t)) continue;
            if (t.TargetField == null) { decidedIgnore.TryAdd(c.SourceIndex, (Tier.Template, "แม่แบบบอกว่าไม่ใช้คอลัมน์นี้")); continue; }
            if (byField.TryGetValue(t.TargetField, out var f)) cands.Add(new Cand(c.SourceIndex, f, 0.99m, Tier.Template, "ตรงกับแม่แบบที่เคยใช้"));
        }

        // โปรไฟล์โปรแกรมต้นทาง (ชั้นคำพ้อง)
        var profile = DetectProfile(req, cols);
        var profileMap = profile?.Profile.ColumnMap
            .GroupBy(kv => ImportHeaderSignature.Normalize(kv.Key)).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        // คอลัมน์ที่โปรไฟล์บอกว่า "ของระบบเดิม ไม่ใช้" (รหัสลูกค้าของ Express ฯลฯ) — ไม่ให้คำพ้องบางส่วน ("รหัสลูกค้า" ⊃ "ลูกค้า") ลากไป field อื่น ·
        // ผู้ใช้/แม่แบบยังชนะ (ข้ามคอลัมน์ที่ชั้นนั้นตัดสินแล้ว)
        if (profileMap != null)
            foreach (var c in cols)
                if (!userCols.Contains(c.SourceIndex) && !decidedIgnore.ContainsKey(c.SourceIndex)
                    && !cands.Any(x => x.Col == c.SourceIndex && x.Tier == Tier.Template)
                    && profileMap.TryGetValue(norm[c.SourceIndex], out var pf0) && pf0 == null)
                    decidedIgnore[c.SourceIndex] = (Tier.Synonym, $"รูปแบบ {profile!.Label}: คอลัมน์ของระบบเดิม ไม่ใช้");

        var learned = (req.Learned ?? Array.Empty<ImportLearnedHeader>())
            .Where(l => ImportExportPermissionScope.NormalizeEntityType(l.EntityType) == entityNorm)
            .GroupBy(l => ImportHeaderSignature.Normalize(l.Header), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Weight).First(), StringComparer.Ordinal);

        foreach (var c in cols)
        {
            var h = norm[c.SourceIndex];
            if (h.Length == 0) continue;
            foreach (var f in targets)
            {
                // ชื่อตรง
                if (h == ImportHeaderSignature.Normalize(f.Field))
                    cands.Add(new Cand(c.SourceIndex, f, Adjust(c, f, 1m), Tier.Exact, "ชื่อคอลัมน์ตรงกับ field"));
                else if (h == ImportHeaderSignature.Normalize(f.Label))
                    cands.Add(new Cand(c.SourceIndex, f, Adjust(c, f, 0.97m), Tier.Exact, "ชื่อคอลัมน์ตรงกับชื่อไทยของ field"));

                // 📚 คำพ้อง
                decimal best = 0m; string? why = null;
                foreach (var syn in ImportFieldSynonymSeed.For(req.EntityType, f.Field))
                {
                    var s = ImportHeaderSignature.Normalize(syn);
                    if (s.Length == 0) continue;
                    decimal sc;
                    if (h == s) sc = 0.92m;
                    // บางส่วน: คำพ้องอังกฤษสั้น ("cr" · "ref") อยู่ในคำอื่นได้ง่าย ⇒ อังกฤษ ≥ 4 ตัว · ไทย ≥ 2 ตัว
                    else if (s.Length >= (s.All(char.IsAscii) ? 4 : 2) && h.Length > s.Length && h.Contains(s, StringComparison.Ordinal))
                        sc = Math.Min(0.88m, 0.70m + 0.18m * s.Length / h.Length);
                    else continue;
                    if (sc > best) { best = sc; why = syn; }
                }
                if (profileMap != null && profileMap.TryGetValue(h, out var pf) && pf != null
                    && string.Equals(pf, f.Field, StringComparison.OrdinalIgnoreCase))
                    cands.Add(new Cand(c.SourceIndex, f, Adjust(c, f, 0.93m), Tier.Synonym, $"หัวคอลัมน์รูปแบบ {profile!.Label}", profile.Label));
                else if (best > 0m)
                    cands.Add(new Cand(c.SourceIndex, f, Adjust(c, f, best), Tier.Synonym, $"คำพ้อง \"{why}\""));
            }
            // 🧠 เรียนรู้
            if (learned.TryGetValue(h, out var l) && byField.TryGetValue(l.TargetField, out var lf))
                cands.Add(new Cand(c.SourceIndex, lf, Adjust(c, lf, Math.Min(0.95m, 0.86m + 0.01m * l.Weight)), Tier.Learned,
                    "บริษัทนี้เคยยืนยันคู่นี้แล้ว"));
        }

        // ── จับคู่ทีละชั้น (greedy unique) ──
        var assigned = new Dictionary<int, Cand>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Pass(IEnumerable<Cand> tier)
        {
            foreach (var x in tier.OrderByDescending(x => x.Score).ThenBy(x => x.Col).ThenBy(x => x.Field.Field, StringComparer.Ordinal))
            {
                if (x.Score < AssignFloor && x.Tier is not (Tier.User or Tier.Template)) continue;
                if (assigned.ContainsKey(x.Col) || decidedIgnore.ContainsKey(x.Col) || taken.Contains(x.Field.Field)) continue;
                assigned[x.Col] = x;
                taken.Add(x.Field.Field);
            }
        }
        foreach (var t in new[] { Tier.User, Tier.Template, Tier.Exact, Tier.Synonym, Tier.Learned })
            Pass(cands.Where(c => c.Tier == t));

        // รูปข้อมูล — เฉพาะเมื่อไม่กำกวม (field ที่เข้ากับค่าในคอลัมน์เหลือตัวเดียวในชั้นความเฉพาะสูงสุด)
        foreach (var c in cols.Where(c => !assigned.ContainsKey(c.SourceIndex) && !decidedIgnore.ContainsKey(c.SourceIndex)))
        {
            var fits = targets.Where(f => !taken.Contains(f.Field) && PatternRank(f.Type) < int.MaxValue)
                .Select(f => (F: f, Fit: PatternFit(f, c.SampleValues))).Where(x => x.Fit >= 0.8m).ToList();
            if (fits.Count == 0) continue;
            var top = fits.Min(x => PatternRank(x.F.Type));
            var group = fits.Where(x => PatternRank(x.F.Type) == top).ToList();
            var dp = new Cand(c.SourceIndex, group[0].F, Math.Round(0.60m + 0.15m * group[0].Fit, 4, MidpointRounding.AwayFromZero), Tier.DataPattern,
                group.Count == 1 ? "ค่าในคอลัมน์ตรงรูปแบบของ field นี้" : "ค่าในคอลัมน์เข้ากับหลาย field — เลือกเอง");
            cands.Add(dp);
            if (group.Count == 1) { assigned[c.SourceIndex] = dp; taken.Add(dp.Field.Field); }
            else foreach (var g in group.Skip(1))
                cands.Add(dp with { Field = g.F, Score = Math.Round(0.60m + 0.15m * g.Fit, 4, MidpointRounding.AwayFromZero) });
        }

        // 🤖 AI — ด่านก่อน แล้วเติมเฉพาะช่องที่ชั้นสูงกว่ายังว่าง
        var guarded = GuardAiAnswers(req.AiAnswers ?? Array.Empty<ImportColumnAiAnswer>(), targets, cols.Select(c => c.SourceIndex));
        var rejected = (req.AiAnswers?.Count(a => a.TargetField != null) ?? 0) - guarded.Count;
        var aiCands = guarded.Select(a => new Cand(a.SourceIndex, byField[a.TargetField!], a.Confidence, Tier.Ai,
            string.IsNullOrWhiteSpace(a.Reasoning) ? (a.UsedAi ? "AI แนะนำ" : "ระบบแนะนำ") : a.Reasoning!, UsedAi: a.UsedAi)).ToList();
        cands.AddRange(aiCands);
        Pass(aiCands);

        // ── ประกอบผล ──
        var plans = new List<ImportColumnPlanDto>(cols.Count);
        var uncertain = new List<ImportColumnInput>();
        var usedAi = false;
        foreach (var c in cols)
        {
            assigned.TryGetValue(c.SourceIndex, out var pick);
            var alts = cands.Where(x => x.Col == c.SourceIndex && (pick == null || !string.Equals(x.Field.Field, pick.Field.Field, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Field.Field, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(x => x.Tier).ThenByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score).Take(3)
                .Select(x => new ImportColumnCandidateDto(x.Field.Field, x.Field.Label, x.Score, ToSource(x.Tier, x.UsedAi), x.Reason))
                .ToList();
            var samples = c.SampleValues.Where(v => !string.IsNullOrWhiteSpace(v)).Take(MaxSamples).ToList();
            var userTransform = req.UserAssignments?.FirstOrDefault(a => a.SourceIndex == c.SourceIndex)?.Transform;
            if (pick != null)
            {
                var src = ToSource(pick.Tier, pick.UsedAi);
                if (pick.Tier == Tier.Ai && pick.UsedAi) usedAi = true;
                var transform = userTransform ?? (pick.Tier is Tier.Template or Tier.User && tmpl.TryGetValue(norm[c.SourceIndex], out var tt)
                    && string.Equals(tt.TargetField, pick.Field.Field, StringComparison.OrdinalIgnoreCase) ? tt.Transform : null);
                plans.Add(new ImportColumnPlanDto(c.SourceIndex, c.Header, pick.Field.Field, pick.Field.Label, src,
                    SourceLabel(src, pick.Detail), pick.Score, pick.Tier == Tier.Ai && pick.UsedAi, samples, alts, transform));
            }
            else if (decidedIgnore.TryGetValue(c.SourceIndex, out var ig))
            {
                var src = ToSource(ig.Tier, false);
                plans.Add(new ImportColumnPlanDto(c.SourceIndex, c.Header, null, null, src,
                    SourceLabel(src, ig.Tier == Tier.Synonym ? profile?.Label : null) + " — ไม่ใช้คอลัมน์นี้", 1m, false, samples, alts, userTransform));
            }
            else
            {
                plans.Add(new ImportColumnPlanDto(c.SourceIndex, c.Header, null, null, ImportMapSource.None, SourceLabel(ImportMapSource.None, null),
                    0m, false, samples, alts, userTransform));
                if (ImportHeaderSignature.Normalize(c.Header).Length > 0 || samples.Count > 0) uncertain.Add(c);
            }
        }

        var missing = targets.Where(t => t.Required && !taken.Contains(t.Field)).ToList();
        return new ImportColumnPlan(plans, usedAi, missing, uncertain, profile, Math.Max(0, rejected));
    }

    /// <summary>
    /// **ด่านคำตอบชั้น AI** (กฎเหล็ก #1 anti-hallucination) — รับเฉพาะ: field ∈ schema ของชนิดนี้ (คืนสะกดของ schema) · มั่นใจ ≥ <see cref="AiAcceptFloor"/> ·
    /// คอลัมน์มีจริงในไฟล์ · คอลัมน์ละหนึ่งคำตอบ (มั่นใจสุด) · field ละหนึ่งคอลัมน์ (มั่นใจสุด) · คำตอบ "ไม่ใช้" (field null) ไม่ถูกนับเป็นการจับคู่
    /// <para>การ "ไม่ทับชั้นสูงกว่า" ทำใน <see cref="Resolve"/> (เติมเฉพาะช่องที่ยังว่าง)</para>
    /// </summary>
    public static IReadOnlyList<ImportColumnAiAnswer> GuardAiAnswers(IEnumerable<ImportColumnAiAnswer> answers, IReadOnlyList<ImportFieldDto> targets,
        IEnumerable<int> sourceIndexes)
    {
        var canonical = targets.GroupBy(t => t.Field, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Field, StringComparer.OrdinalIgnoreCase);
        var cols = new HashSet<int>(sourceIndexes);
        return answers
            .Where(a => a.TargetField != null && canonical.ContainsKey(a.TargetField.Trim()) && a.Confidence >= AiAcceptFloor && a.Confidence <= 1m
                        && cols.Contains(a.SourceIndex))
            .Select(a => a with { TargetField = canonical[a.TargetField!.Trim()] })
            .GroupBy(a => a.SourceIndex).Select(g => g.OrderByDescending(a => a.Confidence).First())
            .GroupBy(a => a.TargetField!, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(a => a.Confidence).ThenBy(a => a.SourceIndex).First())
            .OrderBy(a => a.SourceIndex)
            .ToList();
    }

    /// <summary>
    /// คำตอบรูปมาตรฐานของ ImportColumnMatch — <c>{"mappings":[{"sourceIndex":0,"targetField":"Name"},…]}</c> เรียงตามคอลัมน์ ·
    /// ตัวเดียวที่ทั้งนักเรียน (LocalModelAnswer) และการบันทึกผู้ใช้ยืนยัน (UserChosenAnswer) ใช้ ⇒ สถิติความแม่น (เทียบสตริงตรง) มีความหมาย
    /// </summary>
    public static string CanonicalAnswer(IEnumerable<(int SourceIndex, string? TargetField)> pairs)
        => ImportJson.Serialize(new CanonicalMappings(pairs
            .GroupBy(p => p.SourceIndex).Select(g => g.First())
            .OrderBy(p => p.SourceIndex)
            .Select(p => new CanonicalMapping(p.SourceIndex, string.IsNullOrWhiteSpace(p.TargetField) ? null : p.TargetField.Trim()))
            .ToList()));

    private sealed record CanonicalMapping(int SourceIndex, string? TargetField);
    private sealed record CanonicalMappings(IReadOnlyList<CanonicalMapping> Mappings);

    /// <summary>
    /// เดาชนิดข้อมูลของไฟล์ — จับคู่กับทุกชนิดใน <see cref="ImportSchemas"/> (ชั้น local เท่านั้น) แล้วให้คะแนน = 0.7 × สัดส่วน field บังคับที่ได้คอลัมน์
    /// + 0.3 × สัดส่วนคอลัมน์ที่จับคู่ได้ · โปรไฟล์โปรแกรมต้นทางที่ตรง +0.05 · ผู้ใช้แก้ chip ได้เสมอ (ระบบแนะนำ ผู้ใช้ตัดสิน)
    /// </summary>
    public static IReadOnlyList<ImportEntityGuess> GuessEntityType(IReadOnlyList<ImportColumnInput> columns,
        IReadOnlyList<ImportLearnedHeader>? learned = null)
    {
        var result = new List<ImportEntityGuess>();
        var nonEmpty = Math.Max(1, columns.Count(c => ImportHeaderSignature.Normalize(c.Header).Length > 0));
        foreach (var et in ImportSchemas.EntityTypes)
        {
            var fields = ImportSchemas.For(et)!;
            var plan = Resolve(new ImportColumnMapRequest(et, fields, columns, Learned: learned));
            var required = fields.Count(f => f.Required);
            var reqMapped = required - plan.MissingRequired.Count;
            // ชั้นรูปข้อมูลไม่นับ (เดาชนิดจากค่าอย่างเดียวกว้างเกิน — ตัวเลขเข้าได้ทุกชนิด)
            var mapped = plan.Columns.Count(p => p.TargetField != null && p.Source != ImportMapSource.DataPattern);
            var score = 0.7m * (required == 0 ? 0m : (decimal)reqMapped / required) + 0.3m * Math.Min(1m, (decimal)mapped / nonEmpty)
                        + (plan.SourceProfile != null ? 0.05m : 0m);
            result.Add(new ImportEntityGuess(et, ImportSchemas.EntityLabel(et), Math.Round(Math.Min(1m, score), 4, MidpointRounding.AwayFromZero),
                reqMapped, required, mapped, plan.SourceProfile?.Profile.SourceSystem));
        }
        return result.OrderByDescending(g => g.Score).ThenByDescending(g => g.ColumnsMapped).ThenBy(g => ImportSchemasOrder(g.EntityType)).ToList();
    }

    private static int ImportSchemasOrder(string entityType)
    {
        for (var i = 0; i < ImportSchemas.EntityTypes.Count; i++)
            if (ImportSchemas.EntityTypes[i] == entityType) return i;
        return int.MaxValue;
    }

    private static ImportSourceMatch? DetectProfile(ImportColumnMapRequest req, IReadOnlyList<ImportColumnInput> cols)
    {
        var headers = cols.Select(c => c.Header).ToList();
        var detected = ImportSourceSeed.Detect(req.EntityType, headers);
        if (detected == null || string.IsNullOrWhiteSpace(req.SourceSystem)) return detected;
        // ผู้ใช้เลือกโปรแกรมต้นทางเอง ⇒ ใช้โปรไฟล์ของโปรแกรมนั้นเท่านั้น (ไม่ทับคำตัดสินผู้ใช้)
        return string.Equals(detected.Profile.SourceSystem, req.SourceSystem.Trim(), StringComparison.OrdinalIgnoreCase) ? detected : null;
    }

    private static ImportMapSource ToSource(Tier t, bool usedAi) => t switch
    {
        Tier.User => ImportMapSource.User,
        Tier.Template => ImportMapSource.Template,
        Tier.Exact => ImportMapSource.Exact,
        Tier.Synonym => ImportMapSource.Synonym,
        Tier.Learned => ImportMapSource.Learned,
        Tier.DataPattern => ImportMapSource.DataPattern,
        // นักเรียนตอบแทนครู (AI ไม่ถูกเรียก) = ระบบเรียนรู้ ไม่ใช่ AI — ป้ายซื่อสัตย์ (กฎเหล็ก #1)
        Tier.Ai => usedAi ? ImportMapSource.Ai : ImportMapSource.Learned,
        _ => ImportMapSource.None,
    };

    /// <summary>ป้ายแหล่งการจับคู่ (ไทย · หน้าเว็บแสดงตรง — F2 ข้อ 5)</summary>
    private static string SourceLabel(ImportMapSource s, string? detail) => s switch
    {
        ImportMapSource.User => "✋ ผู้ใช้เลือก",
        ImportMapSource.Template => "📌 แม่แบบ",
        ImportMapSource.Exact => "⚙️ ชื่อตรง",
        ImportMapSource.Synonym => detail == null ? "📚 คำพ้อง" : $"📚 คำพ้อง (รูปแบบ {detail})",
        ImportMapSource.Learned => "🧠 เรียนรู้จากที่เคยยืนยัน",
        ImportMapSource.DataPattern => "⚙️ ระบบแนะนำจากรูปข้อมูล",
        ImportMapSource.Ai => "🤖 AI แนะนำ",
        _ => "ยังไม่จับคู่ — เลือก field หรือปล่อยว่างถ้าไม่ใช้",
    };

    // ═══════════════════════ ค่าในคอลัมน์เข้ากับชนิด field ไหม ═══════════════════════

    private static readonly Regex DateLike = new(@"[/\-.]|[A-Za-zก-๙]", RegexOptions.Compiled);
    private static readonly Regex BranchLike = new(@"^0[0-9]{4}$", RegexOptions.Compiled);

    private static IReadOnlyList<string> NonEmpty(IReadOnlyList<string> samples)
        => samples.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(20).ToList();

    private static decimal Share(IReadOnlyList<string> v, Func<string, bool> ok)
        => v.Count == 0 ? 1m : Math.Round((decimal)v.Count(ok) / v.Count, 4, MidpointRounding.AwayFromZero);

    /// <summary>0–1 ค่าในคอลัมน์เข้ากับชนิด field — ไม่มีตัวอย่าง = 1 (ไม่ลงโทษ) · ข้อความ = 1 ยกเว้นค่าทั้งหมดเป็น ใช่/ไม่ใช่</summary>
    private static decimal TypeFit(ImportFieldDto f, IReadOnlyList<string> samples)
    {
        var v = NonEmpty(samples);
        if (v.Count == 0) return 1m;
        switch (f.Type)
        {
            case ImportFieldType.Text:
                return v.All(s => ImportValueNormalizer.Flag(s).Ok && !s.All(char.IsDigit)) ? 0.2m : 1m;
            case ImportFieldType.Flag:
                return Share(v, s => ImportValueNormalizer.Flag(s).Ok);
            case ImportFieldType.Date:
                return Share(v, s => ImportValueNormalizer.Date(s).Ok);
            case ImportFieldType.Amount:
                return Share(v, s => ImportValueNormalizer.Amount(s).Ok);
            case ImportFieldType.Decimal:
            case ImportFieldType.Integer:
                return Share(v, s => ImportValueNormalizer.Number(s).Ok || ImportValueNormalizer.Amount(s).Ok);
            case ImportFieldType.TaxId:
                return Share(v, s => ImportValueNormalizer.TaxId(s).Value != null || ImportValueNormalizer.TaxId(s).IssueCode is "taxid-leading-zero" or "taxid-invalid"
                                     && s.Count(char.IsDigit) >= 12);
            case ImportFieldType.Email:
                return Share(v, s => s.Contains('@'));
            case ImportFieldType.Phone:
                return Share(v, s => s.Count(char.IsDigit) >= 9);
            case ImportFieldType.BranchCode:
                return Share(v, s => ImportValueNormalizer.BranchCode(s).Ok);
            case ImportFieldType.VatRate:
                return Share(v, s => ImportValueNormalizer.VatRate(s).Ok);
            case ImportFieldType.Enum:
                if (f.AllowedValues is not { Count: > 0 } allowed) return 1m;
                // ค่าภาษาไทย ("ลูกค้า") แปลงด้วย valueMap ของแม่แบบได้ ⇒ ลงโทษครึ่งเดียว
                var share = Share(v, s => allowed.Any(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)));
                return Math.Max(0.5m, share);
            default:
                return 1m;
        }
    }

    /// <summary>ความเฉพาะของรูปข้อมูล (น้อย = เฉพาะกว่า) · int.MaxValue = ชนิดที่ไม่เดาจากค่า (ข้อความ/enum/รหัส)</summary>
    private static int PatternRank(ImportFieldType t) => t switch
    {
        ImportFieldType.TaxId or ImportFieldType.Email => 1,
        ImportFieldType.Date => 2,
        ImportFieldType.Phone or ImportFieldType.BranchCode => 3,
        ImportFieldType.Flag => 4,
        ImportFieldType.VatRate => 5,
        ImportFieldType.Amount or ImportFieldType.Decimal or ImportFieldType.Integer => 6,
        _ => int.MaxValue,
    };

    /// <summary>ค่าในคอลัมน์ "ตรงรูป" ของ field แบบเข้มสำหรับชั้นรูปข้อมูล (กันเดาผิด: ยอด 15000 ไม่ใช่วันที่ serial · 1/0 ไม่ใช่ ใช่/ไม่ใช่)</summary>
    private static decimal PatternFit(ImportFieldDto f, IReadOnlyList<string> samples)
    {
        var v = NonEmpty(samples);
        if (v.Count < 2) return 0m;
        return f.Type switch
        {
            ImportFieldType.TaxId => Share(v, s => ImportValueNormalizer.TaxId(s).Value != null),
            ImportFieldType.Email => Share(v, s => ImportValueNormalizer.Email(s).Ok),
            ImportFieldType.Date => Share(v, s => DateLike.IsMatch(s) && ImportValueNormalizer.Date(s).Ok),
            ImportFieldType.Phone => Share(v, s => ImportValueNormalizer.Phone(s).IssueCode == null && !s.All(char.IsDigit) || s.StartsWith('0') && s.Length is 9 or 10 && s.All(char.IsDigit)),
            ImportFieldType.BranchCode => Share(v, s => BranchLike.IsMatch(s) || TaxBranchCode.IsHeadOfficeName(s)),
            ImportFieldType.Flag => v.Any(s => !s.All(char.IsDigit)) ? Share(v, s => ImportValueNormalizer.Flag(s).Ok) : 0m,
            ImportFieldType.VatRate => v.Any(s => s.EndsWith('%')) ? Share(v, s => ImportValueNormalizer.VatRate(s).Ok) : 0m,
            ImportFieldType.Amount or ImportFieldType.Decimal or ImportFieldType.Integer
                => Share(v, s => ImportValueNormalizer.Amount(s).Ok && !s.StartsWith('0') || s == "0" || s.StartsWith("0.", StringComparison.Ordinal)),
            _ => 0m,
        };
    }

}
