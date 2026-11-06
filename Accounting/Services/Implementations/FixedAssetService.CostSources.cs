using Accounting.Data;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// ═══ รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md) ═══
///
/// <para>ผู้เขียน <c>FixedAssetCostSources</c> มีที่เดียว (ไฟล์นี้) · กติกาทั้งหมดอยู่ที่ <c>Helpers/AssetCostSourceRules</c> ·
/// ทุกเส้นที่เขียน: ล็อกแถวเอกสารต้นทาง (FOR UPDATE · เรียงตาม Id กัน deadlock) + ล็อกแถวสินทรัพย์ ในธุรกรรมเดียว ⇒ สองคำขอผูกบรรทัดเดียวกัน
/// พร้อมกันไม่ผ่านทั้งคู่ · ทุกการเปลี่ยนเข้า audit chain</para>
/// </summary>
public partial class FixedAssetService
{
    /// <summary>ข้อเท็จจริงของบรรทัดหนึ่งที่ตัวตัดสินต้องใช้ (โหลดครั้งเดียว กรอง CompanyId ผ่านเอกสาร)</summary>
    private sealed class CostLineFacts
    {
        public DocumentLine Line { get; init; } = null!;
        public Document Doc { get; init; } = null!;
        public string? AccountCode { get; init; }
        public Accounting.Helpers.AssetCostSourceRules.AccountRole Role { get; init; }
        public int? Sign { get; init; }
        public Accounting.Helpers.AssetCostSourceRules.LineCost Cost { get; init; }
        public decimal LinkedAbs { get; set; }
        public FixedAsset? LegacyAsset { get; set; }
        public bool LegacyAbsorbable { get; set; }
        public string? Problem { get; set; }
        public decimal CostAbs => Math.Abs(Cost.Amount);
        public string Label => $"{Doc.DocumentNumber} · {Line.Description}";
    }

    private static DateTime TodayBangkok() => DateTime.UtcNow.AddHours(7).Date;

    private static Guid? ActorId(string actor) => Guid.TryParse(actor, out var g) ? g : null;

    /// <summary>โหลดข้อเท็จจริงของบรรทัด — <paramref name="lineIds"/> ว่าง + <paramref name="documentId"/>/<paramref name="search"/> = โหมดค้นหา</summary>
    private async Task<List<CostLineFacts>> LoadCostLineFactsAsync(
        Guid companyId, IReadOnlyCollection<Guid>? lineIds, Guid? documentId = null, string? search = null, int take = 200)
    {
        var q = _db.DocumentLines.AsNoTracking()
            .Include(l => l.Document).ThenInclude(d => d.Contact)
            .Include(l => l.Account)
            .Where(l => l.Document.CompanyId == companyId && !l.Document.IsDeleted);
        if (lineIds != null) q = q.Where(l => lineIds.Contains(l.Id));
        if (documentId.HasValue) q = q.Where(l => l.DocumentId == documentId.Value);
        if (lineIds == null)
        {
            // โหมดค้นหา: เฉพาะชนิดฝั่งซื้อ + ใบลด/เพิ่มหนี้ · ผังที่อาจเป็นต้นทุน (ตัวกรองหยาบ — ตัวตัดสินจริงคือ AssetCostSourceRules ด้านล่าง)
            var types = new[]
            {
                DocumentType.PurchaseInvoice, DocumentType.Expense, DocumentType.PaymentVoucher, DocumentType.CertificateInLieu,
                DocumentType.CreditNote, DocumentType.DebitNote,
            };
            q = q.Where(l => types.Contains(l.Document.DocumentType) && l.Account != null
                && (l.Account.AccountCode.StartsWith("118") || l.Account.AccountCode.StartsWith("12")));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim()}%";
                q = q.Where(l => EF.Functions.ILike(l.Document.DocumentNumber, pattern)
                    || EF.Functions.ILike(l.Description, pattern)
                    || (l.Document.Contact != null && EF.Functions.ILike(l.Document.Contact.Name, pattern)));
            }
            q = q.OrderByDescending(l => l.Document.DocumentDate).ThenBy(l => l.LineOrder).Take(take);
        }
        var lines = await q.ToListAsync();
        if (lines.Count == 0) return new List<CostLineFacts>();

        // ชนิดของใบที่อ้าง (ใบลด/เพิ่มหนี้ → ฝั่ง · ใบซื้ออ้างใบรับสินค้า)
        var relatedIds = lines.Where(l => l.Document.RelatedDocumentId.HasValue)
            .Select(l => l.Document.RelatedDocumentId!.Value).Distinct().ToList();
        var relatedTypes = relatedIds.Count == 0
            ? new Dictionary<Guid, DocumentType>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && relatedIds.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentType })
                .ToDictionaryAsync(x => x.Id, x => x.DocumentType);

        var ids = lines.Select(l => l.Id).ToList();
        var links = await _db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && ids.Contains(s.DocumentLineId) && s.ReleasedAt == null)
            .Select(s => new { s.DocumentLineId, s.Amount })
            .ToListAsync();
        var legacy = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId && a.SourceDocumentLineId != null && ids.Contains(a.SourceDocumentLineId.Value))
            .ToListAsync();
        var legacyIds = legacy.Select(a => a.Id).ToList();
        var postedDep = legacyIds.Count == 0 ? new HashSet<Guid>() : (await _db.AssetDepreciations.AsNoTracking()
            .Where(d => d.CompanyId == companyId && legacyIds.Contains(d.FixedAssetId) && d.IsPosted)
            .Select(d => d.FixedAssetId).Distinct().ToListAsync()).ToHashSet();
        var ownSources = legacyIds.Count == 0 ? new HashSet<Guid>() : (await _db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && legacyIds.Contains(s.FixedAssetId) && s.ReleasedAt == null)
            .Select(s => s.FixedAssetId).Distinct().ToListAsync()).ToHashSet();
        // เอกสารที่สร้างจากสแกนซึ่ง "ลงทะเบียนสินทรัพย์" ไปแล้ว (สินทรัพย์นั้นลง JE ซื้อของตัวเอง · ไม่มี SourceDocumentLineId) —
        // ผูกบรรทัดของใบนี้ซ้ำ = ต้นทุนสองครั้ง (คลาสเดียวกับ SourceScanResultId ใน FixedAsset.cs)
        var docIdsAll = lines.Select(l => l.DocumentId).Distinct().ToList();
        var scanAssetByDoc = (await (
                from r in _db.Set<OcrScanResult>().AsNoTracking()
                join a in _db.FixedAssets.AsNoTracking() on (Guid?)r.Id equals a.SourceScanResultId
                where r.CompanyId == companyId && a.CompanyId == companyId && !r.IsDeleted
                    && r.CreatedDocumentId != null && docIdsAll.Contains(r.CreatedDocumentId.Value)
                select new { DocId = r.CreatedDocumentId!.Value, a.AssetCode })
            .ToListAsync())
            .GroupBy(x => x.DocId).ToDictionary(g => g.Key, g => g.First().AssetCode);

        var result = new List<CostLineFacts>(lines.Count);
        foreach (var l in lines)
        {
            var d = l.Document;
            DocumentType? relatedType = d.RelatedDocumentId.HasValue && relatedTypes.TryGetValue(d.RelatedDocumentId.Value, out var rt)
                ? rt : null;
            var adjSide = d.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote
                ? Accounting.Helpers.AdjustmentNoteAccount.ResolveSide(relatedType, d.CnDnPurchaseSideOverride)
                : null;
            var sign = Accounting.Helpers.AssetCostSourceRules.SignFor(d.DocumentType, adjSide);
            var code = l.Account?.AccountCode;
            var role = Accounting.Helpers.AssetCostSourceRules.RoleOf(code);
            var cost = Accounting.Helpers.AssetCostSourceRules.CostOf(l.Amount, l.VatAmount, l.IsVatClaimable, d.ExchangeRate, sign ?? 1);
            var f = new CostLineFacts { Line = l, Doc = d, AccountCode = code, Role = role, Sign = sign, Cost = cost };
            f.LinkedAbs = links.Where(x => x.DocumentLineId == l.Id).Sum(x => Math.Abs(x.Amount));
            f.Problem = Accounting.Helpers.AssetCostSourceRules.LineProblem(
                d.DocumentNumber, d.DocumentType, sign,
                Accounting.Helpers.DocumentStatusRules.IsEffective(d.Status),
                isPaymentSettlement: d.DocumentType == DocumentType.PaymentVoucher
                    && relatedType is DocumentType.PurchaseInvoice or DocumentType.Expense,
                viaGoodsReceipt: relatedType == DocumentType.GoodsReceiptNote,
                role, code, f.CostAbs);
            if (f.Problem == null && scanAssetByDoc.TryGetValue(d.Id, out var scanAssetCode))
                f.Problem = $"เอกสาร {d.DocumentNumber} สร้างจากสแกนที่ลงทะเบียนเป็นสินทรัพย์ {scanAssetCode} ไปแล้ว (พร้อมรายการบัญชีของตัวเอง) — "
                    + "ผูกซ้ำเป็นต้นทุนของสินทรัพย์อื่นไม่ได้ · ถ้าเป็นของชิ้นเดียวกัน ให้ผูกใบอื่นเข้ากับ " + scanAssetCode;
            var la = legacy.FirstOrDefault(a => a.SourceDocumentLineId == l.Id);
            if (la != null)
            {
                f.LegacyAsset = la;
                f.LegacyAbsorbable = Accounting.Helpers.AssetCostSourceRules.CanAbsorbLegacyDraft(
                    la.NeedsReview, la.Status, la.AccumulatedDepreciation, postedDep.Contains(la.Id),
                    la.IsUnderAcquisition, ownSources.Contains(la.Id) ? 1 : 0);
                if (f.Problem == null && !f.LegacyAbsorbable)
                    f.Problem = $"บรรทัดนี้ขึ้นทะเบียนเป็นสินทรัพย์ {la.AssetCode} แล้ว (ยืนยัน/คิดค่าเสื่อมแล้ว) — ผูกซ้ำเป็นต้นทุนของสินทรัพย์อื่นไม่ได้. "
                        + "ถ้าเป็นของชิ้นเดียวกัน ให้ผูกใบอื่น ๆ เข้ากับ " + la.AssetCode + " แทน";
            }
            result.Add(f);
        }
        return result;
    }

    public async Task<List<LinkableCostLine>> GetLinkableCostLinesAsync(Guid companyId, string? search, Guid? documentId)
    {
        var facts = await LoadCostLineFactsAsync(companyId, null, documentId, search);
        return facts
            .Where(f => f.Sign != null && f.Role != Accounting.Helpers.AssetCostSourceRules.AccountRole.NotEligible)
            .Select(f =>
            {
                var remaining = Accounting.Helpers.AssetCostSourceRules.Remaining(f.CostAbs, f.LinkedAbs);
                var problem = f.Problem ?? (remaining <= 0m ? "ผูกเป็นต้นทุนครบยอดแล้ว" : null);
                return new LinkableCostLine(
                    f.Doc.Id, f.Doc.DocumentNumber, f.Doc.DocumentType.ToString(), f.Doc.DocumentDate, f.Doc.Contact?.Name,
                    f.Line.Id, f.Line.Description, f.AccountCode,
                    Accounting.Helpers.AssetCostSourceRules.KindFor(f.Doc.DocumentType, f.Role, f.Line.Description).ToString(),
                    f.Cost.Amount, f.LinkedAbs, remaining,
                    f.LegacyAsset?.AssetCode, f.LegacyAbsorbable, problem);
            }).ToList();
    }

    /// <summary>ล็อกแถวเอกสารต้นทาง (เรียง Id กัน deadlock) — ต้องอยู่ในธุรกรรมของผู้เรียก</summary>
    private async Task LockDocumentsAsync(Guid companyId, IEnumerable<Guid> documentIds)
    {
        foreach (var id in documentIds.Distinct().OrderBy(g => g))
            await _db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", id, companyId);
    }

    private async Task LockAssetAsync(Guid companyId, Guid assetId)
        => await _db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"FixedAssets\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", assetId, companyId);

    /// <summary>งวดบัญชีของวันที่นี้ต้องไม่ปิด/ล็อก (JE โอน/ปรับต้นทุน)</summary>
    private async Task EnsurePeriodOpenForAssetJournalAsync(Guid companyId, DateTime date, string action)
    {
        var d = date.Date;
        var fp = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.StartDate <= d && p.EndDate >= d)
            .Select(p => new { p.Name, p.Status })
            .FirstOrDefaultAsync();
        if (fp != null && (fp.Status == FiscalPeriodStatus.Closed || fp.Status == FiscalPeriodStatus.Locked))
            throw new Accounting.Helpers.BusinessRuleException(
                $"งวดบัญชี '{fp.Name}' ถูกปิด/ล็อกแล้ว — {action}ลงวันที่ {d:dd/MM/yyyy} ไม่ได้ · เลือกวันที่ในงวดที่เปิดอยู่ หรือให้ผู้มีสิทธิ์เปิดงวดก่อน",
                "GL-PERIOD-CLOSED", 409);
    }

    /// <summary>ยอดผูก (ทุกสินทรัพย์ · แถวที่ยังไม่ปลด) ต่อผังต้นทาง เทียบ GL ของใบ (<c>AssetCostSourceRules.ExcessOverGl</c>)</summary>
    private async Task<decimal> LinkExcessOverGlAsync(Guid companyId, Guid documentId, int sign)
    {
        var gl = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.SourceDocumentId == documentId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && !l.JournalEntry.IsDeleted)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToListAsync();
        var linked = await _db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.DocumentId == documentId && s.ReleasedAt == null)
            .Select(s => new { s.SourceAccountId, s.Amount })
            .ToListAsync();
        return Accounting.Helpers.AssetCostSourceRules.ExcessOverGl(
            gl.Select(x => new Accounting.Helpers.AssetCostSourceRules.AccountAmount(x.AccountId, x.Net)),
            linked.Select(x => new Accounting.Helpers.AssetCostSourceRules.AccountAmount(x.SourceAccountId, x.Amount)),
            sign);
    }

    /// <summary>ยอดผังสินทรัพย์ใน GL ของใบ กับยอดที่ทะเบียนรองรับ (ทะเบียนใบเดียว + แถวผูกบนผังสินทรัพย์) — ใช้ทั้ง "ลบแล้วค้างไหม" และ "ผูกแล้วเกินไหม"</summary>
    internal static async Task<(decimal Gl, decimal Registered)> AssetGlBackingAsync(
        AccountingDbContext db, Guid companyId, Guid documentId, IReadOnlyCollection<Guid> excludeAssetIds)
    {
        var byAccount = await db.JournalEntryLines.AsNoTracking()
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.SourceDocumentId == documentId
                && (l.JournalEntry.Status == JournalEntryStatus.Posted || l.JournalEntry.Status == JournalEntryStatus.Reversed)
                && !l.JournalEntry.IsDeleted)
            .GroupBy(l => l.Account.AccountCode)
            .Select(g => new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
            .ToListAsync();
        var gl = byAccount
            .Where(r => Accounting.Services.Implementations.Tax.FixedAssetAccountClassifier.Resolve(r.Code) != null)
            .Sum(r => r.Net);
        var legacyCost = await db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.SourceDocumentId == documentId && !excludeAssetIds.Contains(a.Id))
            .SumAsync(a => (decimal?)a.PurchaseCost) ?? 0m;
        // รอบ 249: แถวผูก (สินทรัพย์หลายใบ) ที่ยอดอยู่บนผังสินทรัพย์/งานระหว่างก่อสร้าง ก็เป็น "ทะเบียนรองรับ" ของ GL ใบนี้ —
        // มัดจำ 118xx ไม่นับ (ไม่ได้อยู่บนผังสินทรัพย์ของใบนี้) · JE โอนไม่มี SourceDocumentId ⇒ GL ของใบไม่ขยับหลังโอน
        var linkRows = await db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.DocumentId == documentId && s.ReleasedAt == null
                && !excludeAssetIds.Contains(s.FixedAssetId))
            .Select(s => new { s.SourceAccountCode, s.Amount })
            .ToListAsync();
        var linked = linkRows
            .Where(s => Accounting.Services.Implementations.Tax.FixedAssetAccountClassifier.Resolve(s.SourceAccountCode) != null)
            .Sum(s => s.Amount);
        return (gl, legacyCost + linked);
    }

    /// <summary>บรรทัดที่ผูกเป็นต้นทุนสินทรัพย์อยู่ (แถวที่ยังไม่ปลด) — ตัวตอบของการอนุมัติ (ข้าม auto-register) และด่านแก้ผัง</summary>
    internal static async Task<HashSet<Guid>> LinkedLineIdsAsync(AccountingDbContext db, Guid companyId, Guid documentId)
        => (await db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.DocumentId == documentId && s.ReleasedAt == null)
            .Select(s => s.DocumentLineId).Distinct().ToListAsync()).ToHashSet();

    /// <summary>ด่านแก้ผังบัญชีของบรรทัดที่ผูกอยู่ (DocumentService.ReclassifyLineAccountAsync) — null = แก้ได้</summary>
    public static async Task<string?> CostSourceLineChangeBlockAsync(AccountingDbContext db, Guid companyId, Guid lineId)
    {
        var codes = await db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.DocumentLineId == lineId && s.ReleasedAt == null)
            .Select(s => s.FixedAsset.AssetCode).Distinct().ToListAsync();
        return Accounting.Helpers.AssetCostSourceRules.LineChangeBlock(codes);
    }

    /// <summary>ด่านยกเลิกใบต้นทาง (DocumentService.VoidDocumentAsync) — แถวที่โอนแล้ว ⇒ ข้อความ (409) · null = ยกเลิกได้</summary>
    public static async Task<string?> CostSourceVoidBlockAsync(AccountingDbContext db, Guid companyId, Guid documentId, string documentNumber)
    {
        var codes = await db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.DocumentId == documentId && s.ReleasedAt == null && s.CapitalizedAt != null)
            .Select(s => s.FixedAsset.AssetCode).Distinct().ToListAsync();
        return Accounting.Helpers.AssetCostSourceRules.VoidBlock(documentNumber, codes);
    }

    /// <summary>ยกเลิกใบต้นทาง: ปลดแถวที่ยังไม่โอน + คิดต้นทุนสะสมใหม่ — <b>ไม่ SaveChanges</b> (อยู่ในธุรกรรมของการยกเลิก) ·
    /// ผู้เรียกต้องผ่าน <see cref="CostSourceVoidBlockAsync"/> มาก่อน (แถวที่โอนแล้วห้ามมาถึงตรงนี้ — ถ้ามา โยนดัง)</summary>
    public static async Task<int> ReleaseCostSourcesForVoidAsync(AccountingDbContext db, Guid companyId, Guid documentId, string documentNumber)
    {
        // ล็อกแถวเอกสาร (ธุรกรรมของการยกเลิก) — "โอนเป็นสินทรัพย์พร้อมใช้" ล็อกแถวเดียวกันก่อนอ่านแถวผูก ⇒ สองเส้นไม่สอดกัน
        await db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", documentId, companyId);
        var rows = await db.FixedAssetCostSources
            .Where(s => s.CompanyId == companyId && s.DocumentId == documentId && s.ReleasedAt == null)
            .ToListAsync();
        if (rows.Count == 0) return 0;
        if (rows.Any(r => r.CapitalizedAt != null))
            throw new Accounting.Helpers.BusinessRuleException(
                Accounting.Helpers.AssetCostSourceRules.VoidBlock(documentNumber, new[] { documentNumber })!,
                Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            r.ReleasedAt = now;
            r.ReleasedReason = $"เอกสารต้นทาง {documentNumber} ถูกยกเลิก";
            r.ReleasedBy = "system:void";
            r.UpdatedAt = now;
        }
        foreach (var assetId in rows.Select(r => r.FixedAssetId).Distinct())
        {
            var asset = await db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId);
            if (asset == null) continue;
            var released = rows.Where(r => r.FixedAssetId == assetId).Sum(r => r.Amount);
            asset.PurchaseCost -= released;
            asset.NetBookValue -= released;
            asset.UpdatedAt = now;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                Action = AuditAction.Update,
                EntityType = nameof(FixedAsset),
                EntityId = asset.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "cost-source-released-by-void",
                    documentNumber,
                    released,
                    heldCostAfter = asset.PurchaseCost,
                    ruleCode = Accounting.Helpers.AssetCostSourceRules.RuleCode,
                }),
                Timestamp = now,
            });
        }
        return rows.Count;
    }

    public async Task<AttachCostSourcesResult> CreateFromSourcesAsync(Guid companyId, CreateAssetFromSourcesRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new Accounting.Helpers.BusinessRuleException("ระบุชื่อสินทรัพย์");
        if (request.Items == null || request.Items.Count == 0)
            throw new Accounting.Helpers.BusinessRuleException("เลือกบรรทัดเอกสารอย่างน้อย 1 บรรทัด (ใบมัดจำ/ใบงวด/ใบสุดท้าย)");
        await _db.EnsurePostingAccountsAsync(companyId, new (Guid?, string)[] { (request.AssetAccountId, "บัญชีสินทรัพย์") });
        string? category = request.Category;
        if (request.AssetAccountId.HasValue)
        {
            var code = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == request.AssetAccountId.Value && a.CompanyId == companyId)
                .Select(a => a.AccountCode).FirstOrDefaultAsync();
            if (Accounting.Helpers.AssetCostSourceRules.RoleOf(code) != Accounting.Helpers.AssetCostSourceRules.AccountRole.Asset)
                throw new Accounting.Helpers.BusinessRuleException(
                    "ผังสินทรัพย์ที่ตั้งใจไว้ต้องเป็นผังสินทรัพย์พร้อมใช้ (เช่น 12260) — ไม่ใช่งานระหว่างก่อสร้าง/มัดจำ (ยอดระหว่างจัดหาอยู่ผังเดิมของแต่ละใบจนกว่าจะโอน)",
                    Accounting.Helpers.AssetCostSourceRules.RuleCode);
            category ??= Tax.FixedAssetAccountClassifier.Resolve(code)?.Category;
        }

        var ownsTx = _db.Database.CurrentTransaction is null;
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            var asset = new FixedAsset
            {
                CompanyId = companyId,
                AssetCode = $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14),
                Name = request.Name.Trim(),
                Description = request.Note,
                Category = category,
                Location = request.Location,
                SerialNumber = request.SerialNumber,
                PurchaseDate = TodayBangkok(),
                PurchaseCost = 0m,
                NetBookValue = 0m,
                SalvageValue = 0m,
                UsefulLifeMonths = 0,
                DepreciationMethod = DepreciationMethod.None,   // ระหว่างจัดหา = ไม่คิดค่าเสื่อม (ตัวคิดค่าเสื่อมข้ามเอง)
                AssetAccountId = request.AssetAccountId,
                ProjectId = request.ProjectId,
                IsUnderAcquisition = true,
                NeedsReview = false,
                CreatedBy = actor,
            };
            _db.FixedAssets.Add(asset);
            await _db.SaveChangesAsync();
            var (linked, absorbed, jeId) = await LinkLinesCoreAsync(companyId, asset, request.Items, request.Note, actor);
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            return new AttachCostSourcesResult(MapToResponse(asset), linked, absorbed, jeId);
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    public async Task<AttachCostSourcesResult> AttachCostSourcesAsync(Guid companyId, Guid assetId, AttachCostSourcesRequest request, string actor)
    {
        if (request.Items == null || request.Items.Count == 0)
            throw new Accounting.Helpers.BusinessRuleException("เลือกบรรทัดเอกสารอย่างน้อย 1 บรรทัด");
        var ownsTx = _db.Database.CurrentTransaction is null;
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            await LockAssetAsync(companyId, assetId);
            var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
            if (asset.Status != AssetStatus.Active)
                throw new Accounting.Helpers.BusinessRuleException(
                    "สินทรัพย์นี้จำหน่าย/ตัดจำหน่าย/หมดค่าเสื่อมแล้ว — ผูกต้นทุนเพิ่มไม่ได้", Accounting.Helpers.AssetCostSourceRules.RuleCode);
            var (linked, absorbed, jeId) = await LinkLinesCoreAsync(companyId, asset, request.Items, request.Note, actor);
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            return new AttachCostSourcesResult(MapToResponse(asset), linked, absorbed, jeId);
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <summary>แกนเดียวของการผูก (สร้างจากหลายใบ · ผูกเพิ่มก่อน/หลังโอน) — ต้องอยู่ในธุรกรรมของผู้เรียก · asset ต้องถูก track และบันทึกแล้ว</summary>
    private async Task<(int Linked, List<string> Absorbed, Guid? JournalEntryId)> LinkLinesCoreAsync(
        Guid companyId, FixedAsset asset, List<AttachCostSourceItem> items, string? note, string actor)
    {
        var dupInRequest = items.GroupBy(i => i.DocumentLineId).FirstOrDefault(g => g.Count() > 1);
        if (dupInRequest != null)
            throw new Accounting.Helpers.BusinessRuleException("เลือกบรรทัดเดียวกันซ้ำในคำขอเดียว — รวมเป็นรายการเดียว");
        var lineIds = items.Select(i => i.DocumentLineId).ToList();

        // ล็อกเอกสารต้นทางก่อนอ่านยอดที่ผูกไว้ (กันสองคำขอผูกบรรทัดเดียวกันพร้อมกัน)
        var docIds = await _db.DocumentLines.AsNoTracking()
            .Where(l => lineIds.Contains(l.Id) && l.Document.CompanyId == companyId)
            .Select(l => l.DocumentId).Distinct().ToListAsync();
        await LockDocumentsAsync(companyId, docIds);

        var facts = await LoadCostLineFactsAsync(companyId, lineIds);
        if (facts.Count != lineIds.Count)
            throw new KeyNotFoundException("ไม่พบบรรทัดเอกสารบางรายการในบริษัทนี้");

        // ยอด GL ผังสินทรัพย์ vs ทะเบียน ก่อนผูก (ด่าน "ห้ามทำให้แย่ลง" ต่อใบ) — คูณเครื่องหมายของใบ (ใบลดหนี้: GL/ทะเบียนติดลบทั้งคู่)
        int DocSign(Guid docId) => facts.First(x => x.Doc.Id == docId).Sign ?? 1;
        var overBefore = new Dictionary<Guid, decimal>();
        var excessBefore = new Dictionary<Guid, decimal>();
        foreach (var docId in docIds)
        {
            var (gl, reg) = await AssetGlBackingAsync(_db, companyId, docId, Array.Empty<Guid>());
            overBefore[docId] = DocSign(docId) * (reg - gl);
            excessBefore[docId] = await LinkExcessOverGlAsync(companyId, docId, DocSign(docId));
        }

        var absorbed = new List<string>();
        var newRows = new List<FixedAssetCostSource>();
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var f = facts.First(x => x.Line.Id == item.DocumentLineId);
            if (f.Problem != null)
                throw new Accounting.Helpers.BusinessRuleException(f.Problem, Accounting.Helpers.AssetCostSourceRules.RuleCode);
            if (f.LegacyAsset != null && f.LegacyAsset.Id == asset.Id)
                throw new Accounting.Helpers.BusinessRuleException($"“{f.Label}” เป็นใบที่สร้างสินทรัพย์นี้อยู่แล้ว");

            var remaining = Accounting.Helpers.AssetCostSourceRules.Remaining(f.CostAbs, f.LinkedAbs);
            var requested = item.Amount ?? remaining;
            var allocProblem = Accounting.Helpers.AssetCostSourceRules.AllocationProblem(f.CostAbs, f.LinkedAbs, requested, f.Label);
            if (allocProblem != null)
                throw new Accounting.Helpers.BusinessRuleException(allocProblem, Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);

            // ร่างเดี่ยวจากการอนุมัติ (ยังไม่ยืนยัน · ไม่มีค่าเสื่อม) ⇒ รวมเข้า: ลบร่างในธุรกรรมเดียวกัน
            if (f.LegacyAsset != null && f.LegacyAbsorbable)
            {
                var draft = f.LegacyAsset;
                var projected = await _db.AssetDepreciations
                    .Where(d => d.CompanyId == companyId && d.FixedAssetId == draft.Id).ToListAsync();
                _db.AssetDepreciations.RemoveRange(projected);
                _db.FixedAssets.Remove(draft);
                absorbed.Add(draft.AssetCode);
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = ActorId(actor),
                    Action = AuditAction.Delete,
                    EntityType = nameof(FixedAsset),
                    EntityId = draft.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { draft.AssetCode, draft.Name, draft.PurchaseCost, draft.SourceDocumentId, draft.SourceDocumentLineId }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "draft-absorbed-into-multi-document-asset",
                        intoAssetId = asset.Id,
                        ruleCode = Accounting.Helpers.AssetCostSourceRules.RuleCode,
                    }),
                    Timestamp = now,
                });
            }

            var sign = f.Sign ?? 1;
            var amount = sign * requested;
            // VAT ต้องห้ามตามสัดส่วนเมื่อผูกบางส่วน (ปัด 2 ตำแหน่ง AwayFromZero)
            var ncv = f.CostAbs == 0m ? 0m
                : Math.Round(f.Cost.NonClaimableVat * requested / f.CostAbs, 2, MidpointRounding.AwayFromZero);
            var row = new FixedAssetCostSource
            {
                CompanyId = companyId,
                FixedAssetId = asset.Id,
                DocumentId = f.Doc.Id,
                DocumentLineId = f.Line.Id,
                DocumentNumber = f.Doc.DocumentNumber,
                DocumentType = f.Doc.DocumentType,
                DocumentDate = f.Doc.DocumentDate,
                Kind = Accounting.Helpers.AssetCostSourceRules.KindFor(f.Doc.DocumentType, f.Role, f.Line.Description),
                Amount = amount,
                NonClaimableVatIncluded = ncv,
                SourceAccountId = f.Line.AccountId!.Value,
                SourceAccountCode = f.AccountCode,
                Note = note,
                CreatedBy = actor,
            };
            _db.FixedAssetCostSources.Add(row);
            newRows.Add(row);
        }
        await _db.SaveChangesAsync();

        // ด่านหลังผูก: ทะเบียน (ใบเดียว + แถวผูก) ต้องไม่เกิน GL ผังสินทรัพย์ของใบ "มากกว่าเดิม" — จับค่าประกอบ (ขนส่ง/ติดตั้ง) ที่เฉลี่ยเข้าทะเบียนเดี่ยวไปแล้ว
        // แล้วถูกผูกซ้ำ และร่างที่ถูกรวมแล้วทิ้งค่าประกอบไว้ (ทะเบียนน้อยกว่า GL — ตรวจด้วยทิศ "ค้าง")
        foreach (var docId in docIds)
        {
            // ตาข่ายกลาง: ยอดผูกต่อผังต้นทาง ≤ ยอดที่ใบนั้นลงผังนั้นจริงใน GL (มัดจำ 118xx รวมด้วย — ด่านทะเบียนข้างล่างดูแค่ผังสินทรัพย์)
            var excessAfter = await LinkExcessOverGlAsync(companyId, docId, DocSign(docId));
            if (excessAfter > excessBefore[docId] + Accounting.Helpers.AssetRemovalGlCheck.Tolerance)
                throw new Accounting.Helpers.BusinessRuleException(
                    $"เอกสาร {facts.First(x => x.Doc.Id == docId).Doc.DocumentNumber} ลงบัญชีบนผังต้นทางน้อยกว่ายอดที่ผูก {excessAfter - excessBefore[docId]:N2} บาท "
                    + "(ใบตัดชำระ/อ้างใบรับสินค้า/ถูกกลับรายการ/ยังไม่มีรายการบัญชี?) — ผูกได้ไม่เกินยอดที่ใบนั้นลงบัญชีจริง",
                    Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
            var (gl, reg) = await AssetGlBackingAsync(_db, companyId, docId, Array.Empty<Guid>());
            var over = DocSign(docId) * (reg - gl);
            if (over > overBefore[docId] + Accounting.Helpers.AssetRemovalGlCheck.Tolerance)
                throw new Accounting.Helpers.BusinessRuleException(
                    $"ผูกแล้วต้นทุนในทะเบียนของเอกสาร {facts.First(x => x.Doc.Id == docId).Doc.DocumentNumber} จะเกินยอดที่ใบนี้ลงผังสินทรัพย์ {over - overBefore[docId]:N2} บาท "
                    + "(บรรทัดนี้อาจถูกเฉลี่ยเข้าทะเบียนเดี่ยวไปแล้ว เช่นค่าขนส่ง/ติดตั้ง) — ยอดเดียวกันเป็นต้นทุนสองครั้งไม่ได้",
                    Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
            var orphanBefore = -overBefore[docId];
            var orphanAfter = -over;
            if (absorbed.Count > 0 && orphanAfter > Math.Max(orphanBefore, 0m) + Accounting.Helpers.AssetRemovalGlCheck.Tolerance)
                throw new Accounting.Helpers.BusinessRuleException(
                    $"รวมทะเบียนร่างแล้วยอดผังสินทรัพย์ของเอกสาร {facts.First(x => x.Doc.Id == docId).Doc.DocumentNumber} จะค้าง {orphanAfter - Math.Max(orphanBefore, 0m):N2} บาท "
                    + "โดยไม่มีทะเบียนรองรับ (ร่างนั้นรวมค่าขนส่ง/ติดตั้งจากบรรทัดอื่น) — เลือกบรรทัดค่าประกอบของใบเดียวกันมาผูกด้วยในคำขอเดียวกัน",
                    Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
        }

        var delta = newRows.Sum(r => r.Amount);
        Guid? jeId = null;
        if (asset.IsUnderAcquisition)
        {
            // ระหว่างจัดหา: สะสมต้นทุน (ยอดยังอยู่ผังของแต่ละใบ — ไม่มี JE)
            asset.PurchaseCost += delta;
            asset.NetBookValue += delta;
        }
        else
        {
            jeId = await ApplyPostCapitalizationCostAsync(companyId, asset, newRows, actor);
        }
        asset.UpdatedAt = now;

        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = ActorId(actor),
            Action = AuditAction.Update,
            EntityType = nameof(FixedAsset),
            EntityId = asset.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = asset.IsUnderAcquisition ? "cost-sources-linked" : "cost-sources-linked-after-capitalization",
                sources = newRows.Select(r => new { r.DocumentNumber, r.DocumentLineId, r.Amount, r.SourceAccountCode, kind = r.Kind.ToString() }),
                delta,
                costAfter = asset.PurchaseCost,
                absorbedDrafts = absorbed,
                journalEntryId = jeId,
                ruleCode = Accounting.Helpers.AssetCostSourceRules.RuleCode,
            }),
            Timestamp = now,
        });
        return (newRows.Count, absorbed, jeId);
    }

    /// <summary>ผูกใบหลังโอนแล้ว: JE ย้ายผัง (ถ้าผังต่าง) + ปรับต้นทุน/ฐานค่าเสื่อมไปข้างหน้า (งวดที่ลงแล้วไม่ถูกแตะ)</summary>
    private async Task<Guid?> ApplyPostCapitalizationCostAsync(Guid companyId, FixedAsset asset, List<FixedAssetCostSource> rows, string actor)
    {
        if (!asset.AssetAccountId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException("สินทรัพย์นี้ยังไม่ได้ผูกผังสินทรัพย์ — ผูกผังก่อนจึงปรับต้นทุนได้");
        var delta = rows.Sum(r => r.Amount);

        var lastPosted = await _db.AssetDepreciations
            .Where(d => d.CompanyId == companyId && d.FixedAssetId == asset.Id && d.IsPosted)
            .OrderByDescending(d => d.Year).ThenByDescending(d => d.Month)
            .Select(d => new { d.Year, d.Month })
            .FirstOrDefaultAsync();
        var elapsed = lastPosted == null ? 0
            : Accounting.Helpers.DepreciationSchedule.MonthIndexFor(asset.PurchaseDate, lastPosted.Year, lastPosted.Month) + 1;
        var problem = Accounting.Helpers.AssetCostSourceRules.AdjustmentProblem(
            asset.NetBookValue, delta, asset.SalvageValue, asset.DepreciationMethod, asset.UsefulLifeMonths, elapsed);
        if (problem != null)
            throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);

        var targetCode = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.Id == asset.AssetAccountId.Value && a.CompanyId == companyId)
            .Select(a => a.AccountCode).FirstOrDefaultAsync();
        var legs = Accounting.Helpers.AssetCostSourceRules.TransferLegs(asset.AssetAccountId.Value, targetCode,
            rows.Select(r => new Accounting.Helpers.AssetCostSourceRules.Source(r.SourceAccountId, r.SourceAccountCode, r.Amount)).ToList());
        var entryDate = rows.Max(r => r.DocumentDate);
        if (asset.AvailableForUseDate.HasValue && entryDate < asset.AvailableForUseDate.Value) entryDate = asset.AvailableForUseDate.Value;
        var je = await PostAssetTransferJournalAsync(companyId, asset, legs, entryDate,
            $"ปรับต้นทุนสินทรัพย์ {asset.AssetCode} {asset.Name} จาก {string.Join(", ", rows.Select(r => r.DocumentNumber).Distinct())}", actor);

        var adj = Accounting.Helpers.AssetCostSourceRules.Adjust(
            asset.PurchaseCost, asset.NetBookValue, delta, asset.SalvageValue, asset.UsefulLifeMonths, elapsed);
        asset.PurchaseCost = adj.NewCost;
        asset.NetBookValue = adj.NewNetBookValue;
        asset.DepreciableBaseAtReview = adj.DepreciableBase;
        asset.RemainingLifeMonthsAtReview = adj.RemainingLifeMonths;
        asset.ReviewEffectiveFromMonthIndex = adj.EffectiveFromMonthIndex;
        await RebuildPlannedScheduleAsync(companyId, asset, actor);

        var now = DateTime.UtcNow;
        foreach (var r in rows)
        {
            r.CapitalizedAt = now;
            r.CapitalizationJournalEntryId = je?.Id;
        }
        return je?.Id;
    }

    /// <summary>แผนค่าเสื่อมที่ยังไม่ลงบัญชีสร้างใหม่จากสถานะสินทรัพย์ปัจจุบัน (สูตรเดียว BuildScheduleRows)</summary>
    private async Task RebuildPlannedScheduleAsync(Guid companyId, FixedAsset asset, string actor)
    {
        var stale = await _db.AssetDepreciations
            .Where(d => d.CompanyId == companyId && d.FixedAssetId == asset.Id && !d.IsPosted)
            .ToListAsync();
        _db.AssetDepreciations.RemoveRange(stale);
        foreach (var (y, m, amount, accumulated, nbv) in BuildScheduleRows(asset))
        {
            _db.AssetDepreciations.Add(new AssetDepreciation
            {
                CompanyId = companyId, FixedAssetId = asset.Id,
                Year = y, Month = m, Amount = amount,
                AccumulatedAmount = accumulated, NetBookValue = nbv,
                IsPosted = false, CreatedBy = actor,
            });
        }
    }

    /// <summary>ลง JE โอน/ปรับต้นทุนตามขาที่ <c>AssetCostSourceRules.TransferLegs</c> วางไว้ — ขาว่าง = ไม่ลง (คืน null) · ตรวจงวดเปิด + สมดุล</summary>
    private async Task<JournalEntry?> PostAssetTransferJournalAsync(
        Guid companyId, FixedAsset asset, List<Accounting.Helpers.AssetCostSourceRules.Leg> legs, DateTime entryDate, string description, string actor)
    {
        if (legs.Count == 0) return null;
        // ผังปลายทางผ่าน PostingAccountGuard มาแล้วตอนโอน · ขาเครดิตคือผังที่ใบต้นทางลงไว้จริง (ล้างยอดออก — ไม่ตรวจซ้ำ ไม่งั้นผังที่ถูกปิดใช้ทีหลังล้างไม่ได้)
        await EnsurePeriodOpenForAssetJournalAsync(companyId, entryDate, "รายการโอนต้นทุนสินทรัพย์ ");
        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-AST-CAP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            EntryDate = entryDate,
            JournalType = JournalType.General,
            Description = description,
            Reference = asset.AssetCode,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            ProjectId = asset.ProjectId,
            CreatedBy = actor,
        };
        var order = 1;
        foreach (var leg in legs)
            je.Lines.Add(new JournalEntryLine
            {
                AccountId = leg.AccountId,
                DebitAmount = leg.Debit,
                CreditAmount = leg.Credit,
                Description = leg.Debit > 0m ? $"โอนเข้าสินทรัพย์ {asset.Name}" : $"โอนออกเป็นต้นทุนสินทรัพย์ {asset.AssetCode}",
                ProjectId = asset.ProjectId,
                LineOrder = order++,
            });
        je.TotalDebit = je.Lines.Sum(l => l.DebitAmount);
        je.TotalCredit = je.Lines.Sum(l => l.CreditAmount);
        if (je.TotalDebit != je.TotalCredit)
            throw new Accounting.Helpers.BusinessRuleException(
                $"รายการโอนต้นทุนสินทรัพย์ไม่สมดุล: Dr={je.TotalDebit:N2} Cr={je.TotalCredit:N2}", Accounting.Helpers.AssetCostSourceRules.RuleCode);
        _db.JournalEntries.Add(je);
        return je;
    }

    public async Task<AssetCostSourcesResponse> GetCostSourcesAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
        var rows = await _db.FixedAssetCostSources.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.FixedAssetId == assetId)
            .OrderBy(s => s.DocumentDate).ThenBy(s => s.CreatedAt)
            .ToListAsync();
        var docIds = rows.Select(r => r.DocumentId).Distinct().ToList();
        var docStatus = docIds.Count == 0 ? new Dictionary<Guid, DocumentStatus>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Status })
                .ToDictionaryAsync(x => x.Id, x => x.Status);
        var lineIds = rows.Select(r => r.DocumentLineId).Distinct().ToList();
        var lineInfo = lineIds.Count == 0 ? new Dictionary<Guid, (string Desc, string? Code)>()
            : (await _db.DocumentLines.AsNoTracking()
                .Where(l => lineIds.Contains(l.Id) && l.Document.CompanyId == companyId)
                .Select(l => new { l.Id, l.Description, Code = l.Account != null ? l.Account.AccountCode : null })
                .ToListAsync())
                .ToDictionary(x => x.Id, x => (Desc: x.Description, Code: x.Code));

        var problems = new List<string>();
        var list = rows.Select(r =>
        {
            var voided = !docStatus.TryGetValue(r.DocumentId, out var st) || !Accounting.Helpers.DocumentStatusRules.IsEffective(st);
            var status = r.ReleasedAt != null ? "Released" : r.CapitalizedAt != null ? "Capitalized" : "Held";
            lineInfo.TryGetValue(r.DocumentLineId, out var li);
            if (r.ReleasedAt == null && voided)
                problems.Add($"เอกสาร {r.DocumentNumber} ถูกยกเลิก/ไม่มีผลแล้ว แต่ยังผูกเป็นต้นทุน {r.Amount:N2} บาท — "
                    + (r.CapitalizedAt == null ? "ปลดออกก่อนโอน" : "ต้องปรับปรุงด้วยใบลดหนี้/รายการปรับปรุง"));
            if (r.ReleasedAt == null && r.CapitalizedAt == null
                && Accounting.Helpers.AssetCostSourceRules.RoleOf(li.Code) == Accounting.Helpers.AssetCostSourceRules.AccountRole.NotEligible)
                problems.Add($"บรรทัดของเอกสาร {r.DocumentNumber} ถูกย้ายไปผัง {li.Code ?? "(ไม่ระบุ)"} แล้ว — ปลดออกหรือแก้ผังกลับก่อนโอน");
            return new AssetCostSourceResponse(
                r.Id, r.DocumentId, r.DocumentNumber, r.DocumentType.ToString(), r.DocumentDate,
                r.DocumentLineId, li.Desc, r.Kind.ToString(), r.Amount, r.NonClaimableVatIncluded,
                r.SourceAccountCode, status, r.CapitalizedAt, r.CapitalizationJournalEntryId,
                voided, r.ReleasedAt, r.ReleasedReason, r.Note);
        }).ToList();
        var active = rows.Where(r => r.ReleasedAt == null).ToList();
        var held = active.Where(r => r.CapitalizedAt == null).Sum(r => r.Amount);
        var capitalized = active.Where(r => r.CapitalizedAt != null).Sum(r => r.Amount);
        // ระหว่างจัดหา ราคาทุนในทะเบียนต้องเท่ากับ Σ แถวที่ถือ (ผู้เขียนมีแค่ไฟล์นี้) — ไม่ตรง = มีเส้นที่แก้ราคาทุนข้ามตัวนี้ ·
        // หลังโอนไม่ตรวจ (ตีราคาใหม่เปลี่ยนราคาทุนได้โดยชอบ · สินทรัพย์ใบเดียวเดิมมีต้นทุนจากบรรทัดของตัวเองที่ไม่อยู่ในตารางนี้)
        if (asset.IsUnderAcquisition && asset.PurchaseCost != held)
            problems.Add($"ราคาทุนสะสมในทะเบียน {asset.PurchaseCost:N2} ไม่เท่ากับ Σ เอกสารต้นทุนที่ถืออยู่ {held:N2} — ตรวจการแก้ไขที่ทำนอกหน้านี้");
        return new AssetCostSourcesResponse(asset.Id, asset.IsUnderAcquisition, asset.AvailableForUseDate,
            held, capitalized, asset.PurchaseCost, list, problems);
    }

    public async Task<AssetCostSourcesResponse> ReleaseCostSourceAsync(Guid companyId, Guid assetId, Guid sourceId, ReleaseCostSourceRequest request, string actor)
    {
        var ownsTx = _db.Database.CurrentTransaction is null;
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            await LockAssetAsync(companyId, assetId);
            var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
            var row = await _db.FixedAssetCostSources
                .FirstOrDefaultAsync(s => s.Id == sourceId && s.FixedAssetId == assetId && s.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบเอกสารต้นทุนนี้ในสินทรัพย์");
            var problem = Accounting.Helpers.AssetCostSourceRules.ReleaseProblem(
                row.CapitalizedAt != null, row.ReleasedAt != null, request?.Reason);
            if (problem != null)
                throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
            var now = DateTime.UtcNow;
            row.ReleasedAt = now;
            row.ReleasedReason = request!.Reason.Trim();
            row.ReleasedBy = actor;
            row.UpdatedAt = now;
            asset.PurchaseCost -= row.Amount;
            asset.NetBookValue -= row.Amount;
            asset.UpdatedAt = now;
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = ActorId(actor),
                Action = AuditAction.Update,
                EntityType = nameof(FixedAsset),
                EntityId = asset.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "cost-source-released",
                    row.DocumentNumber, row.DocumentLineId, row.Amount,
                    reason = row.ReleasedReason,
                    heldCostAfter = asset.PurchaseCost,
                    ruleCode = Accounting.Helpers.AssetCostSourceRules.RuleCode,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
        return await GetCostSourcesAsync(companyId, assetId);
    }

    public async Task<FixedAssetResponse> PlaceInServiceAsync(Guid companyId, Guid assetId, PlaceInServiceRequest request, string actor)
    {
        var ownsTx = _db.Database.CurrentTransaction is null;
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            await LockAssetAsync(companyId, assetId);
            var asset = await _db.FixedAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
            // ล็อกเอกสารต้นทาง "ก่อน" อ่านแถวผูกแบบ track — การยกเลิกใบที่วิ่งพร้อมกันปลดแถวไปแล้วต้องไม่ถูกโอนจากค่าที่อ่านค้างไว้
            var lockDocIds = await _db.FixedAssetCostSources.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.FixedAssetId == assetId && s.ReleasedAt == null)
                .Select(s => s.DocumentId).Distinct().ToListAsync();
            await LockDocumentsAsync(companyId, lockDocIds);
            var rows = await _db.FixedAssetCostSources
                .Where(s => s.CompanyId == companyId && s.FixedAssetId == assetId && s.ReleasedAt == null)
                .ToListAsync();

            await _db.EnsurePostingAccountsAsync(companyId, new (Guid?, string)[]
            {
                (request.AssetAccountId, "บัญชีสินทรัพย์"),
                (request.DepreciationExpenseAccountId, "บัญชีค่าเสื่อมราคา"),
                (request.AccumulatedDepreciationAccountId, "บัญชีค่าเสื่อมราคาสะสม"),
            });
            var targetCode = await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == request.AssetAccountId && a.CompanyId == companyId)
                .Select(a => a.AccountCode).FirstOrDefaultAsync();
            var targetClass = Tax.FixedAssetAccountClassifier.Resolve(targetCode);

            // ข้อเท็จจริงของใบต้นทาง ณ ตอนนี้: ยังมีผลไหม · บรรทัดอยู่ผังไหน (ผู้ใช้อาจย้ายผังระหว่างจัดหา — JE ต้องเครดิตผังที่ยอดอยู่จริง)
            var docIds = rows.Select(r => r.DocumentId).Distinct().ToList();
            var effectiveDocs = (await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Status }).ToListAsync())
                .Where(d => Accounting.Helpers.DocumentStatusRules.IsEffective(d.Status)).Select(d => d.Id).ToHashSet();
            var lineIds = rows.Select(r => r.DocumentLineId).ToList();
            var lineAccounts = await _db.DocumentLines.AsNoTracking()
                .Where(l => lineIds.Contains(l.Id) && l.Document.CompanyId == companyId && l.AccountId != null)
                .Select(l => new { l.Id, AccountId = l.AccountId!.Value, Code = l.Account!.AccountCode })
                .ToListAsync();
            var voided = rows.Where(r => !effectiveDocs.Contains(r.DocumentId)).Select(r => r.DocumentId).Distinct().Count();
            var ineligible = 0;
            foreach (var r in rows)
            {
                var la = lineAccounts.FirstOrDefault(x => x.Id == r.DocumentLineId);
                if (la == null || Accounting.Helpers.AssetCostSourceRules.RoleOf(la.Code) == Accounting.Helpers.AssetCostSourceRules.AccountRole.NotEligible)
                {
                    ineligible++;
                    continue;
                }
                r.SourceAccountId = la.AccountId;
                r.SourceAccountCode = la.Code;
            }

            var cost = rows.Sum(r => r.Amount);
            var method = request.DepreciationMethod;
            var problem = Accounting.Helpers.AssetCostSourceRules.PlaceInServiceProblem(
                asset.IsUnderAcquisition, rows.Count, voided, ineligible, cost, request.SalvageValue,
                Accounting.Helpers.AssetCostSourceRules.RoleOf(targetCode), targetClass?.Depreciable ?? false,
                method, request.UsefulLifeMonths, request.AvailableForUseDate, TodayBangkok());
            if (problem != null)
                throw new Accounting.Helpers.BusinessRuleException(problem, Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);

            var depExpenseId = request.DepreciationExpenseAccountId;
            var accumId = request.AccumulatedDepreciationAccountId;
            if (method != DepreciationMethod.None && targetClass != null && (depExpenseId == null || accumId == null))
            {
                // ผังค่าเสื่อม/ค่าเสื่อมสะสมตามประเภท (ตัวจำแนกเดียวกับการขึ้นทะเบียนจากใบซื้อ)
                var wanted = new[] { targetClass.DepExpenseAccountCode, targetClass.AccumDepAccountCode }.Where(c => c != null).ToList();
                var map = await _db.ChartOfAccounts.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && wanted.Contains(a.AccountCode))
                    .Select(a => new { a.Id, a.AccountCode }).ToListAsync();
                depExpenseId ??= map.FirstOrDefault(a => a.AccountCode == targetClass.DepExpenseAccountCode)?.Id;
                accumId ??= map.FirstOrDefault(a => a.AccountCode == targetClass.AccumDepAccountCode)?.Id;
            }
            if (method != DepreciationMethod.None && (depExpenseId == null || accumId == null))
                throw new Accounting.Helpers.BusinessRuleException(
                    "ระบุผังค่าเสื่อมราคาและค่าเสื่อมราคาสะสม — ไม่งั้นค่าเสื่อมรายเดือนจะไม่ลงบัญชี", Accounting.Helpers.AssetCostSourceRules.RuleCode);

            var availableDate = request.AvailableForUseDate.Date;
            var legs = Accounting.Helpers.AssetCostSourceRules.TransferLegs(request.AssetAccountId, targetCode,
                rows.Select(r => new Accounting.Helpers.AssetCostSourceRules.Source(r.SourceAccountId, r.SourceAccountCode, r.Amount)).ToList());
            if (asset.AssetCode.StartsWith("DRAFT-"))
                asset.AssetCode = await GenerateAssetCodeAsync(companyId);
            var je = await PostAssetTransferJournalAsync(companyId, asset, legs, availableDate,
                $"โอนเป็นสินทรัพย์พร้อมใช้ {asset.AssetCode} {asset.Name} (ต้นทุนจาก {string.Join(", ", rows.Select(r => r.DocumentNumber).Distinct())})", actor);

            var before = new { asset.PurchaseCost, asset.AssetAccountId, method = asset.DepreciationMethod.ToString(), asset.IsUnderAcquisition };
            asset.IsUnderAcquisition = false;
            asset.AvailableForUseDate = availableDate;
            asset.PurchaseDate = availableDate;      // จุดตั้งต้นค่าเสื่อม (DepreciationSchedule: งวดแรก = เดือนถัดไป)
            asset.PurchaseCost = cost;
            asset.NetBookValue = cost;
            asset.AccumulatedDepreciation = 0m;
            asset.SalvageValue = request.SalvageValue;
            asset.DepreciationMethod = method;
            asset.UsefulLifeMonths = method == DepreciationMethod.None ? 0 : request.UsefulLifeMonths;
            asset.AssetAccountId = request.AssetAccountId;
            asset.DepreciationExpenseAccountId = depExpenseId;
            asset.AccumulatedDepreciationAccountId = accumId;
            asset.DepreciableBaseAtReview = null;
            asset.RemainingLifeMonthsAtReview = null;
            asset.ReviewEffectiveFromMonthIndex = null;
            asset.Category ??= targetClass?.Category;
            asset.NeedsReview = false;
            asset.UpdatedAt = DateTime.UtcNow;
            await RebuildPlannedScheduleAsync(companyId, asset, actor);

            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                r.CapitalizedAt = now;
                r.CapitalizationJournalEntryId = je?.Id;
                r.UpdatedAt = now;
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = ActorId(actor),
                Action = AuditAction.Update,
                EntityType = nameof(FixedAsset),
                EntityId = asset.Id.ToString(),
                OldValues = System.Text.Json.JsonSerializer.Serialize(before),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "placed-in-service",
                    asset.AssetCode, availableForUseDate = availableDate, cost,
                    method = method.ToString(), asset.UsefulLifeMonths, asset.SalvageValue,
                    journalEntryId = je?.Id,
                    legs = legs.Select(l => new { l.AccountCode, l.Debit, l.Credit }),
                    ruleCode = Accounting.Helpers.AssetCostSourceRules.RuleCode,
                    legalReference = Accounting.Helpers.AssetCostSourceRules.LegalReference,
                }),
                Timestamp = now,
            });
            await _db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
            _logger?.LogInformation("Asset {Code} placed in service {Date:yyyy-MM-dd} cost {Cost}", asset.AssetCode, availableDate, cost);
            return MapToResponse(asset);
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync();
            throw;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    /// <summary>ด่านของเส้นที่แก้มูลค่า/สถานะสินทรัพย์ (จำหน่าย · ตัดจำหน่าย · ตีราคา · ปรับอายุ · แก้วิธีคิดค่าเสื่อม) — ระหว่างจัดหาห้าม</summary>
    private static void EnsureNotUnderAcquisition(FixedAsset asset, string action)
    {
        if (asset.IsUnderAcquisition)
            throw new Accounting.Helpers.BusinessRuleException(
                $"{action}ไม่ได้ — สินทรัพย์ {asset.AssetCode} ยังอยู่ระหว่างจัดหา/ติดตั้ง (ต้นทุนยังอยู่ผังมัดจำ/งานระหว่างก่อสร้างของแต่ละใบ). "
                + "กด 'โอนเป็นสินทรัพย์พร้อมใช้' ก่อน หรือปลดเอกสารต้นทุนออกแล้วลบ",
                Accounting.Helpers.AssetCostSourceRules.RuleCode, 409);
    }
}
