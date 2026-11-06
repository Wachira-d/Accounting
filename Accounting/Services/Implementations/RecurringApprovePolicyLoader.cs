using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
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

    internal const string BindingChangedReason = "ค่าของรายการ (ผู้ติดต่อ · ชนิดเอกสาร · ยอด/บรรทัด · อนุมัติ/ส่งอีเมลอัตโนมัติ) เปลี่ยนหลังผู้มีสิทธิ์ตั้ง/รับรองนโยบาย — ให้ผู้มีสิทธิ์อนุมัติตั้ง/รับรองใหม่สำหรับค่าปัจจุบัน";
    internal const string SetterGoneReason = "ผู้ตั้ง/รับรองนโยบายไม่ได้เป็นสมาชิกของบริษัท/ไม่มีสิทธิ์อนุมัติเอกสารชนิดนี้แล้ว — ให้ผู้มีสิทธิ์ตั้งนโยบายใหม่";

    /// <summary>ลายนิ้วมือของค่าปัจจุบันของรายการ (ตัวเดียวทั้งตอนตั้ง/รับรองและตอนตรวจ)</summary>
    internal static string CurrentBindingHash(RecurringTransaction r)
        => AutoApprovePolicy.BindingHash(r.ContactId, (r.DocumentType ?? DocumentType.Invoice).ToString(), r.TemplateData,
            r.AutoApprove || r.AutoSendEmail, r.AutoSendEmail);

    /// <summary>นโยบายที่มีผลของรายการ — ไม่มีใบรับรองระดับรายการ = ไม่ปล่อย (แม้ผู้ติดต่อมีนโยบาย) · ลายนิ้วมือไม่ตรง/ผู้ตั้งหรือผู้รับรองหมดสิทธิ์ = ไม่ปล่อย</summary>
    internal static async Task<Loaded> LoadAsync(AccountingDbContext db, IPermissionService? permissions, Guid companyId,
        RecurringTransaction r, CancellationToken ct = default)
    {
        var docType = r.DocumentType ?? DocumentType.Invoice;
        var cert = AutoApprovePolicy.Parse(r.AutoApprovePolicyJson, AutoApprovePolicy.ScopeRecurring);
        if (cert == null) return new Loaded(null, null, null);
        string? contactJson = null;
        if (cert.InheritContact && r.ContactId is { } cid)
            contactJson = await db.Contacts.AsNoTracking()
                .Where(c => c.Id == cid && c.CompanyId == companyId)
                .Select(c => c.AutoApprovePolicyJson)
                .FirstOrDefaultAsync(ct);
        var stored = AutoApprovePolicy.Effective(r.AutoApprovePolicyJson, contactJson);
        if (stored is not { PassCodes.Count: > 0 }) return new Loaded(stored, stored, null);

        // ฝ่ายค้าน HIGH/MED-2: ใบรับรองผูกลายนิ้วมือของค่าที่ผู้มีสิทธิ์เห็นตอนตั้ง — แก้ยอด/ผู้ติดต่อ/ธงอัตโนมัติภายหลัง ⇒ ไม่ปล่อย
        if (!AutoApprovePolicy.MatchesBinding(cert, CurrentBindingHash(r)))
            return new Loaded(null, stored, false, BindingChangedReason);
        // ผู้ตั้ง (และผู้รับรองระดับรายการ เมื่อใช้นโยบายของผู้ติดต่อ) ต้องยังเป็นสมาชิก + มีสิทธิ์อนุมัติชนิดนี้ (ตัวตัดสินเดียว CompanyMembership)
        foreach (var who in new[] { stored.SetBy, cert.SetBy }.Distinct())
            if (!await CanStillApproveAsync(db, permissions, companyId, who, docType, ct))
                return new Loaded(null, stored, false, SetterGoneReason);
        return new Loaded(stored, stored, true);
    }

    private static async Task<bool> CanStillApproveAsync(AccountingDbContext db, IPermissionService? permissions, Guid companyId,
        string? userId, DocumentType docType, CancellationToken ct)
    {
        if (!Guid.TryParse(userId, out var uid) || !await CompanyMembership.IsActiveMemberAsync(db, companyId, uid, ct)) return false;
        return permissions == null || await DocumentPermissionHelper.CanApproveAsync(permissions, companyId, uid, docType);
    }

    /// <summary>มุมมองของนโยบายสำหรับหน้า</summary>
    internal static global::Accounting.Models.DTOs.Recurring.AutoApprovePolicyView? View(AutoApprovePolicy? p)
        => p == null ? null : new global::Accounting.Models.DTOs.Recurring.AutoApprovePolicyView(p.PassCodes, p.SetBy, p.SetAt, p.Scope, p.InheritContact);
}
