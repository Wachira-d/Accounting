using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers.V1;

/// <summary>
/// ฐานร่วมของทุก endpoint ใน `/api/v1` — พื้นที่สาธารณะที่ลูกค้าเชื่อมจาก ERP อื่น
/// (ACCOUNT_STRUCTURE.md §7)
///
/// **สัญญาที่ต่างจาก API ภายใน** และห้ามลืม:
///   • versioned — ลูกค้าเขียนโค้ดเชื่อมแล้วเปลี่ยน contract ไม่ได้ (ภายใน
///     refactor ได้ตามใจ แต่ shape ของ v1 ต้องนิ่ง)
///   • ทุก request ต้องผ่าน **3 ด่าน**: key ถูกต้อง → scope พอ → ฟีเจอร์เปิดอยู่
///   • ทุกงานที่สำเร็จต้องเกิด <see cref="UsageEvent"/> — ไม่มี event = ไม่ได้เงิน
///   • ทุก response ที่มี AI ต้องแนบ feedbackId (กฎเหล็ก #1 / §7.3)
/// </summary>
public abstract class PublicApiControllerBase : ControllerBase
{
    protected readonly AccountingDbContext Db;
    protected readonly IUsageMeteringService Metering;

    protected PublicApiControllerBase(AccountingDbContext db, IUsageMeteringService metering)
    { Db = db; Metering = metering; }

    /// <summary>บริบทของ key ที่กำลังเรียก — ได้จาก claim ที่ ApiKeyMiddleware ใส่ไว้</summary>
    protected sealed record ApiCallerContext(
        Guid CompanyId, Guid? ApiKeyId, Guid? BranchId, Guid? BillingAccountId,
        bool IsSandbox, string Scopes);

    /// <summary>
    /// ตรวจ 3 ด่านรวดเดียว. คืน error result เมื่อไม่ผ่าน (ผู้เรียกต้อง return ทันที)
    ///
    /// เจตนาของการรวมเป็นเมธอดเดียว: ถ้าแยกกันเช็ค วันหนึ่งจะมี endpoint ที่ลืม
    /// เช็คด่านใดด่านหนึ่ง — ซึ่งแปลว่าใช้ฟรีหรือใช้เกินสิทธิ์
    /// </summary>
    protected async Task<(ApiCallerContext? Ctx, IActionResult? Error)> ResolveCallerAsync(
        string requiredScope, string featureCode, CancellationToken ct = default)
    {
        // ชื่อ claim ต้องตรงกับที่ ApiKeyMiddleware ใส่ไว้เป๊ะ ("CompanyId"/"ApiKeyId")
        // — อ่าน HttpContext.Items เป็นทางหลักเพราะ middleware set ไว้เป็น Guid แล้ว
        var companyId = HttpContext.Items.TryGetValue("CompanyId", out var cidObj) && cidObj is Guid cg
            ? cg
            : Guid.TryParse(User.FindFirst("CompanyId")?.Value, out var cp) ? cp : Guid.Empty;
        if (companyId == Guid.Empty)
            return (null, Unauthorized(new ApiResponse<string>(false, null,
                "ต้องเรียกผ่าน API key ที่ผูกกับบริษัท (header X-Api-Key)")));

        Guid? apiKeyId = HttpContext.Items.TryGetValue("ApiKeyId", out var kidObj) && kidObj is Guid kg
            ? kg
            : Guid.TryParse(User.FindFirst("ApiKeyId")?.Value, out var k) ? k : null;

        var key = apiKeyId.HasValue
            ? await Db.Set<ApiKey>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == apiKeyId.Value, ct)
            : null;

        // ── ด่าน 2: scope ──
        // key ที่ไม่มี Scopes เลย (คีย์เก่าทุกใบ) ถูกกันออกจาก /api/v1 โดยอัตโนมัติ
        // — ต้องตั้งใจให้สิทธิ์เท่านั้น ไม่ใช่ได้มาโดยบังเอิญจากการอัปเกรดระบบ
        var scopes = key?.Scopes ?? "";
        if (!HasScope(scopes, requiredScope))
            return (null, StatusCode(403, new ApiResponse<string>(false, null,
                $"API key นี้ไม่มีสิทธิ์ {requiredScope} — เพิ่มสิทธิ์ได้ที่หน้าจัดการ API key")));

        // ── ด่าน 3: ฟีเจอร์ต้องเปิดอยู่ ──
        if (!await Metering.IsFeatureEnabledAsync(companyId, featureCode, ct))
            return (null, StatusCode(403, new ApiResponse<string>(false, null,
                $"ยังไม่ได้เปิดใช้ฟีเจอร์ {featureCode} สำหรับบริษัทนี้ — เปิดได้ที่หน้าเลือกฟีเจอร์ (เห็นราคาก่อนเปิด)")));

        var billingAccountId = key?.BillingAccountId
            ?? await Db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId).Select(c => c.BillingAccountId).FirstOrDefaultAsync(ct);

        return (new ApiCallerContext(companyId, key?.Id, key?.BranchId, billingAccountId,
            key?.IsSandbox ?? false, scopes), null);
    }

    /// <summary>scope ตรงตัว หรือมี wildcard ของหมวดนั้น ("ocr:*" ครอบ "ocr:write")</summary>
    protected static bool HasScope(string granted, string required)
    {
        if (string.IsNullOrWhiteSpace(granted) || string.IsNullOrWhiteSpace(required)) return false;
        var parts = granted.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var domain = required.Split(':')[0];
        return parts.Any(p =>
            p.Equals(required, StringComparison.OrdinalIgnoreCase)
            || p.Equals("*", StringComparison.Ordinal)
            || p.Equals($"{domain}:*", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// บันทึกการใช้งาน 1 หน่วย — เรียก **หลังงานสำเร็จเท่านั้น**
    /// (ล้มเหลวแล้วคิดเงินคือสิ่งที่ลูกค้าให้อภัยยากที่สุด)
    ///
    /// `Idempotency-Key` header ถูกใช้อัตโนมัติถ้าลูกค้าส่งมา — retry จึงไม่โดนเก็บซ้ำ
    /// </summary>
    protected Task<UsageRecordResult> MeterAsync(
        ApiCallerContext ctx, string featureCode, int quantity,
        string? refType = null, Guid? refId = null, CancellationToken ct = default)
    {
        var idem = Request.Headers.TryGetValue("Idempotency-Key", out var v) ? v.ToString() : null;
        return Metering.RecordAsync(new UsageRecordRequest(
            CompanyId: ctx.CompanyId,
            FeatureCode: featureCode,
            Quantity: Math.Max(1, quantity),
            IdempotencyKey: string.IsNullOrWhiteSpace(idem) ? null : idem,
            BranchId: ctx.BranchId,
            ApiClientId: ctx.ApiKeyId,
            RefEntityType: refType,
            RefEntityId: refId), ct);
    }
}
