using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accounting.Helpers;

/// <summary>ชนิดบรรทัดในการ "แยกยอดจ่ายจริงตามแหล่ง"</summary>
public enum PaymentSourceKind
{
    /// <summary>เงินออกจริงจากแหล่งนี้ (เงินสด · ธนาคาร · เจ้าหนี้กรรมการที่จ่ายแทน 21230 ฯลฯ)</summary>
    Money = 1,
    /// <summary>หัก/ส่วนลดหลัง VAT (ไม่ใช่เงิน) — ลงผังตาม <see cref="PostVatDeduction"/> (ส่วนลดรับ 51150 / ผังค่าใช้จ่ายของใบ)</summary>
    PostVatDeduction = 2,
}

/// <summary>บรรทัดหนึ่งของการแยกยอด</summary>
/// <param name="Kind">เงิน หรือ หักหลัง VAT</param>
/// <param name="AccountCode">รหัสผังของบริษัท (ต้องมีจริง — service ตรวจ)</param>
/// <param name="Amount">ยอดบวกเสมอ</param>
/// <param name="Note">หมายเหตุ/ป้ายบนกระดาษ</param>
public sealed record PaymentSourceLine(PaymentSourceKind Kind, string AccountCode, decimal Amount, string? Note = null);

/// <summary>ผลตรวจชุดบรรทัดแยกยอด</summary>
/// <param name="Ok">บันทึกได้ไหม</param>
/// <param name="MoneyTotal">Σ เงินออกจริง (= ยอดชำระจริงของเอกสาร)</param>
/// <param name="DeductionTotal">Σ หักหลัง VAT (= <c>Document.PostVatDeductionAmount</c>)</param>
/// <param name="Error">เหตุผลภาษาไทยพร้อมทางไปต่อ (null เมื่อผ่าน)</param>
public readonly record struct PaymentSourceSplitCheck(bool Ok, decimal MoneyTotal, decimal DeductionTotal, string? Error);

/// <summary>ขา JE หนึ่งขาที่ตัวแยกยอดต้องเพิ่มเข้า JE ของเอกสาร (รหัสผัง · เดบิต · เครดิต · คำอธิบาย)</summary>
public sealed record PaymentSourceJournalLine(string AccountCode, decimal Debit, decimal Credit, string Description);

/// <summary>
/// **ติ๊ก "แยกยอดจ่ายจริงตามแหล่ง" ของใบสำคัญจ่าย/ฟอร์มชำระ — ตัวตัดสิน/ตัวตรวจผลรวมตัวเดียว** (pure · ไม่ throw)
///
/// ═══ ที่มา (คำสั่งผู้ใช้ 2026-10-26 · ทีม U) ═══
/// <para>"ระบบต้องรองรับ ให้มีติ๊กแยกยอดจ่ายจริงได้ว่าแต่ละยอดออกจากแหล่งไหนบ้าง สำหรับเคสพิเศษแบบนี้ — บันทึกมือติ๊กนี้แล้วเลือกยอดจ่ายจาก
/// 21230 438 บาท และสร้าง 52120 อีก 98 บาท" ⇒ บรรทัดเงิน (21230 · 438) + บรรทัดหักหลัง VAT (52120 · 98) รวม = ยอดรวมทั้งสิ้น 536</para>
///
/// ═══ กติกา ═══
/// <list type="bullet">
/// <item>Σ ทุกบรรทัด = ยอดรวมทั้งสิ้น (± <see cref="Tolerance"/>) · ทุกบรรทัดยอด &gt; 0 ทศนิยม 2 ตำแหน่ง · ต้องมีเงินออกอย่างน้อย 1 บรรทัด ·
///   บรรทัดหักหลัง VAT ห้ามใช้ผังเงินสด/เงินฝาก</item>
/// <item><b>ไม่สร้าง posting layer ใหม่</b>: ผลตรวจถูกย่อยเป็น <c>Document.ActualPaidAmount</c> = Σ เงิน และ <c>Document.PostVatDeductionAmount</c>
///   = Σ หักหลัง VAT (ความจริงเดียว — <see cref="PostVatDeduction.Resolve"/>) · ขาเพิ่มเติม (<see cref="JournalLines"/>) เดินผ่าน adjusting lines
///   ของ JE เดิม: หักหลัง VAT = Cr ผังนั้น · เงินที่ออกจากแหล่งอื่นที่ไม่ใช่ "จ่ายจาก" ของเอกสาร = Dr แหล่งหลัก / Cr แหล่งนั้น (net-zero)</item>
/// <item>เก็บเป็น JSON ใน <c>Document.PaymentSourceSplitJson</c> ด้วย <see cref="Serialize"/>/<see cref="Parse"/> คู่เดียว (canonical write/read)</item>
/// </list>
/// </summary>
public static class PaymentSourceSplit
{
    /// <summary>รหัสกฎสำหรับ audit/ข้อความ</summary>
    public const string RuleCode = "PAY-SOURCE-SPLIT";

    /// <summary>ค่าเผื่อผลรวม — ตัวเดียวกับยอดชำระ/บรรทัดปรับ</summary>
    public const decimal Tolerance = PaymentSettlementAdjustment.MatchTolerance;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>ตรวจชุดบรรทัดแยกยอดกับยอดรวมทั้งสิ้นของเอกสาร — ว่าง/null = ไม่ได้ติ๊ก (ผ่าน · เงิน = ยอดรวม · หัก 0 — พฤติกรรมเดิม)</summary>
    public static PaymentSourceSplitCheck Check(decimal totalAmount, IReadOnlyList<PaymentSourceLine>? lines)
    {
        if (lines is null || lines.Count == 0)
            return new(true, totalAmount, 0m, null);

        foreach (var l in lines)
        {
            var label = l.Kind == PaymentSourceKind.Money ? "แหล่งเงิน" : "หักหลัง VAT";
            if (string.IsNullOrWhiteSpace(l.AccountCode))
                return Fail($"บรรทัด{label}ต้องระบุผังบัญชี (เช่น 11110 เงินสด · 21230 เจ้าหนี้กรรมการ · {PostVatDeduction.DefaultAccountCode} ส่วนลดรับ)");
            if (l.Amount <= 0m)
                return Fail($"บรรทัด{label} {l.AccountCode} ยอดต้องมากกว่า 0 — ลบบรรทัดที่ไม่ใช้ทิ้ง");
            if (decimal.Round(l.Amount, 2) != l.Amount)
                return Fail($"บรรทัด{label} {l.AccountCode} ต้องเป็นทศนิยมไม่เกิน 2 ตำแหน่ง");
            if (l.Kind == PaymentSourceKind.PostVatDeduction && PostVatDeduction.AccountError(l.AccountCode) is string accErr)
                return Fail(accErr);
        }

        var money = lines.Where(l => l.Kind == PaymentSourceKind.Money).Sum(l => l.Amount);
        var deduction = lines.Where(l => l.Kind == PaymentSourceKind.PostVatDeduction).Sum(l => l.Amount);
        if (money <= 0m)
            return Fail("การแยกยอดต้องมีเงินออกจริงอย่างน้อย 1 แหล่ง — ถ้าไม่มีเงินออกเลย (ปิดด้วยคูปองทั้งใบ) ให้บันทึกเป็นใบตั้งหนี้แล้วปิดด้วยบรรทัดปรับตอนชำระ");
        var sum = Math.Round(money + deduction, 2, MidpointRounding.AwayFromZero);
        var gap = Math.Round(sum - totalAmount, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(gap) > Tolerance)
            return Fail($"ผลรวมการแยกยอด {sum:N2} (เงิน {money:N2} + หักหลัง VAT {deduction:N2}) ไม่เท่ายอดรวมทั้งสิ้น {totalAmount:N2} "
                + $"(ต่าง {(gap >= 0 ? "+" : "−")}{Math.Abs(gap):N2}) — แก้ยอดบรรทัดให้รวมเท่ายอดใบ หรือเพิ่มบรรทัดหักหลัง VAT เท่าส่วนต่าง");
        return new(true, money, deduction, null);
    }

    /// <summary>ขา JE ที่ต้องเพิ่มเข้า adjusting lines ของเอกสาร · <paramref name="primaryMoneyAccountCode"/> = ผัง "จ่ายจาก" ของเอกสาร
    /// (ขาเงินหลักของ JE ลงที่นี่ตามยอดชำระจริงอยู่แล้ว)
    /// <list type="bullet">
    /// <item>หักหลัง VAT ⇒ Cr ผังนั้น (ตัวเดียวกับ <see cref="PostVatDeduction.JournalLine"/>)</item>
    /// <item>เงินจากแหล่งอื่นที่ไม่ใช่แหล่งหลัก ⇒ Dr แหล่งหลัก / Cr แหล่งนั้น (ย้ายภาระจ่าย · net-zero)</item>
    /// </list></summary>
    public static IReadOnlyList<PaymentSourceJournalLine> JournalLines(
        IReadOnlyList<PaymentSourceLine> lines, string primaryMoneyAccountCode, string documentNumber)
    {
        var result = new List<PaymentSourceJournalLine>();
        foreach (var l in lines)
        {
            if (l.Kind == PaymentSourceKind.PostVatDeduction)
            {
                var (dr, cr) = PostVatDeduction.JournalLine(l.Amount);
                result.Add(new(l.AccountCode, dr, cr, PostVatDeduction.JournalDescription(l.Amount, documentNumber)
                    + (string.IsNullOrWhiteSpace(l.Note) ? "" : $" ({l.Note})")));
            }
            else if (!string.Equals(l.AccountCode.Trim(), (primaryMoneyAccountCode ?? "").Trim(), StringComparison.Ordinal))
            {
                result.Add(new(primaryMoneyAccountCode, l.Amount, 0m, $"ย้ายภาระจ่ายไปแหล่ง {l.AccountCode} - {documentNumber}"));
                result.Add(new(l.AccountCode, 0m, l.Amount, $"จ่ายจาก {l.AccountCode}{(string.IsNullOrWhiteSpace(l.Note) ? "" : " (" + l.Note + ")")} - {documentNumber}"));
            }
        }
        return result;
    }

    /// <summary>JSON สำหรับ <c>Document.PaymentSourceSplitJson</c> — null เมื่อไม่มีบรรทัด</summary>
    public static string? Serialize(IReadOnlyList<PaymentSourceLine>? lines)
        => lines is null || lines.Count == 0 ? null : JsonSerializer.Serialize(lines, JsonOpts);

    /// <summary>อ่านกลับ — null/ว่าง/อ่านไม่ได้ = ไม่มีการแยกยอด</summary>
    public static IReadOnlyList<PaymentSourceLine>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<PaymentSourceLine>>(json, JsonOpts);
            return list is { Count: > 0 } ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PaymentSourceSplitCheck Fail(string why) => new(false, 0m, 0m, why);
}
