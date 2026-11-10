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
    internal const string DefaultSetterGoneReason = "ผู้รับผิดชอบค่าเริ่มต้น (ผู้มีสิทธิ์ที่เลือก \"ค่าเริ่มต้น\" ล่าสุด หรือผู้สร้างรายการประจำ) ไม่ได้เป็นสมาชิก/ไม่มีสิทธิ์อนุมัติเอกสารชนิดนี้แล้ว — ใบงวดใหม่ที่ลูกค้ามีใบค้างจะหยุดรอคน · ให้ผู้มีสิทธิ์อนุมัติตั้งนโยบายของรายการ";
    /// <summary>รอบ 257 ชุด 4 (A-H2 · server computes): คำอธิบายค่าเริ่มต้นของหน้านโยบาย · ข้อความหลังผู้มีสิทธิ์เลือก "ค่าเริ่มต้น" (เดิม toast บอก "ไม่ปล่อยคำเตือนใด ๆ" ผิดหลังรอบ 256)</summary>
    internal const string DefaultNote = "ค่าเริ่มต้นของรายการประจำ (รอบ 256): ลูกค้าค้างชำระ ⇒ อนุมัติใบงวดใหม่ต่อ + หมายเหตุบนใบ + บันทึกตรวจสอบ · ข้ออื่นหยุดรอคน · มีผลเฉพาะรายการที่อนุมัติอัตโนมัติ ผู้สร้างยังมีสิทธิ์อนุมัติ และค่าของรายการไม่เปลี่ยนหลังสร้าง/รับรอง · ผู้ติดต่อตั้ง \"หยุดรอคน\" ⇒ หยุดรอคน · อยากเปลี่ยนแถวใด เลือก \"ตั้งเองสำหรับรายการนี้\"";
    /// <summary>รอบ 257 ชุด 4 (E-M3): ประกาศครั้งเดียวบนหน้ารายการประจำ — ค่าตั้งต้นเปลี่ยนในรอบ 256 (1dd21fd7) โดยเจ้าของบริษัทไม่ถูกแจ้ง · คีย์ = รุ่นของประกาศ</summary>
    internal const string DefaultChangeNoticeKey = "r256-recurring-default";
    internal const string DefaultChangeNotice = "ค่าเริ่มต้นของรายการประจำเปลี่ยนแล้ว (รอบ 256): ใบงวดใหม่ของรายการที่อนุมัติอัตโนมัติ จะอนุมัติต่อแม้ลูกค้ามีใบค้างชำระ "
        + "(ลงหมายเหตุบนใบ + บันทึกตรวจสอบ) — เดิมหยุดเป็นใบร่าง · อยากให้หยุดรอคนเหมือนเดิม: เปิดรายการ › ขั้น \"อนุมัติ\" › เลือก \"ตั้งเองสำหรับรายการนี้\" แล้วตั้ง \"หยุดรอคน\" "
        + "หรือตั้งที่ผู้ติดต่อ (\"บันทึกเป็นนโยบายของผู้ติดต่อ\" ด้วยแถว \"หยุดรอคน\") ให้มีผลกับทุกรายการของลูกค้ารายนั้น";
    internal const string DefaultConfirmedMessage = "ใช้ค่าเริ่มต้นแล้ว — ลูกค้าค้างชำระ ⇒ อนุมัติใบงวดใหม่ต่อ (ผูกกับค่าปัจจุบันของรายการ · แก้ยอด/ผู้ติดต่อ/ธงอัตโนมัติภายหลังต้องรับรองใหม่) · อยากให้หยุด เลือก \"ตั้งเองสำหรับรายการนี้\" แล้วตั้ง \"หยุดรอคน\"";
    internal const string SetterGoneReason = "ผู้ตั้ง/รับรองนโยบายไม่ได้เป็นสมาชิกของบริษัท/ไม่มีสิทธิ์อนุมัติเอกสารชนิดนี้แล้ว — ให้ผู้มีสิทธิ์ตั้งนโยบายใหม่";

    /// <summary>ลายนิ้วมือของค่าปัจจุบันของรายการ (ตัวเดียวทั้งตอนตั้ง/รับรองและตอนตรวจ) — รอบ 257 ชุด 4 ฝ่ายค้าน LOW-1: รุ่น 2 (+ ตารางเวลา) ·
    /// ค่าที่ประทับใหม่ทุกตัวใช้ตัวนี้ · ตรวจผ่าน <c>AutoApprovePolicy.MatchesStamp</c> (เทียบตามรุ่นที่ประทับ)</summary>
    internal static string CurrentBindingHash(RecurringTransaction r)
        => AutoApprovePolicy.BindingHashV2(r.ContactId, (r.DocumentType ?? DocumentType.Invoice).ToString(), r.TemplateData,
            r.AutoApprove || r.AutoSendEmail, r.AutoSendEmail,
            r.Frequency.ToString(), r.IntervalMonths, r.LeadDays, r.EndDate, r.MaxRuns);

    /// <summary>รุ่น 1 (ก่อนรอบ 257 ฝ่ายค้าน) — ใช้ตรวจค่าที่ประทับไว้ก่อนหน้าเท่านั้น ห้ามใช้ประทับใหม่</summary>
    internal static string CurrentBindingHashV1(RecurringTransaction r)
        => AutoApprovePolicy.BindingHash(r.ContactId, (r.DocumentType ?? DocumentType.Invoice).ToString(), r.TemplateData,
            r.AutoApprove || r.AutoSendEmail, r.AutoSendEmail);

    /// <summary>ผู้รับผิดชอบค่าตั้งต้น — ผู้มีสิทธิ์ที่ประทับล่าสุด (ฝ่ายค้าน MED-2) ไม่งั้นผู้สร้างรายการ</summary>
    internal static string? DefaultResponsible(RecurringTransaction r) => r.DefaultPolicyStampedBy ?? r.CreatedBy;

    /// <summary>รอบ 257 ชุด 4 ฝ่ายค้าน MED-1/LOW-1 — เรียก<b>ก่อน</b>แก้ค่าใดของรายการ (<c>RecurringTransactionService.UpdateAsync</c>):
    /// (1) ค่าตั้งต้นที่ยังไม่เคยประทับ (แถวก่อนรอบ 257) ⇒ ประทับด้วยค่าก่อนแก้ — แก้ผู้ติดต่อ/ยอด/ธงแล้วไม่ตรง ⇒ หยุดรอคน (เดิม null = ตรงตลอดไป) ·
    /// (2) ค่าที่ประทับรุ่น 1 ที่ยังตรง ⇒ ยกเป็นรุ่น 2 (รวมตารางเวลา) ทั้งค่าตั้งต้นและใบรับรองนโยบาย — ไม่ตรงแล้วไม่แตะ (ยังไม่ตรงต่อไป) ·
    /// ไม่เปลี่ยนผู้รับผิดชอบ/ผู้ตั้ง (ไม่มีผู้มีสิทธิ์อนุมัติในเส้นนี้)</summary>
    internal static void UpgradeStampsBeforeEdit(RecurringTransaction r)
    {
        var v1 = CurrentBindingHashV1(r);
        var v2 = CurrentBindingHash(r);
        if (r.DefaultPolicyBindingHash == null || string.Equals(r.DefaultPolicyBindingHash, v1, StringComparison.Ordinal))
            r.DefaultPolicyBindingHash = v2;
        var cert = AutoApprovePolicy.Parse(r.AutoApprovePolicyJson);
        if (cert?.BindingHashValue != null && string.Equals(cert.BindingHashValue, v1, StringComparison.Ordinal))
            r.AutoApprovePolicyJson = AutoApprovePolicy.Serialize(cert with { BindingHashValue = v2 });
    }

    /// <summary>นโยบายที่มีผลของรายการ — ไม่มีใบรับรองระดับรายการ = ไม่ปล่อย (แม้ผู้ติดต่อมีนโยบาย) · ลายนิ้วมือไม่ตรง/ผู้ตั้งหรือผู้รับรองหมดสิทธิ์ = ไม่ปล่อย</summary>
    internal static async Task<Loaded> LoadAsync(AccountingDbContext db, IPermissionService? permissions, Guid companyId,
        RecurringTransaction r, CancellationToken ct = default)
    {
        var docType = r.DocumentType ?? DocumentType.Invoice;
        var cert = AutoApprovePolicy.Parse(r.AutoApprovePolicyJson, AutoApprovePolicy.ScopeRecurring);
        if (cert == null)
        {
            // รอบ 256 (คำตัดสินเจ้าของ): ไม่ได้ตั้งนโยบาย ⇒ ค่าตั้งต้นของรายการประจำ = ปล่อย "ลูกค้าค้างชำระ" ต่อ (ใบงวดใหม่ไม่ค้างเพราะใบเก่า) ·
            // เฉพาะรายการที่อนุมัติอัตโนมัติ + ผู้สร้างยังเป็นสมาชิกที่มีสิทธิ์อนุมัติชนิดนี้ (ไม่มี ⇒ หยุดรอคนตามเดิม พร้อมเหตุ)
            if (!r.AutoApprove) return new Loaded(null, null, null);
            // รอบ 257 ชุด 4 (A-M5): ผู้ติดต่อตั้ง "หยุดรอคน" ⇒ ทับค่าตั้งต้นของรายการของผู้ติดต่อนั้น (Policy null = เส้น Unattended เดิม · Stored = แสดงผล)
            if (r.ContactId is { } dcid)
            {
                var contactPolicy = AutoApprovePolicy.Parse(await db.Contacts.AsNoTracking()
                    .Where(c => c.Id == dcid && c.CompanyId == companyId)
                    .Select(c => c.AutoApprovePolicyJson)
                    .FirstOrDefaultAsync(ct), AutoApprovePolicy.ScopeContact);
                if (AutoApprovePolicy.StopsRecurringDefault(contactPolicy)) return new Loaded(null, contactPolicy, null);
            }
            // รอบ 257 ชุด 4 (A-M4): ค่าตั้งต้นผูกลายนิ้วมือที่ประทับตอนสร้าง/ตอนผู้มีสิทธิ์ใช้ค่าเริ่มต้น — แก้ยอด/ผู้ติดต่อ/ธงภายหลัง ⇒ ไม่ปล่อย
            if (!AutoApprovePolicy.MatchesDefaultBinding(r.DefaultPolicyBindingHash, CurrentBindingHashV1(r), CurrentBindingHash(r)))
                return new Loaded(null, null, false, BindingChangedReason);
            // ฝ่ายค้าน MED-2: ผู้รับผิดชอบ = ผู้มีสิทธิ์ที่เลือก "ค่าเริ่มต้น" ล่าสุด › ผู้สร้าง (ตรวจและลงชื่อบนหมายเหตุด้วยคนเดียวกัน)
            var responsible = DefaultResponsible(r);
            if (!await CanStillApproveAsync(db, permissions, companyId, responsible, docType, ct))
                return new Loaded(null, null, false, DefaultSetterGoneReason);
            return new Loaded(AutoApprovePolicy.RecurringDefault(responsible), null, true);
        }
        string? contactJson = null;
        if (cert.InheritContact && r.ContactId is { } cid)
            contactJson = await db.Contacts.AsNoTracking()
                .Where(c => c.Id == cid && c.CompanyId == companyId)
                .Select(c => c.AutoApprovePolicyJson)
                .FirstOrDefaultAsync(ct);
        var stored = AutoApprovePolicy.Effective(r.AutoApprovePolicyJson, contactJson);
        if (stored is not { PassCodes.Count: > 0 }) return new Loaded(stored, stored, null);

        // ฝ่ายค้าน HIGH/MED-2: ใบรับรองผูกลายนิ้วมือของค่าที่ผู้มีสิทธิ์เห็นตอนตั้ง — แก้ยอด/ผู้ติดต่อ/ธงอัตโนมัติภายหลัง ⇒ ไม่ปล่อย
        if (!AutoApprovePolicy.MatchesBinding(cert, CurrentBindingHashV1(r), CurrentBindingHash(r)))   // รอบ 257 ฝ่ายค้าน LOW-1: ตามรุ่นที่ประทับ
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
