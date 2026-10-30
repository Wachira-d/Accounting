using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 เฟส 2 (ฝ่ายค้าน S2/B2/S3) — <b>ตัวเขียนเดียว</b>ของ "เรื่องที่ผู้ดูแลระบบต้องจัดการบนรายการชำระค่าบริการ" (ล้มดัง 3 ที่ · F2 ข้อ 7):
/// (1) ตัวข้อมูล = <c>SubscriptionPayment.AttentionReason/Rule/At</c> (หน้าแอดมิน <c>payment-exceptions</c>) · (2) audit chain พร้อม RuleCode ·
/// (3) กระดิ่งของผู้ดูแลระบบแพลตฟอร์ม (ผู้ใช้ <c>IsSystemAdmin</c> ที่เป็นสมาชิกบริษัทผู้ให้บริการ) — เดิมตัดชำระล้ม = หมายเหตุใน ReviewNotes + LogError (เงียบ)
/// </summary>
public static class PlatformPaymentAttention
{
    public const string RuleSettleFailed = "PLATFORM-SETTLE-FAILED";
    public const string RuleHeld = "PLATFORM-PAYMENT-HELD";
    public const string RuleOverpaid = "PLATFORM-OVERPAYMENT";
    /// <summary>ฝ่ายค้านรอบสุดท้าย 4a: ชำระออนไลน์โดยหักภาษี ณ ที่จ่าย — อนุมัติแล้ว รอหนังสือรับรอง 50 ทวิ</summary>
    public const string RuleWhtCertPending = "PLATFORM-WHT-CERT-PENDING";

    /// <summary>ต้องรอหนังสือรับรอง 50 ทวิ ไหม — ชำระออนไลน์ (ไม่มีคนเห็นเอกสาร) + มีภาษีหัก + ยังไม่ได้รับไฟล์</summary>
    public static bool WhtCertificateOutstanding(bool systemConfirmed, decimal withholdingTax, string? certificateFileName)
        => systemConfirmed && withholdingTax > 0m && string.IsNullOrEmpty(certificateFileName);

    /// <summary>ประทับเรื่องค้าง + audit chain (ไม่บันทึก — ผู้เรียก SaveChanges) · เหตุเดิมที่ยังไม่ถูกเคลียร์ = ต่อท้าย (ไม่ทับ)</summary>
    public static void Flag(AccountingDbContext db, SubscriptionPayment p, Guid buyerCompanyId, string reason, string rule, string actor)
    {
        var merged = string.IsNullOrWhiteSpace(p.AttentionReason) || p.AttentionReason!.Contains(reason) ? reason : $"{p.AttentionReason} · {reason}";
        p.AttentionReason = merged.Length > 500 ? merged[..500] : merged;
        p.AttentionRule = rule;
        p.AttentionAt = DateTime.UtcNow;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = buyerCompanyId,
            UserEmail = actor,
            Action = AuditAction.Update,
            EntityType = nameof(SubscriptionPayment),
            EntityId = p.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new { paymentNumber = p.PaymentNumber, reason, ruleCode = rule }),
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>เคลียร์เรื่องค้าง (เช่น ตัดชำระซ้ำสำเร็จ) — audit ว่าใครเคลียร์ (ไม่บันทึก)</summary>
    public static void Clear(AccountingDbContext db, SubscriptionPayment p, Guid buyerCompanyId, string actor, string note)
    {
        if (p.AttentionReason == null) return;
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = buyerCompanyId,
            UserEmail = actor,
            Action = AuditAction.Update,
            EntityType = nameof(SubscriptionPayment),
            EntityId = p.Id.ToString(),
            OldValues = JsonSerializer.Serialize(new { attention = p.AttentionReason, ruleCode = p.AttentionRule }),
            NewValues = JsonSerializer.Serialize(new { attention = (string?)null, note }),
            Timestamp = DateTime.UtcNow,
        });
        p.AttentionReason = null; p.AttentionRule = null; p.AttentionAt = null;
    }

    public const string AdminPageUrl = "/admin/platform-billing.html#exceptions";

    /// <summary>กระดิ่งถึงผู้ดูแลระบบแพลตฟอร์ม — คืนจำนวนที่ส่ง · ล้มโยนต่อให้ผู้เรียก log (ตัวข้อมูล + audit ประทับแล้ว ไม่ขึ้นกับกระดิ่ง)</summary>
    public static async Task<int> NotifyAdminsAsync(AccountingDbContext db, Services.Interfaces.INotificationService? notifications,
        string title, string message, Guid paymentId, CancellationToken ct = default)
    {
        if (notifications == null) return 0;
        var platformId = await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.PlatformCompanyId).FirstOrDefaultAsync(ct);
        if (platformId is not Guid pid) return 0;
        var n = 0;
        foreach (var uid in await PlatformAdminUserIdsAsync(db, pid, ct))
        {
            await notifications.SendAsync(uid, pid, NotificationType.SubscriptionPaymentPending, title, message, AdminPageUrl,
                nameof(SubscriptionPayment), paymentId);
            n++;
        }
        return n;
    }

    /// <summary>ผู้รับกระดิ่ง: ผู้ดูแลระบบแพลตฟอร์มที่เป็นสมาชิกบริษัทผู้ให้บริการ · ไม่มี = รายการว่าง (ยังมีหน้าแอดมิน + audit)</summary>
    private static async Task<List<Guid>> PlatformAdminUserIdsAsync(AccountingDbContext db, Guid platformCompanyId, CancellationToken ct = default)
        => await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == platformCompanyId && cu.User.IsSystemAdmin)
            .Select(cu => cu.UserId).Distinct().ToListAsync(ct);
}
