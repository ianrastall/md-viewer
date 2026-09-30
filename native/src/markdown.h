#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace mdv {

// The binary document model the C# renderer draws (format described in markdown.cpp).
std::vector<uint8_t> parse_markdown(std::string_view markdown);

// "<changed>\t<total>\t<warning count>\n<warning>\n...<markdown>".
std::string reflow_headings(std::string_view markdown);

}  // namespace mdv
