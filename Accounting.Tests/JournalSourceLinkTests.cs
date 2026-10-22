using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ทีม M 2026-10-22 — <see cref="JournalSourceLink"/>: ใบสำคัญ → เอกสารต้นทาง · ไม่แต่งลิงก์เมื่อไม่มีเอกสาร · ใบที่ซ่อนไม่เปิดเผยชนิดต้นทาง
/// </summary>
public class JournalSourceLinkTests
{
    private static JournalSourceFacts F(bool hidden = false, Guid? srcDoc = null, Guid? docId = null, string? docNo = null, string? docType = null,
        Guid? pos = null, string? posNo = null, Guid? run = null, string? runLabel = null, Guid? asset = null, string? assetLabel = null,
        Guid? claim = null, string? claimNo = null, Guid? orig = null, bool auto = false)
        => new(hidden, srcDoc, docId, docNo, docType, pos, posNo, run, runLabel, asset, assetLabel, claim, claimNo, orig, auto);

    [Fact]
    public void เอกสาร_ลิงก์เปิดเอกสาร_พร้อมชื่อชนิดและเลขที่()
    {
        var id = Guid.NewGuid();
        var s = JournalSourceLink.Resolve(F(srcDoc: id, docId: id, docNo: "INV-001", docType: "ใบแจ้งหนี้"));
        Assert.Equal("Document", s.Kind);
        Assert.Equal("ใบแจ้งหนี้ INV-001", s.Label);
        Assert.Equal("/pages/documents.html?openDoc=" + id, s.Href);
        Assert.False(JournalSourceLink.IsWithoutSource(s.Kind));
    }

    [Fact]
    public void แหล่งที่ผูกย้อนกลับ_POS_เงินเดือน_ค่าเสื่อม_ใบเบิก_มีลิงก์ไปหน้าของตัวเอง()
    {
        var id = Guid.NewGuid();
        Assert.Equal(("PosOrder", "/pages/pos-reports.html?orderId=" + id), K(JournalSourceLink.Resolve(F(pos: id, posNo: "POS-1"))));
        Assert.Equal(("Payroll", "/pages/payroll.html?runId=" + id), K(JournalSourceLink.Resolve(F(run: id, runLabel: "PR-1"))));
        Assert.Equal(("Depreciation", "/pages/fixed-assets.html?id=" + id), K(JournalSourceLink.Resolve(F(asset: id, assetLabel: "FA-1"))));
        Assert.Equal(("ExpenseClaim", "/pages/expense.html?claimId=" + id), K(JournalSourceLink.Resolve(F(claim: id, claimNo: "EC-1"))));
        Assert.Equal("บิล POS POS-1", JournalSourceLink.Resolve(F(pos: id, posNo: "POS-1")).Label);
    }

    private static (string, string?) K(Accounting.Models.DTOs.Accounting.PnlSourceLink s) => (s.Kind, s.Href);

    [Fact]
    public void ซ่อนตามสิทธิ์ชนะทุกอย่าง_ไม่มีลิงก์ไม่บอกชนิด()
    {
        var id = Guid.NewGuid();
        var s = JournalSourceLink.Resolve(F(hidden: true, srcDoc: id, docId: id, docNo: "X", run: id, runLabel: "เงินเดือน"));
        Assert.Equal("Hidden", s.Kind);
        Assert.Null(s.Href);
        Assert.DoesNotContain("เงินเดือน", s.Label);
    }

    [Fact]
    public void ไม่มีเอกสาร_แสดงตามจริง_ไม่แต่งลิงก์()
    {
        var manual = JournalSourceLink.Resolve(F());
        Assert.Equal("Manual", manual.Kind);
        Assert.Null(manual.Href);
        Assert.Contains("ไม่มีเอกสารต้นทาง", manual.Label);
        Assert.True(JournalSourceLink.IsWithoutSource(manual.Kind));

        var auto = JournalSourceLink.Resolve(F(auto: true));
        Assert.Equal("AutoNoSource", auto.Kind);
        Assert.Null(auto.Href);
        Assert.True(JournalSourceLink.IsWithoutSource(auto.Kind));

        // อ้างเอกสารที่หาไม่พบ (ลบ/คนละบริษัท) — ห้ามสร้างลิงก์ไปเอกสารที่เปิดไม่ได้
        var missing = JournalSourceLink.Resolve(F(srcDoc: Guid.NewGuid()));
        Assert.Equal("MissingDocument", missing.Kind);
        Assert.Null(missing.Href);
        Assert.True(JournalSourceLink.IsWithoutSource(missing.Kind));
    }

    [Fact]
    public void กลับรายการ_ชี้ใบสำคัญเดิม()
    {
        var orig = Guid.NewGuid();
        var s = JournalSourceLink.Resolve(F(orig: orig, auto: true));
        Assert.Equal("Reversal", s.Kind);
        Assert.Equal(JournalSourceLink.JournalHref(orig), s.Href);
        Assert.False(JournalSourceLink.IsWithoutSource(s.Kind));
    }
}
