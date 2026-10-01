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
        var emailed = await EtaxEmailedWithRdTimestampAsync(db, companyId, ids, ct);
        foreach (var id in ids)
            result[id] = EffectiveEtax(StrongestEtax(rows.Where(r => r.DocumentId == id).Select(r => r.Status)), emailed.Contains(id));
        return result;
    }

    /// <summary>เอกสารที่มีบันทึก e-Tax by Email ส่งสำเร็จพร้อม CC ประทับเวลาของกรมสรรพากร (ถึงกรมสรรพากรแล้วแม้แถว e-Tax ยังไม่ใช่ส่งแล้ว/ตอบรับ) —
    /// เกณฑ์ตัวเดียวของ <see cref="EffectiveEtaxAsync"/> และด่านยกเลิกแถว e-Tax รายแถว (รอบ 200 ทีม V1I · ฝ่ายค้าน V1H-O2) · tenant แล้ว</summary>
    public static async Task<HashSet<Guid>> EtaxEmailedWithRdTimestampAsync(AccountingDbContext db, Guid companyId,
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return new HashSet<Guid>();
        var ids = documentIds.Distinct().ToList();
        return (await db.DocumentEmailLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && !l.IsDeleted && l.DocumentId != null && ids.Contains(l.DocumentId.Value)
                    && l.IsEtaxByEmail && l.IncludedRdTimestamp && l.Status == EmailLogStatus.Sent)
                .Select(l => l.DocumentId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
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
                    : "เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ (ทำได้ก่อนกรมสรรพากรตอบรับ · ต้องแนบไฟล์หลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ e-Tax) ")
                + "แล้วกด “บันทึกว่ายกเลิกทาง e-Tax แล้ว” บนใบเสร็จนี้ (ระบบยกเลิกใบเสร็จและปลดบล็อกใบต้นทางให้) · แจ้งลูกค้าว่าใบเสร็จนี้ไม่มีผล"
                // รอบ 200 ทีม V1I (ฝ่ายค้าน V1H-O7): ทาง (ค) ใช้ได้เฉพาะใบที่กรมสรรพากรตอบรับแล้ว (EtaxCancellationResolution ปฏิเสธ Submitted) ⇒
                // ใบที่ยังไม่รู้ผลห้ามแนะนำทางที่จะถูกปฏิเสธ
                + (accepted
                    ? " · ถ้าลูกค้าชำระใหม่ครอบยอดแล้วและไม่ยกเลิกใบนี้ ให้เลือกทาง “ใบกำกับเดิมยังใช้ได้” (คำตัดสินข้อ 54)"
                    : ""));
        return new AutoReceiptEtaxDecision(AutoReceiptEtaxAction.Refuse,
            $"ยกเลิกการชำระนี้ไม่ได้ — ใบเสร็จ {no} ที่ออกคู่การรับชำระ "
            + (accepted
                ? "ถึงกรมสรรพากรแล้วและยกเลิกจากระบบนี้ไม่ได้ (Accepted — ตอบรับแล้ว หรือ e-Tax by Email ที่ประทับเวลาแล้ว) — ต้องยกเลิก/ออกแทนที่ระบบ e-Tax ของกรมสรรพากรก่อน "
                  + "· ถ้าเงินไม่ได้เข้าจริง (เช็คเด้ง) ให้บันทึก “เช็คเด้ง” ที่หน้าเช็ค ระบบจะกลับรายการเงินและติดธงให้ตามยกเลิก e-Tax"
                : "ส่ง e-Tax ไปกรมสรรพากรแล้ว (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบเสร็จนี้ก่อน (ทำได้ก่อนกรมสรรพากรตอบรับ · ต้องแนบไฟล์หลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ e-Tax) "
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
    internal static bool FlaggedReceiptKeepsTaxPoint(AutoReceiptEtaxAction action, decimal receiptVatAmount)
        => action == AutoReceiptEtaxAction.FlagEtaxCancellation && receiptVatAmount > 0.005m;

    /// <summary>
    /// **ใบเสร็จอัตโนมัติของการรับชำระที่กำลังยกเลิก ยังถือจุดความรับผิด §78/1 ของ "ใบต้นทางใบนี้" ไหม** (รอบ 200 ทีม V1H · คำตัดสินข้อ 50) —
    /// ตัวเดียวของเส้นใบเดียว (<c>ReversePaymentInternalAsync</c>) และเส้นจัดสรรหลายใบ (<c>ReverseMultiDocPaymentInternalAsync</c>)
    /// <para>การชำระที่จัดสรรหลายใบมีใบเสร็จ (ถ้ามี) อ้างได้ใบเดียว (<c>RelatedDocumentId</c>) ⇒ ใบเสร็จที่ติดธงถือจุดความรับผิดของใบที่มันอ้างเท่านั้น ·
    /// ใบอื่นในการจัดสรรเดียวกันตัดสินด้วยใบเสร็จถือ VAT ของตัวเอง (<c>LiveVatReceiptExistsAsync</c>) · เงื่อนไขภาษีเดียวกับ
    /// <see cref="FlaggedReceiptKeepsTaxPoint"/> · G6: pure</para>
    /// </summary>
    public static bool ReceiptHoldsTaxPointFor(Guid documentId, Guid? receiptRelatedDocumentId, AutoReceiptEtaxAction action, decimal receiptVatAmount)
        => receiptRelatedDocumentId == documentId && FlaggedReceiptKeepsTaxPoint(action, receiptVatAmount);

    /// <summary>
    /// **ใบกำกับที่ถูกยืนยันว่า “ใบกำกับเดิมยังใช้ได้” (ทาง ค · ข้อ 54) เสียการรับชำระที่ครอบยอดไปแล้ว — ต้องติดธงกลับไหม** (รอบ 200 ทีม V1I · ฝ่ายค้าน V1H-O1)
    /// <para>ทาง (ค) ล้างธงของใบเสร็จเดิม (การรับชำระของมันถูกยกเลิกไปแล้ว) โดยอาศัย "การรับชำระใหม่ที่ยังมีผลครอบยอดใบเสร็จ" เป็นเงื่อนไข · ถ้าภายหลังการรับชำระใหม่นั้น
    /// ถูกยกเลิก/เช็คเด้ง ⇒ ใบกำกับที่กรมสรรพากรมีระบุเงินที่ไม่ได้รับจริงอีกครั้ง — เดิมตัวถอยภาษีเห็นใบนี้เป็น "ใบเสร็จถือ VAT ที่ยังมีผล" แล้วจบเงียบ
    /// (ไม่ถอยภาษี · ไม่มีธง · ไม่มีคำเตือน) ⇒ <b>ติดธง "ต้องยกเลิกทาง e-Tax" กลับ</b> พร้อมทางไปต่อ (ทิศที่มองเห็นและแก้ทัน — DOCTRINE §1) ·
    /// ภาษีขายตามใบนี้ยังอยู่ในแบบ (ใบกำกับยังมีผล) จนผู้ใช้เลือกทางปิดธงใหม่</para>
    /// <para>ไม่ใช่ทาง (ค) ครั้งล่าสุด / ติดธงอยู่แล้ว / ใบไม่ถือภาษี / ยอดที่ยังมีผลยังครอบ ⇒ null (ไม่แตะ) · G6: pure</para>
    /// </summary>
    /// <param name="lastResolutionKeptOriginal">การปิดธงครั้งล่าสุดของใบนี้เป็นทาง (ค) (<see cref="EtaxReissueReview.LastResolutionKeptOriginal"/>)</param>
    /// <param name="flagged">ใบนี้ติดธง "ต้องยกเลิกทาง e-Tax" อยู่แล้ว</param>
    /// <param name="livePaymentCoverage">ยอดรับชำระที่ยังมีผลของใบต้นทาง <b>ไม่นับ</b>การรับชำระที่กำลังยกเลิก</param>
    /// <returns>ข้อความธง (null = ไม่ต้องติดธง)</returns>
    public static string? KeptOriginalCoverageLost(bool lastResolutionKeptOriginal, bool flagged, decimal receiptVatAmount,
        decimal receiptTotalAmount, decimal livePaymentCoverage, string? receiptNumber, string? paymentNumber)
    {
        if (!lastResolutionKeptOriginal || flagged || receiptVatAmount <= 0.005m) return null;
        if (livePaymentCoverage + 0.005m >= receiptTotalAmount) return null;
        var no = string.IsNullOrWhiteSpace(receiptNumber) ? "ใบนี้" : receiptNumber!.Trim();
        var pay = string.IsNullOrWhiteSpace(paymentNumber) ? "" : " " + paymentNumber!.Trim();
        return $"ต้องยกเลิกทาง e-Tax หรือยืนยันใหม่ — ใบกำกับ {no} เคยถูกยืนยันว่า “ใบกำกับเดิมยังใช้ได้” เพราะลูกค้าชำระใหม่ครอบยอด "
            + $"แต่การรับชำระ{pay} ถูกยกเลิกแล้ว ⇒ ยอดรับชำระที่ยังมีผล ({livePaymentCoverage:N2}) ไม่ครอบยอดใบกำกับนี้ ({receiptTotalAmount:N2}) · "
            + "ใบกำกับยังมีผลที่กรมสรรพากร และภาษีขายตามใบนี้ยังรายงานใน ภ.พ.30 — ทางไปต่อ: บันทึกการรับชำระใหม่ให้ครบแล้วเลือก “ใบกำกับเดิมยังใช้ได้” อีกครั้ง "
            + "หรือยกเลิกทาง e-Tax / ออกใบลดหนี้ แล้วบันทึกผลที่ใบนี้";
    }


    /// <summary>
    /// **ปิดธง "ต้องยกเลิกทาง e-Tax"** — ตัวตัดสินเดียวของ endpoint "บันทึกการยกเลิกทาง e-Tax" (รอบ 200 ทีม V1F · V1-R3 · แก้ต่อรอบ V1G ·
    /// คำตัดสินข้อ 46–47)
    /// <para>R1 (DECISION_AUDIT): ระบบ<b>ไม่ประทับ</b>สถานะของกรมสรรพากรเอง — แยก<b>สองทาง</b> (ข้อ 47 · RV1F-1 — เดิม "เลขที่ใบลดหนี้" ถูกใช้เป็นหลักฐาน
    /// แล้วระบบยกเลิกใบเสร็จ ⇒ ภาษีขายหายจากเดือนเดิมย้อนหลังทั้งที่ใบลดหนี้ไม่มีในระบบ):</para>
    /// <para>(ก) <see cref="EtaxCancellationPath.CancelledAtRd"/> — ใบกำกับถูกยกเลิกแล้ว ⇒ ยกเลิกใบเสร็จ (คงแสดงเป็น Voided) · หลักฐาน: แถว e-Tax
    /// ไม่ถึงกรมสรรพากร/ถูกยกเลิกในระบบนี้ หรือ (ถึงแล้ว) <b>เลขอ้างอิงการยกเลิก + ไฟล์หลักฐานที่แนบผ่านด่านไฟล์แนบ</b> (ข้อ 46 · RV1F-4)</para>
    /// <para>(ข) <see cref="EtaxCancellationPath.CreditNote"/> — ออกใบลดหนี้แล้ว ⇒ <b>ใบลดหนี้ต้องมีอยู่ในระบบนี้</b> (อ้างใบเสร็จ/ใบต้นทาง ·
    /// ผู้ซื้อเดียวกัน · ออกแล้ว · ภาษีครอบภาษีของใบเสร็จ · ยังไม่เคยใช้ปิดธงใบอื่น) — ใบเสร็จเดิมคงอยู่ (ภาษีขายลดในเดือนของใบลดหนี้ §86/10) ·
    /// ห้ามรับแค่ "เลขที่" ข้อความ</para>
    /// <para>(ค) <see cref="EtaxCancellationPath.OriginalStillValid"/> — ใบกำกับเดิมยังใช้ได้ (รอบ 200 ทีม V1H · คำตัดสินข้อ 54 — กรณีปกติที่สุดของเช็คเด้ง):
    /// ลูกค้าชำระใหม่ครอบยอดใบเสร็จนี้ · ใบนี้ตอบรับที่กรมสรรพากรแล้ว · ไม่มีใบกำกับใบอื่นของการขายนี้ · ภาษีขายของใบต้นทางยังไม่ถูกถอย ⇒ ล้างธงอย่างเดียว
    /// (ใบเสร็จเดิมคงมีผล · ไม่ถอยภาษี · ไม่ยกเลิกอะไร)</para>
    /// <para>ส่งแล้วยังไม่รู้ผล (Submitted) ⇒ ปฏิเสธพร้อมทางไปต่อทุกทาง · เหตุผลบังคับ · G6: pure</para>
    /// </summary>
    public static EtaxCancellationResolutionVerdict EtaxCancellationResolution(EtaxCancellationClaim c)
    {
        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        if (!c.Flagged)
            return EtaxCancellationResolutionVerdict.Refused("เอกสารนี้ไม่ได้ติดธง “ต้องยกเลิกทาง e-Tax”" + Tail);
        if (string.IsNullOrWhiteSpace(c.Reason))
            return EtaxCancellationResolutionVerdict.Refused("กรุณาระบุเหตุผล (เก็บไว้ให้ผู้สอบบัญชี)" + Tail);
        if (c.EffectiveEtax == EtaxStatus.Submitted)
            return EtaxCancellationResolutionVerdict.Refused(
                "e-Tax ของใบนี้ส่งไปกรมสรรพากรแล้วแต่ยังไม่รู้ผล (Submitted) — เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบนี้ก่อน "
                + "หรือรอผลตอบรับแล้วบันทึกตามสิ่งที่ทำจริงที่กรมสรรพากร (ยกเลิก หรือออกใบลดหนี้)" + Tail);

        if (c.Path == EtaxCancellationPath.OriginalStillValid)
        {
            // ── (ค) ใบกำกับเดิมยังใช้ได้ (รอบ 200 ทีม V1H · คำตัดสินข้อ 54): เช็คเด้ง → ลูกค้าชำระใหม่ครอบยอด ⇒ ใบเสร็จเดิมคงมีผล · ไม่ถอยภาษี ·
            // การรับชำระใหม่มีใบรับที่ไม่ใช่ใบกำกับ (ใบกำกับของการขายนี้มีใบเดียว) ──
            if (c.EffectiveEtax != EtaxStatus.Accepted)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ใบนี้ไม่มีใบกำกับที่มีผลที่กรมสรรพากร (e-Tax ไม่ถึงกรมสรรพากรหรือถูกยกเลิกในระบบนี้แล้ว) — “ใบกำกับเดิมยังใช้ได้” ใช้ไม่ได้ · "
                    + "ใช้ทาง “ยกเลิกทาง e-Tax แล้ว” (ระบบออกใบกำกับ ณ วันรับเงินให้การรับชำระที่ยังมีผล)" + Tail);
            if (c.OtherLiveVatReceipt)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ใบต้นทางมีใบเสร็จถือภาษีขาย (ใบกำกับ) ใบอื่นที่ยังมีผลอยู่แล้ว — ถ้าใบนี้ยังใช้ได้อีก การขายเดียวกันจะมีใบกำกับสองใบ (ภาษีขายนับซ้ำ) · "
                    + "ใช้ทาง “ยกเลิกทาง e-Tax แล้ว” หรือ “ออกใบลดหนี้แล้ว” สำหรับใบนี้" + Tail);
            if (c.SourceVatUndone)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ภาษีขายของใบต้นทางถูกถอยออกไปแล้ว (ข้อบกพร่องรอบก่อน — อยู่ในรายงานตรวจข้อ 44) — ปิดธงแบบ “ใบกำกับเดิมยังใช้ได้” ตอนนี้ ภ.พ.30 จะไม่มี"
                    + "ภาษีขายของใบกำกับนี้ · ให้ผู้ทำบัญชีตรวจรายงานข้อ 44 แล้วตัดสินก่อน" + Tail);
            if (c.LivePaymentCoverage + 0.005m < c.ReceiptTotalAmount)
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ยอดรับชำระที่ยังมีผลของใบต้นทาง ({c.LivePaymentCoverage:N2}) ยังไม่ครอบยอดใบเสร็จนี้ ({c.ReceiptTotalAmount:N2}) — ใบกำกับเดิมระบุยอดที่ลูกค้า"
                    + "ยังไม่ได้ชำระจริง · บันทึกการรับชำระใหม่ให้ครบก่อนแล้วกดอีกครั้ง หรือใช้ทาง “ยกเลิกทาง e-Tax แล้ว” / “ออกใบลดหนี้แล้ว”" + Tail);
            return new EtaxCancellationResolutionVerdict(true, null, EtaxCancellationEvidence.OriginalInvoiceStillValid);
        }

        if (c.Path == EtaxCancellationPath.CreditNote)
        {
            if (c.EffectiveEtax != EtaxStatus.Accepted)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ใบนี้ไม่ได้ถึงกรมสรรพากร (ไม่มีใบกำกับที่มีผลให้ลดหนี้) — ใช้ทาง “ยกเลิกทาง e-Tax แล้ว” แทน" + Tail);
            if (c.SourceRemainingPaid > 0.005m)
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ใบต้นทางยังมีการรับชำระที่มีผลอยู่ {c.SourceRemainingPaid:N2} บาท — การขายยังเกิดจริง ใบลดหนี้เต็มจำนวนจะทำให้ยอดลดหนี้รวมรับชำระเกินหนี้ "
                    + "(§86/10) · ถ้าลูกค้าชำระใหม่แล้วและไม่ได้ยกเลิกใบนี้ที่กรมสรรพากร ใบเสร็จนี้ยังเป็นใบกำกับของการขายนี้ — เลือกทาง “ใบกำกับเดิมยังใช้ได้” "
                    + "(คำตัดสินข้อ 54) · หรือยกเลิกการรับชำระที่เหลือก่อน หรือใช้ทาง “ยกเลิกทาง e-Tax แล้ว” เมื่อยกเลิกทางกรมสรรพากรแล้ว" + Tail);
            if (c.CreditNote is not { Found: true } cn)
                return EtaxCancellationResolutionVerdict.Refused(
                    "เลือกใบลดหนี้ในระบบนี้ที่ออกอ้างใบเสร็จ/ใบต้นทางแล้ว (สร้างใบลดหนี้ที่หน้าเอกสารก่อน) — ระบบรับเลขที่เป็นข้อความอย่างเดียวไม่ได้ "
                    + "(ภ.พ.30 ต้องเห็นใบลดหนี้จริงในเดือนที่ออก)" + Tail);
            if (cn.Type != DocumentType.CreditNote)
                return EtaxCancellationResolutionVerdict.Refused($"เอกสาร {cn.Number} ไม่ใช่ใบลดหนี้" + Tail);
            if (!DocumentStatusRules.IsIssued(cn.Status) || cn.Status == DocumentStatus.Voided)
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ใบลดหนี้ {cn.Number} ยังไม่ได้ออก/ถูกยกเลิก (สถานะ {cn.Status}) — อนุมัติใบลดหนี้ก่อนแล้วกดอีกครั้ง" + Tail);
            if (!cn.ReferencesReceiptOrSource)
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ใบลดหนี้ {cn.Number} ไม่ได้อ้างใบเสร็จนี้หรือใบต้นทางของใบเสร็จ (§86/10 ต้องอ้างใบกำกับเดิม)" + Tail);
            if (!cn.SameContact)
                return EtaxCancellationResolutionVerdict.Refused($"ใบลดหนี้ {cn.Number} ออกให้ผู้ซื้อคนละรายกับใบเสร็จนี้" + Tail);
            if (cn.VatAmount + 0.005m < c.ReceiptVatAmount)
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ภาษีของใบลดหนี้ {cn.Number} ({cn.VatAmount:N2}) น้อยกว่าภาษีของใบเสร็จนี้ ({c.ReceiptVatAmount:N2}) — ใบกำกับเดิมยังมีผลบางส่วน" + Tail);
            if (!string.IsNullOrWhiteSpace(cn.AlreadyResolvesReceiptNumber))
                return EtaxCancellationResolutionVerdict.Refused(
                    $"ใบลดหนี้ {cn.Number} ถูกใช้ปิดธงของใบเสร็จ {cn.AlreadyResolvesReceiptNumber} ไปแล้ว" + Tail);
            return new EtaxCancellationResolutionVerdict(true, null, EtaxCancellationEvidence.CreditNoteInSystem);
        }

        if (EtaxReachedRd(c.EffectiveEtax))
        {
            var reference = c.RdCancellationReference?.Trim() ?? "";
            if (reference.Length < 3)
                return EtaxCancellationResolutionVerdict.Refused(
                    "ใบนี้ถึงกรมสรรพากรแล้ว (ตอบรับ หรือ e-Tax by Email ที่ประทับเวลาแล้ว) — ระบบยืนยันการยกเลิกกับกรมสรรพากรเองไม่ได้ "
                    + "กรุณาระบุเลขอ้างอิงการยกเลิกที่ได้จากระบบ e-Tax ของกรมสรรพากร (หรือผู้ให้บริการ e-Tax) · ถ้าออกใบลดหนี้แทนการยกเลิก "
                    + "ให้เลือกทาง “ออกใบลดหนี้แล้ว”" + Tail);
            if (!c.EvidenceFileAttached)
                return EtaxCancellationResolutionVerdict.Refused(
                    "แนบไฟล์หลักฐานการยกเลิก (ภาพ/ไฟล์ตอบกลับจากกรมสรรพากรหรือผู้ให้บริการ e-Tax) ที่ใบเสร็จนี้ด้วย — เลขอ้างอิงอย่างเดียวระบบตรวจไม่ได้ "
                    + "(คำตัดสินข้อ 46)" + Tail);
            return new EtaxCancellationResolutionVerdict(true, null, EtaxCancellationEvidence.RdReference);
        }
        // ไม่ถึงกรมสรรพากร: ป้ายตามความจริง (RV1F-9) — แถวถูกยกเลิกในระบบนี้ ≠ ไม่เคยถึงกรมสรรพากร
        return new EtaxCancellationResolutionVerdict(true, null,
            c.EffectiveEtax == null && c.AnyEtaxRowVoided ? EtaxCancellationEvidence.EtaxVoidedInSystem : EtaxCancellationEvidence.EtaxNeverReachedRd);
    }

    /// <summary>ป้ายหลักฐานที่ลง audit/หมายเหตุภายใน — บอก "สิ่งที่ระบบรู้จริง" ห้ามเกินความจริง (RV1F-9: ยกเลิกในระบบนี้ ≠ กรมสรรพากรยกเลิก) · pure</summary>
    public static string EvidenceLabel(EtaxCancellationEvidence e) => e switch
    {
        EtaxCancellationEvidence.EtaxVoidedInSystem =>
            "ผู้ใช้ยกเลิกแถว e-Tax ในระบบนี้ก่อนกรมสรรพากรตอบรับ (ระบบไม่ได้ส่งคำยกเลิกถึงกรมสรรพากร — ไม่ใช่คำยืนยันจากกรมสรรพากร)",
        EtaxCancellationEvidence.EtaxNeverReachedRd =>
            "e-Tax ของใบนี้ไม่ได้ถึงกรมสรรพากร (สถานะที่เหลือไม่ใช่ส่งแล้ว/ตอบรับ) — ไม่มีอะไรต้องยกเลิกที่กรมสรรพากร",
        EtaxCancellationEvidence.RdReference =>
            "เลขอ้างอิงการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ + ไฟล์หลักฐานแนบ (ผู้ใช้ยืนยัน — ระบบตรวจกับกรมสรรพากรเองไม่ได้)",
        EtaxCancellationEvidence.CreditNoteInSystem =>
            "ใบลดหนี้ในระบบนี้อ้างใบกำกับเดิม (ใบเสร็จเดิมยังมีผล · ภาษีขายลดในเดือนของใบลดหนี้ §86/10)",
        EtaxCancellationEvidence.OriginalInvoiceStillValid =>
            "ผู้ใช้ยืนยันว่าใบกำกับเดิมยังใช้ได้ (ไม่ได้ยกเลิกที่กรมสรรพากร) — ลูกค้าชำระใหม่ครอบยอดใบเสร็จนี้ · ภาษีขายไม่ถูกถอย · การรับชำระใหม่มีใบรับที่ไม่ใช่ใบกำกับ",
        _ => "ไม่มีหลักฐาน",
    };

    // ═══ RV1F-2/3 (คำตัดสินข้อ 48): ถอยภาษีขายลงเดือนที่ตั้งรายการ · งวดปิด/เดือนล็อก = ปฏิเสธดัง · ปิดธงแล้วการขายต้องมีใบกำกับเสมอ ═══

    /// <summary>
    /// **ยกเลิกการรับชำระแล้วต้องถอยภาษีขายถึงกำหนด (§78/1) แต่เดือนที่ตั้งรายการปิด/ยื่นแล้ว — ทำอะไร** (ตัวตัดสินเดียว · ข้อ 48 · RV1F-2)
    /// <para>ตัวกลับลงวันที่เดียวกับ JE ย้ายภาษี (เดือนรับเงิน) เสมอ — เดิมลงวันที่ใบแจ้งหนี้ ⇒ ม.ค. −70 / ก.พ. +70 ขณะที่ ภ.พ.30 = 0/0 ·
    /// เดือนนั้นปิด/ยื่น/ล็อก ⇒ ผู้ใช้กดเอง = <b>ปฏิเสธดัง</b>พร้อมทางไปต่อ (เดิม LogError แล้วตอบสำเร็จ) · เช็คเด้ง/ยกเลิกการลงบัญชีรอบโอน =
    /// <b>ห้ามบล็อก</b>การกลับรายการเงิน (ข้อ 11) ⇒ ไม่ถอยภาษี + ธงที่มองเห็นบนใบต้นทางและในผลลัพธ์ · G6: pure</para>
    /// </summary>
    /// <param name="reclassPeriodLock">เหตุที่เดือนของ JE ย้ายภาษีปิด/ยื่นแล้ว (null = เปิด)</param>
    public static OutputVatUndoDecision OutputVatUndoOnPaymentVoid(string? reclassPeriodLock, PaymentVoidCause cause, string? invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(reclassPeriodLock))
            return new OutputVatUndoDecision(OutputVatUndoAction.Undo, null);
        var no = string.IsNullOrWhiteSpace(invoiceNumber) ? "ใบต้นทาง" : invoiceNumber!.Trim();
        if (cause == PaymentVoidCause.User)
            return new OutputVatUndoDecision(OutputVatUndoAction.Refuse,
                $"ยกเลิกการชำระนี้ไม่ได้ — ภาษีขายของ {no} ถึงกำหนดตอนรับเงินใน{reclassPeriodLock} · ถอยภาษีย้อนเข้างวดนั้นไม่ได้ "
                + "(แบบที่ยื่น/งบที่ปิดจะไม่ตรงบัญชี) — ทางไปต่อ: เปิดงวด/Reject & Reverse รายงานเดือนนั้นก่อน "
                + "หรือออกใบลดหนี้อ้างใบนี้ในเดือนปัจจุบัน (ภาษีขายลดในเดือนที่ออกใบลดหนี้ §86/10) · ระบบยังไม่ได้แตะอะไร");
        return new OutputVatUndoDecision(OutputVatUndoAction.KeepAndFlag,
            $"[VAT-UNDO-BLOCKED] {(cause == PaymentVoidCause.ChequeBounce ? "เช็คเด้ง" : "ยกเลิกการลงบัญชีรอบโอน")} — กลับรายการเงินแล้ว "
            + $"แต่ภาษีขายของ {no} ถึงกำหนดใน{reclassPeriodLock} จึงยังอยู่ในแบบ/บัญชีภาษีขาย (ถอยย้อนเข้างวดนั้นไม่ได้) — "
            + "ออกใบลดหนี้อ้างใบนี้ในเดือนปัจจุบัน (§86/10) หรือเปิดงวด/Reject & Reverse รายงานเดือนนั้นแล้วตรวจกับผู้ทำบัญชี");
    }

    /// <summary>
    /// **หลังปิดธงทาง (ก) ใบต้นทางต้องทำอะไรต่อ** (ข้อ 48 · RV1F-2/RV1F-3) — ใบเสร็จถือ VAT ที่ถูกยกเลิกเคยถือจุดความรับผิด §78/1 ของการขายนี้:
    /// <list type="bullet">
    /// <item>ใบเสร็จถือ VAT อื่นยังมีผล ⇒ ไม่ต้องทำอะไร (จุดความรับผิดยังถูกถือ)</item>
    /// <item>ไม่มีการรับชำระเหลือ ⇒ ถอยภาษีขาย (ลงเดือนที่ตั้งรายการ) · เดือนนั้นปิด/ยื่นแล้ว ⇒ ปฏิเสธ + ทางไปต่อ (ใบลดหนี้)</item>
    /// <item>มีการรับชำระที่ยังมีผล (เช็คเด้ง → รับใหม่ → ปิดธง · RV1F-3) ⇒ จุดความรับผิดย้ายไปวันรับเงินจริงครั้งแรกที่เหลือ (ถอยในเดือนเดิม +
    /// ย้ายใหม่ ณ วันรับเงิน) · รับครบงวดเดียว (กติกาเดียวกับ <see cref="SettlementReceiptPolicy.CarriesTaxInvoiceRole"/>) ⇒ <b>ออกใบกำกับ ณ วันรับเงิน</b>
    /// ให้การรับชำระนั้นในธุรกรรมเดียวกัน (ยกเลิกใบรับเปล่าเดิม) — ไม่มีช่วงที่การขายไม่มีใบกำกับ · ใบรับเปล่าที่ส่ง e-Tax แล้ว / งวดของวันรับเงินปิด ⇒ ปฏิเสธ</item>
    /// <item>รอบ 200 ทีม V1H (ข้อ 52): ใบแจ้งหนี้ถือ VAT ที่รับหลายงวด/บางส่วน/รับรวมกับเอกสารอื่น ⇒ <b>ปฏิเสธพร้อมทางไปต่อ</b>
    /// (<see cref="InstallmentTaxInvoiceRequired"/> — ใบกำกับรายงวดตาม §78/1 ระบบยังออกอัตโนมัติไม่ได้ · เดิมยกเลิกใบเสร็จแล้วไม่มีใบกำกับเลย)</item>
    /// </list>
    /// G6: pure</summary>
    public static EtaxCancelFollowUpPlan EtaxCancellationFollowUp(EtaxCancelFollowUpFacts f)
    {
        const string Tail = " · ระบบยังไม่ได้แตะอะไร";
        var none = new EtaxCancelFollowUpPlan(true, null, false, null, null, null);
        if (f.OtherLiveVatReceipt) return none;

        if (f.LivePayments.Count == 0)
        {
            if (f.OutputVatDueAt == null) return none;
            if (!string.IsNullOrWhiteSpace(f.ReclassPeriodLock))
                return EtaxCancelFollowUpPlan.Refused(
                    $"ภาษีขายของใบต้นทางถึงกำหนดใน{f.ReclassPeriodLock} — ยกเลิกใบเสร็จแล้วต้องถอยภาษีย้อนเข้างวดนั้น ซึ่งทำไม่ได้ · ทางไปต่อ: "
                    + "ออกใบลดหนี้อ้างใบต้นทางในเดือนปัจจุบันแล้วเลือกทาง “ออกใบลดหนี้แล้ว” (ภาษีขายลดในเดือนของใบลดหนี้) "
                    + "หรือเปิดงวด/Reject & Reverse รายงานเดือนนั้นก่อน" + Tail);
            return none with { UndoReclass = true };
        }

        var first = f.LivePayments.OrderBy(p => p.PaymentDate).ThenBy(p => p.PaymentId).First();
        var singleFull = f.LivePayments.Count == 1 && !first.HasAllocations && f.SourceFullyPaid;
        var carries = SettlementReceiptPolicy.CarriesTaxInvoiceRole(f.SourceType, f.SourceVat, singleFull, liveVatReceiptExists: false)
            && first.ReceiptVat <= 0.005m;
        // รอบ 200 ทีม V1H (คำตัดสินข้อ 52): การขายที่ต้องมีใบกำกับ (ใบแจ้งหนี้ถือ VAT) แต่การรับชำระที่ยังมีผลไม่ใช่ "รับครบงวดเดียว" (หลายงวด ·
        // บางส่วน · รับรวมกับเอกสารอื่น) ⇒ ใบกำกับต้องออก<b>รายงวด</b>ตามจุดความรับผิด §78/1 ซึ่งระบบยังออกอัตโนมัติไม่ได้ (📋 backlog) — เดิมยกเลิกใบเสร็จแล้ว
        // ย้ายภาษีไปวันรับเงินแรกโดยไม่มีใบกำกับเลย (การขายที่รับเงินแล้วไม่มีใบกำกับ) ⇒ ปฏิเสธพร้อมทางไปต่อ ธงยังค้างให้เห็น
        if (InstallmentTaxInvoiceRequired(f.SourceType, f.SourceVat, carries))
            return EtaxCancelFollowUpPlan.Refused(
                $"ใบต้นทางยังมีการรับชำระที่มีผล {f.LivePayments.Count} รายการแบบรับหลายงวด/บางส่วน/รับรวมกับเอกสารอื่น — ใบกำกับต้องออกรายงวดตามจุดความรับผิด "
                + "§78/1 (บริการ: รับเงินแต่ละงวด = จุดความรับผิดของงวดนั้น · คำตัดสินข้อ 52) ซึ่งระบบยังออกให้อัตโนมัติไม่ได้ · ยกเลิกใบเสร็จนี้ตอนนี้ = "
                + "การขายที่รับเงินแล้วไม่มีใบกำกับ · ทางไปต่อ: (1) ถ้าลูกค้าชำระใหม่ครอบยอดใบเสร็จนี้และไม่ได้ยกเลิกใบนี้ที่กรมสรรพากร ให้เลือกทาง "
                + "“ใบกำกับเดิมยังใช้ได้” (2) ถ้ายกเลิกที่กรมสรรพากรแล้วจริง ให้ผู้ทำบัญชีตัดสินการออกใบกำกับรายงวด (ธงยังค้างให้เห็นจนกว่าจะจัดการ)" + Tail);
        var moveTaxPoint = f.OutputVatDueAt?.Date != first.PaymentDate.Date;
        if (moveTaxPoint && f.OutputVatDueAt != null && !string.IsNullOrWhiteSpace(f.ReclassPeriodLock))
            return EtaxCancelFollowUpPlan.Refused(
                $"ภาษีขายของใบต้นทางถึงกำหนดใน{f.ReclassPeriodLock} (วันที่ใบเสร็จที่ยกเลิก) แต่เงินที่มีผลรับจริงวันที่ {first.PaymentDate:dd/MM/yyyy} — "
                + "ย้ายจุดความรับผิดต้องถอยในงวดเดิมซึ่งปิด/ยื่นแล้ว · ทางไปต่อ: เปิดงวด/Reject & Reverse รายงานเดือนนั้นก่อน (หรือปรึกษาผู้ทำบัญชี — "
                + "ถ้าการรับเงินใหม่อยู่ในเดือนเดียวกัน ใบกำกับเดิมอาจยังใช้ได้โดยไม่ต้องยกเลิก)" + Tail);
        if ((moveTaxPoint || carries) && !string.IsNullOrWhiteSpace(f.FirstPaymentPeriodLock))
            return EtaxCancelFollowUpPlan.Refused(
                $"การรับชำระที่มีผล ({first.PaymentDate:dd/MM/yyyy}) อยู่ใน{f.FirstPaymentPeriodLock} — ภาษีขาย/ใบกำกับ ณ วันรับเงินต้องลงงวดนั้นซึ่งปิด/ยื่นแล้ว · "
                + "ทางไปต่อ: เปิดงวด/Reject & Reverse รายงานเดือนนั้นก่อน" + Tail);
        if (carries && first.ReceiptReachedRd)
            return EtaxCancelFollowUpPlan.Refused(
                $"การรับชำระที่มีผลอยู่มีใบรับ {first.ReceiptNumber} ที่ส่ง e-Tax แล้ว — ระบบต้องออกใบกำกับ ณ วันรับเงินแทนใบรับนั้น (การขายนี้ต้องมีใบกำกับ) "
                + "แต่ยกเลิกใบที่ถึงกรมสรรพากรแล้วเงียบ ๆ ไม่ได้ · ทางไปต่อ: ยกเลิก e-Tax ของใบรับนั้นก่อน (หรือยกเลิกทางกรมสรรพากร) แล้วกดอีกครั้ง" + Tail);
        return new EtaxCancelFollowUpPlan(true, null,
            UndoReclass: moveTaxPoint && f.OutputVatDueAt != null,
            ReclassAt: moveTaxPoint ? first.PaymentDate.Date : null,
            IssueVatReceiptForPaymentId: carries ? first.PaymentId : null,
            VoidPlainReceiptId: carries ? first.ReceiptId : null);
    }

    /// <summary>
    /// **การรับชำระที่ยังมีผลต้องได้ใบกำกับรายงวด (§78/1) ที่ระบบยังออกอัตโนมัติไม่ได้ไหม** (รอบ 200 ทีม V1H · คำตัดสินข้อ 52) — ใบต้นทางเป็นใบแจ้งหนี้
    /// ถือ VAT (ภาษีขายพักจนรับเงิน ⇒ การรับเงินคือจุดที่ต้องออกใบกำกับ) และการรับชำระไม่ใช่ "รับครบงวดเดียวที่ออกใบกำกับ ณ วันรับเงินได้"
    /// (<paramref name="singleShotTaxInvoiceIssuable"/> = ผลของ <see cref="SettlementReceiptPolicy.CarriesTaxInvoiceRole"/> ของงวดแรก) · G6: pure
    /// </summary>
    internal static bool InstallmentTaxInvoiceRequired(DocumentType sourceType, decimal sourceVat, bool singleShotTaxInvoiceIssuable)
        => sourceType == DocumentType.Invoice && sourceVat > 0m && !singleShotTaxInvoiceIssuable;

    /// <summary>
    /// **ยกเลิก/กู้คืนเอกสารที่ e-Tax ถึงกรมสรรพากรแล้ว** (ข้อ 43 · RV1F-6) — ตัวตัดสินเดียวของ <c>VoidDocumentAsync</c> และ <c>RestoreVoidedDocumentAsync</c>
    /// (ทุกทางเข้า: หน้าเอกสาร · integration · ยกเลิกการลงบัญชีรอบโอน) · ชุดสถานะ <see cref="EtaxReachedRdStatuses"/> + e-Tax by Email (<see cref="EffectiveEtaxAsync"/>)
    /// — เดิมดูแค่ Accepted แล้วพลิก Submitted เป็น Voided เงียบ ⇒ กรมสรรพากรตอบรับภายหลังได้ทั้งที่ระบบเรายกเลิกไปแล้ว · null = ไม่บล็อก · G6: pure
    /// </summary>
    public static string? DocumentVoidEtaxBlock(EtaxStatus? effectiveEtax, string? documentNumber, bool restore)
    {
        if (!EtaxReachedRd(effectiveEtax)) return null;
        var no = string.IsNullOrWhiteSpace(documentNumber) ? "เอกสารนี้" : documentNumber!.Trim();
        var what = restore ? "กู้คืน" : "ยกเลิก";
        return effectiveEtax == EtaxStatus.Accepted
            ? $"{what}ไม่ได้ — e-Tax ของ {no} ถึงกรมสรรพากรแล้ว (ตอบรับแล้ว หรือ e-Tax by Email ที่ประทับเวลาแล้ว) · "
              + (restore ? "ออกเอกสารใหม่แทน" : "ต้องยกเลิกทางระบบ e-Tax ของกรมสรรพากรก่อน หรือออกใบลดหนี้อ้างใบนี้ (§86/10)")
              + " · ระบบยังไม่ได้แตะอะไร"
            : $"{what}ไม่ได้ — e-Tax ของ {no} ส่งไปกรมสรรพากรแล้วแต่ยังไม่รู้ผล (Submitted) · เปิดหน้า e-Tax แล้วกดยกเลิก e-Tax ของใบนี้ก่อน "
              + $"(ทำได้ก่อนกรมสรรพากรตอบรับ · ต้องแนบไฟล์หลักฐานการยกเลิกจากกรมสรรพากร/ผู้ให้บริการ e-Tax) แล้ว{what}อีกครั้ง · ระบบยังไม่ได้แตะอะไร";
    }
}

/// <summary>ทางของการปิดธง "ต้องยกเลิกทาง e-Tax" (คำตัดสินข้อ 47)</summary>
public enum EtaxCancellationPath
{
    /// <summary>(ก) ยกเลิกทาง e-Tax สำเร็จ — ใบเสร็จไม่มีผลแล้ว ⇒ ยกเลิกใบเสร็จ (คงแสดงเป็น Voided)</summary>
    CancelledAtRd = 0,
    /// <summary>(ข) ออกใบลดหนี้แล้ว — ใบเสร็จเดิมยังมีผล ⇒ ผูกใบลดหนี้ในระบบนี้ (ภาษีขายลดในเดือนของใบลดหนี้)</summary>
    CreditNote = 1,
    /// <summary>(ค) ใบกำกับเดิมยังใช้ได้ (คำตัดสินข้อ 54) — ลูกค้าชำระใหม่ครอบยอดแทนเช็คที่เด้ง ⇒ ล้างธงอย่างเดียว ใบเสร็จเดิมคงมีผล · ไม่ถอยภาษี</summary>
    OriginalStillValid = 2,
}

/// <summary>หลักฐานที่ใช้ปิดธง "ต้องยกเลิกทาง e-Tax" (<see cref="DocumentVoidPreconditions.EtaxCancellationResolution"/>) — ป้าย
/// <see cref="DocumentVoidPreconditions.EvidenceLabel"/></summary>
public enum EtaxCancellationEvidence
{
    /// <summary>ไม่มี (ถูกปฏิเสธ)</summary>
    None = 0,
    /// <summary>แถว e-Tax ของใบถูกยกเลิก<b>ในระบบนี้</b>ก่อนกรมสรรพากรตอบรับ (ไม่ใช่คำยืนยันจากกรมสรรพากร — RV1F-9)</summary>
    EtaxVoidedInSystem = 1,
    /// <summary>เลขอ้างอิงการยกเลิกจากกรมสรรพากรหรือผู้ให้บริการ e-Tax + ไฟล์หลักฐานที่แนบ (ผู้ใช้ยืนยัน · แถว e-Tax ไม่ถูกแตะ · ข้อ 46)</summary>
    RdReference = 2,
    /// <summary>e-Tax ของใบไม่เคยถึงกรมสรรพากร (สถานะที่เหลือ สร้าง/เซ็น/ผิดพลาด/ถูกปฏิเสธ — RV1F-9)</summary>
    EtaxNeverReachedRd = 3,
    /// <summary>ใบลดหนี้ในระบบนี้อ้างใบกำกับเดิม (ทาง ข · ข้อ 47)</summary>
    CreditNoteInSystem = 4,
    /// <summary>ผู้ใช้ยืนยันว่าใบกำกับเดิมยังใช้ได้ + การรับชำระที่มีผลครอบยอดใบเสร็จ (ทาง ค · ข้อ 54)</summary>
    OriginalInvoiceStillValid = 5,
}

/// <summary>ใบลดหนี้ที่ผู้ใช้เลือกปิดธงทาง (ข) — ข้อเท็จจริงจากฐาน (tenant แล้ว)</summary>
/// <param name="Found">พบใบนี้ในบริษัทนี้ (false = ไม่พบ)</param>
/// <param name="ReferencesReceiptOrSource">อ้าง (<c>RelatedDocumentId</c>) ใบเสร็จนี้หรือใบต้นทางของใบเสร็จ</param>
/// <param name="AlreadyResolvesReceiptNumber">เลขที่ใบเสร็จอื่นที่ใบลดหนี้นี้เคยปิดธงไปแล้ว (null = ยังไม่เคย)</param>
public sealed record EtaxCreditNoteFact(bool Found, string? Number, DocumentType Type, DocumentStatus Status, bool ReferencesReceiptOrSource,
    bool SameContact, decimal VatAmount, string? AlreadyResolvesReceiptNumber);

/// <summary>คำขอปิดธง + ข้อเท็จจริงที่ตัวตัดสินต้องใช้ (<see cref="DocumentVoidPreconditions.EtaxCancellationResolution"/>)</summary>
/// <param name="EffectiveEtax">สถานะ e-Tax ที่ใช้ตัดสิน (<see cref="DocumentVoidPreconditions.EffectiveEtaxAsync"/> — null = ไม่มีแถวที่ยังมีผล)</param>
/// <param name="AnyEtaxRowVoided">มีแถว e-Tax ของใบที่ถูกยกเลิก (Voided) ในระบบนี้</param>
/// <param name="EvidenceFileAttached">ไฟล์หลักฐานที่ผู้ใช้ระบุผ่านด่านไฟล์แนบแล้ว และเป็นไฟล์ของใบเสร็จนี้จริง</param>
/// <param name="SourceRemainingPaid">ยอดรับชำระที่ยังมีผลบนใบต้นทาง</param>
/// <param name="ReceiptTotalAmount">ยอดรวมของใบเสร็จที่ติดธง (ทาง ค — การรับชำระใหม่ต้องครอบยอดนี้)</param>
/// <param name="LivePaymentCoverage">ยอดรับชำระที่ยังมีผลของใบต้นทาง นับจากรายการรับชำระจริง (ตรง = ยอด+ค่าธรรมเนียม+บรรทัดปรับ · จัดสรร = ยอดจัดสรร) — ทาง ค</param>
/// <param name="OtherLiveVatReceipt">ใบต้นทางมีใบเสร็จถือ VAT ใบอื่นที่ยังมีผล (ทาง ค — ห้ามใบกำกับสองใบของการขายเดียว)</param>
/// <param name="SourceVatUndone">ภาษีขายของใบต้นทางถูกถอยไปแล้วทั้งที่ใบนี้ยังมีผล (<see cref="EtaxReissueReview.FlaggedReceiptVatUndone"/> — ทาง ค)</param>
public sealed record EtaxCancellationClaim(bool Flagged, EtaxStatus? EffectiveEtax, bool AnyEtaxRowVoided, EtaxCancellationPath Path,
    string? Reason, string? RdCancellationReference, bool EvidenceFileAttached, EtaxCreditNoteFact? CreditNote, decimal ReceiptVatAmount,
    decimal SourceRemainingPaid, decimal ReceiptTotalAmount = 0m, decimal LivePaymentCoverage = 0m, bool OtherLiveVatReceipt = false,
    bool SourceVatUndone = false);

/// <param name="Allowed">ปิดธงได้</param>
/// <param name="Reason">ข้อความไทยพร้อมทางไปต่อเมื่อปิดไม่ได้</param>
/// <param name="Evidence">หลักฐานที่ใช้ (ลง audit ด้วยป้าย <see cref="DocumentVoidPreconditions.EvidenceLabel"/>)</param>
public sealed record EtaxCancellationResolutionVerdict(bool Allowed, string? Reason, EtaxCancellationEvidence Evidence)
{
    internal static EtaxCancellationResolutionVerdict Refused(string reason) => new(false, reason, EtaxCancellationEvidence.None);
}

/// <summary>การรับชำระที่ยังมีผลของใบต้นทาง (ตอนปิดธงทาง ก)</summary>
/// <param name="ReceiptVat">ภาษีของใบเสร็จอัตโนมัติของการรับชำระนี้ (0 = ใบรับเปล่า/ไม่มีใบ)</param>
/// <param name="ReceiptReachedRd">ใบเสร็จอัตโนมัติของการรับชำระนี้ถึงกรมสรรพากรแล้ว (ยกเลิกเงียบไม่ได้)</param>
public sealed record EtaxCancelLivePayment(Guid PaymentId, DateTime PaymentDate, bool HasAllocations, Guid? ReceiptId, string? ReceiptNumber,
    decimal ReceiptVat, bool ReceiptReachedRd);

/// <summary>ข้อเท็จจริงของ <see cref="DocumentVoidPreconditions.EtaxCancellationFollowUp"/></summary>
/// <param name="OutputVatDueAt">วันที่ภาษีขายถึงกำหนดของใบต้นทาง (null = ยังพัก/ไม่มี)</param>
/// <param name="OtherLiveVatReceipt">มีใบเสร็จถือ VAT อื่น (นอกจากใบที่กำลังยกเลิก) ที่ยังมีผล</param>
/// <param name="ReclassPeriodLock">เหตุที่เดือนของ JE ย้ายภาษีขายปิด/ยื่นแล้ว (null = เปิด)</param>
/// <param name="FirstPaymentPeriodLock">เหตุที่เดือนของวันรับเงินครั้งแรกที่ยังมีผลปิด/ยื่นแล้ว (null = เปิด/ไม่มี)</param>
public sealed record EtaxCancelFollowUpFacts(DocumentType SourceType, decimal SourceVat, bool SourceFullyPaid, DateTime? OutputVatDueAt,
    bool OtherLiveVatReceipt, IReadOnlyList<EtaxCancelLivePayment> LivePayments, string? ReclassPeriodLock, string? FirstPaymentPeriodLock);

/// <param name="UndoReclass">ถอยภาษีขายถึงกำหนด (ลงวันที่ของ JE ย้ายภาษีเอง)</param>
/// <param name="ReclassAt">ย้ายภาษีขายถึงกำหนดใหม่ ณ วันรับเงินนี้ (null = ไม่ย้าย)</param>
/// <param name="IssueVatReceiptForPaymentId">ออกใบเสร็จถือ VAT (ใบกำกับ ณ วันรับเงิน) ให้การรับชำระนี้</param>
/// <param name="VoidPlainReceiptId">ใบรับเปล่าเดิมของการรับชำระนั้นที่ต้องยกเลิก (คงแสดงเป็น Voided)</param>
public sealed record EtaxCancelFollowUpPlan(bool Allowed, string? Reason, bool UndoReclass, DateTime? ReclassAt,
    Guid? IssueVatReceiptForPaymentId, Guid? VoidPlainReceiptId)
{
    internal static EtaxCancelFollowUpPlan Refused(string reason) => new(false, reason, false, null, null, null);
}

/// <summary>ผลของ <see cref="DocumentVoidPreconditions.OutputVatUndoOnPaymentVoid"/></summary>
public enum OutputVatUndoAction
{
    /// <summary>ถอยภาษีขายได้ (ลงเดือนที่ตั้งรายการ)</summary>
    Undo = 0,
    /// <summary>ปฏิเสธการยกเลิกการชำระทั้งรายการ (ข้อความพร้อมทางไปต่อ)</summary>
    Refuse = 1,
    /// <summary>กลับรายการเงินต่อ แต่ไม่ถอยภาษี + ธงบนใบต้นทาง/ในผลลัพธ์ (เช็คเด้ง · ยกเลิกการลงบัญชีรอบโอน)</summary>
    KeepAndFlag = 2,
}

/// <param name="Message">ข้อความไทย — ปฏิเสธ หรือธง · Undo = null</param>
public sealed record OutputVatUndoDecision(OutputVatUndoAction Action, string? Message);


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
