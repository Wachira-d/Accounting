using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **งานอัตโนมัติหลังลูกค้าชำระเงิน — ตัวตัดสินเดียว** (รอบ 241 · คำตัดสิน 241-5..241-9 · DOCUMENT_FLOW §2.12g)
///
/// <para>ไม่มีตรรกะลงบัญชีใหม่: ใบเสร็จ / ใบเสร็จรับเงิน-ใบกำกับภาษี (§78/1) ออกด้วยเส้นเดิม (<c>CreatePaymentAsync</c> ออกให้ตอนบันทึกรับชำระ ·
/// ถ้ายังไม่มี ⇒ <c>IssueReceiptForPaymentAsync</c> — <c>SettlementReceiptPolicy</c> ตัดสิน VAT/หัวเอกสาร · เลขออกตอนอนุมัติ gap-free) ·
/// helper นี้ตัดสินแค่ "ทำขั้นไหน · ขั้นไหนต้องหยุดแล้วบอกคน"</para>
///
/// <para>50 ทวิ (คำตัดสิน 241-7 · ปรับขอบเขตจากเจ้าของ 2026-11-06): ฝั่งขาย<b>ลูกค้าเป็นผู้ออก</b> — รอบนี้ทำแค่ "ตรวจว่าการชำระที่มีหัก ณ ที่จ่าย
/// มีแถวรอ 50 ทวิ ในทะเบียนเครดิตภาษีเดิม" (แถวสร้างโดย <c>SyncWhtCreditReceivedAsync</c> ในเส้นบันทึกรับชำระ — ไม่สร้างสำเนาที่สอง) ·
/// ลิงก์อัปโหลด/อีเมลทวง/ออก 50 ทวิ ข้ามบริษัท = ทีมออกแบบส่งแบบมารอบถัดไป (จุดต่อ: <see cref="ReceiptEmailSections"/>)</para>
/// </summary>
public static class PostPaymentAutomationPolicy
{
    public const string RuleCode = "POSTPAY-241";

    /// <summary>รอบ 250 (WP-02 §2.1 · คำตัดสิน §2 #4): ผลของ "ทางเข้ารับชำระนี้ต้องเข้าคิวงานหลังรับชำระไหม" — ตัดสินที่
    /// <see cref="ForEnqueue"/> ตัวเดียว (ผู้เรียกเดียว = <c>PostPaymentAutomationService.EnqueueCoreAsync</c> ที่ <c>DocumentService.CreatePaymentAsync</c>
    /// เรียกหลัง commit ทุกทางเข้า)</summary>
    public enum EnqueueKind
    {
        /// <summary>ไม่สร้างแถว (ปิดค่าตั้ง · การชำระหลายใบ)</summary>
        None,
        /// <summary>สร้างแถวรอประมวลผล</summary>
        Pending,
        /// <summary>สร้างแถวสถานะ "ไม่มีอะไรต้องทำ" พร้อมเหตุ — ให้หน้าเอกสารเห็นว่าระบบรู้และตั้งใจไม่ทำ (ไม่ใช่ไม่มีแถวเงียบ ๆ)</summary>
        Skipped,
    }

    public sealed record EnqueueDecision(EnqueueKind Kind, string? Reason);

    public const string MultiDocReason =
        "การชำระนี้กระจายไปหลายเอกสาร — ระบบไม่ออก/ส่งใบเสร็จอัตโนมัติ (ยอดต่อใบเป็นการจัดสรร · SettlementReceiptPolicy) · ออกใบเสร็จที่เอกสารแต่ละใบแล้วส่งเอง";

    /// <summary>ทางเข้าที่มีเส้นออกใบเสร็จ/แจ้งลูกค้าของตัวเอง (หน้าร้านออนไลน์ · การจอง · ที่พัก · ค่าบริการแพลตฟอร์ม) ⇒ ไม่ออก/ไม่ส่งซ้ำ · null = เดินเต็ม</summary>
    private static string? OwnReceiptPathReason(PaymentEntrySource source) => source switch
    {
        PaymentEntrySource.Cms => "มีเส้นออกใบเสร็จของตัวเอง (หน้าร้านออนไลน์/การจองบริการ) — งานหลังรับชำระไม่ออก/ไม่ส่งใบเสร็จซ้ำ",
        PaymentEntrySource.Lodging => "มีเส้นออกใบเสร็จของตัวเอง (โมดูลที่พัก — ใบเช็คเอาต์/ใบกำกับล่วงหน้า) — งานหลังรับชำระไม่ออก/ไม่ส่งใบเสร็จซ้ำ",
        PaymentEntrySource.PlatformBilling => "มีเส้นออกใบเสร็จของตัวเอง (ใบเสร็จค่าบริการแพลตฟอร์ม) — งานหลังรับชำระไม่ออก/ไม่ส่งใบเสร็จซ้ำ",
        _ => null,
    };

    /// <summary>ทางเข้านี้ + ค่าตั้งที่มีผล ⇒ สร้างแถวงานแบบไหน · การชำระหลายใบไม่สร้างแถว (เหตุ <see cref="MultiDocReason"/>) · ปิดค่าตั้ง = ไม่สร้าง (พฤติกรรมเดิม) ·
    /// ทางเข้าที่มีเส้นออกใบเสร็จของตัวเอง = แถว Skipped พร้อมเหตุ · อื่น ๆ (มือ · gateway · รอบโอน · นำเข้าไฟล์ · integration) = เดินเต็ม</summary>
    public static EnqueueDecision ForEnqueue(Settings s, PaymentEntrySource source)
    {
        if (source == PaymentEntrySource.MultiDoc) return new EnqueueDecision(EnqueueKind.None, MultiDocReason);
        if (!s.Any) return new EnqueueDecision(EnqueueKind.None, "ปิดงานอัตโนมัติหลังรับชำระ");
        if (OwnReceiptPathReason(source) is string own) return new EnqueueDecision(EnqueueKind.Skipped, own);
        // ฝ่ายค้าน WP-02 ข้อ 4: นำเข้าการชำระย้อนหลังทั้งไฟล์ตอนเปิดส่งใบเสร็จอัตโนมัติ ⇒ ลูกค้าได้อีเมลใบเสร็จเก่าเป็นชุด — ค่าตั้งต้นไม่ส่ง
        // (แถว Skipped พร้อมเหตุ) · ผู้นำเข้าเลือก "ส่งใบเสร็จ" ในตัวเลือกของชุดเอง ⇒ ImportSendReceipt (เดินเต็ม)
        if (source == PaymentEntrySource.Import) return new EnqueueDecision(EnqueueKind.Skipped, ImportNoSendReason);
        return new EnqueueDecision(EnqueueKind.Pending, null);
    }

    public const string ImportNoSendReason =
        "รับชำระที่นำเข้าจากไฟล์ — ไม่ส่งใบเสร็จถึงลูกค้าอัตโนมัติ (กันส่งใบเสร็จย้อนหลังเป็นชุด) · ถ้าต้องการให้เลือก \"ส่งใบเสร็จถึงลูกค้า\" ตอนนำเข้า หรือส่งเองจากหน้าเอกสาร";

    /// <summary>ชื่อทางเข้าที่เก็บในแถวงาน (ชื่อ enum) — แถวเก่าเก็บ "manual"/"gateway" ตัวเล็ก ⇒ อ่านแบบไม่สนตัวพิมพ์ · ไม่รู้จัก ⇒ Manual</summary>
    public static PaymentEntrySource ParseSource(string? stored)
        => Enum.TryParse<PaymentEntrySource>(stored, true, out var s) && Enum.IsDefined(typeof(PaymentEntrySource), s) ? s : PaymentEntrySource.Manual;

    /// <summary>รอบ 250 (WP-02 B-13): ชั้นค่าตั้งงานหลังรับชำระ (เฉพาะเจาะจงชนะ): ตารางส่งที่ส่งใบนี้ › รายการประจำที่สร้างใบนี้ › ผู้ติดต่อ › บริษัท ·
    /// null ทุกช่องของชั้น = ตามชั้นถัดไป ⇒ ไม่ตั้งรายการ/ผู้ติดต่อ = ผลเท่ารอบ 241 เป๊ะ</summary>
    public static Settings EffectiveLayers(Settings company, Overrides? schedule, Overrides? recurring, Overrides? contact)
    {
        var layers = new[] { schedule, recurring, contact };
        bool? email = null, partial = null;
        foreach (var l in layers)
        {
            if (email == null && l?.EmailReceipt is bool e) email = e;
            if (partial == null && l?.IncludePartial is bool p) partial = p;
        }
        return new Settings(email ?? company.EmailReceipt, partial ?? company.IncludePartial);
    }

    /// <summary>ค่าตั้งที่มีผล (บริษัท + ตารางส่งเอกสารที่ส่งใบนี้ทับได้ทีละช่อง)</summary>
    public sealed record Settings(bool EmailReceipt, bool IncludePartial)
    {
        public bool Any => EmailReceipt;
    }

    /// <summary>ช่องของตารางส่งเอกสาร: null = ตามค่าบริษัท</summary>
    public sealed record Overrides(bool? EmailReceipt, bool? IncludePartial);

    public static Settings Effective(Settings company, Overrides? schedule)
        => schedule == null ? company : new Settings(
            schedule.EmailReceipt ?? company.EmailReceipt,
            schedule.IncludePartial ?? company.IncludePartial);

    public enum Step
    {
        /// <summary>ยังไม่มีใบเสร็จของการชำระนี้ ⇒ ออกด้วยเส้นเดิม <c>IssueReceiptForPaymentAsync</c></summary>
        IssueReceipt,
        /// <summary>ส่งใบเสร็จ (หรือใบเสร็จ/ใบกำกับภาษี) ของการชำระนี้ถึงลูกค้า</summary>
        EmailReceipt,
        /// <summary>ใบต้นทางเป็นใบเสร็จของการชำระนี้อยู่แล้ว (หัว "ใบกำกับภาษี/ใบเสร็จรับเงิน") ⇒ ส่งใบต้นทาง</summary>
        EmailSourceAsReceipt,
    }

    /// <summary>ข้อเท็จจริง ณ ตอนประมวลผล (ผู้เรียกโหลดจากฐาน — ไม่มีค่าเริ่มต้นสำหรับช่องที่ตัดสินผลทางกฎหมาย)</summary>
    public sealed record Facts(
        bool SaleSide,
        DocumentStatus SourceStatus,
        bool PaymentVoided,
        bool FullyPaid,
        int AllocationCount,
        bool HasReceipt,
        bool ReceiptIsDraft,
        bool ReceiptCarriesVat,
        bool SourceServesAsReceipt,
        bool MayCarryVatIfIssued,
        string? CustomerEmail,
        IReadOnlyList<string>? BuyerBlockingFields,
        decimal PaymentWht,
        bool WhtCreditRegistered,
        // รอบ 250 (WP-02): ทางเข้าที่บันทึกการชำระ (ชื่อในแถวงาน) · เลขใบกำกับภาษีที่อ้างใบแจ้งหนี้นี้และยังมีผล (null = ไม่มี) — ไม่มีค่าเริ่มต้น
        PaymentEntrySource Source,
        string? SupersedingTaxInvoiceNumber,
        // รอบ 250 WP-08: ผู้ติดต่อมีอีเมลแต่ไม่รับเรื่อง "ใบเสร็จ" (ContactRecipientRule.ForEmail) — บอกเหตุนี้แทน "ไม่มีอีเมล" · null = ไม่ได้ติดที่ค่าตั้งนี้
        string? EmailOptOutReason = null,
        // ฝ่ายค้าน WP-08 HIGH-2/MED-4: ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" (AutopilotSettingsResolver.AutoSendBlock) — ห้ามเฉพาะขั้นส่งอีเมล (ทุกชั้นค่าตั้ง) · การออกใบเสร็จยังเดินตามเดิม
        string? AutoSendBlockedReason = null);

    /// <summary>ผล — ขั้นที่ทำ + เหตุที่ต้องหยุด (ข้อความไทยพร้อมทางไปต่อ · ส่งเข้าการแจ้งเตือน) · <paramref name="Notes"/> = ข้อสังเกตที่ไม่หยุดงาน ·
    /// <paramref name="Skipped"/> = ไม่มีอะไรต้องทำ (ไม่ใช่ความผิดพลาด)</summary>
    public sealed record Plan(IReadOnlyList<Step> Steps, IReadOnlyList<string> Blocked, IReadOnlyList<string> Notes, string? Skipped);

    public const string WhtCreditMissingNote =
        "การชำระนี้มีหัก ณ ที่จ่าย แต่ไม่พบแถวรอ 50 ทวิ ในทะเบียนเครดิตภาษีที่ถูกหัก — ตรวจที่ ภาษี → ภาษีถูกหัก ณ ที่จ่าย (เพิ่มรายการรอหนังสือรับรองเอง)";

    public static Plan Decide(Settings s, Facts f)
    {
        var steps = new List<Step>();
        var blocked = new List<string>();
        var notes = new List<string>();
        if (!s.Any) return new Plan(steps, blocked, notes, "ปิดงานอัตโนมัติหลังรับชำระ");
        if (f.PaymentVoided) return new Plan(steps, blocked, notes, "การชำระถูกยกเลิกแล้ว");
        if (!f.SaleSide) return new Plan(steps, blocked, notes, "เอกสารฝั่งซื้อ — ไม่มีเอกสารต้องส่งถึงลูกค้า");
        if (f.SourceStatus is DocumentStatus.Voided or DocumentStatus.Rejected or DocumentStatus.Draft)
            return new Plan(steps, blocked, notes, "เอกสารต้นทางถูกยกเลิก/ไม่ได้อนุมัติ — ไม่ส่งเอกสารต่อ");
        // รอบ 250 (WP-02 §2.1): ทางเข้าที่มีเส้นออกใบเสร็จของตัวเอง/การชำระหลายใบ — ตัวตัดสินเดียวกับตอนเข้าคิว (กันแถวที่เข้าคิวไว้ก่อนแก้)
        if (f.Source == PaymentEntrySource.MultiDoc) return new Plan(steps, blocked, notes, MultiDocReason);
        if (OwnReceiptPathReason(f.Source) is string own) return new Plan(steps, blocked, notes, own);
        // รอบ 250 (WP-02 §2.3 · คำตัดสิน §2 #16): ใบแจ้งหนี้ที่ออกใบกำกับภาษีอ้างแล้ว — ลูกหนี้ย้ายไปที่ใบกำกับ ⇒ ใบเสร็จต้องออกจากใบกำกับ (กันใบเสร็จซ้อน §86/4)
        if (f.SupersedingTaxInvoiceNumber is string taxInv)
        {
            blocked.Add(TaxInvoiceSupersedesBlock(taxInv));
            return new Plan(steps, blocked, notes, null);
        }

        // จุดต่อ WHT ขั้นต่ำ: ตรวจทะเบียนเดิม ไม่สร้างแถวเอง (ตัวสร้างเดียว = เส้นบันทึกรับชำระ ยึด GL 11910)
        if (f.PaymentWht > 0.005m && !f.WhtCreditRegistered) notes.Add(WhtCreditMissingNote);

        if (!(f.FullyPaid || s.IncludePartial))
            return new Plan(steps, blocked, notes, notes.Count == 0 ? "ยังชำระไม่ครบ — ตั้งให้ส่งเฉพาะงวดที่ชำระครบ" : null);

        var autoOff = f.AutoSendBlockedReason;
        if (autoOff == null && !OnBehalfEmailSender.IsPlausibleEmail(f.CustomerEmail))
            blocked.Add(f.EmailOptOutReason != null
                ? "ยังไม่ได้ส่งใบเสร็จ — " + f.EmailOptOutReason + " · แก้แล้วกด \"ลองใหม่\" หรือส่งอีเมลที่ใบเสร็จ"
                : "ลูกค้าไม่มีอีเมลที่ใช้ได้ — ยังไม่ได้ส่งใบเสร็จ · เติมอีเมลที่ผู้ติดต่อแล้วกด \"ลองใหม่\" หรือส่งอีเมลที่ใบเสร็จ");
        else if (f.AllocationCount > 0)
            blocked.Add("การชำระนี้กระจายไปหลายเอกสาร — ระบบไม่ออก/ส่งใบเสร็จอัตโนมัติ (ยอดต่อใบเป็นการจัดสรร) · ออกใบเสร็จที่เอกสารแต่ละใบแล้วส่งเอง");
        else if (f.HasReceipt)
        {
            if (f.ReceiptIsDraft)
                blocked.Add("ใบเสร็จของการชำระนี้ยังเป็นฉบับร่าง (ผู้บันทึกไม่มีสิทธิ์อนุมัติใบเสร็จ) — อนุมัติใบเสร็จก่อน แล้วกด \"ลองใหม่\" (§86/4 ห้ามส่งเลข DRAFT)");
            else if (f.ReceiptCarriesVat && f.BuyerBlockingFields is { Count: > 0 })
                blocked.Add("ใบเสร็จรับเงิน/ใบกำกับภาษี ขาดข้อมูลผู้ซื้อ (" + string.Join(", ", f.BuyerBlockingFields) + ") — ยังไม่ส่งอัตโนมัติ · แก้ข้อมูลผู้ติดต่อแล้วกด \"ลองใหม่\"");
            else steps.Add(Step.EmailReceipt);
        }
        else if (f.SourceServesAsReceipt)
            steps.Add(Step.EmailSourceAsReceipt);
        else if (f.MayCarryVatIfIssued && f.BuyerBlockingFields is { Count: > 0 })
            blocked.Add("ข้อมูลผู้ซื้อไม่ครบสำหรับใบกำกับภาษี (" + string.Join(", ", f.BuyerBlockingFields) + ") — ระบบไม่ออกใบเสร็จรับเงิน/ใบกำกับภาษีอัตโนมัติ · แก้ข้อมูลผู้ติดต่อแล้วกด \"ลองใหม่\" หรือ \"ออกใบเสร็จ\" ที่การชำระนี้");
        else { steps.Add(Step.IssueReceipt); steps.Add(Step.EmailReceipt); }

        // ฝ่ายค้าน WP-08 HIGH-2 (Option 1): ไม่ส่งอัตโนมัติ ⇒ ตัดเฉพาะขั้นส่งอีเมล แล้วหยุดพร้อมเหตุบนเอกสาร (ใบเสร็จยังออกตามค่าตั้ง — ไม่เงียบ ไม่หายทั้งงาน)
        if (autoOff != null && steps.RemoveAll(x => x is Step.EmailReceipt or Step.EmailSourceAsReceipt) > 0)
            blocked.Add(AutoSendBlockedPrefix + autoOff);

        return new Plan(steps, blocked, notes, null);
    }

    /// <summary>คำนำของเหตุ "ไม่ส่งอีเมลเพราะผู้ติดต่อปิดส่งอัตโนมัติ" (ตัวเดียว — เทสต์อ้างได้)</summary>
    public const string AutoSendBlockedPrefix = "ไม่ส่งอีเมลใบเสร็จ — ";

    /// <summary>ข้อความหยุดเมื่อใบแจ้งหนี้มีใบกำกับภาษีอ้างแล้ว (ตัวเดียว — เทสต์อ้างได้)</summary>
    private static string TaxInvoiceSupersedesBlock(string taxInvoiceNumber)
        => $"ใบแจ้งหนี้นี้ออกใบกำกับภาษี {taxInvoiceNumber} แล้ว — ออกใบเสร็จจากใบกำกับ {taxInvoiceNumber} (ระบบไม่ออกใบเสร็จซ้อนจากใบแจ้งหนี้ · §86/4)";

    /// <summary>คีย์กันซ้ำ — 1 แถวงานต่อการชำระ (unique ที่ฐาน) · webhook ซ้ำ/บันทึกซ้ำ/งานวนซ้ำ = แถวเดิม</summary>
    public static string IdempotencyKey(Guid paymentId) => $"postpay:{paymentId:N}";

    // สถานะงาน (เก็บเป็นข้อความ · ป้ายไทยจาก StatusLabel — หน้าเว็บไม่มีตารางป้ายเอง)
    public const string StatusPending = "Pending";
    public const string StatusDone = "Done";
    public const string StatusBlocked = "Blocked";
    public const string StatusFailed = "Failed";
    public const string StatusSkipped = "Skipped";

    /// <summary>ลองใหม่สูงสุดกี่ครั้งเมื่อส่งไม่สำเร็จ (ครบ = Failed + แจ้งเตือน)</summary>
    public const int MaxAttempts = 4;

    public static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Min(120, 5 * Math.Pow(3, Math.Max(0, attempts - 1))));

    public static string StatusLabel(string status) => status switch
    {
        StatusPending => "รอดำเนินการ",
        StatusDone => "ส่งเอกสารแล้ว",
        StatusBlocked => "หยุด — ต้องให้คนดำเนินการ",
        StatusFailed => "ส่งไม่สำเร็จ",
        StatusSkipped => "ไม่มีอะไรต้องทำ",
        _ => status,
    };
}

/// <summary>รอบ 250 (WP-02 §2.1 · คำตัดสิน §2 #4): ทางเข้าที่บันทึกการรับ/จ่ายชำระผ่าน <c>DocumentService.CreatePaymentAsync</c> — ผู้เรียกทุกตัวต้องระบุ
/// (คอมไพเลอร์บังคับ · ไม่มีค่าเริ่มต้น) ⇒ งานหลังรับชำระตัดสินจากทางเข้าที่ <see cref="PostPaymentAutomationPolicy.ForEnqueue"/> ตัวเดียว ·
/// เก็บเป็นชื่อ enum ในแถวงาน (<c>PostPaymentAutomationRun.Source</c>)</summary>
public enum PaymentEntrySource
{
    /// <summary>ผู้ใช้บันทึกที่หน้าเอกสาร (1:1)</summary>
    Manual,
    /// <summary>เงินเข้าผ่านลิงก์ชำระ/ผู้ให้บริการรับชำระ (DocumentPaymentHandler)</summary>
    Gateway,
    /// <summary>ลงบัญชีรอบโอนของช่องทางขาย (SettlementPostingService)</summary>
    Settlement,
    /// <summary>ศูนย์นำเข้าไฟล์รับชำระ (PaymentImportTarget) — ค่าตั้งต้นไม่ส่งใบเสร็จ (แถว Skipped · ฝ่ายค้าน WP-02 ข้อ 4)</summary>
    Import,
    /// <summary>หน้าร้านออนไลน์/การจองบริการ — มีเส้นออกใบเสร็จของตัวเอง</summary>
    Cms,
    /// <summary>โมดูลที่พัก — มีเส้นออกใบเสร็จของตัวเอง</summary>
    Lodging,
    /// <summary>ค่าบริการแพลตฟอร์มในบริษัทผู้ให้บริการ — มีเส้นออกใบเสร็จของตัวเอง</summary>
    PlatformBilling,
    /// <summary>การชำระหลายใบ (CreateMultiDocPaymentAsync) — ไม่ออกใบเสร็จอัตโนมัติ (SettlementReceiptPolicy)</summary>
    MultiDoc,
    /// <summary>ศูนย์นำเข้าไฟล์รับชำระ ที่ผู้นำเข้าเลือก "ส่งใบเสร็จถึงลูกค้า" ในตัวเลือกของชุด (<c>sendReceipts=true</c>) — เดินเต็ม</summary>
    ImportSendReceipt,
    /// <summary>ระบบภายนอกผ่าน integration (IntegrationService.ProcessPaymentAsync — ทางเข้าที่ 9 · AUDIT_249_COVERAGE M-3) — บันทึกการชำระเอง
    /// แล้วเรียก <c>IDocumentService.AfterPaymentRecordedAsync</c> (ขั้นหลัง commit ตัวเดียวกับ CreatePaymentAsync)</summary>
    Integration,
}

/// <summary>
/// **จุดต่อ "บล็อกเพิ่มในอีเมลใบเสร็จ" — helper เดียว** (รอบ 241 · ตามคำสั่งปรับขอบเขต: ทีมออกแบบเส้นรับ 50 ทวิ จะเสียบลิงก์อัปโหลด/ข้อความที่นี่รอบถัดไป ·
/// รอบนี้<b>ไม่มีผู้ให้บล็อก</b> ⇒ อีเมลใบเสร็จ = เทมเพลตเดิมทุกตัวอักษร) · ผู้ให้บล็อก = <c>IReceiptEmailSectionProvider</c> (DI) · ผู้ประกอบ = ที่นี่เท่านั้น
/// (ห้ามต่อสตริงลิงก์เองในเส้นส่ง) · ค่าทุกตัวถูกหนีก่อนเข้า HTML (กฎ #4 C)
/// </summary>
public static class ReceiptEmailSections
{
    /// <summary>บล็อกหนึ่ง — <paramref name="Url"/> ต้องเป็น https (อื่น ๆ ถูกตัดทิ้ง ไม่ใส่ลงอีเมล)</summary>
    public sealed record Section(string Heading, string Text, string? Url = null, string? UrlLabel = null);

    private static bool SafeUrl(string? u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == Uri.UriSchemeHttps;

    /// <summary>เนื้ออีเมลใบเสร็จเมื่อมีบล็อกเพิ่ม — null = ไม่มีบล็อก (ผู้เรียกส่ง body ว่าง ⇒ เทมเพลตเดิมของ <c>DocumentEmailService</c>) ·
    /// <paramref name="platformPlainText"/> = เส้นอีเมลแพลตฟอร์มในนามบริษัท (รับข้อความล้วน — ห่อเทมเพลตคงที่เอง) · ไม่งั้นคืน HTML (เทมเพลตเดิม + บล็อกที่หนีแล้ว)</summary>
    public static string? ComposeBody(bool platformPlainText, string defaultHtml, string leadText, IReadOnlyList<Section>? sections)
    {
        var list = (sections ?? Array.Empty<Section>()).Where(s => !string.IsNullOrWhiteSpace(s.Heading) || !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (list.Count == 0) return null;
        if (platformPlainText)
        {
            var sb = new System.Text.StringBuilder(leadText.Trim());
            foreach (var s in list)
            {
                sb.Append("\n\n").Append(s.Heading.Trim()).Append('\n').Append(s.Text.Trim());
                if (SafeUrl(s.Url)) sb.Append('\n').Append(s.Url!.Trim());
            }
            return sb.ToString();
        }
        var html = new System.Text.StringBuilder(defaultHtml);
        foreach (var s in list)
        {
            html.Append("<div style='margin-top:14px;padding:12px;border:1px solid #e2e8f0;border-radius:8px'><strong>")
                .Append(System.Net.WebUtility.HtmlEncode(s.Heading.Trim())).Append("</strong><br>")
                .Append(System.Net.WebUtility.HtmlEncode(s.Text.Trim()).Replace("\n", "<br>"));
            if (SafeUrl(s.Url))
            {
                var u = System.Net.WebUtility.HtmlEncode(s.Url!.Trim());
                html.Append("<br><a href='").Append(u).Append("'>")
                    .Append(System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(s.UrlLabel) ? s.Url!.Trim() : s.UrlLabel!.Trim())).Append("</a>");
            }
            html.Append("</div>");
        }
        return html.ToString();
    }
}
