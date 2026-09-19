using System;
using System.Collections.Generic;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ยอดเงินได้ของพนักงานหนึ่งคนในงวดหนึ่ง แยกตามลักษณะที่เครื่องคำนวณ
/// ภาษีต้องใช้ — <see cref="RecurringAllowance"/> เท่านั้นที่ถูกฉายไปงวดที่เหลือ</summary>
/// <param name="Overtime">ค่าล่วงเวลา (เข้าฐานภาษีงวดนี้ · ไม่ฉาย)</param>
/// <param name="RecurringAllowance">เบี้ยเลี้ยงประจำ (เข้าฐานภาษี · **ฉาย**)</param>
/// <param name="OneTimeAllowance">เงินได้ครั้งคราว (เข้าฐานภาษีงวดนี้ · ไม่ฉาย)</param>
/// <param name="Commission">คอมมิชชัน (เข้าฐานภาษีงวดนี้ · ไม่ฉาย)</param>
/// <param name="Bonus">โบนัส (เข้าฐานภาษีงวดนี้ · ไม่ฉาย)</param>
/// <param name="NonTaxable">รายการที่ติ๊ก "ไม่หักภาษี" — จ่ายให้พนักงานแต่
/// **ไม่เข้าฐาน WHT** (สวัสดิการที่ได้รับยกเว้น)</param>
public readonly record struct PayrollEarningBuckets(
    decimal Overtime,
    decimal RecurringAllowance,
    decimal OneTimeAllowance,
    decimal Commission,
    decimal Bonus,
    decimal NonTaxable)
{
    /// <summary>ส่วนที่เข้าฐานภาษีของงวดนี้ (ไม่รวมเงินเดือนฐาน)</summary>
    public decimal TaxableExtras
        => Overtime + RecurringAllowance + OneTimeAllowance + Commission + Bonus;
}

/// <summary>
/// ตัวตัดสิน**ตัวเดียว**ของ "รายการเงินเดือนนี้เป็นฐานประจำหรือครั้งคราว"
///
/// <para>═══ บั๊กที่ปิด (D6-3 · P0) ═══ <c>PayrollService</c> เคยตัดสินจาก
/// <b>prefix ของรหัส</b>: <c>Code.StartsWith("OT")</c> → ค่าล่วงเวลา ·
/// <c>== "COM"</c> → คอมมิชชัน · <c>== "BONUS"</c> → โบนัส · <b>ที่เหลือทุกตัว
/// → เบี้ยเลี้ยงประจำ</b> ⇒ บริษัทที่ตั้งรหัสเอง (<c>BN01</c> ชื่อ "โบนัส",
/// <c>INCENTIVE</c>) ได้โบนัสถูกฉาย × งวดที่เหลือ ⇒ หักภาษีเกินจริง
/// (golden: โบนัส 300,000 จ่ายเดือน 6 ต้องได้ภาษีทั้งปี 61,925 ไม่ใช่ 82,221)</para>
///
/// <para>═══ และ <c>PayrollItem.IsTaxable</c> เคยเป็น silent no-op ═══
/// ถูก<b>เขียน</b>ตอนสร้างรายการ แต่ <b>ไม่มีใครอ่านในเส้นคำนวณเลย</b>
/// (มีแต่ <c>CustomAllowanceItem.IsTaxable</c> ที่เป็นคนละตัว) ⇒ HR ติ๊ก
/// "ไม่หักภาษี" แล้วไม่มีผลอะไร (กฎเหล็ก #4 A "ห้าม silent no-op")
/// ตัวรวมยอดตัวนี้อ่านธงนั้นจริง</para>
///
/// <para>⚠️ <c>FromLegacyCode</c> มีไว้เพื่อ **คงพฤติกรรมเดิม** ของแถวที่
/// ยังเป็น <see cref="PayrollIncomeNature.Unspecified"/> (และเป็นสูตรที่ migration
/// ใช้ backfill) — ห้ามใช้เป็นตัวตัดสินหลักของรายการใหม่ เพราะมันคือกฎที่ผิด
/// ตั้งแต่แรก · รายการใหม่ต้องให้ผู้ใช้เลือกลักษณะเอง</para>
/// </summary>
public static class PayrollIncomeNatureRules
{
    /// <summary>กฎเดิม (prefix ของรหัส) — ใช้กับแถวที่ยังไม่ระบุลักษณะ และเป็น
    /// สูตรเดียวกับที่ migration ใช้เติมค่าให้ข้อมูลเก่า ⇒ ตัวเลขของรอบเงินเดือน
    /// ที่คำนวณไปแล้ว **ไม่เปลี่ยนแม้แต่สตางค์เดียว**</summary>
    private static PayrollIncomeNature FromLegacyCode(string? code)
    {
        var c = code ?? "";
        if (c.StartsWith("OT", StringComparison.OrdinalIgnoreCase)) return PayrollIncomeNature.Overtime;
        if (c.Equals("COM", StringComparison.OrdinalIgnoreCase)) return PayrollIncomeNature.Commission;
        if (c.Equals("BONUS", StringComparison.OrdinalIgnoreCase)) return PayrollIncomeNature.Bonus;
        // ที่เหลือทุกตัว = เบี้ยเลี้ยงประจำ — นี่คือจุดที่เคยผิด แต่ต้องคงไว้
        // สำหรับแถวที่ยังไม่ได้ระบุ เพื่อไม่ให้ตัวเลขเก่าขยับเอง
        return PayrollIncomeNature.RecurringAllowance;
    }

    /// <summary>ลักษณะที่ใช้จริงของรายการหนึ่ง — ระบุไว้แล้วใช้ตามนั้น ·
    /// ยังไม่ระบุจึงตกกลับไปกฎรหัสเดิม</summary>
    public static PayrollIncomeNature Effective(PayrollIncomeNature stored, string? code)
        => stored == PayrollIncomeNature.Unspecified ? FromLegacyCode(code) : stored;

    /// <summary>รวมยอดรายการเงินได้ทั้งหมดของพนักงานหนึ่งคนลงถังที่ถูกต้อง —
    /// pure ทั้งหมด (เทสต์ได้โดยไม่ต้องมี DB)</summary>
    /// <param name="items">(ลักษณะที่เก็บไว้, รหัส, เข้าฐานภาษีไหม, จำนวนเงิน)</param>
    public static PayrollEarningBuckets Accumulate(
        IEnumerable<(PayrollIncomeNature Nature, string? Code, bool IsTaxable, decimal Amount)> items)
    {
        decimal ot = 0, recurring = 0, oneTime = 0, com = 0, bonus = 0, nonTaxable = 0;

        foreach (var it in items ?? Array.Empty<(PayrollIncomeNature, string?, bool, decimal)>())
        {
            if (it.Amount == 0m) continue;

            // ★ ธง "ไม่หักภาษี" ต้องมีผลจริง — เดิมเขียนแล้วไม่มีใครอ่าน
            if (!it.IsTaxable) { nonTaxable += it.Amount; continue; }

            switch (Effective(it.Nature, it.Code))
            {
                case PayrollIncomeNature.Overtime: ot += it.Amount; break;
                case PayrollIncomeNature.Commission: com += it.Amount; break;
                case PayrollIncomeNature.Bonus: bonus += it.Amount; break;
                case PayrollIncomeNature.OneTimeAllowance: oneTime += it.Amount; break;
                default: recurring += it.Amount; break;
            }
        }

        return new PayrollEarningBuckets(ot, recurring, oneTime, com, bonus, nonTaxable);
    }

    /// <summary>คำอธิบายสั้นสำหรับหน้าตั้งค่า — บอกผลต่อการประมาณการภาษี
    /// (เซิร์ฟเวอร์เป็นเจ้าของถ้อยคำ หน้าเว็บห้ามเขียนตารางเอง)</summary>
    public static string Describe(PayrollIncomeNature nature) => nature switch
    {
        PayrollIncomeNature.RecurringAllowance =>
            "เบี้ยเลี้ยงประจำ — ระบบถือว่าจะได้ทุกงวด จึงนำไปประมาณการเงินได้ทั้งปี (ฉายไปงวดที่เหลือ)",
        PayrollIncomeNature.OneTimeAllowance =>
            "เงินได้ครั้งคราว — เข้าฐานภาษีเฉพาะงวดที่จ่าย ไม่ถูกนำไปคูณกับงวดที่เหลือ",
        PayrollIncomeNature.Overtime =>
            "ค่าล่วงเวลา — เข้าฐานภาษีเฉพาะงวดที่จ่าย ไม่ถูกฉาย",
        PayrollIncomeNature.Commission =>
            "คอมมิชชัน — เข้าฐานภาษีเฉพาะงวดที่จ่าย ไม่ถูกฉาย",
        PayrollIncomeNature.Bonus =>
            "โบนัส — เข้าฐานภาษีเฉพาะงวดที่จ่าย ไม่ถูกฉาย (ม.50(1) เงินได้ครั้งคราวรวมครั้งเดียว)",
        _ =>
            "ยังไม่ระบุ — ระบบใช้กฎรหัสเดิม (ขึ้นต้น OT = ค่าล่วงเวลา · COM = คอมมิชชัน · "
            + "BONUS = โบนัส · ที่เหลือถือเป็นเบี้ยเลี้ยงประจำ) ⚠️ รหัสที่ตั้งเอง เช่น BN01 "
            + "จะถูกนับเป็นเบี้ยเลี้ยงประจำและถูกฉายไปทั้งปี ทำให้หักภาษีเกินจริง — โปรดระบุลักษณะให้ถูก",
    };
}
