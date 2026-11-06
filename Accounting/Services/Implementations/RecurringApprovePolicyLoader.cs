using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 248 — โหลด "นโยบายอนุมัติข้ามคำเตือน" ที่มีผลของรายการประจำ (รายการ › ผู้ติดต่อ · <see cref="AutoApprovePolicy.Effective"/>) และตรวจว่า
/// <b>ผู้ตั้งยังทำหน้าที่ได้</b> (สมาชิกที่ใช้งานอยู่ของบริษัท + ยังมีสิทธิ์อนุมัติเอกสารชนิดนั้น เมื่อมีตัวตรวจสิทธิ์) — ตัวเดียวทั้งเส้นอนุมัติจริง
/// (<c>RecurringTransactionService</c>) และแผงความพร้อม/ทดลองรัน (<c>RecurringAutopilotService</c>) · ทุก query กรองบริษัท
/// </summary>
internal static class RecurringApprovePolicyLoader
{
    /// <summary>ผล — <c>Policy</c> = นโยบายที่ใช้ได้จริง (null = ไม่มี/หยุดมีผล) · <c>Stored</c> = นโยบายที่ตั้งไว้ (แสดงผล) ·
    /// <c>SetterActive</c> = null เมื่อไม่มีนโยบายที่ปล่อยอะไร</summary>
    internal sealed record Loaded(AutoApprovePolicy? Policy, AutoApprovePolicy? Stored, bool? SetterActive, string? InactiveReason = null);

    internal const string BindingChangedReason = "ผู้ติดต่อหรือชนิดเอกสารของรายการเปลี่ยนหลังตั้งนโยบาย — ให้ผู้มีสิทธิ์อนุมัติตั้งนโยบายใหม่สำหรับค่าปัจจุบัน";
    internal const string SetterGoneReason = "ผู้ตั้งนโยบายไม่ได้เป็นสมาชิกของบริษัท/ไม่มีสิทธิ์อนุมัติเอกสารชนิดนี้แล้ว — ให้ผู้มีสิทธิ์ตั้งนโยบายใหม่";

    internal static async Task<Loaded> LoadAsync(AccountingDbContext db, IPermissionService? permissions, Guid companyId,
        string? recurringPolicyJson, Guid? contactId, DocumentType? documentType, CancellationToken ct = default)
    {
        string? contactJson = null;
        if (contactId is { } cid)
            contactJson = await db.Contacts.AsNoTracking()
                .Where(c => c.Id == cid && c.CompanyId == companyId)
                .Select(c => c.AutoApprovePolicyJson)
                .FirstOrDefaultAsync(ct);
        var stored = AutoApprovePolicy.Effective(recurringPolicyJson, contactJson);
        if (stored is not { PassCodes.Count: > 0 }) return new Loaded(stored, stored, null);

        // ฝ่ายค้านรอบ 248: นโยบายของรายการผูกผู้ติดต่อ/ชนิดเอกสารตอนตั้ง — ผู้ไม่มีสิทธิ์อนุมัติเปลี่ยนผู้ติดต่อของรายการแล้ว ⇒ ไม่ปล่อย
        if (!AutoApprovePolicy.MatchesBinding(stored, contactId, documentType?.ToString()))
            return new Loaded(null, stored, false, BindingChangedReason);
        // ผู้ตั้งออกจากบริษัท/ถูกปิดบัญชี ⇒ นโยบายหยุดมีผล (ตัวตัดสินเดียว CompanyMembership — แบบผู้อนุมัติเริ่มต้นของ workflow ข้ามบริษัท)
        var active = Guid.TryParse(stored.SetBy, out var setter)
            && await CompanyMembership.IsActiveMemberAsync(db, companyId, setter, ct);
        if (active && permissions != null)
            active = await DocumentPermissionHelper.CanApproveAsync(permissions, companyId, setter, documentType ?? DocumentType.Invoice);
        return new Loaded(active ? stored : null, stored, active, active ? null : SetterGoneReason);
    }

    /// <summary>มุมมองของนโยบายสำหรับหน้า</summary>
    internal static global::Accounting.Models.DTOs.Recurring.AutoApprovePolicyView? View(AutoApprovePolicy? p)
        => p == null ? null : new global::Accounting.Models.DTOs.Recurring.AutoApprovePolicyView(p.PassCodes, p.SetBy, p.SetAt, p.Scope);
}
