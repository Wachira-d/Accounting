using System.Globalization;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Product;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ไฟล์ส่งออก (CSV / Excel) ของรายการเอกสารซื้อ-ขาย · รายการบรรทัดเอกสาร · ผู้ติดต่อ · สินค้า — ตัวตั้งตัวเดียว** (ทีม X2 · 2026-10-14)
///
/// <para>═══ ที่มา (ผลตรวจ EXPORT_AUDIT §X2) ═══ ปุ่ม Export ของหน้าเอกสาร/หน้าซื้อสินค้าเดิมดึง <c>pageSize=10000</c> แต่เซิร์ฟเวอร์
/// บีบเหลือ 200 (<c>PagedRequest</c>) ⇒ <b>ไฟล์ขาดแถวเงียบ ๆ</b> เมื่อมีเกิน 200 ใบ · ไม่ส่งตัวกรองค้นหา/ช่วงวันที่/โครงการ/ผู้ติดต่อตามหน้าจอ ·
/// ยอดเงินเป็นข้อความคั่นหลักพัน ("1,234.00") Excel คำนวณไม่ได้ · ไม่มีเลขภาษี/สาขา/WHT/ยอดค้าง/สกุลเงิน · หน้าผู้ติดต่อและสินค้า
/// ไม่มีปุ่มส่งออกเลย</para>
///
/// <para>═══ หลัก ═══
/// (1) แถวมาจาก <b>query เจ้าของเดียวกับหน้าจอ</b> (ผู้เรียกวนทุกหน้าผ่าน <see cref="CollectAllAsync{T}"/> — ไม่เขียนตัวกรองซ้ำ) ·
/// (2) ตัวเลขเป็นตัวเลขจริง (ปัด 2 ตำแหน่ง AwayFromZero · ไม่มีตัวคั่นหลักพัน) · (3) ข้อความทุกช่องผ่าน <see cref="CsvFieldSafety"/> ·
/// ตัวเลขติดลบ<b>ไม่</b>ถูกเติม <c>'</c> (ไม่ใช่สูตร — นำเข้ากลับได้ตรง) · (4) CSV = UTF-8 มี BOM + CRLF (Excel ภาษาไทยไม่เพี้ยน) ·
/// (5) วันที่ = <c>yyyy-MM-dd</c> ค.ศ. ตามปฏิทินไทย (ไฟล์ทำงาน/นำเข้ากลับ — ไม่ใช่แบบยื่นทางการ ซึ่งใช้ พ.ศ. ที่ทีม X1)</para>
///
/// <para>═══ ตัวเขียนไฟล์ ═══ ไฟล์ทุกไบต์เขียนโดย <see cref="TabularExport"/> (ตัวกลางของทีม X3) ผ่าน <see cref="Write"/> —
/// ไฟล์นี้เป็นเจ้าของแค่ "ชุดคอลัมน์ + การแปลง DTO เป็นแถว" ของโดเมนเอกสาร/ผู้ติดต่อ/สินค้า</para>
/// </summary>
public static class TradeListExport
{
    /// <summary>หนึ่งคอลัมน์ของไฟล์ — หัวคอลัมน์ไทย + ชนิดช่องของตัวเขียนกลาง (<see cref="TabularCellKind"/>)</summary>
    public sealed record Column(string Header, TabularCellKind Kind = TabularCellKind.Text);

    /// <summary>เพดานแถวต่อไฟล์ (เกิน = ปฏิเสธพร้อมบอกให้กรองแคบลง — ห้ามตัดเงียบ)</summary>
    public const int MaxDocumentRows = 50_000;
    public const int MaxLineRows = 200_000;
    public const int MaxMasterRows = 100_000;

    /// <summary>ขนาดหน้าที่ใช้วนดึงจาก query เจ้าของ (เพดานของ <c>PagedRequest</c>)</summary>
    public const int FetchPageSize = 200;

    // ═══════════════════════ รูปแบบไฟล์ ═══════════════════════

    /// <summary>"csv" | "xlsx" | "excel" (ไม่สนตัวพิมพ์ · ว่าง = xlsx) — ค่าอื่นโยน <see cref="BusinessRuleException"/> ข้อความไทย
    /// (ตัวแปลงจริงคือ <see cref="TabularExport.TryParseFormat"/>)</summary>
    public static TabularFormat NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return TabularFormat.Xlsx;
        if (TabularExport.TryParseFormat(format, out var f)) return f;
        throw new BusinessRuleException($"รูปแบบไฟล์ \"{format}\" ไม่รองรับ — ใช้ csv หรือ xlsx");
    }

    /// <summary>
    /// คอลัมน์ + แถว (ค่าเรียงตามคอลัมน์) → ไฟล์ — <b>ส่งต่อให้ตัวเขียนกลาง <see cref="TabularExport"/> (ทีม X3) ทั้งหมด</b>
    /// (BOM · CRLF · กันสูตรเฉพาะข้อความ · เงินปัด AwayFromZero · วันที่เป็นช่องวันที่ใน Excel) · ที่นี่แปลงแค่ <c>bool</c> เป็น "ใช่/ไม่ใช่"
    /// </summary>
    public static TabularFile Write(TabularFormat format, IReadOnlyList<Column> columns, IEnumerable<IReadOnlyList<object?>> rows,
        string baseName, string sheetName = "ข้อมูล")
    {
        var cols = columns.Select((c, i) => new TabularColumn<IReadOnlyList<object?>>(c.Header,
            r => i < r.Count ? (r[i] is bool b ? (b ? "ใช่" : "ไม่ใช่") : r[i]) : null, c.Kind)).ToList();
        return TabularExport.Render(format, baseName, null, null, TabularExport.Sheet(sheetName, cols, rows));
    }

    /// <summary>เงิน 2 ตำแหน่ง ปัด AwayFromZero (กฎเหล็ก #4 E) — ใช้กับค่าที่ต้องคำนวณก่อนส่ง (รับ/จ่ายสุทธิ)</summary>
    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // ═══════════════════════ วนดึงทุกหน้าจาก query เจ้าของ ═══════════════════════

    /// <summary>
    /// ดึง<b>ทุกหน้า</b>ของ query เจ้าของ (ตัวเดียวกับที่หน้าจอเรียก) ทีละ <see cref="FetchPageSize"/> —
    /// ยอดรวมเกิน <paramref name="maxRows"/> ⇒ โยน <see cref="BusinessRuleException"/> ก่อนดึง (ห้ามตัดไฟล์เงียบ ๆ)
    /// </summary>
    public static async Task<List<T>> CollectAllAsync<T>(Func<PagedRequest, Task<PagedResponse<T>>> fetchPage,
        Func<int, PagedRequest> requestForPage, int maxRows)
    {
        var first = await fetchPage(requestForPage(1));
        if (first.TotalCount > maxRows)
            throw new BusinessRuleException(
                $"ข้อมูลตามตัวกรองมี {first.TotalCount:N0} รายการ เกินเพดานไฟล์ละ {maxRows:N0} รายการ — กรุณากรองช่วงวันที่/ประเภทให้แคบลงแล้วส่งออกเป็นหลายไฟล์",
                "EXPORT-TOO-LARGE");
        var all = new List<T>(Math.Max(first.Items.Count, 0));
        all.AddRange(first.Items);
        for (var p = 2; p <= first.TotalPages; p++)
        {
            var next = await fetchPage(requestForPage(p));
            if (next.Items.Count == 0) break;
            all.AddRange(next.Items);
        }
        return all;
    }

    // ═══════════════════════ ฝั่งของเอกสาร (รายรับ/รายจ่าย) ═══════════════════════

    /// <summary>"revenue" | "expense" | null (ทุกฝั่ง) — ค่าอื่นโยนข้อความไทย</summary>
    public static string? NormalizeSide(string? side)
    {
        var s = (side ?? "").Trim().ToLowerInvariant();
        if (s == "") return null;
        if (s is "revenue" or "sales") return "revenue";
        if (s is "expense" or "purchase") return "expense";
        throw new BusinessRuleException($"ฝั่งเอกสาร \"{side}\" ไม่ถูกต้อง — ใช้ revenue หรือ expense");
    }

    /// <summary>ชนิดเอกสารที่ต้องขอจาก query เมื่อเลือกฝั่ง — ชนิดของฝั่งนั้น + ชนิดสองฝั่งเสมอ (สร้างจาก
    /// <see cref="DocumentSide.BuildSideMap"/> ตัวเดียว · ตรงกับ <c>Layout.docTypesForSide</c> ของหน้าเอกสาร)</summary>
    public static List<DocumentType> TypesForSide(string side)
    {
        var want = side == "revenue" ? "Sales" : "Purchase";
        return DocumentSide.BuildSideMap()
            .Where(kv => kv.Value == want || kv.Value == "Both")
            .Select(kv => Enum.Parse<DocumentType>(kv.Key))
            .OrderBy(t => (int)t)
            .ToList();
    }

    /// <summary>เอกสารใบนี้อยู่ฝั่ง <paramref name="side"/> ไหม — กติกาเดียวกับ <c>Layout.docSide</c> บนหน้าเอกสาร:
    /// ชนิดไม่กำกวมตอบจากชนิด (<see cref="DocumentSide.IsSales"/>) · ชนิดสองฝั่งดู <c>CnDnPurchaseSideOverride</c> ก่อน ·
    /// ไม่รู้ = ฝั่งซื้อ (default ที่ปลอดภัยของ DocumentSide)</summary>
    public static bool IsOnSide(DocumentType type, bool? purchaseSideOverride, string side)
    {
        bool sales;
        if (DocumentSide.IsAmbiguous(type) && purchaseSideOverride.HasValue) sales = !purchaseSideOverride.Value;
        else sales = DocumentSide.IsSales(type);
        return side == "revenue" ? sales : !sales;
    }

    // ═══════════════════════ รายการเอกสาร (หัวเอกสาร 1 แถว/ใบ) ═══════════════════════

    /// <summary>ข้อมูลประกอบที่ list DTO ไม่มี — ผู้เรียกโหลดแบบ batch (ผู้สร้าง · เลขที่ใบต้นทาง)</summary>
    public sealed record DocumentExtras(string? CreatorName, string? SourceDocumentNumber);

    public static readonly IReadOnlyList<Column> DocumentColumns = new List<Column>
    {
        new("เลขที่"), new("ชนิดเอกสาร (รหัส)"), new("หัวเอกสาร"), new("วันที่", TabularCellKind.Date), new("ครบกำหนด", TabularCellKind.Date),
        new("ผู้ติดต่อ"), new("เลขประจำตัวผู้เสียภาษีผู้ติดต่อ"), new("สาขาผู้ติดต่อ"),
        new("สาขาผู้ออกเอกสาร"), new("เลขที่ใบกำกับของผู้ขาย"), new("วันที่ใบกำกับของผู้ขาย", TabularCellKind.Date), new("สาขาผู้ขายตามใบกำกับ"),
        new("อ้างอิง"), new("เอกสารต้นทาง"), new("สกุลเงิน"), new("อัตราแลกเปลี่ยน", TabularCellKind.Number), new("ราคารวม VAT"),
        new("ยอดก่อน VAT (หลังส่วนลด)", TabularCellKind.Money), new("ส่วนลดรายบรรทัด", TabularCellKind.Money), new("ส่วนลดท้ายบิล", TabularCellKind.Money),
        new("VAT", TabularCellKind.Money), new("หัก ณ ที่จ่าย", TabularCellKind.Money), new("ยอดรวม", TabularCellKind.Money),
        new("หักมัดจำ", TabularCellKind.Money), new("รับ/จ่ายสุทธิ", TabularCellKind.Money),
        new("ชำระแล้ว", TabularCellKind.Money), new("ยอดค้าง", TabularCellKind.Money),
        new("สถานะ (รหัส)"), new("สถานะงาน"), new("โครงการ"), new("ผู้สร้าง"), new("สร้างเมื่อ", TabularCellKind.Timestamp), new("หมายเหตุการซ่อน"),
        new("รหัสระบบ"),
    };

    /// <summary>หนึ่งแถวของรายการเอกสาร — ค่าทุกช่องมาจาก DTO ของเซิร์ฟเวอร์ (ไม่คำนวณใหม่) ยกเว้น "รับ/จ่ายสุทธิ" =
    /// ยอดรวม − หักมัดจำ (ตัวเดียวกับบรรทัดเล็กบนหน้าเอกสาร) · ยอดค้างของชนิดที่ไม่ใช่หนี้ (<c>BalanceDueApplies=false</c>) = ว่าง
    /// เหมือนหน้าจอแสดง "—"</summary>
    public static object?[] DocumentRow(DocumentResponse d, DocumentExtras? extras = null)
    {
        var deposit = d.DepositAppliedAmount;
        var balance = d.BalanceDueApplies == false ? (object?)null : d.BalanceDue;
        return new object?[]
        {
            d.DocumentNumber,
            d.DocumentType.ToString(),
            string.IsNullOrWhiteSpace(d.DocumentTitle) ? DocumentTypeNames.Title(d.DocumentType, "th") : d.DocumentTitle,
            d.DocumentDate,
            d.DueDate,
            d.Contact?.Name ?? "",
            d.Contact?.TaxId ?? "",
            d.Contact?.BranchCode ?? "",
            d.IssuerBranchCode ?? "",
            d.SupplierInvoiceNumber ?? "",
            d.SupplierTaxInvoiceDate,
            d.SupplierBranchCode ?? "",
            d.Reference ?? "",
            extras?.SourceDocumentNumber ?? "",
            string.IsNullOrWhiteSpace(d.Currency) ? "THB" : d.Currency,
            d.ExchangeRate,
            d.PricesIncludeVat,
            d.SubTotal,
            d.DiscountAmount,
            d.BillDiscountAmount,
            d.VatAmount,
            d.WithholdingTaxAmount,
            d.TotalAmount,
            deposit,
            Money(d.TotalAmount) - Money(deposit),
            d.PaidAmount,
            balance,
            d.Status.ToString(),
            d.LifecycleReason ?? "",
            d.ProjectCode ?? d.ProjectName ?? "",
            extras?.CreatorName ?? "",
            d.CreatedAt,
            d.IsRedacted ? (d.RedactedReason ?? "ซ่อนตามชั้นความลับ") : "",
            d.Id.ToString(),
        };
    }

    // ═══════════════════════ รายการบรรทัดเอกสาร (1 แถว/บรรทัด) ═══════════════════════

    public static readonly IReadOnlyList<Column> DocumentLineColumns = new List<Column>
    {
        new("เลขที่"), new("ชนิดเอกสาร (รหัส)"), new("วันที่", TabularCellKind.Date), new("ผู้ติดต่อ"), new("เลขประจำตัวผู้เสียภาษีผู้ติดต่อ"),
        new("สถานะ (รหัส)"), new("สกุลเงิน"), new("ราคารวม VAT"), new("ลำดับ", TabularCellKind.Integer), new("รหัสสินค้า"), new("รายการ"),
        new("จำนวน", TabularCellKind.Number), new("หน่วย"), new("ราคาต่อหน่วย", TabularCellKind.Money), new("ส่วนลด %", TabularCellKind.Number),
        new("ส่วนลด", TabularCellKind.Money), new("ยอดบรรทัด", TabularCellKind.Money), new("อัตรา VAT %", TabularCellKind.Number), new("VAT", TabularCellKind.Money),
        new("อัตราหัก ณ ที่จ่าย %", TabularCellKind.Number), new("หัก ณ ที่จ่าย", TabularCellKind.Money), new("ประเภทเงินได้"),
        new("รหัสบัญชี"), new("ชื่อบัญชี"), new("โครงการ"), new("เคลมภาษีซื้อได้"), new("เหตุผลเคลมไม่ได้"),
        new("หมายเหตุการซ่อน"), new("รหัสระบบเอกสาร"),
    };

    /// <summary>แถวบรรทัดของเอกสารหนึ่งใบ — ใบที่ถูกซ่อน (ชั้นความลับ) ได้ 1 แถวที่บอกเหตุผล (ไม่หายเงียบจากไฟล์) ·
    /// <paramref name="accounts"/> = รหัส/ชื่อบัญชีของ AccountId (list DTO ไม่ include บัญชี — ผู้เรียกโหลด batch)</summary>
    public static IEnumerable<object?[]> DocumentLineRows(DocumentResponse d,
        IReadOnlyDictionary<Guid, (string Code, string Name)>? accounts = null)
    {
        object?[] Head(object? order) => new object?[]
        {
            d.DocumentNumber, d.DocumentType.ToString(), d.DocumentDate, d.Contact?.Name ?? "", d.Contact?.TaxId ?? "",
            d.Status.ToString(), string.IsNullOrWhiteSpace(d.Currency) ? "THB" : d.Currency, d.PricesIncludeVat, order,
        };
        if (d.IsRedacted || d.Lines == null || d.Lines.Count == 0)
        {
            var head = Head(null);
            var blank = new object?[DocumentLineColumns.Count - head.Length - 2];
            yield return head.Concat(blank)
                .Append(d.IsRedacted ? (d.RedactedReason ?? "ซ่อนตามชั้นความลับ") : "(เอกสารไม่มีบรรทัดรายการ)")
                .Append(d.Id.ToString()).ToArray();
            yield break;
        }
        foreach (var l in d.Lines.OrderBy(x => x.LineOrder))
        {
            var code = l.AccountCode;
            var name = l.AccountName;
            if (string.IsNullOrEmpty(code) && l.AccountId.HasValue && accounts != null
                && accounts.TryGetValue(l.AccountId.Value, out var acc))
            { code = acc.Code; name = acc.Name; }
            yield return Head(l.LineOrder).Concat(new object?[]
            {
                l.ProductCode ?? "", l.Description ?? "", l.Quantity, l.Unit ?? "", l.UnitPrice, l.DiscountPercent,
                l.DiscountAmount, l.Amount, l.VatRate, l.VatAmount, l.WithholdingTaxRate, l.WithholdingTaxAmount,
                l.IncomeTypeCode ?? "", code ?? "", name ?? "", l.ProjectCode ?? l.ProjectName ?? "",
                l.IsVatClaimable, l.VatNonClaimableReason ?? "", "", d.Id.ToString(),
            }).ToArray();
        }
    }

    // ═══════════════════════ ผู้ติดต่อ ═══════════════════════

    public static readonly IReadOnlyList<Column> ContactColumns = new List<Column>
    {
        new("ชื่อ"), new("ชื่อภาษาอังกฤษ"), new("เลขประจำตัวผู้เสียภาษี"), new("รหัสสาขา"), new("ชื่อสาขา"), new("ชนิดผู้ติดต่อ (รหัส)"),
        new("ลูกค้า"), new("ผู้จำหน่าย"), new("ที่อยู่"), new("จังหวัด"), new("รหัสไปรษณีย์"), new("ประเทศ"), new("โทรศัพท์"), new("อีเมล"),
        new("ผู้ติดต่อ"), new("เครดิต (วัน)", TabularCellKind.Integer), new("วงเงินเครดิต", TabularCellKind.Money),
        new("บัญชีลูกหนี้"), new("บัญชีเจ้าหนี้"), new("สถานะ"), new("รหัสระบบ"),
    };

    public static object?[] ContactRow(ContactResponse c) => new object?[]
    {
        c.Name, c.NameEn ?? "", c.TaxId ?? "", c.BranchCode ?? "", c.BranchName ?? c.BranchLabel ?? "", c.ContactType.ToString(),
        c.IsCustomer, c.IsSupplier, c.Address ?? "", c.Province ?? "", c.PostalCode ?? "", c.CountryCode ?? "",
        c.Phone ?? "", c.Email ?? "", c.ContactPerson ?? "", c.PaymentDueDays, c.CreditLimit,
        c.DefaultArAccountCode ?? "", c.DefaultApAccountCode ?? "", c.IsActive ? "ใช้งาน" : "ปิด", c.Id.ToString(),
    };

    // ═══════════════════════ สินค้า/บริการ ═══════════════════════

    public static readonly IReadOnlyList<Column> ProductColumns = new List<Column>
    {
        new("รหัส"), new("ชื่อ"), new("ชื่อภาษาอังกฤษ"), new("ประเภท (รหัส)"), new("SKU"), new("บาร์โค้ด"), new("หมวด"), new("หน่วย"),
        new("ราคาขาย", TabularCellKind.Money), new("ต้นทุน", TabularCellKind.Money), new("อัตรา VAT %", TabularCellKind.Number), new("ราคารวม VAT"),
        new("คงเหลือ", TabularCellKind.Number), new("จุดสั่งซื้อขั้นต่ำ", TabularCellKind.Number), new("ติดตามสต็อก"), new("วิธีคิดต้นทุน"),
        new("บัญชีขาย"), new("บัญชีซื้อ"), new("บัญชีสินค้าคงเหลือ"), new("สถานะ"), new("รหัสระบบ"),
    };

    public static object?[] ProductRow(ProductResponse p) => new object?[]
    {
        p.Code, p.Name, p.NameEn ?? "", p.ProductType.ToString(), p.SKU ?? "", p.Barcode ?? "", p.Category ?? "", p.Unit,
        p.SellingPrice, p.CostPrice, p.VatRate, p.IsVatIncluded, p.CurrentStock, p.MinimumStock, p.TrackStock,
        p.CostingMethod.ToString(), p.SalesAccountName ?? "", p.PurchaseAccountName ?? "", p.InventoryAccountName ?? "",
        p.IsActive ? "ใช้งาน" : "ปิด", p.Id.ToString(),
    };

    // ═══════════════════════ ตารางสตริง (ศูนย์นำเข้า/ส่งออก) ═══════════════════════

    /// <summary>
    /// ตารางที่ทุกค่าเป็นสตริง (ไฟล์ส่งออกของศูนย์นำเข้า/ส่งออก — หัวคอลัมน์ = ชื่อช่องของเทมเพลตนำเข้า เพื่อนำเข้ากลับได้) → คอลัมน์ + แถว
    /// <para>คอลัมน์เป็น "ตัวเลข" เมื่อ<b>ทุกค่า</b>ที่ไม่ว่างเป็นเลขทศนิยม invariant <b>ที่มีจุดทศนิยม</b> (รูปแบบ F2/F4 ของตัวส่งออก) —
    /// เลขภาษี/โทรศัพท์/รหัสไม่มีจุด ⇒ คงเป็นข้อความ (เลขขึ้นต้น 0 / 13 หลักไม่ถูก Excel แปลง)</para>
    /// </summary>
    public static (List<Column> Columns, List<object?[]> Rows) FromStringTable(List<Dictionary<string, string>> data)
    {
        if (data.Count == 0) return (new List<Column>(), new List<object?[]>());
        var headers = data[0].Keys.ToList();
        var numeric = headers.Select(h =>
        {
            var any = false;
            foreach (var row in data)
            {
                var v = row.GetValueOrDefault(h);
                if (string.IsNullOrEmpty(v)) continue;
                if (!IsDecimalLiteral(v)) return false;
                any = true;
            }
            return any;
        }).ToList();
        var cols = headers.Select((h, i) => new Column(h, numeric[i] ? TabularCellKind.Number : TabularCellKind.Text)).ToList();
        var rows = data.Select(r => headers.Select((h, i) =>
        {
            var v = r.GetValueOrDefault(h) ?? "";
            if (numeric[i] && v.Length > 0) return (object?)decimal.Parse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            return v;
        }).ToArray()).ToList();
        return (cols, rows);
    }

    /// <summary>เลขทศนิยมแบบ invariant ที่มีจุด เช่น <c>-1500.00</c> · <c>0.5000</c> (ไม่รับตัวคั่นหลักพัน/ช่องว่าง/เลขไม่มีจุด)</summary>
    internal static bool IsDecimalLiteral(string v)
    {
        var i = 0;
        if (v.Length > 0 && v[0] == '-') i = 1;
        var digitsBefore = 0; var dot = false; var digitsAfter = 0;
        for (; i < v.Length; i++)
        {
            var ch = v[i];
            if (ch >= '0' && ch <= '9') { if (dot) digitsAfter++; else digitsBefore++; }
            else if (ch == '.' && !dot) dot = true;
            else return false;
        }
        return dot && digitsBefore > 0 && digitsAfter > 0;
    }
}
