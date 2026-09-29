using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>บรรทัด settlement ที่เก็บแล้ว (ช่องที่ใช้เทียบเนื้อหา) — <c>PayoutRef</c> = รอบโอนที่บรรทัดอยู่</summary>
public sealed record SettlementContentLine(Guid LineId, string? ExternalTxnId, string? ExternalOrderId, string? RawTypeLabel, decimal Amount,
    DateTime? TxnDate, string? PayoutRef);

/// <summary>แถว/บรรทัดที่เนื้อหาตรงกับบรรทัดของรอบโอนอื่น — <c>Id</c> = เลขแถวในไฟล์ (ตอนนำเข้า) หรือ id บรรทัด (ตอนพรีวิว/ลงบัญชี)</summary>
public sealed record SettlementContentHit<T>(T Id, IReadOnlyList<string> PayoutRefs);

/// <summary>
/// **"เนื้อหาตรงกับบรรทัดของรอบโอนอื่น" — ข้อเท็จจริงตัวเดียวของทั้งผู้นำเข้าและด่านลงบัญชี** (review198-S3 S3-4 → review198-S4 S4-4 · ทีม I รอบ 200)
///
/// <para>═══ ที่มา ═══ แถวที่ไม่มีเลขรายการของแพลตฟอร์มกันซ้ำด้วยคีย์เนื้อหา+ลายนิ้วมือไฟล์ ⇒ ไฟล์ฉบับแก้ (มีแถวเพิ่ม) ที่นำเข้าด้วยเลขรอบโอน<b>ที่พิมพ์ต่าง</b>
/// ได้คีย์ใหม่ทุกแถว ⇒ รอบโอนใหม่ซ้ำทั้งก้อน · เดิมมีแค่คำเตือนครั้งเดียวตอนนำเข้า — พรีวิว/ลงบัญชีไม่เตือน ⇒ ถ้าผู้ใช้พิมพ์ยอดโอนให้สมการลงตัว
/// ค่าธรรมเนียม/ปรับปรุงลงซ้ำได้ (ไม่มีตัวกันซ้ำอื่น) · ตัวนี้คิด<b>ใหม่ทุกครั้ง</b>จากข้อมูลปัจจุบัน (รอบที่ยกเลิกแล้วหลุดเอง · ไม่มีธงค้างใน DB)</para>
/// <para>═══ ไม่บล็อก ═══ แถวไม่มี id ที่เหมือนกันทุกช่องอาจเป็นรายการจริงคนละรายการ (ค่าธรรมเนียมถอนเงินของสองรอบในวันเดียวกัน · R-B5) — ระบบตัดสินแทนไม่ได้
/// และยังไม่มีกลไก "รับทราบรายแถว" ⇒ คำเตือนที่ติดอยู่กับพรีวิวทุกครั้ง + ทางไปต่อ (คำถามค้างถึงเจ้าของ: ควรเป็นด่านที่ต้องรับทราบไหม)</para>
/// </summary>
public static class SettlementContentOverlap
{
    /// <summary>จำนวนวันที่สูงสุดที่ค้นต่อครั้ง (กัน IN-list ยาว) — รอบโอนจริงไม่กี่สิบวัน</summary>
    public const int MaxDates = 500;

    /// <summary>
    /// ผู้สมัครที่เนื้อหาตรงกับบรรทัดแบบไม่มี id ของ <paramref name="others"/> — บรรทัดที่อยู่ใน <paramref name="claimed"/> (แถวในไฟล์อ้างด้วยคีย์แล้ว) ไม่นับ ·
    /// ผู้สมัครที่ <c>ContentKey</c> null (มี id / มาจาก intent / ไม่มีวันที่) ไม่เทียบ
    /// </summary>
    public static IReadOnlyList<SettlementContentHit<T>> Find<T>(IReadOnlyList<(T Id, string? ContentKey)> candidates,
        IReadOnlyList<SettlementContentLine> others, IReadOnlySet<string>? claimed = null)
    {
        var refsByContent = others
            .Where(l => SettlementTxnKey.IsRowKey(l.ExternalTxnId) && (claimed == null || !claimed.Contains(l.ExternalTxnId!)))
            .GroupBy(l => SettlementTxnKey.ContentKey(l.ExternalOrderId, l.RawTypeLabel, l.Amount, l.TxnDate), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.PayoutRef ?? "").Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var hits = new List<SettlementContentHit<T>>();
        foreach (var (id, key) in candidates)
            if (key != null && refsByContent.TryGetValue(key, out var refs))
                hits.Add(new SettlementContentHit<T>(id, refs));
        return hits;
    }

    /// <summary>ผู้สมัครจากบรรทัดของรอบโอน (พรีวิว/ลงบัญชี) — เฉพาะบรรทัดแบบไม่มี id ที่มีวันที่ · คีย์เนื้อหาจากค่าที่เก็บ (ตัด PII แล้ว) แบบเดียวกับผู้นำเข้า</summary>
    internal static IReadOnlyList<(Guid Id, string? ContentKey)> BatchCandidates(IEnumerable<SettlementLine> lines)
        => lines.Where(l => SettlementTxnKey.IsRowKey(l.ExternalTxnId) && l.TxnDate != null)
            .Select(l => (l.Id, (string?)SettlementTxnKey.ContentKey(l.ExternalOrderId, l.RawTypeLabel, l.Amount, l.TxnDate)))
            .ToList();

    /// <summary>บรรทัดแบบมีคีย์ของรอบโอนอื่นในช่องทางเดียวกันที่ลงวันที่ใน <paramref name="dates"/> (tenant · ไม่นับที่ยกเลิก/ลบแล้ว — ตัวกรองส่วนกลาง IsDeleted)</summary>
    public static async Task<List<SettlementContentLine>> LoadOtherBatchesAsync(AccountingDbContext db, Guid companyId, Guid channelId,
        Guid? excludeBatchId, IEnumerable<DateTime> dates, CancellationToken ct)
    {
        var dateList = dates.Distinct().Take(MaxDates).ToList();
        if (dateList.Count == 0) return new List<SettlementContentLine>();
        return await db.SettlementLines.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.ChannelId == channelId && l.ExternalTxnId != null && l.TxnDate != null
                && dateList.Contains(l.TxnDate.Value) && (excludeBatchId == null || l.BatchId != excludeBatchId))
            .Select(l => new SettlementContentLine(l.Id, l.ExternalTxnId, l.ExternalOrderId, l.RawTypeLabel, l.Amount, l.TxnDate,
                l.Batch.PayoutRef))
            .ToListAsync(ct);
    }

    /// <summary>ผลของรอบโอนนี้ (โหลด + เทียบ) สำหรับพรีวิว/ลงบัญชี — บรรทัดเดียวที่ผู้ลงบัญชีเรียก</summary>
    public static async Task<IReadOnlyList<SettlementContentHit<Guid>>> ForBatchAsync(AccountingDbContext db, Guid companyId, Guid channelId,
        Guid batchId, IReadOnlyList<SettlementLine> lines, CancellationToken ct)
    {
        var candidates = BatchCandidates(lines);
        if (candidates.Count == 0) return Array.Empty<SettlementContentHit<Guid>>();
        var dates = lines.Where(l => SettlementTxnKey.IsRowKey(l.ExternalTxnId) && l.TxnDate != null).Select(l => l.TxnDate!.Value);
        var others = await LoadOtherBatchesAsync(db, companyId, channelId, batchId, dates, ct);
        return Find(candidates, others);
    }

    /// <summary>
    /// เติมคำเตือน (ไม่บล็อก) <see cref="SettlementPlanIssueCode.ContentOverlapElsewhere"/> ลงแผนที่ผ่านด่านแล้ว — <c>CanPost</c> ไม่เปลี่ยน ·
    /// ไม่มีผลตรง ⇒ คืนแผนเดิม
    /// </summary>
    public static SettlementPostingPlan Annotate(SettlementPostingPlan plan, IReadOnlyList<SettlementContentHit<Guid>> hits,
        IReadOnlyList<SettlementLine> lines)
    {
        if (hits.Count == 0) return plan;
        var ids = hits.Select(h => h.Id).ToList();
        var idSet = ids.ToHashSet();
        var amount = lines.Where(l => idSet.Contains(l.Id)).Sum(l => l.Amount);
        var refs = hits.SelectMany(h => h.PayoutRefs).Distinct(StringComparer.Ordinal).Take(5).ToList();
        var issue = new SettlementPlanIssue(SettlementPlanIssueCode.ContentOverlapElsewhere, false,
            $"{hits.Count:N0} บรรทัดไม่มีเลขรายการของแพลตฟอร์ม และเนื้อหาตรงทุกช่อง (ออเดอร์ · ป้าย · ยอด · วันที่) กับบรรทัดในรอบโอน "
            + $"{string.Join(", ", refs)} ที่นำเข้าไว้แล้ว",
            "ถ้าเป็นรายการเดียวกัน (ไฟล์ช่วงวันทับกัน หรือไฟล์เดิม/ไฟล์ฉบับแก้ที่นำเข้าด้วยเลขรอบโอนที่พิมพ์ต่าง) ห้ามลงบัญชีรอบนี้ — ยกเลิกรอบโอนนี้ "
            + "แล้วนำเข้าไฟล์อีกครั้งด้วยเลขรอบโอนเดิม · ถ้าเป็นคนละรายการจริง (หน้าตาเหมือนกัน เช่น ค่าธรรมเนียมถอนเงินของสองรอบในวันเดียวกัน) ลงบัญชีได้ตามปกติ",
            ids, Math.Round(amount, 2, MidpointRounding.AwayFromZero));
        return plan with { Issues = plan.Issues.Append(issue).ToList() };
    }
}
