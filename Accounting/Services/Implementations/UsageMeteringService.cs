using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// นับและคิดเงินการใช้งานรายหน่วย (ACCOUNT_STRUCTURE.md §6)
///
/// ลำดับการทำงานของ <see cref="RecordAsync"/>:
///   1. ฟีเจอร์เปิดอยู่ไหม → ปิด = ไม่บันทึก ไม่คิดเงิน
///   2. IdempotencyKey ซ้ำไหม → ซ้ำ = คืนแถวเดิม (retry ต้องไม่โดนเก็บ 2 รอบ)
///   3. resolve ราคา ณ วันนี้ (ราคาเฉพาะกลุ่มชนะราคามาตรฐาน)
///   4. หักโควตาฟรีของเดือน (นับรวมทั้งกลุ่ม ไม่ใช่ต่อบริษัท)
///   5. เขียน UsageEvent (append-only) + ตัดเครดิตถ้า Prepaid
///
/// **ทุก path ห้าม throw** — งานหลักของลูกค้า (สร้างเอกสาร/สแกน) ต้องไม่พัง
/// เพราะระบบนับเงินมีปัญหา เสียรายได้ 1 รายการยอมรับได้ ทำให้ลูกค้าใช้งาน
/// ไม่ได้ยอมรับไม่ได้
/// </summary>
public class UsageMeteringService : IUsageMeteringService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<UsageMeteringService> _logger;

    public UsageMeteringService(AccountingDbContext db, ILogger<UsageMeteringService> logger)
    { _db = db; _logger = logger; }

    public async Task<UsageRecordResult> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
    {
        try
        {
            if (request.Quantity <= 0)
                return new UsageRecordResult(false, null, 0, false, false, "จำนวนต้องมากกว่า 0");

            var company = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == request.CompanyId)
                .Select(c => new { c.Id, c.BillingAccountId })
                .FirstOrDefaultAsync(ct);
            if (company == null)
                return new UsageRecordResult(false, null, 0, false, false, "ไม่พบบริษัท");

            // ── 1. ฟีเจอร์ต้องเปิดอยู่ ──
            if (!await IsFeatureEnabledAsync(request.CompanyId, request.FeatureCode, ct))
                return new UsageRecordResult(false, null, 0, false, false,
                    $"ฟีเจอร์ {request.FeatureCode} ยังไม่ได้เปิดใช้งานสำหรับบริษัทนี้");

            // ── 2. กันซ้ำ ──
            // เช็คก่อนเพื่อคืนคำตอบที่เป็นมิตร; ยังมี unique index กันอีกชั้น
            // สำหรับกรณี 2 request ชนกันพอดี (จับ DbUpdateException ด้านล่าง)
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var dup = await _db.UsageEvents.AsNoTracking()
                    .Where(u => u.CompanyId == request.CompanyId
                             && u.IdempotencyKey == request.IdempotencyKey
                             && !u.IsDeleted)
                    .Select(u => new { u.Id, u.ChargedAmount, u.CoveredByFreeQuota })
                    .FirstOrDefaultAsync(ct);
                if (dup != null)
                    return new UsageRecordResult(false, dup.Id, dup.ChargedAmount,
                        dup.CoveredByFreeQuota, true, "รายการนี้ถูกบันทึกไปแล้ว (idempotency)");
            }

            var account = company.BillingAccountId.HasValue
                ? await _db.BillingAccounts.AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == company.BillingAccountId.Value, ct)
                : null;

            // ── 3. ราคา ณ วันนี้ ──
            var nowUtc = DateTime.UtcNow;
            var plan = await ResolvePlanAsync(request.FeatureCode, company.BillingAccountId, nowUtc, ct);

            // ไม่มีแผนราคา = ยังไม่ได้ตั้งราคาฟีเจอร์นี้ → บันทึกการใช้งานไว้
            // (ราคา 0) ไม่ปฏิเสธงาน — admin ตั้งราคาย้อนหลังแล้วเห็นปริมาณจริงได้
            var unitPrice = plan?.UnitPrice ?? 0m;

            // ── 4. โควตาฟรีของเดือน (นับรวมทั้งกลุ่ม) ──
            var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var usedThisMonth = 0;
            if (plan is { FreeQuotaPerMonth: > 0 })
            {
                var scope = _db.UsageEvents.AsNoTracking()
                    .Where(u => u.FeatureCode == request.FeatureCode
                             && u.OccurredAt >= monthStart && !u.IsDeleted && !u.IsSandbox);
                scope = company.BillingAccountId.HasValue
                    ? scope.Where(u => u.BillingAccountId == company.BillingAccountId)
                    : scope.Where(u => u.CompanyId == request.CompanyId);
                usedThisMonth = await scope.SumAsync(u => (int?)u.Quantity, ct) ?? 0;
            }

            var isSandbox = account?.IsSandbox == true;
            var freeRemaining = Math.Max(0, (plan?.FreeQuotaPerMonth ?? 0) - usedThisMonth);
            var billableQty = Math.Max(0, request.Quantity - freeRemaining);
            var coveredFree = billableQty == 0 && (plan?.FreeQuotaPerMonth ?? 0) > 0;

            var charged = isSandbox
                ? 0m   // sandbox ไม่เข้าบิล — แต่ยังบันทึกเพื่อดูพฤติกรรมช่วงทดสอบ
                : Math.Round(ComputeCharge(plan, unitPrice, billableQty), 2, MidpointRounding.AwayFromZero);

            // ── 5. เขียนแถว + ตัดเครดิต ──
            var ev = new UsageEvent
            {
                CompanyId = request.CompanyId,
                BillingAccountId = company.BillingAccountId,
                BranchId = request.BranchId,
                ApiClientId = request.ApiClientId,
                FeatureCode = request.FeatureCode,
                Quantity = request.Quantity,
                UnitPriceSnapshot = unitPrice,
                ChargedAmount = charged,
                CoveredByFreeQuota = coveredFree,
                IsSandbox = isSandbox,
                IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? null : request.IdempotencyKey.Trim(),
                RefEntityType = request.RefEntityType,
                RefEntityId = request.RefEntityId,
                OccurredAt = nowUtc,
            };
            _db.UsageEvents.Add(ev);

            // Prepaid: ตัดเครดิตในธุรกรรมเดียวกับการบันทึก usage — ถ้าตัดไม่ได้
            // ต้องไม่มีแถว usage ค้าง (ยอดเครดิตกับประวัติต้องตรงกันเสมอ)
            if (charged > 0 && account is { PaymentModel: PaymentModel.Prepaid })
            {
                var tracked = await _db.BillingAccounts.FirstOrDefaultAsync(a => a.Id == account.Id, ct);
                if (tracked != null) tracked.CreditBalance -= charged;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                // แข่งกับ request ฝาแฝดที่เขียนสำเร็จก่อน — unique index ปฏิเสธ
                // ถือเป็น duplicate ไม่ใช่ error (นี่คือเจตนาของ idempotency)
                _db.Entry(ev).State = EntityState.Detached;
                var existing = await _db.UsageEvents.AsNoTracking()
                    .Where(u => u.CompanyId == request.CompanyId
                             && u.IdempotencyKey == request.IdempotencyKey && !u.IsDeleted)
                    .Select(u => new { u.Id, u.ChargedAmount, u.CoveredByFreeQuota })
                    .FirstOrDefaultAsync(ct);
                return new UsageRecordResult(false, existing?.Id, existing?.ChargedAmount ?? 0,
                    existing?.CoveredByFreeQuota ?? false, true, "รายการซ้ำ (race) — ไม่คิดเงินซ้ำ");
            }

            return new UsageRecordResult(true, ev.Id, charged, coveredFree, false);
        }
        catch (Exception ex)
        {
            // ห้ามให้ระบบนับเงินทำให้งานของลูกค้าพัง
            _logger.LogError(ex, "บันทึก UsageEvent ไม่สำเร็จ company={Company} feature={Feature}",
                request.CompanyId, request.FeatureCode);
            return new UsageRecordResult(false, null, 0, false, false, "บันทึกการใช้งานไม่สำเร็จ");
        }
    }

    /// <summary>คิดยอดตามวิธีที่ admin ตั้งไว้</summary>
    private static decimal ComputeCharge(ApiPricingPlan? plan, decimal unitPrice, int billableQty)
    {
        if (plan == null || billableQty <= 0) return 0m;
        return plan.Method switch
        {
            // เหมารายเดือน — ค่าบริการมาจากรอบบิล ไม่ผูกกับจำนวนครั้ง
            PricingMethod.FlatMonthly => 0m,
            PricingMethod.Tiered => ComputeTiered(plan, unitPrice, billableQty),
            _ => unitPrice * billableQty,   // PerUnit / PerCall
        };
    }

    /// <summary>ขั้นบันได: หาชั้นที่ครอบจำนวนนี้แล้วคูณทั้งก้อน
    /// (ไม่ใช่ progressive — ธุรกิจไทยเข้าใจแบบ "ถึงชั้นไหนได้ราคาชั้นนั้น" ง่ายกว่า
    /// และ resolver ต้องอธิบายบนใบแจ้งหนี้ได้ด้วยราคาต่อหน่วยเดียว)</summary>
    private static decimal ComputeTiered(ApiPricingPlan plan, decimal fallbackPrice, int qty)
    {
        if (string.IsNullOrWhiteSpace(plan.TierJson)) return fallbackPrice * qty;
        try
        {
            using var doc = JsonDocument.Parse(plan.TierJson);
            decimal best = fallbackPrice;
            var bestFrom = -1;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                if (!t.TryGetProperty("fromQty", out var f) || !t.TryGetProperty("unitPrice", out var p))
                    continue;
                var from = f.GetInt32();
                if (qty >= from && from > bestFrom) { bestFrom = from; best = p.GetDecimal(); }
            }
            return best * qty;
        }
        catch { return fallbackPrice * qty; }
    }

    /// <summary>ราคาเฉพาะกลุ่ม (ดีลพิเศษ) ชนะราคามาตรฐานเสมอ; ในกลุ่มเดียวกัน
    /// เอาแผนที่เริ่มมีผลล่าสุด</summary>
    private async Task<ApiPricingPlan?> ResolvePlanAsync(
        string featureCode, Guid? billingAccountId, DateTime nowUtc, CancellationToken ct)
    {
        var candidates = await _db.ApiPricingPlans.AsNoTracking()
            .Where(p => p.FeatureCode == featureCode && !p.IsDeleted
                     && p.EffectiveFrom <= nowUtc
                     && (p.EffectiveTo == null || p.EffectiveTo > nowUtc)
                     && (p.BillingAccountId == null || p.BillingAccountId == billingAccountId))
            .ToListAsync(ct);

        return candidates
            .OrderByDescending(p => p.BillingAccountId.HasValue)   // ดีลเฉพาะกลุ่มมาก่อน
            .ThenByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();
    }

    public async Task<bool> IsFeatureEnabledAsync(Guid companyId, string featureCode, CancellationToken ct = default)
        => await _db.CompanyFeatures.AsNoTracking()
            .AnyAsync(f => f.CompanyId == companyId && f.FeatureCode == featureCode
                        && f.IsEnabled && !f.IsDeleted, ct);

    public async Task SetFeatureEnabledAsync(Guid companyId, string featureCode, bool enabled,
        string actor, CancellationToken ct = default)
    {
        var row = await _db.CompanyFeatures
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.FeatureCode == featureCode && !f.IsDeleted, ct);

        if (row == null)
        {
            row = new CompanyFeature { CompanyId = companyId, FeatureCode = featureCode, CreatedBy = actor };
            _db.CompanyFeatures.Add(row);
        }

        if (row.IsEnabled == enabled) { await _db.SaveChangesAsync(ct); return; }

        row.IsEnabled = enabled;
        if (enabled)
        {
            row.EnabledAt = DateTime.UtcNow;
            row.EnabledBy = actor;
            // เก็บราคาที่ลูกค้าเห็นตอนกดเปิดไว้เป็นหลักฐานว่าแจ้งราคาแล้ว
            var account = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync(ct);
            var plan = await ResolvePlanAsync(featureCode, account, DateTime.UtcNow, ct);
            row.AcceptedUnitPrice = plan?.UnitPrice;
        }
        else
        {
            row.DisabledAt = DateTime.UtcNow;
            row.DisabledBy = actor;
        }
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
    }

    public Task<List<UsageSummaryRow>> GetMonthlyUsageAsync(Guid companyId, int year, int month,
        CancellationToken ct = default)
        => SummarizeAsync(u => u.CompanyId == companyId, year, month, ct);

    public Task<List<UsageSummaryRow>> GetAccountMonthlyUsageAsync(Guid billingAccountId, int year, int month,
        CancellationToken ct = default)
        => SummarizeAsync(u => u.BillingAccountId == billingAccountId, year, month, ct);

    private async Task<List<UsageSummaryRow>> SummarizeAsync(
        System.Linq.Expressions.Expression<Func<UsageEvent, bool>> scope,
        int year, int month, CancellationToken ct)
    {
        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);

        var rows = await _db.UsageEvents.AsNoTracking()
            .Where(scope)
            .Where(u => u.OccurredAt >= from && u.OccurredAt < to && !u.IsDeleted)
            .GroupBy(u => new { u.CompanyId, u.FeatureCode })
            .Select(g => new
            {
                g.Key.CompanyId,
                g.Key.FeatureCode,
                Qty = g.Sum(x => x.Quantity),
                FreeQty = g.Sum(x => x.CoveredByFreeQuota ? x.Quantity : 0),
                Charged = g.Sum(x => x.ChargedAmount),
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return new List<UsageSummaryRow>();

        // hydrate ชื่อบริษัท/ฟีเจอร์แยก — เลี่ยง INNER JOIN ที่ตัดแถวทิ้งเมื่อ
        // บริษัทถูกลบหรือฟีเจอร์ถูกถอดออกจากแคตตาล็อก (ประวัติบิลต้องไม่หาย)
        var companyIds = rows.Select(r => r.CompanyId).Distinct().ToList();
        var names = await _db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var codes = rows.Select(r => r.FeatureCode).Distinct().ToList();
        var features = await _db.ApiFeatures.AsNoTracking()
            .Where(f => codes.Contains(f.FeatureCode))
            .ToDictionaryAsync(f => f.FeatureCode, f => new { f.Name, f.UnitLabel }, ct);

        return rows.Select(r =>
        {
            features.TryGetValue(r.FeatureCode, out var f);
            return new UsageSummaryRow(
                r.CompanyId,
                names.GetValueOrDefault(r.CompanyId, "(บริษัทถูกลบ)"),
                r.FeatureCode,
                f?.Name ?? r.FeatureCode,
                f?.UnitLabel ?? "รายการ",
                r.Qty, r.FreeQty, r.Charged);
        })
        .OrderByDescending(r => r.TotalCharged)
        .ToList();
    }
}
