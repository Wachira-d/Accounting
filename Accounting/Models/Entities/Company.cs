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
    public IndustryType IndustryType { get; set; } = IndustryType.General;  // ลักษณะธุรกิจ
    public CompanyStatus Status { get; set; } = CompanyStatus.Active;
    /// <summary>เหตุผลที่ admin ระงับบริษัท (CompanyStatus.Suspended) — โชว์ใน 403
    /// ให้ผู้ใช้รู้ว่าทำไมใช้งานไม่ได้ + ติดต่อใคร. null เมื่อ Active.</summary>
    public string? SuspendReason { get; set; }
    /// <summary>เวลาที่ถูกระงับ (UTC) — audit + แสดงใน admin.</summary>
    public DateTime? SuspendedAt { get; set; }

    // ===== โครงลูกค้า/กลุ่มบริษัท (ACCOUNT_STRUCTURE.md §1) =====
    // ทั้ง 3 field เป็น optional ล้วน — บริษัทเดิมที่ไม่ได้อยู่กลุ่มไหน
    // ทำงานเหมือนเดิมทุกประการ ไม่มี behavior change

    /// <summary>กลุ่ม/องค์กรผู้จ่ายเงินที่บริษัทนี้สังกัด. null = จ่ายเอง
    /// (subscription ของตัวเอง หรือ trial) — เส้นทางเดิมของระบบ.
    ///
    /// **สำคัญ**: เป็นความสัมพันธ์เรื่อง *เงิน* เท่านั้น ห้ามใช้เป็นทางลัด
    /// query ข้ามบริษัท — tenant isolation ยังอยู่ที่ CompanyId ทุก query</summary>
    public Guid? BillingAccountId { get; set; }
    public BillingAccount? BillingAccount { get; set; }

    /// <summary>บริษัทแม่ในผังเครือ (holding). null = ไม่มีแม่ / เป็นบริษัทแม่เอง.
    ///
    /// ใช้ **แสดงผังและอ้างอิงตอนทำงบรวมเท่านั้น** — ไม่มีผลต่อสิทธิ์, โควตา,
    /// หรือการมองเห็นข้อมูลใด ๆ ทั้งสิ้น. กลุ่มบริษัทพี่น้องแนวขนาน (เจ้าของ
    /// เดียวกันแต่ไม่มีใครถือหุ้นใคร) ปล่อย null ได้ — billing เหมือนกันเป๊ะ.
    /// สัดส่วนถือหุ้น/วิธีรวมงบอยู่ที่ <see cref="ConsolidationMember"/></summary>
    public Guid? ParentCompanyId { get; set; }
    public Company? ParentCompany { get; set; }

    /// <summary>ใช้ระบบผ่านหน้าเว็บ (Full) หรือเชื่อม API จาก ERP อื่น (Connected).
    /// default Full = พฤติกรรมเดิมของทุกบริษัทที่มีอยู่</summary>
    public CompanyKind CompanyKind { get; set; } = CompanyKind.Full;

    // Thai Business Registration (ข้อมูลตามกฎหมายไทย)
    public string? JuristicId { get; set; }              // เลขทะเบียนนิติบุคคล (DBD)
    public bool IsVatRegistered { get; set; }            // จดทะเบียนภาษีมูลค่าเพิ่ม
    public decimal VatRate { get; set; } = 7m;           // อัตราภาษีมูลค่าเพิ่ม (%)
    /// <summary>ทุนชำระแล้ว (registered paid-up capital) — ใช้คำนวณเพดาน
    /// ค่ารับรอง §65 ทวิ (4): 0.3% ของรายได้ หรือ 0.3% ของทุนชำระแล้ว
    /// แล้วแต่อย่างไหนสูงกว่า แต่ไม่เกิน 10 ล้านบาท. Default 0 = ใช้แค่
    /// เพดาน revenue-based.</summary>
    public decimal PaidUpCapital { get; set; }
    public bool IsWhtRegistered { get; set; } = true;    // หัก ณ ที่จ่าย
    public bool IsSocialSecurityRegistered { get; set; } // จดทะเบียนประกันสังคม
    public string? SocialSecurityAccountNo { get; set; } // เลขที่บัญชีประกันสังคม

    // Address (ที่อยู่ตามใบทะเบียน) — structured per ETDA Schematron
    public string? Address { get; set; }                  // free-text fallback / display
    public string? BuildingNumber { get; set; }           // บ้านเลขที่ (required by ETDA)
    public string? BuildingName { get; set; }             // ชื่ออาคาร
    public string? Moo { get; set; }                      // หมู่ที่ (provincial / rural addresses)
    public string? StreetName { get; set; }               // ถนน/ซอย
    public string? SubDistrict { get; set; }              // แขวง/ตำบล
    public string? District { get; set; }                 // เขต/อำเภอ
    public string? Province { get; set; }                 // จังหวัด
    public string? PostalCode { get; set; }               // รหัสไปรษณีย์ 5 digits
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
