using System.Globalization;
using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของการชำระหนึ่งครั้งที่ข้อความแจ้งเตือนต้องใช้ — ผู้เรียกโหลดจากฐาน (กรอง CompanyId) แล้วส่งมา · helper ไม่แตะฐาน</summary>
/// <param name="IsInflow">เงินเข้า (ลูกค้าจ่ายเรา) — ผลของ <c>Helpers/CashFlowDirection.IsInflow</c> (รอบ 250) ตัวเดียวกับที่ใช้ปรับยอดธนาคาร (ใบเพิ่มหนี้ฝั่งซื้อ = เงินออก)</param>
/// <param name="LineDescriptions">คำอธิบายบรรทัดตามลำดับ (บรรทัดที่ไม่ถูกลบ) — ว่าง = ไม่มีบรรทัดรายการในข้อความ</param>
/// <param name="Currency">สกุลเงินของเอกสาร (ยอดชำระอยู่ในสกุลนี้) — null/THB = "บาท"</param>
public sealed record PaymentNotificationFacts(
    bool IsInflow,
    DocumentType DocumentType,
    string? DocumentNumber,
    string? ContactName,
    IReadOnlyList<string?>? LineDescriptions,
    decimal Amount,
    string? Currency,
    PaymentMethod Method,
    decimal BalanceDue);

/// <summary>ผลประกอบข้อความ — <see cref="EventKey"/> ตัดสินตามฝั่งเงิน (ผู้เรียกห้ามเลือกคีย์เอง)</summary>
public sealed record PaymentNotification(string EventKey, string Title, string Message);

/// <summary>รอบ 249 — ข้อความแจ้งเตือน "รับ/จ่ายชำระเงินแล้ว" ของเอกสาร · pure · ตัวตัดสินเดียว (ฝั่ง · คีย์เหตุการณ์ · หัวข้อ · เนื้อความ)
/// <para>ที่มา (เจ้าของรายงาน 2026-11-06): กลุ่ม LINE ได้ "💵 รับชำระเงินแล้ว 📄 PI-20260917-0007 💰 1,733.40 บาท" —
/// (1) PI = ใบตั้งหนี้ฝั่งซื้อ ⇒ เป็นเงินที่<b>เราจ่าย</b> แต่หัวข้อบอก "รับชำระ" · (2) ไม่บอกว่าจ่ายให้/รับจากใคร รายการอะไร ·
/// (3) ลิงก์เป็น path สัมพัทธ์ (แก้ที่ <see cref="NotificationLink"/>)</para>
/// <list type="bullet">
/// <item>เงินเข้า ⇒ <see cref="NotificationEvents.PaymentReceived"/> · "💵 รับชำระเงินแล้ว" · "จาก &lt;ลูกค้า&gt;"</item>
/// <item>เงินออก ⇒ <see cref="NotificationEvents.PaymentMade"/> · "💸 จ่ายชำระเงินแล้ว" · "ให้ &lt;ผู้ขาย&gt;" — คีย์แยก ⇒ บริษัทตั้งปิด/เปิดฝั่งจ่ายแยกจากฝั่งรับได้</item>
/// <item>รายการ = บรรทัดแรก (ตัดที่ <see cref="ItemSummaryMaxChars"/> ตัว) + "และอีก N รายการ" · ไม่มีชื่อคู่ค้า/บรรทัด ⇒ ไม่มีบรรทัดนั้น (ไม่แต่งค่า)</item>
/// </list></summary>
public static class PaymentNotificationText
{
    public const int ItemSummaryMaxChars = 60;
    public const string ReceivedTitle = "💵 รับชำระเงินแล้ว";
    public const string PaidTitle = "💸 จ่ายชำระเงินแล้ว";

    public static PaymentNotification Build(PaymentNotificationFacts f)
    {
        var lines = new List<string>();
        var number = OneLine(f.DocumentNumber);
        lines.Add("📄 " + DocumentTypeNames.Title(f.DocumentType, "th") + (number.Length > 0 ? " " + number : ""));

        var contact = OneLine(f.ContactName);
        if (contact.Length > 0)
            lines.Add((f.IsInflow ? "👤 จาก " : "👤 ให้ ") + contact);

        var items = ItemSummary(f.LineDescriptions);
        if (items != null) lines.Add("🧾 " + items);

        lines.Add("💰 " + AmountText(f.Amount, f.Currency) + " · " + PaymentMethodNames.Thai(f.Method));
        lines.Add(f.BalanceDue <= 0.01m ? "✅ ชำระครบแล้ว" : "⏳ คงค้าง " + AmountText(f.BalanceDue, f.Currency));

        return new PaymentNotification(
            f.IsInflow ? NotificationEvents.PaymentReceived : NotificationEvents.PaymentMade,
            f.IsInflow ? ReceivedTitle : PaidTitle,
            string.Join("\n", lines));
    }

    /// <summary>สรุปรายการ: บรรทัดแรกที่มีข้อความ (ยุบขึ้นบรรทัด/ช่องว่างซ้ำ · ยาวเกิน ⇒ ตัด + "…") + " และอีก N รายการ" เมื่อมีหลายบรรทัด · ไม่มี ⇒ null</summary>
    private static string? ItemSummary(IReadOnlyList<string?>? descriptions)
    {
        if (descriptions == null) return null;
        var items = descriptions.Select(OneLine).Where(s => s.Length > 0).ToList();
        if (items.Count == 0) return null;
        var first = Truncate(items[0], ItemSummaryMaxChars);
        return items.Count > 1 ? $"{first} และอีก {items.Count - 1} รายการ" : first;
    }

    /// <summary>ยอดเงิน "1,733.40 บาท" (THB/ว่าง) หรือ "1,200.00 USD" — รูปแบบตายตัว ไม่ขึ้นกับ culture ของเครื่อง</summary>
    private static string AmountText(decimal amount, string? currency)
    {
        var cur = (currency ?? "").Trim().ToUpperInvariant();
        var n = amount.ToString("N2", CultureInfo.InvariantCulture);
        return cur.Length == 0 || cur == "THB" ? n + " บาท" : n + " " + cur;
    }

    private static string OneLine(string? s)
        => string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;   // ไม่ตัดกลางอีโมจิ/อักขระคู่
        return s[..cut].TrimEnd() + "…";
    }
}
