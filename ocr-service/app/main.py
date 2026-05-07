import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, File, UploadFile, HTTPException
from fastapi.middleware.cors import CORSMiddleware

from .models import OcrResult, CorrectionRequest, HealthResponse, SuggestedAccounts, LineItem
from .ocr_engine import extract_text, extract_text_from_pdf
from .ai_engine import extract_with_fallback, check_ollama_health, AI_MODEL
from .learning import save_correction, get_training_stats, export_for_finetuning

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("OCR+AI Service starting...")
    logger.info(f"Ollama URL: {os.environ.get('OLLAMA_BASE_URL', 'http://localhost:11434')}")
    logger.info(f"AI Model: {AI_MODEL}")

    # Pre-load PaddleOCR model (primary)
    try:
        from .ocr_engine import get_ocr
        get_ocr()
        logger.info("PaddleOCR loaded successfully")
    except Exception as e:
        logger.warning(f"PaddleOCR pre-load failed (will retry on first request): {e}")

    # Pre-load EasyOCR model (verification ensemble partner).
    # Background-load to keep startup snappy — first request still works while it warms up.
    try:
        from .ocr_engine import _get_easyocr
        _get_easyocr()
        logger.info("EasyOCR loaded successfully — ensemble ready")
    except Exception as e:
        logger.info(f"EasyOCR pre-load skipped: {e} (PaddleOCR-only mode is still functional)")

    yield
    logger.info("OCR+AI Service shutting down")


app = FastAPI(
    title="Thai Accounting OCR+AI Service",
    version="1.0.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health", response_model=HealthResponse)
async def health_check():
    ai_ready, ai_info = await check_ollama_health()
    try:
        from .ocr_engine import get_ocr, _get_easyocr
        get_ocr()
        ocr_ready = True
        # EasyOCR is optional — if it fails, primary OCR still works
        easy_loaded = _get_easyocr() is not None
    except Exception:
        ocr_ready = False
        easy_loaded = False

    return HealthResponse(
        status="ok" if ocr_ready else "degraded",
        ocr_ready=ocr_ready,
        ai_ready=ai_ready,
        ai_model=f"{AI_MODEL} | EasyOCR={'✓' if easy_loaded else '✗'}" if ai_ready
                 else f"{ai_info} | EasyOCR={'✓' if easy_loaded else '✗'}",
    )


def _build_field_confidence(detailed: list[dict], extracted_values: dict) -> dict[str, float]:
    """For each extracted field value, find which OCR detection contained that string
    and use its confidence. Fields whose values aren't found in OCR text get 0.5
    (default uncertainty — the AI inferred them rather than copy-pasting).

    This gives the C# layer field-level confidence parity with Azure DI's output,
    so the same UI badges (per-field confidence %) work for both providers.
    """
    field_conf: dict[str, float] = {}

    # Map our snake_case fields to canonical PascalCase used by Azure DI mapper
    field_map = {
        "vendor_name": "VendorName",
        "vendor_tax_id": "VendorTaxId",
        "buyer_name": "CustomerName",
        "buyer_tax_id": "CustomerTaxId",
        "document_number": "InvoiceId",
        "document_date": "InvoiceDate",
        "subtotal": "SubTotal",
        "vat_amount": "TotalTax",
        "total_amount": "InvoiceTotal",
    }

    for field_key, canonical in field_map.items():
        value = extracted_values.get(field_key)
        if value is None or value == "":
            continue
        value_str = str(value).strip()
        if not value_str:
            continue

        # Find any OCR detection whose text contains the extracted value (or vice versa
        # for short tokens like dates that might have been re-formatted)
        best_conf = 0.0
        for d in detailed:
            t = d.get("text", "").strip()
            if not t:
                continue
            # Substring either direction; ignore whitespace differences
            t_compact = "".join(t.split())
            v_compact = "".join(value_str.split())
            if v_compact in t_compact or t_compact in v_compact:
                conf = float(d.get("confidence", 0))
                if conf > best_conf:
                    best_conf = conf
        # If no match found, AI hallucinated/inferred — use 0.5 as soft uncertainty
        field_conf[canonical] = best_conf if best_conf > 0 else 0.5

    return field_conf


@app.post("/ocr/extract", response_model=OcrResult)
async def extract_document(file: UploadFile = File(...)):
    """Extract structured data from uploaded document image/PDF.

    Pipeline:
      1. OCR (PaddleOCR primary + Tesseract verification on low-confidence regions)
      2. AI structured extraction (Ollama if configured, else rule-based)
      3. Build per-field confidence map (parity with Azure DI output shape)
      4. Detect multi-page / multi-document warnings
    """
    if not file.content_type:
        raise HTTPException(400, "Missing content type")

    allowed_types = [
        "image/jpeg", "image/png", "image/tiff", "image/webp", "image/bmp",
        "application/pdf",
    ]
    if file.content_type not in allowed_types:
        raise HTTPException(400, f"Unsupported file type: {file.content_type}")

    file_bytes = await file.read()
    if len(file_bytes) == 0:
        raise HTTPException(400, "Empty file")
    if len(file_bytes) > 50 * 1024 * 1024:
        raise HTTPException(400, "File too large (max 50MB)")

    # Step 1: OCR (with Tesseract verification baked into extract_text)
    warnings: list[str] = []
    pages_text: list[str] = []
    page_count = 1
    try:
        if file.content_type == "application/pdf":
            raw_text, detailed = extract_text_from_pdf(file_bytes)
            # Count "PAGE BREAK" markers + 1 = total pages
            pages_text = raw_text.split("\n---PAGE BREAK---\n")
            page_count = len(pages_text)
            if page_count > 1:
                warnings.append(f"พบ PDF {page_count} หน้า — ระบบประมวลผลรวมทุกหน้าเป็นเอกสารเดียว")
        else:
            raw_text, detailed = extract_text(file_bytes)
            pages_text = [raw_text]
    except Exception as e:
        logger.error(f"OCR failed: {e}")
        raise HTTPException(500, f"OCR extraction failed: {e}")

    if not raw_text.strip():
        return OcrResult(
            raw_text="",
            confidence=0.0,
            reasoning="OCR ไม่สามารถอ่านข้อความจากไฟล์นี้ได้",
            reasoning_trace=["[Local OCR] PaddleOCR + EasyOCR ไม่พบข้อความที่อ่านได้"],
            ocr_engine="paddleocr+easyocr",
            warnings=warnings,
            page_count=page_count,
        )

    # Step 2: AI structured extraction
    ai_result = await extract_with_fallback(raw_text)

    # Engine label: combined when EasyOCR actually replaced any PaddleOCR detection
    engine_label = "paddleocr+easyocr" if any(
        d.get("engine") == "easyocr" for d in detailed
    ) else "paddleocr"

    if ai_result:
        suggested = ai_result.get("suggested_accounts")
        sa = SuggestedAccounts(**suggested) if isinstance(suggested, dict) else None

        items_raw = ai_result.get("items") or []
        items = [LineItem(**it) if isinstance(it, dict) else it for it in items_raw]

        # Build per-field confidence map for plug-compatibility with Azure DI
        field_conf = _build_field_confidence(detailed, ai_result)

        # Build human-readable reasoning trace
        trace = [f"[Local OCR] Engine: {engine_label}, lines detected: {len(detailed)}"]
        avg_paddle_conf = (
            sum(d.get("paddle_conf", 0) for d in detailed) / len(detailed)
            if detailed else 0.0
        )
        trace.append(f"[Local OCR] Average PaddleOCR confidence: {avg_paddle_conf:.0%}")
        easy_replaced = sum(1 for d in detailed if d.get("engine") == "easyocr")
        if easy_replaced > 0:
            trace.append(f"[Local OCR] EasyOCR corrected {easy_replaced} low-confidence regions")
        if ai_result.get("reasoning"):
            trace.append(f"[Local AI] {ai_result['reasoning']}")

        return OcrResult(
            raw_text=raw_text,
            document_type=ai_result.get("document_type"),
            confidence=float(ai_result.get("confidence", 0.5)),
            vendor_name=ai_result.get("vendor_name"),
            vendor_tax_id=ai_result.get("vendor_tax_id"),
            buyer_name=ai_result.get("buyer_name"),
            buyer_tax_id=ai_result.get("buyer_tax_id"),
            document_number=ai_result.get("document_number"),
            document_date=ai_result.get("document_date"),
            subtotal=ai_result.get("subtotal"),
            vat_amount=ai_result.get("vat_amount"),
            total_amount=ai_result.get("total_amount"),
            items=items,
            expense_category=ai_result.get("expense_category"),
            suggested_accounts=sa,
            has_wht=bool(ai_result.get("has_wht", False)),
            wht_rate=ai_result.get("wht_rate"),
            payment_terms_days=ai_result.get("payment_terms_days"),
            reasoning=ai_result.get("reasoning"),
            reasoning_trace=trace,
            ocr_engine=engine_label,
            ai_engine=AI_MODEL if ai_result.get("reasoning") != "Rule-based extraction (AI unavailable)" else "rule-based",
            field_confidence=field_conf,
            multi_document_count=1,
            page_count=page_count,
            warnings=warnings,
            pages=pages_text if page_count > 1 else [],
        )

    return OcrResult(
        raw_text=raw_text,
        confidence=0.3,
        reasoning="ไม่สามารถวิเคราะห์โครงสร้างเอกสารได้",
        reasoning_trace=[f"[Local OCR] Engine: {engine_label}", "[Local AI] โครงสร้างไม่ครบ — ส่ง raw text กลับให้ user แก้ไข"],
        ocr_engine=engine_label,
        warnings=warnings,
        page_count=page_count,
    )


@app.post("/ocr/correct")
async def submit_correction(correction: CorrectionRequest):
    """Submit a user correction to improve future extractions."""
    filepath = save_correction(
        original_text=correction.original_text,
        original_result=correction.original_result.model_dump(),
        corrected_result=correction.corrected_result.model_dump(),
        document_type=correction.document_type,
    )
    return {"status": "saved", "file": filepath}


@app.get("/ocr/training/stats")
async def training_stats():
    """Get training data collection statistics."""
    return get_training_stats()


@app.post("/ocr/training/export")
async def export_training_data():
    """Export collected corrections as fine-tuning dataset."""
    output = export_for_finetuning()
    stats = get_training_stats()
    return {
        "status": "exported",
        "output_file": output,
        "total_entries": stats["total_corrections"],
    }
