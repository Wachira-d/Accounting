using System;
using System.Linq.Expressions;
using Accounting.Models.Entities;

namespace Accounting.Helpers;

/// <summary>
/// ตัวกรอง "รายงานภาษีใบนี้ของบริษัทนี้" — กฎ M (tenant isolation) ของทางอ่านรายงานภาษีด้วย id จาก URL
///
/// <para>═══ ที่มา (ผลตรวจ B-03 · รอบ 193) ═══ <c>TaxComplianceChecker.CheckAsync</c> เคยค้น
/// <c>r.Id == taxReportId</c> ล้วน ๆ ⇒ ผู้ใช้บริษัท A ใส่ reportId ของบริษัท B ใน
/// <c>GET .../tax/{reportId}/precheck</c> แล้วได้ OutputVat/InputVat/NetVat/TotalTaxWithheld ของบริษัท B
/// กลับมาในข้อความ finding · global query filter ของเรพนี้กรองแค่ <c>IsDeleted</c> จึงไม่มีอะไรกั้น</para>
///
/// <para>เป็น expression (ไม่ใช่ lambda ในที่) เพื่อให้เทสต์พิสูจน์ได้ทั้งสองทิศโดยไม่ต้องมีฐานข้อมูล:
/// id ตรง + บริษัทตรง = เจอ · id ตรง + บริษัทอื่น = ไม่เจอ</para>
/// </summary>
public static class TaxReportTenantScope
{
    public static Expression<Func<TaxReport, bool>> ById(Guid companyId, Guid reportId)
        => r => r.Id == reportId && r.CompanyId == companyId;
}
