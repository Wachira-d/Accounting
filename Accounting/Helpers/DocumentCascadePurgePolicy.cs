using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของเอกสาร 1 ใบในแผนลบถาวร (ใบที่สั่งลบ = ราก · ใบที่เกี่ยวข้อง = ลูกหลานผ่าน <c>RelatedDocumentId</c> รวมใบเสร็จหลักฐานการชำระ /
/// ใบลดหนี้-เพิ่มหนี้ที่อ้างเลขที่) — ผู้โหลด (<c>DocumentService.BuildPurgePlanAsync</c>) เติมให้ทุกช่องจากฐานจริง · pure data</summary>
/// <param name="ParentId">ใบที่ใบนี้สืบมา (null = ราก)</param>
/// <param name="Depth">0 = ราก · 1 = ลูก · 2 = หลาน … (ใบเสร็จหลักฐาน = ใบแม่ + 1)</param>
/// <param name="RetentionUntil">วันสิ้นสุดการเก็บที่มีผล (<see cref="DocumentRetention.EffectiveUntil"/>) · null = ยังไม่เคยออก</param>
/// <param name="EtaxReachedRd">e-Tax ของใบนี้ถึงกรมสรรพากรแล้ว (ส่งแล้ว/ตอบรับ/e-Tax by Email ประทับเวลา — <c>DocumentVoidPreconditions.EffectiveEtaxAsync</c>)</param>
/// <param name="FilingLocked">ใบนี้อยู่ในรายงานภาษีที่ยื่น/ล็อกแล้ว (<c>ITaxService.IsDocumentFilingLockedAsync</c>)</param>
/// <param name="ByTextReference">ใบลดหนี้/เพิ่มหนี้รุ่นเก่าที่อ้าง "เลขที่" ใบแม่ในช่องอ้างอิง (ไม่ได้ผูก id — ระบบลบตามไม่ได้)</param>
public sealed record PurgeNodeFacts(
    Guid Id, Guid? ParentId, int Depth, string? DocumentNumber, DocumentType Type, DocumentStatus Status,
    DateTime? RetentionUntil, bool EtaxReachedRd, bool FilingLocked, bool ByTextReference = false)
{
    public bool IsRoot => Depth == 0;
    /// <summary>ใบเสร็จหลักฐานการชำระ (<c>IsSettlementReceipt</c>) — ถูกลบไปพร้อมใบแม่เสมอ (ทั้งสองโหมด) ⇒ ผ่านด่านเดียวกับใบที่ถูกลบ (คำตัดสินข้อ 242-A · B1)</summary>
    public bool IsSettlementReceipt { get; init; }
    /// <summary>JE ของใบนี้ (หรือ JE กลับรายการของมัน) อยู่ในงวดบัญชีที่ปิด/ล็อก หรือปีที่ปิดบัญชีแล้ว (S2)</summary>
    public bool PeriodClosed { get; init; }
    /// <summary>มี JE ที่อ้างใบนี้ (<c>SourceDocumentId</c>)</summary>
    public bool HasJournal { get; init; }
    /// <summary>มีการเคลื่อนไหวสต็อกของใบนี้ (<c>StockMovements</c>)</summary>
    public bool HasStockMovement { get; init; }
    /// <summary>มีการรับ/จ่ายชำระที่ยังมีผล (ไม่ถูกลบ)</summary>
    public bool HasLivePayment { get; init; }
    /// <summary>บิล POS อ้างใบนี้หรือ JE ของใบนี้ (N3 · <c>PosOrders.JournalEntryId</c> เป็น RESTRICT)</summary>
    public bool PosLinked { get; init; }
    /// <summary>เครดิตภาษีถูกหัก ณ ที่จ่ายของใบนี้ได้รับหนังสือรับรอง/ใช้เครดิตแล้ว (Received/Claimed · N3)</summary>
    public bool WhtCreditUsed { get; init; }
    /// <summary>ใบนี้เป็น "ใบแทน" ของใบที่ยังมีผล (<c>ReplacesDocumentId</c> · S3) — id ใบเดิม</summary>
    public Guid? ReplacesLiveDocumentId { get; init; }
    /// <summary>ใบอื่นที่ชี้มาที่ใบนี้ด้วย <c>ReplacedByDocumentId</c> (S3)</summary>
    public IReadOnlyList<Guid> ReplacedByPointers { get; init; } = Array.Empty<Guid>();
    /// <summary>เอกสารอีกฝั่งของการปันส่วนชำระที่ยังมีผล (การชำระของใบอื่นปันมาที่ใบนี้ · การชำระของใบนี้ปันไปใบอื่น · S4)</summary>
    public IReadOnlyList<Guid> AllocationCounterparts { get; init; } = Array.Empty<Guid>();
}

/// <summary>สิ่งที่จะเกิดกับใบหนึ่งในแผน</summary>
public enum PurgeNodeAction
{
    /// <summary>ลบถาวรพร้อมข้อมูลของใบ (JE · การชำระ · e-Tax · ลายเซ็น …)</summary>
    Delete,
    /// <summary>คงไว้ แต่ตัดการอ้างอิงถึงใบที่ถูกลบ (โหมด "ลบเฉพาะใบนี้" — พฤติกรรมเดิม)</summary>
    Unlink,
    /// <summary>ลบไม่ได้ — ทำให้ทั้งคำขอถูกปฏิเสธ (ไม่ลบอะไรเลย)</summary>
    Block,
}

/// <summary>ผลต่อใบ — <paramref name="Reason"/> เป็นภาษาไทยที่โชว์บนหน้าจอ/ลง audit ได้ตรง ๆ ·
/// <paramref name="RequiresReason"/> = ลบใบนี้ต้อง force + เหตุผล (ช่วงเก็บรักษา หรือใบที่ออกแล้วแต่ไม่มีผลทางบัญชี — N1)</summary>
public sealed record PurgeNodeVerdict(PurgeNodeFacts Node, PurgeNodeAction Action, bool InRetention, string? RuleCode, string Reason,
    bool RequiresReason = false);

/// <summary>ผลของทั้งคำขอ — <paramref name="Allowed"/> = ลบได้ตามที่ขอ (รวมเงื่อนไขเหตุผล) · <paramref name="NeedsReason"/> = ต้อง force + เหตุผล ·
/// <paramref name="DeleteOrder"/> = ลำดับลบ (ลึกสุดก่อน รากท้ายสุด) · <paramref name="PlanToken"/> = ลายนิ้วมือของแผน (ใบ · สถานะ · การกระทำ) ให้ผู้เรียก
/// ส่งกลับตอนยืนยัน — แผนเปลี่ยนระหว่างยืนยัน ⇒ ปฏิเสธ (S1)</summary>
public sealed record DocumentPurgeVerdict(
    bool Allowed, bool Cascade, bool NeedsReason, string Message, string? RuleCode,
    IReadOnlyList<PurgeNodeVerdict> Nodes, IReadOnlyList<Guid> DeleteOrder, string PlanToken = "");

/// <summary>
/// <b>ลบเอกสารถาวร (และเอกสารที่เกี่ยวข้อง) ได้แค่ไหน — ตัวตัดสินตัวเดียว</b> (รอบ 242 · คำตัดสินข้อ 242-A · เจ้าของ 2026-11-05:
/// "กดลบใบเสนอราคาที่ออกใบแจ้งหนี้แล้ว ระบบต้องยอมให้ลบ และลบเอกสารที่เกี่ยวข้องทั้งหมดไปเลยถ้าเอกสารต้นทางถูกลบ" · ฝ่ายค้านรอบ 242 B1/S1–S4/N1/N3)
///
/// <para>═══ กติกา (ทุกใบที่ "จะถูกลบ" = ราก · ลูกหลานเมื่อ cascade · ใบเสร็จหลักฐานของใบที่ถูกลบเสมอ) ═══
/// <list type="number">
/// <item><b>ห้ามเด็ดขาด (ไม่มี override)</b>: e-Tax ถึงกรมสรรพากร (§86/4) · อยู่ในรายงานภาษีที่ยื่น/ล็อก (§87) · JE อยู่ในงวด/ปีที่ปิดบัญชีแล้ว (พ.ร.บ.บัญชี ม.11 — S2) ·
/// บิล POS อ้างอยู่ (N3) · เครดิตภาษีถูกหัก ณ ที่จ่ายได้รับหนังสือรับรอง/ใช้แล้ว (N3) · เป็นใบแทนของใบที่ยังมีผล หรือมีใบนอกแผนชี้มาว่าเป็นใบแทน (S3) ·
/// การปันส่วนชำระกับใบนอกแผนที่ยังมีผล (S4)</item>
/// <item><b>ลูกหลาน/ใบเสร็จหลักฐานที่ยังมีผล</b> (ออกแล้ว ไม่ถูกยกเลิก): มีผลทางบัญชี/ภาษี/สต็อก (<see cref="HasAccountingEffect"/>) ⇒ ปฏิเสธทั้งคำขอพร้อมรายชื่อ +
/// ทางไปต่อ (ยกเลิกก่อน — ระบบไม่ยกเลิกแทน) · <b>ไม่มีผล</b> (ใบเสนอราคา/ใบขอซื้อ/ใบสั่งซื้อ ฯลฯ ที่ไม่มี JE/สต็อก/การชำระ) ⇒ ลบตามได้ด้วย force + เหตุผล (N1 · คำขอเจ้าของ)</item>
/// <item>ร่าง/รออนุมัติ/ถูกปฏิเสธ ⇒ ลบได้ · ถูกยกเลิกแล้ว/ราก ⇒ ลบได้ภายใต้ช่วงเก็บรักษา 5 ปี (§87/3 · ม.10): force + เหตุผล (เหตุผลเดียวครอบทั้งคำขอ)</item>
/// <item>"ลบเฉพาะใบนี้" (ไม่ cascade): ลูกหลานคงไว้ ตัดการอ้างอิง (ไม่ถูกตัดสิน) — ยกเว้นใบเสร็จหลักฐานของรากที่ลบไปพร้อมราก</item>
/// </list>
/// G6: pure · ไม่ throw · ผู้เรียกแปลง <see cref="DocumentPurgeVerdict.Allowed"/> = false เป็น 409 พร้อม <see cref="DocumentPurgeVerdict.Message"/></para>
/// </summary>
public static class DocumentCascadePurgePolicy
{
    public const string RuleEtaxReached = "RD-86/4-ETAX-PURGE";
    public const string RuleFilingLocked = "RD-87-FILED-PURGE";
    public const string RulePeriodClosed = "ACC-M11-PERIOD-CLOSED-PURGE";
    public const string RulePosLinked = "POS-ORDER-PURGE";
    public const string RuleWhtCreditUsed = "RD-WHT-CREDIT-PURGE";
    public const string RuleReplacement = "RD-86/4-REPLACEMENT-PURGE";
    public const string RuleAllocation = "PAYMENT-ALLOCATION-PURGE";
    public const string RuleDescendantEffective = "PURGE-CASCADE-EFFECTIVE";
    public const string RuleRetention = "ACC-M10-RETENTION-PURGE";
    public const string RuleReasonRequired = "ACC-M10-PURGE-REASON";
    public const string RulePlanChanged = "PURGE-PLAN-CHANGED";

    /// <summary>ข้อความเมื่อแผนเปลี่ยนระหว่างผู้ใช้ยืนยัน (S1)</summary>
    public const string PlanChangedMessage = "เอกสารเปลี่ยนระหว่างยืนยัน — เปิดแผนการลบใหม่แล้วตรวจอีกครั้ง · ระบบยังไม่ได้ลบอะไร";

    /// <summary>ลึกสุดที่ผู้โหลดไล่ลูกหลาน (กันวงวนจากข้อมูลเสีย) — ลึกกว่านี้ถือว่าลบตามไม่ได้ (ไม่รู้ = ไม่ลบ)</summary>
    public const int MaxDepth = 12;

    /// <summary>ชนิดที่ "ออกแล้ว" ได้โดยไม่มีผลทางบัญชี/ภาษีในตัวเอง (ไม่มีเลข §86/4 · ไม่ลงบัญชีเมื่ออนุมัติ) — ผลจริงตัดสินจากข้อเท็จจริงต่อใบ (JE/สต็อก/ชำระ)</summary>
    private static readonly DocumentType[] NonPostingTypes =
    {
        DocumentType.Quotation, DocumentType.PurchaseRequisition, DocumentType.PurchaseOrder,
        DocumentType.DeliveryNote, DocumentType.GoodsReceiptNote, DocumentType.BillingNote,
    };

    /// <summary>
    /// <b>ใบนี้มีผลทางบัญชี/ภาษี/สต็อกไหม</b> (N1 · ตัวตัดสินเดียว) — ชนิดที่ลงบัญชี/มีเลขตาม §86/4 (ใบกำกับ · ใบเสร็จ · ใบลด/เพิ่มหนี้ · ใบแจ้งหนี้ ·
    /// ค่าใช้จ่าย ฯลฯ) = มีเสมอ · ชนิดที่ไม่ลงบัญชี (<see cref="NonPostingTypes"/>) = มีเมื่อ<b>ข้อเท็จจริงจริง</b>บอกว่ามี: JE อ้างใบนี้ · สต็อกเคลื่อน · การชำระที่ยังมีผล
    /// (ไม่ดูจากชื่อสถานะ) · pure
    /// </summary>
    internal static bool HasAccountingEffect(DocumentType type, bool hasJournal, bool hasStockMovement, bool hasLivePayment)
        => Array.IndexOf(NonPostingTypes, type) < 0 || hasJournal || hasStockMovement || hasLivePayment;

    public static DocumentPurgeVerdict Decide(IReadOnlyList<PurgeNodeFacts> nodes, bool cascade, bool force, string? reason, DateTime todayUtc)
    {
        // ผ่านแรก: ทีละชั้น (ใบแม่ก่อน) — ใบเสร็จหลักฐานถูกลบเมื่อใบแม่ถูกลบ
        var verdicts = new List<PurgeNodeVerdict>(nodes.Count);
        var deleting = new HashSet<Guid>();
        foreach (var node in nodes.OrderBy(p => p.Depth))
        {
            var parentDeleted = node.ParentId is Guid pid && deleting.Contains(pid);
            var willDelete = node.IsRoot || cascade || (node.IsSettlementReceipt && parentDeleted);
            var v = Judge(node, willDelete, todayUtc);
            if (v.Action != PurgeNodeAction.Unlink) deleting.Add(node.Id);
            verdicts.Add(v);
        }
        // ผ่านสอง: ตัวชี้ข้ามใบ (ใบแทน · การปันส่วนชำระ) ต้องชี้ใบที่ถูกลบในแผนเดียวกันเท่านั้น — ห้ามทิ้งตัวชี้ลอย (S3 · S4)
        var deleteSet = verdicts.Where(v => v.Action == PurgeNodeAction.Delete).Select(v => v.Node.Id).ToHashSet();
        for (var i = 0; i < verdicts.Count; i++)
        {
            var v = verdicts[i];
            if (v.Action != PurgeNodeAction.Delete) continue;
            var n = v.Node;
            if (n.ReplacesLiveDocumentId is Guid rid && !deleteSet.Contains(rid))
                verdicts[i] = v with { Action = PurgeNodeAction.Block, RuleCode = RuleReplacement,
                    Reason = "เป็นใบแทนของใบที่ยังมีผล (§86/4) — ลบแล้วใบเดิมจะชี้ใบที่ไม่มีอยู่ · ยกเลิก (Void) ใบนี้แทน" };
            else if (n.ReplacedByPointers.Any(p => !deleteSet.Contains(p)))
                verdicts[i] = v with { Action = PurgeNodeAction.Block, RuleCode = RuleReplacement,
                    Reason = "มีใบเดิมที่ระบุว่าใบนี้เป็นใบแทน (§86/4) — ลบแล้วตัวชี้ของใบเดิมจะลอย · ยกเลิก (Void) ใบนี้แทน" };
            else if (n.AllocationCounterparts.Any(p => !deleteSet.Contains(p)))
                verdicts[i] = v with { Action = PurgeNodeAction.Block, RuleCode = RuleAllocation,
                    Reason = "มีการปันส่วนชำระกับเอกสารอื่นที่ยังมีผล — ยกเลิกการชำระนั้นก่อน (ยอดคงค้างของใบอื่นจะคลาดถ้าลบ)" };
        }

        var blocked = verdicts.Where(v => v.Action == PurgeNodeAction.Block).ToList();
        var deletes = verdicts.Where(v => v.Action == PurgeNodeAction.Delete).ToList();
        var order = deletes.OrderByDescending(v => v.Node.Depth).Select(v => v.Node.Id).ToList();
        var needsReason = deletes.Any(v => v.RequiresReason);
        var token = PlanToken(verdicts);

        if (blocked.Count > 0)
        {
            var rootBlocked = blocked.Any(b => b.Node.IsRoot);
            var list = string.Join(" · ", blocked.Select(b => $"{Label(b.Node)} — {b.Reason}"));
            var way = rootBlocked
                ? "ลบใบนี้ไม่ได้ทั้งแบบลบเฉพาะใบและแบบลบพร้อมเอกสารที่เกี่ยวข้อง"
                : "ทางไปต่อ: ยกเลิก (Void) ใบที่ยังมีผลก่อนด้วยปุ่ม \"ยกเลิกเอกสาร\" (ระบบกลับรายการบัญชีและเก็บหลักฐานการยกเลิกให้) แล้วกลับมาลบอีกครั้ง "
                  + "— หรือเลือก \"ลบเฉพาะใบนี้\" (คงเอกสารที่เกี่ยวข้องไว้ ตัดการอ้างอิงถึงใบนี้)";
            return new DocumentPurgeVerdict(false, cascade, needsReason,
                $"{(cascade ? "ลบพร้อมเอกสารที่เกี่ยวข้อง" : "ลบถาวร")}ไม่ได้ — {blocked.Count} ใบลบไม่ได้: {list} · {way} · ระบบยังไม่ได้ลบอะไร",
                blocked[0].RuleCode, verdicts, Array.Empty<Guid>(), token);
        }

        if (needsReason)
        {
            var need = deletes.Where(v => v.RequiresReason).ToList();
            var names = string.Join(", ", need.Select(v => Number(v.Node)));
            if (!force)
            {
                var until = need.Where(v => v.InRetention).Max(v => v.Node.RetentionUntil);
                var msg = until.HasValue
                    ? $"ห้ามลบถาวร — เอกสารอยู่ในช่วงเก็บรักษาตามกฎหมาย (§87/3 · พ.ร.บ.การบัญชี ม.10) ถึง {until:dd/MM/yyyy} ({names}). " +
                      "ใช้ 'ยกเลิกเอกสาร' (Void) แทนเพื่อคงหลักฐานการตรวจสอบ"
                    : $"ห้ามลบถาวรโดยไม่ยืนยัน — มีเอกสารที่ออกแล้ว ({names}) · ยืนยันการลบ (force) พร้อมเหตุผล";
                return new DocumentPurgeVerdict(false, cascade, true, msg, RuleRetention, verdicts, Array.Empty<Guid>(), token);
            }
            if (string.IsNullOrWhiteSpace(reason))
                return new DocumentPurgeVerdict(false, cascade, true,
                    "การลบเอกสารในช่วงเก็บรักษาตามกฎหมายต้องระบุเหตุผล (เช่น ข้อมูลทดสอบ / สร้างผิดซ้ำ)",
                    RuleReasonRequired, verdicts, Array.Empty<Guid>(), token);
        }

        var ok = deletes.Count == 1
            ? $"ลบ {Label(deletes[0].Node)} ถาวร"
            : $"ลบถาวร {deletes.Count} ใบ: {string.Join(" · ", deletes.OrderBy(v => v.Node.Depth).Select(v => Label(v.Node)))}";
        var unlinked = verdicts.Count(v => v.Action == PurgeNodeAction.Unlink);
        if (unlinked > 0) ok += $" · คงเอกสารที่เกี่ยวข้อง {unlinked} ใบไว้ (ตัดการอ้างอิงถึงใบที่ลบ)";
        return new DocumentPurgeVerdict(true, cascade, needsReason, ok, null, verdicts, order, token);
    }

    private static PurgeNodeVerdict Judge(PurgeNodeFacts n, bool willDelete, DateTime todayUtc)
    {
        var inRetention = DocumentRetention.InRetention(n.RetentionUntil, todayUtc);
        if (!willDelete)
            return new(n, PurgeNodeAction.Unlink, inRetention, null, "คงไว้ — ตัดการอ้างอิงถึงใบที่ลบ");
        // (1) ห้ามเด็ดขาด — ทุกใบที่จะถูกลบ
        if (n.EtaxReachedRd)
            return Block(n, inRetention, RuleEtaxReached,
                "e-Tax ของใบนี้ถึงกรมสรรพากรแล้ว (§86/4) — ลบไม่ได้ · ออกใบลดหนี้หรือยกเลิกที่ระบบ e-Tax ของกรมสรรพากรแทน");
        if (n.FilingLocked)
            return Block(n, inRetention, RuleFilingLocked,
                "อยู่ในรายงานภาษีที่ยื่น/ล็อกแล้ว (§87) — ลบไม่ได้ · ปลดล็อกรายงานหรือใช้ 'Reject & Reverse' ก่อน");
        if (n.PeriodClosed)
            return Block(n, inRetention, RulePeriodClosed,
                "รายการบัญชีของใบนี้อยู่ในงวด/ปีที่ปิดบัญชีแล้ว (พ.ร.บ.การบัญชี ม.11) — ลบแล้วงบของงวดที่ปิดจะเปลี่ยน · เปิดงวดก่อน หรือใช้ยกเลิก (Void) ซึ่งลงรายการกลับในงวดที่เปิด");
        if (n.PosLinked)
            return Block(n, inRetention, RulePosLinked,
                "เป็นเอกสารของบิล POS — ลบจากหน้าเอกสารไม่ได้ · ยกเลิก/คืนเงินบิลที่หน้า POS แทน");
        if (n.WhtCreditUsed)
            return Block(n, inRetention, RuleWhtCreditUsed,
                "เครดิตภาษีถูกหัก ณ ที่จ่ายของใบนี้ได้รับหนังสือรับรอง/ใช้เครดิตแล้ว — ลบไม่ได้ · ยกเลิก (Void) ใบนี้แทน");
        // (2) ลูกหลาน/ใบเสร็จหลักฐานที่ยังมีผล — ระบบไม่ยกเลิกแทน
        if (!n.IsRoot)
        {
            if (n.ByTextReference)
                return Block(n, inRetention, RuleDescendantEffective, "อ้างเลขที่ใบนี้ในช่องอ้างอิงและยังมีผล — ยกเลิกใบนั้นก่อน");
            if (DocumentStatusRules.IsEffective(n.Status))
            {
                if (HasAccountingEffect(n.Type, n.HasJournal, n.HasStockMovement, n.HasLivePayment))
                    return Block(n, inRetention, RuleDescendantEffective, n.IsSettlementReceipt
                        ? "ใบเสร็จหลักฐานการชำระที่ยังมีผล — ยกเลิกการชำระเงินก่อน (ระบบยกเลิกใบเสร็จให้เอง)"
                        : "ออกแล้วและยังมีผลทางบัญชี/ภาษี/สต็อก — ยกเลิก (Void) ใบนี้ก่อน");
                // N1: ออกแล้วแต่ไม่มีผลทางบัญชี/ภาษี/สต็อก (ใบเสนอราคา/ใบสั่งซื้อที่ยังไม่ลงอะไร) ⇒ ลบตามได้ด้วย force + เหตุผล
                return new(n, PurgeNodeAction.Delete, inRetention, RuleRetention,
                    "ออกแล้วแต่ไม่มีผลทางบัญชี/ภาษี/สต็อก — ลบตามได้ (ต้องระบุเหตุผล)", RequiresReason: true);
            }
        }
        // (3) ราก · ยังไม่เคยออก · ถูกยกเลิกแล้ว ⇒ ลบได้ (ช่วงเก็บรักษา ⇒ ต้องเหตุผล)
        var why = n.IsRoot ? "ใบที่สั่งลบ"
            : DocumentStatusRules.IsIssued(n.Status) ? "ถูกยกเลิกแล้ว — ลบตามได้" : "ยังไม่เคยออก (ร่าง) — ลบตามได้";
        if (n.IsSettlementReceipt) why = "ใบเสร็จหลักฐานการชำระ — ลบพร้อมใบแม่";
        return new(n, PurgeNodeAction.Delete, inRetention, inRetention ? RuleRetention : null,
            inRetention ? why + " · อยู่ในช่วงเก็บรักษา ต้องระบุเหตุผล" : why, RequiresReason: inRetention);
    }

    private static PurgeNodeVerdict Block(PurgeNodeFacts n, bool inRetention, string rule, string reason)
        => new(n, PurgeNodeAction.Block, inRetention, rule, reason);

    /// <summary>ลายนิ้วมือของแผน (S1) — ฟังก์ชันเดียวทั้งตอนแสดงแผนและตอนยืนยันใต้ล็อก: id · สถานะ · การกระทำ ของทุกใบ เรียงตาม id · SHA-256 hex 16 ตัวแรก</summary>
    internal static string PlanToken(IEnumerable<PurgeNodeVerdict> verdicts)
    {
        var canon = string.Join("|", verdicts
            .OrderBy(v => v.Node.Id)
            .Select(v => $"{v.Node.Id:N}:{(int)v.Node.Status}:{v.Action}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon)))[..16].ToLowerInvariant();
    }

    private static string Label(PurgeNodeFacts n) => $"{DocumentTypeNames.Title(n.Type, "th")} {Number(n)}";

    private static string Number(PurgeNodeFacts n)
        => string.IsNullOrWhiteSpace(n.DocumentNumber) || n.DocumentNumber.StartsWith("DRAFT-", StringComparison.OrdinalIgnoreCase)
            ? "(ร่าง)" : n.DocumentNumber;
}
