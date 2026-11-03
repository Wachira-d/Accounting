using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>รอบ 231 (คำตัดสินข้อ 231-11 · LICENSE_USAGE_OVERAGE_AUDIT §A.1 "override รายบริษัท 2 กลไกซ้อน") — <b>ผู้เขียนเดียว</b>ของ <c>CompanyQuotaOverride</c>
/// (เพดานรายบริษัทที่ชนะแพ็กเกจ · <c>QuotaLimitPolicy.Effective</c> อ่าน) · ทางเข้าทั้งสอง (<c>AdminQuotaController.SetOverride</c> · <c>AdminController.AdminChangeLimits</c> ที่เดิมเขียน
/// <c>Subscription.MaxUsers/MaxStorageBytes</c> ตรง ⇒ หน้า usage กับ hub ไม่ตรงกัน) เรียกที่นี่ · เหตุผลบังคับ · audit chain ทุกครั้ง (ค่าเก่า/ใหม่) · ผู้เรียก SaveChanges
/// <para>ทรัพยากรที่ <c>QuotaResources</c> ไม่รู้จัก (เอกสาร/รายการบัญชี/บริษัท) ยังอยู่บน <c>Subscription.Max*</c> — <c>AdminChangeLimits</c> เขียนเองพร้อม audit (ระบุในเอกสาร)</para></summary>
public static class QuotaOverrideWriter
{
    public const string RuleCode = "PLATFORM-QUOTA-OVERRIDE";

    public sealed record Result(string ResourceKey, int? Before, int? After, bool Removed);

    /// <summary>ตั้ง (<paramref name="limit"/> ≥ 0 · -1 = ไม่จำกัด) หรือลบ (null = กลับไปตามแพ็กเกจ) · เหตุผล ≥ 3 ตัวอักษร · คืน null เมื่อไม่มีอะไรเปลี่ยน (ไม่เขียน audit)</summary>
    public static async Task<Result?> SetAsync(AccountingDbContext db, Guid companyId, string resourceKey, int? limit, string reason, string actor,
        string? ipAddress = null, CancellationToken ct = default)
    {
        var res = QuotaResources.Find(resourceKey) ?? throw new BusinessRuleException("ทรัพยากรต้องเลือกจากรายการของระบบ", RuleCode, 400);
        reason = (reason ?? "").Trim();
        if (reason.Length < 3) throw new BusinessRuleException("ต้องระบุเหตุผล (บันทึกใน audit)", RuleCode, 400);
        if (reason.Length > 500) reason = reason[..500];
        if (limit is int l && l < -1) throw new BusinessRuleException("ค่าต้องเป็น -1 (ไม่จำกัด) หรือ 0 ขึ้นไป", RuleCode, 400);

        var row = await db.CompanyQuotaOverrides.FirstOrDefaultAsync(o => o.CompanyId == companyId && o.ResourceKey == res.Key, ct);
        var before = row?.Limit;
        if (limit == null && row == null) return null;
        if (limit is int same && row != null && row.Limit == same) return null;
        var now = DateTime.UtcNow;
        if (limit == null)
        {
            row!.IsDeleted = true; row.UpdatedAt = now; row.UpdatedBy = actor;
        }
        else
        {
            if (row == null) { row = new CompanyQuotaOverride { CompanyId = companyId, ResourceKey = res.Key, CreatedBy = actor }; db.CompanyQuotaOverrides.Add(row); }
            row.Limit = limit.Value; row.Reason = reason; row.SetBy = actor; row.SetAt = now;
            row.UpdatedAt = now; row.UpdatedBy = actor;
        }
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actor, Action = AuditAction.Update, EntityType = nameof(CompanyQuotaOverride), EntityId = res.Key,
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { limit = before }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { limit, reason, removed = limit == null, ruleCode = RuleCode }),
            IpAddress = ipAddress,
            Timestamp = now,
        });
        return new Result(res.Key, before, limit, limit == null);
    }

    /// <summary>แปลง byte → GB สำหรับ override พื้นที่ (ปัดขึ้น · ≥ UnlimitedBytes = -1) — สูตรเดียวกับ SQL ใน <c>PlatformPriceOverrideMigration</c></summary>
    public static int StorageGbFromBytes(long bytes)
    {
        if (bytes >= QuotaResources.UnlimitedBytes || bytes < 0) return QuotaLimitPolicy.Unlimited;
        var gb = (bytes + QuotaResources.BytesPerGb - 1) / QuotaResources.BytesPerGb;
        return gb > int.MaxValue ? int.MaxValue : (int)gb;
    }

    /// <summary>แปลงจำนวนผู้ใช้ของช่องเดิม (≥ 999 = ไม่จำกัดตาม <c>UsageBarRule</c>) → ค่า override</summary>
    public static int UsersFromLegacy(int maxUsers) => maxUsers >= 999 ? QuotaLimitPolicy.Unlimited : Math.Max(0, maxUsers);
}
