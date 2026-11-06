namespace Accounting.Helpers;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §4.3 · คำตัดสินเจ้าของ §11 ข้อ 4) — <b>จังหวะทวงหนังสือรับรอง 50 ทวิ: ตัวตัดสินเดียว</b> (pure)
///
/// <para>ทวงวันที่ 7 · 21 · 45 หลังถูกหัก (นับจากรายการที่ค้างเก่าสุดของลูกค้ารายนั้น) แล้วทวงรวมรายเดือนทุก 30 วัน สูงสุด 6 ครั้ง ·
/// รวมรายลูกค้า (1 อีเมลต่อลูกค้าต่อรอบ — ไม่ส่งรายใบ) · ตาข่ายนิรภัยเพิ่ม: ไม่เกิน <see cref="MaxPerRolling30Days"/> ฉบับต่อลูกค้าใน 30 วัน และ
/// เว้นอย่างน้อย <see cref="MinGapDays"/> วันระหว่างสองฉบับ (กันอีเมลถี่เมื่อรายการเก่าถูกปิดแล้วรายการถัดไปขึ้นมาเป็นรายการเก่าสุด)</para>
///
/// <para>ผู้เรียก: <c>WhtCertReminderService.RunCompanyAsync</c> (งานรายวัน) · ป้ายสถานะทวงบนหน้าทะเบียนอ่านจากแถว <c>WhtCertReminderLog</c></para>
/// </summary>
public static class WhtCertReminderCadence
{
    public const string RuleCode = "WHT-CERT-REMINDER";

    /// <summary>ชนิดแถวคิวอีเมล (<c>EmailQueue.EntityType</c>) ของอีเมลทวง — <c>EntityId</c> = ผู้ติดต่อ · ผู้ส่งคิวตรวจซ้ำตอนส่ง</summary>
    public const string QueueEntityType = "WhtCertReminder";

    /// <summary>ที่หมายของลิงก์อัปโหลดในเนื้ออีเมลที่เข้าคิว — คิวไม่เก็บ token · ผู้ส่งคิวออกลิงก์ใหม่ตอนส่งแล้วแทนที่เฉพาะข้อความที่ส่ง</summary>
    public const string UploadLinkPlaceholder = "{WhtUploadLink}";

    /// <summary>ขั้นแรก 3 ขั้น (วันหลังถูกหัก) — §4.3</summary>
    public static readonly IReadOnlyList<int> InitialSteps = new[] { 7, 21, 45 };

    /// <summary>ทวงรวมรายเดือนหลังขั้นที่ 3 — ทุก 30 วัน สูงสุด 6 ครั้ง (§11 ข้อ 4)</summary>
    public const int MonthlyEveryDays = 30;
    public const int MonthlyMaxCount = 6;

    /// <summary>ตาข่าย: ฉบับต่อลูกค้าใน 30 วันล่าสุดไม่เกินนี้</summary>
    public const int MaxPerRolling30Days = 6;

    /// <summary>ตาข่าย: เว้นระหว่างสองฉบับถึงลูกค้าเดียวกันอย่างน้อยกี่วัน</summary>
    public const int MinGapDays = 7;

    /// <summary>ทุกขั้น (วันหลังถูกหัก) — 7, 21, 45, 75, 105, 135, 165, 195, 225</summary>
    public static IReadOnlyList<int> Steps { get; } = InitialSteps
        .Concat(Enumerable.Range(1, MonthlyMaxCount).Select(i => InitialSteps[^1] + i * MonthlyEveryDays)).ToList();

    /// <summary>ขั้นที่ถึงกำหนดสำหรับรายการที่ค้างมาแล้ว <paramref name="daysWaiting"/> วัน (ขั้นล่าสุดที่ ≤ จำนวนวัน) · ยังไม่ถึงขั้นแรก ⇒ null</summary>
    internal static int? DueStep(int daysWaiting)
    {
        int? due = null;
        foreach (var s in Steps) if (s <= daysWaiting) due = s;
        return due;
    }

    /// <summary>หนึ่งฉบับที่ส่งไปแล้วถึงลูกค้ารายนี้ — <paramref name="Step"/> ขั้นที่ส่ง · <paramref name="AnchorUtc"/> วันถูกหักของรายการเก่าสุด ณ ตอนส่ง</summary>
    public sealed record Sent(int Step, DateTime AnchorUtc, DateTime SentAtUtc);

    /// <summary>ผลตัดสิน — <paramref name="Send"/> true ⇒ เข้าคิวขั้น <paramref name="Step"/> · false ⇒ <paramref name="Reason"/> (เพื่อบันทึก/เทสต์ · ไม่ใช่ข้อความถึงลูกค้า)</summary>
    public sealed record Decision(bool Send, int? Step, string Reason);

    /// <param name="enabled">บริษัทเปิด "ทวง 50 ทวิ อัตโนมัติ" (<c>CompanySettings.WhtCertReminderEnabled</c>)</param>
    /// <param name="oldestWithheldUtc">วันถูกหักของรายการที่ยังค้างใบเก่าสุดของลูกค้า (เฉพาะสถานะที่ต้องทวง) · null = ไม่มีรายการต้องทวง</param>
    /// <param name="history">ฉบับที่เคยเข้าคิวถึงลูกค้ารายนี้ (ทุกช่วง) — ผู้ติดต่อที่ไม่รับ (ไม่ส่งอัตโนมัติ/ยกเลิกรับเรื่อง 50 ทวิ) ผู้เรียกตัดสินหลังได้ "ถึงกำหนด"
    /// แล้วบันทึกแถว "ข้าม" พร้อมเหตุ (ล้มดัง ไม่เงียบ)</param>
    public static Decision Decide(bool enabled, DateTime? oldestWithheldUtc, IReadOnlyCollection<Sent> history, DateTime nowUtc)
    {
        if (!enabled) return new Decision(false, null, "บริษัทยังไม่เปิดทวง 50 ทวิ อัตโนมัติ");
        if (oldestWithheldUtc is not DateTime anchor) return new Decision(false, null, "ไม่มีรายการที่ต้องทวง");
        var days = Math.Max(0, (int)Math.Floor((nowUtc - anchor).TotalDays));
        if (DueStep(days) is not int step) return new Decision(false, null, $"ค้าง {days} วัน — ยังไม่ถึงขั้นแรก ({InitialSteps[0]} วัน)");
        // ขั้นนี้ (หรือขั้นที่สูงกว่า) ของรายการเก่าสุดชุดเดียวกันเคยส่งแล้ว ⇒ ไม่ส่งซ้ำ (ขั้นที่ข้ามไประหว่างปิดงาน = ส่งเฉพาะขั้นล่าสุดครั้งเดียว)
        if (history.Any(h => h.AnchorUtc.Date == anchor.Date && h.Step >= step))
            return new Decision(false, step, $"ขั้น {step} วันส่งแล้ว");
        if (history.Count(h => h.AnchorUtc.Date == anchor.Date && h.Step > InitialSteps[^1]) >= MonthlyMaxCount && step > InitialSteps[^1])
            return new Decision(false, step, $"ทวงรายเดือนครบ {MonthlyMaxCount} ครั้งแล้ว");
        if (history.Any(h => (nowUtc - h.SentAtUtc).TotalDays < MinGapDays))
            return new Decision(false, step, $"เพิ่งทวงไปไม่ถึง {MinGapDays} วัน");
        if (history.Count(h => (nowUtc - h.SentAtUtc).TotalDays < 30) >= MaxPerRolling30Days)
            return new Decision(false, step, $"ทวงครบ {MaxPerRolling30Days} ฉบับใน 30 วันแล้ว");
        return new Decision(true, step, $"ค้าง {days} วัน — ทวงขั้น {step} วัน");
    }

    /// <summary>กุญแจกันซ้ำของคิวอีเมล — ลูกค้า + ขั้น + วันถูกหักของรายการเก่าสุด (สองเครื่อง/สองรอบได้แถวเดียว)</summary>
    public static string QueueKey(Guid contactId, int step, DateTime anchorUtc) => $"whtrem:{contactId:N}:{step}:{anchorUtc:yyyyMMdd}";

    /// <summary>หัวเรื่องอีเมลทวง (ขั้นที่ 45 วันขึ้นไปบอกว่าบริษัทต้องใช้ยื่น ภ.ง.ด.50 — §4.3)</summary>
    public static string Subject(string companyName, int step)
        => step >= InitialSteps[^1]
            ? $"ขอหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) — {companyName} ต้องใช้ประกอบการยื่น ภ.ง.ด.50"
            : $"ขอหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) — {companyName}";

    /// <summary>หนึ่งบรรทัดของรายการที่ค้างในอีเมล</summary>
    public sealed record Item(string? DocumentNumber, DateTime? WithheldSinceUtc, decimal Missing);

    /// <summary>เนื้ออีเมล (HTML · ทุกค่าหนีก่อนต่อ — กฎ #4 C) — รายการทุกใบที่ค้างของลูกค้ารายนี้ในอีเมลเดียว + ที่หมายลิงก์อัปโหลด ·
    /// ไม่แสดงข้อมูลของลูกค้ารายอื่น · วันที่แสดงแบบ พ.ศ.</summary>
    public static string BodyHtml(string companyName, string? companyTaxId, string branchLabel, string contactName, int step, IReadOnlyList<Item> items)
    {
        static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var sb = new System.Text.StringBuilder();
        sb.Append("<p>เรียน ").Append(E(contactName)).Append("</p>");
        sb.Append("<p>").Append(E(companyName)).Append(" ยังไม่ได้รับหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) สำหรับรายการต่อไปนี้");
        if (step >= InitialSteps[^1]) sb.Append(" ซึ่งบริษัทต้องใช้ประกอบการยื่นแบบ ภ.ง.ด.50");
        sb.Append("</p><ul>");
        foreach (var i in items)
        {
            sb.Append("<li>").Append(E(i.DocumentNumber ?? "รายการรับชำระ"));
            if (i.WithheldSinceUtc is DateTime d)
                sb.Append(" · ชำระวันที่ ").Append(E(ThaiDate.ToThaiDisplayString(d)));
            sb.Append(" · ภาษีที่หัก ").Append(E(i.Missing.ToString("N2", System.Globalization.CultureInfo.InvariantCulture))).Append(" บาท</li>");
        }
        sb.Append("</ul><p>โปรดออกหนังสือรับรองในนาม <b>").Append(E(companyName)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(companyTaxId)) sb.Append(" เลขประจำตัวผู้เสียภาษี ").Append(E(companyTaxId));
        sb.Append(" (").Append(E(branchLabel)).Append(")</p>");
        sb.Append("<p>ส่งไฟล์หนังสือรับรอง (PDF หรือรูปถ่าย) ได้ทันทีที่ลิงก์นี้ — ไม่ต้องเข้าสู่ระบบ: <a href='")
            .Append(UploadLinkPlaceholder).Append("'>อัปโหลดหนังสือรับรอง</a></p>");
        sb.Append("<p style='color:#64748b;font-size:12px'>ถ้าส่งให้แล้ว ขออภัยและขอบคุณค่ะ — ระบบจะหยุดทวงเมื่อได้รับใบครบ</p>");
        return sb.ToString();
    }
}
