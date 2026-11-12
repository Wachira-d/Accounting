using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **หน้าเอกสารสำหรับลูกค้า — ตัวตัดสินเดียวของ "ใบนี้ออกลิงก์ได้ไหม · เปิดดูได้ไหม · ทำอะไรได้บนหน้า"** (รอบ 267 แพ็ก P1 ·
/// คำตัดสิน 267-1-1..267-1-3 ใน erp-review/2026-09-29/DECISIONS.md · DOCUMENT_FLOW §2.12n)
///
/// <para>หนึ่งใบ = หนึ่ง token (คอลัมน์ <c>Document.PayLink*</c> เดิม — ไม่มีคอลัมน์ token ใหม่) · สิทธิ์ของ token = "เป็นผู้ติดต่อของใบนี้" ·
/// การกระทำที่ทำได้<b>คำนวณสดทุกคำขอ</b>จากชนิด/สถานะ/โหมด (ไม่เก็บ scope ใน token) · หน้าเว็บไม่มีตารางชนิด/ปุ่ม/ป้ายของตัวเอง —
/// ทุกปุ่มมาพร้อมรหัส + ข้อความ + เหตุที่ทำไม่ได้จากที่นี่ (F2 ข้อ 5)</para>
///
/// <para>ผู้อ่าน: <c>DocumentPayLinkService</c> (<c>EnsureCustomerLinkAsync</c> · <c>PublicViewAsync</c> · <c>PageForDocumentAsync</c>) ·
/// <c>PublicDocumentPageController</c> · <c>PublicQuotationController</c> (หน้ายอมรับ/เซ็นรับเดิมรับ token ของหน้าเอกสารในช่วงเปลี่ยนผ่าน) ·
/// <c>SignRequestKinds</c> (ป้ายสถานะลิงก์เซ็น)</para>
///
/// <para>P1 = อ่านอย่างเดียว + ทางเดิม: ยอมรับใบเสนอราคา/เซ็นรับสินค้า ยังทำที่หน้าเดิมของรอบ 239/240 (ปุ่มบนหน้าเอกสารพาไป) ·
/// ผู้ลงนาม/หลักฐาน/ฉบับเซ็น = P2 · ไม่มี AI ในเส้นนี้ (กฎเหล็ก #1 ไม่ถูกแตะ)</para>
/// </summary>
public static class CustomerDocPage
{
    public const string RuleCode = "DOCPAGE-267";

    /// <summary>อายุลิงก์หน้าเอกสาร (วัน) — เท่าลิงก์ดู PDF ของการ์ด LINE (<see cref="LineDocumentViewLink.TtlDays"/>) ·
    /// การกระทำแต่ละอย่างมีหน้าต่างของตัวเอง (จ่าย = ขณะยอดค้าง &gt; 0 · ยอมรับ = ถึงวันยืนราคา · เซ็นรับ = 14 วัน)</summary>
    public const int LinkTtlDays = 400;

    /// <summary>ลิงก์ที่หมดอายุแล้วยัง "ขอลิงก์ใหม่" ได้อีกกี่วัน (หลังจากนั้น = ข้อความเดียว ไม่มีปุ่ม)</summary>
    public const int ExpiredRequestWindowDays = 180;

    /// <summary>ใบเสนอราคาที่ไม่มีวันยืนราคา — ยอมรับได้กี่วันนับจากวันออกลิงก์ (ค่าเดิมของลิงก์ยอมรับ รอบ 239)</summary>
    public static int QuotationDefaultWindowDays => SignRequestKinds.QuotationAccept.TtlDays;

    /// <summary>ใบส่งของ — เซ็นรับได้กี่วันนับจากวันออกลิงก์ (ค่าเดิม <c>SignRequestKinds.DeliverySign</c>)</summary>
    public static int DeliverySignWindowDays => SignRequestKinds.DeliverySign.TtlDays;

    /// <summary>ข้อความล้มเดียวของทุกเหตุ (ไม่พบ · เพิกถอน · หมดอายุ · ใบกลับเป็นร่าง) — ไม่บอกว่ามีใบ/บริษัท (กันเดา token)</summary>
    public const string NotFoundMessage = "ลิงก์ไม่ถูกต้องหรือหมดอายุ — กรุณาติดต่อผู้ขายเพื่อขอลิงก์ใหม่";

    public const string RateMessage = "เปิดถี่เกินไป — กรุณารอสักครู่แล้วลองใหม่";

    /// <summary>เพดานของเส้นสาธารณะ (ตัวเลขเดียว · นับใน <c>IChatRateLimiter</c> — ฐานข้อมูล ใช้ร่วมหลายเครื่อง)</summary>
    public static class Limits
    {
        public const int ViewPerMinute = 30;
        public const int ViewPerDay = 300;
        public const int PdfPerMinute = 10;
        public const int PdfPerDay = 120;
    }

    /// <summary>กุญแจตัวนับของเส้นสาธารณะ — ต่อ (token + IP) ไม่ใช่ IP อย่างเดียว (ลูกค้าหลัง CGNAT มือถือ/สำนักงานเดียวกันไม่โดนรวมกัน — หน้านี้คือหน้าชำระ) ·
    /// กุญแจ = hash สั้นของ (token + IP) — ไม่มี token/IP ตรงในตารางตัวนับ (แบบเดียวกับ <see cref="LineDocumentViewLink.RateKey"/>) ·
    /// รูป token ผิด = ถังเดียวต่อ IP (ไม่สร้างแถวตัวนับจากขยะทีละค่า)</summary>
    public static string RateKey(string bucket, string? token, string? ip)
    {
        var who = (DocumentPayLinkPolicy.LooksValid(token) ? token! : "invalid") + "|" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip);
        return $"docpage:{bucket}:{QuotationAcceptance.Sha256Hex(who)![..32]}";
    }

    /// <summary>โหมดของหน้า — หน้าเดียว ข้อมูลรูปเดียว ต่างกันที่ "ใครเปิด" (ชื่อที่ส่งออก = <see cref="ModeCode"/>)</summary>
    public enum Mode
    {
        /// <summary>ผู้ถือ token ของใบ (ลูกค้า/ผู้ขายที่ได้ลิงก์)</summary>
        Link = 0,
        /// <summary>ลูกค้าที่ล็อกอินพอร์ทัล (JWT พอร์ทัล + ด่านลูกค้าเห็นได้ + ธงสิทธิ์)</summary>
        Portal = 1,
        /// <summary>ผู้รับการ์ด LINE (HMAC ของแถวคิว) — ดู/PDF เท่านั้น</summary>
        LineView = 2,
        /// <summary>พนักงานดูตัวอย่างหน้าลูกค้า — ปุ่มทั้งหมดปิด · ไม่ประทับ "เปิดแล้ว"</summary>
        Preview = 3,
    }

    public static string ModeCode(Mode m) => m switch
    {
        Mode.Link => "link",
        Mode.Portal => "portal",
        Mode.LineView => "line-view",
        _ => "preview",
    };

    /// <summary>ชนิดที่มีหน้าเอกสารได้ (ออกเลขแล้วเท่านั้น) — ฝั่งขายที่ลูกค้าเห็นได้ + ใบสั่งซื้อถึงผู้ขาย ·
    /// ตัวตัดสินจริงต่อใบ = <see cref="IssueRefusal"/> (ฝั่งของใบลดหนี้/เพิ่มหนี้มาจาก <c>DocumentPayLinkPolicy.IsCustomerFacing</c>)</summary>
    public static readonly DocumentType[] PageTypes =
    {
        DocumentType.Quotation, DocumentType.DeliveryNote, DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote,
        DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.CreditNote, DocumentType.BillingNote, DocumentType.PurchaseOrder,
    };

    /// <summary>ชนิดนี้ + ฝั่งนี้ มีหน้าเอกสารไหม — ฝั่งขาย = ต้อง "ลูกค้าเห็นได้" (ไม่รู้ฝั่ง = ไม่ใช่) · ใบสั่งซื้อ = ถึงผู้ขายเสมอ</summary>
    private static bool TypeAllowed(DocumentType type, bool customerFacing)
        => type == DocumentType.PurchaseOrder || (customerFacing && Array.IndexOf(PageTypes, type) >= 0);

    /// <summary>หน้านี้แสดงราคา/ยอดไหม — ใบส่งของ = ไม่ (รายการ + จำนวนเท่านั้น · เท่าหน้าเซ็นรับเดิม) ·
    /// กลุ่ม LINE ที่ตั้งซ่อนยอดไม่ได้ลิงก์หน้าเอกสารเลย (<see cref="PageLinkAllowedForLineEndpoint"/>)</summary>
    public static bool ShowsAmounts(DocumentType type) => type != DocumentType.DeliveryNote;

    /// <summary>ปลายทาง LINE นี้รับลิงก์หน้าเอกสารได้ไหม — หน้าเอกสารแสดงยอดเสมอ ⇒ กลุ่มที่ซ่อนยอด/ยกเลิกผูก/บล็อก = ไม่ได้
    /// (ตัวตัดสินเดิมของลิงก์ดู PDF ตัวเดียว <see cref="LineDocumentViewLink.EndpointAllows"/> — ไม่มีสำเนากติกา)</summary>
    public static bool PageLinkAllowedForLineEndpoint(string? kind, bool showAmounts, DateTime? unlinkedAt, DateTime? blockedAt)
        => LineDocumentViewLink.EndpointAllows(kind, showAmounts, unlinkedAt, blockedAt);

    private static bool NotYetIssued(DocumentStatus s) => s is DocumentStatus.Draft or DocumentStatus.WaitingApproval or DocumentStatus.Rejected;

    /// <summary>เหตุที่ออกลิงก์หน้าเอกสารให้ใบนี้ไม่ได้ (null = ได้) — ใบค่าบริการ NextAcc ของผู้เช่า (คำตัดสิน 262-1-9) · ชนิด/ฝั่งที่ลูกค้าไม่เห็น ·
    /// ร่าง/รออนุมัติ/ถูกปฏิเสธ (ยังไม่มีเลขจริง — §86/4) · ยกเลิกแล้ว (ลิงก์ที่ส่งไปก่อนยกเลิกยังเปิดดูสถานะได้ — <see cref="ViewRefusal"/>)</summary>
    public static string? IssueRefusal(DocumentType type, DocumentStatus status, bool customerFacing, bool platformTenantDocument)
    {
        if (platformTenantDocument) return DocumentPayLinkPolicy.PlatformTenantRefusal;
        if (!TypeAllowed(type, customerFacing))
            return "ลิงก์หน้าเอกสารใช้ได้กับเอกสารขายที่ลูกค้าเห็นได้ (ใบเสนอราคา · ใบส่งของ · ใบแจ้งหนี้ · ใบกำกับภาษี · ใบเสร็จ · ใบลดหนี้/เพิ่มหนี้ฝั่งขาย · ใบวางบิล) "
                 + "และใบสั่งซื้อถึงผู้ขายเท่านั้น";
        if (NotYetIssued(status))
            return "ต้องอนุมัติเอกสารก่อน (ฉบับร่าง/รออนุมัติยังไม่มีเลขเอกสารจริง)";
        if (status == DocumentStatus.Voided)
            return "เอกสารถูกยกเลิกแล้ว — ออกลิงก์ใหม่ไม่ได้ (ลิงก์ที่ส่งไปแล้วยังเปิดดูสถานะยกเลิกได้)";
        return null;
    }

    /// <summary>ผู้ถือลิงก์เปิดใบนี้ได้ไหม (ตัดสินสดทุกครั้ง) — ต่างจาก <see cref="IssueRefusal"/> สองข้อ: ใบที่<b>ยกเลิกหลังส่งลิงก์</b>ยังเปิดได้
    /// (แสดง "ยกเลิกแล้ว" ไม่มีปุ่ม) และใบค่าบริการของผู้เช่าที่มีลิงก์อยู่ก่อนยังดูได้ (ปุ่มจ่ายถูกปิดที่ชั้นจ่าย) ·
    /// ใบกลับเป็นร่าง/ชนิดที่ลูกค้าไม่เห็น = เปิดไม่ได้ (true = ปฏิเสธ → ข้อความเดียว)</summary>
    public static bool ViewRefused(DocumentType type, DocumentStatus status, bool customerFacing)
        => !TypeAllowed(type, customerFacing) || NotYetIssued(status);

    /// <summary>สถานะลิงก์ของใบ — ชื่อที่ส่งออก = ชื่อ enum</summary>
    public enum LinkState { None = 0, Usable = 1, Expired = 2, Revoked = 3 }

    /// <summary>ลิงก์ (ระดับ "เปิดหน้า") — มี hash · ไม่ถูกเพิกถอน · ยังไม่หมดอายุ · <b>ไม่</b>ขึ้นกับว่าจ่ายได้ไหม
    /// (ใบแจ้งหนี้ที่ออกใบกำกับแล้ว/ชำระครบ ยังเปิดดูได้ — ปุ่มจ่ายปิดที่ <c>DocumentPayLinkPolicy.IssueRefusal</c>)</summary>
    public static LinkState StateOf(string? tokenHash, DateTime? expiresAt, DateTime? revokedAt, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) return LinkState.None;
        if (revokedAt != null) return LinkState.Revoked;   // เพิกถอนชนะหมดอายุ — ผู้ขายตั้งใจปิด ⇒ ไม่มีปุ่มขอใหม่
        if (expiresAt != null && expiresAt <= nowUtc) return LinkState.Expired;
        return LinkState.Usable;
    }

    /// <summary>ลิงก์นี้ "ขอลิงก์ใหม่" ได้ไหม — เฉพาะลิงก์ที่<b>หมดอายุ</b>ไม่เกิน <see cref="ExpiredRequestWindowDays"/> วัน ·
    /// เพิกถอน = ไม่ได้ (ผู้ขายตั้งใจปิด) · ใช้ได้อยู่ = ไม่จำเป็น · ระบบไม่ออก token ให้คนนอก — แค่แจ้งพนักงาน</summary>
    public static bool CanRequestNewLink(LinkState state, DateTime? expiresAt, DateTime nowUtc)
        => state == LinkState.Expired && expiresAt != null && nowUtc <= expiresAt.Value.AddDays(ExpiredRequestWindowDays);

    /// <summary>วันหมดอายุของลิงก์ที่ออกใหม่</summary>
    public static DateTime NewLinkExpiry(DateTime nowUtc) => nowUtc.AddDays(LinkTtlDays);

    /// <summary>สิ้นสุดหน้าต่าง "ตอบรับ" (เวลา UTC · ไม่รวมขอบ) — ใบเสนอราคา: สิ้นวันยืนราคา (เวลาไทย) · ไม่มีวันที่ = 30 วันจากออกลิงก์ ·
    /// ใบส่งของ: 14 วันจากออกลิงก์ · ชนิดอื่น/ไม่รู้วันออกลิงก์ = null (ไม่มีหน้าต่าง)</summary>
    /// <param name="dueDate">วันยืนราคาของใบเสนอราคา (เก็บเป็นวันปฏิทินไทย ณ 00:00 UTC — <c>ThaiDate.CalendarDateUtc</c>)</param>
    /// <param name="linkStartUtc">วันที่ผู้ขายส่งลิงก์ให้ตอบรับ (<c>SignLinkIssuedAt</c> · ไม่มี = วันออกลิงก์หน้าเอกสาร)</param>
    public static DateTime? RespondWindowEnd(DocumentType type, DateTime? dueDate, DateTime? linkStartUtc)
    {
        if (type == DocumentType.Quotation)
        {
            if (dueDate is DateTime due)
                return DateTime.SpecifyKind(due.Date.AddDays(1).AddHours(-7), DateTimeKind.Utc);   // เที่ยงคืนเวลาไทยของวันถัดจากวันยืนราคา
            return linkStartUtc?.AddDays(QuotationDefaultWindowDays);
        }
        if (type == DocumentType.DeliveryNote) return linkStartUtc?.AddDays(DeliverySignWindowDays);
        return null;
    }

    /// <summary>เหตุที่ "ตอบรับ" (ยอมรับใบเสนอราคา/เซ็นรับสินค้า) ไม่ได้ ณ ตอนนี้เพราะหน้าต่างเวลา — null = ยังเปิด ·
    /// ตัวตัดสินเดียวของปุ่มบนหน้าเอกสาร (<see cref="Actions"/>) และของหน้าตอบรับเดิมเมื่อเปิดด้วย token ของหน้าเอกสาร
    /// (<c>PublicQuotationController</c>) — ลิงก์ยอมรับ/เซ็นรับที่ออกก่อนรอบ 267 ใช้วันหมดอายุของตัวเองตามเดิม</summary>
    public static string? RespondWindowClosedReason(bool en, DocumentType type, DateTime? dueDate, DateTime? linkStartUtc, DateTime nowUtc)
    {
        var end = RespondWindowEnd(type, dueDate, linkStartUtc);
        if (end == null)
            return T(en, "ผู้ขายยังไม่ได้ส่งลิงก์ให้ตอบรับเอกสารนี้ — กรุณาติดต่อผู้ขาย", "The seller has not opened this document for a response — please contact the seller");
        if (nowUtc < end) return null;
        return type == DocumentType.Quotation && dueDate != null
            ? T(en, $"ใบเสนอราคานี้ยืนราคาถึง {ThaiDate.ToThaiDisplayString(dueDate.Value)} — ขอให้ผู้ขายออกใบเสนอราคาใหม่",
                $"This quotation was valid until {dueDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)} — please ask the seller for a new quotation")
            : T(en, "พ้นกำหนดตอบรับเอกสารนี้แล้ว — ขอให้ผู้ขายส่งลิงก์ใหม่", "The response period has ended — please ask the seller for a new link");
    }

    /// <summary>ข้อเท็จจริงของใบ ณ ตอนถาม (ตัวโหลดเดียว <c>DocumentPayLinkService</c>) —
    /// <paramref name="PayOffered"/> = ชั้นจ่ายเปิด (<c>DocumentPayLinkPolicy.IssueRefusal == null</c> + มีช่องทางออนไลน์ + ยังไม่รับเงินผ่านลิงก์) ·
    /// <paramref name="PayBlockedNote"/> = เหตุที่จ่ายไม่ได้ (ข้อความเดิมของหน้าชำระ) · <paramref name="SlipOffered"/> = ฟอร์มแจ้งโอนเปิด
    /// (<c>SlipEvidenceRule.PayLinkSlipAllowed</c>) · <paramref name="RespondHref"/> = ที่อยู่หน้าตอบรับเดิม (null = ไม่มีทางตอบรับในโหมดนี้)</summary>
    public sealed record Facts(
        DocumentType Type, DocumentStatus Status, Mode Mode, bool IsEnglish,
        bool PayOffered, string? PayBlockedNote, bool SlipOffered,
        DateTime? DueDate, DateTime? RespondStartUtc, DateTime? RespondedAt, string? RespondHref,
        bool PdfAvailable = true);

    /// <summary>ปุ่มหนึ่งปุ่มบนหน้า — <paramref name="Id"/> = รหัสที่หน้าเว็บใช้เลือกตัววาด (ไม่ใช่ข้อความ) · <paramref name="Href"/> = ที่ไปต่อ
    /// (หน้าตอบรับเดิมในช่วงเปลี่ยนผ่าน) · ปิด = <paramref name="DisabledReason"/> ต้องมีเสมอ (ไม่มีปุ่มเทาที่ไม่บอกเหตุ)</summary>
    public sealed record PageAction(string Id, bool Enabled, string Label, string? DisabledReason, string RuleCode, string? Href = null);

    public const string ActionDownloadPdf = "download-pdf";
    public const string ActionPayOnline = "pay-online";
    public const string ActionSubmitSlip = "submit-slip";
    public const string ActionAccept = "accept";
    public const string ActionSignDelivery = "sign-delivery";
    public const string ActionRequestLink = "request-link";

    /// <summary>ชนิดที่ "จ่ายได้" บนหน้า (ตัวตัดสินจริง = ชั้นจ่าย <c>DocumentPayLinkPolicy.IssueRefusal</c> — ที่นี่ใช้แค่เลือกว่าจะแสดงปุ่มจ่ายไหม)</summary>
    public static bool IsPayableType(DocumentType type) => Array.IndexOf(DocumentPayLinkPolicy.PayableTypes, type) >= 0;

    private static string T(bool en, string th, string enText) => en ? enText : th;

    /// <summary>
    /// **ปุ่มทั้งหมดของหน้า ณ ตอนนี้** — ตัวตัดสินเดียว (ทุกโหมด ทุกชนิด) · ลำดับ = ลำดับที่หน้าแสดง ·
    /// ใบยกเลิก = ดาวน์โหลดได้อย่างเดียว (กระดาษพิมพ์ "ยกเลิก" ตาม renderer เดิม) · โหมดพรีวิว = ทุกปุ่มปิด ·
    /// โหมด LINE-ดู/พอร์ทัล = ดู + ดาวน์โหลด · การจ่าย/ตอบรับ ต้องใช้ลิงก์หน้าเอกสาร (ปุ่ม "ขอลิงก์ทำรายการ")
    /// </summary>
    public static IReadOnlyList<PageAction> Actions(Facts f, DateTime nowUtc)
    {
        var en = f.IsEnglish;
        var list = new List<PageAction>();
        var preview = f.Mode == Mode.Preview;
        var previewNote = T(en, "โหมดดูตัวอย่างของพนักงาน — ปุ่มใช้งานไม่ได้", "Staff preview — buttons are disabled");
        var voided = f.Status == DocumentStatus.Voided;

        list.Add(!f.PdfAvailable
            ? new PageAction(ActionDownloadPdf, false, T(en, "ดาวน์โหลด PDF", "Download PDF"),
                T(en, "ยังสร้างไฟล์ PDF ไม่ได้ในขณะนี้ — กรุณาติดต่อผู้ขาย", "The PDF is not available right now — please contact the seller"), RuleCode)
            : preview
                ? new PageAction(ActionDownloadPdf, false, T(en, "ดาวน์โหลด PDF", "Download PDF"), previewNote, RuleCode)
                : new PageAction(ActionDownloadPdf, true, T(en, "ดาวน์โหลด PDF", "Download PDF"), null, RuleCode));
        if (voided) return list;

        var needsLinkNote = T(en, "ทำรายการนี้ได้จากลิงก์หน้าเอกสารที่ผู้ขายส่งให้ — กด \"ขอลิงก์ทำรายการ\" เพื่อแจ้งผู้ขาย",
            "This needs the document link from the seller — tap \"Request a link\" to notify the seller");
        var wantsLink = false;

        if (IsPayableType(f.Type))
        {
            var payLabel = T(en, "ชำระเงินออนไลน์", "Pay online");
            var slipLabel = T(en, "แจ้งโอน + แนบสลิป", "Report a transfer + attach slip");
            if (preview)
            {
                list.Add(new PageAction(ActionPayOnline, false, payLabel, previewNote, DocumentPayLinkPolicy.RuleCode));
                list.Add(new PageAction(ActionSubmitSlip, false, slipLabel, previewNote, DocumentPayLinkPolicy.RuleCode));
            }
            else if (f.Mode != Mode.Link)
            {
                // ชำระครบ/จ่ายไม่ได้อยู่แล้ว ⇒ ไม่ชวนขอลิงก์ (ไม่มีอะไรให้ทำ)
                var open = f.PayOffered || f.SlipOffered;
                list.Add(new PageAction(ActionPayOnline, false, payLabel, open ? needsLinkNote : f.PayBlockedNote ?? needsLinkNote, DocumentPayLinkPolicy.RuleCode));
                wantsLink |= open;
            }
            else
            {
                list.Add(new PageAction(ActionPayOnline, f.PayOffered, payLabel,
                    f.PayOffered ? null : f.PayBlockedNote ?? T(en, "เอกสารนี้ชำระออนไลน์ไม่ได้ตอนนี้ — กรุณาติดต่อผู้ขาย", "Online payment is not available — please contact the seller"),
                    DocumentPayLinkPolicy.RuleCode));
                list.Add(new PageAction(ActionSubmitSlip, f.SlipOffered, slipLabel,
                    f.SlipOffered ? null : T(en, "ยังแจ้งโอนจากหน้านี้ไม่ได้ (ไม่มีบัญชีรับโอน หรือไม่มียอดค้าง)", "Transfer reporting is not available (no receiving account or nothing due)"),
                    DocumentPayLinkPolicy.RuleCode));
            }
        }

        var respondId = f.Type == DocumentType.Quotation ? ActionAccept : f.Type == DocumentType.DeliveryNote ? ActionSignDelivery : null;
        if (respondId != null)
        {
            var label = respondId == ActionAccept ? T(en, "ยอมรับใบเสนอราคา", "Accept the quotation") : T(en, "เซ็นรับสินค้า", "Sign for the delivery");
            string? closed = null;
            if (f.RespondedAt != null)
                closed = respondId == ActionAccept ? T(en, "ยอมรับใบเสนอราคานี้แล้ว", "This quotation has been accepted")
                    : T(en, "เซ็นรับสินค้าแล้ว", "This delivery has been signed for");
            else if (preview) closed = previewNote;
            else if (RespondWindowClosedReason(en, f.Type, f.DueDate, f.RespondStartUtc, nowUtc) is { } windowClosed) closed = windowClosed;
            else if (f.Mode != Mode.Link || string.IsNullOrEmpty(f.RespondHref)) closed = needsLinkNote;

            list.Add(new PageAction(respondId, closed == null, label, closed, RuleCode, closed == null ? f.RespondHref : null));
            // ปิดเพราะพ้นกำหนด/ไม่มีลิงก์ทำรายการ (ไม่ใช่เพราะตอบรับแล้ว/พรีวิว) ⇒ มีทางไปต่อ: แจ้งผู้ขาย
            wantsLink |= closed != null && f.RespondedAt == null && !preview;
        }

        if (wantsLink)
            list.Add(new PageAction(ActionRequestLink, true, T(en, "ขอลิงก์ทำรายการจากผู้ขาย", "Request a link from the seller"), null, RuleCode));
        return list;
    }

    /// <summary>ข้อความ/ป้ายของหน้า (ไทย/อังกฤษตามภาษาเอกสาร — resolver ภาษาเดียวกับกระดาษ) · หน้าเว็บไม่มีข้อความของตัวเอง</summary>
    public static IReadOnlyDictionary<string, string> Labels(bool en, DocumentType type) => new Dictionary<string, string>
    {
        ["to"] = T(en, "ถึง", "To"),
        ["date"] = T(en, "ลงวันที่", "Date"),
        ["due"] = DueLabel(en, type),
        ["item"] = T(en, "รายการ", "Description"),
        ["qty"] = T(en, "จำนวน", "Qty"),
        ["unitPrice"] = T(en, "ราคา/หน่วย", "Unit price"),
        ["amount"] = T(en, "จำนวนเงิน", "Amount"),
        ["subTotal"] = T(en, "รวมเป็นเงิน", "Subtotal"),
        ["discount"] = T(en, "ส่วนลด", "Discount"),
        ["vat"] = T(en, "ภาษีมูลค่าเพิ่ม", "VAT"),
        ["wht"] = T(en, "หัก ณ ที่จ่าย", "Withholding tax"),
        ["total"] = T(en, "จำนวนเงินรวมทั้งสิ้น", "Grand total"),
        ["paid"] = T(en, "ชำระแล้ว", "Paid"),
        ["balance"] = T(en, "ยอดค้างชำระ", "Balance due"),
        ["related"] = T(en, "เอกสารที่เกี่ยวข้อง", "Related documents"),
        ["voided"] = T(en, "เอกสารนี้ถูกยกเลิกแล้ว", "This document has been cancelled"),
        ["replacement"] = T(en, "เอกสารที่ออกแทน", "Replaced by"),
        ["pdfNote"] = T(en, "ฉบับที่มีผลตามกฎหมายคือไฟล์ PDF ของเอกสาร", "The PDF file is the legally effective copy"),
        ["preview"] = T(en, "ตัวอย่างหน้าที่ลูกค้าจะเห็น (พนักงานเท่านั้น)", "Preview of the customer page (staff only)"),
        ["requestSent"] = T(en, "แจ้งผู้ขายแล้ว — ผู้ขายจะส่งลิงก์ใหม่ให้ท่าน", "The seller has been notified and will send you a new link"),
        ["requestAlready"] = T(en, "วันนี้แจ้งผู้ขายไปแล้ว — กรุณารอผู้ขายติดต่อกลับ", "The seller was already notified today — please wait for their reply"),
        ["currency"] = T(en, "บาท", "THB"),
    };

    /// <summary>ป้ายวันที่ตัวที่สองตามชนิด — ข้อความเดียวกับกระดาษ (<c>DocumentLabels.DueDateFor</c>: ใบเสนอราคา = ยืนราคาถึง · ใบสั่งซื้อ = กำหนดส่งมอบ)</summary>
    private static string DueLabel(bool en, DocumentType type) => type switch
    {
        DocumentType.Quotation => T(en, "ยืนราคาถึง", "Valid until"),
        DocumentType.PurchaseOrder => T(en, "กำหนดส่งมอบ", "Delivery date"),
        _ => T(en, "ครบกำหนด", "Due date"),
    };

    /// <summary>ป้ายสถานะของใบบนหน้า (ชื่อ enum → ข้อความ · หน้าเว็บไม่แปลเอง)</summary>
    public static string StatusLabel(bool en, DocumentType type, DocumentStatus status, decimal balanceDue)
    {
        if (status == DocumentStatus.Voided) return T(en, "ยกเลิกแล้ว", "Cancelled");
        if (IsPayableType(type))
        {
            if (status == DocumentStatus.Paid || balanceDue <= 0.005m) return T(en, "ชำระครบแล้ว", "Paid in full");
            if (status == DocumentStatus.PartiallyPaid) return T(en, "ชำระบางส่วน", "Partially paid");
            return T(en, "รอชำระ", "Awaiting payment");
        }
        return T(en, "ออกเอกสารแล้ว", "Issued");
    }

    // ═══════════════ token ของลิงก์ยอมรับ/เซ็นรับเดิม (L1/L2) — ฐานไม่เก็บค่าดิบ (คำตัดสิน 267-1-3) ═══════════════

    /// <summary>คำนำหน้าของค่าที่เก็บในคอลัมน์ <c>QuotationAcceptToken</c> / <c>DeliverySignToken</c> ตั้งแต่รอบ 267 = hash (ไม่ใช่ token)</summary>
    public const string LegacyHashPrefix = "h1:";

    /// <summary>ค่าที่เก็บ/ค้นในคอลัมน์ token เดิม — <c>h1:</c> + SHA-256 hex ของ token ตัวพิมพ์เล็ก (สูตรเดียวกับ
    /// <c>QuotationAcceptTokenHash</c> ที่เป็นหลักฐาน · ฝั่ง migration ใน PostgreSQL ใช้สูตรนี้ตัวเดียว — <see cref="LegacyTokenHashSql"/>)</summary>
    public static string LegacyStored(string token) => LegacyHashPrefix + QuotationAcceptance.Sha256Hex(token.ToLowerInvariant());

    /// <summary>migration รอบ 267 (267-1-3): token ดิบของลิงก์ยอมรับ/เซ็นรับที่ออกก่อนรอบนี้ ⇒ แทนด้วย hash ในที่เดิม (ลิงก์ที่ลูกค้าถืออยู่ยังเปิดได้
    /// จนหมดอายุ — ฝั่งค้นใช้ <see cref="LegacyStored"/>) · รันซ้ำได้ (ข้ามแถวที่ขึ้นต้น <c>h1:</c>) · สูตรต้องตรงกับ <see cref="LegacyStored"/>
    /// (ล็อกด้วย <c>CustomerDocPageRound267DbTests</c> — เขียนใน PostgreSQL แล้วค้นด้วย C#)</summary>
    public static readonly string[] LegacyTokenHashSql =
    {
        """UPDATE "Documents" SET "QuotationAcceptToken" = 'h1:' || encode(sha256(convert_to(lower("QuotationAcceptToken"), 'UTF8')), 'hex') WHERE "QuotationAcceptToken" IS NOT NULL AND "QuotationAcceptToken" NOT LIKE 'h1:%';""",
        """UPDATE "Documents" SET "DeliverySignToken" = 'h1:' || encode(sha256(convert_to(lower("DeliverySignToken"), 'UTF8')), 'hex') WHERE "DeliverySignToken" IS NOT NULL AND "DeliverySignToken" NOT LIKE 'h1:%';""",
    };

    /// <summary>ที่อยู่หน้าตอบรับเดิมของชนิดนี้ (ช่วงเปลี่ยนผ่าน P1→P2) — token ของหน้าเอกสารตัวเดียวกัน · ชนิดอื่น = null</summary>
    public static string? LegacyRespondPath(DocumentType type, string token) => SignRequestKinds.For(type) is { } k
        ? $"{k.PagePath}?token={Uri.EscapeDataString(token)}" : null;

    // ═══════════════ log — ไม่เขียน token ลง log (token อยู่ใน path ของเส้น API สาธารณะ) ═══════════════

    private static readonly string[] TokenPathMarkers = { "/public-pay/documents/", "/api/public/quotation/", "/api/public/delivery/" };

    /// <summary>ปิดบัง token ใน path ก่อนเขียน log (<c>RequestLoggingMiddleware</c> · <c>ApiErrorLoggingMiddleware</c> · <c>ExceptionMiddleware</c>
    /// ผ่าน <c>GatewayWebhookRoute.RedactPath</c>) — segment ถัดจากเส้นหน้าเอกสาร/หน้าตอบรับเดิม ⇒ <c>[redacted]</c> · path อื่นคืนเดิม</summary>
    public static string RedactPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        foreach (var marker in TokenPathMarkers)
        {
            var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var start = i + marker.Length;
            if (start >= path.Length) return path;
            var end = path.IndexOf('/', start);
            return path[..start] + "[redacted]" + (end < 0 ? "" : path[end..]);
        }
        return path;
    }
}
