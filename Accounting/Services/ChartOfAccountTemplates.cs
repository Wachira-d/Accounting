using Accounting.Models.Enums;

namespace Accounting.Services;

/// <summary>
/// ผังบัญชีมาตรฐานไทย (Thai Standard Chart of Accounts)
/// อ้างอิงตามมาตรฐานการรายงานทางการเงิน (TFRS/TAS) และ PEAK Account
/// รหัส 6 หลัก: หลักที่ 1 = ประเภท, หลัก 1-2 = หมวด, หลัก 1-3 = กลุ่ม, หลัก 1-6 = บัญชีย่อย
/// </summary>
public static class ChartOfAccountTemplates
{
    // (code, nameTh, nameEn, type, level)
    public record AccountTemplate(string Code, string NameTh, string NameEn, AccountType Type, int Level);

    /// <summary>
    /// ผังบัญชีพื้นฐานที่ใช้ร่วมกันทุกประเภทธุรกิจ (ยกเว้นหมวด 3 ส่วนของเจ้าของ)
    /// </summary>
    public static List<AccountTemplate> GetCommonAccounts()
    {
        return new List<AccountTemplate>
        {
            // ==================== 1. สินทรัพย์ (Assets) ====================
            new("10", "สินทรัพย์", "Assets", AccountType.Asset, 1),

            // 11 - สินทรัพย์หมุนเวียน
            new("11", "สินทรัพย์หมุนเวียน", "Current Assets", AccountType.Asset, 2),

            new("111", "เงินสดและเงินฝากธนาคาร", "Cash and Bank Deposits", AccountType.Asset, 3),
            new("111101", "เงินสด", "Cash on Hand", AccountType.Asset, 4),
            new("111201", "เงินฝากธนาคาร - ออมทรัพย์", "Bank Deposit - Savings", AccountType.Asset, 4),
            new("111202", "เงินฝากธนาคาร - กระแสรายวัน", "Bank Deposit - Current", AccountType.Asset, 4),
            new("111301", "เงินสดย่อย", "Petty Cash", AccountType.Asset, 4),

            new("112", "ลูกหนี้การค้าและตั๋วเงินรับ", "Trade Receivables", AccountType.Asset, 3),
            new("112101", "ลูกหนี้การค้า", "Accounts Receivable", AccountType.Asset, 4),
            new("112102", "ลูกหนี้การค้า - เช็ครับ", "Cheque Receivable", AccountType.Asset, 4),
            new("112103", "ตั๋วเงินรับ", "Notes Receivable", AccountType.Asset, 4),
            new("112199", "ค่าเผื่อหนี้สงสัยจะสูญ", "Allowance for Doubtful Accounts", AccountType.Asset, 4),

            new("113", "สินค้าคงเหลือ", "Inventories", AccountType.Asset, 3),
            new("113101", "สินค้าสำเร็จรูป", "Finished Goods", AccountType.Asset, 4),
            new("113102", "วัตถุดิบ", "Raw Materials", AccountType.Asset, 4),
            new("113103", "งานระหว่างทำ", "Work in Process", AccountType.Asset, 4),
            new("113104", "สินค้าระหว่างทาง", "Goods in Transit", AccountType.Asset, 4),

            new("114", "สินทรัพย์หมุนเวียนอื่น", "Other Current Assets", AccountType.Asset, 3),
            new("114101", "ภาษีซื้อ", "Input VAT", AccountType.Asset, 4),
            new("114102", "ภาษีซื้อยังไม่ถึงกำหนด", "Deferred Input VAT", AccountType.Asset, 4),
            new("114103", "ภาษีหัก ณ ที่จ่าย", "Withholding Tax Receivable", AccountType.Asset, 4),
            new("114104", "เงินมัดจำ", "Deposits", AccountType.Asset, 4),
            new("114105", "ค่าใช้จ่ายจ่ายล่วงหน้า", "Prepaid Expenses", AccountType.Asset, 4),
            new("114106", "รายได้ค้างรับ", "Accrued Revenue", AccountType.Asset, 4),
            new("114107", "เงินให้กู้ยืมระยะสั้น", "Short-term Loans", AccountType.Asset, 4),

            // 12 - สินทรัพย์ไม่หมุนเวียน
            new("12", "สินทรัพย์ไม่หมุนเวียน", "Non-Current Assets", AccountType.Asset, 2),

            new("121", "ที่ดิน อาคารและอุปกรณ์", "Property, Plant and Equipment", AccountType.Asset, 3),
            new("121101", "ที่ดิน", "Land", AccountType.Asset, 4),
            new("121201", "อาคาร", "Building", AccountType.Asset, 4),
            new("121202", "ค่าเสื่อมราคาสะสม - อาคาร", "Accum. Depreciation - Building", AccountType.Asset, 4),
            new("121301", "อุปกรณ์สำนักงาน", "Office Equipment", AccountType.Asset, 4),
            new("121302", "ค่าเสื่อมราคาสะสม - อุปกรณ์สำนักงาน", "Accum. Depreciation - Office Equipment", AccountType.Asset, 4),
            new("121401", "เครื่องจักร", "Machinery", AccountType.Asset, 4),
            new("121402", "ค่าเสื่อมราคาสะสม - เครื่องจักร", "Accum. Depreciation - Machinery", AccountType.Asset, 4),
            new("121501", "ยานพาหนะ", "Vehicles", AccountType.Asset, 4),
            new("121502", "ค่าเสื่อมราคาสะสม - ยานพาหนะ", "Accum. Depreciation - Vehicles", AccountType.Asset, 4),
            new("121601", "คอมพิวเตอร์และอุปกรณ์", "Computer and Equipment", AccountType.Asset, 4),
            new("121602", "ค่าเสื่อมราคาสะสม - คอมพิวเตอร์", "Accum. Depreciation - Computer", AccountType.Asset, 4),
            new("121701", "ส่วนปรับปรุงอาคารเช่า", "Leasehold Improvement", AccountType.Asset, 4),
            new("121702", "ค่าตัดจำหน่ายสะสม - ส่วนปรับปรุงอาคารเช่า", "Accum. Amort. - Leasehold Improvement", AccountType.Asset, 4),

            new("122", "สินทรัพย์ไม่มีตัวตน", "Intangible Assets", AccountType.Asset, 3),
            new("122101", "ซอฟต์แวร์คอมพิวเตอร์", "Computer Software", AccountType.Asset, 4),
            new("122102", "ค่าตัดจำหน่ายสะสม - ซอฟต์แวร์", "Accum. Amort. - Software", AccountType.Asset, 4),
            new("122201", "ค่าลิขสิทธิ์/สิทธิบัตร", "Copyright/Patent", AccountType.Asset, 4),

            new("123", "สินทรัพย์ไม่หมุนเวียนอื่น", "Other Non-Current Assets", AccountType.Asset, 3),
            new("123101", "เงินมัดจำค้ำประกัน", "Guarantee Deposits", AccountType.Asset, 4),
            new("123102", "เงินลงทุนระยะยาว", "Long-term Investments", AccountType.Asset, 4),

            // ==================== 2. หนี้สิน (Liabilities) ====================
            new("20", "หนี้สิน", "Liabilities", AccountType.Liability, 1),

            // 21 - หนี้สินหมุนเวียน
            new("21", "หนี้สินหมุนเวียน", "Current Liabilities", AccountType.Liability, 2),

            new("211", "เจ้าหนี้การค้าและตั๋วเงินจ่าย", "Trade Payables", AccountType.Liability, 3),
            new("211101", "เจ้าหนี้การค้า", "Accounts Payable", AccountType.Liability, 4),
            new("211102", "เจ้าหนี้การค้า - เช็คจ่าย", "Cheque Payable", AccountType.Liability, 4),
            new("211103", "ตั๋วเงินจ่าย", "Notes Payable", AccountType.Liability, 4),

            new("212", "ภาษีค้างจ่าย", "Taxes Payable", AccountType.Liability, 3),
            new("212101", "ภาษีขาย", "Output VAT", AccountType.Liability, 4),
            new("212102", "ภาษีขายยังไม่ถึงกำหนด", "Deferred Output VAT", AccountType.Liability, 4),
            new("212103", "ภาษีมูลค่าเพิ่มค้างจ่าย", "VAT Payable", AccountType.Liability, 4),
            new("212201", "ภาษีเงินได้หัก ณ ที่จ่ายค้างจ่าย", "Withholding Tax Payable", AccountType.Liability, 4),
            new("212202", "ภาษีเงินได้นิติบุคคลค้างจ่าย", "Corporate Income Tax Payable", AccountType.Liability, 4),

            new("213", "ค่าใช้จ่ายค้างจ่ายและหนี้สินหมุนเวียนอื่น", "Accrued Expenses and Other CL", AccountType.Liability, 3),
            new("213101", "เงินเดือนค้างจ่าย", "Accrued Salaries", AccountType.Liability, 4),
            new("213102", "ประกันสังคมค้างจ่าย", "Social Security Payable", AccountType.Liability, 4),
            new("213103", "ค่าใช้จ่ายค้างจ่าย", "Accrued Expenses", AccountType.Liability, 4),
            new("213104", "เงินรับล่วงหน้า", "Unearned Revenue", AccountType.Liability, 4),
            new("213105", "เงินกู้ยืมระยะสั้น", "Short-term Borrowings", AccountType.Liability, 4),
            new("213106", "ส่วนของเงินกู้ยืมระยะยาวที่ถึงกำหนดชำระ", "Current Portion of LT Debt", AccountType.Liability, 4),

            // 22 - หนี้สินไม่หมุนเวียน
            new("22", "หนี้สินไม่หมุนเวียน", "Non-Current Liabilities", AccountType.Liability, 2),

            new("221", "เงินกู้ยืมระยะยาว", "Long-term Borrowings", AccountType.Liability, 3),
            new("221101", "เงินกู้ยืมระยะยาวจากธนาคาร", "Long-term Bank Loans", AccountType.Liability, 4),
            new("221102", "เงินกู้ยืมระยะยาวจากบุคคลที่เกี่ยวข้อง", "Long-term Loans from Related Parties", AccountType.Liability, 4),
            new("221201", "หนี้สินตามสัญญาเช่า", "Lease Liabilities", AccountType.Liability, 4),

            new("222", "หนี้สินไม่หมุนเวียนอื่น", "Other Non-Current Liabilities", AccountType.Liability, 3),
            new("222101", "เงินประกันรับ", "Deposits Received", AccountType.Liability, 4),
            new("222102", "ประมาณการหนี้สินระยะยาว", "Long-term Provisions", AccountType.Liability, 4),

            // ==================== 4. รายได้ (Revenue) ====================
            new("40", "รายได้", "Revenue", AccountType.Revenue, 1),

            new("41", "รายได้จากการดำเนินงาน", "Operating Revenue", AccountType.Revenue, 2),

            new("411", "รายได้จากการขายสินค้า", "Sales Revenue", AccountType.Revenue, 3),
            new("411101", "รายได้จากการขายสินค้า", "Sales Revenue", AccountType.Revenue, 4),
            new("411102", "รับคืนสินค้าและส่วนลด", "Sales Returns and Allowances", AccountType.Revenue, 4),

            new("412", "รายได้จากการให้บริการ", "Service Revenue", AccountType.Revenue, 3),
            new("412101", "รายได้จากการให้บริการ", "Service Revenue", AccountType.Revenue, 4),

            new("42", "รายได้อื่น", "Other Income", AccountType.Revenue, 2),

            new("421", "รายได้อื่น", "Other Income", AccountType.Revenue, 3),
            new("421101", "ดอกเบี้ยรับ", "Interest Income", AccountType.Revenue, 4),
            new("421102", "กำไรจากการขายสินทรัพย์", "Gain on Sale of Assets", AccountType.Revenue, 4),
            new("421103", "กำไรจากอัตราแลกเปลี่ยน", "Foreign Exchange Gain", AccountType.Revenue, 4),
            new("421104", "รายได้เบ็ดเตล็ด", "Miscellaneous Income", AccountType.Revenue, 4),
            new("421105", "ส่วนลดรับ", "Discounts Received", AccountType.Revenue, 4),

            // ==================== 5. ค่าใช้จ่าย (Expenses) ====================
            new("50", "ค่าใช้จ่าย", "Expenses", AccountType.Expense, 1),

            new("51", "ต้นทุนขายและบริการ", "Cost of Sales and Services", AccountType.Expense, 2),

            new("511", "ต้นทุนขาย", "Cost of Goods Sold", AccountType.Expense, 3),
            new("511101", "ต้นทุนสินค้าที่ขาย", "Cost of Goods Sold", AccountType.Expense, 4),
            new("511102", "ซื้อสินค้า/วัตถุดิบ", "Purchases", AccountType.Expense, 4),
            new("511103", "ค่าขนส่งขาเข้า", "Freight-In", AccountType.Expense, 4),
            new("511104", "ส่งคืนสินค้าและส่วนลดรับ", "Purchase Returns and Allowances", AccountType.Expense, 4),

            new("512", "ต้นทุนบริการ", "Cost of Services", AccountType.Expense, 3),
            new("512101", "ต้นทุนการให้บริการ", "Cost of Services Rendered", AccountType.Expense, 4),

            new("52", "ค่าใช้จ่ายในการขาย", "Selling Expenses", AccountType.Expense, 2),

            new("521", "ค่าใช้จ่ายในการขาย", "Selling Expenses", AccountType.Expense, 3),
            new("521101", "เงินเดือนพนักงานขาย", "Sales Staff Salaries", AccountType.Expense, 4),
            new("521102", "ค่าคอมมิชชั่น", "Commission Expense", AccountType.Expense, 4),
            new("521103", "ค่าโฆษณาและส่งเสริมการขาย", "Advertising and Promotion", AccountType.Expense, 4),
            new("521104", "ค่าขนส่งขาออก", "Freight-Out", AccountType.Expense, 4),
            new("521105", "ค่าเสื่อมราคา - ส่วนขาย", "Depreciation - Selling", AccountType.Expense, 4),

            new("53", "ค่าใช้จ่ายในการบริหาร", "Administrative Expenses", AccountType.Expense, 2),

            new("531", "ค่าใช้จ่ายเกี่ยวกับพนักงาน", "Employee Expenses", AccountType.Expense, 3),
            new("531101", "เงินเดือนและค่าจ้าง", "Salaries and Wages", AccountType.Expense, 4),
            new("531102", "ค่าล่วงเวลา", "Overtime", AccountType.Expense, 4),
            new("531103", "โบนัส", "Bonus", AccountType.Expense, 4),
            new("531104", "เงินสมทบประกันสังคม", "Social Security Contribution", AccountType.Expense, 4),
            new("531105", "เงินสมทบกองทุนสำรองเลี้ยงชีพ", "Provident Fund Contribution", AccountType.Expense, 4),
            new("531106", "สวัสดิการพนักงาน", "Employee Benefits", AccountType.Expense, 4),

            new("532", "ค่าใช้จ่ายสำนักงาน", "Office Expenses", AccountType.Expense, 3),
            new("532101", "ค่าเช่าสำนักงาน", "Office Rent", AccountType.Expense, 4),
            new("532102", "ค่าไฟฟ้า", "Electricity Expense", AccountType.Expense, 4),
            new("532103", "ค่าน้ำประปา", "Water Expense", AccountType.Expense, 4),
            new("532104", "ค่าโทรศัพท์และอินเทอร์เน็ต", "Telephone and Internet", AccountType.Expense, 4),
            new("532105", "ค่าเครื่องเขียนและวัสดุสิ้นเปลือง", "Stationery and Supplies", AccountType.Expense, 4),
            new("532106", "ค่าไปรษณีย์และขนส่ง", "Postage and Delivery", AccountType.Expense, 4),
            new("532107", "ค่าซ่อมแซมและบำรุงรักษา", "Repairs and Maintenance", AccountType.Expense, 4),
            new("532108", "ค่าประกันภัย", "Insurance Expense", AccountType.Expense, 4),

            new("533", "ค่าเสื่อมราคาและค่าตัดจำหน่าย", "Depreciation and Amortization", AccountType.Expense, 3),
            new("533101", "ค่าเสื่อมราคา", "Depreciation Expense", AccountType.Expense, 4),
            new("533102", "ค่าตัดจำหน่าย", "Amortization Expense", AccountType.Expense, 4),

            new("534", "ค่าใช้จ่ายวิชาชีพ", "Professional Expenses", AccountType.Expense, 3),
            new("534101", "ค่าทำบัญชี", "Accounting Fee", AccountType.Expense, 4),
            new("534102", "ค่าสอบบัญชี", "Audit Fee", AccountType.Expense, 4),
            new("534103", "ค่าที่ปรึกษากฎหมาย", "Legal Fee", AccountType.Expense, 4),
            new("534104", "ค่าที่ปรึกษาอื่น", "Other Consulting Fee", AccountType.Expense, 4),

            new("535", "ค่าใช้จ่ายเกี่ยวกับภาษี", "Tax Expenses", AccountType.Expense, 3),
            new("535101", "ค่าอากรแสตมป์", "Stamp Duty", AccountType.Expense, 4),
            new("535102", "ค่าภาษีโรงเรือนและที่ดิน", "Property Tax", AccountType.Expense, 4),
            new("535103", "ค่าภาษีป้าย", "Signboard Tax", AccountType.Expense, 4),

            new("54", "ค่าใช้จ่ายอื่น", "Other Expenses", AccountType.Expense, 2),

            new("541", "ค่าใช้จ่ายอื่น", "Other Expenses", AccountType.Expense, 3),
            new("541101", "ดอกเบี้ยจ่าย", "Interest Expense", AccountType.Expense, 4),
            new("541102", "ค่าธรรมเนียมธนาคาร", "Bank Charges", AccountType.Expense, 4),
            new("541103", "ขาดทุนจากการขายสินทรัพย์", "Loss on Sale of Assets", AccountType.Expense, 4),
            new("541104", "ขาดทุนจากอัตราแลกเปลี่ยน", "Foreign Exchange Loss", AccountType.Expense, 4),
            new("541105", "หนี้สูญ", "Bad Debt Expense", AccountType.Expense, 4),
            new("541106", "ค่าปรับและเงินเพิ่ม", "Fines and Penalties", AccountType.Expense, 4),
            new("541107", "ค่าใช้จ่ายเบ็ดเตล็ด", "Miscellaneous Expenses", AccountType.Expense, 4),
            new("541108", "ค่าเลี้ยงรับรอง", "Entertainment Expense", AccountType.Expense, 4),
            new("541109", "ค่าเดินทาง", "Travel Expense", AccountType.Expense, 4),

            new("55", "ภาษีเงินได้", "Income Tax Expense", AccountType.Expense, 2),
            new("551", "ภาษีเงินได้นิติบุคคล", "Corporate Income Tax", AccountType.Expense, 3),
            new("551101", "ภาษีเงินได้นิติบุคคล", "Corporate Income Tax", AccountType.Expense, 4),
        };
    }

    /// <summary>
    /// หมวด 3 ส่วนของเจ้าของ - นิติบุคคล (บริษัทจำกัด)
    /// </summary>
    public static List<AccountTemplate> GetEquityJuristicPerson()
    {
        return new List<AccountTemplate>
        {
            new("30", "ส่วนของผู้ถือหุ้น", "Shareholders' Equity", AccountType.Equity, 1),

            new("31", "ทุนเรือนหุ้น", "Share Capital", AccountType.Equity, 2),
            new("311", "ทุนจดทะเบียน", "Authorized Share Capital", AccountType.Equity, 3),
            new("311101", "ทุนจดทะเบียน - หุ้นสามัญ", "Authorized Capital - Common Shares", AccountType.Equity, 4),
            new("311201", "ทุนที่ออกและชำระแล้ว - หุ้นสามัญ", "Issued and Paid-up Capital", AccountType.Equity, 4),
            new("311301", "ส่วนเกินมูลค่าหุ้นสามัญ", "Share Premium", AccountType.Equity, 4),

            new("32", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 2),
            new("321", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 3),
            new("321101", "กำไรสะสม - จัดสรรแล้ว (สำรองตามกฎหมาย)", "Appropriated - Legal Reserve", AccountType.Equity, 4),
            new("321201", "กำไรสะสม - ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
            new("321301", "กำไร(ขาดทุน)สุทธิประจำปี", "Net Income (Loss) for the Year", AccountType.Equity, 4),

            new("33", "องค์ประกอบอื่นของส่วนของผู้ถือหุ้น", "Other Components of Equity", AccountType.Equity, 2),
            new("331", "องค์ประกอบอื่น", "Other Components", AccountType.Equity, 3),
            new("331101", "เงินปันผลจ่าย", "Dividends", AccountType.Equity, 4),
        };
    }

    /// <summary>
    /// หมวด 3 ส่วนของเจ้าของ - บริษัทมหาชน
    /// </summary>
    public static List<AccountTemplate> GetEquityPublicCompany()
    {
        return new List<AccountTemplate>
        {
            new("30", "ส่วนของผู้ถือหุ้น", "Shareholders' Equity", AccountType.Equity, 1),

            new("31", "ทุนเรือนหุ้น", "Share Capital", AccountType.Equity, 2),
            new("311", "ทุนจดทะเบียน", "Authorized Share Capital", AccountType.Equity, 3),
            new("311101", "ทุนจดทะเบียน - หุ้นสามัญ", "Authorized Capital - Common Shares", AccountType.Equity, 4),
            new("311102", "ทุนจดทะเบียน - หุ้นบุริมสิทธิ", "Authorized Capital - Preferred Shares", AccountType.Equity, 4),
            new("311201", "ทุนที่ออกและชำระแล้ว - หุ้นสามัญ", "Issued and Paid-up - Common", AccountType.Equity, 4),
            new("311202", "ทุนที่ออกและชำระแล้ว - หุ้นบุริมสิทธิ", "Issued and Paid-up - Preferred", AccountType.Equity, 4),
            new("311301", "ส่วนเกินมูลค่าหุ้นสามัญ", "Share Premium - Common", AccountType.Equity, 4),
            new("311302", "ส่วนเกินมูลค่าหุ้นบุริมสิทธิ", "Share Premium - Preferred", AccountType.Equity, 4),
            new("311401", "หุ้นทุนซื้อคืน", "Treasury Shares", AccountType.Equity, 4),

            new("32", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 2),
            new("321", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 3),
            new("321101", "กำไรสะสม - จัดสรรแล้ว (สำรองตามกฎหมาย)", "Appropriated - Legal Reserve", AccountType.Equity, 4),
            new("321102", "กำไรสะสม - จัดสรรแล้ว (สำรองอื่น)", "Appropriated - Other Reserve", AccountType.Equity, 4),
            new("321201", "กำไรสะสม - ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
            new("321301", "กำไร(ขาดทุน)สุทธิประจำปี", "Net Income (Loss) for the Year", AccountType.Equity, 4),

            new("33", "องค์ประกอบอื่นของส่วนของผู้ถือหุ้น", "Other Components of Equity", AccountType.Equity, 2),
            new("331", "องค์ประกอบอื่น", "Other Components", AccountType.Equity, 3),
            new("331101", "ผลต่างจากการแปลงค่างบการเงิน", "Translation Adjustments", AccountType.Equity, 4),
            new("331102", "ผลกำไร(ขาดทุน)จากการวัดมูลค่า", "Revaluation Surplus", AccountType.Equity, 4),
            new("331201", "เงินปันผลจ่าย", "Dividends", AccountType.Equity, 4),
        };
    }

    /// <summary>
    /// หมวด 3 ส่วนของเจ้าของ - ห้างหุ้นส่วน
    /// </summary>
    public static List<AccountTemplate> GetEquityPartnership()
    {
        return new List<AccountTemplate>
        {
            new("30", "ส่วนของผู้เป็นหุ้นส่วน", "Partners' Equity", AccountType.Equity, 1),

            new("31", "ทุนหุ้นส่วน", "Partners' Capital", AccountType.Equity, 2),
            new("311", "ทุนหุ้นส่วน", "Partners' Capital", AccountType.Equity, 3),
            new("311101", "ทุน - หุ้นส่วนผู้จัดการ", "Capital - Managing Partner", AccountType.Equity, 4),
            new("311102", "ทุน - หุ้นส่วนคนที่ 2", "Capital - Partner 2", AccountType.Equity, 4),
            new("311201", "เงินถอน - หุ้นส่วนผู้จัดการ", "Drawings - Managing Partner", AccountType.Equity, 4),
            new("311202", "เงินถอน - หุ้นส่วนคนที่ 2", "Drawings - Partner 2", AccountType.Equity, 4),

            new("32", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 2),
            new("321", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 3),
            new("321101", "กำไรสะสม - ยังไม่ได้จัดสรร", "Unappropriated Retained Earnings", AccountType.Equity, 4),
            new("321201", "กำไร(ขาดทุน)สุทธิประจำปี", "Net Income (Loss) for the Year", AccountType.Equity, 4),
        };
    }

    /// <summary>
    /// หมวด 3 ส่วนของเจ้าของ - บุคคลธรรมดา
    /// </summary>
    public static List<AccountTemplate> GetEquityIndividual()
    {
        return new List<AccountTemplate>
        {
            new("30", "ส่วนของเจ้าของ", "Owner's Equity", AccountType.Equity, 1),

            new("31", "ทุนเจ้าของกิจการ", "Owner's Capital", AccountType.Equity, 2),
            new("311", "ทุนเจ้าของกิจการ", "Owner's Capital", AccountType.Equity, 3),
            new("311101", "ทุนเจ้าของกิจการ", "Owner's Capital", AccountType.Equity, 4),
            new("311201", "เงินถอนส่วนตัว", "Owner's Drawings", AccountType.Equity, 4),

            new("32", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 2),
            new("321", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 3),
            new("321101", "กำไรสะสม", "Retained Earnings", AccountType.Equity, 4),
            new("321201", "กำไร(ขาดทุน)สุทธิประจำปี", "Net Income (Loss) for the Year", AccountType.Equity, 4),
        };
    }

    /// <summary>
    /// หมวด 3 ส่วนของเจ้าของ - มูลนิธิ/สมาคม
    /// </summary>
    public static List<AccountTemplate> GetEquityFoundation()
    {
        return new List<AccountTemplate>
        {
            new("30", "สินทรัพย์สุทธิ", "Net Assets", AccountType.Equity, 1),

            new("31", "ทุนสะสม", "Accumulated Funds", AccountType.Equity, 2),
            new("311", "ทุนสะสม", "Accumulated Funds", AccountType.Equity, 3),
            new("311101", "ทุนเริ่มแรก", "Initial Fund", AccountType.Equity, 4),
            new("311102", "เงินรับบริจาคสะสม", "Accumulated Donations", AccountType.Equity, 4),
            new("311201", "ทุนสำรอง", "Reserve Fund", AccountType.Equity, 4),

            new("32", "รายได้สูง(ต่ำ)กว่าค่าใช้จ่ายสะสม", "Accumulated Surplus (Deficit)", AccountType.Equity, 2),
            new("321", "รายได้สูง(ต่ำ)กว่าค่าใช้จ่ายสะสม", "Accumulated Surplus (Deficit)", AccountType.Equity, 3),
            new("321101", "รายได้สูง(ต่ำ)กว่าค่าใช้จ่ายสะสม", "Accumulated Surplus (Deficit)", AccountType.Equity, 4),
            new("321201", "รายได้สูง(ต่ำ)กว่าค่าใช้จ่ายประจำปี", "Surplus (Deficit) for the Year", AccountType.Equity, 4),
        };
    }

    /// <summary>
    /// ดึงผังบัญชีตามประเภทธุรกิจ
    /// </summary>
    public static List<AccountTemplate> GetTemplateByBusinessType(BusinessType businessType)
    {
        var accounts = GetCommonAccounts();

        var equityAccounts = businessType switch
        {
            BusinessType.Individual => GetEquityIndividual(),
            BusinessType.Partnership => GetEquityPartnership(),
            BusinessType.PublicCompany => GetEquityPublicCompany(),
            BusinessType.Foundation or BusinessType.Association => GetEquityFoundation(),
            _ => GetEquityJuristicPerson(), // Default: JuristicPerson, Other
        };

        // Insert equity accounts after liabilities (position after "22" group)
        var insertIndex = accounts.FindIndex(a => a.Code == "40");
        if (insertIndex >= 0)
            accounts.InsertRange(insertIndex, equityAccounts);
        else
            accounts.AddRange(equityAccounts);

        // For Individual business type, replace corporate income tax with personal income tax
        if (businessType == BusinessType.Individual)
        {
            var corpTaxIdx = accounts.FindIndex(a => a.Code == "551101");
            if (corpTaxIdx >= 0)
                accounts[corpTaxIdx] = new AccountTemplate("551101", "ภาษีเงินได้บุคคลธรรมดา", "Personal Income Tax", AccountType.Expense, 4);

            var corpTaxGroupIdx = accounts.FindIndex(a => a.Code == "551");
            if (corpTaxGroupIdx >= 0)
                accounts[corpTaxGroupIdx] = new AccountTemplate("551", "ภาษีเงินได้บุคคลธรรมดา", "Personal Income Tax", AccountType.Expense, 3);

            var corpTaxParentIdx = accounts.FindIndex(a => a.Code == "55");
            if (corpTaxParentIdx >= 0)
                accounts[corpTaxParentIdx] = new AccountTemplate("55", "ภาษีเงินได้", "Income Tax Expense", AccountType.Expense, 2);

            // Remove corporate income tax payable, add personal income tax
            var corpTaxPayableIdx = accounts.FindIndex(a => a.Code == "212202");
            if (corpTaxPayableIdx >= 0)
                accounts[corpTaxPayableIdx] = new AccountTemplate("212202", "ภาษีเงินได้บุคคลธรรมดาค้างจ่าย", "Personal Income Tax Payable", AccountType.Liability, 4);
        }

        // For Foundation/Association, modify revenue/expense labels
        if (businessType is BusinessType.Foundation or BusinessType.Association)
        {
            // Add donation-specific revenue accounts
            var otherIncomeIdx = accounts.FindIndex(a => a.Code == "421105");
            if (otherIncomeIdx >= 0)
            {
                accounts.InsertRange(otherIncomeIdx + 1, new[]
                {
                    new AccountTemplate("421106", "เงินรับบริจาค", "Donation Income", AccountType.Revenue, 4),
                    new AccountTemplate("421107", "เงินอุดหนุนจากรัฐ", "Government Grants", AccountType.Revenue, 4),
                    new AccountTemplate("421108", "ค่าสมาชิก", "Membership Fees", AccountType.Revenue, 4),
                });
            }

            // Remove corporate income tax (foundations are typically exempt)
            accounts.RemoveAll(a => a.Code.StartsWith("55"));
        }

        return accounts;
    }
}
