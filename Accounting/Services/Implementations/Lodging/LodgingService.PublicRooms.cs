using Accounting.Helpers;
using Accounting.Models.DTOs.Lodging;

namespace Accounting.Services.Implementations.Lodging;

/// <summary>ป้ายการ์ดประเภทห้องของหน้าเว็บสาธารณะ (รอบ 202 ทีม LW · ฝ่ายค้าน P2-1/P2-2) — แยกไฟล์จาก Reservations.cs (ทีม LO ดูแล)</summary>
public partial class LodgingService
{
    /// <summary>เติม <c>FromRate/FromRateLabel/CapacityLabel</c> ของทุกประเภทห้องใน <paramref name="info"/> — ราคาเริ่มต้นผ่าน engine ตัวเดียวกับ
    /// ใบเสนอราคา (แผนตั้งต้นของห้อง · ช่วง <see cref="LodgingPublicRoomLabels.FromRateWindowDays"/> วันนับจากวันนี้เวลาไทย)</summary>
    public async Task ApplyPublicRoomLabelsAsync(Guid companyId, LodgingPublicInfo info)
    {
        if (info.RoomTypes.Count == 0) return;
        var firstNight = ThaiDate.CalendarDateUtc(DateTime.UtcNow);
        var ctx = await LoadContextAsync(companyId, info.PropertyId, firstNight, firstNight.AddDays(LodgingPublicRoomLabels.FromRateWindowDays));
        foreach (var dto in info.RoomTypes)
        {
            var rt = ctx.RoomTypes.FirstOrDefault(x => x.Id == dto.Id);
            if (rt == null) continue;
            var plan = PickRatePlan(ctx, null, rt, firstNight, 1, isStaff: false);
            var rate = LodgingPublicRoomLabels.FromRate(ctx.InputFor(rt, plan), firstNight, LodgingPublicRoomLabels.FromRateWindowDays);
            dto.FromRate = rate;
            dto.FromRateLabel = LodgingPublicRoomLabels.FromRateLabel(rate, rt.PricingMode, info.PricesIncludeVat, info.VatRate);
            dto.CapacityLabel = LodgingPublicRoomLabels.CapacityLabel(rt.MaxAdults, rt.AllowExtraBed, rt.MaxExtraBeds, rt.ExtraBedPrice);
        }
    }
}
