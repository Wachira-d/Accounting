using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// บัญชี "ทิปพนักงานค้างจ่าย" ที่ POS ลง Cr ตอนรับทิป และ TipPayout ลง Dr ตอนจ่าย — ตัวเดียว
/// ให้สองฝั่งใช้ร่วม (เดิม POS fallback prefix "216" ⇒ ผังมาตรฐานได้ **21610 เงินมัดจำรับ** ทุกบริษัท ·
/// TipPayout หา "2160" ที่ไม่มีในผังแล้ว throw เสมอ — ERP_REVIEW_2026-09-05 H-07).
/// เจ้าของโปรเจกต์ให้ **ตั้งค่าได้** (CompanySettings.PosTipPayableAccountCode)
/// </summary>
public static class TipAccountResolver
{
    /// <summary>รหัสผังมาตรฐานที่ใกล้ความหมาย "เงินค้างจ่ายพนักงาน" ที่สุด เมื่อบริษัทยังไม่ตั้งค่า:
    /// 21814 เงินเดือน/ค่าจ้างค้างจ่ายอื่นๆ → 21819 สวัสดิการค้างจ่าย. **ห้าม** ใช้ prefix 216 (เงินมัดจำ)</summary>
    public static readonly string[] DefaultCodes = { "21814", "21819" };

    /// <summary>ลำดับรหัสที่จะลอง: ค่าที่ตั้งไว้ก่อน แล้วค่อย default — ตัดค่าว่าง/ซ้ำ</summary>
    public static IReadOnlyList<string> CodeCandidates(string? configuredCode)
    {
        var list = new List<string>();
        var cfg = configuredCode?.Trim();
        if (!string.IsNullOrEmpty(cfg)) list.Add(cfg);
        foreach (var c in DefaultCodes) if (!list.Contains(c)) list.Add(c);
        return list;
    }

    /// <summary>หาบัญชีตามลำดับ: ตั้งค่า → default → หนี้สิน 21xxx ที่ชื่อมีคำว่า "ทิป" · คืน null ถ้าไม่มีเลย
    /// (ผู้เรียกตัดสินเองว่าจะ fail loud หรือ fold — ห้ามตกไป 216xx)</summary>
    public static async Task<ChartOfAccount?> ResolveAsync(AccountingDbContext db, Guid companyId,
        string? configuredCode, CancellationToken ct = default)
    {
        foreach (var code in CodeCandidates(configuredCode))
        {
            var acc = await db.ChartOfAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == code
                    && a.IsActive && !a.IsDeleted, ct);
            if (acc != null) return acc;
        }
        return await db.ChartOfAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                && a.Level >= 4 && a.AccountCode.StartsWith("21") && !a.AccountCode.StartsWith("216")
                && a.AccountName.Contains("ทิป"), ct);
    }
}
