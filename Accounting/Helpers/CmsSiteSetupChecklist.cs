namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของเว็บหนึ่งเว็บสำหรับแผง "สิ่งที่ยังไม่ได้ตั้งค่า" (ผู้ประกอบรวบรวมจากตัวตรวจเดิม — ตัวนี้ไม่อ่านฐานข้อมูล)</summary>
public sealed record CmsSiteSetupFacts(
    bool Published,
    bool HasContact,
    bool HasPrivacyPolicy,
    bool UsesBookings, int ActiveServiceCount, string? BookingPaymentProblem, IReadOnlyList<string> BookingChannelWarnings,
    bool UsesOrders, int ActiveProductCount,
    bool UsesLodging, string? LodgingReadinessProblem, string? LodgingTransferWarning,
    bool NeedsTransferAccount, bool TransferPayable, bool OnlineReady,
    string? FreeDeliveryWarning);

/// <summary>หนึ่งข้อในแผง — <see cref="Group"/> = กลุ่มของศูนย์ตั้งค่าเว็บที่แก้ (ลิงก์ <c>cms-site-settings?group=</c>) · <see cref="Href"/> = หน้าอื่นเมื่อยังไม่ย้าย</summary>
public sealed record CmsSiteSetupItem(string Key, string Label, bool Done, string? Detail, string? Group, string? Href, bool Blocking);

/// <summary>
/// รอบ 228 (<c>erp-review/2026-11-02/CMS_SETTINGS_UX_AUDIT.md</c> แพ็ก E · §4.2 · §1 ข้อ 5) — <b>ตัวตัดสินเดียว</b>ของแผง "สิ่งที่ยังไม่ได้ตั้งค่า" ต่อเว็บ
/// (หน้าศูนย์ตั้งค่าเว็บ · การ์ดเว็บใน "เว็บไซต์ทั้งหมด" (x/y) · ก่อนเผยแพร่ · ขั้นต่อไปหลังสร้างเว็บ/แม่แบบ) — <b>ไม่ตรวจซ้ำเอง</b>: ข้อความ/เงื่อนไขมาจากตัวตรวจเดิม
/// (<see cref="BookingPaymentChannels.Warnings"/> · <see cref="BookingPaymentPolicy.SiteSaveProblem"/> · <see cref="LodgingPublicReadiness"/> ·
/// <see cref="SiteTransferAccount"/> · <see cref="ShopDeliveryFee.FreeByOmissionWarning"/>) · "Blocking" = ลูกค้าจ่าย/จองไม่ได้จริง (แสดง ไม่บล็อกการเผยแพร่)
/// </summary>
public static class CmsSiteSetupChecklist
{
    public static List<CmsSiteSetupItem> Evaluate(CmsSiteSetupFacts f)
    {
        var items = new List<CmsSiteSetupItem>
        {
            new("contact", "ข้อมูลติดต่อร้าน (โทร / อีเมล / LINE)", f.HasContact,
                f.HasContact ? null : "ลูกค้าติดต่อร้านไม่ได้จากหน้าเว็บ", "general", null, false),
            new("privacy", "นโยบายความเป็นส่วนตัว (PDPA)", f.HasPrivacyPolicy,
                f.HasPrivacyPolicy ? null : "เว็บที่เก็บชื่อ/เบอร์ลูกค้าต้องมีนโยบายความเป็นส่วนตัว", "legal", null, false),
        };
        if (f.NeedsTransferAccount)
            items.Add(new("transfer", "ช่องทางรับเงิน (บัญชีรับโอน/พร้อมเพย์ หรือชำระออนไลน์)", f.TransferPayable || f.OnlineReady,
                f.TransferPayable || f.OnlineReady ? null : "ลูกค้าที่ต้องโอนจะเห็น “ติดต่อร้านเพื่อขอเลขบัญชี”", "payment", null, true));
        if (f.UsesBookings)
        {
            items.Add(new("services", "บริการที่เปิดให้จอง", f.ActiveServiceCount > 0,
                f.ActiveServiceCount > 0 ? $"{f.ActiveServiceCount} บริการ" : "ยังไม่มีบริการ — หน้าจองว่าง", null, "cms-edit:booking", true));
            var payProblem = f.BookingPaymentProblem ?? (f.BookingChannelWarnings.Count > 0 ? f.BookingChannelWarnings[0] : null);
            items.Add(new("booking-payment", "การชำระล่วงหน้าของการจอง", payProblem == null, payProblem, "payment", null, f.BookingPaymentProblem != null));
        }
        if (f.UsesOrders)
            items.Add(new("products", "สินค้าที่วางขาย", f.ActiveProductCount > 0,
                f.ActiveProductCount > 0 ? $"{f.ActiveProductCount} รายการ" : "ยังไม่มีสินค้า", null, "cms-edit:products", true));
        if (f.FreeDeliveryWarning != null || f.UsesOrders || f.UsesBookings)
            items.Add(new("shipping", "ค่าจัดส่ง", f.FreeDeliveryWarning == null, f.FreeDeliveryWarning, "shipping", null, false));
        if (f.UsesLodging)
        {
            items.Add(new("lodging", "ที่พักเปิดจองออนไลน์", f.LodgingReadinessProblem == null, f.LodgingReadinessProblem, "lodging", null, true));
            if (f.LodgingTransferWarning != null)
                items.Add(new("lodging-transfer", "บัญชีรับโอนสำหรับแขกที่พัก", false, f.LodgingTransferWarning, "payment", null, true));
        }
        items.Add(new("publish", "เผยแพร่เว็บไซต์", f.Published, f.Published ? null : "เว็บยังเป็นร่าง — ลูกค้ายังเปิดไม่ได้", null, "cms-edit:publish", false));
        return items;
    }

    /// <summary>"x/y" ที่ตั้งแล้ว</summary>
    public static (int Done, int Total) Score(IReadOnlyCollection<CmsSiteSetupItem> items) => (items.Count(i => i.Done), items.Count);

    /// <summary>ข้อความก่อนเผยแพร่ (แสดง ไม่บล็อก) — null = ครบแล้ว (ไม่นับข้อ "เผยแพร่")</summary>
    public static string? BeforePublishNote(IReadOnlyCollection<CmsSiteSetupItem> items)
    {
        var missing = items.Where(i => !i.Done && i.Key != "publish").ToList();
        return missing.Count == 0 ? null
            : "ยังตั้งค่าไม่ครบ " + missing.Count + " ข้อ:\n• " + string.Join("\n• ", missing.Select(i => i.Label + (i.Detail == null ? "" : " — " + i.Detail)))
              + "\n\nเผยแพร่ได้ แต่ลูกค้าอาจจ่ายเงิน/จองไม่ได้ในข้อที่ขาด";
    }
}
