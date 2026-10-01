#pragma once

#include "common.h"

#include <string>
#include <vector>

namespace mdv {

struct OcrPreferences {
    std::string engine = "auto";  // "auto" (Tesseract when installed), "windows", or "tesseract"
    std::string languages = "eng";  // Tesseract languages, e.g. "eng+deu"
};
void set_ocr_preferences(OcrPreferences preferences);
OcrPreferences ocr_preferences();
std::string active_ocr_engine();  // "tesseract" or "windows"

struct PdfImport {
    std::string markdown;
    std::string ocr_engine;  // e.g. "Tesseract 5.5.3" or "Windows OCR"; empty when no page needed OCR
    int page_count = 0;
    int embedded_pages = 0;
    std::vector<int> ocr_pages;
    std::vector<int> missing_pages;
    std::vector<std::string> warnings;
};

// Extracts embedded text with PDFium and runs OCR (Tesseract or Windows OCR) on pages whose text layer is
// missing or weak. The Markdown starts with a hidden diagnostics comment.
PdfImport import_pdf(const std::string& path, const Progress& progress);

}  // namespace mdv
