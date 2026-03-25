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

    // ==================== Industry-specific accounts ====================

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจซื้อมาขายไป / ค้าปลีก</summary>
    public static List<AccountTemplate> GetIndustryTrading()
    {
        return new List<AccountTemplate>
        {
            new("113105", "สินค้าระหว่างตรวจนับ", "Goods Under Count", AccountType.Asset, 4),
            new("113106", "สินค้าชำรุด/เสื่อมสภาพ", "Damaged Goods", AccountType.Asset, 4),
            new("114108", "ค่ามัดจำสินค้า", "Goods Deposit", AccountType.Asset, 4),
            new("411103", "ส่วนลดการค้า", "Trade Discount", AccountType.Revenue, 4),
            new("411104", "รายได้จากการขายออนไลน์", "Online Sales Revenue", AccountType.Revenue, 4),
            new("511105", "ค่าบรรจุภัณฑ์", "Packaging Cost", AccountType.Expense, 4),
            new("521106", "ค่าตลาดออนไลน์/แพลตฟอร์ม", "Online Platform Fee", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับธุรกิจบริการ / ความงาม / สปา</summary>
    public static List<AccountTemplate> GetIndustryService()
    {
        return new List<AccountTemplate>
        {
            new("412102", "รายได้จากแพ็คเกจบริการ", "Package Service Revenue", AccountType.Revenue, 4),
            new("412103", "รายได้จากค่าบริการเสริม", "Additional Service Revenue", AccountType.Revenue, 4),
            new("412104", "รายได้จากสมาชิก/บัตรเติมเงิน", "Membership/Prepaid Card Revenue", AccountType.Revenue, 4),
            new("213107", "รายได้รับล่วงหน้า - แพ็คเกจ", "Deferred Revenue - Packages", AccountType.Liability, 4),
            new("213108", "รายได้รับล่วงหน้า - บัตรสมาชิก", "Deferred Revenue - Membership", AccountType.Liability, 4),
            new("512102", "ค่าวัสดุสิ้นเปลือง - บริการ", "Service Supplies", AccountType.Expense, 4),
            new("531107", "ค่าคอมมิชชั่นพนักงาน", "Staff Commission", AccountType.Expense, 4),
            new("531108", "ค่าคอมมิชชั่นช่าง/ผู้ให้บริการ", "Technician Commission", AccountType.Expense, 4),
            new("532109", "ค่าอุปกรณ์บริการ", "Service Equipment Expense", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับร้านอาหาร</summary>
    public static List<AccountTemplate> GetIndustryRestaurant()
    {
        return new List<AccountTemplate>
        {
            new("113105", "วัตถุดิบอาหาร", "Food Ingredients", AccountType.Asset, 4),
            new("113106", "เครื่องดื่มคงเหลือ", "Beverage Inventory", AccountType.Asset, 4),
            new("411103", "รายได้จากร้านอาหาร", "Restaurant Revenue", AccountType.Revenue, 4),
            new("411104", "รายได้จากเดลิเวอรี่", "Delivery Revenue", AccountType.Revenue, 4),
            new("411105", "รายได้ค่าบริการ (Service Charge)", "Service Charge Revenue", AccountType.Revenue, 4),
            new("511105", "ต้นทุนวัตถุดิบอาหาร", "Food Cost", AccountType.Expense, 4),
            new("511106", "ต้นทุนเครื่องดื่ม", "Beverage Cost", AccountType.Expense, 4),
            new("511107", "วัตถุดิบเสียหาย/หมดอายุ", "Food Waste/Spoilage", AccountType.Expense, 4),
            new("532109", "ค่าแก๊ส/เชื้อเพลิงครัว", "Kitchen Gas/Fuel", AccountType.Expense, 4),
            new("532110", "ค่าแพลตฟอร์มเดลิเวอรี่", "Delivery Platform Fee", AccountType.Expense, 4),
            new("531107", "ค่าคอมมิชชั่นพนักงาน", "Staff Commission", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับคาเฟ่/เครื่องดื่ม</summary>
    public static List<AccountTemplate> GetIndustryCafe()
    {
        return new List<AccountTemplate>
        {
            new("113105", "เมล็ดกาแฟ/ชา คงเหลือ", "Coffee/Tea Beans Inventory", AccountType.Asset, 4),
            new("113106", "วัตถุดิบเครื่องดื่ม", "Beverage Ingredients", AccountType.Asset, 4),
            new("113107", "บรรจุภัณฑ์/แก้ว", "Cups & Packaging", AccountType.Asset, 4),
            new("411103", "รายได้จากเครื่องดื่ม", "Beverage Revenue", AccountType.Revenue, 4),
            new("411104", "รายได้จากเบเกอรี่/ขนม", "Bakery Revenue", AccountType.Revenue, 4),
            new("411105", "รายได้จากเดลิเวอรี่", "Delivery Revenue", AccountType.Revenue, 4),
            new("511105", "ต้นทุนวัตถุดิบเครื่องดื่ม", "Beverage Material Cost", AccountType.Expense, 4),
            new("511106", "ต้นทุนเบเกอรี่/ขนม", "Bakery Cost", AccountType.Expense, 4),
            new("511107", "ค่าบรรจุภัณฑ์/แก้ว", "Cup & Packaging Cost", AccountType.Expense, 4),
            new("532109", "ค่าแพลตฟอร์มเดลิเวอรี่", "Delivery Platform Fee", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับรับเหมาก่อสร้าง</summary>
    public static List<AccountTemplate> GetIndustryConstruction()
    {
        return new List<AccountTemplate>
        {
            new("113105", "วัสดุก่อสร้าง", "Construction Materials", AccountType.Asset, 4),
            new("114108", "ค่างานระหว่างก่อสร้าง", "Construction Work in Progress", AccountType.Asset, 4),
            new("411103", "รายได้จากงานก่อสร้าง", "Construction Revenue", AccountType.Revenue, 4),
            new("411104", "รายได้จากงานรับเหมาช่วง", "Sub-contract Revenue", AccountType.Revenue, 4),
            new("511105", "ต้นทุนวัสดุก่อสร้าง", "Construction Material Cost", AccountType.Expense, 4),
            new("511106", "ค่าแรงงานก่อสร้าง", "Construction Labor Cost", AccountType.Expense, 4),
            new("511107", "ค่ารับเหมาช่วง", "Sub-contractor Cost", AccountType.Expense, 4),
            new("511108", "ค่าเช่าเครื่องจักร/อุปกรณ์", "Equipment Rental", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับเทคโนโลยี/ซอฟต์แวร์</summary>
    public static List<AccountTemplate> GetIndustryTechnology()
    {
        return new List<AccountTemplate>
        {
            new("412102", "รายได้จากพัฒนาซอฟต์แวร์", "Software Development Revenue", AccountType.Revenue, 4),
            new("412103", "รายได้จาก SaaS/สมาชิก", "SaaS/Subscription Revenue", AccountType.Revenue, 4),
            new("412104", "รายได้จากที่ปรึกษา IT", "IT Consulting Revenue", AccountType.Revenue, 4),
            new("213107", "รายได้รับล่วงหน้า - สมาชิก", "Deferred Revenue - Subscription", AccountType.Liability, 4),
            new("512102", "ต้นทุนเซิร์ฟเวอร์/โฮสติ้ง", "Server/Hosting Cost", AccountType.Expense, 4),
            new("512103", "ค่าบริการ Cloud", "Cloud Service Cost", AccountType.Expense, 4),
            new("532109", "ค่าลิขสิทธิ์ซอฟต์แวร์", "Software License", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับโรงแรม/ที่พัก</summary>
    public static List<AccountTemplate> GetIndustryHotel()
    {
        return new List<AccountTemplate>
        {
            new("113105", "วัสดุสิ้นเปลืองห้องพัก", "Room Supplies", AccountType.Asset, 4),
            new("411103", "รายได้จากห้องพัก", "Room Revenue", AccountType.Revenue, 4),
            new("412102", "รายได้จากอาหารและเครื่องดื่ม", "F&B Revenue", AccountType.Revenue, 4),
            new("412103", "รายได้จากบริการซักรีด", "Laundry Revenue", AccountType.Revenue, 4),
            new("412104", "รายได้จากห้องประชุม/จัดเลี้ยง", "Banquet Revenue", AccountType.Revenue, 4),
            new("213107", "เงินจอง/มัดจำห้องพัก", "Room Deposit Liability", AccountType.Liability, 4),
            new("511105", "ต้นทุนอาหารและเครื่องดื่ม", "F&B Cost", AccountType.Expense, 4),
            new("532109", "ค่าซักรีด", "Laundry Expense", AccountType.Expense, 4),
            new("532110", "ค่าแพลตฟอร์ม OTA", "OTA Commission", AccountType.Expense, 4),
            new("531107", "ค่าคอมมิชชั่นพนักงาน", "Staff Commission", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับอีคอมเมิร์ซ</summary>
    public static List<AccountTemplate> GetIndustryEcommerce()
    {
        return new List<AccountTemplate>
        {
            new("113105", "สินค้าคงเหลือ - คลังออนไลน์", "Online Warehouse Stock", AccountType.Asset, 4),
            new("411103", "รายได้จากขายออนไลน์", "Online Sales Revenue", AccountType.Revenue, 4),
            new("411104", "รายได้จากค่าจัดส่ง", "Shipping Revenue", AccountType.Revenue, 4),
            new("511105", "ค่าบรรจุภัณฑ์", "Packaging Cost", AccountType.Expense, 4),
            new("521106", "ค่าแพลตฟอร์มออนไลน์", "Marketplace Platform Fee", AccountType.Expense, 4),
            new("521107", "ค่าโฆษณาออนไลน์", "Online Advertising", AccountType.Expense, 4),
            new("521108", "ค่าจัดส่ง/ขนส่ง", "Shipping/Delivery Cost", AccountType.Expense, 4),
            new("521109", "ค่า Payment Gateway", "Payment Gateway Fee", AccountType.Expense, 4),
        };
    }

    /// <summary>บัญชีเพิ่มเติมสำหรับการผลิต/โรงงาน</summary>
    public static List<AccountTemplate> GetIndustryManufacturing()
    {
        return new List<AccountTemplate>
        {
            new("113105", "วัตถุดิบทางตรง", "Direct Materials", AccountType.Asset, 4),
            new("113106", "วัตถุดิบทางอ้อม", "Indirect Materials", AccountType.Asset, 4),
            new("113107", "สินค้าระหว่างผลิต", "Work in Process - Production", AccountType.Asset, 4),
            new("511105", "ค่าวัตถุดิบทางตรง", "Direct Material Cost", AccountType.Expense, 4),
            new("511106", "ค่าแรงงานทางตรง", "Direct Labor Cost", AccountType.Expense, 4),
            new("511107", "ค่าโสหุ้ยการผลิต", "Manufacturing Overhead", AccountType.Expense, 4),
            new("511108", "ค่าเสื่อมราคาเครื่องจักร", "Machine Depreciation", AccountType.Expense, 4),
            new("511109", "ค่าซ่อมบำรุงเครื่องจักร", "Machine Maintenance", AccountType.Expense, 4),
            new("511110", "ค่าพลังงาน/ไฟฟ้าโรงงาน", "Factory Utilities", AccountType.Expense, 4),
            new("511111", "ของเสียจากการผลิต", "Production Waste", AccountType.Expense, 4),
        };
    }

    // ==================== Industry metadata ====================

    public record IndustryInfo(IndustryType Type, string NameTh, string NameEn, string Description, string Icon);

    public static List<IndustryInfo> GetAllIndustryTypes()
    {
        return new List<IndustryInfo>
        {
            new(IndustryType.General, "ทั่วไป", "General", "ธุรกิจทั่วไป ใช้ผังบัญชีมาตรฐาน", "🏢"),
            new(IndustryType.Trading, "ซื้อมาขายไป", "Trading", "ธุรกิจซื้อสินค้ามาจำหน่ายต่อ ทั้งค้าส่งและค้าปลีก", "🛒"),
            new(IndustryType.Service, "บริการ", "Service", "ธุรกิจให้บริการ เช่น ร้านทำเล็บ สปา ซ่อม ทำความสะอาด", "💅"),
            new(IndustryType.Manufacturing, "ผลิต/โรงงาน", "Manufacturing", "ธุรกิจผลิตสินค้าจากวัตถุดิบ มีสายการผลิต", "🏭"),
            new(IndustryType.Restaurant, "ร้านอาหาร", "Restaurant", "ร้านอาหาร ภัตตาคาร ครัวกลาง รองรับ dine-in/delivery", "🍜"),
            new(IndustryType.Cafe, "คาเฟ่/เครื่องดื่ม", "Cafe", "ร้านกาแฟ ชานมไข่มุก เครื่องดื่ม เบเกอรี่", "☕"),
            new(IndustryType.Retail, "ค้าปลีก", "Retail", "ร้านค้าปลีก ร้านสะดวกซื้อ มินิมาร์ท", "🏪"),
            new(IndustryType.Construction, "รับเหมาก่อสร้าง", "Construction", "ธุรกิจรับเหมาก่อสร้าง ปรับปรุงอาคาร งานโครงการ", "🏗️"),
            new(IndustryType.RealEstate, "อสังหาริมทรัพย์", "Real Estate", "ธุรกิจพัฒนาอสังหาริมทรัพย์ นายหน้า บ้านจัดสรร", "🏠"),
            new(IndustryType.Technology, "เทคโนโลยี/IT", "Technology", "ธุรกิจซอฟต์แวร์ SaaS ที่ปรึกษา IT พัฒนาเว็บ/แอป", "💻"),
            new(IndustryType.Healthcare, "สุขภาพ/คลินิก", "Healthcare", "คลินิก ร้านยา ศูนย์สุขภาพ ทันตกรรม", "🏥"),
            new(IndustryType.Education, "การศึกษา", "Education", "โรงเรียนกวดวิชา สถาบันสอน คอร์สออนไลน์", "🎓"),
            new(IndustryType.Beauty, "ความงาม/สปา", "Beauty & Spa", "ร้านทำเล็บ ร้านทำผม สปา คลินิกเสริมความงาม", "💆"),
            new(IndustryType.Transportation, "ขนส่ง/โลจิสติกส์", "Transportation", "ธุรกิจขนส่ง โลจิสติกส์ รถเช่า Messenger", "🚛"),
            new(IndustryType.Agriculture, "เกษตร", "Agriculture", "เกษตรกรรม ฟาร์ม ปศุสัตว์ ประมง", "🌾"),
            new(IndustryType.Hotel, "โรงแรม/ที่พัก", "Hotel", "โรงแรม รีสอร์ท เกสต์เฮาส์ Airbnb", "🏨"),
            new(IndustryType.Ecommerce, "อีคอมเมิร์ซ", "E-commerce", "ขายสินค้าออนไลน์ Shopee Lazada Facebook", "📱"),
            new(IndustryType.Freelance, "ฟรีแลนซ์", "Freelance", "ทำงานอิสระ รับงานเป็นโปรเจค กราฟิก เขียนโปรแกรม", "🎨"),
            new(IndustryType.Other, "อื่นๆ", "Other", "ธุรกิจประเภทอื่นที่ไม่อยู่ในรายการ", "📋"),
        };
    }

    // ==================== Business type metadata ====================

    public record BusinessTypeInfo(BusinessType Type, string NameTh, string NameEn, string Description, string Icon, string EquityLabel);

    public static List<BusinessTypeInfo> GetAllBusinessTypes()
    {
        return new List<BusinessTypeInfo>
        {
            new(BusinessType.Individual, "บุคคลธรรมดา", "Individual", "เจ้าของคนเดียว ไม่จดทะเบียนนิติบุคคล เสียภาษีเงินได้บุคคลธรรมดา", "👤", "ทุนเจ้าของกิจการ"),
            new(BusinessType.JuristicPerson, "บริษัทจำกัด", "Limited Company", "จดทะเบียนเป็นบริษัทจำกัด มีผู้ถือหุ้น เสียภาษีเงินได้นิติบุคคล", "🏢", "ทุนเรือนหุ้น (หุ้นสามัญ)"),
            new(BusinessType.Partnership, "ห้างหุ้นส่วน", "Partnership", "ห้างหุ้นส่วนสามัญ/จำกัด มีหุ้นส่วนตั้งแต่ 2 คนขึ้นไป", "🤝", "ทุนหุ้นส่วน"),
            new(BusinessType.PublicCompany, "บริษัทมหาชน", "Public Company", "บริษัทมหาชนจำกัด จดทะเบียนในตลาดหลักทรัพย์", "🏛️", "ทุนเรือนหุ้น (สามัญ+บุริมสิทธิ)"),
            new(BusinessType.Foundation, "มูลนิธิ", "Foundation", "มูลนิธิ องค์กรไม่แสวงหากำไร ได้รับยกเว้นภาษีเงินได้", "🏫", "ทุนสะสม/เงินบริจาค"),
            new(BusinessType.Association, "สมาคม", "Association", "สมาคม สโมสร องค์กรสมาชิก", "🏟️", "ทุนสะสม/ค่าสมาชิก"),
            new(BusinessType.Other, "อื่นๆ", "Other", "ประเภทกิจการอื่นที่ไม่อยู่ในรายการ", "📋", "ส่วนของเจ้าของ"),
        };
    }

    // ==================== Main template builder ====================

    /// <summary>
    /// ดึงผังบัญชีตามประเภทธุรกิจ (backward compatible)
    /// </summary>
    public static List<AccountTemplate> GetTemplateByBusinessType(BusinessType businessType)
    {
        return GetTemplateByBusinessType(businessType, IndustryType.General);
    }

    /// <summary>
    /// ดึงผังบัญชีตามประเภทธุรกิจ + ลักษณะธุรกิจ
    /// </summary>
    public static List<AccountTemplate> GetTemplateByBusinessType(BusinessType businessType, IndustryType industryType)
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

            var corpTaxPayableIdx = accounts.FindIndex(a => a.Code == "212202");
            if (corpTaxPayableIdx >= 0)
                accounts[corpTaxPayableIdx] = new AccountTemplate("212202", "ภาษีเงินได้บุคคลธรรมดาค้างจ่าย", "Personal Income Tax Payable", AccountType.Liability, 4);
        }

        // For Foundation/Association, modify revenue/expense labels
        if (businessType is BusinessType.Foundation or BusinessType.Association)
        {
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
            accounts.RemoveAll(a => a.Code.StartsWith("55"));
        }

        // Add industry-specific accounts
        var industryAccounts = industryType switch
        {
            IndustryType.Trading or IndustryType.Retail => GetIndustryTrading(),
            IndustryType.Service or IndustryType.Beauty or IndustryType.Healthcare => GetIndustryService(),
            IndustryType.Restaurant => GetIndustryRestaurant(),
            IndustryType.Cafe => GetIndustryCafe(),
            IndustryType.Construction => GetIndustryConstruction(),
            IndustryType.Technology => GetIndustryTechnology(),
            IndustryType.Hotel => GetIndustryHotel(),
            IndustryType.Ecommerce => GetIndustryEcommerce(),
            IndustryType.Manufacturing => GetIndustryManufacturing(),
            IndustryType.Freelance => GetIndustryService(), // Freelance uses service template
            _ => new List<AccountTemplate>()
        };

        // Merge industry accounts: insert after matching parent group
        foreach (var ia in industryAccounts)
        {
            // Check for duplicates
            if (accounts.Any(a => a.Code == ia.Code)) continue;

            // Find insert position: after last account in same group
            var groupPrefix = ia.Code.Length >= 3 ? ia.Code[..3] : ia.Code[..2];
            var lastInGroup = accounts.FindLastIndex(a => a.Code.StartsWith(groupPrefix) && a.Code != groupPrefix);
            if (lastInGroup >= 0)
                accounts.Insert(lastInGroup + 1, ia);
            else
                accounts.Add(ia);
        }

        return accounts;
    }
}
