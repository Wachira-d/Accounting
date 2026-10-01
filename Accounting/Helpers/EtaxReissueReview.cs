using System.Reflection;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **รายงานอ่านอย่างเดียวให้นักบัญชีตรวจ** (รอบ 200 ทีม V1G · คำตัดสินข้อ 44 — แนวเดียวกับข้อ 20: งวดที่อาจยื่นแล้วห้ามแก้เงียบ ⇒ ไม่แก้อัตโนมัติ)
/// — ตัวจำแนกแบบ pure · คิวรีอยู่ที่ <c>DocumentService.GetEtaxReissueReviewAsync</c>
/// <list type="bullet">
/// <item><see cref="FlaggedReceiptVatUndone"/> — ใบเสร็จถือ VAT ที่ติดธง "ต้องยกเลิกทาง e-Tax" ก่อนรอบ V1F แล้วภาษีขายของใบต้นทางถูกถอยไปแล้ว
/// (ข้อบกพร่อง V1-R2 เดิม: ใบกำกับยังมีผลที่กรมสรรพากร แต่ ภ.พ.30 ไม่นับ)</item>
/// <item><see cref="ResolvedByV1FMarker"/> — ใบเสร็จที่ปิดธงด้วยเส้นของรอบ V1F (ยกเลิก + ซ่อน · หลักฐานอาจเป็น "เลขที่ใบลดหนี้" — RV1F-1) ⇒ ตรวจว่า
/// ทางกรมสรรพากรยกเลิกจริง หรือออกใบลดหนี้ (ถ้าลดหนี้ ต้องบันทึกใบลดหนี้ในระบบ และภาษีขายของเดือนเดิมถูกลดย้อนหลังไปแล้ว)</item>
/// <item><see cref="CarriedExcess"/> — ใบแทน ("ยกเลิกและออกใบแทน") ที่ออกก่อนรอบ V1F ด้วยตัวคัดลอกทุกช่อง (<c>CopyScalars</c>) ⇒ พาหลักฐานของใบเดิม
/// (ลายเซ็นรับของ · ผลตรวจ RD · feedback AI · ผู้จัดทำภายนอก ฯลฯ) มาด้วย</item>
/// <item><see cref="ReclassReversalMisdated"/> — รอบ 200 ทีม V1H (คำตัดสินข้อ 53): ตัวกลับ "ภาษีขายถึงกำหนด" (§78/1) ที่ลงคนละเดือนกับ JE ย้ายภาษีที่มันกลับ —
/// ก่อนคำตัดสินข้อ 48 ตัวถอยลงวันที่ใบแจ้งหนี้ ⇒ GL ภาษีขายคลาดสองเดือน (เดือนใบแจ้งหนี้ −VAT · เดือนรับเงิน +VAT) ขณะที่ ภ.พ.30 = 0/0</item>
/// </list>
/// </summary>
public static class EtaxReissueReview
{
    /// <summary>ป้ายในหมายเหตุภายในที่เส้นปิดธงของรอบ V1F เขียนไว้ (ใบเสร็จถูกยกเลิก + soft-delete) — รอบ V1G เขียน <see cref="ResolvedMarker"/> แทน</summary>
    public const string ResolvedByV1FMarker = "[ETAX-CANCELLED]";

    /// <summary>ป้ายของเส้นปิดธงรอบ V1G (ใบเสร็จคงแสดง · แยกทางยกเลิก/ใบลดหนี้)</summary>
    public const string ResolvedMarker = "[ETAX-CANCEL-RESOLVED]";

    /// <summary>ป้ายของการปิดธงทาง (ค) “ใบกำกับเดิมยังใช้ได้” (ข้อ 54) — ฝั่งเขียน (<c>ResolveEtaxCancellationAsync</c>) และฝั่งอ่าน
    /// (<see cref="LastResolutionKeptOriginal"/>) ใช้ค่าคงที่ตัวนี้ตัวเดียว (รอบ 200 ทีม V1I · ฝ่ายค้าน V1H-O1)</summary>
    public const string KeptOriginalMarker = ResolvedMarker + " ใบกำกับเดิมยังใช้ได้";

    /// <summary>การปิดธงครั้ง<b>ล่าสุด</b>ของใบเสร็จนี้เป็นทาง (ค) ไหม — ป้าย <see cref="ResolvedMarker"/> ตัวสุดท้ายในหมายเหตุภายในต้องเป็น
    /// <see cref="KeptOriginalMarker"/> (ปิดด้วยทาง ค แล้วภายหลังปิดซ้ำด้วยใบลดหนี้ = ไม่ใช่) · หมายเหตุว่าง/ไม่มีป้าย = false · pure (V1H-O1)</summary>
    public static bool LastResolutionKeptOriginal(string? internalNotes)
    {
        if (string.IsNullOrEmpty(internalNotes)) return false;
        var i = internalNotes.LastIndexOf(ResolvedMarker, StringComparison.Ordinal);
        return i >= 0 && string.CompareOrdinal(internalNotes, i, KeptOriginalMarker, 0, KeptOriginalMarker.Length) == 0;
    }

    /// <summary>ใบเสร็จถือ VAT ที่ยังมีผลและติดธง แต่ใบต้นทาง (ใบแจ้งหนี้) ไม่มีวันที่ภาษีขายถึงกำหนดแล้ว = ถูกถอยไปแล้วทั้งที่ใบกำกับยังมีผล</summary>
    public static bool FlaggedReceiptVatUndone(bool receiptLive, decimal receiptVat, bool flagged, DocumentType sourceType, DateTime? sourceOutputVatDueAt)
        => receiptLive && flagged && receiptVat > 0.005m && sourceType == DocumentType.Invoice && sourceOutputVatDueAt == null;

    /// <summary>ตัวกลับของ JE ย้ายภาษีขายถึงกำหนด (§78/1) ลงคนละเดือนภาษี (ปี+เดือน) กับ JE ที่มันกลับไหม — ตัวกลับที่ถูกต้องลงวันที่ของ JE ย้ายภาษีเอง
    /// (คำตัดสินข้อ 48) ⇒ คนละเดือน = GL ภาษีขายของสองเดือนไม่ตรง ภ.พ.30 · วันเดียวกัน/เดือนเดียวกัน = ไม่จำแนก · pure (ข้อ 53)</summary>
    public static bool ReclassReversalMisdated(DateTime reclassEntryDate, DateTime reversalEntryDate)
        => reclassEntryDate.Year != reversalEntryDate.Year || reclassEntryDate.Month != reversalEntryDate.Month;

    /// <summary>ช่องตัวตน/เวลา/สถานะของใบแทนเองที่ต่างจากใบเดิมโดยธรรมชาติ — ไม่นับในการตรวจช่องเกิน</summary>
    private static readonly HashSet<string> OwnFields = new(StringComparer.Ordinal)
    {
        nameof(Document.Id), nameof(Document.CreatedAt), nameof(Document.UpdatedAt), nameof(Document.CreatedBy), nameof(Document.UpdatedBy),
        nameof(Document.IsDeleted), nameof(Document.DocumentNumber), nameof(Document.Status), nameof(Document.ReplacedByDocumentId),
        nameof(Document.ReplacesDocumentId), nameof(Document.ReplacementReason), nameof(Document.ReplacedAt),
        nameof(Document.ReplacementCarriesPostings), nameof(Document.InternalNotes), nameof(Document.AgingDays),
        nameof(Document.AgingLastEvaluatedAt), nameof(Document.RevisionNumber), nameof(Document.ReissueRequestedAt),
        nameof(Document.ReissueRequestedBy), nameof(Document.ReissueRequestJson),
    };

    /// <summary>ช่องที่ "ไม่ควรตามไป" ใบแทนที่ตรวจในรายงาน — ชุด <see cref="SettlementPaidReissue.DocumentNotCarriedFields"/> ตัวเดียว ลบช่องตัวตนของใบเอง</summary>
    public static IReadOnlyList<string> ExcessCheckFields { get; } =
        SettlementPaidReissue.DocumentNotCarriedFields.Where(f => !OwnFields.Contains(f)).ToList();

    /// <summary>ช่องที่ใบแทนถือค่าเดียวกับใบเดิม (ใบเดิมมีค่า ไม่ใช่ค่าเริ่มต้น) ทั้งที่ไม่ควรตามไป — ว่าง = ไม่พบ · pure (reflection บน entity)</summary>
    public static IReadOnlyList<string> CarriedExcess(Document original, Document replacement)
    {
        var result = new List<string>();
        foreach (var name in ExcessCheckFields)
        {
            var p = typeof(Document).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) continue;
            var o = p.GetValue(original);
            if (o == null) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t.IsValueType && o.Equals(Activator.CreateInstance(t))) continue;
            if (o is string str && string.IsNullOrWhiteSpace(str)) continue;
            if (Equals(o, p.GetValue(replacement))) result.Add(name);
        }
        return result;
    }
}

/// <summary>ใบเสร็จ 1 ใบในรายงานตรวจ (ข้อ 44)</summary>
public sealed record EtaxReviewReceiptRow(Guid ReceiptId, string ReceiptNumber, DateTime ReceiptDate, decimal VatAmount, Guid? SourceId,
    string? SourceNumber, DateTime? FlaggedAt, string Finding);

/// <summary>ใบแทน 1 ใบที่พาช่องของใบเดิมมาเกิน (ข้อ 44)</summary>
public sealed record EtaxReviewReplacementRow(Guid ReplacementId, string ReplacementNumber, Guid OriginalId, string OriginalNumber,
    DateTime? ReplacedAt, IReadOnlyList<string> CarriedFields);

/// <summary>ตัวกลับภาษีขายถึงกำหนดที่ลงคนละเดือนกับ JE ย้ายภาษี 1 คู่ (ข้อ 53) — นักบัญชีตรวจว่า ภ.พ.30/GL ของสองเดือนต้องปรับไหม</summary>
public sealed record EtaxReviewReversalRow(Guid SourceId, string SourceNumber, Guid ReclassEntryId, string ReclassEntryNumber, DateTime ReclassDate,
    Guid ReversalEntryId, string ReversalEntryNumber, DateTime ReversalDate, decimal Amount, string Finding);

/// <summary>รายงานอ่านอย่างเดียว (ข้อ 44 · ข้อ 53) — ไม่มีอะไรถูกแก้ · นักบัญชีตัดสินรายใบ</summary>
public sealed record EtaxReissueReviewReport(IReadOnlyList<EtaxReviewReceiptRow> FlaggedReceiptsVatUndone,
    IReadOnlyList<EtaxReviewReceiptRow> ResolvedBeforeSplit, IReadOnlyList<EtaxReviewReplacementRow> ReplacementsCarriedExcess,
    IReadOnlyList<EtaxReviewReversalRow> MisdatedOutputVatReversals);
