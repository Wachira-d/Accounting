using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **หนังสือรับรองหัก ณ ที่จ่าย (50 ทวิ) ที่อยู่ในแบบ ภ.ง.ด. ที่ยื่นแล้ว ยกเลิกไม่ได้** — ตัวตัดสินตัวเดียวของหน้ายกเลิก 50 ทวิ
/// (<c>WithholdingTaxCertService.VoidAsync</c>) · การยกเลิกเอกสารต้นทาง (<c>DocumentService.VoidDocumentAsync</c>) · ด่านยกเลิกการลงบัญชี
/// รอบโอน (<see cref="SettlementUnpostGate"/>) · การออกใบ ภ.ง.ด.1 ใหม่ของรอบเงินเดือนที่ re-post (<c>PayrollService.IssueMonthlyPnd1CertsAsync</c> —
/// เดิมประทับ Voided ตรง · review198-S3 S3-10 ทีม S4) — ฝ่ายค้าน review198-C C-2
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

    /// <summary>ตัวตัดสินเต็ม (pure) — รายงานภาษีที่ประกาศว่ายื่น (ทั้งงวด) <b>หรือ</b> ใบนี้อยู่ในการนำส่งของงวด · null = ยกเลิกได้
    ///
    /// <para>รอบ 201 ฝ่ายค้านรอบสาม PR2 (P1-1): บันทึกนำส่ง (<c>StatutoryRemittance</c>) นับเฉพาะใบที่ออก<b>ก่อนหรือพร้อม</b>เวลาบันทึกนำส่ง
    /// (<see cref="RemittanceInclusion"/>) — ใบที่ออกหลังนำส่ง (PV ลงวันที่ย้อนเข้างวดที่นำส่งแล้ว) ยังไม่อยู่ในเงินที่นำส่ง
    /// (หน้านำส่งนับเป็นยอดค้าง) ⇒ ยกเลิกได้ · เดิมนับทั้งงวด ⇒ ยกเลิกไม่ได้และข้อความสั่ง “ยื่นเพิ่มเติม” ซึ่งผิด</para></summary>
    /// <param name="remittedAtUtc">เวลาบันทึกการนำส่งล่าสุดของแบบ+งวดนั้น (null = ยังไม่นำส่ง)</param>
    /// <param name="certCountedAtUtc">เวลาที่ใบนับเข้ายอด (<see cref="RemittanceInclusion.CertCountedAt"/>)</param>
    public static string? Reason(WithholdingTaxCertStatus status, string? certNumber, TaxType formType, int year, int month,
        bool periodDeclaredOrFiled, DateTime? remittedAtUtc, DateTime certCountedAtUtc)
    {
        if (Reason(status, certNumber, formType, year, month, periodDeclaredOrFiled) is string declared) return declared;
        if (!WhtCertFilingScope.Filed.Contains(status) || remittedAtUtc is not DateTime remittedAt
            || !RemittanceInclusion.Includes(remittedAt, certCountedAtUtc)) return null;
        var form = FormLabel(formType);
        return $"หนังสือรับรองหัก ณ ที่จ่าย {certNumber} อยู่ในการนำส่ง {form} เดือน {month:00}/{year + 543} ณ วันที่ "
            + $"{RemittanceInclusion.ThaiStamp(remittedAt)} — ยอดของใบนี้ชำระกรมสรรพากรแล้วและผู้ถูกหักถือฉบับจริงไปแล้ว ยกเลิกในระบบไม่ได้ · "
            + $"ถ้าหักผิด ให้ยื่น {form} เพิ่มเติม (หรือขอคืนตามขั้นตอนของกรมสรรพากร) แล้วบันทึกรายการปรับปรุงในงวดปัจจุบัน · "
            + "ถ้าบันทึกการนำส่งงวดนี้ผิด ให้ผู้ดูแลระบบยกเลิกรายการนำส่งนั้นก่อน (ระบบยังไม่มีปุ่มยกเลิกการนำส่งบนหน้าจอ)";
    }

    /// <summary>ตรวจหนังสือรับรองชุดหนึ่ง (ของบริษัทนี้) — คืนเหตุผลข้อแรกที่ห้ามยกเลิก หรือ null</summary>
    public static async Task<string?> CheckAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> certIds,
        CancellationToken ct = default)
    {
        if (certIds.Count == 0) return null;
        var ids = certIds.ToList();
        var certs = await db.WithholdingTaxCerts.AsNoTracking()
            .Where(w => w.CompanyId == companyId && ids.Contains(w.Id) && WhtCertFilingScope.Filed.Contains(w.Status))
            .Select(w => new { w.CertificateNumber, w.Status, w.TaxFormType, w.TaxYear, w.TaxMonth, w.IssuedDate, w.CreatedAt })
            .ToListAsync(ct);
        if (certs.Count == 0) return null;
        var years = certs.Select(c => c.TaxYear).Distinct().ToList();
        var filed = (await db.TaxReports.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted && years.Contains(t.Year)
                    && TaxFilingLockPolicy.DeclaredOrFiledStatuses.Contains(t.Status))
                .Select(t => new { t.TaxType, t.Year, t.Month }).ToListAsync(ct))
            .Select(t => (t.TaxType, t.Year, t.Month)).ToHashSet();
        // รอบ 201 ฝ่ายค้าน PR2 (P1-a): บันทึกการนำส่ง ภ.ง.ด. ของงวด (StatutoryRemittance) = ยื่นแบบพร้อมชำระแล้ว — หลักฐานชุดเดียวกับ
        // ด่านยกเลิก/แก้ยอดรอบเงินเดือน (เดิมเห็นแต่รายงานภาษีที่ประกาศว่ายื่น ⇒ นำส่งแล้วแต่ไม่ได้ประกาศ = ยกเลิก 50 ทวิ ได้)
        // ★ ฝ่ายค้านรอบสาม (P1-1): นับเฉพาะใบที่ออกก่อนเวลาบันทึกนำส่ง (RemittanceInclusion) · (P2-5) รวม ภ.ง.ด.54
        var remitted = await db.StatutoryRemittances.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && years.Contains(r.PeriodYear)
                && (r.RemittanceType == "WhtPnd1" || r.RemittanceType == "WhtPnd3" || r.RemittanceType == "WhtPnd53"
                    || r.RemittanceType == "WhtPnd54"))
            .Select(r => new { r.RemittanceType, r.PeriodYear, r.PeriodMonth, r.CreatedAt })
            .ToListAsync(ct);
        var remittedAt = RemittanceInclusion.LatestByPeriod(remitted
            .Where(r => RemittanceForm(r.RemittanceType) != null)
            .Select(r => ((RemittanceForm(r.RemittanceType)!.Value, r.PeriodYear, r.PeriodMonth), r.CreatedAt)));
        foreach (var c in certs)
        {
            DateTime? periodRemittedAt = remittedAt.TryGetValue((c.TaxFormType, c.TaxYear, c.TaxMonth), out var at) ? (DateTime?)at : null;
            if (Reason(c.Status, c.CertificateNumber, c.TaxFormType, c.TaxYear, c.TaxMonth,
                    filed.Contains((c.TaxFormType, c.TaxYear, c.TaxMonth)),
                    periodRemittedAt, RemittanceInclusion.CertCountedAt(c.IssuedDate, c.CreatedAt)) is string why)
                return why;
        }
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

    /// <summary>ชนิดการนำส่ง → แบบ ภ.ง.ด. ของ 50 ทวิ (null = ไม่ใช่การนำส่งภาษีหัก ณ ที่จ่ายที่มีหนังสือรับรอง)</summary>
    internal static TaxType? RemittanceForm(string? remittanceType) => remittanceType switch
    {
        "WhtPnd1" => TaxType.WithholdingTax1,
        "WhtPnd3" => TaxType.WithholdingTax3,
        "WhtPnd53" => TaxType.WithholdingTax53,
        // ฝ่ายค้านรอบสาม PR2 (P2-5): ภ.ง.ด.54 (จ่ายต่างประเทศ ม.70) มี 50 ทวิ และมีบันทึกนำส่ง — เดิมหลุด (FormLabel รองรับอยู่แล้ว)
        "WhtPnd54" => TaxType.WithholdingTax54,
        _ => null,
    };

    private static string FormLabel(TaxType t) => t switch
    {
        TaxType.WithholdingTax1 => "ภ.ง.ด.1",
        TaxType.WithholdingTax3 => "ภ.ง.ด.3",
        TaxType.WithholdingTax53 => "ภ.ง.ด.53",
        TaxType.WithholdingTax54 => "ภ.ง.ด.54",
        _ => t.ToString(),
    };
}
