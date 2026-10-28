using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>กติกาตัวเดียว</b>ของรายการ folio เมื่อการจองถูกยกเลิก/ไม่มา (ทะเบียนงานคงค้าง 2026-10-26 R-51 · เปิดให้เพิ่ม folio ก่อนเช็คอินได้)
/// <list type="bullet">
/// <item>folio ที่<b>ยังไม่ออกใบ</b> (<see cref="LodgingChargeStatus.Pending"/>) บนการจองที่ยกเลิก/ไม่มา = <b>ล้าง</b> (ตั้ง Cancelled + audit) —
///   แขกไม่ได้รับบริการนั้น ห้ามค้างเป็นยอดที่ไม่มีใบและไม่มีใครเรียกเก็บ</item>
/// <item>folio ที่<b>ออกใบแล้ว</b> (<see cref="LodgingChargeStatus.Paid"/>) ไม่แตะ — ยอดอยู่ในใบกำกับแล้ว ลด = ใบลดหนี้อ้างใบเดิม (คำตัดสินข้อ 157)</item>
/// <item>แขกที่<b>เช็คอินแล้ว</b> ยกเลิกไม่ได้ (ต้องเช็คเอาต์) ⇒ folio ของ CheckedIn ไม่ถูกล้างจากเส้นนี้เสมอ (ทิศตรงข้าม)</item>
/// </list>
/// </summary>
public static class LodgingFolioOnCancel
{
    public const string RuleCode = "LODGING-FOLIO-ON-CANCEL";

    /// <summary>folio ที่ต้องล้างเมื่อการจองสถานะ <paramref name="statusBefore"/> กำลังถูกยกเลิก/ไม่มา — เฉพาะ Pending · CheckedIn = ว่างเสมอ</summary>
    public static IReadOnlyList<Guid> ToVoid(LodgingReservationStatus statusBefore,
        IEnumerable<(Guid Id, LodgingChargeStatus Status)> charges)
    {
        if (statusBefore is LodgingReservationStatus.CheckedIn or LodgingReservationStatus.CheckedOut) return Array.Empty<Guid>();
        return charges.Where(c => c.Status == LodgingChargeStatus.Pending).Select(c => c.Id).ToList();
    }

    /// <summary>หมายเหตุภายในเมื่อล้าง (ไม่เรียกเมื่อ count = 0)</summary>
    public static string Note(int count, decimal total, bool noShow)
        => $"ล้างรายการ folio ที่ยังไม่ออกใบ {count} รายการ รวม {total:N2} เพราะ{(noShow ? "ไม่มาเข้าพัก" : "ยกเลิกการจอง")} "
         + "(รายการที่ออกใบแล้วไม่ถูกแตะ — ลดด้วยใบลดหนี้อ้างใบเดิม)";
}
