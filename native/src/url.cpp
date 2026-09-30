#include "url.h"

#include "common.h"

#include <cctype>

namespace mdv {
namespace {

bool is_scheme_char(char c) { return is_ascii_alnum(c) || c == '+' || c == '-' || c == '.'; }
bool is_hex(char c) { return (c >= '0' && c <= '9') || (ascii_lower(c) >= 'a' && ascii_lower(c) <= 'f'); }
int hex_value(char c) { return c <= '9' ? c - '0' : ascii_lower(c) - 'a' + 10; }

bool escape_at(std::string_view text, size_t i) { return text[i] == '%' && i + 2 < text.size() && is_hex(text[i + 1]) && is_hex(text[i + 2]); }

// Percent-encodes bytes a URL may not carry literally, keeping existing escapes.
std::string encode_unsafe(std::string_view text, bool query) {
    static constexpr char hex[] = "0123456789ABCDEF";
    std::string out;
    for (size_t i = 0; i < text.size(); ++i) {
        const auto c = static_cast<unsigned char>(text[i]);
        const bool unsafe = c <= 0x20 || c >= 0x7F || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`' ||
                            c == '{' || c == '|' || c == '}' || (c == '%' && !escape_at(text, i)) || (!query && c == '?');
        if (unsafe) { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
        else out += static_cast<char>(c);
    }
    return out;
}

std::string remove_dot_segments(std::string_view path) {
    std::string input(path), output;
    while (!input.empty()) {
        if (input.starts_with("../")) input.erase(0, 3);
        else if (input.starts_with("./")) input.erase(0, 2);
        else if (input.starts_with("/./")) input.erase(0, 2);
        else if (input == "/.") input = "/";
        else if (input.starts_with("/../") || input == "/..") {
            input = input == "/.." ? "/" : input.substr(3);
            const auto slash = output.rfind('/');
            output.erase(slash == std::string::npos ? 0 : slash);
        } else if (input == "." || input == "..") input.clear();
        else {
            const auto next = input.find('/', input[0] == '/' ? 1 : 0);
            output += input.substr(0, next);
            input.erase(0, next == std::string::npos ? input.size() : next);
        }
    }
    return output;
}

void normalize(Url& url) {
    url.scheme = lower(url.scheme);
    url.host = lower(url.host);
    if (url.port == 80 && url.scheme == "http") url.port = -1;
    if (url.port == 443 && url.scheme == "https") url.port = -1;
    if (url.has_authority) {
        url.path = remove_dot_segments(url.path);
        if (url.path.empty() && url.is_http()) url.path = "/";
    }
    url.path = encode_unsafe(url.path, false);
    if (url.query) url.query = encode_unsafe(*url.query, true);
    if (url.fragment) url.fragment = encode_unsafe(*url.fragment, true);
}

// Browsers drop tabs and newlines inside URLs and trim surrounding spaces.
std::string clean_reference(std::string_view text, bool http) {
    std::string out;
    for (const char c : trim(text))
        if (c != '\t' && c != '\n' && c != '\r') out += (http && c == '\\') ? '/' : c;
    return out;
}

}  // namespace

std::optional<Url> Url::parse(std::string_view raw) {
    auto text = clean_reference(raw, false);
    const auto colon = text.find(':');
    if (colon == std::string::npos || colon == 0 || !std::isalpha(static_cast<unsigned char>(text[0]))) return std::nullopt;
    for (size_t i = 0; i < colon; ++i)
        if (!is_scheme_char(text[i])) return std::nullopt;
    Url url;
    url.scheme = lower(std::string_view(text).substr(0, colon));
    if (url.is_http()) text = clean_reference(raw, true);
    std::string_view rest = std::string_view(text).substr(colon + 1);
    if (const auto hash = rest.find('#'); hash != std::string_view::npos) { url.fragment = std::string(rest.substr(hash + 1)); rest = rest.substr(0, hash); }
    if (const auto question = rest.find('?'); question != std::string_view::npos) { url.query = std::string(rest.substr(question + 1)); rest = rest.substr(0, question); }
    if (rest.starts_with("//")) {
        url.has_authority = true;
        rest.remove_prefix(2);
        const auto slash = rest.find('/');
        auto authority = rest.substr(0, slash);
        rest = slash == std::string_view::npos ? std::string_view() : rest.substr(slash);
        if (const auto at = authority.rfind('@'); at != std::string_view::npos) { url.userinfo = std::string(authority.substr(0, at)); authority.remove_prefix(at + 1); }
        const auto bracket = authority.rfind(']');
        const auto port_colon = authority.rfind(':');
        if (port_colon != std::string_view::npos && (bracket == std::string_view::npos || port_colon > bracket)) {
            const auto digits = authority.substr(port_colon + 1);
            if (!digits.empty()) {
                int port = 0;
                for (const char c : digits) {
                    if (c < '0' || c > '9' || port > 65535) return std::nullopt;
                    port = port * 10 + (c - '0');
                }
                if (port > 65535) return std::nullopt;
                url.port = port;
            }
            authority = authority.substr(0, port_colon);
        }
        url.host = std::string(authority);
        if (url.is_http() && url.host.empty()) return std::nullopt;
    } else if (url.is_http()) {
        return std::nullopt;
    }
    url.path = std::string(rest);
    normalize(url);
    return url;
}

std::optional<Url> Url::resolve(std::string_view raw) const {
    auto reference = clean_reference(raw, is_http());
    if (auto absolute = parse(reference)) {
        if (absolute->scheme != scheme || absolute->has_authority || !is_http()) return absolute;
        // "http:page" with this URL's scheme and no authority is relative (RFC 3986 5.2.2, non-strict).
        reference.erase(0, reference.find(':') + 1);
    }
    std::string_view rest = reference;
    std::optional<std::string> reference_fragment, reference_query;
    if (const auto hash = rest.find('#'); hash != std::string_view::npos) { reference_fragment = std::string(rest.substr(hash + 1)); rest = rest.substr(0, hash); }
    if (const auto question = rest.find('?'); question != std::string_view::npos) { reference_query = std::string(rest.substr(question + 1)); rest = rest.substr(0, question); }
    if (rest.starts_with("//")) return parse(scheme + ":" + reference);

    Url target = *this;
    target.fragment = reference_fragment;
    if (rest.empty()) {
        if (reference_query) target.query = reference_query;
    } else {
        target.query = reference_query;
        if (rest.starts_with('/')) target.path = std::string(rest);
        else if (has_authority && path.empty()) target.path = "/" + std::string(rest);
        else {
            const auto slash = path.rfind('/');
            target.path = (slash == std::string::npos ? std::string() : path.substr(0, slash + 1)) + std::string(rest);
        }
    }
    normalize(target);
    return target;
}

std::string Url::authority() const {
    std::string text = userinfo.empty() ? host : userinfo + "@" + host;
    if (port >= 0) text += ":" + std::to_string(port);
    return text;
}

std::string Url::path_and_query() const { return path + (query ? "?" + *query : std::string()); }

std::string Url::str() const {
    std::string text = scheme + ":";
    if (has_authority) text += "//" + authority();
    text += path_and_query();
    if (fragment) text += "#" + *fragment;
    return text;
}

int Url::effective_port() const {
    if (port >= 0) return port;
    return scheme == "https" ? 443 : scheme == "http" ? 80 : -1;
}

Url Url::without_fragment() const {
    Url copy = *this;
    copy.fragment.reset();
    return copy;
}

bool same_origin(const Url& a, const Url& b) {
    return a.scheme == b.scheme && iequals(a.host, b.host) && a.effective_port() == b.effective_port();
}

std::string percent_decode(std::string_view text) {
    std::string out;
    for (size_t i = 0; i < text.size(); ++i) {
        if (escape_at(text, i)) {
            out += static_cast<char>(hex_value(text[i + 1]) * 16 + hex_value(text[i + 2]));
            i += 2;
        } else out += text[i];
    }
    return out;
}

std::string percent_encode_component(std::string_view text) {
    static constexpr char hex[] = "0123456789ABCDEF";
    std::string out;
    for (const char ch : text) {
        const auto c = static_cast<unsigned char>(ch);
        if (is_ascii_alnum(ch) || c == '-' || c == '_' || c == '.' || c == '~') out += ch;
        else { out += '%'; out += hex[c >> 4]; out += hex[c & 15]; }
    }
    return out;
}

}  // namespace mdv
