import io
import logging
from PIL import Image
import numpy as np

logger = logging.getLogger(__name__)

_ocr_instance = None
_tesseract_available = None


def get_ocr():
    global _ocr_instance
    if _ocr_instance is None:
        from paddleocr import PaddleOCR
        _ocr_instance = PaddleOCR(
            use_angle_cls=True,
            lang="th",
            use_gpu=False,
            show_log=False,
            det_db_thresh=0.3,
            rec_batch_num=6,
        )
        logger.info("PaddleOCR initialized (Thai)")
    return _ocr_instance


def _tesseract_ready() -> bool:
    """Probe Tesseract once; cache result. Used only for verification of low-confidence
    PaddleOCR detections — keeps Tesseract optional (engine still works without it)."""
    global _tesseract_available
    if _tesseract_available is not None:
        return _tesseract_available
    try:
        import pytesseract
        pytesseract.get_tesseract_version()
        _tesseract_available = True
        logger.info("Tesseract available — will be used for low-confidence verification")
    except Exception as e:
        _tesseract_available = False
        logger.info(f"Tesseract not available ({e}) — using PaddleOCR only")
    return _tesseract_available


def _tesseract_recognize(crop_image: Image.Image) -> tuple[str, float]:
    """Run Tesseract on a cropped region. Returns (text, confidence).
    Confidence is averaged across detected words; 0 if Tesseract returns nothing."""
    if not _tesseract_ready():
        return "", 0.0
    try:
        import pytesseract
        # Try Thai+English; fall back to eng if Thai language pack missing
        try:
            data = pytesseract.image_to_data(
                crop_image, lang="tha+eng",
                output_type=pytesseract.Output.DICT,
            )
        except Exception:
            data = pytesseract.image_to_data(
                crop_image, lang="eng",
                output_type=pytesseract.Output.DICT,
            )
        words = [(w, c) for w, c in zip(data["text"], data["conf"]) if w.strip() and int(c) > 0]
        if not words:
            return "", 0.0
        text = " ".join(w for w, _ in words)
        avg_conf = sum(int(c) for _, c in words) / len(words) / 100.0
        return text, avg_conf
    except Exception as e:
        logger.warning(f"Tesseract recognize failed: {e}")
        return "", 0.0


def extract_text(image_bytes: bytes) -> tuple[str, list[dict]]:
    """Extract text from image bytes using PaddleOCR primary + Tesseract verification.

    For each PaddleOCR detection with confidence below 0.70, we crop the bounding
    box and re-run Tesseract on it. If Tesseract gives a higher confidence, we
    use that text instead. This combination is consistently more accurate on
    Thai invoices than either engine alone.

    Returns (full_text, detailed_results) where each detailed entry includes:
      - text, confidence, box
      - engine: "paddleocr" or "tesseract" (which one's output was kept)
      - paddle_conf, tesseract_conf (when both were tried, for transparency)
    """
    ocr = get_ocr()

    image = Image.open(io.BytesIO(image_bytes))
    if image.mode != "RGB":
        image = image.convert("RGB")
    img_array = np.array(image)

    results = ocr.ocr(img_array, cls=True)

    if not results or not results[0]:
        return "", []

    detailed: list[dict] = []
    use_tess = _tesseract_ready()
    LOW_CONF_THRESHOLD = 0.70

    for line in results[0]:
        box, (paddle_text, paddle_conf) = line[0], line[1]
        text = paddle_text
        conf = float(paddle_conf)
        engine = "paddleocr"
        tess_conf = None

        # Verify low-confidence detections with Tesseract
        if use_tess and conf < LOW_CONF_THRESHOLD:
            try:
                xs = [p[0] for p in box]
                ys = [p[1] for p in box]
                left, top, right, bottom = int(min(xs)), int(min(ys)), int(max(xs)), int(max(ys))
                # Add small padding for Tesseract margin
                left = max(0, left - 3); top = max(0, top - 3)
                right = min(image.width, right + 3); bottom = min(image.height, bottom + 3)
                if right > left and bottom > top:
                    crop = image.crop((left, top, right, bottom))
                    tess_text, tess_conf_val = _tesseract_recognize(crop)
                    tess_conf = tess_conf_val
                    if tess_text.strip() and tess_conf_val > conf:
                        text = tess_text
                        conf = tess_conf_val
                        engine = "tesseract"
            except Exception as e:
                logger.debug(f"Tesseract verification skipped: {e}")

        entry = {
            "text": text,
            "confidence": conf,
            "box": [[float(p[0]), float(p[1])] for p in box],
            "engine": engine,
            "paddle_conf": float(paddle_conf),
        }
        if tess_conf is not None:
            entry["tesseract_conf"] = tess_conf
        detailed.append(entry)

    detailed.sort(key=lambda d: (min(p[1] for p in d["box"]), min(p[0] for p in d["box"])))
    full_text = "\n".join(d["text"] for d in detailed)
    return full_text, detailed


def extract_text_from_pdf(pdf_bytes: bytes) -> tuple[str, list[dict]]:
    """Extract text from PDF by converting pages to images."""
    try:
        from pdf2image import convert_from_bytes
        images = convert_from_bytes(pdf_bytes, dpi=300)
    except ImportError:
        logger.warning("pdf2image not installed, trying first page only")
        return extract_text(pdf_bytes)

    all_text = []
    all_detailed = []
    for i, img in enumerate(images):
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        text, detailed = extract_text(buf.getvalue())
        for d in detailed:
            d["page"] = i
        all_text.append(text)
        all_detailed.extend(detailed)

    return "\n---PAGE BREAK---\n".join(all_text), all_detailed
