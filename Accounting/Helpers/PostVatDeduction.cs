using System;
using System.Collections.Generic;
using System.Linq;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัดฐาน/ภาษีหนึ่งบรรทัดที่ใช้ตรวจความสอดคล้องของ VAT (ยอดบรรทัดหลังส่วนลด · อัตรา % · VAT ที่บันทึก)</summary>
public readonly record struct VatLineFact(decimal Net, decimal Rate, decimal Vat);

/// <summary>ผลตัดสิน "ยอดชำระจริง" จากช่องหักหลัง VAT — <see cref="Error"/> ≠ null = ปฏิเสธ (ด่านข้อ 4)</summary>
public readonly record struct PostVatDeductionResolution(decimal? ActualPaidAmount, decimal AmountDue, string? Error);

/// <summary>
/// **"หักหลัง VAT" (ส่วนลด/คูปอง/voucher ที่ลดจากยอดรวมทั้งสิ้น<b>หลัง</b>คำนวณภาษีแล้ว) — ตัวตั้งตัวเดียว** (pure · ไม่ throw · ไม่มี I/O)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-10-26 · ใบ Shopee INV2026090199 · ทีม U) ═══
/// <para>กระดาษ: บรรทัด 2 × 268 = 536 (รวม VAT) · ฐาน 500.93 · VAT 35.07 · รวมทั้งสิ้น 536 · "ส่วนลดพิเศษ 98" · ยอดชำระ 438 —
/// ส่วนลดเกิด<b>หลัง</b> VAT (voucher ของแพลตฟอร์ม) ⇒ ฐานภาษีซื้อ/ภาษีซื้อไม่เปลี่ยน แต่เงินออกจริง 438 ·
/// ระบบเคยลง PV-202610-0040 ผิด: Dr 52120 <b>402.93</b> / Dr 11610 35.07 / Cr 21230 438 — ฐานถูกบีบเป็น 438 − 35.07 ⇒ ค่าใช้จ่ายและฐาน
/// ภาษีซื้อใน ภ.พ.30 ไม่ตรงใบกำกับ (VAT 35.07 บนฐาน 402.93 = 8.7% ซึ่งเป็นไปไม่ได้) และระบบ "ปล่อยผ่าน"</para>
/// <para>JE ที่ถูก (ผู้ใช้ให้มา): Dr 52120 500.93 / Dr 11640 35.07 / <b>Cr 52120 98</b> (หรือผังส่วนลดรับ 51150 ตามที่เลือก) / Cr 21230 438</para>
///
/// ═══ กติกา ═══
/// <list type="bullet">
/// <item><b>ยอดที่ต้องชำระ</b> = ยอดรวมทั้งสิ้น − หักหลัง VAT (<see cref="AmountDue"/>) · ฐาน/VAT ของเอกสารไม่แตะ</item>
/// <item><b>ความจริงเดียวกับ <c>Document.ActualPaidAmount</c></b>: ช่องหักหลัง VAT เป็น "สาเหตุ" · ยอดชำระจริงเป็น "ผล" —
///   ไม่ระบุยอดชำระจริง ⇒ ระบบตั้ง = ยอดที่ต้องชำระ · ระบุทั้งคู่แล้วไม่ตรง (เกิน <see cref="Tolerance"/>) ⇒ ปฏิเสธ (<see cref="Resolve"/>)
///   ⇒ ขาเงินสดของ JE ลดลงเท่าหักหลัง VAT ผ่านกลไกเดิม <see cref="PaymentSettlementAdjustment.DocumentCashDelta"/></item>
/// <item><b>ขา JE</b> (ฝั่งซื้อ): Cr ผังที่เลือก (<see cref="JournalLine"/>) — ค่าเริ่มต้นผัง <see cref="DefaultAccountCode"/> 51150 ส่วนลดรับ
///   ถ้าบริษัทมี ไม่งั้นผังค่าใช้จ่ายหลักของใบ (<see cref="FallbackAccountIdFromLines"/> — ตามตัวอย่างผู้ใช้ Cr 52120)</item>
/// <item><b>ด่านกันปล่อยผ่าน</b> (<see cref="VatConsistencyError"/>): Σ VAT ต่ออัตรา ต้องเท่า round(Σ ฐาน × อัตรา) ± <see cref="VatTolerance"/>
///   (เกณฑ์เดียวกับ <c>ReconcileTaxRounding</c> "ต่างเกิน ฿1 ไม่ใช่เศษปัด") — ใบที่ฐานถูกบีบ (402.93 · 35.07) ถูกบล็อกพร้อมทางไปต่อ
///   "ใส่ช่องหักหลัง VAT / แก้ยอดบรรทัด" · ใบปกติ/ผลต่างปัดเศษ/บางส่วน ผ่านเหมือนเดิม (เทสต์ทิศตรงข้าม)</item>
/// <item><b>ฝั่งขาย</b>ยังไม่รองรับที่ช่องนี้ — ค่าธรรมเนียม/ส่วนลดที่แพลตฟอร์มหักจากยอดโอนเดินผ่านโมดูล Settlement
///   (<c>SettlementChannelAccounts</c>) ⇒ ปฏิเสธพร้อมทางไปต่อ ไม่ใช่ silent no-op (กฎ #4 A)</item>
/// </list>
/// </summary>
public static class PostVatDeduction
{
    /// <summary>รหัสกฎสำหรับ audit/ข้อความ</summary>
    public const string RuleCode = "POST-VAT-DEDUCTION";

    /// <summary>ผังค่าเริ่มต้นของขาหักหลัง VAT ฝั่งซื้อ (51150 ส่วนลดรับ (สินค้า) — ตัวเดียวกับบรรทัดปรับตอนชำระ)</summary>
    public const string DefaultAccountCode = PaymentSettlementAdjustment.PurchaseDiscountAccountCode;

    /// <summary>ค่าเผื่อ "ยอดชำระจริงตรงยอดที่ต้องชำระไหม" — ตัวเดียวกับ <see cref="PaymentSettlementAdjustment.MatchTolerance"/></summary>
    public const decimal Tolerance = PaymentSettlementAdjustment.MatchTolerance;

    /// <summary>ค่าเผื่อความสอดคล้อง VAT ต่อกลุ่มอัตรา — เศษปัดสะสมระดับสตางค์ · ต่างเกินนี้ = ฐานหรือ VAT ผิดเรื่อง (ตัวเลขเดียวกับ ReconcileTaxRounding)</summary>
    public const decimal VatTolerance = 1.00m;

    /// <summary>ชนิดเอกสารฝั่งซื้อที่ช่องนี้ใช้ได้ (ใบสำคัญจ่าย · ใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย)</summary>
    public static bool Applies(DocumentType type)
        => type is DocumentType.PaymentVoucher or DocumentType.PurchaseInvoice or DocumentType.Expense;

    /// <summary>ยอดที่ต้องชำระ = ยอดรวมทั้งสิ้น − หักหลัง VAT (ปัด 2 ตำแหน่ง AwayFromZero)</summary>
    public static decimal AmountDue(decimal totalAmount, decimal deduction)
        => Math.Round(totalAmount - deduction, 2, MidpointRounding.AwayFromZero);

    /// <summary>ตรวจค่าของช่องก่อนเก็บ — คืนข้อความไทยพร้อมทางไปต่อ (null = ใช้ได้) · 0 = ไม่มี (ผ่านเสมอ)</summary>
    public static string? ValidateAmount(decimal deduction, decimal totalAmount, DocumentType type)
    {
        if (deduction == 0m) return null;
        if (!Applies(type))
            return $"ช่อง \"หักหลัง VAT\" ใช้ได้เฉพาะเอกสารฝั่งซื้อ (ใบสำคัญจ่าย · ใบแจ้งหนี้ซื้อ · ค่าใช้จ่าย) — เอกสารชนิด {type} ไม่มีขาเงินจ่ายให้หัก · "
                + "ฝั่งขายที่แพลตฟอร์มหักค่าธรรมเนียม/คูปองจากยอดโอน ให้บันทึกผ่านโมดูล Settlement (นำเข้ารายงานการโอนของแพลตฟอร์ม) · ล้างช่องนี้";
        if (deduction < 0m)
            return "หักหลัง VAT ต้องไม่ติดลบ — ถ้าจ่ายเกินยอดใบ (ค่าส่งที่แพลตฟอร์มเก็บเพิ่ม) ให้ใส่เป็นบรรทัดปรับ + ที่ \"ปรับปรุงรายการบัญชี\" แทน";
        if (decimal.Round(deduction, 2) != deduction)
            return "หักหลัง VAT ต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง";
        if (deduction > totalAmount)
            return $"หักหลัง VAT {deduction:N2} มากกว่ายอดรวมทั้งสิ้น {totalAmount:N2} — ตรวจกระดาษ: ส่วนลดหลังภาษีต้องไม่เกินยอดใบ";
        return null;
    }

    /// <summary>ตัดสินยอดชำระจริงจากช่องหักหลัง VAT (ความจริงเดียว) + ด่านข้อ 4: ฐาน + VAT − หักหลัง VAT ต้อง = ยอดชำระ
    /// <list type="bullet">
    /// <item>หักหลัง VAT 0 ⇒ ยอดชำระจริงตามที่ส่งมา (พฤติกรรมเดิมทุกประการ)</item>
    /// <item>มีหักหลัง VAT · ไม่ระบุยอดชำระจริง ⇒ ตั้ง = ยอดที่ต้องชำระ</item>
    /// <item>มีทั้งคู่ · ต่างเกิน <see cref="Tolerance"/> ⇒ ปฏิเสธ พร้อมบอกว่าต้องแก้ช่องไหน</item>
    /// </list></summary>
    public static PostVatDeductionResolution Resolve(decimal totalAmount, decimal deduction, decimal? actualPaidAmount)
    {
        if (deduction <= 0m)
            return new(actualPaidAmount, actualPaidAmount ?? totalAmount, null);
        var due = AmountDue(totalAmount, deduction);
        if (actualPaidAmount is not decimal paid)
            return new(due, due, null);
        if (Math.Abs(paid - due) <= Tolerance)
            return new(paid, due, null);
        return new(paid, due,
            $"⛔ ยอดชำระจริง {paid:N2} ≠ ยอดรวมทั้งสิ้น {totalAmount:N2} − หักหลัง VAT {deduction:N2} = {due:N2} (ต่าง {Signed(paid - due)}) — "
            + "แก้ช่อง \"หักหลัง VAT\" ให้เท่าส่วนลด/คูปองที่พิมพ์หลังยอดรวม หรือแก้ \"ยอดชำระจริง\" ให้เท่ายอดที่ต้องชำระ · "
            + "ถ้ามีค่าส่งที่จ่ายเพิ่ม ให้ลงเป็นบรรทัดปรับ + ที่ \"ปรับปรุงรายการบัญชี\" (ฐานและ VAT ของใบกำกับห้ามแก้ตามเงินที่จ่าย)");
    }

    /// <summary>ขา JE ของหักหลัง VAT ฝั่งซื้อ: (เดบิต, เครดิต) = (0, ยอด) — ลดค่าใช้จ่าย/ต้นทุน · ผังเงินสดถูกลดด้วย DocumentCashDelta อยู่แล้ว</summary>
    public static (decimal Debit, decimal Credit) JournalLine(decimal deduction)
        => deduction > 0m ? (0m, deduction) : (0m, 0m);

    /// <summary>คำอธิบายบรรทัด JE</summary>
    public static string JournalDescription(decimal deduction, string documentNumber)
        => $"หักหลัง VAT (ส่วนลด/คูปองหลังยอดรวม) {deduction:N2} - {documentNumber}";

    /// <summary>ผังสำรองเมื่อไม่ได้เลือกและบริษัทไม่มี 51150: ผังค่าใช้จ่ายของบรรทัดที่ยอดสูงสุดของใบ (ตัวอย่างผู้ใช้: Cr 52120) · null = ไม่มีบรรทัดที่มีผัง</summary>
    public static Guid? FallbackAccountIdFromLines(IEnumerable<(Guid? AccountId, decimal Amount)> lines)
        => lines.Where(l => l.AccountId.HasValue).OrderByDescending(l => l.Amount).Select(l => l.AccountId).FirstOrDefault();

    /// <summary>ผังที่ใช้ลงขาหักหลัง VAT ห้ามเป็นผังเงินสด/เงินฝาก (เงินที่จ่ายจริงลงที่แหล่งเงินของเอกสาร)</summary>
    public static string? AccountError(string? accountCode)
        => PaymentSettlementAdjustment.IsMoneyAccountCode(accountCode)
            ? $"ผังหักหลัง VAT ใช้ผังเงินสด/เงินฝาก ({accountCode}) ไม่ได้ — ใช้ผังส่วนลดรับ (51150) หรือผังค่าใช้จ่ายของใบ · เงินที่จ่ายจริงลงที่ \"จ่ายจาก\" ของเอกสาร"
            : null;

    /// <summary>
    /// **ด่านกันปล่อยผ่าน (คำสั่งผู้ใช้ "ระบบไม่ควรปล่อยให้ยอดภาษีไม่ตรง")** — VAT ที่บันทึกต่ออัตราต้องสอดคล้องกับฐาน:
    /// |Σ VAT − round(Σ ฐาน × อัตรา/100)| ≤ <see cref="VatTolerance"/> ต่อกลุ่มอัตรา · นับเฉพาะบรรทัดที่อัตรา &gt; 0 และ VAT &gt; 0
    /// (บรรทัดยกเว้น/0%/ไม่มี VAT ไม่เกี่ยว · หลายอัตราแยกกลุ่มกัน) · คืนข้อความไทยพร้อมทางไปต่อ (null = ผ่าน)
    /// <para>ใบ PV-202610-0040: ฐาน 402.93 × 7% = 28.21 แต่ VAT 35.07 ⇒ ต่าง 6.86 ⇒ บล็อก · ใบที่ถูก (500.93 · 35.07) ผ่าน</para>
    /// </summary>
    public static string? VatConsistencyError(IEnumerable<VatLineFact> lines)
    {
        foreach (var g in lines.Where(l => l.Rate > 0m && l.Vat > 0m).GroupBy(l => l.Rate))
        {
            var net = g.Sum(l => l.Net);
            var vat = g.Sum(l => l.Vat);
            var expected = Math.Round(net * g.Key / 100m, 2, MidpointRounding.AwayFromZero);
            var gap = Math.Round(vat - expected, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(gap) <= VatTolerance) continue;
            var impliedBase = g.Key > 0m ? Math.Round(vat * 100m / g.Key, 2, MidpointRounding.AwayFromZero) : 0m;
            return $"⛔ ภาษีซื้อ {vat:N2} ไม่ตรงกับฐาน {net:N2} × {g.Key:0.##}% = {expected:N2} (ต่าง {Signed(gap)}) — "
                + $"VAT {vat:N2} ที่ {g.Key:0.##}% ควรมาจากฐาน {impliedBase:N2} · ถ้ากระดาษมีส่วนลด/คูปอง/voucher พิมพ์หลัง \"รวมทั้งสิ้น\" "
                + $"(ยอดชำระน้อยกว่ายอดใบ) ให้คงฐานและ VAT ตามใบกำกับ แล้วใส่ช่อง \"หักหลัง VAT\" = ส่วนลดนั้น (ยอดชำระ = รวมทั้งสิ้น − หักหลัง VAT) · "
                + "หรือแก้ยอดบรรทัด/VAT ให้ตรงใบกำกับ — ระบบไม่ลงบัญชีฐานภาษีซื้อที่ไม่ตรงใบ (ภ.พ.30)";
        }
        return null;
    }

    private static string Signed(decimal v) => v >= 0m ? $"+{v:N2}" : $"−{Math.Abs(v):N2}";
}
