using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 235 (คำตัดสิน 235-X3 + ฝ่ายค้าน B1/S5) — <b>ตัวคัดกรองเดียวของ "เงินทดสอบที่ลงบัญชีไปแล้วก่อนรอบ 232"</b> (pure)
///
/// <para>ที่มา: ก่อนรอบ 232 (<see cref="TestModePaymentPolicy"/>) ร้านที่ทดลองจ่ายบนหน้าร้าน/ใบแจ้งหนี้ด้วยคีย์ทดสอบ ⇒ handler สร้าง JE · ใบเสร็จ ·
/// ตัดสต็อก · ตัดชำระเอกสารจริง · รอบ 232 กันของใหม่แล้ว แต่ของเก่าค้างในสมุด · คำตัดสิน: <b>ไม่กลับรายการอัตโนมัติ</b> (ห้ามแก้เงียบ — F2 ข้อ 9) ⇒
/// รายงานบนหน้า "รายการรับชำระออนไลน์" ให้ผู้มีสิทธิ์กลับรายการเองด้วยเส้นยกเลิกเดิมของระบบ</para>
///
/// <para><b>หลักฐานว่าเป็นเงินทดสอบ (ฝ่ายค้าน B1):</b> แถวก่อนมีคอลัมน์ (<c>IsTestMode == null</c>) ตัดสินจากโหมด<b>ปัจจุบัน</b>ของช่องทาง — ถ้ารับเงินตอน Live
/// แล้วสลับกลับเป็น Test ภายหลัง แถวเงินจริงจะดูเหมือนทดสอบ ⇒ ปุ่มกลับรายการ (void เงินจริง) ได้เฉพาะเมื่อมีหลักฐานที่ไม่ขึ้นกับค่าตั้งปัจจุบัน:
/// (ก) ประทับ <c>IsTestMode = true</c> ตอนสร้าง หรือ (ข) เลขอ้างอิงผู้ให้บริการบอกว่าเป็นรายการทดสอบ (<see cref="IsProviderTestRef"/>) ·
/// มีแค่โหมดของ config ⇒ แสดงในรายงาน<b>ไม่มีปุ่ม</b> + ข้อความ <see cref="UnprovenMessage"/> และห้ามยืนยันว่า "เงินไม่ได้เข้าจริง"</para>
/// </summary>
public static class TestModeLegacyEffectPolicy
{
    public const string RuleCode = "PAY-TEST-LEGACY-EFFECT";
    public const string ReverseRuleCode = "PAY-TEST-LEGACY-REVERSE";

    /// <summary>ฝ่ายค้าน S5: ขอบเวลาของรายงาน — รายการที่สร้าง<b>หลัง</b>วันนี้ผ่านด่าน <see cref="TestModePaymentPolicy"/> แล้ว (ไม่มีผล) จึงไม่ต้องสแกน ·
    /// รอบ 232 คอมมิต b4780a33 เมื่อ 2026-11-03 11:51 UTC — ตั้งเผื่อช่วงรอ deploy ไว้ถึง 2026-11-10 00:00 UTC (เลยไปก็แค่สแกนมากขึ้น: รายการที่ไม่มีผลถูกตัดอยู่แล้ว) ·
    /// ถ้า deploy รอบ 232 จริงหลังวันนี้ ให้เลื่อนค่าคงที่นี้</summary>
    public static readonly DateTime GuardSinceUtc = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>จำนวนรายการสูงสุดต่อรายงาน (เกิน ⇒ <c>truncated</c>)</summary>
    public const int MaxRows = 2000;

    /// <summary>ชนิดของ "ผลทางบัญชี" ที่พบ</summary>
    public enum EffectKind
    {
        /// <summary>แถว <c>Payment</c> (ตัดชำระเอกสาร + JE รับเงิน) — กลับรายการได้ในหน้านี้ผ่าน <c>VoidPaymentAsync</c> เดิม</summary>
        DocumentPayment = 1,
        /// <summary>คำสั่งซื้อหน้าร้านถูกยืนยันการชำระ (เอกสารขาย/สต็อก/JE ผ่านเส้นยืนยันคำสั่งซื้อ)</summary>
        SiteOrderPayment = 2,
        /// <summary>บิล POS ปิดด้วยเงินก้อนนี้ (JE ขาย + ตัดสต็อก)</summary>
        PosPayment = 3,
        /// <summary>มัดจำที่พักถูกยืนยัน (ใบมัดจำ/JE)</summary>
        LodgingDeposit = 4,
        /// <summary>มัดจำการจองนัดหมายถูกบันทึก (ใบมัดจำ/JE)</summary>
        BookingDeposit = 5,
    }

    /// <summary>หลักฐานว่าเป็นเงินทดสอบ</summary>
    public enum TestEvidence
    {
        /// <summary>ไม่ใช่ทดสอบ (ประทับ false หรือไม่มีหลักฐานใด)</summary>
        None = 0,
        /// <summary>ประทับ <c>IsTestMode = true</c> ตอนสร้าง</summary>
        Stamped = 1,
        /// <summary>เลขอ้างอิงผู้ให้บริการเป็นรายการทดสอบ</summary>
        ProviderTestRef = 2,
        /// <summary>มีแค่โหมด<b>ปัจจุบัน</b>ของช่องทาง = Test (แถวก่อนมีคอลัมน์) — พิสูจน์ไม่ได้</summary>
        ConfigOnly = 3,
    }

    /// <summary>Omise: charge ของคีย์ทดสอบขึ้นต้น <c>chrg_test_</c> (รูปเดียวกับ <c>evnt_test_</c> ของ webhook — <c>OmisePaymentProvider</c>) ·
    /// ผู้ให้บริการอื่น/ไม่มีเลข = ไม่มีหลักฐาน</summary>
    private static bool IsProviderTestRef(string? providerCode, string? providerRef)
        => string.Equals(providerCode, "omise", StringComparison.OrdinalIgnoreCase)
           && providerRef is not null && providerRef.StartsWith(OmiseTestChargePrefix, StringComparison.Ordinal);

    /// <summary>คำนำหน้าเลข charge ทดสอบของ Omise — ตัวตั้งเดียว (ผู้โหลดใช้กรองฝั่ง SQL · ตัดสินจริงที่ <see cref="Evidence"/>)</summary>
    public const string OmiseTestChargePrefix = "chrg_test_";

    /// <summary>ตัวตัดสินหลักฐานเดียว — <paramref name="resolvedTest"/> = ผลของ <c>PaymentIntentTestMode.Decide(stamped, configMode)</c></summary>
    public static TestEvidence Evidence(bool? stamped, bool resolvedTest, string? providerCode, string? providerRef)
    {
        if (stamped == true) return TestEvidence.Stamped;
        if (stamped == false) return TestEvidence.None;
        if (IsProviderTestRef(providerCode, providerRef)) return TestEvidence.ProviderTestRef;
        return resolvedTest ? TestEvidence.ConfigOnly : TestEvidence.None;
    }

    /// <summary>มีหลักฐานที่ไม่ขึ้นกับค่าตั้งปัจจุบัน (พอจะ void ได้)</summary>
    public static bool IsProven(TestEvidence e) => e is TestEvidence.Stamped or TestEvidence.ProviderTestRef;

    /// <summary>ต้นทางของร้าน (ไม่ใช่เงินแพลตฟอร์ม/ทดลองจ่ายของระบบ)</summary>
    public static bool IsStoreSource(PaymentSourceKind kind) => kind is PaymentSourceKind.SiteOrder or PaymentSourceKind.Document
        or PaymentSourceKind.LodgingReservation or PaymentSourceKind.PosOrder or PaymentSourceKind.SiteBooking;

    /// <summary>intent นี้เข้าข่ายตรวจหาผลค้างไหม (ยังไม่ดูผล)</summary>
    public static bool IsCandidate(PaymentSourceKind kind, PaymentIntentStatus status, TestEvidence evidence, DateTime createdAtUtc)
        => evidence != TestEvidence.None && IsStoreSource(kind) && PaymentIntentPolicy.IsSettledPositive(status) && createdAtUtc < GuardSinceUtc;

    /// <summary>ขึ้นรายงานเมื่อเข้าข่าย + พบผลอย่างน้อยหนึ่งชิ้น</summary>
    public static bool ShouldReport(PaymentSourceKind kind, PaymentIntentStatus status, TestEvidence evidence, DateTime createdAtUtc, int effectCount)
        => IsCandidate(kind, status, evidence, createdAtUtc) && effectCount > 0;

    /// <summary>กลับรายการในหน้านี้ได้ไหม — เฉพาะการรับชำระเอกสาร (เส้น <c>VoidPaymentAsync</c>) <b>และ</b>มีหลักฐานว่าเป็นทดสอบจริง ·
    /// ชนิดอื่นผลกระจายหลายชิ้น ⇒ ต้องใช้เส้นยกเลิก/ใบลดหนี้ของต้นทางนั้น (ลิงก์) — ห้ามลบ JE ตรง</summary>
    public static bool CanReverseHere(EffectKind kind, TestEvidence evidence) => kind == EffectKind.DocumentPayment && IsProven(evidence);

    public const string UnprovenMessage =
        "พิสูจน์ไม่ได้ว่าเป็นเงินทดสอบ (รายการก่อนระบบบันทึกโหมด) — ตรวจกับแดชบอร์ดผู้ให้บริการก่อน แล้วกลับรายการที่เอกสารต้นทาง";

    /// <summary>ป้ายหลักฐานที่ผู้ใช้เห็น — ประโยค "ไม่มีเงินจริง" เฉพาะแถวที่พิสูจน์ได้</summary>
    public static string EvidenceLabel(TestEvidence e) => e switch
    {
        TestEvidence.Stamped => "ประทับโหมดทดสอบตอนสร้างรายการ — ไม่มีเงินจริง",
        TestEvidence.ProviderTestRef => "เลขอ้างอิงผู้ให้บริการเป็นรายการทดสอบ — ไม่มีเงินจริง",
        TestEvidence.ConfigOnly => UnprovenMessage,
        _ => "",
    };

    public static string Label(EffectKind kind) => kind switch
    {
        EffectKind.DocumentPayment => "รับชำระเอกสาร (JE รับเงิน + ตัดยอดค้าง)",
        EffectKind.SiteOrderPayment => "ยืนยันชำระคำสั่งซื้อหน้าร้าน (เอกสารขาย/สต็อก/JE)",
        EffectKind.PosPayment => "ปิดบิล POS (JE ขาย + ตัดสต็อก)",
        EffectKind.LodgingDeposit => "ยืนยันมัดจำที่พัก (ใบมัดจำ/JE)",
        EffectKind.BookingDeposit => "บันทึกมัดจำการจองนัดหมาย (ใบมัดจำ/JE)",
        _ => kind.ToString(),
    };

    /// <summary>ทางไปต่อของผู้ใช้ต่อชนิด (ข้อความไทย · เซิร์ฟเวอร์ส่ง หน้าเว็บแค่แสดง) — หลักฐานไม่พอ ⇒ <see cref="UnprovenMessage"/></summary>
    public static string ReverseHint(EffectKind kind, TestEvidence evidence)
    {
        if (!IsProven(evidence)) return UnprovenMessage;
        return kind switch
        {
            EffectKind.DocumentPayment => "กด \"กลับรายการ\" — ยกเลิกการรับชำระด้วยเส้นเดิม (กลับ JE รับเงิน · คืนยอดค้างให้ใบ · audit)",
            EffectKind.SiteOrderPayment => "เปิดคำสั่งซื้อ แล้วยกเลิก/ออกใบลดหนี้ด้วยเส้นของคำสั่งซื้อ (คืนสต็อก · ยกเลิกเอกสารขาย)",
            EffectKind.PosPayment => "เปิดบิล POS แล้วยกเลิกบิล/คืนสินค้าด้วยเส้นของ POS",
            EffectKind.LodgingDeposit => "เปิดการจองที่พัก แล้วยกเลิกมัดจำ/ออกใบลดหนี้ด้วยเส้นของที่พัก",
            EffectKind.BookingDeposit => "เปิดการจองนัดหมาย แล้วยกเลิกมัดจำด้วยเส้นของการจอง",
            _ => "",
        };
    }

    /// <summary>เหตุผลที่ปุ่มกลับรายการใช้ไม่ได้ (null = ทำได้) — ตัดสินซ้ำฝั่งเซิร์ฟเวอร์ตอนกด ด้วยกติกาเดียวกับรายงาน</summary>
    public static string? ReverseRefusal(bool isCandidate, bool effectBelongsToIntent, EffectKind? kind, TestEvidence evidence)
    {
        if (!isCandidate) return "รายการนี้ไม่ใช่เงินโหมดทดสอบที่ชำระสำเร็จของร้าน (ก่อนรอบ 232) — กลับรายการจากหน้านี้ไม่ได้";
        if (!IsProven(evidence)) return UnprovenMessage;
        if (!effectBelongsToIntent || kind == null) return "ไม่พบผลทางบัญชีนี้ของรายการทดสอบ — โหลดรายงานใหม่";
        if (!CanReverseHere(kind.Value, evidence)) return ReverseHint(kind.Value, evidence);
        return null;
    }
}
