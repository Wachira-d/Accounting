using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// สร้างรายการ candidate ผังบัญชีที่ส่งให้ AI — แก้ bug เดิมที่
/// OrderBy(Code).Take(40) ทำให้บัญชีค่าใช้จ่าย 5xxxx (ที่อยู่ท้าย code) ถูก
/// ตัดทิ้ง AI จึงเลือกได้แต่บัญชีสินทรัพย์ 1xxxx.
///
/// หลักการใหม่ (กฎเหล็ก #1 — ส่งบริบทที่เกี่ยวข้องให้ครบ):
///   1. บัญชี "ที่บริษัทใช้ล่าสุด/บ่อย" (6 เดือนหลัง) มาก่อนเสมอ — relevance
///   2. ตามด้วยบัญชีที่เหลือในหมวดที่เกี่ยว (Expense+Asset, หรือทุกหมวดสำหรับ PV)
///   3. **ส่ง Description (คำอธิบายผัง) ด้วย** — เดิมส่งแค่ code+name
///   4. cap สูงพอครอบผังจริง (Thai chart ~150-200 บัญชี) แต่ตัด detail-level
///      (Level >= 4 = posting accounts เท่านั้น — header ลงไม่ได้อยู่แล้ว)
/// </summary>
public static class GlCandidateBuilder
{
    public sealed record Candidate(string Code, string Name, string Type, string? Description, bool IsActive, int RecentUseCount);

    /// <summary>โหลด candidate ผังบัญชีของบริษัท เรียงตามความเกี่ยวข้อง.</summary>
    /// <param name="expenseAssetOnly">true = เฉพาะ Expense+Asset (OCR/expense doc);
    /// false = ทุกหมวด (PV ที่อาจ Dr liability เช่น คืนเงินกู้กรรมการ).</param>
    /// <param name="cap">จำนวนสูงสุด (default 150 — พอกับผังไทยทั่วไป).</param>
    public static async Task<List<Candidate>> LoadAsync(
        AccountingDbContext db, Guid companyId, bool expenseAssetOnly,
        int cap = 150, CancellationToken ct = default)
    {
        // 1) ความถี่การใช้ผังใน 6 เดือนหลัง (relevance ranking)
        var since = DateTime.UtcNow.AddMonths(-6);
        var usage = await (
            from line in db.DocumentLines.AsNoTracking()
            join doc in db.Documents.AsNoTracking() on line.DocumentId equals doc.Id
            where doc.CompanyId == companyId && line.AccountId != null
                && doc.DocumentDate >= since && doc.Status != DocumentStatus.Voided
            group line by line.AccountId!.Value into g
            select new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, ct);

        // 2) ผังบัญชีที่ active + posting-level (Level >= 4) ในหมวดที่เกี่ยว
        var q = db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.Level >= 4);
        if (expenseAssetOnly)
            q = q.Where(a => a.AccountType == AccountType.Expense || a.AccountType == AccountType.Asset);
        var all = await q
            .Select(a => new { a.Id, a.AccountCode, a.AccountName, Type = a.AccountType, a.Description })
            .ToListAsync(ct);

        // 3) เรียง: ใช้บ่อย/ล่าสุดก่อน (desc) → แล้วตาม code
        var ranked = all
            .Select(a => new Candidate(
                a.AccountCode, a.AccountName, a.Type.ToString(), a.Description, true,
                usage.GetValueOrDefault(a.Id, 0)))
            .OrderByDescending(c => c.RecentUseCount)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .Take(cap)
            .ToList();

        return ranked;
    }
}
