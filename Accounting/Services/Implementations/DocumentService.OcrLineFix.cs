using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลแก้ส่วนลดบรรทัดของเอกสารจากสแกนหนึ่งใบ (<see cref="DocumentService.FixOcrLineDiscountAsync"/>)</summary>
/// <param name="LinesFixed">จำนวนบรรทัดที่แก้ (0 = ไม่มีอะไรต้องแก้ — อาจแก้ไปแล้ว)</param>
/// <param name="Message">ข้อความถึงผู้ใช้</param>
public sealed record OcrLineDiscountFixResult(int LinesFixed, string Message);

/// <summary>บรรทัดหนึ่งที่ "เปิดแก้แล้วบันทึก ยอดจะขยับ" เพราะส่วนลด % ซ้อนบนราคาที่ลดแล้ว</summary>
/// <param name="LineIndex">ลำดับในรายการบรรทัด (เรียงตาม LineOrder · ไม่นับบรรทัดที่ลบ)</param>
/// <param name="LineOrder">ลำดับที่ผู้ใช้เห็น</param>
/// <param name="Stored">ยอดก่อน VAT ที่เก็บไว้ (= ยอดตามกระดาษ)</param>
/// <param name="AfterResave">ยอดที่จะได้ถ้าเปิดแก้แล้วบันทึก (ComputeLineAmounts หัก % ซ้ำ)</param>
public sealed record OcrLineDiscountFinding(int LineIndex, int LineOrder, string Description, decimal DiscountPercent,
    decimal Stored, decimal AfterResave);

/// <summary>
/// **ทีม E 2026-10-13 — เอกสารจากสแกนที่ "เปิดแก้แล้วบันทึก ยอดขยับเอง"** (ข้อมูลที่บันทึกไว้ก่อนทีม D B5 แก้ <c>OcrLineReconciler.LineDiscount</c>)
///
/// <para>ตัวสร้างบรรทัดจากสแกนเคยเก็บบรรทัด "ไม่มีราคาต่อหน่วย" เป็น <c>UnitPrice = ยอดหลังลด</c> + <c>DiscountPercent</c> ของทั้งใบ +
/// <c>DiscountAmount = 0</c> ⇒ <see cref="ComputeLineAmounts"/> (ส่วนลดบาท 0 ⇒ ใช้ %) หัก % ซ้ำ — ใบ 950 ลด 5% กลายเป็น 902.50 เมื่อมีคนกดบันทึก.
/// ยอด/VAT/JE ที่บันทึกไว้<b>ถูกอยู่แล้ว</b> (ตามกระดาษ) — ที่ผิดคือ metadata ส่วนลดที่ทำให้การบันทึกครั้งถัดไปเปลี่ยนเงิน</para>
///
/// <para>ตัวตรวจ (<see cref="FindOcrLineDiscountIssues"/>) กับตัวแก้ (<see cref="FixOcrLineDiscountAsync"/>) ใช้การจำลองตัวเดียว
/// (<see cref="ResaveLineNets"/> = ลำดับเดียวกับ <c>UpdateDocumentAsync</c>: AllocateBillDeductions → ComputeLineAmounts → ReconcileTaxRounding) ·
/// ไม่มี migration อัตโนมัติ (คำตัดสินเจ้าของ 2026-10-08 "รายงานก่อน ซ่อมรายตัว") — ผู้ใช้กด "แก้" รายใบจากหน้าตรวจข้อมูล</para>
/// </summary>
public partial class DocumentService
{
    /// <summary>รหัสกฎของ audit การแก้ส่วนลดบรรทัดจากสแกน</summary>
    public const string OcrLineDiscountFixRuleCode = "OCR-LINE-DISC-FIX";

    /// <summary>บรรทัดที่บันทึกไว้ในรูปคำขอแก้ไข (ค่าที่หน้าเอกสารส่งกลับเมื่อเปิดแก้แล้วกดบันทึกโดยไม่แตะ)</summary>
    internal static DocumentLineRequest ResaveRequest(DocumentLine l, decimal? unitPrice = null, decimal? discountPercent = null,
        decimal? discountAmount = null)
        => new(Description: l.Description, Quantity: l.Quantity, Unit: l.Unit, UnitPrice: unitPrice ?? l.UnitPrice,
            DiscountPercent: discountPercent ?? l.DiscountPercent, VatRate: l.VatRate, WithholdingTaxRate: l.WithholdingTaxRate,
            AccountId: l.AccountId, DiscountAmount: discountAmount ?? l.DiscountAmount);

    /// <summary>ยอดก่อน VAT ของทุกบรรทัด "ถ้าบันทึกซ้ำ" — สูตรเดียวกับ <c>UpdateDocumentAsync</c> (ไม่ใช่สำเนา: เรียกตัวคำนวณจริง)</summary>
    internal static decimal[] ResaveLineNets(IReadOnlyList<DocumentLineRequest> lines, bool pricesIncludeVat,
        decimal billDiscountPercent, decimal billDiscountAmount, decimal depositBaseDeducted)
    {
        var (alloc, _, _) = AllocateBillDeductions(lines, pricesIncludeVat, billDiscountPercent, billDiscountAmount, depositBaseDeducted);
        var amts = new LineAmounts[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            amts[i] = ComputeLineAmounts(lines[i], pricesIncludeVat, alloc[i]);
        ReconcileTaxRounding(lines, amts, pricesIncludeVat);
        return amts.Select(a => a.NetAmount).ToArray();
    }

    /// <summary>บรรทัดที่ติด % ส่วนลดโดยไม่มีส่วนลดบาท และการบันทึกซ้ำจะทำให้ยอดก่อน VAT ต่างจากที่เก็บไว้เกิน 0.01 —
    /// ตัวตัดสินเดียวของรายงานตรวจข้อมูลและปุ่ม "แก้" · <paramref name="lines"/> = บรรทัดที่ไม่ถูกลบ เรียงตาม LineOrder</summary>
    internal static List<OcrLineDiscountFinding> FindOcrLineDiscountIssues(Document doc, IReadOnlyList<DocumentLine> lines)
    {
        var reqs = lines.Select(l => ResaveRequest(l)).ToList();
        var nets = ResaveLineNets(reqs, doc.PricesIncludeVat, doc.BillDiscountPercent, doc.BillDiscountAmount, doc.DepositBaseDeducted);
        var found = new List<OcrLineDiscountFinding>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.DiscountPercent <= 0m || l.DiscountAmount != 0m) continue;
            if (Math.Abs(nets[i] - l.Amount) <= 0.01m) continue;
            found.Add(new OcrLineDiscountFinding(i, l.LineOrder, l.Description, l.DiscountPercent, l.Amount, nets[i]));
        }
        return found;
    }

    /// <summary>ราคา/ส่วนลดบาทใหม่ของบรรทัดหนึ่ง (ส่วนลด % = 0) ที่ทำให้บรรทัดได้ยอดก่อน VAT <paramref name="targetNet"/> —
    /// ราคาเดิมคงไว้ถ้า round(จำนวน × ราคา) ≥ ยอดที่ต้องการ (ส่วนลดบาท = ส่วนต่าง) · ไม่พอ (จำนวน &lt; 1) ⇒ ราคาปัดขึ้น 2 ตำแหน่ง ·
    /// ราคารวม VAT: ยอดรวม VAT เป้า = ยอดก่อน VAT + VAT ที่เก็บ (หรือค่าใกล้สุด ±0.02 ที่ถอด VAT แล้วได้ยอดก่อน VAT เดิม) · null = หาไม่ได้</summary>
    internal static (decimal UnitPrice, decimal DiscountAmount)? OcrLineDiscountRepair(
        decimal quantity, decimal unitPrice, decimal vatRate, bool pricesIncludeVat, decimal targetNet, decimal storedLineVat)
    {
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        if (quantity <= 0m || targetNet < 0m) return null;
        decimal? afterTarget = targetNet;
        if (pricesIncludeVat && vatRate > 0m)
        {
            // ยอดรวม VAT หลังลดที่ถอด VAT แล้วได้ยอดก่อน VAT เดิมพอดี — ตัวแรกที่ลงตัวตามลำดับ (VAT ที่เก็บก่อน · สูตรตรงรองลงมา)
            afterTarget = new[] { targetNet + storedLineVat, Math.Round(targetNet * (100m + vatRate) / 100m, 2, R) }
                .SelectMany(seed => new[] { 0m, -0.01m, 0.01m, -0.02m, 0.02m }.Select(d => seed + d))
                .Where(a => a >= 0m && Math.Round(a * 100m / (100m + vatRate), 2, R) == targetNet)
                .Select(a => (decimal?)a)
                .FirstOrDefault();
            if (afterTarget == null) return null;
        }
        var want = afterTarget.Value;
        var gross = Math.Round(quantity * unitPrice, 2, R);
        if (gross >= want) return (unitPrice, gross - want);
        var up = Math.Ceiling(want / quantity * 100m) / 100m;
        return (up, Math.Round(quantity * up, 2, R) - want);
    }

    /// <summary>
    /// ฝ่ายค้านรอบ 204 L2 — เหตุที่ปุ่ม "แก้" ใช้กับใบนี้ไม่ได้ (null = แก้ได้) · ตัวเดียวของหน้าตรวจข้อมูล (ซ่อนปุ่ม) และ <see cref="FixOcrLineDiscountAsync"/> (ปฏิเสธ) ·
    /// ใบลด/เพิ่มหนี้: ฝั่งจากตัวตัดสินกลาง <see cref="AdjustmentNoteAccount.ResolvePostedSide"/> (ผู้ใช้ย้ายฝั่ง → ใบต้นทาง → ผู้ติดต่อเป็นผู้ขายอย่างเดียว) ·
    /// กติกาอยู่ที่ <see cref="OcrLineDiscountFixScope"/> · tenant: อ่านใบต้นทาง/ผู้ติดต่อเฉพาะ <c>CompanyId</c> นี้
    /// </summary>
    internal static async Task<string?> OcrLineDiscountFixRefusalAsync(AccountingDbContext db, Guid companyId, Document doc, CancellationToken ct = default)
    {
        (bool IsPurchase, bool Unknown)? cnDnSide = null;
        if (doc.DocumentType is DocumentType.CreditNote or DocumentType.DebitNote)
        {
            DocumentType? srcType = doc.RelatedDocumentId is Guid srcId
                ? await db.Documents.AsNoTracking().Where(x => x.Id == srcId && x.CompanyId == companyId)
                    .Select(x => (DocumentType?)x.DocumentType).FirstOrDefaultAsync(ct)
                : null;
            var supplierOnly = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(c => c.Id == doc.ContactId && c.CompanyId == companyId && c.IsSupplier && !c.IsCustomer, ct);
            cnDnSide = AdjustmentNoteAccount.ResolvePostedSide(doc.CnDnPurchaseSideOverride, srcType, null, supplierOnly);
        }
        return OcrLineDiscountFixScope.Refusal(doc.DocumentNumber, doc.Status,
            OcrLineDiscountFixScope.IsOurSalesDocument(doc.DocumentType, cnDnSide));
    }

    /// <summary>
    /// ปุ่ม "แก้" ของแถว <c>OcrLineDiscountWouldChangeOnResave</c> — ตั้ง <c>DiscountPercent = 0</c> และคิดส่วนลดบาท/ราคาใหม่ให้บันทึกซ้ำได้
    /// ยอดเดิมทุกบรรทัด · <b>ไม่แตะ</b> ยอด/VAT/ยอดรวม/JE/เลขที่ (metadata อย่างเดียว ⇒ ทำได้ทุกสถานะที่ไม่ใช่ยกเลิก) ·
    /// จำลองผลก่อนเขียน — เงินบรรทัดใดจะขยับ ⇒ ปฏิเสธพร้อมเหตุผล (ไม่บันทึกอะไร) · audit <see cref="OcrLineDiscountFixRuleCode"/> ก่อน/หลัง
    /// </summary>
    /// <param name="db">context ของคำขอ (static เพื่อให้หน้าตรวจข้อมูลเรียกได้โดยไม่ผูก DI ของ IDocumentService)</param>
    internal static async Task<OcrLineDiscountFixResult> FixOcrLineDiscountAsync(
        AccountingDbContext db, Guid companyId, Guid documentId, Guid? actorUserId, CancellationToken ct = default)
    {
        var doc = await db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสาร");
        if (doc.Status == DocumentStatus.Voided)
            throw new BusinessRuleException("เอกสารนี้ถูกยกเลิกแล้ว — ไม่มีผลทางบัญชี ไม่ต้องแก้", OcrLineDiscountFixRuleCode, 409);
        var fromScan = await db.Set<OcrScanResult>().AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && s.CreatedDocumentId == documentId, ct);
        if (!fromScan)
            throw new BusinessRuleException("เอกสารนี้ไม่ได้สร้างจากสแกน — ปุ่มนี้แก้เฉพาะบรรทัดที่ตัวสร้างจากสแกนเขียนไว้", OcrLineDiscountFixRuleCode, 409);
        // ฝ่ายค้านรอบ 204 L2: ใบขายที่เราออกแล้ว (§86/4) ⇒ ไม่แก้รายการที่พิมพ์ — ทางไปต่อ "ยกเลิกแล้วออกใบแทน" (ใบฝั่งซื้อ/ใบขายร่าง แก้ได้เหมือนเดิม)
        if (await OcrLineDiscountFixRefusalAsync(db, companyId, doc, ct) is string issuedSales)
            throw new BusinessRuleException(issuedSales, OcrLineDiscountFixRuleCode, 409);

        var lines = doc.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).ToList();
        var issues = FindOcrLineDiscountIssues(doc, lines);
        if (issues.Count == 0)
            return new OcrLineDiscountFixResult(0, "ไม่มีบรรทัดที่ต้องแก้ (อาจแก้ไปแล้ว) — บันทึกซ้ำได้ยอดเดิม");

        var reqs = lines.Select(l => ResaveRequest(l)).ToList();
        var before = ResaveLineNets(reqs, doc.PricesIncludeVat, doc.BillDiscountPercent, doc.BillDiscountAmount, doc.DepositBaseDeducted);
        var plan = new Dictionary<int, (decimal UnitPrice, decimal DiscountAmount)>();
        foreach (var f in issues)
        {
            var l = lines[f.LineIndex];
            var repair = OcrLineDiscountRepair(l.Quantity, l.UnitPrice, l.VatRate, doc.PricesIncludeVat, l.Amount, l.VatAmount)
                ?? throw new BusinessRuleException(
                    $"บรรทัดที่ {f.LineOrder} ({f.Description}) หาราคา/ส่วนลดที่ได้ยอดเดิม {f.Stored:N2} ไม่ได้ — ไม่ได้แก้อะไร · "
                    + "ให้ผู้ทำบัญชีแก้ในฟอร์มเอกสาร (ตั้งส่วนลด % เป็น 0 แล้วใส่ส่วนลดบาทให้ยอดตรงกระดาษ)", OcrLineDiscountFixRuleCode, 409);
            plan[f.LineIndex] = repair;
            reqs[f.LineIndex] = ResaveRequest(l, repair.UnitPrice, 0m, repair.DiscountAmount);
        }

        // จำลอง "บันทึกซ้ำหลังแก้" ทั้งใบ: บรรทัดที่แก้ต้องได้ยอดที่เก็บไว้ · บรรทัดอื่นต้องได้ยอดที่เก็บไว้หรือผลเดิม (การแก้ไม่ขยับเงินบรรทัดอื่น —
        // ราคารวม VAT: ตัวกระทบเศษ VAT อาจย้ายเศษไปบรรทัดใหญ่สุด ⇒ ยอมเฉพาะเมื่อกลับมาตรงที่เก็บ)
        var after = ResaveLineNets(reqs, doc.PricesIncludeVat, doc.BillDiscountPercent, doc.BillDiscountAmount, doc.DepositBaseDeducted);
        for (var i = 0; i < lines.Count; i++)
        {
            var moved = plan.ContainsKey(i)
                ? after[i] != lines[i].Amount
                : after[i] != lines[i].Amount && after[i] != before[i];
            if (moved)
                throw new BusinessRuleException(
                    $"แก้แล้วยอดบรรทัดที่ {lines[i].LineOrder} จะเป็น {after[i]:N2} (ต้องเป็น {(plan.ContainsKey(i) ? lines[i].Amount : before[i]):N2}) — "
                    + "ไม่ได้แก้อะไร (เอกสารมีส่วนลดท้ายบิล/หักมัดจำหรือราคารวม VAT ที่จำลองแล้วไม่ลงตัว) · ให้ผู้ทำบัญชีแก้ในฟอร์มเอกสาร",
                    OcrLineDiscountFixRuleCode, 409);
        }

        var beforeRows = plan.Keys.OrderBy(i => i).Select(i => new
        {
            lines[i].LineOrder, lines[i].UnitPrice, lines[i].DiscountPercent, lines[i].DiscountAmount, lines[i].Amount,
        }).ToList();
        foreach (var (i, r) in plan)
        {
            lines[i].UnitPrice = r.UnitPrice;
            lines[i].DiscountPercent = 0m;
            lines[i].DiscountAmount = r.DiscountAmount;
        }
        var afterRows = plan.Keys.OrderBy(i => i).Select(i => new
        {
            lines[i].LineOrder, lines[i].UnitPrice, lines[i].DiscountPercent, lines[i].DiscountAmount, lines[i].Amount,
        }).ToList();
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = actorUserId,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = documentId.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { doc.DocumentNumber, Lines = beforeRows }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                RuleCode = OcrLineDiscountFixRuleCode,
                Reason = "ส่วนลด % ซ้อนบนราคาที่ลดแล้ว (บรรทัดจากสแกน) — ตั้ง % = 0 ให้บันทึกซ้ำได้ยอดเดิม · ยอด/VAT/ยอดรวม/JE ไม่เปลี่ยน",
                doc.DocumentNumber,
                Lines = afterRows,
            }),
            Timestamp = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return new OcrLineDiscountFixResult(plan.Count,
            $"แก้ส่วนลด {plan.Count} บรรทัดของ {doc.DocumentNumber} แล้ว — ยอด/VAT/ยอดรวมไม่เปลี่ยน · เปิดแก้แล้วบันทึกได้ยอดเดิม");
    }
}
