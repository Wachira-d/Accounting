using System.Globalization;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>ข้อเสนอการจับคู่คอลัมน์จากหัวคอลัมน์ (ผู้ใช้ยืนยันก่อนใช้เสมอ)</summary>
/// <param name="Map">การจับคู่ที่เสนอ</param>
/// <param name="UnmappedHeaders">หัวคอลัมน์ที่ระบบไม่รู้ว่าเป็นอะไร (ไม่ใช่ PII ที่รู้จัก) — ผู้ใช้ต้องเลือกว่าใช้หรือไม่ใช้</param>
/// <param name="Issues">การจับคู่ที่ยังขาด (ผลของ <see cref="SettlementColumnMap.Validate"/>)</param>
public sealed record SettlementColumnMapSuggestion(
    SettlementColumnMap Map,
    IReadOnlyList<string> UnmappedHeaders,
    IReadOnlyList<string> Issues);

/// <summary>
/// **adapter ทั่วไป — ผู้ใช้จับคู่คอลัมน์ · ระบบจำไว้ที่ <c>SettlementChannel.ColumnMapJson</c>** (DECISIONS: เฟส 1 ใช้ตัวนี้ตัวเดียว)
///
/// <para>═══ ล้มดังทั้งไฟล์ (ไม่มีแถวใดถูกบันทึก) เมื่อ ═══ ยังไม่มีการจับคู่ · หัวคอลัมน์ที่จับคู่ไว้หายไป (รูปแบบไฟล์เปลี่ยน) ·
/// หัวคอลัมน์ซ้ำ · ไฟล์แบบกว้างมีคอลัมน์ใหม่ที่ยังไม่ได้เลือกว่าใช้/ไม่ใช้ · ยอด/วันที่ในช่องที่จับคู่อ่านไม่ได้ ·
/// วันที่ปนสองรูปแบบ — ข้อความบอกแถว/คอลัมน์และทางไปต่อ (จับคู่คอลัมน์ใหม่)</para>
/// <para>ข้ามโดยตั้งใจ: แถวว่าง · แถวยอด 0 (ไม่ใช่หลักฐาน — ตัวคิดแผนข้ามอยู่แล้ว · บรรทัดเทา) · แถวสรุปยอดที่<b>พิสูจน์ได้</b>
/// (<see cref="SettlementFileDecisions.IsSummaryRow"/> — คำสรุป · แบบกว้าง: ไม่มีวันที่ หรือยอดเท่าผลรวมของแถวที่มีเลข · แบบยาว: ไม่มีป้าย/วันที่)
/// ⇒ <b>คำเตือน</b>พร้อมยอดสุทธิที่คิดแบบเดียวกับแถวจริง (DECISIONS ข้อ 39) · แถวแบบกว้างไม่มีเลขที่เหลือ = รายการจริง นำเข้า + คำเตือนรายแถว</para>
/// <para>ทีม I รอบ 200 (review198-B R-B7–R-B11): เลขอ้างอิงที่ Excel ปัดหลักแล้ว ⇒ ล้มดัง · ลำดับวัน/เดือน + เขตเวลาตัดสินจากทั้งไฟล์
/// (<see cref="SettlementFileDecisions"/> — กำกวมและมีผล ⇒ ล้มดังให้เลือก · พิสูจน์ได้ ⇒ คืนให้ผู้นำเข้าจำ) · CSV ; + ยอด "1,500" ⇒ ล้มดัง</para>
/// </summary>
public sealed class GenericColumnMapAdapter : ISettlementReportAdapter
{
    public const string AdapterCode = "generic-column-map";
    public string Code => AdapterCode;

    /// <summary>จำนวนแถวแรกที่ค้นหาหัวตาราง (รายงานบางเจ้ามีหัวกระดาษก่อนตาราง)</summary>
    private const int HeaderSearchRows = 30;

    public decimal Detect(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> sampleRows, SettlementChannel channel)
    {
        SettlementColumnMap? map;
        try { map = SettlementColumnMap.Parse(channel.ColumnMapJson); }
        catch (SettlementFormatException) { return 0m; }
        if (map == null) return 0m;
        var have = new HashSet<string>(headers.Select(SettlementColumnMap.NormalizeHeader), StringComparer.Ordinal);
        var refs = map.ReferencedHeaders().Select(SettlementColumnMap.NormalizeHeader).Distinct().ToList();
        if (refs.Count == 0) return 0m;
        return Math.Round((decimal)refs.Count(have.Contains) / refs.Count, 2, MidpointRounding.AwayFromZero);
    }

    public SettlementParseResult Parse(SettlementFileInput file, SettlementChannel channel, string? columnMapJson,
        SettlementParseContext? context = null)
    {
        var map = SettlementColumnMap.Parse(columnMapJson)
                  ?? throw new SettlementFormatException("column-map-missing",
                      "ช่องทางนี้ยังไม่ได้จับคู่คอลัมน์ของไฟล์ — เปิด \"ตรวจไฟล์\" เพื่อให้ระบบเสนอการจับคู่ แล้วยืนยันก่อนนำเข้า");
        var mapIssues = map.Validate();
        if (mapIssues.Count > 0)
            throw new SettlementFormatException("column-map-invalid", "การจับคู่คอลัมน์ยังไม่ครบ: " + string.Join(" · ", mapIssues));

        var opened = SettlementFileReader.Open(file);
        using var rows = opened.Rows.GetEnumerator();
        var (headerRowNo, headers) = FindHeader(rows, map);
        var index = BuildIndex(headers, map);

        // ── เก็บเฉพาะช่องที่จับคู่ (ไม่เก็บคอลัมน์ชื่อ/ที่อยู่ผู้ซื้อเลย) แล้วค่อยแปลงค่า — ต้องเห็นทั้งไฟล์ก่อนตัดสินแถวสรุป/ลำดับวัน/เดือน/เขตเวลา ──
        var all = new List<SourceRow>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var rowNo = headerRowNo;
        while (rows.MoveNext())
        {
            rowNo++;
            var r = rows.Current;
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, col) in index)
                cells[key] = col < r.Count ? r[col] : "";
            var first = r.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "";
            var txn = Cell(cells, map.TxnId);
            var orderId = Cell(cells, map.OrderId);
            var payout = Cell(cells, map.PayoutRef);
            // R-B7: เลขอ้างอิงที่ผ่านตัวเลขของ Excel แล้วเสียหลักท้าย ⇒ ล้มดังทั้งไฟล์ (เก็บไว้ = คีย์กันซ้ำชน + จับคู่ใบขายไม่เจอ)
            EnsureIdIntact(txn, rowNo, map.TxnId);
            EnsureIdIntact(orderId, rowNo, map.OrderId);
            EnsureIdIntact(payout, rowNo, map.PayoutRef);
            all.Add(new SourceRow(rowNo, cells, first, txn, orderId, payout));
        }

        // I-6: ไฟล์แบบกว้างที่คอลัมน์เลขออเดอร์/เลขรายการว่างทุกแถว = จับคู่คอลัมน์ผิด ⇒ ล้มดังด้วยเหตุจริง (เดิม "ตรวจคอลัมน์ยอดเงิน")
        if (SettlementFileDecisions.WideIdColumnsEmpty(map.Layout, map.OrderId, map.TxnId,
                all.Select(b => (b.Txn, b.Order)).ToList()) is string noIds)
            throw new SettlementFormatException("id-column-empty", noIds);

        // R-B10: CSV คั่นด้วย ; + ยอด "1,500" ไม่มีจุดทศนิยม = อ่านได้สองแบบ ⇒ ล้มดัง (ไม่เดาว่าเป็น 1,500 หรือ 1.5) · ผู้ใช้ยืนยันได้ (I-5)
        var amountHeaders = map.Layout == SettlementFileLayout.Long
            ? new[] { map.Amount, map.AmountIn, map.AmountOut, map.Vat, map.Wht }.Where(h => h != null).Select(h => h!).ToList()
            : map.AmountColumns.Select(c => c.Header).ToList();
        if (SettlementFileDecisions.DecimalCommaAmbiguity(opened.CsvDelimiter,
                all.SelectMany(b => amountHeaders.Select(h => (b.RowNo, h, Cell(b.Cells, h)))).ToList(),
                map.CommaIsThousands, context) is string decimalComma)
            throw new SettlementFormatException("decimal-comma", decimalComma);

        // R-B11 · DECISIONS ข้อ 39: ข้ามเฉพาะแถวที่พิสูจน์ได้ว่าเป็นแถวสรุป (คำสรุป · ไม่มีวันที่ · ยอดเท่าผลรวมของแถวที่มีเลข) — ยอดที่ข้ามแสดงเป็นคำเตือน
        // (ไม่ใช่บรรทัดเทา) · แถวไม่มีเลขที่เหลือ = รายการจริง นำเข้าพร้อมคำเตือนรายแถว · ตัดแถวสรุปก่อนตัดสินวันที่
        var idRowAmounts = map.Layout == SettlementFileLayout.Wide
            ? all.Where(b => b.HasId).Select(b => RawWideAmounts(b.Cells, map)).ToList()
            : new List<IReadOnlyList<decimal?>>();
        var body = new List<SourceRow>();
        foreach (var b in all)
        {
            var totals = map.Layout == SettlementFileLayout.Wide && !b.HasId
                         && SettlementFileDecisions.TotalsIdRows(RawWideAmounts(b.Cells, map), idRowAmounts);
            var rawDate = Cell(b.Cells, map.Date);
            if (SettlementFileDecisions.IsSummaryRow(map.Layout, b.Txn, b.Order,
                    map.Layout == SettlementFileLayout.Long ? Cell(b.Cells, map.Type) : null, rawDate, b.First, totals))
            {
                warnings.Add(SummaryNotice(b, map, totals, rawDate == null));
                continue;
            }
            body.Add(b);
        }

        // R-B9/R-B8: ลำดับวัน/เดือน และเขตเวลา — ตัดสินจากทั้งไฟล์ในรอบเดียว (I-2: กำกวมทั้งคู่ถามพร้อมกัน) · กำกวมและมีผล = ล้มดังให้เลือก ·
        // พิสูจน์ได้ = จำให้ช่องทาง · ค่าที่จำไว้ขัดกับไฟล์ = ล้มดังบอกเหตุ (I-10)
        var dateRaws = new List<(int RowNo, string? Raw)>();
        if (map.Date != null)
            foreach (var b in body) dateRaws.Add((b.RowNo, Cell(b.Cells, map.Date)));
        var (orderDecision, zoneDecision) = SettlementFileDecisions.DecideDates(map.DateOrder, map.TimeZone, map.Date, dateRaws, context);
        var order = orderDecision.Value;
        var zone = zoneDecision.Value;
        // I-1: วันที่แบบที่ตัวอ่านก่อนรอบ 200 อ่าน — ผู้นำเข้าคิดคีย์กันซ้ำรุ่นก่อนจากค่านี้ (ไฟล์ที่นำเข้าก่อน deploy ต้องถูกจับว่าซ้ำ)
        var legacyOrders = SettlementFileDecisions.LegacyReadOrders(order, dateRaws);

        var parsed = new List<SettlementParsedRow>();
        var loose = new List<(int RowNo, decimal Total)>();
        foreach (var b in body)
        {
            var no = b.RowNo;
            var cells = b.Cells;
            var rawDate = Cell(cells, map.Date);
            var date = ParseDate(rawDate, order, zone, no, map.Date);
            var literal = LiteralDates(rawDate, legacyOrders, date);
            var desc = Cell(cells, map.Description);
            decimal? Strict(string? raw, string? header) => ParseAmount(raw, no, header);

            if (map.Layout == SettlementFileLayout.Long)
            {
                var label = Cell(cells, map.Type);
                var (amount, vat, wht) = LongAmount(cells, map, Strict);
                if (amount is not decimal amt || amt == 0m)
                {
                    skipped.Add($"แถว {no}: ยอด 0 หรือว่าง (\"{Truncate(label ?? b.First, 40)}\")");
                    continue;
                }
                if (label == null)
                    throw new SettlementFormatException("type-missing",
                        $"แถว {no}: ไม่มีค่าในคอลัมน์ประเภทรายการ \"{map.Type}\" — ตรวจว่าจับคู่คอลัมน์ถูก หรือไฟล์มีแถวที่ไม่สมบูรณ์");
                parsed.Add(new SettlementParsedRow(no, label, desc, date, b.Order, b.Txn, amt, vat, wht, null, b.Payout, null, literal));
            }
            else
            {
                var amounts = WideAmounts(cells, map, Strict);
                foreach (var (col, amt, vat) in amounts)
                    parsed.Add(new SettlementParsedRow(no, col.Header.Trim(), desc, date, b.Order, b.Txn, amt, vat, null,
                        SettlementColumnMap.ParseType(col.Type), b.Payout, null, literal));
                if (amounts.Count == 0) skipped.Add($"แถว {no}: ไม่มียอดในคอลัมน์ที่เลือก");
                else if (!b.HasId) loose.Add((no, amounts.Sum(a => a.Amount)));
            }
        }
        // DECISIONS ข้อ 39: แถวแบบกว้างที่ไม่มีเลขแต่ไม่ใช่แถวสรุปที่พิสูจน์ได้ = รายการจริง — นำเข้า + บอกรายแถว (ห้ามข้ามเงียบ/บรรทัดเทา)
        if (loose.Count > 0)
            warnings.Add($"แถวที่ {string.Join(", ", loose.Take(20).Select(x => x.RowNo))}{(loose.Count > 20 ? " …" : "")} "
                + $"({loose.Count:N0} แถว · ยอดสุทธิรวม {Money(loose.Sum(x => x.Total))}) ไม่มีเลขออเดอร์/เลขรายการ แต่มีวันที่และยอดที่ไม่ใช่ผลรวมของแถวอื่น — "
                + "ระบบนำเข้าเป็นรายการ (ข้ามเฉพาะแถวสรุปที่พิสูจน์ได้) · ถ้าเป็นแถวสินค้าของออเดอร์ก่อนหน้า (xlsx ที่รวมเซลล์เลขออเดอร์) ให้เติมเลขออเดอร์ทุกแถว "
                + "· ถ้าเป็นแถวสรุป/แถวรวม ให้ลบออกจากไฟล์ — แล้วยกเลิกรอบโอนนี้และนำเข้าใหม่ก่อนลงบัญชี");

        if (parsed.Count == 0)
            throw new SettlementFormatException("no-rows",
                "ไม่พบรายการที่มียอดเงินในไฟล์ — ตรวจว่าเลือกไฟล์ถูกรอบ และจับคู่คอลัมน์ยอดเงินถูกต้อง"
                + (warnings.Count > 0 ? " · แถวที่ข้ามเป็นแถวสรุป: " + string.Join(" · ", warnings.Take(3)) : ""));
        return new SettlementParseResult(AdapterCode, headers, parsed, skipped,
            orderDecision.Proven ? order : null, zoneDecision.Proven ? zone : null, warnings.Count == 0 ? null : warnings);
    }

    /// <summary>แถวในไฟล์ (เฉพาะช่องที่จับคู่) ก่อนแปลงค่า</summary>
    private sealed record SourceRow(int RowNo, Dictionary<string, string> Cells, string First, string? Txn, string? Order, string? Payout)
    {
        public bool HasId => !string.IsNullOrWhiteSpace(Txn) || !string.IsNullOrWhiteSpace(Order);
    }

    /// <summary>
    /// **ยอดของแถวแบบยาว — ตัวเดียวของทั้งการอ่านแถวจริงและข้อความแถวสรุป** (ฝ่ายค้าน I-4: เดิมข้อความแถวสรุปรวมค่าดิบ ไม่ดู กลับเครื่องหมาย/VAT/ยอดออก) ·
    /// ยอด 0/ว่าง ⇒ ไม่อ่าน VAT/WHT (ลำดับเดิม)
    /// </summary>
    private static (decimal? Amount, decimal? Vat, decimal? Wht) LongAmount(Dictionary<string, string> cells, SettlementColumnMap map,
        Func<string?, string?, decimal?> read)
    {
        decimal? amount;
        if (map.Amount != null)
        {
            amount = read(Cell(cells, map.Amount), map.Amount);
            if (amount is decimal a0 && map.Negate) amount = -a0;
        }
        else
        {
            var inn = read(Cell(cells, map.AmountIn), map.AmountIn);
            var outt = read(Cell(cells, map.AmountOut), map.AmountOut);
            amount = inn == null && outt == null ? null : Math.Abs(inn ?? 0m) - Math.Abs(outt ?? 0m);
        }
        if (amount is not decimal amt || amt == 0m) return (amount, null, null);
        var vat = AlignSign(read(Cell(cells, map.Vat), map.Vat), amt);
        if (map.VatExclusive && vat is decimal v && v != 0m) amt += v;      // ยอดในไฟล์ยังไม่รวมคอลัมน์ VAT
        var wht = read(Cell(cells, map.Wht), map.Wht);
        return (amt, vat, wht);
    }

    /// <summary>
    /// **ยอดของแถวแบบกว้างทีละคอลัมน์ (มีเครื่องหมายมุม wallet · รวม VAT) — ตัวเดียวของทั้งการอ่านแถวจริงและข้อความแถวสรุป** (ฝ่ายค้าน I-4) ·
    /// คอลัมน์ที่ว่าง/0 ไม่นับ
    /// </summary>
    private static List<(SettlementAmountColumn Col, decimal Amount, decimal? Vat)> WideAmounts(Dictionary<string, string> cells,
        SettlementColumnMap map, Func<string?, string?, decimal?> read)
    {
        var result = new List<(SettlementAmountColumn Col, decimal Amount, decimal? Vat)>();
        foreach (var col in map.AmountColumns)
        {
            var value = read(Cell(cells, col.Header), col.Header);
            if (value is not decimal v0 || v0 == 0m) continue;
            var amt = col.Negate ? -v0 : v0;
            decimal? vat = null;
            if (col.VatExclusive)
            {
                var (deducted, v) = SettlementFeeTax.FromExclusive(amt);
                amt = Math.Sign(amt) * deducted;
                vat = Math.Sign(amt) * v;
            }
            result.Add((col, amt, vat));
        }
        return result;
    }

    /// <summary>ค่าดิบของทุกคอลัมน์ยอดเงิน (แบบกว้าง · ลำดับตามการจับคู่ · อ่านไม่ได้ = null) — ป้อน <see cref="SettlementFileDecisions.TotalsIdRows"/></summary>
    private static IReadOnlyList<decimal?> RawWideAmounts(Dictionary<string, string> cells, SettlementColumnMap map)
        => map.AmountColumns.Select(c => Tolerant(Cell(cells, c.Header), c.Header)).ToList();

    /// <summary>อ่านยอดแบบไม่ล้ม (อ่านไม่ได้ = null) — ใช้กับแถวที่<b>ไม่นำเข้า</b>เท่านั้น (ข้อความแถวสรุป · หลักฐานแถวรวม)</summary>
    private static decimal? Tolerant(string? raw, string? header)
        => SettlementValueParser.TryParseAmount(raw, out var v) ? v : null;

    /// <summary>วันที่ของแถวตามกติกาตัวอ่านก่อนรอบ 200 ทีละลำดับวัน/เดือนที่ตัวอ่านเดิมอาจใช้ (อ่านไม่ได้ ⇒ วันที่ที่อ่านวันนี้) — ฝ่ายค้าน I-1</summary>
    private static IReadOnlyList<DateTime?> LiteralDates(string? raw, IReadOnlyList<SettlementDateOrder> orders, DateTime? read)
        => orders.Select(o => SettlementValueParser.TryParseLiteralDate(raw, o, out var v) ? v : read).ToList();

    /// <summary>R-B7: เลขอ้างอิงที่เสียหลักแล้ว (<see cref="SettlementValueParser.IdLostPrecision"/>) ⇒ ล้มดังทั้งไฟล์พร้อมทางไปต่อ</summary>
    private static void EnsureIdIntact(string? raw, int rowNo, string? header)
    {
        if (!SettlementValueParser.IdLostPrecision(raw)) return;
        throw new SettlementFormatException("id-precision",
            $"แถว {rowNo} คอลัมน์ \"{header}\": \"{Truncate(raw, 30)}\" เป็นตัวเลขของ Excel ที่เสียหลักท้ายไปแล้ว (Excel เก็บตัวเลขได้ 15 หลัก — "
            + "เลขออเดอร์/เลขรายการที่ยาวกว่านั้นถูกปัด) · ส่งออกรายงานจากแพลตฟอร์มเป็น CSV โดยตรง (ไม่เปิดแล้วบันทึกด้วย Excel) หรือจัดรูปแบบคอลัมน์นั้น"
            + "เป็นข้อความก่อนบันทึก แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า — เลขที่ผิดจะทำให้รายการต่างกันชนกันและจับคู่ใบขายไม่เจอ)");
    }

    /// <summary>
    /// ข้อความแจ้งแถวสรุปที่ข้าม — บอกเหตุที่พิสูจน์ได้ + <b>ยอดสุทธิที่คิดแบบเดียวกับแถวจริง</b> (กลับเครื่องหมาย · VAT · ยอดออก — I-4) ·
    /// ไปที่คำเตือน (แถบเหลือง) ไม่ใช่บรรทัดเทา (DECISIONS ข้อ 39: ยอดที่ข้ามต้องแสดงชัด)
    /// </summary>
    private static string SummaryNotice(SourceRow b, SettlementColumnMap map, bool totals, bool dateMissing)
    {
        decimal total;
        bool seen;
        if (map.Layout == SettlementFileLayout.Long)
        {
            var (amount, _, _) = LongAmount(b.Cells, map, Tolerant);
            total = amount ?? 0m;
            seen = amount != null;
        }
        else
        {
            var amounts = WideAmounts(b.Cells, map, Tolerant);
            total = amounts.Sum(a => a.Amount);
            seen = amounts.Count > 0;
        }
        var amountText = seen ? $" · ยอดสุทธิของแถว {Money(total)}" : "";
        if (map.Layout == SettlementFileLayout.Wide)
        {
            var why = totals ? "ยอดทุกคอลัมน์เท่าผลรวมของแถวที่มีเลขออเดอร์" : dateMissing ? "ไม่มีเลขออเดอร์และไม่มีวันที่" : "ขึ้นต้นด้วยคำสรุป";
            return $"แถว {b.RowNo}: ข้ามแถวสรุปยอด/แถวรวม (\"{Truncate(b.First, 40)}\" — {why}){amountText} — ไม่นำเข้าเพราะนับซ้ำกับรายการ · "
                   + "ถ้าเป็นรายการจริง ให้เติมเลขออเดอร์และวันที่ในไฟล์แล้วนำเข้าใหม่";
        }
        return $"แถว {b.RowNo}: ข้ามแถวสรุปยอด \"{Truncate(b.First, 40)}\"{amountText} — ไม่นำเข้าเพราะนับซ้ำกับรายการ · "
               + "ถ้าเป็นรายการจริง ให้เติมประเภทรายการและวันที่ในไฟล์แล้วนำเข้าใหม่";
    }

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string? Cell(Dictionary<string, string> cells, string? header)
        => header == null ? null : NullIfBlank(cells.GetValueOrDefault(Key(header)));

    // ═══ หัวตาราง ═══

    private static (int RowNo, IReadOnlyList<string> Headers) FindHeader(IEnumerator<IReadOnlyList<string>> rows, SettlementColumnMap map)
    {
        var wanted = map.ReferencedHeaders().Select(SettlementColumnMap.NormalizeHeader).Distinct().ToList();
        var seen = new List<IReadOnlyList<string>>();
        var bestMissing = wanted;
        for (var i = 1; i <= HeaderSearchRows && rows.MoveNext(); i++)
        {
            var r = rows.Current;
            seen.Add(r);
            var have = new HashSet<string>(r.Select(SettlementColumnMap.NormalizeHeader), StringComparer.Ordinal);
            var missing = wanted.Where(w => !have.Contains(w)).ToList();
            if (missing.Count == 0) return (i, r);
            if (missing.Count < bestMissing.Count) bestMissing = missing;
        }
        var preview = string.Join(" / ", seen.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).Take(2)
            .Select(r => string.Join(" | ", r.Take(8).Select(c => Truncate(SettlementPiiScrubber.Scrub(c) ?? "(ว่าง)", 20)))));
        throw new SettlementFormatException("header-mismatch",
            $"ไม่พบหัวคอลัมน์ที่จับคู่ไว้: {string.Join(", ", bestMissing.Select(h => "\"" + h + "\""))} — รูปแบบไฟล์ของแพลตฟอร์มอาจเปลี่ยน "
            + $"หรือเลือกไฟล์ผิดชนิด · จับคู่คอลัมน์ใหม่ (\"ตรวจไฟล์\") แล้วนำเข้าอีกครั้ง · ระบบเห็น: {preview}");
    }

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> headers, SettlementColumnMap map)
    {
        var positions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            var h = SettlementColumnMap.NormalizeHeader(headers[i]);
            if (h.Length == 0) continue;
            if (!positions.TryGetValue(h, out var l)) positions[h] = l = new List<int>();
            l.Add(i);
        }
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in map.ReferencedHeaders().Select(SettlementColumnMap.NormalizeHeader).Distinct())
        {
            var at = positions[h];
            if (at.Count > 1)
                throw new SettlementFormatException("header-duplicate",
                    $"ไฟล์มีคอลัมน์ \"{h}\" มากกว่าหนึ่งคอลัมน์ — ระบบไม่รู้ว่าควรอ่านคอลัมน์ไหน · เปลี่ยนชื่อหัวคอลัมน์ในไฟล์ให้ไม่ซ้ำแล้วนำเข้าใหม่");
            index[h] = at[0];
        }
        if (map.Layout == SettlementFileLayout.Wide)
        {
            var known = new HashSet<string>(index.Keys, StringComparer.Ordinal);
            foreach (var ig in map.Ignore) known.Add(SettlementColumnMap.NormalizeHeader(ig));
            var unknown = positions.Keys.Where(h => !known.Contains(h)).ToList();
            if (unknown.Count > 0)
                throw new SettlementFormatException("header-new",
                    $"ไฟล์มีคอลัมน์ใหม่ที่ยังไม่ได้เลือกว่าใช้หรือไม่ใช้: {string.Join(", ", unknown.Select(h => "\"" + h + "\""))} — "
                    + "คอลัมน์ใหม่ในรายงานแบบกว้างมักเป็นค่าธรรมเนียมใหม่ (ข้ามเงียบ = ยอดหาย) · จับคู่คอลัมน์ใหม่แล้วนำเข้าอีกครั้ง");
        }
        return index;
    }

    // ═══ ค่า ═══

    private static decimal? ParseAmount(string? raw, int rowNo, string? header)
    {
        if (!SettlementValueParser.TryParseAmount(raw, out var v))
            throw new SettlementFormatException("amount-invalid",
                $"แถว {rowNo} คอลัมน์ \"{header}\": \"{Truncate(raw, 30)}\" ไม่ใช่ยอดเงิน — ตรวจการจับคู่คอลัมน์ยอดเงิน (ไม่มีรายการใดถูกนำเข้า)");
        return v;
    }

    private static DateTime? ParseDate(string? raw, SettlementDateOrder order, SettlementFileTimeZone zone, int rowNo, string? header)
    {
        if (!SettlementValueParser.TryParseDate(raw, order, zone, out var d))
            throw new SettlementFormatException("date-invalid",
                $"แถว {rowNo} คอลัมน์ \"{header}\": \"{Truncate(raw, 30)}\" อ่านเป็นวันที่ไม่ได้ (ปี 2 หลักถือว่ากำกวม · เวลาต้องเป็น ชม.:นาที) — "
                + "ส่งออกรายงานให้ปีเป็น 4 หลัก หรือจับคู่คอลัมน์วันที่ใหม่ (ไม่มีรายการใดถูกนำเข้า)");
        return d;
    }

    /// <summary>VAT ในไฟล์มักเป็นค่าบวกเสมอ — ให้เครื่องหมายตามยอดของแถว (ตัวคิดแผนตรวจว่า VAT อยู่ในยอดและเครื่องหมายเดียวกัน)</summary>
    private static decimal? AlignSign(decimal? vat, decimal amount)
        => vat is decimal v && v != 0m ? (amount < 0m ? -Math.Abs(v) : Math.Abs(v)) : vat;

    private static string Key(string header) => SettlementColumnMap.NormalizeHeader(header);
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Truncate(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";

    // ═══ เสนอการจับคู่ ═══

    /// <summary>เสนอการจับคู่จากหัวคอลัมน์ + แถวตัวอย่าง (pure) — แบบยาวถ้าพบคอลัมน์ประเภท+ยอด · ไม่งั้นแบบกว้างจากคอลัมน์ตัวเลขที่หัวเป็นป้ายที่รู้จัก ·
    /// หัวคอลัมน์ข้อมูลส่วนบุคคลเข้ารายการ "ไม่ใช้" เสมอ · ผลต้องให้ผู้ใช้ยืนยัน (ไม่ใช้เองอัตโนมัติ)</summary>
    public static SettlementColumnMapSuggestion Suggest(IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, SettlementChannelKind kind)
    {
        var norm = headers.Select(SettlementColumnMap.NormalizeHeader).ToList();
        var used = new HashSet<int>();
        string? Pick(string field)
        {
            foreach (var syn in SettlementLabelSeed.HeaderSynonyms[field])
            {
                var i = norm.FindIndex(h => h == syn);
                if (i >= 0 && used.Add(i)) return headers[i].Trim();
            }
            return null;
        }
        var map = new SettlementColumnMap
        {
            TxnId = Pick("txnId"), OrderId = Pick("orderId"), Date = Pick("date"), PayoutRef = Pick("payoutRef"),
        };
        var type = Pick("type");
        var amount = Pick("amount");
        if (type != null && amount != null)
        {
            map.Layout = SettlementFileLayout.Long;
            map.Type = type; map.Amount = amount;
            map.Description = Pick("description"); map.Vat = Pick("vat"); map.Wht = Pick("wht");
        }
        else if (type != null && Pick("amountIn") is string inCol)
        {
            map.Layout = SettlementFileLayout.Long;
            map.Type = type; map.AmountIn = inCol; map.AmountOut = Pick("amountOut");
            map.Description = Pick("description"); map.Vat = Pick("vat");
        }
        else
        {
            map.Layout = SettlementFileLayout.Wide;
            if (type != null) used.Remove(norm.FindIndex(h => h == SettlementColumnMap.NormalizeHeader(type)));
            map.Description = Pick("description");
            for (var i = 0; i < headers.Count; i++)
            {
                if (used.Contains(i) || norm[i].Length == 0) continue;
                if (SettlementLabelSeed.Lookup(headers[i], kind) != null && LooksNumeric(sampleRows, i))
                {
                    map.AmountColumns.Add(new SettlementAmountColumn { Header = headers[i].Trim() });
                    used.Add(i);
                }
            }
        }
        var unmapped = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            if (used.Contains(i) || norm[i].Length == 0) continue;
            if (SettlementLabelSeed.PersonalHeaderHints.Any(p => norm[i].Contains(p, StringComparison.Ordinal)))
                map.Ignore.Add(headers[i].Trim());
            else unmapped.Add(headers[i].Trim());
        }
        return new SettlementColumnMapSuggestion(map, unmapped, map.Validate());
    }

    private static bool LooksNumeric(IReadOnlyList<IReadOnlyList<string>> rows, int col)
    {
        var seen = 0;
        foreach (var r in rows)
        {
            if (col >= r.Count || string.IsNullOrWhiteSpace(r[col])) continue;
            if (!SettlementValueParser.TryParseAmount(r[col], out _)) return false;
            seen++;
        }
        return seen > 0;
    }

    /// <summary>อ่านหัวตาราง + แถวตัวอย่าง (ไม่เกิน <paramref name="sampleCount"/>) สำหรับหน้าจับคู่คอลัมน์ —
    /// หัวตาราง = แถวแรกที่ไม่ว่างซึ่งมีช่องข้อความอย่างน้อย 2 ช่องและไม่มีช่องไหนเป็นตัวเลขล้วน</summary>
    public static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Samples) Peek(SettlementFileInput file, int sampleCount = 5)
    {
        IReadOnlyList<string>? headers = null;
        var samples = new List<IReadOnlyList<string>>();
        var scanned = 0;
        foreach (var r in SettlementFileReader.ReadRows(file))
        {
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            if (headers == null)
            {
                if (++scanned > HeaderSearchRows) break;
                var filled = r.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                if (filled.Count >= 2 && filled.All(c => !SettlementValueParser.TryParseAmount(c, out var n) || n == null))
                    headers = r;
                continue;
            }
            samples.Add(r);
            if (samples.Count >= sampleCount) break;
        }
        if (headers == null)
            throw new SettlementFormatException("header-not-found",
                "หาแถวหัวตารางในไฟล์ไม่เจอ (ต้องเป็นแถวที่มีชื่อคอลัมน์อย่างน้อย 2 ช่อง) — ตรวจว่าเลือกไฟล์รายงานถูกชนิด");
        return (headers, samples);
    }
}
