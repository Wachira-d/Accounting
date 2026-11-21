using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 284 ฝ่ายค้าน HIGH-3 (คำตัดสิน 284-12) — <b>ตัวตัดสินเดียว</b>ของรายงานตั้งเวลา (อีเมลตัวเลขการเงินออกนอกระบบ):
/// <list type="bullet">
/// <item>สร้าง/แก้: ผู้บันทึกต้องผ่าน <see cref="FinancialReportAccess"/> ของ "ส่งออกรายงาน" + คีย์ตามชนิดรายงาน (<see cref="KeysFor"/>) · ชนิดที่ไม่รู้จัก ⇒ ปฏิเสธ</item>
/// <item>ผู้รับ: อีเมลของสมาชิกบริษัท (ใช้งานได้) หรือรายชื่อภายนอกที่<b>เจ้าของ</b>อนุมัติ (บันทึกโดยเจ้าของ ⇒ อนุมัติรายชื่อนั้น) ·
/// ผู้ไม่ใช่เจ้าของเพิ่มผู้รับภายนอกใหม่ไม่ได้</item>
/// <item>ตอนส่ง: ผู้รับผิดชอบ (ผู้บันทึกล่าสุด — <c>UpdatedBy ?? CreatedBy</c>) ต้องยังเป็นสมาชิกและยังมีสิทธิ์ ณ ตอนส่ง · ไม่ผ่าน ⇒ ไม่ส่ง + แจ้ง ·
/// ผู้รับภายนอกที่ไม่ได้อนุมัติ ⇒ ไม่ส่งถึงคนนั้น (นับเป็นส่งไม่สำเร็จพร้อมเหตุ)</item>
/// </list>
/// </summary>
public static class ScheduledReportAccess
{
    public const string RuleCode = "R284-SCHEDULED-REPORT";

    /// <summary>คีย์ตามชนิดรายงาน (คีย์ใดคีย์หนึ่ง · ตัวเดียวกับหน้ารายงานนั้น) · null = ชนิดที่ระบบไม่รู้จัก</summary>
    public static string[]? KeysFor(string? reportCode) => (reportCode ?? "").Trim().ToUpperInvariant() switch
    {
        "PNL" or "BALANCE_SHEET" or "TRIAL_BALANCE" => new[] { PermissionKeys.ReportsFinancial },
        "AR_AGING" or "AP_AGING" => new[] { PermissionKeys.ReportsOperational },
        "CASH_FORECAST" => new[] { PermissionKeys.ReportsFinancial, PermissionKeys.ReportsExecutive },
        _ => null,
    };

    public static readonly string[] ExportKeys = { PermissionKeys.ReportsExport };

    private static string Norm(string? e) => (e ?? "").Trim().ToLowerInvariant();

    /// <summary>ผู้รับที่ไม่ใช่สมาชิกและไม่อยู่ในรายชื่อที่เจ้าของอนุมัติ (pure)</summary>
    public static List<string> UnapprovedRecipients(IEnumerable<string> recipients, IReadOnlySet<string> memberEmails, IEnumerable<string>? approvedExternal)
    {
        var approved = (approvedExternal ?? Array.Empty<string>()).Select(Norm).ToHashSet();
        return recipients.Select(Norm).Where(r => r.Length > 0 && !memberEmails.Contains(r) && !approved.Contains(r)).Distinct().ToList();
    }

    /// <summary>รายชื่อภายนอกที่อนุมัติหลังบันทึก (pure) — เจ้าของบันทึก ⇒ ผู้รับภายนอกทั้งหมดในรายการ · คนอื่นบันทึก ⇒ คงที่อนุมัติไว้ (ตัดคนที่ไม่อยู่ในรายการแล้ว)</summary>
    public static List<string> ApprovedAfterSave(IEnumerable<string> recipients, IReadOnlySet<string> memberEmails, IEnumerable<string>? previouslyApproved, bool saverIsOwner)
    {
        var list = recipients.Select(Norm).Where(r => r.Length > 0).Distinct().ToList();
        var external = list.Where(r => !memberEmails.Contains(r)).ToList();
        if (saverIsOwner) return external;
        var prev = (previouslyApproved ?? Array.Empty<string>()).Select(Norm).ToHashSet();
        return external.Where(prev.Contains).ToList();
    }

    public static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    /// <summary>อีเมลของสมาชิกที่ใช้งานได้ของบริษัท (ตัวพิมพ์เล็ก)</summary>
    public static async Task<HashSet<string>> MemberEmailsAsync(AccountingDbContext db, Guid companyId, CancellationToken ct = default)
        => (await db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == companyId && !cu.AccessPending)
                .Join(db.Users.AsNoTracking(), cu => cu.UserId, u => u.Id, (cu, u) => u.Email)
                .ToListAsync(ct))
            .Select(Norm).Where(e => e.Length > 0).ToHashSet();

    /// <summary>ผู้ใช้นี้สร้าง/แก้/ส่งรายงานชนิดนี้ได้ไหม (null = ได้) — ข้อความไทยบอกคีย์ที่ขาด</summary>
    public static async Task<string?> UserProblemAsync(AccountingDbContext db, IPermissionService perms, Guid companyId, Guid userId, string? reportCode)
    {
        var keys = KeysFor(reportCode);
        if (keys == null) return $"ไม่รู้จักชนิดรายงาน “{reportCode}” — เลือกจาก PNL · BALANCE_SHEET · TRIAL_BALANCE · AR_AGING · AP_AGING · CASH_FORECAST";
        if (!await FinancialReportAccess.AllowsAsync(db, perms, companyId, userId, ExportKeys)) return FinancialReportAccess.DeniedMessage(ExportKeys);
        if (!await FinancialReportAccess.AllowsAsync(db, perms, companyId, userId, keys)) return FinancialReportAccess.DeniedMessage(keys);
        return null;
    }

    /// <summary>ตอนส่ง: ผู้รับผิดชอบยังมีสิทธิ์ไหม (null = ส่งได้) · ไม่รู้ผู้รับผิดชอบ (แถวรุ่นเก่า) ⇒ ไม่ส่ง (ไม่เดา)</summary>
    public static async Task<string?> SendProblemAsync(AccountingDbContext db, IPermissionService? perms, ScheduledReport s)
    {
        if (!Guid.TryParse(s.UpdatedBy ?? s.CreatedBy, out var responsible))
            return "ไม่รู้ว่าใครเป็นผู้รับผิดชอบรายงานนี้ (บันทึกก่อนรอบ 284) — ผู้มีสิทธิ์ดูรายงานนี้เปิดแก้แล้วกดบันทึกหนึ่งครั้ง";
        if (perms == null) return "ตรวจสิทธิ์ไม่ได้ (ระบบสิทธิ์ไม่พร้อม) — ไม่ส่งรอบนี้";
        var problem = await UserProblemAsync(db, perms, s.CompanyId, responsible, s.ReportCode);
        return problem == null ? null : "ผู้รับผิดชอบรายงานนี้ไม่มีสิทธิ์แล้ว (หรือออกจากบริษัท): " + problem + " — ผู้มีสิทธิ์เปิดแก้แล้วกดบันทึกเพื่อรับผิดชอบแทน";
    }

    public const string UnapprovedRecipientError = "ผู้รับภายนอกที่เจ้าของบริษัทยังไม่ได้อนุมัติ — ให้เจ้าของเปิดแก้รายงานนี้แล้วกดบันทึก";
}
