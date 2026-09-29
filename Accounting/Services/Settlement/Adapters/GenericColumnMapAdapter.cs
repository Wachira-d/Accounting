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
/// <para>ข้ามโดยตั้งใจ (นับแจ้งผู้ใช้): แถวว่าง · แถวสรุปยอด (<see cref="SettlementFileDecisions.IsSummaryRow"/> — แบบกว้าง = ไม่มีเลขอ้างอิงใดเลย ·
/// แบบยาว = "รวม/Total/ยอดสุทธิ" หรือไม่มีเลข/ป้าย/วันที่) · แถวยอด 0 (ไม่ใช่หลักฐาน — ตัวคิดแผนข้ามอยู่แล้ว)</para>
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

        // ── เก็บเฉพาะช่องที่จับคู่ (ไม่เก็บคอลัมน์ชื่อ/ที่อยู่ผู้ซื้อเลย) แล้วค่อยแปลงค่า — ต้องเห็นวันที่ทั้งไฟล์ก่อนตัดสินลำดับวัน/เดือน/เขตเวลา ──
        var body = new List<(int RowNo, Dictionary<string, string> Cells, string First, string? Txn, string? Order, string? Payout)>();
        var skipped = new List<string>();
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
            // R-B11: แถวสรุป/แถวรวม — แบบกว้าง = ไม่มีเลขอ้างอิงใดเลย · แบบยาว = ไม่มีเลขอ้างอิง + คำสรุปหรือไม่มีป้าย/วันที่
            if (SettlementFileDecisions.IsSummaryRow(map.Layout, txn, orderId,
                    map.Layout == SettlementFileLayout.Long ? Cell(cells, map.Type) : null, Cell(cells, map.Date), first))
            {
                skipped.Add(SummaryNotice(rowNo, first, cells, map));
                continue;
            }
            body.Add((rowNo, cells, first, txn, orderId, payout));
        }

        // R-B10: CSV คั่นด้วย ; + ยอด "1,500" ไม่มีจุดทศนิยม = อ่านได้สองแบบ ⇒ ล้มดัง (ไม่เดาว่าเป็น 1,500 หรือ 1.5)
        var amountHeaders = map.Layout == SettlementFileLayout.Long
            ? new[] { map.Amount, map.AmountIn, map.AmountOut, map.Vat, map.Wht }.Where(h => h != null).Select(h => h!).ToList()
            : map.AmountColumns.Select(c => c.Header).ToList();
        if (SettlementFileDecisions.DecimalCommaAmbiguity(opened.CsvDelimiter,
                body.SelectMany(b => amountHeaders.Select(h => (b.RowNo, h, Cell(b.Cells, h)))).ToList()) is string decimalComma)
            throw new SettlementFormatException("decimal-comma", decimalComma);

        // R-B9/R-B8: ลำดับวัน/เดือน และเขตเวลา — ตัดสินจากทั้งไฟล์ · กำกวมและมีผล = ล้มดังให้เลือก (ไม่เดา) · พิสูจน์ได้ = จำให้ช่องทาง
        var dateRaws = new List<(int RowNo, string? Raw)>();
        if (map.Date != null)
            foreach (var b in body) dateRaws.Add((b.RowNo, Cell(b.Cells, map.Date)));
        var orderDecision = SettlementFileDecisions.DecideDateOrder(map.DateOrder, dateRaws, map.Date, context?.PeriodFrom, context?.PeriodTo);
        var zoneDecision = SettlementFileDecisions.DecideTimeZone(map.TimeZone, map.Date, dateRaws, orderDecision.Value);
        var order = orderDecision.Value;
        var zone = zoneDecision.Value;

        var parsed = new List<SettlementParsedRow>();
        foreach (var (no, cells, first, txn, orderId, payout) in body)
        {
            var date = ParseDate(Cell(cells, map.Date), order, zone, no, map.Date);
            var desc = Cell(cells, map.Description);

            if (map.Layout == SettlementFileLayout.Long)
            {
                var label = Cell(cells, map.Type);
                decimal? amount;
                if (map.Amount != null)
                {
                    amount = ParseAmount(Cell(cells, map.Amount), no, map.Amount);
                    if (amount is decimal a0 && map.Negate) amount = -a0;
                }
                else
                {
                    var inn = ParseAmount(Cell(cells, map.AmountIn), no, map.AmountIn);
                    var outt = ParseAmount(Cell(cells, map.AmountOut), no, map.AmountOut);
                    amount = inn == null && outt == null ? null : Math.Abs(inn ?? 0m) - Math.Abs(outt ?? 0m);
                }
                if (amount is not decimal amt || amt == 0m)
                {
                    skipped.Add($"แถว {no}: ยอด 0 หรือว่าง (\"{Truncate(label ?? first, 40)}\")");
                    continue;
                }
                var vat = AlignSign(ParseAmount(Cell(cells, map.Vat), no, map.Vat), amt);
                if (map.VatExclusive && vat is decimal v && v != 0m) amt += v;      // ยอดในไฟล์ยังไม่รวมคอลัมน์ VAT
                var wht = ParseAmount(Cell(cells, map.Wht), no, map.Wht);
                if (label == null)
                    throw new SettlementFormatException("type-missing",
                        $"แถว {no}: ไม่มีค่าในคอลัมน์ประเภทรายการ \"{map.Type}\" — ตรวจว่าจับคู่คอลัมน์ถูก หรือไฟล์มีแถวที่ไม่สมบูรณ์");
                parsed.Add(new SettlementParsedRow(no, label, desc, date, orderId, txn, amt, vat, wht, null, payout));
            }
            else
            {
                var any = false;
                foreach (var col in map.AmountColumns)
                {
                    var value = ParseAmount(Cell(cells, col.Header), no, col.Header);
                    if (value is not decimal v0 || v0 == 0m) continue;
                    any = true;
                    var amt = col.Negate ? -v0 : v0;
                    decimal? vat = null;
                    if (col.VatExclusive)
                    {
                        var (deducted, v) = SettlementFeeTax.FromExclusive(amt);
                        amt = Math.Sign(amt) * deducted;
                        vat = Math.Sign(amt) * v;
                    }
                    parsed.Add(new SettlementParsedRow(no, col.Header.Trim(), desc, date, orderId, txn, amt, vat, null,
                        SettlementColumnMap.ParseType(col.Type), payout));
                }
                if (!any) skipped.Add($"แถว {no}: ไม่มียอดในคอลัมน์ที่เลือก");
            }
        }

        if (parsed.Count == 0)
            throw new SettlementFormatException("no-rows",
                "ไม่พบรายการที่มียอดเงินในไฟล์ — ตรวจว่าเลือกไฟล์ถูกรอบ และจับคู่คอลัมน์ยอดเงินถูกต้อง");
        return new SettlementParseResult(AdapterCode, headers, parsed, skipped,
            orderDecision.Proven ? order : null, zoneDecision.Proven ? zone : null);
    }

    /// <summary>R-B7: เลขอ้างอิงที่เสียหลักแล้ว (<see cref="SettlementValueParser.IdLostPrecision"/>) ⇒ ล้มดังทั้งไฟล์พร้อมทางไปต่อ</summary>
    private static void EnsureIdIntact(string? raw, int rowNo, string? header)
    {
        if (!SettlementValueParser.IdLostPrecision(raw)) return;
        throw new SettlementFormatException("id-precision",
            $"แถว {rowNo} คอลัมน์ \"{header}\": \"{Truncate(raw, 30)}\" เป็นตัวเลขของ Excel ที่เสียหลักท้ายไปแล้ว (Excel เก็บตัวเลขได้ 15 หลัก — "
            + "เลขออเดอร์/เลขรายการที่ยาวกว่านั้นถูกปัด) · ส่งออกรายงานจากแพลตฟอร์มเป็น CSV โดยตรง (ไม่เปิดแล้วบันทึกด้วย Excel) หรือจัดรูปแบบคอลัมน์นั้น"
            + "เป็นข้อความก่อนบันทึก แล้วนำเข้าใหม่ (ไม่มีรายการใดถูกนำเข้า — เลขที่ผิดจะทำให้รายการต่างกันชนกันและจับคู่ใบขายไม่เจอ)");
    }

    /// <summary>ข้อความแจ้งแถวสรุปที่ข้าม — บอกยอดในคอลัมน์ที่เลือก (ผู้ใช้เห็นว่าข้ามอะไรไป · ถ้าเป็นรายการจริงสมการรอบโอนจะไม่ลงตัว)</summary>
    private static string SummaryNotice(int rowNo, string first, Dictionary<string, string> cells, SettlementColumnMap map)
    {
        decimal total = 0m;
        var seen = false;
        IEnumerable<string?> cols = map.Layout == SettlementFileLayout.Long
            ? new[] { map.Amount, map.AmountIn }
            : map.AmountColumns.Select(c => (string?)c.Header);
        foreach (var h in cols)
            if (SettlementValueParser.TryParseAmount(Cell(cells, h), out var v) && v is decimal d)
            {
                total += d;
                seen = true;
            }
        var amount = seen ? $" · ยอดในคอลัมน์ที่เลือก {total:N2}" : "";
        if (map.Layout == SettlementFileLayout.Wide)
            return $"แถว {rowNo}: แถวสรุปยอด/แถวรวม — ไม่มีเลขออเดอร์และเลขรายการ (\"{Truncate(first, 40)}\"{amount}) ไม่นำเข้า · "
                   + "ถ้าเป็นรายการจริง ให้เติมเลขออเดอร์ในไฟล์แล้วนำเข้าใหม่";
        return $"แถว {rowNo}: แถวสรุปยอด \"{Truncate(first, 40)}\"{amount} (ไม่นำเข้า — นับซ้ำกับรายการ)";
    }

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
