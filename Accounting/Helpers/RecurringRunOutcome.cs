using System.Text.Json.Serialization;
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

    [JsonIgnore]
    public bool IsEmpty => ApproveBlockedReason == null && !EmailHeldForDraft && !EmailSkippedNoContactEmail && !EmailEnqueueFailed
        && EmailNotSentReason == null && FollowUpError == null && SkippedPeriods <= 0;
}

/// <summary>สถานะ "ตอนนี้" ของเอกสารล่าสุดที่รอบสร้าง — อ่านจากฐานข้อมูลทุกครั้งที่เปิดดู (ผู้ใช้อนุมัติเองแล้ว ⇒ ขั้นอนุมัติหายไปเอง)</summary>
public sealed record RecurringRunDoc(Guid Id, string? DocumentNumber, DocumentStatus Status, DocumentType Type, DateTime DocumentDate);

/// <summary>ปุ่มทางไปต่อ — <c>Kind</c>: <c>openDocument</c> · <c>openContact</c> · <c>runNow</c> (เรียก "สร้างทันที" ของรายการนี้) · URL ประกอบที่เซิร์ฟเวอร์</summary>
public sealed record RecurringRunAction(string Kind, string Label, string? Url);

/// <summary>ผลรอบล่าสุดที่หน้าแสดงตรง ๆ (server computes · page displays)</summary>
public sealed record RecurringRunOutcomeView(
    string Headline,
    IReadOnlyList<string> Steps,
    IReadOnlyList<RecurringRunAction> Actions,
    string? AutoApproveNote,
    string? LegalNote,
    string? RawNotice);

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

    /// <summary>ประกอบผลรอบล่าสุด · คืน null เมื่อไม่มีอะไรต้องทำ (รอบสำเร็จ — หน้าไม่แสดงกล่อง)</summary>
    /// <param name="facts">ข้อเท็จจริงที่รอบบันทึกไว้ (null = แถวเก่าก่อนรอบ 247 ⇒ ใช้ <paramref name="legacyNotice"/>)</param>
    /// <param name="doc">เอกสารล่าสุด "ตอนนี้" (null = ไม่พบ/ถูกลบ)</param>
    /// <param name="contactEmail">อีเมลผู้ติดต่อตอนนี้ (ตัวตัดสินเดียว <see cref="RecurringEmailReadiness"/>)</param>
    /// <param name="approvedEmailRuleApplies">มีกฎ "เมื่ออนุมัติเอกสาร" (DocumentApproved) ที่ครอบชนิดเอกสารนี้ในหน้าตารางส่งอีเมล ⇒ อนุมัติแล้วระบบส่งให้</param>
    /// <param name="canRunNow">รายการยังเปิดใช้งาน (ปุ่ม "สร้างทันที" ใช้ได้)</param>
    public static RecurringRunOutcomeView? Build(RecurringRunFacts? facts, string? legacyNotice, RecurringRunDoc? doc,
        Guid? contactId, string? contactEmail, bool approvedEmailRuleApplies, bool canRunNow)
    {
        var isDraft = doc != null && doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval;
        var docLabel = doc != null ? DocumentTypeNames.Title(doc.Type, "th") : "เอกสาร";
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

        // ── แถวเก่า (ก่อนรอบ 247): มีแต่ข้อความเดียว — แสดงข้อความเดิม + ปุ่มเปิดเอกสาร ──
        if (facts == null)
        {
            if (string.IsNullOrWhiteSpace(legacyNotice)) return null;
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

        var steps = new List<string>();
        string? headline = null;
        string? autoApproveNote = null;
        var warnings = facts.ApproveWarnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? new List<string>();

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

        // ── 2) อีเมล — บอกตามที่ระบบทำจริง: AutoSendEmail ทำงานตอนสร้างใบเท่านั้น · อนุมัติเองทีหลังส่งเฉพาะเมื่อมีกฎ DocumentApproved ──
        if (facts.EmailHeldForDraft || facts.EmailSkippedNoContactEmail || facts.EmailEnqueueFailed)
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
                // ใบอนุมัติแล้ว (ตอนรอบหรือผู้ใช้อนุมัติเองทีหลัง) แต่ระบบไม่ได้ส่ง/เข้าคิวไม่สำเร็จ
                steps.Add(facts.EmailEnqueueFailed
                    ? "อีเมล: เข้าคิวส่งไม่สำเร็จ — เปิดเอกสาร แล้วกด \"ส่งอีเมล\" เอง"
                    : facts.EmailHeldForDraft && approvedEmailRuleApplies
                        ? "อีเมล: ตรวจที่หน้าตารางส่งอีเมลว่ากฎ \"เมื่ออนุมัติเอกสาร\" ส่งแล้ว — ถ้ายังไม่ถึงลูกค้า กด \"ส่งอีเมล\" ที่หน้าเอกสาร"
                        : "อีเมล: ระบบยังไม่ได้ส่งเอกสารนี้ให้ลูกค้า — ถ้ายังไม่ได้ส่งเอง เปิดเอกสาร แล้วกด \"ส่งอีเมล\"");
                OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารเพื่อส่งอีเมล");
            }
        }

        // ── 2b) รอบ 250 WP-08: ไม่ส่งเพราะค่าตั้งของผู้ติดต่อ (ช่องทางแจ้งเตือน/ไม่ส่งอัตโนมัติ) — บอกเหตุตรงตัว + ทางไปต่อ (แก้ที่ผู้ติดต่อ หรือส่งเองจากหน้าเอกสาร) ──
        if (facts.EmailNotSentReason != null)
        {
            steps.Add("อีเมล: " + facts.EmailNotSentReason + " — ถ้าต้องการส่งงวดนี้ เปิดเอกสารแล้วกด \"ส่งอีเมล\" เอง");
            if (contactId != null && actions.All(a => a.Kind != "openContact"))
                actions.Add(new RecurringRunAction("openContact", "เปิดหน้าผู้ติดต่อ", RecurringEmailReadiness.ContactUrl(contactId.Value)));
            OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารเพื่อส่งอีเมล");
        }

        // ── 3) ขั้นหลังสร้างใบล้ม ──
        if (facts.FollowUpError != null)
        {
            steps.Add(facts.FollowUpError);
            OpenDoc(isDraft ? "เปิดใบร่างเพื่ออนุมัติ" : "เปิดเอกสารล่าสุด");
        }

        // ── 4) งวดที่ข้าม ──
        if (facts.SkippedPeriods > 0)
        {
            var from = facts.SkippedFrom is { } sf ? $" (ตั้งแต่งวด {ThaiDay(sf)})" : "";
            steps.Add($"ระบบข้าม {facts.SkippedPeriods} งวดที่ค้าง{from} ช่วงที่ระบบสร้างเอกสารไม่ได้ และออกเฉพาะงวดล่าสุด — "
                      + (canRunNow
                          ? "ถ้างวดเหล่านั้นยังไม่ได้ออกเอง กด \"สร้างงวดที่ข้าม\" ได้ครั้งละหนึ่งใบต่อหนึ่งงวด (ใบที่สร้างเพิ่มลงวันที่วันนี้ — แก้วันที่ในใบร่างก่อนอนุมัติถ้าต้องการ)"
                          : "ถ้างวดเหล่านั้นยังไม่ได้ออกเอง เปิดใช้งานรายการนี้อีกครั้งแล้วกด \"สร้างทันที\" ทีละใบ"));
            if (canRunNow) actions.Add(new RecurringRunAction("runNow", "สร้างงวดที่ข้าม", null));
            headline ??= $"สร้าง{period}แล้ว แต่ข้าม {facts.SkippedPeriods} งวดที่ค้าง";
        }

        if (steps.Count == 0) return null;   // ทุกเรื่องจบแล้ว (เช่น ผู้ใช้อนุมัติเอง + ไม่มีอีเมลค้าง) — ไม่แสดงกล่อง
        if (headline == null)
            headline = facts.ApproveBlockedReason != null && doc != null && !isDraft
                ? $"{docLabel} {doc.DocumentNumber} อนุมัติแล้ว — เหลือเรื่องต้องทำต่อ"
                : $"สร้าง{period}แล้ว แต่มีเรื่องต้องทำต่อ";
        return new RecurringRunOutcomeView(headline, steps, actions, autoApproveNote, legal, legacyNotice);
    }
}
