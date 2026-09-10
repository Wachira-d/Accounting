using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class FixedAssetService : IFixedAssetService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<FixedAssetService>? _logger;

    public FixedAssetService(AccountingDbContext db, ILogger<FixedAssetService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy)
    {
        var existing = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == request.AssetCode);
        if (existing)
            throw new InvalidOperationException($"รหัสสินทรัพย์ {request.AssetCode} ซ้ำ");

        // For a Right-of-Use lease, useful life defaults to the lease term if
        // the caller didn't supply it explicitly — TFRS 16 amortizes over the
        // shorter of useful life or lease term, and lease term is the safer
        // default for an intangible right.
        var effectiveLife = request.UsefulLifeMonths;
        if (request.AssetType == AssetType.RightOfUse && effectiveLife <= 0 && request.LeaseTermMonths.HasValue)
            effectiveLife = request.LeaseTermMonths.Value;

        // ── ที่ดิน / งานระหว่างก่อสร้าง คิดค่าเสื่อมไม่ได้ (ผลตรวจ C-T15) ──
        //
        // `FixedAssetAccountClassifier` รู้เรื่องนี้อยู่แล้ว (Depreciable=false)
        // แต่ **เส้นสร้างด้วยมือไม่เคยเรียกมัน** ⇒ ผู้ใช้เลือกผัง 12290 (ที่ดิน)
        // แล้วตั้งวิธีคิดค่าเสื่อมเป็นเส้นตรงได้ ⇒ ระบบสร้างตารางค่าเสื่อมและลง JE
        // ทุกเดือน ⇒ **ค่าเสื่อมที่ดินหักภาษีไม่ได้ (พ.ร.ฎ.145)** ต้องบวกกลับใน
        // ภ.ง.ด.50 ทั้งจำนวน — แต่ไม่มีใครรู้เพราะตัวเลขดูปกติทุกเดือน
        //
        // บล็อกดัง ๆ พร้อมบอกทางแก้ ไม่ใช่แก้ค่าให้เงียบ ๆ (ผู้ใช้อาจตั้งใจเลือก
        // ผังผิด — ถ้าเราแก้ให้เอง เขาจะไม่รู้ว่าผังที่เลือกไว้ไม่ตรงกับของจริง)
        var assetAccountCode = request.AssetAccountId == null ? null
            : await _db.ChartOfAccounts.AsNoTracking()
                .Where(a => a.Id == request.AssetAccountId && a.CompanyId == companyId)
                .Select(a => a.AccountCode).FirstOrDefaultAsync();
        var assetClass = Tax.FixedAssetAccountClassifier.Resolve(assetAccountCode);
        if (assetClass is { Depreciable: false }
            && request.DepreciationMethod != DepreciationMethod.None)
        {
            throw new Accounting.Helpers.BusinessRuleException(
                $"\"{assetClass.Category}\" (ผัง {assetClass.AssetAccountCode}) คิดค่าเสื่อมราคาไม่ได้ "
                + "— ค่าเสื่อมของที่ดิน/งานระหว่างก่อสร้างหักเป็นรายจ่ายทางภาษีไม่ได้ "
                + "(พ.ร.ฎ.145) ต้องบวกกลับทั้งจำนวนใน ภ.ง.ด.50. "
                + "ทางแก้: เลือกวิธีคิดค่าเสื่อมเป็น \"ไม่คิดค่าเสื่อม\" "
                + "หรือเปลี่ยนผังบัญชีให้ตรงกับสินทรัพย์จริง");
        }

        var asset = new FixedAsset
        {
            CompanyId = companyId,
            AssetCode = request.AssetCode,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Location = request.Location,
            SerialNumber = request.SerialNumber,
            PurchaseDate = request.PurchaseDate,
            PurchaseCost = request.PurchaseCost,
            SalvageValue = request.SalvageValue,
            UsefulLifeMonths = effectiveLife,
            DepreciationMethod = request.DepreciationMethod,
            NetBookValue = request.PurchaseCost,
            AssetAccountId = request.AssetAccountId,
            DepreciationExpenseAccountId = request.DepreciationExpenseAccountId,
            AccumulatedDepreciationAccountId = request.AccumulatedDepreciationAccountId,
            AssetType = request.AssetType,
            LeaseTermMonths = request.LeaseTermMonths,
            LessorName = request.LessorName,
            MonthlyLeasePayment = request.MonthlyLeasePayment,
            LeaseLiabilityAccountId = request.LeaseLiabilityAccountId,
            ProjectId = request.ProjectId,
            SourceDocumentId = request.SourceDocumentId,
            SourceDocumentLineId = request.SourceDocumentLineId,
            NeedsReview = request.NeedsReview,
            CreatedBy = createdBy
        };

        _db.FixedAssets.Add(asset);

        // Persist the projected depreciation schedule up-front (IsPosted=false).
        // The monthly CalculateDepreciationAsync run later "activates" each
        // planned row by posting its journal entry and flipping IsPosted.
        foreach (var row in BuildScheduleRows(asset))
        {
            _db.AssetDepreciations.Add(new AssetDepreciation
            {
                CompanyId = companyId,
                FixedAssetId = asset.Id,
                Year = row.Year,
                Month = row.Month,
                Amount = row.Amount,
                AccumulatedAmount = row.Accumulated,
                NetBookValue = row.Nbv,
                IsPosted = false,
                CreatedBy = createdBy
            });
        }

        // Optional acquisition journal entry — Dr Asset / Cr (cash or A/P).
        // The OCR "Register Asset" path posts its own entry and passes
        // PostAcquisitionJournalEntry=false to avoid a double post.
        if (request.PostAcquisitionJournalEntry)
        {
            if (!request.AssetAccountId.HasValue || !request.CreditAccountId.HasValue)
                throw new InvalidOperationException(
                    "ต้องระบุบัญชีสินทรัพย์และบัญชีเครดิต (เงินสด/เจ้าหนี้) เพื่อบันทึกบัญชีตอนซื้อสินทรัพย์");
            if (request.PurchaseCost <= 0)
                throw new InvalidOperationException("ราคาทุนต้องมากกว่า 0 จึงจะบันทึกบัญชีได้");

            var je = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = $"JV-AST-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}",
                EntryDate = request.PurchaseDate,
                JournalType = JournalType.General,
                Description = $"ลงทะเบียนสินทรัพย์ {asset.AssetCode} {asset.Name}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                TotalDebit = request.PurchaseCost,
                TotalCredit = request.PurchaseCost,
                CreatedBy = createdBy
            };
            je.Lines.Add(new JournalEntryLine
            {
                AccountId = request.AssetAccountId.Value,
                DebitAmount = request.PurchaseCost,
                CreditAmount = 0,
                Description = $"ลงทะเบียนสินทรัพย์ {asset.Name}"
            });
            je.Lines.Add(new JournalEntryLine
            {
                AccountId = request.CreditAccountId.Value,
                DebitAmount = 0,
                CreditAmount = request.PurchaseCost,
                Description = $"ชำระ/ตั้งเจ้าหนี้ค่าสินทรัพย์ {asset.Name}"
            });
            if (Math.Abs(je.TotalDebit - je.TotalCredit) > 0.01m)
                throw new InvalidOperationException(
                    $"รายการบัญชีลงทะเบียนสินทรัพย์ไม่สมดุล: Dr={je.TotalDebit:N2} Cr={je.TotalCredit:N2}");
            _db.JournalEntries.Add(je);
        }

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    /// <summary>
    /// Project the period-by-period depreciation rows for a freshly
    /// registered asset (NBV starts at full PurchaseCost). Straight-line
    /// and (double-)declining-balance are supported; declining methods
    /// stop once NBV reaches salvage value. Shared by CreateAsync /
    /// ImportAsync (persisted plan) and GetDepreciationScheduleAsync
    /// (report) so the projection math lives in exactly one place.
    /// </summary>
    private static List<(int Year, int Month, decimal Amount, decimal Accumulated, decimal Nbv)> BuildScheduleRows(FixedAsset asset)
    {
        var rows = new List<(int, int, decimal, decimal, decimal)>();
        // ที่ดิน/งานระหว่างก่อสร้าง = None → ไม่มีตารางค่าเสื่อม
        if (asset.DepreciationMethod == DepreciationMethod.None
            || asset.UsefulLifeMonths <= 0 || asset.PurchaseCost <= asset.SalvageValue)
            return rows;

        // สูตรอยู่ที่ Helpers/DepreciationSchedule ที่เดียว — เส้นที่โพสต์ JE
        // (`CalculateDepreciationAsync`) และ catch-up ตอนจำหน่าย เรียกตัวเดียวกัน
        // (เดิมสูตรถูกเขียนซ้ำสองที่ แล้วเส้นโพสต์ **ไม่มี switch-to-straight-line**
        //  ⇒ ตัวเลขที่ผู้ใช้เห็นล่วงหน้า ≠ ตัวเลขที่ลงบัญชี ตั้งแต่งวดที่ 2)
        // ทบทวนอายุแล้ว → แผนที่เหลือคิดจากฐานใหม่ และเริ่มที่งวดถัดจากวันทบทวน
        var ep = EffectiveDepParams(asset, 0);
        var shift = asset.ReviewEffectiveFromMonthIndex ?? 0;
        var already = asset.AccumulatedDepreciation;
        foreach (var p in Accounting.Helpers.DepreciationSchedule.Build(
                     asset.DepreciationMethod, ep.Cost, ep.Salvage, ep.Life,
                     asset.PurchaseDate.AddMonths(shift)))
            rows.Add((p.Year, p.Month, p.Amount,
                      shift > 0 ? already + p.Accumulated : p.Accumulated,
                      p.NetBookValue));
        return rows;
    }

    public async Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");
        return MapToResponse(asset);
    }

    public async Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.FixedAssets.Where(a => a.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var search = $"%{request.Search}%";
            query = query.Where(a => EF.Functions.ILike(a.Name, search) || EF.Functions.ILike(a.AssetCode, search));
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(a => a.AssetCode)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<FixedAssetResponse>(
            await MapWithSourceDocAsync(companyId, items),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    /// <summary>คืน list สินทรัพย์ที่ระบบ auto-register จาก PV/PI/Expense
    /// แล้ว NeedsReview=true (ยังไม่ผ่านการยืนยันจากผู้ใช้). UI โชว์ banner
    /// เตือน + บังคับให้กรอก UsefulLifeMonths/DepreciationMethod/Location
    /// ก่อนถึงจะเริ่มคิดค่าเสื่อมจริงได้.</summary>
    public async Task<List<FixedAssetResponse>> GetByDocumentAsync(Guid companyId, Guid documentId)
    {
        var items = await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.SourceDocumentId == documentId)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
        return await MapWithSourceDocAsync(companyId, items);
    }

    public async Task<List<FixedAssetResponse>> GetNeedsReviewAsync(Guid companyId)
    {
        var items = await _db.FixedAssets.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.NeedsReview)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
        return await MapWithSourceDocAsync(companyId, items);
    }

    /// <summary>ลบสินทรัพย์ที่ลงทะเบียนผิด (เช่น auto-register แยกผิด/ซ้ำ).
    /// guard: ห้ามลบถ้าคิดค่าเสื่อมจริงไปแล้ว (มี posted depreciation หรือ
    /// AccumulatedDepreciation > 0 หรือ disposed/written-off) — ต้อง Dispose/
    /// WriteOff แทน. ต้นทุน asset มาจากเอกสารต้นทาง (PostAcquisitionJournalEntry
    /// =false ตอน auto-register) → ลบ record ไม่กระทบ GL. ลบ projected
    /// depreciation rows (unposted) ที่ผูกอยู่ด้วย.</summary>
    public async Task DeleteAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.AccumulatedDepreciation > 0m)
            throw new InvalidOperationException(
                "ลบไม่ได้ — สินทรัพย์นี้คิดค่าเสื่อมไปแล้ว กรุณาใช้ 'จำหน่าย' หรือ 'ตัดจำหน่าย' แทน");
        if (asset.Status is not AssetStatus.Active)
            throw new InvalidOperationException(
                "ลบไม่ได้ — สินทรัพย์นี้ถูกจำหน่าย/ตัดจำหน่ายแล้ว");

        // กันลบ asset ที่มี depreciation ลง JE จริงแล้ว (posted)
        var hasPosted = await _db.AssetDepreciations
            .AnyAsync(d => d.FixedAssetId == assetId && d.IsPosted && !d.IsDeleted);
        if (hasPosted)
            throw new InvalidOperationException(
                "ลบไม่ได้ — มีค่าเสื่อมที่ลงบัญชีแล้ว กรุณาใช้ 'จำหน่าย' หรือ 'ตัดจำหน่าย' แทน");

        // กันลบ asset ที่ source PV/PI ยังใช้งานอยู่ — ใบต้นทางลง Dr 12210 ใน JE
        // ถ้าลบ asset ออกขณะใบยังอยู่ จะเกิด orphan (12210 บนงบดุล vs ทะเบียน
        // สินทรัพย์ไม่ตรง). อนุญาตเฉพาะ:
        //   • asset ที่ user สร้างเอง (ไม่มี SourceDocumentId)
        //   • asset ที่ source doc ถูก void/rejected แล้ว
        if (asset.SourceDocumentId.HasValue)
        {
            var sourceDoc = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == asset.SourceDocumentId.Value)
                .Select(d => new { d.DocumentNumber, d.Status })
                .FirstOrDefaultAsync();
            if (sourceDoc != null
                && sourceDoc.Status != DocumentStatus.Voided
                && sourceDoc.Status != DocumentStatus.Rejected)
                throw new InvalidOperationException(
                    $"ลบไม่ได้ — สินทรัพย์นี้สร้างจากเอกสาร {sourceDoc.DocumentNumber} ที่ยังใช้งานอยู่ "
                    + "(สถานะ: " + sourceDoc.Status + "). กรุณายกเลิกเอกสารต้นทางก่อน หรือใช้ 'จำหน่าย/ตัดจำหน่าย' แทน");
        }

        // ลบ projected depreciation rows (unposted) แล้วลบ asset
        var projectedDeps = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == assetId)
            .ToListAsync();
        _db.AssetDepreciations.RemoveRange(projectedDeps);
        _db.FixedAssets.Remove(asset);
        await _db.SaveChangesAsync();
    }

    public async Task<FixedAssetResponse> UpdateAsync(Guid companyId, Guid assetId, UpdateFixedAssetRequest request)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (request.Name != null) asset.Name = request.Name;
        if (request.Description != null) asset.Description = request.Description;
        if (request.Category != null) asset.Category = request.Category;
        if (request.Location != null) asset.Location = request.Location;
        if (request.SerialNumber != null) asset.SerialNumber = request.SerialNumber;
        // Guid.Empty = **ปลดค่า** (dropdown มี "— ไม่ระบุ —" ให้เลือกกลับ แต่เดิม
        // เลือกแล้วบันทึกไม่มีผล — สินทรัพย์ที่ถอดออกจากโครงการยังคิดค่าเสื่อม
        // เข้าโครงการที่ปิดไปแล้วต่อ)
        static Guid? G(Guid? v) => v == Guid.Empty ? null : v;
        if (request.AssetAccountId.HasValue) asset.AssetAccountId = G(request.AssetAccountId);
        if (request.DepreciationExpenseAccountId.HasValue) asset.DepreciationExpenseAccountId = G(request.DepreciationExpenseAccountId);
        if (request.AccumulatedDepreciationAccountId.HasValue) asset.AccumulatedDepreciationAccountId = G(request.AccumulatedDepreciationAccountId);
        if (request.ProjectId.HasValue) asset.ProjectId = G(request.ProjectId);

        // เมื่อผู้ใช้ "ยืนยัน" สินทรัพย์ที่ระบบ auto-register (กดบันทึกในหน้า edit)
        // → ปลดธง NeedsReview เพื่อออกจาก "รอตรวจสอบ" queue. ไม่ใช่ field ใน DTO
        // เพื่อกัน client เผลอเซ็ตกลับเป็น true; ใช้ implicit semantics ที่ว่า
        // "การเปิดมาแก้แล้วบันทึก = การตรวจสอบสินทรัพย์ตัวนี้แล้ว".
        if (asset.NeedsReview)
        {
            asset.NeedsReview = false;
            // ออก AssetCode จริงตอนยืนยัน (auto-register ใส่ "DRAFT-xxxxxxxx"
            // ไว้ไม่กิน counter) → gap-free + ลำดับเรียงตามเวลายืนยัน ไม่ใช่
            // เวลา auto-register ที่อาจถูกลบทิ้ง
            if (asset.AssetCode.StartsWith("DRAFT-"))
                asset.AssetCode = await GenerateAssetCodeAsync(companyId);
        }

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    /// <summary>สร้างรหัสสินทรัพย์ FA-yyyyMM-#### (gap-tolerant — MAX+1, ยกเว้น
    /// DRAFT-* placeholder). ใช้ตอนผู้ใช้ยืนยัน asset ที่ auto-register —
    /// ทำให้เลขจริงออกตามลำดับการยืนยัน ไม่ใช่ลำดับ scan/approve.</summary>
    private Task<string> GenerateAssetCodeAsync(Guid companyId)
        => Accounting.Helpers.AssetCodeGenerator.NextAsync(_db, companyId);

    public async Task<FixedAssetResponse> DisposeAsync(Guid companyId, Guid assetId, DisposeAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active && asset.Status != AssetStatus.FullyDepreciated)
            throw new InvalidOperationException("สินทรัพย์นี้ไม่สามารถจำหน่ายได้");

        // สินทรัพย์ที่ไม่มี GL mapping: เดิมข้ามการสร้าง JE ทั้งก้อนแบบเงียบ ๆ แต่ยัง
        // เขียนทะเบียนเป็น "ตัดครบแล้ว" ⇒ ทะเบียนกับ GL แยกทางกันถาวรโดย API ตอบว่า
        // "จำหน่ายสำเร็จ" (ผลตรวจทีม E · E-05 — คลาสเดียวกับ IntegrationService)
        if (!asset.AssetAccountId.HasValue || !asset.AccumulatedDepreciationAccountId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException(
                $"สินทรัพย์ “{asset.Name}” ยังไม่ได้ผูกผังบัญชีสินทรัพย์/ค่าเสื่อมสะสม — "
                + "ผูกบัญชีที่หน้าทะเบียนสินทรัพย์ก่อน จึงจะจำหน่ายได้ "
                + "(ไม่งั้นทะเบียนจะบอกว่าจำหน่ายแล้วแต่บัญชีไม่มีรายการใด ๆ)",
                "ASSET-DISPOSE-NO-GL");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
        // ค่าเสื่อมงวดที่ค้างถึงเดือนที่ขาย ต้องลงก่อนคิดกำไร/ขาดทุน (§65 ทวิ) —
        // ไม่งั้นกำไรจากการจำหน่ายเกินจริงเท่ากับค่าเสื่อมที่ขาด และงวดเหล่านั้น
        // จะไม่มีวันถูกโพสต์ย้อนหลังเพราะ Status กลายเป็น Disposed
        await PostCatchUpDepreciationAsync(companyId, asset, request.DisposalDate, performedBy);

        asset.Status = AssetStatus.Disposed;
        asset.DisposalDate = request.DisposalDate;
        asset.DisposalAmount = request.DisposalAmount;

        var gainLoss = request.DisposalAmount - asset.NetBookValue;

        {
            var entryNumber = $"DEP-DISP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";

            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.DisposalDate,
                JournalType = JournalType.General,
                Description = $"จำหน่ายสินทรัพย์: {asset.Name} ({asset.AssetCode})",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                ProjectId = asset.ProjectId,
                CreatedBy = performedBy
            };

            // Dr: ค่าเสื่อมราคาสะสม (ล้างยอดสะสม)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AccumulatedDepreciationAccountId.Value,
                DebitAmount = asset.AccumulatedDepreciation,
                CreditAmount = 0,
                Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}"
            });

            // Cr: สินทรัพย์ (ตัดออกราคาทุน)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = 0,
                CreditAmount = asset.PurchaseCost,
                Description = $"ตัดสินทรัพย์ - {asset.Name}"
            });

            // Dr: เงินสด/ธนาคาร (ถ้าขายได้เงิน)
            if (request.DisposalAmount > 0)
            {
                var cashAccount = await FindAccountAsync(companyId, "111")
                    ?? throw new InvalidOperationException(
                        "ไม่พบบัญชีเงินสด/ธนาคาร (111) ในผังบัญชี — ไม่สามารถบันทึกรายการจำหน่ายสินทรัพย์ได้");
                journalEntry.Lines.Add(new JournalEntryLine
                {
                    AccountId = cashAccount.Id,
                    DebitAmount = request.DisposalAmount,
                    CreditAmount = 0,
                    Description = $"รับเงินจากจำหน่ายสินทรัพย์ - {asset.Name}"
                });
            }

            // กำไร/ขาดทุนจากการจำหน่าย
            if (gainLoss != 0)
            {
                if (gainLoss > 0)
                {
                    var gainAccount = await FindAccountAsync(companyId, "43030")
                        ?? await FindAccountAsync(companyId, "430")
                        ?? await FindAccountAsync(companyId, "43")
                        ?? throw new InvalidOperationException(
                            "ไม่พบบัญชีกำไรจากการจำหน่ายสินทรัพย์ (43030/430/43) ในผังบัญชี");
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = gainAccount.Id,
                        DebitAmount = 0,
                        CreditAmount = gainLoss,
                        Description = $"กำไรจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
                else
                {
                    var lossAccount = await FindAccountAsync(companyId, "57110")
                        ?? await FindAccountAsync(companyId, "571")
                        ?? await FindAccountAsync(companyId, "57")
                        ?? throw new InvalidOperationException(
                            "ไม่พบบัญชีขาดทุนจากการจำหน่ายสินทรัพย์ (57110/571/57) ในผังบัญชี");
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = lossAccount.Id,
                        DebitAmount = Math.Abs(gainLoss),
                        CreditAmount = 0,
                        Description = $"ขาดทุนจากการจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);

            if (Math.Abs(journalEntry.TotalDebit - journalEntry.TotalCredit) > 0.01m)
                throw new InvalidOperationException(
                    $"รายการบัญชีจำหน่ายสินทรัพย์ไม่สมดุล: Dr={journalEntry.TotalDebit:N2} Cr={journalEntry.TotalCredit:N2}");

            _db.JournalEntries.Add(journalEntry);
        }

        // ⚠️ เดิมเขียน `AccumulatedDepreciation = PurchaseCost` ซึ่งเป็นค่าที่
        // **ไม่ตรงกับที่โพสต์จริง** (โพสต์ 60,000 แต่ทะเบียนบันทึก 120,000)
        // ⇒ รายงานทะเบียน/หมายเหตุประกอบงบที่อ่านฟิลด์นี้เล่าคนละเรื่องกับ GL
        // (ผลตรวจทีม E · E-05 ข้อ ง) · ตอนนี้คงยอดสะสมจริงไว้ (ซึ่งถูกต้องแล้ว
        // หลังคิดค่าเสื่อมค้างถึงวันจำหน่าย) และ NBV = 0 หมายถึง "ไม่ได้ถือครอง
        // แล้ว" ตามที่ JE ตัดออกทั้งราคาทุนและค่าเสื่อมสะสม
        asset.NetBookValue = 0;

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return MapToResponse(asset);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<FixedAssetResponse> WriteOffAsync(Guid companyId, Guid assetId, WriteOffAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active && asset.Status != AssetStatus.FullyDepreciated)
            throw new InvalidOperationException("สินทรัพย์นี้ไม่สามารถตัดจำหน่ายได้");

        // fail loud เหมือนเส้นจำหน่าย — ห้ามล้างทะเบียนทั้งที่ไม่มี JE ใด ๆ เกิดขึ้น
        if (!asset.AssetAccountId.HasValue || !asset.AccumulatedDepreciationAccountId.HasValue)
            throw new Accounting.Helpers.BusinessRuleException(
                $"สินทรัพย์ “{asset.Name}” ยังไม่ได้ผูกผังบัญชีสินทรัพย์/ค่าเสื่อมสะสม — "
                + "ผูกบัญชีที่หน้าทะเบียนสินทรัพย์ก่อน จึงจะตัดจำหน่ายได้",
                "ASSET-WRITEOFF-NO-GL");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
        // ค่าเสื่อมที่ค้างถึงวันตัดจำหน่ายต้องลงก่อน — ไม่งั้นขาดทุนจากการตัดจำหน่าย
        // สูงเกินจริงเท่ากับค่าเสื่อมที่ไม่ได้ลง
        await PostCatchUpDepreciationAsync(companyId, asset, request.WriteOffDate, performedBy);

        // อ่าน NBV **หลัง** คิดค่าเสื่อมค้าง — ไม่งั้นขาดทุนจากการตัดจำหน่ายจะสูง
        // เกินจริงเท่ากับค่าเสื่อมที่ยังไม่ได้ลง
        var remainingNBV = asset.NetBookValue;

        asset.Status = AssetStatus.WrittenOff;
        asset.DisposalDate = request.WriteOffDate;
        asset.DisposalAmount = 0;

        // Journal entry: ตัดจำหน่ายสินทรัพย์ (NBV เหลือ 0, ไม่ได้รับเงิน)
        {
            var entryNumber = $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";

            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.WriteOffDate,
                JournalType = JournalType.General,
                Description = $"ตัดจำหน่ายสินทรัพย์: {asset.Name} ({asset.AssetCode}){(request.Reason != null ? $" - {request.Reason}" : "")}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                ProjectId = asset.ProjectId,
                CreatedBy = performedBy
            };

            // Dr: ค่าเสื่อมราคาสะสม (ล้างยอดสะสม)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AccumulatedDepreciationAccountId.Value,
                DebitAmount = asset.AccumulatedDepreciation,
                CreditAmount = 0,
                Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}"
            });

            // Dr: ขาดทุนจากการตัดจำหน่าย (ส่วนที่ยังเหลือ NBV)
            if (remainingNBV > 0)
            {
                var lossAccount = await FindAccountAsync(companyId, "57110")
                    ?? await FindAccountAsync(companyId, "571")
                    ?? await FindAccountAsync(companyId, "57")
                    ?? throw new InvalidOperationException("ไม่พบบัญชีขาดทุนจากการตัดจำหน่าย (571xx) — กรุณาสร้างบัญชีก่อน");
                {
                    journalEntry.Lines.Add(new JournalEntryLine
                    {
                        AccountId = lossAccount.Id,
                        DebitAmount = remainingNBV,
                        CreditAmount = 0,
                        Description = $"ขาดทุนจากการตัดจำหน่ายสินทรัพย์ - {asset.Name}"
                    });
                }
            }

            // Cr: สินทรัพย์ (ตัดออกราคาทุน)
            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = 0,
                CreditAmount = asset.PurchaseCost,
                Description = $"ตัดสินทรัพย์ - {asset.Name}"
            });

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);
            _db.JournalEntries.Add(journalEntry);
        }

        // Update asset NBV
        // ⚠️ เดิมเขียน `AccumulatedDepreciation = PurchaseCost` ซึ่งเป็นค่าที่
        // **ไม่ตรงกับที่โพสต์จริง** (โพสต์ 60,000 แต่ทะเบียนบันทึก 120,000)
        // ⇒ รายงานทะเบียน/หมายเหตุประกอบงบที่อ่านฟิลด์นี้เล่าคนละเรื่องกับ GL
        // (ผลตรวจทีม E · E-05 ข้อ ง) · ตอนนี้คงยอดสะสมจริงไว้ (ซึ่งถูกต้องแล้ว
        // หลังคิดค่าเสื่อมค้างถึงวันจำหน่าย) และ NBV = 0 หมายถึง "ไม่ได้ถือครอง
        // แล้ว" ตามที่ JE ตัดออกทั้งราคาทุนและค่าเสื่อมสะสม
        asset.NetBookValue = 0;

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return MapToResponse(asset);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<FixedAssetResponse> AdjustUsefulLifeAsync(Guid companyId, Guid assetId, AdjustUsefulLifeRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException("สามารถปรับอายุการใช้งานได้เฉพาะสินทรัพย์ที่ Active เท่านั้น");

        if (request.NewUsefulLifeMonths <= 0)
            throw new ArgumentException("อายุการใช้งานต้องมากกว่า 0 เดือน");

        if (request.NewSalvageValue.HasValue)
        {
            if (request.NewSalvageValue.Value < 0)
                throw new ArgumentException("มูลค่าซากต้องไม่ติดลบ");
            asset.SalvageValue = request.NewSalvageValue.Value;
        }

        // งวดล่าสุดที่โพสต์ไปแล้ว = จุดที่การทบทวนเริ่มมีผลกับงวดถัดไป
        var lastPosted = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == asset.Id && d.IsPosted)
            .OrderByDescending(d => d.Year).ThenByDescending(d => d.Month)
            .Select(d => new { d.Year, d.Month })
            .FirstOrDefaultAsync();
        var postedIndex = lastPosted == null
            ? -1
            : Accounting.Helpers.DepreciationSchedule.MonthIndexFor(
                  asset.PurchaseDate, lastPosted.Year, lastPosted.Month);
        var elapsed = postedIndex + 1;                       // จำนวนงวดที่คิดไปแล้ว
        var remaining = request.NewUsefulLifeMonths - elapsed;
        if (remaining <= 0)
            throw new Accounting.Helpers.BusinessRuleException(
                $"อายุใหม่ {request.NewUsefulLifeMonths} เดือน สั้นกว่าที่คิดค่าเสื่อมไปแล้ว "
                + $"({elapsed} งวด) — ถ้าต้องการตัดจบทันที ให้ใช้ “ตัดจำหน่าย” แทน",
                "ASSET-LIFE-TOO-SHORT");

        // เปลี่ยนประมาณการ = **prospective** — ตรึงฐานที่เหลือ (NBV) กับอายุคงเหลือ
        // ไว้ตรง ๆ แทนการหวังให้สูตรที่หารจากราคาทุนเดาถูก (ผลตรวจทีม E · E-06)
        var before = new
        {
            asset.UsefulLifeMonths, asset.SalvageValue,
            asset.NetBookValue, asset.AccumulatedDepreciation,
        };
        asset.UsefulLifeMonths = request.NewUsefulLifeMonths;
        asset.DepreciableBaseAtReview = asset.NetBookValue - asset.SalvageValue;
        asset.RemainingLifeMonthsAtReview = remaining;
        asset.ReviewEffectiveFromMonthIndex = elapsed;
        asset.UsefulLifeReviewedAt = DateTime.UtcNow;
        asset.UsefulLifeReviewedBy = performedBy;

        // แถวแผนที่ยังไม่โพสต์เป็นตารางของอายุ**เดิม** — ต้องสร้างใหม่ในธุรกรรม
        // เดียวกัน ไม่งั้นหน้าจอโชว์แผนเก่าขณะที่ยอดโพสต์เดินตามฐานใหม่
        var stalePlan = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == asset.Id && !d.IsPosted)
            .ToListAsync();
        _db.AssetDepreciations.RemoveRange(stalePlan);
        foreach (var (y, m, amount, accumulated, nbv) in BuildScheduleRows(asset))
        {
            _db.AssetDepreciations.Add(new AssetDepreciation
            {
                CompanyId = companyId, FixedAssetId = asset.Id,
                Year = y, Month = m, Amount = amount,
                AccumulatedAmount = accumulated, NetBookValue = nbv,
                IsPosted = false, CreatedBy = performedBy,
            });
        }

        // การเปลี่ยนตัวเลขที่กระทบค่าใช้จ่ายต้องเข้า AuditLog (hash chain) —
        // ผู้สอบบัญชีถามว่า "ใครเปลี่ยนอายุจาก X เป็น Y ด้วยอำนาจอะไร"
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(performedBy, out var uid) ? uid : Guid.Empty,
            Action = Models.Enums.AuditAction.Update,
            EntityType = nameof(FixedAsset),
            EntityId = asset.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(before),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                asset.UsefulLifeMonths, asset.SalvageValue,
                asset.DepreciableBaseAtReview, asset.RemainingLifeMonthsAtReview,
                Reason = request.Reason,
            }),
        });

        await _db.SaveChangesAsync();
        return MapToResponse(asset);
    }

    public async Task<List<DepreciationResponse>> GetDepreciationsAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        var depreciations = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == assetId)
            .OrderBy(d => d.Year).ThenBy(d => d.Month)
            .ToListAsync();

        return depreciations.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
    }

    /// <summary>
    /// พารามิเตอร์ที่ใช้คิดค่าเสื่อม "ณ ตอนนี้" — แปลงฐาน/อายุ/ดัชนีงวดให้สะท้อน
    /// การทบทวนอายุการใช้งานล่าสุด (TFRS for NPAEs บทที่ 10: เปลี่ยนประมาณการ =
    /// <b>prospective</b> คิดจาก NBV คงเหลือ ÷ อายุคงเหลือ ไม่ใช่ราคาทุน ÷ อายุใหม่)
    ///
    /// <para>ทำที่นี่แทนการแก้ตัวสูตร เพื่อให้ <c>Helpers/DepreciationSchedule</c>
    /// ยังเป็นฟังก์ชันบริสุทธิ์ที่เทสต์ได้โดยไม่ต้องรู้จัก entity</para>
    ///
    /// <para><c>DepreciableBaseAtReview == null</c> = <b>ยังไม่เคยทบทวน</b> ⇒ ใช้
    /// ราคาทุนเดิมตามปกติ (ต้องแยกจาก "ทบทวนแล้วได้ค่าเท่าเดิม")</para>
    /// </summary>
    private static (decimal Cost, decimal Salvage, int Life, int Index) EffectiveDepParams(
        FixedAsset asset, int monthIndex)
    {
        if (asset.DepreciableBaseAtReview is not decimal baseAmt
            || asset.RemainingLifeMonthsAtReview is not int remaining
            || asset.ReviewEffectiveFromMonthIndex is not int from
            || remaining <= 0)
            return (asset.PurchaseCost, asset.SalvageValue, asset.UsefulLifeMonths, monthIndex);

        // ฐานใหม่ถูกใส่กลับเป็น "ราคาทุนเสมือน" = ฐาน + ซาก เพื่อให้สูตรเส้นตรง
        // `(cost − salvage) / life` ให้ผลเท่ากับ `ฐาน / อายุคงเหลือ` พอดี
        return (baseAmt + asset.SalvageValue, asset.SalvageValue, remaining, monthIndex - from);
    }

    /// <summary>
    /// คิดค่าเสื่อมงวดที่ยัง<b>ค้าง</b>จนถึงเดือนที่จำหน่าย/ตัดจำหน่าย แล้วโพสต์เป็น
    /// JE แยกลงวันเดียวกับการจำหน่าย — คืนยอดที่โพสต์ (0 = ไม่มีอะไรค้าง)
    ///
    /// ═══ ที่มา (ผลตรวจทีม E · SYSTEM_AUDIT_2026-09-07.md E-05) ═══
    /// <para><c>DisposeAsync</c>/<c>WriteOffAsync</c> คิด <c>gainLoss</c> จาก
    /// <c>NetBookValue</c> ที่ค้างอยู่จาก<b>งวดที่โพสต์ล่าสุด</b> ⇒ สินทรัพย์ที่ขาย
    /// กลางปีหลัง cron โพสต์ถึงเดือนก่อนหน้า จะขาดค่าเสื่อมของเดือนที่เหลือ
    /// (ตัวอย่างจริง: ทุน 120,000 · SL 60 เดือน · โพสต์ถึง 06/2026 · ขาย 15 ก.ย.
    /// ⇒ ขาด 3 งวด = 6,000 ⇒ กำไรจากการจำหน่ายเกินจริง 6,000 และค่าใช้จ่าย
    /// ค่าเสื่อมขาดถาวร เพราะพอ <c>Status</c> เป็น <c>Disposed</c> แล้ว
    /// <c>CalculateDepreciationAsync</c> กรอง <c>Status == Active</c> ทิ้ง)</para>
    ///
    /// <para>§65 ทวิ ให้หักค่าเสื่อมตามส่วนของเวลาที่ถือครอง — กำไรสุทธิรวมเท่ากัน
    /// แต่<b>การจำแนกใน ภ.ง.ด.50 ผิด</b> ถ้าไม่คิดส่วนนี้</para>
    /// </summary>
    private async Task<decimal> PostCatchUpDepreciationAsync(
        Guid companyId, FixedAsset asset, DateTime upTo, string performedBy)
    {
        if (asset.DepreciationMethod == DepreciationMethod.None || asset.UsefulLifeMonths <= 0)
            return 0m;
        if (!asset.DepreciationExpenseAccountId.HasValue || !asset.AccumulatedDepreciationAccountId.HasValue)
            return 0m;   // ไม่มี GL mapping — เส้นจำหน่ายจะ throw ให้เองอยู่แล้ว

        // ไล่ทีละงวดตั้งแต่งวดถัดจากที่โพสต์ล่าสุด จนถึงเดือนของวันที่จำหน่าย
        var lastPosted = await _db.AssetDepreciations
            .Where(d => d.FixedAssetId == asset.Id && d.IsPosted)
            .OrderByDescending(d => d.Year).ThenByDescending(d => d.Month)
            .Select(d => new { d.Year, d.Month })
            .FirstOrDefaultAsync();

        var startIndex = lastPosted == null
            ? 0
            : Accounting.Helpers.DepreciationSchedule.MonthIndexFor(
                  asset.PurchaseDate, lastPosted.Year, lastPosted.Month) + 1;
        var endIndex = Accounting.Helpers.DepreciationSchedule.MonthIndexFor(
            asset.PurchaseDate, upTo.Year, upTo.Month);
        if (startIndex < 0) startIndex = 0;
        if (endIndex < startIndex) return 0m;

        var total = 0m;
        var nbv = asset.NetBookValue;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var ep = EffectiveDepParams(asset, i);
            var amount = Accounting.Helpers.DepreciationSchedule.AmountForPeriod(
                asset.DepreciationMethod, ep.Cost, ep.Salvage, ep.Life, ep.Index, nbv);
            if (amount <= 0m) break;
            var periodDate = asset.PurchaseDate.AddMonths(i + 1);
            _db.AssetDepreciations.Add(new AssetDepreciation
            {
                CompanyId = companyId,
                FixedAssetId = asset.Id,
                Year = periodDate.Year,
                Month = periodDate.Month,
                Amount = amount,
                AccumulatedAmount = asset.AccumulatedDepreciation + total + amount,
                NetBookValue = nbv - amount,
                IsPosted = true,
                CreatedBy = performedBy,
            });
            total += amount;
            nbv -= amount;
        }
        if (total <= 0m) return 0m;

        // งวดปิดต้องกันเหมือนเส้นค่าเสื่อมปกติ
        var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
            p.CompanyId == companyId && p.StartDate <= upTo && p.EndDate >= upTo);
        if (period != null && period.Status != FiscalPeriodStatus.Open)
            throw new Accounting.Helpers.BusinessRuleException(
                $"ยังมีค่าเสื่อมค้าง {total:N2} บาทถึงวันที่จำหน่าย แต่งวดบัญชี "
                + $"“{period.Name}” ปิดแล้ว — เปิดงวดก่อนแล้วทำรายการจำหน่ายใหม่",
                "ASSET-DISPOSE-CLOSED-PERIOD");

        var je = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"JV-{upTo:yyyyMM}-DEPCU{Guid.NewGuid().ToString()[..4].ToUpper()}",
            // ⚠️ ลงวันเดียวกับการจำหน่าย ไม่ใช่ "วันนี้" — ค่าเสื่อมส่วนนี้เกิดขึ้น
            // ในช่วงที่ยังถือครองอยู่ (บทเรียน "วันที่ของรายการกลับ ไม่ใช่วันนี้เสมอ")
            EntryDate = upTo,
            JournalType = JournalType.General,
            Description = $"ค่าเสื่อมราคาถึงวันจำหน่าย - {asset.Name} ({asset.AssetCode})",
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true,
            FiscalPeriodId = period?.Id,
            ProjectId = asset.ProjectId,
            TotalDebit = total,
            TotalCredit = total,
            CreatedBy = performedBy,
        };
        je.Lines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id,
            AccountId = asset.DepreciationExpenseAccountId.Value,
            DebitAmount = total, CreditAmount = 0,
            Description = $"ค่าเสื่อมราคาถึงวันจำหน่าย - {asset.Name}",
        });
        je.Lines.Add(new JournalEntryLine
        {
            JournalEntryId = je.Id,
            AccountId = asset.AccumulatedDepreciationAccountId.Value,
            DebitAmount = 0, CreditAmount = total,
            Description = $"ค่าเสื่อมราคาสะสม - {asset.Name}",
        });
        _db.JournalEntries.Add(je);

        asset.AccumulatedDepreciation += total;
        asset.NetBookValue -= total;
        return total;
    }

    public async Task<List<DepreciationResponse>> CalculateDepreciationAsync(
        Guid companyId, CalculateDepreciationRequest request, string performedBy)
    {
        // กันการโพสต์ซ้ำของงวดเดียวกันข้าม instance/แท็บ — cron มี JobLock ของ
        // ตัวเองอยู่แล้ว แต่ล็อกนั้นอยู่ที่ตัว background service ไม่ได้อยู่ที่นี่
        // ⇒ endpoint มือกับ cron แข่งกันได้ แล้วบวกค่าเสื่อมสองเท่า (E-04)
        // ต้องอยู่ใน transaction — pg_advisory_xact_lock ปล่อยล็อกตอน commit
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var tx = ownsTransaction ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            Accounting.Helpers.AdvisoryLockKey.For(companyId,
                Accounting.Helpers.AdvisoryLockKey.AssetDepreciation,
                $"{request.Year}-{request.Month:D2}"));

        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
            .ToListAsync();

        var periodStart = new DateTime(request.Year, request.Month, 1);
        var processed = new List<AssetDepreciation>();   // every row touched this run (returned to caller)
        var toPost = new List<AssetDepreciation>();       // rows backed by a journal entry → linked below
        var journalLines = new List<JournalEntryLine>();

        foreach (var asset in assets)
        {
            if (asset.PurchaseDate > periodStart) continue;
            // ที่ดิน/งานระหว่างก่อสร้าง (None) หรือไม่มีอายุใช้งาน → ไม่คิดค่าเสื่อม
            if (asset.DepreciationMethod == DepreciationMethod.None || asset.UsefulLifeMonths <= 0)
                continue;

            // A planned (unposted) row may already exist from CreateAsync /
            // ImportAsync. If it's already posted, skip. If it exists but
            // is unposted, reuse it. If none exists (asset registered
            // before schedules were persisted), create one now.
            var depreciation = await _db.AssetDepreciations
                .FirstOrDefaultAsync(d => d.FixedAssetId == asset.Id
                    && d.Year == request.Year && d.Month == request.Month);
            if (depreciation != null && depreciation.IsPosted) continue;

            // **สูตรเดียวกับตารางที่ผู้ใช้เห็น** (Helpers/DepreciationSchedule) —
            // รวม switch-to-straight-line · การหยุดเมื่อครบอายุใช้งาน · cap ที่
            // มูลค่าซาก · การปัดเศษ 2 ตำแหน่งแบบ AwayFromZero และงวดสุดท้ายรับเศษ
            var monthIndex = Accounting.Helpers.DepreciationSchedule.MonthIndexFor(
                asset.PurchaseDate, request.Year, request.Month);
            var effective = EffectiveDepParams(asset, monthIndex);
            var monthlyDepreciation = Accounting.Helpers.DepreciationSchedule.AmountForPeriod(
                asset.DepreciationMethod, effective.Cost, effective.Salvage,
                effective.Life, effective.Index, asset.NetBookValue);

            if (monthlyDepreciation <= 0)
            {
                // Nothing left to depreciate — drop a stale planned row so
                // it doesn't linger forever as a never-postable item.
                if (depreciation != null && !depreciation.IsPosted)
                    _db.AssetDepreciations.Remove(depreciation);
                continue;
            }

            // สินทรัพย์ที่ยังไม่ผูกบัญชีค่าเสื่อม/ค่าเสื่อมสะสม: ห้าม mutate ตัวเลข
            // ทะเบียน — แถวจะไม่มีวันถูก stamp IsPosted (stamp เฉพาะ toPost ที่ต้อง
            // มี GL mapping) ทำให้รันซ้ำ/cron หัก NBV ซ้ำทุกรอบ และทะเบียนวิ่งหนี
            // GL โดยไม่มี JE ใด ๆ — ข้ามไว้จนกว่าผู้ใช้จะผูกบัญชีแล้วรันงวดนี้ใหม่
            if (!asset.DepreciationExpenseAccountId.HasValue || !asset.AccumulatedDepreciationAccountId.HasValue)
                continue;

            asset.AccumulatedDepreciation += monthlyDepreciation;
            asset.NetBookValue -= monthlyDepreciation;

            if (asset.NetBookValue <= asset.SalvageValue)
                asset.Status = AssetStatus.FullyDepreciated;

            if (depreciation == null)
            {
                depreciation = new AssetDepreciation
                {
                    CompanyId = companyId,
                    FixedAssetId = asset.Id,
                    Year = request.Year,
                    Month = request.Month,
                    CreatedBy = performedBy
                };
                _db.AssetDepreciations.Add(depreciation);
            }
            // Overwrite the planned projection with the actually-posted
            // figures — for declining-balance these can drift from the
            // original plan if periods are posted out of order.
            depreciation.Amount = monthlyDepreciation;
            depreciation.AccumulatedAmount = asset.AccumulatedDepreciation;
            depreciation.NetBookValue = asset.NetBookValue;
            processed.Add(depreciation);

            if (asset.DepreciationExpenseAccountId.HasValue && asset.AccumulatedDepreciationAccountId.HasValue)
            {
                toPost.Add(depreciation);
                // ProjectId on the depreciation lines so per-project P&L
                // captures monthly depreciation cost without manual reclass.
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = asset.DepreciationExpenseAccountId.Value,
                    DebitAmount = monthlyDepreciation,
                    CreditAmount = 0,
                    Description = $"ค่าเสื่อมราคา {request.Month}/{request.Year} - {asset.Name}",
                    ProjectId = asset.ProjectId,
                });
                journalLines.Add(new JournalEntryLine
                {
                    AccountId = asset.AccumulatedDepreciationAccountId.Value,
                    DebitAmount = 0,
                    CreditAmount = monthlyDepreciation,
                    Description = $"ค่าเสื่อมราคาสะสม {request.Month}/{request.Year} - {asset.Name}",
                    ProjectId = asset.ProjectId,
                });
            }
        }

        if (journalLines.Count > 0)
        {
            var entryDate = new DateTime(request.Year, request.Month, DateTime.DaysInMonth(request.Year, request.Month));

            // งวดปิด/ล็อกแล้วห้าม post (TAS 1 — งบที่ปิด/ยื่นแล้วเปลี่ยนไม่ได้)
            // เดิมไม่เช็คเลย → ค่าเสื่อมย้อนเข้างวดที่ยื่น ภ.ง.ด.50 ไปแล้วได้
            // และ FiscalPeriodId เป็น null ทำให้ query ตามงวด/ปิดปีมองไม่เห็น JE นี้
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId && p.StartDate <= entryDate && p.EndDate >= entryDate);
            if (period != null && period.Status != FiscalPeriodStatus.Open)
                throw new InvalidOperationException(
                    $"งวดบัญชี {request.Month}/{request.Year} ปิดแล้ว ({period.Status}) — "
                    + "ไม่สามารถบันทึกค่าเสื่อมราคาย้อนหลังได้ กรุณาเปิดงวดก่อนหรือบันทึกในงวดปัจจุบัน");

            var entryNumber = $"JV-{request.Year}{request.Month:D2}-DEP{Guid.NewGuid().ToString()[..4].ToUpper()}";
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = entryDate,
                JournalType = JournalType.General,
                Description = $"ค่าเสื่อมราคาประจำเดือน {request.Month}/{request.Year}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                FiscalPeriodId = period?.Id,
                TotalDebit = journalLines.Sum(l => l.DebitAmount),
                TotalCredit = journalLines.Sum(l => l.CreditAmount),
                CreatedBy = performedBy
            };

            if (Math.Abs(journalEntry.TotalDebit - journalEntry.TotalCredit) > 0.01m)
                throw new InvalidOperationException(
                    $"ค่าเสื่อมราคาไม่สมดุล: Dr={journalEntry.TotalDebit:N2} Cr={journalEntry.TotalCredit:N2}");

            foreach (var line in journalLines)
            {
                line.JournalEntryId = journalEntry.Id;
                journalEntry.Lines.Add(line);
            }

            _db.JournalEntries.Add(journalEntry);

            // Link every GL-backed row to the run's journal entry and
            // flag it posted. Rows for assets without expense/accumulated
            // accounts stay IsPosted=false — recorded, but not in the GL.
            foreach (var dep in toPost)
            {
                dep.IsPosted = true;
                dep.JournalEntryId = journalEntry.Id;
            }
        }

        await _db.SaveChangesAsync();
        if (tx != null) await tx.CommitAsync();

        return processed.Select(d => new DepreciationResponse(
            d.Id, d.FixedAssetId, d.Year, d.Month,
            d.Amount, d.AccumulatedAmount, d.NetBookValue, d.IsPosted)).ToList();
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }

    public async Task<RevaluationResponse> RevalueAsync(Guid companyId, Guid assetId, RevalueAssetRequest request, string performedBy)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        if (asset.Status != AssetStatus.Active)
            throw new InvalidOperationException("สามารถตีราคาใหม่ได้เฉพาะสินทรัพย์ที่ Active เท่านั้น");

        if (request.NewFairValue <= 0)
            throw new ArgumentException("มูลค่ายุติธรรมต้องมากกว่า 0");

        var oldNbv = asset.NetBookValue;
        var surplus = request.NewFairValue - oldNbv;

        asset.NetBookValue = request.NewFairValue;
        asset.PurchaseCost = asset.PurchaseCost + surplus;

        if (asset.AssetAccountId.HasValue && surplus != 0)
        {
            // งวดปิดแล้วห้าม post (เหมือน RunDepreciation) + ผูก FiscalPeriodId
            var revalPeriod = await _db.FiscalPeriods.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId
                && p.StartDate <= request.RevaluationDate && p.EndDate >= request.RevaluationDate);
            if (revalPeriod != null && revalPeriod.Status != FiscalPeriodStatus.Open)
                throw new InvalidOperationException(
                    $"งวดบัญชีของวันที่ {request.RevaluationDate:dd/MM/yyyy} ปิดแล้ว ({revalPeriod.Status}) — "
                    + "ไม่สามารถบันทึกการตีราคาใหม่ย้อนหลังได้");

            var entryNumber = $"REVAL-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}";
            var journalEntry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = entryNumber,
                EntryDate = request.RevaluationDate,
                JournalType = JournalType.General,
                Description = $"ตีราคาสินทรัพย์ใหม่: {asset.Name} ({asset.AssetCode}) - {request.Notes}",
                Status = JournalEntryStatus.Posted,
                IsAutoGenerated = true,
                FiscalPeriodId = revalPeriod?.Id,
                CreatedBy = performedBy
            };

            journalEntry.Lines.Add(new JournalEntryLine
            {
                AccountId = asset.AssetAccountId.Value,
                DebitAmount = surplus > 0 ? surplus : 0,
                CreditAmount = surplus < 0 ? Math.Abs(surplus) : 0,
                Description = $"ตีราคา{(surplus > 0 ? "เพิ่ม" : "ลด")} - {asset.Name}"
            });

            if (surplus > 0)
            {
                // Cr: ส่วนเกินทุนจากการตีราคาสินทรัพย์ (Equity 313xx per TAS 16)
                var revalSurplusAcc = await FindAccountAsync(companyId, "31300")
                    ?? await FindAccountAsync(companyId, "313")
                    ?? throw new InvalidOperationException(
                        "ไม่พบบัญชีส่วนเกินทุนจากการตีราคาสินทรัพย์ (313) ในผังบัญชี — กรุณาเพิ่มก่อนทำการตีราคา");
                journalEntry.Lines.Add(new JournalEntryLine
                {
                    AccountId = revalSurplusAcc.Id,
                    DebitAmount = 0,
                    CreditAmount = surplus,
                    Description = $"ส่วนเกินทุนจากการตีราคา - {asset.Name}"
                });
            }
            else
            {
                // Dr: ขาดทุนจากการตีราคาสินทรัพย์ (Expense 571xx per TAS 16/36)
                var revalLossAcc = await FindAccountAsync(companyId, "57120")
                    ?? await FindAccountAsync(companyId, "571")
                    ?? throw new InvalidOperationException(
                        "ไม่พบบัญชีขาดทุนจากการตีราคาสินทรัพย์ (571) ในผังบัญชี — กรุณาเพิ่มก่อนทำการตีราคา");
                journalEntry.Lines.Add(new JournalEntryLine
                {
                    AccountId = revalLossAcc.Id,
                    DebitAmount = Math.Abs(surplus),
                    CreditAmount = 0,
                    Description = $"ขาดทุนจากการตีราคา - {asset.Name}"
                });
            }

            journalEntry.TotalDebit = journalEntry.Lines.Sum(l => l.DebitAmount);
            journalEntry.TotalCredit = journalEntry.Lines.Sum(l => l.CreditAmount);

            if (Math.Abs(journalEntry.TotalDebit - journalEntry.TotalCredit) > 0.01m)
                throw new InvalidOperationException(
                    $"รายการบัญชีตีราคาสินทรัพย์ไม่สมดุล: Dr={journalEntry.TotalDebit:N2} Cr={journalEntry.TotalCredit:N2}");

            _db.JournalEntries.Add(journalEntry);
        }

        await _db.SaveChangesAsync();

        return new RevaluationResponse(
            asset.Id, asset.AssetCode, asset.Name,
            oldNbv, request.NewFairValue, surplus, request.RevaluationDate);
    }

    public async Task<List<AssetCategoryResponse>> GetCategoriesAsync(Guid companyId)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .ToListAsync();

        return assets
            .GroupBy(a => a.Category ?? "ไม่ระบุหมวดหมู่")
            .Select(g => new AssetCategoryResponse(
                g.Key,
                g.Count(),
                g.Sum(a => a.PurchaseCost),
                g.Sum(a => a.NetBookValue)))
            .OrderBy(c => c.Category)
            .ToList();
    }

    public async Task<AssetRegisterReport> GetAssetRegisterReportAsync(Guid companyId)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();

        var items = assets.Select(a => new AssetRegisterReportItem(
            a.AssetCode, a.Name, a.Category, a.Location,
            a.PurchaseDate, a.PurchaseCost, a.SalvageValue,
            a.UsefulLifeMonths, a.DepreciationMethod.ToString(),
            a.AccumulatedDepreciation, a.NetBookValue,
            a.Status.ToString(), a.DisposalDate, a.DisposalAmount)).ToList();

        return new AssetRegisterReport(
            DateTime.UtcNow,
            items,
            assets.Sum(a => a.PurchaseCost),
            assets.Sum(a => a.AccumulatedDepreciation),
            assets.Sum(a => a.NetBookValue),
            assets.Count(a => a.Status == AssetStatus.Active),
            assets.Count(a => a.Status == AssetStatus.Disposed || a.Status == AssetStatus.WrittenOff),
            assets.Count(a => a.Status == AssetStatus.FullyDepreciated));
    }

    public async Task<DepreciationScheduleReport> GetDepreciationScheduleAsync(Guid companyId, Guid assetId)
    {
        var asset = await _db.FixedAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสินทรัพย์ถาวร");

        var schedule = new List<DepreciationScheduleItem>();
        var openingNBV = asset.PurchaseCost;
        foreach (var row in BuildScheduleRows(asset))
        {
            schedule.Add(new DepreciationScheduleItem(
                row.Year, row.Month, openingNBV, row.Amount, row.Accumulated, row.Nbv));
            openingNBV = row.Nbv;
        }

        return new DepreciationScheduleReport(
            asset.Id, asset.AssetCode, asset.Name,
            asset.PurchaseCost, asset.SalvageValue,
            asset.UsefulLifeMonths, asset.DepreciationMethod.ToString(),
            schedule);
    }

    public async Task<ImportFixedAssetsResult> ImportAsync(Guid companyId, List<ImportFixedAssetRow> rows, string createdBy)
    {
        var errors = new List<string>();
        var successCount = 0;

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 1;

            if (string.IsNullOrWhiteSpace(row.AssetCode))
            {
                errors.Add($"แถวที่ {rowNum}: รหัสสินทรัพย์ว่างเปล่า");
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add($"แถวที่ {rowNum}: ชื่อสินทรัพย์ว่างเปล่า");
                continue;
            }
            if (row.PurchaseCost <= 0)
            {
                errors.Add($"แถวที่ {rowNum}: ราคาทุนต้องมากกว่า 0");
                continue;
            }

            var exists = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == row.AssetCode);
            if (exists)
            {
                errors.Add($"แถวที่ {rowNum}: รหัสสินทรัพย์ {row.AssetCode} ซ้ำ");
                continue;
            }

            var method = row.DepreciationMethod?.ToLower() switch
            {
                "decliningbalance" or "declining" or "ยอดลดลง" => DepreciationMethod.DecliningBalance,
                "doubledecliningbalance" or "doubledeclining" or "ยอดลดลงทวีคูณ" => DepreciationMethod.DoubleDecliningBalance,
                _ => DepreciationMethod.StraightLine
            };

            var asset = new FixedAsset
            {
                CompanyId = companyId,
                AssetCode = row.AssetCode,
                Name = row.Name,
                Category = row.Category,
                Location = row.Location,
                SerialNumber = row.SerialNumber,
                PurchaseDate = row.PurchaseDate,
                PurchaseCost = row.PurchaseCost,
                SalvageValue = row.SalvageValue,
                UsefulLifeMonths = row.UsefulLifeMonths > 0 ? row.UsefulLifeMonths : 60,
                DepreciationMethod = method,
                NetBookValue = row.PurchaseCost,
                CreatedBy = createdBy
            };
            _db.FixedAssets.Add(asset);

            // Persist the projected schedule so imported assets behave
            // identically to ones registered via CreateAsync.
            foreach (var sched in BuildScheduleRows(asset))
            {
                _db.AssetDepreciations.Add(new AssetDepreciation
                {
                    CompanyId = companyId,
                    FixedAssetId = asset.Id,
                    Year = sched.Year,
                    Month = sched.Month,
                    Amount = sched.Amount,
                    AccumulatedAmount = sched.Accumulated,
                    NetBookValue = sched.Nbv,
                    IsPosted = false,
                    CreatedBy = createdBy
                });
            }
            successCount++;
        }

        if (successCount > 0)
            await _db.SaveChangesAsync();

        return new ImportFixedAssetsResult(rows.Count, successCount, errors.Count, errors);
    }

    private async Task<ChartOfAccount?> FindAccountAsync(Guid companyId, string codePrefix)
    {
        return await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                a.CompanyId == companyId && a.AccountCode == codePrefix && a.IsActive)
            ?? await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(codePrefix) && a.Level >= 4 && a.IsActive)
                .OrderBy(a => a.AccountCode)
                .FirstOrDefaultAsync();
    }

    /// <summary>เติม "สร้างจากเอกสารใบไหน" ให้ทุกแถวในครั้งเดียว (ไม่ยิง query
    /// ต่อแถว) — ใช้กับเส้นที่แสดง**รายการ** เพื่อให้ผู้ใช้เห็นต้นทางทันที</summary>
    private async Task<List<FixedAssetResponse>> MapWithSourceDocAsync(
        Guid companyId, List<FixedAsset> items)
    {
        var ids = items.Where(a => a.SourceDocumentId.HasValue)
            .Select(a => a.SourceDocumentId!.Value).Distinct().ToList();
        var map = ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && ids.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber })
                .ToDictionaryAsync(x => x.Id, x => x.DocumentNumber);
        return items.Select(a => MapToResponse(a,
            a.SourceDocumentId.HasValue && map.TryGetValue(a.SourceDocumentId.Value, out var n)
                ? n : null)).ToList();
    }


    // ═══════════════ ลงทะเบียนจากเอกสาร (ตัวเดียวของทั้ง approve และปุ่มบนหน้าเอกสาร) ═══════════════

    /// <summary>ชนิดเอกสารฝั่งซื้อที่การลงผัง PPE = “ได้มาสินทรัพย์” — ตัวเดียวกับที่หน้าเอกสาร
    /// ใช้ตัดสินว่าจะโชว์แผงสินทรัพย์ไหม (เดิม UI ถือลิสต์เองและนับใบรับสินค้าด้วย ⇒ บอกว่า
    /// “ระบบจะสร้างให้ตอนอนุมัติ” แล้วไม่สร้าง)</summary>
    public static bool SupportsAutoRegister(DocumentType type)
        => type is DocumentType.Expense or DocumentType.PurchaseInvoice or DocumentType.PaymentVoucher;

    public async Task<DocumentAssetLinesResponse> GetDocumentAssetLinesAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");

        var assets = await GetByDocumentAsync(companyId, documentId);
        var byLine = assets.Where(a => a.SourceDocumentLineId.HasValue)
            .GroupBy(a => a.SourceDocumentLineId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var lines = doc.Lines.OrderBy(l => l.LineOrder).Select(l =>
        {
            var code = l.Account?.AccountCode;
            var isAsset = Accounting.Services.Implementations.Tax.FixedAssetAccountClassifier.Resolve(code) != null && l.Amount > 0;
            byLine.TryGetValue(l.Id, out var asset);
            return new DocumentAssetLineStatus(
                l.Id, l.LineOrder, l.Description, l.Amount, code, isAsset,
                asset?.Id, asset?.AssetCode, asset?.NeedsReview,
                IsAuxiliary: isAsset && Accounting.Helpers.AssetRegistrationPlanner.IsAuxiliary(l.Description));
        }).ToList();

        return new DocumentAssetLinesResponse(
            doc.Id, doc.DocumentNumber, doc.Status.ToString(),
            IsIssued: Accounting.Helpers.DocumentStatusRules.IsEffective(doc.Status),
            TypeSupportsAutoRegister: SupportsAutoRegister(doc.DocumentType),
            lines, assets);
    }

    public async Task<RegisterFromDocumentResult> RegisterFromDocumentAsync(
        Guid companyId, Guid documentId, Guid? onlyLineId, string actor)
    {
        var doc = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        // ต้นทุนสินทรัพย์มาจาก JE ของเอกสาร — ใบที่ยังไม่โพสต์/ถูกยกเลิก ไม่มี Dr 12xxx ให้คู่
        if (!Accounting.Helpers.DocumentStatusRules.IsEffective(doc.Status))
            throw new Accounting.Helpers.BusinessRuleException(
                $"เอกสาร {doc.DocumentNumber} สถานะ {doc.Status} ยังไม่มีรายการบัญชี — อนุมัติก่อนแล้วระบบจะขึ้นทะเบียนให้อัตโนมัติ");
        if (onlyLineId.HasValue && doc.Lines.All(l => l.Id != onlyLineId.Value))
            throw new KeyNotFoundException("ไม่พบบรรทัดนี้ในเอกสาร");
        // ปุ่มกดเอง = ผู้ใช้ตัดสินแล้วว่าต้องการ — ไม่ข้ามด้วยเหตุ “สแกนต้นทางลงไปแล้ว”
        return await RegisterFromDocumentAsync(companyId, doc, onlyLineId, actor, skipIfScanRegistered: false);
    }

    /// <summary>ตรรกะรายบรรทัดตัวเดียว: แผนจาก <c>AssetRegistrationPlanner</c> (บรรทัดจริง = 1 สินทรัพย์
    /// · ค่าประกอบเฉลี่ย) · ผังค่าเสื่อมจาก <c>FixedAssetAccountClassifier</c> · dedupe ด้วย
    /// <c>SourceDocumentLineId</c> · NeedsReview=true · ไม่โพสต์ JE ซื้อ (เอกสารลง Dr แล้ว)</summary>
    public async Task<RegisterFromDocumentResult> RegisterFromDocumentAsync(
        Guid companyId, Document doc, Guid? onlyLineId, string actor, bool skipIfScanRegistered)
    {
        var createdList = new List<FixedAssetResponse>();
        var notes = new List<string>();
        var skipped = 0;
        RegisterFromDocumentResult Done() => new(createdList.Count, skipped, createdList, notes);

        if (!SupportsAutoRegister(doc.DocumentType))
        {
            notes.Add($"เอกสารชนิด {doc.DocumentType} ไม่ใช่ใบได้มาสินทรัพย์ (รองรับ ใบซื้อ/ใบสำคัญจ่าย/บันทึกค่าใช้จ่าย)");
            return Done();
        }
        if (doc.Lines == null || doc.Lines.Count == 0) { notes.Add("เอกสารไม่มีรายการ"); return Done(); }

        // ⚠️ เอกสารที่สร้างจากสแกนที่ "ลงทะเบียนสินทรัพย์ไปแล้ว" ต้องไม่สร้างซ้ำ
        // เดิม dedup ใช้ `SourceDocumentLineId` ซึ่งสาย scan ไม่เคยเซ็ต (ตอนนั้น
        // ยังไม่มีเอกสาร) ⇒ key ไม่มีวันชน ⇒ ของชิ้นเดียวได้ 2 แถวในทะเบียน
        // + PPE เดบิตสองเท่า + ค่าเสื่อมถูกตัดทั้งสองแถวทุกเดือน
        var scanIds = !skipIfScanRegistered ? new List<Guid>() : await _db.Set<OcrScanResult>().AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.CreatedDocumentId == doc.Id && !r.IsDeleted)
            .Select(r => r.Id)
            .ToListAsync();
        if (scanIds.Count > 0)
        {
            var alreadyFromScan = await _db.Set<FixedAsset>().AsNoTracking()
                .AnyAsync(a => a.CompanyId == companyId && !a.IsDeleted
                               && a.SourceScanResultId != null
                               && scanIds.Contains(a.SourceScanResultId.Value));
            if (alreadyFromScan)
            {
                notes.Add("สแกนต้นทางของใบนี้ลงทะเบียนสินทรัพย์ไปแล้ว — ไม่สร้างซ้ำ");
                return Done();
            }
        }

        // ผังที่แต่ละบรรทัดลง → code
        var accIds = doc.Lines.Where(l => l.AccountId.HasValue).Select(l => l.AccountId!.Value).Distinct().ToList();
        if (accIds.Count == 0) { notes.Add("ไม่มีบรรทัดที่ระบุผังบัญชี"); return Done(); }
        var accMap = (await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => accIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync())
            .ToDictionary(a => a.Id, a => a.AccountCode);

        // PPE accounts ทั้งบริษัท (code → Id) สำหรับ resolve accum/dep accounts
        var allPpe = await _db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId
                && (a.AccountCode.StartsWith("18") || a.AccountCode.StartsWith("56")))
            .Select(a => new { a.Id, a.AccountCode }).ToListAsync();
        Guid? CodeToId(string? code) => code == null ? null
            : allPpe.FirstOrDefault(a => a.AccountCode == code)?.Id;

        // TFRS for NPAEs บทที่ 10 — ต้นทุนสินทรัพย์ = ราคาซื้อ + ค่าใช้จ่ายที่
        // ทำให้พร้อมใช้ (ค่าขนส่ง/ติดตั้ง/ฝึกอบรม/ค่าธรรมเนียม)
        //
        // ⚠️ **หนึ่งใบ = หลายสินทรัพย์ได้** — เดิมโค้ดนี้จัดกลุ่มด้วย AccountId แล้ว
        // สร้าง asset **ตัวเดียวต่อผัง** ⇒ ใบที่ซื้อแอร์ 3 เครื่องบน 12210 ได้
        // ทะเบียน 1 แถวราคารวม (จำหน่ายทีละเครื่องไม่ได้ · นับจำนวนผิด).
        // ตอนนี้แผนมาจาก `Helpers/AssetRegistrationPlanner` ตัวเดียว: บรรทัดจริง
        // = 1 สินทรัพย์/บรรทัด · ค่าใช้จ่ายประกอบเฉลี่ยตามสัดส่วนเข้าทุกตัว

        // group บรรทัดที่เป็น PPE ตามผัง (AccountId) — บรรทัดที่ผังไม่ใช่ PPE ข้าม
        var ppeLines = doc.Lines.Where(l =>
            l.AccountId.HasValue
            && accMap.TryGetValue(l.AccountId.Value, out var c)
            && Accounting.Services.Implementations.Tax.FixedAssetAccountClassifier.Resolve(c) != null
            && l.Amount > 0).ToList();
        if (ppeLines.Count == 0) { notes.Add("ไม่มีบรรทัดที่ลงผังสินทรัพย์ถาวร (PPE)"); return Done(); }

        var groups = ppeLines.GroupBy(l => l.AccountId!.Value);
        foreach (var grp in groups)
        {
            var code = accMap[grp.Key];
            var cls = Accounting.Services.Implementations.Tax.FixedAssetAccountClassifier.Resolve(code)!;
            // แผนของกลุ่มนี้: บรรทัดจริง = 1 สินทรัพย์/บรรทัด · ค่าใช้จ่ายประกอบ
            // เฉลี่ยตามสัดส่วนราคาเข้าทุกตัว (ทุกบรรทัดเป็น auxiliary = ใบค่าติดตั้ง
            // เดี่ยว ๆ ที่ผังลง PPE → ยังขึ้นทะเบียน 1 ตัว ไม่ปล่อยให้ Dr 12xxx
            // ลอยโดยไม่มีคู่ในทะเบียน)
            var planned = Accounting.Helpers.AssetRegistrationPlanner.Plan(
                grp.Select(l => new Accounting.Helpers.AssetRegistrationPlanner.Line(
                    l.Id, l.Description, l.Amount)).ToList());
            var auxDescs = grp
                .Where(l => Accounting.Helpers.AssetRegistrationPlanner.IsAuxiliary(l.Description))
                .Select(l => $"{l.Description?.Trim()} {l.Amount:N2}").ToList();

            foreach (var plan in planned)
            {
            // ขึ้นทะเบียนเฉพาะบรรทัดที่ขอ (ปุ่มบนหน้าเอกสาร) — ค่าประกอบยังเฉลี่ยจากทั้งกลุ่ม
            if (onlyLineId.HasValue && plan.SourceLineId != onlyLineId.Value) continue;
            var mainLine = grp.First(l => l.Id == plan.SourceLineId);
            var totalCost = plan.Cost;

            // dedupe — เคยลง asset จากบรรทัดนี้แล้ว (re-approve) ข้าม
            var dup = await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId
                && a.SourceDocumentLineId == mainLine.Id);
            if (dup) { skipped++; notes.Add($"บรรทัด “{mainLine.Description}” ขึ้นทะเบียนอยู่แล้ว — ข้าม"); continue; }

            // ใส่ aux description ลง Description ของ asset เพื่อ audit trail
            // (เห็นว่าต้นทุนรวมค่าขนส่ง/ติดตั้งแล้ว — และเฉลี่ยมาเท่าไร)
            var assetDesc = $"ลงทะเบียนอัตโนมัติจาก {doc.DocumentNumber}"
                + (auxDescs.Count > 0
                    ? $" (รวม: {string.Join(", ", auxDescs)}"
                      + (plan.AllocatedAuxiliary > 0m && planned.Count > 1
                          ? $" — เฉลี่ยเข้ารายการนี้ {plan.AllocatedAuxiliary:N2}" : "")
                      + ")"
                    : "");

            // ยังไม่ออกเลขจริงตอน NeedsReview — ใส่ placeholder "DRAFT-{guid}"
            // เพื่อไม่ให้กิน counter (gap-free). เลขจริง FA-yyyyMM-#### จะออก
            // ตอน user กดบันทึกในหน้า edit (FixedAssetService.UpdateAsync ตอน
            // NeedsReview=true → false). ถ้า user ลบ asset ที่ยังเป็น DRAFT
            // ก่อนยืนยัน จะไม่กระทบลำดับเลขของ asset อื่น
            var assetCode = $"DRAFT-{Guid.NewGuid():N}".Substring(0, 14);
            try
            {
                var created = await CreateAsync(companyId, new CreateFixedAssetRequest(
                    AssetCode: assetCode,
                    Name: string.IsNullOrWhiteSpace(mainLine.Description) ? cls.Category : mainLine.Description,
                    Description: assetDesc,
                    Category: cls.Category,
                    Location: null, SerialNumber: null,
                    PurchaseDate: doc.DocumentDate,
                    PurchaseCost: totalCost,
                    SalvageValue: 0m,
                    UsefulLifeMonths: cls.DefaultUsefulLifeMonths,
                    DepreciationMethod: cls.Depreciable
                        ? DepreciationMethod.StraightLine
                        : DepreciationMethod.None,
                    AssetAccountId: mainLine.AccountId,
                    DepreciationExpenseAccountId: CodeToId(cls.DepExpenseAccountCode),
                    AccumulatedDepreciationAccountId: CodeToId(cls.AccumDepAccountCode),
                    PostAcquisitionJournalEntry: false,   // เอกสารลง Dr asset แล้ว
                    CreditAccountId: null,
                    ProjectId: mainLine.ProjectId ?? doc.ProjectId,
                    SourceDocumentId: doc.Id,
                    SourceDocumentLineId: mainLine.Id,
                    NeedsReview: true), actor);
                createdList.Add(created);
            }
            catch (Exception ex)
            {
                // ไม่ให้ทั้งชุดล้มเพราะบรรทัดเดียว — บอกเหตุผลกลับให้ผู้เรียก (หน้าเอกสารแสดง)
                skipped++;
                notes.Add($"บรรทัด “{mainLine.Description}” ขึ้นทะเบียนไม่สำเร็จ: {ex.Message}");
                _logger?.LogWarning(ex, "Register fixed asset failed (doc {Doc} line {Line})",
                    doc.DocumentNumber, mainLine.Id);
            }
            }
        }
        return Done();
    }

    private static FixedAssetResponse MapToResponse(FixedAsset a, string? sourceDocumentNumber = null) =>
        new(a.Id, a.AssetCode, a.Name, a.Description, a.Category,
            a.Location, a.SerialNumber, a.PurchaseDate, a.PurchaseCost,
            a.SalvageValue, a.UsefulLifeMonths, a.DepreciationMethod,
            a.AccumulatedDepreciation, a.NetBookValue, a.Status,
            a.DisposalDate, a.DisposalAmount, a.CreatedAt,
            a.AssetAccountId, a.DepreciationExpenseAccountId,
            a.AccumulatedDepreciationAccountId,
            a.AssetType, a.LeaseTermMonths, a.LessorName, a.MonthlyLeasePayment,
            a.ProjectId, a.NeedsReview, a.SourceDocumentId, sourceDocumentNumber, a.SourceDocumentLineId);
}
