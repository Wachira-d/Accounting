namespace Accounting.Helpers;

/// <summary>
/// รอบ 258-3 (F-M2 · คำตัดสิน 258-3-4) — **ป้าย "ออกใบเสร็จตอนรับชำระไม่สำเร็จ" บนหมายเหตุภายในของใบต้นทาง** (รูปแบบเดียวกับ <see cref="PartnerRoutingFailedNote"/>):
/// เดิม <c>DocumentService.CreatePaymentAsync</c> จับข้อผิดพลาดการออกใบเสร็จ/ใบกำกับ §78/1 แล้ว <c>LogWarning</c> อย่างเดียว ⇒ ปิดงานหลังรับชำระไว้ = ใบกำกับไม่ออกและไม่มีใครเห็น ·
/// ตอนนี้ล้มดัง 3 ที่: ป้ายนี้บนใบต้นทาง (Autopilot แถว "ออกใบเสร็จตอนรับชำระไม่สำเร็จ") · แจ้งเตือน <c>PostPaymentAutomationBlocked</c> · LogError ·
/// ออกใบเสร็จของใบนี้สำเร็จภายหลัง (ปุ่ม "ออกใบเสร็จ" / งานหลังรับชำระ) ⇒ ล้างป้าย
/// </summary>
public static class SettlementReceiptFailedNote
{
    public const string Marker = "[RECEIPT-ISSUE-FAILED]";
    public const string RuleCode = "RD-86/4-RECEIPT-AT-PAYMENT";

    public static string Build(string? paymentNumber, string? reason)
        => $"{Marker} รับชำระ {OneLine(paymentNumber, 40)} แล้ว แต่ออกใบเสร็จ/ใบกำกับภาษี ณ วันรับเงินไม่สำเร็จ: {OneLine(reason, 300)} — "
           + "กด “ออกใบเสร็จ” ที่การชำระนี้ในหน้ารายละเอียดเอกสาร (ใบกำกับ §78/1 ต้องออก ณ วันรับเงิน)";

    public static string Append(string? existing, string? paymentNumber, string? reason)
    {
        var kept = Clear(existing);
        var note = Build(paymentNumber, reason);
        return string.IsNullOrWhiteSpace(kept) ? note : kept!.TrimEnd() + "\n\n" + note;
    }

    public static bool Has(string? notes) => !string.IsNullOrEmpty(notes) && notes.Contains(Marker, StringComparison.Ordinal);

    public static string? Clear(string? notes)
    {
        if (!Has(notes)) return notes;
        var paras = notes!.Replace("\r\n", "\n").Split("\n\n")
            .Where(p => !p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        var joined = string.Join("\n\n", paras).Trim();
        return joined.Length == 0 ? null : joined;
    }

    private static string OneLine(string? s, int max)
    {
        var r = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (r.Length == 0) r = "ไม่ทราบ";
        return r.Length > max ? r[..max] : r;
    }
}
