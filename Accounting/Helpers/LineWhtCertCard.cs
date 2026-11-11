using System.Globalization;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 แพ็ก B (คำตัดสิน 258-3-6 · 259-2-1) — <b>ทวงหนังสือรับรอง 50 ทวิ ทาง LINE: ตัวตัดสินช่องทาง + ตัวประกอบการ์ด</b> (pure)
/// <list type="bullet">
/// <item>ค่าตั้งตัวเดียว <c>CompanySettings.WhtCertReminderEnabled</c> คุมทั้งอีเมลและ LINE (ไม่มีค่าตั้งที่สอง) · จังหวะ/กันซ้ำจาก <see cref="WhtCertReminderCadence"/> ตัวเดิม
/// (หนึ่งขั้นต่อลูกค้า — อีเมลและการ์ด LINE ของขั้นเดียวกันอยู่ในบันทึกทวงแถวเดียว)</item>
/// <item>ช่องทาง (<see cref="Channels"/>): ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะทุกช่อง · อีเมลที่รับเรื่อง 50 ทวิ ⇒ อีเมล · ปลายทาง LINE ที่รับเรื่อง 50 ทวิ ⇒ การ์ดต่อปลายทาง ·
/// ไม่มีทั้งสอง ⇒ "ข้าม" พร้อมเหตุ (ล้มดัง) — เดิม (รอบ 253) ไม่มีอีเมล = ข้ามเสมอแม้ผูก LINE ไว้</item>
/// <item>การ์ด (<see cref="Build"/>): ส่งให้น้อยที่สุด (PDPA ม.22) — เลขที่เอกสาร · วันชำระ · ภาษีที่หัก (ซ่อนได้ต่อกลุ่ม) · นามที่ต้องออกใบ · ปุ่มอัปโหลดต่อแถวคิว
/// (<see cref="WhtCertUploadLinkPolicy.TokenForLineQueue"/>) · กลุ่มที่ "ซ่อนยอด" ⇒ ไม่มีปุ่ม (หน้าอัปโหลดแสดงยอดที่ค้าง — ตัวตัดสินเดียวกับปุ่มดู PDF
/// <see cref="LineDocumentViewLink.EndpointAllows"/>) · ทางเลิกรับทุกการ์ด (C5)</item>
/// </list>
/// </summary>
public static class LineWhtCertCard
{
    public const string RuleCode = "WHT-CERT-REMINDER-LINE";

    /// <summary>ชนิดแถวคิว LINE (<c>LineQueue.EntityType</c>) — <c>EntityId</c> = ผู้ติดต่อ (ไม่ใช่เอกสาร ⇒ ลิงก์ดู PDF ไม่รับแถวชนิดนี้)</summary>
    public const string QueueEntityType = "WhtCertRequest";

    /// <summary>trigger ของแถวคิว LINE (ผู้ส่งคิวแยกเส้นด้วยค่านี้ — ไม่ใช่ trigger ของกฎตารางส่ง)</summary>
    public const string QueueTrigger = "WhtCertReminder";

    public const int MaxItems = 8;

    /// <summary>ผลตัดสินช่องทางของขั้นหนึ่ง — <paramref name="SkipReason"/> ไม่ว่าง ⇒ ไม่ส่งทางไหนเลย (แถวบันทึก "ข้าม")</summary>
    public sealed record ReminderChannels(bool Email, bool Line, string? SkipReason);

    public const string NoChannelReason =
        "ผู้ติดต่อไม่มีอีเมลหรือ LINE ที่รับเรื่อง 50 ทวิ — ทวง 50 ทวิ อัตโนมัติไม่ได้ (เพิ่มอีเมล/ผูก LINE ที่หน้าผู้ติดต่อ หรือส่งลิงก์อัปโหลดเอง)";

    /// <param name="autoSendBlock">เหตุของผู้ติดต่อ "ไม่ส่งอัตโนมัติ" (<c>AutopilotSettingsResolver.AutoSendBlock</c>) · null = ไม่บล็อก</param>
    /// <param name="contactDeleted">ผู้ติดต่อถูกลบ ⇒ ไม่ส่งทุกช่อง</param>
    /// <param name="emailDeliverable">มีอีเมลที่รับเรื่อง 50 ทวิ (<c>ContactRecipientRule.ForEmail</c>)</param>
    /// <param name="emailOptOutReason">เหตุที่อีเมลไม่รับ (ใช้เป็นเหตุเมื่อไม่มี LINE ด้วย)</param>
    /// <param name="lineEndpoints">จำนวนปลายทาง LINE ที่ใช้ได้และรับเรื่อง 50 ทวิ (<c>ContactLineChannel.LineEndpoints</c>)</param>
    public static ReminderChannels Channels(string? autoSendBlock, bool contactDeleted, bool emailDeliverable, string? emailOptOutReason, int lineEndpoints)
    {
        if (autoSendBlock != null) return new(false, false, autoSendBlock);
        if (contactDeleted) return new(false, false, ContactRecipientRule.ContactDeletedReason);
        if (!emailDeliverable && lineEndpoints <= 0) return new(false, false, emailOptOutReason ?? NoChannelReason);
        return new(emailDeliverable, lineEndpoints > 0, null);
    }

    /// <summary>กุญแจกันซ้ำต่อปลายทาง (unique ต่อบริษัท) — จากกุญแจของขั้นเดียวกับอีเมล (<see cref="WhtCertReminderCadence.QueueKey"/>)</summary>
    public static string QueueKey(string baseKey, Guid endpointId) => ContactLineChannel.QueueKey(baseKey, endpointId);

    /// <summary>ปลายทางนี้ได้ปุ่มอัปโหลดไหม — ตัวเดียวกับปุ่มดู PDF (กลุ่มซ่อนยอดไม่ได้)</summary>
    public static bool UploadLinkAllowed(bool isGroup, bool showAmounts)
        => LineDocumentViewLink.EndpointAllows(isGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.LineUser, showAmounts, null, null);

    /// <param name="ShowAmounts">false = กลุ่มที่ปิดแสดงยอด ⇒ ไม่แสดงภาษีที่หัก</param>
    /// <param name="UploadUrl">ลิงก์อัปโหลด (https เท่านั้น · null = ไม่มีปุ่ม)</param>
    public sealed record Input(string CompanyName, string? CompanyTaxId, string BranchLabel, string? ContactName, int Step,
        IReadOnlyList<WhtCertReminderCadence.Item> Items, bool ShowAmounts, string? UploadUrl, bool IsGroup);

    public static LineDocumentCard.Card Build(Input i)
    {
        var company = LineDocumentCard.Clean(i.CompanyName, 120);
        var body = new List<object>
        {
            new { type = "text", text = "จาก " + company, size = "xs", color = "#64748b", wrap = true },
            new { type = "text", text = "ขอหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ)", weight = "bold", size = "md", wrap = true },
        };
        if (!string.IsNullOrWhiteSpace(i.ContactName))
            body.Add(new { type = "text", text = "เรียน " + LineDocumentCard.Clean(i.ContactName, 120), size = "sm", color = "#334155", wrap = true });
        body.Add(new { type = "text", text = i.Step >= WhtCertReminderCadence.InitialSteps[^1]
            ? "ยังไม่ได้รับหนังสือรับรองของรายการต่อไปนี้ (บริษัทต้องใช้ยื่น ภ.ง.ด.50)"
            : "ยังไม่ได้รับหนังสือรับรองของรายการต่อไปนี้", size = "sm", wrap = true });
        foreach (var it in i.Items.Take(MaxItems))
        {
            var line = new StringBuilder(LineDocumentCard.Clean(it.DocumentNumber ?? "รายการรับชำระ", 60));
            if (it.WithheldSinceUtc is DateTime d) line.Append(" · ชำระ ").Append(ThaiDate.ToThaiDisplayString(d));
            if (i.ShowAmounts) line.Append(" · หัก ").Append(it.Missing.ToString("N2", CultureInfo.InvariantCulture)).Append(" บาท");
            body.Add(new { type = "text", text = "• " + line, size = "xs", color = "#334155", wrap = true });
        }
        if (i.Items.Count > MaxItems)
            body.Add(new { type = "text", text = $"และอีก {i.Items.Count - MaxItems} รายการ", size = "xs", color = "#64748b", wrap = true });
        var issueTo = new StringBuilder("ออกในนาม ").Append(company);
        if (!string.IsNullOrWhiteSpace(i.CompanyTaxId)) issueTo.Append(" เลขผู้เสียภาษี ").Append(LineDocumentCard.Clean(i.CompanyTaxId, 13));
        issueTo.Append(" (").Append(LineDocumentCard.Clean(i.BranchLabel, 40)).Append(')');
        body.Add(new { type = "text", text = issueTo.ToString(), size = "xs", color = "#64748b", wrap = true });

        var footer = new List<object>();
        if (LineDocumentCard.SafeUrl(i.UploadUrl) is string up)
            footer.Add(new { type = "button", style = "primary", height = "sm", action = new { type = "uri", label = "อัปโหลดหนังสือรับรอง", uri = up } });
        else
            footer.Add(new { type = "text", text = "ส่งหนังสือรับรองถึงผู้ขายทางอีเมลหรือช่องทางที่ติดต่อกันอยู่", size = "xs", color = "#64748b", wrap = true });
        // ทางเลิกรับ (C5) — ข้อความเดียวกับการ์ดเอกสาร (DocumentLabels ภาษาไทย · การ์ดทวงเป็นภาษาไทยแบบเดียวกับอีเมลทวง)
        var l = Accounting.Services.Implementations.Pdf.DocumentLabels.For("th");
        footer.Add(new { type = "text", text = i.IsGroup ? l.LineCardStopGroup : l.LineCardStopUser, size = "xxs", color = "#94a3b8", wrap = true });

        var contents = new
        {
            type = "bubble",
            body = new { type = "box", layout = "vertical", spacing = "sm", contents = body },
            footer = new { type = "box", layout = "vertical", spacing = "sm", contents = footer },
        };
        var alt = $"ขอหนังสือรับรอง 50 ทวิ — จาก {company} · {i.Items.Count} รายการ";
        return new LineDocumentCard.Card(LineMessageLimits.Truncate(alt, LineDocumentCard.MaxAltText), contents);
    }
}
