using Accounting.Data;
using Accounting.Models.DTOs.Ai;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// รายงานการใช้งาน AI — ตอบคำถาม "ลูกค้ารายไหนใช้ AI เท่าไร ผ่านทางไหน
/// จ่ายไปเท่าไร และคุ้มไหม"
///
/// <para><b>แหล่งข้อมูล 2 ชั้น (ตั้งใจ)</b></para>
/// <list type="number">
///   <item><b>ยอดรวม/แนวโน้ม</b> อ่านจาก <see cref="AiUsageDailyTenant"/> —
///     สรุปรายวันที่ upsert ตอนเกิด call. คิวรีเบา ใช้ได้กับช่วงเป็นปี</item>
///   <item><b>เจาะรายคีย์ API + ตัวอย่าง call</b> อ่านจาก
///     <see cref="AiSuggestionFeedback"/> ระดับ call ตรง ๆ — มีดัชนี
///     (ApiClientId, CreatedAt) และจำกัดช่วงวันที่เสมอ</item>
/// </list>
///
/// <para><b>ทำไมต้องโชว์ทั้งที่จ่ายและที่ประหยัด</b>: ตามกฎเหล็ก #1 ตัวชี้วัด
/// ความสำเร็จคือ "เรียก AI น้อยลงเรื่อย ๆ" ไม่ใช่ "ใช้เยอะ" — รายงานที่โชว์
/// แต่ยอดเงินจะทำให้อ่านผิดว่าใช้น้อย = ระบบไม่ถูกใช้ ทั้งที่จริงคือ local
/// เก่งขึ้นจนไม่ต้องถามครูแล้ว</para>
///
/// <para><b>ขอบเขตข้อมูล</b>: เมธอดทั้งหมดรับ <c>companyIds</c> ที่ผู้เรียก
/// มีสิทธิ์เห็นแล้วเท่านั้น (controller เป็นคนกรอง) — ฝั่งลูกค้าเรียกได้เฉพาะ
/// บริษัทตัวเอง ฝั่ง SystemAdmin เห็นทั้งแพลตฟอร์ม</para>
/// </summary>
public class AiUsageReportService
{
    private readonly AccountingDbContext _db;

    public AiUsageReportService(AccountingDbContext db) => _db = db;

    // ══════════════════════════════════════════════════════════════════
    //  สรุประดับแพลตฟอร์ม (ตารางลูกค้าทุกราย)
    // ══════════════════════════════════════════════════════════════════

    public async Task<AiUsageSummaryResponse> GetSummaryAsync(
        DateTime fromDate, DateTime toDate,
        AiUsageChannel? channel = null,
        Guid? billingAccountId = null,
        IReadOnlyCollection<Guid>? restrictToCompanyIds = null,
        bool includeSandbox = false,
        decimal usdToThb = 36.5m,
        CancellationToken ct = default)
    {
        var (from, to) = NormalizeRange(fromDate, toDate);

        var q = _db.AiUsageDailyTenants.AsNoTracking()
            .Where(u => u.UsageDate >= from && u.UsageDate <= to);
        if (channel.HasValue) q = q.Where(u => u.Channel == channel.Value);
        if (billingAccountId.HasValue) q = q.Where(u => u.BillingAccountId == billingAccountId.Value);
        if (restrictToCompanyIds != null) q = q.Where(u => restrictToCompanyIds.Contains(u.CompanyId));
        // คีย์ทดสอบไม่เข้าบิล — ค่าเริ่มต้นจึงตัดออกเพื่อให้ตัวเลขตรงกับบิลจริง
        if (!includeSandbox) q = q.Where(u => !u.IsSandbox);

        var rows = await q.ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new AiUsageSummaryResponse(from, to, usdToThb, includeSandbox,
                AiUsageTotals.Empty,
                Array.Empty<AiUsageCustomerRow>(), Array.Empty<AiUsageChannelRow>(),
                Array.Empty<AiUsageFeatureRow>(), Array.Empty<AiUsageProviderRow>(),
                Array.Empty<AiUsageDailyPoint>(),
                new[] { "ไม่มีการใช้งาน AI ในช่วงที่เลือก" });
        }

        var companyIds = rows.Select(r => r.CompanyId).Distinct().ToList();
        var companies = await _db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.TaxId })
            .ToDictionaryAsync(c => c.Id, ct);

        var baIds = rows.Where(r => r.BillingAccountId.HasValue)
            .Select(r => r.BillingAccountId!.Value).Distinct().ToList();
        var billingAccounts = baIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Set<BillingAccount>().AsNoTracking()
                .Where(b => baIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        // จำนวนคีย์ API ที่ "ใช้งานจริง" ในช่วงนี้ ต่อบริษัท — นับจากแถวระดับ
        // call เพราะ rollup ไม่ได้เก็บรายคีย์ (cardinality จะระเบิด)
        var apiClientCounts = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CreatedAt >= from && f.CreatedAt < to.AddDays(1)
                        && f.ApiClientId != null
                        && companyIds.Contains(f.CompanyId))
            .GroupBy(f => f.CompanyId)
            .Select(g => new { CompanyId = g.Key, Count = g.Select(x => x.ApiClientId).Distinct().Count() })
            .ToDictionaryAsync(x => x.CompanyId, x => x.Count, ct);

        var customers = rows
            .GroupBy(r => r.CompanyId)
            .Select(g =>
            {
                var usesWeb = g.Any(x => x.Channel == AiUsageChannel.Web && x.CallsTotal > 0);
                var usesApi = g.Any(x => x.Channel == AiUsageChannel.ApiKey && x.CallsTotal > 0);
                var top = g.GroupBy(x => x.FeatureKey)
                    .Select(fg => new { Feature = fg.Key, Calls = fg.Sum(x => x.CallsTotal) })
                    .OrderByDescending(x => x.Calls).FirstOrDefault();
                var ba = g.Select(x => x.BillingAccountId).FirstOrDefault(x => x.HasValue);
                var co = companies.GetValueOrDefault(g.Key);
                return new AiUsageCustomerRow(
                    g.Key,
                    co?.Name ?? "(บริษัทถูกลบ)",
                    co?.TaxId,
                    ba,
                    ba.HasValue ? billingAccounts.GetValueOrDefault(ba.Value) : null,
                    usesWeb, usesApi,
                    DescribeCustomerKind(usesWeb, usesApi),
                    apiClientCounts.GetValueOrDefault(g.Key),
                    top?.Feature is { } fk ? FeatureLabelTh(fk) : null,
                    top?.Calls ?? 0,
                    g.Max(x => x.UsageDate),
                    BuildTotals(g, usdToThb));
            })
            .OrderByDescending(c => c.Totals.CostUsd)
            .ThenByDescending(c => c.Totals.CallsTotal)
            .ToList();

        var totals = BuildTotals(rows, usdToThb);

        return new AiUsageSummaryResponse(
            from, to, usdToThb, includeSandbox, totals, customers,
            BuildChannelRows(rows, usdToThb),
            BuildFeatureRows(rows, usdToThb),
            BuildProviderRows(rows, usdToThb),
            BuildDailyPoints(rows, usdToThb),
            BuildSummaryHighlights(totals, customers));
    }

    // ══════════════════════════════════════════════════════════════════
    //  เจาะลึกรายลูกค้า
    // ══════════════════════════════════════════════════════════════════

    public async Task<AiUsageCustomerDetailResponse> GetCustomerDetailAsync(
        Guid companyId, DateTime fromDate, DateTime toDate,
        bool includeSandbox = false, decimal usdToThb = 36.5m,
        int recentCallLimit = 50, CancellationToken ct = default)
    {
        var (from, to) = NormalizeRange(fromDate, toDate);

        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Id, c.Name, c.TaxId, c.BillingAccountId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบบริษัท");

        var q = _db.AiUsageDailyTenants.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.UsageDate >= from && u.UsageDate <= to);
        if (!includeSandbox) q = q.Where(u => !u.IsSandbox);
        var rows = await q.ToListAsync(ct);

        var usesWeb = rows.Any(x => x.Channel == AiUsageChannel.Web && x.CallsTotal > 0);
        var usesApi = rows.Any(x => x.Channel == AiUsageChannel.ApiKey && x.CallsTotal > 0);

        string? baName = null;
        if (company.BillingAccountId is Guid baId)
        {
            baName = await _db.Set<BillingAccount>().AsNoTracking()
                .Where(b => b.Id == baId).Select(b => b.Name).FirstOrDefaultAsync(ct);
        }

        var totals = BuildTotals(rows, usdToThb);
        var apiClients = await BuildApiClientRowsAsync(companyId, from, to, includeSandbox, usdToThb, ct);
        var recent = await BuildRecentCallsAsync(companyId, from, to, recentCallLimit, ct);

        return new AiUsageCustomerDetailResponse(
            company.Id, company.Name, company.TaxId,
            company.BillingAccountId, baName,
            DescribeCustomerKind(usesWeb, usesApi),
            from, to, usdToThb, totals,
            BuildChannelRows(rows, usdToThb),
            BuildFeatureRows(rows, usdToThb),
            BuildProviderRows(rows, usdToThb),
            apiClients,
            BuildDailyPoints(rows, usdToThb),
            recent,
            BuildCustomerHighlights(totals, apiClients, usesWeb, usesApi));
    }

    /// <summary>แยกตามคีย์ API — มุมมองหลักของลูกค้ากลุ่ม Connected ที่มีหลาย
    /// ระบบเสียบเข้ามา (POS สาขา A, ERP สำนักงานใหญ่, sandbox ของ dev)</summary>
    private async Task<List<AiUsageApiClientRow>> BuildApiClientRowsAsync(
        Guid companyId, DateTime from, DateTime to, bool includeSandbox,
        decimal usdToThb, CancellationToken ct)
    {
        var toExclusive = to.AddDays(1);
        var fq = _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId
                        && f.ApiClientId != null
                        && f.CreatedAt >= from && f.CreatedAt < toExclusive);
        if (!includeSandbox) fq = fq.Where(f => !f.IsSandbox);

        var grouped = await fq
            .GroupBy(f => f.ApiClientId!.Value)
            .Select(g => new
            {
                ApiClientId = g.Key,
                CallsTotal = g.Count(),
                CallsAi = g.Count(x => x.Status == AiCallStatus.Success),
                CallsCached = g.Count(x => x.Status == AiCallStatus.Cached),
                CallsLocalServed = g.Count(x => x.Status == AiCallStatus.Skipped && x.LocalModelAnswer != null),
                CallsFailed = g.Count(x => x.Status == AiCallStatus.Failed || x.Status == AiCallStatus.InvalidResponse),
                CallsBudgetBlocked = g.Count(x => x.Status == AiCallStatus.BudgetExceeded),
                CallsNoProvider = g.Count(x => x.Status == AiCallStatus.NoProvider),
                InputTokens = g.Sum(x => (long)(x.InputTokens ?? 0)),
                OutputTokens = g.Sum(x => (long)(x.OutputTokens ?? 0)),
                CostUsd = g.Sum(x => x.CostUsd ?? 0m),
                LatencySum = g.Sum(x => (long)(x.LatencyMs ?? 0)),
                LatencySamples = g.Count(x => x.LatencyMs != null && x.LatencyMs > 0),
                UserReviewed = g.Count(x => x.UserChosenAt != null),
                UserAcceptedAi = g.Count(x => x.UserAcceptedAi == true),
                LastAiCallAt = g.Max(x => x.CreatedAt),
            })
            .ToListAsync(ct);

        if (grouped.Count == 0) return new List<AiUsageApiClientRow>();

        var keyIds = grouped.Select(g => g.ApiClientId).ToList();
        var keys = await _db.Set<ApiKey>().AsNoTracking()
            .Where(k => keyIds.Contains(k.Id) && k.CompanyId == companyId)
            .Select(k => new { k.Id, k.Name, k.KeyPrefix, k.IsSandbox, k.Scopes, k.LastUsedAt })
            .ToDictionaryAsync(k => k.Id, ct);

        return grouped
            .Select(g =>
            {
                var k = keys.GetValueOrDefault(g.ApiClientId);
                var totals = MakeTotals(
                    g.CallsTotal, g.CallsAi, g.CallsCached, g.CallsLocalServed,
                    g.CallsFailed, g.CallsBudgetBlocked, g.CallsNoProvider,
                    g.InputTokens, g.OutputTokens, g.CostUsd,
                    g.LatencySum, g.LatencySamples,
                    g.UserReviewed, g.UserAcceptedAi, usdToThb);
                return new AiUsageApiClientRow(
                    g.ApiClientId,
                    k?.Name ?? "(คีย์ถูกลบ)",
                    k?.KeyPrefix ?? "",
                    k?.IsSandbox ?? false,
                    k?.Scopes,
                    k?.LastUsedAt,
                    g.LastAiCallAt,
                    totals);
            })
            .OrderByDescending(r => r.Totals.CallsTotal)
            .ToList();
    }

    private async Task<List<AiUsageCallSample>> BuildRecentCallsAsync(
        Guid companyId, DateTime from, DateTime to, int limit, CancellationToken ct)
    {
        var toExclusive = to.AddDays(1);
        var rows = await _db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.CreatedAt >= from && f.CreatedAt < toExclusive)
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(f => new
            {
                f.Id, f.CreatedAt, f.FeatureKey, f.Channel, f.Status, f.ApiClientId,
                f.LatencyMs, f.InputTokens, f.OutputTokens, f.CostUsd,
                f.AiPrimaryAnswer, f.LocalModelAnswer, f.UserChosenAnswer, f.UserAcceptedAi,
                f.SourceEntityType, f.SourceEntityId,
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return new List<AiUsageCallSample>();

        var keyIds = rows.Where(r => r.ApiClientId.HasValue).Select(r => r.ApiClientId!.Value).Distinct().ToList();
        var keyNames = keyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Set<ApiKey>().AsNoTracking()
                .Where(k => keyIds.Contains(k.Id))
                .ToDictionaryAsync(k => k.Id, k => k.Name, ct);

        return rows.Select(r => new AiUsageCallSample(
            r.Id, r.CreatedAt, FeatureLabelTh(r.FeatureKey), r.Channel, ChannelLabelTh(r.Channel),
            StatusLabelTh(r.Status),
            r.ApiClientId.HasValue ? keyNames.GetValueOrDefault(r.ApiClientId.Value) : null,
            r.LatencyMs, r.InputTokens, r.OutputTokens, r.CostUsd,
            r.AiPrimaryAnswer, r.LocalModelAnswer, r.UserChosenAnswer, r.UserAcceptedAi,
            r.SourceEntityType, r.SourceEntityId)).ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    //  ตัวสร้างตัวเลข — จุดเดียวที่คำนวณสูตร (ห้ามคำนวณซ้ำที่อื่น)
    // ══════════════════════════════════════════════════════════════════

    private static AiUsageTotals BuildTotals(IEnumerable<AiUsageDailyTenant> rows, decimal usdToThb)
    {
        var list = rows as ICollection<AiUsageDailyTenant> ?? rows.ToList();
        return MakeTotals(
            list.Sum(r => r.CallsTotal), list.Sum(r => r.CallsAi), list.Sum(r => r.CallsCached),
            list.Sum(r => r.CallsLocalServed), list.Sum(r => r.CallsFailed),
            list.Sum(r => r.CallsBudgetBlocked), list.Sum(r => r.CallsNoProvider),
            list.Sum(r => r.InputTokensTotal), list.Sum(r => r.OutputTokensTotal),
            list.Sum(r => r.CostUsdTotal),
            list.Sum(r => r.LatencySumMs), list.Sum(r => r.LatencySamples),
            list.Sum(r => r.UserReviewed), list.Sum(r => r.UserAcceptedAi), usdToThb);
    }

    private static AiUsageTotals MakeTotals(
        int callsTotal, int callsAi, int callsCached, int callsLocalServed,
        int callsFailed, int callsBudgetBlocked, int callsNoProvider,
        long inputTokens, long outputTokens, decimal costUsd,
        long latencySum, int latencySamples,
        int userReviewed, int userAcceptedAi, decimal usdToThb)
    {
        static decimal Rate(int part, int whole)
            => whole <= 0 ? 0m : Math.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);

        return new AiUsageTotals(
            callsTotal, callsAi, callsCached, callsLocalServed,
            callsFailed, callsBudgetBlocked, callsNoProvider,
            inputTokens, outputTokens,
            Math.Round(costUsd, 6, MidpointRounding.AwayFromZero),
            Math.Round(costUsd * usdToThb, 2, MidpointRounding.AwayFromZero),
            latencySamples <= 0 ? 0 : (int)(latencySum / latencySamples),
            userReviewed, userAcceptedAi,
            AiUsageRate: Rate(callsAi, callsTotal),
            SelfServedRate: Rate(callsLocalServed + callsCached, callsTotal),
            AcceptanceRate: Rate(userAcceptedAi, userReviewed));
    }

    private static List<AiUsageChannelRow> BuildChannelRows(
        IEnumerable<AiUsageDailyTenant> rows, decimal usdToThb)
        => rows.GroupBy(r => r.Channel)
            .Select(g => new AiUsageChannelRow(g.Key, ChannelLabelTh(g.Key), BuildTotals(g, usdToThb)))
            .OrderByDescending(r => r.Totals.CallsTotal)
            .ToList();

    private static List<AiUsageFeatureRow> BuildFeatureRows(
        IEnumerable<AiUsageDailyTenant> rows, decimal usdToThb)
        => rows.GroupBy(r => r.FeatureKey)
            .Select(g => new AiUsageFeatureRow(g.Key, FeatureLabelTh(g.Key), BuildTotals(g, usdToThb)))
            .OrderByDescending(r => r.Totals.CallsTotal)
            .ToList();

    private static List<AiUsageProviderRow> BuildProviderRows(
        IEnumerable<AiUsageDailyTenant> rows, decimal usdToThb)
        => rows.GroupBy(r => r.ProviderType)
            .Select(g => new AiUsageProviderRow(g.Key, g.Key.ToString(), BuildTotals(g, usdToThb)))
            .OrderByDescending(r => r.Totals.CostUsd)
            .ToList();

    private static List<AiUsageDailyPoint> BuildDailyPoints(
        IEnumerable<AiUsageDailyTenant> rows, decimal usdToThb)
        => rows.GroupBy(r => r.UsageDate.Date)
            .Select(g =>
            {
                var cost = g.Sum(x => x.CostUsdTotal);
                return new AiUsageDailyPoint(
                    g.Key,
                    g.Sum(x => x.CallsTotal), g.Sum(x => x.CallsAi),
                    g.Sum(x => x.CallsLocalServed), g.Sum(x => x.CallsCached),
                    Math.Round(cost, 6, MidpointRounding.AwayFromZero),
                    Math.Round(cost * usdToThb, 2, MidpointRounding.AwayFromZero));
            })
            .OrderBy(p => p.Date)
            .ToList();

    // ══════════════════════════════════════════════════════════════════
    //  ข้อความสรุปเชิงธุรกิจ — ให้แอดมินอ่านได้โดยไม่ต้องตีความตัวเลขเอง
    // ══════════════════════════════════════════════════════════════════

    private static List<string> BuildSummaryHighlights(
        AiUsageTotals t, IReadOnlyList<AiUsageCustomerRow> customers)
    {
        var lines = new List<string>
        {
            $"เรียกทั้งหมด {t.CallsTotal:N0} ครั้ง — ถึง AI จริง {t.CallsAi:N0} ครั้ง "
            + $"({t.AiUsageRate:P1}) · ระบบตอบเองได้ {t.CallsLocalServed + t.CallsCached:N0} ครั้ง ({t.SelfServedRate:P1})",
            $"ค่าใช้จ่าย {t.CostUsd:N4} USD (≈ {t.CostThb:N2} บาท) · โทเคน "
            + $"{t.InputTokens:N0} เข้า / {t.OutputTokens:N0} ออก · หน่วงเฉลี่ย {t.AvgLatencyMs:N0} ms",
        };

        if (t.UserReviewed > 0)
            lines.Add($"ผู้ใช้ตัดสินใจแล้ว {t.UserReviewed:N0} ครั้ง — รับคำตอบ AI ตรง ๆ "
                      + $"{t.AcceptanceRate:P1} (ยิ่งสูง = คำแนะนำตรงใจ)");

        var apiOnly = customers.Where(c => c.UsesApi && !c.UsesWeb).ToList();
        if (apiOnly.Count > 0)
            lines.Add($"ลูกค้าที่ใช้ผ่าน API อย่างเดียว {apiOnly.Count:N0} ราย — "
                      + $"{apiOnly.Sum(c => c.Totals.CallsTotal):N0} ครั้ง "
                      + $"(≈ {apiOnly.Sum(c => c.Totals.CostThb):N2} บาท)");

        if (t.CallsBudgetBlocked > 0)
            lines.Add($"⚠️ ถูกงบสกัด {t.CallsBudgetBlocked:N0} ครั้ง — ตรวจเพดานงบรายวัน/รายเดือน "
                      + "(ผู้ใช้ยังได้คำตอบจาก local แต่คุณภาพอาจต่ำกว่าปกติ)");
        if (t.CallsFailed > 0)
            lines.Add($"⚠️ ล้มเหลว {t.CallsFailed:N0} ครั้ง — ตรวจสถานะ provider/คีย์");
        if (t.CallsNoProvider > 0)
            lines.Add($"⚠️ ไม่มี provider ที่เปิดใช้งาน {t.CallsNoProvider:N0} ครั้ง");

        return lines;
    }

    private static List<string> BuildCustomerHighlights(
        AiUsageTotals t, IReadOnlyList<AiUsageApiClientRow> apiClients, bool usesWeb, bool usesApi)
    {
        var lines = new List<string>
        {
            $"เรียก {t.CallsTotal:N0} ครั้ง — ถึง AI จริง {t.CallsAi:N0} ({t.AiUsageRate:P1}) · "
            + $"ระบบตอบเอง {t.CallsLocalServed + t.CallsCached:N0} ({t.SelfServedRate:P1})",
            $"ต้นทุน {t.CostUsd:N4} USD ≈ {t.CostThb:N2} บาท",
        };
        if (usesApi && !usesWeb)
            lines.Add($"ลูกค้ากลุ่ม **API อย่างเดียว** — ไม่พบการใช้งานผ่านหน้าเว็บในช่วงนี้ "
                      + $"(คีย์ที่ใช้จริง {apiClients.Count} ใบ)");
        else if (usesApi && usesWeb)
            lines.Add($"ใช้ทั้งหน้าเว็บและ API (คีย์ที่ใช้จริง {apiClients.Count} ใบ)");
        var sandbox = apiClients.Where(k => k.IsSandbox).ToList();
        if (sandbox.Count > 0)
            lines.Add($"มีคีย์ sandbox ใช้งานอยู่ {sandbox.Count} ใบ — ยอดเหล่านี้ไม่เข้าบิล");
        return lines;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ป้ายภาษาไทย
    // ══════════════════════════════════════════════════════════════════

    /// <summary>"ลูกค้ารายนี้เป็นลูกค้าแบบไหน" ในคอลัมน์เดียว — คำถามแรกที่
    /// ทีมขาย/ซัพพอร์ตถามเสมอเวลาเปิดรายงาน</summary>
    public static string DescribeCustomerKind(bool usesWeb, bool usesApi) => (usesWeb, usesApi) switch
    {
        (true, true) => "ผสม (เว็บ + API)",
        (false, true) => "API อย่างเดียว",
        (true, false) => "เว็บอย่างเดียว",
        _ => "งานระบบเท่านั้น",
    };

    public static string ChannelLabelTh(AiUsageChannel c) => c switch
    {
        AiUsageChannel.Web => "หน้าเว็บ NextAcc",
        AiUsageChannel.ApiKey => "API (คีย์ลูกค้า)",
        AiUsageChannel.Background => "งานเบื้องหลังของระบบ",
        _ => "ไม่ระบุ",
    };

    public static string StatusLabelTh(AiCallStatus s) => s switch
    {
        AiCallStatus.Success => "สำเร็จ (เรียก AI)",
        AiCallStatus.Cached => "ใช้คำตอบจากแคช",
        AiCallStatus.Failed => "ล้มเหลว",
        AiCallStatus.Skipped => "ข้าม (local ตอบเอง)",
        AiCallStatus.BudgetExceeded => "ถูกงบสกัด",
        AiCallStatus.NoProvider => "ไม่มี provider",
        AiCallStatus.InvalidResponse => "คำตอบไม่ผ่านการตรวจ",
        _ => s.ToString(),
    };

    /// <summary>ชื่อไทยของฟีเจอร์ — ครอบตัวที่ใช้บ่อย ที่เหลือคืนชื่อ enum
    /// ตามเดิม (ฟีเจอร์ใหม่จึงไม่พังรายงาน แค่แสดงเป็นอังกฤษจนกว่าจะเติม)</summary>
    public static string FeatureLabelTh(string featureKey) => featureKey switch
    {
        "GlAccountSuggestion" => "แนะนำผังบัญชี",
        "OcrFullReview" => "ตรวจ OCR ทั้งใบ",
        "VendorCanon" => "จับคู่ชื่อผู้ขาย",
        "DocTypeClassification" => "จำแนกประเภทเอกสาร",
        "ImportColumnMatch" => "จับคู่คอลัมน์ตอนนำเข้า",
        "BulkBankStatementMatch" => "จับคู่รายการเดินบัญชี (ยกชุด)",
        "BankMatch" => "จับคู่รายการเดินบัญชี",
        "ApprovalWarningFix" => "อธิบายคำเตือนตอนอนุมัติ",
        "PaymentTypeSuggestion" => "แนะนำประเภทการจ่าย",
        "DuplicateDocument" => "ตรวจเอกสารซ้ำ",
        "AnomalyExplanation" => "อธิบายความผิดปกติ",
        "TenantAssistantChat" => "ผู้ช่วยตอบคำถามในระบบ",
        "WhtCategory" => "ประเภทเงินได้หัก ณ ที่จ่าย",
        "AdHocAnalysis" => "วิเคราะห์เฉพาะกิจ",
        _ => featureKey,
    };

    /// <summary>ตัดช่วงวันที่ให้ปลอดภัย — กันคิวรีทั้งฐานเมื่อ UI ส่งค่าว่าง
    /// และกันช่วงกลับหัว (from &gt; to) ที่จะได้ผลลัพธ์ว่างแบบงง ๆ</summary>
    private static (DateTime From, DateTime To) NormalizeRange(DateTime from, DateTime to)
    {
        var f = from.Date;
        var t = to.Date;
        if (t < f) (f, t) = (t, f);
        if (f == default) f = DateTime.UtcNow.Date.AddDays(-30);
        if (t == default) t = DateTime.UtcNow.Date;
        // เพดาน 400 วัน — พอดูปีต่อปีได้ แต่ไม่เปิดช่องให้ดึงทั้งฐานในคำขอเดียว
        if ((t - f).TotalDays > 400) f = t.AddDays(-400);
        return (f, t);
    }
}
