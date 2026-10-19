using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **golden ช่องคู่สัญญา §86/4 ของชุดกระดาษ replay** (ทีม OCR 15 · ฝ่ายค้าน OCR 13 P1-3) — ชื่อ/เลขผู้เสียภาษี/สาขา ของผู้ขายและผู้ซื้อ
/// ต่อใบ ตามที่ <c>ParseThaiDocument</c> → <c>SmartFieldExtractor.Enrich</c> ตอบ<b>วันนี้</b>
///
/// <para>═══ ทำไมมี ═══ ทีม OCR 13 เปลี่ยนตัวอ่านที่ใช้ร่วมกันหกตัว (<c>ThaiCompanyName</c> · ป้ายผู้ซื้อบนบรรทัด · ผู้ออกบิลรัฐ · สาขาจากบรรทัดเลขผู้ซื้อ ·
/// การแทนสาขา · ชื่อผูกเลขผู้ขาย) แล้วอ้าง "0 เปลี่ยนนอก VendorAddress" ซึ่ง snapshot ตรวจไม่ได้ เพราะไม่มีแถวช่องคู่สัญญาเลย ⇒ ชื่อผู้ขาย/ผู้ซื้อสลับคอลัมน์
/// บนหัวกระดาษสองคอลัมน์ (ฝ่ายค้าน OCR 13 P1-2) ผ่านไปเงียบ ๆ</para>
///
/// <para>═══ กติกา ═══ แถวที่ล้ม = การแก้เปลี่ยนคำตอบของใบนั้น — แก้ golden ได้เฉพาะเมื่อ<b>อธิบายทุกแถวได้</b>ในข้อความคอมมิต (กฎเหล็ก #4 §H) ·
/// ค่าตั้งต้นสร้างที่ 9cbc7166 · ผลต่างจาก OCR 10 base 7ca8c0d8 (ข้อความ corpus ชุดเดียวกันรันผ่านโค้ดสองรุ่น) 9 แถว อธิบายใน CHANGELOG / docs/lessons/ocr-pipeline.md §OCR 15 ·
/// tab ในค่าแสดงเป็น ⇥ · ว่าง = (ว่าง)</para>
/// </summary>
public class OcrReplayPartyGoldenTests
{
    private static readonly string[] PartyFields = { "BuyerBranch", "BuyerName", "BuyerTaxId", "VendorBranch", "VendorName", "VendorTaxId" };

    /// <summary>แถวช่องคู่สัญญาของทุกใบใน <see cref="OcrReplayHarness.Corpus"/> — รูปเดียวกับ <see cref="OcrReplayHarness.Snapshot"/></summary>
    internal static string PartySnapshot(IReadOnlyList<ReplayAnswer> answers)
        => OcrReplayHarness.Snapshot(answers.Where(a => PartyFields.Contains(a.Field))
                .Select(a => a with { Value = a.Value?.Replace("\t", "⇥").Replace("\n", "⏎") }).ToList())
            .TrimEnd('\n');

    private const string Golden = """
        boonsap-original-copy-pages · BuyerBranch = (ว่าง)
        boonsap-original-copy-pages · BuyerName = บริษัท บุญทรัพย์ถาวรจำากัด
        boonsap-original-copy-pages · BuyerTaxId = 0205569010244
        boonsap-original-copy-pages · VendorBranch = (ว่าง)
        boonsap-original-copy-pages · VendorName = หจก. แอมแฮปปี้เนสในนามบริษัทบุญทรัพย์ถาวรจำากัด
        boonsap-original-copy-pages · VendorTaxId = 0203562005871
        buyer-name-truncated · BuyerBranch = (ว่าง)
        buyer-name-truncated · BuyerName = (ว่าง)
        buyer-name-truncated · BuyerTaxId = (ว่าง)
        buyer-name-truncated · VendorBranch = (ว่าง)
        buyer-name-truncated · VendorName = หจก. แอมแฮปปี้เนส
        buyer-name-truncated · VendorTaxId = (ว่าง)
        crc-thaiwatsadu-srcie75384-positional · BuyerBranch = (ว่าง)
        crc-thaiwatsadu-srcie75384-positional · BuyerName = ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส
        crc-thaiwatsadu-srcie75384-positional · BuyerTaxId = 0203562005871
        crc-thaiwatsadu-srcie75384-positional · VendorBranch = 00050
        crc-thaiwatsadu-srcie75384-positional · VendorName = บริษัท ซีอาร์ซีไทวัสดุ จำกัด
        crc-thaiwatsadu-srcie75384-positional · VendorTaxId = 0105555021215
        deposit-form-row-zero · BuyerBranch = (ว่าง)
        deposit-form-row-zero · BuyerName = (ว่าง)
        deposit-form-row-zero · BuyerTaxId = (ว่าง)
        deposit-form-row-zero · VendorBranch = (ว่าง)
        deposit-form-row-zero · VendorName = (ว่าง)
        deposit-form-row-zero · VendorTaxId = (ว่าง)
        deposit-installment-invoice · BuyerBranch = (ว่าง)
        deposit-installment-invoice · BuyerName = (ว่าง)
        deposit-installment-invoice · BuyerTaxId = (ว่าง)
        deposit-installment-invoice · VendorBranch = (ว่าง)
        deposit-installment-invoice · VendorName = (ว่าง)
        deposit-installment-invoice · VendorTaxId = (ว่าง)
        deposit-real · BuyerBranch = (ว่าง)
        deposit-real · BuyerName = (ว่าง)
        deposit-real · BuyerTaxId = (ว่าง)
        deposit-real · VendorBranch = (ว่าง)
        deposit-real · VendorName = (ว่าง)
        deposit-real · VendorTaxId = (ว่าง)
        discount-header-invoice · BuyerBranch = (ว่าง)
        discount-header-invoice · BuyerName = (ว่าง)
        discount-header-invoice · BuyerTaxId = (ว่าง)
        discount-header-invoice · VendorBranch = (ว่าง)
        discount-header-invoice · VendorName = (ว่าง)
        discount-header-invoice · VendorTaxId = (ว่าง)
        export-zero-rated · BuyerBranch = (ว่าง)
        export-zero-rated · BuyerName = (ว่าง)
        export-zero-rated · BuyerTaxId = (ว่าง)
        export-zero-rated · VendorBranch = (ว่าง)
        export-zero-rated · VendorName = (ว่าง)
        export-zero-rated · VendorTaxId = (ว่าง)
        goods-fractional-qty-no-price · BuyerBranch = (ว่าง)
        goods-fractional-qty-no-price · BuyerName = (ว่าง)
        goods-fractional-qty-no-price · BuyerTaxId = (ว่าง)
        goods-fractional-qty-no-price · VendorBranch = (ว่าง)
        goods-fractional-qty-no-price · VendorName = (ว่าง)
        goods-fractional-qty-no-price · VendorTaxId = (ว่าง)
        goods-price-read-as-amount · BuyerBranch = (ว่าง)
        goods-price-read-as-amount · BuyerName = (ว่าง)
        goods-price-read-as-amount · BuyerTaxId = (ว่าง)
        goods-price-read-as-amount · VendorBranch = (ว่าง)
        goods-price-read-as-amount · VendorName = (ว่าง)
        goods-price-read-as-amount · VendorTaxId = (ว่าง)
        goods-qty-price-column-lost · BuyerBranch = (ว่าง)
        goods-qty-price-column-lost · BuyerName = (ว่าง)
        goods-qty-price-column-lost · BuyerTaxId = (ว่าง)
        goods-qty-price-column-lost · VendorBranch = (ว่าง)
        goods-qty-price-column-lost · VendorName = (ว่าง)
        goods-qty-price-column-lost · VendorTaxId = (ว่าง)
        hardware-bill-discount · BuyerBranch = (ว่าง)
        hardware-bill-discount · BuyerName = (ว่าง)
        hardware-bill-discount · BuyerTaxId = (ว่าง)
        hardware-bill-discount · VendorBranch = 00000
        hardware-bill-discount · VendorName = บริษัท ไทยวัสดุภัณฑ์ก่อสร้าง จำกัด
        hardware-bill-discount · VendorTaxId = (ว่าง)
        hardware-bill-discount-amounts-only · BuyerBranch = (ว่าง)
        hardware-bill-discount-amounts-only · BuyerName = (ว่าง)
        hardware-bill-discount-amounts-only · BuyerTaxId = (ว่าง)
        hardware-bill-discount-amounts-only · VendorBranch = 00000
        hardware-bill-discount-amounts-only · VendorName = บริษัท ไทยวัสดุภัณฑ์ก่อสร้าง จำกัด
        hardware-bill-discount-amounts-only · VendorTaxId = (ว่าง)
        luckyway-swapped · BuyerBranch = (ว่าง)
        luckyway-swapped · BuyerName = (ว่าง)
        luckyway-swapped · BuyerTaxId = (ว่าง)
        luckyway-swapped · VendorBranch = (ว่าง)
        luckyway-swapped · VendorName = บริษัท ลักกี้เวย์ จำกัด
        luckyway-swapped · VendorTaxId = (ว่าง)
        makro-correct · BuyerBranch = (ว่าง)
        makro-correct · BuyerName = (ว่าง)
        makro-correct · BuyerTaxId = (ว่าง)
        makro-correct · VendorBranch = (ว่าง)
        makro-correct · VendorName = บริษัท สยามแม็คโคร จำกัด (มหาชน)
        makro-correct · VendorTaxId = (ว่าง)
        makro-p1p2-discount-rows-as-items-synthetic · BuyerBranch = 00000
        makro-p1p2-discount-rows-as-items-synthetic · BuyerName = หจก. แอมแฮปปี้เนส
        makro-p1p2-discount-rows-as-items-synthetic · BuyerTaxId = 0107567000414
        makro-p1p2-discount-rows-as-items-synthetic · VendorBranch = 03060
        makro-p1p2-discount-rows-as-items-synthetic · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-p1p2-discount-rows-as-items-synthetic · VendorTaxId = 0203562005871
        makro-p1p2-no-row-codes-synthetic · BuyerBranch = 00000
        makro-p1p2-no-row-codes-synthetic · BuyerName = หจก. แอมแฮปปี้เนส
        makro-p1p2-no-row-codes-synthetic · BuyerTaxId = 0107567000414
        makro-p1p2-no-row-codes-synthetic · VendorBranch = 03060
        makro-p1p2-no-row-codes-synthetic · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-p1p2-no-row-codes-synthetic · VendorTaxId = 0203562005871
        makro-p1p2-vatcode-synthetic · BuyerBranch = 00000
        makro-p1p2-vatcode-synthetic · BuyerName = หจก. แอมแฮปปี้เนส
        makro-p1p2-vatcode-synthetic · BuyerTaxId = 0107567000414
        makro-p1p2-vatcode-synthetic · VendorBranch = 03060
        makro-p1p2-vatcode-synthetic · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-p1p2-vatcode-synthetic · VendorTaxId = 0203562005871
        makro-page2-discount-rows-mixed-accounts · BuyerBranch = 00000
        makro-page2-discount-rows-mixed-accounts · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page2-discount-rows-mixed-accounts · BuyerTaxId = 0107567000414
        makro-page2-discount-rows-mixed-accounts · VendorBranch = 03060
        makro-page2-discount-rows-mixed-accounts · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-page2-discount-rows-mixed-accounts · VendorTaxId = 0203562005871
        makro-page2-discount-rows-only · BuyerBranch = 00000
        makro-page2-discount-rows-only · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page2-discount-rows-only · BuyerTaxId = 0107567000414
        makro-page2-discount-rows-only · VendorBranch = 03060
        makro-page2-discount-rows-only · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-page2-discount-rows-only · VendorTaxId = 0203562005871
        makro-page2-tesseract-discount-rows-only · BuyerBranch = 00000
        makro-page2-tesseract-discount-rows-only · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page2-tesseract-discount-rows-only · BuyerTaxId = 0107567000414
        makro-page2-tesseract-discount-rows-only · VendorBranch = 00000
        makro-page2-tesseract-discount-rows-only · VendorName = บริษัท ซีพีแอ๊กซ์ตร้าจํากัด (มหาชน)
        makro-page2-tesseract-discount-rows-only · VendorTaxId = 0203562005871
        makro-page2-tesseract-garbled-legend · BuyerBranch = 00000
        makro-page2-tesseract-garbled-legend · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page2-tesseract-garbled-legend · BuyerTaxId = 0107567000414
        makro-page2-tesseract-garbled-legend · VendorBranch = 00000
        makro-page2-tesseract-garbled-legend · VendorName = บริษัท ซีพีแอ๊กซ์ตร้าจํากัด (มหาชน)
        makro-page2-tesseract-garbled-legend · VendorTaxId = 0203562005871
        makro-page2-vat-added-twice · BuyerBranch = 00000
        makro-page2-vat-added-twice · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page2-vat-added-twice · BuyerTaxId = 0107567000414
        makro-page2-vat-added-twice · VendorBranch = 03060
        makro-page2-vat-added-twice · VendorName = บริษัท ซีพีแอ็กซ์ตร้า จำกัด (มหาชน)
        makro-page2-vat-added-twice · VendorTaxId = 0203562005871
        makro-page3-total-first · BuyerBranch = 00000
        makro-page3-total-first · BuyerName = หจก. แอมแฮปปี้เนส
        makro-page3-total-first · BuyerTaxId = 0203562005871
        makro-page3-total-first · VendorBranch = 00005
        makro-page3-total-first · VendorName = บมจ. ซีพีแอ็กซ์ตร้า
        makro-page3-total-first · VendorTaxId = 0107567000414
        pea-electricity-bill-0869 · BuyerBranch = 00000
        pea-electricity-bill-0869 · BuyerName = บริษัท บัตรยูโอบี จำกัด (มหาชน)
        pea-electricity-bill-0869 · BuyerTaxId = 0203562005871
        pea-electricity-bill-0869 · VendorBranch = (ว่าง)
        pea-electricity-bill-0869 · VendorName = ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนสชื่อ
        pea-electricity-bill-0869 · VendorTaxId = 0994000165501
        pea-electricity-bill-0869-positional · BuyerBranch = 00000
        pea-electricity-bill-0869-positional · BuyerName = ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส
        pea-electricity-bill-0869-positional · BuyerTaxId = 0203562005871
        pea-electricity-bill-0869-positional · VendorBranch = 00000
        pea-electricity-bill-0869-positional · VendorName = การไฟฟ้าส่วนภูมิภาค
        pea-electricity-bill-0869-positional · VendorTaxId = 0994000165501
        pramie-line-discount-re111 · BuyerBranch = (ว่าง)
        pramie-line-discount-re111 · BuyerName = (ว่าง)
        pramie-line-discount-re111 · BuyerTaxId = (ว่าง)
        pramie-line-discount-re111 · VendorBranch = (ว่าง)
        pramie-line-discount-re111 · VendorName = บริษัท พรามี่ควอลิตี้
        pramie-line-discount-re111 · VendorTaxId = 0105567070534
        pramie-re111-positional · BuyerBranch = 00000
        pramie-re111-positional · BuyerName = ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส
        pramie-re111-positional · BuyerTaxId = 0203562005871
        pramie-re111-positional · VendorBranch = 00000
        pramie-re111-positional · VendorName = บริษัท พรามี่ควอลิตี้ (ไทยแลนด์) จำกัด
        pramie-re111-positional · VendorTaxId = 0105567070534
        pwa-water-bill-we1105-positional · BuyerBranch = (ว่าง)
        pwa-water-bill-we1105-positional · BuyerName = (ว่าง)
        pwa-water-bill-we1105-positional · BuyerTaxId = (ว่าง)
        pwa-water-bill-we1105-positional · VendorBranch = 00261
        pwa-water-bill-we1105-positional · VendorName = การประปาส่วนภูมิภาค
        pwa-water-bill-we1105-positional · VendorTaxId = 0994000164904
        qty-misread-engine-one · BuyerBranch = (ว่าง)
        qty-misread-engine-one · BuyerName = (ว่าง)
        qty-misread-engine-one · BuyerTaxId = (ว่าง)
        qty-misread-engine-one · VendorBranch = (ว่าง)
        qty-misread-engine-one · VendorName = (ว่าง)
        qty-misread-engine-one · VendorTaxId = (ว่าง)
        qty-printed-gap-unexplained · BuyerBranch = (ว่าง)
        qty-printed-gap-unexplained · BuyerName = (ว่าง)
        qty-printed-gap-unexplained · BuyerTaxId = (ว่าง)
        qty-printed-gap-unexplained · VendorBranch = (ว่าง)
        qty-printed-gap-unexplained · VendorName = (ว่าง)
        qty-printed-gap-unexplained · VendorTaxId = (ว่าง)
        row-index-receipt · BuyerBranch = (ว่าง)
        row-index-receipt · BuyerName = (ว่าง)
        row-index-receipt · BuyerTaxId = (ว่าง)
        row-index-receipt · VendorBranch = (ว่าง)
        row-index-receipt · VendorName = (ว่าง)
        row-index-receipt · VendorTaxId = (ว่าง)
        scommerce-lazada-prevat-discount · BuyerBranch = 00000
        scommerce-lazada-prevat-discount · BuyerName = หจก. แอมแฮปปี้เนส
        scommerce-lazada-prevat-discount · BuyerTaxId = 0203562005871
        scommerce-lazada-prevat-discount · VendorBranch = 00005
        scommerce-lazada-prevat-discount · VendorName = (ว่าง)
        scommerce-lazada-prevat-discount · VendorTaxId = 0105560113122
        service-wht-absent · BuyerBranch = (ว่าง)
        service-wht-absent · BuyerName = (ว่าง)
        service-wht-absent · BuyerTaxId = (ว่าง)
        service-wht-absent · VendorBranch = (ว่าง)
        service-wht-absent · VendorName = (ว่าง)
        service-wht-absent · VendorTaxId = (ว่าง)
        service-wht-printed · BuyerBranch = (ว่าง)
        service-wht-printed · BuyerName = (ว่าง)
        service-wht-printed · BuyerTaxId = (ว่าง)
        service-wht-printed · VendorBranch = (ว่าง)
        service-wht-printed · VendorName = (ว่าง)
        service-wht-printed · VendorTaxId = (ว่าง)
        sku-code-on-row · BuyerBranch = (ว่าง)
        sku-code-on-row · BuyerName = (ว่าง)
        sku-code-on-row · BuyerTaxId = (ว่าง)
        sku-code-on-row · VendorBranch = (ว่าง)
        sku-code-on-row · VendorName = (ว่าง)
        sku-code-on-row · VendorTaxId = (ว่าง)
        supermarket-member-discount · BuyerBranch = (ว่าง)
        supermarket-member-discount · BuyerName = (ว่าง)
        supermarket-member-discount · BuyerTaxId = (ว่าง)
        supermarket-member-discount · VendorBranch = 00007
        supermarket-member-discount · VendorName = บริษัท สุขใจมาร์ท จำกัด
        supermarket-member-discount · VendorTaxId = (ว่าง)
        telecom-no-arrears · BuyerBranch = 00000
        telecom-no-arrears · BuyerName = หจก. แอมแฮปปี้เนส
        telecom-no-arrears · BuyerTaxId = 0203562005871
        telecom-no-arrears · VendorBranch = 00000
        telecom-no-arrears · VendorName = บริษัท ทรีบรอดแบนด์เน็ตเวิร์ค จำกัด
        telecom-no-arrears · VendorTaxId = 0105553087421
        telecom-prior-balance · BuyerBranch = 00000
        telecom-prior-balance · BuyerName = หจก. แอมแฮปปี้เนส
        telecom-prior-balance · BuyerTaxId = 0203562005871
        telecom-prior-balance · VendorBranch = 00000
        telecom-prior-balance · VendorName = บริษัท ทรีบรอดแบนด์เน็ตเวิร์ค จำกัด
        telecom-prior-balance · VendorTaxId = 0105553087421
        uptoyou-shopee-pay-not-total · BuyerBranch = 00000
        uptoyou-shopee-pay-not-total · BuyerName = หจก. แอมแฮปปี้เนส
        uptoyou-shopee-pay-not-total · BuyerTaxId = 0203562005871
        uptoyou-shopee-pay-not-total · VendorBranch = 00000
        uptoyou-shopee-pay-not-total · VendorName = ห้างหุ้นส่วนจำกัด อัพทูยูบีเค
        uptoyou-shopee-pay-not-total · VendorTaxId = 0123565003005
        utility-meter-bill · BuyerBranch = (ว่าง)
        utility-meter-bill · BuyerName = (ว่าง)
        utility-meter-bill · BuyerTaxId = (ว่าง)
        utility-meter-bill · VendorBranch = (ว่าง)
        utility-meter-bill · VendorName = การไฟฟ้าส่วนภูมิภาค
        utility-meter-bill · VendorTaxId = (ว่าง)
        water-genuine-repeat-rows · BuyerBranch = (ว่าง)
        water-genuine-repeat-rows · BuyerName = (ว่าง)
        water-genuine-repeat-rows · BuyerTaxId = (ว่าง)
        water-genuine-repeat-rows · VendorBranch = (ว่าง)
        water-genuine-repeat-rows · VendorName = (ว่าง)
        water-genuine-repeat-rows · VendorTaxId = (ว่าง)
        weighed-item-qty-not-printed · BuyerBranch = (ว่าง)
        weighed-item-qty-not-printed · BuyerName = (ว่าง)
        weighed-item-qty-not-printed · BuyerTaxId = (ว่าง)
        weighed-item-qty-not-printed · VendorBranch = (ว่าง)
        weighed-item-qty-not-printed · VendorName = (ว่าง)
        weighed-item-qty-not-printed · VendorTaxId = (ว่าง)
        wholesale-mixed-vat · BuyerBranch = (ว่าง)
        wholesale-mixed-vat · BuyerName = (ว่าง)
        wholesale-mixed-vat · BuyerTaxId = (ว่าง)
        wholesale-mixed-vat · VendorBranch = 00021
        wholesale-mixed-vat · VendorName = บริษัท สยามค้าส่ง จำกัด
        wholesale-mixed-vat · VendorTaxId = 0105536092641
        wht-cert-50tawi-w53-positional · BuyerBranch = (ว่าง)
        wht-cert-50tawi-w53-positional · BuyerName = บริษัท ทิพยประกันภัย จำกัด (มหาชน)
        wht-cert-50tawi-w53-positional · BuyerTaxId = 0107538000533
        wht-cert-50tawi-w53-positional · VendorBranch = (ว่าง)
        wht-cert-50tawi-w53-positional · VendorName = หจก. แอมแฮปปเนส
        wht-cert-50tawi-w53-positional · VendorTaxId = 0203562005871
        wht-column-service-invoice · BuyerBranch = (ว่าง)
        wht-column-service-invoice · BuyerName = (ว่าง)
        wht-column-service-invoice · BuyerTaxId = (ว่าง)
        wht-column-service-invoice · VendorBranch = (ว่าง)
        wht-column-service-invoice · VendorName = (ว่าง)
        wht-column-service-invoice · VendorTaxId = (ว่าง)
        winepro-vat-included · BuyerBranch = 00000
        winepro-vat-included · BuyerName = (ว่าง)
        winepro-vat-included · BuyerTaxId = 0203562005871
        winepro-vat-included · VendorBranch = 00012
        winepro-vat-included · VendorName = หจก. แอมแฮปปี้เนส
        winepro-vat-included · VendorTaxId = 0105555175590
        """;

    [Fact]
    public void ช่องคู่สัญญาทุกใบ_ตรงgolden_ทุกแถวที่เปลี่ยนต้องอธิบายได้()
    {
        var expected = Golden.Replace("\r", "").Split('\n');
        var actual = PartySnapshot(OcrReplayHarness.Run()).Split('\n');
        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            "ช่องคู่สัญญาเปลี่ยน — golden:\n  " + string.Join("\n  ", missing) + "\nตอนนี้:\n  " + string.Join("\n  ", extra));
    }

    [Fact]
    public void ทุกใบในชุด_มีแถวช่องคู่สัญญาครบหกช่อง()
    {
        var run = OcrReplayHarness.Run();
        foreach (var p in OcrReplayHarness.Corpus)
            foreach (var f in PartyFields)
                Assert.Single(run, a => a.Paper == p.Name && a.Field == f);
    }

    /// <summary>negative test ของ golden (F2 #6): สลับชื่อสองฝั่งของใบหนึ่ง (บั๊ก P1-2) แล้ว snapshot ต้องไม่ตรง golden</summary>
    [Fact]
    public void สลับชื่อผู้ขายผู้ซื้อของใบเดียว_goldenต้องจับได้()
    {
        var run = OcrReplayHarness.Run();
        string? V(string f) => run.Single(a => a.Paper == "crc-thaiwatsadu-srcie75384-positional" && a.Field == f).Value;
        var swapped = run.Select(a => a.Paper != "crc-thaiwatsadu-srcie75384-positional" ? a
            : a.Field == "VendorName" ? a with { Value = V("BuyerName") }
            : a.Field == "BuyerName" ? a with { Value = V("VendorName") } : a).ToList();
        Assert.NotEqual(PartySnapshot(run), PartySnapshot(swapped));
        Assert.Contains("crc-thaiwatsadu-srcie75384-positional · VendorName = บริษัท ซีอาร์ซีไทวัสดุ จำกัด", Golden);
    }
}
