import json
import logging
import httpx
import os

logger = logging.getLogger(__name__)

OLLAMA_BASE_URL = os.environ.get("OLLAMA_BASE_URL", "http://localhost:11434")
AI_MODEL = os.environ.get("AI_MODEL", "typhoon2:8b")

EXTRACTION_PROMPT = """คุณเป็น AI ผู้เชี่ยวชาญเอกสารบัญชีภาษาไทย ช่วยวิเคราะห์ข้อความที่ได้จาก OCR แล้วดึงข้อมูลออกมาเป็น JSON

กฎ:
- ถ้าไม่แน่ใจว่าข้อมูลคืออะไร ให้ใส่ null
- Tax ID ไทยมี 13 หลัก
- ประเภทเอกสาร: Invoice, Receipt, TaxInvoice, CreditNote, DebitNote, PurchaseOrder, WHT, Other
- วันที่ให้แปลงเป็น YYYY-MM-DD
- จำนวนเงินเป็นตัวเลข (ไม่มี comma)
- confidence เป็น 0.0-1.0 แสดงความมั่นใจในผลลัพธ์โดยรวม
- reasoning อธิบายสั้นๆ ว่าทำไมถึงตัดสินใจแบบนี้

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
  "items": [{"description": "...", "quantity": 1, "unit_price": 100, "amount": 100}],
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

    tax_id_match = re.search(r"\d{1}\s*-?\s*\d{4}\s*-?\s*\d{5}\s*-?\s*\d{2}\s*-?\s*\d{1}", text)
    if not tax_id_match:
        tax_id_match = re.search(r"(\d{13})", text)
    if tax_id_match:
        result["vendor_tax_id"] = re.sub(r"[\s-]", "", tax_id_match.group())

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

    date_patterns = [
        r"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{4})",
        r"(\d{1,2})\s+(ม\.?ค\.?|ก\.?พ\.?|มี\.?ค\.?|เม\.?ย\.?|พ\.?ค\.?|มิ\.?ย\.?|ก\.?ค\.?|ส\.?ค\.?|ก\.?ย\.?|ต\.?ค\.?|พ\.?ย\.?|ธ\.?ค\.?)\s+(\d{4})",
    ]
    for pattern in date_patterns:
        match = re.search(pattern, text)
        if match:
            groups = match.groups()
            if len(groups) == 3 and groups[0].isdigit():
                day = int(groups[0])
                month_str = groups[1]
                year = int(groups[2])
                if year > 2500:
                    year -= 543
                if month_str.isdigit():
                    month = int(month_str)
                else:
                    thai_months = {
                        "ม.ค": 1, "มค": 1, "ก.พ": 2, "กพ": 2,
                        "มี.ค": 3, "มีค": 3, "เม.ย": 4, "เมย": 4,
                        "พ.ค": 5, "พค": 5, "มิ.ย": 6, "มิย": 6,
                        "ก.ค": 7, "กค": 7, "ส.ค": 8, "สค": 8,
                        "ก.ย": 9, "กย": 9, "ต.ค": 10, "ตค": 10,
                        "พ.ย": 11, "พย": 11, "ธ.ค": 12, "ธค": 12,
                    }
                    month = 1
                    for key, val in thai_months.items():
                        if key in month_str:
                            month = val
                            break
                result["document_date"] = f"{year:04d}-{month:02d}-{day:02d}"
            break

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

    return result
