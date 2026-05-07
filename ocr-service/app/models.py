from pydantic import BaseModel
from typing import Optional
from datetime import datetime


class OcrRequest(BaseModel):
    file_name: str
    content_type: str = "image/jpeg"


class LineItem(BaseModel):
    description: str | None = None
    quantity: float | None = None
    unit_price: float | None = None
    amount: float | None = None


class OcrResult(BaseModel):
    raw_text: str = ""
    document_type: str | None = None
    confidence: float = 0.0
    vendor_name: str | None = None
    vendor_tax_id: str | None = None
    document_number: str | None = None
    document_date: str | None = None
    subtotal: float | None = None
    vat_amount: float | None = None
    total_amount: float | None = None
    items: list[LineItem] = []
    reasoning: str | None = None
    ocr_engine: str = "paddleocr"
    ai_engine: str | None = None


class CorrectionRequest(BaseModel):
    original_text: str
    original_result: OcrResult
    corrected_result: OcrResult
    document_type: str | None = None


class HealthResponse(BaseModel):
    status: str
    ocr_ready: bool
    ai_ready: bool
    ai_model: str | None = None
