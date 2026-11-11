namespace Accounting.Helpers;

/// <summary>รอบ 259-3 (ทีม E-M2 · คำตัดสิน 259-3-4) — <b>การ์ดศูนย์รวมค่าตั้ง Autopilot</b>: ค่าตั้งเชิงอัตโนมัติระดับบริษัทกระจาย 5 หน้า
/// (ตั้งค่า·แท็บเอกสาร · Autopilot · รับชำระออนไลน์ · ธนาคาร · ตารางส่ง/แจ้งเตือน) โดยไม่มีลิงก์ถึงกัน ⇒ หน้า Autopilot แสดงสถานะ<b>อ่านอย่างเดียว</b>
/// ของแต่ละเรื่อง + ลิงก์ไปหน้าที่เป็นบ้านของค่านั้น (ไม่มีช่องแก้ซ้ำ — บ้านเดียวของค่าแต่ละตัวคงเดิม)
/// <para>สถานะประกอบที่นี่ตัวเดียว (server computes · page displays) · <c>IsOn</c> null = ไม่ใช่ค่าเปิด/ปิด หรือตรวจไม่ได้ (ป้ายบอกเอง)</para></summary>
public static class AutopilotSettingsHub
{
    /// <summary>ลิงก์การ์ด "ส่งเอกสารอัตโนมัติ" ในแท็บเอกสารของหน้าตั้งค่า (deep-link ที่ settings.html รองรับ)</summary>
    public const string AutoSendHref = "/pages/settings.html?tab=document&focus=autoSendCard";

    /// <summary><paramref name="PayFacts"/> null = ตรวจช่องทางรับเงินไม่ได้ (ไม่แต่งค่า) · <paramref name="SlipQueueHref"/> = คิวเจ้าของเดียวของสลิป</summary>
    public sealed record Facts(bool PayLinkDefaultEnabled, bool PostPayEmailReceipt, bool WhtCertReminderEnabled,
        DocumentPayLinkPolicy.CompanyFacts? PayFacts, int ActiveScheduleRules, int SlipAwaitingConfirm, string SlipQueueHref);

    public sealed record Item(string Key, string Label, string StatusLabel, bool? IsOn, string Href, string HrefLabel);

    public static IReadOnlyList<Item> Build(Facts f)
    {
        static string OnOff(bool v) => v ? "เปิด" : "ปิด";
        var list = new List<Item>
        {
            new("payLink", "แนบลิงก์ชำระเงินในอีเมลเอกสาร", OnOff(f.PayLinkDefaultEnabled), f.PayLinkDefaultEnabled, AutoSendHref, "ตั้งที่ ตั้งค่า → เอกสาร"),
            new("postPayReceipt", "ส่งใบเสร็จให้ลูกค้าหลังรับชำระ", OnOff(f.PostPayEmailReceipt), f.PostPayEmailReceipt, AutoSendHref, "ตั้งที่ ตั้งค่า → เอกสาร"),
            new("whtReminder", "ทวงหนังสือรับรอง 50 ทวิ จากลูกค้าอัตโนมัติ", OnOff(f.WhtCertReminderEnabled), f.WhtCertReminderEnabled, AutoSendHref, "ตั้งที่ ตั้งค่า → เอกสาร"),
        };
        if (f.PayFacts is { } p)
        {
            var onlineCount = DocumentPayLinkPolicy.OnlineOnly(DocumentPayLinkPolicy.CompanyAvailable(p)).Count;
            list.Add(new("gateway", "รับชำระออนไลน์ (ผู้ให้บริการรับชำระ)",
                onlineCount > 0 ? $"พร้อม — {onlineCount} วิธี" : p.OnlineConfigured ? "ตั้งแล้วแต่ยังไม่มีวิธีที่หน้าชำระทำจบได้" : "ยังไม่ได้ตั้ง",
                onlineCount > 0, "/pages/payment-settings.html", "ไปหน้ารับชำระเงินออนไลน์"));
            list.Add(new("bankTransfer", "โอนเข้าบัญชี / QR พร้อมเพย์ + แจ้งสลิป",
                p.BankTransfer ? "พร้อม" : "ยังไม่มีบัญชีรับโอน", p.BankTransfer, "/pages/bank.html", "ตั้งบัญชี/QR ที่หน้าบัญชีธนาคาร"));
        }
        else
        {
            list.Add(new("gateway", "รับชำระออนไลน์ (ผู้ให้บริการรับชำระ)", "ตรวจไม่ได้", null, "/pages/payment-settings.html", "ไปหน้ารับชำระเงินออนไลน์"));
            list.Add(new("bankTransfer", "โอนเข้าบัญชี / QR พร้อมเพย์ + แจ้งสลิป", "ตรวจไม่ได้", null, "/pages/bank.html", "ตั้งบัญชี/QR ที่หน้าบัญชีธนาคาร"));
        }
        list.Add(new("slipQueue", "ยืนยันสลิปที่ลูกค้าแนบ",
            f.SlipAwaitingConfirm > 0 ? $"รอยืนยัน {f.SlipAwaitingConfirm} รายการ" : "ไม่มีสลิปรอ", null, f.SlipQueueHref, "ไปหน้ารายการรับชำระออนไลน์"));
        list.Add(new("schedule", "ตารางส่งเอกสาร (อีเมล · LINE)",
            f.ActiveScheduleRules > 0 ? $"เปิดอยู่ {f.ActiveScheduleRules} กฎ" : "ยังไม่มีกฎที่เปิด", f.ActiveScheduleRules > 0, "/pages/email-schedule.html", "ไปหน้าตารางส่งเอกสาร"));
        list.Add(new("notify", "ผู้รับและช่องทางแจ้งเตือนทีม (รวมสรุปประจำวัน)", "ตั้งทีละเหตุการณ์", null, "/pages/notifications.html", "ไปหน้าการแจ้งเตือน"));
        return list;
    }
}
