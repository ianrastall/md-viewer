#pragma once

#include <optional>
#include <string>
#include <string_view>

namespace mdv {

// An absolute URL, normalized the way the crawler compares pages: lower-case scheme and host,
// no default port, dot segments removed, unsafe characters percent-encoded.
struct Url {
    std::string scheme;
    std::string host;
    int port = -1;  // -1: the scheme's default
    bool has_authority = false;
    std::string userinfo;
    std::string path;
    std::optional<std::string> query;
    std::optional<std::string> fragment;

    static std::optional<Url> parse(std::string_view text);
    // RFC 3986 reference resolution against this URL.
    std::optional<Url> resolve(std::string_view reference) const;

    std::string str() const;             // full URL, including the fragment
    std::string authority() const;
    std::string path_and_query() const;
    int effective_port() const;
    bool is_http() const { return scheme == "http" || scheme == "https"; }
    Url without_fragment() const;
};

bool same_origin(const Url& a, const Url& b);
std::string percent_decode(std::string_view text);  // invalid escapes are kept
std::string percent_encode_component(std::string_view text);  // like Uri.EscapeDataString

}  // namespace mdv
