using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// กติกาการเปลี่ยนสถานะของ <c>PaymentIntent</c> — **ฟังก์ชันบริสุทธิ์ที่เดียวของระบบ**
///
/// ═══ ทำไมต้องแยกออกมา ═══
/// สถานะการจ่ายเงินถูกเปลี่ยนจาก <b>4 ทาง</b> ที่ไม่เห็นกัน (webhook · job กระทบยอด ·
/// คนกดยืนยัน · หน้าเว็บที่ poll) · ถ้าแต่ละทางเขียนเงื่อนไขเอง จะได้กติกา 4 ชุดที่
/// ขัดกันเองในเคสที่หายาก — เช่น webhook มาช้ากว่า poll แล้วเขียนทับสถานะที่ถูกต้องแล้ว
/// หรือคนกด "ยืนยันด้วยมือ" ทับรายการที่ provider บอกว่า Failed
///
/// ═══ กติกา ═══
/// <list type="number">
/// <item><b>สถานะปลายทางเป็นสถานะสุดท้าย</b> — <c>Succeeded</c> · <c>Refunded</c> ห้ามถอย
///   (เงินเข้าแล้วเข้าเลย) · <c>Failed</c>/<c>Expired</c> ถอยกลับไม่ได้ แต่ **เดินหน้าไป
///   Succeeded ได้** เพราะ provider บางเจ้าตัดสิน timeout ก่อนแล้วเงินเข้าทีหลังจริง ๆ
///   (ถ้าห้ามไว้ ลูกค้าจ่ายแล้วระบบไม่รับ = เคสร้องเรียนที่แก้ยากที่สุด)</item>
/// <item><b>ซ้ำ = no-op ไม่ใช่ error</b> — webhook ส่งซ้ำเป็นเรื่องปกติของทุกเจ้า
///   การ throw จะทำให้ provider retry ไม่รู้จบ</item>
/// <item><b>ถอยหลังต้องเงียบและถูกบันทึก</b> — ไม่ throw (ผู้ส่งไม่ผิด) แต่ต้องรู้ว่าเกิดขึ้น</item>
/// </list>
/// </summary>
public static class PaymentIntentPolicy
{
    /// <summary>ผลการตัดสินว่าจะรับการเปลี่ยนสถานะนี้ไหม</summary>
    public readonly record struct Decision(bool Apply, bool IsDuplicate, string? Reason)
    {
        public static Decision Accept() => new(true, false, null);
        public static Decision Duplicate() => new(false, true, null);
        public static Decision Reject(string reason) => new(false, false, reason);
    }

    /// <summary>สถานะที่ "จบแล้วในทางบวก" — เงินอยู่กับเราแล้ว</summary>
    public static bool IsSettledPositive(PaymentIntentStatus s)
        => s is PaymentIntentStatus.Succeeded
            or PaymentIntentStatus.Refunded
            or PaymentIntentStatus.PartiallyRefunded;

    /// <summary>ยังรอผลอยู่ — job กระทบยอดจะไล่ถามสถานะสดเฉพาะกลุ่มนี้</summary>
    public static bool IsOpen(PaymentIntentStatus s)
        => s is PaymentIntentStatus.Created or PaymentIntentStatus.Pending;

    public static Decision Evaluate(PaymentIntentStatus from, PaymentIntentStatus to)
    {
        if (from == to) return Decision.Duplicate();

        switch (from)
        {
            // เงินเข้าแล้ว: ไปได้แค่ทางคืนเงิน
            case PaymentIntentStatus.Succeeded:
                return to is PaymentIntentStatus.Refunded or PaymentIntentStatus.PartiallyRefunded
                    ? Decision.Accept()
                    : Decision.Reject("รายการที่ชำระสำเร็จแล้วเปลี่ยนกลับไม่ได้ — ถ้าต้องยกเลิกให้บันทึกการคืนเงิน");

            case PaymentIntentStatus.PartiallyRefunded:
                return to == PaymentIntentStatus.Refunded
                    ? Decision.Accept()
                    : Decision.Reject("คืนเงินบางส่วนแล้ว เปลี่ยนได้เฉพาะเป็นคืนเงินเต็มจำนวน");

            case PaymentIntentStatus.Refunded:
                return Decision.Reject("รายการที่คืนเงินครบแล้วเปลี่ยนสถานะไม่ได้");

            // ล้มเหลว/หมดอายุ: เดินหน้าไป Succeeded ได้ (เงินเข้าช้ากว่าที่ provider ตัดสิน)
            // แต่สลับไปมาระหว่างกันเองไม่ได้ — ไม่มีความหมายและปิดบังของจริง
            case PaymentIntentStatus.Failed:
            case PaymentIntentStatus.Expired:
                return to == PaymentIntentStatus.Succeeded
                    ? Decision.Accept()
                    : Decision.Reject("รายการที่ปิดไปแล้วเปลี่ยนได้เฉพาะเมื่อเงินเข้าจริงภายหลัง");

            // ยังเปิดอยู่: ไปได้ทุกสถานะปลายทาง แต่ห้ามถอยจาก Pending กลับ Created
            case PaymentIntentStatus.Pending:
                return to == PaymentIntentStatus.Created
                    ? Decision.Reject("ถอยกลับไปสถานะเริ่มต้นไม่ได้")
                    : Decision.Accept();

            case PaymentIntentStatus.Created:
            default:
                return Decision.Accept();
        }
    }

    /// <summary>ค่าธรรมเนียมที่ผู้ให้บริการส่งมากับ charge ควรเขียนทับ <c>FeeActual</c> ไหม (รอบ 200 ทีม G)
    ///
    /// <para>═══ ที่มา ═══ ตัวคัดค่าจาก provider ลง entity เขียน <c>FeeActual = charge.Fee</c> ทุกครั้งที่สถานะเปลี่ยน ⇒ ค่าธรรมเนียมที่ผู้ใช้
    /// <b>แก้ด้วยมือพร้อมเหตุผล + hash chain</b> (<c>PUT pay/intents/{id}/fee</c>) หรือค่าที่<b>อยู่ในใบสำคัญรอบโอนแล้ว</b> ถูกทับเงียบ ๆ
    /// เมื่อ webhook/poll รอบหลังพาสถานะใหม่มา ⇒ รายงานกระทบยอดคลาดจาก JE โดยไม่มีร่องรอย</para>
    /// <para>═══ กติกา ═══ รับค่าจากผู้ให้บริการเมื่อ (ก) ยังไม่มีค่า หรือ (ข) รายการยังเปิดอยู่ (Created/Pending — charge ที่ยังไม่สำเร็จอาจรายงาน
    /// ค่าธรรมเนียม 0 แล้วค่าจริงมาตอนสำเร็จ ต้องให้ค่าจริงทับได้) · และ<b>ไม่เคย</b>ทับเมื่อบันทึกรอบโอนแล้ว</para></summary>
    public static bool ShouldTakeProviderFee(PaymentIntentStatus currentStatus, decimal? feeActual, bool alreadySettled)
        => !alreadySettled && (feeActual == null || IsOpen(currentStatus));

    /// <summary>ค่าธรรมเนียมที่มากับ charge สถานะนี้ "เป็นค่าจริง" ไหม — ทุกเส้นที่รับ <c>charge.Fee</c> ต้องผ่านตัวนี้ก่อน
    /// (รอบ 200 ฝ่ายค้านทีม G · R200G-6)
    ///
    /// <para>═══ ที่มา ═══ charge ที่ยังรอจ่าย (pending) รายงาน <c>fee: 0</c> ⇒ เดิมเก็บเป็น <c>FeeActual = 0</c> ⇒ ถ้ารายการไปถึง "สำเร็จ" ด้วยทางที่ไม่มี
    /// ค่าธรรมเนียม (ยืนยันด้วยมือ) webhook "สำเร็จ" ที่ตามมาเป็น duplicate ซึ่งรับค่าเฉพาะเมื่อ <c>FeeActual == null</c> ⇒ ค่าจริงไม่ถูกรับ ⇒ รอบโอนยอดไม่ตรง
    /// (0 ที่ยังไม่รู้ ถูกนับเป็น "รู้แล้ว") · กติกา: รับค่าเฉพาะจาก charge ที่เงินเคลื่อนแล้ว (สำเร็จ/คืนบางส่วน/คืนเต็ม) —
    /// สถานะเปิด/ล้มเหลว/หมดอายุ = ยังไม่มีค่าธรรมเนียมจริง ⇒ <c>FeeActual</c> คงเป็น null (ใช้ตัวประมาณจนกว่าค่าจริงมา)</para></summary>
    public static bool IsProviderFeeFinal(PaymentIntentStatus chargeStatus)
        => chargeStatus is PaymentIntentStatus.Succeeded or PaymentIntentStatus.PartiallyRefunded or PaymentIntentStatus.Refunded;

    /// <summary>"ยืนยันรับเงินด้วยมือ" ทำได้ไหม — <c>null</c> = ได้ (รอบ 200 ทีม G)
    ///
    /// <para>ช่องทางที่<b>ผู้ให้บริการถือเงินไว้ก่อน</b> (ขาเงินเข้าลงบัญชีพัก 11340) แต่รายการ<b>ไม่มี charge ที่ผู้ให้บริการเลย</b>
    /// (สร้าง charge ไม่สำเร็จ — <c>ProviderRef</c> ว่าง) ⇒ เงินที่ผู้ใช้เห็นในธนาคารไม่ได้ผ่านผู้ให้บริการ · ยืนยันที่นี่ = Dr 11340 ด้วยเงินที่
    /// ผู้ให้บริการไม่มีวันโอนมา ⇒ ค้างในบัญชีพักถาวร + รอบโอนไม่ตรง · ทางไปต่อ: บันทึกรับชำระที่เอกสารต้นทางโดยตรง (ลงธนาคาร)</para></summary>
    public static string? ManualConfirmBlockReason(bool providerHoldsFunds, string? providerRef)
        => providerHoldsFunds && string.IsNullOrWhiteSpace(providerRef)
            ? "รายการนี้ไม่มีรายการชำระเงินที่ผู้ให้บริการ (สร้างไม่สำเร็จ) — เงินที่เห็นในบัญชีธนาคารไม่ได้ผ่านผู้ให้บริการ "
              + "ยืนยันที่นี่ไม่ได้ (จะลงบัญชีพักของผู้ให้บริการด้วยเงินที่ไม่มีวันถูกโอนมา) · ให้บันทึกรับชำระที่เอกสาร/ออเดอร์ต้นทางโดยตรง"
            : null;

    // ══════════════════════════════════════════════════════════════════
    //  รอบ 201 ทีม GW (A-GW2 · team-G PG-5): ป้าย/ปุ่ม/เกณฑ์ "ค้างนาน" ของหน้ารายการรับชำระ — เซิร์ฟเวอร์ตัดสิน หน้าเว็บแสดงอย่างเดียว
    //  (เดิม JS มีสำเนาเกณฑ์ 30 นาที · เงื่อนไขปุ่มคืนเงิน/แก้ค่าธรรมเนียม · และแสดงชื่อ enum อังกฤษ — F2 ข้อ 5)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>รายการที่ยังเปิดอยู่นานเกินนี้ = "ค้างนาน" — ตัวเดียวของงานเบื้องหลัง (แจ้งเตือน) และหน้ารายการ (ป้าย/แถบเตือน)</summary>
    public static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    /// <summary>ค้างนานผิดปกติไหม — ยังเปิดอยู่ (<see cref="IsOpen"/>) และสร้างมาเกิน <see cref="StuckThreshold"/></summary>
    public static bool IsStuck(PaymentIntentStatus status, DateTime createdAtUtc, DateTime nowUtc)
        => IsOpen(status) && nowUtc - createdAtUtc > StuckThreshold;

    /// <summary>ป้ายสถานะภาษาไทย (ค่าที่ไม่รู้จัก = ชื่อ enum — เพิ่มสถานะใหม่แล้วหน้าเว็บยังแสดงได้)</summary>
    public static string StatusLabel(PaymentIntentStatus status) => status switch
    {
        PaymentIntentStatus.Created => "สร้างแล้ว รอเรียกผู้ให้บริการ",
        PaymentIntentStatus.Pending => "รอชำระ",
        PaymentIntentStatus.Succeeded => "สำเร็จ",
        PaymentIntentStatus.Failed => "ล้มเหลว",
        PaymentIntentStatus.Expired => "หมดอายุ",
        PaymentIntentStatus.Refunded => "คืนเงินแล้ว",
        PaymentIntentStatus.PartiallyRefunded => "คืนเงินบางส่วน",
        _ => status.ToString(),
    };

    /// <summary>ตัวเลือกตัวกรองสถานะของหน้ารายการ — ค่า = ชื่อ enum (ที่ endpoint รับ) · ป้าย = <see cref="StatusLabel"/></summary>
    public static IReadOnlyList<(string Value, string Label)> StatusOptions()
        => Enum.GetValues<PaymentIntentStatus>().Select(s => (s.ToString(), StatusLabel(s))).ToList();

    /// <summary>ป้ายที่มาของรายการภาษาไทย</summary>
    public static string SourceKindLabel(PaymentSourceKind kind) => kind switch
    {
        PaymentSourceKind.SiteOrder => "คำสั่งซื้อหน้าเว็บ",
        PaymentSourceKind.Document => "ใบแจ้งหนี้ (portal ลูกค้า)",
        PaymentSourceKind.LodgingReservation => "มัดจำที่พัก",
        PaymentSourceKind.SubscriptionPayment => "ค่าบริการระบบ",
        PaymentSourceKind.PosOrder => "บิล POS",
        PaymentSourceKind.AddOnPurchase => "ส่วนเสริม",
        _ => kind.ToString(),
    };

    /// <summary>ปุ่ม "แก้ค่าธรรมเนียม" ใช้ได้ไหม — ด่านเดียวกับ <c>GatewaySettlementService.CorrectFeeAsync</c>: รับเงินสำเร็จแล้ว (<see cref="IsSettledPositive"/>) ·
    /// ยังไม่มีใบสำคัญรอบโอนเส้นเดิม · ยังไม่อยู่ในรอบโอน settlement (ฉบับร่างก็นับ — บรรทัดค่าธรรมเนียมถูกบันทึกไปแล้ว)</summary>
    public static bool CanEditFee(PaymentIntentStatus status, bool settledByJournal, bool inSettlementBatch)
        => IsSettledPositive(status) && !settledByJournal && !inSettlementBatch;

    /// <summary>URL ที่ผู้ให้บริการจะพาลูกค้ากลับหลังจ่าย/3-D Secure — <b>เฉพาะโดเมนของบริษัทเอง</b> (รอบ 201 ทีม GW · A-GW3 · team-G PG-6)
    ///
    /// <para>═══ ที่มา ═══ <c>ReturnUrl</c> จากทางเข้าสาธารณะ (หน้าจ่ายเงินของออเดอร์/การจอง) ถูกส่งต่อเป็น <c>return_uri</c> ของผู้ให้บริการโดยไม่จำกัดโดเมน
    /// ⇒ open redirect หลังลูกค้ายืนยันบัตร (ลิงก์จ่ายเงินจริงพาไปหน้าปลอมที่ขอข้อมูลต่อ)</para>
    /// <para>═══ กติกา ═══ URL เต็ม http(s) ที่ host อยู่ใน <paramref name="allowedHosts"/> (โดเมนเว็บไซต์ของบริษัท + โดเมนของระบบ) และไม่มีชื่อผู้ใช้/รหัสผ่านใน URL ⇒
    /// ใช้ตามเดิม · path สัมพัทธ์ (<c>/x</c> ไม่ใช่ <c>//x</c> หรือ <c>/\x</c>) ⇒ ต่อท้าย <paramref name="systemBaseUrl"/> · นอกนั้น (โดเมนอื่น · <c>//evil</c> ·
    /// <c>javascript:</c> · รูปเสีย) ⇒ หน้าแรกของระบบ (<paramref name="systemBaseUrl"/>) · ไม่มี URL ⇒ <c>null</c> (พฤติกรรมเดิม — ไม่ส่ง return_uri) ·
    /// ไม่รู้โดเมนของระบบ ⇒ <c>null</c> (ไม่แต่ง URL)</para></summary>
    public static string? SafeReturnUrl(string? url, IReadOnlyCollection<string> allowedHosts, string? systemBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        var baseUri = Uri.TryCreate(systemBaseUrl?.Trim(), UriKind.Absolute, out var b)
                      && (b.Scheme == Uri.UriSchemeHttps || b.Scheme == Uri.UriSchemeHttp) ? b : null;
        var fallback = baseUri == null ? null : baseUri.GetLeftPart(UriPartial.Authority) + "/";

        // path สัมพัทธ์ของระบบเอง — ต้องขึ้นต้น "/" ตัวเดียว (ห้าม "//host" หรือ "/\host" ที่เบราว์เซอร์ตีเป็นโดเมนอื่น)
        if (u.StartsWith('/'))
        {
            if (u.Length > 1 && (u[1] == '/' || u[1] == '\\')) return fallback;
            return baseUri == null ? null : baseUri.GetLeftPart(UriPartial.Authority) + u;
        }

        if (!Uri.TryCreate(u, UriKind.Absolute, out var abs)
            || (abs.Scheme != Uri.UriSchemeHttps && abs.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(abs.UserInfo))
            return fallback;
        var host = abs.Host;
        var allowed = allowedHosts.Any(h => !string.IsNullOrWhiteSpace(h)
                          && string.Equals(h.Trim().TrimEnd('.'), host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                      || (baseUri != null && string.Equals(baseUri.Host, host, StringComparison.OrdinalIgnoreCase));
        return allowed ? u : fallback;
    }

    /// <summary>คีย์กันสร้าง intent ซ้ำสำหรับการจ่ายครั้งเดียวกัน
    ///
    /// <para><paramref name="sequence"/> เพิ่มเมื่อครั้งก่อน **ปิดไปแล้วโดยไม่สำเร็จ**
    /// (QR หมดอายุ/บัตรถูกปฏิเสธ) — ลูกค้าต้องลองใหม่ได้ · ถ้าใช้คีย์เดิมจะติดที่ unique
    /// index แล้วกดจ่ายซ้ำไม่ได้เลย ซึ่งแย่กว่าปัญหาที่ตั้งใจกัน</para>
    ///
    /// <para>จำนวนเงินอยู่ในคีย์ด้วย เพราะยอดที่เปลี่ยน (เพิ่มรายการในตะกร้า) คือการจ่าย
    /// คนละครั้ง ไม่ใช่ครั้งเดิม</para></summary>
    public static string IdempotencyKey(PaymentSourceKind kind, Guid sourceId, decimal amount, int sequence)
        => $"{kind}:{sourceId:N}:{amount:0.00}:{sequence}";
}
