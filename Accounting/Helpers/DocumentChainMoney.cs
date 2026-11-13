using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ชนิดของ "เงิน" ที่ผูกใบในสายเอกสารโดยไม่ผ่าน <c>RelatedDocumentId</c></summary>
public enum ChainMoneyKind
{
    /// <summary>เงินรอจัดสรร (21630) ถูกนำไปตัดใบนี้ทีหลัง — <c>PaymentAllocation.IsLateAllocation</c></summary>
    CreditAllocation,
    /// <summary>การรับชำระที่ถูกย้ายมาจากใบอื่น — <c>PaymentAllocation.MovedFromAllocationId</c> (รอบ 266 แพ็ก B)</summary>
    MovedPayment,
    /// <summary>ใบมัดจำที่ถูกหักเข้าใบนี้ — <c>Document.DepositAppliedToDocumentId</c></summary>
    DepositApplied,
}

/// <summary>ข้อเท็จจริงหนึ่งรายการ (ผู้เรียกอ่านจากฐาน — <c>DocumentService.LoadChainMoneyFactsAsync</c>)</summary>
/// <param name="Id">id ของแถวที่เป็นที่มา (แถวจัดสรร / ใบมัดจำ)</param>
/// <param name="AttachedDocumentId">ใบในสายที่เงินนี้ตัดชำระ</param>
/// <param name="Number">เลขการรับชำระ / เลขใบมัดจำ</param>
/// <param name="OpenDocumentId">ใบที่คลิกแล้วเปิดได้ (ใบมัดจำ) · null = ไม่มีใบให้เปิด</param>
/// <param name="FromDocumentNumber">ใบเดิมที่เงินถูกย้ายออกมา (เฉพาะ <see cref="ChainMoneyKind.MovedPayment"/>)</param>
public sealed record ChainMoneyFact(
    ChainMoneyKind Kind,
    Guid Id,
    Guid AttachedDocumentId,
    string Number,
    DateTime Date,
    decimal Amount,
    Guid? OpenDocumentId = null,
    Guid? PaymentId = null,
    Guid? AllocationId = null,
    string? FromDocumentNumber = null);

/// <summary>โหนด "เงิน" ที่สายเอกสารแสดง (เซิร์ฟเวอร์คิดป้าย — หน้าแสดง · F2 ข้อ 5)</summary>
/// <param name="HasAdvice">ออก "ใบแจ้งการนำเงินไปตัดชำระ" ของแถวนี้ได้ (<see cref="PaymentApplicationAdvice"/>)</param>
public sealed record ChainMoneyNode(ChainMoneyFact Fact, string Label, bool HasAdvice);

/// <summary>
/// **สายเอกสารเห็นเงิน (รอบ 266 แพ็ก C · คำตัดสิน 266-1-40)** — สายเดิมเดินแค่ <c>RelatedDocumentId</c> ⇒ ใบที่ปิดด้วยเงินรอจัดสรร/การย้ายการรับชำระ/การหักมัดจำ
/// ดูเหมือน "ชำระแล้วโดยไม่มีที่มา" · ตัวนี้แปลงแถวจัดสรร/ใบมัดจำที่ยังมีผลเป็นโหนดชนิด "เงิน" ผูกกับใบในสาย (ไม่เปลี่ยนการเดินสายเดิม · อ่านอย่างเดียว)
/// </summary>
public static class DocumentChainMoney
{
    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string Label(ChainMoneyFact f) => f.Kind switch
    {
        ChainMoneyKind.CreditAllocation => $"เงินรอจัดสรร {f.Number} ตัดชำระ {Money(f.Amount)}",
        ChainMoneyKind.MovedPayment => $"การรับชำระ {f.Number} ย้ายมาจาก {f.FromDocumentNumber ?? "ใบอื่น"} · {Money(f.Amount)}",
        ChainMoneyKind.DepositApplied => $"หักมัดจำ {f.Number} · {Money(f.Amount)}",
        _ => f.Number,
    };

    /// <summary>โหนดเงินของใบในสาย — เฉพาะรายการที่ผูกกับใบใน <paramref name="chainDocumentIds"/> · ไม่ซ้ำ id · เรียงตามวันที่แล้วเลขที่</summary>
    public static IReadOnlyList<ChainMoneyNode> Nodes(IEnumerable<ChainMoneyFact> facts, IReadOnlyCollection<Guid> chainDocumentIds)
    {
        var inChain = chainDocumentIds as ISet<Guid> ?? chainDocumentIds.ToHashSet();
        return facts
            .Where(f => inChain.Contains(f.AttachedDocumentId))
            .GroupBy(f => (f.Kind, f.Id)).Select(g => g.First())
            .OrderBy(f => f.Date).ThenBy(f => f.Number, StringComparer.Ordinal)
            .Select(f => new ChainMoneyNode(f, Label(f), HasAdvice: f.Kind is ChainMoneyKind.CreditAllocation or ChainMoneyKind.MovedPayment))
            .ToList();
    }
}
