namespace Accounting.Helpers;

/// <summary>
/// **คำพ้องของหัวคอลัมน์ → field ปลายทาง — คลังเดียวของทั้งเรพ** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.3)
///
/// <para>รวมสี่สำเนาเดิมที่ต่างคนต่างเขียน: <c>ImportExportService.GetFieldAliases</c> · schema ส่วนตัวใน
/// <c>AiSuggestionController.SuggestImportMapping</c> · หัวคอลัมน์ที่ adapter โปรแกรมอื่นอ่าน (<c>Migration/CompetitorImportFramework</c> ·
/// <c>AccountImportAdapter</c> · <c>ProductImportAdapter</c> — Express/PEAK/FlowAccount) · รูปแบบหัวคอลัมน์ของ <c>BankCsvParser.DetectColumns</c> ·
/// เพิ่มหัวที่พบบ่อยในไฟล์ส่งออกของโปรแกรมบัญชีไทย (ไทย + อังกฤษ + camelCase)</para>
///
/// <para>ผู้ใช้: <see cref="ImportColumnMapper"/> ชั้น "📚 คำพ้อง" (เทียบหลัง <see cref="ImportHeaderSignature.Normalize"/>) · หัวที่ไม่อยู่ที่นี่ =
/// ชั้นเรียนรู้ (แม่แบบ/คู่ที่ผู้ใช้ยืนยัน) → รูปข้อมูล → AI → ผู้ใช้ · <b>เพิ่มคำพ้อง = แก้ที่นี่ที่เดียว</b></para>
///
/// <para>⚠️ หัวของโปรแกรมอื่นที่นี่ส่วนใหญ่ <b>เดาจาก adapter เดิม + ความรู้ทั่วไป ยังไม่ได้ยืนยันกับไฟล์ส่งออกจริง</b> (IMPORT_ETL_REVIEW §4 ข้อ 1) —
/// โปรไฟล์ที่ติดป้ายยืนยัน/เดาอยู่ที่ <see cref="ImportSourceSeed"/></para>
/// </summary>
public static class ImportFieldSynonymSeed
{
    /// <summary>คำพ้องที่ใช้ได้กับทุกชนิดข้อมูล (field ชื่อเดียวกันความหมายเดียวกัน)</summary>
    private static readonly Dictionary<string, string[]> Global = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = new[] { "ชื่อ", "name", "ชื่อเต็ม", "fullname", "full name" },
        ["Code"] = new[] { "รหัส", "code" },
        ["Description"] = new[] { "รายละเอียด", "คำอธิบาย", "description", "desc", "memo" },
        ["Notes"] = new[] { "หมายเหตุ", "notes", "note", "remark", "remarks" },
        ["Reference"] = new[] { "อ้างอิง", "เลขที่อ้างอิง", "เลขอ้างอิง", "reference", "ref", "ref_no", "reference_no", "ref_number", "refno" },
        ["Email"] = new[] { "อีเมล", "อีเมล์", "อีเมลล์", "email", "e-mail", "e_mail", "mail", "email_address" },
        ["Phone"] = new[] { "โทรศัพท์", "เบอร์โทร", "เบอร์โทรศัพท์", "โทร", "มือถือ", "เบอร์มือถือ", "เบอร์ติดต่อ", "tel", "telephone", "phone",
            "phone_number", "mobile", "contact_number" },
        ["Amount"] = new[] { "จำนวนเงิน", "ยอดเงิน", "amount", "value", "มูลค่า" },
        ["Category"] = new[] { "หมวดหมู่", "หมวด", "กลุ่ม", "category", "group" },
        ["Status"] = new[] { "สถานะ", "status" },
    };

    private static readonly string[] TaxIdWords =
    {
        "เลขประจำตัวผู้เสียภาษี", "เลขประจำตัวผู้เสียภาษีอากร", "เลขผู้เสียภาษี", "เลขที่ผู้เสียภาษี", "เลขภาษี", "เลขประจำตัว",
        "เลขทะเบียนนิติบุคคล", "เลขทะเบียน 13 หลัก", "เลขทะเบียน13หลัก", "tax_id", "taxid", "tax id", "tin", "taxnumber", "tax number", "tax_number",
        "vat_id", "vat number", "vat no", "tax_identification", "tax registration no",
    };

    private static readonly string[] ProductCodeWords =
        { "รหัสสินค้า", "รหัสสินค้า/บริการ", "product_code", "productcode", "product code", "item_code", "itemcode", "item code", "sku", "item_no", "part_number" };

    private static readonly string[] AccountCodeWords =
        { "รหัสบัญชี", "เลขที่บัญชี", "รหัสผังบัญชี", "account_code", "accountcode", "account code", "acc_code", "acc code", "gl_code", "gl code",
          "account_no", "account_number", "account no" };

    private static readonly string[] CounterpartyNameWords =
        { "ชื่อลูกค้า", "ชื่อผู้ขาย", "ชื่อผู้จำหน่าย", "ชื่อผู้จัดจำหน่าย", "ชื่อเจ้าหนี้", "ชื่อลูกหนี้", "ชื่อผู้ติดต่อ", "ลูกค้า", "ผู้ขาย", "เจ้าหนี้", "ลูกหนี้",
          "ลูกค้า/ผู้ขาย", "customer", "vendor", "supplier", "contact", "contact name", "contactname", "customer name", "supplier name", "partner" };

    /// <summary>คำพ้องเฉพาะชนิดข้อมูล — คีย์ = <see cref="ImportExportPermissionScope.NormalizeEntityType"/></summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> ByEntity = new(StringComparer.Ordinal)
    {
        ["contacts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            // Express: ชื่อลูกค้า/ชื่อผู้จัดจำหน่าย · PEAK: contactName · FlowAccount: name (TH)
            ["Name"] = new[] { "ชื่อลูกค้า", "ชื่อผู้ขาย", "ชื่อผู้จำหน่าย", "ชื่อผู้จัดจำหน่าย", "ชื่อบริษัท", "ชื่อกิจการ", "ชื่อร้าน", "ชื่อนิติบุคคล",
                "ชื่อผู้ติดต่อ", "ชื่อภาษาไทย", "ลูกค้า", "ผู้ขาย", "customer", "vendor", "supplier", "customer_name", "supplier_name", "vendor_name",
                "company_name", "contact_name", "contactname", "contact name", "name (th)", "name(th)", "company name" },
            ["TaxId"] = TaxIdWords.Concat(new[] { "เลขบัตรประชาชน", "เลขประจำตัวประชาชน" }).ToArray(),
            ["BranchCode"] = new[] { "สาขา", "รหัสสาขา", "เลขที่สาขา", "สาขาที่", "branch", "branchcode", "branch code", "branch no", "branch_no", "branch number" },
            ["ContactType"] = new[] { "ประเภทผู้ติดต่อ", "ประเภทบุคคล", "บุคคล/นิติบุคคล", "ประเภทนิติบุคคล", "contacttype", "contact type", "entity type",
                "person type", "ประเภท", "type" },
            ["IsCustomer"] = new[] { "เป็นลูกค้า", "ลูกค้า", "is_customer", "iscustomer", "is customer", "customer_flag", "customer flag", "customer" },
            ["IsSupplier"] = new[] { "เป็นผู้ขาย", "เป็นผู้จำหน่าย", "ผู้ขาย", "ผู้จำหน่าย", "is_supplier", "issupplier", "is supplier", "supplier_flag",
                "vendor_flag", "supplier", "vendor" },
            ["Address"] = new[] { "ที่อยู่", "ที่อยู่1", "ที่อยู่ 1", "ที่อยู่ติดต่อ", "ที่อยู่ออกใบกำกับ", "ที่อยู่จดทะเบียน", "ที่ตั้ง", "address", "addr",
                "address1", "address 1", "street", "billing address", "location" },
            ["ContactPerson"] = new[] { "ผู้ติดต่อ", "บุคคลติดต่อ", "ชื่อผู้ประสานงาน", "ผู้ประสานงาน", "contact_person", "contactperson", "contact person",
                "attn", "attention", "representative", "person" },
        },
        ["products"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = ProductCodeWords,
            ["Name"] = new[] { "ชื่อสินค้า", "ชื่อสินค้า/บริการ", "ชื่อบริการ", "รายการสินค้า", "product_name", "productname", "product name", "item_name",
                "item name", "product" },
            ["ProductType"] = new[] { "ประเภทสินค้า", "ประเภท", "ชนิด", "ชนิดสินค้า", "product_type", "producttype", "product type", "item_type", "type" },
            ["Unit"] = new[] { "หน่วย", "หน่วยนับ", "uom", "unit_of_measure", "unit", "หน่วยขาย" },
            ["SellingPrice"] = new[] { "ราคาขาย", "ราคาขายต่อหน่วย", "ราคาขาย/หน่วย", "ราคา", "ราคาต่อหน่วย", "selling_price", "sellingprice", "selling price",
                "sell_price", "price", "unit_price", "retail_price", "sale price" },
            ["CostPrice"] = new[] { "ราคาทุน", "ต้นทุน", "ต้นทุนต่อหน่วย", "ราคาซื้อ", "cost_price", "costprice", "cost price", "cost", "purchase_price",
                "buy_price", "purchase price" },
            ["VatRate"] = new[] { "อัตราภาษี", "vat", "vat%", "vat %", "ภาษี", "ภาษีมูลค่าเพิ่ม", "vat_rate", "vatrate", "vat rate", "tax_rate", "tax_percent" },
            ["Category"] = new[] { "หมวดหมู่", "หมวด", "หมวดสินค้า", "กลุ่มสินค้า", "กลุ่ม", "category", "product_group", "item_group", "group" },
        },
        ["chartofaccounts"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AccountCode"] = AccountCodeWords.Concat(new[] { "รหัส", "code" }).ToArray(),
            ["AccountName"] = new[] { "ชื่อบัญชี", "ชื่อบัญชีภาษาไทย", "ชื่อบัญชี (ไทย)", "account_name", "accountname", "account name", "acc_name", "gl_name",
                "account_description", "name", "ชื่อ" },
            ["AccountNameEn"] = new[] { "ชื่อบัญชีอังกฤษ", "ชื่อบัญชีภาษาอังกฤษ", "ชื่อบัญชี (อังกฤษ)", "ชื่ออังกฤษ", "account_name_en", "accountnameen",
                "account name en", "english_name", "name_en", "nameen", "eng_name", "name (en)" },
            ["AccountType"] = new[] { "ประเภทบัญชี", "หมวดบัญชี", "ประเภท", "account_type", "accounttype", "account type", "acc_type", "gl_type",
                "account_category", "type" },
            ["Description"] = new[] { "หมายเหตุ", "คำอธิบาย", "รายละเอียด", "description", "desc", "note", "remark", "memo" },
        },
        ["journalentries"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = new[] { "วันที่", "วันที่เอกสาร", "วันที่บันทึก", "วันที่ลงบัญชี", "date", "entry date", "entrydate", "posting_date", "posting date",
                "document date", "txn date" },
            ["Description"] = new[] { "คำอธิบาย", "รายละเอียด", "คำอธิบายรายการ", "description", "memo", "narrative", "particulars" },
            ["AccountCode"] = AccountCodeWords.Concat(new[] { "account" }).ToArray(),
            ["DebitAmount"] = new[] { "เดบิต", "เดบิท", "ยอดเดบิต", "debit", "dr", "debit amount", "debitamount" },
            ["CreditAmount"] = new[] { "เครดิต", "ยอดเครดิต", "credit", "cr", "credit amount", "creditamount" },
            ["Reference"] = new[] { "เลขที่อ้างอิง", "อ้างอิง", "เลขที่เอกสาร", "เลขที่ใบสำคัญ", "ref", "ref no", "reference", "voucher no", "document no", "jv no" },
            ["ContactName"] = CounterpartyNameWords,
        },
        ["openingbalances"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AccountCode"] = AccountCodeWords.Concat(new[] { "รหัส", "code" }).ToArray(),
            ["AccountName"] = new[] { "ชื่อบัญชี", "account name", "accountname", "account_name", "name", "ชื่อ" },
            ["Debit"] = new[] { "เดบิต", "เดบิท", "ยอดเดบิต", "ยอดยกมาเดบิต", "debit", "dr", "debit balance" },
            ["Credit"] = new[] { "เครดิต", "ยอดเครดิต", "ยอดยกมาเครดิต", "credit", "cr", "credit balance" },
            ["Balance"] = new[] { "ยอดคงเหลือ", "ยอดยกมา", "คงเหลือ", "ยอดสุทธิ", "balance", "closing balance", "ending balance", "net balance" },
            ["AsOfDate"] = new[] { "ณ วันที่", "วันที่", "วันยกมา", "as of", "as of date", "asofdate", "date" },
        },
        ["openingar"] = OpeningArAp(),
        ["openingap"] = OpeningArAp(),
        ["stockopening"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ProductCode"] = ProductCodeWords.Concat(new[] { "รหัส", "code" }).ToArray(),
            ["Quantity"] = new[] { "จำนวน", "ยอดยกมา", "คงเหลือ", "จำนวนคงเหลือ", "ยอดคงเหลือ", "qty", "quantity", "on hand", "onhand", "stock", "คงคลัง" },
            ["UnitCost"] = new[] { "ต้นทุนต่อหน่วย", "ต้นทุน/หน่วย", "ราคาทุน", "ต้นทุน", "unit cost", "unitcost", "cost" },
            ["OpeningDate"] = new[] { "วันยกมา", "วันที่", "ณ วันที่", "as of", "date", "opening date" },
            ["Notes"] = new[] { "หมายเหตุ", "notes", "remark" },
        },
        ["stockadjustments"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ProductCode"] = ProductCodeWords.Concat(new[] { "รหัส", "code" }).ToArray(),
            ["AdjustmentDate"] = new[] { "วันที่ปรับ", "วันที่", "date", "adjustment date" },
            ["MovementType"] = new[] { "ประเภท", "ประเภทการเคลื่อนไหว", "รับ/จ่าย", "movement type", "movementtype", "type", "in/out" },
            ["Quantity"] = new[] { "จำนวน", "จำนวนที่ปรับ", "qty", "quantity" },
            ["UnitCost"] = new[] { "ต้นทุนต่อหน่วย", "ต้นทุน/หน่วย", "ราคาทุน", "ต้นทุน", "unit cost", "cost" },
            ["Reference"] = new[] { "เลขที่อ้างอิง", "เลขเอกสารอ้างอิง", "อ้างอิง", "reference", "ref" },
            ["Notes"] = new[] { "หมายเหตุ", "เหตุผล", "notes", "reason", "remark" },
        },
        ["fixedassets"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AssetCode"] = new[] { "รหัสสินทรัพย์", "รหัสทรัพย์สิน", "เลขที่สินทรัพย์", "เลขทะเบียนสินทรัพย์", "asset code", "assetcode", "asset no", "asset_no",
                "รหัส", "code" },
            ["Name"] = new[] { "ชื่อสินทรัพย์", "ชื่อทรัพย์สิน", "รายการ", "asset name", "assetname", "name", "ชื่อ", "description" },
            ["Category"] = new[] { "หมวดหมู่", "หมวดสินทรัพย์", "ประเภทสินทรัพย์", "category", "asset class", "asset category" },
            ["PurchaseDate"] = new[] { "วันที่ซื้อ", "วันที่ได้มา", "วันที่เริ่มใช้", "purchase date", "acquisition date", "date acquired" },
            ["PurchaseCost"] = new[] { "ราคาซื้อ", "ราคาทุน", "มูลค่าทุน", "ทุน", "cost", "purchase cost", "acquisition cost" },
            ["SalvageValue"] = new[] { "มูลค่าซาก", "ราคาซาก", "salvage value", "residual value", "scrap value" },
            ["UsefulLifeMonths"] = new[] { "อายุการใช้งาน (เดือน)", "อายุการใช้งาน", "อายุใช้งาน", "useful life", "useful life (months)", "life (months)",
                "useful life months" },
            ["DepreciationMethod"] = new[] { "วิธีคิดค่าเสื่อม", "วิธีคิดค่าเสื่อมราคา", "depreciation method", "method" },
            ["AccumulatedDepreciation"] = new[] { "ค่าเสื่อมสะสม", "ค่าเสื่อมราคาสะสม", "ค่าเสื่อมสะสมยกมา", "accumulated depreciation", "acc dep", "accum dep" },
            ["Location"] = new[] { "ตำแหน่ง", "สถานที่", "ที่ตั้ง", "location" },
            ["SerialNumber"] = new[] { "หมายเลขเครื่อง", "serial no", "serial number", "s/n", "sn" },
        },
        ["documents"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DocumentNumber"] = new[] { "เลขที่เอกสาร", "เลขที่", "เลขที่ใบกำกับ", "เลขที่ใบกำกับภาษี", "เลขที่ใบแจ้งหนี้", "document no", "documentnumber",
                "doc no", "invoice no", "invoice number", "number" },
            ["DocumentType"] = new[] { "ประเภทเอกสาร", "ประเภท", "document type", "doc type", "type" },
            ["DocumentDate"] = new[] { "วันที่เอกสาร", "วันที่", "วันที่ออก", "document date", "invoice date", "date", "issue date" },
            ["DueDate"] = new[] { "วันครบกำหนด", "วันที่ครบกำหนด", "ครบกำหนด", "กำหนดชำระ", "due date", "due" },
            ["ContactName"] = CounterpartyNameWords,
            ["ContactTaxId"] = TaxIdWords,
            ["ContactBranchCode"] = new[] { "สาขา", "รหัสสาขา", "สาขาที่", "branch", "branch code" },
            ["PricesIncludeVat"] = new[] { "ราคารวมภาษี", "ราคารวม vat", "รวม vat", "vat inclusive", "price incl vat" },
            ["Reference"] = new[] { "อ้างอิง", "เลขที่อ้างอิง", "reference", "ref", "po no", "เลขที่ใบสั่งซื้อ" },
            ["Notes"] = new[] { "หมายเหตุ", "notes", "remark" },
            ["LineDescription"] = new[] { "รายการ", "รายละเอียด", "รายละเอียดสินค้า", "ชื่อสินค้า/บริการ", "ชื่อสินค้า", "description", "item",
                "item description" },
            ["LineQuantity"] = new[] { "จำนวน", "qty", "quantity" },
            ["LineUnit"] = new[] { "หน่วย", "หน่วยนับ", "unit", "uom" },
            ["LineUnitPrice"] = new[] { "ราคาต่อหน่วย", "ราคา/หน่วย", "ราคา", "unit price", "unitprice", "price" },
            ["LineDiscountPercent"] = new[] { "ส่วนลด%", "ส่วนลด (%)", "ส่วนลด", "discount %", "discount%", "discount" },
            ["LineVatRate"] = new[] { "vat%", "vat %", "vat", "อัตราภาษี", "ภาษีมูลค่าเพิ่ม (%)", "vat rate" },
            ["LineWhtRate"] = new[] { "หัก ณ ที่จ่าย", "หัก ณ ที่จ่าย %", "ภาษีหัก ณ ที่จ่าย", "wht", "wht %", "wht%", "withholding" },
            ["LineProductCode"] = ProductCodeWords,
            ["LineAccountCode"] = AccountCodeWords,
        },
        ["payments"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DocumentNumber"] = new[] { "เลขที่เอกสาร", "เลขที่ใบแจ้งหนี้", "เลขที่ใบกำกับ", "เลขที่บิล", "invoice no", "doc no", "document no", "bill no" },
            ["PaymentDate"] = new[] { "วันที่ชำระ", "วันที่รับชำระ", "วันที่จ่าย", "วันที่รับเงิน", "วันที่", "payment date", "paid date", "date" },
            ["Amount"] = new[] { "จำนวนเงิน", "ยอดชำระ", "ยอดรับชำระ", "จำนวนเงินที่ชำระ", "amount", "paid amount" },
            ["PaymentMethod"] = new[] { "วิธีชำระ", "วิธีการชำระเงิน", "ช่องทางชำระ", "ช่องทาง", "payment method", "method", "channel" },
            ["WithholdingTaxAmount"] = new[] { "ภาษีหัก ณ ที่จ่าย", "หัก ณ ที่จ่าย", "ภาษีถูกหัก", "wht", "wht amount", "withholding tax" },
            ["FeeAmount"] = new[] { "ค่าธรรมเนียม", "ค่าธรรมเนียมธนาคาร", "fee", "bank fee" },
            ["Reference"] = new[] { "เลขที่อ้างอิง", "อ้างอิง", "เลขที่เช็ค", "reference", "ref", "cheque no" },
            ["BankAccount"] = new[] { "บัญชีธนาคาร", "เลขที่บัญชี", "bank account", "bank" },
            ["Notes"] = new[] { "หมายเหตุ", "notes", "remark" },
        },
        // ทั้งแม่แบบเดิม (BankAccountId/TransactionType) และบรรทัด statement จริง (ImportSchemas.BankStatementLines — ทีม C) ·
        // รูปแบบหัวจาก BankCsvParser.DetectColumns (date/time/deposit/withdrawal/balance/amount/desc/channel/details/ref)
        ["banktransactions"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BankAccountId"] = new[] { "bank_account_id", "bank account id", "รหัสบัญชีธนาคาร" },
            ["TransactionDate"] = new[] { "วันที่", "วันที่ทำรายการ", "วันที่รายการ", "date", "posting date", "transaction date", "txn date", "value date",
                "วันที่มีผล" },
            ["TransactionTime"] = new[] { "เวลา", "time", "transaction time", "เวลาทำรายการ" },
            ["TransactionType"] = new[] { "ประเภทรายการ", "ประเภท", "transaction type", "txn type", "type" },
            ["Description"] = new[] { "รายการ", "คำอธิบาย", "description", "transaction type", "transaction" },
            ["Deposit"] = new[] { "ฝากเงิน", "ฝาก", "เงินเข้า", "ยอดเงินฝาก", "จำนวนเงินฝาก", "deposit", "deposits", "credit", "เครดิต", "money in", "cr" },
            ["Withdrawal"] = new[] { "ถอนเงิน", "ถอน", "เงินออก", "จำนวนเงินถอน", "withdrawal", "withdrawals", "withdraw", "debit", "เดบิต", "money out", "dr" },
            ["Balance"] = new[] { "ยอดคงเหลือ", "คงเหลือ", "ยอดเงินคงเหลือ", "balance", "ledger balance", "outstanding balance" },
            ["Amount"] = new[] { "จำนวนเงิน", "amount", "value", "มูลค่า" },
            ["Channel"] = new[] { "ช่องทาง", "ช่องทางการทำรายการ", "channel", "service" },
            ["Details"] = new[] { "รายละเอียด", "details", "remark", "memo", "notes" },
            ["Reference"] = new[] { "เลขอ้างอิง", "เลขที่อ้างอิง", "reference", "ref no", "ref id", "ref", "cheque no" },
        },
        ["projects"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = new[] { "รหัสโปรเจกต์", "รหัสโครงการ", "project code", "project no", "รหัส", "code" },
            ["Name"] = new[] { "ชื่อโปรเจกต์", "ชื่อโครงการ", "project name", "project", "ชื่อ", "name" },
            ["NameEn"] = new[] { "ชื่ออังกฤษ", "ชื่อภาษาอังกฤษ", "name en", "name (en)", "english name" },
            ["CustomerName"] = new[] { "ลูกค้า", "ชื่อลูกค้า", "ผู้ว่าจ้าง", "customer", "client" },
            ["ProjectManagerName"] = new[] { "ผู้จัดการโครงการ", "ผู้จัดการโปรเจกต์", "ผู้รับผิดชอบ", "pm", "project manager" },
            ["StartDate"] = new[] { "วันเริ่ม", "วันเริ่มต้น", "วันที่เริ่ม", "start date", "start" },
            ["EndDate"] = new[] { "วันสิ้นสุด", "วันที่สิ้นสุด", "end date", "end" },
            ["BudgetAmount"] = new[] { "งบประมาณ", "budget", "budget amount" },
            ["ContractAmount"] = new[] { "มูลค่าสัญญา", "มูลค่างาน", "contract amount", "contract value" },
            ["BillingMethod"] = new[] { "วิธีเรียกเก็บ", "วิธีวางบิล", "billing method" },
        },
        ["employees"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["EmployeeCode"] = new[] { "รหัสพนักงาน", "เลขที่พนักงาน", "employee code", "emp code", "employee id", "emp id", "รหัส" },
            ["TitleTh"] = new[] { "คำนำหน้า", "คำนำหน้าชื่อ", "title", "prefix" },
            ["FirstNameTh"] = new[] { "ชื่อ", "ชื่อจริง", "ชื่อพนักงาน", "first name", "firstname" },
            ["LastNameTh"] = new[] { "นามสกุล", "last name", "lastname", "surname" },
            ["FirstNameEn"] = new[] { "ชื่อภาษาอังกฤษ", "ชื่อ (อังกฤษ)", "first name en", "first name (en)" },
            ["LastNameEn"] = new[] { "นามสกุลภาษาอังกฤษ", "นามสกุล (อังกฤษ)", "last name en", "last name (en)" },
            ["CitizenId"] = new[] { "เลขบัตรประชาชน", "เลขประจำตัวประชาชน", "บัตรประชาชน", "citizen id", "national id", "id card", "id card no" },
            ["DateOfBirth"] = new[] { "วันเกิด", "วันเดือนปีเกิด", "date of birth", "dob", "birthdate", "birth date" },
            ["Department"] = new[] { "แผนก", "ฝ่าย", "department", "dept" },
            ["Position"] = new[] { "ตำแหน่ง", "position", "job title" },
            ["StartDate"] = new[] { "วันเริ่มงาน", "วันที่เริ่มงาน", "วันเข้างาน", "วันที่บรรจุ", "start date", "hire date" },
            ["BaseSalary"] = new[] { "เงินเดือน", "เงินเดือนพื้นฐาน", "ฐานเงินเดือน", "salary", "base salary" },
            ["SalaryType"] = new[] { "ประเภทเงินเดือน", "ประเภทการจ้าง", "salary type", "pay type" },
            ["BankName"] = new[] { "ธนาคาร", "ชื่อธนาคาร", "bank", "bank name" },
            ["BankAccountNumber"] = new[] { "เลขที่บัญชี", "เลขบัญชี", "เลขที่บัญชีธนาคาร", "bank account", "account no", "bank account no" },
            ["SocialSecurityNumber"] = new[] { "เลขประกันสังคม", "เลขที่ประกันสังคม", "sso no", "social security no", "social security number" },
        },
        ["budgets"] = BudgetSynonyms(),
    };

    private static Dictionary<string, string[]> OpeningArAp() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ContactName"] = CounterpartyNameWords,
        ["ContactTaxId"] = TaxIdWords,
        ["InvoiceNumber"] = new[] { "เลขที่เอกสาร", "เลขที่ใบแจ้งหนี้", "เลขที่ใบกำกับ", "เลขที่ใบกำกับภาษี", "เลขที่บิล", "เลขที่", "invoice no", "invoice number",
            "invoicenumber", "doc no", "document no", "bill no" },
        ["InvoiceDate"] = new[] { "วันที่เอกสาร", "วันที่ใบแจ้งหนี้", "วันที่ใบกำกับ", "วันที่", "invoice date", "doc date", "date" },
        ["DueDate"] = new[] { "วันครบกำหนด", "วันที่ครบกำหนด", "ครบกำหนด", "กำหนดชำระ", "due date", "due" },
        ["Amount"] = new[] { "ยอดคงค้าง", "คงค้าง", "ยอดค้างชำระ", "ค้างชำระ", "ยอดคงเหลือ", "outstanding", "balance", "amount due", "amount",
            "จำนวนเงิน" },
        ["Description"] = new[] { "รายละเอียด", "หมายเหตุ", "description", "note" },
    };

    private static Dictionary<string, string[]> BudgetSynonyms()
    {
        string[][] months =
        {
            new[] { "ม.ค.", "มกราคม", "jan", "january" }, new[] { "ก.พ.", "กุมภาพันธ์", "feb", "february" },
            new[] { "มี.ค.", "มีนาคม", "mar", "march" }, new[] { "เม.ย.", "เมษายน", "apr", "april" },
            new[] { "พ.ค.", "พฤษภาคม", "may" }, new[] { "มิ.ย.", "มิถุนายน", "jun", "june" },
            new[] { "ก.ค.", "กรกฎาคม", "jul", "july" }, new[] { "ส.ค.", "สิงหาคม", "aug", "august" },
            new[] { "ก.ย.", "กันยายน", "sep", "sept", "september" }, new[] { "ต.ค.", "ตุลาคม", "oct", "october" },
            new[] { "พ.ย.", "พฤศจิกายน", "nov", "november" }, new[] { "ธ.ค.", "ธันวาคม", "dec", "december" },
        };
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["BudgetName"] = new[] { "ชื่องบประมาณ", "ชื่องบ", "budget name", "budget" },
            ["FiscalYear"] = new[] { "ปีงบประมาณ", "ปีบัญชี", "ปี", "fiscal year", "year" },
            ["AccountCode"] = AccountCodeWords,
        };
        for (var i = 0; i < 12; i++) d[$"Month{i + 1}"] = months[i];
        return d;
    }

    /// <summary>
    /// คำพ้องของ <paramref name="field"/> ในชนิดข้อมูล <paramref name="entityType"/> — เฉพาะชนิดก่อน แล้วคำทั่วไป (ไม่ซ้ำ) ·
    /// ชนิดที่ไม่รู้จัก ⇒ คำทั่วไปอย่างเดียว · field ไม่มีคำพ้อง ⇒ ว่าง (ผู้เรียกยังเทียบชื่อ field/ป้ายไทยตรง ๆ ได้)
    /// </summary>
    public static IReadOnlyList<string> For(string? entityType, string field)
    {
        var list = new List<string>();
        if (ByEntity.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var specific)
            && specific.TryGetValue(field, out var s))
            list.AddRange(s);
        if (Global.TryGetValue(field, out var g)) list.AddRange(g);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
