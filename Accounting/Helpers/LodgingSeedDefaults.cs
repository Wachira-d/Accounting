using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ค่าตั้งต้นของที่พักที่ระบบ seed ให้ตอนสร้างเว็บ "โรงแรม/ที่พัก" — <b>ที่เดียว</b>
/// ให้ทั้ง <c>LodgingSeeder</c> (ของจริงที่จองได้) และ <c>CmsSiteTemplateSeeder.HotelPlan</c>
/// (ข้อความบนหน้าเว็บ) อ่าน.
///
/// ที่มา (รอบ 158): หน้าเว็บที่ seed พิมพ์ "Junior Suite ฿3,800 · 4 ประเภท · Check-in 15:00 ·
/// ยกเลิกฟรีก่อน 3 วัน · ราคารวมอาหารเช้า · จองตรงลด 10%" ขณะที่ที่พักที่จองได้จริงมี 3 ประเภท ·
/// เช็คอิน 14:00 · ยกเลิกฟรีก่อน 7 วัน · อาหารเช้าเป็นแผนราคาแยก +700 — สองที่เขียนตัวเลขเอง
/// คนละชุดตั้งแต่วันแรก (defect class "สำเนามือ = drift") แขกเห็นราคา/กติกาบนหน้าแรกแล้วไปเจอ
/// อีกอย่างตอนจอง. เทสต์ <c>LodgingSeedDefaultsTests</c> ล็อกว่าข้อความบนหน้าเว็บอ้างค่าชุดนี้เท่านั้น</summary>
public static class LodgingSeedDefaults
{
    public const string CheckIn = "14:00";
    public const string CheckOut = "12:00";
    public const int CheckInHour = 14;
    public const int CheckOutHour = 12;
    public const int DepositPercent = 50;

    /// <summary>นโยบายยืดหยุ่น (default): ฟรีก่อน N วัน · ระหว่างนั้นคิด 50% · ใกล้กว่านั้นคิด 100%</summary>
    public const int FreeCancelDaysBefore = 7;
    public const int HalfPenaltyDaysBefore = 3;
    public static readonly string CancellationSummary =
        $"ยกเลิกฟรีก่อนเข้าพัก {FreeCancelDaysBefore} วัน · {HalfPenaltyDaysBefore}–{FreeCancelDaysBefore - 1} วัน คิด 50% · น้อยกว่า {HalfPenaltyDaysBefore} วัน คิด 100%";
    public static readonly string CancellationRulesJson =
        $"[{{\"daysBefore\":{FreeCancelDaysBefore},\"penaltyPercent\":0}},{{\"daysBefore\":{HalfPenaltyDaysBefore},\"penaltyPercent\":50}},{{\"daysBefore\":0,\"penaltyPercent\":100}}]";

    /// <summary>แผนราคา: ไม่คืนเงินลด · รวมอาหารเช้า +delta · พักยาวลด</summary>
    public const int NonRefundableDiscountPercent = 10;
    public const decimal BreakfastPlanDelta = 700;
    public const int LongStayDiscountPercent = 15;
    public const int LongStayMinNights = 7;

    /// <summary>บริการเสริม</summary>
    public const decimal BreakfastPerPersonPerNight = 350;
    public const decimal ExtraBedPerNight = 800;
    public const decimal AirportTransferPerStay = 800;
    public const decimal LateCheckoutPerStay = 500;
    public const string LateCheckoutUntil = "16:00";
    public const decimal ExtraGuestPrice = 500;

    public readonly record struct RoomTypeDefault(
        string Name, string Code, decimal Rate, decimal SizeSqm, string Bed,
        int StandardOccupancy, int MaxAdults, int MaxChildren, int MaxOccupancy,
        bool ExtraBed, string Description, string[] Units, string Floor);

    /// <summary>ประเภทห้อง 3 แบบ · 11 ห้อง — ลำดับ = ลำดับแสดงบนหน้าเว็บ</summary>
    public static readonly RoomTypeDefault[] RoomTypes =
    {
        new("Standard Room", "STD", 1500, 25, "เตียง 6 ฟุต", 2, 2, 1, 3, false,
            "25 ตรม. · เตียง 6 ฟุต · ห้องน้ำในตัว · WiFi · TV · มินิบาร์",
            new[] { "101", "102", "103", "104", "105" }, "1"),
        new("Deluxe Room", "DLX", 2500, 32, "King size", 2, 2, 2, 4, true,
            "32 ตรม. · King size · วิวเมือง · เครื่องชงกาแฟ · อ่างอาบน้ำ",
            new[] { "201", "202", "203", "204" }, "2"),
        new("Suite", "SUI", 4500, 65, "King size + โซฟาเบด", 2, 3, 2, 5, true,
            $"65 ตรม. · ห้องนั่งเล่นแยก · มินิบาร์ฟรี · Late checkout {LateCheckoutUntil}",
            new[] { "301", "302" }, "3"),
    };

    public static int TotalUnits => RoomTypes.Sum(r => r.Units.Length);

    /// <summary>"฿1,500" — ตัวเดียวที่แปลงตัวเลขเป็นข้อความบนหน้าเว็บ (InvariantCulture กัน th-TH ปฏิทิน/ตัวเลข)</summary>
    public static string Baht(decimal v) => "฿" + v.ToString("N0", CultureInfo.InvariantCulture);
}
