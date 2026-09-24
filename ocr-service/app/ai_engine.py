import json
import logging
import httpx
import os

from .date_reader import read_document_date

logger = logging.getLogger(__name__)

OLLAMA_BASE_URL = os.environ.get("OLLAMA_BASE_URL", "http://localhost:11434")
AI_MODEL = os.environ.get("AI_MODEL", "typhoon2:8b")

EXTRACTION_PROMPT = """คุณเป็น AI ผู้เชี่ยวชาญเอกสารบัญชีภาษาไทย ช่วยวิเคราะห์ข้อความที่ได้จาก OCR แล้วดึงข้อมูลออกมาเป็น JSON

กฎ:
- ถ้าไม่แน่ใจว่าข้อมูลคืออะไร ให้ใส่ null
- Tax ID ไทยมี 13 หลัก
- ประเภทเอกสาร: Invoice, Receipt, TaxInvoice, CreditNote, DebitNote, PurchaseOrder, WHT, CertificateInLieu (ใบรับรองแทนใบเสร็จ), Other
- วันที่ให้แปลงเป็น YYYY-MM-DD
- จำนวนเงินเป็นตัวเลข (ไม่มี comma)
- confidence เป็น 0.0-1.0 แสดงความมั่นใจในผลลัพธ์โดยรวม
- reasoning อธิบายสั้นๆ ว่าทำไมถึงตัดสินใจแบบนี้
- expense_category ให้เลือกจาก: ค่าสินค้า, ค่าบริการ, ค่าเช่า, ค่าสาธารณูปโภค, ค่าขนส่ง, ค่าโฆษณา, ค่าซ่อมแซม, ค่าวัสดุสำนักงาน, ค่าเดินทาง, ค่าที่ปรึกษา, ค่าประกัน, อื่นๆ
- suggested_accounts ให้แนะนำรหัสบัญชีที่น่าจะใช้บันทึก (ตามมาตรฐานผังบัญชีไทย):
  - debit_account: บัญชีเดบิต เช่น 5100=ต้นทุนขาย, 5200=ค่าใช้จ่ายในการขาย, 5300=ค่าใช้จ่ายบริหาร, 1200=สินค้าคงเหลือ, 1400=ภาษีซื้อ
  - credit_account: บัญชีเครดิต เช่น 2100=เจ้าหนี้การค้า, 1110=เงินสด, 1120=ธนาคาร
- has_wht ถ้าเอกสารมีภาษีหัก ณ ที่จ่าย ให้ระบุ true + wht_rate (1, 2, 3, 5 %)
- payment_terms ถ้าเห็นเงื่อนไขชำระเงิน เช่น "ชำระภายใน 30 วัน" ให้ระบุจำนวนวัน
- document_number = เลขที่ของ "ตัวเอกสารนี้" **เลขเดียวเท่านั้น** และต้องเป็นเลขที่
  ปรากฏบนเอกสารจริงตรงตามตัวอักษร — ห้ามเอาเลขหลายชุดมาต่อ/ผสมกันเด็ดขาด
  ถ้าเอกสารมีหลายเลข (เช่นบิลค่าไฟ/ค่าน้ำ มีทั้ง "เลขที่ (No.)" และ
  "เลขที่ใบแจ้งหนี้ (Invoice No.)") ให้ใช้ "เลขที่ (No.)" ของใบกำกับ/ใบเสร็จ
  เป็น document_number — เลขที่ใบแจ้งหนี้/เลขที่สัญญา/หมายเลขผู้ใช้/รหัสเครื่องวัด
  เป็นเลขอ้างอิง ไม่ใช่เลขที่เอกสาร
- items[].unit = หน่วยนับตามที่พิมพ์บนเอกสาร ถ้าเอกสารไม่พิมพ์ให้อนุมานจากชนิด
  รายการ: ค่าไฟฟ้า="หน่วย" (kWh), น้ำประปา="ลบ.ม.", น้ำมัน="ลิตร",
  ค่าเช่า/ค่าบริการรายเดือน="เดือน", ค่าแรง/บริการเหมา="งาน", สินค้าทั่วไป="ชิ้น"
  — ห้ามใส่ "ชิ้น" กับค่าสาธารณูปโภค/บริการ

ตอบเป็น JSON เท่านั้น ห้ามมีข้อความอื่น:
{
  "document_type": "string or null",
  "confidence": 0.0,
  "vendor_name": "string or null",
  "vendor_tax_id": "string or null",
  "document_number": "string or null",
  "document_date": "YYYY-MM-DD or null",
  "subtotal": null,
  "vat_amount": null,
  "total_amount": null,
  "items": [{"description": "...", "quantity": 1, "unit": "ชิ้น", "unit_price": 100, "amount": 100, "suggested_account_code": "5300"}],
  "expense_category": "string or null",
  "suggested_accounts": {
    "debit_account_code": "5300",
    "debit_account_name": "ค่าใช้จ่ายบริหาร",
    "credit_account_code": "2100",
    "credit_account_name": "เจ้าหนี้การค้า",
    "vat_account_code": "1400",
    "vat_account_name": "ภาษีซื้อ"
  },
  "has_wht": false,
  "wht_rate": null,
  "payment_terms_days": null,
  "reasoning": "string"
}

ข้อความ OCR:
"""


async def check_ollama_health() -> tuple[bool, str | None]:
    """Check if Ollama is running and model is available."""
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            resp = await client.get(f"{OLLAMA_BASE_URL}/api/tags")
            if resp.status_code == 200:
                data = resp.json()
                models = [m["name"] for m in data.get("models", [])]
                model_base = AI_MODEL.split(":")[0]
                available = any(model_base in m for m in models)
                if available:
                    return True, AI_MODEL
                return False, f"Model {AI_MODEL} not found. Available: {models}"
            return False, f"Ollama returned {resp.status_code}"
    except Exception as e:
        return False, f"Cannot reach Ollama: {e}"


async def extract_structured_data(raw_text: str) -> dict | None:
    """Use local LLM (Typhoon via Ollama) to extract structured data from OCR text."""
    if not raw_text or len(raw_text.strip()) < 10:
        return None

    prompt = EXTRACTION_PROMPT + raw_text

    try:
        async with httpx.AsyncClient(timeout=120.0) as client:
            resp = await client.post(
                f"{OLLAMA_BASE_URL}/api/generate",
                json={
                    "model": AI_MODEL,
                    "prompt": prompt,
                    "stream": False,
                    "options": {
                        "temperature": 0.1,
                        "num_predict": 2048,
                    },
                },
            )

            if resp.status_code != 200:
                logger.warning(f"Ollama returned {resp.status_code}")
                return None

            data = resp.json()
            response_text = data.get("response", "")

            json_start = response_text.find("{")
            json_end = response_text.rfind("}") + 1
            if json_start == -1 or json_end == 0:
                logger.warning("No JSON found in LLM response")
                return None

            result = json.loads(response_text[json_start:json_end])
            return result

    except json.JSONDecodeError as e:
        logger.warning(f"Failed to parse LLM JSON: {e}")
        return None
    except Exception as e:
        logger.error(f"AI extraction failed: {e}")
        return None


async def extract_with_fallback(raw_text: str) -> dict | None:
    """Try AI extraction, fall back to rule-based if Ollama unavailable."""
    is_healthy, _ = await check_ollama_health()
    if is_healthy:
        result = await extract_structured_data(raw_text)
        if result:
            return result

    return rule_based_extraction(raw_text)


def rule_based_extraction(text: str) -> dict:
    """Fallback rule-based extraction for Thai documents when AI is unavailable."""
    import re

    result = {
        "document_type": None,
        "confidence": 0.4,
        "vendor_name": None,
        "vendor_tax_id": None,
        "document_number": None,
        "document_date": None,
        "subtotal": None,
        "vat_amount": None,
        "total_amount": None,
        "items": [],
        "reasoning": "Rule-based extraction (AI unavailable)",
    }

    if "ใบกำกับภาษี" in text or "ใบกํากับภาษี" in text or "TAX INVOICE" in text.upper():
        result["document_type"] = "TaxInvoice"
        result["confidence"] = 0.7
    elif "ใบเสร็จรับเงิน" in text or "RECEIPT" in text.upper():
        result["document_type"] = "Receipt"
        result["confidence"] = 0.7
    elif "ใบแจ้งหนี้" in text or "INVOICE" in text.upper():
        result["document_type"] = "Invoice"
        result["confidence"] = 0.7
    elif "ใบสั่งซื้อ" in text or "PURCHASE ORDER" in text.upper():
        result["document_type"] = "PurchaseOrder"
        result["confidence"] = 0.6
    elif "หนังสือรับรอง" in text or "50 ทวิ" in text or "ภาษีหัก ณ ที่จ่าย" in text:
        result["document_type"] = "WHT"
        result["confidence"] = 0.7
    elif "ใบลดหนี้" in text or "CREDIT NOTE" in text.upper():
        result["document_type"] = "CreditNote"
        result["confidence"] = 0.7
    elif "ใบเพิ่มหนี้" in text or "DEBIT NOTE" in text.upper():
        result["document_type"] = "DebitNote"
        result["confidence"] = 0.7
    elif "ใบรับรองแทนใบเสร็จ" in text or "CERTIFICATE IN LIEU" in text.upper():
        result["document_type"] = "CertificateInLieu"
        result["confidence"] = 0.7

    # Extract ALL tax IDs and company names to distinguish vendor from our company
    tax_id_pattern = r"(\d{1}\s*-?\s*\d{4}\s*-?\s*\d{5}\s*-?\s*\d{2}\s*-?\s*\d{1})"
    all_tax_ids = [(re.sub(r"[\s-]", "", m.group()), m.start()) for m in re.finditer(tax_id_pattern, text)]
    if not all_tax_ids:
        all_tax_ids = [(m.group(), m.start()) for m in re.finditer(r"(\d{13})", text)]
    all_tax_ids = [(tid, pos) for tid, pos in all_tax_ids if len(tid) == 13]

    # Extract company names
    company_pattern = r"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่)|$)"
    company_matches = [(m.group().strip(), m.start()) for m in re.finditer(company_pattern, text, re.MULTILINE)]

    # Identify seller vs buyer sections
    seller_keywords = ["ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "SELLER", "FROM", "ผู้ออก"]
    buyer_keywords = ["ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "ส่งถึง"]

    seller_pos = -1
    buyer_pos = -1
    for kw in seller_keywords:
        pos = text.find(kw)
        if pos >= 0:
            seller_pos = pos
            break
    for kw in buyer_keywords:
        pos = text.find(kw)
        if pos >= 0:
            buyer_pos = pos
            break

    if len(company_matches) >= 2 and seller_pos >= 0 and buyer_pos >= 0:
        seller_company = min(company_matches, key=lambda c: abs(c[1] - seller_pos))
        result["vendor_name"] = seller_company[0]
        if len(all_tax_ids) >= 2:
            vendor_tax = min(all_tax_ids, key=lambda t: abs(t[1] - seller_pos))
            result["vendor_tax_id"] = vendor_tax[0]
        elif all_tax_ids:
            result["vendor_tax_id"] = all_tax_ids[0][0]
    elif company_matches:
        result["vendor_name"] = company_matches[0][0]
        if all_tax_ids:
            result["vendor_tax_id"] = all_tax_ids[0][0]
    elif all_tax_ids:
        result["vendor_tax_id"] = all_tax_ids[0][0]

    doc_num_patterns = [
        r"เลขที่\s*[:：]?\s*([A-Za-z0-9\-/]+)",
        r"No\.?\s*[:：]?\s*([A-Za-z0-9\-/]+)",
        r"INV[\-/]?\s*(\d+)",
        r"REC[\-/]?\s*(\d+)",
    ]
    for pattern in doc_num_patterns:
        match = re.search(pattern, text)
        if match:
            result["document_number"] = match.group(1)
            break

    # รอบ 193 (คำตัดสินเจ้าของข้อ 27): ป้ายก่อนตำแหน่ง · แบบไทยก่อน · ปี 2 หลักได้ — ตัวเดิมรู้จักแต่ปี 4 หลัก
    # และหยิบวันที่ตัวแรกของหน้า (วันครบกำหนดได้เป็นวันที่เอกสาร) · ฝั่ง C# (OcrDateReader) ยังตรวจซ้ำทุกใบ
    document_date = read_document_date(text)
    if document_date:
        result["document_date"] = document_date

    amount_patterns = [
        (r"(?:รวม(?:เงิน)?(?:ทั้งสิ้น|ทั้งหมด|สุทธิ)|TOTAL|GRAND\s*TOTAL|ยอดรวม(?:สุทธิ)?)\s*[:：]?\s*([\d,]+\.?\d*)", "total_amount"),
        (r"(?:ภาษีมูลค่าเพิ่ม|VAT|Vat)\s*(?:7%?)?\s*[:：]?\s*([\d,]+\.?\d*)", "vat_amount"),
        (r"(?:รวมเงิน|ราคารวม|SUB\s*TOTAL|Subtotal)\s*[:：]?\s*([\d,]+\.?\d*)", "subtotal"),
    ]
    for pattern, field in amount_patterns:
        match = re.search(pattern, text, re.IGNORECASE)
        if match:
            amount_str = match.group(1).replace(",", "")
            try:
                result[field] = float(amount_str)
            except ValueError:
                pass

    # Suggest GL accounts based on document type
    doc_type = result.get("document_type")
    account_map = {
        "TaxInvoice": {"debit": ("5100", "ต้นทุนขาย"), "credit": ("2100", "เจ้าหนี้การค้า"), "category": "ค่าสินค้า"},
        "Invoice": {"debit": ("5100", "ต้นทุนขาย"), "credit": ("2100", "เจ้าหนี้การค้า"), "category": "ค่าสินค้า"},
        "Receipt": {"debit": ("5300", "ค่าใช้จ่ายบริหาร"), "credit": ("1110", "เงินสด"), "category": "ค่าบริการ"},
        "PurchaseOrder": {"debit": ("1200", "สินค้าคงเหลือ"), "credit": ("2100", "เจ้าหนี้การค้า"), "category": "ค่าสินค้า"},
        "WHT": {"debit": ("2170", "ภาษีหัก ณ ที่จ่าย"), "credit": ("1110", "เงินสด"), "category": "อื่นๆ"},
        "CreditNote": {"debit": ("2100", "เจ้าหนี้การค้า"), "credit": ("5100", "ต้นทุนขาย"), "category": "ค่าสินค้า"},
        "DebitNote": {"debit": ("5100", "ต้นทุนขาย"), "credit": ("2100", "เจ้าหนี้การค้า"), "category": "ค่าสินค้า"},
        "CertificateInLieu": {"debit": ("5300", "ค่าใช้จ่ายบริหาร"), "credit": ("1110", "เงินสด"), "category": "ค่าบริการ"},
    }

    if doc_type and doc_type in account_map:
        mapping = account_map[doc_type]
        result["suggested_accounts"] = {
            "debit_account_code": mapping["debit"][0],
            "debit_account_name": mapping["debit"][1],
            "credit_account_code": mapping["credit"][0],
            "credit_account_name": mapping["credit"][1],
            "vat_account_code": "1400" if result.get("vat_amount") else None,
            "vat_account_name": "ภาษีซื้อ" if result.get("vat_amount") else None,
        }
        result["expense_category"] = mapping["category"]

    # Detect WHT from text
    wht_match = re.search(r"หัก\s*ณ\s*ที่จ่าย|ภาษี\s*หัก|WHT|W/?T", text)
    if wht_match:
        result["has_wht"] = True
        rate_match = re.search(r"(\d+)\s*%", text[max(0, wht_match.start()-20):wht_match.end()+30])
        if rate_match:
            rate = int(rate_match.group(1))
            if rate in (1, 2, 3, 5, 10, 15):
                result["wht_rate"] = rate

    # Detect payment terms
    terms_match = re.search(r"(?:ชำระ|จ่าย).*?(?:ภายใน|within)\s*(\d+)\s*(?:วัน|days)", text, re.IGNORECASE)
    if terms_match:
        result["payment_terms_days"] = int(terms_match.group(1))

    return result
