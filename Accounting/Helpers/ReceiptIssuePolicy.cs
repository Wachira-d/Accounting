using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาเดียวของระบบว่า "การขายหนึ่งครั้งออกกระดาษกี่ใบ" ตาม
/// <see cref="ReceiptIssueMode"/> ของบริษัท
///
/// ═══ ทำไมต้องเป็นตัวกลาง ═══
/// เงื่อนไขนี้ถูกอ่านจาก <b>5 จุดที่อยู่คนละไฟล์</b>: ตอนตัดสินหัวกระดาษ
/// (<c>PdfGenerationService.ResolveServedAsReceiptAsync</c>), ตอนตอบ API/หน้าจอ
/// (<c>DocumentService.ComputeServedAsReceipt</c>), ตอนบันทึกรับชำระ
/// (<c>DocumentService.CreatePaymentAsync</c>), ตอนสร้างเอกสาร (ฟอร์ม
/// documents.html) และตอนกันภาษีขายซ้ำ. ถ้าปล่อยให้แต่ละที่เขียน
/// <c>mode == ReceiptIssueMode.X</c> เอง = สำเนามือ 5 ชุดที่รอ drift
/// (กฎเหล็ก #4 A — defect class ที่เรพนี้เจอบ่อยที่สุด)
///
/// **ทุกฟังก์ชันที่ตอบว่า "ไม่ได้/ไม่ออก" ต้องมีคู่ที่อธิบายเหตุผลเป็นข้อความ
/// ไทยที่เอาไปโชว์ได้** — ห้ามให้หน้าจอซ่อนปุ่มเงียบ ๆ แล้วผู้ใช้เดาเอง
/// </summary>
public static class ReceiptIssuePolicy
{
    /// <summary>ใบกำกับที่รับเงินครบแล้ว "ยกหัวเป็นใบเสร็จในตัว" ได้ไหม
    /// (<c>ServedAsReceipt</c>). โหมดแยกใบ = ไม่ได้ เพราะใบเสร็จตัวจริงคือ REC
    /// อีกใบ — ถ้ายกหัวด้วยจะมีกระดาษ 2 ใบที่ต่างพูดว่า "ใบเสร็จรับเงิน"
    /// จากการรับเงินก้อนเดียว</summary>
    public static bool AllowsCombinedReceiptHeader(ReceiptIssueMode mode)
        => mode != ReceiptIssueMode.SeparateReceipt;

    /// <summary>ใบกำกับใบนี้ยกหัวเป็น "ใบเสร็จรับเงิน" ในตัวได้ไหม — <b>ต้องรับเงิน
    /// ครบในวันเดียวกับวันที่บนใบ</b> (ขายสด/รับเงินหน้างาน) เท่านั้น
    ///
    /// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-10) ═══
    /// กด "บันทึกชำระเงิน" บนใบแจ้งหนี้/ใบกำกับภาษีลงวันที่ 5 ส.ค. โดยเงินเข้าจริง
    /// คนละวัน แล้ว<b>ใบเดิม</b>เปลี่ยนหัวเป็น "ใบแจ้งหนี้/ใบกำกับภาษี/ใบเสร็จรับเงิน"
    /// ⇒ กระดาษที่ลงวันที่ 5 ส.ค. ประกาศว่ารับเงินวันที่ 5 ส.ค. ทั้งที่รับจริงวันอื่น
    /// = <b>ใบรับที่ลงวันที่เท็จ</b> (ป.รัษฎากร ม.105 ใบรับต้องออก "ทันทีที่รับเงิน"
    /// และลงวันที่ที่รับเงินจริง) · ผู้จ่ายที่หัก ณ ที่จ่ายก็ออก 50 ทวิ ลงวันที่ที่
    /// จ่ายจริง ⇒ วันบนใบเสร็จกับ 50 ทวิ ไม่ตรงกัน</para>
    ///
    /// <para>โค้ดเดิม<b>ไม่เคยเทียบวันที่เลย</b> ทั้งที่ doc-comment ของทั้งสองฝั่ง
    /// (<c>ResolveServedAsReceiptAsync</c> / <c>ComputeServedAsReceipt</c>) เขียนไว้เอง
    /// ว่า "ชำระครบ <b>ณ วันออก</b> (cash sale / same-day settlement)" — เจตนาถูก
    /// โค้ดไม่ทำตาม (กฎเหล็ก #4: doc-comment ที่เขียนสูตรถูกไว้ ไม่ได้แปลว่าโค้ดทำตาม)</para>
    ///
    /// <para><paramref name="settledOn"/> = วันที่ของการรับชำระที่ปิดยอด (วันที่บน
    /// ใบเสร็จที่ต้องออก). <c>null</c> = <b>ไม่รู้</b> ว่ารับเงินวันไหน (ยอดถูกปิดด้วย
    /// การหักมัดจำ ซึ่งใบมัดจำเป็นหลักฐานรับเงินอยู่แล้ว / ข้อมูลก่อนย้ายระบบ) →
    /// ไม่ยกหัว: "ไม่รู้ = บอกว่าไม่รู้" และการพิมพ์ใบเสร็จเกินอันตรายกว่าการไม่พิมพ์</para></summary>
    public static bool SettledSameDay(DateTime documentDate, DateTime? settledOn)
        => settledOn.HasValue && settledOn.Value.Date == documentDate.Date;

    /// <summary>ใบต้นทางที่ยกหัวเป็นใบเสร็จ (<paramref name="documentServesAsReceipt"/>)
    /// ทำหน้าที่เป็นใบรับของ<b>การรับชำระรายการนี้</b> ด้วยไหม
    ///
    /// <para>ต้องเทียบวันที่ของรายการชำระเอง ไม่ใช่เชื่อธงระดับเอกสารอย่างเดียว:
    /// ใบที่ผ่อนหลายงวดแล้ว<b>งวดสุดท้าย</b>บังเอิญตรงวันที่บนใบ จะได้ธงระดับเอกสาร
    /// เป็นจริง — แต่งวดก่อนหน้าที่รับเงินคนละวัน<b>ยังไม่มีกระดาษใบรับ</b>
    /// ถ้าเอาธงระดับเอกสารไปปิดปุ่มทั้งแถว ผู้ใช้จะออกใบเสร็จให้งวดนั้นไม่ได้เลย</para>
    ///
    /// <para>ใช้ทั้งฝั่งแสดงผล (<c>GetPaymentsAsync</c> → ซ่อนปุ่ม "ออกใบเสร็จ")
    /// และฝั่งด่าน (<c>IssueReceiptForPaymentAsync</c>) — ตัวเดียวกัน ห้ามคัดลอก</para></summary>
    public static bool CoversPayment(bool documentServesAsReceipt, DateTime documentDate, DateTime paymentDate)
        => documentServesAsReceipt && paymentDate.Date == documentDate.Date;

    /// <summary>เหตุผลที่ยก/ไม่ยกหัวเป็นใบเสร็จ — เอาไปโชว์ได้ (ห้ามให้หน้าจอ
    /// ซ่อนแล้วผู้ใช้เดาเอง). <c>null</c> = ยกหัวได้</summary>
    public static string? WhyNotCombined(ReceiptIssueMode mode, DateTime documentDate, DateTime? settledOn)
    {
        if (!AllowsCombinedReceiptHeader(mode)) return Describe(mode);
        if (!settledOn.HasValue)
            return "ยอดถูกปิดโดยไม่มีรายการรับชำระที่ระบุวันที่ (เช่นหักจากใบมัดจำ) — "
                + "หลักฐานรับเงินคือใบที่รับเงินจริง ไม่ใช่ใบนี้";
        if (settledOn.Value.Date != documentDate.Date)
            // ⚠️ ต้องระบุ InvariantCulture — ถ้า process ตั้ง culture th-TH ปฏิทินเริ่มต้น
            // เป็นพุทธศักราช ⇒ "05/08/2569" ปนกับ ค.ศ. ในข้อความเดียวกันโดยเงียบ
            return $"รับเงินจริงวันที่ {Day(settledOn.Value)} คนละวันกับวันที่บนใบ "
                + $"({Day(documentDate)}) — ใบรับต้องลงวันที่ที่รับเงินจริง (ม.105) "
                + "ระบบจึงออกใบเสร็จรับเงินแยกให้ลงวันที่รับเงิน";
        return null;
    }

    private static string Day(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>ค่าเริ่มต้นของ "ออกใบเสร็จให้ลูกค้าด้วยไหม" ตอนบันทึกรับชำระ
    /// (<c>CreatePaymentRequest.IssueReceiptDocument</c> ที่ไม่ได้ระบุมา)
    ///
    /// Combined คืน true = <b>พฤติกรรมเดิมเป๊ะ</b> (server เดิมใช้ <c>?? true</c>)
    /// — โหมดนี้เป็น default ของทุก tenant จึงต้องไม่เปลี่ยนอะไรเลย</summary>
    public static bool DefaultIssueSeparateReceipt(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt => true,
        // ค้าปลีก: รับเงินพร้อมออกใบอยู่แล้ว ออกใบเสร็จตามหลังอีกใบ = กระดาษเกิน
        ReceiptIssueMode.RetailReceipt => false,
        _ => true,
    };

    /// <summary>บังคับออกใบเสร็จแยกเสมอไหม — true = หน้าจอต้อง<b>ล็อกช่อง</b>
    /// (ติ๊กไว้ กดออกไม่ได้) พร้อมบอกเหตุผล ไม่ใช่ปล่อยให้ติ๊กออกแล้วเงียบ</summary>
    public static bool ForcesSeparateReceipt(ReceiptIssueMode mode)
        => mode == ReceiptIssueMode.SeparateReceipt;

    /// <summary>เปิดให้เลือกกระดาษแบบ "ใบกำกับ+ใบเสร็จในใบเดียว" ตอนสร้างเอกสาร
    /// ไหม (โหมดกระดาษ <c>tax_receipt</c> / <c>invoice_tax_paid</c> /
    /// <c>tax_paid</c> ในฟอร์ม). โหมดแยกใบ = ไม่เปิด — ไม่งั้นผู้ใช้เลือกทางที่
    /// ขัดกับนโยบายของบริษัทตัวเองได้จากหน้าเดียวกัน</summary>
    public static bool AllowsCashReceiptPapers(ReceiptIssueMode mode)
        => mode != ReceiptIssueMode.SeparateReceipt;

    /// <summary>คำอธิบายสั้น ๆ ของโหมด — ใช้ทั้งหน้าตั้งค่าและข้อความบอกเหตุผล
    /// เวลาช่องถูกล็อก (ข้อความชุดเดียว ห้ามให้แต่ละหน้าจอแต่งคำเอง)</summary>
    public static string Describe(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt =>
            "บริษัทตั้งค่าเป็น \"แยกใบกำกับภาษี–ใบเสร็จรับเงิน\" — ใบกำกับ (TIV) ออก ณ วันส่งมอบ/วางบิล "
            + "และใบเสร็จ (REC) ออก ณ วันรับเงินเสมอ",
        ReceiptIssueMode.RetailReceipt =>
            "บริษัทตั้งค่าเป็น \"ค้าปลีก (ใบเดียวที่จุดขาย)\" — ออกใบเสร็จรับเงิน/ใบกำกับภาษีใบเดียวจบ "
            + "ไม่ออกใบเสร็จตามหลังการรับชำระ",
        _ =>
            "บริษัทตั้งค่าเป็น \"ใบเดียวจบ\" — ใบกำกับที่รับเงินครบแล้วทำหน้าที่ใบเสร็จในตัว",
    };

    /// <summary>ชื่อโหมดสั้น ๆ สำหรับ dropdown/ป้าย</summary>
    public static string Label(ReceiptIssueMode mode) => mode switch
    {
        ReceiptIssueMode.SeparateReceipt => "แยกใบกำกับภาษี–ใบเสร็จรับเงิน",
        ReceiptIssueMode.RetailReceipt => "ค้าปลีก (ใบเดียวที่จุดขาย)",
        _ => "ใบเดียวจบ (ค่าเริ่มต้น)",
    };
}
