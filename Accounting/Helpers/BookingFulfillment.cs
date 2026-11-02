using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ค่าตั้งวิธีรับของการจองหนึ่งชั้น — เว็บ (bool ครบ) หรือบริการ (null = ตามเว็บ)</summary>
public sealed record BookingFulfillmentRule(bool? DeliveryEnabled, bool? PickupEnabled, string? DefaultMethod);

/// <summary>วิธีรับที่มีผลกับบริการหนึ่ง (ประกอบโดย <see cref="BookingFulfillment.Resolve"/> เท่านั้น)</summary>
public sealed record BookingFulfillmentEffective(bool Delivery, bool Pickup, string? DefaultMethod, bool FromService)
{
    /// <summary>มีขั้น "จัดส่ง / รับเอง" บนฟอร์มจองไหม (ปิดทั้งคู่ = การจองนัดหมายแบบเดิม ไม่มีขั้นนี้)</summary>
    public bool Enabled => Delivery || Pickup;
    /// <summary>ต้องให้ลูกค้าเลือก (เปิดทั้งสองแบบ)</summary>
    public bool CustomerChooses => Delivery && Pickup;
}

/// <summary>ส่วนของยอดมัดจำ/ชำระล่วงหน้าที่เป็นค่าบริการ / ค่าจัดส่ง (ใบรับมัดจำแยกบรรทัด)</summary>
public readonly record struct BookingPrepaySplit(decimal Service, decimal Delivery);

/// <summary>
/// รอบ 226 (คำขอเจ้าของ · BOOKING_PAYMENT_AUDIT §7) — <b>ตัวตัดสินเดียว</b>ของ "การจองแบบจัดส่ง / รับเอง" (เช่น เบเกอรี่สั่งทำ):
/// วิธีที่เปิด (เว็บ ?? บริการ) · วิธีที่ใช้จริงจากคำขอลูกค้า · ที่อยู่ที่ต้องกรอก · ยอดรวม · การแบ่งยอดล่วงหน้าเป็นค่าบริการ/ค่าส่ง
///
/// <para><b>ค่าจัดส่ง</b> คิดด้วยตัวคิดของร้านตัวเดียว <see cref="ShopDeliveryFee"/> (โซน/จังหวัด/รหัสไปรษณีย์ · เหมาจ่าย · ส่งฟรีเมื่อครบ) โดยใช้ค่าบริการเป็นยอดสินค้า ·
/// ลูกค้าส่งได้แค่ที่อยู่ + อัตราที่เลือก — <b>ยอดคิดที่เซิร์ฟเวอร์ทุกครั้ง</b> (ตัวอย่างก่อนจอง = ตอนสร้างการจอง)</para>
/// <para><b>คำตัดสิน 226-4</b>: <c>SiteBooking.TotalAmount</c> = ค่าบริการเท่านั้น (ค่าคอม/ส่วนแบ่งผู้ให้บริการ/ค่าธรรมเนียมยกเลิกคิดจากค่าบริการเหมือนเดิม) ·
/// ค่าจัดส่งเก็บแยกที่ <c>SiteBooking.DeliveryFee</c> · ยอดที่ลูกค้าต้องจ่ายทั้งหมด = <see cref="GrandTotal"/> · มัดจำ (บาท/%) คิดจากค่าบริการ ·
/// ชำระเต็มจำนวน = ค่าบริการ + ค่าจัดส่ง · เอกสาร ERP มีบรรทัด "ค่าจัดส่ง" แยก อัตรา VAT แบบเดียวกับคำสั่งซื้อ (<see cref="ShippingFeeRules.VatRateForShipping"/> §79)</para>
/// </summary>
public static class BookingFulfillment
{
    public const string RuleCode = "CMS-BOOKING-FULFILLMENT";
    public const int MaxNoteLength = 1000;
    public const int MaxAddressLength = 1000;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Baht(decimal v) => "฿" + v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>บริการ (ถ้าตั้ง) ชนะเว็บทีละช่อง · ค่าตั้งต้นที่ชี้วิธีที่ปิด ⇒ ไม่มีค่าตั้งต้น</summary>
    public static BookingFulfillmentEffective Resolve(BookingFulfillmentRule site, BookingFulfillmentRule? service)
    {
        var delivery = service?.DeliveryEnabled ?? site.DeliveryEnabled ?? false;
        var pickup = service?.PickupEnabled ?? site.PickupEnabled ?? false;
        var def = NormalizeOrNull(service?.DefaultMethod) ?? NormalizeOrNull(site.DefaultMethod);
        if (def == FulfillmentMethod.Delivery && !delivery) def = null;
        if (def == FulfillmentMethod.Pickup && !pickup) def = null;
        var fromService = service is { } s && (s.DeliveryEnabled != null || s.PickupEnabled != null || s.DefaultMethod != null);
        return new BookingFulfillmentEffective(delivery, pickup, def, fromService);
    }

    private static string? NormalizeOrNull(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s == FulfillmentMethod.Delivery || s == FulfillmentMethod.Pickup ? s : null;
    }

    /// <summary>ตรวจค่าตั้งก่อนบันทึก (เว็บ/บริการ) — ค่าตั้งต้นต้องเป็นวิธีที่รู้จัก · ข้อความ/ยอดขั้นต่ำไม่ติดลบ/ไม่ยาวเกิน</summary>
    public static List<string> ValidateSettings(string? defaultMethod, decimal? deliveryMinAmount, string? pickupNote)
    {
        var errs = new List<string>();
        if (!string.IsNullOrWhiteSpace(defaultMethod) && NormalizeOrNull(defaultMethod) == null)
            errs.Add($"วิธีรับตั้งต้น \"{defaultMethod}\" ไม่รู้จัก (ได้: delivery / pickup)");
        if (deliveryMinAmount is < 0m) errs.Add("ยอดขั้นต่ำสำหรับจัดส่งต้องไม่ติดลบ");
        if (deliveryMinAmount is decimal m && decimal.Round(m, 2) != m) errs.Add("ยอดขั้นต่ำสำหรับจัดส่งทศนิยมไม่เกิน 2 ตำแหน่ง");
        if (pickupNote?.Length > MaxNoteLength) errs.Add($"ข้อความรับเองยาวเกิน {MaxNoteLength:N0} ตัวอักษร");
        return errs;
    }

    /// <summary>ค่าตั้งต้นชี้วิธีที่ปิด ⇒ ข้อความ (เว็บ/บริการใช้ตัวเดียว) · null = ผ่าน</summary>
    public static string? DefaultPointsToDisabled(string? defaultMethod, bool deliveryEnabled, bool pickupEnabled) => NormalizeOrNull(defaultMethod) switch
    {
        FulfillmentMethod.Delivery when !deliveryEnabled => "ตั้ง “จัดส่ง” เป็นค่าตั้งต้นแต่ไม่ได้เปิดจัดส่ง",
        FulfillmentMethod.Pickup when !pickupEnabled => "ตั้ง “รับเอง” เป็นค่าตั้งต้นแต่ไม่ได้เปิดรับเอง",
        _ => null,
    };

    /// <summary>ค่าตั้งต้นที่เก็บ (null = ไม่ตั้ง)</summary>
    public static string? StoredDefault(string? raw) => NormalizeOrNull(raw);

    /// <summary>วิธีที่ใช้จริงจากคำขอ — บริการไม่มีขั้นนี้ ⇒ null (คำขอที่ส่งวิธีมา = โยน ไม่เก็บค่าที่ไม่มีผล) · ว่าง ⇒ วิธีเดียวที่เปิด / ค่าตั้งต้น ·
    /// เปิดสองแบบและไม่มีค่าตั้งต้น ⇒ โยน "เลือกจัดส่งหรือรับเอง" · ขอวิธีที่ปิด ⇒ โยน (ไม่สลับเงียบ)</summary>
    public static string? ResolveMethod(BookingFulfillmentEffective e, string? requested)
    {
        var blank = string.IsNullOrWhiteSpace(requested);
        if (!e.Enabled)
        {
            if (blank) return null;
            throw new BusinessRuleException("บริการนี้ไม่มีตัวเลือกจัดส่ง/รับเอง", RuleCode);
        }
        if (blank)
        {
            if (e.DefaultMethod != null) return e.DefaultMethod;
            if (!e.CustomerChooses) return e.Delivery ? FulfillmentMethod.Delivery : FulfillmentMethod.Pickup;
            throw new BusinessRuleException("กรุณาเลือก “จัดส่ง” หรือ “รับเอง”", RuleCode);
        }
        var m = NormalizeOrNull(requested)
            ?? throw new BusinessRuleException($"วิธีรับ \"{requested}\" ไม่รู้จัก (ได้: จัดส่ง / รับเอง)", RuleCode);
        if (m == FulfillmentMethod.Delivery && !e.Delivery)
            throw new BusinessRuleException("บริการนี้ไม่มีบริการจัดส่ง — เลือก “รับเอง”", RuleCode);
        if (m == FulfillmentMethod.Pickup && !e.Pickup)
            throw new BusinessRuleException("บริการนี้ไม่เปิดให้รับเอง — เลือก “จัดส่ง”", RuleCode);
        return m;
    }

    /// <summary>ที่อยู่จัดส่งครบไหม (เฉพาะวิธีจัดส่ง) — ที่อยู่ · จังหวัด · รหัสไปรษณีย์ 5 หลัก · ว่าง = ผ่าน</summary>
    public static List<string> AddressProblems(string? method, string? address, string? province, string? postalCode)
    {
        var errs = new List<string>();
        if (method != FulfillmentMethod.Delivery) return errs;
        var a = (address ?? "").Trim();
        if (a.Length < 5) errs.Add("กรุณากรอกที่อยู่จัดส่ง (บ้านเลขที่ ถนน ตำบล อำเภอ)");
        if (a.Length > MaxAddressLength) errs.Add($"ที่อยู่จัดส่งยาวเกิน {MaxAddressLength:N0} ตัวอักษร");
        if (string.IsNullOrWhiteSpace(province)) errs.Add("กรุณากรอกจังหวัดของที่อยู่จัดส่ง");
        var pc = (postalCode ?? "").Trim();
        if (pc.Length != 5 || !pc.All(char.IsAsciiDigit)) errs.Add("รหัสไปรษณีย์ต้องเป็นตัวเลข 5 หลัก");
        return errs;
    }

    /// <summary>ยอดขั้นต่ำสำหรับจัดส่ง (ค่าบริการ) — ไม่ถึง ⇒ ข้อความพร้อมทางไปต่อ · null = ผ่าน</summary>
    public static string? MinAmountProblem(string? method, decimal serviceTotal, decimal? minAmount, bool pickupAvailable)
        => method == FulfillmentMethod.Delivery && minAmount is decimal min && min > 0m && R2(serviceTotal) < min
            ? $"จัดส่งได้เมื่อยอดค่าบริการตั้งแต่ {Baht(min)} (ตอนนี้ {Baht(R2(serviceTotal))})" + (pickupAvailable ? " — เลือก “รับเอง” แทนได้" : "")
            : null;

    /// <summary>ยอดที่ลูกค้าต้องจ่ายทั้งหมด = ค่าบริการ + ค่าจัดส่ง (AwayFromZero)</summary>
    public static decimal GrandTotal(decimal serviceTotal, decimal deliveryFee)
        => R2(Math.Max(0m, serviceTotal) + Math.Max(0m, deliveryFee));

    /// <summary>ยอดล่วงหน้าที่รับ ⇒ ส่วนค่าบริการ (ไม่เกินค่าบริการ) + ส่วนค่าจัดส่ง (ส่วนเกิน — ชำระเต็มจำนวนที่รวมค่าส่ง) · ใช้แยกบรรทัดใบรับมัดจำ</summary>
    public static BookingPrepaySplit SplitPrepayment(decimal prepaid, decimal serviceTotal, decimal deliveryFee)
    {
        var p = R2(Math.Max(0m, prepaid));
        var svc = Math.Min(p, R2(Math.Max(0m, serviceTotal)));
        var del = Math.Min(R2(p - svc), R2(Math.Max(0m, deliveryFee)));
        return new BookingPrepaySplit(svc, del);
    }

    /// <summary>สรุปผลที่มีจริง (หน้าตั้งค่า/ฟอร์มบริการ)</summary>
    public static string Describe(BookingFulfillmentEffective e)
    {
        var src = e.FromService ? "กำหนดเองที่บริการ" : "ตามค่าเว็บ";
        if (!e.Enabled) return $"ไม่มีขั้นจัดส่ง/รับเอง ({src})";
        var both = e.CustomerChooses ? "ลูกค้าเลือก จัดส่ง หรือ รับเอง" : e.Delivery ? "จัดส่งเท่านั้น" : "รับเองเท่านั้น";
        var def = e.CustomerChooses && e.DefaultMethod != null ? $" · เลือกไว้ก่อน: {Label(e.DefaultMethod)}" : "";
        return $"{both}{def} ({src})";
    }

    /// <summary>ค่าที่ตั้งบนบริการ ("Inherit"/"Custom") — null ทั้งสามช่อง = ตามเว็บ</summary>
    public static string ServiceMode(bool? delivery, bool? pickup, string? def)
        => delivery == null && pickup == null && def == null ? "Inherit" : "Custom";

    /// <summary>ป้ายวิธีรับ — ตัวตั้งเดียวกับคำสั่งซื้อ (<see cref="FulfillmentMethod.Label"/>) · null = ""</summary>
    public static string Label(string? method) => method == null ? "" : FulfillmentMethod.Label(method);

    /// <summary>ข้อความวิธีรับบนใบยืนยัน/ข้อความถึงลูกค้า/หน้าพนักงาน — null = การจองไม่มีขั้นนี้</summary>
    public static string? SummaryLine(string? method, decimal deliveryFee, string? address, string? province, string? postalCode, string? pickupNote)
    {
        if (method == FulfillmentMethod.Delivery)
        {
            var where = string.Join(" ", new[] { address?.Trim(), province?.Trim(), postalCode?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return $"{Label(method)} ({(deliveryFee > 0m ? "ค่าจัดส่ง " + Baht(deliveryFee) : "ส่งฟรี")}) ถึง: {where}";
        }
        if (method == FulfillmentMethod.Pickup)
            return Label(method) + (string.IsNullOrWhiteSpace(pickupNote) ? "" : $" — {pickupNote.Trim()}");
        return null;
    }
}
