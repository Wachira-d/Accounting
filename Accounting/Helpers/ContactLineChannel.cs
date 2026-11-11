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

    /// <summary>รอบ 258-3 (F-H1 · A-M4): trigger ของการ์ด "ใบเสร็จหลังรับชำระ" — ผู้เข้าคิว <c>PostPaymentAutomationService</c> ขั้น <c>LineReceipt</c>
    /// (ไม่ใช่ trigger ของกฎตารางส่ง — ไม่อยู่ใน <see cref="LineTriggers"/> ⇒ ฟอร์มกฎตารางส่งไม่เปิดเรื่องนี้ · ค่าตั้งอยู่ที่ "หลังลูกค้าชำระ: ออกใบเสร็จ…แล้วส่ง")</summary>
    public const string ReceiptTrigger = "ReceiptIssued";

    /// <summary>trigger ที่ผู้ส่งคิว LINE รับได้ = กฎตารางส่ง + ใบเสร็จหลังรับชำระ</summary>
    public static bool SenderSupports(string? trigger) => SupportsLine(trigger) || trigger == ReceiptTrigger;

    /// <summary>รอบ 258-3 (F-H1): ปลายทาง LINE ที่ใช้ส่งได้ของชุดผู้รับ (เรื่องตามที่ตัวหาผู้รับเดียวกรองแล้ว — ปลายทางที่ไม่ได้ติ๊กเรื่องนี้ = ไม่ Available) ·
    /// ตัวเดียวของงานหลังรับชำระ + ธงส่งของรายการประจำ (ห้ามกรองเองในบริการ)</summary>
    public static IReadOnlyList<ContactRecipientRule.RecipientOption> LineEndpoints(IReadOnlyList<ContactRecipientRule.RecipientOption> options)
        => options.Where(o => o.Available && o.EndpointId != null && NotifyEndpointKind.IsLine(o.Kind)).ToList();

    /// <summary>รอบ 258-3 (F-H1): ธง "📧 ส่งอีเมลเอกสาร" ของรายการประจำ (กฎเสมือน) ⇒ LINE ตามปลายทางของผู้ติดต่อที่รับเรื่องนี้ — ช่องที่ติ๊ก = ชนิดที่มีปลายทางใช้ได้จริง
    /// (ไม่มีปลายทาง ⇒ ไม่ติ๊ก ⇒ ไม่มีแถว "ข้าม" ทุกใบของผู้ติดต่อที่ไม่ใช้ LINE)</summary>
    public static (bool LineUser, bool LineGroup) InlineChannels(IReadOnlyList<ContactRecipientRule.RecipientOption> options)
    {
        var eps = LineEndpoints(options);
        return (eps.Any(o => o.Kind == NotifyEndpointKind.LineUser), eps.Any(o => o.Kind == NotifyEndpointKind.LineGroup));
    }

    /// <summary>รอบ 258-3 (F-H1): กุญแจเหตุการณ์ของการ์ดใบเสร็จ — หนึ่งใบต่อเอกสาร (กันซ้ำต่อเอกสาร+ปลายทางผ่าน <see cref="QueueKey"/>)</summary>
    public static string ReceiptBaseKey(Guid receiptDocumentId) => $"doc:{receiptDocumentId}:{ReceiptTrigger}:postpay";

    public static bool WantsLine(bool lineUser, bool lineGroup) => lineUser || lineGroup;

    /// <summary>ด่านตอนบันทึกกฎ — null = ผ่าน · <paramref name="companyOaReady"/> = OA ของบริษัทพร้อมส่ง (เปิด + token/secret ถอดได้)</summary>
    /// <param name="centralGroupOpen">รอบ 256 (คำตัดสิน 256-2): บริษัทผู้ให้บริการส่งเข้ากลุ่มของผู้ติดต่อผ่าน LINE OA กลางได้ ⇒ ติ๊ก "กลุ่ม LINE" อย่างเดียวไม่ต้องมี OA ของบริษัท
    /// (LINE ผู้ใช้ 1:1 ยังต้องมี OA ของบริษัท)</param>
    public static string? RuleProblem(string? trigger, bool email, bool lineUser, bool lineGroup, bool companyOaReady, string? companyOaProblem,
        bool centralGroupOpen = false)
    {
        if (!email && !WantsLine(lineUser, lineGroup))
            return "เลือกช่องทางอย่างน้อย 1 ช่อง (อีเมล · LINE ผู้ใช้ของผู้ติดต่อ · กลุ่ม LINE ของผู้ติดต่อ)";
        if (!WantsLine(lineUser, lineGroup)) return null;
        if (trigger == "PayrollPaid")
            return "สลิปเงินเดือนส่งทาง LINE ของผู้ติดต่อไม่ได้ (ข้อมูลส่วนบุคคลของพนักงาน — ส่งได้เฉพาะ 1:1 ถึงพนักงานตามเส้นสลิปเดิม)";
        if (!SupportsLine(trigger))
            return "เรื่องนี้ยังส่งทาง LINE ไม่ได้ (รองรับ: เมื่ออนุมัติเอกสาร · ใกล้/เกินกำหนด · รายการประจำ) — ใบแจ้งยอด/50 ทวิ ทาง LINE อยู่ในเฟสถัดไป · ติ๊กเฉพาะอีเมล";
        if (!companyOaReady && !(centralGroupOpen && !lineUser))
            return "ยังไม่มีช่อง LINE ของบริษัท — ตั้ง LINE OA ของบริษัทให้พร้อม (เปิดใช้ + Channel Access Token + Channel Secret) ที่ ตั้งค่า → แจ้งเตือนทาง LINE"
                   + (string.IsNullOrWhiteSpace(companyOaProblem) ? "" : " · " + companyOaProblem)
                   + " (ส่งถึงผู้ติดต่อผ่าน LINE OA กลางของ NextAcc ยังไม่เปิด — รอโควตา LINE แยกช่อง)";
        return null;
    }

    /// <summary>ส่งอีเมลของใบนี้ไหม — ติ๊กอีเมล · ถอยมาจาก LINE · หรือผู้เรียกบังคับ (เส้นถอยตอนส่ง)</summary>
    public static bool EmailWanted(bool channelEmail, bool fallbackNow, bool forceEmail, bool etaxByEmail = false)
        => forceEmail || channelEmail || fallbackNow || etaxByEmail;

    /// <summary>รอบ 257 ชุด 4 (C-M5 · server computes): ประโยคเรื่อง LINE OA กลางของ NextAcc — บริษัทผู้ให้บริการ (<c>NextAccGroupOpen</c> · 256-2) ส่งเข้า<b>กลุ่ม</b>
    /// ของผู้ติดต่อผ่าน OA กลางได้แล้ว · บริษัทอื่น/ผู้ใช้ 1:1 ยังไม่เปิด (N8 · 254-WP11-11) — ตัวเดียวของแท็บผู้ติดต่อ · ป้ายตารางส่ง · DOCUMENT_FLOW §2.12h</summary>
    public static string CentralOaSentence(bool nextAccGroupOpen) => nextAccGroupOpen
        ? "กลุ่ม LINE ของผู้ติดต่อผ่าน LINE OA กลางของ NextAcc เปิดแล้วสำหรับบริษัทนี้ (บริษัทผู้ให้บริการ · รอบ 256) · ผู้ใช้ LINE 1:1 ผ่าน OA กลางยังไม่เปิด"
        : "ผ่าน LINE OA กลางของ NextAcc ยังไม่เปิด (ใช้ LINE OA ของบริษัทเอง)";

    /// <summary>รอบ 257 ชุด 4 (C-M5): ป้ายช่องทาง LINE ของฟอร์มกฎตารางส่ง (email-schedule.html) — เซิร์ฟเวอร์ส่ง หน้าแสดงตรงตัว</summary>
    public sealed record RuleChannelLabels(string LineUser, string LineGroup, string Note);

    public static RuleChannelLabels ChannelLabels(bool nextAccGroupOpen) => new(
        "LINE ผู้ใช้ของผู้ติดต่อ (ผ่าน LINE OA ของบริษัท)",
        nextAccGroupOpen ? "กลุ่ม LINE ของผู้ติดต่อ (ผ่าน LINE OA ของบริษัท หรือ LINE OA กลางของ NextAcc)" : "กลุ่ม LINE ของผู้ติดต่อ (ผ่าน LINE OA ของบริษัท)",
        CentralOaSentence(nextAccGroupOpen));

    /// <summary>รอบ 257 ชุด 4 (C-M3): แถว LINE ถอยไปอีเมลตอนล้มถาวรไหม — เฉพาะกฎที่ติ๊กถอย + ไม่ได้ติ๊กอีเมล (ติ๊กอีเมล = อีเมลออกอยู่แล้ว) +
    /// อีเมลไม่ได้ถูกบังคับด้วย e-Tax by Email (บังคับ = อีเมลหลักออกอยู่แล้ว ⇒ ถอยอีก = ลูกค้าได้สองฉบับ + โควตาอีเมลนับสองครั้ง)</summary>
    public static bool LineFallbackAtSend(bool channelEmail, bool lineFallbackToEmail, bool etaxForcesEmail)
        => lineFallbackToEmail && !channelEmail && !etaxForcesEmail;

    /// <summary>รอบ 257 ชุด 4 ฝ่ายค้าน LOW-2: แถว LINE ล้มแล้ว แต่อีเมลหลักของใบ (กุญแจเดียวกัน) มีแถวอยู่แล้ว ⇒ ไม่ถอยซ้ำ · คำตอบของเส้นถอย:
    /// null = อีเมลหลักออก/กำลังออก (Pending · Sending · Sent) · ข้อความ = อีเมลหลัก<b>ไม่ได้</b>ส่ง (Skipped · Cancelled · Failed) — บอกเหตุ ไม่รายงานว่าเข้าคิว</summary>
    public static string? MainEmailFallbackOutcome(string? mainStatus, string? mainError)
        => mainStatus is "Pending" or "Sending" or "Sent"
            ? null
            : $"ไม่ถอยไปอีเมล — อีเมลหลักของใบนี้มีอยู่แล้วแต่ไม่ได้ส่ง (สถานะ {ScheduledEmailSendCheck.StatusLabel(mainStatus)})"
              + (string.IsNullOrWhiteSpace(mainError) ? "" : ": " + mainError!.Trim());

    /// <summary>รอบ 257 ชุด 4 (C-M3): ต้องถามเส้น e-Tax ก่อนเข้าคิว LINE ไหม — เฉพาะเมื่อแถว LINE อาจถอยไปอีเมล (ไม่เพิ่มคำค้นให้กฎอื่น)</summary>
    public static bool LineFallbackMayDuplicate(bool channelEmail, bool lineFallbackToEmail) => LineFallbackAtSend(channelEmail, lineFallbackToEmail, false);

    /// <summary>ฝ่ายค้าน M3: หมายเหตุบนแถวคิวอีเมลเมื่อกฎติ๊ก LINE อย่างเดียวแต่ใบต้องส่งแบบ e-Tax by Email (คงไว้หลังส่ง)</summary>
    public const string EtaxForcedEmailNote =
        "ส่งอีเมลด้วยแม้กฎติ๊ก LINE อย่างเดียว: ใบกำกับของบริษัทโหมด e-Tax by Email ต้องส่งทางอีเมล (การ์ด LINE เป็นการแจ้ง ไม่ใช่การส่งใบกำกับภาษีอิเล็กทรอนิกส์)";

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
