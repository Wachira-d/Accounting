using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Accounting.Helpers;

/// <summary>ผู้ซื้อ ณ ตอนแสดงคำขอ (ชื่อ · เลขภาษี · สาขา · ที่อยู่) — ค่าจากทะเบียนผู้ติดต่อ</summary>
public sealed record ReissuePartySnapshot(string? Name, string? TaxId, string? BranchCode, string? Address);

/// <summary>คำบรรยาย 1 บรรทัดที่คำขอเปลี่ยน — เดิม → ใหม่</summary>
public sealed record ReissueLineChange(Guid LineId, int LineOrder, string? Before, string After);

/// <summary>
/// **คำขอ "ยกเลิกและออกใบแทน" ฉบับเต็มที่ผู้ยืนยันเห็น** (รอบ 200 ทีม V1G · คำตัดสินข้อ 49 · RV1F-5) — ผู้ซื้อ/เลขภาษี/สาขา/ที่อยู่ เดิม → ใหม่ ·
/// หมายเหตุ · คำบรรยายรายบรรทัด · เหตุผล + <see cref="RequestHash"/> ที่การยืนยันต้องส่งกลับมา
/// </summary>
/// <param name="RequestHash">SHA-256 ของเนื้อหาข้างต้นทั้งหมด (<see cref="SettlementPaidReissueRequestView.Hash"/>) — ผู้ขอแก้คำขอ หรือทะเบียนผู้ซื้อที่คำขอชี้
/// ถูกแก้ ⇒ ค่าเปลี่ยน ⇒ การยืนยันที่ส่งค่าเดิมถูกปฏิเสธ (ต้องดูใหม่แล้วยืนยันใหม่)</param>
public sealed record ReissueRequestView(
    string? RequestedBy, DateTime? RequestedAt, string Reason,
    ReissuePartySnapshot BuyerBefore, ReissuePartySnapshot BuyerAfter, bool BuyerChanged,
    string? NotesBefore, string? NotesAfter, bool NotesChanged,
    IReadOnlyList<ReissueLineChange> Lines, string RequestHash);

/// <summary>
/// ตัวประกอบ + hash ของคำขอออกใบแทนที่รอคนที่สอง — <b>ตัวเดียว</b>ของฝั่งแสดง (<c>GetDocumentAsync</c>) และฝั่งยืนยัน
/// (<c>ReissueSettlementPaidDocumentAsync</c> ใต้ล็อก) · กฎเหล็ก #4 C "Hash/signature มี canonical function เดียว" · G6: pure
/// <para>ที่มา (ฝ่ายค้านรอบสอง RV1F-5): ผู้ยืนยันเห็นแค่ "เหตุผล" ⇒ ผู้ขอเลือกผู้ซื้อเป็นนิติบุคคลอื่นแล้วพิมพ์ว่า "สะกดชื่อผิด" · คนที่สองกดตาม ⇒
/// SoD ผ่านตามรูปแบบแต่ไม่มีใครเห็นสิ่งที่อนุมัติ ("การอนุมัติที่มองไม่เห็นเนื้อหา = ไม่มีการอนุมัติ")</para>
/// </summary>
public static class SettlementPaidReissueRequestView
{
    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    /// <summary>ประกอบภาพคำขอ — <paramref name="requestedLines"/> = (Id บรรทัด, คำบรรยายใหม่) จากคำขอที่บันทึกไว้ · บรรทัดที่คำบรรยายไม่เปลี่ยนไม่แสดง</summary>
    /// <param name="oldLines">บรรทัดของใบเดิม (Id, ลำดับ, คำบรรยาย)</param>
    public static ReissueRequestView Build(string? requestedBy, DateTime? requestedAt, string? reason,
        ReissuePartySnapshot buyerBefore, ReissuePartySnapshot buyerAfter, bool buyerChanged,
        string? notesBefore, string? requestedNotes,
        IEnumerable<(Guid Id, int LineOrder, string? Description)> oldLines,
        IEnumerable<(Guid LineId, string? Description)> requestedLines)
    {
        var desc = requestedLines.Where(l => l.Description != null)
            .GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Last().Description!.Trim());
        var lines = oldLines
            .Where(l => desc.TryGetValue(l.Id, out var d) && !string.Equals(d, l.Description?.Trim(), StringComparison.Ordinal))
            .OrderBy(l => l.LineOrder)
            .Select(l => new ReissueLineChange(l.Id, l.LineOrder, l.Description, desc[l.Id]))
            .ToList();
        var notesAfter = requestedNotes ?? notesBefore;
        var view = new ReissueRequestView(requestedBy, requestedAt, (reason ?? "").Trim(), buyerBefore, buyerAfter,
            buyerChanged || buyerBefore != buyerAfter, notesBefore, notesAfter,
            !string.Equals(notesBefore ?? "", notesAfter ?? "", StringComparison.Ordinal), lines, "");
        return view with { RequestHash = Hash(view) };
    }

    /// <summary>SHA-256 (hex ตัวเล็ก) ของเนื้อหาคำขอ — ไม่รวมช่อง hash เอง · ลำดับช่องตามการประกาศ record (JSON ไม่ย่อหน้า) · ตัวเดียวของทั้งสองฝั่ง</summary>
    public static string Hash(ReissueRequestView v)
    {
        var json = JsonSerializer.Serialize(v with { RequestHash = "" }, Canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>การยืนยันผูกกับคำขอที่ผู้ยืนยันเห็นจริงไหม — null = ตรง · ข้อความไทยพร้อมทางไปต่อเมื่อไม่ตรง/ไม่ส่งมา</summary>
    /// <param name="presentedHash">hash ที่หน้าจอส่งมาพร้อมคำยืนยัน</param>
    /// <param name="current">ภาพคำขอที่ประกอบใหม่ใต้ล็อก</param>
    public static string? ConfirmMismatch(string? presentedHash, ReissueRequestView current)
    {
        if (string.IsNullOrWhiteSpace(presentedHash))
            return "การยืนยันต้องอ้างคำขอที่เห็นบนหน้าจอ — เปิดหน้าเอกสารใหม่ ตรวจผู้ซื้อ/หมายเหตุ/คำบรรยายที่ขอเปลี่ยน แล้วกดยืนยันอีกครั้ง · ระบบยังไม่ได้แตะอะไร";
        if (!string.Equals(presentedHash.Trim(), current.RequestHash, StringComparison.OrdinalIgnoreCase))
            return "คำขอถูกเปลี่ยนหลังจากที่คุณเปิดดู (ผู้ขอส่งคำขอใหม่ หรือข้อมูลผู้ซื้อในทะเบียนถูกแก้) — โหลดหน้าเอกสารใหม่ ตรวจคำขอฉบับล่าสุด "
                + "แล้วยืนยันอีกครั้ง · ระบบยังไม่ได้แตะอะไร";
        return null;
    }
}
