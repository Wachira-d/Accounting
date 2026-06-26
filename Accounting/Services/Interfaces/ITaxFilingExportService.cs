using Accounting.Models.DTOs.Tax;

namespace Accounting.Services.Interfaces;

public interface ITaxFilingExportService
{
    // ภ.ง.ด.1 - Monthly Salary WHT (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd1Async(Guid companyId, int year, int month);

    // ภ.ง.ด.3 - Service WHT for individuals (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd3Async(Guid companyId, int year, int month);

    // ภ.ง.ด.53 - Service WHT for companies (e-Filing text format)
    Task<TaxFilingExportResult> ExportPnd53Async(Guid companyId, int year, int month);

    // ภ.ง.ด.1ก - Annual salary summary
    Task<TaxFilingExportResult> ExportPnd1kAsync(Guid companyId, int year);

    /// <summary>ภ.ง.ด.91 — Annual personal-income summary per employee.
    /// Aggregates YTD income + WHT + SSO + PF from every payroll
    /// month. Used both for company filing AND as input data for
    /// generating each employee's 50 ทวิ certificate.</summary>
    Task<TaxFilingExportResult> ExportPnd91Async(Guid companyId, int year);

    // ภ.พ.30 - VAT filing
    Task<TaxFilingExportResult> ExportPp30Async(Guid companyId, int year, int month);

    // สปส.1-10 - SSO monthly contribution report
    Task<TaxFilingExportResult> ExportSso110Async(Guid companyId, int year, int month);

    /// <summary>สปส.1-03 — ขึ้นทะเบียนผู้ประกันตน (พนักงานเข้าใหม่ภายในเดือน
    /// นั้น). แจ้งภายใน 30 วันนับจากวันเริ่มงาน (§34).</summary>
    Task<TaxFilingExportResult> ExportSps103Async(Guid companyId, int year, int month);

    /// <summary>สปส.6-09 — แจ้งสิ้นสุดความเป็นผู้ประกันตน (พนักงานออกภายใน
    /// เดือนนั้น). แจ้งภายในวันที่ 15 ของเดือนถัดไป.</summary>
    Task<TaxFilingExportResult> ExportSps609Async(Guid companyId, int year, int month);

    /// <summary>ภ.ง.ด.2 — Monthly dividend WHT (เงินปันผล §40(4)(ข)).
    /// บริษัทจ่ายเงินปันผลให้ผู้ถือหุ้น ต้องหัก WHT 10% ส่งสรรพากร
    /// ภายในวันที่ 7 ของเดือนถัดไป. รวมจาก JournalEntries ที่ลงบัญชี
    /// "เงินปันผลค้างจ่าย" / "ภาษีหัก ณ ที่จ่าย" สำหรับ §40(4)(ข).</summary>
    Task<TaxFilingExportResult> ExportPnd2Async(Guid companyId, int year, int month);

    /// <summary>ภ.พ.36 — Foreign Service VAT self-assessment.</summary>
    Task<TaxFilingExportResult> ExportPp36Async(Guid companyId, int year, int month);

    /// <summary>ภ.ง.ด.54 — WHT จาก foreign vendor (จ่ายค่าบริการ ดอกเบี้ย
    /// ค่าสิทธิ์ ฯลฯ ไปต่างประเทศ) ผู้จ่ายในไทยต้องหัก ณ ที่จ่าย ตาม DTA
    /// (15% สำหรับประเทศไม่มีอนุสัญญา / 5-15% มี DTA). ยื่นภายในวันที่ 7
    /// ของเดือนถัดไป. รวมจาก PI/Expense ที่ IsForeignService=true + มี WHT.</summary>
    Task<TaxFilingExportResult> ExportPnd54Async(Guid companyId, int year, int month);
}
