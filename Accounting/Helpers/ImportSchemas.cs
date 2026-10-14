using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **แคตตาล็อก field ปลายทางของทุกชนิดข้อมูลที่นำเข้าได้ — ตัวตั้งตัวเดียว** (รอบ 205 ทีม A · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ย้ายเนื้อจาก <c>ImportExportService.GetTemplateFields</c> (+ <c>GetSampleValue</c> เป็น <see cref="ImportFieldDto.Example"/> · journal-entries
/// ที่เดิมอยู่แค่ใน <c>GetTemplateAsync</c> — <c>GetTemplateFields("journal-entries")</c> คืน null แล้ว <c>!</c> ทับ) · ชื่อ field <b>สะกดเดิมทุกตัว</b>
/// (คีย์ที่ writer เดิมอ่านจาก dictionary แถว) · ชนิดละเอียดขึ้นเป็น <see cref="ImportFieldType"/> (เงิน = Amount · เลขผู้เสียภาษี = TaxId · เลขเอกสาร =
/// Reference ห้ามผ่านตัวเลข) ⇒ <c>ImportCellNormalizer</c> เลือกตัวแปลงถูกตัว</para>
///
/// <para><b>ทีม B</b>: <c>ImportExportService.GetTemplateFields</c> ควรมอบมาที่ <see cref="For"/> (แปลงชนิดกลับเป็น
/// <c>ImportField.DataType</c> เดิมที่ฝั่งผู้เรียก) แล้วลบสำเนา · <b>target ทุกตัว</b> (B/C/D) อ้าง <c>Schema = ImportSchemas.For(EntityType)</c> ·
/// field ใหม่ของ target = เพิ่มที่นี่ (ไม่ใช่ประกาศ schema ของตัวเอง — สองสำเนา = drift)</para>
///
/// <para>คีย์ชนิดข้อมูลหาแบบไม่สนขีด/ตัวพิมพ์ (<see cref="ImportExportPermissionScope.NormalizeEntityType"/> — คีย์เดียวกับด่านสิทธิ์)</para>
/// </summary>
public static class ImportSchemas
{
    private static ImportFieldDto F(string field, string label, ImportFieldType type, bool required = false,
        string? description = null, IReadOnlyList<string>? allowed = null, string? example = null, int? maxLength = null)
        => new(field, label, type, required, description, allowed, example, maxLength);

    private static readonly IReadOnlyList<string> Months =
        new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    /// <summary>(คีย์แสดง, ป้ายไทย, field) — ลำดับ = ลำดับย้ายระบบที่แนะนำ (§2.10) ก่อน แล้วชนิดอื่น</summary>
    private static readonly IReadOnlyList<(string Key, string Label, IReadOnlyList<ImportFieldDto> Fields)> Catalogue = new (string, string, IReadOnlyList<ImportFieldDto>)[]
    {
        ("chart-of-accounts", "ผังบัญชี", new[]
        {
            F("AccountCode", "รหัสบัญชี", ImportFieldType.AccountCode, true, example: "1110"),
            F("AccountName", "ชื่อบัญชี", ImportFieldType.Text, true, example: "เงินสด"),
            F("AccountNameEn", "ชื่อบัญชี (อังกฤษ)", ImportFieldType.Text, example: "Cash"),
            F("AccountType", "ประเภทบัญชี", ImportFieldType.Enum, true, allowed: new[] { "Asset", "Liability", "Equity", "Revenue", "Expense" }, example: "Asset"),
            F("Description", "คำอธิบาย", ImportFieldType.Text, example: "รายละเอียด"),
        }),
        ("contacts", "ผู้ติดต่อ", new[]
        {
            F("Name", "ชื่อ", ImportFieldType.Text, true, "ชื่อลูกค้า/ผู้ขาย", example: "บริษัท ตัวอย่าง จำกัด"),
            F("TaxId", "เลขผู้เสียภาษี", ImportFieldType.TaxId, false,
                "เลขประจำตัวผู้เสียภาษี 13 หลัก (ตรวจ checksum — เลขผิดจะถูกปฏิเสธทั้งแถว)", example: "0105556012341"),
            // ช่องนี้ตัดสิน ภ.ง.ด.3 vs ภ.ง.ด.53 และ scheme ของ e-Tax (NIDN/TXID)
            F("ContactType", "ประเภทผู้ติดต่อ", ImportFieldType.Enum, false,
                "Individual (บุคคลธรรมดา → ภ.ง.ด.3) · JuristicPerson (นิติบุคคล → ภ.ง.ด.53) · GovernmentAgency — เว้นว่างได้ถ้ามีเลขผู้เสียภาษีที่ถูกต้อง ระบบจะอ่านจากเลขให้",
                new[] { "Individual", "JuristicPerson", "GovernmentAgency" }),
            F("BranchCode", "รหัสสาขา", ImportFieldType.BranchCode, false,
                "5 หลักตามประกาศอธิบดีฯ 199 — 00000 = สำนักงานใหญ่ (เก็บเฉพาะนิติบุคคล/ราชการ)", example: "00000"),
            F("IsCustomer", "เป็นลูกค้า", ImportFieldType.Flag, false, "ใช่/ไม่ใช่", example: "true"),
            F("IsSupplier", "เป็นผู้ขาย", ImportFieldType.Flag, false, "ใช่/ไม่ใช่", example: "false"),
            F("Email", "อีเมล", ImportFieldType.Email, example: "info@example.com"),
            F("Phone", "โทรศัพท์", ImportFieldType.Phone, example: "02-123-4567"),
            F("Address", "ที่อยู่", ImportFieldType.Text, example: "123 ถ.สุขุมวิท กรุงเทพฯ 10110"),
            F("ContactPerson", "ผู้ติดต่อ", ImportFieldType.Text, example: "คุณสมชาย"),
        }),
        ("products", "สินค้า/บริการ", new[]
        {
            F("Code", "รหัสสินค้า", ImportFieldType.Reference, true, example: "P001"),
            F("Name", "ชื่อสินค้า", ImportFieldType.Text, true, example: "สินค้าตัวอย่าง"),
            F("ProductType", "ประเภท", ImportFieldType.Enum, false, allowed: new[] { "Product", "Service", "NonStock" }, example: "Product"),
            F("Unit", "หน่วยนับ", ImportFieldType.Text, true, example: "ชิ้น"),
            F("SellingPrice", "ราคาขาย", ImportFieldType.Amount, example: "100.00"),
            F("CostPrice", "ราคาทุน", ImportFieldType.Amount, example: "60.00"),
            F("VatRate", "อัตราภาษี", ImportFieldType.VatRate, example: "7"),
            F("Category", "หมวดหมู่", ImportFieldType.Text, example: "สินค้าทั่วไป"),
        }),
        ("opening-balances", "งบทดลองยกมา", new[]
        {
            // ทีม D เป็นเจ้าของ target · รูปตาม AccountMapping (LegacyCode/LegacyName/LegacyDebit/LegacyCredit) ของ MigrationWizard
            F("AccountCode", "รหัสบัญชี", ImportFieldType.AccountCode, true, "รหัสบัญชีของระบบเดิม — ระบบจับคู่กับผังบัญชีให้", example: "1110"),
            F("AccountName", "ชื่อบัญชี", ImportFieldType.Text, false, "ใช้ช่วยจับคู่ผังเมื่อรหัสไม่ตรง", example: "เงินสด"),
            F("Debit", "เดบิต", ImportFieldType.Amount, false, example: "15000.00"),
            F("Credit", "เครดิต", ImportFieldType.Amount, false, example: ""),
            F("Balance", "ยอดคงเหลือ (สุทธิ)", ImportFieldType.Amount, false, "ไฟล์ที่มีคอลัมน์ยอดเดียว: บวก = เดบิต · ลบ/วงเล็บ = เครดิต", example: "15000.00"),
            F("AsOfDate", "วันยกมา", ImportFieldType.Date, false, "วันที่ของงบทดลอง", example: "2026-01-01"),
        }),
        ("opening-ar", "ลูกหนี้ยกมา", OpeningArAp()),
        ("opening-ap", "เจ้าหนี้ยกมา", OpeningArAp()),
        ("stock-opening", "สต็อกยกมา", new[]
        {
            F("ProductCode", "รหัสสินค้า", ImportFieldType.Reference, true, "ต้องมีอยู่ในระบบแล้ว", example: "P001"),
            F("Quantity", "ยอดยกมา", ImportFieldType.Decimal, true, "สต็อกตั้งต้น (จำนวน)", example: "100"),
            F("UnitCost", "ต้นทุน/หน่วย", ImportFieldType.Amount, false, "ใช้เป็นต้นทุนของสต็อกยกมา (default = CostPrice ของ Product)", example: "60.00"),
            F("OpeningDate", "วันยกมา", ImportFieldType.Date, false, "default = วันนี้", example: "2026-01-01"),
            F("Notes", "หมายเหตุ", ImportFieldType.Text, example: "ปรับนับสิ้นเดือน"),
        }),
        ("fixed-assets", "สินทรัพย์ถาวร", new[]
        {
            F("AssetCode", "รหัสสินทรัพย์", ImportFieldType.Reference, true, example: "FA-001"),
            F("Name", "ชื่อสินทรัพย์", ImportFieldType.Text, true, example: "คอมพิวเตอร์"),
            F("Category", "หมวดหมู่", ImportFieldType.Text, false, "เช่น เครื่องใช้สำนักงาน, ยานพาหนะ", example: "เครื่องใช้สำนักงาน"),
            F("PurchaseDate", "วันที่ซื้อ", ImportFieldType.Date, true, example: "2024-03-15"),
            F("PurchaseCost", "ราคาซื้อ", ImportFieldType.Amount, true, example: "85000.00"),
            F("SalvageValue", "มูลค่าซาก", ImportFieldType.Amount, false, "default = 0", example: "0"),
            // ทีม D (FixedAssetImportTarget): วิธีคิดตัดสินด้วย FixedAssetImportMethod ตัวเดียว (รับไทย/อังกฤษ · ที่ดิน = ไม่คิด) ⇒ ข้อความ ไม่ใช่ Enum เข้ม
            F("UsefulLifeMonths", "อายุการใช้งาน (เดือน)", ImportFieldType.Integer, false, "เช่น 60 = 5 ปี · เว้นว่าง = 60 (ที่ดิน = 0)", example: "60"),
            F("DepreciationMethod", "วิธีคิดค่าเสื่อม", ImportFieldType.Text, false,
                "StraightLine/เส้นตรง · DecliningBalance/ยอดลดลง · DoubleDecliningBalance · None/ไม่คิดค่าเสื่อม — เว้นว่าง = เส้นตรง (ที่ดิน/งานระหว่างก่อสร้าง = ไม่คิด)", example: "StraightLine"),
            F("AccumulatedDepreciation", "ค่าเสื่อมสะสมยกมา", ImportFieldType.Amount, false, "ยังไม่รองรับรายสินทรัพย์ — ลงผ่านงบทดลองยกมา", example: "0"),
            F("Location", "ตำแหน่ง", ImportFieldType.Text, example: "สำนักงานใหญ่"),
            F("SerialNumber", "หมายเลขเครื่อง", ImportFieldType.Reference, example: "SN-12345"),
        }),
        ("journal-entries", "บันทึกบัญชี", new[]
        {
            F("Date", "วันที่", ImportFieldType.Date, true, example: "2026-01-15"),
            F("Description", "คำอธิบาย", ImportFieldType.Text, true, example: "ค่าเช่าสำนักงาน"),
            F("AccountCode", "รหัสบัญชี", ImportFieldType.AccountCode, true, example: "5210"),
            F("DebitAmount", "เดบิต", ImportFieldType.Amount, example: "15000.00"),
            F("CreditAmount", "เครดิต", ImportFieldType.Amount, example: ""),
            F("Reference", "อ้างอิง", ImportFieldType.Reference, example: "JV-0001"),
            F("ContactName", "ผู้ติดต่อ", ImportFieldType.Text, example: "บริษัท ลูกค้า จำกัด"),
        }),
        ("documents", "เอกสาร", new[]
        {
            // ทีม D (DocumentImportTarget/DocumentImportBuilder) — นำเข้าเป็นร่าง + เลข DRAFT · เลขเดิม → Reference (คำตัดสินข้อ 151 (1)) ·
            // ชนิดเป็นข้อความ (ชื่อไทย/อังกฤษ/enum — DocumentImportTypes.Parse) ไม่ใช่ Enum เข้ม เพราะไฟล์จริงใช้ "ใบแจ้งหนี้" · ชนิดที่ร่างไม่ได้ (ใบสำคัญจ่าย/ใบเสร็จ/ใบลด-เพิ่มหนี้) target ปฏิเสธพร้อมทางไปต่อ
            F("DocumentNumber", "เลขเอกสารเดิม", ImportFieldType.Reference, true, "เลขจากโปรแกรมเดิม — เก็บเป็นอ้างอิง (เลขใหม่ออกตอนอนุมัติ) · แถวเลขเดียวกัน = เอกสารเดียว", example: "INV-202601-0001"),
            F("DocumentType", "ประเภทเอกสาร", ImportFieldType.Text, false,
                "ใบเสนอราคา · ใบแจ้งหนี้ · ใบกำกับภาษี · ใบขอซื้อ · ใบสั่งซื้อ · ใบแจ้งหนี้ซื้อ · ใบบันทึกค่าใช้จ่าย (ไทย/อังกฤษ/Invoice ฯลฯ) — เว้นว่างได้ถ้าเลือกชนิดตั้งต้นของชุด", example: "ใบแจ้งหนี้"),
            F("DocumentDate", "วันที่เอกสาร", ImportFieldType.Date, true, example: "2026-01-15"),
            F("DueDate", "วันครบกำหนด", ImportFieldType.Date, example: "2026-02-14"),
            F("ContactName", "ชื่อผู้ติดต่อ", ImportFieldType.Text, false, "ต้องมีชื่อหรือเลขผู้เสียภาษี · ต้องมีในระบบแล้ว (นำเข้าผู้ติดต่อก่อน)", example: "บริษัท ลูกค้า จำกัด"),
            F("ContactTaxId", "เลขผู้เสียภาษีผู้ติดต่อ", ImportFieldType.TaxId, false, "ถ้ามี ใช้จับคู่แม่นกว่าชื่อ", example: "0105556012341"),
            F("ContactBranchCode", "สาขาผู้ติดต่อ", ImportFieldType.BranchCode, false, "00000 = สำนักงานใหญ่", example: "00000"),
            F("Reference", "อ้างอิงเพิ่มเติม", ImportFieldType.Reference, false, "ต่อท้ายหมายเหตุ (ช่องอ้างอิงของเอกสารเก็บเลขเดิม)", example: "PO-778"),
            F("Notes", "หมายเหตุ", ImportFieldType.Text, example: ""),
            F("PricesIncludeVat", "ราคารวม VAT", ImportFieldType.Flag, false, example: "ไม่ใช่"),
            F("LineDescription", "รายละเอียด (line)", ImportFieldType.Text, true, example: "บริการที่ปรึกษา"),
            F("LineQuantity", "จำนวน", ImportFieldType.Decimal, false, "เว้นว่าง = 1", example: "1"),
            F("LineUnit", "หน่วย", ImportFieldType.Text, false, example: "ครั้ง"),
            F("LineUnitPrice", "ราคา/หน่วย", ImportFieldType.Amount, true, example: "10000.00"),
            F("LineDiscountPercent", "ส่วนลด %", ImportFieldType.Decimal, false, "default = 0", example: "0"),
            F("LineVatRate", "VAT %", ImportFieldType.VatRate, false, "default = 7 (กิจการจด VAT)", example: "7"),
            F("LineWhtRate", "หัก ณ ที่จ่าย %", ImportFieldType.Decimal, false, "default = 0", example: "3"),
            F("LineProductCode", "รหัสสินค้า", ImportFieldType.Reference, example: "P001"),
            F("LineAccountCode", "รหัสบัญชี", ImportFieldType.AccountCode, false, "ฝั่งซื้อเว้นว่างได้ — ระบบแนะนำผังให้", example: "4100"),
        }),
        ("payments", "การชำระเงิน", new[]
        {
            // ทีม D (PaymentImportTarget) — ลงผ่าน CreatePaymentAsync ⇒ JE · วิธีชำระเป็นข้อความ (รับ "โอน"/"เงินสด"/"เช็ค" + ชื่อ enum)
            F("DocumentNumber", "เลขเอกสาร", ImportFieldType.Reference, true, "เลขเอกสารในระบบ หรือเลขเดิมที่นำเข้าไว้เป็นอ้างอิง (เอกสารต้องอนุมัติแล้ว)", example: "INV-202601-0001"),
            F("PaymentDate", "วันที่ชำระ", ImportFieldType.Date, true, example: "2026-01-20"),
            F("Amount", "จำนวนเงิน", ImportFieldType.Amount, true, "เงินที่เข้า/ออกจริง (หลังหัก ณ ที่จ่าย)", example: "10700.00"),
            F("PaymentMethod", "วิธีชำระ", ImportFieldType.Text, false,
                "Cash/เงินสด · BankTransfer/โอน · CreditCard/บัตรเครดิต · Cheque/เช็ค · PromptPay/พร้อมเพย์ · DirectDebit · EWallet · Other — เว้นว่าง = โอน", example: "BankTransfer"),
            F("WithholdingTaxAmount", "ภาษีหัก ณ ที่จ่าย", ImportFieldType.Amount, false, "เว้นว่าง = ระบบคิดตามสัดส่วนของเอกสาร", example: "300.00"),
            F("FeeAmount", "ค่าธรรมเนียมที่ถูกหัก", ImportFieldType.Amount, false, example: "0"),
            F("Reference", "อ้างอิง", ImportFieldType.Reference, false, "เลขที่อ้างอิงการโอน/เช็ค", example: "REF001"),
            F("BankAccount", "บัญชีธนาคาร", ImportFieldType.Text, false, "ชื่อ/เลขบัญชี — เก็บเป็น text", example: "SCB 123-4-56789-0"),
            F("Notes", "หมายเหตุ", ImportFieldType.Text, example: ""),
        }),
        ("banktransactions", "รายการธนาคาร", new[]
        {
            F("BankAccountId", "รหัสบัญชีธนาคาร", ImportFieldType.Text, true, example: "00000000-0000-0000-0000-000000000000"),
            F("TransactionDate", "วันที่", ImportFieldType.Date, true, example: "2026-01-15"),
            F("TransactionType", "ประเภท", ImportFieldType.Enum, true, allowed: new[] { "Deposit", "Withdrawal", "Transfer", "Fee", "Interest" }, example: "Deposit"),
            F("Amount", "จำนวนเงิน", ImportFieldType.Amount, true, example: "50000.00"),
            F("Description", "คำอธิบาย", ImportFieldType.Text, example: "รับชำระเงิน"),
            F("Reference", "อ้างอิง", ImportFieldType.Reference, example: "REF001"),
        }),
        ("stock-adjustments", "ปรับสต็อก", new[]
        {
            F("ProductCode", "รหัสสินค้า", ImportFieldType.Reference, true, example: "P001"),
            F("AdjustmentDate", "วันที่ปรับ", ImportFieldType.Date, true, example: "2026-01-15"),
            F("MovementType", "ประเภท", ImportFieldType.Enum, true, "IN=เพิ่ม, OUT=ลด, ADJUST=ปรับนับ", new[] { "IN", "OUT", "ADJUST" }, "ADJUST"),
            F("Quantity", "จำนวน", ImportFieldType.Decimal, true, "บวกสำหรับ IN/ADJUST+, ลบสำหรับ OUT/ADJUST-", example: "100"),
            F("UnitCost", "ต้นทุน/หน่วย", ImportFieldType.Amount, false, "default = CostPrice ของ Product", example: "60.00"),
            F("Reference", "เลขเอกสารอ้างอิง", ImportFieldType.Reference, example: "REF001"),
            F("Notes", "หมายเหตุ", ImportFieldType.Text, false, "เช่น เสียหาย, ปรับนับสิ้นเดือน", example: "ปรับนับสิ้นเดือน"),
        }),
        ("projects", "โปรเจกต์", new[]
        {
            F("Code", "รหัสโปรเจกต์", ImportFieldType.Reference, true, example: "PRJ-001"),
            F("Name", "ชื่อโปรเจกต์", ImportFieldType.Text, true, example: "ก่อสร้างอาคาร A"),
            F("NameEn", "ชื่อ (อังกฤษ)", ImportFieldType.Text),
            F("Description", "รายละเอียด", ImportFieldType.Text),
            F("CustomerName", "ลูกค้า", ImportFieldType.Text, false, "ชื่อลูกค้า/นายจ้าง", example: "บริษัท ลูกค้า จำกัด"),
            F("ProjectManagerName", "ผู้จัดการโปรเจกต์", ImportFieldType.Text, example: "คุณสมหญิง"),
            F("StartDate", "วันเริ่มต้น", ImportFieldType.Date, true, example: "2026-01-01"),
            F("EndDate", "วันสิ้นสุด (แผน)", ImportFieldType.Date, example: "2026-12-31"),
            F("Status", "สถานะ", ImportFieldType.Enum, false, "default = Active", new[] { "Active", "OnHold", "Completed", "Cancelled" }, "Active"),
            F("BudgetAmount", "งบประมาณ", ImportFieldType.Amount, example: "500000.00"),
            F("ContractAmount", "มูลค่าสัญญา", ImportFieldType.Amount, example: "550000.00"),
            // ชุดค่ามาจากตัวตั้งเดียวกับฟอร์ม/API — ห้ามพิมพ์สำเนาที่สอง (F2 ข้อ 4)
            F("BillingMethod", "วิธีเรียกเก็บ", ImportFieldType.Enum, false, "default = " + ProjectContractMethods.DefaultBilling,
                ProjectContractMethods.Billing.Select(x => x.Value).ToList(), "FixedPrice"),
        }),
        ("employees", "พนักงาน", new[]
        {
            F("EmployeeCode", "รหัสพนักงาน", ImportFieldType.Reference, true, example: "EMP-001"),
            F("TitleTh", "คำนำหน้า", ImportFieldType.Text, false, "นาย / นาง / นางสาว", example: "นาย"),
            F("FirstNameTh", "ชื่อ", ImportFieldType.Text, true, example: "สมชาย"),
            F("LastNameTh", "นามสกุล", ImportFieldType.Text, true, example: "ใจดี"),
            F("FirstNameEn", "ชื่อ (อังกฤษ)", ImportFieldType.Text),
            F("LastNameEn", "นามสกุล (อังกฤษ)", ImportFieldType.Text),
            F("CitizenId", "เลขบัตร ปชช.", ImportFieldType.TaxId, false, "13 หลัก", example: "1101700203450"),
            F("DateOfBirth", "วันเกิด", ImportFieldType.Date, example: "1990-05-15"),
            F("Phone", "โทรศัพท์", ImportFieldType.Phone),
            F("Email", "อีเมล", ImportFieldType.Email),
            F("Department", "แผนก", ImportFieldType.Text, example: "ฝ่ายขาย"),
            F("Position", "ตำแหน่ง", ImportFieldType.Text, example: "พนักงานขาย"),
            F("StartDate", "วันเริ่มงาน", ImportFieldType.Date, true, example: "2026-01-01"),
            F("BaseSalary", "เงินเดือนพื้นฐาน", ImportFieldType.Amount, true, example: "25000.00"),
            F("SalaryType", "ประเภทเงินเดือน", ImportFieldType.Enum, false, "default = Monthly", new[] { "Monthly", "Daily", "Hourly" }, "Monthly"),
            F("BankName", "ธนาคาร", ImportFieldType.Text, example: "ธนาคารกสิกรไทย"),
            F("BankAccountNumber", "เลขที่บัญชีธนาคาร", ImportFieldType.Reference, example: "123-4-56789-0"),
            // รอบ 201 PR2: ตัวอย่างต้องผ่านตัวตัดสินเลขประกันสังคม (13 หลัก)
            F("SocialSecurityNumber", "เลขประกันสังคม", ImportFieldType.Reference, example: "1101700203450"),
        }),
        ("budgets", "งบประมาณ", Budget()),
    };

    private static IReadOnlyList<ImportFieldDto> OpeningArAp() => new[]
    {
        F("ContactName", "ชื่อลูกค้า/ผู้ขาย", ImportFieldType.Text, true, "จับคู่ผู้ติดต่อเดิม — ถ้าไม่พบจะสร้างใหม่ให้", example: "บริษัท ลูกค้า จำกัด"),
        F("ContactTaxId", "เลขผู้เสียภาษี", ImportFieldType.TaxId, false, "ใช้จับคู่ผู้ติดต่อ (แม่นกว่าชื่อ)", example: "0105556012341"),
        F("InvoiceNumber", "เลขที่เอกสารเดิม", ImportFieldType.Reference, true, "เลขใบแจ้งหนี้จากระบบเก่า — ใช้กันการนำเข้าซ้ำ", example: "INV-6812-001"),
        F("InvoiceDate", "วันที่เอกสาร", ImportFieldType.Date, true, "ใช้คำนวณอายุหนี้", example: "2025-12-15"),
        F("DueDate", "วันครบกำหนด", ImportFieldType.Date, example: "2026-01-14"),
        F("Amount", "ยอดคงค้าง", ImportFieldType.Amount, true, "ยอดที่ยังค้างชำระ ณ วันยกมา", example: "10700.00"),
        F("Description", "รายละเอียด", ImportFieldType.Text),
    };

    private static IReadOnlyList<ImportFieldDto> Budget()
    {
        var list = new List<ImportFieldDto>
        {
            F("BudgetName", "ชื่องบประมาณ", ImportFieldType.Text, true, "เช่น 'งบ 2026' — ใช้ group บรรทัดเข้าด้วยกัน", example: "งบ 2026"),
            F("FiscalYear", "ปีงบประมาณ", ImportFieldType.Integer, true, "เช่น 2026", example: "2026"),
            F("AccountCode", "รหัสบัญชี", ImportFieldType.AccountCode, true, "ต้องมีในผังบัญชีแล้ว", example: "5210"),
        };
        for (var m = 1; m <= 12; m++)
            list.Add(F($"Month{m}", Months[m - 1], ImportFieldType.Amount, example: m <= 3 ? "10000" : m <= 9 ? "12000" : "15000"));
        return list;
    }

    /// <summary>
    /// field ของ "บรรทัด statement ธนาคาร" (วันที่ · เวลา · ฝาก · ถอน · ยอดคงเหลือ · รายการ · ช่องทาง) — <b>ทีม C</b> ใช้เป็น schema ของ
    /// <c>BankStatementImportTarget</c> (ไฟล์ statement จริงไม่มี BankAccountId ต่อแถว — ผู้ใช้เลือกบัญชีต่อ batch) · คำพ้องจาก <c>BankCsvParser</c>
    /// อยู่ที่ <see cref="ImportFieldSynonymSeed"/> · โปรไฟล์ธนาคารไทยทั่วไปใน <see cref="ImportSourceSeed"/> จับคู่มาที่ชุดนี้
    /// </summary>
    public static readonly IReadOnlyList<ImportFieldDto> BankStatementLines = new[]
    {
        F("TransactionDate", "วันที่", ImportFieldType.Date, true, example: "15/01/2569"),
        F("TransactionTime", "เวลา", ImportFieldType.Text, example: "10:30"),
        F("Description", "รายการ", ImportFieldType.Text, example: "รับโอนเงิน"),
        F("Withdrawal", "ถอน", ImportFieldType.Amount, example: "1,500.00"),
        F("Deposit", "ฝาก", ImportFieldType.Amount, example: "50,000.00"),
        F("Amount", "จำนวนเงิน (มีเครื่องหมาย)", ImportFieldType.Amount, false, "ไฟล์ที่มีคอลัมน์ยอดเดียว: บวก = เงินเข้า · ลบ = เงินออก", example: "-1,500.00"),
        F("Balance", "ยอดคงเหลือ", ImportFieldType.Amount, example: "98,500.00"),
        F("Channel", "ช่องทาง", ImportFieldType.Text, example: "K PLUS"),
        F("Details", "รายละเอียด", ImportFieldType.Text, example: "จาก นาย ก"),
        F("Reference", "เลขอ้างอิง", ImportFieldType.Reference, example: "FT26015ABC"),
    };

    private static readonly Dictionary<string, (string Key, string Label, IReadOnlyList<ImportFieldDto> Fields)> ByNorm =
        Catalogue.ToDictionary(c => ImportExportPermissionScope.NormalizeEntityType(c.Key), c => c, StringComparer.Ordinal);

    /// <summary>คีย์ชนิดข้อมูลทั้งหมด (สะกดแบบแสดง เช่น "chart-of-accounts") — ลำดับย้ายระบบก่อน</summary>
    public static IReadOnlyList<string> EntityTypes { get; } = Catalogue.Select(c => c.Key).ToList();

    /// <summary>field ของชนิดข้อมูล (ไม่สนขีด/ตัวพิมพ์: "chartofaccounts" = "chart-of-accounts") · ไม่รู้จัก ⇒ null</summary>
    public static IReadOnlyList<ImportFieldDto>? For(string? entityType)
        => ByNorm.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var c) ? c.Fields : null;

    /// <summary>คีย์ชนิดข้อมูลแบบแสดง ("ChartOfAccounts" → "chart-of-accounts") · ไม่รู้จัก ⇒ null</summary>
    public static string? CanonicalEntityType(string? entityType)
        => ByNorm.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var c) ? c.Key : null;

    /// <summary>ชื่อไทยของชนิดข้อมูล · ไม่รู้จัก ⇒ "ข้อมูล" (ห้ามโชว์คีย์อังกฤษให้ผู้ใช้)</summary>
    public static string EntityLabel(string? entityType)
        => ByNorm.TryGetValue(ImportExportPermissionScope.NormalizeEntityType(entityType), out var c) ? c.Label : "ข้อมูล";

    /// <summary>
    /// ชนิดข้อมูลของ field จากสตริงชนิด — ชื่อ <see cref="ImportFieldType"/> ("Amount") · ชนิดเดิมของ <c>ImportField.DataType</c>
    /// ("string"/"decimal"/"date"/"bool"/"enum"/"guid") · ชื่อ field ช่วยตัดสินเมื่อชนิดกว้าง (string + "TaxId"/"Email"/"Phone") ·
    /// ตัวเดียวที่ <c>ImportReviewHeuristics</c> และนักเรียน <c>ImportColumnMatch</c> ใช้แปลชนิดจาก prompt
    /// </summary>
    public static ImportFieldType ParseFieldType(string? dataType, string? fieldName = null)
    {
        var dt = (dataType ?? "").Trim();
        if (dt.Length > 0 && !int.TryParse(dt, out _) && Enum.TryParse<ImportFieldType>(dt, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
            return parsed;
        var legacy = dt.ToLowerInvariant() switch
        {
            "decimal" or "number" or "numeric" or "money" or "currency" => ImportFieldType.Decimal,
            "int" or "integer" => ImportFieldType.Integer,
            "date" or "datetime" => ImportFieldType.Date,
            "bool" or "boolean" => ImportFieldType.Flag,
            "enum" => ImportFieldType.Enum,
            _ => ImportFieldType.Text,
        };
        if (legacy != ImportFieldType.Text) return legacy;
        var name = fieldName ?? "";
        if (name.Contains("TaxId", StringComparison.OrdinalIgnoreCase) || name.Contains("CitizenId", StringComparison.OrdinalIgnoreCase))
            return ImportFieldType.TaxId;
        if (name.Contains("Email", StringComparison.OrdinalIgnoreCase)) return ImportFieldType.Email;
        if (name.Contains("Phone", StringComparison.OrdinalIgnoreCase)) return ImportFieldType.Phone;
        return ImportFieldType.Text;
    }
}
