using System.Net;
using System.Text;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **หลักฐานการจอง (Booking Voucher) — ตัวประกอบ HTML ล้วน** (LDG-P1-04)
///
/// ═══ ที่มา ═══
/// <para>หน้าการจองของแขกเดิมมีแค่ปุ่ม "กลับหน้าหลัก" กับ "ยกเลิกการจอง" —
/// <b>ไม่มีอะไรให้โหลดเก็บหรือพิมพ์เลย</b> ทั้งที่มาตรฐานของที่พักทุกเจ้าคือแขก
/// ต้องมีหลักฐานติดตัวไปแสดงตอนเช็คอิน (และใช้ยื่นวีซ่า/เบิกบริษัทได้)</para>
///
/// <para><b>⚠️ นี่ไม่ใช่เอกสารภาษี</b> — ห้ามมีคำว่า "ใบกำกับภาษี" หรือ
/// "ใบเสร็จรับเงิน" บนหัวเด็ดขาด. ใบเสร็จมัดจำและใบกำกับตอนเช็คเอาต์เป็น
/// <b>คนละใบ</b> และออกผ่าน <c>IDocumentService</c> ตามเลข gap-free §86/4
/// อยู่แล้ว · ถ้าเอกสารนี้ดูเหมือนใบกำกับ ลูกค้าจะเอาไปใช้เคลมภาษีซื้อแล้วผิด</para>
///
/// <para>เป็น <b>ตรรกะล้วน</b> (ไม่แตะ DB/ไฟล์) เพื่อให้เทสต์ยืนยันได้ว่ายอดบน
/// กระดาษตรงกับยอดในระบบ และไม่มีคำต้องห้ามหลุดขึ้นหัว</para>
///
/// <para>ทุกค่าที่มาจากผู้ใช้/แขกผ่าน <see cref="WebUtility.HtmlEncode"/> —
/// เอกสารนี้ถูก render ด้วย Chromium ตัวเดียวกับ PDF อื่น ⇒ script ที่หลุดเข้าไป
/// รันได้จริง (กฎเหล็ก #4 C)</para>
/// </summary>
public static class LodgingVoucherBuilder
{
    /// <summary>คำที่ห้ามปรากฏบนหัวเอกสารนี้ — ใช้ในเทสต์เป็นด่านถาวร</summary>
    public static readonly string[] ForbiddenTitleWords =
        { "ใบกำกับภาษี", "ใบเสร็จรับเงิน", "ใบแจ้งหนี้", "Tax Invoice", "Receipt" };

    public const string Title = "ยืนยันการจองที่พัก";
    public const string TitleEn = "Booking Confirmation";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string M(decimal v) => v.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>จำนวน — ตัดทศนิยมที่ไม่จำเป็นทิ้ง แต่ยังผูก culture ไว้เสมอ</summary>
    private static string Q(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>วันที่แบบไทย (พ.ศ.) — เอกสารนี้เป็นของแขก ไม่ใช่แบบยื่นภาษี
    /// แต่ผู้ใช้ไทยอ่าน พ.ศ. เป็นปกติ · ระบุ culture ชัดเจนเสมอ ไม่พึ่ง culture
    /// ของ process (บทเรียน `ToString("dd/MM/yyyy")` ที่กลายเป็น พ.ศ. เงียบ ๆ)</summary>
    private static string D(DateTime d)
        => $"{d.Day:00}/{d.Month:00}/{d.Year + 543}";

    private static string StatusLabel(LodgingReservationStatus s) => s switch
    {
        LodgingReservationStatus.Pending => "รอชำระมัดจำ",
        LodgingReservationStatus.Confirmed => "ยืนยันแล้ว",
        LodgingReservationStatus.CheckedIn => "เช็คอินแล้ว",
        LodgingReservationStatus.CheckedOut => "เช็คเอาต์แล้ว",
        LodgingReservationStatus.Cancelled => "ยกเลิกแล้ว",
        LodgingReservationStatus.NoShow => "ไม่มาเข้าพัก",
        _ => s.ToString(),
    };

    public static string BuildHtml(LodgingReservationResponse r, string? logoDataUri = null, string? accentHex = null)
    {
        var accent = SafeHex(accentHex) ?? "#0F766E";
        var sb = new StringBuilder(8192);

        sb.Append("<!DOCTYPE html><html lang=\"th\"><head><meta charset=\"utf-8\">");
        sb.Append($"<title>{E(Title)} {E(r.ReservationNumber)}</title><style>");
        sb.Append(@"
*{box-sizing:border-box}
body{font-family:'Sarabun','Noto Sans Thai',sans-serif;font-size:12.5px;color:#111827;margin:0;padding:26px 30px}
.hd{display:flex;justify-content:space-between;align-items:flex-start;border-bottom:3px solid " + accent + @";padding-bottom:12px}
.hd h1{margin:0;font-size:21px;letter-spacing:.3px}
.hd .sub{font-size:11px;color:#6B7280;margin-top:2px}
.logo{max-height:56px;max-width:190px;object-fit:contain}
.no{text-align:right}
.no .n{font-size:17px;font-weight:800;font-family:monospace}
.badge{display:inline-block;padding:2px 10px;border-radius:999px;font-size:11px;font-weight:700;background:#ECFDF5;color:#065F46}
.grid{display:flex;gap:14px;margin-top:16px}
.box{flex:1;border:1px solid #E5E7EB;border-radius:8px;padding:12px 14px}
.box h3{margin:0 0 6px;font-size:12px;color:" + accent + @";text-transform:uppercase;letter-spacing:.5px}
.kv{display:flex;justify-content:space-between;gap:10px;padding:2px 0}
.kv span:first-child{color:#6B7280}
table{width:100%;border-collapse:collapse;margin-top:16px;font-size:12px}
th{background:" + accent + @";color:#fff;text-align:left;padding:7px 9px;font-size:11px}
td{padding:6px 9px;border-bottom:1px solid #F3F4F6}
td.r,th.r{text-align:right}
.tot{margin-top:10px;margin-left:auto;width:290px}
.tot .kv{padding:3px 0}
.tot .grand{border-top:2px solid #111827;margin-top:4px;padding-top:5px;font-weight:800;font-size:14px}
.note{margin-top:16px;border:1px dashed #D1D5DB;border-radius:8px;padding:11px 13px;font-size:11.5px;color:#374151;white-space:pre-wrap}
.foot{margin-top:20px;border-top:1px solid #E5E7EB;padding-top:9px;font-size:10.5px;color:#6B7280;line-height:1.7}
");
        sb.Append("</style></head><body>");

        // ── หัวเอกสาร ──
        sb.Append("<div class=\"hd\"><div>");
        if (!string.IsNullOrEmpty(logoDataUri))
            sb.Append($"<img class=\"logo\" src=\"{E(logoDataUri)}\" alt=\"\"><br>");
        sb.Append($"<h1>{E(Title)}</h1><div class=\"sub\">{E(TitleEn)} · {E(r.PropertyName)}</div>");
        sb.Append("</div><div class=\"no\">");
        sb.Append($"<div class=\"sub\">เลขที่การจอง</div><div class=\"n\">{E(r.ReservationNumber)}</div>");
        sb.Append($"<div style=\"margin-top:5px\"><span class=\"badge\">{E(StatusLabel(r.Status))}</span></div>");
        sb.Append("</div></div>");

        // ── ที่พัก / ผู้เข้าพัก ──
        sb.Append("<div class=\"grid\"><div class=\"box\"><h3>ที่พัก</h3>");
        sb.Append($"<div><b>{E(r.PropertyName)}</b></div>");
        if (!string.IsNullOrWhiteSpace(r.PropertyAddress))
            sb.Append($"<div style=\"color:#6B7280\">{E(r.PropertyAddress)}</div>");
        if (!string.IsNullOrWhiteSpace(r.PropertyPhone))
            sb.Append($"<div>โทร {E(r.PropertyPhone)}</div>");
        if (!string.IsNullOrWhiteSpace(r.PropertyLineId))
            sb.Append($"<div>LINE {E(r.PropertyLineId)}</div>");
        if (!string.IsNullOrWhiteSpace(r.PropertyMapUrl))
            sb.Append($"<div style=\"font-size:10.5px;color:#6B7280;word-break:break-all\">{E(r.PropertyMapUrl)}</div>");
        sb.Append("</div><div class=\"box\"><h3>ผู้เข้าพัก</h3>");
        sb.Append($"<div><b>{E(r.GuestName)}</b></div>");
        if (!string.IsNullOrWhiteSpace(r.GuestCompanyName))
            sb.Append($"<div>{E(r.GuestCompanyName)}</div>");
        sb.Append($"<div style=\"color:#6B7280\">{E(r.GuestPhone)} {E(r.GuestEmail)}</div>");
        sb.Append("</div></div>");

        // ── ช่วงเข้าพัก ──
        sb.Append("<div class=\"grid\"><div class=\"box\">");
        sb.Append($"<div class=\"kv\"><span>เช็คอิน</span><b>{D(r.CheckInDate)} หลัง {E(r.CheckInTime)} น.</b></div>");
        sb.Append($"<div class=\"kv\"><span>เช็คเอาต์</span><b>{D(r.CheckOutDate)} ก่อน {E(r.CheckOutTime)} น.</b></div>");
        sb.Append($"<div class=\"kv\"><span>จำนวนคืน</span><b>{r.Nights} คืน</b></div>");
        sb.Append($"<div class=\"kv\"><span>ผู้เข้าพัก</span><b>{r.Adults} ผู้ใหญ่"
            + (r.Children > 0 ? $" · {r.Children} เด็ก" : "")
            + (r.Infants > 0 ? $" · {r.Infants} ทารก" : "") + "</b></div>");
        if (!string.IsNullOrWhiteSpace(r.ArrivalTime))
            sb.Append($"<div class=\"kv\"><span>เวลาที่คาดว่าจะถึง</span><b>{E(r.ArrivalTime)}</b></div>");
        sb.Append("</div></div>");

        // ── รายการห้อง/บริการเสริม ──
        sb.Append("<table><thead><tr><th>รายการ</th><th class=\"r\">จำนวน</th><th class=\"r\">รวม</th></tr></thead><tbody>");
        foreach (var room in r.Rooms)
        {
            var name = room.RoomTypeName + (string.IsNullOrWhiteSpace(room.UnitNumber) ? "" : $" (ห้อง {room.UnitNumber})");
            sb.Append($"<tr><td>{E(name)}<div style=\"color:#6B7280;font-size:10.5px\">{room.Adults} ผู้ใหญ่"
                + (room.Children > 0 ? $" · {room.Children} เด็ก" : "")
                + $" · {r.Nights} คืน</div></td><td class=\"r\">1</td><td class=\"r\">{M(room.Subtotal)}</td></tr>");
        }
        foreach (var x in r.Extras)
            sb.Append($"<tr><td>{E(x.Name)}</td><td class=\"r\">{x.Quantity}</td><td class=\"r\">{M(x.Total)}</td></tr>");
        foreach (var c in r.Charges.Where(c => c.Status != LodgingChargeStatus.Cancelled))
            sb.Append($"<tr><td>{E(c.Description)}</td><td class=\"r\">{Q(c.Quantity)}</td><td class=\"r\">{M(c.Total)}</td></tr>");
        sb.Append("</tbody></table>");

        // ── ยอดเงิน ──
        sb.Append("<div class=\"tot\">");
        if (r.DiscountAmount > 0) sb.Append($"<div class=\"kv\"><span>ส่วนลด</span><span>-{M(r.DiscountAmount)}</span></div>");
        if (r.ServiceChargeAmount > 0) sb.Append($"<div class=\"kv\"><span>ค่าบริการ</span><span>{M(r.ServiceChargeAmount)}</span></div>");
        if (r.VatAmount > 0) sb.Append($"<div class=\"kv\"><span>ภาษีมูลค่าเพิ่ม (รวมในยอด)</span><span>{M(r.VatAmount)}</span></div>");
        sb.Append($"<div class=\"kv grand\"><span>ยอดรวมทั้งสิ้น</span><span>{M(r.GrandTotal)} {E(r.Currency)}</span></div>");
        sb.Append($"<div class=\"kv\"><span>ชำระแล้ว</span><span>{M(r.PaidAmount)}</span></div>");
        sb.Append($"<div class=\"kv\"><span><b>คงเหลือ</b></span><span><b>{M(r.BalanceDue)}</b></span></div>");
        if (r.Status == LodgingReservationStatus.Pending && r.DepositRequired > 0)
            sb.Append($"<div class=\"kv\" style=\"color:#92400E\"><span>มัดจำที่ต้องชำระ</span><b>{M(r.DepositRequired)}</b></div>");
        sb.Append("</div>");

        // ── นโยบายยกเลิก (snapshot ณ วันจอง) ──
        if (!string.IsNullOrWhiteSpace(r.CancellationPolicyName) || r.CancellationRules.Count > 0)
        {
            sb.Append("<div class=\"note\"><b>นโยบายการยกเลิก</b> — ตรึงตามเงื่อนไข ณ วันที่จอง<br>");
            sb.Append(E(r.CancellationPolicyName));
            if (r.NonRefundable) sb.Append(" · <b>ไม่คืนเงินทุกกรณี</b>");
            foreach (var rule in r.CancellationRules)
                sb.Append($"<br>ยกเลิกก่อนเช็คอิน {rule.DaysBefore} วัน — คิดค่าปรับ {Q(rule.PenaltyPercent)}%");
            sb.Append("</div>");
        }

        if (!string.IsNullOrWhiteSpace(r.HouseRules))
            sb.Append($"<div class=\"note\"><b>กติกาที่พัก</b><br>{E(r.HouseRules)}</div>");
        if (!string.IsNullOrWhiteSpace(r.ConfirmationMessage))
            sb.Append($"<div class=\"note\">{E(r.ConfirmationMessage)}</div>");
        if (!string.IsNullOrWhiteSpace(r.SpecialRequests))
            sb.Append($"<div class=\"note\"><b>คำขอพิเศษของผู้เข้าพัก</b><br>{E(r.SpecialRequests)}</div>"
                + "<div style=\"font-size:10.5px;color:#6B7280\">* คำขอพิเศษขึ้นอยู่กับความพร้อมของที่พัก ไม่ถือเป็นการยืนยัน</div>");

        // ── ท้ายเอกสาร — ต้องบอกให้ชัดว่าไม่ใช่เอกสารภาษี ──
        sb.Append("<div class=\"foot\">");
        sb.Append("เอกสารนี้เป็น<b>หลักฐานการจอง</b>สำหรับแสดงต่อที่พักเท่านั้น "
            + "<b>ไม่ใช่เอกสารทางภาษี</b> และใช้เป็นหลักฐานการชำระเงินหรือเครดิตภาษีซื้อไม่ได้<br>");
        if (!string.IsNullOrWhiteSpace(r.DepositDocumentNumber))
            sb.Append($"ใบเสร็จมัดจำเลขที่ {E(r.DepositDocumentNumber)}<br>");
        if (!string.IsNullOrWhiteSpace(r.FinalDocumentNumber))
            sb.Append($"เอกสารเมื่อเช็คเอาต์เลขที่ {E(r.FinalDocumentNumber)}<br>");
        sb.Append($"ออกเอกสารเมื่อ {D(DateTime.UtcNow.AddHours(7))} · "
            + $"อ้างอิงการจอง {E(r.ReservationNumber)}");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>ชื่อไฟล์ที่แขกจะเห็นตอนดาวน์โหลด — ห้ามมีอักขระที่ทำให้ header เพี้ยน</summary>
    public static string FileName(LodgingReservationResponse r)
    {
        var safe = new string((r.ReservationNumber ?? "booking")
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) safe = "booking";
        return $"booking-{safe}.pdf";
    }

    private static string? SafeHex(string? hex)
        => hex is { Length: 7 } h && h[0] == '#'
           && h.Skip(1).All(Uri.IsHexDigit) ? h : null;
}
