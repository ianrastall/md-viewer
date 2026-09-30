#pragma once

#include "common.h"

#include <string>
#include <vector>

namespace mdv {

struct PdfImport {
    std::string markdown;
    int page_count = 0;
    int embedded_pages = 0;
    std::vector<int> ocr_pages;
    std::vector<int> missing_pages;
    std::vector<std::string> warnings;
};

// Extracts embedded text with PDFium and runs Windows OCR on pages whose text layer is
// missing or weak. The Markdown starts with a hidden diagnostics comment.
PdfImport import_pdf(const std::string& path, const Progress& progress);

}  // namespace mdv
