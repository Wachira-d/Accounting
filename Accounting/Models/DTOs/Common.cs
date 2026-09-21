namespace Accounting.Models.DTOs;

public record ApiResponse<T>(bool Success, T? Data, string? Message = null, List<string>? Errors = null);

/// <summary>เนื้อของ <c>Data</c> เมื่อ request ไม่ผ่าน model validation
///
/// <para><c>Fields</c> = ชื่อช่องแบบ camelCase ที่ตรงกับ <c>name="..."</c> บนฟอร์ม —
/// หน้าเว็บเอาไปหา <c>[name=...]</c> แล้วอ่าน**ป้ายไทยจริงจาก DOM ของตัวเอง** ·
/// เซิร์ฟเวอร์ไม่เก็บสำเนาป้าย (F2 ข้อ 5 "Server computes · page displays")</para>
///
/// <para><c>Messages</c> เรียงตรงกับ <c>Fields</c> ตัวต่อตัว — สร้างโดย
/// <c>Helpers/ValidationErrorText.Describe</c> ที่เดียว</para></summary>
public record ValidationErrorData(List<string> Fields, List<string> Messages);

public record PagedRequest(int Page = 1, int PageSize = 20, string? Search = null, string? SortBy = null, bool SortDesc = false)
{
    public int Page { get; init; } = Math.Max(Page, 1);
    public int PageSize { get; init; } = Math.Clamp(PageSize, 1, 200);
}

public record PagedResponse<T>(List<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);
