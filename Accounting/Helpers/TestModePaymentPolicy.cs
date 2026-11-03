using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 232 (คำตัดสิน 232-Omise-2) — <b>เงินโหมดทดสอบไม่มีผลทางธุรกิจ</b> (ตัวตัดสินเดียว · pure + ด่าน async สำหรับ handler):
/// intent ที่สร้างด้วยคีย์ทดสอบ (<c>IsTestMode</c> / config.Mode=Test) เมื่อ charge สำเร็จ ⇒ <b>ห้าม</b> สร้าง JE · ออกใบเสร็จ/ใบกำกับ ·
/// ตัดสต็อก · ตัดชำระเอกสาร · ยืนยันออเดอร์/การจอง — intent คง <c>Succeeded</c> (ตามที่ผู้ให้บริการตอบ) แต่ป้าย "ทดสอบ — ไม่ลงบัญชี" และ
/// ต้นทาง (ออเดอร์/ใบ/การจอง/บิล) <b>คงรอชำระ</b> พร้อมเหตุการณ์บน intent (ทางที่สต็อก/เงินไม่ขยับ) ·
/// ยกเว้น <see cref="PaymentSourceKind.GatewaySelfTest"/> ที่ handler แค่ประทับ <c>LastSelfTestPaidAt</c> (ไม่มีผลบัญชีอยู่แล้ว)
///
/// <para><b>ที่มา:</b> ก่อนรอบนี้มีด่านโหมดทดสอบเฉพาะรายได้แพลตฟอร์ม (<c>PlatformRevenueSource.EntitlementBlockReason</c>) — ร้านทดลองจ่ายบน
/// storefront ด้วยคีย์ทดสอบแล้ว JE/ใบเสร็จ/สต็อกขยับจริง</para>
/// </summary>
public static class TestModePaymentPolicy
{
    public const string RuleCode = "PAY-TEST-MODE-NO-EFFECT";
    // ── ย้ายจาก GatewayTestPaymentRules (รอบ 219 B2) — ฝ่ายค้าน 232 S2: ตัวตั้งเดียวของ "เงินทดสอบ" ทั้งขาเข้าและขาคืน ──
    public const string RefundRuleCode = "PAY-TEST-NO-LEDGER";
    public const string SelfTestRefundMessage =
        "รายการทดลองจ่าย (โหมดทดสอบ) คืนเงินผ่านระบบไม่ได้ — ไม่มีเงินจริงเข้า และระบบไม่ลงบัญชีรายการนี้";
    public const string TestModeNotBookedNote = "โหมดทดสอบ — ไม่ลงบัญชี (ไม่มีเงินจริง)";

    /// <summary>ทดลองจ่ายของระบบคืนเงินผ่านระบบไม่ได้ (ไม่มีเงินจริง) · ต้นทางอื่น = null</summary>
    public static string? RefundRefusal(PaymentSourceKind kind)
        => kind == PaymentSourceKind.GatewaySelfTest ? SelfTestRefundMessage : null;

    /// <summary>"ไม่มีเงินจริง" = ไม่ลง JE ทั้งขาเข้าและขาคืน — ทดลองจ่ายของระบบ หรือ intent โหมดทดสอบ (ผู้เรียก resolve โหมดด้วย
    /// <see cref="PaymentIntentTestMode.ResolveAsync"/> — null→config เหมือน dispatcher) · ต่างจาก <see cref="AllowsBusinessEffects"/> ตรงที่
    /// ทดลองจ่ายของระบบ "ไม่มีเงิน" แต่ยัง "มีผล" (ประทับ LastSelfTestPaidAt)</summary>
    public static bool IsNoMoney(PaymentSourceKind kind, bool isTestMode)
        => kind == PaymentSourceKind.GatewaySelfTest || isTestMode;
    /// <summary>คำนำหน้าเหตุการณ์บน intent (ไม่ใช่ <c>PaymentRedispatchPolicy.UnfulfilledNotePrefix</c> — ไม่ใช่งานค้างที่ต้อง "ส่งต่อใหม่")</summary>
    public const string NotePrefix = "🧪 โหมดทดสอบ — ไม่ลงบัญชี";

    /// <summary>true = handler ทำงานตามปกติ (เงินจริง หรือทดลองจ่ายของระบบ) · false = ห้ามมีผลทางธุรกิจ</summary>
    public static bool AllowsBusinessEffects(bool isTestMode, PaymentSourceKind sourceKind)
        => !isTestMode || sourceKind == PaymentSourceKind.GatewaySelfTest;

    /// <summary>ข้อความเหตุการณ์/ข้อผิดพลาดเมื่อถูกกัน (ภาษาไทย · บอกทางไปต่อ)</summary>
    public static string BlockMessage(PaymentSourceKind sourceKind)
        => $"{NotePrefix}: รายการ{PaymentIntentPolicy.SourceKindLabel(sourceKind)}นี้ชำระด้วยคีย์ทดสอบ — ไม่สร้าง JE/ใบเสร็จ ไม่ตัดสต็อก ไม่ตัดชำระต้นทาง "
           + "(ต้นทางคงรอชำระ) · จะมีผลจริงเมื่อสลับช่องทางเป็นโหมดใช้งานจริงแล้วชำระใหม่";

    /// <summary>ป้ายสถานะที่ผู้ใช้เห็น — ทดสอบ+สำเร็จ ⇒ "สำเร็จ (ทดสอบ — ไม่ลงบัญชี)" · อื่น ๆ = ป้ายเดิม</summary>
    public static string StatusLabel(PaymentIntentStatus status, bool isTestMode)
    {
        var baseLabel = PaymentIntentPolicy.StatusLabel(status);
        return isTestMode && PaymentIntentPolicy.IsSettledPositive(status) ? $"{baseLabel} (ทดสอบ — ไม่ลงบัญชี)" : baseLabel;
    }

    /// <summary>หมายเหตุถึงผู้จ่ายบนหน้าจ่าย (รอบ 232 S3 · ส่งจากเซิร์ฟเวอร์) — เงินทดสอบสำเร็จ ⇒ บอกว่าไม่มีผลทางบัญชี/ต้นทางยังรอชำระจริง ·
    /// ทดลองจ่ายของระบบ ⇒ ไม่มีต้นทาง · อื่น ๆ = null</summary>
    public static string? PayerNote(PaymentIntentStatus status, bool isTestMode, PaymentSourceKind sourceKind)
    {
        if (!isTestMode || !PaymentIntentPolicy.IsSettledPositive(status)) return null;
        return sourceKind == PaymentSourceKind.GatewaySelfTest
            ? "โหมดทดสอบ: ไม่มีเงินจริงและไม่ลงบัญชี"
            : "โหมดทดสอบ: ไม่มีผลทางบัญชี — รายการต้นทางยังรอชำระจริง";
    }

    /// <summary>ด่านชั้นสองใน handler ทุกตัว (dispatcher กันชั้นแรก) — ถูกเรียกตรงจากทางอื่น (ส่งต่อใหม่/เทสต์/โค้ดใหม่) ก็ยังไม่มีผลทางธุรกิจ ·
    /// โยน <see cref="BusinessRuleException"/> ข้อความไทย</summary>
    public static async Task GuardAsync(AccountingDbContext db, PaymentIntent intent, CancellationToken ct)
    {
        var isTest = await PaymentIntentTestMode.ResolveAsync(db, intent, ct);
        if (!AllowsBusinessEffects(isTest, intent.SourceKind))
            throw new BusinessRuleException(BlockMessage(intent.SourceKind), RuleCode);
    }
}
