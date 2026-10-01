#pragma once

#include "common.h"

#include <optional>
#include <string>
#include <vector>

namespace mdv {

// External programs md-viewer uses: Pandoc (conversion) and Tesseract (OCR).
std::optional<std::string> find_tesseract();
std::vector<std::string> tesseract_languages(const std::string& executable);

// Installed paths and versions, Windows OCR languages, the OCR preference, and (with check_latest)
// the newest official release of each tool, as JSON for the Tools window.
std::string tools_status_json(bool check_latest, const Progress& progress);

// "pandoc": downloads the latest official Windows release into md-viewer's data folder.
// "tesseract": downloads the official installer (SHA-256 checked) and runs it; Windows asks for
// administrator permission. Returns a status line.
std::string install_tool(const std::string& tool, const Progress& progress);

// Compares dotted version strings numerically ("5.5.3.20260724" > "5.5.0.20241111").
int compare_versions(const std::string& a, const std::string& b);

}  // namespace mdv
