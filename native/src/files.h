#pragma once

#include <string>
#include <string_view>

namespace mdv {

struct TextFile {
    std::string text;      // UTF-8
    std::string encoding;  // "UTF-8", "UTF-8 BOM", "UTF-16 LE", "UTF-16 BE", or "Windows-1252"
};

constexpr unsigned long long MaxDocumentBytes = 64ull * 1024 * 1024;

std::string read_file(const std::string& path, unsigned long long limit = MaxDocumentBytes);
TextFile decode_text(std::string_view bytes);
TextFile read_document(const std::string& path);

// Writes through a temporary file in the same folder, then replaces the target, so a failed
// save never truncates the original. Returns the encoding used (legacy encodings fall back
// to UTF-8 when the text does not fit).
std::string write_document(const std::string& path, std::string_view utf8, const std::string& encoding);
void write_file_atomic(const std::string& path, std::string_view bytes);

}  // namespace mdv
