using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// **รายงาน + ซ่อมเมื่อกด: ข้อมูลที่ผลแกะรุ่น 6/7/8 ลงบรรทัดสินค้าเป็น 1 × ยอด** (ทีม OCR 11 · ฝ่ายค้าน OCR 9 P1-5) — รูปเดียวกับ
/// <c>StockLedger.FindProductTotalMismatchesAsync</c>/<c>RepairProductTotalsAsync</c>: รายงานอ่านอย่างเดียว → ซ่อมเฉพาะแถวที่ผู้ใช้เลือก + audit chain ทุกแถว ·
/// ไม่มีงานไหนเรียกอัตโนมัติ · กติกาอยู่ที่ <see cref="OcrLumpSumRepair"/> (pure) · ทุก query กรอง <c>CompanyId</c> (กฎ M)
/// </summary>
public sealed class OcrLumpSumRepairService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<OcrLumpSumRepairService> _logger;

    public OcrLumpSumRepairService(AccountingDbContext db, ILogger<OcrLumpSumRepairService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>บรรทัดที่เสียของสแกนหนึ่งใบ (JSON เสีย = ไม่มีบรรทัด — สแกนนั้นไม่อยู่ในรายงาน และ log ไว้)</summary>
    private List<OcrLumpSumLine> PollutedLines(Guid scanId, string? itemsJson)
    {
        var result = new List<OcrLumpSumLine>();
        if (string.IsNullOrWhiteSpace(itemsJson)) return result;
        List<OcrLineItemDto>? items;
        try { items = JsonSerializer.Deserialize<List<OcrLineItemDto>>(itemsJson, JsonOpts); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "รายงานซ่อมผลแกะรุ่น 6–8: ExtractedItemsJson ของสแกน {ScanId} อ่านไม่ได้ — ข้าม", scanId);
            return result;
        }
        if (items == null) return result;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!OcrLumpSumRepair.IsPollutedLine(it.Description, it.Quantity, it.Unit)) continue;
            result.Add(new OcrLumpSumLine(i + 1, it.Description, it.Amount ?? 0m,
                OcrLineQuantityGuard.LumpSumSuffix(it.Description, it.Quantity, it.Unit)?.Suffix));
        }
        return result;
    }

    private async Task<List<(OcrScanResult Scan, List<OcrLumpSumLine> Lines)>> LoadPollutedScansAsync(
        Guid companyId, List<Guid>? onlyIds, bool track, CancellationToken ct)
    {
        var versions = OcrLumpSumRepair.AffectedVersions.ToList();
        var q = _db.OcrScanResults.Where(s => s.CompanyId == companyId && !s.IsDeleted
            && s.ExtractionVersion != null && versions.Contains(s.ExtractionVersion.Value) && s.ExtractedItemsJson != null);
        if (onlyIds is { } ids) q = q.Where(s => ids.Contains(s.Id));
        if (!track) q = q.AsNoTracking();
        var scans = await q.OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return scans.Select(s => (s, PollutedLines(s.Id, s.ExtractedItemsJson))).Where(x => x.Item2.Count > 0).ToList();
    }

    /// <summary>คีย์ alias ที่ติดท้ายของตัวกัน (สร้างหลังเวลาที่ท้ายเริ่มถูกเขียน)</summary>
    private IQueryable<ProductAlias> AliasCandidates(Guid companyId)
        => _db.ProductAliases.Where(a => a.CompanyId == companyId && !a.IsDeleted
            && a.CreatedAt >= OcrLumpSumRepair.PollutionStartUtc && a.AliasName.Contains("("));

    private IQueryable<OcrCategoryMapping> LearnerCandidates(Guid companyId)
        => _db.OcrCategoryMappings.Where(m => m.CompanyId == companyId && !m.IsDeleted
            && m.CreatedAt >= OcrLumpSumRepair.PollutionStartUtc && m.DescriptionKeyword.Contains("("));

    /// <summary>คีย์ตัวเรียนเก็บตัวพิมพ์เล็ก 50 ตัวอักษร (<c>ExpenseCategoryLearner.NormalizeDescription</c>) — คีย์ที่ตัดท้ายต้องรูปเดียวกัน</summary>
    private static string LearnerKey(string text)
    {
        var lowered = text.Trim().ToLowerInvariant();
        return lowered.Length > 50 ? lowered[..50] : lowered;
    }

    public async Task<OcrLumpSumRepairReport> FindAsync(Guid companyId, CancellationToken ct = default)
    {
        var polluted = await LoadPollutedScansAsync(companyId, null, track: false, ct);
        var scans = polluted.Select(p => new OcrLumpSumScan(p.Scan.Id, p.Scan.OriginalFileName, p.Scan.ExtractionVersion, p.Scan.CreatedAt,
            p.Scan.ExtractedDocumentNumber, p.Lines, p.Scan.CreatedDocumentId, p.Scan.StockImportedAt.HasValue,
            OcrLumpSumRepair.AlreadyMarked(p.Scan.ProcessingNotes))).ToList();

        var docScan = polluted.Where(p => p.Scan.CreatedDocumentId.HasValue)
            .GroupBy(p => p.Scan.CreatedDocumentId!.Value).ToDictionary(g => g.Key, g => g.First().Scan.Id);
        var docIds = docScan.Keys.ToList();
        var docs = docIds.Count == 0 ? new List<OcrLumpSumDocument>()
            : (await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == companyId && !d.IsDeleted && docIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.Status })
                    .ToListAsync(ct))
                .Select(d => new OcrLumpSumDocument(d.Id, docScan[d.Id], d.DocumentNumber, d.DocumentType.ToString(), d.Status.ToString(),
                    OcrLumpSumRepair.DocumentWayForward(d.Status)))
                .ToList();

        // สต็อก: เข้าจากหน้านำเข้าสต็อก (Notes "นำเข้าจาก OCR (สแกน {id})") หรือจากเอกสารที่สร้างจากสแกน
        var importedScanIds = polluted.Where(p => p.Scan.StockImportedAt.HasValue).Select(p => p.Scan.Id).ToList();
        var movements = new List<OcrLumpSumStockMovement>();
        if (importedScanIds.Count > 0 || docIds.Count > 0)
        {
            var rows = await _db.StockMovements.AsNoTracking()
                .Where(m => m.CompanyId == companyId && !m.IsDeleted
                    && ((m.DocumentId != null && docIds.Contains(m.DocumentId.Value))
                        || (importedScanIds.Count > 0 && m.Reference != null && m.Reference.StartsWith("OCR-IMPORT/"))))
                .Select(m => new { m.Id, m.ProductId, m.Product.Code, m.Product.Name, m.Quantity, m.UnitCost, m.MovementDate, m.Reference, m.DocumentId, m.Notes })
                .ToListAsync(ct);
            foreach (var m in rows)
            {
                Guid? scanId = null;
                if (m.DocumentId is Guid did && docScan.TryGetValue(did, out var sid)) scanId = sid;
                else
                    foreach (var id in importedScanIds)
                        if ((m.Notes ?? "").Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)) { scanId = id; break; }
                if (scanId is null) continue;
                movements.Add(new OcrLumpSumStockMovement(m.Id, m.ProductId, m.Code, m.Name, m.Quantity, m.UnitCost, m.MovementDate, m.Reference,
                    scanId, m.DocumentId, OcrLumpSumRepair.StockWayForward));
            }
        }

        var aliasRows = await AliasCandidates(companyId).AsNoTracking()
            .Select(a => new { a.Id, a.AliasName, a.CreatedAt, a.Product.Code }).ToListAsync(ct);
        var aliases = aliasRows.Select(a => (a, clean: OcrLumpSumRepair.CleanKey(a.AliasName)))
            .Where(x => x.clean != null)
            .Select(x => new OcrLumpSumKey(x.a.Id, x.a.AliasName, x.clean!, x.a.CreatedAt, x.a.Code)).ToList();

        var learnerRows = await LearnerCandidates(companyId).AsNoTracking()
            .Select(m => new { m.Id, m.DescriptionKeyword, m.CreatedAt, m.VendorKey, m.AccountCode }).ToListAsync(ct);
        var learner = learnerRows.Select(m => (m, clean: OcrLumpSumRepair.CleanKey(m.DescriptionKeyword)))
            .Where(x => x.clean != null)
            .Select(x => new OcrLumpSumKey(x.m.Id, x.m.DescriptionKeyword, LearnerKey(x.clean!), x.m.CreatedAt, $"{x.m.VendorKey} → {x.m.AccountCode}"))
            .ToList();

        var productRows = await _db.Products.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.CreatedAt >= OcrLumpSumRepair.PollutionStartUtc && p.Name.Contains("("))
            .Select(p => new { p.Id, p.Name, p.CreatedAt, p.Code }).ToListAsync(ct);
        var products = productRows.Select(p => (p, clean: OcrLumpSumRepair.CleanKey(p.Name)))
            .Where(x => x.clean != null)
            .Select(x => new OcrLumpSumKey(x.p.Id, x.p.Name, x.clean!, x.p.CreatedAt, x.p.Code, OcrLumpSumRepair.ProductWayForward)).ToList();

        return new OcrLumpSumRepairReport(scans, docs, movements, aliases, learner, products);
    }

    public async Task<OcrLumpSumRepairResult> RepairAsync(Guid companyId, OcrLumpSumRepairRequest request, string actor,
        CancellationToken ct = default)
    {
        var scanIds = (request.ScanIds ?? new()).Distinct().ToList();
        var aliasIds = (request.AliasIds ?? new()).Distinct().ToList();
        var learnerIds = (request.LearnerKeyIds ?? new()).Distinct().ToList();
        if (scanIds.Count + aliasIds.Count + learnerIds.Count == 0)
            throw new BusinessRuleException("ยังไม่ได้เลือกแถวที่จะซ่อม — เปิดรายงานตรวจผลแกะรุ่น 6–8 แล้วเลือกแถวก่อน", OcrLumpSumRepair.RuleCode);

        await using var tx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        // หนึ่งการซ่อมต่อบริษัทในเวลาเดียวกัน (สองแอดมินกดพร้อมกัน = หมายเหตุ/audit ซ้ำ)
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.OcrLumpSumRepair, "repair") }, ct);
        Guid? actorId = Guid.TryParse(actor, out var uid) ? uid : null;
        var outcomes = new List<OcrLumpSumRepairOutcome>();

        // ── สแกน: ติดแท็กบล็อกต่อบรรทัด + คืนสิทธิ์ "แกะใหม่" (ไม่แก้บรรทัดเอง — จำนวนจริงอยู่บนกระดาษ ไม่อยู่ใน JSON) ──
        if (scanIds.Count > 0)
        {
            var polluted = (await LoadPollutedScansAsync(companyId, scanIds, track: true, ct)).ToDictionary(p => p.Scan.Id);
            foreach (var id in scanIds)
            {
                if (!polluted.TryGetValue(id, out var p))
                {
                    outcomes.Add(new("scan", id, false, null, null, "ไม่พบสแกนรุ่น 6–8 ที่มีบรรทัดต้องซ่อมในบริษัทนี้ (อาจแกะใหม่/แก้แล้ว)"));
                    continue;
                }
                if (OcrLumpSumRepair.AlreadyMarked(p.Scan.ProcessingNotes))
                {
                    outcomes.Add(new("scan", id, false, null, null, "ติดแท็กให้แกะใหม่ไว้แล้ว"));
                    continue;
                }
                var beforeRetry = p.Scan.RetryCount;
                var notes = p.Lines.Select(l => OcrLumpSumRepair.ScanNote(p.Scan.ExtractionVersion ?? 0, l.LineNo, l.Description, l.Amount)).ToList();
                p.Scan.ProcessingNotes = OcrLineQuantityGuard.MergeNotes(p.Scan.ProcessingNotes, notes);
                p.Scan.RetryCount = 0;
                p.Scan.UpdatedAt = DateTime.UtcNow;
                p.Scan.UpdatedBy = actor;
                Audit(companyId, actorId, nameof(OcrScanResult), id,
                    new { retryCount = beforeRetry, extractionVersion = p.Scan.ExtractionVersion },
                    new { action = "ocr-lumpsum-mark-reextract", retryCount = 0, lines = p.Lines, ruleCode = OcrLumpSumRepair.RuleCode, by = actor });
                outcomes.Add(new("scan", id, true, $"{p.Lines.Count} บรรทัด 1 × ยอด", "ติดแท็กบล็อก · แกะใหม่ได้โดยไม่ใช้โควต้า", null));
            }
        }

        // ── alias: ตัดท้าย (รวมกับ alias ที่ตัดแล้วซ้ำกัน) ──
        if (aliasIds.Count > 0)
        {
            var rows = await AliasCandidates(companyId).Where(a => aliasIds.Contains(a.Id)).ToListAsync(ct);
            foreach (var id in aliasIds)
            {
                var a = rows.FirstOrDefault(r => r.Id == id);
                var clean = a is null ? null : OcrLumpSumRepair.CleanKey(a.AliasName);
                if (a is null || clean is null)
                {
                    outcomes.Add(new("alias", id, false, a?.AliasName, null, "ไม่พบ alias ที่ติดท้ายจำนวนในบริษัทนี้"));
                    continue;
                }
                var norm = ProductMatcher.Normalize(clean);
                var before = a.AliasName;
                var twin = await _db.ProductAliases.FirstOrDefaultAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Id != a.Id
                    && x.ProductId == a.ProductId && x.ContactId == a.ContactId && x.NormalizedName == norm, ct);
                if (twin != null)
                {
                    twin.TimesUsed += a.TimesUsed;
                    twin.LastUsedAt = a.LastUsedAt > twin.LastUsedAt ? a.LastUsedAt : twin.LastUsedAt;
                    a.IsDeleted = true;
                }
                else
                {
                    a.AliasName = clean;
                    a.NormalizedName = norm.Length > 500 ? norm[..500] : norm;
                }
                a.UpdatedAt = DateTime.UtcNow;
                a.UpdatedBy = actor;
                Audit(companyId, actorId, nameof(ProductAlias), a.Id, new { aliasName = before },
                    new { action = twin != null ? "ocr-lumpsum-alias-merge" : "ocr-lumpsum-alias-rename", aliasName = clean, mergedInto = twin?.Id,
                          ruleCode = OcrLumpSumRepair.RuleCode, by = actor });
                outcomes.Add(new("alias", id, true, before, twin != null ? $"รวมกับ \"{twin.AliasName}\"" : clean, null));
            }
        }

        // ── คีย์ตัวเรียนผัง (OcrCategoryMappings): ตัดท้าย (รวมกับคีย์เดิมที่ซ้ำ) ──
        if (learnerIds.Count > 0)
        {
            var rows = await LearnerCandidates(companyId).Where(m => learnerIds.Contains(m.Id)).ToListAsync(ct);
            foreach (var id in learnerIds)
            {
                var m = rows.FirstOrDefault(r => r.Id == id);
                var clean = m is null ? null : OcrLumpSumRepair.CleanKey(m.DescriptionKeyword);
                if (m is null || clean is null)
                {
                    outcomes.Add(new("learner", id, false, m?.DescriptionKeyword, null, "ไม่พบคีย์ตัวเรียนที่ติดท้ายจำนวนในบริษัทนี้"));
                    continue;
                }
                var key = LearnerKey(clean);
                var before = m.DescriptionKeyword;
                var twin = await _db.OcrCategoryMappings.FirstOrDefaultAsync(x => x.CompanyId == companyId && !x.IsDeleted && x.Id != m.Id
                    && x.VendorKey == m.VendorKey && x.DescriptionKeyword == key && x.AccountCode == m.AccountCode, ct);
                if (twin != null)
                {
                    twin.TimesUsed += m.TimesUsed;
                    twin.LastUsedAt = m.LastUsedAt > twin.LastUsedAt ? m.LastUsedAt : twin.LastUsedAt;
                    m.IsDeleted = true;
                }
                else m.DescriptionKeyword = key;
                m.UpdatedAt = DateTime.UtcNow;
                m.UpdatedBy = actor;
                Audit(companyId, actorId, nameof(OcrCategoryMapping), m.Id, new { descriptionKeyword = before },
                    new { action = twin != null ? "ocr-lumpsum-learner-merge" : "ocr-lumpsum-learner-rename", descriptionKeyword = key, mergedInto = twin?.Id,
                          ruleCode = OcrLumpSumRepair.RuleCode, by = actor });
                outcomes.Add(new("learner", id, true, before, twin != null ? $"รวมกับคีย์ \"{key}\"" : key, null));
            }
        }

        await _db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        var repaired = outcomes.Count(o => o.Repaired);
        _logger.LogInformation("ซ่อมผลแกะรุ่น 6–8 บริษัท {Company}: ซ่อม {Repaired} · ข้าม {Skipped} โดย {Actor}",
            companyId, repaired, outcomes.Count - repaired, actor);
        return new OcrLumpSumRepairResult(repaired, outcomes.Count - repaired, outcomes);
    }

    private void Audit(Guid companyId, Guid? actorId, string entityType, Guid entityId, object before, object after)
    {
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorId,
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Action = Models.Enums.AuditAction.Update,
            OldValues = JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(after),
            Timestamp = DateTime.UtcNow,
        });
    }
}
