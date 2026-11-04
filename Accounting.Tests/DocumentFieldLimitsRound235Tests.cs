using Accounting.Helpers;
using Accounting.Middleware;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 — ผู้ใช้รายงาน 2026-11-04 (REF:42DAB82F): สร้างเอกสารแล้วได้ 500 <c>22001 value too long for type character varying(100)</c> ·
/// ล็อก (1) ตัวตั้งความยาวเดียว <see cref="DocumentFieldLimits"/> บอกชื่อช่องเป็นไทย (400) · (2) ขอบพอดีผ่าน · (3) 22001 ที่หลุดถึง DB = 400 ไม่ใช่ 500
/// </summary>
public class DocumentFieldLimitsRound235Tests
{
    [Fact]
    public void อ้างอิงมัดจำหลายใบ_ยาวเกิน100เดิม_ตอนนี้บันทึกได้()
    {
        // ทางที่เกิดจริง: หน้าเว็บต่อเลขมัดจำหลายใบด้วย ", " (หรือ id 36 ตัวเมื่อหาเลขใบไม่เจอ) — 3 id = 112 ตัว > 100 เดิม
        var refs = string.Join(", ", Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString()));
        Assert.True(refs.Length > 100);
        DocumentFieldLimits.Require(refs, DocumentFieldLimits.DepositAppliedRef, "อ้างอิงมัดจำที่หัก");
    }

    [Fact]
    public void ขอบพอดีผ่าน_เกินหนึ่งตัวถูกปฏิเสธพร้อมชื่อช่อง()
    {
        DocumentFieldLimits.Require(new string('x', DocumentFieldLimits.SupplierInvoiceNumber), DocumentFieldLimits.SupplierInvoiceNumber, "เลขใบกำกับของผู้ขาย");
        DocumentFieldLimits.Require(null, DocumentFieldLimits.SupplierInvoiceNumber, "เลขใบกำกับของผู้ขาย");
        var ex = Assert.Throws<BusinessRuleException>(() => DocumentFieldLimits.Require(
            new string('x', DocumentFieldLimits.SupplierInvoiceNumber + 1), DocumentFieldLimits.SupplierInvoiceNumber, "เลขใบกำกับของผู้ขาย"));
        Assert.Contains("เลขใบกำกับของผู้ขาย", ex.Message);
        Assert.Equal(DocumentFieldLimits.RuleCode, ex.RuleCode);
        Assert.Equal(400, ExceptionMiddleware.StatusFor(ex));
    }

    [Fact]
    public void ข้อความยาวเกินคอลัมน์ที่หลุดถึงฐานข้อมูล_ได้400_ไม่ใช่500()
    {
        var pg = new PostgresException("value too long for type character varying(100)", "ERROR", "ERROR", "22001");
        Assert.Equal(400, ExceptionMiddleware.StatusFor(new DbUpdateException("save", pg)));
    }

    [Fact]
    public void ทิศตรงข้าม_ข้อผิดพลาดฐานข้อมูลอื่นยังเป็น500()
    {
        var pg = new PostgresException("division by zero", "ERROR", "ERROR", "22012");
        Assert.Equal(500, ExceptionMiddleware.StatusFor(new DbUpdateException("save", pg)));
    }
}
