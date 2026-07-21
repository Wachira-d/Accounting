using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Ai;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Auto-assigns a Project to each OCR-extracted invoice line by matching it
/// against the structured metadata an external system (e.g. a project /
/// materials-ordering system like "Mangkorn") uploads ALONGSIDE the document.
/// The metadata lists every ordered material together with its originating
/// project; we link each OCR'd line back to its order so the resulting
/// DocumentLine.ProjectId is pre-selected — the user no longer hand-picks the
/// project for cost allocation.
///
/// Strategy — deterministic first, AI only when genuinely ambiguous:
///   1. Resolve every distinct project referenced in the metadata to a real
///      Project row (by Code → ExternalId → Name), scoped to the company so a
///      partner can never attribute cost to a project that isn't theirs.
///   2. SINGLE-PROJECT SHORTCUT — when the whole payload references exactly one
///      resolved project, stamp it on every line (robust against OCR text
///      noise; the common "one PO = one job" case).
///   3. PER-LINE MATCH — otherwise score each OCR line against each order
///      (exact amount &gt; unit-price/qty &gt; material-name similarity) and
///      take the best hit above a floor.
///   4. AI FALLBACK — lines still unresolved while ≥2 projects are in play are
///      handed to the AI augmenter, which picks the best order by description.
///
/// Every stage is defensive: a malformed payload, an unmatched line, or an AI
/// outage simply leaves ProjectId null (manual pick, exactly as before) — it
/// never breaks the scan.
/// </summary>
internal sealed class OcrMetadataProjectMatcher
{
    private readonly AccountingDbContext _db;
    private readonly IOcrAiAugmenter? _ai;
    private readonly ILogger _logger;

    public OcrMetadataProjectMatcher(AccountingDbContext db, IOcrAiAugmenter? ai, ILogger logger)
    { _db = db; _ai = ai; _logger = logger; }

    /// <summary>A single ordered item parsed from the partner payload.</summary>
    private sealed record OrderItem(
        string? Material, decimal? Quantity, decimal? UnitPrice, decimal? Amount,
        string? ProjectCode, string? ProjectName, string? ProjectExternalId);

    /// <summary>A metadata order whose project has been resolved to a real row.</summary>
    private sealed record ResolvedOrder(OrderItem Order, Guid ProjectId, string ProjectName);

    /// <summary>
    /// Mutates <paramref name="items"/> in place, setting ProjectId/ProjectName
    /// on each line we can confidently link to a project. Returns a short trace
    /// for the scan's ReasoningTrace panel. Safe to call with any string.
    /// </summary>
    public async Task<List<string>> ApplyAsync(
        Guid companyId, Guid scanResultId, string? metadataJson,
        List<OcrExtractedLineItem> items, CancellationToken ct = default)
    {
        var trace = new List<string>();
        if (string.IsNullOrWhiteSpace(metadataJson) || items.Count == 0) return trace;

        var orders = ParseOrders(metadataJson);
        if (orders.Count == 0)
        {
            trace.Add("metadata มีแต่ไม่พบรายการสั่งซื้อ (orders) — ข้ามการจับคู่โครงการ");
            return trace;
        }

        // ── 1. Resolve each distinct project reference to a real Project ──
        // (auto-creates the project from the metadata when none exists yet, so
        //  the partner's projects mirror into NextAcc on first sync).
        var externalSystem = ParseTopLevelString(metadataJson, "source", "system");
        var resolved = await ResolveOrdersAsync(companyId, orders, externalSystem, trace, ct);
        if (resolved.Count == 0)
        {
            trace.Add($"พบ {orders.Count} รายการใน metadata แต่จับคู่/สร้างโครงการในระบบไม่ได้ (ขาดรหัส/ชื่อโครงการ)");
            return trace;
        }

        var distinctProjects = resolved.Select(r => r.ProjectId).Distinct().ToList();

        // ── 2. Single-project shortcut ──
        if (distinctProjects.Count == 1)
        {
            var only = resolved[0];
            var n = 0;
            foreach (var it in items)
            {
                if (it.ProjectId.HasValue) continue;   // never clobber a manual pick
                it.ProjectId = only.ProjectId;
                it.ProjectName = only.ProjectName;
                n++;
            }
            trace.Add($"ทุกรายการอยู่โครงการเดียว → เลือก \"{only.ProjectName}\" ให้ {n} บรรทัดอัตโนมัติ");
            return trace;
        }

        // ── 3. Per-line deterministic match ──
        var unmatched = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.ProjectId.HasValue) continue;

            ResolvedOrder? best = null;
            double bestScore = 0;
            foreach (var r in resolved)
            {
                var s = Score(it, r.Order);
                if (s > bestScore) { bestScore = s; best = r; }
            }

            if (best != null && bestScore >= MatchFloor)
            {
                it.ProjectId = best.ProjectId;
                it.ProjectName = best.ProjectName;
                trace.Add($"บรรทัด {i + 1} \"{Short(it.Description)}\" → \"{best.ProjectName}\" (คะแนน {bestScore:0})");
            }
            else
            {
                unmatched.Add(i);
            }
        }

        // ── 4. AI fallback for the still-unmatched lines ──
        if (unmatched.Count > 0 && _ai != null)
            await AiResolveAsync(companyId, scanResultId, items, resolved, unmatched, trace, ct);
        else if (unmatched.Count > 0)
            trace.Add($"{unmatched.Count} บรรทัดจับคู่โครงการอัตโนมัติไม่ได้ — ต้องเลือกเอง");

        return trace;
    }

    // Amount match alone clears the floor; a strong name match alone clears it too.
    private const double MatchFloor = 40;

    private static double Score(OcrExtractedLineItem it, OrderItem o)
    {
        double s = 0;
        if (Close(it.Amount, o.Amount)) s += 100;
        if (Close(it.UnitPrice, o.UnitPrice)) s += 30;
        if (Close(it.Quantity, o.Quantity)) s += 20;
        s += NameSimilarity(it.Description, o.Material) * 60;
        return s;
    }

    /// <summary>Two money/qty values are "the same" within 1% or ฿0.5.</summary>
    private static bool Close(decimal? a, decimal? b)
    {
        if (!a.HasValue || !b.HasValue || a.Value <= 0 || b.Value <= 0) return false;
        var diff = Math.Abs(a.Value - b.Value);
        var tol = Math.Max(0.5m, Math.Max(a.Value, b.Value) * 0.01m);
        return diff <= tol;
    }

    /// <summary>0..1 similarity tuned for Thai (no word spaces): substring
    /// containment wins outright, otherwise character-bigram Dice coefficient.</summary>
    private static double NameSimilarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x.Contains(y) || y.Contains(x)) return 1.0;

        var bx = Bigrams(x);
        var by = Bigrams(y);
        if (bx.Count == 0 || by.Count == 0) return x == y ? 1.0 : 0;
        var inter = 0;
        foreach (var g in bx) if (by.Contains(g)) inter++;
        return 2.0 * inter / (bx.Count + by.Count);
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        for (var i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
        return set;
    }

    private static string Normalize(string? s)
        => string.IsNullOrEmpty(s)
            ? ""
            : new string(s.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray()).ToLowerInvariant();

    private static string Short(string? s)
        => string.IsNullOrEmpty(s) ? "-" : (s.Length > 40 ? s[..40] + "…" : s);

    // ── Parsing ──────────────────────────────────────────────────────────
    private List<OrderItem> ParseOrders(string json)
    {
        var list = new List<OrderItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Accept either { "orders": [...] } (or "items") or a bare array.
            JsonElement arr = default;
            var haveArr = false;
            if (root.ValueKind == JsonValueKind.Array) { arr = root; haveArr = true; }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if ((root.TryGetProperty("orders", out var a) || root.TryGetProperty("items", out a))
                    && a.ValueKind == JsonValueKind.Array)
                { arr = a; haveArr = true; }
            }
            if (!haveArr) return list;

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                list.Add(new OrderItem(
                    Material: Str(el, "material", "description", "name", "itemName"),
                    Quantity: Dec(el, "quantity", "qty"),
                    UnitPrice: Dec(el, "unitPrice", "price"),
                    Amount: Dec(el, "amount", "total", "lineTotal"),
                    ProjectCode: Str(el, "projectCode", "projectNo"),
                    ProjectName: Str(el, "projectName", "project"),
                    ProjectExternalId: StrOrNum(el, "projectId", "projectExternalId")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OcrMetadataProjectMatcher: metadata JSON parse failed");
        }
        return list;
    }

    private static string? Str(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        return null;
    }

    /// <summary>Read a value that may arrive as either a JSON string or number.</summary>
    private static string? StrOrNum(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!.Trim();
                if (v.ValueKind == JsonValueKind.Number)
                    return v.GetRawText();
            }
        return null;
    }

    private static decimal? Dec(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                if (v.ValueKind == JsonValueKind.String
                    && decimal.TryParse(v.GetString(), out var ds)) return ds;
            }
        return null;
    }

    // A project row we match against (and can append freshly-created rows to).
    private sealed record ProjectRow(Guid Id, string Code, string Name, string? ExternalId);

    // ── Project resolution (with auto-create) ────────────────────────────
    private async Task<List<ResolvedOrder>> ResolveOrdersAsync(
        Guid companyId, List<OrderItem> orders, string? externalSystem,
        List<string> trace, CancellationToken ct)
    {
        // Pull the company's projects once; match in memory (project counts are
        // small per tenant, and we need flexible Code/ExternalId/Name fallback).
        var projects = (await _db.Set<Project>().AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted)
            .Select(p => new { p.Id, p.Code, p.Name, p.ExternalId })
            .ToListAsync(ct))
            .Select(p => new ProjectRow(p.Id, p.Code, p.Name, p.ExternalId))
            .ToList();

        var result = new List<ResolvedOrder>();
        // Cache per (code|extId|name) key so we don't re-scan / re-create.
        var cache = new Dictionary<string, (Guid Id, string Name)?>(StringComparer.OrdinalIgnoreCase);

        foreach (var o in orders)
        {
            var key = $"{o.ProjectCode}|{o.ProjectExternalId}|{o.ProjectName}";
            if (!cache.TryGetValue(key, out var hit))
            {
                hit = null;
                // 1) exact project Code
                if (!string.IsNullOrWhiteSpace(o.ProjectCode))
                {
                    var p = projects.FirstOrDefault(x =>
                        string.Equals(x.Code, o.ProjectCode, StringComparison.OrdinalIgnoreCase));
                    if (p != null) hit = (p.Id, p.Name);
                }
                // 2) partner ExternalId
                if (hit == null && !string.IsNullOrWhiteSpace(o.ProjectExternalId))
                {
                    var p = projects.FirstOrDefault(x =>
                        !string.IsNullOrEmpty(x.ExternalId)
                        && string.Equals(x.ExternalId, o.ProjectExternalId, StringComparison.OrdinalIgnoreCase));
                    if (p != null) hit = (p.Id, p.Name);
                }
                // 3) exact project Name
                if (hit == null && !string.IsNullOrWhiteSpace(o.ProjectName))
                {
                    var p = projects.FirstOrDefault(x =>
                        string.Equals(x.Name?.Trim(), o.ProjectName, StringComparison.OrdinalIgnoreCase));
                    if (p != null) hit = (p.Id, p.Name);
                }
                // 4) AUTO-CREATE — no existing project matched, but the metadata
                //    carries enough identity (a name and/or code) to mirror the
                //    partner's project into NextAcc. The new row is tracked and
                //    persisted by the caller's later SaveChanges (same context).
                if (hit == null && (!string.IsNullOrWhiteSpace(o.ProjectName) || !string.IsNullOrWhiteSpace(o.ProjectCode)))
                {
                    var created = CreateProject(companyId, o, externalSystem, projects);
                    projects.Add(created);                 // so sibling orders match it
                    hit = (created.Id, created.Name);
                    trace.Add($"สร้างโครงการใหม่จาก metadata: \"{created.Name}\" (รหัส {created.Code})");
                }
                cache[key] = hit;
            }
            if (hit != null) result.Add(new ResolvedOrder(o, hit.Value.Id, hit.Value.Name));
        }
        return result;
    }

    /// <summary>Mirror a partner project into NextAcc from the order metadata.
    /// Tracked-only (no SaveChanges) — the OCR scan's later save commits it
    /// atomically with the assigned line ProjectIds.</summary>
    private ProjectRow CreateProject(
        Guid companyId, OrderItem o, string? externalSystem, List<ProjectRow> existing)
    {
        // Prefer the partner's code (we already know it matches nothing); else
        // synthesise a unique one. Guard against colliding with a code we just
        // generated for another order in the same payload.
        var code = !string.IsNullOrWhiteSpace(o.ProjectCode) ? o.ProjectCode!.Trim() : "";
        if (code.Length == 0 || existing.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            code = $"EXT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var name = !string.IsNullOrWhiteSpace(o.ProjectName) ? o.ProjectName!.Trim()
                 : !string.IsNullOrWhiteSpace(o.ProjectCode) ? o.ProjectCode!.Trim()
                 : code;

        var entity = new Project
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Code = code,
            Name = name,
            Status = "Active",
            StartDate = DateTime.UtcNow.Date,
            ExternalId = string.IsNullOrWhiteSpace(o.ProjectExternalId) ? null : o.ProjectExternalId,
            ExternalSystem = string.IsNullOrWhiteSpace(externalSystem) ? null : externalSystem,
            LastSyncedAt = DateTime.UtcNow,
        };
        _db.Set<Project>().Add(entity);
        return new ProjectRow(entity.Id, entity.Code, entity.Name, entity.ExternalId);
    }

    /// <summary>Read a top-level string property (first non-empty among names).</summary>
    private static string? ParseTopLevelString(string json, params string[] names)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var n in names)
                if (doc.RootElement.TryGetProperty(n, out var v)
                    && v.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!.Trim();
        }
        catch { /* malformed → no source */ }
        return null;
    }

    // ── AI fallback ──────────────────────────────────────────────────────
    private async Task AiResolveAsync(
        Guid companyId, Guid scanResultId,
        List<OcrExtractedLineItem> items, List<ResolvedOrder> resolved,
        List<int> unmatched, List<string> trace, CancellationToken ct)
    {
        // Build the candidate project list once: id + name + a few sample
        // material names so the model can match by description.
        var candidates = resolved
            .GroupBy(r => r.ProjectId)
            .Select(g => new ProjectMatchCandidate(
                ProjectId: g.Key.ToString(),
                ProjectName: g.First().ProjectName,
                ProjectCode: g.First().Order.ProjectCode,
                SampleMaterials: g.Select(x => x.Order.Material)
                                  .Where(m => !string.IsNullOrWhiteSpace(m))
                                  .Select(m => m!).Distinct().Take(6).ToList()))
            .ToList();
        if (candidates.Count < 2) return;
        var validIds = candidates.Select(c => c.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var i in unmatched)
        {
            var it = items[i];
            if (string.IsNullOrWhiteSpace(it.Description)) continue;
            try
            {
                var res = await _ai!.MatchLineProjectAsync(
                    companyId, scanResultId, it.Description!, it.Amount, candidates, ct);
                if (res.UsedAi && !string.IsNullOrEmpty(res.Answer)
                    && validIds.Contains(res.Answer)
                    && Guid.TryParse(res.Answer, out var pid))
                {
                    var c = candidates.First(x => string.Equals(x.ProjectId, res.Answer, StringComparison.OrdinalIgnoreCase));
                    it.ProjectId = pid;
                    it.ProjectName = c.ProjectName;
                    // ปิดลูปการสอน (กฎเหล็ก #1) — เก็บ feedback row + คำตอบ AI ฝังบรรทัด
                    // ไว้ ให้ตอนผู้ใช้ override project (SetExtractedLineProjectAsync)
                    // เรียก RecordUserChoiceAsync ปิดลูปได้ (ก่อนหน้านี้ CAPTURE ผ่าน
                    // orchestrator แล้วแต่ไม่มีใคร record คำตอบจริง → student ไม่เคยเรียน)
                    it.ProjectAiFeedbackId = res.FeedbackId;
                    it.AiSuggestedProjectId = pid;
                    trace.Add($"บรรทัด {i + 1} \"{Short(it.Description)}\" → \"{c.ProjectName}\" (AI)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OcrMetadataProjectMatcher: AI line→project failed (scan {Sid}, line {Line})", scanResultId, i);
            }
        }
    }
}
