using System.Security.Cryptography;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 209 · P1 (R-209-A1/B3/B4/A9) — ตัวตัดสินเดียวของ "ออเดอร์หน้าร้านที่ลูกค้าเห็น" (pure):
/// โทเคนสาธารณะ · คีย์กันกดสั่งซ้ำ · ตะกร้าว่าง · ป้ายสถานะฝั่งลูกค้า · ผู้ขนส่ง + ลิงก์ติดตามพัสดุ · และ <b>มุมมองจำกัดฟิลด์</b> <see cref="ToPublic"/>
/// (ไม่มีที่อยู่/เบอร์/อีเมล/เลขผู้เสียภาษี/หมายเหตุภายใน/สลิป — คำตัดสินข้อ 168 ข)
/// </summary>
public static class SiteOrderPublic
{
    /// <summary>ฝ่ายค้านรอบ 209 (security ข้อ 2): คำตอบ "สร้างออเดอร์" ให้ผู้เรียกที่ไม่ใช่พนักงาน — เหลือเฉพาะที่หน้าเว็บใช้ (โทเคนหน้าสถานะ · เลข · ยอด) ·
    /// ไม่มีหมายเหตุภายใน (ประทับส่งแจ้งเตือนล้มมีอีเมลร้าน/ข้อความ error) · ไม่มีที่อยู่/อีเมล/เลขภาษี/สลิป (คีย์กันสั่งซ้ำซ้ำ ⇒ คืนออเดอร์เดิม — ห้ามเปิด PII ให้ผู้ถือคีย์)</summary>
    public static Accounting.Models.DTOs.Cms.OrderResponse ForGuestCreate(Accounting.Models.DTOs.Cms.OrderResponse full) => new()
    {
        Id = full.Id, OrderNumber = full.OrderNumber, Status = full.Status, PublicToken = full.PublicToken,
        Currency = full.Currency, SubTotal = full.SubTotal, DiscountAmount = full.DiscountAmount, ShippingAmount = full.ShippingAmount,
        VatAmount = full.VatAmount, TotalAmount = full.TotalAmount, AmountDueNow = full.AmountDueNow, NextPayableAmount = full.NextPayableAmount,
        CreatedAt = full.CreatedAt,
    };

    public const string RuleCode = "CMS-ORDER-PUBLIC";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int IdempotencyKeyMinLength = 16;
    public const int IdempotencyKeyMaxLength = 80;

    /// <summary>โทเคนสุ่ม 192 บิต (CSPRNG) แบบ base64url 32 ตัว — URL-safe · เดาไม่ได้ · ไม่ใช่ GUID ของออเดอร์</summary>
    public static string NewToken()
    {
        Span<byte> b = stackalloc byte[24];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>รูปโทเคนที่รับ (ใหม่ base64url 32 · backfill hex 64) — รูปอื่น ⇒ ไม่ถามฐานข้อมูลเลย (ตอบไม่พบ)</summary>
    public static bool IsWellFormedToken(string? token)
        => !string.IsNullOrEmpty(token) && token.Length is >= 20 and <= 64 && token.All(IsUrlSafe);

    private static bool IsUrlSafe(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    /// <summary>คีย์กันกดซ้ำ: ว่าง ⇒ (null, null) = ไม่กัน (ผู้เรียกเดิม) · สั้น/ยาว/ตัวอักษรนอก URL-safe ⇒ ปัญหา (400) ·
    /// ขั้นต่ำ 16 ตัวกันคีย์ง่าย ๆ ชนกันข้ามลูกค้า (คีย์ซ้ำคืนออเดอร์เดิม)</summary>
    public static (string? Key, string? Problem) NormalizeIdempotencyKey(string? raw)
    {
        var k = raw?.Trim();
        if (string.IsNullOrEmpty(k)) return (null, null);
        if (k.Length < IdempotencyKeyMinLength || k.Length > IdempotencyKeyMaxLength || !k.All(IsUrlSafe))
            return (null, $"คีย์กันสั่งซ้ำ (Idempotency-Key) ต้องยาว {IdempotencyKeyMinLength}–{IdempotencyKeyMaxLength} ตัว และใช้ได้เฉพาะ A–Z a–z 0–9 - _");
        return (k, null);
    }

    /// <summary>คีย์จาก body ชนะ header (ผู้เรียกตั้งใจส่งในคำขอ) · ไม่มีทั้งคู่ = null</summary>
    public static string? PickIdempotencyKey(string? bodyKey, string? headerKey)
        => !string.IsNullOrWhiteSpace(bodyKey) ? bodyKey : (string.IsNullOrWhiteSpace(headerKey) ? null : headerKey);

    /// <summary>R-209-A9: ออเดอร์ไม่มีบรรทัด (ตะกร้าว่าง/ตะกร้าผิดเว็บ/ไม่ส่งรายการ) ⇒ ปฏิเสธ · null = ผ่าน</summary>
    public static string? EmptyOrderProblem(int lineCount)
        => lineCount > 0 ? null : "ตะกร้าว่าง — เพิ่มสินค้าลงตะกร้าก่อนยืนยันสั่งซื้อ (ถ้าเพิ่งสั่งไปแล้ว ดูคำสั่งซื้อได้จากลิงก์ติดตามหรือหน้า \"บัญชีของฉัน\")";

    /// <summary>ผู้ขนส่งของออเดอร์ตอนสั่ง: มารับเอง ⇒ ไม่มี · จัดส่ง ⇒ ชื่อผู้ขนส่งของอัตราที่เลือก (ตัดช่องว่าง · ≤ 100 ตัว) · ไม่ตั้งไว้ ⇒ null</summary>
    public static string? CarrierAtOrder(string? fulfillmentMethod, string? rateCarrierName)
    {
        if (fulfillmentMethod == FulfillmentMethod.Pickup) return null;
        return CleanCarrier(rateCarrierName);
    }

    /// <summary>ค่าผู้ขนส่งที่พนักงานพิมพ์: null = ไม่แก้ (คืน current) · "" = ล้าง · อื่น ๆ = ตัดช่องว่าง ≤ 100</summary>
    public static string? CarrierEdit(string? current, string? requested)
        => requested == null ? current : CleanCarrier(requested);

    private static string? CleanCarrier(string? s)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > 100 ? t[..100] : t;
    }

    /// <summary>ลิงก์ติดตามพัสดุ — เฉพาะผู้ขนส่งที่รู้รูปแบบ URL จริง (ไปรษณีย์ไทย/EMS · Kerry/KEX · Flash) · ไม่รู้จัก ⇒ null (หน้าเว็บแสดงเลขพัสดุอย่างเดียว ไม่แต่งลิงก์)</summary>
    public static string? TrackingUrl(string? carrier, string? trackingNumber)
    {
        var no = trackingNumber?.Trim();
        if (string.IsNullOrEmpty(no) || string.IsNullOrWhiteSpace(carrier)) return null;
        var c = carrier.Trim().ToLowerInvariant();
        var q = Uri.EscapeDataString(no);
        if (c.Contains("ไปรษณีย์") || c.Contains("thailand post") || c.Contains("thaipost") || c == "ems" || c.Contains("ems "))
            return "https://track.thailandpost.co.th/?trackNumber=" + q;
        if (c.Contains("kerry") || c.Contains("kex") || c.Contains("เคอรี่"))
            return "https://th.kex-express.com/th/track/?track=" + q;
        if (c.Contains("flash") || c.Contains("แฟลช"))
            return "https://www.flashexpress.co.th/fle/tracking?se=" + q;
        return null;
    }

    /// <summary>ป้ายสถานะที่<b>ลูกค้า</b>เห็น (ตัวตั้งเดียว · หน้าเว็บไม่มีตารางเอง) — ป้ายฝั่งพนักงานอยู่ที่หน้า cms-orders</summary>
    public static string StatusLabel(SiteOrderStatus s, string? fulfillmentMethod = null) => s switch
    {
        SiteOrderStatus.Pending => "รอร้านยืนยัน",
        SiteOrderStatus.Confirmed => "ร้านยืนยันแล้ว",
        SiteOrderStatus.Processing => "กำลังเตรียมสินค้า",
        SiteOrderStatus.Shipped => fulfillmentMethod == FulfillmentMethod.Pickup ? "พร้อมให้มารับ" : "จัดส่งแล้ว",
        SiteOrderStatus.Delivered => fulfillmentMethod == FulfillmentMethod.Pickup ? "รับสินค้าแล้ว" : "ส่งถึงแล้ว",
        SiteOrderStatus.Cancelled => "ยกเลิกแล้ว",
        SiteOrderStatus.Refunded => "คืนเงินแล้ว",
        SiteOrderStatus.PartiallyRefunded => "คืนเงินบางส่วน",
        _ => s.ToString(),
    };

    /// <summary>ยอดของ QR พร้อมเพย์ที่สร้างฝั่งเซิร์ฟเวอร์ (R-209 ฑ · เลิกส่งเลข/ยอดไป api.qrserver.com): ต้อง &gt; 0 และ ≤ 10 ล้าน · ปัด 2 ตำแหน่ง (AwayFromZero) · อื่น ๆ ⇒ null</summary>
    public static decimal? QrAmount(decimal? amount)
    {
        if (amount is not decimal a || a <= 0m || a > 10_000_000m) return null;
        return Math.Round(a, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>บัตรคอร์สของลูกค้า "ใช้จองได้ตอนนี้" — สถานะใช้งาน · ไม่หมดอายุ · เหลือเครดิต (เซิร์ฟเวอร์ตัดสินซ้ำตอนจองจริงที่สมุดเครดิต)</summary>
    public static bool CreditUsable(CreditPassStatus status, bool expired, decimal available)
        => status == CreditPassStatus.Active && !expired && available > 0m;

    public static string CreditUnitLabel(CreditUnit u) => u == CreditUnit.Hour ? "ชั่วโมง" : "ครั้ง";

    /// <summary>มุมมองสาธารณะจากคำตอบเต็มของหลังบ้าน (ยอด/งวด/ป้ายคำนวณที่เซิร์ฟเวอร์แล้ว — ที่นี่แค่ "เลือกฟิลด์") ·
    /// ไม่ copy: ที่อยู่ · เบอร์ · อีเมล · ชื่อลูกค้า · เลขผู้เสียภาษี · หมายเหตุลูกค้า/ภายใน · คูปอง · รายการรับเงิน/สลิป · เอกสาร ERP</summary>
    public static PublicOrderResponse ToPublic(OrderResponse o, bool promptPayAvailable)
    {
        var fulfillment = o.FulfillmentMethod ?? FulfillmentMethod.Delivery;
        return new PublicOrderResponse
        {
            Id = o.Id,
            OrderNumber = o.OrderNumber,
            Status = o.Status,
            StatusLabel = StatusLabel(o.Status, fulfillment),
            CreatedAt = o.CreatedAt,
            Currency = o.Currency,
            SubTotal = o.SubTotal, DiscountAmount = o.DiscountAmount, ShippingAmount = o.ShippingAmount,
            VatAmount = o.VatAmount, TotalAmount = o.TotalAmount, PaidAmount = o.PaidAmount,
            AmountDueNow = o.AmountDueNow, NextPayableAmount = o.NextPayableAmount, BalanceAmount = o.BalanceAmount,
            PaymentTermMode = o.PaymentTermMode, PaymentTermLabel = o.PaymentTermLabel,
            DepositRequiredAmount = o.DepositRequiredAmount, DepositInvoicingNote = o.DepositInvoicingNote,
            EarliestReadyDate = o.EarliestReadyDate,
            FulfillmentMethod = fulfillment, FulfillmentLabel = o.FulfillmentLabel ?? FulfillmentMethod.Label(fulfillment),
            ShippingMethod = o.ShippingMethod,
            ShippingCarrier = o.ShippingCarrier,
            TrackingNumber = o.TrackingNumber,
            TrackingUrl = TrackingUrl(o.ShippingCarrier, o.TrackingNumber),
            ShippedAt = o.ShippedAt, DeliveredAt = o.DeliveredAt,
            OnlinePaymentBlockedNote = o.OnlinePaymentBlockedNote,
            PromptPayQrAvailable = promptPayAvailable && o.NextPayableAmount > 0.005m,
            DepositDocumentNumbers = o.DepositDocuments.Select(d => d.DocumentNumber).Where(n => !string.IsNullOrEmpty(n)).ToList(),
            Lines = o.Lines.Select(l => new PublicOrderLineResponse
            {
                ProductName = l.ProductName, Quantity = l.Quantity, Unit = l.Unit,
                UnitPrice = l.UnitPrice, TotalAmount = l.TotalAmount, ModifierSummary = l.ModifierSummary,
            }).ToList(),
        };
    }
}
