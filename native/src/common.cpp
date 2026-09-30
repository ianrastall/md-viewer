#include "common.h"

extern "C" {
#include "entity.h"
}

#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <thread>

namespace mdv {

void Progress::report(const std::string& message) const {
    if (callback_ && callback_(context_, message.c_str()) != 0) throw Cancelled();
}

void Progress::check() const {
    if (cancelled()) throw Cancelled();
}

bool Progress::cancelled() const { return callback_ && callback_(context_, nullptr) != 0; }

void Progress::sleep(std::chrono::milliseconds duration) const {
    const auto until = std::chrono::steady_clock::now() + duration;
    while (true) {
        check();
        const auto now = std::chrono::steady_clock::now();
        if (now >= until) return;
        std::this_thread::sleep_for(std::min<std::chrono::steady_clock::duration>(until - now, std::chrono::milliseconds(100)));
    }
}

std::wstring widen(std::string_view utf8) {
    if (utf8.empty()) return {};
    const int length = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
    std::wstring wide(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), static_cast<int>(utf8.size()), wide.data(), length);
    return wide;
}

std::string narrow(std::wstring_view wide) {
    if (wide.empty()) return {};
    const int length = WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(static_cast<size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide.data(), static_cast<int>(wide.size()), utf8.data(), length, nullptr, nullptr);
    return utf8;
}

std::string win32_message(unsigned long code, const wchar_t* module) {
    wchar_t* buffer = nullptr;
    const HMODULE source = module ? GetModuleHandleW(module) : nullptr;
    const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_IGNORE_INSERTS | (source ? FORMAT_MESSAGE_FROM_HMODULE : FORMAT_MESSAGE_FROM_SYSTEM);
    const DWORD length = FormatMessageW(flags, source, code, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::string message = length ? std::string(trim(narrow(std::wstring_view(buffer, length)))) : "Windows error " + std::to_string(code) + ".";
    if (buffer) LocalFree(buffer);
    return message;
}

void throw_win32(const std::string& what, unsigned long code) {
    throw Error(what + " " + win32_message(code));
}

char ascii_lower(char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c + 32) : c; }

std::string lower(std::string_view text) {
    std::string result(text);
    for (auto& c : result) c = ascii_lower(c);
    return result;
}

bool iequals(std::string_view a, std::string_view b) {
    return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(), [](char x, char y) { return ascii_lower(x) == ascii_lower(y); });
}

bool istarts_with(std::string_view text, std::string_view prefix) { return text.size() >= prefix.size() && iequals(text.substr(0, prefix.size()), prefix); }
bool iends_with(std::string_view text, std::string_view suffix) { return text.size() >= suffix.size() && iequals(text.substr(text.size() - suffix.size()), suffix); }

size_t ifind(std::string_view haystack, std::string_view needle, size_t from) {
    if (needle.empty()) return from <= haystack.size() ? from : std::string_view::npos;
    for (size_t i = from; i + needle.size() <= haystack.size(); ++i)
        if (iequals(haystack.substr(i, needle.size()), needle)) return i;
    return std::string_view::npos;
}

bool icontains(std::string_view haystack, std::string_view needle) { return ifind(haystack, needle) != std::string_view::npos; }
bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v'; }
bool is_ascii_alnum(char c) { return (c >= '0' && c <= '9') || (ascii_lower(c) >= 'a' && ascii_lower(c) <= 'z'); }

std::string_view trim_start(std::string_view text) {
    while (!text.empty() && is_space(text.front())) text.remove_prefix(1);
    return text;
}
std::string_view trim_end(std::string_view text) {
    while (!text.empty() && is_space(text.back())) text.remove_suffix(1);
    return text;
}
std::string_view trim(std::string_view text) { return trim_end(trim_start(text)); }

std::string replace_all(std::string text, std::string_view from, std::string_view to) {
    if (from.empty()) return text;
    for (size_t pos = 0; (pos = text.find(from, pos)) != std::string::npos; pos += to.size()) text.replace(pos, from.size(), to);
    return text;
}

std::string normalize_newlines(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '\r') {
            out += '\n';
            if (i + 1 < text.size() && text[i + 1] == '\n') ++i;
        } else out += text[i];
    }
    return out;
}

std::vector<std::string> split_lines(std::string_view text) {
    std::vector<std::string> lines;
    size_t start = 0;
    for (size_t i = 0; i <= text.size(); ++i) {
        if (i == text.size() || text[i] == '\n') {
            lines.emplace_back(text.substr(start, i - start));
            start = i + 1;
        }
    }
    return lines;
}

std::string join_lines(const std::vector<std::string>& lines) {
    std::string out;
    for (size_t i = 0; i < lines.size(); ++i) {
        if (i) out += '\n';
        out += lines[i];
    }
    return out;
}

std::string collapse_whitespace(std::string_view text) {
    std::string out;
    bool space = false;
    for (size_t i = 0; i < text.size(); ++i) {
        const bool nbsp = static_cast<unsigned char>(text[i]) == 0xC2 && i + 1 < text.size() && static_cast<unsigned char>(text[i + 1]) == 0xA0;
        if (is_space(text[i]) || nbsp) {
            if (nbsp) ++i;
            space = !out.empty();
            continue;
        }
        if (space) { out += ' '; space = false; }
        out += text[i];
    }
    return out;
}

std::string collapse_blank_lines(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    size_t run = 0;
    for (const char c : text) {
        run = c == '\n' ? run + 1 : 0;
        if (run <= 2) out += c;
    }
    return out;
}

bool is_fence(std::string_view line) {
    const auto t = trim(line);
    return t.starts_with("```") || t.starts_with("~~~");
}

void append_utf8(std::string& out, unsigned cp) {
    if (cp == 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
    if (cp < 0x80) out += static_cast<char>(cp);
    else if (cp < 0x800) { out += static_cast<char>(0xC0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) {
        out += static_cast<char>(0xE0 | (cp >> 12));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
        out += static_cast<char>(0xF0 | (cp >> 18));
        out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
        out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
        out += static_cast<char>(0x80 | (cp & 0x3F));
    }
}

std::string html_decode(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] != '&') { out += text[i]; continue; }
        const auto end = text.find(';', i + 1);
        if (end == std::string_view::npos || end - i > 40) { out += '&'; continue; }
        const auto entity = text.substr(i, end - i + 1);
        if (entity.size() >= 4 && entity[1] == '#') {
            const bool hex = entity[2] == 'x' || entity[2] == 'X';
            unsigned long long cp = 0;
            bool valid = entity.size() > (hex ? 4u : 3u);
            for (size_t k = hex ? 3 : 2; valid && k + 1 < entity.size(); ++k) {
                const char c = entity[k];
                const int digit = c >= '0' && c <= '9' ? c - '0' : hex && ascii_lower(c) >= 'a' && ascii_lower(c) <= 'f' ? ascii_lower(c) - 'a' + 10 : -1;
                if (digit < 0) valid = false;
                else cp = std::min<unsigned long long>(cp * (hex ? 16 : 10) + static_cast<unsigned>(digit), 0x110000);
            }
            if (valid) { append_utf8(out, static_cast<unsigned>(cp)); i = end; continue; }
        } else if (const ENTITY* known = entity_lookup(entity.data(), entity.size())) {
            append_utf8(out, known->codepoints[0]);
            if (known->codepoints[1]) append_utf8(out, known->codepoints[1]);
            i = end;
            continue;
        }
        out += '&';
    }
    return out;
}

std::string extension_of(std::string_view path) {
    const auto name = file_name_of(path);
    const auto dot = name.rfind('.');
    return dot == std::string::npos || dot == 0 ? std::string() : lower(std::string_view(name).substr(dot));
}

std::string file_name_of(std::string_view path) {
    const auto slash = path.find_last_of("\\/");
    return std::string(slash == std::string_view::npos ? path : path.substr(slash + 1));
}

std::string stem_of(std::string_view path) {
    auto name = file_name_of(path);
    const auto dot = name.rfind('.');
    if (dot != std::string::npos && dot > 0) name.resize(dot);
    return name;
}

std::string directory_of(std::string_view path) {
    const auto slash = path.find_last_of("\\/");
    return slash == std::string_view::npos ? std::string() : std::string(path.substr(0, slash));
}

std::string join_path(std::string_view directory, std::string_view name) {
    if (directory.empty()) return std::string(name);
    std::string result(directory);
    if (result.back() != '\\' && result.back() != '/') result += '\\';
    return result + std::string(name);
}

bool file_exists(const std::string& path) {
    const DWORD attributes = GetFileAttributesW(widen(path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

bool directory_exists(const std::string& path) {
    const DWORD attributes = GetFileAttributesW(widen(path).c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

void create_directories(const std::string& path) {
    if (path.empty() || directory_exists(path)) return;
    create_directories(directory_of(path));
    if (!CreateDirectoryW(widen(path).c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
        throw_win32("Could not create " + path + ".", GetLastError());
}

std::string environment(const char* name) {
    const auto wide_name = widen(name);
    const DWORD length = GetEnvironmentVariableW(wide_name.c_str(), nullptr, 0);
    if (length == 0) return {};
    std::wstring value(length, L'\0');
    const DWORD written = GetEnvironmentVariableW(wide_name.c_str(), value.data(), length);
    value.resize(written);
    return narrow(value);
}

std::string temp_directory() {
    wchar_t buffer[MAX_PATH + 2];
    const DWORD length = GetTempPathW(MAX_PATH + 2, buffer);
    return narrow(std::wstring_view(buffer, length));
}

std::string new_guid() {
    GUID guid{};
    CoCreateGuid(&guid);
    char text[40];
    snprintf(text, sizeof text, "%08lx%04x%04x%02x%02x%02x%02x%02x%02x%02x%02x", guid.Data1, guid.Data2, guid.Data3,
             guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3], guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7]);
    return text;
}

std::string format_count(long long value) {
    auto digits = std::to_string(value < 0 ? -value : value);
    for (int i = static_cast<int>(digits.size()) - 3; i > 0; i -= 3) digits.insert(static_cast<size_t>(i), ",");
    return value < 0 ? "-" + digits : digits;
}

}  // namespace mdv
