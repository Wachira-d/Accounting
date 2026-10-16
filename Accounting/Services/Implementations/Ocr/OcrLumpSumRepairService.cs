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

    /// <summary>บรรทัดของสแกนหนึ่งใบ (JSON เสีย = ไม่มีบรรทัด — สแกนนั้นไม่อยู่ในรายงาน และ log ไว้)</summary>
    private List<OcrLineItemDto> ScanItems(Guid scanId, string? itemsJson)
    {
        if (string.IsNullOrWhiteSpace(itemsJson)) return new();
        try { return JsonSerializer.Deserialize<List<OcrLineItemDto>>(itemsJson, JsonOpts) ?? new(); }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "รายงานซ่อมผลแกะรุ่น 6–10: ExtractedItemsJson ของสแกน {ScanId} อ่านไม่ได้ — ข้าม", scanId);
            return new();
        }
    }

    /// <summary>บรรทัดที่เสียของสแกนหนึ่งใบ — ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-2): ตัดสินด้วยบริบทเดียวกับตัวตัดสินตอนเขียน (ข้อความกระดาษ + ชนิดผู้ขายของสแกน) ⇒
    /// บิลมิเตอร์จริงที่ตัวเขียนเขียน "(จำนวน N)" (PEA ค่าพลังงาน · ค่า Ft · Water charge) ไม่ถูกติดป้าย "จำนวนสินค้าหาย"</summary>
    private static List<OcrLumpSumLine> PollutedLines(OcrScanResult scan, List<OcrLineItemDto> items)
    {
        var result = new List<OcrLumpSumLine>();
        if (items.Count == 0) return result;
        var paper = OcrLineQuantityGuard.Read(scan.RawTextContent);
        var seller = OcrUtilityVendor.KindOf(scan.ExtractedVendorTaxId, scan.ExtractedVendorName);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (!OcrLumpSumRepair.IsPollutedLine(it.Description, it.Quantity, it.Unit, it.Amount ?? 0m, paper, seller)) continue;
            result.Add(new OcrLumpSumLine(i + 1, it.Description, it.Amount ?? 0m,
                OcrLineQuantityGuard.LumpSumSuffix(it.Description, it.Quantity, it.Unit)?.Suffix));
        }
        return result;
    }

    private sealed record AffectedScan(OcrScanResult Scan, List<OcrLumpSumLine> Lines, List<string> SuffixedDescriptions);

    /// <summary>สแกนรุ่น 6–10 ทุกใบของบริษัท + บรรทัดที่เสีย + คำอธิบายทุกบรรทัดที่ติดท้ายของตัวกัน (รวมมิเตอร์ — ที่มาของ alias/คีย์ตัวเรียนที่ติดท้าย)</summary>
    private async Task<List<AffectedScan>> LoadAffectedScansAsync(Guid companyId, List<Guid>? onlyIds, bool track, CancellationToken ct)
    {
        var versions = OcrLumpSumRepair.AffectedVersions.ToList();
        var q = _db.OcrScanResults.Where(s => s.CompanyId == companyId && !s.IsDeleted
            && s.ExtractionVersion != null && versions.Contains(s.ExtractionVersion.Value) && s.ExtractedItemsJson != null);
        if (onlyIds is { } ids) q = q.Where(s => ids.Contains(s.Id));
        if (!track) q = q.AsNoTracking();
        var scans = await q.OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return scans.Select(s =>
        {
            var items = ScanItems(s.Id, s.ExtractedItemsJson);
            var suffixed = items.Where(it => OcrLineQuantityGuard.LumpSumSuffix(it.Description, it.Quantity, it.Unit) != null)
                .Select(it => it.Description!.Trim()).ToList();
            return new AffectedScan(s, PollutedLines(s, items), suffixed);
        }).ToList();
    }

    private async Task<List<(OcrScanResult Scan, List<OcrLumpSumLine> Lines)>> LoadPollutedScansAsync(
        Guid companyId, List<Guid>? onlyIds, bool track, CancellationToken ct)
        => (await LoadAffectedScansAsync(companyId, onlyIds, track, ct)).Where(a => a.Lines.Count > 0).Select(a => (a.Scan, a.Lines)).ToList();

    /// <summary>คำอธิบายบรรทัดสแกนรุ่น 6–10 ที่ตัวกันติดท้าย (ที่มาของคีย์) — ใช้ทั้งรายงานและตอนซ่อม (ข้อเสนอเดียวกันสองฝั่ง)</summary>
    private async Task<List<string>> GuardSuffixedDescriptionsAsync(Guid companyId, CancellationToken ct)
        => (await LoadAffectedScansAsync(companyId, null, track: false, ct)).SelectMany(a => a.SuffixedDescriptions).Distinct().ToList();

    /// <summary>ข้อเสนอคีย์ตัวเรียนหลังซ่อม — ท้ายครบ ⇒ <see cref="OcrLumpSumRepair.CleanKey"/> · ถูกตัด 50 ตัวจนท้ายขาด ⇒ ที่มาจากบรรทัดสแกน
    /// (<see cref="OcrLumpSumRepair.CleanTruncatedLearnerKey"/> · ฝ่ายค้านรอบ 3 P2-3) · ไม่รู้ = null</summary>
    private static string? LearnerProposal(string keyword, IReadOnlyList<string> suffixed)
        => OcrLumpSumRepair.CleanKey(keyword) is string c ? OcrLumpSumRepair.LearnerKey(c)
           : OcrLumpSumRepair.CleanTruncatedLearnerKey(keyword, suffixed);

    /// <summary>
    /// **ด่านอนุมัติของเอกสารที่สร้างจากสแกน** (ทีม OCR 12 · ฝ่ายค้านรอบ 3 P1-4) — ผู้เรียกตัวเดียว <c>DocumentService.ApproveDocumentAsync</c> (ทุกทางเข้า) ·
    /// อ่านหมายเหตุของทุกสแกนที่สร้างเอกสารนี้ (<c>CreatedDocumentId</c> · กรอง <c>CompanyId</c>) แล้วถามตัวตัดสินเดียว
    /// <see cref="OcrPostingReadiness.EvaluateHumanApproval"/> บนบรรทัดเอกสาร<b>ตอนนี้</b> · ไม่ใช่เอกสารจากสแกน/สแกนไม่มีหมายเหตุที่บล็อก ⇒ null (อนุมัติตามเดิม)
    /// </summary>
    /// <remarks>ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-a · CLAUDE #4 A "เอกสารลูกต้องสืบทอด"): ด่านเดิมผูกกับลิงก์ <c>CreatedDocumentId</c> อย่างเดียว ⇒ คัดลอก draft ที่ถูกบล็อก
    /// (<c>DocumentCloneController</c> ก๊อป <c>DiscountAmount</c>) · แปลงจาก draft OCR · รายการเกิดซ้ำที่ก๊อปบรรทัด ⇒ ใบลูกไม่มีลิงก์สแกน ⇒ อนุมัติได้ และ "ส่วนลด" ที่เป็นเศษปัดพิมพ์ออก
    /// เป็นส่วนลดจริง · <b>ทางที่เลือก = บล็อกตามค่า (carry by value)</b> ไม่ใช่สืบทอดธงทีละเส้น: บรรทัดที่มีรูปค่าที่ระบบตั้ง (<see cref="OcrLineQuantityGuard.MayCarryHumanBlock"/>)
    /// ⇒ ค้นหมายเหตุบล็อกของสแกนบริษัทนี้ทุกใบ แล้วนับเฉพาะบรรทัดที่ตรงร่องรอย + ยอดของหมายเหตุ — ครอบทุกเส้นที่ก๊อปค่า (5 เส้นอนุพันธ์ + API + JSON รายการเกิดซ้ำ)
    /// โดยไม่ต้องมีคอลัมน์ใหม่/โค้ดสืบทอดในแต่ละเส้น (defect class "เส้นอนุพันธ์ลืมสืบทอด") · ปลดด้วยเงื่อนไขเดียวกัน (แก้บรรทัดในใบลูก) ·
    /// ใบทั่วไปไม่ผ่านตัวกรอง ⇒ ไม่มี query เพิ่ม</remarks>
    public static async Task<string?> ApprovalBlockAsync(AccountingDbContext db, Guid companyId, Document doc, CancellationToken ct = default)
    {
        var m0 = OcrPostingReadiness.HumanApprovalBlockingMarkers[0].Marker;
        var m1 = OcrPostingReadiness.HumanApprovalBlockingMarkers[1].Marker;
        var live = doc.Lines.Where(l => !l.IsDeleted).ToList();
        var lines = live
            .Select(l => new OcrLiveLine(l.Quantity, l.UnitPrice, l.DiscountPercent, l.DiscountAmount, l.Amount, l.VatRate) { Description = l.Description, Unit = l.Unit })
            .ToList();
        var notes = await db.OcrScanResults.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.CreatedDocumentId == doc.Id && r.ProcessingNotes != null
                && (r.ProcessingNotes.Contains(m0) || r.ProcessingNotes.Contains(m1)))
            .Select(r => new { r.ProcessingNotes, r.ExtractedVendorTaxId, r.ExtractedVendorName })
            .ToListAsync(ct);
        foreach (var n in notes)
            if (OcrPostingReadiness.EvaluateHumanApproval(n.ProcessingNotes, lines, OcrUtilityVendor.KindOf(n.ExtractedVendorTaxId, n.ExtractedVendorName))
                    is { CanAutoApprove: false } v) return v.Reason;

        // ── เอกสารลูก: ค่าที่ระบบตั้งมากับบรรทัด (ไม่มีลิงก์สแกน) ──
        if (!live.Any(l => OcrLineQuantityGuard.MayCarryHumanBlock(l.Quantity, l.UnitPrice, l.DiscountAmount, l.Amount, l.Description, l.Unit))) return null;
        var carried = await db.OcrScanResults.AsNoTracking()
            .Where(r => r.CompanyId == companyId && (r.CreatedDocumentId == null || r.CreatedDocumentId != doc.Id) && r.ProcessingNotes != null
                && (r.ProcessingNotes.Contains(m0) || r.ProcessingNotes.Contains(m1)))
            .Select(r => new { r.ProcessingNotes, r.ExtractedVendorTaxId, r.ExtractedVendorName })
            .ToListAsync(ct);
        foreach (var n in carried)
            if (OcrPostingReadiness.EvaluateHumanApproval(n.ProcessingNotes, lines, OcrUtilityVendor.KindOf(n.ExtractedVendorTaxId, n.ExtractedVendorName), carried: true)
                    is { CanAutoApprove: false } v) return v.Reason;
        return null;
    }

    /// <summary>คีย์ alias ที่ติดท้ายของตัวกัน (สร้างหลังเวลาที่ท้ายเริ่มถูกเขียน)</summary>
    private IQueryable<ProductAlias> AliasCandidates(Guid companyId)
        => _db.ProductAliases.Where(a => a.CompanyId == companyId && !a.IsDeleted
            && a.CreatedAt >= OcrLumpSumRepair.PollutionStartUtc && a.AliasName.Contains("("));

    private IQueryable<OcrCategoryMapping> LearnerCandidates(Guid companyId)
        => _db.OcrCategoryMappings.Where(m => m.CompanyId == companyId && !m.IsDeleted
            && m.CreatedAt >= OcrLumpSumRepair.PollutionStartUtc && m.DescriptionKeyword.Contains("("));

    public async Task<OcrLumpSumRepairReport> FindAsync(Guid companyId, CancellationToken ct = default)
    {
        var affected = await LoadAffectedScansAsync(companyId, null, track: false, ct);
        var polluted = affected.Where(a => a.Lines.Count > 0).Select(a => (a.Scan, a.Lines)).ToList();
        var suffixed = affected.SelectMany(a => a.SuffixedDescriptions).Distinct().ToList();
        var guardNorm = suffixed.Select(ProductMatcher.Normalize).ToHashSet();
        var guardLearner = suffixed.Select(OcrLumpSumRepair.LearnerKey).ToHashSet();
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
            .Select(a => new { a.Id, a.AliasName, a.NormalizedName, a.CreatedAt, a.Product.Code }).ToListAsync(ct);
        // ทีม OCR 12 (ฝ่ายค้านรอบ 3 P2-3): "(12 ขวด)" อาจเป็นขนาดแพ็กจริง ⇒ คำเตือนจากเซิร์ฟเวอร์ เว้นแต่ alias ตรงกับบรรทัดที่ตัวกันรุ่น 6–10 เขียนเอง (ที่มา)
        var aliases = aliasRows.Select(a => (a, clean: OcrLumpSumRepair.CleanKey(a.AliasName)))
            .Where(x => x.clean != null)
            .Select(x => new OcrLumpSumKey(x.a.Id, x.a.AliasName, x.clean!, x.a.CreatedAt, x.a.Code,
                Warning: OcrLumpSumRepair.KeyWarning(x.a.AliasName, guardNorm.Contains(x.a.NormalizedName)))).ToList();

        var learnerRows = await LearnerCandidates(companyId).AsNoTracking()
            .Select(m => new { m.Id, m.DescriptionKeyword, m.CreatedAt, m.VendorKey, m.AccountCode }).ToListAsync(ct);
        var learner = learnerRows.Select(m => (m, clean: LearnerProposal(m.DescriptionKeyword, suffixed)))
            .Where(x => x.clean != null)
            .Select(x => new OcrLumpSumKey(x.m.Id, x.m.DescriptionKeyword, x.clean!, x.m.CreatedAt, $"{x.m.VendorKey} → {x.m.AccountCode}",
                Warning: OcrLumpSumRepair.KeyWarning(x.m.DescriptionKeyword, guardLearner.Contains(x.m.DescriptionKeyword))))
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
            throw new BusinessRuleException("ยังไม่ได้เลือกแถวที่จะซ่อม — เปิดรายงานตรวจผลแกะรุ่น 6–10 แล้วเลือกแถวก่อน", OcrLumpSumRepair.RuleCode);

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
                    outcomes.Add(new("scan", id, false, null, null, "ไม่พบสแกนรุ่น 6–10 ที่มีบรรทัดต้องซ่อมในบริษัทนี้ (อาจแกะใหม่/แก้แล้ว)"));
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
            var suffixed = await GuardSuffixedDescriptionsAsync(companyId, ct);
            foreach (var id in learnerIds)
            {
                var m = rows.FirstOrDefault(r => r.Id == id);
                var key = m is null ? null : LearnerProposal(m.DescriptionKeyword, suffixed);
                if (m is null || key is null)
                {
                    outcomes.Add(new("learner", id, false, m?.DescriptionKeyword, null, "ไม่พบคีย์ตัวเรียนที่ติดท้ายจำนวนในบริษัทนี้"));
                    continue;
                }
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
        _logger.LogInformation("ซ่อมผลแกะรุ่น 6–10 บริษัท {Company}: ซ่อม {Repaired} · ข้าม {Skipped} โดย {Actor}",
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
