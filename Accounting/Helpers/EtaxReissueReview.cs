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
/// <item>รอบ 201 ทีม DV (A-DV1 · คำตัดสินข้อ 62 · 66) — อีก 4 กลุ่ม: <see cref="StuckOutputVatAfterPaymentVoid"/> (ยกเลิกการชำระหลายใบก่อนข้อ 50 ⇒ ภาษีขายค้าง) ·
/// <see cref="SubmittedEtaxVoidedWithoutEvidence"/> (แถว e-Tax ที่ส่งแล้วถูกยกเลิกในระบบโดยไม่มีหลักฐานก่อนข้อ 51) · ใบทาง (ค) ที่เสียยอดครอบก่อนตัวติดธงกลับของ V1I
/// (ตัวตัดสินเดียวกับเส้นยกเลิกการชำระ <see cref="DocumentVoidPreconditions.KeptOriginalCoverageLost"/>) · <see cref="EmailedEtaxVoidedInSystem"/> (แถว e-Tax ของใบที่ส่ง
/// e-Tax by Email ประทับเวลาแล้ว ถูกยกเลิกในระบบนี้ก่อน V1I-O2)</item>
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
    /// <remarks>รอบ 201 ทีม DV (A-DV2): ผู้อ่านเชิงธุรกิจเรียกผ่าน <see cref="KeptOriginal"/> ตัวเดียว ⇒ internal (เทสต์เข้าถึงได้)</remarks>
    internal static bool LastResolutionKeptOriginal(string? internalNotes)
    {
        if (string.IsNullOrEmpty(internalNotes)) return false;
        var i = internalNotes.LastIndexOf(ResolvedMarker, StringComparison.Ordinal);
        return i >= 0 && string.CompareOrdinal(internalNotes, i, KeptOriginalMarker, 0, KeptOriginalMarker.Length) == 0;
    }

    /// <summary>
    /// **การปิดธงครั้งล่าสุดของใบเสร็จนี้เป็นทาง (ค) "ใบกำกับเดิมยังใช้ได้" ไหม — ตัวอ่านตัวเดียว** (รอบ 201 ทีม DV · A-DV2 · คำตัดสินข้อ 65): คอลัมน์
    /// <c>Document.EtaxKeptOriginalAt</c> (ผู้เขียนตัวเดียว: ทาง ค ตั้ง · ทาง ก/ข ล้าง) เป็นหลัก · ใบเก่าที่คอลัมน์ว่าง (migration เติมไม่ได้/ยังไม่รัน) อ่านป้ายในหมายเหตุภายใน
    /// (<see cref="LastResolutionKeptOriginal"/>) เป็นทางสำรอง — ทางสำรองตอบ true ได้เฉพาะเมื่อป้ายตัวสุดท้ายเป็นทาง (ค) จึงไม่ขัดกับการล้างคอลัมน์ของทาง ก/ข
    /// (ทาง ก/ข เขียนป้าย <see cref="ResolvedMarker"/> ของตัวเองต่อท้ายทุกครั้ง) · pure
    /// </summary>
    public static bool KeptOriginal(DateTime? etaxKeptOriginalAt, string? internalNotes)
        => etaxKeptOriginalAt != null || LastResolutionKeptOriginal(internalNotes);

    /// <summary>
    /// **ภาษีขายถึงกำหนดค้างหลังยกเลิกการชำระ** (รอบ 201 ทีม DV · A-DV1 · คำตัดสินข้อ 62 — กลุ่ม "การชำระหลายใบที่ยกเลิกก่อนข้อ 50"): ใบแจ้งหนี้ที่ยังมีผล
    /// ยังตั้ง <c>OutputVatDueAt</c> (ภาษีขายย้าย 21913 → 21911 แล้ว) แต่ไม่เหลือยอดรับเลย และไม่มีใบเสร็จถือ VAT ที่มีผลถือจุดความรับผิดไว้ และเคยมีการจัดสรรหลายใบ
    /// ที่ถูกยกเลิก (ก่อนข้อ 50 เส้นจัดสรรหลายใบไม่ถอยภาษีเลย) ⇒ ภ.พ.30 ของเดือนรับเงินมีภาษีขายของเงินที่ไม่ได้รับจริง · อ่านอย่างเดียว (งวดที่อาจยื่นแล้ว
    /// ห้ามแก้เงียบ — แนวข้อ 20/44) · ใบที่ติดธงถอยไม่ได้ (<c>[VAT-UNDO-BLOCKED]</c>) ก็เข้ากลุ่มนี้ได้ — มองเห็นแล้วแต่ยังค้างจริง · pure
    /// </summary>
    public static bool StuckOutputVatAfterPaymentVoid(DocumentType sourceType, DocumentStatus sourceStatus, DateTime? outputVatDueAt,
        decimal paidAmount, bool liveVatReceipt, bool hadVoidedMultiDocAllocation)
        => sourceType == DocumentType.Invoice && sourceStatus != DocumentStatus.Voided && outputVatDueAt != null
           && paidAmount <= 0.005m && !liveVatReceipt && hadVoidedMultiDocAllocation;

    /// <summary>
    /// **แถว e-Tax ที่ส่งถึงกรมสรรพากรแล้วถูกยกเลิกในระบบนี้โดยไม่มีหลักฐาน** (รอบ 201 ทีม DV · A-DV1 · คำตัดสินข้อ 62 — กลุ่ม "ยกเลิกเงียบก่อนข้อ 51"): แถว Voided
    /// ที่มีเวลาส่ง (ถึงกรมสรรพากร/ผู้ให้บริการแล้ว) แต่<b>ไม่มี audit การตัดสินยกเลิกแถว</b> (<c>etax-voided-in-system</c> ของเส้น <c>EtaxInvoiceService.VoidAsync</c>
    /// ตั้งแต่ข้อ 51 — มีหลักฐาน หรือตัดสินว่ายังไม่ถึง) และเอกสารไม่มีบันทึก "ยกเลิกทาง e-Tax แล้ว" พร้อมหลักฐาน (<c>RD-ETAX-CANCEL-EVIDENCE</c>) ⇒ กรมสรรพากร
    /// อาจถือใบนี้อยู่ทั้งที่ระบบเราบอกว่ายกเลิก · แถวที่กรมสรรพากรปฏิเสธไปแล้วก็มีเวลาส่ง (แยกไม่ได้จากข้อมูลรุ่นก่อน) — ข้อความรายงานบอกให้ตรวจ · pure
    /// </summary>
    public static bool SubmittedEtaxVoidedWithoutEvidence(EtaxStatus rowStatus, DateTime? submittedAt, bool hasVoidDecisionAudit,
        bool documentCancellationRecorded)
        => rowStatus == EtaxStatus.Voided && submittedAt != null && !hasVoidDecisionAudit && !documentCancellationRecorded;

    /// <summary>
    /// **แถว e-Tax ของใบที่ส่ง e-Tax by Email ประทับเวลาแล้ว ถูกยกเลิกในระบบนี้** (รอบ 201 ทีม DV · A-DV1 · คำตัดสินข้อ 66): ใบถึงผู้ประทับเวลาของกรมสรรพากรแล้ว
    /// (เกณฑ์เดียวกับ <see cref="DocumentVoidPreconditions.EtaxEmailedWithRdTimestampAsync"/>) แต่ก่อน V1I-O2 เส้นยกเลิกแถวดูแค่สถานะแถว ⇒ ยกเลิกได้โดยไม่มีหลักฐาน
    /// และ audit เขียนว่า "ก่อนส่งถึงกรมสรรพากร" (audit ของรุ่นนั้นจึง<b>ไม่ใช่</b>หลักฐาน — ไม่ใช้ตัดออก) · เอกสารที่บันทึก "ยกเลิกทาง e-Tax แล้ว" พร้อมเลขอ้างอิง +
    /// ไฟล์หลักฐาน (<c>RD-ETAX-CANCEL-EVIDENCE</c>) = ยกเลิกถึงกรมสรรพากรแล้ว ไม่จำแนก · pure
    /// </summary>
    public static bool EmailedEtaxVoidedInSystem(EtaxStatus rowStatus, bool documentEmailedWithRdTimestamp, bool documentCancellationRecorded)
        => rowStatus == EtaxStatus.Voided && documentEmailedWithRdTimestamp && !documentCancellationRecorded;

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

/// <summary>ใบต้นทาง 1 ใบที่ภาษีขายถึงกำหนดค้างหลังยกเลิกการชำระ (รอบ 201 ทีม DV · ข้อ 62)</summary>
public sealed record EtaxReviewDocumentRow(Guid DocumentId, string DocumentNumber, DateTime DocumentDate, decimal VatAmount, DateTime? OutputVatDueAt,
    string Finding);

/// <summary>แถว e-Tax 1 แถวที่ถูกยกเลิกในระบบนี้ทั้งที่ถึงกรมสรรพากรแล้ว (รอบ 201 ทีม DV · ข้อ 62 · 66)</summary>
public sealed record EtaxReviewEtaxRow(Guid EtaxId, Guid DocumentId, string DocumentNumber, string? EtaxRefNumber, DateTime? SubmittedAt,
    DateTime? VoidedAt, string? VoidReason, string Finding);

/// <summary>รายงานอ่านอย่างเดียว (ข้อ 44 · ข้อ 53 · รอบ 201 ข้อ 62/66) — ไม่มีอะไรถูกแก้ · นักบัญชีตัดสินรายใบ</summary>
public sealed record EtaxReissueReviewReport(IReadOnlyList<EtaxReviewReceiptRow> FlaggedReceiptsVatUndone,
    IReadOnlyList<EtaxReviewReceiptRow> ResolvedBeforeSplit, IReadOnlyList<EtaxReviewReplacementRow> ReplacementsCarriedExcess,
    IReadOnlyList<EtaxReviewReversalRow> MisdatedOutputVatReversals,
    IReadOnlyList<EtaxReviewDocumentRow> StuckOutputVatAfterPaymentVoid,
    IReadOnlyList<EtaxReviewEtaxRow> SubmittedEtaxVoidedWithoutEvidence,
    IReadOnlyList<EtaxReviewReceiptRow> KeptOriginalCoverageLost,
    IReadOnlyList<EtaxReviewEtaxRow> EmailedEtaxVoidedInSystem)
{
    /// <summary>จำนวนรายการทั้งหมดทุกกลุ่ม — ตัวนับเดียวของข้อความบน endpoint</summary>
    public int Total => FlaggedReceiptsVatUndone.Count + ResolvedBeforeSplit.Count + ReplacementsCarriedExcess.Count + MisdatedOutputVatReversals.Count
        + StuckOutputVatAfterPaymentVoid.Count + SubmittedEtaxVoidedWithoutEvidence.Count + KeptOriginalCoverageLost.Count + EmailedEtaxVoidedInSystem.Count;
}
