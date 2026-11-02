using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Payments;

/// <summary>
/// รอบ 228 (PLATFORM_OMISE_AUDIT §5.1) — <b>สิ่งที่หน้าชำระต้องรู้เพื่อรับบัตร</b> (ตัวเดียวของทุกทางเข้า: ร้าน <c>public-pay/methods</c> ·
/// ค่าบริการ NextAcc <c>PlatformPayInfo</c>) · ไม่มีชื่อเจ้า/โดเมนในไฟล์นี้ — ค่ามาจาก adapter (<see cref="IPaymentProvider.ClientScriptUrl"/> ·
/// <see cref="IPaymentProvider.ClientPublicKey"/>) · หน้าเว็บ (<c>js/pay-widget.js</c>) โหลดสคริปต์จาก URL นี้แล้วแปลงเลขบัตรเป็นโทเคนในเบราว์เซอร์
/// <para><b>PCI-DSS SAQ-A:</b> เลขบัตร/CVV ไม่เคยถูกส่งมาที่เซิร์ฟเวอร์ของเรา — มีแต่โทเคน (<c>ChargeRequest.CardToken</c>)</para>
/// </summary>
/// <param name="ProviderCode">ชื่อ adapter — หน้าเว็บเลือกตัวแปลงโทเคนตามค่านี้ (ไม่เดาเอง)</param>
/// <param name="ScriptUrl">สคริปต์ฝั่งเบราว์เซอร์ของผู้ให้บริการ (โดเมนอยู่ใน CSP จาก <see cref="IPaymentProvider.CspNeeds"/> แล้ว)</param>
/// <param name="PublicKey">คีย์สาธารณะของโหมดปัจจุบันของช่องทาง</param>
/// <param name="IsTestMode">โหมดทดสอบ (หน้าเว็บแสดงป้าย + บัตรทดสอบเท่านั้น)</param>
public sealed record PaymentClientSetup(string ProviderCode, string ScriptUrl, string PublicKey, bool IsTestMode)
{
    /// <summary>หน้าชำระรับบัตรของช่องทางนี้ได้ไหม — null = ไม่ได้ (ผู้ให้บริการไม่รองรับบัตร · ไม่มีสคริปต์ · ยังไม่ใส่คีย์สาธารณะของโหมดนี้) · pure</summary>
    public static PaymentClientSetup? For(IPaymentProvider? provider, PaymentProviderConfig? config)
    {
        if (provider == null || config == null) return null;
        if (!provider.Capabilities.Methods.Contains(PaymentMethodKind.Card)) return null;
        var script = provider.ClientScriptUrl;
        var key = provider.ClientPublicKey(config);
        if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(key)) return null;
        return new PaymentClientSetup(provider.ProviderCode, script, key.Trim(), config.Mode == PaymentProviderMode.Test);
    }

    /// <summary>ตัดวิธี "บัตร" ออกจากรายการวิธีของหน้าชำระเมื่อรับบัตรไม่ได้ (ไม่มีคีย์สาธารณะ/สคริปต์) — เดิมปุ่มบัตรขึ้นแล้วเซิร์ฟเวอร์โยน
    /// "ไม่พบโทเคนบัตร" ทุกครั้ง · ชื่อ enum · pure</summary>
    public static List<string> WithoutUnusableCard(IEnumerable<string> methods, PaymentClientSetup? card)
        => methods.Where(m => card != null || !string.Equals(m, nameof(PaymentMethodKind.Card), StringComparison.OrdinalIgnoreCase)).ToList();
}
