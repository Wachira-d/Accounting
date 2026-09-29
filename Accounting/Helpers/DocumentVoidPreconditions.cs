using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>เอกสารที่อ้างใบหนึ่งอยู่จนยกเลิกใบนั้นไม่ได้ — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="ByTextReference">true = ใบลดหนี้/ใบเพิ่มหนี้รุ่นเก่าที่อ้างด้วย "เลขที่" ในช่องอ้างอิง (ไม่มี <c>RelatedDocumentId</c>) ·
/// false = เอกสารลูกที่ผูก <c>RelatedDocumentId</c></param>
public sealed record DocumentVoidChildFact(Guid ParentId, DocumentType ChildType, string? ChildNumber, bool ByTextReference);

/// <summary>
/// **เหตุที่ยกเลิกเอกสารไม่ได้เพราะมีเอกสารอื่นอ้างอยู่ — ตัวตัดสินตัวเดียว** ของ <c>DocumentService.VoidDocumentAsync</c> และด่านก่อน
/// "ยกเลิกการลงบัญชีรอบโอน" (<see cref="SettlementUnpostGate"/> · review198-S3 S3-3 · ทีม S4)
///
/// <para>ที่มา: ด่าน C-2 ตรวจ e-Tax/รายงานล็อก/ภาษีที่ยื่นแล้ว แต่ไม่รู้เหตุที่ <c>VoidDocumentAsync</c> ปฏิเสธเอง (เอกสารลูก active อ้าง ·
/// ใบลดหนี้/ใบเพิ่มหนี้อ้างเลขที่) ⇒ รอบโอนที่มีใบสรุปหลายวันและมีใบลดหนี้อ้างใบหนึ่ง: ใบแรกถูกยกเลิก ใบที่สองล้มกลางทาง = ครึ่งกลับครึ่งค้าง ·
/// เดิมเงื่อนไขทั้งสองเขียนอยู่ในเมธอดเดียว — ย้ายมาที่นี่ให้สองเส้นเรียกตัวเดียว (F2 ข้อ 4 · ห้ามสำเนาที่สอง)</para>
/// <para>กติกา (เหมือนเดิมทุกตัวอักษร): (1) เอกสารลูกที่ <c>RelatedDocumentId</c> = ใบนี้ ไม่ถูกลบ ไม่ Voided/Rejected · (2) ใบลดหนี้/ใบเพิ่มหนี้ที่
/// ไม่มี <c>RelatedDocumentId</c> แต่ <c>Reference</c> = เลขที่ใบนี้ ไม่ใช่ร่าง ไม่ Voided/Rejected — มีข้อ (1) ใช้ข้อ (1) ก่อน</para>
/// </summary>
public static class DocumentVoidPreconditions
{
    /// <summary>ข้อความของเหตุ 1 ข้อ (ข้อความเดิมของ <c>VoidDocumentAsync</c>)</summary>
    internal static string Reason(DocumentVoidChildFact c)
        => c.ByTextReference
            ? $"ยกเลิกไม่ได้ — มี{(c.ChildType == DocumentType.CreditNote ? "ใบลดหนี้" : "ใบเพิ่มหนี้")} "
              + $"{c.ChildNumber} อ้างเลขที่ใบนี้อยู่ (§86/9-10) — ยกเลิกใบนั้นก่อน"
            : $"ยกเลิกไม่ได้ — เอกสารนี้มีเอกสารลูก {c.ChildType} ({c.ChildNumber}) "
              + "อ้างอิงอยู่ (เช่นใบกำกับภาษี/ใบเสร็จที่แปลงไป). กรุณายกเลิกเอกสารลูกก่อน";

    /// <summary>เลือกเหตุของแต่ละใบ — เอกสารลูก (<c>RelatedDocumentId</c>) ก่อนใบที่อ้างด้วยเลขที่ · ใบที่ไม่มีเหตุไม่อยู่ในผล · pure</summary>
    internal static IReadOnlyDictionary<Guid, string> Decide(IEnumerable<DocumentVoidChildFact> facts)
        => facts.GroupBy(f => f.ParentId)
            .ToDictionary(g => g.Key, g => Reason(g.OrderBy(f => f.ByTextReference ? 1 : 0).First()));

    /// <summary>ตัวโหลดข้อเท็จจริง + ตัดสิน สำหรับเอกสารหลายใบ (ของบริษัทนี้) — คืนเหตุต่อใบ · ใบที่ยกเลิกได้ไม่อยู่ในผล</summary>
    /// <param name="ignoreChildIds">เอกสารลูกที่ผู้เรียกจัดการเองในธุรกรรมเดียวกัน (ไม่นับเป็นเหตุ) — รอบ 200 ทีม V1: ใบเสร็จอัตโนมัติของการรับชำระ
    /// ที่ "ยกเลิกและออกใบแทน" ย้ายไปใบใหม่ (ยกเลิกใบเสร็จเดิม + ออกใหม่อ้างใบใหม่) · null/ว่าง = นับทุกใบ (พฤติกรรมเดิมของทุกผู้เรียก)</param>
    public static async Task<IReadOnlyDictionary<Guid, string>> ChildBlocksAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default, IReadOnlyCollection<Guid>? ignoreChildIds = null)
    {
        if (documentIds.Count == 0) return new Dictionary<Guid, string>();
        var ids = documentIds.Distinct().ToList();
        var ignore = (ignoreChildIds ?? Array.Empty<Guid>()).Distinct().ToList();
        var facts = (await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.RelatedDocumentId != null && ids.Contains(d.RelatedDocumentId.Value)
                    && !d.IsDeleted && !ignore.Contains(d.Id)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { Parent = d.RelatedDocumentId!.Value, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct))
            .Select(d => new DocumentVoidChildFact(d.Parent, d.DocumentType, d.DocumentNumber, false))
            .ToList();

        var numbers = (await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && ids.Contains(d.Id))
                .Select(d => new { d.Id, d.DocumentNumber }).ToListAsync(ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.DocumentNumber)).ToList();
        if (numbers.Count > 0)
        {
            var nums = numbers.Select(n => n.DocumentNumber).Distinct().ToList();
            var textRefs = await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted
                    && (d.DocumentType == DocumentType.CreditNote || d.DocumentType == DocumentType.DebitNote)
                    && d.RelatedDocumentId == null && d.Reference != null && nums.Contains(d.Reference)
                    && d.Status != DocumentStatus.Draft
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Reference, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct);
            foreach (var t in textRefs)
                foreach (var parent in numbers.Where(n => n.DocumentNumber == t.Reference))
                    facts.Add(new DocumentVoidChildFact(parent.Id, t.DocumentType, t.DocumentNumber, true));
        }
        return Decide(facts);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // รอบ 200 ทีม V1 · คำตัดสินข้อ 11 (review198-S4 S4-8 ค้าง): ใบเสร็จอัตโนมัติคู่การรับชำระ vs e-Tax
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>สถานะ e-Tax ที่ถือว่า "ถึงกรมสรรพากรแล้ว" (ส่งแล้ว/ตอบรับแล้ว) — เก็บเป็น array ให้ EF แปลเป็น <c>IN (...)</c> ได้ ·
    /// ใบที่ถึงกรมสรรพากรแล้วห้ามถูกประทับ Voided ในฐานข้อมูลเราเงียบ ๆ (คำตัดสินข้อ 11) · Generated/Signed/Error/Rejected = ยังไม่ถึง</summary>
    public static readonly EtaxStatus[] EtaxReachedRdStatuses = { EtaxStatus.Submitted, EtaxStatus.Accepted };

    /// <summary>e-Tax สถานะนี้ถึงกรมสรรพากรแล้วไหม · null (ไม่มี e-Tax) = ยังไม่ถึง</summary>
    internal static bool EtaxReachedRd(EtaxStatus? status) => status is EtaxStatus s && EtaxReachedRdStatuses.Contains(s);

    /// <summary>สถานะ "ไปไกลที่สุด" ของ e-Tax หลายแถวของใบเดียว (ไม่นับ Voided) — Accepted &gt; Submitted &gt; อื่น ๆ · ไม่มีแถว = null</summary>
    public static EtaxStatus? StrongestEtax(IEnumerable<EtaxStatus> statuses)
    {
        var live = statuses.Where(s => s != EtaxStatus.Voided).ToList();
        if (live.Count == 0) return null;
        if (live.Contains(EtaxStatus.Accepted)) return EtaxStatus.Accepted;
        if (live.Contains(EtaxStatus.Submitted)) return EtaxStatus.Submitted;
        return live[0];
    }

    /// <summary>
    /// **ยกเลิกการรับชำระแล้วใบเสร็จอัตโนมัติคู่กัน (<c>Payment.ReceiptDocumentId</c>) ต้องทำอะไร — ตัวตัดสินตัวเดียว** ของ
    /// <c>DocumentService.VoidPaymentAsync</c> ทุกทางเข้า (หน้าเอกสาร · ยกเลิกการลงบัญชีรอบโอน · เช็คเด้ง) และด่าน "ยกเลิกและออกใบแทน"
    /// <para>คำตัดสินข้อ 11: ใบเสร็จยังไม่ถึงกรมสรรพากร ⇒ ยกเลิกได้เหมือนเดิม · ถึงแล้ว (ส่ง/ตอบรับ) ⇒ ผู้ใช้กดเอง = <b>ปฏิเสธพร้อมทางไปต่อ</b> ·
    /// เช็คเด้ง = <b>ห้ามบล็อก</b> (เงินไม่เข้าจริง ต้องกลับรายการเงินเสมอ) แต่<b>ไม่ประทับ Voided เงียบ</b> — ติดธง "ต้องยกเลิกทาง e-Tax" บนใบเสร็จ
    /// (ระบบยังไม่มีช่องทางส่งยกเลิก/ออกแทน e-Tax ที่กรมสรรพากรตอบรับแล้ว — มีแค่ยกเลิก e-Tax ที่ยังไม่ตอบรับ) · G6: pure</para>
    /// </summary>
    public static AutoReceiptEtaxDecision AutoReceiptOnPaymentVoid(EtaxStatus? strongestReceiptEtax, string? receiptNumber, PaymentVoidCause cause)
    {
        if (!EtaxReachedRd(strongestReceiptEtax))
            return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null);
        var no = string.IsNullOrWhiteSpace(receiptNumber) ? "คู่การรับชำระนี้" : receiptNumber!.Trim();
        var accepted = strongestReceiptEtax == EtaxStatus.Accepted;
        if (cause == PaymentVoidCause.ChequeBounce)
            return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.FlagEtaxCancellation,
                $"ต้องยกเลิกทาง e-Tax — เช็คเด้ง (เงินไม่เข้าจริง) ระบบกลับรายการรับชำระแล้ว แต่ใบเสร็จ {no} "
                + (accepted ? "ได้รับตอบรับจากกรมสรรพากรแล้ว (Accepted)" : "ส่ง e-Tax ไปกรมสรรพากรแล้ว (Submitted)")
                + " จึงยังไม่ถูกยกเลิกในระบบ — "
                + (accepted
                    ? "ดำเนินการยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากร (หรือผู้ให้บริการ e-Tax) "
                    : "เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ (ทำได้ก่อนกรมสรรพากรตอบรับ) ")
                + "แล้วแจ้งลูกค้าว่าใบเสร็จนี้ไม่มีผล");
        return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Refuse,
            $"ยกเลิกการชำระนี้ไม่ได้ — ใบเสร็จ {no} ที่ออกคู่การรับชำระ "
            + (accepted
                ? "ได้รับตอบรับ e-Tax จากกรมสรรพากรแล้ว (Accepted) — ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน (ระบบนี้ยังไม่มีช่องทางส่งยกเลิก e-Tax ที่ตอบรับแล้ว) "
                  + "· ถ้าเงินไม่ได้เข้าจริง (เช็คเด้ง) ให้บันทึก “เช็คเด้ง” ที่หน้าเช็ค ระบบจะกลับรายการเงินและติดธงให้ตามยกเลิก e-Tax"
                : "ส่ง e-Tax ไปกรมสรรพากรแล้ว (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ก่อน (ทำได้ก่อนกรมสรรพากรตอบรับ) "
                  + "แล้วกดยกเลิกการชำระอีกครั้ง")
            + " · ระบบยังไม่ได้แตะอะไร");
    }
}

/// <summary>ทางเข้าที่ยกเลิกการรับชำระ — ตัดสินว่าใบเสร็จที่ถึงกรมสรรพากรแล้วต้องปฏิเสธหรือติดธง (<see cref="DocumentVoidPreconditions.AutoReceiptOnPaymentVoid"/>)</summary>
public enum PaymentVoidCause
{
    /// <summary>ผู้ใช้กดยกเลิกการชำระ (หน้าเอกสาร) หรือเส้นที่ยกเลิกตามคำสั่งผู้ใช้ — ใบเสร็จถึงกรมสรรพากรแล้ว = ปฏิเสธ</summary>
    User = 0,
    /// <summary>ยกเลิกการลงบัญชีรอบโอน — ด่าน <see cref="SettlementUnpostGate"/> ปฏิเสธไว้ก่อนแตะชิ้นแรกแล้ว (ที่นี่เป็นตาข่ายชั้นสอง = ปฏิเสธ)</summary>
    SettlementUnpost = 1,
    /// <summary>เช็คเด้ง — เงินไม่เข้าจริง ห้ามบล็อก · ใบเสร็จที่ถึงกรมสรรพากรแล้ว = ติดธงให้ตามยกเลิกทาง e-Tax</summary>
    ChequeBounce = 2,
}

/// <summary>สิ่งที่ต้องทำกับใบเสร็จอัตโนมัติเมื่อการรับชำระถูกยกเลิก</summary>
public enum AutoReceiptEtaxAction
{
    /// <summary>ยกเลิกใบเสร็จได้ (ยังไม่ถึงกรมสรรพากร) — พฤติกรรมเดิม</summary>
    Void = 0,
    /// <summary>ปฏิเสธการยกเลิกการชำระทั้งรายการ (ข้อความพร้อมทางไปต่อ)</summary>
    Refuse = 1,
    /// <summary>กลับรายการเงินตามปกติ แต่คงใบเสร็จไว้พร้อมธง "ต้องยกเลิกทาง e-Tax" (เช็คเด้ง)</summary>
    FlagEtaxCancellation = 2,
}

/// <param name="Message">ข้อความไทย — ข้อความปฏิเสธ (Refuse) หรือข้อความของธง (FlagEtaxCancellation) · Void = null</param>
public sealed record AutoReceiptEtaxDecision(AutoReceiptEtaxAction Action, string? Message);
