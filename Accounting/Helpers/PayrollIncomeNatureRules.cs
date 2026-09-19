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

    /// <summary>เบี้ยเลี้ยงทั้งหมดของงวดนี้ (ประจำ + ครั้งคราว) — ยอดที่ขึ้นช่อง
    /// "เบี้ยเลี้ยง" บนสลิป/รายงาน · **คนละตัว**กับ <see cref="RecurringAllowance"/>
    /// ที่ใช้ฉายไปงวดที่เหลือ (รวมสองเรื่องนี้เป็นถังเดียวคือบั๊ก D6-3)</summary>
    public decimal AllowanceTotal => RecurringAllowance + OneTimeAllowance;

    /// <summary>รวมสองชุด — ใช้เมื่อเงินได้ของคนหนึ่งงวดมาจากหลายแหล่ง
    /// (แถว <c>PayrollItem</c> + การลงเวลา + เบี้ยเลี้ยงที่บริษัทตั้งเอง)
    /// โดยที่ทุกแหล่งต้องเดิน <see cref="PayrollIncomeNatureRules.Accumulate"/>
    /// ตัวเดียวกัน</summary>
    public PayrollEarningBuckets Plus(PayrollEarningBuckets o) => new(
        Overtime + o.Overtime,
        RecurringAllowance + o.RecurringAllowance,
        OneTimeAllowance + o.OneTimeAllowance,
        Commission + o.Commission,
        Bonus + o.Bonus,
        NonTaxable + o.NonTaxable);
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

    /// <summary>เบี้ยเลี้ยงที่เกิดจาก**การลงเวลา** (เบี้ยเลี้ยงเดินทาง/ต่างจังหวัด ·
    /// ค่าที่พัก · ค่าอาหารวัน OT · ค่าอาหารรายวัน) — **ครั้งคราวเสมอ**
    ///
    /// <para>═══ บั๊กที่ปิด (D6-3 แหล่งที่สอง · DECISION_AUDIT §10.5 ข้อ 9) ═══
    /// รอบที่แล้วแก้ให้ <c>PayrollItem</c> แยกประจำ/ครั้งคราวได้แล้ว แต่
    /// <c>PayrollService</c> ยังบวกเบี้ยเลี้ยงจาก attendance เข้าถัง
    /// <c>allowances</c> ตัวเดียวกับเบี้ยเลี้ยงประจำ ⇒ ถูก**ฉาย × งวดที่เหลือ**
    /// ทั้งที่ผันแปรตามวันที่ไปทำงานจริงทุกเดือน ⇒ พนักงานที่ไปต่างจังหวัด
    /// เดือนเดียวถูกประมาณการรายได้ทั้งปีสูงเกินจริง แล้ว**ถูกหักภาษีเกินทุกงวด
    /// ที่เหลือ** (ลูกจ้างออกเงินให้บริษัทไปก่อนจนกว่าจะยื่นแบบขอคืนเอง)</para>
    ///
    /// <para>ทำไมไม่ให้ผู้ใช้เลือก: ค่าพวกนี้ไม่ได้มาจากแถว <c>PayrollItem</c>
    /// แต่คำนวณจาก <c>EmployeeProjectTime</c> ของ**เดือนนั้น** ⇒ ตามนิยามแล้ว
    /// เป็นศูนย์ในเดือนที่ไม่ได้ไป ⇒ ไม่มีเคสที่ "ประจำ" เป็นคำตอบที่ถูก</para></summary>
    public const PayrollIncomeNature AttendanceAllowance = PayrollIncomeNature.OneTimeAllowance;

    /// <summary>ลักษณะของ "เบี้ยเลี้ยงที่บริษัทตั้งเอง" (<c>CustomAllowanceItem</c>)
    /// — <c>Type = "Daily"</c> คูณจำนวนวันทำงานจริง ⇒ ผันแปรทุกงวด = ครั้งคราว ·
    /// <c>"Monthly"</c> จ่ายก้อนเดียวต่อรอบไม่ว่าจะมาทำงานกี่วัน = ประจำ
    ///
    /// <para>ตัวเทียบที่ชัดที่สุด: ค่าตำแหน่ง/ค่าครองชีพ (Monthly) ได้เท่ากันทุก
    /// เดือน ⇒ ฉายถูก · ค่าอาหาร 50 บาท/วัน (Daily) เดือนที่ลาเยอะได้น้อยลง
    /// ⇒ ฉายจากเดือนที่ทำงานครบทำให้ประมาณการเกิน</para></summary>
    public static PayrollIncomeNature ForCustomAllowance(string? type)
        => string.Equals(type, "Daily", StringComparison.OrdinalIgnoreCase)
            ? PayrollIncomeNature.OneTimeAllowance
            : PayrollIncomeNature.RecurringAllowance;

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
