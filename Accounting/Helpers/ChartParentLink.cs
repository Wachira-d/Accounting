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
/// ยังไม่มีบัญชีย่อย<b>และถูกใช้อยู่แล้ว</b> (รายการบัญชี · ยอดยกมา · สินค้า/ผู้ติดต่อ/ค่าตั้ง/บัญชีธนาคารที่ปักผังนี้ — <see cref="ChartAccountReferences"/>:
/// ผูกแล้วผังที่ใช้อยู่จะกลายเป็นบัญชีคุม — ยอด/ค่าตั้งเดิมค้างในบัญชีคุม) ⇒ บอกเหตุผล ·
/// <paramref name="allowRehome"/> (ตอนนำเข้า) = ผังที่นำเข้าใหม่ซึ่งอยู่ "ระหว่าง" ผังเดิมกับบัญชีแม่ของมัน รับผังเดิมนั้นมาเป็นลูก
/// (หัวบัญชีมาทีหลังลูกในไฟล์) · ผังของแม่แบบระบบไม่ถูกย้ายแม่</para>
///
/// <para><b>รอบ 2 (ฝ่ายค้าน 2026-10-15 P1-4):</b> ผังแม่แบบระบบที่<b>ยังไม่มีบัญชีย่อย</b> (ผังลงรายการ เช่น 11310 ลูกหนี้การค้า · 21210 เจ้าหนี้การค้า)
/// <b>ไม่เป็นบัญชีแม่ของผังนำเข้าเด็ดขาด</b> — เดิมผัง PEAK "113101" ถูกผูกใต้ 11310 ⇒ 11310 กลายเป็นบัญชีคุม ⇒ ลูกหนี้ตั้งต้นพลิกไป 11320 ·
/// ตอนนี้ข้ามไปหัวบัญชีที่สั้นกว่าถัดไป (113) · ผังที่ผูกแล้วระดับเปลี่ยน ⇒ ลูกหลานทุกชั้นปรับระดับตาม (<see cref="Relevel"/>)</para>
/// </summary>
public static class ChartParentLink
{
    public const string RuleCode = "COA-PARENT-LINK";

    /// <summary>action ของ audit เมื่อย้ายผังออกจากใต้ผังลงรายการของแม่แบบ (<see cref="PlanTemplateLeafRescue"/> · รอบ 3 P2-7)</summary>
    public const string LeafRescueAction = "coa-template-leaf-rescue";

    /// <summary>action ของ audit เมื่อผู้ใช้กดผูกบัญชีแม่จากรายงานตรวจ (<c>ChartParentLinker.RepairAsync</c> — มีตั้งแต่ 825f5c0e)</summary>
    public const string ParentLinkAction = "coa-parent-link";

    /// <summary>action ของ audit เมื่อการนำเข้าย้ายแม่ของผังเดิม (<c>ChartParentLinker.LinkImportedAsync</c> · รอบ 2 P2-8 เป็นต้นไป)</summary>
    public const string ParentLinkImportAction = "coa-parent-link-import";

    /// <summary>ผู้สร้าง (<c>CreatedBy</c>) ของผังที่นำเข้าจากไฟล์/ศูนย์นำเข้า (<c>ImportExportService.ImportAccountAsync</c> · รอบ 4 P2-c เป็นต้นไป) +
    /// actor ของ <c>LinkImportedAsync</c> ทางนั้น</summary>
    public const string FileImportActor = "import:chart-of-accounts";

    /// <summary>ต้น <c>CreatedBy</c> ของผังที่นำเข้าจากโปรแกรมเดิม (Express/PEAK/FlowAccount · <c>AccountImportAdapter</c> — มีตั้งแต่ก่อน 825f5c0e)</summary>
    public const string MigrateActorPrefix = "Migrate:";

    /// <summary>
    /// ผังนี้<b>สร้างจากการนำเข้า</b> (pure · รอบ 4 P2-c) — ผังที่นำเข้าเกิดมาไม่มีแม่ ⇒ แม่ที่มี = การผูกของระบบ (<c>LinkImportedAsync</c>/<c>RepairAsync</c>) ·
    /// ผังที่ผู้ใช้สร้างเองที่หน้าผังบัญชี (<c>CreateAccountAsync</c> เลือกแม่ได้ทุกผัง) ไม่มีเครื่องหมายนี้ — เป็นหลักฐานหนึ่งในสามของ
    /// <c>ChartParentLinker.ImportLinkedAccountIdsAsync</c> (อีกสอง: แถวพักของศูนย์นำเข้าที่สร้างผังนี้ · audit การผูก)
    /// </summary>
    public static bool IsImportCreated(string? createdBy)
        => createdBy != null && (createdBy == FileImportActor || createdBy.StartsWith(MigrateActorPrefix, StringComparison.Ordinal));

    /// <summary>ส่วนของ JSON <c>NewValues</c> ใน audit ที่บอกว่าระบบผูกบัญชีแม่ (<see cref="ParentLinkAction"/> และ <see cref="ParentLinkImportAction"/> ขึ้นต้นเหมือนกัน)</summary>
    public const string ParentLinkAuditMarker = "\"action\":\"" + ParentLinkAction;

    /// <summary>บัญชีแม่ที่ควรเป็น (prefix แท้ที่ยาวที่สุด · ประเภทเดียวกัน · <paramref name="canBeParent"/>) — null = ไม่มี</summary>
    private static ChartNode? FindParent(ChartNode account, IEnumerable<ChartNode> chart, Func<ChartNode, bool> canBeParent)
        => chart
            .Where(c => c.Id != account.Id && c.Type == account.Type
                && c.Code.Length < account.Code.Length
                && account.Code.StartsWith(c.Code, StringComparison.Ordinal))
            .OrderByDescending(c => c.Code.Length)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .FirstOrDefault(canBeParent);

    /// <summary>
    /// ผังที่ต้องถามว่า "ถูกใช้อยู่ไหม" (<see cref="ChartAccountReferences.ReferencedAccountIdsAsync"/> ไล่ ~40 ตารางต่อครั้ง) ก่อนวางแผน (pure · รอบ 3 P2-2):
    /// ผังที่<b>อาจถูกทำเป็นบัญชีแม่</b> = รหัสเป็นส่วนต้นของผู้สมัคร · ประเภทเดียวกัน · ยังไม่มีลูก · ไม่ใช่ผังระบบ (ผังระบบที่ไม่มีลูกเป็นแม่ไม่ได้อยู่แล้ว) +
    /// ผู้สมัครเอง (ซ่อม/รายงาน: ผู้สมัครอาจเป็นแม่ของผู้สมัครอีกตัว) · <b>ตัดผังที่เพิ่งสร้าง</b> (<paramref name="unsavedIds"/> — ยังไม่มีใครอ้างได้) ·
    /// <para>บั๊กที่มา: นำเข้าผังทีละแถว ผู้สมัคร = ผังใหม่ที่ยังไม่บันทึก อยู่ในรายการเสมอ ⇒ ไล่ทุก FK ทุกแถว (~41 คิวรีต่อแถว) ทั้งที่ไม่มีผังเดิมให้ตรวจ ·
    /// ตอนนี้ว่าง ⇒ ผู้เรียกไม่ยิงคิวรี</para>
    /// </summary>
    public static List<Guid> InUseCheckCandidates(IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid> candidateIds,
        IReadOnlyCollection<Guid> unsavedIds)
    {
        var withChildren = chart.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        var candidates = chart.Where(c => candidateIds.Contains(c.Id)).ToList();
        return chart
            .Where(c => !unsavedIds.Contains(c.Id))
            .Where(c => candidateIds.Contains(c.Id)
                || (!c.IsSystem && !withChildren.Contains(c.Id)
                    && candidates.Any(x => x.Type == c.Type && x.Code.Length > c.Code.Length && x.Code.StartsWith(c.Code, StringComparison.Ordinal))))
            .Select(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// วางแผนผูกบัญชีแม่ของ <paramref name="candidateIds"/> (เฉพาะผังที่ยังไม่มีบัญชีแม่) · <paramref name="inUseAccountIds"/> = ผังที่ถูกใช้อยู่แล้ว
    /// (รายการบัญชี · ยอดยกมา · ค่าตั้ง/ผังที่ปักไว้ — <see cref="ChartAccountReferences.ReferencedAccountIdsAsync"/>) ·
    /// คืนข้อเสนอทุกแถวที่พิจารณา (ทั้งที่ผูกได้และไม่ได้) ตามลำดับรหัสสั้นไปยาว (หัวบัญชีก่อน)
    /// </summary>
    public static List<ChartLinkProposal> Plan(IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid> candidateIds,
        IReadOnlySet<Guid> inUseAccountIds, bool allowRehome)
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
            => !hasChildren.Contains(parent.Id) && inUseAccountIds.Contains(parent.Id)
                ? $"บัญชีแม่ที่ควรเป็น ({parent.Code}) ถูกใช้อยู่แล้ว (มีรายการบัญชี/ยอดยกมา หรือถูกตั้งเป็นผังของสินค้า/ผู้ติดต่อ/ค่าตั้ง/บัญชีธนาคาร) — "
                    + "ผูกแล้วจะกลายเป็นบัญชีคุมที่มียอด/ค่าตั้งค้าง · ย้ายยอด/ค่าตั้งออกก่อน หรือผูกเองที่หน้าผังบัญชี"
                : null;
        // รอบ 2 P1-4: ผังแม่แบบระบบที่ยังไม่มีบัญชีย่อย = ผังลงรายการ ⇒ ไม่เป็นบัญชีแม่ (ข้ามไปหัวบัญชีที่สั้นกว่า)
        bool CanBeParent(ChartNode c) => !(c.IsSystem && !hasChildren.Contains(c.Id));

        foreach (var x in candidates)
        {
            if (parentOf[x.Id] == null && !x.IsSystem)
            {
                var parent = FindParent(x, chart, CanBeParent);
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

            if (!allowRehome || x.IsSystem || (inUseAccountIds.Contains(x.Id) && !hasChildren.Contains(x.Id))) continue;
            // ผังเดิมที่ x ควรเป็นบัญชีแม่ (x คือ prefix ที่ยาวที่สุดของมัน) และแม่ปัจจุบันสั้นกว่า x (หรือไม่มีแม่) — ไม่ย้ายผังระบบ
            foreach (var y in chart.Where(c => c.Id != x.Id && !c.IsSystem && c.Type == x.Type
                         && c.Code.Length > x.Code.Length && c.Code.StartsWith(x.Code, StringComparison.Ordinal))
                     .OrderBy(c => c.Code.Length).ThenBy(c => c.Code, StringComparer.Ordinal))
            {
                if (FindParent(y, chart, CanBeParent)?.Id != x.Id) continue;
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

    /// <summary>รหัสหัวกลุ่มของแม่แบบยาวไม่เกิน 3 หลัก (1 · 11 · 113) — ผังลงรายการของแม่แบบ 5 หลัก (11310)</summary>
    private const int TemplateGroupCodeMaxLength = 3;

    /// <summary>ผังลงรายการของแม่แบบระบบ = ผังระบบที่รหัสยาวกว่าหัวกลุ่มของแม่แบบ (ตัดสินด้วยรูปรหัสของแม่แบบ ไม่ใช่ระดับ — <c>tools/posting_level_check.py</c>)</summary>
    private static bool IsTemplateLeaf(ChartNode c) => c.IsSystem && c.Code.Length > TemplateGroupCodeMaxLength;

    /// <summary>
    /// <b>ผังที่ถูกผูกใต้ผังลงรายการของแม่แบบ</b> (pure · รอบ 3 P2-7) — ผลของการผูกบัญชีแม่รอบแรก (825f5c0e: ผัง PEAK "113101" ใต้ 11310 ⇒ 11310
    /// กลายเป็นบัญชีคุม ⇒ ลูกหนี้ตั้งต้นพลิกไป 11320) · ลูก = ผังที่ไม่ใช่ผังระบบซึ่งแม่เป็น <see cref="IsTemplateLeaf"/> (ลูกที่ระบบสร้าง เช่น ผังย่อยธนาคาร
    /// ใต้ 11122 ไม่นับ) · ข้อเสนอ = ย้ายไปหัวบัญชีถัดไปที่ถูกต้อง (prefix ที่ยาวที่สุด · ประเภทเดียวกัน · ไม่ใช่ผังลงรายการของแม่แบบ) ระดับ = แม่ + 1 ·
    /// หัวบัญชีของผู้ใช้ที่ยังไม่มีลูกแต่ถูกใช้อยู่ (<paramref name="inUseAccountIds"/>) / ไม่มีหัวบัญชีเลย ⇒ ไม่ย้าย บอกเหตุผล ·
    /// <paramref name="onlyChildIds"/> = เฉพาะผังที่ผู้ใช้เลือก (null = ทุกผัง) · รอบ 4 P2-c: ผู้เรียก (<c>ChartParentLinker</c>) ส่งเฉพาะลูกที่มีหลักฐานว่า
    /// <b>ระบบผูก</b> (<see cref="IsImportCreated"/> / แถวพักนำเข้า / audit การผูก) — ลูกที่ผู้ใช้สร้างใต้ผังแม่แบบเองไม่ถูกเสนอ
    /// </summary>
    public static List<ChartLinkProposal> PlanTemplateLeafRescue(IReadOnlyList<ChartNode> chart, IReadOnlyCollection<Guid>? onlyChildIds,
        IReadOnlySet<Guid> inUseAccountIds)
    {
        var byId = chart.ToDictionary(c => c.Id);
        var hasChildren = chart.Where(c => c.ParentId != null).Select(c => c.ParentId!.Value).ToHashSet();
        var result = new List<ChartLinkProposal>();
        bool CanBeHeader(ChartNode c) => !IsTemplateLeaf(c) && !(c.IsSystem && !hasChildren.Contains(c.Id));
        foreach (var child in chart
                     .Where(c => !c.IsSystem && c.ParentId is Guid pid && byId.TryGetValue(pid, out var parent) && IsTemplateLeaf(parent)
                         && (onlyChildIds == null || onlyChildIds.Contains(c.Id)))
                     .OrderBy(c => c.Code.Length).ThenBy(c => c.Code, StringComparer.Ordinal))
        {
            var leaf = byId[child.ParentId!.Value];
            var target = FindParent(child, chart, CanBeHeader);
            string? blocked = target == null
                ? $"ไม่มีหัวบัญชีอื่นที่รหัสเป็นส่วนต้นของ {child.Code} (นอกจากผังลงรายการ {leaf.Code}) — ย้ายเองที่หน้าผังบัญชี"
                : !hasChildren.Contains(target.Id) && inUseAccountIds.Contains(target.Id)
                    ? $"หัวบัญชีที่ควรเป็น ({target.Code}) ถูกใช้อยู่แล้ว (มีรายการบัญชี/ยอดยกมา หรือถูกตั้งเป็นผังของสินค้า/ผู้ติดต่อ/ค่าตั้ง) — "
                        + "ย้ายแล้วจะกลายเป็นบัญชีคุมที่มียอดค้าง · ย้ายยอด/ค่าตั้งออกก่อน หรือย้ายเองที่หน้าผังบัญชี"
                    : null;
            result.Add(new ChartLinkProposal(child.Id, child.Code, blocked == null ? target!.Id : null, target?.Code,
                blocked == null ? target!.Level + 1 : child.Level, blocked));
        }
        return result;
    }

    /// <summary>
    /// ระดับใหม่ของทุกผังหลังใช้ <paramref name="applied"/> (pure · รอบ 2 P2-8): ผังที่ถูกผูก/ย้ายแม่ได้ระดับจากข้อเสนอ แล้ว<b>ลูกหลานทุกชั้น</b>ของมัน
    /// = ระดับแม่ + 1 (เดิมปรับเฉพาะผังที่ย้าย ⇒ ลูกของหัวบัญชีที่ย้ายค้างระดับเก่า) · คืนเฉพาะผังที่ระดับต่างจากใน <paramref name="chart"/> · กันวนซ้ำ
    /// </summary>
    public static Dictionary<Guid, int> Relevel(IReadOnlyList<ChartNode> chart, IEnumerable<ChartLinkProposal> applied)
    {
        var parentOf = chart.ToDictionary(c => c.Id, c => c.ParentId);
        var level = chart.ToDictionary(c => c.Id, c => c.Level);
        var moved = new List<Guid>();
        foreach (var p in applied.Where(p => p.BlockedReason == null && p.ParentId != null && parentOf.ContainsKey(p.AccountId)))
        {
            parentOf[p.AccountId] = p.ParentId;
            level[p.AccountId] = p.NewLevel;
            moved.Add(p.AccountId);
        }
        var children = parentOf.Where(kv => kv.Value != null)
            .GroupBy(kv => kv.Value!.Value)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var queue = new Queue<Guid>(moved);
        var seen = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || !children.TryGetValue(id, out var kids)) continue;
            foreach (var k in kids)
            {
                level[k] = level[id] + 1;
                queue.Enqueue(k);
            }
        }
        var original = chart.ToDictionary(c => c.Id, c => c.Level);
        return level.Where(kv => original[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
