namespace Accounting.Helpers;

/// <summary>
/// รอบ 235 (K-8 · คำตัดสิน 235-4) — <b>ข้อความอีเมลยืนยันคำสั่งซื้อ/ใบเสร็จค่าแพ็กเกจ NextAcc ตัวเดียว</b> · pure
/// <para>เดิมอีเมลคำสั่งซื้อบอกแค่ชื่อ enum แพ็กเกจ/รอบ + ยอด (ไม่มีวันหมดอายุ) · อีเมลใบเสร็จบอกแค่เลขที่ใบ ⇒ ลูกค้าไม่รู้ว่าจ่ายแล้วได้ถึงวันไหน/ถูกหักเท่าไร</para>
/// <list type="bullet">
/// <item>แพ็ก (ชื่อแสดงผล) · รอบ (ป้ายไทย <see cref="PlatformPlanPricing.CycleLabel"/>) · ใช้ได้ถึง (พ.ศ. ผ่านตัวแปลงเดียว <see cref="QuotaAddOnPricing.ThaiDateText"/>)
/// · ยอด · หัก ณ ที่จ่าย (เฉพาะเมื่อ &gt; 0: ยอดเต็ม/ภาษีหัก/ยอดสุทธิ + เตือนส่ง 50 ทวิ)</item>
/// <item>คำสั่งซื้อ: วันที่ = "ถ้าชำระวันนี้" จาก preview (<see cref="PlanPurchasePreview.PaidPeriod"/>) — ข้อความบอกว่าวันจริงนับจากวันที่ชำระ ·
/// ใบเสร็จ: วันที่ = วันหมดอายุที่ประทับแล้ว (<c>SubscriptionPayment.SubscriptionExtendedTo</c>)</item>
/// <item>ไม่รู้วันที่ (null — ใบค่าใช้งาน/ตัดชำระใบ) ⇒ ไม่แต่งวันที่</item>
/// </list>
/// ผู้เรียก: <c>PublicCheckoutService.SendOrderEmailAsync</c> · <c>SaasBillingDocumentService.SendEmailAsync</c>
/// </summary>
public static class PlatformPurchaseEmailText
{
    /// <summary>วันที่ไทย พ.ศ. ของเวลา UTC — ตัวแปลงเดียวกับใบเสร็จ/preview</summary>
    public static string BeDate(DateTime utc) => QuotaAddOnPricing.ThaiDateText(utc.AddHours(7).Date);

    /// <summary>บรรทัดรายละเอียด (ข้อความล้วน — ผู้เรียก HtmlEncode เองก่อนต่อเข้า HTML)</summary>
    /// <param name="amountCharged">ยอดที่ตัด/โอน (สุทธิหลังหัก ณ ที่จ่าย)</param>
    /// <param name="withholdingTax">ภาษีหัก ณ ที่จ่าย (0 = ไม่หัก)</param>
    /// <param name="validUntilUtc">ใช้ได้ถึง (UTC) · null = ไม่แสดง</param>
    /// <param name="validUntilIsEstimate">true = วันที่ "ถ้าชำระวันนี้" (คำสั่งซื้อยังไม่จ่าย)</param>
    public static IReadOnlyList<string> Lines(string? planName, Models.Enums.BillingCycle? cycle, DateTime? validUntilUtc, bool validUntilIsEstimate,
        decimal amountCharged, decimal withholdingTax)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(planName))
            lines.Add(cycle is Models.Enums.BillingCycle c
                ? $"แพ็กเกจ {planName} · รอบบิลราย{PlatformPlanPricing.CycleLabel(c)} ({PlatformPlanPricing.MonthsOf(c)} เดือน)"
                : $"แพ็กเกจ {planName}");
        if (validUntilUtc is DateTime until)
            lines.Add(validUntilIsEstimate
                ? $"ใช้ได้ถึง {BeDate(until)} (ถ้าชำระวันนี้ — นับจากวันที่ชำระจริง)"
                : $"ใช้ได้ถึง {BeDate(until)}");
        var wht = Math.Max(0m, withholdingTax);
        if (wht > 0m)
        {
            var gross = PlatformWhtSuggestion.GrossOf(amountCharged, wht);
            lines.Add($"ยอดค่าบริการ {gross:N2} บาท · หักภาษี ณ ที่จ่าย {wht:N2} บาท · ยอดชำระสุทธิ {amountCharged:N2} บาท");
            lines.Add("กรุณาส่งหนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ) ที่ศูนย์บิลในระบบ");
        }
        else
            lines.Add($"ยอดชำระ {amountCharged:N2} บาท");
        return lines;
    }
}
