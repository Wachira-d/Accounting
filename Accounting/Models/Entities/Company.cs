using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class Company : BaseEntity
{
    public string Name { get; set; } = null!;
    public string? NameEn { get; set; }
    public string TaxId { get; set; } = null!;          // เลขผู้เสียภาษี 13 หลัก
    public string? BranchCode { get; set; } = "00000";  // รหัสสาขา (00000 = สำนักงานใหญ่)
    public string? BranchName { get; set; }              // ชื่อสาขา
    public BusinessType BusinessType { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.Active;

    // Thai Business Registration (ข้อมูลตามกฎหมายไทย)
    public string? JuristicId { get; set; }              // เลขทะเบียนนิติบุคคล (DBD)
    public bool IsVatRegistered { get; set; }            // จดทะเบียนภาษีมูลค่าเพิ่ม
    public decimal VatRate { get; set; } = 7m;           // อัตราภาษีมูลค่าเพิ่ม (%)
    public bool IsWhtRegistered { get; set; } = true;    // หัก ณ ที่จ่าย
    public bool IsSocialSecurityRegistered { get; set; } // จดทะเบียนประกันสังคม
    public string? SocialSecurityAccountNo { get; set; } // เลขที่บัญชีประกันสังคม

    // Address (ที่อยู่ตามใบทะเบียน)
    public string? Address { get; set; }
    public string? SubDistrict { get; set; }             // แขวง/ตำบล
    public string? District { get; set; }                // เขต/อำเภอ
    public string? Province { get; set; }                // จังหวัด
    public string? PostalCode { get; set; }              // รหัสไปรษณีย์
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    // Settings
    public int FiscalYearStartMonth { get; set; } = 1;  // เดือนเริ่มต้นปีบัญชี
    public string BaseCurrency { get; set; } = "THB";
    public bool IsSetupComplete { get; set; }            // ตั้งค่าเสร็จแล้วหรือยัง

    // Navigation
    public ICollection<CompanyUser> CompanyUsers { get; set; } = new List<CompanyUser>();
    public Subscription? Subscription { get; set; }
    public ICollection<ChartOfAccount> ChartOfAccounts { get; set; } = new List<ChartOfAccount>();
    public ICollection<FiscalPeriod> FiscalPeriods { get; set; } = new List<FiscalPeriod>();
    public ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
}
