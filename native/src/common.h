// Shared helpers for the md-viewer native core: UTF-8/UTF-16 conversion, string
// utilities, errors, and cooperative progress/cancellation through the C ABI callback.
#pragma once

#include "mdv_native.h"

#include <chrono>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace mdv {

// Thrown when the caller asks to stop; the ABI layer reports it as MDV_CANCELLED.
struct Cancelled : std::exception {
    const char* what() const noexcept override { return "Cancelled."; }
};

// A failure whose message is shown to the user as is.
struct Error : std::runtime_error {
    using std::runtime_error::runtime_error;
};

class Progress {
public:
    Progress() = default;
    Progress(mdv_progress_fn callback, void* context) : callback_(callback), context_(context) {}
    // Reports a status line; throws Cancelled if the caller has cancelled.
    void report(const std::string& message) const;
    // Polls for cancellation without a message.
    void check() const;
    bool cancelled() const;
    // Sleeps in short slices so cancellation is prompt.
    void sleep(std::chrono::milliseconds duration) const;

private:
    mdv_progress_fn callback_ = nullptr;
    void* context_ = nullptr;
};

std::wstring widen(std::string_view utf8);
std::string narrow(std::wstring_view wide);
std::string win32_message(unsigned long code, const wchar_t* module = nullptr);
[[noreturn]] void throw_win32(const std::string& what, unsigned long code);

char ascii_lower(char c);
std::string lower(std::string_view text);
bool iequals(std::string_view a, std::string_view b);
bool istarts_with(std::string_view text, std::string_view prefix);
bool iends_with(std::string_view text, std::string_view suffix);
size_t ifind(std::string_view haystack, std::string_view needle, size_t from = 0);
bool icontains(std::string_view haystack, std::string_view needle);
bool is_space(char c);
bool is_ascii_alnum(char c);
std::string_view trim(std::string_view text);
std::string_view trim_start(std::string_view text);
std::string_view trim_end(std::string_view text);
std::string replace_all(std::string text, std::string_view from, std::string_view to);
std::string normalize_newlines(std::string_view text);
std::vector<std::string> split_lines(std::string_view text);  // on '\n' only
std::string join_lines(const std::vector<std::string>& lines);
std::string collapse_whitespace(std::string_view text);  // runs of whitespace (incl. NBSP) become one space; trimmed
std::string collapse_blank_lines(std::string_view text);  // "\n{3,}" becomes "\n\n"
bool is_fence(std::string_view line);  // trimmed line starts with ``` or ~~~

void append_utf8(std::string& out, unsigned codepoint);
// Decodes HTML character references (&amp; &#169; &#x1F600;) like WebUtility.HtmlDecode.
std::string html_decode(std::string_view text);

std::string extension_of(std::string_view path);  // lower-case, with the dot; "" when none
std::string file_name_of(std::string_view path);
std::string stem_of(std::string_view path);
std::string directory_of(std::string_view path);
std::string join_path(std::string_view directory, std::string_view name);
bool file_exists(const std::string& path);
bool directory_exists(const std::string& path);
void create_directories(const std::string& path);
std::string environment(const char* name);
std::string temp_directory();
std::string new_guid();
std::string format_count(long long value);  // 12,345

}  // namespace mdv
