using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;

namespace Accounting.Helpers;

/// <summary>
/// "ดึงเอกสารเข้ารายงาน ภ.พ.30" — <b>ตัวตัดสินกลางตัวเดียว</b> (รอบ 225) ของทั้ง 3 ทางเข้า:
/// รายการ "ดึงเอกสาร" (<c>TaxService.GetPullableDocumentsAsync</c>) · ดึงทีละใบ (<c>PullDocumentIntoReportAsync</c> ·
/// <c>TryPullIntoDraftReportAsync</c>) · ดึงหลายใบ (<c>PullDocumentsIntoReportAsync</c>)
///
/// <para>ที่มา: เดิมกติกาอยู่สองชุด — รายการซ่อน PV ไม่ติ๊กใบกำกับ / VAT override ลงต้นทุน / VAT พัก 11640 แต่ตอนดึงจริงไม่ตรวจสองข้อหลัง
/// (เรียก API ตรงก็ดึงเข้ามาเคลมได้) · ส่วนตอนดึงตรวจ "ออกใบกำกับเต็มรูปแทนแล้ว" ซึ่งรายการไม่ตรวจ (โชว์แล้วกดไม่ได้) ·
/// ทั้งสองทางไม่ตรวจสถานะเอกสาร (ใบร่าง/ยกเลิกเรียก API ตรงก็ดึงได้) · CIL อยู่ในตาราง "ดึงได้ฝั่งขาย" ทั้งที่คอมเมนต์ระบุห้ามดึง ⇒
/// ใบรับรองแทนใบเสร็จที่มี VAT โผล่เป็น "ขาย" ในรายการ — ตอนนี้ทุกทางถามที่นี่ที่เดียว</para>
///
/// <para>Pure — ไม่แตะ DB: ผู้เรียกเตรียมข้อเท็จจริงที่ต้อง query (บรรทัดเดิมในงวดนี้ · งวดอื่นที่ใช้ใบนี้ · เลขใบแทน) ส่งเข้ามา</para>
/// </summary>
public static class TaxReportPullEligibility
{
    /// <summary>เพดานจำนวนใบต่อการดึงหนึ่งครั้ง (ธุรกรรมเดียว) — เกินนี้ให้แบ่งรอบ</summary>
    public const int MaxPerCall = 500;

    // ── RuleCode (log/audit/UI — กฎ M "Legal reference logging") ──
    public const string RuleNotFound = "TAX-PULL-NOT-FOUND";
    public const string RuleNotVatReport = "TAX-PULL-NOT-VAT";
    public const string RuleReportLocked = "TAX-PULL-REPORT-FILED";
    public const string RuleTypeNotPullable = "TAX-PULL-DOC-TYPE";
    public const string RuleCertificateInLieu = "RD-82/5(1)-CIL";
    public const string RuleNotIssued = "TAX-PULL-NOT-ISSUED";
    public const string RuleVoided = "TAX-PULL-VOIDED";
    public const string RuleNoVat = "TAX-PULL-NO-VAT";
    public const string RuleReplaced = "RD-86/4-REPLACED";
    public const string RulePvNoTaxInvoice = "RD-82/5(1)";
    public const string RuleVatExpensed = "TAX-PULL-VAT-EXPENSED";
    public const string RuleUndueVat = "RD-86/4-UNDUE-11640";
    public const string RuleBeforeDocMonth = "TAX-PULL-BEFORE-DOC-MONTH";
    public const string RuleClaimWindow = "RD-82/3";
    public const string RulePendingLineThisReport = "TAX-PULL-PENDING-LINE";
    public const string RuleAlreadyInThisReport = "TAX-PULL-DUP-THIS";
    public const string RuleClaimedInOtherReport = "TAX-PULL-DUP-OTHER";
    public const string RuleWarningLineThisReport = "TAX-PULL-WARNING-LINE";
    public const string RuleNoClaimableVat = "TAX-PULL-NO-CLAIMABLE";
    public const string RuleOutputLate = "RD-83-OUTPUT-LATE";
    public const string RuleFactsMissing = "TAX-PULL-FACTS-MISSING";

    /// <summary>ประเภทเอกสารที่ดึงด้วยมือได้ + ฝั่งภาษี (true = ภาษีซื้อ) — ย้ายมาจาก <c>TaxService.PullableVatTypes</c>.
    /// ต้องครอบคลุมทุกประเภทที่ loop หลักนับเป็น VAT (PV = ภาษีซื้อเมื่อติ๊ก "ใช้งานใบกำกับภาษี") ·
    /// CN/DN ไม่อยู่ในนี้ตั้งใจ — ฝั่ง/เครื่องหมายขึ้นกับใบต้นทาง ต้อง regenerate งวดที่ถูกต้องแทน ·
    /// CIL ไม่อยู่ในนี้ (§82/5(1) — GL fold VAT เข้า expense ไม่มีขา 11610 · เดิมอยู่ในตารางเป็น "ฝั่งขาย" โดยผิด)</summary>
    private static readonly IReadOnlyDictionary<DocumentType, bool> Sides = new Dictionary<DocumentType, bool>
    {
        [DocumentType.TaxInvoice] = false,
        [DocumentType.Invoice] = false,
        [DocumentType.Receipt] = false,
        [DocumentType.ReceiptVoucher] = false,
        [DocumentType.PurchaseInvoice] = true,
        [DocumentType.Expense] = true,
        [DocumentType.PaymentVoucher] = true,
    };

    /// <summary>ชุดประเภทสำหรับกรองใน SQL (<c>PullableTypes.Contains(d.DocumentType)</c>)</summary>
    public static readonly DocumentType[] PullableTypes = Sides.Keys.ToArray();

    /// <summary>ประเภทฝั่งภาษีซื้อในตาราง (สำหรับกรอง/นับใน SQL)</summary>
    public static readonly DocumentType[] InputTypes = Sides.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();

    /// <summary>ฝั่งภาษีของประเภทเอกสาร — null = ดึงเข้า ภ.พ.30 ด้วยมือไม่ได้</summary>
    private static bool? IsInputSide(DocumentType type) => Sides.TryGetValue(type, out var i) ? i : null;

    /// <summary>ผลตัดสินหนึ่งใบ</summary>
    public sealed record Verdict(bool Ok, bool IsInput, string? RuleCode, string? Reason)
    {
        public static Verdict Pass(bool isInput) => new(true, isInput, null, null);
        public static Verdict Fail(string rule, string reason, bool isInput = false) => new(false, isInput, rule, reason);
    }

    /// <summary>บรรทัดของใบนี้ที่มีอยู่แล้วใน "รายงานงวดนี้" — null = ไม่มี ·
    /// <paramref name="IsWarningLine"/> = บรรทัดเตือนที่ระบบตั้งใจไม่นับ (🚫 เกิน 6 เดือน · ⚠️ · [รอใบกำกับ]) ดู <see cref="IsWarningLineDescription"/></summary>
    public sealed record ExistingLine(bool IsExcluded, bool IsWarningLine = false);

    /// <summary>บรรทัดเตือน/ต้องห้ามที่ระบบสร้างและตั้งใจไม่นับ — ชุดเดียวกับที่ "สร้างใหม่" ไม่คืนติ๊กให้ (TaxService.RegenerateTaxReportAsync)</summary>
    public static bool IsWarningLineDescription(string? description)
    {
        var d = description ?? "";
        return d.StartsWith("🚫") || d.StartsWith("⚠️") || d.StartsWith("[รอใบกำกับ");
    }

    /// <summary>ข้อเท็จจริงจาก DB ที่ตัวตัดสินต้องใช้ (ผู้เรียกเตรียมผ่าน loader ชุดเดียวใน TaxService)</summary>
    /// <param name="LineInThisReport">บรรทัดของใบนี้ในงวดนี้ (รวมบรรทัดที่ติ๊กออก) · null = ไม่มี</param>
    /// <param name="ClaimedInOther">งวดอื่น (ปี, เดือน) ที่ใช้ใบนี้เป็นบรรทัดที่ "ใช้" อยู่</param>
    /// <param name="ReplacementNumber">เลขใบกำกับเต็มรูปที่ออกแทน (ประกอบข้อความ)</param>
    /// <param name="Output">ผลตัดสิน "เจ้าของภาษีขาย" (<see cref="OutputVatOwnership"/>) — <b>บังคับ</b>ฝั่งขาย (ไม่มี = ไม่ผ่าน · fail closed)</param>
    /// <param name="ClaimableInputVat">ภาษีซื้อที่เคลมได้ (สกุลเอกสาร · หลังหัก §82/5 ตัวเดียวกับ loop หลัก) — <b>บังคับ</b>ฝั่งซื้อ</param>
    public sealed record PullFacts(
        ExistingLine? LineInThisReport = null,
        (int Year, int Month)? ClaimedInOther = null,
        string? ReplacementNumber = null,
        OutputVatOwnership.Decision? Output = null,
        decimal? ClaimableInputVat = null);

    /// <summary>ด่านระดับรายงาน (ก่อนดูเอกสารใดเลย) — ไม่ผ่าน = ทั้งคำขอล้ม ไม่ใช่ข้ามรายใบ</summary>
    public static Verdict EvaluateReport(TaxType taxType, TaxReportStatus status)
    {
        if (taxType != TaxType.VAT)
            return Verdict.Fail(RuleNotVatReport, "ดึงเอกสารได้เฉพาะรายงาน ภพ.30");
        if (TaxFilingLockPolicy.DeclaredOrFiled(status))
            return Verdict.Fail(RuleReportLocked, "ไม่สามารถแก้ไขได้ — รายงานนี้ถูกยื่น/บันทึกว่ายื่นแล้ว");
        return Verdict.Pass(false);
    }

    /// <summary>วันที่ที่ใช้หา "เดือนภาษี" ของใบเมื่อดึง — ฝั่งซื้อ = <c>TaxService.ClaimBasisDate</c> (วันที่ใบกำกับผู้ขาย) ·
    /// ฝั่งขาย = วันที่ VAT ถึงกำหนดตาม <see cref="OutputVatOwnership"/> (ใบแจ้งหนี้บริการ = วันรับเงิน · มัดจำ = วันรับรู้)</summary>
    private static DateTime TaxBasisDate(Document doc, bool isInput, OutputVatOwnership.Decision? output)
        => !isInput && output?.VatDueDate is { } due ? due : TaxService.ClaimBasisDate(doc);

    /// <summary>ตัดสินว่าใบ <paramref name="doc"/> ดึงเข้ารายงานงวด <paramref name="reportYear"/>/<paramref name="reportMonth"/> ได้ไหม</summary>
    public static Verdict Evaluate(Document doc, int reportYear, int reportMonth, PullFacts facts)
    {
        if (doc.DocumentType == DocumentType.CertificateInLieu)
            return Verdict.Fail(RuleCertificateInLieu,
                $"{doc.DocumentNumber} เป็นใบรับรองแทนใบเสร็จ — ไม่ใช่ใบกำกับภาษี เคลมภาษีซื้อไม่ได้ (§82/5(1)) จึงดึงเข้า ภ.พ.30 ไม่ได้");
        if (IsInputSide(doc.DocumentType) is not { } isInput)
            return Verdict.Fail(RuleTypeNotPullable,
                $"เอกสารประเภท {doc.DocumentType} ไม่สามารถดึงเข้ารายงาน ภพ.30 ได้");
        if (doc.Status == DocumentStatus.Voided)
            return Verdict.Fail(RuleVoided, $"เอกสาร {doc.DocumentNumber} ถูกยกเลิกแล้ว — ดึงเข้ารายงานไม่ได้", isInput);
        if (!DocumentStatusRules.IsIssued(doc.Status))
            return Verdict.Fail(RuleNotIssued,
                $"เอกสาร {doc.DocumentNumber} ยังไม่ได้อนุมัติ (สถานะ {doc.Status}) — อนุมัติก่อนจึงจะดึงเข้ารายงานได้", isInput);
        if (doc.VatAmount == 0)
            return Verdict.Fail(RuleNoVat, "เอกสารนี้ไม่มี VAT", isInput);
        // รอบ 269 H1 — ใบยอดยกมาไม่เข้า ภ.พ.30 ทั้งสองฝั่ง (ตัวตัดสินเดียว OutputVatOwnership.IsOutsidePp30)
        if (OutputVatOwnership.IsOutsidePp30(doc))
            return Verdict.Fail(OutputVatOwnership.RuleOpeningBalance,
                $"{doc.DocumentNumber} เป็นใบยอดยกมา (ลูกหนี้/เจ้าหนี้ยกมา) — ไม่ใช่การขาย/การซื้อของงวดใด จึงดึงเข้า ภ.พ.30 ไม่ได้", isInput);
        if (doc.ReplacedByDocumentId.HasValue)
            return Verdict.Fail(RuleReplaced,
                $"เอกสาร {doc.DocumentNumber} ออกใบกำกับภาษีเต็มรูปแทนไปแล้ว"
                + (facts.ReplacementNumber != null ? $" ({facts.ReplacementNumber})" : "")
                + " — ให้ดึง**ใบแทน**เข้ารายงานแทน มิฉะนั้นภาษีขายจะถูกนับสองครั้ง", isInput);
        if (doc.DocumentType == DocumentType.PaymentVoucher
            && (!doc.HasTaxInvoiceReference || doc.RelatedDocumentId != null))
            return Verdict.Fail(RulePvNoTaxInvoice,
                $"ใบสำคัญจ่าย {doc.DocumentNumber} ยังไม่ได้ติ๊ก \"ใช้งานใบกำกับภาษี\" (ขอเครดิตภาษีซื้อ) — "
                + "เปิดเอกสารแล้วติ๊ก + กรอกเลขที่/วันที่ใบกำกับของผู้ขายก่อน จึงจะดึงเข้า ภ.พ.30 ได้ (§86/4)", isInput);
        if (isInput && !string.IsNullOrWhiteSpace(doc.InputVatAccountCodeOverride)
            && !doc.InputVatAccountCodeOverride.StartsWith("116"))
            return Verdict.Fail(RuleVatExpensed,
                $"เอกสาร {doc.DocumentNumber} ตั้งผังภาษีซื้อเป็น {doc.InputVatAccountCodeOverride} (ลง VAT เป็นต้นทุน ไม่เคลม) — "
                + "ดึงเข้ามาเคลมซ้ำไม่ได้ ถ้าจะเคลมให้แก้ผังภาษีซื้อในเอกสารเป็น 116xx ก่อน", isInput);
        if (isInput && doc.InputVatPostedAsUndue && doc.InputVatBecameClaimableAt == null)
            return Verdict.Fail(RuleUndueVat,
                $"เอกสาร {doc.DocumentNumber} ภาษีซื้อยังพักที่ 11640 (ใบกำกับ §86/4 ยังไม่ครบ / ภ.พ.36 ยังไม่รับรู้) — "
                + "เติมใบกำกับให้ครบหรือกด \"รับรู้ภาษีซื้อ\" ก่อน ระบบจะดึงเข้างวดที่ถึงกำหนดให้เอง", isInput);
        if (isInput)
        {
            // §82/5 — ตัวคำนวณเดียวกับ loop หลัก (ธงรายบรรทัด + ผังต้องห้าม) · ไม่มีภาษีซื้อที่เคลมได้ = ดึงแล้วได้บรรทัด 0 (หรือติดลบ)
            if (facts.ClaimableInputVat is not { } claimable)
                return Verdict.Fail(RuleFactsMissing, $"ตรวจภาษีซื้อที่เคลมได้ของ {doc.DocumentNumber} ไม่ได้ — ลองใหม่อีกครั้ง", isInput);
            if (claimable <= 0m)
                return Verdict.Fail(RuleNoClaimableVat,
                    $"เอกสาร {doc.DocumentNumber} ไม่มีภาษีซื้อที่เคลมได้ (ทุกบรรทัดติ๊ก \"ไม่เคลม\" หรือลงผังภาษีซื้อต้องห้าม §82/5) — ดึงเข้ามาไม่มีผล", isInput);
        }
        else
        {
            // ภาษีขาย: ใบนี้ต้องเป็น "เจ้าของภาษีขาย" ตามกติกาเดียวกับ loop หลัก (กันนับซ้ำ/นับก่อนถึงกำหนด)
            if (facts.Output is not { } own)
                return Verdict.Fail(RuleFactsMissing, $"ตรวจความเป็นเจ้าของภาษีขายของ {doc.DocumentNumber} ไม่ได้ — ลองใหม่อีกครั้ง", isInput);
            if (!own.Owns)
                return Verdict.Fail(own.RuleCode ?? OutputVatOwnership.RuleNotOutputDocument,
                    $"เอกสาร {doc.DocumentNumber}: {own.Reason}", isInput);
        }

        var basis = TaxBasisDate(doc, isInput, facts.Output);
        var docMonth = new DateTime(basis.Year, basis.Month, 1);
        var reportStart = new DateTime(reportYear, reportMonth, 1);
        // คำตัดสินผู้ทำบัญชี (รอบ 225): ภาษีขายของเดือนก่อน ดึงเข้างวดหลังไม่ได้ — ต้องยื่น ภ.พ.30 เพิ่มเติมของเดือนนั้น
        // (§83 ยื่นภายในวันที่ 15 ของเดือนถัดไป · ยื่นเพิ่มเติมเสียเงินเพิ่ม §89/1) · ฝั่งซื้อยังย้อนได้ตาม §82/3
        if (!isInput && docMonth < reportStart)
            return Verdict.Fail(RuleOutputLate,
                $"เอกสาร {doc.DocumentNumber} ภาษีขายถึงกำหนดเดือน {docMonth:MM/yyyy} — ต้องรายงานในแบบ ภ.พ.30 ของเดือนนั้น "
                + $"ดึงเข้างวด {reportMonth:D2}/{reportYear} ไม่ได้ ถ้าเดือนนั้นยื่นไปแล้วให้ยื่น ภ.พ.30 เพิ่มเติม (§83 · เงินเพิ่ม §89/1)", isInput);
        var window = TaxService.EvaluateClaimPeriod(basis, isInput, reportYear, reportMonth);
        if (!window.Ok)
            return Verdict.Fail(reportStart < docMonth ? RuleBeforeDocMonth : RuleClaimWindow, window.Reason!, isInput);

        if (facts.LineInThisReport is { } line)
        {
            if (line.IsWarningLine)
                return Verdict.Fail(RuleWarningLineThisReport,
                    $"เอกสาร {doc.DocumentNumber} มีบรรทัดเตือนในงวดนี้อยู่แล้ว (เช่น เกิน 6 เดือน / ต้องห้าม / รอใบกำกับ) ที่ระบบตั้งใจไม่นับ — "
                    + "ดึงซ้ำไม่ได้ ให้แก้ที่ต้นเหตุในเอกสาร แล้วกด \"สร้างใหม่\"", isInput);
            return line.IsExcluded
                ? Verdict.Fail(RulePendingLineThisReport,
                    $"เอกสาร {doc.DocumentNumber} มีบรรทัดรออยู่ในรายงานงวดนี้แล้ว "
                    + "(บรรทัดที่ยังไม่ได้ติ๊ก \"ใช้\") — ให้ติ๊กช่อง \"ใช้\" ที่บรรทัดนั้นแล้วกด \"บันทึก\" "
                    + "แทนการดึงซ้ำ มิฉะนั้นจะได้บรรทัดซ้ำในงวดเดียวกัน", isInput)
                : Verdict.Fail(RuleAlreadyInThisReport, $"เอกสาร {doc.DocumentNumber} อยู่ในรายงานงวดนี้แล้ว", isInput);
        }

        if (facts.ClaimedInOther is { } o)
            return Verdict.Fail(RuleClaimedInOtherReport,
                $"เอกสารนี้ถูกใช้ในรายงานภาษีงวด {o.Month:D2}/{o.Year} แล้ว — กรุณานำออกจากงวดนั้นก่อน", isInput);

        return Verdict.Pass(isInput);
    }

    /// <summary>เดือนภาษีของใบ (วันที่ 1) — ฐานนับเดียวกับด่านเวลา</summary>
    public static DateTime TaxMonthOf(Document doc, bool isInput, OutputVatOwnership.Decision? output)
    {
        var b = TaxBasisDate(doc, isInput, output);
        return new DateTime(b.Year, b.Month, 1);
    }

    /// <summary>ใบนี้ "ย้อนเดือน" กี่เดือนเมื่อดึงเข้างวดนี้ (0 = เดือนเดียวกับงวด) — ใช้ติดป้าย "ย้อนเดือน" บนรายการ</summary>
    public static int MonthsLate(DateTime taxMonth, int reportYear, int reportMonth)
        => Math.Max(0, (reportYear - taxMonth.Year) * 12 + (reportMonth - taxMonth.Month));

    /// <summary>วันแรกของกรอบ "ย้อนเดือน" ที่ภาษีซื้อยังเคลมได้ในงวดนี้ (§82/3: เดือนใบ + 6)</summary>
    public static DateTime LateWindowStart(int reportYear, int reportMonth)
        => new DateTime(reportYear, reportMonth, 1).AddMonths(-6);

    /// <summary>หนึ่งรายการในผลดึงหลายใบ</summary>
    public sealed record ItemResult(Guid DocumentId, string? DocumentNumber, bool Added, string? RuleCode, string? Reason);

    /// <summary>ไล่ตัดสินทุกใบในคำขอ (ลำดับตามคำขอ · id ซ้ำนับครั้งเดียว) — ใบที่ไม่พบ/ต่างบริษัท ⇒ ข้ามด้วย
    /// <see cref="RuleNotFound"/> (ไม่บอกว่ามีอยู่ในบริษัทอื่น)</summary>
    public static (List<ItemResult> Items, List<(Document Doc, bool IsInput)> Accepted) EvaluateBatch(
        IReadOnlyList<Guid> requestedIds,
        IReadOnlyDictionary<Guid, Document> docsInTenant,
        int reportYear, int reportMonth,
        IReadOnlyDictionary<Guid, PullFacts> facts)
    {
        var items = new List<ItemResult>();
        var accepted = new List<(Document, bool)>();
        var seen = new HashSet<Guid>();
        foreach (var id in requestedIds)
        {
            if (!seen.Add(id)) continue;
            if (!docsInTenant.TryGetValue(id, out var doc))
            {
                items.Add(new ItemResult(id, null, false, RuleNotFound, "ไม่พบเอกสาร"));
                continue;
            }
            var v = Evaluate(doc, reportYear, reportMonth, facts.TryGetValue(id, out var f) ? f : new PullFacts());
            if (v.Ok)
            {
                items.Add(new ItemResult(id, doc.DocumentNumber, true, null, null));
                accepted.Add((doc, v.IsInput));
            }
            else items.Add(new ItemResult(id, doc.DocumentNumber, false, v.RuleCode, v.Reason));
        }
        return (items, accepted);
    }

    /// <summary>ตรวจขนาดคำขอ — คืนข้อความผิด (null = ผ่าน)</summary>
    public static string? ValidateRequestSize(IReadOnlyCollection<Guid>? ids)
    {
        if (ids == null || ids.Count == 0) return "ยังไม่ได้เลือกเอกสาร";
        var distinct = ids.Distinct().Count();
        if (distinct > MaxPerCall)
            return $"เลือกไว้ {distinct:N0} ใบ — ดึงได้ครั้งละไม่เกิน {MaxPerCall:N0} ใบ กรุณาแบ่งเป็นหลายรอบ";
        return null;
    }
}
