using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวอ่าน "เงินรอจัดสรรคงเหลือต่อลูกค้า" — ตัวเดียว** (รอบ 258) · ใช้โดยทุกหน้าที่ต้องรู้ว่าลูกค้ามีเงินที่รับแล้วแต่ยังไม่ผูกกับใบ
/// (ระงับทวงหนี้ · รายงานอายุลูกหนี้ · ใบแจ้งยอดลูกค้า · ปุ่ม "ใช้เงินรอจัดสรร" บนใบ) — นิยามเดียว:
/// การรับชำระที่ยังมีผล (ไม่ถูกยกเลิก) · มีผังพัก (<c>UnappliedAccountId</c>) · รู้เจ้าของ (<c>ContactId</c>) · ยอดคงเหลือ &gt; ครึ่งสตางค์ · tenant เสมอ
/// </summary>
public static class CustomerCreditQuery
{
    /// <summary>ยอดเงินรอจัดสรรคงเหลือรวมต่อผู้ติดต่อของบริษัท (ผู้ติดต่อที่ไม่มี = ไม่อยู่ในผล)</summary>
    public static async Task<Dictionary<Guid, decimal>> UnappliedByContactAsync(AccountingDbContext db, Guid companyId,
        CancellationToken ct = default)
    {
        var rows = await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.UnappliedAccountId != null && p.ContactId != null && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .GroupBy(p => p.ContactId!.Value)
            .Select(g => new { ContactId = g.Key, Total = g.Sum(p => p.UnappliedAmount) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.ContactId, r => r.Total);
    }

    /// <summary>
    /// ในบรรดา <paramref name="references"/> (เลขอ้างอิงของสมุดรายวัน) ตัวไหนเป็น<b>เลขการรับชำระ</b>ของบริษัทนี้ — JE ที่ระบบสร้างและอ้างเลขการชำระตรง ๆ
    /// โดยไม่มีเอกสารต้นทาง = JE "เงินส่วนเกิน" ของการรับชำระหลายใบ (P-2: ห้ามถูกหยิบเป็น JV มัดจำ) · นับการชำระที่ถูกยกเลิกแล้วด้วย · tenant
    /// </summary>
    public static async Task<HashSet<string>> PaymentExcessReferencesAsync(AccountingDbContext db, Guid companyId,
        IEnumerable<string?> references, CancellationToken ct = default)
    {
        var refs = references.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct().ToList();
        if (refs.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        var hits = await db.Payments.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.CompanyId == companyId && refs.Contains(p.PaymentNumber))
            .Select(p => p.PaymentNumber)
            .ToListAsync(ct);
        return new HashSet<string>(hits, StringComparer.Ordinal);
    }

    /// <summary>ยอดเงินรอจัดสรรคงเหลือของผู้ติดต่อรายเดียว (0 = ไม่มี)</summary>
    public static async Task<decimal> UnappliedForContactAsync(AccountingDbContext db, Guid companyId, Guid contactId,
        CancellationToken ct = default)
        => await db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == companyId && !p.IsDeleted && p.ContactId == contactId
                && p.UnappliedAccountId != null && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .SumAsync(p => (decimal?)p.UnappliedAmount, ct) ?? 0m;
}
