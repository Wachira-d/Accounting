using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>เอกสารที่อ้างใบหนึ่งอยู่จนยกเลิกใบนั้นไม่ได้ — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="ByTextReference">true = ใบลดหนี้/ใบเพิ่มหนี้รุ่นเก่าที่อ้างด้วย "เลขที่" ในช่องอ้างอิง (ไม่มี <c>RelatedDocumentId</c>) ·
/// false = เอกสารลูกที่ผูก <c>RelatedDocumentId</c></param>
/// <param name="ChildId">id ของใบที่อ้าง (รอบ 200 ทีม V2 — ตัวแยกของกำพร้าต้องรู้ว่าใบที่อ้าง "เอง" ยกเลิกได้ไหม · null = ผู้สร้างไม่ได้ระบุ)</param>
public sealed record DocumentVoidChildFact(Guid ParentId, DocumentType ChildType, string? ChildNumber, bool ByTextReference,
    Guid? ChildId = null);

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
        => Decide(await ChildFactsAsync(db, companyId, documentIds, ct, ignoreChildIds));

    /// <summary>ข้อเท็จจริงดิบ (ใบที่อ้างแต่ละใบ พร้อม id) ชุดเดียวกับที่ <see cref="ChildBlocksAsync"/> ตัดสิน — ให้ตัวแยกของกำพร้าของรอบโอน
    /// (<c>SettlementOrphanTriage</c> · รอบ 200 ทีม V2 · DECISIONS ข้อ 10) ตรวจต่อว่าใบที่อ้าง "เอง" ยกเลิกได้ไหม โดยไม่เขียนเงื่อนไขชุดที่สอง</summary>
    public static async Task<IReadOnlyList<DocumentVoidChildFact>> ChildFactsAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default, IReadOnlyCollection<Guid>? ignoreChildIds = null)
    {
        if (documentIds.Count == 0) return Array.Empty<DocumentVoidChildFact>();
        var ids = documentIds.Distinct().ToList();
        var ignore = (ignoreChildIds ?? Array.Empty<Guid>()).Distinct().ToList();
        var facts = (await db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && d.RelatedDocumentId != null && ids.Contains(d.RelatedDocumentId.Value)
                    && !d.IsDeleted && !ignore.Contains(d.Id)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, Parent = d.RelatedDocumentId!.Value, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct))
            .Select(d => new DocumentVoidChildFact(d.Parent, d.DocumentType, d.DocumentNumber, false, d.Id))
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
                .Select(d => new { d.Id, d.Reference, d.DocumentType, d.DocumentNumber })
                .ToListAsync(ct);
            foreach (var t in textRefs)
                foreach (var parent in numbers.Where(n => n.DocumentNumber == t.Reference))
                    facts.Add(new DocumentVoidChildFact(parent.Id, t.DocumentType, t.DocumentNumber, true, t.Id));
        }
        return facts;
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
    /// **สถานะ e-Tax ที่ใช้ตัดสินการยกเลิก** (รอบ 200 ทีม V1F · ฝ่ายค้าน V1-P1) — e-Tax by Email (SME: ส่งอีเมลพร้อม CC ที่อยู่ประทับเวลาของ
    /// กรมสรรพากร) ไม่เปลี่ยน <c>EtaxInvoice.Status</c> แต่ใบถึงผู้ประทับเวลาแล้วจริง ⇒ มีบันทึกการส่งสำเร็จ = ถือเท่า "ตอบรับแล้ว"
    /// (ยกเลิกจากระบบนี้ไม่ได้ — ต้องยกเลิก/ลดหนี้ทางกรมสรรพากร) · ไม่มีบันทึก = สถานะแถว e-Tax ตามเดิม · G6: pure
    /// </summary>
    internal static EtaxStatus? EffectiveEtax(EtaxStatus? strongest, bool sentByEmailWithRdTimestamp)
        => sentByEmailWithRdTimestamp ? EtaxStatus.Accepted : strongest;

    /// <summary>ตัวโหลดเดียวของสถานะ e-Tax ที่ใช้ตัดสินการยกเลิก — แถว e-Tax (ไปไกลที่สุด ไม่นับ Voided) + บันทึก e-Tax by Email ที่ส่งสำเร็จ
    /// พร้อม CC ประทับเวลา (<see cref="EffectiveEtax"/>) · ทุก id ที่ถามอยู่ในผล (ไม่มี e-Tax = null) · tenant แล้ว</summary>
    public static async Task<Dictionary<Guid, EtaxStatus?>> EffectiveEtaxAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, EtaxStatus?>();
        if (documentIds.Count == 0) return result;
        var ids = documentIds.Distinct().ToList();
        var rows = await db.EtaxInvoices.AsNoTracking()
            .Where(e => e.CompanyId == companyId && ids.Contains(e.DocumentId))
            .Select(e => new { e.DocumentId, e.Status })
            .ToListAsync(ct);
        var emailed = (await db.DocumentEmailLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && !l.IsDeleted && l.DocumentId != null && ids.Contains(l.DocumentId.Value)
                    && l.IsEtaxByEmail && l.IncludedRdTimestamp && l.Status == EmailLogStatus.Sent)
                .Select(l => l.DocumentId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        foreach (var id in ids)
            result[id] = EffectiveEtax(StrongestEtax(rows.Where(r => r.DocumentId == id).Select(r => r.Status)), emailed.Contains(id));
        return result;
    }

    /// <summary>
    /// **ยกเลิกการรับชำระแล้วใบเสร็จอัตโนมัติคู่กัน (<c>Payment.ReceiptDocumentId</c>) ต้องทำอะไร — ตัวตัดสินตัวเดียว** ของ
    /// <c>DocumentService.VoidPaymentAsync</c> ทุกทางเข้า (หน้าเอกสาร · ยกเลิกการลงบัญชีรอบโอน · เช็คเด้ง) และด่าน "ยกเลิกและออกใบแทน"
    /// <para>คำตัดสินข้อ 11: ใบเสร็จยังไม่ถึงกรมสรรพากร ⇒ ยกเลิกได้เหมือนเดิม · ถึงแล้ว (ส่ง/ตอบรับ/อีเมลประทับเวลา) ⇒ ผู้ใช้กดเอง =
    /// <b>ปฏิเสธพร้อมทางไปต่อ</b> · เช็คเด้ง = <b>ห้ามบล็อก</b> (เงินไม่เข้าจริง ต้องกลับรายการเงินเสมอ) แต่<b>ไม่ประทับ Voided เงียบ</b> —
    /// ติดธง "ต้องยกเลิกทาง e-Tax" บนใบเสร็จ · G6: pure</para>
    /// <para>รอบ 200 ทีม V1F (ฝ่ายค้าน V1-P2): <b>ยกเลิกการลงบัญชีรอบโอน</b> = ติดธงแบบเช็คเด้ง (เดิมปฏิเสธ) — ด่าน <see cref="SettlementUnpostGate"/>
    /// ปฏิเสธใบเสร็จที่ส่ง e-Tax แล้วไว้ก่อนแตะชิ้นแรกอยู่แล้ว ที่นี่ถึงได้เฉพาะเมื่อใบเสร็จถูกส่ง e-Tax <b>ระหว่าง</b>ด่านกับลูป ⇒ throw กลางลูป =
    /// เอกสารยกเลิกไปแล้วบางใบ การรับชำระค้างครึ่ง · ธงมองเห็นและตามปิดได้ (<see cref="EtaxCancellationResolution"/>) โดยภาษีขายของใบยังอยู่ในแบบ
    /// (<see cref="ShouldUndoOutputVatReclass"/>)</para>
    /// </summary>
    public static AutoReceiptEtaxDecision AutoReceiptOnPaymentVoid(EtaxStatus? strongestReceiptEtax, string? receiptNumber, PaymentVoidCause cause)
    {
        if (!EtaxReachedRd(strongestReceiptEtax))
            return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Void, null);
        var no = string.IsNullOrWhiteSpace(receiptNumber) ? "คู่การรับชำระนี้" : receiptNumber!.Trim();
        var accepted = strongestReceiptEtax == EtaxStatus.Accepted;
        if (cause is PaymentVoidCause.ChequeBounce or PaymentVoidCause.SettlementUnpost)
            return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.FlagEtaxCancellation,
                "ต้องยกเลิกทาง e-Tax — "
                + (cause == PaymentVoidCause.ChequeBounce
                    ? "เช็คเด้ง (เงินไม่เข้าจริง) ระบบกลับรายการรับชำระแล้ว"
                    : "ยกเลิกการลงบัญชีรอบโอน ระบบกลับรายการรับชำระแล้ว (ใบเสร็จถูกส่ง e-Tax ระหว่างทาง)")
                + $" แต่ใบเสร็จ {no} "
                + (accepted
                    ? "ถึงกรมสรรพากรแล้วและยกเลิกจากระบบนี้ไม่ได้ (Accepted — ตอบรับแล้ว หรือ e-Tax by Email ที่ประทับเวลาแล้ว)"
                    : "ส่ง e-Tax ไปกรมสรรพากรแล้ว (Submitted)")
                + " จึงยังไม่ถูกยกเลิกในระบบ · ภาษีขายตามใบนี้ (ถ้าใบถือ VAT) ยังรายงานใน ภ.พ.30 จนกว่าจะยกเลิกทางกรมสรรพากร — "
                + (accepted
                    ? "ดำเนินการยกเลิก/ออกใบลดหนี้ที่ระบบ e-Tax ของกรมสรรพากร (หรือผู้ให้บริการ e-Tax) "
                    : "เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ (ทำได้ก่อนกรมสรรพากรตอบรับ) ")
                + "แล้วกด “บันทึกว่ายกเลิกทาง e-Tax แล้ว” บนใบเสร็จนี้ (ระบบยกเลิกใบเสร็จและปลดบล็อกใบต้นทางให้) · แจ้งลูกค้าว่าใบเสร็จนี้ไม่มีผล");
        return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Refuse,
            $"ยกเลิกการชำระนี้ไม่ได้ — ใบเสร็จ {no} ที่ออกคู่การรับชำระ "
            + (accepted
                ? "ถึงกรมสรรพากรแล้วและยกเลิกจากระบบนี้ไม่ได้ (Accepted — ตอบรับแล้ว หรือ e-Tax by Email ที่ประทับเวลาแล้ว) — ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน "
                  + "· ถ้าเงินไม่ได้เข้าจริง (เช็คเด้ง) ให้บันทึก “เช็คเด้ง” ที่หน้าเช็ค ระบบจะกลับรายการเงินและติดธงให้ตามยกเลิก e-Tax"
                : "ส่ง e-Tax ไปกรมสรรพากรแล้ว (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ก่อน (ทำได้ก่อนกรมสรรพากรตอบรับ) "
                  + "แล้วกดยกเลิกการชำระอีกครั้ง")
            + " · ระบบยังไม่ได้แตะอะไร");
    }

    /// <summary>
    /// **กลับ "ภาษีขายถึงกำหนด" (§78/1 · ย้าย 21911 กลับ 21913) ได้ไหม** เมื่อการรับชำระถูกยกเลิก หรือใบเสร็จถูกบันทึกว่ายกเลิกทาง e-Tax แล้ว —
    /// ตัวตัดสินเดียว (รอบ 200 ทีม V1F · ฝ่ายค้าน V1-R2)
    /// <para>ใบเสร็จถือ VAT (ใบกำกับ ณ วันรับเงิน) ที่ยังมีผลที่กรมสรรพากร (ติดธงต้องยกเลิกทาง e-Tax) = ความรับผิดเกิดแล้วตามใบกำกับที่ออก ⇒
    /// <b>ห้ามถอยภาษีขายออกจาก ภ.พ.30</b> จนกว่าจะยกเลิก/ลดหนี้ตามกฎหมาย · เงินกลับได้ (ลูกหนี้เปิดใหม่) แต่ VAT ยังรายงาน ·
    /// ยังมีการรับชำระเหลือ = ไม่กลับ (พฤติกรรมเดิม) · G6: pure</para>
    /// </summary>
    /// <param name="remainingPaidAmount">ยอดรับชำระที่เหลือบนใบต้นทางหลังยกเลิก</param>
    /// <param name="outputVatDue">ใบต้นทางถูกย้ายภาษีขายถึงกำหนดแล้ว (<c>OutputVatDueAt != null</c>)</param>
    /// <param name="liveVatReceiptKeepsTaxPoint">ยังมีใบเสร็จถือ VAT ที่มีผลอยู่ (ติดธง/ยังไม่ยกเลิก) ของใบต้นทางนี้</param>
    public static bool ShouldUndoOutputVatReclass(decimal remainingPaidAmount, bool outputVatDue, bool liveVatReceiptKeepsTaxPoint)
        => remainingPaidAmount <= 0.005m && outputVatDue && !liveVatReceiptKeepsTaxPoint;

    /// <summary>ใบเสร็จที่ติดธงยังถือภาษีขายไหม — เงื่อนไขเดียวกับตัวเลือกเจ้าของแถว ภ.พ.30 ของ <c>TaxService</c> (<c>VatAmount &gt; 0.005</c>) ·
    /// ใบที่ถูกยกเลิกตามปกติ (Void) ไม่ถือแล้ว</summary>
    public static bool FlaggedReceiptKeepsTaxPoint(AutoReceiptEtaxAction action, decimal receiptVatAmount)
        => action == AutoReceiptEtaxAction.FlagEtaxCancellation && receiptVatAmount > 0.005m;

    /// <summary>
    /// **ปิดธง "ต้องยกเลิกทาง e-Tax"** (รอบ 200 ทีม V1F · ฝ่ายค้าน V1-R3) — ตัวตัดสินเดียวของ endpoint "บันทึกว่ายกเลิกทาง e-Tax แล้ว"
    /// <para>R1 (DECISION_AUDIT): ระบบ<b>ไม่ประทับ</b>สถานะของกรมสรรพากรเอง — ต้องมีหลักฐาน: (ก) e-Tax ทุกแถวของใบถูกยกเลิกในระบบแล้ว
    /// (ผู้ใช้กดยกเลิกที่หน้า e-Tax ก่อนตอบรับ) หรือ (ข) ใบถึงกรมสรรพากรแล้ว (ตอบรับ/อีเมลประทับเวลา) + <b>เลขอ้างอิงการยกเลิก/ใบลดหนี้</b>
    /// ที่ผู้ใช้ได้จากกรมสรรพากร/ผู้ให้บริการ (แถว e-Tax ไม่ถูกแตะ) · ส่งแล้วยังไม่รู้ผล (Submitted) ⇒ ปฏิเสธพร้อมทางไปต่อ · เหตุผลบังคับ · G6: pure</para>
    /// </summary>
    /// <param name="flagged">ใบนี้ติดธงอยู่จริง</param>
    /// <param name="effectiveEtax">สถานะ e-Tax ที่ใช้ตัดสิน (<see cref="EffectiveEtaxAsync"/> — null = ไม่มีแถวที่ยังมีผล)</param>
    public static EtaxCancellationResolutionVerdict EtaxCancellationResolution(bool flagged, EtaxStatus? effectiveEtax,
        string? rdCancellationReference, string? reason)
    {
        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        if (!flagged)
            return EtaxCancellationResolutionVerdict.Refused("เอกสารนี้ไม่ได้ติดธง “ต้องยกเลิกทาง e-Tax”" + Tail);
        if (string.IsNullOrWhiteSpace(reason))
            return EtaxCancellationResolutionVerdict.Refused("กรุณาระบุเหตุผล (เก็บไว้ให้ผู้สอบบัญชี)" + Tail);
        if (effectiveEtax == EtaxStatus.Submitted)
            return EtaxCancellationResolutionVerdict.Refused(
                "e-Tax ของใบนี้ส่งไปกรมสรรพากรแล้วแต่ยังไม่รู้ผล (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบนี้ก่อน "
                + "หรือรอผลตอบรับแล้วยกเลิกทางกรมสรรพากรพร้อมเลขอ้างอิง" + Tail);
        if (EtaxReachedRd(effectiveEtax))
        {
            var reference = rdCancellationReference?.Trim() ?? "";
            if (reference.Length < 3)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ใบนี้ถึงกรมสรรพากรแล้ว (ตอบรับ หรือ e-Tax by Email ที่ประทับเวลาแล้ว) — ระบบยืนยันการยกเลิกกับกรมสรรพากรเองไม่ได้ "
                    + "กรุณาระบุเลขอ้างอิงการยกเลิก/เลขที่ใบลดหนี้ที่ออกทางระบบ e-Tax ของกรมสรรพากร (หรือผู้ให้บริการ e-Tax) เป็นหลักฐาน" + Tail);
            return new EtaxCancellationResolutionVerdict(true, null, EtaxCancellationEvidence.RdReference);
        }
        return new EtaxCancellationResolutionVerdict(true, null, EtaxCancellationEvidence.EtaxVoidedInSystem);
    }
}

/// <summary>หลักฐานที่ใช้ปิดธง "ต้องยกเลิกทาง e-Tax" (<see cref="DocumentVoidPreconditions.EtaxCancellationResolution"/>)</summary>
public enum EtaxCancellationEvidence
{
    /// <summary>ไม่มี (ถูกปฏิเสธ)</summary>
    None = 0,
    /// <summary>e-Tax ทุกแถวของใบถูกยกเลิกในระบบแล้ว (ก่อนกรมสรรพากรตอบรับ)</summary>
    EtaxVoidedInSystem = 1,
    /// <summary>เลขอ้างอิงการยกเลิก/ใบลดหนี้จากกรมสรรพากรหรือผู้ให้บริการ e-Tax (ผู้ใช้ยืนยัน · แถว e-Tax ไม่ถูกแตะ)</summary>
    RdReference = 2,
}

/// <param name="Allowed">ปิดธงได้</param>
/// <param name="Reason">ข้อความไทยพร้อมทางไปต่อเมื่อปิดไม่ได้</param>
/// <param name="Evidence">หลักฐานที่ใช้ (ลง audit)</param>
public sealed record EtaxCancellationResolutionVerdict(bool Allowed, string? Reason, EtaxCancellationEvidence Evidence)
{
    internal static EtaxCancellationResolutionVerdict Refused(string reason) => new(false, reason, EtaxCancellationEvidence.None);
}

/// <summary>ทางเข้าที่ยกเลิกการรับชำระ — ตัดสินว่าใบเสร็จที่ถึงกรมสรรพากรแล้วต้องปฏิเสธหรือติดธง (<see cref="DocumentVoidPreconditions.AutoReceiptOnPaymentVoid"/>)</summary>
public enum PaymentVoidCause
{
    /// <summary>ผู้ใช้กดยกเลิกการชำระ (หน้าเอกสาร) หรือเส้นที่ยกเลิกตามคำสั่งผู้ใช้ — ใบเสร็จถึงกรมสรรพากรแล้ว = ปฏิเสธ</summary>
    User = 0,
    /// <summary>ยกเลิกการลงบัญชีรอบโอน — ด่าน <see cref="SettlementUnpostGate"/> ปฏิเสธไว้ก่อนแตะชิ้นแรกแล้ว · ที่นี่ถึงได้เฉพาะใบเสร็จที่ถูกส่ง e-Tax
    /// ระหว่างด่านกับลูป ⇒ ติดธงแบบเช็คเด้ง ไม่ throw กลางลูป (รอบ 200 ทีม V1F · V1-P2)</summary>
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
