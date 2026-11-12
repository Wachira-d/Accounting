using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 247 (UX รายการประจำ · คำร้องเจ้าของ 2026-11-06 "อ่านแล้วไม่เข้าใจว่าต้องไปทำอะไรที่ไหน") — ข้อเท็จจริงของ "เรื่องที่ต้องทำต่อ"
/// ของรอบล่าสุด เก็บเป็นโครงสร้าง (คอลัมน์ <c>RecurringTransactions.LastRunOutcome</c> · JSON) แทนการต่อสตริงยาวบรรทัดเดียว ·
/// ข้อความเดิม (<c>LastRunNotice</c>) ยังเขียนคู่กันเพื่อแถวเก่า/ป้ายในตาราง</summary>
public sealed record RecurringRunFacts
{
    /// <summary>อนุมัติอัตโนมัติไม่ผ่าน — ข้อความเหตุผลสำหรับคน (null = ไม่ได้ติดที่การอนุมัติ)</summary>
    public string? ApproveBlockedReason { get; init; }
    /// <summary>คำเตือนที่ต้องให้ "คน" กดรับทราบก่อนอนุมัติ (เช่น วงเงิน/ค้างชำระ) — ทางอัตโนมัติกดแทนไม่ได้โดยออกแบบ · ว่าง = เหตุผลอื่น</summary>
    public IReadOnlyList<string>? ApproveWarnings { get; init; }
    /// <summary>ติ๊กส่งอีเมลไว้ แต่ตอนสร้างใบยังเป็นร่าง ⇒ ระบบไม่ได้ส่ง (ใบร่างยังไม่มีเลขที่จริง §86/4)</summary>
    public bool EmailHeldForDraft { get; init; }
    /// <summary>ติ๊กส่งอีเมลไว้ ใบอนุมัติแล้ว แต่ผู้ติดต่อไม่มีอีเมล ⇒ ไม่ได้ส่ง</summary>
    public bool EmailSkippedNoContactEmail { get; init; }
    /// <summary>เข้าคิวส่งอีเมลไม่สำเร็จ</summary>
    public bool EmailEnqueueFailed { get; init; }
    /// <summary>รอบ 250 WP-08: ไม่ส่งเพราะค่าตั้งของผู้ติดต่อ (ยกเลิกรับเรื่อง "เอกสาร" ทุกปลายทาง · "ไม่ส่งอัตโนมัติ") — เหตุจากตัวตัดสินเดียว
    /// (<c>ContactRecipientRule.ForEmail</c> / <c>AutopilotSettingsResolver.AutoSendBlock</c>) · null = ไม่ได้ติดที่ค่าตั้งผู้ติดต่อ</summary>
    public string? EmailNotSentReason { get; init; }
    /// <summary>ขั้นหลังสร้างใบล้ม (ข้อความกลางของ <see cref="RecurringRunFailure.AfterCreateNotice"/>)</summary>
    public string? FollowUpError { get; init; }
    /// <summary>จำนวนงวดที่ระบบข้าม (ระบบสร้างเอกสารไม่ได้ช่วงหนึ่ง) · 0 = ไม่ได้ข้าม</summary>
    public int SkippedPeriods { get; init; }
    public DateTime? SkippedFrom { get; init; }
    /// <summary>รอบ 263: งวดที่ข้ามซึ่งออกเพิ่มแล้วด้วย "สร้างงวดที่ข้าม" (รอบพิเศษที่ไม่กินงวด) — ครบ <see cref="SkippedPeriods"/> ⇒ เรื่องนี้จบ</summary>
    public int SkippedIssued { get; init; }
    /// <summary>รอบ 263: ผู้ใช้กด "ออกเองแล้ว/ไม่ต้องออก" ของงวดที่ข้าม (คำตัดสิน 263-1-3) — เวลา UTC + ชื่อผู้กด</summary>
    public DateTime? SkippedDismissedAt { get; init; }
    public string? SkippedDismissedBy { get; init; }
    /// <summary>รอบ 263: สมุดรายวันของรอบนี้ลงบัญชีอัตโนมัติไม่ได้ (ค้างร่าง) — เหตุผล + ใบ (ตัวประเมินดูสถานะใบ "ตอนนี้") ·
    /// ฝ่ายค้าน 263 F2: ช่องเดี่ยวคงไว้ให้แถวที่เขียนก่อนแก้ — แถวใหม่ใช้ <see cref="JournalBlocks"/> (ทุกงวดของรอบไล่งวด)</summary>
    public string? JournalPostBlockedReason { get; init; }
    public Guid? JournalEntryId { get; init; }
    public string? JournalEntryNumber { get; init; }
    /// <summary>ฝ่ายค้าน 263 F2: สมุดรายวันทุกใบของรอบ (รวมรอบไล่งวด) ที่ลงบัญชีอัตโนมัติไม่ได้ — จบเมื่อ<b>ทุกใบ</b>ลงบัญชี/ยกเลิก/ถูกลบ</summary>
    public IReadOnlyList<RecurringJournalBlock>? JournalBlocks { get; init; }
    /// <summary>รอบ 263 + ฝ่ายค้าน F1: ข้อความของรอบที่ระบบ<b>ตรวจสถานะปัจจุบันให้ไม่ได้</b> (ส่วนแถวเก่าที่ตัวอ่านไม่รู้จัก · งวดบัญชีปิด/ลงย้อนหลัง ·
    /// นโยบายอนุมัติหยุดมีผล ฯลฯ) — แสดงเป็นขั้นตอน "ข้อความจากรอบนั้น" เสมอ และ<b>กันไม่ให้บอกว่าแก้ครบ</b> · บันทึกลง JSON (ปิดเรื่องงวดที่ข้ามแล้วไม่หาย)</summary>
    public IReadOnlyList<string>? UnverifiedNotes { get; init; }

    [JsonIgnore]
    public bool IsEmpty => ApproveBlockedReason == null && !EmailHeldForDraft && !EmailSkippedNoContactEmail && !EmailEnqueueFailed
        && EmailNotSentReason == null && FollowUpError == null && SkippedPeriods <= 0 && JournalPostBlockedReason == null
        && (JournalBlocks == null || JournalBlocks.Count == 0) && (UnverifiedNotes == null || UnverifiedNotes.Count == 0);

    /// <summary>ฝ่ายค้าน 263 F2: รายการสมุดรายวันที่ค้าง — รวมช่องเดี่ยวของแถวเก่า</summary>
    [JsonIgnore]
    public IReadOnlyList<RecurringJournalBlock> AllJournalBlocks
        => JournalBlocks is { Count: > 0 } list ? list
            : JournalPostBlockedReason != null ? new[] { new RecurringJournalBlock(JournalEntryId, JournalEntryNumber ?? "", JournalPostBlockedReason) }
            : Array.Empty<RecurringJournalBlock>();

    /// <summary>ฝ่ายค้าน 263 F1: เติมข้อความที่ตรวจสถานะให้ไม่ได้ (ไม่ซ้ำ)</summary>
    public RecurringRunFacts WithUnverifiedNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return this;
        var list = UnverifiedNotes?.ToList() ?? new List<string>();
        if (!list.Contains(note, StringComparer.Ordinal)) list.Add(note.Trim());
        return this with { UnverifiedNotes = list };
    }

    /// <summary>รอบ 263: งวดที่ข้ามที่ยังค้าง (ไม่ติดลบ) — ปิดเรื่องแล้ว = 0</summary>
    [JsonIgnore]
    public int SkippedRemaining => SkippedDismissedAt != null ? 0 : Math.Max(0, SkippedPeriods - SkippedIssued);

    /// <summary>รอบ 263: ข้อเท็จจริงของ "งวดที่ข้าม" ที่ต้องพาไปรอบถัดไป (ยังค้าง + ยังไม่ปิดเรื่อง) — null = ไม่มีอะไรต้องพาไป ·
    /// <paramref name="issuedOneNow"/> = รอบนี้คือ "สร้างงวดที่ข้าม" (รอบพิเศษไม่กินงวด) ⇒ นับออกเพิ่ม 1 งวด</summary>
    public RecurringRunFacts? SkipCarry(bool issuedOneNow)
    {
        if (SkippedPeriods <= 0 || SkippedDismissedAt != null) return null;
        var issued = SkippedIssued + (issuedOneNow ? 1 : 0);
        return new RecurringRunFacts { SkippedPeriods = SkippedPeriods, SkippedFrom = SkippedFrom, SkippedIssued = Math.Min(issued, SkippedPeriods) };
    }
}

/// <summary>รอบ 263 (คำร้องผู้ใช้: กล่อง "มีเรื่องต้องทำต่อ" ยังเตือนให้อนุมัติ ทั้งที่อนุมัติ+รับชำระครบแล้ว) — หลักฐาน "ตอนนี้" ที่ตัวประเมินใช้ตัดสินว่าแต่ละเรื่องจบแล้วหรือยัง ·
/// เซิร์ฟเวอร์อ่านจากฐานทุกครั้งที่เปิดหน้า (ไม่ประทับ) · ค่าว่าง = "ไม่มีหลักฐาน" ⇒ เรื่องนั้นยังค้าง (ไม่เดาว่าจบ)</summary>
public sealed record RecurringRunLive
{
    /// <summary>"แก้แล้ว" บนหมายเหตุของใบ (<see cref="AutoApproveFailure.LastResolution"/>) — เวลาไทย + ชื่อผู้อนุมัติ (แปลง id เป็นชื่อแล้ว)</summary>
    public AutoApproveFailure.Resolution? Approval { get; init; }
    /// <summary>อีเมลเอกสารนี้ส่งถึงแล้ว (บันทึกส่ง/คิวที่ส่งแล้ว) — เวลาส่งแรก UTC</summary>
    public DateTime? EmailSentAtUtc { get; init; }
    /// <summary>มีคิวอีเมลของเอกสารนี้ที่รอส่ง/กำลังส่ง</summary>
    public bool EmailQueued { get; init; }
    /// <summary>มีคิว LINE ของเอกสารนี้ที่ส่งถึงผู้ติดต่อแล้ว (สถานะส่งแล้ว — เข้มกว่า "ไม่ใช่ข้าม" ของรอบ 258 เพราะที่นี่คือการบอกว่าจบ)</summary>
    public bool LineSent { get; init; }
    /// <summary>สมุดรายวันที่ข้อเท็จจริงอ้าง (ฝ่ายค้าน 263 F2: หลายใบ) — ดู <see cref="JournalsLoaded"/></summary>
    public IReadOnlyList<RecurringRunJournal> Journals { get; init; } = Array.Empty<RecurringRunJournal>();
    /// <summary>ฝ่ายค้าน 263 F7: ค้นสมุดรายวันจากฐานแล้วจริง ⇒ ใบที่อ้างด้วย id แต่ไม่พบ = ถูกลบ (ไม่ได้ค้น = ไม่รู้ ⇒ ยังค้าง)</summary>
    public bool JournalsLoaded { get; init; }
    /// <summary>ฝ่ายค้าน 263 F6: ผู้รับของบันทึกส่งอีเมลที่ตรงกับผู้รับเรื่อง "เอกสาร" ของผู้ติดต่อ (แสดงในข้อความ)</summary>
    public string? EmailSentTo { get; init; }

    public static readonly RecurringRunLive None = new();
}

/// <summary>รอบ 263: สมุดรายวันของรอบ "ตอนนี้"</summary>
public sealed record RecurringRunJournal(Guid Id, string EntryNumber, JournalEntryStatus Status);

/// <summary>ฝ่ายค้าน 263 F2: สมุดรายวันหนึ่งใบของรอบที่ลงบัญชีอัตโนมัติไม่ได้ (id null = แถวเก่ามีแต่เลขที่)</summary>
public sealed record RecurringJournalBlock(Guid? Id, string EntryNumber, string Reason);

/// <summary>สถานะ "ตอนนี้" ของเอกสารล่าสุดที่รอบสร้าง — อ่านจากฐานข้อมูลทุกครั้งที่เปิดดู (ผู้ใช้อนุมัติเองแล้ว ⇒ ขั้นอนุมัติหายไปเอง)</summary>
/// <param name="IsTaxInvoice">ฝ่ายค้าน 263 F3: ใบนี้เป็นใบกำกับภาษี (ชนิดใบกำกับ หรือใบแจ้งหนี้/ใบกำกับรวม) — ต้องส่งมอบผู้ซื้อแม้ชำระแล้ว (§86)</param>
public sealed record RecurringRunDoc(Guid Id, string? DocumentNumber, DocumentStatus Status, DocumentType Type, DateTime DocumentDate, bool IsTaxInvoice = false);

/// <summary>ปุ่มทางไปต่อ — <c>Kind</c>: <c>openDocument</c> · <c>openContact</c> · <c>openJournal</c> · <c>runNow</c> (เรียก "สร้างทันที" ของรายการนี้) ·
/// <c>dismissSkipped</c> (รอบ 263: "ออกเองแล้ว/ไม่ต้องออก" ของงวดที่ข้าม) · URL ประกอบที่เซิร์ฟเวอร์</summary>
public sealed record RecurringRunAction(string Kind, string Label, string? Url);

/// <summary>ผลรอบล่าสุดที่หน้าแสดงตรง ๆ (server computes · page displays)</summary>
/// <param name="Resolved">รอบ 263: เรื่องของรอบนี้ที่จบแล้ว "ตอนนี้" (ใคร/เมื่อไร ถ้ามีหลักฐาน) — ไม่ใช่เรื่องที่ต้องทำ</param>
/// <param name="AllResolved">รอบ 263: ทุกเรื่องจบแล้ว ⇒ หน้าไม่แสดงกล่องเตือน (บรรทัดเล็ก "แก้ครบแล้ว" แทน) · ป้าย "มีเรื่องต้องทำต่อ" ในตารางไม่ขึ้น</param>
public sealed record RecurringRunOutcomeView(
    string Headline,
    IReadOnlyList<string> Steps,
    IReadOnlyList<RecurringRunAction> Actions,
    string? AutoApproveNote,
    string? LegalNote,
    string? RawNotice,
    IReadOnlyList<string>? Resolved = null,
    bool AllResolved = false);

/// <summary>ตัวตัดสิน "ผู้ติดต่อรับอีเมลเอกสารได้ไหม" ตัวเดียว — กติกาเดียวกับผู้ส่งจริง (<c>EmailScheduleService.EnqueueDocumentAsync</c>) ·
/// รอบ 250 WP-08: ผู้อ่านทุกตัวส่ง <see cref="ContactRecipientRule.EmailDelivery.To"/> ของเรื่อง "เอกสาร" (จาก <c>IContactRecipientResolver</c>) เข้ามา —
/// ไม่ใช่ <c>Contact.Email</c> ตรง (อีเมลเพิ่มเติม/เรื่องที่ยกเลิกรับมีผลแล้ว) · ว่าง ⇒ ไม่เข้าคิว · ใช้ทั้งฟอร์ม (ปิดช่องติ๊ก) · คำเตือนตอนบันทึก · หน้ารายละเอียด · ผลรอบ</summary>
public static class RecurringEmailReadiness
{
    public const string NoEmailReason = "ผู้ติดต่อนี้ยังไม่มีอีเมล — เพิ่มอีเมลที่หน้าผู้ติดต่อก่อน";
    public const string NotSentNoEmail = "ไม่ได้ส่งอีเมล เพราะผู้ติดต่อไม่มีอีเมล";

    public static bool HasDeliverableEmail(string? email) => !string.IsNullOrWhiteSpace(email);

    public static string ContactUrl(Guid contactId) => "/pages/contacts.html?id=" + contactId.ToString("D");

    /// <summary>คำเตือนระดับ "การตั้งค่า" ของรายการประจำ (ตอนบันทึก · หน้ารายละเอียด) — null = ไม่มีปัญหา/ไม่ได้ติ๊กส่งอีเมล</summary>
    public static string? SettingWarning(bool autoSendEmail, bool isDocumentTemplate, Guid? contactId, string? contactEmail)
    {
        if (!autoSendEmail || !isDocumentTemplate || contactId == null) return null;
        return HasDeliverableEmail(contactEmail) ? null
            : "ติ๊ก \"ส่งอีเมลเอกสารให้ผู้ติดต่อ\" ไว้ แต่ผู้ติดต่อนี้ยังไม่มีอีเมล — ระบบจะไม่ส่งอีเมลจนกว่าจะเพิ่มอีเมลที่หน้าผู้ติดต่อ (บันทึกไว้แล้ว เพิ่มอีเมลทีหลังได้)";
    }
}

public static class RecurringRunOutcome
{
    public static string DocumentUrl(Guid documentId) => "/pages/documents.html?openDoc=" + documentId.ToString("D");

    /// <summary>วันที่แบบไทย dd/MM/พ.ศ. ตามปฏิทินกรุงเทพ</summary>
    private static string ThaiDay(DateTime utc)
    {
        var d = ThaiDate.CalendarDateUtc(utc);
        return $"{d:dd/MM}/{d.Year + 543}";
    }

    /// <summary>ส่วนแรกของคำเตือน (ก่อน " — ") สำหรับประโยคหัวเรื่อง — ตัวเต็มอยู่ในขั้นตอน</summary>
    private static string Short(string warning)
    {
        // รอบ 248: ตัดป้ายรหัสนำหน้า ("[AR-OVERDUE] ") ออกจากประโยคหัวเรื่อง — ป้ายมีไว้ให้นโยบาย/ตัวกรองอ่าน ไม่ใช่ให้คนอ่าน
        var w = warning.TrimStart();
        var close = w.StartsWith('[') ? w.IndexOf("] ", StringComparison.Ordinal) : -1;
        if (close > 0 && close < 24) w = w[(close + 2)..];
        var i = w.IndexOf(" — ", StringComparison.Ordinal);
        return (i > 0 ? w[..i] : w).Trim();
    }

    /// <summary>รอบ 263: ข้อความของงวดที่ข้ามที่ยังค้าง (ข้อความเดิมบนแถว/ส่งออกไฟล์ — หน้าจออ่านจากข้อเท็จจริง)</summary>
    public static string SkipCarryNotice(RecurringRunFacts f)
    {
        var from = f.SkippedFrom is { } sf ? $" (ตั้งแต่งวด {ThaiDay(sf)})" : "";
        var left = f.SkippedIssued > 0 ? $" เหลือ {f.SkippedRemaining} งวด" : "";
        return $"ข้าม {f.SkippedPeriods} งวดที่ค้าง{from}{left} — ถ้างวดที่ข้ามยังไม่ได้ออกเอง กด \"สร้างทันที\" เพื่อออกเพิ่มทีละใบ";
    }

    /// <summary>รอบ 263: รวม "ข้ามงวด" ของรอบนี้เข้ากับข้อเท็จจริงเดิม — ช่วงเดิม (เรียกซ้ำ) = ไม่นับซ้ำ · ช่วงก่อนที่ยังค้าง = รวมจำนวนที่ยังค้าง ·
    /// เรื่องอื่นของข้อเท็จจริงเดิมคงไว้ (ผู้เขียนตัดเองตอนประทับรอบ)</summary>
    public static RecurringRunFacts MergeSkip(RecurringRunFacts prev, int count, DateTime from)
    {
        if (prev.SkippedPeriods > 0 && prev.SkippedFrom == from) return prev with { SkippedPeriods = Math.Max(prev.SkippedPeriods, count) };
        var open = prev.SkipCarry(issuedOneNow: false);
        if (open != null && open.SkippedRemaining > 0)
            return prev with
            {
                SkippedPeriods = open.SkippedRemaining + count, SkippedIssued = 0,
                SkippedFrom = open.SkippedFrom is { } f && f < from ? f : from, SkippedDismissedAt = null, SkippedDismissedBy = null,
            };
        return prev with { SkippedPeriods = count, SkippedFrom = from, SkippedIssued = 0, SkippedDismissedAt = null, SkippedDismissedBy = null };
    }

    /// <summary>ฝ่ายค้าน 263 F6: ผู้รับของบันทึกส่ง (<paramref name="sentTo"/> · คั่น , หรือ ;) ตรงกับผู้รับเรื่อง "เอกสาร" ของผู้ติดต่อ (<paramref name="recipients"/>) อย่างน้อยหนึ่งที่อยู่ ·
    /// ไม่มีผู้รับของผู้ติดต่อ = ไม่นับ (ไม่เดาว่าส่งถึงลูกค้า)</summary>
    public static bool SentToRecipient(string? sentTo, string? recipients)
    {
        static IEnumerable<string> Split(string? v) => (v ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant());
        var want = Split(recipients).ToHashSet();
        return want.Count > 0 && Split(sentTo).Any(want.Contains);
    }

    /// <summary>ฝ่ายค้าน 263 F3: เอกสารเรียกเก็บล้วน (ใบแจ้งหนี้/ใบวางบิล ที่ไม่ใช่ใบกำกับภาษี) — ชำระครบแล้วไม่ต้องส่งอีก</summary>
    private static bool IsBillingOnly(RecurringRunDoc doc)
        => doc.Type is DocumentType.Invoice or DocumentType.BillingNote && !doc.IsTaxInvoice;

    private static string JournalUrl(Guid journalEntryId) => "/pages/journals.html?entryId=" + journalEntryId.ToString("D");

    /// <summary>เวลาไทยของหลักฐาน UTC (dd/MM/พ.ศ. HH:mm)</summary>
    private static string ThaiDateTime(DateTime utc)
    {
        var t = DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(7);
        return $"{t:dd/MM}/{t.Year + 543} {t:HH:mm}";
    }

    // ── รอบ 263: ตัวอ่านข้อความแถวเก่า (ก่อนรอบ 247 ไม่มี LastRunOutcome) — รู้จักเฉพาะประโยคที่โค้ดของเราเองเคยเขียน · ที่ไม่รู้จัก = UnverifiedNotes ──
    private static readonly Regex LegacySkip = new(
        @"ข้าม (?<n>[0-9]+) งวดที่ค้าง \(ตั้งแต่งวด (?<d>[0-9]{2})/(?<m>[0-9]{2})/(?<y>[0-9]{4})\)", RegexOptions.CultureInvariant);
    // ข้อความของ AutoApproveFailure.Message (StillDraft) — เหตุผลอาจมี " · " อยู่ข้างใน ⇒ จับทั้งก้อนจากข้อความเต็มก่อนแยกส่วน
    private static readonly Regex LegacyApprove = new(
        @"(?:⚠️ )?" + Regex.Escape(AutoApproveFailure.NoteMarker) + @" \(.*?\) ไม่สำเร็จ: (?<why>.+?) — เอกสาร \S+ ยังเป็นร่าง(?: ยังไม่ออกเลข ยังไม่ลงบัญชี)?(?: · แก้ตามเหตุผลแล้วกด “อนุมัติ” ที่หน้าเอกสาร)?",
        RegexOptions.CultureInvariant);
    private static readonly Regex LegacyJournalAutoPost = new(
        @"^ลงบัญชีสมุดรายวัน (?<no>\S+) อัตโนมัติไม่สำเร็จ \(ค้างเป็นร่าง\): (?<why>.+?)(?: — เปิดสมุดรายวันแล้วกดลงบัญชีเอง)?$", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyJournalRefused = new(
        @"^สมุดรายวัน (?<no>\S+) ไม่ได้ลงบัญชีอัตโนมัติ: (?<why>.+)$", RegexOptions.CultureInvariant);
    private const string LegacyEmailHeldPrefix = "ยังไม่ได้ส่งอีเมล — เอกสารไม่ได้ถูกอนุมัติ";
    private const string LegacySkipTail = "ถ้างวดที่ข้ามยังไม่ได้ออกเอง";

    /// <summary>รอบ 263: แปลงข้อความแถวเก่า (คั่น " · ") เป็นข้อเท็จจริง เพื่อให้ตัวประเมินตรวจกับสถานะ "ตอนนี้" ได้ (เดิมแถวเก่าแสดงข้อความแช่แข็งตลอดไป) ·
    /// ไม่รู้จักสักส่วน = null (ผู้เรียกแสดงข้อความเดิมแบบเดิม — ไม่แต่งว่าจบแล้ว) · ส่วนที่ไม่รู้จัก = <see cref="RecurringRunFacts.UnverifiedNotes"/> (ฝ่ายค้าน 263 F1: กันไม่ให้บอกว่าแก้ครบ)</summary>
    public static RecurringRunFacts? FactsFromLegacyNotice(string? notice)
    {
        if (string.IsNullOrWhiteSpace(notice)) return null;
        var facts = new RecurringRunFacts();
        var other = new List<string>();
        var known = false;
        var rest = notice;
        var approve = LegacyApprove.Match(notice);
        if (approve.Success)
        {
            facts = facts with { ApproveBlockedReason = approve.Groups["why"].Value.Trim() };
            known = true;
            rest = notice.Remove(approve.Index, approve.Length);
        }
        foreach (var raw in rest.Split(" · "))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            Match m;
            if (part.StartsWith(LegacyEmailHeldPrefix, StringComparison.Ordinal))
            {
                facts = facts with { EmailHeldForDraft = true };
                known = true;
            }
            else if (part.StartsWith(RecurringEmailReadiness.NotSentNoEmail, StringComparison.Ordinal))
            {
                facts = facts with { EmailSkippedNoContactEmail = true };
                known = true;
            }
            else if ((m = LegacySkip.Match(part)).Success)
            {
                var inv = CultureInfo.InvariantCulture;
                DateTime? from = null;
                try
                {
                    from = new DateTime(int.Parse(m.Groups["y"].Value, inv) - 543, int.Parse(m.Groups["m"].Value, inv),
                        int.Parse(m.Groups["d"].Value, inv), 0, 0, 0, DateTimeKind.Utc);
                }
                catch (ArgumentOutOfRangeException) { from = null; }
                facts = facts with { SkippedPeriods = int.Parse(m.Groups["n"].Value, inv), SkippedFrom = from };
                known = true;
            }
            else if (part.StartsWith(LegacySkipTail, StringComparison.Ordinal)) { /* ท้ายประโยคงวดที่ข้าม (ข้อความเดิมคั่นด้วย " · ") */ }
            else if ((m = LegacyJournalAutoPost.Match(part)).Success || (m = LegacyJournalRefused.Match(part)).Success)
            {
                facts = facts with { JournalPostBlockedReason = m.Groups["why"].Value.Trim(), JournalEntryNumber = m.Groups["no"].Value };
                known = true;
            }
            else other.Add(part);
        }
        return known ? facts with { UnverifiedNotes = other.Count > 0 ? other : null } : null;
    }

    /// <summary>ประกอบผลรอบล่าสุด — <b>ตัวประเมินสด</b> (รอบ 263): ทุกเรื่องตรวจกับสถานะ "ตอนนี้" (<paramref name="doc"/> · <paramref name="live"/>) ·
    /// เรื่องที่จบแล้วย้ายไป <see cref="RecurringRunOutcomeView.Resolved"/> (พร้อมใคร/เมื่อไรถ้ามีหลักฐาน) · จบครบ ⇒ <see cref="RecurringRunOutcomeView.AllResolved"/> ·
    /// คืน null เมื่อไม่มีอะไรจะบอกเลย</summary>
    /// <param name="facts">ข้อเท็จจริงที่รอบบันทึกไว้ (null = แถวเก่าก่อนรอบ 247 ⇒ อ่านจาก <paramref name="legacyNotice"/> ด้วย <see cref="FactsFromLegacyNotice"/>)</param>
    /// <param name="doc">เอกสารล่าสุด "ตอนนี้" (null = ไม่พบ/ถูกลบ หรือรายการสมุดรายวัน)</param>
    /// <param name="contactEmail">อีเมลผู้ติดต่อตอนนี้ (ตัวตัดสินเดียว <see cref="RecurringEmailReadiness"/>)</param>
    /// <param name="approvedEmailRuleApplies">มีกฎ "เมื่ออนุมัติเอกสาร" (DocumentApproved) ที่ครอบชนิดเอกสารนี้ในหน้าตารางส่งอีเมล ⇒ อนุมัติแล้วระบบส่งให้</param>
    /// <param name="canRunNow">รายการยังเปิดใช้งาน (ปุ่ม "สร้างทันที" ใช้ได้)</param>
    /// <param name="live">หลักฐาน "ตอนนี้" นอกตัวใบ (อีเมล/LINE/สมุดรายวัน/ผู้อนุมัติ) — null = ไม่มีหลักฐาน (เรื่องนั้นยังค้าง)</param>
    public static RecurringRunOutcomeView? Build(RecurringRunFacts? facts, string? legacyNotice, RecurringRunDoc? doc,
        Guid? contactId, string? contactEmail, bool approvedEmailRuleApplies, bool canRunNow, RecurringRunLive? live = null)
    {
        live ??= RecurringRunLive.None;
        var isDraft = doc != null && doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval;
        // รอบ 263: ใบถูกยกเลิก/ปฏิเสธ ⇒ เรื่องอนุมัติ/อีเมลของใบนี้ไม่ต้องทำแล้ว (ข้อความต่างจาก "อนุมัติแล้ว")
        var isClosed = doc != null && !isDraft && !DocumentStatusRules.IsEffective(doc.Status);
        var isPaid = doc != null && doc.Status == DocumentStatus.Paid;
        var docLabel = doc != null ? DocumentTypeNames.Title(doc.Type, "th") : "เอกสาร";
        var docRef = doc != null ? $"{docLabel} {doc.DocumentNumber}" : "เอกสารของรอบนี้";
        // รอบ 250 WP-01 (เทสต์รอบ 247 เพิ่งรันครั้งแรก): วันที่ต้องมีช่องว่างคั่นคำไทยที่ต่อท้าย ("งวด 26/10/2569 เป็นร่างแล้ว") —
        // ทุกจุดใช้ "สร้าง{period}…" ต่อคำไทยตรง ๆ · เดิมหัวข้อบนหน้าขึ้น "26/10/2569เป็นร่างแล้ว"
        var period = doc != null ? $"{docLabel}งวด {ThaiDay(doc.DocumentDate)} " : docLabel;
        var hasEmail = RecurringEmailReadiness.HasDeliverableEmail(contactEmail);
        var actions = new List<RecurringRunAction>();
        void OpenDoc(string label)
        {
            if (doc != null && actions.All(a => a.Kind != "openDocument"))
                actions.Add(new RecurringRunAction("openDocument", label, DocumentUrl(doc.Id)));
        }
        void OpenContact()
        {
            if (contactId != null && actions.All(a => a.Kind != "openContact"))
                actions.Add(new RecurringRunAction("openContact", "เปิดหน้าผู้ติดต่อเพื่อเพิ่มอีเมล", RecurringEmailReadiness.ContactUrl(contactId.Value)));
        }
        var legal = isDraft
            ? "ใบร่างใช้เลข DRAFT-… ชั่วคราว — เลขที่จริงแบบเรียงต่อกันออกตอนอนุมัติเท่านั้น (ป.รัษฎากร §86/4) จึงส่งใบร่างให้ลูกค้าและลงบัญชีไม่ได้"
            : null;

        // ── แถวเก่า (ก่อนรอบ 247): รอบ 263 อ่านประโยคที่รู้จักเป็นข้อเท็จจริงก่อน (ประเมินสดได้) · ไม่รู้จักเลย = แสดงข้อความเดิม + ปุ่มเปิดเอกสาร ──
        if (facts == null)
        {
            if (string.IsNullOrWhiteSpace(legacyNotice)) return null;
            facts = FactsFromLegacyNotice(legacyNotice);
            if (facts == null)
            {
                var legacySteps = new List<string>();
                if (isDraft)
                {
                    legacySteps.Add($"กดปุ่ม \"เปิดใบร่างเพื่ออนุมัติ\" → ตรวจใบ → กด \"อนุมัติ\" (ถ้ามีคำเตือนขึ้นมา ตรวจแล้วกดยืนยันรับทราบ)");
                    OpenDoc("เปิดใบร่างเพื่ออนุมัติ");
                }
                else OpenDoc("เปิดเอกสารล่าสุด");
                legacySteps.Add("รายละเอียดจากรอบนั้น: " + legacyNotice);
                return new RecurringRunOutcomeView(
                    isDraft ? $"สร้าง{period}เป็นร่างแล้ว แต่ยังไม่อนุมัติ" : "รอบล่าสุดสร้างเอกสารแล้ว แต่มีเรื่องต้องทำต่อ",
                    legacySteps, actions, null, legal, legacyNotice);
            }
        }

        var steps = new List<string>();
        var resolved = new List<string>();
        string? headline = null;
        string? autoApproveNote = null;
        var warnings = facts.ApproveWarnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? new List<string>();
        var docFacts = facts.ApproveBlockedReason != null || facts.EmailHeldForDraft || facts.EmailSkippedNoContactEmail
                       || facts.EmailEnqueueFailed || facts.EmailNotSentReason != null || facts.FollowUpError != null;
        var journalBlocks = facts.AllJournalBlocks;
        var docGone = doc == null && docFacts && journalBlocks.Count == 0;
        if (docGone) resolved.Add("ไม่พบเอกสารของรอบนี้แล้ว (ถูกลบ) — เรื่องอนุมัติ/ส่งเอกสารของใบนั้นไม่ต้องทำต่อ");
        if (isClosed && docFacts) resolved.Add($"{docRef} ถูกยกเลิก/ปฏิเสธแล้ว — เรื่องอนุมัติ/ส่งเอกสารของใบนี้ไม่ต้องทำต่อ");

        // ── 1) อนุมัติ — เฉพาะเมื่อใบยังเป็นร่างอยู่จริงตอนนี้ (ผู้ใช้อนุมัติเองแล้ว ⇒ ขั้นนี้จบ) ──
        if (facts.ApproveBlockedReason != null && isDraft)
        {
            if (warnings.Count > 0)
            {
                var more = warnings.Count > 1 ? $" (และอีก {warnings.Count - 1} ข้อ)" : "";
                headline = $"สร้าง{period}เป็นร่างแล้ว แต่ยังไม่อนุมัติ เพราะ{Short(warnings[0])}{more}";
                steps.Add("กดปุ่ม \"เปิดใบร่างเพื่ออนุมัติ\" ข้างล่าง → ตรวจใบ → กด \"อนุมัติ\" → ระบบจะแสดงคำเตือน: "
                          + string.Join(" · ", warnings) + " — ถ้าตรวจแล้วรับได้ ให้กดยืนยันรับทราบ ใบจะได้เลขที่จริงและลงบัญชี");
                // รอบ 248: คำเตือนที่มีรหัสในนโยบาย (เช่น ลูกค้าค้างชำระ) ⇒ บอกทางไปต่อ "ตั้งนโยบาย" (ไม่ใช่ "กดแทนไม่ได้" ตลอดไป)
                var releasable = warnings.Where(w => AutoApprovePolicy.CodeOf(w) != null).ToList();
                autoApproveNote = releasable.Count > 0 && releasable.Count == warnings.Count
                    ? "ช่อง \"อนุมัติอัตโนมัติ\" ของรายการนี้เปิดอยู่แล้ว — ระบบหยุดเพราะยังไม่มีนโยบายให้ข้ามคำเตือนนี้ · "
                      + "ถ้าต้องการให้ระบบอนุมัติต่อเองเมื่อพบคำเตือนนี้ (พร้อมบันทึกหมายเหตุ + ร่องรอยตรวจสอบ) ผู้มีสิทธิ์อนุมัติตั้งได้ที่ แก้ไข → ขั้น \"อนุมัติ\" → นโยบายอนุมัติ · "
                      + "งวดถัดไปถ้าไม่มีคำเตือน ระบบจะอนุมัติให้เองตามปกติ"
                    : "ช่อง \"อนุมัติอัตโนมัติ\" ของรายการนี้เปิดอยู่แล้ว ไม่ต้องไปแก้ที่ฟอร์ม — ระบบหยุดเองโดยตั้งใจ "
                      + "เพราะคำเตือนแบบนี้ (เช่น ยอดค้างชำระ/วงเงินเครดิต) ต้องให้คนกดรับทราบทุกครั้ง ระบบอัตโนมัติกดแทนไม่ได้ · "
                      + "งวดถัดไปถ้าไม่มีคำเตือน ระบบจะอนุมัติให้เองตามปกติ";
            }
            else
            {
                headline = $"สร้าง{period}เป็นร่างแล้ว แต่อนุมัติอัตโนมัติไม่ผ่าน";
                steps.Add($"กดปุ่ม \"เปิดใบร่างเพื่ออนุมัติ\" ข้างล่าง → แก้ตามเหตุผลนี้: {facts.ApproveBlockedReason} → แล้วกด \"อนุมัติ\"");
                autoApproveNote = "ช่อง \"อนุมัติอัตโนมัติ\" ของรายการนี้เปิดอยู่แล้ว — ที่ไม่ผ่านเป็นเพราะเหตุผลข้างบน ไม่ใช่เพราะช่องนี้";
            }
            OpenDoc("เปิดใบร่างเพื่ออนุมัติ");
        }
        else if (facts.ApproveBlockedReason != null && doc != null && !isClosed)
        {
            // รอบ 263: อนุมัติแล้ว — บอกใคร/เมื่อไรจากหมายเหตุ "แก้แล้ว" บนใบ (ไม่มีหลักฐาน = บอกแค่ว่าอนุมัติแล้ว ไม่แต่งชื่อ)
            var who = live.Approval is { } ap ? $" เมื่อ {ap.AtBangkok} (เวลาไทย) โดย {ap.By}" : "";
            resolved.Add($"อนุมัติแล้ว: {docRef}{who}"
                         + (isPaid ? " · รับชำระครบแล้ว" : doc.Status == DocumentStatus.PartiallyPaid ? " · รับชำระบางส่วนแล้ว" : ""));
        }

        // ── 2) อีเมล — บอกตามที่ระบบทำจริง: AutoSendEmail ทำงานตอนสร้างใบเท่านั้น · อนุมัติเองทีหลังส่งเฉพาะเมื่อมีกฎ DocumentApproved ──
        // รอบ 263: ส่งแล้ว (บันทึกส่ง/คิวส่งแล้ว) · อยู่ในคิว · ส่งทาง LINE · ชำระครบแล้ว (คำตัดสิน 263-1-2) · ใบถูกยกเลิก/ลบ ⇒ เรื่องอีเมลจบ
        var emailFact = facts.EmailHeldForDraft || facts.EmailSkippedNoContactEmail || facts.EmailEnqueueFailed || facts.EmailNotSentReason != null;
        string? emailDone = !emailFact || isDraft ? null
            : doc == null || isClosed ? ""   // บอกไว้ข้างบนแล้ว
            // ฝ่ายค้าน 263 F6: หลักฐานกรองผู้รับให้ตรงผู้รับเรื่อง "เอกสาร" ของผู้ติดต่อแล้ว (ฝั่งเซิร์ฟเวอร์) · บอกปลายทางตรงตัว
            : live.EmailSentAtUtc is { } sentAt ? $"มีบันทึกส่งอีเมล{docRef}"
                + (string.IsNullOrWhiteSpace(live.EmailSentTo) ? "" : $" ถึง {live.EmailSentTo}") + $" แล้ว เมื่อ {ThaiDateTime(sentAt)} (เวลาไทย)"
            : live.EmailQueued ? $"อีเมล{docRef} อยู่ในคิวส่งแล้ว (ดูสถานะที่หน้าตารางส่งอีเมล)"
            : live.LineSent ? $"ส่ง{docRef} ทาง LINE ถึงผู้ติดต่อแล้ว"
            // ฝ่ายค้าน 263 F3 (แคบคำตัดสิน 263-1-2): ชำระครบจบเรื่องอีเมลเฉพาะ "เอกสารเรียกเก็บ" ที่ไม่ใช่ใบกำกับภาษี — ใบกำกับต้องส่งมอบผู้ซื้อ (§86)
            : isPaid && IsBillingOnly(doc) ? $"{docRef} รับชำระครบแล้ว — ไม่ต้องส่งเอกสารเรียกเก็บทางอีเมลอีก"
            : null;
        if (emailDone != null)
        {
            if (emailDone.Length > 0) resolved.Add(emailDone);
        }
        else if (facts.EmailHeldForDraft || facts.EmailSkippedNoContactEmail || facts.EmailEnqueueFailed)
        {
            if (!hasEmail)
            {
                steps.Add(RecurringEmailReadiness.NotSentNoEmail + " — กด \"เปิดหน้าผู้ติดต่อเพื่อเพิ่มอีเมล\" แล้วกลับมาส่งอีเมลจากหน้าเอกสาร");
                OpenContact();
                OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารเพื่อส่งอีเมล");
            }
            else if (facts.EmailHeldForDraft && isDraft && approvedEmailRuleApplies)
                steps.Add("อีเมล: ยังไม่ได้ส่ง (ใบยังเป็นร่าง) — หลังคุณอนุมัติ ระบบจะส่งให้เองตามกฎ \"เมื่ออนุมัติเอกสาร\" ในหน้าตารางส่งอีเมล");
            else if (facts.EmailHeldForDraft && isDraft)
                steps.Add("อีเมล: ยังไม่ได้ส่ง และระบบจะไม่ส่งงวดนี้ให้เองหลังคุณอนุมัติ (การส่งอัตโนมัติของรายการประจำทำงานตอนสร้างใบเท่านั้น) — "
                          + "หลังอนุมัติแล้ว กด \"ส่งอีเมล\" ที่หน้าเอกสารเดียวกันนั้น");
            else
            {
                // ใบอนุมัติแล้ว (ตอนรอบหรือผู้ใช้อนุมัติเองทีหลัง) แต่ไม่มีบันทึกส่ง/คิวของใบนี้จริง (รอบ 263: ตรวจแล้ว ไม่ใช่เดา)
                steps.Add(facts.EmailEnqueueFailed
                    ? "อีเมล: เข้าคิวส่งไม่สำเร็จ — เปิดเอกสาร แล้วกด \"ส่งอีเมล\" เอง"
                    : "อีเมล: ยังไม่มีบันทึกการส่งเอกสารนี้ถึงลูกค้า — เปิดเอกสาร แล้วกด \"ส่งอีเมล\"");
                OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารเพื่อส่งอีเมล");
            }
        }

        // ── 2b) รอบ 250 WP-08: ไม่ส่งเพราะค่าตั้งของผู้ติดต่อ (ช่องทางแจ้งเตือน/ไม่ส่งอัตโนมัติ) — บอกเหตุตรงตัว + ทางไปต่อ (แก้ที่ผู้ติดต่อ หรือส่งเองจากหน้าเอกสาร) ──
        if (facts.EmailNotSentReason != null && emailDone == null)
        {
            steps.Add("อีเมล: " + facts.EmailNotSentReason + " — ถ้าต้องการส่งงวดนี้ เปิดเอกสารแล้วกด \"ส่งอีเมล\" เอง");
            if (contactId != null && actions.All(a => a.Kind != "openContact"))
                actions.Add(new RecurringRunAction("openContact", "เปิดหน้าผู้ติดต่อ", RecurringEmailReadiness.ContactUrl(contactId.Value)));
            OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารเพื่อส่งอีเมล");
        }

        // ── 3) ขั้นหลังสร้างใบล้ม — ข้อความทั่วไป ตรวจกับสถานะปัจจุบันไม่ได้ ⇒ ค้างจนรอบถัดไป · ใบถูกยกเลิก/ลบ = จบ (บอกไว้ข้างบน) ──
        if (facts.FollowUpError != null && !isClosed && !docGone)
        {
            steps.Add(facts.FollowUpError);
            OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารล่าสุด");
        }

        // ── 3b) รอบ 263 + ฝ่ายค้าน F2/F7: สมุดรายวันลงบัญชีอัตโนมัติไม่ได้ — ทุกใบของรอบ · ลงบัญชี/ยกเลิก/ถูกลบ = จบ · ไม่ได้ค้น/ไม่พบด้วยเลขที่ = ค้าง (ไม่เดา) ──
        if (journalBlocks.Count > 0)
        {
            var pending = new List<string>();
            foreach (var b in journalBlocks)
            {
                var j = b.Id is Guid bid ? live.Journals.FirstOrDefault(x => x.Id == bid)
                    : live.Journals.Where(x => x.EntryNumber == b.EntryNumber).ToList() is { Count: 1 } one ? one[0] : null;
                var jNo = j?.EntryNumber ?? b.EntryNumber;
                if (j?.Status == JournalEntryStatus.Posted) resolved.Add($"สมุดรายวัน {jNo} ลงบัญชีแล้ว");
                else if (j?.Status is JournalEntryStatus.Voided or JournalEntryStatus.Reversed)
                    resolved.Add($"สมุดรายวัน {jNo} ถูกยกเลิก/กลับรายการแล้ว — ไม่ต้องลงบัญชี");
                else if (j == null && b.Id != null && live.JournalsLoaded)
                    resolved.Add($"ไม่พบสมุดรายวัน {jNo} แล้ว (ถูกลบ) — ไม่มีอะไรต้องลงบัญชี");
                else
                {
                    pending.Add(jNo);
                    steps.Add($"สมุดรายวัน {jNo} ยังเป็นร่าง (ลงบัญชีอัตโนมัติไม่ได้: {b.Reason}) — เปิดสมุดรายวันแล้วกด \"ลงบัญชี\"");
                    if (j != null && actions.All(a => a.Url != JournalUrl(j.Id)))
                        actions.Add(new RecurringRunAction("openJournal", $"เปิดสมุดรายวัน {jNo}", JournalUrl(j.Id)));
                }
            }
            if (pending.Count > 0)
                headline ??= pending.Count == 1 ? $"สมุดรายวัน {pending[0]} ยังไม่ได้ลงบัญชี" : $"สมุดรายวัน {pending.Count} ใบยังไม่ได้ลงบัญชี";
        }

        // ── 4) งวดที่ข้าม — รอบ 263 (คำตัดสิน 263-1-3): ค้างจนออกครบ ("สร้างงวดที่ข้าม" นับทีละงวด) หรือผู้ใช้กด "ออกเองแล้ว/ไม่ต้องออก" ──
        if (facts.SkippedPeriods > 0)
        {
            var from = facts.SkippedFrom is { } sf ? $" (ตั้งแต่งวด {ThaiDay(sf)})" : "";
            if (facts.SkippedDismissedAt is { } dis)
                resolved.Add($"งวดที่ข้าม {facts.SkippedPeriods} งวด{from}: ปิดเรื่องแล้ว (ออกเอง/ไม่ต้องออก) เมื่อ {ThaiDateTime(dis)} (เวลาไทย)"
                             + (string.IsNullOrWhiteSpace(facts.SkippedDismissedBy) ? "" : " โดย " + facts.SkippedDismissedBy));
            else if (facts.SkippedRemaining <= 0)
                resolved.Add($"งวดที่ข้าม {facts.SkippedPeriods} งวด{from}: ออกเพิ่มครบแล้ว");
            else
            {
                var done = facts.SkippedIssued > 0 ? $" (ออกเพิ่มแล้ว {facts.SkippedIssued} จาก {facts.SkippedPeriods} งวด)" : "";
                steps.Add($"ระบบข้าม {facts.SkippedPeriods} งวดที่ค้าง{from} ช่วงที่ระบบสร้างเอกสารไม่ได้ และออกเฉพาะงวดล่าสุด{done} — "
                          + (canRunNow
                              ? "ถ้างวดเหล่านั้นยังไม่ได้ออกเอง กด \"สร้างงวดที่ข้าม\" ได้ครั้งละหนึ่งใบต่อหนึ่งงวด (ใบที่สร้างเพิ่มลงวันที่วันนี้ — แก้วันที่ในใบร่างก่อนอนุมัติถ้าต้องการ)"
                              : "ถ้างวดเหล่านั้นยังไม่ได้ออกเอง เปิดใช้งานรายการนี้อีกครั้งแล้วกด \"สร้างทันที\" ทีละใบ")
                          + " · ออกเองแล้วหรือไม่ต้องออก กด \"ออกเองแล้ว/ไม่ต้องออก\" เพื่อปิดเรื่องนี้");
                if (canRunNow) actions.Add(new RecurringRunAction("runNow", "สร้างงวดที่ข้าม", null));
                actions.Add(new RecurringRunAction("dismissSkipped", "ออกเองแล้ว/ไม่ต้องออก", null));
                headline ??= facts.SkippedIssued > 0
                    ? $"ยังเหลืองวดที่ข้าม {facts.SkippedRemaining} งวด"
                    : $"สร้าง{period}แล้ว แต่ข้าม {facts.SkippedPeriods} งวดที่ค้าง";
            }
        }

        // ── 5) ฝ่ายค้าน 263 F1: ข้อความที่ระบบตรวจสถานะให้ไม่ได้ — แสดงเป็นขั้นตอนเสมอ (ไม่ซ่อนในรายละเอียด) และกันไม่ให้บอกว่าแก้ครบ ──
        foreach (var note in facts.UnverifiedNotes ?? Array.Empty<string>())
            steps.Add("ข้อความจากรอบนั้นที่ระบบตรวจสถานะให้ไม่ได้ (ตรวจเองแล้วไม่ต้องทำอะไร ข้อความนี้จะหายในรอบถัดไป): " + note);

        if (steps.Count == 0)
        {
            if (resolved.Count == 0) return null;   // ทุกเรื่องจบแล้วแต่ไม่มีอะไรจะบอก — ไม่แสดงกล่อง
            // รอบ 263: จบครบ ⇒ ไม่มีกล่องเตือน — บรรทัดเล็ก "แก้ครบแล้ว" + รายการที่จบ (ข้อความเดิมของรอบยังเปิดดูได้ใน "รายละเอียดเพิ่มเติม")
            return new RecurringRunOutcomeView("เรื่องที่ต้องทำต่อของรอบล่าสุดแก้ครบแล้ว", Array.Empty<string>(), Array.Empty<RecurringRunAction>(),
                null, null, legacyNotice, resolved, AllResolved: true);
        }
        if (headline == null)
            headline = facts.ApproveBlockedReason != null && doc != null && !isDraft && !isClosed
                ? $"{docLabel} {doc.DocumentNumber} อนุมัติแล้ว — เหลือเรื่องต้องทำต่อ"
                : $"สร้าง{period}แล้ว แต่มีเรื่องต้องทำต่อ";
        return new RecurringRunOutcomeView(headline, steps, actions, autoApproveNote, legal, legacyNotice, resolved, AllResolved: false);
    }
}
