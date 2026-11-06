using System.Security.Cryptography;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §6.3 · คำตัดสินเจ้าของ §11 ข้อ 3) — <b>"ไม่หัก ณ ที่จ่าย" บนหน้าชำระตามเอกสาร: ตัวตัดสินเดียว</b> (pure)
///
/// <para>ลูกค้าเลือกไม่หักภาษี ณ ที่จ่ายได้ · ผู้ซื้อที่เป็นนิติบุคคล/หน่วยงานรัฐ (หรือไม่รู้ชนิด) ต้องเห็นคำเตือนหน้าที่หักตามกฎหมาย และ<b>บันทึกการยอมรับ</b>
/// (ข้อความที่แสดง · version · เวลา · hash ของ IP · hash หลักฐาน) · ยอดทุกตัวมาจากตัวคิดยอดเดิมของลิงก์ชำระ
/// (<see cref="DocumentPayLinkPolicy.AmountsFor"/>) — ไม่หัก = ยอด <c>Gross</c> · หัก = <c>Net</c> (ไม่มีสูตรที่สอง) · หน้าเว็บส่งแค่ตัวเลือก + version ที่เห็น</para>
///
/// <para>ยอดค้างของเอกสาร (<c>BalanceDue</c>) เป็นยอดสุทธิหลังหัก ณ ที่จ่ายอยู่แล้ว (คำตัดสิน 244-7) ⇒ เงินเข้าเต็มยอด "เกินยอดค้าง" เท่ายอด WHT ·
/// ตัวบันทึกรับชำระจึงลงให้อัตโนมัติไม่ได้ (ด่านจ่ายเกินเดิม) — <see cref="HandlerRefusal"/> ทำให้ล้มดังพร้อมป้ายฝั่งผู้ขาย (ประวัติรายการชำระ + แจ้งเจ้าของ)
/// แทนการเดาว่าจะลง WHT/ส่วนเกินที่ไหน (คำตัดสิน 253-WP10-3)</para>
/// </summary>
public static class NoWhtChoicePolicy
{
    public const string RuleCode = "PAYLINK-NOWHT";

    /// <summary>version ของข้อความคำเตือน — เปลี่ยนข้อความ ⇒ เปลี่ยน version (การยอมรับเก่าผูก version เดิม · หน้าเก่าที่ส่ง version เก่า = ปฏิเสธ ให้โหลดใหม่)</summary>
    public const string NoticeVersion = "NOWHT-2026-11-1";

    /// <summary>ข้อความคำเตือน (ข้อความเดียวที่หน้าแสดงและที่บันทึกเป็นหลักฐาน)</summary>
    public const string NoticeText =
        "ผู้จ่ายเงินที่เป็นนิติบุคคล/หน่วยงานรัฐมีหน้าที่หักภาษี ณ ที่จ่ายจากค่าบริการตามกฎหมาย (ประมวลรัษฎากร ม.3 เตรส · ม.50 และคำสั่งกรมสรรพากร ท.ป.4/2528) "
        + "หากไม่หักทั้งที่มีหน้าที่ ผู้จ่ายอาจต้องร่วมรับผิดชำระภาษีที่ไม่ได้หัก (ม.54) พร้อมเงินเพิ่ม ·เมื่อเลือก \"ไม่หัก\" ท่านจะชำระยอดเต็มตามเอกสาร "
        + "และผู้ขายจะไม่รอหนังสือรับรอง 50 ทวิ จากท่าน · ข้าพเจ้าได้อ่านและยืนยันว่าไม่หักภาษี ณ ที่จ่ายสำหรับการชำระครั้งนี้";

    /// <summary>ตัวเลือกบนหน้าชำระ — <paramref name="Offered"/> แสดงตัวเลือกไหม · <paramref name="Warn"/> ต้องแสดงคำเตือน + ติ๊กยอมรับ ·
    /// <paramref name="GrossAmount"/> ยอดที่ต้องชำระเมื่อไม่หัก (จากตัวคิดยอดเดิม)</summary>
    /// <param name="Eligible">ใบนี้ "เข้าเงื่อนไข" ให้เลือกไม่หักได้ถ้าเส้นลงบัญชีพร้อม (มี WHT · ผู้ซื้อไม่ใช่บุคคลธรรมดาที่รู้แน่) — ใช้เลือกข้อความปฏิเสธเท่านั้น</param>
    public sealed record Offer(bool Offered, bool Warn, decimal GrossAmount, bool Eligible = false);

    /// <summary>ข้อความเมื่อผู้ซื้อขอ "ไม่หัก ณ ที่จ่าย" ระหว่างที่ยังไม่เปิด (ฝ่ายค้าน WP-10 H1 · คำตัดสิน 253-WP10-8) — ปฏิเสธ<b>ก่อน</b>สร้างรายการชำระ (ไม่มีเงินเคลื่อน)</summary>
    public const string NotAvailableMessage =
        "ยังชำระแบบ \"ไม่หัก ณ ที่จ่าย\" ผ่านลิงก์นี้ไม่ได้ — กรุณาชำระยอดสุทธิที่แสดง (หลังหักภาษี ณ ที่จ่าย) แล้วส่งหนังสือรับรอง 50 ทวิ ให้ผู้ขาย "
        + "หรือติดต่อผู้ขายหากท่านไม่ต้องหักภาษี ณ ที่จ่าย";

    /// <summary>
    /// เสนอตัวเลือกไหม — <b>ไม่เสนอเลย</b> (ฝ่ายค้าน WP-10 H1 · คำตัดสิน 253-WP10-8): ยอดค้างของเอกสารเป็นยอดสุทธิหลังหัก (244-7) ⇒ เงินเต็มยอด = จ่ายเกิน
    /// ตัวบันทึกรับชำระลงไม่ได้ ⇒ เงินเข้าแต่ไม่ถูกบันทึก · เปิดได้เมื่อมีเส้น "ชำระโดยไม่ถูกหัก" เจ้าของเดียวใน <c>CreatePaymentAsync</c> (backlog) ·
    /// <paramref name="Eligible"/> = ใบนี้เข้าเงื่อนไขเดิม (มี WHT ที่ลูกค้าจะหัก และผู้ซื้อไม่ใช่บุคคลธรรมดาที่รู้แน่) — ใช้เลือกข้อความปฏิเสธเท่านั้น
    /// </summary>
    public static Offer OfferFor(DocumentPayLinkPolicy.Amounts amounts, bool? buyerWithholds)
    {
        var eligible = amounts.Wht > 0.005m && amounts.Net > 0.005m && buyerWithholds != false;
        return new Offer(false, false, amounts.Net, eligible);
    }

    /// <summary>คำขอเริ่มชำระที่เลือก "ไม่หัก" ⇒ เหตุปฏิเสธ (null = ไม่ได้เลือก/ดำเนินต่อได้) — ตัดสินก่อนสร้างรายการชำระ/บันทึกการยอมรับใด ๆ</summary>
    public static string? StartRefusal(bool noWithholding, Offer offer)
        => noWithholding && !offer.Offered ? NotAvailableMessage : null;

    /// <summary>ยอดที่ต้องชำระตามตัวเลือก — ไม่หัก (และเสนอจริง) = Gross · อื่น ๆ = Net (ยอดเดิมของลิงก์)</summary>
    public static decimal AmountDue(DocumentPayLinkPolicy.Amounts amounts, Offer offer, bool noWithholding)
        => noWithholding && offer.Offered ? amounts.Gross : amounts.Net;

    /// <summary>เหตุที่รับตัวเลือก "ไม่หัก" ไม่ได้ (null = รับได้) — ไม่ได้เสนอ (ไม่มี WHT/บุคคลธรรมดา) · ไม่ติ๊กยอมรับ · version ไม่ตรง (หน้าเก่า)</summary>
    public static string? AcceptRefusal(Offer offer, bool accepted, string? shownVersion)
    {
        if (!offer.Offered) return offer.Eligible ? NotAvailableMessage : "เอกสารนี้ไม่มีภาษีหัก ณ ที่จ่ายให้เลือก — ชำระตามยอดที่แสดง";
        if (!accepted) return "กรุณาอ่านคำเตือนแล้วติ๊กยืนยันก่อนเลือก \"ไม่หัก ณ ที่จ่าย\"";
        if (!string.Equals(shownVersion, NoticeVersion, StringComparison.Ordinal))
            return "ข้อความคำเตือนมีการปรับปรุง — กรุณาโหลดหน้านี้ใหม่แล้วยืนยันอีกครั้ง";
        return null;
    }

    /// <summary>SHA-256 hex ตัวเล็ก (ฟังก์ชัน hash เดียวของไฟล์นี้)</summary>
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>hash ของข้อความคำเตือนที่แสดง (หลักฐานว่าเห็นข้อความใด)</summary>
    public static string NoticeHash() => Sha(NoticeVersion + "\n" + NoticeText);

    /// <summary>ฝ่ายค้าน WP-10 L4: กุญแจ HMAC ของ hash IP — อนุพันธ์จากความลับของเซิร์ฟเวอร์ (JWT secret · fail-fast ใน production ที่ Program.cs อยู่แล้ว)
    /// ด้วยป้ายของเรื่องนี้ (แบบ <c>IdempotencyKeyPolicy.DeriveHashKey</c>) · ว่าง ⇒ โยน (กฎ #4 C ห้ามถอยไปกุญแจ dev เงียบ ๆ)</summary>
    public static byte[] DeriveIpKey(string? serverSecret)
    {
        if (string.IsNullOrWhiteSpace(serverSecret))
            throw new InvalidOperationException("ไม่ได้ตั้งความลับของเซิร์ฟเวอร์ (Jwt:Secret) — คำนวณ hash IP ของหลักฐาน \"ไม่หัก ณ ที่จ่าย\" ไม่ได้");
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(serverSecret), Encoding.UTF8.GetBytes("nowht-ip-hmac-v1"));
    }

    /// <summary>HMAC-SHA256 ของ IP ผูกบริษัท (PDPA — ไม่เก็บ IP ดิบ · เดาย้อนจาก IPv4 ทั้งช่วงไม่ได้หากไม่มีความลับ · บริษัทอื่นเทียบข้ามไม่ได้) · ไม่มี IP ⇒ null</summary>
    public static string? IpHash(byte[] key, Guid companyId, string? ip)
        => string.IsNullOrWhiteSpace(ip) ? null
            : Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"nowht-ip:{companyId:N}:{ip.Trim()}"))).ToLowerInvariant();

    /// <summary>ฝ่ายค้าน WP-10 L4: หลักฐานการยอมรับเก็บอย่างน้อย 5 ปี (หลักฐานประกอบการไม่หักภาษี ณ ที่จ่าย — พ.ร.บ.การบัญชี ม.14 · ป.รัษฎากร ม.87/3) · legal hold</summary>
    public static DateTime RetainUntil(DateTime acceptedAtUtc) => acceptedAtUtc.AddYears(5);

    /// <summary>hash หลักฐานของการยอมรับ (canonical function เดียว — ฝั่งเขียนและฝั่งตรวจเรียกที่นี่ · กฎ #4 C) — ผูกเอกสาร · เวลา (UTC ระดับวินาที) ·
    /// ยอด · version · hash ข้อความ · hash IP</summary>
    public static string EvidenceHash(Guid companyId, Guid documentId, DateTime acceptedAtUtc, decimal gross, decimal wht, decimal net,
        string noticeVersion, string noticeHash, string? ipHash, string? payerName)
        => Sha(string.Join("|", companyId.ToString("N"), documentId.ToString("N"),
            DateTime.SpecifyKind(acceptedAtUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            gross.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), wht.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            net.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), noticeVersion, noticeHash, ipHash ?? "", payerName ?? ""));

    /// <summary>
    /// เงินเข้าแล้วจากรายการชำระที่ลูกค้าเลือก "ไม่หัก": ยอดเกินยอดค้างของเอกสาร (= ยอดสุทธิหลังหักที่ตั้งไว้) ⇒ ข้อความป้ายฝั่งผู้ขาย (ตัวบันทึกรับชำระ
    /// ต้องไม่ลงเอง) · ยอดไม่เกิน (ผู้ขายแก้ใบแล้ว/ยอดค้างเท่าเงินเข้า) ⇒ null = บันทึกได้ โดยภาษีหัก ณ ที่จ่ายของงวดนี้ = 0 (ลูกค้าไม่ได้หัก — ห้ามออก 50 ทวิ เท็จ)
    /// </summary>
    public static string? HandlerRefusal(decimal paidAmount, decimal balanceDue, string? documentNumber)
        => paidAmount > balanceDue + 0.01m
            ? $"ลูกค้าเลือก \"ไม่หัก ณ ที่จ่าย\" และชำระยอดเต็ม {paidAmount:N2} บาท ซึ่งมากกว่ายอดค้างของ {documentNumber} ({balanceDue:N2} บาท · ยอดหลังหัก ณ ที่จ่ายที่ตั้งไว้บนใบ) — "
              + "ระบบไม่บันทึกรับชำระให้อัตโนมัติ: ตรวจกับลูกค้า/ผู้ทำบัญชี แล้วปรับภาษีหัก ณ ที่จ่ายของใบ หรือบันทึกรับชำระด้วยมือ (เงินเข้าแล้วจริง) "
              + $"[{RuleCode}]"
            : null;
}
