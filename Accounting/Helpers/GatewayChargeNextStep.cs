using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 — <b>"ขั้นถัดไปของผู้จ่าย"</b> หลังผู้ให้บริการสร้าง charge แล้ว (ตัวตัดสินเดียว · pure):
/// ลูกค้าต้อง <b>เห็น QR ในหน้าเดิม</b> หรือ <b>ถูกพาออกไปหน้าธนาคาร</b> — อย่างใดอย่างหนึ่ง ห้ามทั้งสอง ห้ามไม่มีเลย
///
/// <para><b>ที่มา (อาการจริง 2026-11-03):</b> หน้าทดลองจ่าย ฿20 กดพร้อมเพย์แล้ว "เด้งกลับหน้าเดิม" — ผู้ให้บริการคืน charge ที่มี<b>ทั้ง</b>
/// <c>source.scannable_code</c> (QR · flow offline) <b>และ</b> <c>authorize_uri</c> (หน้า offsite ที่ใช้กับ flow redirect) · adapter เก็บทั้งคู่ ·
/// หน้าจ่ายเลือก redirect ก่อน QR ⇒ เบราว์เซอร์ออกไปหน้า offsite ซึ่งไม่มีอะไรให้ทำกับ charge offline แล้วส่งกลับ <c>return_uri</c> ทันที
/// (ก่อนรอบ 221 = app.nextacc.com/hugedomains · หลังรอบ 221 = หน้าเดิม) — ผู้ใช้ไม่เห็น QR เลย</para>
///
/// <para><b>กติกา:</b> QR มา ⇒ QR ชนะเสมอ (ไม่พาออกจากหน้า) · ไม่มี QR แต่มีลิงก์ https ⇒ พาไป · ไม่มีทั้งคู่ทั้งที่ charge ยังรอ ⇒
/// <see cref="MissingReason"/> คืนข้อความไทย (ผู้เรียกปิดรายการเป็นล้มเหลวพร้อมเหตุผล — ไม่ปล่อยให้ผู้จ่ายนั่งรอ "กำลังรอการชำระเงิน…" กับหน้าว่าง)
/// · สลิปโอน (<see cref="PaymentMethodKind.ManualSlip"/>) และบัตร (<see cref="PaymentMethodKind.Card"/> — รอผลธนาคาร) ไม่มีขั้นถัดไปที่ผู้ให้บริการต้องส่ง ⇒ ไม่ตัดสิน</para>
/// </summary>
public static class GatewayChargeNextStep
{
    public const string MissingCode = "no_next_step";

    /// <summary>ผลการตัดสิน: ค่าที่ต้องเก็บลง intent (QR กับลิงก์ไม่มีพร้อมกัน)</summary>
    public readonly record struct Step(string? QrPayload, string? AuthorizeUrl);

    /// <summary>เลือกขั้นถัดไปจากสิ่งที่ผู้ให้บริการส่งมา — QR ชนะลิงก์ · ลิงก์ต้องเป็น https (หน้าจ่ายไม่ตาม http/javascript: อยู่แล้ว —
    /// ตัดที่นี่ให้ intent ไม่เก็บค่าที่ไม่มีวันใช้)</summary>
    public static Step Decide(string? qrPayload, string? authorizeUri)
    {
        var qr = string.IsNullOrWhiteSpace(qrPayload) ? null : qrPayload.Trim();
        var link = IsHttps(authorizeUri) ? authorizeUri!.Trim() : null;
        return qr != null ? new Step(qr, null) : new Step(null, link);
    }

    /// <summary>charge ยังรอ (Pending) แต่ไม่มีทั้ง QR และลิงก์ ⇒ เหตุผลภาษาไทย · null = มีขั้นถัดไป/ไม่ต้องมี ·
    /// Succeeded/Failed/Expired/Refunded = null เสมอ (บัตรไม่ 3-D Secure สำเร็จทันทีโดยไม่มี QR/ลิงก์ — ฝ่ายค้าน 232 B1) ·
    /// <b>ตั้งใจ</b>ให้เรียกเฉพาะตอนสร้าง (<c>StartAsync</c>) — ตอน poll/Refresh ไม่ตัดสิน (N1): charge ที่ pending อยู่เคยมี QR แล้ว</summary>
    public static string? MissingReason(PaymentMethodKind method, PaymentIntentStatus status, string? qrPayload, string? authorizeUrl)
    {
        // บัตร: ผู้จ่ายกรอกบัตรไปแล้ว — Pending ที่ไม่มีลิงก์ 3-D Secure = รอผลจากธนาคาร (webhook/poll ตัดสินต่อ) ไม่ใช่ "ไม่มีทางจ่าย"
        // (CI รอบ 232: Db test ตัดบัตรที่บันทึกไว้แล้วรอผล ถูกปิดเป็น Failed ผิด) · ต้องมีขั้นถัดไปเฉพาะ QR/วิธีที่พาไปหน้าธนาคาร
        if (!NeedsPayerStep(method) || status != PaymentIntentStatus.Pending) return null;
        if (!string.IsNullOrWhiteSpace(qrPayload) || IsHttps(authorizeUrl)) return null;
        return method == PaymentMethodKind.PromptPay
            ? "ผู้ให้บริการสร้างรายการแล้วแต่ไม่ส่ง QR พร้อมเพย์กลับมา — ลองใหม่อีกครั้ง ถ้ายังเป็นอยู่ให้ผู้ดูแลระบบตรวจว่าบัญชีผู้ให้บริการเปิดใช้พร้อมเพย์แล้ว"
            : "ผู้ให้บริการสร้างรายการแล้วแต่ไม่ส่งลิงก์หน้าชำระเงินกลับมา — ลองใหม่อีกครั้ง หรือเลือกวิธีชำระอื่น";
    }

    /// <summary>ข้อความไทยของความล้มเหลวระดับการเชื่อมต่อ (หมดเวลา · เครือข่าย · ผู้เรียกยกเลิก) — เดิมผู้จ่ายเห็นข้อความอังกฤษของ .NET
    /// ("The request was canceled due to the configured HttpClient.Timeout…") · ข้อผิดพลาดเชิงธุรกิจ (ข้อความไทยจาก adapter) คงเดิม</summary>
    public static string TransportFailureText(Exception ex, bool callerCancelled)
    {
        if (ex is OperationCanceledException && callerCancelled) return "ผู้ใช้ยกเลิกคำขอก่อนผู้ให้บริการตอบ";
        if (ex is OperationCanceledException or TimeoutException)
            return "ผู้ให้บริการรับชำระเงินไม่ตอบภายในเวลาที่กำหนด — ลองใหม่อีกครั้ง (ยังไม่มีการตัดเงิน)";
        if (ex is HttpRequestException)
            return "เชื่อมต่อผู้ให้บริการรับชำระเงินไม่ได้ (เครือข่าย/DNS/ใบรับรอง) — ตรวจว่าเซิร์ฟเวอร์ออกอินเทอร์เน็ตได้ แล้วลองใหม่";
        return string.IsNullOrWhiteSpace(ex.Message) ? "เริ่มการชำระเงินไม่สำเร็จ" : ex.Message;
    }

    /// <summary>วิธีที่ผู้จ่ายต้องทำขั้นถัดไปเอง (สแกน QR / ไปหน้าธนาคาร) — บัตรกับสลิปโอนไม่ต้อง</summary>
    public static bool NeedsPayerStep(PaymentMethodKind method)
        => method is not (PaymentMethodKind.Card or PaymentMethodKind.ManualSlip);

    private static bool IsHttps(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps;
}
