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
    suggested_account_code: str | None = None


class SuggestedAccounts(BaseModel):
    debit_account_code: str | None = None
    debit_account_name: str | None = None
    credit_account_code: str | None = None
    credit_account_name: str | None = None
    vat_account_code: str | None = None
    vat_account_name: str | None = None


class OcrResult(BaseModel):
    raw_text: str = ""
    document_type: str | None = None
    confidence: float = 0.0
    vendor_name: str | None = None
    vendor_tax_id: str | None = None
    buyer_name: str | None = None
    buyer_tax_id: str | None = None
    document_number: str | None = None
    document_date: str | None = None
    subtotal: float | None = None
    vat_amount: float | None = None
    total_amount: float | None = None
    items: list[LineItem] = []
    expense_category: str | None = None
    suggested_accounts: SuggestedAccounts | None = None
    has_wht: bool = False
    wht_rate: float | None = None
    payment_terms_days: int | None = None
    reasoning: str | None = None
    reasoning_trace: list[str] = []
    ocr_engine: str = "paddleocr"
    ai_engine: str | None = None
    # ── Plug-compatibility with Azure DI ──
    # Per-field confidence map keyed by canonical field name
    # (VendorName, VendorTaxId, InvoiceId, InvoiceDate, SubTotal, TotalTax, InvoiceTotal, ...)
    field_confidence: dict[str, float] = {}
    # Multi-document detection (e.g., bundled invoices in one PDF)
    multi_document_count: int = 1
    page_count: int = 1
    # Engine warnings surfaced to user
    warnings: list[str] = []
    # Raw text per page for debug (only populated when explicitly requested)
    pages: list[str] = []


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
