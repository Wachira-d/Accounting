using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>หนึ่งผังในผังบัญชีของบริษัท (ข้อเท็จจริงที่การผูกบัญชีแม่ต้องใช้)</summary>
public sealed record ChartNode(Guid Id, string Code, AccountType Type, Guid? ParentId, int Level, bool IsSystem);

/// <summary>ข้อเสนอผูกบัญชีแม่หนึ่งแถว · <c>BlockedReason</c> ≠ null = ไม่ผูก (บอกเหตุผล + ทางไปต่อ)</summary>
public sealed record ChartLinkProposal(Guid AccountId, string Code, Guid? ParentId, string? ParentCode, int NewLevel, string? BlockedReason);

/// <summary>
/// **ผูกบัญชีแม่ของผังที่นำเข้า — กติกาตัวเดียว** (ฝ่ายค้านรอบส่งออก 2026-10-15 P1-2)
///
/// <para><b>บั๊กที่มา:</b> ผังที่นำเข้าจากไฟล์ (<c>ImportExportService.ImportAccountAsync</c>) และจากโปรแกรมเดิม (<c>AccountImportAdapter</c>)
/// ได้ <c>Level</c> ตามความยาวรหัสแต่<b>ไม่มี <c>ParentAccountId</c></b> ⇒ หัวบัญชีที่นำเข้า ("1130" · "113000" · "4100") ดู "ไม่มีบัญชีย่อย" ⇒
/// กติกาบัญชีคุม (<c>PostingAccountRule</c> = มีบัญชีย่อย) ปล่อยผ่าน และตัวหาผังตั้งต้นตามรหัสหยิบหัวบัญชีก่อนผังจริง</para>
///
/// <para><b>กติกา:</b> บัญชีแม่ = ผังที่รหัส<b>ยาวที่สุด</b>ซึ่งเป็นส่วนต้นของรหัสนี้ (prefix แท้) และประเภทเดียวกัน · ไม่ผูกเมื่อบัญชีแม่ที่จะได้
/// ยังไม่มีบัญชีย่อย<b>และมีรายการบัญชีแล้ว</b> (ผูกแล้วผังที่ใช้ลงรายการอยู่จะกลายเป็นบัญชีคุม — ยอดเดิมค้างในบัญชีคุม) ⇒ บอกเหตุผล ·
/// <paramref name="allowRehome"/> (ตอนนำเข้า) = ผังที่นำเข้าใหม่ซึ่งอยู่ "ระหว่าง" ผังเดิมกับบัญชีแม่ของมัน รับผังเดิมนั้นมาเป็นลูก
/// (หัวบัญชีมาทีหลังลูกในไฟล์) · ผังของแม่แบบระบบไม่ถูกย้ายแม่</para>
/// </summary>
public static class ChartParentLink
{
    public const string RuleCode = "COA-PARENT-LINK";

    /// <summary>บัญชีแม่ที่ควรเป็น (prefix แท้ที่ยาวที่สุด · ประเภทเดียวกัน) — null = ไม่มี</summary>
    private static ChartNode? FindParent(ChartNode account, IEnumerable<ChartNode> chart)
        => chart
            .Where(c => c.Id != account.Id && c.Type == account.Type
                && c.Code.Length < account.Code.Length
                && account.Code.StartsWith(c.Code, StringComparison.Ordinal))
            .OrderByDescending(c => c.Code.Length)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// วางแผนผูกบัญชีแม่ของ <paramref name="candidateIds"/> (เฉพาะผังที่ยังไม่มีบัญชีแม่) · <paramref name="postedAccountIds"/> = ผังที่มีรายการบัญชีแล้ว ·
    /// คืนข้อเสนอทุกแถวที่พิจารณา (ทั้งที่ผูกได้และไม่ได้) ตามลำดับรหัสสั้นไปยาว (หัวบัญชีก่อน)
    /// </summary>
    public static List<ChartLinkProposal> Plan(IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid> candidateIds,
        IReadOnlySet<Guid> postedAccountIds, bool allowRehome)
    {
        var byId = chart.ToDictionary(c => c.Id);
        var parentOf = chart.ToDictionary(c => c.Id, c => c.ParentId);
        var levelOf = chart.ToDictionary(c => c.Id, c => c.Level);
        var hasChildren = chart.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        var result = new List<ChartLinkProposal>();
        var candidates = candidateIds.Distinct()
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .OrderBy(c => c.Code.Length).ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList();

        string? Blocked(ChartNode parent)
            => !hasChildren.Contains(parent.Id) && postedAccountIds.Contains(parent.Id)
                ? $"บัญชีแม่ที่ควรเป็น ({parent.Code}) มีรายการบัญชีแล้ว — ผูกแล้วจะกลายเป็นบัญชีคุมที่มียอดค้าง · ย้ายยอดออกก่อน หรือผูกเองที่หน้าผังบัญชี"
                : null;

        foreach (var x in candidates)
        {
            if (parentOf[x.Id] == null && !x.IsSystem)
            {
                var parent = FindParent(x, chart);
                if (parent != null)
                {
                    var blocked = Blocked(parent);
                    var level = levelOf[parent.Id] + 1;
                    result.Add(new ChartLinkProposal(x.Id, x.Code, blocked == null ? parent.Id : null, parent.Code,
                        blocked == null ? level : x.Level, blocked));
                    if (blocked == null)
                    {
                        parentOf[x.Id] = parent.Id;
                        levelOf[x.Id] = level;
                        hasChildren.Add(parent.Id);
                    }
                }
            }

            if (!allowRehome || x.IsSystem || (postedAccountIds.Contains(x.Id) && !hasChildren.Contains(x.Id))) continue;
            // ผังเดิมที่ x ควรเป็นบัญชีแม่ (x คือ prefix ที่ยาวที่สุดของมัน) และแม่ปัจจุบันสั้นกว่า x (หรือไม่มีแม่) — ไม่ย้ายผังระบบ
            foreach (var y in chart.Where(c => c.Id != x.Id && !c.IsSystem && c.Type == x.Type
                         && c.Code.Length > x.Code.Length && c.Code.StartsWith(x.Code, StringComparison.Ordinal))
                     .OrderBy(c => c.Code.Length).ThenBy(c => c.Code, StringComparer.Ordinal))
            {
                if (FindParent(y, chart)?.Id != x.Id) continue;
                var current = parentOf[y.Id];
                if (current == x.Id) continue;
                if (current != null && byId.TryGetValue(current.Value, out var cur) && cur.Code.Length >= x.Code.Length) continue;
                var level = levelOf[x.Id] + 1;
                result.Add(new ChartLinkProposal(y.Id, y.Code, x.Id, x.Code, level, null));
                parentOf[y.Id] = x.Id;
                levelOf[y.Id] = level;
                hasChildren.Add(x.Id);
            }
        }
        return result;
    }
}
