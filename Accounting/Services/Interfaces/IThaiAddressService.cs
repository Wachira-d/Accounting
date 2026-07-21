namespace Accounting.Services.Interfaces;

/// <summary>
/// บริการค้นหาที่อยู่ไทย จาก open data ของกรมการปกครอง (DOPA)
/// ข้อมูล: ตำบล อำเภอ จังหวัด รหัสไปรษณีย์
/// แหล่งข้อมูล: Thailand Administrative Subdivisions (data.go.th)
/// </summary>
public interface IThaiAddressService
{
    Task<List<ThaiAddressResult>> SearchAsync(string query, int limit = 20);
    Task<List<ThaiAddressResult>> GetByPostalCodeAsync(string postalCode);
    Task<List<ThaiProvinceInfo>> GetProvincesAsync();
    Task<List<ThaiDistrictInfo>> GetDistrictsAsync(string provinceCode);
    Task<List<ThaiSubDistrictInfo>> GetSubDistrictsAsync(string districtCode);
}

public record ThaiAddressResult(
    string SubDistrictCode,
    string SubDistrictNameTh,
    string SubDistrictNameEn,
    string DistrictCode,
    string DistrictNameTh,
    string DistrictNameEn,
    string ProvinceCode,
    string ProvinceNameTh,
    string ProvinceNameEn,
    string PostalCode);

public record ThaiProvinceInfo(string Code, string NameTh, string NameEn);
public record ThaiDistrictInfo(string Code, string NameTh, string NameEn, string ProvinceCode);
public record ThaiSubDistrictInfo(string Code, string NameTh, string NameEn, string DistrictCode, string PostalCode);
