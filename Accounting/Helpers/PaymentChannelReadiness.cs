using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-3 (คำตัดสิน 260-3-4) — <b>การ์ด "ความพร้อมรับเงินจากลูกค้า"</b> บนหน้ารับชำระออนไลน์ (payment-settings.html) · pure
/// <para>ไม่มีกติกาใหม่: ประกอบจากแถวที่มีอยู่แล้ว — สถานะช่องทาง/บัญชีรับโอน/ลิงก์ชำระ/คิวสลิปจาก <see cref="AutopilotSettingsHub.Build"/> (ตัวเดียวกับการ์ดศูนย์รวมของ Autopilot) ·
/// แถว PAY-LIVE จาก <see cref="AutopilotReadiness.PayLiveItem"/> (ตัวเดียวกับความพร้อมของรายการประจำ) · ความพร้อม LINE OA จาก <c>LineChannelAsync</c>
/// (<see cref="CompanyLineReadiness"/>) · URL ของระบบจาก <see cref="SystemBaseUrl.IsConfigured"/> · หน้าเว็บแสดงอย่างเดียว (ไม่มีเงื่อนไขใน JS)</para>
/// </summary>
public static class PaymentChannelReadiness
{
    public const string LineSettingsUrl = "/pages/settings.html?tab=line";

    /// <summary><paramref name="Hub"/> = ผลของ <see cref="AutopilotSettingsHub.Build"/> · <paramref name="LineOaProblem"/> null เมื่อ <paramref name="LineOaReady"/></summary>
    public sealed record Facts(IReadOnlyList<AutopilotSettingsHub.Item> Hub, bool GatewayLive, bool SystemBaseUrlConfigured, bool LineOaReady, string? LineOaProblem);

    /// <summary>ผู้ให้บริการรับชำระที่ "รับเงินจริงอยู่" ของบริษัท — โหมดเงินจริง + เปิดใช้ + ไม่ถูกลบ · ผู้อ่าน: การ์ดนี้ และความพร้อมของรายการประจำ
    /// (<c>RecurringAutopilotService</c> — เดิมเขียน query เองโดยไม่ดู <c>IsActive</c>) · tenant</summary>
    public static IQueryable<PaymentProviderConfig> LiveConfigs(AccountingDbContext db, Guid companyId)
        => db.PaymentProviderConfigs.Where(pc => pc.CompanyId == companyId && !pc.IsDeleted && pc.IsActive && pc.Mode == PaymentProviderMode.Live);

    private static AutopilotSettingsHub.Item? HubItem(Facts f, string key) => f.Hub.FirstOrDefault(i => i.Key == key);

    private static string Verdict(bool? isOn, string whenOff) => isOn switch
    {
        true => AutopilotReadiness.Ok,
        false => whenOff,
        _ => AutopilotReadiness.Warn,   // ตรวจไม่ได้ — ไม่แต่งว่าพร้อม
    };

    public static IReadOnlyList<AutopilotReadiness.Item> Build(Facts f)
    {
        var items = new List<AutopilotReadiness.Item>();
        var gateway = HubItem(f, "gateway");
        var transfer = HubItem(f, "bankTransfer");
        var payLink = HubItem(f, "payLink");
        var slip = HubItem(f, "slipQueue");
        var onlineOk = gateway?.IsOn == true;

        if (gateway != null)
            items.Add(new("GATEWAY", "รับเงิน", Verdict(gateway.IsOn, AutopilotReadiness.Info), $"{gateway.Label}: {gateway.StatusLabel}",
                gateway.IsOn == false ? "ไม่บังคับ — ร้านที่รับโอนอย่างเดียวใช้บัญชีรับโอน/QR + แจ้งสลิปได้" : null, null, null));
        items.Add(AutopilotReadiness.PayLiveItem(f.GatewayLive));
        if (transfer != null)
            items.Add(new("PAY-TRANSFER", "รับเงิน", Verdict(transfer.IsOn, onlineOk ? AutopilotReadiness.Info : AutopilotReadiness.Warn),
                $"{transfer.Label}: {transfer.StatusLabel}",
                transfer.IsOn == true ? "ลูกค้าแนบสลิปจากหน้าชำระของเอกสาร/พอร์ทัล — ระบบไม่ปิดหนี้จากสลิปเอง พนักงานตรวจแล้วกดยืนยัน" : null,
                transfer.IsOn == true ? null : transfer.Href, transfer.IsOn == true ? null : transfer.HrefLabel));
        if (payLink != null)
        {
            if (payLink.IsOn != true)
                items.Add(new("PAY-LINK", "ส่ง", AutopilotReadiness.Info, $"{payLink.Label}: {payLink.StatusLabel}",
                    "ลูกค้ายังเปิดหน้าชำระจากพอร์ทัล/ลิงก์ที่ส่งเองได้", payLink.Href, payLink.HrefLabel));
            else if (!f.SystemBaseUrlConfigured)
                items.Add(new("PAY-LINK", "ส่ง", AutopilotReadiness.Warn, AutopilotReadiness.SystemBaseUrlMissingTitle,
                    "ลิงก์ชำระสร้างจาก URL ของระบบ — แจ้งผู้ดูแลระบบให้ตั้งที่หน้าแอดมิน → ตั้งค่าระบบ", AutopilotReadiness.SystemSettingsUrl, "ตั้ง URL ของระบบ (ผู้ดูแลระบบ)"));
            else if (!onlineOk)
                items.Add(new("PAY-LINK", "ส่ง", AutopilotReadiness.Warn, AutopilotReadiness.PayLinkNoOnlineTitle,
                    gateway?.StatusLabel, null, null));
            else
                items.Add(new("PAY-LINK", "ส่ง", AutopilotReadiness.Ok, $"{payLink.Label}: {payLink.StatusLabel}", null, null, null));
        }
        // LINE: การ์ดเอกสารมีปุ่มชำระเมื่อออกลิงก์ชำระได้ (LineDocumentCard · ตัวออกลิงก์เดียวกับอีเมล)
        if (!f.LineOaReady)
            items.Add(new("LINE-PAY", "ส่ง", AutopilotReadiness.Info, "ลิงก์ชำระทาง LINE: ยังไม่พร้อม — ส่งเอกสารทางอีเมลอย่างเดียว",
                f.LineOaProblem, LineSettingsUrl, "ตั้ง LINE OA ของบริษัท"));
        else if (!onlineOk || !f.SystemBaseUrlConfigured)
            items.Add(new("LINE-PAY", "ส่ง", AutopilotReadiness.Info, "ส่งการ์ดเอกสารทาง LINE ได้ — แต่ยังไม่มีปุ่มชำระ (ยังออกลิงก์ชำระไม่ได้)", null, null, null));
        else
            items.Add(new("LINE-PAY", "ส่ง", AutopilotReadiness.Ok, "การ์ดเอกสารทาง LINE มีปุ่มชำระ (ลิงก์เดียวกับอีเมล)", null, null, null));
        if (slip != null)
            items.Add(new("SLIP-QUEUE", "หลังรับเงิน", AutopilotReadiness.Info, $"{slip.Label}: {slip.StatusLabel}", null, slip.Href, slip.HrefLabel));
        return items;
    }
}
