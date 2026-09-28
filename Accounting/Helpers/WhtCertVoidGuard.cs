using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ที่อยู่ในแบบ ภ.ง.ด. ที่ยื่นแล้ว ยกเลิกไม่ได้** — ตัวตัดสินตัวเดียวของหน้ายกเลิก 50 ทวิ
/// (<c>WithholdingTaxCertService.VoidAsync</c>) · การยกเลิกเอกสารต้นทาง (<c>DocumentService.VoidDocumentAsync</c>) · ด่านยกเลิกการลงบัญชี
/// รอบโอน (<see cref="SettlementUnpostGate"/>) — ฝ่ายค้าน review198-C C-2
///
/// <para>ที่มา: <c>VoidAsync</c> เดิมเปลี่ยนสถานะเป็น Voided ได้ทุกกรณี ⇒ 50 ทวิ ที่ยื่น ภ.ง.ด.53 และนำส่งแล้ว (ผู้ถูกหักถือฉบับจริงไปแล้ว)
/// หายจากยอดนำส่ง/ไฟล์ยื่นเงียบ ๆ และ JE กลับรายการพลิก 21917 เป็นยอดเดบิต</para>
/// <para>"ยื่นแล้ว" = ใบนับเข้าแบบยื่น (<see cref="WhtCertFilingScope.Filed"/> — ออกแล้ว/พิมพ์แล้ว) <b>และ</b> รายงานของแบบ+เดือนนั้นถูกประกาศว่ายื่น
/// หรือยื่นแล้ว (<see cref="TaxFilingLockPolicy.DeclaredOrFiledStatuses"/> ชุดเดียวกับด่านลงบัญชี) · ร่าง/ยังไม่ยื่น ⇒ ยกเลิกได้ตามเดิม</para>
/// </summary>
public static class WhtCertVoidGuard
{
    /// <summary>เหตุผลที่ห้ามยกเลิก — null = ยกเลิกได้</summary>
    /// <param name="periodDeclaredOrFiled">รายงานภาษีของแบบ <paramref name="formType"/> เดือน <paramref name="month"/>/<paramref name="year"/>
    /// อยู่ในสถานะประกาศว่ายื่น/ยื่นแล้ว</param>
    public static string? Reason(WithholdingTaxCertStatus status, string? certNumber, TaxType formType, int year, int month,
        bool periodDeclaredOrFiled)
    {
        if (!WhtCertFilingScope.Filed.Contains(status) || !periodDeclaredOrFiled) return null;
        var form = FormLabel(formType);
        return $"หนังสือรับรองหัก ณ ที่จ่าย {certNumber} อยู่ใน {form} เดือน {month:00}/{year + 543} ที่ยื่นแล้ว — ผู้ถูกหักถือฉบับจริงไปแล้ว "
            + $"ยกเลิกในระบบไม่ได้ · ถ้าหักผิด ให้ยื่น {form} เพิ่มเติม (หรือขอคืนตามขั้นตอนของกรมสรรพากร) แล้วบันทึกรายการปรับปรุงในงวดปัจจุบัน";
    }

    /// <summary>ตรวจหนังสือรับรองชุดหนึ่ง (ของบริษัทนี้) — คืนเหตุผลข้อแรกที่ห้ามยกเลิก หรือ null</summary>
    public static async Task<string?> CheckAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> certIds,
        CancellationToken ct = default)
    {
        if (certIds.Count == 0) return null;
        var ids = certIds.ToList();
        var certs = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && ids.Contains(w.Id) && WhtCertFilingScope.Filed.Contains(w.Status))
            .Select(w => new { w.CertificateNumber, w.Status, w.TaxFormType, w.TaxYear, w.TaxMonth })
            .ToListAsync(ct);
        if (certs.Count == 0) return null;
        var years = certs.Select(c => c.TaxYear).Distinct().ToList();
        var filed = (await db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted && years.Contains(t.Year)
                    && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
                .Select(t => new { t.TaxType, t.Year, t.Month }).ToListAsync(ct))
            .Select(t => (t.TaxType, t.Year, t.Month)).ToHashSet();
        foreach (var c in certs)
            if (Reason(c.Status, c.CertificateNumber, c.TaxFormType, c.TaxYear, c.TaxMonth,
                    filed.Contains((c.TaxFormType, c.TaxYear, c.TaxMonth))) is string why)
                return why;
        return null;
    }

    /// <summary>ตรวจทุก 50 ทวิ ที่ยังไม่ถูกยกเลิกของเอกสารต้นทางใบหนึ่ง (ก่อนยกเลิกเอกสาร — ห้ามให้เอกสารหายแต่ใบรับรองที่ยื่นแล้วค้าง)</summary>
    public static async Task<string?> CheckDocumentAsync(AccountingDbContext db, Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        var ids = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && w.DocumentId == documentId && !w.IsDeleted
                && w.Status != WithholdingTaxCertStatus.Voided)
            .Select(w => w.Id).ToListAsync(ct);
        return await CheckAsync(db, companyId, ids, ct);
    }

    private static string FormLabel(TaxType t) => t switch
    {
        TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        TaxType.WithholdingTax54 => "ภ.ง.ด.54",
        _ => t.ToString(),
    };
}
