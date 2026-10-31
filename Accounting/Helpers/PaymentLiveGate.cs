using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// <b>ด่านเดียวของ "เปิดรับเงินจริง" (test → live)</b> ของผู้ให้บริการรับชำระเงิน — ใช้ร่วมทั้งร้าน (<c>PaymentSettingsController</c>)
/// และแพลตฟอร์ม NextAcc (<c>AdminPlatformGatewayController</c>) ผ่าน <c>IPaymentProviderConfigService</c> ตัวเดียว · pure · รอบ 219 ส่วน B (คำตัดสินข้อ 179)
/// <para><b>ที่มา</b> (PLATFORM_GATEWAY_REFUND_POLICY_DESIGN §1.1): ด่านเดิมดูแค่ "เคยทดสอบผ่าน" — แต่การทดสอบใช้คีย์ของโหมดปัจจุบัน (ทดสอบ) ⇒
/// <b>คีย์ live ไม่เคยถูกตรวจ</b>ก่อนสลับ · ใส่คีย์ live ผิด = ลูกค้าจ่ายไม่ได้ทันทีหลังเปิด</para>
/// <list type="bullet">
/// <item>ทุกฝั่ง: ทดสอบผ่าน (คีย์ทดสอบ + webhook มาถึงจริง) <b>หลังการแก้คีย์ครั้งล่าสุด</b> (แก้คีย์ = ล้างผล) · มีคีย์ live ครบคู่ · คีย์ live ผ่านการตรวจกับผู้ให้บริการ</item>
/// <item>แพลตฟอร์มเพิ่ม: เผยแพร่นโยบายการยกเลิกและคืนเงินฉบับของตัวเองแล้ว (เงื่อนไขร้านค้าของผู้ให้บริการ — ต้องยืนยันกับสัญญา) · พิมพ์ยืนยัน <see cref="ConfirmPhrase"/></item>
/// <item>ถอยกลับโหมดทดสอบ/ปิดฉุกเฉิน ทำได้เสมอ (ห้ามขวางการถอย)</item>
/// </list>
/// </summary>
public static class PaymentLiveGate
{
    public const string RuleCode = "PAY-LIVE-GATE";
    /// <summary>คำที่แอดมินแพลตฟอร์มต้องพิมพ์ก่อนเปิดรับเงินจริง</summary>
    public const string ConfirmPhrase = "เปิดรับเงินจริง";

    /// <summary>ข้อเท็จจริงของ config ที่ด่านต้องใช้ (แยกจาก entity ⇒ เทสต์ได้)</summary>
    public sealed record Facts(DateTime? LastTestPassedAt, DateTime? LiveKeyVerifiedAt, bool HasLivePublicKey, bool LiveSecretUsable);

    /// <summary>เหตุที่ยังเปิด live ไม่ได้ (ทุกข้อที่ขาด · ไทย) — ว่าง = ผ่าน</summary>
    public static List<string> Missing(Facts f)
    {
        var m = new List<string>();
        if (f.LastTestPassedAt == null)
            m.Add("ยังไม่ผ่าน \"ทดสอบเชื่อมต่อ\" หลังแก้คีย์ครั้งล่าสุด — ต้องผ่านทั้งคีย์และต้องเคยได้รับการแจ้งเตือน (webhook) จากผู้ให้บริการจริง");
        if (!f.HasLivePublicKey || !f.LiveSecretUsable)
            m.Add("ยังไม่ได้กรอกคีย์สำหรับโหมดใช้งานจริงให้ครบทั้งคู่");
        else if (f.LiveKeyVerifiedAt == null)
            m.Add("คีย์โหมดใช้งานจริงยังไม่ผ่านการตรวจกับผู้ให้บริการ — กด \"ทดสอบเชื่อมต่อ\" อีกครั้งหลังกรอกคีย์ live");
        return m;
    }

    public static bool CanEnableLive(Facts f) => Missing(f).Count == 0;

    /// <summary>ด่านของร้าน — null = ผ่าน</summary>
    public static string? Refusal(Facts f)
    {
        var m = Missing(f);
        return m.Count == 0 ? null : string.Join(" · ", m);
    }

    /// <summary>ด่านของแพลตฟอร์ม NextAcc — ด่านร้านทุกข้อ + นโยบายคืนเงินที่เผยแพร่แล้ว + คำยืนยันที่พิมพ์ · null = ผ่าน</summary>
    public static string? PlatformRefusal(Facts f, bool refundPolicyPublished, string? typedConfirmation)
    {
        var m = Missing(f);
        if (!refundPolicyPublished)
            m.Add("ยังไม่ได้เผยแพร่ \"นโยบายการยกเลิกและคืนเงิน\" ฉบับของ NextAcc (ผู้ดูแลระบบ → เอกสารกฎหมาย) — ผู้ให้บริการรับชำระเงินกำหนดให้เว็บต้องแสดงนโยบายนี้");
        if (!string.Equals(typedConfirmation?.Trim(), ConfirmPhrase, StringComparison.Ordinal))
            m.Add($"พิมพ์คำว่า \"{ConfirmPhrase}\" เพื่อยืนยัน — หลังเปิด ลูกค้าจะถูกเรียกเก็บเงินจริง");
        return m.Count == 0 ? null : string.Join(" · ", m);
    }

    /// <summary>การทดลองจ่าย (<see cref="PaymentSourceKind.GatewaySelfTest"/>) ทำได้เฉพาะ config โหมดทดสอบ — ไม่มีเงินจริงผ่านปุ่มทดสอบ/หน้าทดสอบเด็ดขาด · null = ได้</summary>
    public static string? SelfTestRefusal(PaymentSourceKind kind, PaymentProviderMode? configMode)
        => kind != PaymentSourceKind.GatewaySelfTest ? null
            : configMode == PaymentProviderMode.Test ? null
            : "ทดลองจ่ายได้เฉพาะโหมดทดสอบ (ไม่มีเงินจริง) — กลับสู่โหมดทดสอบก่อน หรือใช้คีย์ทดสอบ";

    /// <summary>ยอดทดลองจ่าย (บาท)</summary>
    public const decimal SelfTestAmount = 20m;
}
