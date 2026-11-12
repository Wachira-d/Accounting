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
        /// <summary>รอบ 258 (B-M2): เหตุที่ "ผู้รับผิดชอบการอนุมัติของรายการ" อนุมัติไม่ได้แล้ว ณ ตอนนี้ (ตัวเดียวกับรอบรันจริง
        /// <c>RecurringApprovePolicyLoader.AutoApproverRefusalAsync</c>) · null = อนุมัติได้/ไม่ได้ตรวจ (ร่างใหม่ที่ยังไม่บันทึก)</summary>
        public string? AutoApproverRefusal { get; init; }
        /// <summary>เหตุผลที่สร้างเอกสารไม่ได้เลย (ตัวตรวจเทมเพลตเดิม) · null = สร้างได้</summary>
        public string? TemplateError { get; init; }
        /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 2 · คำตัดสิน §2 #9/Y7): ตั้ง URL ของระบบแล้วจริงไหม (<see cref="SystemBaseUrl.IsConfigured"/> — ค่าที่มากับซอร์ส ≠ ตั้งแล้ว) ·
        /// false ⇒ ลิงก์ชำระ/QR/ลิงก์ในอีเมล-LINE ไม่ถูกแนบ · null = ผู้เรียกไม่ได้ตรวจ (ไม่ตัดสิน)</summary>
        public bool? SystemBaseUrlConfigured { get; init; }
        /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 3 · คำตัดสิน §2 #10): มีกฎ "เมื่ออนุมัติเอกสาร" ที่ใช้งานอยู่ครอบชนิดเอกสารนี้</summary>
        public bool ApprovedEmailRuleCovers { get; init; }
        /// <summary>มีกฎ "เมื่อรายการประจำสร้างเอกสาร" ครอบชนิดเอกสารนี้</summary>
        public bool RecurringEmailRuleExists { get; init; }

        // ── รอบ 251 (WP-07 · A B-17/B-18): ข้อเท็จจริงจากผู้อ่านเดิม — null = ผู้เรียกไม่ได้ตรวจ ⇒ ไม่ออกข้อ (ผู้เรียก/เทสต์เดิมไม่ถดถอย) ──

        /// <summary>ช่องทางส่งอีเมลของใบรอบหน้า (ผล <see cref="EtaxDeliveryRouter.Decide"/>) · null = บริษัทไม่ได้ใช้ e-Tax by Email / ไม่ได้ตรวจ</summary>
        public EtaxEmailChannel? EtaxChannel { get; init; }
        /// <summary>เหตุผลของช่องทาง (ข้อความจากตัวตัดสิน)</summary>
        public string? EtaxRouteReason { get; init; }
        /// <summary>เหตุที่ด่านผู้ส่ง e-Tax by Email จะปฏิเสธตอนส่ง (<see cref="EtaxByEmailSenderGate.Refusal"/>) · null = ผ่าน</summary>
        public string? EtaxSenderRefusal { get; init; }
        /// <summary>เหตุที่ผู้รับของ e-Tax by Email จะถูกปฏิเสธตอนส่ง (<see cref="EtaxByEmailFormat.Recipients"/>) · null = ผ่าน</summary>
        public string? EtaxRecipientProblem { get; init; }
        /// <summary>ปัญหาผู้รับมาจากสำเนาลับ (BCC) ของตารางส่ง ⇒ ทางแก้ = หน้าตารางส่ง · false = จากอีเมลของผู้ติดต่อ</summary>
        public bool EtaxRecipientFromSchedule { get; init; }
        /// <summary>ฝ่ายค้านข้อ 5: ชนิดนี้ตัดสิน "เป็นใบกำกับไหม" ตอนอนุมัติจากรูปแบบใบเสร็จ (<see cref="EtaxAutoIssueScope.PredictForUnissued"/>) ⇒ ทำนายไม่ได้ ·
        /// ด่านผู้ส่ง/ผู้รับที่ไม่ผ่านแสดงเป็น ⓘ "ตรวจตอนอนุมัติ" (ไม่ฟ้องแดงหลอก)</summary>
        public bool EtaxScopeUncertain { get; init; }
        /// <summary>ฝ่ายค้านข้อ 4: ใบเสร็จที่งานหลังรับชำระจะออก (อ้างใบชนิดนี้) ต้องไปทาง e-Tax by Email แต่ด่านผู้ส่งปฏิเสธ — null = ผ่าน/ไม่เกี่ยว</summary>
        public string? PostPayReceiptEtaxRefusal { get; init; }

        /// <summary>เอกสารชนิดนี้พิมพ์ QR บัญชีธนาคาร (เทมเพลตเปิด + ชนิดเข้า — <c>PaymentQrPolicy.IsBankQrDocument</c>) · false = ไม่ออกข้อ PAY-QR</summary>
        public bool BankQrApplies { get; init; }
        /// <summary>บัญชีที่จะพิมพ์ QR (ผลตัวเลือกบัญชีเดียวกับกระดาษ) · null = ไม่พิมพ์</summary>
        public string? BankQrAccount { get; init; }
        /// <summary>เหตุที่ไม่พิมพ์ QR (เลือกบัญชีไว้แต่ไม่มี QR · หลายบัญชีไม่เดา) · null = ไม่มีอะไรต้องบอก</summary>
        public string? BankQrProblem { get; init; }

        /// <summary>เพดานอีเมลผ่าน NextAcc ต่อวันของบริษัทนี้ (<c>PlatformEmailQuota</c> → <see cref="SignEmailThrottle"/>) · null = ไม่ได้ตรวจ/ส่งจากอีเมลบริษัท</summary>
        public int? PlatformDailyCap { get; init; }
        public int PlatformSentToday { get; init; }
        public bool PlatformIdentityVerified { get; init; } = true;

        /// <summary>วันที่เอกสารของรอบหน้า (รอบค้าง = วันที่รันจริง)</summary>
        public DateTime? NextDocumentDate { get; init; }
        /// <summary>วันที่นั้นอยู่ในงวดบัญชีที่ปิดแล้ว (<see cref="ClosedPeriodRanges.Covers"/>) · null = ไม่ได้ตรวจ</summary>
        public bool? NextPeriodClosed { get; init; }
        /// <summary>รอบถัด ๆ ไป (ไม่นับรอบหน้า) ที่ตกในงวดที่ปิดแล้ว</summary>
        public IReadOnlyList<DateTime> LaterClosedRunDates { get; init; } = Array.Empty<DateTime>();

        /// <summary>รอบ 257 ชุด 4 (A L13): สร้างล่วงหน้า (วัน) + วันของงวดถัดไป — ใบกำกับภาษีที่สร้างข้ามเดือน ⇒ เดือนภาษีย้าย (เตือน)</summary>
        public int LeadDays { get; init; }
        public DateTime? NextPeriodDate { get; init; }

        /// <summary>หลังรับชำระส่งใบเสร็จอัตโนมัติไหม (<see cref="PostPaymentAutomationPolicy.EffectiveLayers"/>) · null = ไม่ได้ตรวจ</summary>
        public bool? PostPayEmailReceipt { get; init; }
        public bool PostPayIncludePartial { get; init; }

        // ── รอบ 258-3 (F-M1): ความพร้อมทั้งเส้น — LINE · gateway · คู่ค้า (null = ไม่ได้ตรวจ ⇒ ไม่มีแถว) ──
        /// <summary>ปลายทาง LINE ของผู้ติดต่อที่รับเรื่อง "เอกสาร" และส่งได้ (<see cref="ContactLineChannel.LineEndpoints"/>)</summary>
        public int? LineDocumentEndpoints { get; init; }
        /// <summary>ปลายทาง LINE ที่รับเรื่อง "ใบเสร็จ" และส่งได้</summary>
        public int? LineReceiptEndpoints { get; init; }
        /// <summary>ช่อง LINE ของบริษัทยังไม่พร้อม (เหตุ) · null = พร้อม/ไม่ได้ตรวจ</summary>
        public string? LineOaProblem { get; init; }
        /// <summary>ผู้ให้บริการรับชำระอยู่โหมดเงินจริง</summary>
        public bool? GatewayLive { get; init; }
        /// <summary>ผู้ติดต่อเป็นบริษัทคู่ค้า NextAcc (Accepted) — ใบไหลเข้าระบบของเขา</summary>
        public bool? PartnerLinked { get; init; }
    }

    /// <summary>รอบ 258-3 (F-M1): แถวข้อมูลประกอบของเส้นทั้งเส้น — ไม่กดระดับ "อัตโนมัติเต็ม" (ⓘ ไม่ใช่ข้อบกพร่องของรายการ)</summary>
    public static readonly IReadOnlySet<string> ChainInfoCodes = new HashSet<string>(StringComparer.Ordinal) { "LINE-DOC", "LINE-RECEIPT", "PAY-LIVE", "PARTNER" };
    public const string ContactsUrl = "/pages/contacts.html";
    public const string PartnersUrl = "/pages/partners.html";

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
    /// <summary>รอบ 251 (WP-07): ทางแก้ของรหัสใหม่</summary>
    public const string EtaxSettingsUrl = "/pages/settings.html#etax";
    public const string BankAccountsUrl = "/pages/bank.html";
    public const string FiscalPeriodUrl = "/pages/fiscal.html";
    /// <summary>ตั้งแนบลิงก์ชำระแต่ยังออกลิงก์ไม่ได้ — ถ้อยคำเดียวของรายการประจำและการ์ดหน้ารับชำระออนไลน์ (รอบ 260-3)</summary>
    public const string PayLinkNoOnlineTitle = "ตั้งให้แนบลิงก์ชำระ แต่บริษัทยังรับชำระออนไลน์ไม่ได้";
    public const string EtaxSenderFailTitle = "e-Tax by Email: จะล้มที่ด่านผู้ส่งตอนส่ง — ใบกำกับของรอบนี้จะไม่ถูกส่ง";

    /// <summary>แถว PAY-LIVE ตัวเดียว (รอบ 260-3 แยกออกมา) — ผู้อ่าน: <see cref="Evaluate"/> (ความพร้อมของรายการประจำ) และ
    /// <see cref="PaymentChannelReadiness.Build"/> (การ์ดความพร้อมหน้ารับชำระออนไลน์) ⇒ ถ้อยคำ/ระดับเดียวกันทั้งสองหน้า</summary>
    public static Item PayLiveItem(bool live) => new("PAY-LIVE", "รับเงิน", live ? Ok : Info,
        live ? "รับชำระออนไลน์โหมดเงินจริงแล้ว — เงินเข้าลงรับชำระ/ออกใบเสร็จเอง" : "รับชำระออนไลน์ยังเป็นโหมดทดสอบ/ยังไม่ตั้ง — เงินโหมดทดสอบไม่มีผลทางบัญชี",
        null, PaymentSettingsUrl, live ? null : "ตั้งรับชำระออนไลน์");

    private static string D(DateTime? d) => d is { } v ? ThaiDate.ToThaiDisplayString(v) : "-";

    public static string LevelLabel(string level) => level switch
    {
        LevelFull => "Autopilot เต็ม — สร้าง อนุมัติ ส่ง และแนบลิงก์ชำระเองทั้งหมด",
        LevelPartial => "Autopilot บางส่วน — มีขั้นที่ต้องมีคนทำ (ดูข้อ ⚠️/ⓘ)",
        _ => "สร้างใบร่างเท่านั้น — ต้องมีคนอนุมัติ/ส่งทุกงวด",
    };

    /// <summary>ฝ่ายค้านข้อ 3: ใบของรายการนี้จะถูกส่งอีเมลอัตโนมัติไหม — กฎ "เมื่ออนุมัติเอกสาร" (อนุมัติมือหรืออัตโนมัติ) ·
    /// หรืออนุมัติตอนสร้าง + (ติ๊กส่งของรายการ หรือกฎ "เมื่อรายการประจำสร้างเอกสาร") — ตัวเดียวกับเส้นเข้าคิว (<c>EmailScheduleService</c>)</summary>
    public static bool WillEmail(Facts f) => f.ApprovedEmailRuleCovers || (f.AutoApprove && (f.AutoSendEmail || f.RecurringEmailRuleExists));

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
        // รอบ 258 (B-M2): ผู้รับผิดชอบการอนุมัติของรายการหมดสิทธิ์/ออกจากบริษัท ⇒ ทุกรอบค้างร่าง (RECURRING-APPROVER-GONE) — เดิมแผงยังเขียว ·
        // เป็นข้อเพิ่ม (ข้อคำเตือนก่อนอนุมัติด้านล่างยังแสดงตามเดิม) — แต่ไม่ขึ้นข้อ "อนุมัติอัตโนมัติจะผ่าน" ขัดกัน
        if (f.AutoApprove && f.AutoApproverRefusal != null)
        {
            approveFails = true;
            Add("APPROVER-GONE", "อนุมัติ", Fail, "ถ้ารันตอนนี้ ใบจะค้างเป็นร่าง — ผู้รับผิดชอบการอนุมัติของรายการไม่มีสิทธิ์แล้ว",
                f.AutoApproverRefusal, PolicyStepUrl, "ไปขั้นอนุมัติ");
        }
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
        else if (f.AutoApproverRefusal == null)
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
        // รอบ 257 ชุด 4 (A-M6): ค่าตั้งต้นรอบ 256 ไม่มีผล (ผู้สร้างหมดสิทธิ์ · ค่ารายการเปลี่ยนหลังสร้าง/รับรอง) — เดิมไม่มีนโยบายที่ตั้ง ⇒ แผงเงียบ ·
        // บอกเหตุจากตัวโหลด (DefaultSetterGoneReason / BindingChangedReason) · Warn ไม่ใช่ Fail: ใบยังสร้างได้ แค่ "ลูกค้าค้างชำระ" จะหยุดรอคน
        else if (f.Policy == null && f.AutoApprove && f.PolicySetterActive == false && f.PolicyInactiveReason != null)
            Add("OWNER-POLICY", "อนุมัติ", Warn, "ค่าเริ่มต้น (ลูกค้าค้างชำระ ⇒ อนุมัติต่อ) ไม่มีผลกับรายการนี้", f.PolicyInactiveReason, PolicyStepUrl, "ตั้ง/รับรองนโยบาย");

        // ── รอบ 257 ชุด 4 (A L13): ใบกำกับภาษีสร้างล่วงหน้าข้ามเดือน ⇒ เดือนภาษีของใบ = เดือนที่สร้าง (ไม่ใช่เดือนของงวด) ──
        if (f.DocumentType == Accounting.Models.Enums.DocumentType.TaxInvoice && f.NextPeriodDate is { } np
            && RecurringSchedule.LeadDaysMovesVatMonth(np, f.LeadDays))
            Add("LEAD-VAT-MONTH", "ภาษี", Warn,
                $"สร้างล่วงหน้า {f.LeadDays} วัน ⇒ ใบกำกับภาษีของงวด {D(np)} ลงวันที่ {D(RecurringSchedule.CreateAt(np, f.LeadDays))} — ภาษีขายเข้าเดือนก่อนเดือนของงวด",
                "ใบกำกับภาษีลงเดือนภาษีตามวันที่ออก (ป.รัษฎากร §78/§78/1 · ภ.พ.30 ของเดือนที่สร้าง) — ถ้าต้องการให้อยู่เดือนของงวด ลดวันสร้างล่วงหน้า หรือใช้ใบแจ้งหนี้แล้วออกใบกำกับตอนรับชำระ",
                null, null);

        // ── รอบ 251 (WP-07 · PERIOD): งวดบัญชีของวันที่เอกสารรอบหน้า (กติกางวดปิดเดียวกับ ClosedPeriodRanges) ──
        if (f.NextPeriodClosed == true)
        {
            approveFails = true;   // ตัวสร้างเอกสารปฏิเสธวันที่ในงวดที่ปิดตั้งแต่ตอนสร้าง ⇒ รอบนี้ไม่ได้แม้แต่ใบร่าง
            Add("PERIOD", "อนุมัติ", Fail, $"วันที่เอกสารรอบหน้า ({D(f.NextDocumentDate)}) อยู่ในงวดบัญชีที่ปิดแล้ว — รอบนี้จะสร้างเอกสารไม่ได้",
                "ระบบไม่ให้สร้าง/อนุมัติเอกสารที่มีวันที่ในงวดที่ปิด (รอบจะล้มและแจ้งในผลรอบล่าสุด) — เปิดงวดที่หน้างวดบัญชี (ต้องมีสิทธิ์) หรือเลื่อนวันรันครั้งถัดไป",
                FiscalPeriodUrl, "เปิดหน้างวดบัญชี");
        }
        else if (f.NextPeriodClosed == false && f.LaterClosedRunDates.Count > 0)
            Add("PERIOD", "อนุมัติ", Warn, "รอบถัด ๆ ไปบางรอบตกในงวดบัญชีที่ปิดแล้ว: " + string.Join(", ", f.LaterClosedRunDates.Select(x => D(x))),
                "รอบเหล่านั้นจะสร้างเอกสารไม่ได้ถ้างวดยังปิดอยู่ตอนถึงรอบ", FiscalPeriodUrl, "เปิดหน้างวดบัญชี");
        else if (f.NextPeriodClosed == false)
            Add("PERIOD", "อนุมัติ", Ok, $"งวดบัญชีของรอบหน้า ({D(f.NextDocumentDate)}) เปิดอยู่");

        // ── ส่ง ──
        if (!f.AutoSendEmail)
            Add("RECIPIENT", "ส่ง", Info, "ไม่ได้เปิดส่งอีเมลอัตโนมัติ — ต้องกดส่งเองทุกงวด");
        else if (!RecurringEmailReadiness.HasDeliverableEmail(f.ContactEmail))
            Add("RECIPIENT", "ส่ง", Fail, RecurringEmailReadiness.NoEmailReason, null,
                f.ContactId is { } ecid ? RecurringEmailReadiness.ContactUrl(ecid) : null, "เพิ่มอีเมลที่หน้าผู้ติดต่อ");
        else
            Add("RECIPIENT", "ส่ง", Ok, "ส่งถึง " + f.ContactEmail!.Trim());

        // รอบ 251 (WP-07 · B-17/F-06): ใบที่ต้องส่งแบบ e-Tax by Email ไม่มีทางถอยไปอีเมลของ NextAcc (ด่าน EtaxByEmailSenderGate) ⇒ ข้อ SMTP แบบเดิมไม่จริงสำหรับใบนี้ —
        // ผู้ส่งตัดสินที่ข้อ ETAX-SENDER แทน
        var etaxEmail = f.EtaxChannel == EtaxEmailChannel.EtaxByEmail && !f.EtaxScopeUncertain;
        var willEmail = WillEmail(f);
        if (f.AutoSendEmail && !etaxEmail)
            Add("SMTP", "ส่ง", f.CompanySenderVerified ? Ok : Warn,
                f.CompanySenderVerified ? "ส่งจากอีเมลของบริษัท (ทดสอบผ่านแล้ว)" : "ยังไม่ได้ตั้ง/ทดสอบอีเมลบริษัท — ระบบส่งผ่านอีเมลของ NextAcc ในนามบริษัท (มีเพดานต่อวัน)",
                null, f.CompanySenderVerified ? null : EmailSettingsUrl, f.CompanySenderVerified ? null : "ตั้งค่าอีเมลบริษัท");

        // ฝ่ายค้านข้อ 3: ประเมินเมื่อใบจะถูกส่งจริง (ติ๊กส่ง หรือกฎในตารางส่ง) — ไม่ใช่เฉพาะติ๊กส่งของรายการ
        if (willEmail && f.EtaxChannel is { } channel)
        {
            if (channel == EtaxEmailChannel.EtaxByEmail)
            {
                // ฝ่ายค้านข้อ 5: รูปแบบใบเสร็จตัดสินตอนอนุมัติ ⇒ ไม่รู้แน่ว่าเป็นใบกำกับ — ด่านที่ไม่ผ่านเป็น ⓘ "ตรวจตอนอนุมัติ"
                var unsure = f.EtaxScopeUncertain;
                var failOrCheck = unsure ? Info : Fail;
                Add("ETAX", "ส่ง", unsure ? Info : Ok, unsure
                        ? "ตรวจตอนอนุมัติ — ถ้ารูปแบบที่ออกเป็นใบกำกับภาษี ใบของรอบนี้จะส่งแบบ e-Tax by Email (สำเนาถึง ETDA)"
                        : "ใบของรอบนี้ส่งแบบ e-Tax by Email (สำเนาถึง ETDA เพื่อประทับเวลา)", f.EtaxRouteReason);
                if (f.EtaxSenderRefusal != null)
                    Add("ETAX-SENDER", "ส่ง", failOrCheck, unsure ? "e-Tax by Email: ถ้าออกเป็นใบกำกับ จะล้มที่ด่านผู้ส่ง (ตรวจตอนอนุมัติ)" : EtaxSenderFailTitle,
                        f.EtaxSenderRefusal, EtaxSettingsUrl, "ตั้งค่า e-Tax / อีเมลบริษัท");
                else
                    Add("ETAX-SENDER", "ส่ง", Ok, "ผู้ส่ง e-Tax by Email พร้อม — ส่งจากอีเมลที่ลงทะเบียนกับกรมสรรพากร (ทดสอบผ่านแล้ว)");
                if (f.EtaxRecipientProblem != null)
                    Add("ETAX-RECIPIENT", "ส่ง", failOrCheck, unsure ? "e-Tax by Email: ถ้าออกเป็นใบกำกับ ผู้รับไม่ตรงกติกา (ตรวจตอนอนุมัติ)"
                            : "e-Tax by Email: ผู้รับไม่ตรงกติกา — อีเมลของรอบนี้จะล้มตอนส่ง", f.EtaxRecipientProblem,
                        f.EtaxRecipientFromSchedule ? EmailScheduleUrl : f.ContactId is { } rcid ? RecurringEmailReadiness.ContactUrl(rcid) : null,
                        f.EtaxRecipientFromSchedule ? "แก้สำเนาลับในตารางส่ง" : "แก้อีเมลที่หน้าผู้ติดต่อ");
                else if (RecurringEmailReadiness.HasDeliverableEmail(f.ContactEmail))
                    Add("ETAX-RECIPIENT", "ส่ง", Ok, "ผู้รับ e-Tax by Email: ผู้ซื้อ 1 อีเมล + สำเนาประทับเวลาของ ETDA (ระบบใส่ให้)");
            }
            else
                Add("ETAX", "ส่ง", Ok, "ใบของรอบนี้ส่งเป็นอีเมลเอกสารธรรมดา (บริษัทใช้ e-Tax by Email แต่ใบนี้อยู่นอกขอบเขต)", f.EtaxRouteReason);
        }

        // รอบ 251 (WP-07 · QUOTA): ตัวจำกัดจริงของอีเมลเอกสารที่ส่งผ่าน NextAcc = เพดานรายวัน (SignEmailThrottle ผ่าน PlatformEmailQuota) —
        // อีเมลเอกสารไม่ตัดโควตาข้อความแจ้งเตือน (NotifyQuotaPolicy นับเฉพาะ LINE OA/อีเมลระบบของการแจ้งเตือนลูกค้า) ⇒ ไม่บอกตัวเลขที่ไม่ใช่ตัวจำกัด
        if (f.AutoSendEmail && !etaxEmail && !f.CompanySenderVerified && f.PlatformDailyCap is int cap)
        {
            const string QuotaWhy = "เพดานนับใหม่ทุกวัน (เวลาไทย) · อีเมลเอกสารไม่ตัดโควตาข้อความแจ้งเตือน · ตั้งอีเมลบริษัทแล้วทดสอบผ่าน = ไม่มีเพดานนี้";
            if (f.PlatformSentToday >= cap)
                Add("QUOTA", "ส่ง", Warn, $"วันนี้ส่งอีเมลผ่าน NextAcc ครบเพดาน {cap} ฉบับแล้ว — ถ้ารอบถึงวันนี้ อีเมลจะส่งไม่ได้จนถึงพรุ่งนี้", QuotaWhy,
                    EmailSettingsUrl, "ตั้งค่าอีเมลบริษัท");
            else if (!f.PlatformIdentityVerified)
                Add("QUOTA", "ส่ง", Warn, $"บริษัทยังไม่ยืนยันตัวตน — เพดานอีเมลผ่าน NextAcc {cap} ฉบับ/วัน (วันนี้ใช้ไป {f.PlatformSentToday})",
                    "ยืนยันตัวตน = เลขผู้เสียภาษีของบริษัทถูกต้อง + เคยชำระค่าบริการ NextAcc · " + QuotaWhy, EmailSettingsUrl, "ตั้งค่าอีเมลบริษัท");
            else
                Add("QUOTA", "ส่ง", Ok, $"เพดานอีเมลผ่าน NextAcc {cap} ฉบับ/วัน (วันนี้ใช้ไป {f.PlatformSentToday})", QuotaWhy);
        }

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
            Add("GATEWAY", "รับเงิน", Warn, PayLinkNoOnlineTitle, f.PayLinkUnavailableReason,
                PaymentSettingsUrl, "ตั้งค่ารับชำระเงินออนไลน์");
        else if (DocumentPayLinkPolicy.OnlineOnly(f.PayLinkAllowed).Count == 0)
            Add("GATEWAY", "รับเงิน", Warn, "ช่องทางที่เลือกไม่มีช่องทางออนไลน์ที่บริษัทเปิดอยู่ — จะไม่แนบลิงก์ชำระ", null,
                PaymentSettingsUrl, "ตรวจช่องทางที่เปิด");
        else
            Add("GATEWAY", "รับเงิน", Ok, "แนบลิงก์ชำระ: " + string.Join(" · ", f.PayLinkAllowed.Select(DocumentPayLinkPolicy.Label)));

        // ── รอบ 251 (WP-07 · PAY-QR): QR บัญชีธนาคารบนเอกสาร (ตัวเลือกบัญชีเดียวกับกระดาษ — PaymentQrService.DiagnoseTemplateAsync → PaymentQrPolicy) ──
        var thb = string.Equals(f.Currency, "THB", StringComparison.OrdinalIgnoreCase);
        if (f.SaleSide && thb && f.BankQrApplies)
        {
            if (f.BankQrAccount != null)
                Add("PAY-QR", "รับเงิน", Ok, "QR ชำระเงินบนเอกสาร: " + f.BankQrAccount);
            else if (f.BankQrProblem != null)
                Add("PAY-QR", "รับเงิน", Warn, "QR ชำระเงินบนเอกสารจะไม่ถูกพิมพ์", f.BankQrProblem, BankAccountsUrl, "ตั้ง QR ที่บัญชีธนาคาร");
            else if (!f.PayLinkAttach)
                Add("PAY-QR", "รับเงิน", Warn, "ยังไม่มีบัญชีธนาคารที่ตั้ง QR — ลูกค้าที่โอนต้องพิมพ์เลขบัญชีเอง",
                    "ตั้งเลขพร้อมเพย์หรืออัปโหลดรูป QR ที่บัญชีธนาคาร แล้วเอกสารจะพิมพ์ QR ให้เอง", BankAccountsUrl, "ตั้งพร้อมเพย์/QR");
            else
                Add("PAY-QR", "รับเงิน", Ok, "ลูกค้าชำระผ่านลิงก์ชำระได้ — ยังไม่มี QR บัญชีธนาคารบนเอกสาร (ไม่บังคับ)", null, BankAccountsUrl, "ตั้งพร้อมเพย์/QR");
        }

        // ── รอบ 251 (WP-07 · POSTPAY): หลังรับชำระ (ชั้น ตาราง › รายการ › ผู้ติดต่อ › บริษัท — PostPaymentAutomationPolicy.EffectiveLayers) · ปิด ⇒ ⓘ (ระดับบางส่วน) ──
        // ฝ่ายค้านข้อ 4: ใบเสร็จหลังรับชำระของบริษัทโหมด e-Tax by Email ไปทาง e-Tax — ด่านผู้ส่งปฏิเสธ ⇒ งานหลังรับชำระจะล้มตอนส่ง
        // (⚠️ ไม่ใช่ ❌: รูปแบบใบเสร็จตัดสินตอนออก — ฝ่ายค้านข้อ 5)
        if (f.SaleSide && f.PostPayEmailReceipt == true && f.PostPayReceiptEtaxRefusal != null)
            Add("POSTPAY", "หลังรับเงิน", Warn, "หลังรับชำระ ใบเสร็จ(-ใบกำกับ) จะต้องส่งแบบ e-Tax by Email แต่ด่านผู้ส่งยังไม่ผ่าน — งานส่งใบเสร็จจะล้ม",
                f.PostPayReceiptEtaxRefusal, EtaxSettingsUrl, "ตั้งค่า e-Tax / อีเมลบริษัท");
        else if (f.SaleSide && f.PostPayEmailReceipt is bool postPay)
            Add("POSTPAY", "หลังรับเงิน", postPay ? Ok : Info,
                postPay
                    ? "หลังรับชำระ ระบบออก/ส่งใบเสร็จถึงลูกค้าอัตโนมัติ — " + (f.PostPayIncludePartial ? "ทุกงวดที่รับ (รวมชำระบางส่วน)" : "เฉพาะงวดที่ชำระครบ")
                    : "หลังรับชำระ ไม่ส่งใบเสร็จอัตโนมัติ — ต้องมีคนออก/ส่งใบเสร็จเอง",
                null, postPay ? null : PaymentStepUrl, postPay ? null : "ตั้งงานหลังรับชำระ");

        // ── รอบ 258-3 (F-M1): ความพร้อมทั้งเส้น (บิล → LINE → รับเงินจริง → ใบเสร็จ → คู่ค้า) ──
        if (f.SaleSide && f.LineDocumentEndpoints is int lineDoc)
            Add("LINE-DOC", "ส่ง", lineDoc > 0 ? Ok : Info,
                lineDoc > 0
                    ? $"ผู้ติดต่อผูก LINE ที่รับเรื่องเอกสาร {lineDoc} ปลายทาง — ติ๊กส่งเอกสารแล้วการ์ด LINE ไปด้วย (ถ้ามีกฎในตารางส่ง ต้องติ๊ก LINE ในกฎเอง)"
                    : "ผู้ติดต่อยังไม่มี LINE ที่รับเรื่องเอกสาร — ส่งทางอีเมลอย่างเดียว",
                f.LineOaProblem, ContactsUrl, lineDoc > 0 ? null : "ผูก LINE ที่ผู้ติดต่อ");
        if (f.SaleSide && f.PostPayEmailReceipt == true && f.LineReceiptEndpoints is int lineRc)
            Add("LINE-RECEIPT", "หลังรับเงิน", lineRc > 0 ? Ok : Info,
                lineRc > 0 ? $"ใบเสร็จหลังรับชำระไปทาง LINE ด้วย ({lineRc} ปลายทางที่ติ๊ก “ใบเสร็จ”)" : "ใบเสร็จหลังรับชำระไปทางอีเมลอย่างเดียว (ไม่มี LINE ที่ติ๊ก “ใบเสร็จ”)",
                null, ContactsUrl, lineRc > 0 ? null : "ตั้งช่องทางแจ้งเตือนของผู้ติดต่อ");
        if (f.SaleSide && f.GatewayLive is bool live)
            items.Add(PayLiveItem(live));
        if (f.SaleSide && f.PartnerLinked == true)
            Add("PARTNER", "คู่ค้า", Ok,
                "ผู้ติดต่อเป็นคู่ค้า NextAcc — ใบแจ้งหนี้และใบเสร็จ/ใบกำกับ ณ วันรับเงินเข้าระบบของเขา · จ่ายผ่านลิงก์แล้วเขาได้รับแจ้งให้กดบันทึกจ่าย",
                null, PartnersUrl, "หน้าคู่ค้า");

        return Finish(items, f, forceDraftOnly: !f.AutoApprove || approveFails
            || items.Any(i => i.Verdict == Fail && i.Code is "TEMPLATE" or "CONTACT"));
    }

    private static Report Finish(List<Item> items, Facts f, bool forceDraftOnly)
    {
        var fails = items.Count(i => i.Verdict == Fail);
        var warns = items.Count(i => i.Verdict == Warn);
        string level;
        if (forceDraftOnly) level = LevelDraftOnly;
        else if (f.AutoSendEmail && f.PayLinkAttach && items.Where(i => !ChainInfoCodes.Contains(i.Code)).All(i => i.Verdict == Ok))
            level = LevelFull;
        else level = LevelPartial;
        return new Report(level, LevelLabel(level), items, warns, fails);
    }
}
