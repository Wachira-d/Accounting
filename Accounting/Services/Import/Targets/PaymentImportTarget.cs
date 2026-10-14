using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using static Accounting.Services.Import.Targets.ImportTargetValues;

namespace Accounting.Services.Import.Targets;

/// <summary>
/// **ปลายทาง "รับ/จ่ายชำระ"** ของศูนย์นำเข้า (รอบ 205 ทีม D · IMPORT_ETL_REVIEW §1 ข้อเท็จจริง "Payments ข้าม domain service" · §2.9)
///
/// <para>ลงผ่าน <see cref="IDocumentService.CreatePaymentAsync"/> เท่านั้น ⇒ JE รับ/จ่ายเงิน · ยอดค้างของเอกสาร · หัก ณ ที่จ่าย · ธนาคาร
/// เดินด่านเดียวกับหน้าจอ (เดิม <c>ImportExportService.ImportPaymentAsync</c> แก้ <c>PaidAmount</c> ตรง ⇒ ลูกหนี้ลดใน subledger แต่ GL ไม่ขยับ) ·
/// แถวละหนึ่งการชำระ (กลุ่มละแถว — แถวที่ล้มไม่ลากแถวอื่น)</para>
/// <para>หาเอกสาร: เลขที่เอกสารของเรา (ตรงตัว) ก่อน แล้วเลขเดิมที่นำเข้าไว้ใน <c>Reference</c> · ไม่พบ/กำกวม/ยังเป็นร่าง/งวดปิด/เกินยอดค้าง = บล็อก
/// พร้อมทางไปต่อ (CreatePaymentAsync ก็ปฏิเสธเหมือนกัน — ตรวจก่อนให้ผู้ใช้เห็นตั้งแต่หน้าตรวจ) · ซ้ำ (เอกสาร+วันที่+ยอดเดียวกัน) = คำเตือน + ตั้ง "ข้าม"</para>
/// <para>ย้อน = <see cref="IDocumentService.VoidPaymentAsync"/> (กลับรายการ JE + คืนยอดเอกสาร) · ยกเลิกไปแล้ว/ด่าน e-Tax ⇒ บล็อกพร้อมเหตุผล</para>
/// </summary>
public class PaymentImportTarget : IImportTarget
{
    public const string Key = "payments";
    public const string CommittedEntity = "Payment";

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documents;

    public PaymentImportTarget(AccountingDbContext db, IDocumentService documents)
    {
        _db = db;
        _documents = documents;
    }

    public string EntityType => Key;
    public string Label => "รับ/จ่ายชำระ";
    public ImportCommitStyle CommitStyle => ImportCommitStyle.OwnTransactionPerGroup;
    public bool SupportsUpdate => false;

    private static readonly IReadOnlyDictionary<string, PaymentMethod> MethodAliases = new Dictionary<string, PaymentMethod>(StringComparer.OrdinalIgnoreCase)
    {
        ["เงินสด"] = PaymentMethod.Cash, ["สด"] = PaymentMethod.Cash, ["cash"] = PaymentMethod.Cash,
        ["โอน"] = PaymentMethod.BankTransfer, ["โอนเงิน"] = PaymentMethod.BankTransfer, ["transfer"] = PaymentMethod.BankTransfer,
        ["บัตรเครดิต"] = PaymentMethod.CreditCard, ["บัตร"] = PaymentMethod.CreditCard, ["card"] = PaymentMethod.CreditCard,
        ["เช็ค"] = PaymentMethod.Cheque, ["เช็ก"] = PaymentMethod.Cheque, ["check"] = PaymentMethod.Cheque,
        ["พร้อมเพย์"] = PaymentMethod.PromptPay, ["หักบัญชี"] = PaymentMethod.DirectDebit, ["อีวอลเล็ท"] = PaymentMethod.EWallet,
        ["อื่น ๆ"] = PaymentMethod.Other, ["อื่นๆ"] = PaymentMethod.Other,
    };

    public IReadOnlyList<ImportFieldDto> Schema { get; } = new List<ImportFieldDto>
    {
        new("DocumentNumber", "เลขที่เอกสารที่ตัดชำระ", ImportFieldType.Reference, true,
            "เลขเอกสารในระบบ หรือเลขเดิมที่นำเข้าไว้เป็นอ้างอิง (เอกสารต้องอนุมัติแล้ว)", null, "INV2567-0001"),
        new("PaymentDate", "วันที่ชำระ", ImportFieldType.Date, true, null, null, "31/01/2567"),
        new("Amount", "จำนวนเงินที่รับ/จ่าย", ImportFieldType.Amount, true, "เงินที่เข้า/ออกจริง (หลังหัก ณ ที่จ่าย)", null, "9,700.00"),
        new("PaymentMethod", "วิธีชำระ", ImportFieldType.Enum, false, "เว้นว่าง = โอนเงิน",
            Enum.GetNames<PaymentMethod>().ToList(), "โอน"),
        new("WithholdingTaxAmount", "ภาษีหัก ณ ที่จ่าย", ImportFieldType.Amount, false, "เว้นว่าง = ระบบคิดตามสัดส่วนของเอกสาร"),
        new("FeeAmount", "ค่าธรรมเนียมที่ถูกหัก", ImportFieldType.Amount, false),
        new("Reference", "เลขอ้างอิงการโอน/เช็ค", ImportFieldType.Reference, false),
        new("BankAccount", "บัญชีธนาคาร (ข้อความ)", ImportFieldType.Text, false, null, null, null, 200),
        new("Notes", "หมายเหตุ", ImportFieldType.Text, false, null, null, null, 1000),
    };

    private sealed record DocRef(Guid Id, string DocumentNumber, string? Reference, DocumentStatus Status, decimal BalanceDue);

    /// <summary>หาเอกสารของแถว — เลขของเราตรงตัวก่อน แล้วเลขเดิม (Reference) · คืนรายการผู้สมัคร (0 = ไม่พบ · &gt;1 = กำกวม)</summary>
    private static IReadOnlyList<DocRef> FindDocs(IReadOnlyList<DocRef> pool, string? number)
    {
        if (number == null) return Array.Empty<DocRef>();
        var own = pool.Where(d => string.Equals(d.DocumentNumber, number, StringComparison.OrdinalIgnoreCase)).ToList();
        return own.Count > 0 ? own : pool.Where(d => string.Equals(d.Reference, number, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task<IReadOnlyList<DocRef>> LoadDocsAsync(Guid companyId, IEnumerable<ImportTargetRow> rows, CancellationToken ct)
    {
        var numbers = rows.Select(r => Text(r, "DocumentNumber")).Where(n => n != null).Distinct().ToList();
        return await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && (numbers.Contains(d.DocumentNumber) || (d.Reference != null && numbers.Contains(d.Reference))))
            .Select(d => new DocRef(d.Id, d.DocumentNumber, d.Reference, d.Status, d.BalanceDue))
            .ToListAsync(ct);
    }

    private static readonly DocumentStatus[] Payable =
        { DocumentStatus.Approved, DocumentStatus.PartiallyPaid, DocumentStatus.Sent, DocumentStatus.Overdue };

    public async Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
    {
        var pool = await LoadDocsAsync(ctx.CompanyId, rows, ct);
        var docIds = pool.Select(d => d.Id).ToList();
        var existing = await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == ctx.CompanyId && !p.IsDeleted && docIds.Contains(p.DocumentId))
            .Select(p => new { p.DocumentId, p.PaymentDate, p.Amount, p.Reference, p.PaymentNumber })
            .ToListAsync(ct);
        var closed = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == ctx.CompanyId && p.Status != FiscalPeriodStatus.Open)
            .Select(p => new { p.Name, p.StartDate, p.EndDate }).ToListAsync(ct);
        var today = DateTime.UtcNow.AddHours(7).Date;

        var result = new List<ImportRowValidation>(rows.Count);
        foreach (var r in rows)
        {
            var issues = new List<ImportIssueDto>();
            ImportRowAction? action = null;
            var number = Text(r, "DocumentNumber");
            var amount = Amount(r, "Amount");
            var date = Date(r, "PaymentDate");
            var docs = FindDocs(pool, number);
            if (docs.Count == 0)
                issues.Add(Error("DocumentNumber", "payment-doc-not-found",
                    $"ไม่พบเอกสาร \"{number}\" — นำเข้า/สร้างเอกสารและอนุมัติก่อน แล้วนำเข้าการชำระอีกครั้ง"));
            else if (docs.Count > 1)
                issues.Add(Error("DocumentNumber", "payment-doc-ambiguous",
                    $"เลข \"{number}\" ตรงกับเอกสาร {docs.Count} ใบ ({string.Join(", ", docs.Take(3).Select(d => d.DocumentNumber))}) — ใส่เลขเอกสารของระบบแทนเลขเดิม"));
            else
            {
                var d = docs[0];
                if (!Payable.Contains(d.Status))
                    issues.Add(Error("DocumentNumber", "payment-doc-not-approved", d.Status switch
                    {
                        DocumentStatus.Paid => $"เอกสาร {d.DocumentNumber} ชำระครบแล้ว — ตรวจว่าแถวนี้ซ้ำหรือไม่ แล้วข้ามแถว",
                        DocumentStatus.Voided or DocumentStatus.Rejected => $"เอกสาร {d.DocumentNumber} ถูกยกเลิก/ปฏิเสธแล้ว — รับ/จ่ายชำระไม่ได้ ข้ามแถวนี้",
                        _ => $"เอกสาร {d.DocumentNumber} ยังไม่อนุมัติ (ร่าง/รออนุมัติ) — อนุมัติเอกสารที่หน้าเอกสารก่อน แล้วนำเข้าการชำระอีกครั้ง",
                    }));
                else if (amount is { } a && a > d.BalanceDue + 0.005m)
                    issues.Add(Error("Amount", "payment-exceeds-balance",
                        $"ยอด {a:N2} เกินยอดค้างของ {d.DocumentNumber} ({d.BalanceDue:N2}) — แก้ยอด หรือแยกส่วนเกินเป็นเงินรับล่วงหน้า"));
                if (amount is { } a2 && date is { } dt
                    && existing.FirstOrDefault(p => p.DocumentId == d.Id && p.PaymentDate.Date == dt.Date && p.Amount == a2) is { } dup)
                {
                    issues.Add(Warning(null, "payment-possible-duplicate",
                        $"มีการชำระของ {d.DocumentNumber} วันเดียวกันยอดเดียวกันแล้ว ({dup.PaymentNumber}) — ระบบตั้งเป็น \"ข้าม\""));
                    action = ImportRowAction.Skip;
                }
            }
            if (amount is not > 0m)
                issues.Add(Error("Amount", "payment-amount-invalid", "จำนวนเงินต้องมากกว่า 0"));
            if (date is { } pd)
            {
                if (pd.Date > today)
                    issues.Add(Error("PaymentDate", "payment-future-date", "วันที่ชำระอยู่ในอนาคต — แก้วันที่"));
                else if (closed.FirstOrDefault(p => p.StartDate <= pd && p.EndDate >= pd) is { } cp)
                    issues.Add(Error("PaymentDate", "period-closed", $"วันที่ชำระอยู่ในงวด {cp.Name} ที่ปิดแล้ว — เปิดงวดก่อน หรือแก้วันที่", null, "PERIOD-LOCK"));
            }
            var methodText = Text(r, "PaymentMethod");
            if (methodText != null && !ImportValueNormalizer.Enum(methodText, MethodAliases).Ok)
                issues.Add(Warning("PaymentMethod", "payment-method-unknown", $"ไม่รู้จักวิธีชำระ \"{methodText}\" — ระบบใช้ \"โอนเงิน\"", "BankTransfer"));

            result.Add(new ImportRowValidation(r.RowId, issues, Action: action,
                DisplayKey: $"{number} · {amount:N2}", GroupKey: $"payment-{r.RowNo}"));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
    {
        var pool = await LoadDocsAsync(ctx.CompanyId, group, ct);
        var results = new List<ImportRowCommitResult>(group.Count);
        foreach (var r in group.OrderBy(x => x.RowNo))
        {
            if (r.Action == ImportRowAction.Skip) { results.Add(new ImportRowCommitResult(r.RowId, true, Skipped: true)); continue; }
            var docs = FindDocs(pool, Text(r, "DocumentNumber"));
            if (docs.Count != 1)
            {
                results.Add(new ImportRowCommitResult(r.RowId, false,
                    Error: docs.Count == 0 ? $"ไม่พบเอกสาร \"{Text(r, "DocumentNumber")}\"" : "เลขเอกสารกำกวม — ใส่เลขเอกสารของระบบ"));
                continue;
            }
            var amount = Amount(r, "Amount");
            var date = Date(r, "PaymentDate");
            if (amount is not > 0m || date == null)
            {
                results.Add(new ImportRowCommitResult(r.RowId, false, Error: "ต้องมีวันที่ชำระและจำนวนเงินมากกว่า 0"));
                continue;
            }
            var method = ImportValueNormalizer.Enum(Text(r, "PaymentMethod"), MethodAliases) is { Ok: true, Value: { } m } ? m : PaymentMethod.BankTransfer;
            var req = new CreatePaymentRequest(docs[0].Id, date.Value, amount.Value, method,
                Text(r, "Reference"), Text(r, "BankAccount"), Text(r, "Notes"),
                WithholdingTaxAmount: Amount(r, "WithholdingTaxAmount"),
                FeeAmount: Amount(r, "FeeAmount"));
            try
            {
                var p = await _documents.CreatePaymentAsync(ctx.CompanyId, req, CreatedBy(ctx));
                results.Add(new ImportRowCommitResult(r.RowId, true, CommittedEntity, p.Id));
            }
            catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
            {
                // CreatePaymentAsync เปิด/ถอยธุรกรรมเอง — แถวนี้ล้มดังที่ผล แถวอื่นยังลงต่อ
                results.Add(new ImportRowCommitResult(r.RowId, false, Error: ex.Message));
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EntityId).ToList();
        var live = (await _db.Payments.AsNoTracking()
            .Where(p => p.CompanyId == ctx.CompanyId && !p.IsDeleted && ids.Contains(p.Id))
            .Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        return rows.Select(r => live.Contains(r.EntityId)
            ? new ImportUndoCheck(r.RowId, true, ImportUndoKind.ReverseEntry)
            : new ImportUndoCheck(r.RowId, false, ImportUndoKind.None, "การชำระนี้ถูกยกเลิกไปแล้ว", null)).ToList();
    }

    public async Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
    {
        var results = new List<ImportRowUndoResult>(rows.Count);
        foreach (var r in rows)
        {
            try
            {
                // ทางกลับรายการเดิม (กลับ JE + คืนยอดเอกสาร) — ด่าน e-Tax/settlement ของ VoidPaymentAsync ปฏิเสธพร้อมเหตุผลเอง
                await _documents.VoidPaymentAsync(ctx.CompanyId, r.EntityId);
                results.Add(new ImportRowUndoResult(r.RowId, true));
            }
            catch (Exception ex) when (ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException)
            {
                results.Add(new ImportRowUndoResult(r.RowId, false, ex.Message));
            }
        }
        return results;
    }
}
