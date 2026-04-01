using Accounting.Models.Enums;

namespace Accounting.Services;

/// <summary>
/// ผังบัญชีมาตรฐานไทย (Thai Standard Chart of Accounts)
/// อ้างอิงตามมาตรฐานการรายงานทางการเงิน (TFRS/TAS)
/// รหัส: หลักที่ 1 = ประเภท (1-5), หลัก 1-2 = หมวด, หลัก 1-3 = กลุ่ม, หลัก 1-5 = บัญชีย่อย
/// </summary>
public static class ChartOfAccountTemplates
{
    public record AccountTemplate(string Code, string NameTh, string NameEn, AccountType Type, int Level);

    /// <summary>ผังบัญชีพื้นฐานที่ใช้ร่วมกันทุกประเภทธุรกิจ (ยกเว้นหมวด 3 ส่วนของเจ้าของ)</summary>
    public static List<AccountTemplate> GetCommonAccounts()
    {
        return new List<AccountTemplate>
        {
            // ==================== 1. สินทรัพย์ (Assets) ====================
            new("1", "ทรัพย์สิน", "Asset", AccountType.Asset, 1),

            new("11", "สินทรัพย์หมุนเวียน", "Current Assets", AccountType.Asset, 2),
            new("111", "เงินสดและรายการเทียบเท่าเงินสด", "Cash and Cash Equivalents", AccountType.Asset, 3),
            new("11111", "เงินสด", "Cash", AccountType.Asset, 4),
            new("11112", "เงินสดย่อย", "Petty Cash", AccountType.Asset, 4),
            new("11113", "กระเป๋าเงิน Digital", "Digital Wallet", AccountType.Asset, 4),
            new("11121", "เงินฝากกระแสรายวัน", "Current Account / Checking Account", AccountType.Asset, 4),
            new("11122", "เงินฝากออมทรัพย์", "Savings Account", AccountType.Asset, 4),
            new("11123", "เงินฝากประจำ", "Fixed Deposit Account / Time Deposit", AccountType.Asset, 4),
            new("11131", "เช็คในมือ", "Cheques on Hand", AccountType.Asset, 4),
            new("112", "เงินลงทุนชั่วคราว", "Temporary Investments / Short-term Investments", AccountType.Asset, 3),
            new("11200", "เงินลงทุนชั่วคราว", "Short-term Investments", AccountType.Asset, 4),
            new("113", "ลูกหนี้การค้าและลูกหนี้อื่น", "Trade and Other Receivables", AccountType.Asset, 3),
            new("11310", "ลูกหนี้การค้า", "Trade Receivables", AccountType.Asset, 4),
            new("11320", "ลูกหนี้อื่น", "Other Receivables", AccountType.Asset, 4),
            new("114", "เงินให้กู้ยืมระยะสั้น", "Short-term Loans", AccountType.Asset, 3),
            new("11400", "เงินให้กู้ยืมระยะสั้น", "Short-term Loans", AccountType.Asset, 4),
            new("115", "สินค้าคงเหลือ", "Inventories", AccountType.Asset, 3),
            new("11500", "สินค้าคงเหลือ", "Inventory", AccountType.Asset, 4),
            new("119", "สินทรัพย์หมุนเวียนอื่น", "Other Current Assets", AccountType.Asset, 3),
            new("11900", "สินทรัพย์หมุนเวียนอื่น", "Other Current Assets", AccountType.Asset, 4),

            new("12", "สินทรัพย์ไม่หมุนเวียน", "Non-Current Assets", AccountType.Asset, 2),
            new("121", "เงินให้กู้ยืมระยะยาว", "Long-term Loans", AccountType.Asset, 3),
            new("12110", "เงินให้กู้ยืมระยะยาว", "Long-term Loans", AccountType.Asset, 4),
            new("122", "ที่ดิน อาคารและอุปกรณ์", "Property, Plant and Equipment (PPE)", AccountType.Asset, 3),
            new("12210", "อุปกรณ์สำนักงาน", "Office Equipment", AccountType.Asset, 4),
            new("12220", "คอมพิวเตอร์", "Computer Equipment", AccountType.Asset, 4),
            new("12230", "เครื่องตกแต่งสำนักงาน", "Office Furniture and Fixtures", AccountType.Asset, 4),
            new("12240", "ยานพาหนะ", "Vehicles", AccountType.Asset, 4),
            new("12250", "สินทรัพย์ตามสัญญาเช่า", "Right-of-Use Assets", AccountType.Asset, 4),
            new("12260", "เครื่องจักรและอุปกรณ์", "Machinery and Equipment", AccountType.Asset, 4),
            new("12270", "อาคาร", "Buildings", AccountType.Asset, 4),
            new("12280", "งานระหว่างก่อสร้าง", "Construction in Progress", AccountType.Asset, 4),
            new("12290", "ที่ดิน", "Land", AccountType.Asset, 4),
            new("123", "สินทรัพย์ไม่มีตัวตน", "Intangible Assets", AccountType.Asset, 3),
            new("12310", "ซอฟต์แวร์คอมพิวเตอร์", "Computer Software", AccountType.Asset, 4),
            new("124", "ค่าใช้จ่ายรอตัดจ่ายระยะยาว", "Long-term Deferred Expenses", AccountType.Asset, 3),
            new("12410", "ค่าใช้จ่ายรอตัดจ่ายระยะยาว", "Deferred Expenses (Long-term)", AccountType.Asset, 4),
            new("125", "สินทรัพย์ไม่หมุนเวียนอื่น", "Other Non-current Assets", AccountType.Asset, 3),
            new("12510", "เงินมัดจำระยะยาว", "Long-term Deposits", AccountType.Asset, 4),
            new("12520", "เงินประกันระยะยาว", "Long-term Guarantees", AccountType.Asset, 4),

            new("18", "ค่าเสื่อมราคา ด้อยค่า และการตีราคา", "Depreciation, Impairment and Revaluations", AccountType.Asset, 2),
            new("181", "ค่าเผื่อหนี้สงสัยจะสูญ ลูกหนี้การค้าและลูกหนี้อื่น", "Allowance for Doubtful Accounts", AccountType.Asset, 3),
            new("18100", "ค่าเผื่อหนี้สงสัยจะสูญ ลูกหนี้การค้าและลูกหนี้อื่น", "Allowance for Doubtful Accounts", AccountType.Asset, 4),
            new("182", "ค่าเผื่อการด้อยค่า/การตีราคา สินค้าคงเหลือ", "Allowance for Inventory Obsolescence / Devaluation", AccountType.Asset, 3),
            new("18200", "ค่าเผื่อการด้อยค่า/การตีราคา สินค้าคงเหลือ", "Allowance for Inventory Obsolescence", AccountType.Asset, 4),
            new("183", "ค่าเสื่อมราคาสะสม-ที่ดินอาคารและอุปกรณ์", "Accumulated Depreciation - PPE", AccountType.Asset, 3),
            new("18310", "ค่าเสื่อมราคาสะสม-อุปกรณ์สำนักงาน", "Accumulated Depreciation - Office Equipment", AccountType.Asset, 4),
            new("18320", "ค่าเสื่อมราคาสะสม-คอมพิวเตอร์", "Accumulated Depreciation - Computer Equipment", AccountType.Asset, 4),
            new("18330", "ค่าเสื่อมราคาสะสม-เครื่องตกแต่งสำนักงาน", "Accumulated Depreciation - Furniture and Fixtures", AccountType.Asset, 4),
            new("18340", "ค่าเสื่อมราคาสะสม-ยานพาหนะ", "Accumulated Depreciation - Vehicles", AccountType.Asset, 4),
            new("18350", "ค่าเสื่อมราคาสะสม-สินทรัพย์ตามสัญญาเช่า", "Accumulated Depreciation - Right-of-Use Assets", AccountType.Asset, 4),
            new("18360", "ค่าเสื่อมราคาสะสม-เครื่องจักรและอุปกรณ์", "Accumulated Depreciation - Machinery and Equipment", AccountType.Asset, 4),
            new("18370", "ค่าเสื่อมราคาสะสม-อาคาร", "Accumulated Depreciation - Buildings", AccountType.Asset, 4),
            new("184", "ค่าเผื่อการด้อยค่าสะสม-ซอฟต์แวร์คอมพิวเตอร์", "Accumulated Impairment - Computer Software", AccountType.Asset, 3),
            new("18410", "ค่าเผื่อการด้อยค่าสะสม-ซอฟต์แวร์คอมพิวเตอร์", "Accumulated Impairment - Software", AccountType.Asset, 4),

            // ==================== 2. หนี้สิน (Liabilities) ====================
            new("2", "หนี้สิน", "Liabilities", AccountType.Liability, 1),

            new("21", "หนี้สินหมุนเวียน", "Current Liabilities", AccountType.Liability, 2),
            new("211", "เงินเบิกเกินบัญชี/เงินกู้ยืมธนาคารระยะสั้น", "Bank Overdrafts and Short-term Borrowings", AccountType.Liability, 3),
            new("21100", "เงินเบิกเกินบัญชี/เงินกู้ยืมธนาคารระยะสั้น", "Bank Overdrafts and Short-term Borrowings", AccountType.Liability, 4),
            new("212", "เจ้าหนี้การค้าและเจ้าหนี้ไม่หมุนเวียนอื่น", "Trade and Other Non-current Payables", AccountType.Liability, 3),
            new("21210", "เจ้าหนี้การค้า", "Trade Payables", AccountType.Liability, 4),
            new("21220", "เจ้าหนี้อื่น", "Other Current Payables", AccountType.Liability, 4),
            new("213", "เงินกู้ยืมระยะสั้น", "Short-term Borrowings / Short-term Loans", AccountType.Liability, 3),
            new("21300", "เงินกู้ยืมระยะสั้น", "Short-term Loans", AccountType.Liability, 4),
            new("214", "หนี้สินที่ถึงกำหนดชำระภายใน 1 ปี", "Current Portion of Long-term Liabilities", AccountType.Liability, 3),
            new("21400", "หนี้สินที่ถึงกำหนดชำระภายใน 1 ปี", "Current Portion of Long-term Liabilities", AccountType.Liability, 4),
            new("215", "ค่าสาธารณูปโภคค้างจ่าย", "Accrued Utilities Expenses", AccountType.Liability, 3),
            new("21511", "ค่าไฟฟ้าค้างจ่าย", "Accrued Electricity Expenses", AccountType.Liability, 4),
            new("21512", "ค่าน้ำประปาค้างจ่าย", "Accrued Water Supply Expenses", AccountType.Liability, 4),
            new("21513", "ค่าโทรศัพท์ค้างจ่าย", "Accrued Telephone Expenses", AccountType.Liability, 4),
            new("21514", "ค่าอินเทอร์เน็ตค้างจ่าย", "Accrued Internet Expenses", AccountType.Liability, 4),
            new("21515", "ค่าสาธารณูปโภคอื่นๆ", "Other Utilities Expenses", AccountType.Liability, 4),
            new("21516", "ค่าใช้จ่ายค้างจ่ายอื่นๆ", "Other Accrued Expenses", AccountType.Liability, 4),
            new("216", "เงินมัดจำและเงินรับล่วงหน้าอื่น", "Deposits and Other Advances Received", AccountType.Liability, 3),
            new("21610", "เงินมัดจำรับ", "Deposit Received", AccountType.Liability, 4),
            new("21620", "เงินค้ำประกัน", "Security Deposit / Guarantee Deposit", AccountType.Liability, 4),
            new("217", "รายได้รับล่วงหน้า", "Unearned Revenue / Deferred Revenue", AccountType.Liability, 3),
            new("21711", "ค่าเช่ารับล่วงหน้า", "Unearned Rental Income", AccountType.Liability, 4),
            new("21712", "ค่าสินค้ารับล่วงหน้า", "Advance Received from Customers", AccountType.Liability, 4),
            new("21713", "ค่าบริการรับล่วงหน้า", "Unearned Service Income", AccountType.Liability, 4),
            new("21714", "ดอกเบี้ยค้างจ่าย", "Accrued Interest Expense", AccountType.Liability, 4),
            new("218", "ค่าใช้จ่ายบุคลากรค้างจ่าย", "Accrued Staff Expenses / Salaries Payable", AccountType.Liability, 3),
            new("21811", "เงินเดือน/ค่าจ้างค้างจ่าย", "Accrued Salaries and Wages", AccountType.Liability, 4),
            new("21812", "โบนัสค้างจ่าย", "Accrued Bonus", AccountType.Liability, 4),
            new("21813", "ค่านายหน้าค้างจ่าย", "Accrued Commission", AccountType.Liability, 4),
            new("21814", "เงินเดือน/ค่าจ้างค้างจ่ายอื่นๆ", "Other Accrued Salaries and Wages", AccountType.Liability, 4),
            new("21815", "เงินประกันสังคมรอนำส่ง", "Social Security Fund Payable", AccountType.Liability, 4),
            new("21816", "กองทุนเงินทดแทนค้างจ่าย", "Workmen's Compensation Fund Payable", AccountType.Liability, 4),
            new("21817", "กองทุนสงเคราะห์ลูกจ้างรอนำส่ง", "Employee Welfare Fund Payable", AccountType.Liability, 4),
            new("21818", "กองทุนสำรองเลี้ยงชีพรอนำส่ง", "Provident Fund Payable", AccountType.Liability, 4),
            new("21819", "สวัสดิการค้างจ่าย", "Accrued Employee Benefits", AccountType.Liability, 4),
            new("21820", "ค่าตอบแทนกรรมการค้างจ่าย", "Accrued Director's Remuneration", AccountType.Liability, 4),
            new("219", "ภาษีค้างจ่าย", "Taxes Payable", AccountType.Liability, 3),
            new("21911", "ภาษีขาย ภ.พ. 30", "Output VAT (P.P. 30)", AccountType.Liability, 4),
            new("21912", "ภาษีขาย ภ.พ. 36", "Output VAT (P.P. 36)", AccountType.Liability, 4),
            new("21913", "ภาษีขายรอเรียกเก็บ", "Deferred Output VAT", AccountType.Liability, 4),
            new("21914", "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 1", "Withholding Tax Payable (P.N.D. 1)", AccountType.Liability, 4),
            new("21915", "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 2", "Withholding Tax Payable (P.N.D. 2)", AccountType.Liability, 4),
            new("21916", "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 3", "Withholding Tax Payable (P.N.D. 3)", AccountType.Liability, 4),
            new("21917", "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 53", "Withholding Tax Payable (P.N.D. 53)", AccountType.Liability, 4),
            new("21918", "ภาษีหัก ณ ที่จ่าย - ภ.ง.ด. 54", "Withholding Tax Payable (P.N.D. 54)", AccountType.Liability, 4),
            new("21920", "ภาษีเงินได้นิติบุคคลค้างจ่าย", "Corporate Income Tax Payable", AccountType.Liability, 4),
            new("21921", "ภาษีอื่นๆ ค้างจ่าย", "Other Taxes Payable", AccountType.Liability, 4),
            new("21922", "เจ้าหนี้สรรพากร", "Revenue Department Payable", AccountType.Liability, 4),

            new("22", "หนี้สินไม่หมุนเวียน", "Non-Current Liabilities", AccountType.Liability, 2),
            new("221", "เงินกู้ยืมระยะยาว", "Long-term Borrowings / Long-term Loans", AccountType.Liability, 3),
            new("22100", "เงินกู้ยืมระยะยาว", "Long-term Loans", AccountType.Liability, 4),
            new("229", "หนี้สินอื่นๆ", "Other Liabilities", AccountType.Liability, 3),
            new("22900", "หนี้สินอื่นๆ", "Other Liabilities", AccountType.Liability, 4),

            // ==================== 4. รายได้ (Revenue) ====================
            new("4", "รายได้", "Revenue", AccountType.Revenue, 1),

            new("41", "รายได้จากการขาย", "Sales Revenue", AccountType.Revenue, 2),
            new("410", "รายได้จากการขาย", "Sales Revenue", AccountType.Revenue, 3),
            new("41000", "รายได้จากการขาย", "Sales Revenue", AccountType.Revenue, 4),

            new("42", "รายได้จากการบริการ", "Service Revenue", AccountType.Revenue, 2),
            new("420", "รายได้จากการบริการ", "Service Revenue", AccountType.Revenue, 3),
            new("42000", "รายได้จากการบริการ", "Service Revenue", AccountType.Revenue, 4),

            new("43", "รายได้อื่น", "Other Income", AccountType.Revenue, 2),
            new("430", "รายได้อื่น", "Other Income", AccountType.Revenue, 3),
            new("43010", "รายได้ดอกเบี้ย", "Interest Income", AccountType.Revenue, 4),
            new("43020", "รายได้เงินปันผล", "Dividend Income", AccountType.Revenue, 4),
            new("43030", "รายได้จากการขายสินทรัพย์", "Gain on Sale of Assets", AccountType.Revenue, 4),
            new("43040", "รายได้จากการเช่า", "Rental Income", AccountType.Revenue, 4),
            new("43050", "รายได้จากอัตราแลกเปลี่ยน", "Foreign Exchange Gain", AccountType.Revenue, 4),
            new("43060", "ส่วนลดรับ", "Discounts Received", AccountType.Revenue, 4),
            new("43070", "รายได้อื่นๆ", "Other Income", AccountType.Revenue, 4),
            new("43080", "รายได้ค่าปรับ/ค่าเสียหายที่ได้รับ", "Penalties and Damages Received", AccountType.Revenue, 4),

            // ==================== 5. ค่าใช้จ่าย (Expenses) ====================
            new("5", "ค่าใช้จ่าย", "Expenses", AccountType.Expense, 1),

            // --- 51 ต้นทุนขาย ---
            new("51", "ต้นทุนขาย", "Cost of Goods Sold (COGS)", AccountType.Expense, 2),
            new("511", "ต้นทุนขาย", "Cost of Goods Sold", AccountType.Expense, 3),
            new("51110", "ต้นทุนสินค้า", "Cost of Goods", AccountType.Expense, 4),
            new("51120", "ค่าขนส่งสินค้า", "Freight-in / Delivery Cost", AccountType.Expense, 4),
            new("51130", "ค่าแรงงานในการผลิต", "Direct Labor Cost", AccountType.Expense, 4),
            new("51140", "ค่าใช้จ่ายในการผลิต", "Manufacturing Overhead", AccountType.Expense, 4),

            // --- 52 ต้นทุนบริการ ---
            new("52", "ต้นทุนบริการ", "Cost of Services", AccountType.Expense, 2),
            new("521", "ต้นทุนบริการ", "Cost of Services", AccountType.Expense, 3),
            new("52110", "ค่าแรงงาน (บริการ)", "Direct Labor (Service)", AccountType.Expense, 4),
            new("52120", "ค่าวัสดุสิ้นเปลือง (บริการ)", "Supplies (Service)", AccountType.Expense, 4),
            new("52130", "ค่าเหมาช่วง/Subcontract", "Subcontract Cost", AccountType.Expense, 4),

            // --- 53 ค่าใช้จ่ายในการขาย ---
            new("53", "ค่าใช้จ่ายในการขาย", "Selling Expenses", AccountType.Expense, 2),
            new("531", "ค่าใช้จ่ายในการขาย", "Selling Expenses", AccountType.Expense, 3),
            new("53110", "เงินเดือน/ค่าจ้าง (ฝ่ายขาย)", "Salaries - Sales", AccountType.Expense, 4),
            new("53120", "ค่าโฆษณาและส่งเสริมการขาย", "Advertising and Promotion", AccountType.Expense, 4),
            new("53130", "ค่าขนส่ง (ฝ่ายขาย)", "Freight-out / Delivery (Sales)", AccountType.Expense, 4),
            new("53140", "ค่านายหน้าการขาย", "Sales Commission", AccountType.Expense, 4),
            new("53150", "ค่าใช้จ่ายในการขายอื่นๆ", "Other Selling Expenses", AccountType.Expense, 4),

            // --- 54 ค่าใช้จ่ายในการบริหาร ---
            new("54", "ค่าใช้จ่ายในการบริหาร", "Administrative Expenses", AccountType.Expense, 2),
            new("541", "ค่าใช้จ่ายบุคลากร", "Staff Expenses", AccountType.Expense, 3),
            new("54111", "เงินเดือน/ค่าจ้าง (บริหาร)", "Salaries and Wages (Admin)", AccountType.Expense, 4),
            new("54112", "ค่าล่วงเวลา", "Overtime Pay", AccountType.Expense, 4),
            new("54113", "โบนัส", "Bonus", AccountType.Expense, 4),
            new("54114", "ค่าตอบแทนกรรมการ", "Director's Remuneration", AccountType.Expense, 4),
            new("54120", "เงินสมทบประกันสังคม (นายจ้าง)", "Social Security Fund (Employer Portion)", AccountType.Expense, 4),
            new("54121", "กองทุนเงินทดแทน", "Workmen's Compensation Fund", AccountType.Expense, 4),
            new("54122", "กองทุนสงเคราะห์ลูกจ้าง", "Employee Welfare Fund", AccountType.Expense, 4),
            new("54123", "สวัสดิการพนักงาน", "Employee Benefits", AccountType.Expense, 4),
            new("54124", "เงินสมทบกองทุนสำรองเลี้ยงชีพ (นายจ้าง)", "Provident Fund (Employer Portion)", AccountType.Expense, 4),
            new("54125", "ค่าใช้จ่ายบุคลากรอื่นๆ", "Other Staff Expenses", AccountType.Expense, 4),
            new("542", "ค่าโฆษณาและการตลาด", "Advertising and Marketing", AccountType.Expense, 3),
            new("54210", "ค่าโฆษณาและประชาสัมพันธ์", "Advertising and PR", AccountType.Expense, 4),
            new("54220", "ค่าการตลาด", "Marketing Expenses", AccountType.Expense, 4),
            new("543", "ค่าสาธารณูปโภค", "Utilities Expenses", AccountType.Expense, 3),
            new("54310", "ค่าไฟฟ้า", "Electricity Expense", AccountType.Expense, 4),
            new("54320", "ค่าน้ำประปา", "Water Supply Expense", AccountType.Expense, 4),
            new("54330", "ค่าโทรศัพท์", "Telephone Expense", AccountType.Expense, 4),
            new("54340", "ค่าอินเทอร์เน็ต", "Internet Expense", AccountType.Expense, 4),
            new("54350", "ค่าสาธารณูปโภคอื่นๆ", "Other Utilities", AccountType.Expense, 4),
            new("544", "ค่าใช้จ่ายสำนักงาน", "Office Expenses", AccountType.Expense, 3),
            new("54410", "ค่าเช่าสำนักงาน", "Office Rental", AccountType.Expense, 4),
            new("54420", "ค่าวัสดุสิ้นเปลืองสำนักงาน", "Office Supplies", AccountType.Expense, 4),
            new("54430", "ค่าซ่อมแซมและบำรุงรักษา", "Repairs and Maintenance", AccountType.Expense, 4),
            new("54440", "ค่าเดินทางและพาหนะ", "Travel and Transportation", AccountType.Expense, 4),
            new("54450", "ค่าเบี้ยเลี้ยง", "Per Diem / Subsistence Allowance", AccountType.Expense, 4),
            new("54460", "ค่ารับรอง / เลี้ยงรับรอง", "Entertainment / Hospitality", AccountType.Expense, 4),
            new("545", "ค่าเบี้ยประกันภัย", "Insurance Expenses", AccountType.Expense, 3),
            new("54510", "ค่าเบี้ยประกันภัย", "Insurance Premium", AccountType.Expense, 4),
            new("546", "ค่าธรรมเนียมวิชาชีพ", "Professional Fees", AccountType.Expense, 3),
            new("54610", "ค่าสอบบัญชี/ค่าตรวจสอบ", "Audit Fee", AccountType.Expense, 4),
            new("54620", "ค่าที่ปรึกษากฎหมาย", "Legal Fees", AccountType.Expense, 4),
            new("54630", "ค่าที่ปรึกษาอื่นๆ", "Other Consulting Fees", AccountType.Expense, 4),
            new("547", "ค่าบริการต่างๆ", "Service Charges", AccountType.Expense, 3),
            new("54710", "ค่าธรรมเนียมธนาคาร", "Bank Charges", AccountType.Expense, 4),
            new("54720", "ค่าบริการ IT / Software", "IT / Software Service Fee", AccountType.Expense, 4),
            new("54730", "ค่าบริการอื่นๆ", "Other Service Charges", AccountType.Expense, 4),
            new("548", "ค่าธรรมเนียมและค่าปรับ", "Fees and Penalties", AccountType.Expense, 3),
            new("54810", "ค่าธรรมเนียมราชการ", "Government Fees", AccountType.Expense, 4),
            new("54820", "ค่าปรับ/เบี้ยปรับ", "Fines and Penalties", AccountType.Expense, 4),
            new("549", "ค่าใช้จ่ายบริหารอื่นๆ", "Other Administrative Expenses", AccountType.Expense, 3),
            new("54910", "ค่าใช้จ่ายบริหารอื่นๆ", "Other Admin Expenses", AccountType.Expense, 4),
            new("54920", "ค่าฝึกอบรม/สัมมนา", "Training and Seminar", AccountType.Expense, 4),
            new("54930", "ค่าหนังสือพิมพ์/วารสาร", "Newspapers and Periodicals", AccountType.Expense, 4),
            new("54940", "เงินบริจาค/การกุศล", "Donations / Charity", AccountType.Expense, 4),
            new("54950", "ขาดทุนจากอัตราแลกเปลี่ยน", "Foreign Exchange Loss", AccountType.Expense, 4),

            // --- 55 ต้นทุนทางการเงิน ---
            new("55", "ต้นทุนทางการเงิน", "Finance Costs", AccountType.Expense, 2),
            new("551", "ต้นทุนทางการเงิน", "Finance Costs", AccountType.Expense, 3),
            new("55110", "ดอกเบี้ยจ่าย - เงินกู้ยืม", "Interest Expense - Loans", AccountType.Expense, 4),
            new("55120", "ดอกเบี้ยจ่าย - สัญญาเช่า", "Interest Expense - Lease Liabilities", AccountType.Expense, 4),
            new("55130", "ดอกเบี้ยจ่ายอื่นๆ", "Other Interest Expense", AccountType.Expense, 4),

            // --- 56 ค่าเสื่อมราคาและค่าตัดจำหน่าย ---
            new("56", "ค่าเสื่อมราคาและค่าตัดจำหน่าย", "Depreciation and Amortization", AccountType.Expense, 2),
            new("561", "ค่าเสื่อมราคา", "Depreciation Expense", AccountType.Expense, 3),
            new("56110", "ค่าเสื่อมราคา-อุปกรณ์สำนักงาน", "Depreciation - Office Equipment", AccountType.Expense, 4),
            new("56120", "ค่าเสื่อมราคา-คอมพิวเตอร์", "Depreciation - Computer Equipment", AccountType.Expense, 4),
            new("56130", "ค่าเสื่อมราคา-เครื่องตกแต่งสำนักงาน", "Depreciation - Furniture and Fixtures", AccountType.Expense, 4),
            new("56140", "ค่าเสื่อมราคา-ยานพาหนะ", "Depreciation - Vehicles", AccountType.Expense, 4),
            new("56150", "ค่าเสื่อมราคา-สินทรัพย์ตามสัญญาเช่า", "Depreciation - Right-of-Use Assets", AccountType.Expense, 4),
            new("56160", "ค่าเสื่อมราคา-เครื่องจักรและอุปกรณ์", "Depreciation - Machinery", AccountType.Expense, 4),
            new("56170", "ค่าเสื่อมราคา-อาคาร", "Depreciation - Buildings", AccountType.Expense, 4),
            new("562", "ค่าตัดจำหน่าย", "Amortization Expense", AccountType.Expense, 3),
            new("56210", "ค่าตัดจำหน่าย-ซอฟต์แวร์", "Amortization - Software", AccountType.Expense, 4),

            // --- 57 ค่าใช้จ่ายอื่น ---
            new("57", "ค่าใช้จ่ายอื่น", "Other Expenses", AccountType.Expense, 2),
            new("571", "ค่าใช้จ่ายอื่น", "Other Expenses", AccountType.Expense, 3),
            new("57110", "ขาดทุนจากการขายสินทรัพย์", "Loss on Disposal of Assets", AccountType.Expense, 4),
            new("57120", "ขาดทุนจากการด้อยค่า", "Impairment Loss", AccountType.Expense, 4),
            new("57130", "หนี้สงสัยจะสูญ/หนี้สูญ", "Bad Debt Expense / Doubtful Accounts", AccountType.Expense, 4),

            // --- 58 ภาษีเงินได้ ---
            new("58", "ภาษีเงินได้", "Income Tax Expense", AccountType.Expense, 2),
            new("580", "ภาษีเงินได้", "Income Tax Expense", AccountType.Expense, 3),
            new("58000", "ภาษีเงินได้นิติบุคคล", "Corporate Income Tax", AccountType.Expense, 4),
        };
    }

    // ==================== 3. ส่วนของเจ้าของ (Equity) ====================

    /// <summary>ส่วนของเจ้าของ - นิติบุคคล (บริษัทจำกัด)</summary>
    public static List<AccountTemplate> GetEquityJuristicPerson() => new()
    {
        new("3", "ส่วนของเจ้าของ", "Equity", AccountType.Equity, 1),
        new("31", "ทุน", "Share Capital", AccountType.Equity, 2),
        new("310", "ทุนจดทะเบียน", "Authorized Share Capital", AccountType.Equity, 3),
        new("31010", "ทุนจดทะเบียน", "Authorized Share Capital", AccountType.Equity, 4),
        new("31020", "ทุนที่ออกและชำระแล้ว", "Issued and Paid-up Capital", AccountType.Equity, 4),
        new("31030", "ส่วนเกินมูลค่าหุ้น", "Share Premium", AccountType.Equity, 4),
        new("32", "กำไร(ขาดทุน)สะสม", "Retained Earnings (Deficit)", AccountType.Equity, 2),
        new("320", "กำไร(ขาดทุน)สะสม", "Retained Earnings", AccountType.Equity, 3),
        new("32010", "กำไรสะสม – จัดสรรแล้ว (สำรองตามกฎหมาย)", "Appropriated - Legal Reserve", AccountType.Equity, 4),
        new("32020", "กำไรสะสม – ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
        new("33", "องค์ประกอบอื่นของส่วนของเจ้าของ", "Other Components of Equity", AccountType.Equity, 2),
        new("330", "องค์ประกอบอื่นของส่วนของเจ้าของ", "Other Components of Equity", AccountType.Equity, 3),
        new("33010", "ผลต่างจากการแปลงค่างบการเงิน", "Translation Differences", AccountType.Equity, 4),
        new("33020", "กำไร(ขาดทุน)เบ็ดเสร็จอื่น", "Other Comprehensive Income (Loss)", AccountType.Equity, 4),
    };

    /// <summary>ส่วนของเจ้าของ - บริษัทมหาชน</summary>
    public static List<AccountTemplate> GetEquityPublicCompany() => new()
    {
        new("3", "ส่วนของผู้ถือหุ้น", "Shareholders' Equity", AccountType.Equity, 1),
        new("31", "ทุนเรือนหุ้น", "Share Capital", AccountType.Equity, 2),
        new("310", "ทุนเรือนหุ้น", "Share Capital", AccountType.Equity, 3),
        new("31010", "ทุนจดทะเบียน", "Authorized Share Capital", AccountType.Equity, 4),
        new("31020", "หุ้นสามัญ", "Common Stock", AccountType.Equity, 4),
        new("31030", "หุ้นบุริมสิทธิ", "Preferred Stock", AccountType.Equity, 4),
        new("31040", "ส่วนเกินมูลค่าหุ้นสามัญ", "Share Premium - Common", AccountType.Equity, 4),
        new("31050", "ส่วนเกินมูลค่าหุ้นบุริมสิทธิ", "Share Premium - Preferred", AccountType.Equity, 4),
        new("31060", "หุ้นทุนซื้อคืน", "Treasury Stock", AccountType.Equity, 4),
        new("32", "กำไร(ขาดทุน)สะสม", "Retained Earnings (Deficit)", AccountType.Equity, 2),
        new("320", "กำไร(ขาดทุน)สะสม", "Retained Earnings", AccountType.Equity, 3),
        new("32010", "สำรองตามกฎหมาย", "Legal Reserve", AccountType.Equity, 4),
        new("32020", "กำไรสะสม – ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
        new("33", "องค์ประกอบอื่นของส่วนของผู้ถือหุ้น", "Other Components of Equity", AccountType.Equity, 2),
        new("330", "องค์ประกอบอื่นของส่วนของผู้ถือหุ้น", "Other Components of Equity", AccountType.Equity, 3),
        new("33010", "ผลต่างจากการแปลงค่างบการเงิน", "Translation Differences", AccountType.Equity, 4),
        new("33020", "กำไร(ขาดทุน)เบ็ดเสร็จอื่น", "Other Comprehensive Income (Loss)", AccountType.Equity, 4),
    };

    /// <summary>ส่วนของเจ้าของ - ห้างหุ้นส่วน</summary>
    public static List<AccountTemplate> GetEquityPartnership() => new()
    {
        new("3", "ส่วนของผู้เป็นหุ้นส่วน", "Partners' Equity", AccountType.Equity, 1),
        new("31", "ทุน", "Partners' Capital", AccountType.Equity, 2),
        new("310", "ทุนหุ้นส่วน", "Partners' Capital", AccountType.Equity, 3),
        new("31010", "ทุน – หุ้นส่วนผู้จัดการ", "Capital - Managing Partner", AccountType.Equity, 4),
        new("31020", "ทุน – หุ้นส่วนสามัญ", "Capital - General Partner", AccountType.Equity, 4),
        new("31030", "ถอนใช้ส่วนตัว – หุ้นส่วนผู้จัดการ", "Drawings - Managing Partner", AccountType.Equity, 4),
        new("31040", "ถอนใช้ส่วนตัว – หุ้นส่วนสามัญ", "Drawings - General Partner", AccountType.Equity, 4),
        new("32", "กำไร(ขาดทุน)สะสม", "Retained Earnings (Deficit)", AccountType.Equity, 2),
        new("320", "กำไร(ขาดทุน)สะสม", "Retained Earnings", AccountType.Equity, 3),
        new("32010", "กำไรสะสม – ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
    };

    /// <summary>ส่วนของเจ้าของ - กิจการเจ้าของคนเดียว</summary>
    public static List<AccountTemplate> GetEquityIndividual() => new()
    {
        new("3", "ส่วนของเจ้าของ", "Owner's Equity", AccountType.Equity, 1),
        new("31", "ทุน", "Owner's Capital", AccountType.Equity, 2),
        new("310", "ทุนเจ้าของ", "Owner's Capital", AccountType.Equity, 3),
        new("31010", "ทุน – เจ้าของกิจการ", "Owner's Capital", AccountType.Equity, 4),
        new("31020", "ถอนใช้ส่วนตัว", "Owner's Drawings", AccountType.Equity, 4),
        new("32", "กำไร(ขาดทุน)สะสม", "Retained Earnings (Deficit)", AccountType.Equity, 2),
        new("320", "กำไร(ขาดทุน)สะสม", "Retained Earnings", AccountType.Equity, 3),
        new("32010", "กำไรสะสม – ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
    };

    /// <summary>ส่วนของเจ้าของ - มูลนิธิ/สมาคม</summary>
    public static List<AccountTemplate> GetEquityFoundation() => new()
    {
        new("3", "ทุนสะสม", "Accumulated Fund", AccountType.Equity, 1),
        new("31", "ทุนสะสม", "Accumulated Fund", AccountType.Equity, 2),
        new("310", "ทุนสะสม", "Accumulated Fund", AccountType.Equity, 3),
        new("31010", "ทุนจดทะเบียน (มูลนิธิ)", "Registered Fund", AccountType.Equity, 4),
        new("31020", "เงินบริจาค/เงินอุดหนุน", "Donations / Grants Received", AccountType.Equity, 4),
        new("32", "ทุนสะสมจากการดำเนินงาน", "Accumulated Operating Fund", AccountType.Equity, 2),
        new("320", "ทุนสะสมจากการดำเนินงาน", "Accumulated Operating Fund", AccountType.Equity, 3),
        new("32010", "ทุนสะสม – ยังไม่ได้จัดสรร", "Unappropriated Accumulated Fund", AccountType.Equity, 4),
    };

    // ==================== Industry-specific accounts ====================

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจซื้อมาขายไป</summary>
    public static List<AccountTemplate> GetIndustryTrading() => new()
    {
        new("51150", "ส่วนลดรับ (สินค้า)", "Purchase Discount", AccountType.Expense, 4),
        new("51160", "ส่งคืนสินค้า", "Purchase Returns", AccountType.Expense, 4),
    };

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจผลิต</summary>
    public static List<AccountTemplate> GetIndustryManufacturing() => new()
    {
        new("51210", "วัตถุดิบ", "Raw Materials", AccountType.Expense, 4),
        new("51220", "ค่าแรงงานทางตรง", "Direct Labor", AccountType.Expense, 4),
        new("51230", "โสหุ้ยการผลิต", "Factory Overhead", AccountType.Expense, 4),
        new("51240", "งานระหว่างทำ", "Work in Process", AccountType.Expense, 4),
    };

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจบริการ</summary>
    public static List<AccountTemplate> GetIndustryService() => new()
    {
        new("52140", "ค่าเครื่องมือ/อุปกรณ์บริการ", "Service Tools and Equipment", AccountType.Expense, 4),
    };

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจอสังหาริมทรัพย์</summary>
    public static List<AccountTemplate> GetIndustryRealEstate() => new()
    {
        new("51310", "ต้นทุนโครงการพัฒนาอสังหาริมทรัพย์", "Real Estate Development Cost", AccountType.Expense, 4),
        new("51320", "ต้นทุนที่ดิน", "Land Cost", AccountType.Expense, 4),
        new("51330", "ต้นทุนก่อสร้าง", "Construction Cost", AccountType.Expense, 4),
    };

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจร้านอาหาร</summary>
    public static List<AccountTemplate> GetIndustryRestaurant() => new()
    {
        new("51410", "ต้นทุนวัตถุดิบอาหาร", "Food Ingredient Cost", AccountType.Expense, 4),
        new("51420", "ต้นทุนเครื่องดื่ม", "Beverage Cost", AccountType.Expense, 4),
        new("51430", "ต้นทุนบรรจุภัณฑ์", "Packaging Cost", AccountType.Expense, 4),
    };

    // ==================== Metadata & Builder ====================

    public record BusinessTypeInfo(BusinessType Type, string NameTh, string NameEn, string Description, string Icon, string EquityLabel);
    public record IndustryTypeInfo(IndustryType Type, string NameTh, string NameEn, string Description, string Icon);

    public static List<BusinessTypeInfo> GetAllBusinessTypes() => new()
    {
        new(BusinessType.JuristicPerson, "บริษัทจำกัด", "Limited Company", "บริษัทจำกัดตามประมวลกฎหมายแพ่งและพาณิชย์", "🏢", "ส่วนของผู้ถือหุ้น"),
        new(BusinessType.PublicCompany, "บริษัทมหาชนจำกัด", "Public Limited Company", "บริษัทมหาชนจำกัดตาม พ.ร.บ.บริษัทมหาชน", "🏛️", "ส่วนของผู้ถือหุ้น"),
        new(BusinessType.Partnership, "ห้างหุ้นส่วน", "Partnership", "ห้างหุ้นส่วนสามัญ/จำกัด", "🤝", "ส่วนของผู้เป็นหุ้นส่วน"),
        new(BusinessType.Individual, "กิจการเจ้าของคนเดียว", "Sole Proprietorship", "บุคคลธรรมดาประกอบกิจการ", "👤", "ส่วนของเจ้าของ"),
        new(BusinessType.Foundation, "มูลนิธิ", "Foundation", "มูลนิธิตามประมวลกฎหมายแพ่งและพาณิชย์", "🏥", "ทุนสะสม"),
        new(BusinessType.Association, "สมาคม", "Association", "สมาคมตามประมวลกฎหมายแพ่งและพาณิชย์", "🏘️", "ทุนสะสม"),
        new(BusinessType.Other, "อื่นๆ", "Other", "ประเภทธุรกิจอื่นๆ", "📋", "ส่วนของเจ้าของ"),
    };

    public static List<IndustryTypeInfo> GetAllIndustryTypes() => new()
    {
        new(IndustryType.General, "ทั่วไป", "General", "ธุรกิจทั่วไป", "📊"),
        new(IndustryType.Trading, "ซื้อมาขายไป", "Trading", "ธุรกิจซื้อมาขายไป", "🛒"),
        new(IndustryType.Service, "บริการ", "Service", "ธุรกิจบริการ", "🔧"),
        new(IndustryType.Manufacturing, "ผลิต/โรงงาน", "Manufacturing", "ธุรกิจผลิตสินค้า", "🏭"),
        new(IndustryType.Restaurant, "ร้านอาหาร", "Restaurant", "ธุรกิจร้านอาหาร", "🍽️"),
        new(IndustryType.Cafe, "คาเฟ่/เครื่องดื่ม", "Cafe", "ธุรกิจคาเฟ่และเครื่องดื่ม", "☕"),
        new(IndustryType.Retail, "ค้าปลีก", "Retail", "ธุรกิจค้าปลีก", "🏪"),
        new(IndustryType.Construction, "รับเหมาก่อสร้าง", "Construction", "ธุรกิจรับเหมาก่อสร้าง", "🏗️"),
        new(IndustryType.RealEstate, "อสังหาริมทรัพย์", "Real Estate", "ธุรกิจอสังหาริมทรัพย์", "🏠"),
        new(IndustryType.Technology, "เทคโนโลยี/ซอฟต์แวร์", "Technology", "ธุรกิจเทคโนโลยีและซอฟต์แวร์", "💻"),
        new(IndustryType.Healthcare, "สุขภาพ/คลินิก", "Healthcare", "ธุรกิจดูแลสุขภาพและคลินิก", "🏥"),
        new(IndustryType.Education, "การศึกษา", "Education", "ธุรกิจการศึกษา", "📚"),
        new(IndustryType.Beauty, "ความงาม/สปา", "Beauty", "ธุรกิจความงามและสปา", "💅"),
        new(IndustryType.Transportation, "ขนส่ง/โลจิสติกส์", "Transportation", "ธุรกิจขนส่งและโลจิสติกส์", "🚛"),
        new(IndustryType.Agriculture, "เกษตร", "Agriculture", "ธุรกิจเกษตรกรรม", "🌾"),
        new(IndustryType.Hotel, "โรงแรม/ที่พัก", "Hotel", "ธุรกิจโรงแรมและที่พัก", "🏨"),
        new(IndustryType.Ecommerce, "อีคอมเมิร์ซ/ออนไลน์", "E-Commerce", "ธุรกิจออนไลน์", "🛍️"),
        new(IndustryType.Freelance, "ฟรีแลนซ์", "Freelance", "ฟรีแลนซ์/อาชีพอิสระ", "💼"),
        new(IndustryType.Other, "อื่นๆ", "Other", "ประเภทอุตสาหกรรมอื่นๆ", "📋"),
    };

    /// <summary>สร้างผังบัญชีตามประเภทธุรกิจและอุตสาหกรรม</summary>
    public static List<AccountTemplate> GetTemplateByBusinessType(
        BusinessType businessType,
        IndustryType industryType = IndustryType.General)
    {
        var accounts = new List<AccountTemplate>();

        // 1. Common accounts (1,2,4,5)
        accounts.AddRange(GetCommonAccounts());

        // 2. Equity accounts (3) based on business type
        accounts.AddRange(businessType switch
        {
            BusinessType.JuristicPerson => GetEquityJuristicPerson(),
            BusinessType.PublicCompany  => GetEquityPublicCompany(),
            BusinessType.Partnership    => GetEquityPartnership(),
            BusinessType.Individual     => GetEquityIndividual(),
            BusinessType.Foundation or BusinessType.Association => GetEquityFoundation(),
            _ => GetEquityJuristicPerson()
        });

        // 3. Industry-specific accounts
        if (industryType != IndustryType.General)
        {
            accounts.AddRange(industryType switch
            {
                IndustryType.Trading or IndustryType.Retail or IndustryType.Ecommerce => GetIndustryTrading(),
                IndustryType.Manufacturing or IndustryType.Construction or IndustryType.Agriculture => GetIndustryManufacturing(),
                IndustryType.Service or IndustryType.Technology or IndustryType.Healthcare
                    or IndustryType.Education or IndustryType.Beauty or IndustryType.Transportation
                    or IndustryType.Freelance or IndustryType.Hotel => GetIndustryService(),
                IndustryType.RealEstate    => GetIndustryRealEstate(),
                IndustryType.Restaurant or IndustryType.Cafe => GetIndustryRestaurant(),
                _ => new List<AccountTemplate>()
            });
        }

        // Sort by code for proper hierarchy
        return accounts.OrderBy(a => a.Code).ToList();
    }
}
