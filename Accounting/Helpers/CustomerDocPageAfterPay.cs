using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **หน้าเอกสารสำหรับลูกค้า — ส่วน "หลังจ่าย" (รอบ 267 แพ็ก P4 · คำตัดสิน 267-1-45…267-1-50 · DOCUMENT_FLOW §2.12n-P4)** — pure ·
/// ตัวตัดสิน/ข้อความเดียวของ: สถานะสลิปที่ลูกค้าเห็น · ข้อความเมื่อยังไม่มีใบเสร็จ · ป้าย e-Tax ของใบเสร็จ · ปุ่ม "ชำระใบนี้" ของใบที่เกี่ยวข้อง ·
/// การ์ดอัปโหลด 50 ทวิ · หน้าเว็บ (<c>pay-document.html</c>) แสดงอย่างเดียว — ไม่มีตารางสถานะ/ข้อความของตัวเอง
///
/// <para>หลักคิด (F2 ข้อ 3): หน้านี้<b>แสดงผล</b>อย่างเดียว — ไม่ออกใบเสร็จ ไม่ตัดสินสลิป ไม่ประทับ "ชำระแล้ว" ·
/// ใบเสร็จมาจากงานหลังรับชำระ/พนักงานเท่านั้น · สลิป = รอผู้ขายยืนยัน (กติกาเดิมรอบ 255)</para>
/// <para>ผู้เรียก: <c>DocumentPayLinkService.AfterPayAsync</c> / <c>RelatedAsync</c> · เทสต์ <c>PostPayRound267Tests</c></para>
/// </summary>
public static class CustomerDocPageAfterPay
{
    public const string RuleCode = "DOCPAGE-267-P4";

    /// <summary>จำนวนสลิปล่าสุดที่หน้าแสดง (ใบเดียวแจ้งโอนได้หลายงวด — แสดงชุดล่าสุด)</summary>
    public const int MaxSlips = 5;

    private static string T(bool en, string th, string enText) => en ? enText : th;

    // ═══════════════ สถานะสลิปที่ลูกค้าเห็น ═══════════════

    public const string SlipPending = "pending", SlipConfirmed = "confirmed", SlipRejected = "rejected", SlipClosed = "closed";

    /// <summary>ข้อความกลางเมื่อพนักงานปฏิเสธสลิปโดยไม่พิมพ์เหตุถึงลูกค้า (เหตุผลภายในของพนักงานไม่ส่งออกหน้าเอกสาร)</summary>
    private static string DefaultRejectNote(bool en) => T(en,
        "ผู้ขายไม่พบยอดโอนตามสลิปนี้ — กรุณาตรวจสลิปแล้วแนบใหม่ หรือติดต่อผู้ขาย",
        "The seller could not find a transfer matching this slip — please check and attach it again, or contact the seller");

    /// <summary>ความยาวสูงสุดของเหตุผลที่ลูกค้าเห็น (คอลัมน์ <c>PaymentIntent.CustomerVisibleRejectReason</c>)</summary>
    public const int RejectReasonMaxLength = 300;

    /// <summary>ทำความสะอาดเหตุผลที่พนักงานพิมพ์ถึงลูกค้า — ว่าง = null (ใช้ข้อความกลาง) · ตัดความยาว · ตัดอักขระควบคุม</summary>
    public static string? NormalizeRejectReason(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var clean = new string(typed.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > RejectReasonMaxLength ? clean[..RejectReasonMaxLength] : clean;
    }

    /// <summary>สถานะของสลิปหนึ่งใบบนหน้า — <paramref name="State"/> = รหัส (หน้าเว็บใช้เลือกสีเท่านั้น) · <paramref name="Note"/> = เหตุ/ทางไปต่อ</summary>
    public sealed record SlipView(string State, string Label, string? Note);

    /// <summary>
    /// สถานะสลิปที่ลูกค้าเห็น — รอตรวจ (ยังไม่ใช่ "ชำระแล้ว") · ยืนยันแล้ว · ปฏิเสธ (เหตุที่พนักงานพิมพ์ถึงลูกค้า หรือข้อความกลาง — แนบใหม่ได้) ·
    /// คืนเงิน/หมดอายุ = ปิดแล้ว (ไม่เดาเหตุ)
    /// </summary>
    public static SlipView SlipState(bool en, PaymentIntentStatus status, string? customerReason)
    {
        if (PaymentIntentPolicy.IsOpen(status))
            return new SlipView(SlipPending, T(en, "รอผู้ขายตรวจสลิป", "Waiting for the seller to check the slip"),
                T(en, "สถานะเอกสารจะเปลี่ยนเป็น \"ชำระแล้ว\" เมื่อผู้ขายยืนยันเท่านั้น — ไม่ต้องส่งสลิปซ้ำ",
                    "The document is marked paid only after the seller confirms — no need to send the slip again"));
        return status switch
        {
            PaymentIntentStatus.Succeeded => new SlipView(SlipConfirmed, T(en, "ผู้ขายยืนยันการรับเงินแล้ว", "The seller confirmed this payment"), null),
            PaymentIntentStatus.Failed => new SlipView(SlipRejected, T(en, "ผู้ขายไม่รับสลิปนี้", "The seller did not accept this slip"),
                NormalizeRejectReason(customerReason) ?? DefaultRejectNote(en)),
            _ => new SlipView(SlipClosed, T(en, "รายการนี้ปิดแล้ว", "This entry is closed"),
                T(en, "หากมีข้อสงสัย กรุณาติดต่อผู้ขาย", "Please contact the seller if you have questions")),
        };
    }

    // ═══════════════ ใบเสร็จ ═══════════════

    /// <summary>
    /// ข้อความเมื่อ "รับชำระแล้วแต่ยังไม่มีใบเสร็จบนหน้านี้" (S16 — ไม่แต่งใบเสร็จ · ไม่ออกใบเสร็จจากการเปิดหน้า) · null = ไม่ต้องบอก ·
    /// เฉพาะใบแจ้งหนี้ (ใบกำกับภาษี/ใบเพิ่มหนี้ที่รับเงินครบอาจทำหน้าที่ใบเสร็จในตัว — ไม่บอกว่า "ยังไม่มีใบเสร็จ" กับใบที่อาจเป็นใบเสร็จเอง)
    /// </summary>
    public static string? ReceiptPendingNote(bool en, DocumentType type, DocumentStatus status, decimal paidAmount, int receiptCount)
    {
        if (type != DocumentType.Invoice || receiptCount > 0 || paidAmount <= 0.005m) return null;
        if (!DocumentStatusRules.IsEffective(status)) return null;
        return T(en,
            "รับชำระแล้ว — ผู้ขายยังไม่ได้ออกใบเสร็จรับเงินของการชำระนี้ในระบบ · เมื่อผู้ขายออกแล้ว ดาวน์โหลดได้จากหน้านี้",
            "Payment received — the seller has not issued a receipt for it yet. Once issued, it can be downloaded from this page");
    }

    /// <summary>
    /// ป้าย e-Tax ของใบเสร็จ/ใบกำกับที่ออกจากการชำระ (S20) — มีเฉพาะเมื่อ<b>ส่งทางอีเมล e-Tax แล้วจริง</b> (<c>EtaxInvoice.ByEmailSentAt</c>) ·
    /// ยังไม่ส่ง = null (ไม่อ้างสิ่งที่ยังไม่เกิด) · ไม่อ้างว่าไฟล์บนหน้านี้คือไฟล์เดียวกับฉบับที่ประทับเวลา (ฉบับที่มีผล = ไฟล์ในอีเมล e-Tax)
    /// </summary>
    public static string? EtaxSentNote(bool en, DateTime? byEmailSentAtUtc)
    {
        if (byEmailSentAtUtc is not DateTime sent) return null;
        var th = sent.AddHours(7);
        return en
            ? $"The legally effective e-Tax copy was emailed on {th.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} — the file here is a reference copy"
            : $"ฉบับที่มีผลทางภาษี (e-Tax Invoice) ส่งทางอีเมลแล้วเมื่อ {th.Day:00}/{th.Month:00}/{th.Year + 543} — ไฟล์บนหน้านี้เป็นสำเนาสำหรับอ้างอิง";
    }

    public const string KindReceipt = "receipt";

    public static string ReceiptKindLabel(bool en) => T(en, "ใบเสร็จ/ใบกำกับภาษีจากการชำระของท่าน", "Receipt / tax invoice for your payment");

    // ═══════════════ ปุ่ม "ชำระใบนี้" ของใบที่เกี่ยวข้อง (ใบวางบิล → ใบแจ้งหนี้ในบิล · ใบเสนอราคา → ใบแจ้งหนี้ที่ออกต่อ) ═══════════════

    /// <summary>
    /// ใบที่เกี่ยวข้องใบนี้มีปุ่ม "ชำระใบนี้" ไหม (267-1-9 v1: จ่ายรายใบ — ไม่มีจ่ายรวมทั้งบิล) — เฉพาะโหมดลิงก์ของใบ · ชนิดที่รับชำระ ·
    /// ออกแล้วยังมีผล · ยังมียอดค้าง · ปุ่มพาไป<b>หน้าเอกสารของใบนั้น</b> (ด่านจ่ายทั้งหมดอยู่ที่หน้านั้น — หน้านี้ไม่รับเงินแทน)
    /// </summary>
    public static bool RelatedPayOffered(CustomerDocPage.Mode mode, DocumentType relatedType, DocumentStatus relatedStatus, decimal relatedBalanceDue)
        => mode == CustomerDocPage.Mode.Link
           && CustomerDocPage.IsPayableType(relatedType)
           && DocumentStatusRules.IsEffective(relatedStatus)
           && relatedBalanceDue > 0.005m;

    public static string RelatedPayLabel(bool en) => T(en, "ชำระใบนี้", "Pay this document");

    /// <summary>ข้อความเมื่อเปิดหน้าของใบที่เกี่ยวข้องไม่ได้ (ผู้ขายยกเลิกลิงก์ของใบนั้น ฯลฯ) — ไม่บอกเหตุภายใน</summary>
    public static string RelatedLinkRefusedMessage(bool en) => T(en,
        "เปิดหน้าชำระของเอกสารนี้ไม่ได้ในขณะนี้ — กรุณาติดต่อผู้ขาย",
        "This document's payment page is not available right now — please contact the seller");

    // ═══════════════ หนังสือรับรอง 50 ทวิ ═══════════════

    /// <summary>การ์ด 50 ทวิ บนหน้า — <paramref name="CanUpload"/> = แสดงฟอร์มส่งไฟล์ (ยังรอใบจากลูกค้า) · ส่งแล้วรอตรวจ = ไม่ชวนส่งซ้ำ</summary>
    public sealed record WhtCertView(string State, string Label, string Note, bool CanUpload, decimal Missing);

    public const string WhtAwaiting = "awaiting", WhtSubmitted = "submitted";

    /// <summary>
    /// การ์ด 50 ทวิ ของใบนี้ — <paramref name="hasOpenItem"/> = ทะเบียน "ภาษีถูกหัก" มีแถวของใบนี้ที่ยังตามใบอยู่ (ตัวโหลด/ตัวตัดสินเดียวกับ
    /// หน้าอัปโหลด 50 ทวิ เดิม — <c>WhtCertUploadLinkService.ItemsForContactAsync</c>) · ไม่มีแถว = null (ไม่มีการ์ด — ยังไม่ถูกหัก/ได้ใบครบแล้ว)
    /// </summary>
    public static WhtCertView? WhtCert(bool en, bool hasOpenItem, bool submitted, decimal missing)
    {
        if (!hasOpenItem) return null;
        return submitted
            ? new WhtCertView(WhtSubmitted, T(en, "หนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ)", "Withholding tax certificate (50 Tawi)"),
                T(en, "ได้รับไฟล์แล้ว — รอผู้ขายตรวจ (ไม่ต้องส่งซ้ำ)", "Received — waiting for the seller to review (no need to send again)"), false, missing)
            : new WhtCertView(WhtAwaiting, T(en, "หนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ)", "Withholding tax certificate (50 Tawi)"),
                T(en, "ท่านหักภาษี ณ ที่จ่ายจากการชำระเอกสารนี้ — กรุณาส่งหนังสือรับรอง (PDF หรือรูปภาพ ไม่เกิน 10 MB)",
                    "You withheld tax on this payment — please send the certificate (PDF or image, up to 10 MB)"), true, missing);
    }

    /// <summary>ป้ายของส่วนหลังจ่าย (ไทย/อังกฤษตามภาษาเอกสาร) — หน้าเว็บไม่มีข้อความของตัวเอง</summary>
    public static IReadOnlyDictionary<string, string> Labels(bool en) => new Dictionary<string, string>
    {
        ["slips"] = T(en, "สถานะการแจ้งโอน", "Transfer slips you sent"),
        ["slipSent"] = T(en, "ส่งเมื่อ", "Sent"),
        ["slipAgain"] = T(en, "แนบสลิปใหม่ได้ที่ฟอร์มแจ้งโอนด้านบน", "You can attach a new slip in the transfer form above"),
        ["receipts"] = T(en, "ใบเสร็จ/ใบกำกับภาษีของการชำระ", "Receipts / tax invoices for your payments"),
        ["whtFile"] = T(en, "ไฟล์หนังสือรับรอง", "Certificate file"),
        ["whtSender"] = T(en, "ชื่อผู้ส่ง (ไม่บังคับ)", "Sender name (optional)"),
        ["whtSend"] = T(en, "ส่งหนังสือรับรอง", "Send certificate"),
        ["whtMissing"] = T(en, "ภาษีที่ยังรอหนังสือรับรอง", "Withheld tax awaiting a certificate"),
        ["sending"] = T(en, "กำลังส่ง...", "Sending..."),
        ["offline"] = T(en, "เชื่อมต่อไม่ได้ — ลองใหม่อีกครั้ง", "Connection failed — please try again"),
        ["chooseFile"] = T(en, "กรุณาเลือกไฟล์", "Please choose a file"),
        ["failed"] = T(en, "ทำรายการไม่สำเร็จ — ลองใหม่ หรือติดต่อผู้ขาย", "Something went wrong — try again or contact the seller"),
        ["portalPay"] = T(en, "ไปหน้าชำระเงิน", "Go to the payment page"),
    };
}
