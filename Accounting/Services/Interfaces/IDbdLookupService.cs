namespace Accounting.Services.Interfaces;

public interface IDbdLookupService
{
    /// <summary>ค้นหานิติบุคคลจากชื่อ (autocomplete)</summary>
    Task<List<DbdCompanyResult>> SearchByNameAsync(string query, int limit = 10);

    /// <summary>ดึงข้อมูลนิติบุคคลจากเลขทะเบียน 13 หลัก</summary>
    Task<DbdCompanyResult?> GetByJuristicIdAsync(string juristicId);

    /// <summary>ตรวจสอบเลขผู้เสียภาษี (กรมสรรพากร TIN Check)</summary>
    Task<TinCheckResult> VerifyTinAsync(string tin);
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
    string? Objective            // วัตถุประสงค์
);

public record TinCheckResult(
    bool IsValid,       // เลขถูกต้องหรือไม่
    bool IsExist,       // มีในระบบหรือไม่
    string Tin           // เลขที่ตรวจสอบ
);
