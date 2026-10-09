namespace Accounting.Tests;

/// <summary>
/// ไฟล์ e-Tax XML <b>จริง</b> ที่ฝังในใบ PDF ของ Shopee (ใบ U · หจก. อัพทูยู บีเค · INV2026090199) — สำเนาตรงตัวอักษรจาก
/// <c>erp-review/2026-09-24/fixtures/ETDA-invoice-U-INV2026090199.xml</c> (ชื่อไฟล์แนบใน PDF: <c>ETDA-invoice.xml</c>)
/// <para>ไฟล์ต้นฉบับขึ้นต้นด้วย BOM (U+FEFF) — ค่าคงที่นี้ตัด BOM ออก · เทสต์ที่ต้องการ BOM เติม <see cref="Bom"/> เอง</para>
/// <para>รอบ 193 (คำตัดสินเจ้าของข้อ 10: "XML ที่ฝังใน PDF e-Tax = หลักฐานอันดับหนึ่ง")</para>
/// </summary>
public static class EtaxFixtures
{
    public const string Bom = "\uFEFF";

    public const string ShopeeUptoyouXml = """
<rsm:TaxInvoice_CrossIndustryInvoice xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xsi:schemaLocation="rsm file:../data/standard/TaxInvoice_CrossIndustryInvoice_2p0.xsd" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">
  <rsm:ExchangedDocumentContext>
    <ram:GuidelineSpecifiedDocumentContextParameter>
      <ram:ID schemeAgencyID="ETDA" schemeVersionID="v2.0">ER3-2560</ram:ID>
    </ram:GuidelineSpecifiedDocumentContextParameter>
  </rsm:ExchangedDocumentContext>
  <rsm:ExchangedDocument>
    <ram:ID>INV2026090199</ram:ID>
    <ram:Name>ใบกำกับภาษี/ใบเสร็จรับเงิน</ram:Name>
    <ram:TypeCode>T03</ram:TypeCode>
    <ram:IssueDateTime>2026-09-17T00:00:00</ram:IssueDateTime>
    <ram:CreationDateTime>2026-09-22T07:51:41.491</ram:CreationDateTime>
  </rsm:ExchangedDocument>
  <rsm:SupplyChainTradeTransaction>
    <ram:ApplicableHeaderTradeAgreement>
      <ram:SellerTradeParty>
        <ram:Name>ห้างหุ้นส่วนจำกัด อัพทูยู บีเค</ram:Name>
        <ram:SpecifiedTaxRegistration>
          <ram:ID schemeID="TXID">012356500300500000</ram:ID>
        </ram:SpecifiedTaxRegistration>
        <ram:PostalTradeAddress>
          <ram:PostcodeCode>11140</ram:PostcodeCode>
          <ram:CityName>1203</ram:CityName>
          <ram:CitySubDivisionName>120304</ram:CitySubDivisionName>
          <ram:CountryID schemeID="3166-1 alpha-2">TH</ram:CountryID>
          <ram:CountrySubDivisionID>12</ram:CountrySubDivisionID>
          <ram:BuildingNumber>49/199</ram:BuildingNumber>
        </ram:PostalTradeAddress>
      </ram:SellerTradeParty>
      <ram:BuyerTradeParty>
        <ram:Name>ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส</ram:Name>
        <ram:SpecifiedTaxRegistration>
          <ram:ID schemeID="TXID">020356200587100000</ram:ID>
        </ram:SpecifiedTaxRegistration>
        <ram:PostalTradeAddress>
          <ram:PostcodeCode>20110</ram:PostcodeCode>
          <ram:LineOne>202/24 ซอยบ้านห้วยกุ่ม4 หมู่5 ตำบลบางพระ อำเภอศรีราชา ชลบุรี</ram:LineOne>
          <ram:CountryID schemeID="3166-1 alpha-2">TH</ram:CountryID>
        </ram:PostalTradeAddress>
      </ram:BuyerTradeParty>
    </ram:ApplicableHeaderTradeAgreement>
    <ram:ApplicableHeaderTradeDelivery />
    <ram:ApplicableHeaderTradeSettlement>
      <ram:InvoiceCurrencyCode listID="ISO 4217 3A">THB</ram:InvoiceCurrencyCode>
      <ram:ApplicableTradeTax>
        <ram:TypeCode>VAT</ram:TypeCode>
        <ram:CalculatedRate>7</ram:CalculatedRate>
        <ram:BasisAmount>500.93</ram:BasisAmount>
        <ram:CalculatedAmount>35.07</ram:CalculatedAmount>
      </ram:ApplicableTradeTax>
      <ram:SpecifiedTradeAllowanceCharge>
        <ram:ChargeIndicator>false</ram:ChargeIndicator>
        <ram:ActualAmount>0.00</ram:ActualAmount>
        <ram:TypeCode>95</ram:TypeCode>
      </ram:SpecifiedTradeAllowanceCharge>
      <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        <ram:LineTotalAmount>500.93</ram:LineTotalAmount>
        <ram:AllowanceTotalAmount>0.00</ram:AllowanceTotalAmount>
        <ram:TaxBasisTotalAmount>500.93</ram:TaxBasisTotalAmount>
        <ram:TaxTotalAmount>35.07</ram:TaxTotalAmount>
        <ram:GrandTotalAmount>536.00</ram:GrandTotalAmount>
      </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
    </ram:ApplicableHeaderTradeSettlement>
    <ram:IncludedSupplyChainTradeLineItem>
      <ram:AssociatedDocumentLineDocument>
        <ram:LineID>1</ram:LineID>
      </ram:AssociatedDocumentLineDocument>
      <ram:SpecifiedTradeProduct>
        <ram:Name>[ซัก+ปรับ] ไฮยีน เลิฟทัช น้ำยาปรับผ้านุ่ม 1000 มล. + น้ำยาซักผ้า 1400 มล. [แพ็คคู่]</ram:Name>
      </ram:SpecifiedTradeProduct>
      <ram:SpecifiedLineTradeAgreement>
        <ram:GrossPriceProductTradePrice>
          <ram:ChargeAmount>250.47</ram:ChargeAmount>
        </ram:GrossPriceProductTradePrice>
      </ram:SpecifiedLineTradeAgreement>
      <ram:SpecifiedLineTradeDelivery>
        <ram:BilledQuantity unitCode="">2</ram:BilledQuantity>
      </ram:SpecifiedLineTradeDelivery>
      <ram:SpecifiedLineTradeSettlement>
        <ram:ApplicableTradeTax>
          <ram:TypeCode>VAT</ram:TypeCode>
          <ram:CalculatedRate>7</ram:CalculatedRate>
          <ram:BasisAmount>500.93</ram:BasisAmount>
          <ram:CalculatedAmount>35.07</ram:CalculatedAmount>
        </ram:ApplicableTradeTax>
        <ram:SpecifiedTradeAllowanceCharge>
          <ram:ChargeIndicator>false</ram:ChargeIndicator>
          <ram:ActualAmount>0.00</ram:ActualAmount>
          <ram:TypeCode>95</ram:TypeCode>
        </ram:SpecifiedTradeAllowanceCharge>
        <ram:SpecifiedTradeSettlementLineMonetarySummation>
          <ram:TaxTotalAmount>35.07</ram:TaxTotalAmount>
          <ram:NetLineTotalAmount currencyID="THB">500.93</ram:NetLineTotalAmount>
          <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">536.00</ram:NetIncludingTaxesLineTotalAmount>
        </ram:SpecifiedTradeSettlementLineMonetarySummation>
      </ram:SpecifiedLineTradeSettlement>
    </ram:IncludedSupplyChainTradeLineItem>
  </rsm:SupplyChainTradeTransaction>
</rsm:TaxInvoice_CrossIndustryInvoice>
""";

    /// <summary>
    /// ไฟล์ e-Tax XML <b>จริง</b> ที่ฝังใน PDF/A-3 ของ CRC ไทวัสดุ (ใบกำกับภาษี/ใบเสร็จ <c>SRCIE26100075384</c> · T03 · สแกน f1690d11 ·
    /// ผู้ใช้รายงาน 2026-10-09 "จำนวน ราคาต่อหน่วย ส่วนลด ผิด") — ถอดจากไฟล์ <c>177b6f88-60961-900030696-AT-2614478719.pdf</c>
    /// <para>ตัดออกเฉพาะ: ลายเซ็นดิจิทัล (<c>ds:Signature</c> + ใบรับรอง) · processing instruction ก่อน root ·
    /// <c>InformationNote</c> ของสินค้าทุกตัว และ <c>IncludedNote</c> หัวใบ ยกเว้น DocumentName/AllowanceTotalAmount/TradeAllowanceReason
    /// (ซ้ำกับค่าใน element จริง หรือเป็น "-") · ตัวเลข/โครงสร้างบรรทัดทุกตัวตรงตัวอักษร</para>
    /// <para>ธรรมเนียมผู้ขายรายนี้: <c>GrossPriceProductTradePrice</c> = ราคา<b>รวม VAT</b> · ส่วนลดบรรทัด = ยอด<b>รวม VAT</b> ·
    /// <c>BasisAmount</c> บรรทัด = ฐานก่อนส่วนลด · <c>AllowanceTotalAmount</c> หัวใบ 1,080.00 = Σ ส่วนลดบรรทัด (อยู่ในยอดบรรทัดแล้ว)</para>
    /// </summary>
    public const string CrcThaiwatsaduXml = """
<rsm:TaxInvoice_CrossIndustryInvoice xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">
    <rsm:ExchangedDocumentContext>
        <ram:GuidelineSpecifiedDocumentContextParameter>
            <ram:ID schemeAgencyID="ETDA" schemeVersionID="v2.0">ER3-2560</ram:ID>
        </ram:GuidelineSpecifiedDocumentContextParameter>
    </rsm:ExchangedDocumentContext>
    <rsm:ExchangedDocument>
        <ram:ID>SRCIE26100075384</ram:ID>
        <ram:Name>ใบเสร็จรับเงิน/ใบกำกับภาษี</ram:Name>
        <ram:TypeCode listAgencyID="RD/ETDA" listID="1001_ThaiDocumentNameCodeInvoice" listVersionID="15A">T03</ram:TypeCode>
        <ram:IssueDateTime>2026-10-05T23:15:00</ram:IssueDateTime>
        <ram:Purpose>-</ram:Purpose>
        <ram:CreationDateTime>2026-10-05T23:15:43</ram:CreationDateTime>
        <ram:IncludedNote>
            <ram:Subject>DocumentName</ram:Subject>
            <ram:Content>ใบกำกับภาษี/ใบเสร็จรับเงิน</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>AllowanceTotalAmount</ram:Subject>
            <ram:Content>1080.00</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>TradeAllowanceReason</ram:Subject>
            <ram:Content>หักเงินมัดจำ</ram:Content>
        </ram:IncludedNote>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>บริษัท ซีอาร์ซี ไทวัสดุ จำกัด</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">010555502121500050</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>10540</ram:PostcodeCode>
                    <ram:CityName>1103</ram:CityName>
                    <ram:CitySubDivisionName>110302</ram:CitySubDivisionName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:CountrySubDivisionID>11</ram:CountrySubDivisionID>
                    <ram:BuildingNumber>88/88</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:SellerTradeParty>
            <ram:BuyerTradeParty>
                <ram:ID>0001428023</ram:ID>
                <ram:Name>ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">020356200587100000</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>20110</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne> เลขที่ 202/24 หมู่ที่ 5 ซอย บ้านห้วยกุ่ม4 </ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:BuyerTradeParty>
            <ram:BuyerOrderReferencedDocument>
                <ram:IssuerAssignedID>2614478719</ram:IssuerAssignedID>
            </ram:BuyerOrderReferencedDocument>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeDelivery>
            <ram:ShipToTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipToTradeParty>
            <ram:ShipFromTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipFromTradeParty>
        </ram:ApplicableHeaderTradeDelivery>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>THB</ram:InvoiceCurrencyCode>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>VAT</ram:TypeCode>
                <ram:CalculatedRate>7.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">2991.59</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">209.41</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>FRE</ram:TypeCode>
                <ram:CalculatedRate>0.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">0.00</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">0.00</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:SpecifiedTradeAllowanceCharge>
                <ram:ChargeIndicator>false</ram:ChargeIndicator>
                <ram:ActualAmount currencyID="THB">0.00</ram:ActualAmount>
                <ram:Reason>หักเงินมัดจำ</ram:Reason>
            </ram:SpecifiedTradeAllowanceCharge>
            <ram:SpecifiedTradePaymentTerms>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradePaymentTerms>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount currencyID="THB">2991.59</ram:LineTotalAmount>
                <ram:AllowanceTotalAmount currencyID="THB">1080.00</ram:AllowanceTotalAmount>
                <ram:TaxBasisTotalAmount currencyID="THB">2991.59</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount currencyID="THB">209.41</ram:TaxTotalAmount>
                <ram:GrandTotalAmount currencyID="THB">3201.00</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>1</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8857127756783</ram:ID>
                <ram:Name>ยางแบนรองขาแอร์  TEK 1 ชุด (4 ชิ้น) ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">37.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">3.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">103.74</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">7.26</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">26.68</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">78.80</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">84.32</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>2</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8857127756776</ram:ID>
                <ram:Name>ขาแขวนคอยล์ร้อนแอร์ 2.2 มม. 50 CM. TEK  เบจ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">229.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">4.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">856.07</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">59.93</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">220.14</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">650.34</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">695.86</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>4</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858658382922</ram:ID>
                <ram:Name>รางครอบท่อแอร์Abco LEETECH A-AR75 ขาว 2ม.</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">123.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">12.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">1379.44</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">96.56</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">354.72</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">1047.93</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">1121.28</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>5</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858658383073</ram:ID>
                <ram:Name>ข้อต่อตรง LEETECH A-JCAR75 ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">28.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">10.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">261.68</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">18.32</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">67.29</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">198.79</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">212.71</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>6</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858658383080</ram:ID>
                <ram:Name>ข้องอโค้ง LEETECH A-JFAR75 ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">43.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">241.12</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">16.88</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">62.00</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">183.18</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">196.00</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>7</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858658383097</ram:ID>
                <ram:Name>ข้องอมุม LEETECH A-JEAR75 ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">43.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">241.12</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">16.88</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">62.00</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">183.18</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">196.00</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>8</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858658383103</ram:ID>
                <ram:Name>ฝาครอบ LEETECH A-JAAR75 ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">43.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">4.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">160.75</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">11.25</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">41.34</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">122.11</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">130.66</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>9</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>0885911555715</ram:ID>
                <ram:Name>เครื่องตรวจหาโครงผนังสำหรับผนังหนา DEWALT DW0150 1-1/2 นิ้ว เหลือง-ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">690.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">644.86</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">45.14</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">165.83</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">489.88</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">524.17</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>10</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>2000602613033</ram:ID>
                <ram:Name>ค่าขนส่ง CTD</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">1.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">120.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">112.15</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">7.85</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">80.00</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">37.38</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">40.00</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
    </rsm:SupplyChainTradeTransaction>
</rsm:TaxInvoice_CrossIndustryInvoice>
""";

    /// <summary>
    /// ไฟล์ e-Tax XML <b>จริง</b> ใบที่สองของ CRC ไทวัสดุ (T03 · คำสั่งซื้อ 2614501699 · ผู้ใช้สแกน 2026-10-09 รอบ 6) — ถอดจาก
    /// <c>c9b6f069-60956-900041371-E2-2614501699.pdf</c> · ตัดเหมือน <see cref="CrcThaiwatsaduXml"/> (ลายเซ็น · PI · notes ที่ซ้ำ/เป็น "-")
    /// <para>จุดสำคัญ: ทุกบรรทัดราคารวม VAT แต่ผู้ขายคิด VAT ระดับหัวใบ (3,160.00 ÷ 1.07) ⇒ Σ ก่อน VAT รายบรรทัด 2,953.26 ≠ หัวใบ 2,953.27 ·
    /// Σ VAT รายบรรทัด 206.74 ≠ 206.73 — ขั้น ReconcileTaxRounding ของเอกสารขยับบรรทัดใหญ่สุด 1 สตางค์แล้วตรงกระดาษ</para>
    /// </summary>
    public const string CrcThaiwatsadu2Xml = """
<rsm:TaxInvoice_CrossIndustryInvoice xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">
    <rsm:ExchangedDocumentContext>
        <ram:GuidelineSpecifiedDocumentContextParameter>
            <ram:ID schemeAgencyID="ETDA" schemeVersionID="v2.0">ER3-2560</ram:ID>
        </ram:GuidelineSpecifiedDocumentContextParameter>
    </rsm:ExchangedDocumentContext>
    <rsm:ExchangedDocument>
        <ram:ID>CBRIE26100125485</ram:ID>
        <ram:Name>ใบเสร็จรับเงิน/ใบกำกับภาษี</ram:Name>
        <ram:TypeCode listAgencyID="RD/ETDA" listID="1001_ThaiDocumentNameCodeInvoice" listVersionID="15A">T03</ram:TypeCode>
        <ram:IssueDateTime>2026-10-05T18:01:00</ram:IssueDateTime>
        <ram:Purpose>-</ram:Purpose>
        <ram:CreationDateTime>2026-10-05T18:01:05</ram:CreationDateTime>
        <ram:IncludedNote>
            <ram:Subject>DocumentName</ram:Subject>
            <ram:Content>ใบกำกับภาษี/ใบเสร็จรับเงิน</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>AllowanceTotalAmount</ram:Subject>
            <ram:Content>1080.00</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>TradeAllowanceReason</ram:Subject>
            <ram:Content>หักเงินมัดจำ</ram:Content>
        </ram:IncludedNote>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>บริษัท ซีอาร์ซี ไทวัสดุ จำกัด</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">010555502121500043</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>10540</ram:PostcodeCode>
                    <ram:CityName>1103</ram:CityName>
                    <ram:CitySubDivisionName>110302</ram:CitySubDivisionName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:CountrySubDivisionID>11</ram:CountrySubDivisionID>
                    <ram:BuildingNumber>88/88</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:SellerTradeParty>
            <ram:BuyerTradeParty>
                <ram:ID>0001719904</ram:ID>
                <ram:Name>ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">020356200587100000</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>20110</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne> เลขที่ 202/24 หมู่ที่ 5 ซอย บ้านห้วยกุ่ม4 </ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:BuyerTradeParty>
            <ram:BuyerOrderReferencedDocument>
                <ram:IssuerAssignedID>2614501699</ram:IssuerAssignedID>
            </ram:BuyerOrderReferencedDocument>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeDelivery>
            <ram:ShipToTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipToTradeParty>
            <ram:ShipFromTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipFromTradeParty>
        </ram:ApplicableHeaderTradeDelivery>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>THB</ram:InvoiceCurrencyCode>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>VAT</ram:TypeCode>
                <ram:CalculatedRate>7.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">2953.27</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">206.73</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>FRE</ram:TypeCode>
                <ram:CalculatedRate>0.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">0.00</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">0.00</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:SpecifiedTradeAllowanceCharge>
                <ram:ChargeIndicator>false</ram:ChargeIndicator>
                <ram:ActualAmount currencyID="THB">0.00</ram:ActualAmount>
                <ram:Reason>หักเงินมัดจำ</ram:Reason>
            </ram:SpecifiedTradeAllowanceCharge>
            <ram:SpecifiedTradePaymentTerms>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradePaymentTerms>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount currencyID="THB">2953.27</ram:LineTotalAmount>
                <ram:AllowanceTotalAmount currencyID="THB">1080.00</ram:AllowanceTotalAmount>
                <ram:TaxBasisTotalAmount currencyID="THB">2953.27</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount currencyID="THB">206.73</ram:TaxTotalAmount>
                <ram:GrandTotalAmount currencyID="THB">3160.00</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>1</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858901304411</ram:ID>
                <ram:Name>ท่อ HDPE ร้อยสายไฟ 32 มม.  ตรามือ 398-32-1R(PN6) 100ม. ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">1730.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">1616.82</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">113.18</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">422.99</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">1221.50</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">1307.01</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>2</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858901302974</ram:ID>
                <ram:Name>ข้อต่อตรงท่อร้อยสายไฟ PE แบบสวม Type A 32 มม. ตรามือ 398-350-32R ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">30.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">168.22</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">11.78</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">44.01</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">127.09</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">135.99</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>3</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8858901309638</ram:ID>
                <ram:Name>ท่อโค้งไฟฟ้าพีอี 90? 32 มม.  ตรามือ 398-350-32RS ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">118.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">4.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">441.12</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">30.88</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">115.40</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">333.27</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">356.60</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>5</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8859042890849</ram:ID>
                <ram:Name>ตู้คอนซูมเมอร์ยูนิตครบชุด 4 ช่อง RCBO 50A CT ELECTRIC CTU-4R RCBO 50A ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">1130.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">1056.07</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">73.93</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">276.28</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">797.87</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">853.72</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>6</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8850304238584</ram:ID>
                <ram:Name>เทปพันสายไฟ Temflex170 3M Z053-0037 20 เมตร ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">38.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">5.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">177.57</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">12.43</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">46.45</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">134.16</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">143.55</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>7</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>2000604201344</ram:ID>
                <ram:Name>คีมย้ำหางปลาคอร์ดเอ็น GIANT KINGKONG PRO PL2028 สี น้ำเงิน-เทา</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">388.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">362.62</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">25.38</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">94.87</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">273.95</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">293.13</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>8</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>2000602613033</ram:ID>
                <ram:Name>ค่าขนส่ง CTD</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">1.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">150.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">140.19</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">9.81</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">80.00</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">65.42</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">70.00</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
    </rsm:SupplyChainTradeTransaction>
</rsm:TaxInvoice_CrossIndustryInvoice>
""";

    /// <summary>
    /// ไฟล์ e-Tax XML <b>จริง</b> ใบที่สามของ CRC ไทวัสดุ (คำสั่งซื้อ 2614502187 · ผู้ใช้สแกน 2026-10-09 รอบ 7) — ถอดจาก
    /// <c>73b2c9a9-60022-900032558-E2-2614502187.pdf</c> · ตัดเหมือน <see cref="CrcThaiwatsaduXml"/> (ลายเซ็น · PI · notes ที่ซ้ำ/เป็น "-")
    /// <para>จุดสำคัญ: ทุกบรรทัดราคารวม VAT แต่ผู้ขายคิด VAT ระดับหัวใบ (3,063.00 ÷ 1.07) ⇒ Σ ก่อน VAT รายบรรทัด 2,862.60 ≠ หัวใบ 2,862.62 ·
    /// Σ VAT รายบรรทัด 200.40 ≠ 200.38 — ขั้น ReconcileTaxRounding ขยับบรรทัดใหญ่สุด 2 สตางค์แล้วตรงกระดาษ</para>
    /// </summary>
    public const string CrcThaiwatsadu3Xml = """
<rsm:TaxInvoice_CrossIndustryInvoice xmlns:ram="urn:etda:uncefact:data:standard:TaxInvoice_ReusableAggregateBusinessInformationEntity:2" xmlns:rsm="urn:etda:uncefact:data:standard:TaxInvoice_CrossIndustryInvoice:2">
    <rsm:ExchangedDocumentContext>
        <ram:GuidelineSpecifiedDocumentContextParameter>
            <ram:ID schemeAgencyID="ETDA" schemeVersionID="v2.0">ER3-2560</ram:ID>
        </ram:GuidelineSpecifiedDocumentContextParameter>
    </rsm:ExchangedDocumentContext>
    <rsm:ExchangedDocument>
        <ram:ID>BASIE26100125491</ram:ID>
        <ram:Name>ใบเสร็จรับเงิน/ใบกำกับภาษี</ram:Name>
        <ram:TypeCode listAgencyID="RD/ETDA" listID="1001_ThaiDocumentNameCodeInvoice" listVersionID="15A">T03</ram:TypeCode>
        <ram:IssueDateTime>2026-10-05T19:07:00</ram:IssueDateTime>
        <ram:Purpose>-</ram:Purpose>
        <ram:CreationDateTime>2026-10-05T19:07:46</ram:CreationDateTime>
        <ram:IncludedNote>
            <ram:Subject>DocumentName</ram:Subject>
            <ram:Content>ใบกำกับภาษี/ใบเสร็จรับเงิน</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>AllowanceTotalAmount</ram:Subject>
            <ram:Content>1104.00</ram:Content>
        </ram:IncludedNote>
        <ram:IncludedNote>
            <ram:Subject>TradeAllowanceReason</ram:Subject>
            <ram:Content>หักเงินมัดจำ</ram:Content>
        </ram:IncludedNote>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>บริษัท ซีอาร์ซี ไทวัสดุ จำกัด</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">010555502121500112</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>10540</ram:PostcodeCode>
                    <ram:CityName>1103</ram:CityName>
                    <ram:CitySubDivisionName>110302</ram:CitySubDivisionName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:CountrySubDivisionID>11</ram:CountrySubDivisionID>
                    <ram:BuildingNumber>88/88</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:SellerTradeParty>
            <ram:BuyerTradeParty>
                <ram:ID>0001719904</ram:ID>
                <ram:Name>ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID="TXID">020356200587100000</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>20110</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne> เลขที่ 202/24 หมู่ที่ 5 ซอย บ้านห้วยกุ่ม4 </ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:CountryID>TH</ram:CountryID>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:BuyerTradeParty>
            <ram:BuyerOrderReferencedDocument>
                <ram:IssuerAssignedID>2614502187</ram:IssuerAssignedID>
            </ram:BuyerOrderReferencedDocument>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeDelivery>
            <ram:ShipToTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipToTradeParty>
            <ram:ShipFromTradeParty>
                <ram:ID>-</ram:ID>
                <ram:Name>-</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:PersonName>-</ram:PersonName>
                    <ram:DepartmentName>-</ram:DepartmentName>
                </ram:DefinedTradeContact>
                <ram:PostalTradeAddress>
                    <ram:PostcodeCode>-</ram:PostcodeCode>
                    <ram:BuildingName>-</ram:BuildingName>
                    <ram:LineOne>-</ram:LineOne>
                    <ram:LineTwo>-</ram:LineTwo>
                    <ram:LineThree>-</ram:LineThree>
                    <ram:LineFour>-</ram:LineFour>
                    <ram:LineFive>-</ram:LineFive>
                    <ram:StreetName>-</ram:StreetName>
                    <ram:BuildingNumber>-</ram:BuildingNumber>
                </ram:PostalTradeAddress>
            </ram:ShipFromTradeParty>
        </ram:ApplicableHeaderTradeDelivery>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>THB</ram:InvoiceCurrencyCode>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>VAT</ram:TypeCode>
                <ram:CalculatedRate>7.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">2862.62</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">200.38</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:ApplicableTradeTax>
                <ram:TypeCode>FRE</ram:TypeCode>
                <ram:CalculatedRate>0.00</ram:CalculatedRate>
                <ram:BasisAmount currencyID="THB">0.00</ram:BasisAmount>
                <ram:CalculatedAmount currencyID="THB">0.00</ram:CalculatedAmount>
            </ram:ApplicableTradeTax>
            <ram:SpecifiedTradeAllowanceCharge>
                <ram:ChargeIndicator>false</ram:ChargeIndicator>
                <ram:ActualAmount currencyID="THB">0.00</ram:ActualAmount>
                <ram:Reason>หักเงินมัดจำ</ram:Reason>
            </ram:SpecifiedTradeAllowanceCharge>
            <ram:SpecifiedTradePaymentTerms>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradePaymentTerms>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount currencyID="THB">2862.62</ram:LineTotalAmount>
                <ram:AllowanceTotalAmount currencyID="THB">1104.00</ram:AllowanceTotalAmount>
                <ram:TaxBasisTotalAmount currencyID="THB">2862.62</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount currencyID="THB">200.38</ram:TaxTotalAmount>
                <ram:GrandTotalAmount currencyID="THB">3063.00</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>1</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890012563</ram:ID>
                <ram:Name>ตัวยึดท่อ HACO CC32/P 32 มม. แพ็ค 5 ชิ้น ขาว</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">33.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">185.05</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">12.95</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">49.46</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">138.82</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">148.54</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>2</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890050237</ram:ID>
                <ram:Name>ตัวยึดท่อ HACO CC25/BK/P 25 มม. ดำ (แพ็ค 5 ชิ้น)</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">25.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">140.19</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">9.81</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">37.47</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">105.17</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">112.53</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>3</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890048739</ram:ID>
                <ram:Name>ท่ออ่อนลูกฟูก HACO FX25/BK 25 มม. 40 เมตร ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">890.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">831.78</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">58.22</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">222.34</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">623.98</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">667.66</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>4</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890048692</ram:ID>
                <ram:Name>ท่อยูพีวีซี HACO EC25 S/BK 25 มม. 2.9 เมตร ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">77.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">11.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">791.59</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">55.41</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">211.59</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">593.84</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">635.41</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>5</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890049736</ram:ID>
                <ram:Name>ข้อต่อท่อ3ทาง ตัวที HACO IT25/BK 25 มม. ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">35.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">5.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">163.55</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">11.45</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">43.72</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">122.69</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">131.28</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>6</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890050275</ram:ID>
                <ram:Name>ข้อต่อกลางท่อ HACO JC25/BK/P 25 มม. ดำ (แพ็ค 4 ชิ้น)</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">20.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">112.15</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">7.85</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">29.98</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">84.13</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">90.02</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>7</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890050183</ram:ID>
                <ram:Name>ข้อต่อเข้ากล่องพักสาย HACO BC25/BK/P 25 มม. ดำ (แพ็ค 4 ชิ้น)</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">35.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">196.26</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">13.74</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">52.46</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">147.23</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">157.54</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>8</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890049699</ram:ID>
                <ram:Name>ข้อต่อโค้ง HACO IE25/BK 25 มม. ดำ</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">25.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">140.19</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">9.81</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">37.47</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">105.17</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">112.53</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>9</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8855890049828</ram:ID>
                <ram:Name>ข้อต่อท่ออ่อน HACO BF25/BK/P 25 มม. ดำ (แพ็ค 2 ชิ้น)</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">30.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">6.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">168.22</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">11.78</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">44.97</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">126.20</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">135.03</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>11</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>6903366140356</ram:ID>
                <ram:Name>ดอกโฮลซอว์เจาะเหล็ก GIANTTECH G471027 27 มม.</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">168.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">157.01</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">10.99</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">41.97</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">117.79</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">126.03</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>12</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>6903366140394</ram:ID>
                <ram:Name>ดอกโฮลซอว์เจาะเหล็ก GIANTTECH G471033 33 มม.</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">208.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">194.39</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">13.61</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">51.96</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">145.83</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">156.04</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>17</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>2000603184556</ram:ID>
                <ram:Name>แก้วเบียร์ทรงสูง 14 oz. KASSA HOME GY860 6.7x6.3x19.7 ซม. ใส</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">23.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">12.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">257.94</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">18.06</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">86.95</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">176.68</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">189.05</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>18</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8852163012855</ram:ID>
                <ram:Name>สายยางเครื่องกรองน้ำ MAZUMA รุ่น 12855-F ขนาด 1/4x2M.</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">165.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">154.21</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">10.79</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">41.22</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">115.68</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">123.78</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>19</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8852163012800</ram:ID>
                <ram:Name>ข้องอ MAZUMA รุ่น 12800-F ขนาด 1/4x1/4</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">100.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">93.46</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">6.54</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">24.98</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">70.11</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">75.02</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>20</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>8852163012756</ram:ID>
                <ram:Name>โอริง เครื่องกรองน้ำ MAZUMA รุ่น 12756-F ขนาด 10</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">190.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">1.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">177.57</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">12.43</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">47.46</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">133.21</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">142.54</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:AssociatedDocumentLineDocument>
                <ram:LineID>21</ram:LineID>
            </ram:AssociatedDocumentLineDocument>
            <ram:SpecifiedTradeProduct>
                <ram:ID>2000602613033</ram:ID>
                <ram:Name>ค่าขนส่ง CTD</ram:Name>
                <ram:Description>-</ram:Description>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice>
                    <ram:ChargeAmount currencyID="THB">1.00</ram:ChargeAmount>
                </ram:GrossPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity unitCode="-">140.00</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                    <ram:TypeCode>VAT</ram:TypeCode>
                    <ram:CalculatedRate>7.00</ram:CalculatedRate>
                    <ram:BasisAmount currencyID="THB">130.84</ram:BasisAmount>
                    <ram:CalculatedAmount currencyID="THB">9.16</ram:CalculatedAmount>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeAllowanceCharge>
                    <ram:ChargeIndicator>false</ram:ChargeIndicator>
                    <ram:ActualAmount currencyID="THB">80.00</ram:ActualAmount>
                </ram:SpecifiedTradeAllowanceCharge>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:TaxTotalAmount>0</ram:TaxTotalAmount>
                    <ram:NetLineTotalAmount currencyID="THB">56.07</ram:NetLineTotalAmount>
                    <ram:NetIncludingTaxesLineTotalAmount currencyID="THB">60.00</ram:NetIncludingTaxesLineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
    </rsm:SupplyChainTradeTransaction>
</rsm:TaxInvoice_CrossIndustryInvoice>
""";
}
