using System.Globalization;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อความแจ้งเตือนหนึ่งฉบับ (ข้อความธรรมดา — ช่องส่ง <c>BookingNotifier</c> แปลงเป็น HTML ด้วย <see cref="BookingReminderPlan.ToHtml"/> ซึ่ง HtmlEncode ทุกบรรทัด)</summary>
public sealed record CmsNotification(string Subject, string Text);

/// <summary>
/// <b>ข้อความแจ้งเตือนของร้านค้า/การจอง/lead หน้าเว็บ — ที่เดียว</b> (รอบ 209 P3 · R-209-A4/B5/D5/E2/C4 · คำตัดสินข้อ 168 ซ)
/// <para><b>ที่มา</b>: ช่องส่ง <c>IBookingNotifier</c> (อีเมล → LINE · บอกผลจริง) มีแล้วแต่ไม่มีผู้เรียกในเส้นออเดอร์/lead/การจอง ·
/// ค่าตั้ง <c>NotifyOnNewOrder/OrderNotificationEmails/OrderConfirmMessageTh</c> มีช่องบนจอแต่ไม่มีผู้อ่าน</para>
/// <para><b>กติกา</b>: ข้อความเป็นข้อความธรรมดา (ข้อมูลผู้ใช้ไม่ต่อเป็น HTML ที่นี่ — ช่องอีเมล encode ทั้งก้อน กฎ #4 C) · วันที่แบบไทย พ.ศ.
/// (<see cref="BookingReminderPlan.ThaiDate"/>) · ผู้รับฝั่งร้าน = <c>OrderNotificationEmails</c> → อีเมลติดต่อของเว็บ → อีเมลบริษัท ·
/// ส่งล้ม = ไม่ล้มคำสั่ง แต่ประทับ <see cref="FailureTag"/> บนรายการ (F2 #7) — ผู้เรียกเป็นคนประทับ</para>
/// </summary>
public static class CmsNotificationText
{
    public const string RuleCode = "CMS-NOTIFY";
    /// <summary>ป้ายบนหมายเหตุภายในของออเดอร์/การจอง/lead เมื่อแจ้งไม่ถึง (ค้นหาได้)</summary>
    public const string FailureTag = "[CMS-NOTIFY-FAILED]";
    public const int MaxShopRecipients = 10;

    private static readonly char[] Separators = { ',', ';', ' ', '\n', '\r', '\t' };

    /// <summary>"a@x.com; b@y.com" → [a@x.com, b@y.com] (ตัดซ้ำไม่สนตัวพิมพ์ · ตัดค่าที่ไม่ใช่อีเมล · ไม่เกิน <see cref="MaxShopRecipients"/>)</summary>
    private static IReadOnlyList<string> ParseEmails(string? raw)
        => (raw ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(LooksLikeEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxShopRecipients)
            .ToList();

    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at == s.LastIndexOf('@') && s.IndexOf('.', at) > at + 1 && !s.EndsWith('.') && s.Length <= 254;
    }

    /// <summary>ผู้รับฝั่งร้าน — ลำดับชั้น: ค่าตั้งของร้าน (<c>OrderNotificationEmails</c>) → อีเมลติดต่อของเว็บ → อีเมลบริษัท · ว่างทั้งหมด = ไม่มีผู้รับ (ผู้เรียกประทับเหตุ)</summary>
    public static IReadOnlyList<string> ShopRecipients(string? configured, string? siteContactEmail, string? companyEmail)
    {
        var list = ParseEmails(configured);
        if (list.Count > 0) return list;
        list = ParseEmails(siteContactEmail);
        return list.Count > 0 ? list : ParseEmails(companyEmail);
    }

    /// <summary>แจ้งร้านเรื่องออเดอร์ (ออเดอร์ใหม่ · สลิปเข้า) ไหม — ไม่มีแถวค่าตั้ง = ค่าเริ่มต้นของคอลัมน์ (<c>true</c>)</summary>
    public static bool ShopOrderNotifyEnabled(bool? notifyOnNewOrder) => notifyOnNewOrder ?? true;

    private static string Who(string? name) => string.IsNullOrWhiteSpace(name) ? "ลูกค้า" : name.Trim();
    private static string Baht(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    private static string Time(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    // ───────────── ฝั่งร้าน ─────────────

    public static CmsNotification ShopNewOrder(string siteName, string orderNumber, string? customerName, string? phone, decimal total,
        string fulfillmentLabel, DateOnly? readyDate, int itemCount)
        => new($"ออเดอร์ใหม่ #{orderNumber} — {siteName}",
            $"มีคำสั่งซื้อใหม่จากเว็บ {siteName}\nเลขที่ {orderNumber}\nลูกค้า {Who(customerName)}{(string.IsNullOrWhiteSpace(phone) ? "" : $" · โทร {phone.Trim()}")}\n"
            + $"{itemCount} รายการ · ยอดรวม {Baht(total)} บาท\nการรับสินค้า: {fulfillmentLabel}"
            + (readyDate is DateOnly d ? $" · พร้อมเร็วสุด {BookingReminderPlan.ThaiDate(d)}" : "")
            + "\nเปิดดู/ยืนยันที่เมนู คำสั่งซื้อจากเว็บไซต์");

    public static CmsNotification ShopSlipUploaded(string siteName, string orderNumber, string? customerName, decimal amount)
        => new($"ลูกค้าแนบสลิป #{orderNumber} — {siteName}",
            $"ลูกค้า {Who(customerName)} แนบสลิปโอนเงินของออเดอร์ {orderNumber} ยอด {Baht(amount)} บาท\nตรวจสลิปแล้วกด \"ยืนยันรับเงิน + ลงบัญชี\" ที่เมนู คำสั่งซื้อจากเว็บไซต์");

    public static CmsNotification ShopNewBooking(string siteName, string bookingNumber, string serviceName, string? customerName, string? phone,
        DateOnly date, TimeOnly start, int guestCount, bool waitlisted)
        => new($"{(waitlisted ? "คิวสำรองใหม่" : "การจองใหม่")} #{bookingNumber} — {serviceName}",
            $"{(waitlisted ? "มีลูกค้าลงคิวสำรอง" : "มีการจองใหม่")}จากเว็บ {siteName}\nเลขที่ {bookingNumber} · {serviceName}\n"
            + $"วันที่ {BookingReminderPlan.ThaiDate(date)} เวลา {Time(start)} · {Math.Max(1, guestCount)} ท่าน\n"
            + $"ลูกค้า {Who(customerName)}{(string.IsNullOrWhiteSpace(phone) ? "" : $" · โทร {phone.Trim()}")}\nเปิดดูที่เมนู การจอง");

    public static CmsNotification ShopNewLead(string siteName, string leadNumber, LeadType type, string? customerName, string? email, string? phone, string? message)
    {
        var msg = string.IsNullOrWhiteSpace(message) ? "" : "\nข้อความ: " + (message.Trim().Length > 500 ? message.Trim()[..500] + "…" : message.Trim());
        return new($"{LeadTypeLabel(type)}ใหม่ #{leadNumber} — {siteName}",
            $"มี{LeadTypeLabel(type)}ใหม่จากเว็บ {siteName}\nเลขที่ {leadNumber}\nจาก {Who(customerName)}"
            + (string.IsNullOrWhiteSpace(email) ? "" : $" · {email.Trim()}") + (string.IsNullOrWhiteSpace(phone) ? "" : $" · โทร {phone.Trim()}")
            + msg + "\nเปิดดูที่เมนู ลูกค้าเป้าหมาย (Leads)");
    }

    public static CmsNotification ShopQuotationAccepted(string documentNumber, string acceptedBy, decimal total, string? leadNumber)
        => new($"ลูกค้ายอมรับใบเสนอราคา {documentNumber}",
            $"ลูกค้า ({acceptedBy.Trim()}) กดยอมรับใบเสนอราคา {documentNumber} ยอด {Baht(total)} บาท ทางลิงก์ออนไลน์"
            + (leadNumber != null ? $"\nlead {leadNumber} ปรับเป็น \"ได้งาน\" แล้ว" : "")
            + "\nดำเนินการต่อ (ออกใบสั่งขาย/ใบแจ้งหนี้) ที่หน้าเอกสาร");

    /// <summary>ชื่อชนิด lead ภาษาไทย (ข้อความแจ้งเตือน)</summary>
    private static string LeadTypeLabel(LeadType t) => t switch
    {
        LeadType.Rfq => "คำขอใบเสนอราคา",
        LeadType.Quote => "คำขอใบเสนอราคา",
        LeadType.ShipmentQuote => "คำขอราคาขนส่ง",
        LeadType.Viewing => "นัดชม",
        LeadType.Demo => "คำขอสาธิต",
        LeadType.Enrollment => "ใบสมัคร",
        LeadType.Subscription => "คำขอสมัครสมาชิก",
        _ => "ข้อความติดต่อ",
    };

    // ───────────── ฝั่งลูกค้า ─────────────

    /// <summary>ยืนยันคำสั่งซื้อถึงลูกค้า — <paramref name="shopMessage"/> = <c>OrderConfirmMessageTh</c> ของร้าน (ว่าง = ไม่มีบรรทัดนี้)</summary>
    public static CmsNotification CustomerOrderConfirmation(string siteName, string orderNumber, string? customerName, decimal total,
        decimal amountDueNow, string fulfillmentLabel, DateOnly? readyDate, string? shopMessage)
        => new($"ได้รับคำสั่งซื้อ #{orderNumber} แล้ว — {siteName}",
            $"เรียน {Who(customerName)}\n{siteName} ได้รับคำสั่งซื้อของคุณแล้ว\nเลขที่ {orderNumber} · ยอดรวม {Baht(total)} บาท\nการรับสินค้า: {fulfillmentLabel}"
            + (readyDate is DateOnly d ? $" · พร้อมเร็วสุด {BookingReminderPlan.ThaiDate(d)}" : "")
            + (amountDueNow > 0.005m ? $"\nยอดที่ต้องชำระเพื่อยืนยันคำสั่งซื้อ {Baht(amountDueNow)} บาท" : "")
            + (string.IsNullOrWhiteSpace(shopMessage) ? "" : "\n" + shopMessage.Trim())
            + "\nขอบคุณที่สั่งซื้อ");

    /// <summary>เปลี่ยนสถานะที่ลูกค้าควรรู้ — จัดส่งแล้ว (+ผู้ขนส่ง/เลขพัสดุ) · พร้อมให้รับ (+ยอดค้าง) · ส่งมอบแล้ว · สถานะอื่น = null (ไม่ส่ง)</summary>
    public static CmsNotification? CustomerOrderStatus(SiteOrderStatus status, string siteName, string orderNumber, string? customerName,
        string? carrier, string? trackingNumber, decimal balance)
    {
        var who = Who(customerName);
        switch (status)
        {
            case SiteOrderStatus.Shipped:
                var track = string.IsNullOrWhiteSpace(trackingNumber) ? "" :
                    $"\n{(string.IsNullOrWhiteSpace(carrier) ? "เลขพัสดุ" : $"ขนส่ง {carrier.Trim()} · เลขพัสดุ")} {trackingNumber.Trim()}";
                return new($"จัดส่งคำสั่งซื้อ #{orderNumber} แล้ว — {siteName}",
                    $"เรียน {who}\nคำสั่งซื้อ {orderNumber} จาก {siteName} จัดส่งแล้ว{track}"
                    + (balance > 0.005m ? $"\nยอดคงค้าง {Baht(balance)} บาท" : ""));
            case SiteOrderStatus.ReadyForPickup:
                return new($"คำสั่งซื้อ #{orderNumber} พร้อมให้รับแล้ว — {siteName}",
                    $"เรียน {who}\nคำสั่งซื้อ {orderNumber} พร้อมให้รับที่ร้าน {siteName} แล้ว"
                    + (balance > 0.005m ? $"\nยอดที่ชำระตอนรับ {Baht(balance)} บาท" : "") + "\nกรุณาแจ้งเลขที่คำสั่งซื้อเมื่อมารับ");
            case SiteOrderStatus.Delivered:
                return new($"ส่งมอบคำสั่งซื้อ #{orderNumber} แล้ว — {siteName}",
                    $"เรียน {who}\nคำสั่งซื้อ {orderNumber} จาก {siteName} ส่งมอบเรียบร้อยแล้ว ขอบคุณที่อุดหนุน");
            default:
                return null;
        }
    }

    public static CmsNotification CustomerBookingConfirmation(string siteName, string bookingNumber, string serviceName, string? customerName,
        DateOnly date, TimeOnly start, string? resourceName, BookingStatus status)
    {
        var head = status == BookingStatus.Waitlisted ? "ลงคิวสำรองแล้ว" : status == BookingStatus.Confirmed ? "ยืนยันการจองแล้ว" : "ได้รับการจองแล้ว";
        var tail = status switch
        {
            BookingStatus.Waitlisted => "รอบนี้เต็ม — ระบบจะแจ้งทันทีเมื่อมีที่ว่าง",
            BookingStatus.Confirmed => "พบกันตามนัด",
            _ => "ร้านจะยืนยันการจองอีกครั้ง",
        };
        return new($"{head} #{bookingNumber} — {serviceName}",
            $"เรียน {Who(customerName)}\n{siteName}: {head}\n{serviceName} วันที่ {BookingReminderPlan.ThaiDate(date)} เวลา {Time(start)}"
            + (string.IsNullOrWhiteSpace(resourceName) ? "" : $" กับ {resourceName.Trim()}") + $"\nเลขที่จอง {bookingNumber}\n{tail}");
    }

    public static CmsNotification CustomerBookingCancelledByShop(string siteName, string bookingNumber, string serviceName, string? customerName,
        DateOnly date, TimeOnly start, string? reason)
        => new($"การจอง #{bookingNumber} ถูกยกเลิก — {serviceName}",
            $"เรียน {Who(customerName)}\n{siteName} ยกเลิกการจอง {serviceName} วันที่ {BookingReminderPlan.ThaiDate(date)} เวลา {Time(start)}\nเลขที่จอง {bookingNumber}"
            + (string.IsNullOrWhiteSpace(reason) ? "" : $"\nเหตุผล: {reason.Trim()}") + "\nหากชำระเงินไว้ ร้านจะติดต่อเรื่องการคืนเงิน/เลื่อนนัด");

    /// <summary>รอบ 210: ร้าน/ลูกค้าเลื่อนนัด ⇒ ยืนยันวัน-เวลาใหม่ถึงลูกค้า</summary>
    public static CmsNotification CustomerBookingRescheduled(string siteName, string bookingNumber, string serviceName, string? customerName,
        DateOnly oldDate, TimeOnly oldStart, DateOnly newDate, TimeOnly newStart)
        => new($"เลื่อนนัด #{bookingNumber} แล้ว — {serviceName}",
            $"เรียน {Who(customerName)}\n{siteName}: เลื่อนนัด {serviceName} แล้ว\n"
            + $"จากเดิม {BookingReminderPlan.ThaiDate(oldDate)} เวลา {Time(oldStart)}\nเป็น {BookingReminderPlan.ThaiDate(newDate)} เวลา {Time(newStart)}\nเลขที่จอง {bookingNumber}");

    /// <summary>รอบ 210: ต่อบรรทัดลิงก์จัดการ/ติดตาม (<see cref="CmsStorefrontLink.BookingManageLine"/> · <see cref="CmsStorefrontLink.OrderTrackLine"/>) ท้ายข้อความถึงลูกค้า ·
    /// บรรทัดว่าง = ข้อความเดิม</summary>
    public static CmsNotification WithLine(CmsNotification message, string? line)
        => string.IsNullOrWhiteSpace(line) ? message : message with { Text = message.Text + "\n" + line.Trim() };

    /// <summary>หมายเหตุภายในเมื่อแจ้งไม่ถึง — "ใคร" (ร้าน/ลูกค้า) + เรื่อง + เหตุจริงจากช่องส่ง</summary>
    public static string FailureNote(string target, string subject, string error, DateTime atUtc)
        => $"{FailureTag} [{atUtc:yyyy-MM-dd HH:mm}Z] แจ้ง{target}ไม่สำเร็จ ({subject}): {error} — กรุณาติดต่อเอง";
}
