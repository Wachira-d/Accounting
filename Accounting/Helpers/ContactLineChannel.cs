using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L2 (LINE_MULTICHANNEL_DESIGN §3.d/§3.e · คำตอบเจ้าของ §10 Q7 · คำตัดสิน N8) — <b>ตัวตัดสินเดียว</b> (pure) ของ "ช่องทางต่อกฎ" ในตารางส่ง:
/// ติ๊กอีเมล/LINE ผู้ใช้/กลุ่ม LINE ได้พร้อมกัน (Q7 "ทั้งสองทาง") · <c>LineFallbackToEmail</c> ค่าตั้งต้น <b>true</b> (Q7 "อัตโนมัติ") ·
/// L2 ส่ง LINE ผ่าน <b>OA ของบริษัทเท่านั้น</b> (N8) · ผู้เข้าคิว <c>EmailScheduleService.EnqueueLineAsync</c> · ผู้ส่ง <c>ContactLineQueueService</c>
/// <list type="bullet">
/// <item>trigger ที่ส่ง LINE ได้ใน L2 = เอกสารฝั่งขาย (อนุมัติ · ใกล้/เกินกำหนด · รายการประจำ) · สลิปเงินเดือนห้ามเข้าช่อง LINE ของผู้ติดต่อ (PDPA) ·
/// ใบแจ้งยอด/50 ทวิ = ยังไม่รองรับ (L6) — ติ๊กแล้วบันทึกไม่ได้พร้อมเหตุ (ห้าม silent no-op)</item>
/// <item>ถอยไปอีเมล (Q7): เฉพาะกฎที่<b>ไม่ได้</b>ติ๊กอีเมล (ติ๊กอีเมลอยู่แล้ว = อีเมลออกอยู่แล้ว) — ตอนเข้าคิว: ไม่มีปลายทาง LINE ที่รับเรื่องนี้ ⇒ เข้าคิวอีเมลแทน ·
/// ตอนส่ง: ล้มถาวร (บล็อก · โควตา OA หมด · token เสีย) และไม่มีปลายทาง LINE อื่นของใบเดียวกันที่ส่งแล้ว/รอส่ง ⇒ เข้าคิวอีเมลครั้งเดียว</item>
/// </list>
/// </summary>
public static class ContactLineChannel
{
    public const string RuleCode = "NOTIFY-LINE-RULE-CHANNEL";

    public const string StatusPending = "Pending";
    public const string StatusSending = "Sending";
    public const string StatusSent = "Sent";
    public const string StatusFailed = "Failed";
    public const string StatusSkipped = "Skipped";
    public const string StatusCancelled = "Cancelled";

    /// <summary>trigger ที่ส่ง LINE ได้ในเฟส L2 (การ์ดเอกสารหนึ่งใบ)</summary>
    public static readonly IReadOnlySet<string> LineTriggers = new HashSet<string>(StringComparer.Ordinal)
    {
        "DocumentApproved", "DocumentDueSoon", "DocumentOverdue", "RecurringInvoiceCreated",
    };

    public static bool SupportsLine(string? trigger) => trigger != null && LineTriggers.Contains(trigger);

    public static bool WantsLine(bool lineUser, bool lineGroup) => lineUser || lineGroup;

    /// <summary>ด่านตอนบันทึกกฎ — null = ผ่าน · <paramref name="companyOaReady"/> = OA ของบริษัทพร้อมส่ง (เปิด + token/secret ถอดได้)</summary>
    public static string? RuleProblem(string? trigger, bool email, bool lineUser, bool lineGroup, bool companyOaReady, string? companyOaProblem)
    {
        if (!email && !WantsLine(lineUser, lineGroup))
            return "เลือกช่องทางอย่างน้อย 1 ช่อง (อีเมล · LINE ผู้ใช้ของผู้ติดต่อ · กลุ่ม LINE ของผู้ติดต่อ)";
        if (!WantsLine(lineUser, lineGroup)) return null;
        if (trigger == "PayrollPaid")
            return "สลิปเงินเดือนส่งทาง LINE ของผู้ติดต่อไม่ได้ (ข้อมูลส่วนบุคคลของพนักงาน — ส่งได้เฉพาะ 1:1 ถึงพนักงานตามเส้นสลิปเดิม)";
        if (!SupportsLine(trigger))
            return "เรื่องนี้ยังส่งทาง LINE ไม่ได้ (รองรับ: เมื่ออนุมัติเอกสาร · ใกล้/เกินกำหนด · รายการประจำ) — ใบแจ้งยอด/50 ทวิ ทาง LINE อยู่ในเฟสถัดไป · ติ๊กเฉพาะอีเมล";
        if (!companyOaReady)
            return "ยังไม่มีช่อง LINE ของบริษัท — ตั้ง LINE OA ของบริษัทให้พร้อม (เปิดใช้ + Channel Access Token + Channel Secret) ที่ ตั้งค่า → แจ้งเตือนทาง LINE"
                   + (string.IsNullOrWhiteSpace(companyOaProblem) ? "" : " · " + companyOaProblem)
                   + " (ส่งถึงผู้ติดต่อผ่าน LINE OA กลางของ NextAcc ยังไม่เปิด — รอโควตา LINE แยกช่อง)";
        return null;
    }

    /// <summary>ส่งอีเมลของใบนี้ไหม — ติ๊กอีเมล · ถอยมาจาก LINE · หรือผู้เรียกบังคับ (เส้นถอยตอนส่ง)</summary>
    public static bool EmailWanted(bool channelEmail, bool fallbackNow, bool forceEmail) => forceEmail || channelEmail || fallbackNow;

    /// <summary>แผนเข้าคิว LINE ของใบหนึ่ง — <paramref name="Endpoints"/> = ปลายทางที่ส่ง (ชนิดที่ติ๊ก + ใช้ได้ + รับเรื่องนี้) ·
    /// <paramref name="FallbackNow"/> = ไม่มีปลายทาง LINE ⇒ เข้าคิวอีเมลแทน (Q7) · <paramref name="NotSent"/> = เหตุเมื่อไม่ส่งทาง LINE เลย</summary>
    public sealed record LinePlan(IReadOnlyList<ContactRecipientRule.RecipientOption> Endpoints, bool FallbackNow, string? NotSent);

    public static LinePlan Plan(bool channelEmail, bool lineUser, bool lineGroup, bool fallbackToEmail,
        IReadOnlyList<ContactRecipientRule.RecipientOption> options, string? autoSendBlock)
    {
        if (!WantsLine(lineUser, lineGroup)) return new(Array.Empty<ContactRecipientRule.RecipientOption>(), false, null);
        // ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" (คำตัดสิน 250-29) ชนะทุกช่อง — ไม่ถอยไปอีเมล (อีเมลก็ถูกกันด้วยเหตุเดียวกัน)
        if (autoSendBlock != null) return new(Array.Empty<ContactRecipientRule.RecipientOption>(), false, autoSendBlock);
        bool Ticked(string kind) => (kind == NotifyEndpointKind.LineUser && lineUser) || (kind == NotifyEndpointKind.LineGroup && lineGroup);
        var chosen = options.Where(o => o.Available && o.EndpointId != null && Ticked(o.Kind)).ToList();
        if (chosen.Count > 0) return new(chosen, false, null);
        var reasons = options.Where(o => Ticked(o.Kind) && !o.Available && !string.IsNullOrWhiteSpace(o.Reason))
            .Select(o => $"{o.Display}: {o.Reason}").Distinct().ToList();
        var why = reasons.Count == 0
            ? "ผู้ติดต่อยังไม่มีปลายทาง LINE ที่รับเรื่องนี้ (ผูกได้ที่หน้าผู้ติดต่อ แท็บช่องทางแจ้งเตือน)"
            : "ไม่ส่งทาง LINE — " + string.Join(" · ", reasons);
        var fallback = fallbackToEmail && !channelEmail;
        return new(Array.Empty<ContactRecipientRule.RecipientOption>(), fallback, fallback ? why + " ⇒ ส่งอีเมลแทน (ค่าตั้งถอยไปอีเมล)" : why);
    }

    /// <summary>กุญแจกันซ้ำของแถวคิว LINE ต่อปลายทาง (ตั้งจากกุญแจเดียวกับคิวอีเมลของใบนั้น)</summary>
    public static string QueueKey(string baseKey, Guid? endpointId)
        => $"{baseKey}:line:{(endpointId is Guid id ? id.ToString("N") : "none")}";

    /// <summary><c>X-Line-Retry-Key</c> ของแถวคิว — UUID ที่ได้จาก hash(บริษัท + กุญแจกันซ้ำ = เหตุการณ์ + ผู้รับ) ⇒ ลองใหม่กี่ครั้งก็ key เดิม (LINE ไม่ส่งซ้ำ) ·
    /// ผู้ใช้กด "ส่งอีกครั้ง" เอง ⇒ ผู้เรียกสุ่ม key ใหม่ (ตั้งใจส่งซ้ำ)</summary>
    public static Guid RetryKey(Guid companyId, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"line-retry|{companyId:N}|{idempotencyKey}"));
        var g = new byte[16];
        Array.Copy(bytes, g, 16);
        g[7] = (byte)((g[7] & 0x0F) | 0x40);   // version 4 (ตำแหน่งไบต์ของ Guid แบบ little-endian)
        g[8] = (byte)((g[8] & 0x3F) | 0x80);   // variant RFC 4122
        return new Guid(g);
    }

    /// <summary>suffix ของคิวอีเมลที่ถอยมาจาก LINE (ส่วนท้าย ":line-fallback" ⇒ ตัวแยกกุญแจเดิมยังอ่าน trigger/รายการประจำได้) · กุญแจไม่ใช่ของใบนี้ ⇒ null</summary>
    public static string? FallbackSuffix(string? baseKey, Guid documentId, string? trigger)
    {
        if (string.IsNullOrEmpty(baseKey) || string.IsNullOrEmpty(trigger)) return null;
        var prefix = $"doc:{documentId}:{trigger}:";
        if (!baseKey.StartsWith(prefix, StringComparison.Ordinal) || baseKey.Length == prefix.Length) return null;
        return baseKey[prefix.Length..] + ":line-fallback";
    }

    /// <summary>ล้มถาวรแล้วต้องเข้าคิวอีเมลแทนไหม — แถวนี้ถอยได้ · ยังไม่เคยถอย · ไม่มีปลายทาง LINE อื่นของใบเดียวกันที่ส่งแล้ว/รอส่ง</summary>
    public static bool FallbackNeeded(bool rowFallback, bool eligible, bool alreadyQueued, IEnumerable<string> siblingStatuses)
        => rowFallback && eligible && !alreadyQueued
           && !siblingStatuses.Any(s => s == StatusSent || s == StatusPending || s == StatusSending);

    public static string StatusLabel(string? status) => status switch
    {
        StatusPending => "รอส่ง",
        StatusSending => "กำลังส่ง",
        StatusSent => "ส่งแล้ว",
        StatusFailed => "ล้มเหลว",
        StatusSkipped => "ข้าม (ไม่ส่ง)",
        StatusCancelled => "ยกเลิก",
        _ => string.IsNullOrWhiteSpace(status) ? "ไม่ทราบ" : status!,
    };
}
