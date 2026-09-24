using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **pre-check รายงานภาษีต้องไม่อ่านรายงานของบริษัทอื่น** (ผลตรวจ B-03 · รอบ 193)
///
/// <para>บั๊กที่ล็อกไว้: <c>TaxComplianceChecker.CheckAsync</c> ค้นด้วย <c>r.Id == taxReportId</c> อย่างเดียว ⇒ ผู้ใช้บริษัท A
/// ใส่ reportId ของบริษัท B ใน <c>GET .../tax/{reportId}/precheck</c> แล้วได้ยอด VAT/WHT ของ B · ตอนนี้ค้นผ่าน
/// <see cref="TaxReportTenantScope.ById"/> ตัวเดียว — เทสต์ประเมิน expression เดียวกับที่ EF แปลเป็น SQL</para>
///
/// <para>ครึ่งหลัง = ทิศตรงข้าม: รายงานของบริษัทตัวเองต้อง<b>ยังเจอ</b> (ด่านที่ตอบ "ไม่พบ" ทุกใบก็ผ่านครึ่งแรกได้)</para>
/// </summary>
public class TaxReportTenantScopeTests
{
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    private static List<TaxReport> Reports(out TaxReport ofA, out TaxReport ofB)
    {
        ofA = new TaxReport { Id = Guid.NewGuid(), CompanyId = CompanyA };
        ofB = new TaxReport { Id = Guid.NewGuid(), CompanyId = CompanyB };
        return new List<TaxReport> { ofA, ofB };
    }

    [Fact]
    public void reportId_ของบริษัทอื่น_ต้องไม่เจอ()
    {
        var all = Reports(out _, out var ofB);
        var hit = all.AsQueryable().FirstOrDefault(TaxReportTenantScope.ById(CompanyA, ofB.Id));
        Assert.Null(hit);
    }

    [Fact]
    public void reportId_ที่ไม่มีอยู่จริง_ไม่เจอ()
    {
        var all = Reports(out _, out _);
        Assert.Null(all.AsQueryable().FirstOrDefault(TaxReportTenantScope.ById(CompanyA, Guid.NewGuid())));
    }

    // ── ทิศตรงข้าม ──

    [Fact]
    public void reportId_ของบริษัทตัวเอง_ยังเจอ()
    {
        var all = Reports(out var ofA, out var ofB);
        Assert.Same(ofA, all.AsQueryable().FirstOrDefault(TaxReportTenantScope.ById(CompanyA, ofA.Id)));
        Assert.Same(ofB, all.AsQueryable().FirstOrDefault(TaxReportTenantScope.ById(CompanyB, ofB.Id)));
    }
}
