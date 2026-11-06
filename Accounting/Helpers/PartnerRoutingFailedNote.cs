namespace Accounting.Helpers;

/// <summary>
/// **ป้าย "ส่งเอกสารเข้าระบบคู่ค้าไม่สำเร็จ" บนหมายเหตุภายในของเอกสาร — เจ้าของรูปแบบตัวเดียว** (รอบ 250 WP-02b · AUTOPILOT_BILLING_DESIGN §6.1 P-6)
///
/// <para>═══ ที่มา ═══ <c>DocumentService.ApproveDocumentAsync</c> ส่งเอกสารเข้ากล่องของคู่ค้าแบบ
/// <c>try { OnDocumentSentAsync } catch { LogWarning }</c> ⇒ ล้มแล้วไม่มีใครรู้ (ผู้ใช้เห็น "อนุมัติแล้ว" · คู่ค้าไม่ได้เอกสาร · ไม่มีทางส่งใหม่) —
/// "ล้มดัง 3 ที่" ไม่ครบสักที่ (กฎ #4 F2 ข้อ 7)</para>
///
/// <para>ตอนนี้: ล้ม ⇒ (1) ป้ายนี้บนเอกสาร + ธง <c>DocumentResponse.PartnerRoutingFailed</c> (หน้าเอกสารแสดงแถบเตือน + ปุ่มส่งใหม่) ·
/// (2) log ระดับ Error · (3) คำตอบของการอนุมัติ/ส่งใหม่มีธงนี้ · ส่งสำเร็จ (รวมการกดส่งใหม่) ⇒ ล้างป้าย ·
/// ทางถาวร (outbox + worker) ยังเป็นงาน WP-12 — ป้ายนี้คือ "ระหว่างนี้ห้ามเงียบ"</para>
/// รูปแบบเดียวกับ <see cref="EtaxAutoFailedNote"/> (ย่อหน้าเดียว · ป้ายใหม่แทนป้ายเก่า ไม่สะสม)
/// </summary>
public static class PartnerRoutingFailedNote
{
    public const string Marker = "[PARTNER-ROUTE-FAILED]";
    public const string RuleCode = "XT-ROUTE-FAILED";

    /// <summary>ข้อความป้าย (ย่อหน้าเดียว)</summary>
    public static string Build(string reason)
        => $"{Marker} ส่งเอกสารเข้าระบบของคู่ค้า (NextAcc) ไม่สำเร็จ: {OneLine(reason)} — "
           + "กด “ส่งให้คู่ค้าอีกครั้ง” ที่หน้ารายละเอียดเอกสาร (คู่ค้ายังไม่ได้รับเอกสารนี้ในระบบ)";

    /// <summary>ต่อท้ายหมายเหตุภายในเดิม (ไม่ทับ) · ป้ายเดิมถูกแทนด้วยอันล่าสุด</summary>
    public static string Append(string? existing, string reason)
    {
        var kept = Clear(existing);
        var note = Build(reason);
        return string.IsNullOrWhiteSpace(kept) ? note : kept!.TrimEnd() + "\n\n" + note;
    }

    /// <summary>มีป้ายค้างอยู่ไหม</summary>
    public static bool Has(string? notes)
        => !string.IsNullOrEmpty(notes) && notes.Contains(Marker, StringComparison.Ordinal);

    /// <summary>ถอดทุกย่อหน้าที่ขึ้นต้นด้วยป้าย (ย่อหน้าอื่นคงเดิมตามลำดับ) · ไม่มีป้าย = คืนค่าเดิมทุกตัวอักษร · เหลือแต่ช่องว่าง = <c>null</c></summary>
    public static string? Clear(string? notes)
    {
        if (!Has(notes)) return notes;
        var paras = notes!.Replace("\r\n", "\n").Split("\n\n")
            .Where(p => !p.TrimStart().StartsWith(Marker, StringComparison.Ordinal));
        var joined = string.Join("\n\n", paras).Trim();
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>สาเหตุย่อหน้าเดียว (ข้อความ exception อาจมีขึ้นบรรทัด — ป้ายต้องเป็นย่อหน้าเดียวให้ <see cref="Clear"/> ถอดได้ครบ) · ตัดที่ 300 ตัวอักษร</summary>
    private static string OneLine(string? reason)
    {
        var r = (reason ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (r.Length == 0) r = "ไม่ทราบสาเหตุ";
        return r.Length > 300 ? r[..300] : r;
    }
}
