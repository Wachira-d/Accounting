namespace Accounting.Helpers;

/// <summary>ไฟล์สลิป 1 ไฟล์ที่ระบบเก็บไว้ (แถว <c>FileAttachment</c> ของการจอง) — ข้อมูลดิบให้ <see cref="LodgingSlipGallery.Build"/></summary>
public sealed record LodgingSlipFileRow(Guid AttachmentId, string? StoragePath, string? OriginalFileName, DateTime UploadedAt);

/// <summary>เหตุการณ์สลิปจาก audit ของการจอง — <c>Action</c> = <c>GuestUploadedSlip</c> / <c>SlipRejected</c> ·
/// <c>SlipKey</c> = storage key ของสลิปที่เหตุการณ์นี้พูดถึง (null = แถว audit รุ่นก่อนที่ไม่ได้จดไว้)</summary>
public sealed record LodgingSlipEvent(string Action, string? SlipKey, string? Reason, string? Reference, DateTime At, decimal? DeclaredAmount = null);

/// <summary>สถานะของสลิปแต่ละใบในแกลเลอรีฝั่งพนักงาน (ส่งออกเป็น "ชื่อ")</summary>
public enum LodgingSlipItemState
{
    /// <summary>สลิปล่าสุดที่การจองอ้างอยู่ (<c>PaymentSlipUrl</c>)</summary>
    Current,
    /// <summary>พนักงานปฏิเสธแล้ว (มีเหตุผล)</summary>
    Rejected,
    /// <summary>แขกส่งสลิปใหม่ทับ — ไม่ใช่สลิปที่การจองอ้างแล้ว</summary>
    Superseded,
}

/// <summary>สลิป 1 ใบที่หน้าจอวาด — <c>AttachmentId = null</c> = สลิปปัจจุบันของแถวรุ่นก่อนที่ไม่มีแถวไฟล์แนบ (เปิดผ่านเส้น <c>/slip</c> เดิม)</summary>
public sealed record LodgingSlipGalleryItem(
    Guid? AttachmentId, string StorageKey, DateTime? UploadedAt, string? OriginalFileName,
    LodgingSlipItemState State, string StateLabel, string? RejectedReason, DateTime? RejectedAt, string? Reference,
    decimal? DeclaredAmount = null);

/// <summary>
/// **รวมสลิปทุกใบของการจองเป็นรายการเดียวให้พนักงานดู** (ผู้ใช้รายงาน 2026-10-22: โมดัลการจองบอก "เปิดดูสลิป" แต่ไม่มีที่ให้ดูสลิปจริง)
///
/// <para>ที่มาของข้อมูล: (1) ไฟล์ — ทุกครั้งที่แขกส่งสลิป <c>SaveSlipFileAsync</c> เขียนแถว <c>FileAttachment</c> (EntityType <c>LodgingReservation</c>)
/// ⇒ สลิปที่ถูกปฏิเสธ/ถูกส่งทับยังอยู่ครบ (หลักฐาน) · (2) สลิปปัจจุบัน = <c>PaymentSlipUrl</c> · (3) เหตุผลที่ปฏิเสธ/เลขอ้างอิง = audit ของการจอง</para>
/// <para>ตรรกะล้วน (ไม่แตะ DB/ดิสก์) ⇒ เทสต์ได้ · ด่านพาธไฟล์จริงยังเป็น <see cref="LodgingSlipPath.Resolve"/> ตัวเดียว</para>
/// </summary>
public static class LodgingSlipGallery
{
    public const string UploadedAction = "GuestUploadedSlip";
    public const string RejectedAction = "SlipRejected";
    public const string DeclaredAmountRuleCode = "LODGING-SLIP-DECLARED-AMOUNT";
    /// <summary>เพดานยอดที่แขกกรอกได้ (กันพิมพ์ผิดหลักเป็นพันล้าน) — ไม่ใช่ด่านการเงิน ยอดจริงบันทึกโดยพนักงาน</summary>
    public const decimal DeclaredAmountMax = 10_000_000m;

    /// <summary>ยอดที่แขกกรอกตอนส่งสลิป (R-52): ไม่กรอก/0 = null (ไม่รู้ ≠ 0) · ติดลบ/เกินเพดาน = ปฏิเสธ (ไม่เก็บค่าที่แต่งขึ้น) · ปัด 2 ตำแหน่ง</summary>
    public static (decimal? Amount, string? Problem) NormalizeDeclaredAmount(decimal? declared)
    {
        if (declared is not decimal v || v == 0m) return (null, null);
        if (v < 0m) return (null, "ยอดที่โอนต้องไม่ติดลบ");
        if (v > DeclaredAmountMax) return (null, $"ยอดที่โอนเกิน {DeclaredAmountMax:N0} — ตรวจจำนวนหลักอีกครั้ง");
        return (Math.Round(v, 2, MidpointRounding.AwayFromZero), null);
    }

    /// <summary>ใหม่สุดก่อน · ไฟล์ซ้ำ key เดียวกันนับครั้งเดียว · สลิปปัจจุบันที่ไม่มีแถวไฟล์แนบ (แถวรุ่นก่อน) เติมให้เป็นรายการแรก</summary>
    /// <param name="currentDeclaredAmount">ยอดที่แขกกรอกของสลิปปัจจุบัน (<c>LodgingReservation.SlipDeclaredAmount</c>) — สลิปใบก่อนอ่านจาก audit</param>
    public static List<LodgingSlipGalleryItem> Build(
        IEnumerable<LodgingSlipFileRow> files, string? currentKey, DateTime? currentUploadedAt, string? currentReference,
        IEnumerable<LodgingSlipEvent> events, decimal? currentDeclaredAmount = null)
    {
        var evs = events.ToList();
        var items = new List<LodgingSlipGalleryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files.OrderByDescending(x => x.UploadedAt))
        {
            var key = LodgingSlipPath.StorageKey(f.StoragePath);
            if (key == null || !seen.Add(key)) continue;
            items.Add(Item(f.AttachmentId, key, f.UploadedAt, f.OriginalFileName, currentKey, currentReference, currentDeclaredAmount, evs));
        }
        if (!string.IsNullOrWhiteSpace(currentKey) && !seen.Contains(currentKey))
            items.Add(Item(null, currentKey, currentUploadedAt, null, currentKey, currentReference, currentDeclaredAmount, evs));
        return items
            .OrderByDescending(i => i.State == LodgingSlipItemState.Current)
            .ThenByDescending(i => i.UploadedAt ?? DateTime.MinValue)
            .ToList();
    }

    private static LodgingSlipGalleryItem Item(Guid? attachmentId, string key, DateTime? uploadedAt, string? name,
        string? currentKey, string? currentReference, decimal? currentDeclaredAmount, List<LodgingSlipEvent> evs)
    {
        var isCurrent = string.Equals(key, currentKey, StringComparison.Ordinal);
        var uploadEvent = evs
            .Where(e => e.Action == UploadedAction && string.Equals(e.SlipKey, key, StringComparison.Ordinal))
            .OrderByDescending(e => e.At).FirstOrDefault();
        // ยอดที่แขกกรอก: สลิปปัจจุบันจากแถวการจอง · ใบก่อนจาก audit ของใบนั้น · ไม่มี = null (ไม่แต่งค่า)
        var declared = isCurrent ? currentDeclaredAmount ?? uploadEvent?.DeclaredAmount : uploadEvent?.DeclaredAmount;
        var rejected = isCurrent ? null : evs
            .Where(e => e.Action == RejectedAction && string.Equals(e.SlipKey, key, StringComparison.Ordinal))
            .OrderByDescending(e => e.At).FirstOrDefault();
        var reference = isCurrent && !string.IsNullOrWhiteSpace(currentReference) ? currentReference : evs
            .Where(e => e.Action == UploadedAction && string.Equals(e.SlipKey, key, StringComparison.Ordinal))
            .OrderByDescending(e => e.At).Select(e => e.Reference).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        var state = isCurrent ? LodgingSlipItemState.Current
            : rejected != null ? LodgingSlipItemState.Rejected : LodgingSlipItemState.Superseded;
        return new LodgingSlipGalleryItem(attachmentId, key, uploadedAt, name, state, Label(state),
            rejected?.Reason, rejected?.At, reference, declared);
    }

    /// <summary>ป้ายไทยของสถานะ — ตัวเดียว (หน้าเว็บแสดงตามนี้ ไม่มีตารางป้ายของตัวเอง)</summary>
    public static string Label(LodgingSlipItemState s) => s switch
    {
        LodgingSlipItemState.Current => "สลิปล่าสุด",
        LodgingSlipItemState.Rejected => "ถูกปฏิเสธแล้ว",
        _ => "สลิปก่อนหน้า (แขกส่งใบใหม่ทับ)",
    };
}
