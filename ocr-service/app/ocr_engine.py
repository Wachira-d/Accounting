import io
import logging
from PIL import Image
import numpy as np

logger = logging.getLogger(__name__)

_paddle_instance = None
_easyocr_instance = None
_easyocr_available = None


def get_ocr():
    """Primary engine: PaddleOCR. Loaded lazily on first request."""
    global _paddle_instance
    if _paddle_instance is None:
        from paddleocr import PaddleOCR
        _paddle_instance = PaddleOCR(
            use_angle_cls=True,
            lang="th",
            use_gpu=False,
            show_log=False,
            det_db_thresh=0.3,
            rec_batch_num=6,
        )
        logger.info("PaddleOCR initialized (Thai)")
    return _paddle_instance


def _get_easyocr():
    """Secondary engine: EasyOCR. Pure-Python, pip-installable, different
    architecture (CRNN) from PaddleOCR (PP-OCRv4) — gives true ensemble value.
    Lazy-loaded; cached at module level. Returns None if init fails."""
    global _easyocr_instance, _easyocr_available
    if _easyocr_available is False:
        return None
    if _easyocr_instance is not None:
        return _easyocr_instance
    try:
        import easyocr
        # Thai + English. gpu=False keeps it CPU-bound by default; flip to True
        # if you have a GPU available — same Reader works either way.
        _easyocr_instance = easyocr.Reader(["th", "en"], gpu=False, verbose=False)
        _easyocr_available = True
        logger.info("EasyOCR initialized (Thai+English) — will be used as ensemble partner")
        return _easyocr_instance
    except Exception as e:
        _easyocr_available = False
        logger.info(f"EasyOCR not available ({e}) — using PaddleOCR only (still functional)")
        return None


def _easyocr_recognize_crop(crop_array: np.ndarray) -> tuple[str, float]:
    """Run EasyOCR on a cropped image array. Returns (text, confidence).
    Concatenates multiple text regions if EasyOCR finds more than one in the crop."""
    reader = _get_easyocr()
    if reader is None:
        return "", 0.0
    try:
        # detail=1 returns (bbox, text, confidence); paragraph=False keeps lines separate
        results = reader.readtext(crop_array, detail=1, paragraph=False)
        if not results:
            return "", 0.0
        # Sort top-to-bottom, left-to-right
        results.sort(key=lambda r: (min(p[1] for p in r[0]), min(p[0] for p in r[0])))
        text = " ".join(r[1].strip() for r in results if r[1].strip())
        # Average confidence across recognized regions
        confs = [float(r[2]) for r in results if r[2] is not None]
        avg = sum(confs) / len(confs) if confs else 0.0
        return text, avg
    except Exception as e:
        logger.warning(f"EasyOCR recognize failed: {e}")
        return "", 0.0


def extract_text(image_bytes: bytes) -> tuple[str, list[dict]]:
    """Extract text using PaddleOCR primary + EasyOCR verification ensemble.

    For each PaddleOCR detection with confidence below 0.70, we crop the bounding
    box and re-run EasyOCR (different model architecture). If EasyOCR's confidence
    is higher AND its text differs significantly, we use it. Both engines are
    pure-Python — no Docker apt-get needed.

    Returns (full_text, detailed_results) where each detailed entry includes:
      - text, confidence, box
      - engine: "paddleocr" or "easyocr" (which one's output was kept)
      - paddle_conf, easyocr_conf (when both were tried, for transparency)
    """
    paddle = get_ocr()

    image = Image.open(io.BytesIO(image_bytes))
    if image.mode != "RGB":
        image = image.convert("RGB")
    img_array = np.array(image)

    results = paddle.ocr(img_array, cls=True)

    if not results or not results[0]:
        return "", []

    detailed: list[dict] = []
    use_easy = _get_easyocr() is not None
    LOW_CONF_THRESHOLD = 0.70

    for line in results[0]:
        box, (paddle_text, paddle_conf) = line[0], line[1]
        text = paddle_text
        conf = float(paddle_conf)
        engine = "paddleocr"
        easy_conf = None

        # Verify low-confidence detections with EasyOCR
        if use_easy and conf < LOW_CONF_THRESHOLD:
            try:
                xs = [p[0] for p in box]
                ys = [p[1] for p in box]
                left, top, right, bottom = int(min(xs)), int(min(ys)), int(max(xs)), int(max(ys))
                # Tiny padding helps EasyOCR margin handling
                left = max(0, left - 4); top = max(0, top - 4)
                right = min(image.width, right + 4); bottom = min(image.height, bottom + 4)
                if right > left and bottom > top:
                    crop = img_array[top:bottom, left:right]
                    easy_text, easy_conf_val = _easyocr_recognize_crop(crop)
                    easy_conf = easy_conf_val
                    # Replace only when EasyOCR is meaningfully more confident
                    # (5pp margin) — avoids flip-flopping on near-tie cases
                    if easy_text.strip() and easy_conf_val > conf + 0.05:
                        text = easy_text
                        conf = easy_conf_val
                        engine = "easyocr"
            except Exception as e:
                logger.debug(f"EasyOCR verification skipped: {e}")

        entry = {
            "text": text,
            "confidence": conf,
            "box": [[float(p[0]), float(p[1])] for p in box],
            "engine": engine,
            "paddle_conf": float(paddle_conf),
        }
        if easy_conf is not None:
            entry["easyocr_conf"] = easy_conf
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
