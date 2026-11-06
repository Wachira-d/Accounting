using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตรวจความพร้อมของรายการประจำแบบ Autopilot — ตัวตัดสินเดียว** (pure · รอบ 248 · AUTOPILOT_BILLING_DESIGN §4.4 · หลักการ #3/#5)
///
/// <para>ใช้ทั้งแผงความพร้อมในฟอร์ม · ทดลองรัน (dry-run) · หน้ารายละเอียด — เซิร์ฟเวอร์ตัดสินทุกข้อ หน้าเว็บแสดงอย่างเดียว (ไม่มีรหัสกติกาใน JS) ·
/// ต่อยอด <see cref="RecurringEmailReadiness"/> ของรอบ 247 (ใช้ตัวตัดสินอีเมลตัวเดิม) · ข้อเท็จจริงรวบรวมครั้งเดียวใน <c>RecurringAutopilotService</c></para>
///
/// <para>ระดับ: <see cref="LevelFull"/> (สร้าง→อนุมัติ→ส่ง→แนบลิงก์ชำระ ไม่มีคนแตะ) · <see cref="LevelPartial"/> (บางขั้นต้องมีคน) ·
/// <see cref="LevelDraftOnly"/> (ไม่ได้อนุมัติอัตโนมัติ หรืออนุมัติอัตโนมัติจะหยุด ⇒ ได้แค่ใบร่าง)</para>
/// </summary>
public static class AutopilotReadiness
{
    public const string RuleCode = "AUTOPILOT-READINESS";

    public const string Ok = "Ok";
    public const string Info = "Info";
    public const string Warn = "Warn";
    public const string Fail = "Fail";

    public const string LevelFull = "Full";
    public const string LevelPartial = "Partial";
    public const string LevelDraftOnly = "DraftOnly";

    /// <summary>ข้อเท็จจริงทั้งหมดที่ใช้ตัดสิน — service รวบรวม (อ่านอย่างเดียว)</summary>
    public sealed record Facts
    {
        public bool IsDocumentTemplate { get; init; } = true;
        public DocumentType? DocumentType { get; init; }
        public bool AutoApprove { get; init; }
        public bool AutoSendEmail { get; init; }
        public Guid? ContactId { get; init; }
        /// <summary>ผู้ติดต่อยังใช้ได้ (มีอยู่ · ไม่ถูกลบ)</summary>
        public bool ContactUsable { get; init; }
        public string? ContactEmail { get; init; }
        /// <summary>ช่องผู้ซื้อ §86/4 ที่ขาดจนต้องบล็อก (ผล <c>TaxInvoiceCompletenessChecker.BuyerBlockingFields</c>) · null = ผ่าน/ไม่บังคับ</summary>
        public IReadOnlyList<string>? BuyerBlockingFields { get; init; }
        /// <summary>อีเมลบริษัทตั้ง+ทดสอบผ่าน (ไม่ผ่าน = ส่งผ่านอีเมลระบบในนามบริษัท)</summary>
        public bool CompanySenderVerified { get; init; }
        /// <summary>เอกสารขาย (ลิงก์ชำระใช้ได้)</summary>
        public bool SaleSide { get; init; }
        public string Currency { get; init; } = "THB";
        /// <summary>ผลชั้นช่องทางรับเงิน (<see cref="AutopilotSettingsResolver.Resolve"/>)</summary>
        public bool PayLinkAttach { get; init; }
        public string PayLinkSource { get; init; } = "บริษัท";
        /// <summary>โหมดของรายการ/ผู้ติดต่อที่มีผล (ชื่อ) — null = ตามบริษัท</summary>
        public string? PaymentMode { get; init; }
        /// <summary>เหตุที่บริษัทออกลิงก์ชำระไม่ได้ (<see cref="DocumentPayLinkPolicy.UnavailableReason"/>) · null = ได้</summary>
        public string? PayLinkUnavailableReason { get; init; }
        /// <summary>ช่องทางที่เอกสารจะเปิดจริง (ตัดกับที่บริษัทเปิดแล้ว)</summary>
        public IReadOnlyList<string> PayLinkAllowed { get; init; } = Array.Empty<string>();
        /// <summary>คำเตือนก่อนอนุมัติของใบจำลอง — null = ตรวจไม่ได้ (ไม่มีผู้ติดต่อ/เทมเพลตอ่านไม่ได้)</summary>
        public IReadOnlyList<string>? ApprovalWarnings { get; init; }
        /// <summary>คำเตือนที่ยังหยุดหลังนโยบาย (<see cref="ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource, IReadOnlyList{string}, AutoApprovePolicy?)"/>)</summary>
        public IReadOnlyList<string> ApprovalBlocking { get; init; } = Array.Empty<string>();
        /// <summary>คำเตือนที่นโยบายจะปล่อย</summary>
        public IReadOnlyList<string> ApprovalReleased { get; init; } = Array.Empty<string>();
        /// <summary>นโยบายที่มีผล (null = ไม่ได้ตั้ง)</summary>
        public AutoApprovePolicy? Policy { get; init; }
        /// <summary>ผู้ตั้งนโยบายยังเป็นสมาชิกที่ทำหน้าที่ได้ — null = ไม่มีนโยบาย</summary>
        public bool? PolicySetterActive { get; init; }
        /// <summary>เหตุที่นโยบายหยุดมีผล (ผู้ตั้งออก/หมดสิทธิ์ · ผู้ติดต่อ/ชนิดเอกสารเปลี่ยนหลังตั้ง) — null = มีผล/ไม่มีนโยบาย</summary>
        public string? PolicyInactiveReason { get; init; }
        /// <summary>เหตุผลที่สร้างเอกสารไม่ได้เลย (ตัวตรวจเทมเพลตเดิม) · null = สร้างได้</summary>
        public string? TemplateError { get; init; }
        /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 2 · คำตัดสิน §2 #9/Y7): ตั้ง URL ของระบบแล้วจริงไหม (<see cref="SystemBaseUrl.IsConfigured"/> — ค่าที่มากับซอร์ส ≠ ตั้งแล้ว) ·
        /// false ⇒ ลิงก์ชำระ/QR/ลิงก์ในอีเมล-LINE ไม่ถูกแนบ · null = ผู้เรียกไม่ได้ตรวจ (ไม่ตัดสิน)</summary>
        public bool? SystemBaseUrlConfigured { get; init; }
        /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 3 · คำตัดสิน §2 #10): มีกฎ "เมื่ออนุมัติเอกสาร" ที่ใช้งานอยู่ครอบชนิดเอกสารนี้</summary>
        public bool ApprovedEmailRuleCovers { get; init; }
        /// <summary>มีกฎ "เมื่อรายการประจำสร้างเอกสาร" ครอบชนิดเอกสารนี้</summary>
        public bool RecurringEmailRuleExists { get; init; }
    }

    public sealed record Item(string Code, string Stage, string Verdict, string Title, string? Why, string? FixUrl, string? FixLabel);

    public sealed record Report(string Level, string LevelLabel, IReadOnlyList<Item> Items, int WarnCount, int FailCount);

    public const string PaymentSettingsUrl = "/pages/payment-settings.html";
    /// <summary>รอบ 250: หน้าตั้ง URL ของระบบ (ผู้ดูแลระบบเท่านั้น)</summary>
    public const string SystemSettingsUrl = "/admin/site-settings.html";
    public const string EmailScheduleUrl = "/pages/email-schedule.html";
    public const string SystemBaseUrlMissingTitle = "ยังไม่ได้ตั้ง URL ของระบบ — ลิงก์ชำระ/QR จะไม่ถูกแนบในอีเมล";
    public const string EmailSettingsUrl = "/pages/settings.html#email";
    /// <summary>ทางไปต่อภายในฟอร์ม — หน้ารายการประจำเปิดขั้น "อนุมัติ" ของ wizard</summary>
    public const string PolicyStepUrl = "#step-approve";
    public const string PaymentStepUrl = "#step-pay";

    public static string LevelLabel(string level) => level switch
    {
        LevelFull => "Autopilot เต็ม — สร้าง อนุมัติ ส่ง และแนบลิงก์ชำระเองทั้งหมด",
        LevelPartial => "Autopilot บางส่วน — มีขั้นที่ต้องมีคนทำ (ดูข้อ ⚠️/ⓘ)",
        _ => "สร้างใบร่างเท่านั้น — ต้องมีคนอนุมัติ/ส่งทุกงวด",
    };

    public static Report Evaluate(Facts f)
    {
        var items = new List<Item>();
        void Add(string code, string stage, string verdict, string title, string? why = null, string? url = null, string? label = null)
            => items.Add(new Item(code, stage, verdict, title, why, url, label));

        if (!f.IsDocumentTemplate)
        {
            Add("TEMPLATE", "เอกสาร", Info, "รายการสมุดรายวัน — ไม่มีขั้นส่ง/รับเงิน (Autopilot ใช้กับเอกสารขาย)");
            return Finish(items, f, forceDraftOnly: !f.AutoApprove);
        }

        if (f.TemplateError != null)
            Add("TEMPLATE", "เอกสาร", Fail, "รอบหน้าจะสร้างเอกสารไม่ได้", f.TemplateError, null, null);

        // ── ผู้ติดต่อ ──
        if (f.ContactId == null || !f.ContactUsable)
            Add("CONTACT", "เอกสาร", Fail, f.ContactId == null ? "ยังไม่ได้เลือกผู้ติดต่อ" : "ผู้ติดต่อถูกลบ/ใช้ไม่ได้แล้ว",
                "รายการประจำต้องมีผู้ติดต่อที่ใช้งานอยู่ — เลือกผู้ติดต่อใหม่ในขั้น ① แล้วบันทึก");

        // ── ภาษี: ผู้ซื้อ §86/4 (นิติบุคคล) ──
        if (f.BuyerBlockingFields is { Count: > 0 } missing && f.ContactId is { } bcid)
            Add("BUYER-TAX", "ภาษี", Fail, "ข้อมูลผู้ซื้อไม่ครบตาม §86/4: " + string.Join(", ", missing),
                "ใบกำกับภาษี/ใบเสร็จ-ใบกำกับที่ออกให้นิติบุคคลต้องมีเลขผู้เสียภาษี 13 หลัก สาขา 5 หลัก และที่อยู่ — ระบบจะไม่ออกใบกำกับ/ใบเสร็จอัตโนมัติจนกว่าจะครบ",
                RecurringEmailReadiness.ContactUrl(bcid), "แก้ที่หน้าผู้ติดต่อ");
        else if (f.ContactId != null && f.ContactUsable)
            Add("BUYER-TAX", "ภาษี", Ok, "ข้อมูลผู้ซื้อพอสำหรับเอกสารภาษี");

        // ── อนุมัติ ──
        var approveFails = false;
        if (!f.AutoApprove)
        {
            Add("APPROVE", "อนุมัติ", Info, "ไม่ได้เปิดอนุมัติอัตโนมัติ — ทุกงวดได้ใบร่าง ต้องมีคนอนุมัติก่อนส่ง",
                "ใบร่างยังไม่มีเลขที่จริง (§86/4) จึงส่งให้ลูกค้าไม่ได้", PolicyStepUrl, "เปิดอนุมัติอัตโนมัติ");
        }
        else if (f.ApprovalWarnings == null)
        {
            Add("APPROVE", "อนุมัติ", Info, "ยังตรวจคำเตือนก่อนอนุมัติไม่ได้ (ต้องมีผู้ติดต่อและรายการสินค้า/บริการ)");
        }
        else if (f.ApprovalBlocking.Count > 0)
        {
            approveFails = true;
            var releasable = f.ApprovalBlocking.Where(w => AutoApprovePolicy.CodeOf(w) != null).ToList();
            Add("APPROVE", "อนุมัติ", Fail,
                $"ถ้ารันตอนนี้ การอนุมัติอัตโนมัติจะหยุด เพราะคำเตือน {f.ApprovalBlocking.Count} ข้อ",
                string.Join(" · ", f.ApprovalBlocking)
                    + (releasable.Count > 0 ? " — คำเตือนที่มีรหัส (เช่น ลูกค้าค้างชำระ) เลือกให้ระบบอนุมัติต่อพร้อมบันทึกได้ที่ขั้น \"อนุมัติ\"" : ""),
                releasable.Count > 0 ? PolicyStepUrl : null, releasable.Count > 0 ? "ตั้งนโยบายอนุมัติ" : null);
        }
        else
        {
            Add("APPROVE", "อนุมัติ", Ok, f.ApprovalReleased.Count > 0
                ? $"อนุมัติอัตโนมัติจะผ่าน — นโยบายปล่อยคำเตือน {f.ApprovalReleased.Count} ข้อ (ลงหมายเหตุ + บันทึกตรวจสอบ)"
                : "ไม่มีคำเตือนก่อนอนุมัติ — อนุมัติอัตโนมัติจะผ่าน");
        }

        // ── นโยบาย: ผู้ตั้งยังอยู่ไหม ──
        if (f.Policy is { PassCodes.Count: > 0 } && f.PolicySetterActive == false)
        {
            approveFails = approveFails || f.ApprovalReleased.Count > 0;
            Add("OWNER-POLICY", "อนุมัติ", Fail, "นโยบายอนุมัติข้ามคำเตือนหยุดมีผล",
                f.PolicyInactiveReason ?? "ให้ผู้มีสิทธิ์อนุมัติเอกสารตั้งนโยบายใหม่ (ระบบจะไม่ปล่อยคำเตือนตามนโยบายที่ผู้ตั้งไม่มีสิทธิ์แล้ว)", PolicyStepUrl, "ตั้งนโยบายใหม่");
        }

        // ── ส่ง ──
        if (!f.AutoSendEmail)
            Add("RECIPIENT", "ส่ง", Info, "ไม่ได้เปิดส่งอีเมลอัตโนมัติ — ต้องกดส่งเองทุกงวด");
        else if (!RecurringEmailReadiness.HasDeliverableEmail(f.ContactEmail))
            Add("RECIPIENT", "ส่ง", Fail, RecurringEmailReadiness.NoEmailReason, null,
                f.ContactId is { } ecid ? RecurringEmailReadiness.ContactUrl(ecid) : null, "เพิ่มอีเมลที่หน้าผู้ติดต่อ");
        else
            Add("RECIPIENT", "ส่ง", Ok, "ส่งถึง " + f.ContactEmail!.Trim());

        if (f.AutoSendEmail)
            Add("SMTP", "ส่ง", f.CompanySenderVerified ? Ok : Warn,
                f.CompanySenderVerified ? "ส่งจากอีเมลของบริษัท (ทดสอบผ่านแล้ว)" : "ยังไม่ได้ตั้ง/ทดสอบอีเมลบริษัท — ระบบส่งผ่านอีเมลของ NextAcc ในนามบริษัท (มีเพดานต่อวัน)",
                null, f.CompanySenderVerified ? null : EmailSettingsUrl, f.CompanySenderVerified ? null : "ตั้งค่าอีเมลบริษัท");

        // รอบ 250 (WP-02 §2.4 ข้อ 3 · คำตัดสิน §2 #10): สองกฎครอบใบเดียวกัน — ระบบส่งตามกฎ "เมื่ออนุมัติเอกสาร" ฉบับเดียว (ข้ามฝั่งรายการประจำ) · บอกให้รู้ว่าค่าไหนมีผล
        if (f.AutoApprove && (f.AutoSendEmail || f.RecurringEmailRuleExists) && f.ApprovedEmailRuleCovers)
            Add("EMAIL-RULES", "ส่ง", Warn, "มีกฎส่งทั้งสองแบบ — ระบบส่งตามกฎ \"เมื่ออนุมัติเอกสาร\" ฉบับเดียว",
                "การอนุมัติอัตโนมัติเข้าคิวตามกฎ \"เมื่ออนุมัติเอกสาร\" แล้ว ⇒ การส่งอัตโนมัติของรายการนี้/กฎ \"เมื่อรายการประจำสร้างเอกสาร\" ไม่ส่งซ้ำ "
                + "(ช่องทางรับเงิน/เวลาส่งของรายการนี้ไม่มีผลกับฉบับที่ส่ง) — ถ้าต้องการให้รายการนี้คุม ให้ปิดกฎ \"เมื่ออนุมัติเอกสาร\" ของชนิดนี้",
                EmailScheduleUrl, "เปิดตารางส่งอีเมล");

        // ── รับเงิน ──
        if (!f.SaleSide)
            Add("GATEWAY", "รับเงิน", Info, "เอกสารชนิดนี้ไม่ใช่เอกสารเรียกเก็บเงินจากลูกค้า — ไม่มีลิงก์ชำระ");
        else if (!string.Equals(f.Currency, "THB", StringComparison.OrdinalIgnoreCase))
            Add("THB", "รับเงิน", Info, "ลิงก์ชำระรองรับเงินบาทเท่านั้น — เอกสารสกุลอื่นไม่แนบลิงก์");
        else if (!f.PayLinkAttach)
            Add("GATEWAY", "รับเงิน", Info,
                string.Equals(f.PaymentMode, AutopilotSettingsResolver.TransferOnly, StringComparison.Ordinal)
                    ? "ตั้งให้ลูกค้าโอนเข้าบัญชี — ไม่แนบลิงก์ชำระออนไลน์ (ลูกค้าโอนตามบัญชี/QR บนเอกสาร · คนบันทึกรับชำระ)"
                    : $"ไม่แนบลิงก์ชำระในอีเมล (ตาม{f.PayLinkSource}) — ลูกค้าจ่ายช่องทางอื่น แล้วคนบันทึกรับชำระ",
                null, PaymentStepUrl, "ตั้งช่องทางรับเงิน");
        else if (f.SystemBaseUrlConfigured == false)
            // รอบ 250 (WP-02 §2.4 ข้อ 2 · Y7): ไม่เดาโดเมน — ลิงก์ไม่ถูกแนบ (อีเมลยังส่ง) · ทางไปต่อ = ผู้ดูแลระบบตั้ง URL
            Add("GATEWAY", "รับเงิน", Warn, SystemBaseUrlMissingTitle,
                "ลิงก์ชำระสร้างจาก URL ของระบบ — ยังไม่ได้ตั้ง (หรือยังเป็นค่าที่มากับโปรแกรม) ⇒ อีเมลส่งได้แต่ไม่มีลิงก์/QR ชำระ · แจ้งผู้ดูแลระบบให้ตั้งที่หน้าแอดมิน → ตั้งค่าระบบ",
                SystemSettingsUrl, "ตั้ง URL ของระบบ (ผู้ดูแลระบบ)");
        else if (f.PayLinkUnavailableReason != null)
            Add("GATEWAY", "รับเงิน", Warn, "ตั้งให้แนบลิงก์ชำระ แต่บริษัทยังรับชำระออนไลน์ไม่ได้", f.PayLinkUnavailableReason,
                PaymentSettingsUrl, "ตั้งค่ารับชำระเงินออนไลน์");
        else if (DocumentPayLinkPolicy.OnlineOnly(f.PayLinkAllowed).Count == 0)
            Add("GATEWAY", "รับเงิน", Warn, "ช่องทางที่เลือกไม่มีช่องทางออนไลน์ที่บริษัทเปิดอยู่ — จะไม่แนบลิงก์ชำระ", null,
                PaymentSettingsUrl, "ตรวจช่องทางที่เปิด");
        else
            Add("GATEWAY", "รับเงิน", Ok, "แนบลิงก์ชำระ: " + string.Join(" · ", f.PayLinkAllowed.Select(DocumentPayLinkPolicy.Label)));

        return Finish(items, f, forceDraftOnly: !f.AutoApprove || approveFails
            || items.Any(i => i.Verdict == Fail && i.Code is "TEMPLATE" or "CONTACT"));
    }

    private static Report Finish(List<Item> items, Facts f, bool forceDraftOnly)
    {
        var fails = items.Count(i => i.Verdict == Fail);
        var warns = items.Count(i => i.Verdict == Warn);
        string level;
        if (forceDraftOnly) level = LevelDraftOnly;
        else if (f.AutoSendEmail && f.PayLinkAttach && items.All(i => i.Verdict == Ok))
            level = LevelFull;
        else level = LevelPartial;
        return new Report(level, LevelLabel(level), items, warns, fails);
    }
}
