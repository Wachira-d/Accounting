using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 227 (PLATFORM_OMISE_AUDIT A-3/A-4/B-2) — วิธีชำระที่<b>หน้าชำระค่าบริการของ NextAcc</b> (ศูนย์บิล · แพ็กเกจ · ส่วนเสริม · ลิงก์ชำระ — ทุกหน้าใช้ <c>js/pay-widget.js</c>)
/// ทำให้จบได้จริงวันนี้ · pure · ตัวเดียวของหน้าแอดมิน (ป้าย/เหตุผลต่อวิธี) · <c>PlatformPaymentService</c> (วิธีที่ผู้เช่าเห็น) · ด่านเปิดรับเงินจริง
/// <para><b>ที่มา:</b> ผู้ให้บริการรองรับ 5 วิธี และค่าตั้ง "ไม่ติ๊ก = เปิดทุกวิธีที่รองรับ" ⇒ ผู้เช่าเห็น/ได้วิธีที่หน้าชำระทำไม่จบ:
/// บัตร (ไม่มีช่องกรอกบัตร/โทเคนในเบราว์เซอร์ — เซิร์ฟเวอร์โยน "ไม่พบโทเคนบัตร" ทุกครั้ง) · วอลเล็ท (ต้องมีเบอร์ผู้จ่าย) ·
/// โมบาย/อินเทอร์เน็ตแบงก์กิ้ง (พาออกไปแอป/เว็บธนาคาร แล้วหน้าเดิมไม่รับกลับมาตรวจสถานะ) · ศูนย์บิลเลือก "วิธีแรกของรายการ" ⇒ ติ๊กบัตรไว้ก่อน = ชำระออนไลน์ล้มทุกครั้ง</para>
/// <para>ไม่ตัดสิทธิ์ของร้าน (หน้าชำระของร้านคนละชุด) · เพิ่มวิธีเมื่อหน้าชำระรองรับครบวง (ดีไซน์ §5 ของ PLATFORM_OMISE_AUDIT) = แก้ที่นี่ที่เดียว</para>
/// </summary>
public static class PlatformPayMethods
{
    /// <summary>เหตุที่หน้าชำระของ NextAcc ยังใช้วิธีนี้ไม่ได้ — null = ใช้ได้</summary>
    public static string? NotPayableReason(PaymentMethodKind method) => method switch
    {
        PaymentMethodKind.PromptPay => null,
        PaymentMethodKind.Card => "หน้าชำระของ NextAcc ยังไม่มีช่องกรอกบัตร (แปลงเลขบัตรเป็นโทเคนในเบราว์เซอร์) และหน้ารับกลับหลัง 3-D Secure — เปิดไว้ผู้เช่าจะชำระไม่สำเร็จ",
        PaymentMethodKind.TrueMoney => "ต้องขอเบอร์วอลเล็ทของผู้จ่าย — หน้าชำระของ NextAcc ยังไม่มีช่องนี้",
        PaymentMethodKind.MobileBanking or PaymentMethodKind.InternetBanking
            => "พาผู้จ่ายออกไปแอป/เว็บธนาคาร — หน้าชำระของ NextAcc ยังไม่รับกลับมาตรวจสถานะ (เงินเข้าได้แต่ผู้เช่าไม่เห็นผล)",
        PaymentMethodKind.ManualSlip => "โอนเงิน + แนบสลิปเป็นเส้นแยก (เปิดเสมอ) ไม่ใช่วิธีของช่องทางออนไลน์",
        _ => "ยังไม่รองรับ",
    };

    public static bool IsPayable(PaymentMethodKind method) => NotPayableReason(method) == null;

    /// <summary>ชื่อไทยของวิธี (หน้าแอดมิน/หน้าเลือกวิธี) — ตัวเดียว (หน้าเว็บไม่ต้องมีตารางป้ายของตัวเอง)</summary>
    public static string Label(PaymentMethodKind method) => method switch
    {
        PaymentMethodKind.PromptPay => "พร้อมเพย์ (QR)",
        PaymentMethodKind.Card => "บัตรเครดิต/เดบิต",
        PaymentMethodKind.MobileBanking => "โมบายแบงก์กิ้ง",
        PaymentMethodKind.InternetBanking => "อินเทอร์เน็ตแบงก์กิ้ง",
        PaymentMethodKind.TrueMoney => "TrueMoney Wallet",
        PaymentMethodKind.ManualSlip => "โอนแล้วแนบสลิป",
        _ => method.ToString(),
    };

    /// <summary>วิธีที่ผู้เช่าเห็น = (ที่แอดมินติ๊ก ∩ ที่ผู้ให้บริการรองรับ — ไม่ติ๊กเลย = ทุกวิธีที่รองรับ) ∩ ที่หน้าชำระของ NextAcc ทำจบได้ · ชื่อ enum · ลำดับตามผู้ให้บริการ</summary>
    public static List<string> Effective(IEnumerable<PaymentMethodKind> providerSupported, IReadOnlyCollection<string> configured)
    {
        var supported = providerSupported.Where(m => m != PaymentMethodKind.ManualSlip).ToList();
        var chosen = configured.Count == 0
            ? supported
            : supported.Where(m => configured.Contains(m.ToString(), StringComparer.OrdinalIgnoreCase)).ToList();
        return chosen.Where(IsPayable).Select(m => m.ToString()).ToList();
    }

    /// <summary>ชื่อวิธีที่แอดมินส่งมาแต่ผู้ให้บริการไม่รองรับ/ไม่ใช่ชื่อวิธี (ด่านตอนบันทึก) — ว่าง = ใช้ได้ทั้งหมด</summary>
    public static List<string> Unknown(IEnumerable<PaymentMethodKind> providerSupported, IEnumerable<string> requested)
    {
        var ok = providerSupported.Where(m => m != PaymentMethodKind.ManualSlip).Select(m => m.ToString()).ToList();
        return requested.Where(r => !ok.Contains(r?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)).Select(r => r ?? "").Distinct().ToList();
    }
}
