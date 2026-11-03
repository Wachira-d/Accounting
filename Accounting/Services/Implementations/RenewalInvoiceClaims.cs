using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 230 โควตา/บิล (ฝ่ายค้าน S1/S2 · คำตัดสินข้อ 230-Q1/Q3) — <b>ตัวตัดสินเดียว</b> "ใบแจ้งหนี้ต่ออายุใบนี้ถูกจ่าย/แจ้งชำระแล้วหรือยัง" ก่อนแทนที่ใบ:
/// ใช้ทั้งการเปลี่ยนรายการโควตาเสริมที่ต่อ (<c>QuotaAddOnPurchaseService</c>) และอัปเกรดกลางรอบ (<c>SubscriptionService</c> — ใบของแพ็กเกจเดิม) ·
/// ถูกจ่ายแล้ว = ห้ามแทนที่อัตโนมัติ (ต้องใบลดหนี้โดยแอดมิน) · tenant isolation: ใบหาใต้บริษัทผู้ให้บริการ + รายการชำระของ subscription นี้เท่านั้น
/// </summary>
public static class RenewalInvoiceClaims
{
    /// <summary>null = ยังไม่มีใครจ่าย/แจ้งชำระ (แทนที่ได้) · ข้อความ = เหตุที่แทนที่ไม่ได้ (ไทย)</summary>
    public static async Task<string?> ClaimProblemAsync(AccountingDbContext db, Guid subscriptionId, Guid invoiceId, Guid? excludePaymentId = null,
        CancellationToken ct = default)
    {
        var claimed = await db.SubscriptionPayments.AsNoTracking()
            .AnyAsync(p => p.SubscriptionId == subscriptionId && p.PlatformInvoiceDocumentId == invoiceId
                && (excludePaymentId == null || p.Id != excludePaymentId)
                && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview || p.Status == SubscriptionPaymentStatus.Approved), ct);
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(db, ct);
        var inv = platformId is Guid pid ? await PlatformInvoiceQueries.ByIdAsync(db, pid, invoiceId, ct) : null;
        if (claimed)
            return $"ใบแจ้งหนี้ต่ออายุ {inv?.DocumentNumber} มีการแจ้งชำระ/ชำระแล้ว — แทนที่อัตโนมัติไม่ได้ (ผู้ดูแลระบบออกใบลดหนี้/ใบใหม่)".Replace("  ", " ");
        if (inv != null && inv.BalanceDue < inv.TotalAmount)
            return $"ใบแจ้งหนี้ต่ออายุ {inv.DocumentNumber} รับชำระบางส่วนแล้ว — แทนที่อัตโนมัติไม่ได้ (ผู้ดูแลระบบออกใบลดหนี้)";
        return null;
    }
}
