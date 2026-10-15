using Accounting.Models.DTOs.Accounting;
using Accounting.Models.DTOs.Aging;
using Accounting.Models.DTOs.Audit;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ชื่อที่ต้องใช้แปล id ในไฟล์ส่งออกรายงานบัญชี (สาขา/โครงการ/มิติ/เอกสารต้นทาง) — service โหลดด้วย CompanyId แล้วส่งมา</summary>
public sealed record LedgerExportNames(
    IReadOnlyDictionary<Guid, string> Branches,
    IReadOnlyDictionary<Guid, string> Projects,
    IReadOnlyDictionary<Guid, string> Dimensions,
    IReadOnlyDictionary<Guid, LedgerSourceDocument> Documents)
{
    public static readonly LedgerExportNames Empty = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>(), new Dictionary<Guid, LedgerSourceDocument>());

    internal string? Branch(Guid? id) => id.HasValue && Branches.TryGetValue(id.Value, out var n) ? n : null;
    internal string? Project(Guid? id) => id.HasValue && Projects.TryGetValue(id.Value, out var n) ? n : null;
    internal string? Dimension(Guid? id) => id.HasValue && Dimensions.TryGetValue(id.Value, out var n) ? n : null;
    internal LedgerSourceDocument? Document(Guid? id) => id.HasValue && Documents.TryGetValue(id.Value, out var d) ? d : null;
}

/// <summary>เอกสารต้นทางของใบสำคัญ — เลขที่ · ชนิด · คู่ค้า (ชื่อ + เลขผู้เสียภาษี)</summary>
public sealed record LedgerSourceDocument(string Number, string Type, string? ContactName, string? ContactTaxId);

/// <summary>หนึ่งแถวของงบทดลองแบบช่วงเวลา (ยกมา · เคลื่อนไหว · ยกไป)</summary>
public sealed record TrialBalanceWorksheetRow(
    string AccountCode, string AccountName, AccountType AccountType,
    decimal OpeningDebit, decimal OpeningCredit,
    decimal MovementDebit, decimal MovementCredit,
    decimal ClosingDebit, decimal ClosingCredit,
    decimal PageTotalDebit, decimal PageTotalCredit,
    decimal ContinuityGap);

/// <summary>งบทดลองแบบช่วงเวลา + ยอดรวมที่ต้องสมดุล</summary>
public sealed record TrialBalanceWorksheet(
    DateTime FromDate, DateTime ToDate, IReadOnlyList<TrialBalanceWorksheetRow> Rows)
{
    public decimal OpeningDebit => Rows.Sum(r => r.OpeningDebit);
    public decimal OpeningCredit => Rows.Sum(r => r.OpeningCredit);
    public decimal MovementDebit => Rows.Sum(r => r.MovementDebit);
    public decimal MovementCredit => Rows.Sum(r => r.MovementCredit);
    public decimal ClosingDebit => Rows.Sum(r => r.ClosingDebit);
    public decimal ClosingCredit => Rows.Sum(r => r.ClosingCredit);
    public decimal PageTotalDebit => Rows.Sum(r => r.PageTotalDebit);
    public decimal PageTotalCredit => Rows.Sum(r => r.PageTotalCredit);
}

/// <summary>
/// **คอลัมน์ของไฟล์ส่งออกรายงานบัญชี (สมุดรายวัน · บัญชีแยกประเภท · งบทดลอง · งบการเงิน)** — pure ·
/// รับ DTO ที่ service รายงานคืน (ตัวเดียวกับที่หน้าแสดง) แล้วจัดเป็น <see cref="TabularSheet"/> ·
/// ห้ามคำนวณยอดใหม่ที่นี่ ยกเว้นงบทดลองแบบช่วงเวลาที่ "ประกอบ" จากสองเจ้าของ (ดู <see cref="BuildTrialBalanceWorksheet"/>)
/// </summary>
public static class AccountingReportSheets
{
    /// <summary>ป้ายสมุดรายวัน — ตรงกับตัวย่อเลขที่ใบสำคัญ (JV/SV/UV/RV/PV) ที่หน้าแสดง</summary>
    public static string JournalTypeLabel(JournalType t) => t switch
    {
        JournalType.Sales => "SV สมุดรายวันขาย",
        JournalType.Purchase => "UV สมุดรายวันซื้อ",
        JournalType.CashReceipts => "RV สมุดรายวันรับ",
        JournalType.CashPayments => "PV สมุดรายวันจ่าย",
        _ => "JV สมุดรายวันทั่วไป",
    };

    private static string StatusLabel(JournalEntryStatus s) => s switch
    {
        JournalEntryStatus.Draft => "ร่าง",
        JournalEntryStatus.Posted => "ผ่านรายการแล้ว",
        JournalEntryStatus.Voided => "ยกเลิก",
        JournalEntryStatus.Reversed => "กลับรายการแล้ว",
        _ => s.ToString(),
    };

    private sealed class WsAcc
    {
        public string Name = "";
        public AccountType Type;
        public decimal OpenNet, MovDr, MovCr, CloseDr, CloseCr;
    }

    /// <summary>
    /// งบทดลองแบบช่วงเวลา — <b>ยกมา + เคลื่อนไหว</b> จากบัญชีแยกประเภท (<c>GetGeneralLedgerAsync</c> ช่วงเดียวกัน ·
    /// ตัวกรองเดียวกัน) และ <b>ยกไป</b> จากงบทดลองที่หน้าแสดง (<c>GetTrialBalanceAsync</c> ณ วันสิ้นช่วง) ·
    /// <see cref="TrialBalanceWorksheetRow.ContinuityGap"/> = (ยกมา + เคลื่อนไหว) − ยกไป ด้านเดบิต —
    /// ≠ 0 แปลว่าสองรายงานไม่ต่อกัน (เช่น ยอดยกมาที่นำเข้าเริ่มกลางช่วง หรือกรองสาขาแล้วงบทดลองบวกยอดนำเข้าทั้งบริษัท)
    /// ⇒ แสดงในไฟล์ให้เห็น ไม่กลบด้วยการคำนวณยกมาย้อนจากยกไป ·
    /// ยกเว้นแถวทุน "กำไร(ขาดทุน)ก่อนรอบบัญชีนี้" ของงบทดลองแบบกรอง (<see cref="LedgerFilterRule.IsPriorPnlRow"/>) — ยกมาเท่ายกไปตามนิยาม (รอบ 3 P2-1)
    /// </summary>
    public static TrialBalanceWorksheet BuildTrialBalanceWorksheet(GeneralLedgerResponse gl, TrialBalanceResponse closing, DateTime fromDate)
    {
        var byCode = new Dictionary<string, WsAcc>(StringComparer.Ordinal);
        foreach (var a in gl.Accounts)
        {
            byCode[a.AccountCode] = new WsAcc
            {
                Name = a.AccountName, Type = a.AccountType,
                OpenNet = a.AccountType is AccountType.Asset or AccountType.Expense ? a.OpeningBalance : -a.OpeningBalance,
                MovDr = a.TotalDebit, MovCr = a.TotalCredit,
            };
        }
        foreach (var t in closing.Items)
        {
            if (!byCode.TryGetValue(t.AccountCode, out var acc))
            {
                acc = new WsAcc { Name = t.AccountName, Type = t.AccountType };
                byCode[t.AccountCode] = acc;
            }
            acc.CloseDr += t.DebitBalance;
            acc.CloseCr += t.CreditBalance;
            // แถวกำไรขาดทุนรอบก่อนของงบทดลองแบบกรอง: ยอดทั้งหมดคือยอดยกมา (ไม่มีการเคลื่อนไหวในช่วง · ไม่มีแถวคู่ในบัญชีแยกประเภท) — รอบ 3 P2-1
            if (LedgerFilterRule.IsPriorPnlRow(t.AccountCode, t.AccountName))
                acc.OpenNet += t.DebitBalance - t.CreditBalance;
        }

        var list = new List<TrialBalanceWorksheetRow>();
        foreach (var code in byCode.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var r = byCode[code];
            var od = r.OpenNet >= 0 ? r.OpenNet : 0m;
            var oc = r.OpenNet < 0 ? -r.OpenNet : 0m;
            var closeNet = r.CloseDr - r.CloseCr;
            var cd = closeNet >= 0 ? closeNet : 0m;
            var cc = closeNet < 0 ? -closeNet : 0m;
            var gap = r.OpenNet + r.MovDr - r.MovCr - closeNet;
            if (r.OpenNet == 0 && r.MovDr == 0 && r.MovCr == 0 && r.CloseDr == 0 && r.CloseCr == 0)
                continue;   // บัญชีที่ศูนย์ทุกช่อง ไม่ต้องมีแถว
            list.Add(new TrialBalanceWorksheetRow(code, r.Name, r.Type, od, oc, r.MovDr, r.MovCr, cd, cc,
                r.CloseDr, r.CloseCr, gap));
        }
        return new TrialBalanceWorksheet(fromDate, closing.AsOfDate, list);
    }

    // ═══ งบทดลอง ═══

    private sealed record TbLine(string Code, string Name, string Type,
        decimal OpenDr, decimal OpenCr, decimal MovDr, decimal MovCr, decimal CloseDr, decimal CloseCr,
        decimal PageDr, decimal PageCr, object? Gap);

    public static TabularSheet TrialBalance(TrialBalanceWorksheet ws, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<TbLine>("รหัสบัญชี", r => r.Code),
            new TabularColumn<TbLine>("ชื่อบัญชี", r => r.Name),
            new TabularColumn<TbLine>("ประเภท", r => r.Type),
            new TabularColumn<TbLine>("ยอดยกมา เดบิต", r => r.OpenDr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ยอดยกมา เครดิต", r => r.OpenCr, TabularCellKind.Money),
            new TabularColumn<TbLine>("เคลื่อนไหว เดบิต", r => r.MovDr, TabularCellKind.Money),
            new TabularColumn<TbLine>("เคลื่อนไหว เครดิต", r => r.MovCr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ยอดยกไป เดบิต", r => r.CloseDr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ยอดยกไป เครดิต", r => r.CloseCr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ยอดเดบิตสะสม (ตามหน้างบทดลอง)", r => r.PageDr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ยอดเครดิตสะสม (ตามหน้างบทดลอง)", r => r.PageCr, TabularCellKind.Money),
            new TabularColumn<TbLine>("ผลต่างความต่อเนื่อง (ยกมา+เคลื่อนไหว−ยกไป)", r => r.Gap, TabularCellKind.Money),
        };
        var rows = ws.Rows.Select(r => new TbLine(r.AccountCode, r.AccountName, PickedAccountGuard.TypeLabel(r.AccountType),
            r.OpeningDebit, r.OpeningCredit, r.MovementDebit, r.MovementCredit, r.ClosingDebit, r.ClosingCredit,
            r.PageTotalDebit, r.PageTotalCredit, r.ContinuityGap == 0m ? null : r.ContinuityGap));
        var footer = new[]
        {
            new TbLine("", "รวมทั้งสิ้น", "", ws.OpeningDebit, ws.OpeningCredit, ws.MovementDebit, ws.MovementCredit,
                ws.ClosingDebit, ws.ClosingCredit, ws.PageTotalDebit, ws.PageTotalCredit, null),
        };
        return TabularExport.Sheet("งบทดลอง", cols, rows, footer, title);
    }

    // ═══ บัญชีแยกประเภท ═══

    private sealed record GlLine(string Code, string Name, string Type, DateTime? Date, string? EntryNo, string? Book,
        string? Description, string? Reference, string? DocNo, string? DocType, string? Contact, string? ContactTaxId,
        decimal? Debit, decimal? Credit, decimal Balance, string? Branch, string? Project, string? Dimension, string RowKind);

    public static TabularSheet GeneralLedger(GeneralLedgerResponse gl, LedgerExportNames names, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<GlLine>("รหัสบัญชี", r => r.Code),
            new TabularColumn<GlLine>("ชื่อบัญชี", r => r.Name),
            new TabularColumn<GlLine>("ประเภทบัญชี", r => r.Type),
            new TabularColumn<GlLine>("ชนิดแถว", r => r.RowKind),
            new TabularColumn<GlLine>("วันที่", r => r.Date, TabularCellKind.Date),
            new TabularColumn<GlLine>("เลขที่ใบสำคัญ", r => r.EntryNo),
            new TabularColumn<GlLine>("สมุดรายวัน", r => r.Book),
            new TabularColumn<GlLine>("คำอธิบาย", r => r.Description),
            new TabularColumn<GlLine>("อ้างอิง", r => r.Reference),
            new TabularColumn<GlLine>("เอกสารต้นทาง", r => r.DocNo),
            new TabularColumn<GlLine>("ชนิดเอกสาร", r => r.DocType),
            new TabularColumn<GlLine>("คู่ค้า", r => r.Contact),
            new TabularColumn<GlLine>("เลขผู้เสียภาษีคู่ค้า", r => r.ContactTaxId),
            new TabularColumn<GlLine>("เดบิต", r => r.Debit, TabularCellKind.Money),
            new TabularColumn<GlLine>("เครดิต", r => r.Credit, TabularCellKind.Money),
            new TabularColumn<GlLine>("ยอดคงเหลือ", r => r.Balance, TabularCellKind.Money),
            new TabularColumn<GlLine>("สาขา", r => r.Branch),
            new TabularColumn<GlLine>("โครงการ", r => r.Project),
            new TabularColumn<GlLine>("ศูนย์ต้นทุน/มิติ", r => r.Dimension),
        };
        var rows = new List<GlLine>();
        foreach (var a in gl.Accounts)
        {
            var type = PickedAccountGuard.TypeLabel(a.AccountType);
            rows.Add(new GlLine(a.AccountCode, a.AccountName, type, gl.FromDate, null, null, "ยอดยกมา", null, null, null, null, null,
                null, null, a.OpeningBalance, null, null, null, "ยอดยกมา"));
            foreach (var t in a.Transactions)
            {
                var doc = names.Document(t.SourceDocumentId);
                rows.Add(new GlLine(a.AccountCode, a.AccountName, type, t.EntryDate, t.EntryNumber, JournalTypeLabel(t.JournalType),
                    t.Description, t.EntryReference, doc?.Number, doc?.Type, doc?.ContactName, doc?.ContactTaxId,
                    t.DebitAmount, t.CreditAmount, t.RunningBalance,
                    names.Branch(t.BranchId), names.Project(t.ProjectId), names.Dimension(t.DimensionId), "รายการ"));
            }
            rows.Add(new GlLine(a.AccountCode, a.AccountName, type, gl.ToDate, null, null, "รวมเคลื่อนไหว / ยอดยกไป", null, null, null, null, null,
                a.TotalDebit, a.TotalCredit, a.ClosingBalance, null, null, null, "ยอดยกไป"));
        }
        return TabularExport.Sheet("บัญชีแยกประเภท", cols, rows, title: title);
    }

    // ═══ สมุดรายวัน (หนึ่งแถวต่อบรรทัดบัญชี) ═══

    private sealed record JeLine(string EntryNo, DateTime? Date, string Book, string Status, string? Header, string? Reference,
        string? DocNo, string? DocType, string? Contact, int? LineNo, string? AccountCode, string? AccountName, string? LineDescription,
        decimal? Debit, decimal? Credit, string? Branch, string? Project, string? Dimension, string? Tags, string? Note);

    public static TabularSheet Journals(IEnumerable<JournalEntryResponse> entries, LedgerExportNames names, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<JeLine>("เลขที่ใบสำคัญ", r => r.EntryNo),
            new TabularColumn<JeLine>("วันที่", r => r.Date, TabularCellKind.Date),
            new TabularColumn<JeLine>("สมุดรายวัน", r => r.Book),
            new TabularColumn<JeLine>("สถานะ", r => r.Status),
            new TabularColumn<JeLine>("คำอธิบายใบสำคัญ", r => r.Header),
            new TabularColumn<JeLine>("อ้างอิง", r => r.Reference),
            new TabularColumn<JeLine>("เอกสารต้นทาง", r => r.DocNo),
            new TabularColumn<JeLine>("ชนิดเอกสาร", r => r.DocType),
            new TabularColumn<JeLine>("คู่ค้า", r => r.Contact),
            new TabularColumn<JeLine>("บรรทัด", r => r.LineNo, TabularCellKind.Integer),
            new TabularColumn<JeLine>("รหัสบัญชี", r => r.AccountCode),
            new TabularColumn<JeLine>("ชื่อบัญชี", r => r.AccountName),
            new TabularColumn<JeLine>("คำอธิบายบรรทัด", r => r.LineDescription),
            new TabularColumn<JeLine>("เดบิต", r => r.Debit, TabularCellKind.Money),
            new TabularColumn<JeLine>("เครดิต", r => r.Credit, TabularCellKind.Money),
            new TabularColumn<JeLine>("สาขา", r => r.Branch),
            new TabularColumn<JeLine>("โครงการ", r => r.Project),
            new TabularColumn<JeLine>("ศูนย์ต้นทุน/มิติ", r => r.Dimension),
            new TabularColumn<JeLine>("แท็ก", r => r.Tags),
            new TabularColumn<JeLine>("หมายเหตุ", r => r.Note),
        };
        var rows = new List<JeLine>();
        decimal totalDr = 0m, totalCr = 0m;
        foreach (var e in entries)
        {
            var book = JournalTypeLabel(e.JournalType);
            var status = StatusLabel(e.Status);
            var doc = names.Document(e.SourceDocumentId);
            var docNo = e.SourceDocumentNumber ?? doc?.Number;
            if (e.IsRedacted)
            {
                // ใบที่ผู้ใช้ไม่มีสิทธิ์เห็น (เงินเดือน ฯลฯ) — หน้าแสดงเป็นแถวปิดบัง ⇒ ไฟล์ก็ปิดบังเท่ากัน ไม่ใช่หายเงียบ
                rows.Add(new JeLine(e.EntryNumber, e.EntryDate, book, status, "ปิดบัง — " + (e.RedactedReason ?? "ต้องมีสิทธิ์เพิ่มเติม"),
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null, null));
                continue;
            }
            foreach (var l in e.Lines.OrderBy(l => l.LineOrder))
            {
                rows.Add(new JeLine(e.EntryNumber, e.EntryDate, book, status, e.Description, e.Reference,
                    docNo, doc?.Type ?? e.SourceDocumentType, doc?.ContactName, l.LineOrder, l.AccountCode, l.AccountName, l.Description,
                    l.DebitAmount, l.CreditAmount,
                    names.Branch(l.BranchId ?? e.BranchId), l.ProjectName ?? e.ProjectName ?? names.Project(l.ProjectId ?? e.ProjectId),
                    names.Dimension(l.DimensionId ?? e.DimensionId), l.Tags ?? e.Tags, e.Note));
                totalDr += l.DebitAmount;
                totalCr += l.CreditAmount;
            }
        }
        var footer = new[]
        {
            new JeLine("", null, "", "", "รวมทั้งสิ้น", null, null, null, null, null, null, null, null,
                totalDr, totalCr, null, null, null, null, null),
        };
        return TabularExport.Sheet("สมุดรายวัน", cols, rows, footer, title);
    }

    // ═══ งบการเงิน (หนึ่งคอลัมน์ยอด) ═══

    private sealed record FsLine(string Section, string? Code, string Name, decimal? Amount);

    private static readonly TabularColumn<FsLine>[] FsCols =
    {
        new("หมวด", r => r.Section),
        new("รหัสบัญชี", r => r.Code),
        new("รายการ", r => r.Name),
        new("จำนวนเงิน", r => r.Amount, TabularCellKind.Money),
    };

    private static IEnumerable<FsLine> Flatten(string section, IEnumerable<BalanceSheetSection>? items, int depth = 0)
    {
        foreach (var s in items ?? Enumerable.Empty<BalanceSheetSection>())
        {
            yield return new FsLine(section, s.AccountCode, new string(' ', depth * 2) + s.AccountName, s.Amount);
            foreach (var c in Flatten(section, s.Children, depth + 1)) yield return c;
        }
    }

    public static TabularSheet BalanceSheet(BalanceSheetResponse bs, IEnumerable<string> title)
    {
        var rows = new List<FsLine>();
        rows.AddRange(Flatten("สินทรัพย์", bs.Assets));
        rows.Add(new FsLine("สินทรัพย์", null, "รวมสินทรัพย์", bs.TotalAssets));
        rows.AddRange(Flatten("หนี้สิน", bs.Liabilities));
        rows.Add(new FsLine("หนี้สิน", null, "รวมหนี้สิน", bs.TotalLiabilities));
        rows.AddRange(Flatten("ส่วนของเจ้าของ", bs.Equity));
        rows.Add(new FsLine("ส่วนของเจ้าของ", null, "รวมส่วนของเจ้าของ", bs.TotalEquity));
        var footer = new[] { new FsLine("", null, "รวมหนี้สินและส่วนของเจ้าของ", bs.TotalLiabilities + bs.TotalEquity) };
        return TabularExport.Sheet("งบแสดงฐานะการเงิน", FsCols, rows, footer, title);
    }

    public static TabularSheet ProfitLoss(ProfitAndLossResponse pl, IEnumerable<string> title)
    {
        var rows = new List<FsLine>();
        rows.AddRange(pl.Revenue.Select(r => new FsLine("รายได้", r.AccountCode, r.AccountName, r.Amount)));
        rows.Add(new FsLine("รายได้", null, "รวมรายได้", pl.TotalRevenue));
        rows.AddRange(pl.Expenses.Select(r => new FsLine("ค่าใช้จ่าย", r.AccountCode, r.AccountName, r.Amount)));
        rows.Add(new FsLine("ค่าใช้จ่าย", null, "รวมค่าใช้จ่าย", pl.TotalExpenses));
        var footer = new[] { new FsLine("", null, "กำไร(ขาดทุน)สุทธิ", pl.NetIncome) };
        return TabularExport.Sheet("งบกำไรขาดทุน", FsCols, rows, footer, title);
    }

    public static TabularSheet CashFlow(CashFlowStatementResponse cf, IEnumerable<string> title)
    {
        var rows = new List<FsLine>();
        foreach (var sec in new[] { cf.OperatingActivities, cf.InvestingActivities, cf.FinancingActivities })
        {
            if (sec == null) continue;
            rows.AddRange(sec.Items.Select(i => new FsLine(sec.SectionName, i.AccountCode, i.Description, i.Amount)));
            rows.Add(new FsLine(sec.SectionName, null, "รวม" + sec.SectionName, sec.SubTotal));
        }
        var footer = new[]
        {
            new FsLine("", null, "เงินสดเปลี่ยนแปลงสุทธิ", cf.NetCashChange),
            new FsLine("", null, "เงินสดต้นงวด", cf.OpeningCashBalance),
            new FsLine("", null, "เงินสดปลายงวด", cf.ClosingCashBalance),
        };
        return TabularExport.Sheet("งบกระแสเงินสด", FsCols, rows, footer, title);
    }

    // ═══ อายุลูกหนี้/เจ้าหนี้ ═══

    private sealed record AgingContactLine(string Contact, string? TaxId, decimal Current, decimal D1, decimal D31, decimal D61, decimal D90, decimal Total);

    /// <summary>สรุปรายผู้ติดต่อ — คอลัมน์เดียวกับตารางหลักของหน้า + เลขผู้เสียภาษี · แถวรวมจาก <see cref="AgingReportResponse.Totals"/></summary>
    public static TabularSheet AgingByContact(AgingReportResponse r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<AgingContactLine>("ผู้ติดต่อ", x => x.Contact),
            new TabularColumn<AgingContactLine>("เลขประจำตัวผู้เสียภาษี", x => x.TaxId),
            new TabularColumn<AgingContactLine>("ยังไม่ถึงกำหนด", x => x.Current, TabularCellKind.Money),
            new TabularColumn<AgingContactLine>("1-30 วัน", x => x.D1, TabularCellKind.Money),
            new TabularColumn<AgingContactLine>("31-60 วัน", x => x.D31, TabularCellKind.Money),
            new TabularColumn<AgingContactLine>("61-90 วัน", x => x.D61, TabularCellKind.Money),
            new TabularColumn<AgingContactLine>("เกิน 90 วัน", x => x.D90, TabularCellKind.Money),
            new TabularColumn<AgingContactLine>("รวม", x => x.Total, TabularCellKind.Money),
        };
        var rows = r.Details.Select(c => new AgingContactLine(c.ContactName, c.TaxId, c.Current, c.Days1To30, c.Days31To60, c.Days61To90, c.Over90Days, c.TotalBalance));
        var t = r.Totals;
        var footer = new[] { new AgingContactLine("รวมทั้งสิ้น", null, t.Current, t.Days1To30, t.Days31To60, t.Days61To90, t.Over90Days, t.GrandTotal) };
        return TabularExport.Sheet("สรุปรายผู้ติดต่อ", cols, rows, footer, title);
    }

    private sealed record AgingDocLine(string Contact, string? TaxId, string DocNo, string DocType, DateTime? DocDate, DateTime? Due,
        decimal? Total, decimal Balance, int? Days, string? Bucket);

    /// <summary>รายเอกสาร (ที่หน้าแสดงเมื่อกางผู้ติดต่อ) — ใช้ตามทวง/กระทบยอดรายใบ · แถวรวม = ยอดรวมทั้งสิ้นของรายงาน</summary>
    public static TabularSheet AgingByDocument(AgingReportResponse r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<AgingDocLine>("ผู้ติดต่อ", x => x.Contact),
            new TabularColumn<AgingDocLine>("เลขประจำตัวผู้เสียภาษี", x => x.TaxId),
            new TabularColumn<AgingDocLine>("เลขที่เอกสาร", x => x.DocNo),
            new TabularColumn<AgingDocLine>("ชนิดเอกสาร", x => x.DocType),
            new TabularColumn<AgingDocLine>("วันที่เอกสาร", x => x.DocDate, TabularCellKind.Date),
            new TabularColumn<AgingDocLine>("ครบกำหนด", x => x.Due, TabularCellKind.Date),
            new TabularColumn<AgingDocLine>("ยอดรวมเอกสาร", x => x.Total, TabularCellKind.Money),
            new TabularColumn<AgingDocLine>("คงค้าง", x => x.Balance, TabularCellKind.Money),
            new TabularColumn<AgingDocLine>("อายุ (วัน)", x => x.Days, TabularCellKind.Integer),
            new TabularColumn<AgingDocLine>("กลุ่มอายุ", x => x.Bucket),
        };
        var rows = r.Details.SelectMany(c => c.Documents.Select(d => new AgingDocLine(c.ContactName, c.TaxId, d.DocumentNumber,
            DocumentTypeNames.Title(d.DocumentType, "th"), d.DocumentDate, d.DueDate, d.TotalAmount, d.BalanceDue, d.AgingDays, d.AgingBucket)));
        var footer = new[] { new AgingDocLine("รวมทั้งสิ้น", null, "", "", null, null, null, r.Totals.GrandTotal, null, null) };
        return TabularExport.Sheet("รายเอกสาร", cols, rows, footer, title);
    }

    // ═══ บันทึกการใช้งาน (audit log) ═══

    public static TabularSheet AuditLogs(IEnumerable<AuditLogResponse> logs, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<AuditLogResponse>("ลำดับ", x => x.Id, TabularCellKind.Integer),
            new TabularColumn<AuditLogResponse>("เวลา (ไทย)", x => x.Timestamp, TabularCellKind.Timestamp),
            new TabularColumn<AuditLogResponse>("ผู้ใช้", x => x.UserEmail),
            new TabularColumn<AuditLogResponse>("การกระทำ", x => x.Action.ToString()),
            new TabularColumn<AuditLogResponse>("ประเภทข้อมูล", x => x.EntityType),
            new TabularColumn<AuditLogResponse>("รหัสข้อมูล", x => x.EntityId),
            new TabularColumn<AuditLogResponse>("IP", x => x.IpAddress),
            new TabularColumn<AuditLogResponse>("โปรแกรมที่ใช้", x => x.UserAgent),
            new TabularColumn<AuditLogResponse>("ค่าเดิม", x => x.OldValues),
            new TabularColumn<AuditLogResponse>("ค่าใหม่", x => x.NewValues),
        };
        return TabularExport.Sheet("บันทึกการใช้งาน", cols, logs, title: title);
    }

    // ═══ ทะเบียนสินทรัพย์ + ตารางค่าเสื่อม ═══

    private sealed record AssetLine(string Code, string Name, string? Category, string? Location, DateTime? Purchase, decimal Cost,
        decimal? Salvage, int? Life, string? Method, decimal AccDep, decimal Nbv, string? Status, DateTime? DisposalDate, decimal? DisposalAmount);

    public static TabularSheet AssetRegister(AssetRegisterReport r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<AssetLine>("รหัสสินทรัพย์", x => x.Code),
            new TabularColumn<AssetLine>("ชื่อสินทรัพย์", x => x.Name),
            new TabularColumn<AssetLine>("หมวด", x => x.Category),
            new TabularColumn<AssetLine>("ที่ตั้ง", x => x.Location),
            new TabularColumn<AssetLine>("วันที่ซื้อ", x => x.Purchase, TabularCellKind.Date),
            new TabularColumn<AssetLine>("ราคาทุน", x => x.Cost, TabularCellKind.Money),
            new TabularColumn<AssetLine>("มูลค่าซาก", x => x.Salvage, TabularCellKind.Money),
            new TabularColumn<AssetLine>("อายุการใช้งาน (เดือน)", x => x.Life, TabularCellKind.Integer),
            new TabularColumn<AssetLine>("วิธีคิดค่าเสื่อม", x => x.Method),
            new TabularColumn<AssetLine>("ค่าเสื่อมราคาสะสม", x => x.AccDep, TabularCellKind.Money),
            new TabularColumn<AssetLine>("มูลค่าตามบัญชี", x => x.Nbv, TabularCellKind.Money),
            new TabularColumn<AssetLine>("สถานะ", x => x.Status),
            new TabularColumn<AssetLine>("วันที่จำหน่าย", x => x.DisposalDate, TabularCellKind.Date),
            new TabularColumn<AssetLine>("ราคาจำหน่าย", x => x.DisposalAmount, TabularCellKind.Money),
        };
        var rows = r.Items.Select(i => new AssetLine(i.AssetCode, i.Name, i.Category, i.Location, i.PurchaseDate, i.PurchaseCost, i.SalvageValue,
            i.UsefulLifeMonths, i.DepreciationMethod, i.AccumulatedDepreciation, i.NetBookValue, i.Status, i.DisposalDate, i.DisposalAmount));
        var footer = new[] { new AssetLine("", "รวมทั้งสิ้น", null, null, null, r.TotalCost, null, null, null,
            r.TotalAccumulatedDepreciation, r.TotalNetBookValue, null, null, null) };
        return TabularExport.Sheet("ทะเบียนสินทรัพย์", cols, rows, footer, title);
    }

    public static TabularSheet DepreciationSchedule(DepreciationScheduleReport r, IEnumerable<string> title)
    {
        var cols = new[]
        {
            new TabularColumn<DepreciationScheduleItem>("ปี (ค.ศ.)", x => x.Year, TabularCellKind.Integer),
            new TabularColumn<DepreciationScheduleItem>("เดือน", x => x.Month, TabularCellKind.Integer),
            new TabularColumn<DepreciationScheduleItem>("มูลค่าต้นงวด", x => x.OpeningNBV, TabularCellKind.Money),
            new TabularColumn<DepreciationScheduleItem>("ค่าเสื่อมราคา", x => x.DepreciationAmount, TabularCellKind.Money),
            new TabularColumn<DepreciationScheduleItem>("ค่าเสื่อมราคาสะสม", x => x.AccumulatedDepreciation, TabularCellKind.Money),
            new TabularColumn<DepreciationScheduleItem>("มูลค่าปลายงวด", x => x.ClosingNBV, TabularCellKind.Money),
        };
        return TabularExport.Sheet("ตารางค่าเสื่อมราคา", cols, r.Schedule, title: title);
    }
}
