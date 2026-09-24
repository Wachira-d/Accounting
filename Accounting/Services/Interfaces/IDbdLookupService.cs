namespace Accounting.Services.Interfaces;

public interface IDbdLookupService
{
    /// <summary>ค้นหานิติบุคคลจากชื่อ (autocomplete)</summary>
    Task<List<DbdCompanyResult>> SearchByNameAsync(string query, int limit = 10);

    /// <summary>ดึงข้อมูลนิติบุคคลจากเลขทะเบียน 13 หลัก</summary>
    Task<DbdCompanyResult?> GetByJuristicIdAsync(string juristicId);

    /// <summary>ตรวจสอบเลขผู้เสียภาษี (กรมสรรพากร TIN Check)</summary>
    Task<TinCheckResult> VerifyTinAsync(string tin);

    /// <summary>ข้อมูลของ <b>สาขาที่ระบุ</b> (ชื่อ + ที่อยู่ของสาขานั้น) จากทะเบียนผู้ประกอบการ VAT ของ
    /// กรมสรรพากร — สำนักงานใหญ่/ว่าง = เหมือน <see cref="GetByJuristicIdAsync"/> ·
    /// คืน null เมื่อทะเบียนไม่ยืนยันว่ามีสาขานั้น ("ไม่รู้" ห้ามคืนที่อยู่สำนักงานใหญ่แทน)</summary>
    Task<DbdCompanyResult?> GetBranchAsync(string juristicId, string? branchCode);
}

public record DbdCompanyResult(
    string JuristicId,          // เลขทะเบียนนิติบุคคล 13 หลัก
    string NameTh,              // ชื่อนิติบุคคล (ไทย)
    string? NameEn,             // ชื่อนิติบุคคล (อังกฤษ)
    string? JuristicType,       // ประเภทนิติบุคคล
    string? Status,             // สถานะ
    decimal? RegisteredCapital, // ทุนจดทะเบียน (บาท)
    string? Address,            // ที่ตั้งสำนักงานใหญ่
    string? RegisterDate,       // วันที่จดทะเบียน
    string? Objective,           // วัตถุประสงค์
    // สาขาที่ข้อมูลชุดนี้เป็นของ (รหัส 5 หลัก) — null = ไม่ได้ระบุ (ผลค้นแบบเดิม ≈ สำนักงานใหญ่)
    string? BranchCode = null,
    string? BranchName = null
);

public record TinCheckResult(
    bool IsValid,       // เลขถูกต้องหรือไม่
    bool IsExist,       // มีในระบบหรือไม่
    string Tin           // เลขที่ตรวจสอบ
);
