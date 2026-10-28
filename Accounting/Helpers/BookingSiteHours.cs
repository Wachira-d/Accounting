using System.Text.Json;
using System.Text.Json.Serialization;
using Accounting.Models.DTOs.Cms;

namespace Accounting.Helpers;

/// <summary>
/// เวลาทำการระดับเว็บ (รอบ 205 W3 · R-14 · B13) เก็บเป็น JSON รูปเดียวกับฟอร์ม (<see cref="BookingScheduleDto"/>) ในคอลัมน์
/// <c>SiteBookingSettings.BookingHoursJson</c> — ตัวแปลงเดียว (เขียน/อ่าน) · JSON เสีย/ว่าง ⇒ ตารางว่าง (ไม่โยน — ค่าตั้งที่อ่านไม่ได้ต้องไม่ล้มหน้าเวลาว่าง
/// ของทุกบริการ · บริการที่ตั้งเวลาเองไม่กระทบอยู่แล้ว) · ตัวเลือก "ใช้ตารางไหน" อยู่ที่ <see cref="BookingSlotEngine.ScheduleOf"/>
/// </summary>
public static class BookingSiteHours
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>DTO → JSON · ตารางว่าง (ไม่มีทั้งรายสัปดาห์และวันพิเศษ) ⇒ null (= เว็บไม่ตั้งเวลาทำการ)</summary>
    public static string? Serialize(BookingScheduleDto? schedule)
    {
        if (schedule == null) return null;
        if ((schedule.Weekly?.Count ?? 0) == 0 && (schedule.Exceptions?.Count ?? 0) == 0) return null;
        return JsonSerializer.Serialize(schedule, Options);
    }

    /// <summary>JSON → DTO · null/ว่าง/เสีย ⇒ DTO ว่าง</summary>
    public static BookingScheduleDto Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BookingScheduleDto();
        try { return JsonSerializer.Deserialize<BookingScheduleDto>(json, Options) ?? new BookingScheduleDto(); }
        catch (JsonException) { return new BookingScheduleDto(); }
    }
}
