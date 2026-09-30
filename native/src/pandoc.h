#pragma once

#include "common.h"

#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace mdv {

struct ExportFormat {
    const char* label;
    const char* extension;
    const char* writer;
};
extern const std::vector<ExportFormat> ExportFormats;
extern const std::vector<std::pair<const char*, const char*>> PandocImports;  // extension, reader

void set_data_directory(std::string directory);
const std::string& data_directory();

std::optional<std::string> find_pandoc();
std::string pandoc_path();  // throws with guidance when missing

std::string pandoc_import(const std::string& path, const Progress& progress);
std::string pandoc_format(std::string_view markdown, const Progress& progress);
void pandoc_export(std::string_view markdown, const std::string& target, const std::string& resource_directory, const Progress& progress);
// For the crawler: HTML or MediaWiki text to Markdown; failures report and return "".
std::string pandoc_convert(const std::string& from, std::string_view input, const Progress& progress);

// Splits a leading YAML metadata block ("---" ... "---" or "...") from the body.
std::pair<std::optional<std::string>, std::string> split_metadata_block(std::string_view markdown);

}  // namespace mdv
