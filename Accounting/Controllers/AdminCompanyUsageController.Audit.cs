using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>"ตรวจสอบการใช้งาน" ของบริษัทเดียว (แท็บในโมดัลบริษัท หน้าแอดมิน /admin/customers.html) —
/// เอกสารแยก เดือน × ประเภท × สถานะ × ช่องทางสร้าง + มิเตอร์รายเดือน (OCR · AI · สมุดรายวัน · อีเมล · e-Tax · พื้นที่ · ผู้ใช้ · ส่วนเสริม)
/// เทียบเพดานแพ็กเกจปัจจุบัน.
///
/// <para>หลัก: (1) สิทธิ์ SystemAdmin เท่านั้น (attribute ของคลาสใน AdminCompanyUsageController.cs) · (2) ทุก query กรอง
/// <c>CompanyId == companyId</c> · (3) รวมยอดที่ DB (GroupBy) ไม่ดึงทั้งตาราง · (4) ช่องทางสร้างตัดสินที่
/// <see cref="DocumentCreationChannelRule"/> ตัวเดียว — ไม่มีหลักฐาน = "ไม่ทราบ" ไม่เดา · (5) ไม่ส่ง PII (ไม่มีชื่อคู่ค้า/อีเมลผู้สร้าง).</para>
/// <para>เดือน = เดือนของวันที่บันทึกเข้าระบบ (<c>CreatedAt</c>) ตามเวลาไทย (UTC+7) — มิเตอร์การใช้งาน ไม่ใช่เดือนภาษี.</para></summary>
public partial class AdminCompanyUsageController
{
    private const int MaxAuditMonths = 36;
    private static readonly TimeSpan BangkokOffset = TimeSpan.FromHours(7);

    /// <summary>แถวกลาง: เอกสารหนึ่งใบที่ย่อเหลือเฉพาะ "หลักฐาน" (ไม่มี PII) — ใช้ภายใน query เท่านั้น</summary>
    private sealed class DocProbeRow
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = "";
        public DateTime DocumentDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Y { get; set; }
        public int M { get; set; }
        public DocumentType Type { get; set; }
        public DocumentStatus Status { get; set; }
        public bool IsDeposit { get; set; }
        public string? Origin { get; set; }
        public string Marker { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private sealed class DocGroupRow
    {
        public int Y { get; set; }
        public int M { get; set; }
        public DocumentType Type { get; set; }
        public DocumentStatus Status { get; set; }
        public bool IsDeposit { get; set; }
        public string? Origin { get; set; }
        public string Marker { get; set; } = "";
        public int Count { get; set; }
        public decimal Amount { get; set; }
    }

    private readonly record struct DocGroupKey(int Y, int M, DocumentType Type, DocumentStatus Status, bool IsDeposit, string? Origin, string Marker);

    /// <summary>แปลง "yyyy-MM" → (ปี, เดือน) · ผิดรูป = null</summary>
    private static (int Y, int M)? ParseMonth(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (d.Year, d.Month) : null;
    }

    /// <summary>ต้นเดือน (เวลาไทย) → UTC</summary>
    private static DateTime BangkokMonthStartUtc(int y, int m)
        => DateTime.SpecifyKind(new DateTime(y, m, 1, 0, 0, 0) - BangkokOffset, DateTimeKind.Utc);

    private static string MonthKey(int y, int m) => $"{y:D4}-{m:D2}";

    /// <summary>ชนิดของ CreatedBy คำนวณที่ DB (CASE) — ลำดับเฉพาะ → ทั่วไป · ค่าคงที่มาจาก <see cref="DocumentCreationChannelRule"/></summary>
    private static IQueryable<DocProbeRow> ProbeOf(IQueryable<Document> q) => q.Select(d => new DocProbeRow
    {
        Id = d.Id,
        Number = d.DocumentNumber,
        DocumentDate = d.DocumentDate,
        CreatedAt = d.CreatedAt,
        Y = d.CreatedAt.AddHours(7).Year,
        M = d.CreatedAt.AddHours(7).Month,
        Type = d.DocumentType,
        Status = d.Status,
        IsDeposit = d.IsDeposit,
        Origin = d.OriginModule,
        Marker = d.CreatedBy == null || d.CreatedBy == "" ? DocumentCreationChannelRule.MarkerNone
            : d.CreatedBy.StartsWith(DocumentCreationChannelRule.ApiActorPrefix) ? DocumentCreationChannelRule.MarkerApi
            : d.CreatedBy.StartsWith(DocumentCreationChannelRule.StorefrontActorPrefix) ? DocumentCreationChannelRule.MarkerStorefront
            : d.CreatedBy.StartsWith(DocumentCreationChannelRule.OcrActorPrefix) ? DocumentCreationChannelRule.MarkerOcr
            : d.CreatedBy.StartsWith(DocumentCreationChannelRule.SettlementActorPrefix) ? DocumentCreationChannelRule.MarkerSettlement
            : d.CreatedBy.StartsWith(DocumentCreationChannelRule.ExpenseClaimActorPrefix) ? DocumentCreationChannelRule.MarkerExpenseClaim
            : d.CreatedBy == DocumentCreationChannelRule.PlatformBillingActor ? DocumentCreationChannelRule.MarkerPlatformBilling
            : d.CreatedBy == DocumentCreationChannelRule.EcommerceActor
                || d.CreatedBy.StartsWith(DocumentCreationChannelRule.IntegrationActorPrefix) ? DocumentCreationChannelRule.MarkerIntegration
            : d.CreatedBy == DocumentCreationChannelRule.SystemActor ? DocumentCreationChannelRule.MarkerSystem
            : EF.Functions.Like(d.CreatedBy, DocumentCreationChannelRule.GuidLikePattern) ? DocumentCreationChannelRule.MarkerUser
            : d.CreatedBy.Contains("@") ? DocumentCreationChannelRule.MarkerEmail
            : DocumentCreationChannelRule.MarkerOther,
        Amount = d.TotalAmount,
    });

    private static Task<List<DocGroupRow>> GroupAsync(IQueryable<Document> q) => ProbeOf(q)
        .GroupBy(p => new { p.Y, p.M, p.Type, p.Status, p.IsDeposit, p.Origin, p.Marker })
        .Select(g => new DocGroupRow
        {
            Y = g.Key.Y, M = g.Key.M, Type = g.Key.Type, Status = g.Key.Status,
            IsDeposit = g.Key.IsDeposit, Origin = g.Key.Origin, Marker = g.Key.Marker,
            Count = g.Count(),
            Amount = g.Sum(x => x.Amount),
        })
        .ToListAsync();

    // ── ตัวกรอง "มีตารางผูกกลับมาที่ใบ" (EXISTS) — ทุกตัวกรอง CompanyId ของตารางลูกด้วย ──
    private IQueryable<Document> WithPos(IQueryable<Document> q, Guid cid, bool has) => has
        ? q.Where(d => _db.PosOrders.Any(p => p.CompanyId == cid && p.DocumentId == d.Id))
        : q.Where(d => !_db.PosOrders.Any(p => p.CompanyId == cid && p.DocumentId == d.Id));
    private IQueryable<Document> WithOcr(IQueryable<Document> q, Guid cid, bool has) => has
        ? q.Where(d => _db.OcrScanResults.Any(o => o.CompanyId == cid && o.CreatedDocumentId == d.Id))
        : q.Where(d => !_db.OcrScanResults.Any(o => o.CompanyId == cid && o.CreatedDocumentId == d.Id));
    private IQueryable<Document> WithStorefront(IQueryable<Document> q, Guid cid, bool has) => has
        ? q.Where(d => _db.SiteOrders.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            || _db.SiteBookings.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            || _db.SiteFormSubmissions.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            || _db.CmsLeads.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id))
        : q.Where(d => !_db.SiteOrders.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            && !_db.SiteBookings.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            && !_db.SiteFormSubmissions.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id)
            && !_db.CmsLeads.Any(x => x.CompanyId == cid && x.ErpDocumentId == d.Id));
    private IQueryable<Document> WithApiMeter(IQueryable<Document> q, Guid cid, bool has) => has
        ? q.Where(d => _db.UsageEvents.Any(u => u.CompanyId == cid && u.RefEntityType == "Document"
            && u.RefEntityId == d.Id && u.ApiClientId != null))
        : q.Where(d => !_db.UsageEvents.Any(u => u.CompanyId == cid && u.RefEntityType == "Document"
            && u.RefEntityId == d.Id && u.ApiClientId != null));
    private IQueryable<Document> WithRecurringLast(IQueryable<Document> q, Guid cid, bool has) => has
        ? q.Where(d => _db.RecurringTransactions.Any(r => r.CompanyId == cid && r.LastDocumentId == d.Id))
        : q.Where(d => !_db.RecurringTransactions.Any(r => r.CompanyId == cid && r.LastDocumentId == d.Id));

    /// <summary>GET api/admin/company-usage/{companyId}/audit?fromMonth=yyyy-MM&amp;toMonth=yyyy-MM
    /// — ไม่ระบุ = 12 เดือนล่าสุดรวมเดือนนี้ (เวลาไทย) · สูงสุด 36 เดือน</summary>
    [HttpGet("{companyId:guid}/audit")]
    public async Task<ActionResult<ApiResponse<object>>> GetCompanyAudit(
        Guid companyId, [FromQuery] string? fromMonth, [FromQuery] string? toMonth)
    {
        var company = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Id, c.Name })
            .FirstOrDefaultAsync();
        if (company == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบบริษัท"));

        var nowTh = DateTime.UtcNow + BangkokOffset;
        var toYm = ParseMonth(toMonth) ?? (nowTh.Year, nowTh.Month);
        var fromDefault = new DateTime(toYm.Y, toYm.M, 1).AddMonths(-11);
        var fromYm = ParseMonth(fromMonth) ?? (fromDefault.Year, fromDefault.Month);
        if ((!string.IsNullOrWhiteSpace(fromMonth) && ParseMonth(fromMonth) == null)
            || (!string.IsNullOrWhiteSpace(toMonth) && ParseMonth(toMonth) == null))
            return BadRequest(new ApiResponse<object>(false, null, "รูปแบบเดือนไม่ถูกต้อง — ใช้ yyyy-MM เช่น 2026-01"));
        var monthCount = (toYm.Y - fromYm.Y) * 12 + (toYm.M - fromYm.M) + 1;
        if (monthCount < 1)
            return BadRequest(new ApiResponse<object>(false, null, "เดือนเริ่มต้องไม่หลังเดือนสิ้นสุด"));
        if (monthCount > MaxAuditMonths)
            return BadRequest(new ApiResponse<object>(false, null, $"ช่วงยาวเกินไป — เลือกได้ไม่เกิน {MaxAuditMonths} เดือน"));
        if (fromYm.Y < 2020)
            return BadRequest(new ApiResponse<object>(false, null, "เดือนเริ่มต้องไม่ก่อนปี ค.ศ. 2020"));

        var fromUtc = BangkokMonthStartUtc(fromYm.Y, fromYm.M);
        var toNext = new DateTime(toYm.Y, toYm.M, 1).AddMonths(1);
        var toUtc = BangkokMonthStartUtc(toNext.Year, toNext.Month);
        var months = Enumerable.Range(0, monthCount)
            .Select(i => new DateTime(fromYm.Y, fromYm.M, 1).AddMonths(i))
            .Select(d => MonthKey(d.Year, d.Month)).ToList();

        // ═══ เอกสาร: เดือน × ประเภท × สถานะ × ช่องทาง ═══
        // แยกกลุ่มที่มีตารางผูกกลับตามลำดับความเชื่อถือ (POS → OCR → หน้าร้าน → มิเตอร์ API → รายการประจำ) ด้วย EXISTS
        // แล้วกลุ่มที่เหลือคือ "ไม่มีตารางผูก" — แต่ละ query เป็น GroupBy ที่ DB (ไม่ดึงรายใบ)
        var docs = _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.CreatedAt >= fromUtc && d.CreatedAt < toUtc);

        var noPos = WithPos(docs, companyId, false);
        var noPosOcr = WithOcr(noPos, companyId, false);
        var noPosOcrSf = WithStorefront(noPosOcr, companyId, false);
        var noPosOcrSfApi = WithApiMeter(noPosOcrSf, companyId, false);

        var gPos = await GroupAsync(WithPos(docs, companyId, true));
        var gOcr = await GroupAsync(WithOcr(noPos, companyId, true));
        var gSf = await GroupAsync(WithStorefront(noPosOcr, companyId, true));
        var gApi = await GroupAsync(WithApiMeter(noPosOcrSf, companyId, true));
        var gRec = await GroupAsync(WithRecurringLast(noPosOcrSfApi, companyId, true));
        var gRest = await GroupAsync(WithRecurringLast(noPosOcrSfApi, companyId, false));

        var cells = new Dictionary<(string Month, DocumentType Type, string Status, DocumentCreationChannel Channel), (int Count, decimal Amount, int Quota)>();
        void Add(List<DocGroupRow> rows, DocumentOriginProbe flags)
        {
            foreach (var r in rows)
            {
                var channel = DocumentCreationChannelRule.Classify(flags with { OriginModule = r.Origin, CreatedByMarker = r.Marker });
                var bucket = DocumentCreationChannelRule.StatusBucket(r.Status);
                var key = (MonthKey(r.Y, r.M), r.Type, bucket, channel);
                cells.TryGetValue(key, out var cur);
                // ยอดเงิน = เฉพาะใบที่อนุมัติแล้ว (ร่าง/รอ/ยกเลิก/ปฏิเสธ ไม่ใช่มูลค่าที่ออกจริง)
                var amount = bucket == "Approved" ? r.Amount : 0m;
                var quota = DocumentCreationChannelRule.CountsTowardDocumentQuota(r.Type, r.IsDeposit, r.Origin) ? r.Count : 0;
                cells[key] = (cur.Count + r.Count, cur.Amount + amount, cur.Quota + quota);
            }
        }
        Add(gPos, new DocumentOriginProbe(null, null, HasPosLink: true));
        Add(gOcr, new DocumentOriginProbe(null, null, HasOcrLink: true));
        Add(gSf, new DocumentOriginProbe(null, null, HasStorefrontLink: true));
        Add(gApi, new DocumentOriginProbe(null, null, HasApiMeter: true));
        Add(gRec, new DocumentOriginProbe(null, null, IsRecurringLast: true));
        Add(gRest, new DocumentOriginProbe(null, null));

        var docCells = cells
            .OrderBy(kv => kv.Key.Month).ThenBy(kv => kv.Key.Type).ThenBy(kv => kv.Key.Status).ThenBy(kv => kv.Key.Channel)
            .Select(kv => new
            {
                month = kv.Key.Month,
                type = kv.Key.Type.ToString(),
                typeLabel = DocumentTypeNames.Title(kv.Key.Type, "th"),
                status = kv.Key.Status,
                statusLabel = DocumentCreationChannelRule.StatusBucketLabel(kv.Key.Status),
                channel = kv.Key.Channel.ToString(),
                count = kv.Value.Count,
                amount = kv.Value.Amount,
                quotaCount = kv.Value.Quota,
            })
            .ToList();

        var channels = Enum.GetValues<DocumentCreationChannel>()
            .Select(c => new { channel = c.ToString(), label = DocumentCreationChannelRule.Label(c), caveat = DocumentCreationChannelRule.Caveat(c) })
            .ToList();
        var statuses = new[] { "Draft", "Pending", "Approved", "Voided", "Rejected" }
            .Select(b => new { status = b, label = DocumentCreationChannelRule.StatusBucketLabel(b) }).ToList();

        // ═══ มิเตอร์อื่นรายเดือน (GroupBy ที่ DB ทุกตัว) ═══
        var je = await _db.JournalEntries.AsNoTracking()
            .Where(j => j.CompanyId == companyId && !j.IsDeleted && j.CreatedAt >= fromUtc && j.CreatedAt < toUtc)
            .GroupBy(j => new { Y = j.CreatedAt.AddHours(7).Year, M = j.CreatedAt.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Count = g.Count() })
            .ToListAsync();

        var ocr = await _db.OcrScanResults.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted && o.CreatedAt >= fromUtc && o.CreatedAt < toUtc)
            .GroupBy(o => new { Y = o.CreatedAt.AddHours(7).Year, M = o.CreatedAt.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Total = g.Count(), Completed = g.Count(o => o.ScanStatus == "Completed") })
            .ToListAsync();

        // AI — rollup รายวันชุดเดียวกับหน้ารายงาน AI (UsageDate = วันตามปฏิทินของ rollup · ตัด sandbox)
        var ai = await _db.AiUsageDailyTenants.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsSandbox
                && u.UsageDate >= new DateTime(fromYm.Y, fromYm.M, 1, 0, 0, 0, DateTimeKind.Utc)
                && u.UsageDate < new DateTime(toNext.Year, toNext.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .GroupBy(u => new { u.UsageDate.Year, u.UsageDate.Month })
            .Select(g => new
            {
                Y = g.Key.Year, M = g.Key.Month,
                CallsTotal = g.Sum(u => u.CallsTotal),
                CallsAi = g.Sum(u => u.CallsAi),
                CostUsd = g.Sum(u => u.CostUsdTotal),
            })
            .ToListAsync();

        var aiByFeature = await _db.AiUsageDailyTenants.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsSandbox
                && u.UsageDate >= new DateTime(fromYm.Y, fromYm.M, 1, 0, 0, 0, DateTimeKind.Utc)
                && u.UsageDate < new DateTime(toNext.Year, toNext.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .GroupBy(u => u.FeatureKey)
            .Select(g => new
            {
                featureKey = g.Key,
                callsTotal = g.Sum(u => u.CallsTotal),
                callsAi = g.Sum(u => u.CallsAi),
                costUsd = g.Sum(u => u.CostUsdTotal),
            })
            .OrderByDescending(x => x.callsTotal)
            .Take(20)
            .ToListAsync();

        var emails = await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted && q.Status == "Sent"
                && q.SentAt >= fromUtc && q.SentAt < toUtc)
            .GroupBy(q => new { Y = q.SentAt!.Value.AddHours(7).Year, M = q.SentAt!.Value.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Count = g.Count() })
            .ToListAsync();

        var etax = await _db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted && e.CreatedAt >= fromUtc && e.CreatedAt < toUtc)
            .GroupBy(e => new { Y = e.CreatedAt.AddHours(7).Year, M = e.CreatedAt.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Count = g.Count() })
            .ToListAsync();

        // พื้นที่ — ไฟล์แนบที่อัปโหลดในแต่ละเดือน + ยอดสะสม ณ สิ้นเดือน (ไฟล์ที่ลบไปแล้วไม่อยู่ในยอด · สื่อ CMS ไม่มีประวัติรายเดือน)
        var storageBefore = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.CreatedAt < fromUtc)
            .SumAsync(f => (long?)f.FileSize) ?? 0L;
        var storage = await _db.FileAttachments.AsNoTracking()
            .Where(f => f.CompanyId == companyId && f.CreatedAt >= fromUtc && f.CreatedAt < toUtc)
            .GroupBy(f => new { Y = f.CreatedAt.AddHours(7).Year, M = f.CreatedAt.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Bytes = g.Sum(f => f.FileSize), Files = g.Count() })
            .ToListAsync();

        // ผู้ใช้ — สมาชิกปัจจุบันที่เข้าร่วมภายในสิ้นเดือน (สมาชิกที่ถูกถอดออกไม่มีประวัติ ⇒ ไม่อยู่ในยอดย้อนหลัง)
        var usersBefore = await _db.CompanyUsers.AsNoTracking()
            .CountAsync(cu => cu.CompanyId == companyId && cu.JoinedAt < fromUtc);
        var usersJoined = await _db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.JoinedAt >= fromUtc && cu.JoinedAt < toUtc)
            .GroupBy(cu => new { Y = cu.JoinedAt.AddHours(7).Year, M = cu.JoinedAt.AddHours(7).Month })
            .Select(g => new { g.Key.Y, g.Key.M, Count = g.Count() })
            .ToListAsync();
        var usersNow = await _db.CompanyUsers.AsNoTracking().CountAsync(cu => cu.CompanyId == companyId);

        // มิเตอร์ที่คิดเงิน (UsageEvent) — ส่วนเสริม/overage/API รายเดือน · ตัด sandbox
        var metered = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted && !u.IsSandbox
                && u.OccurredAt >= fromUtc && u.OccurredAt < toUtc)
            .GroupBy(u => new { Y = u.OccurredAt.AddHours(7).Year, M = u.OccurredAt.AddHours(7).Month, u.FeatureCode })
            .Select(g => new
            {
                g.Key.Y, g.Key.M, g.Key.FeatureCode,
                Quantity = g.Sum(u => u.Quantity),
                FreeQuantity = g.Where(u => u.CoveredByFreeQuota).Sum(u => (int?)u.Quantity) ?? 0,
                Charged = g.Sum(u => u.ChargedAmount),
            })
            .ToListAsync();
        var featureCodes = metered.Select(m => m.FeatureCode).Distinct().ToList();
        var featureNames = await _db.ApiFeatures.AsNoTracking()
            .Where(f => featureCodes.Contains(f.FeatureCode))
            .Select(f => new { f.FeatureCode, f.Name, f.UnitLabel })
            .ToListAsync();
        var featureMap = featureNames.GroupBy(f => f.FeatureCode).ToDictionary(g => g.Key, g => g.First());

        // ═══ เพดานแพ็กเกจ (resolver ตัวเดียวกับหน้าลูกค้า · ค่า "ปัจจุบัน" ไม่ใช่ค่า ณ เดือนนั้น) ═══
        Models.DTOs.Subscription.SubscriptionResponse? effective = null;
        try { effective = await _subscriptions.GetSubscriptionAsync(companyId); }
        catch (Exception ex) { _logger.LogWarning(ex, "resolve subscription ของบริษัท {CompanyId} ไม่สำเร็จ — แท็บตรวจสอบการใช้งานแสดง 'ไม่มีข้อมูล' แทนเพดาน", companyId); }
        Services.Interfaces.OcrQuotaStatus? ocrQuota = null;
        try { ocrQuota = await _ocrQuota.GetQuotaStatusAsync(companyId); }
        catch (Exception ex) { _logger.LogWarning(ex, "อ่านโควตา OCR ของบริษัท {CompanyId} ไม่สำเร็จ", companyId); }
        var fromAccountPlan = await _db.Subscriptions.AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && !s.IsDeleted && s.AccountSubscriptionId != null);

        // ═══ รวมเป็นแถวรายเดือน ═══
        long cumulativeBytes = storageBefore;
        int cumulativeUsers = usersBefore;
        var monthly = new List<object>();
        foreach (var mk in months)
        {
            var y = int.Parse(mk[..4], CultureInfo.InvariantCulture);
            var m = int.Parse(mk[5..], CultureInfo.InvariantCulture);
            var dc = docCells.Where(c => c.month == mk).ToList();
            var o = ocr.FirstOrDefault(x => x.Y == y && x.M == m);
            var a = ai.FirstOrDefault(x => x.Y == y && x.M == m);
            var st = storage.FirstOrDefault(x => x.Y == y && x.M == m);
            cumulativeBytes += st?.Bytes ?? 0L;
            cumulativeUsers += usersJoined.FirstOrDefault(x => x.Y == y && x.M == m)?.Count ?? 0;
            monthly.Add(new
            {
                month = mk,
                documents = dc.Sum(c => c.count),
                documentsQuotaCounted = dc.Sum(c => c.quotaCount),
                draft = dc.Where(c => c.status == "Draft").Sum(c => c.count),
                pending = dc.Where(c => c.status == "Pending").Sum(c => c.count),
                approved = dc.Where(c => c.status == "Approved").Sum(c => c.count),
                voided = dc.Where(c => c.status == "Voided").Sum(c => c.count),
                rejected = dc.Where(c => c.status == "Rejected").Sum(c => c.count),
                approvedAmount = dc.Sum(c => c.amount),
                journalEntries = je.FirstOrDefault(x => x.Y == y && x.M == m)?.Count ?? 0,
                ocrScans = o?.Total ?? 0,
                ocrCompleted = o?.Completed ?? 0,
                aiCallsTotal = a?.CallsTotal ?? 0,
                aiCallsPaid = a?.CallsAi ?? 0,
                aiCostUsd = a?.CostUsd ?? 0m,
                emailsSent = emails.FirstOrDefault(x => x.Y == y && x.M == m)?.Count ?? 0,
                etaxIssued = etax.FirstOrDefault(x => x.Y == y && x.M == m)?.Count ?? 0,
                storageAddedBytes = st?.Bytes ?? 0L,
                filesAdded = st?.Files ?? 0,
                storageCumulativeBytes = cumulativeBytes,
                usersCumulative = cumulativeUsers,
                meteredCharged = metered.Where(x => x.Y == y && x.M == m).Sum(x => x.Charged),
            });
        }

        return Ok(new ApiResponse<object>(true, new
        {
            company = new { id = company.Id, name = company.Name },
            fromMonth = months.First(),
            toMonth = months.Last(),
            months,
            channels,
            statuses,
            docCells,
            monthly,
            aiByFeature,
            metered = metered
                .OrderBy(x => x.Y).ThenBy(x => x.M).ThenBy(x => x.FeatureCode)
                .Select(x => new
                {
                    month = MonthKey(x.Y, x.M),
                    featureCode = x.FeatureCode,
                    featureName = featureMap.TryGetValue(x.FeatureCode, out var f) ? f.Name : x.FeatureCode,
                    unitLabel = featureMap.TryGetValue(x.FeatureCode, out var f2) ? f2.UnitLabel : null,
                    quantity = x.Quantity,
                    freeQuantity = x.FreeQuantity,
                    charged = x.Charged,
                }).ToList(),
            // null = resolve ไม่ได้ — หน้าเว็บต้องเขียน "ไม่มีข้อมูล" ไม่ใช่วาดเป็น 0
            limits = effective == null ? null : new
            {
                maxDocumentsPerMonth = effective.Limits.MaxDocumentsPerMonth,
                maxJournalEntriesPerMonth = effective.Limits.MaxJournalEntriesPerMonth,
                maxStorageBytes = effective.Limits.MaxStorageBytes,
                maxUsers = effective.Limits.MaxUsers,
                maxOcrPagesPerMonth = ocrQuota?.MaxPagesPerMonth,
                planName = effective.PlanName ?? effective.Plan.ToString(),
                fromAccountPlan,
            },
            current = new
            {
                documentsThisMonth = effective?.Current.DocumentsThisMonth,
                journalEntriesThisMonth = effective?.Current.JournalEntriesThisMonth,
                storageUsed = effective?.Current.StorageUsed,
                ocrPagesThisMonth = ocrQuota?.UsedThisMonth,
                users = usersNow,
            },
            notes = new[]
            {
                "เดือน = เดือนที่บันทึกเข้าระบบ (เวลาไทย) — มิเตอร์การใช้งาน ไม่ใช่เดือนภาษีของเอกสาร",
                "เอกสารที่นับโควตา = ประมาณย้อนหลังด้วยกติกาโควตาเดียวกับตอนสร้าง (ไม่รวมใบที่ถูกลบ · ใบกำกับที่ออกแทนใบเสร็จอาจนับเกิน) — ตัวนับจริงของเดือนนี้ดูที่ 'ใช้จริงเดือนนี้'",
                "OCR นับเป็นจำนวนใบสแกน (1 ใบอาจมีหลายหน้า) — เพดานของแพ็กเกจนับเป็นหน้า",
                "เพดานเป็นค่าแพ็กเกจปัจจุบัน ไม่ใช่ค่า ณ เดือนนั้น" + (fromAccountPlan ? " · เพดานมาจาก License (รวมทุกบริษัทใต้ License)" : ""),
                "พื้นที่/ผู้ใช้ย้อนหลัง = ไฟล์/สมาชิกที่ยังอยู่ในปัจจุบัน (ของที่ลบ/ถอดออกแล้วไม่มีประวัติ)",
            },
        }));
    }

    /// <summary>GET api/admin/company-usage/{companyId}/audit/documents?month=yyyy-MM&amp;type=&amp;status=&amp;channel=&amp;take=50
    /// — รายการเอกสารล่าสุดตามเงื่อนไข (drill-down) · ไม่ส่งชื่อคู่ค้า/ผู้สร้าง · ช่องทางตัดสินด้วยตัวเดียวกับตาราง</summary>
    [HttpGet("{companyId:guid}/audit/documents")]
    public async Task<ActionResult<ApiResponse<object>>> GetCompanyAuditDocuments(
        Guid companyId, [FromQuery] string? month, [FromQuery] string? fromMonth, [FromQuery] string? toMonth,
        [FromQuery] string? type, [FromQuery] string? status, [FromQuery] string? channel, [FromQuery] int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        var q = _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && !d.IsDeleted);

        foreach (var raw in new[] { month, fromMonth, toMonth })
            if (!string.IsNullOrWhiteSpace(raw) && ParseMonth(raw) == null)
                return BadRequest(new ApiResponse<object>(false, null, "รูปแบบเดือนไม่ถูกต้อง — ใช้ yyyy-MM เช่น 2026-01"));
        var fm = ParseMonth(month) ?? ParseMonth(fromMonth);
        var tm = ParseMonth(month) ?? ParseMonth(toMonth);
        if (fm.HasValue) { var f = BangkokMonthStartUtc(fm.Value.Y, fm.Value.M); q = q.Where(d => d.CreatedAt >= f); }
        if (tm.HasValue)
        {
            var n = new DateTime(tm.Value.Y, tm.Value.M, 1).AddMonths(1);
            var t = BangkokMonthStartUtc(n.Year, n.Month);
            q = q.Where(d => d.CreatedAt < t);
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<DocumentType>(type, out var dt))
                return BadRequest(new ApiResponse<object>(false, null, "ประเภทเอกสารไม่ถูกต้อง"));
            q = q.Where(d => d.DocumentType == dt);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var inBucket = Enum.GetValues<DocumentStatus>()
                .Where(s => DocumentCreationChannelRule.StatusBucket(s) == status).ToList();
            if (inBucket.Count == 0)
                return BadRequest(new ApiResponse<object>(false, null, "สถานะไม่ถูกต้อง"));
            q = q.Where(d => inBucket.Contains(d.Status));
        }
        DocumentCreationChannel? wantChannel = null;
        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (!Enum.TryParse<DocumentCreationChannel>(channel, out var ch))
                return BadRequest(new ApiResponse<object>(false, null, "ช่องทางไม่ถูกต้อง"));
            wantChannel = ch;
        }

        // ช่องทางตัดสินหลังดึง (ลำดับความเชื่อถืออยู่ใน helper) ⇒ กรองช่องทาง = สแกนใบล่าสุดสูงสุด 1,000 ใบของเงื่อนไขนั้น
        const int scanCap = 1000;
        // ชนิดของ CreatedBy มาจาก ProbeOf ตัวเดียวกับตาราง pivot (ห้ามเขียน CASE ชุดที่สอง)
        var rows = await ProbeOf(q.OrderByDescending(d => d.CreatedAt).Take(wantChannel == null ? take : scanCap))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        // ตารางผูกกลับ — ถามเฉพาะ id ในหน้านี้ (≤ 1,000) · กรอง CompanyId ทุกตาราง
        var posIds = (await _db.PosOrders.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.DocumentId != null && ids.Contains(p.DocumentId.Value))
            .Select(p => p.DocumentId!.Value).ToListAsync()).ToHashSet();
        var ocrIds = (await _db.OcrScanResults.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.CreatedDocumentId != null && ids.Contains(o.CreatedDocumentId.Value))
            .Select(o => o.CreatedDocumentId!.Value).ToListAsync()).ToHashSet();
        var sfIds = (await _db.SiteOrders.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.ErpDocumentId != null && ids.Contains(x.ErpDocumentId.Value))
                .Select(x => x.ErpDocumentId!.Value).ToListAsync())
            .Concat(await _db.SiteBookings.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.ErpDocumentId != null && ids.Contains(x.ErpDocumentId.Value))
                .Select(x => x.ErpDocumentId!.Value).ToListAsync())
            .Concat(await _db.SiteFormSubmissions.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.ErpDocumentId != null && ids.Contains(x.ErpDocumentId.Value))
                .Select(x => x.ErpDocumentId!.Value).ToListAsync())
            .Concat(await _db.CmsLeads.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.ErpDocumentId != null && ids.Contains(x.ErpDocumentId.Value))
                .Select(x => x.ErpDocumentId!.Value).ToListAsync())
            .ToHashSet();
        var apiIds = (await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.RefEntityType == "Document" && u.ApiClientId != null
                && u.RefEntityId != null && ids.Contains(u.RefEntityId.Value))
            .Select(u => u.RefEntityId!.Value).ToListAsync()).ToHashSet();
        var recIds = (await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.CompanyId == companyId && r.LastDocumentId != null && ids.Contains(r.LastDocumentId.Value))
            .Select(r => r.LastDocumentId!.Value).ToListAsync()).ToHashSet();

        var items = rows
            .Select(r => new
            {
                r,
                Channel = DocumentCreationChannelRule.Classify(new DocumentOriginProbe(
                    r.Origin, r.Marker, ocrIds.Contains(r.Id), posIds.Contains(r.Id), sfIds.Contains(r.Id),
                    apiIds.Contains(r.Id), recIds.Contains(r.Id))),
            })
            .Where(x => wantChannel == null || x.Channel == wantChannel)
            .Take(take)
            .Select(x =>
            {
                var bucket = DocumentCreationChannelRule.StatusBucket(x.r.Status);
                return new
                {
                    id = x.r.Id,
                    documentNumber = x.r.Number,
                    type = x.r.Type.ToString(),
                    typeLabel = DocumentTypeNames.Title(x.r.Type, "th"),
                    status = bucket,
                    statusLabel = DocumentCreationChannelRule.StatusBucketLabel(bucket),
                    channel = x.Channel.ToString(),
                    channelLabel = DocumentCreationChannelRule.Label(x.Channel),
                    documentDate = x.r.DocumentDate,
                    createdAt = x.r.CreatedAt,
                    totalAmount = x.r.Amount,
                };
            })
            .ToList();

        return Ok(new ApiResponse<object>(true, new
        {
            items,
            // บอกตรง ๆ เมื่อกรองช่องทางแล้วสแกนไม่ครบ (มีใบเก่ากว่านี้ที่ไม่ได้ตรวจ)
            truncated = wantChannel != null && rows.Count >= scanCap,
            scanned = rows.Count,
        }));
    }
}
