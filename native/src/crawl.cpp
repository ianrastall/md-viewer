// The documentation crawler: single-threaded and polite (delays, robots.txt, retries with
// Retry-After), MediaWiki-aware, extracting each page's main content and converting it to
// Markdown through Pandoc.
#include "crawl.h"

#include "html.h"
#include "http.h"
#include "pandoc.h"

#include "nlohmann/json.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <deque>
#include <map>
#include <set>
#include <tuple>

namespace mdv {

const char* const CrawlerUserAgent = "md-viewer/2.0 (single-threaded; personal docs archival)";

namespace {

using html::Node;
using html::Selector;

// Length in characters (UTF-16 units, as the original C# counted), not bytes.
size_t text_length(std::string_view text) {
    size_t count = 0;
    for (const unsigned char c : text)
        if ((c & 0xC0) != 0x80) count += c >= 0xF0 ? 2 : 1;
    return count;
}

std::string seconds_text(std::chrono::milliseconds duration) {
    char buffer[32];
    snprintf(buffer, sizeof buffer, "%.3f", duration.count() / 1000.0);
    std::string text = buffer;
    while (text.back() == '0') text.pop_back();
    if (text.back() == '.') text.pop_back();
    return text;
}

std::string key_of(const Url& url) { return lower(url.str()); }

// Robots.txt paths: '*' matches anything and a trailing '$' anchors the end; otherwise a prefix match.
bool robots_match(std::string_view pattern, std::string_view text) {
    const bool anchored = pattern.ends_with('$');
    if (anchored) pattern.remove_suffix(1);
    size_t p = 0, t = 0, star = std::string_view::npos, resume = 0;
    while (true) {
        if (p == pattern.size()) {
            if (!anchored || t == text.size()) return true;
        } else if (pattern[p] == '*') {
            star = p++;
            resume = t;
            continue;
        } else if (t < text.size() && pattern[p] == text[t]) {
            ++p;
            ++t;
            continue;
        }
        if (star == std::string_view::npos || resume >= text.size()) return false;
        p = star + 1;
        t = ++resume;
    }
}

// ---- Content extraction ------------------------------------------------------------------

constexpr std::string_view BoilerplatePhrases[] = {
    "cookie", "privacy policy", "terms of service", "terms of use", "all rights reserved", "subscribe", "newsletter",
    "sign in", "log in", "accept all", "manage preferences",
};

std::vector<std::string_view> content_selectors(const std::string& generator) {
    if (generator == "doxygen") return {"div.contents", "div#doc-content", "div.textblock", "main", "article"};
    if (generator == "sphinx") return {"div[role='main']", "div.body", "article.bd-article", "main", "section"};
    if (generator == "mkdocs") return {"div.md-content", "article.md-content__inner", "main"};
    if (generator == "gitbook") return {"section.page-inner", "div.page-wrapper", "main"};
    if (generator == "mediawiki") return {".mw-parser-output", "main", "article", "body"};
    return {"main", "article", "div#content", "div.content", "div#main", "div.main", "body"};
}

double score_candidate(Node& element) {
    const auto text = collapse_whitespace(element.text_content());
    const auto length = text_length(text);
    if (length < 80) return -INFINITY;
    size_t link_text = 0;
    int paragraphs = 0, headings = 0, code = 0, tables = 0, items = 0;
    html::for_each_element(element, [&](Node& node) {
        const auto& tag = node.tag;
        if (tag == "a") link_text += text_length(collapse_whitespace(node.text_content()));
        else if (tag == "p") ++paragraphs;
        else if (tag == "h1" || tag == "h2" || tag == "h3") ++headings;
        else if (tag == "pre" || tag == "code") ++code;
        else if (tag == "table") ++tables;
        else if (tag == "li") ++items;
    });
    const double density = static_cast<double>(link_text) / static_cast<double>(length);
    double score = static_cast<double>(length) * (1 - std::min(density, 0.95));
    score += paragraphs * 35 + headings * 25 + code * 18 + tables * 40 - items * 2;
    if (element.tag == "article" || element.tag == "main") score *= 1.4;
    const auto id_class = lower(element.id() + " " + element.class_name());
    for (const auto word : {"article", "content", "main", "post", "entry", "body", "doc"})
        if (id_class.find(word) != std::string::npos) { score *= 1.2; break; }
    for (const auto word : {"nav", "menu", "footer", "header", "sidebar", "breadcrumb", "toc"})
        if (id_class.find(word) != std::string::npos) { score *= 0.5; break; }
    return score;
}

std::string guess_label(const Node& element) {
    if (const auto id = element.id(); !id.empty()) return "#" + id;
    if (const auto classes = element.classes(); !classes.empty()) return "." + classes.front();
    return element.tag;
}

std::string escape_url_for_markdown(std::string url) { return replace_all(replace_all(std::move(url), "(", "%28"), ")", "%29"); }

std::optional<std::string> absolutize(const std::string* value, const Url& base) {
    if (!value) return std::nullopt;
    const auto trimmed = trim(*value);
    if (trimmed.empty() || trimmed.starts_with('#') || istarts_with(trimmed, "mailto:") || istarts_with(trimmed, "tel:") ||
        istarts_with(trimmed, "data:") || istarts_with(trimmed, "javascript:"))
        return std::nullopt;
    const auto resolved = base.resolve(trimmed);
    if (!resolved) return std::nullopt;
    return escape_url_for_markdown(resolved->str());
}

std::string normalize_srcset(const std::string& srcset, const Url& base) {
    std::string out;
    size_t start = 0;
    while (start <= srcset.size()) {
        auto end = srcset.find(',', start);
        if (end == std::string::npos) end = srcset.size();
        const auto part = std::string(trim(std::string_view(srcset).substr(start, end - start)));
        start = end + 1;
        if (part.empty()) continue;
        const auto space = part.find_first_of(" \t\n");
        const auto url = part.substr(0, space);
        const auto descriptor = space == std::string::npos ? std::string() : collapse_whitespace(part.substr(space));
        std::string piece = part;
        if (auto absolute = absolutize(&url, base)) piece = descriptor.empty() ? *absolute : *absolute + " " + descriptor;
        if (!out.empty()) out += ", ";
        out += piece;
    }
    return out;
}

void resolve_urls(html::Document& document, Node* root, const Url& page_url) {
    if (!root) return;
    Url base = page_url;
    if (auto* base_element = html::select_first(*document.root, Selector("base[href]")))
        if (auto resolved = absolutize(base_element->attribute("href"), page_url))
            if (auto parsed = Url::parse(*resolved)) base = *parsed;
    for (auto* element : html::select_all(*root, Selector("a[href], img, source, video, audio, link[rel='canonical']"))) {
        for (const auto* name : {"href", "src", "poster", "data-src", "data-href", "data-original", "data-lazy-src"}) {
            const auto absolute = absolutize(element->attribute(name), base);
            if (!absolute) continue;
            element->set_attribute(name, *absolute);
            const std::string_view attribute = name;
            if ((attribute == "data-src" || attribute == "data-original" || attribute == "data-lazy-src") && !element->has_attribute("src"))
                element->set_attribute("src", *absolute);
            if (attribute == "data-href" && !element->has_attribute("href")) element->set_attribute("href", *absolute);
        }
        if (const auto* srcset = element->attribute("srcset")) element->set_attribute("srcset", normalize_srcset(*srcset, base));
        if (element->tag == "img" && !element->has_attribute("src"))
            if (const auto* srcset = element->attribute("srcset"); srcset && !srcset->empty()) {
                auto first = std::string(trim(srcset->substr(0, srcset->find(','))));
                first = first.substr(0, first.find_first_of(" \t"));
                if (!first.empty()) element->set_attribute("src", first);
            }
        if (element->tag == "a" && trim(element->text_content()).empty())
            if (const auto* href = element->attribute("href"); href && !href->empty() && !href->starts_with('#')) html::set_text_content(*element, *href);
    }
}

void remove_all(Node& root, std::string_view selector) {
    for (auto* element : html::select_all(root, Selector(selector))) html::remove(element);
}

void remove_navigation_chrome(Node& content) {
    remove_all(content,
               "nav, .navtab, .tablist, .tabs, #nav-path, #MSearchBox, .searchresults, .breadcrumb, .breadcrumbs, .toc, "
               ".table-of-contents, .sidebar, .sphinxsidebar, .md-sidebar, .ad, .ads, .advertisement, .sponsored, .promo, "
               ".social-share, .share, .share-buttons, .cookie-banner, .cookie, .consent, .gdpr, .popup, .modal, .newsletter, "
               ".subscribe, .comments, #comments, .related-posts, .related, footer, aside, script, style, iframe, noscript");
}

std::string mediawiki_heading_text(Node& heading) {
    auto* headline = html::select_first(heading, Selector(".mw-headline"));
    auto text = headline ? headline->text_content() : heading.text_content();
    // Drop "[edit]" links.
    for (size_t open = text.find('['); open != std::string::npos; open = text.find('[', open)) {
        const auto close = text.find(']', open);
        if (close == std::string::npos) break;
        if (iequals(trim(std::string_view(text).substr(open + 1, close - open - 1)), "edit")) text.erase(open, close - open + 1);
        else ++open;
    }
    return collapse_whitespace(html_decode(text));
}

void remove_mediawiki_chrome(Node& content) {
    remove_all(content,
               ".mw-editsection, .mw-jump-link, .reference, sup.reference, .mw-references-wrap, .references, .reflist, .noprint, "
               ".metadata, .ambox, .hatnote, .shortdescription, .printfooter, .mw-empty-elt, .navbox, .vertical-navbox, .infobox, "
               ".sidebar, .sistersitebox, .authority-control, .portal, .gallery, .thumb, figure, figcaption, img, audio, video");
    for (auto* span : html::select_all(content, Selector("span"))) html::unwrap(span);

    static const std::set<std::string> dropped = {"notes", "references", "bibliography", "external links", "further reading",
                                                  "sources", "citations", "footnotes", "works cited"};
    bool dropping = false;
    std::vector<Node*> children;
    for (const auto& child : content.children)
        if (child->is_element()) children.push_back(child.get());
    for (auto* child : children) {
        if (child->tag == "h2") dropping = dropped.contains(lower(mediawiki_heading_text(*child)));
        if (dropping) html::remove(child);
    }
    const Selector keep("table, pre, code");
    for (auto* element : html::select_all(content, Selector("p, li, section, div")))
        if (collapse_whitespace(element->text_content()).empty() && !html::select_first(*element, keep)) html::remove(element);
}

void remove_hidden_and_boilerplate(Node& content) {
    remove_all(content, "[hidden], [aria-hidden='true'], [style*='display: none'], [style*='display:none']");
    for (auto* element : html::select_all(content, Selector("div, section, aside, p, li"))) {
        const auto text = collapse_whitespace(element->text_content());
        const auto length = text_length(text);
        if (length == 0 || length > 220) continue;
        for (const auto phrase : BoilerplatePhrases)
            if (icontains(text, phrase)) { html::remove(element); break; }
    }
}

struct Extraction {
    std::string html;
    Node* content = nullptr;
    std::string label;
};

Extraction extract_main_html(html::Document& document, const Url& url, const std::string& generator) {
    resolve_urls(document, document.document_element() ? document.document_element() : document.body(), url);

    std::vector<std::pair<Node*, std::string>> candidates;
    std::set<Node*> seen;
    for (const auto selector : content_selectors(generator))
        if (auto* element = html::select_first(*document.root, Selector(selector)); element && seen.insert(element).second)
            candidates.emplace_back(element, std::string(selector));
    for (auto* element : html::select_all(*document.root, Selector("article, main, [role='main'], section, div")))
        if (seen.insert(element).second) candidates.emplace_back(element, guess_label(*element));

    Node* content = nullptr;
    std::string label;
    std::optional<std::tuple<Node*, std::string, double>> best;
    std::vector<double> scores;
    for (auto& [element, name] : candidates) {
        const double score = score_candidate(*element);
        scores.push_back(score);
        if (!best || score > std::get<2>(*best)) best = std::make_tuple(element, name, score);
    }
    if (best && std::get<2>(*best) > 320) {
        content = std::get<0>(*best);
        char score[32];
        snprintf(score, sizeof score, "%.0f", std::get<2>(*best));
        label = std::get<1>(*best) + " score " + score;
    } else {
        for (size_t i = 0; i < candidates.size() && !content; ++i)
            if (scores[i] > 80) { content = candidates[i].first; label = candidates[i].second + " fallback"; }
        if (!content) {
            content = document.body() ? document.body() : document.document_element();
            label = "body fallback";
        }
    }
    if (!content) content = document.root.get();

    if (generator == "mediawiki") remove_mediawiki_chrome(*content);
    remove_navigation_chrome(*content);
    remove_hidden_and_boilerplate(*content);
    return {content->inner_html(), content, label};
}

std::string meta_content(html::Document& document, const std::string& name) {
    auto* element = html::select_first(*document.root, Selector("meta[name='" + name + "'], meta[property='" + name + "']"));
    const auto* content = element ? element->attribute("content") : nullptr;
    return content ? std::string(trim(*content)) : std::string();
}

std::string page_title(html::Document& document, const Url& url) {
    for (auto title : {meta_content(document, "og:title"), meta_content(document, "twitter:title"), document.title()})
        if (!trim(title).empty()) return collapse_whitespace(title);
    if (auto* h1 = html::select_first(*document.root, Selector("h1")))
        if (auto text = collapse_whitespace(h1->text_content()); !text.empty()) return text;
    auto path = std::string_view(url.path);
    while (path.ends_with('/')) path.remove_suffix(1);
    const auto last = path.substr(path.rfind('/') + 1);
    return last.empty() ? url.host : std::string(last);
}

std::string detect_generator(html::Document& document) {
    if (auto* meta = html::select_first(*document.root, Selector("meta[name='generator']")))
        if (const auto* content = meta->attribute("content"))
            for (const auto key : {"doxygen", "sphinx", "mkdocs", "gitbook", "jsdoc", "rustdoc"})
                if (icontains(*content, key)) return key;
    if (html::select_first(*document.root, Selector("#doxygen-nav, .doxygen, .contents"))) return "doxygen";
    if (html::select_first(*document.root, Selector(".sphinxsidebar, div.highlight, div[role='main']"))) return "sphinx";
    if (html::select_first(*document.root, Selector(".md-sidebar, .md-content"))) return "mkdocs";
    return "_default";
}

// ---- Link discovery ------------------------------------------------------------------------

bool should_skip(const Url& url, const std::string& generator) {
    const auto text = lower(url.path_and_query());
    for (size_t at = text.find("search"); at != std::string::npos; at = text.find("search", at + 1)) {
        const bool starts = at == 0 || text[at - 1] == '/';
        const auto after = std::string_view(text).substr(at + 6);
        if (starts && (after.starts_with('/') || after.starts_with(".html") || after.starts_with('?'))) return true;
    }
    for (const auto folder : {"/assets/", "/_static/", "/_sources/", "/_images/"})
        if (text.find(folder) != std::string::npos) return true;
    if (text.ends_with("/404.html")) return true;
    if (generator == "doxygen") {
        const auto path = lower(url.path);
        for (const auto exact : {"annotated.html", "classes.html", "files.html", "hierarchy.html", "inherits.html"})
            if (path.ends_with(exact)) return true;
        for (const auto prefix : {"functions", "variables", "typedefs", "enums", "enumvalues", "globals", "members"}) {
            const auto at = path.find(prefix);
            if (at != std::string::npos && path.ends_with(".html") && at + std::string_view(prefix).size() <= path.size() - 5) return true;
        }
        if (text.find("navtree") != std::string::npos) return true;
    }
    static constexpr std::string_view skipped[] = {".css", ".js", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".pdf", ".zip"};
    const auto extension = extension_of(url.path);
    return std::find(std::begin(skipped), std::end(skipped), extension) != std::end(skipped);
}

std::vector<Url> discover_links(html::Document& document, Node* preferred_root, const Url& current, const Url& base, const std::string& generator, bool same_base_path_only) {
    std::vector<Url> found;
    std::set<std::string> seen;
    const Selector anchors("a[href]");
    auto add = [&](Node& root) {
        for (auto* anchor : html::select_all(root, anchors)) {
            const auto* raw = anchor->attribute("href");
            if (!raw) continue;
            const auto href = trim(std::string_view(*raw).substr(0, raw->find('#')));
            if (href.empty() || istarts_with(href, "mailto:") || istarts_with(href, "tel:") || istarts_with(href, "javascript:") || istarts_with(href, "data:")) continue;
            auto absolute = current.resolve(href);
            if (!absolute) continue;
            const auto normalized = absolute->without_fragment();
            if (!same_origin(normalized, base)) continue;
            if (same_base_path_only && !is_under_base_path(normalized, base)) continue;
            if (!has_html_like_extension(normalized) || should_skip(normalized, generator)) continue;
            if (seen.insert(key_of(normalized)).second) found.push_back(normalized);
        }
    };
    if (preferred_root) add(*preferred_root);
    add(*document.root);
    return found;
}

// ---- Markdown cleanup helpers -----------------------------------------------------------

bool word_char(char c) { return is_ascii_alnum(c) || c == '_'; }

// Finds "<tag" at a word boundary, case-insensitively.
size_t find_tag(std::string_view text, std::string_view tag, size_t from) {
    const std::string open = "<" + std::string(tag);
    for (size_t at = ifind(text, open, from); at != std::string_view::npos; at = ifind(text, open, at + 1))
        if (at + open.size() >= text.size() || !word_char(text[at + open.size()])) return at;
    return std::string_view::npos;
}

// Removes every <tag ...> start tag.
bool remove_start_tags(std::string& line, std::string_view tag) {
    bool removed = false;
    for (size_t at = find_tag(line, tag, 0); at != std::string::npos; at = find_tag(line, tag, at)) {
        const auto close = line.find('>', at);
        if (close == std::string::npos) break;
        line.erase(at, close - at + 1);
        removed = true;
    }
    return removed;
}

// Removes every <tag ...>...</tag> element that starts and ends on the line.
bool remove_elements(std::string& line, std::string_view tag) {
    bool removed = false;
    const std::string closing = "</" + std::string(tag) + ">";
    for (size_t at = find_tag(line, tag, 0); at != std::string::npos; at = find_tag(line, tag, at)) {
        const auto open_end = line.find('>', at);
        const auto end = open_end == std::string::npos ? std::string::npos : ifind(line, closing, open_end);
        if (end == std::string::npos) { at += 1; continue; }
        line.erase(at, end + closing.size() - at);
        removed = true;
    }
    return removed;
}

bool find_closing_bracket(std::string_view text, size_t start, size_t& end) {
    int depth = 0;
    for (size_t i = start; i < text.size(); ++i) {
        if (text[i] == '\\') { ++i; continue; }
        if (text[i] == '[') { ++depth; continue; }
        if (text[i] != ']') continue;
        if (--depth == 0) { end = i; return true; }
    }
    return false;
}

bool is_link_boundary(char c) { return c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?' || c == ')' || c == ']' || c == '*' || c == '_' || c == '"' || c == '\''; }

bool find_destination_end(std::string_view text, size_t start, size_t& end) {
    bool single = false, dbl = false;
    for (size_t i = start; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '\\') { ++i; continue; }
        if (c == '\'' && !dbl && (single || i == start || is_space(text[i - 1]))) { single = !single; continue; }
        if (c == '"' && !single && (dbl || i == start || is_space(text[i - 1]))) { dbl = !dbl; continue; }
        if (c != ')' || single || dbl) continue;
        if (i + 1 == text.size() || is_space(text[i + 1]) || is_link_boundary(text[i + 1])) { end = i; return true; }
    }
    return false;
}

bool read_link(std::string_view text, size_t start, std::string& label, std::string& destination, size_t& end) {
    size_t label_end = 0;
    if (!find_closing_bracket(text, start, label_end) || label_end + 1 >= text.size() || text[label_end + 1] != '(') return false;
    size_t destination_end = 0;
    if (!find_destination_end(text, label_end + 2, destination_end)) return false;
    label = std::string(text.substr(start + 1, label_end - start - 1));
    destination = std::string(text.substr(label_end + 2, destination_end - label_end - 2));
    end = destination_end;
    return true;
}

std::string escape_link_text(std::string text) { return replace_all(replace_all(std::move(text), "[", "\\["), "]", "\\]"); }
std::string escape_link_destination(std::string href) { return replace_all(replace_all(std::move(href), " ", "%20"), ")", "%29"); }

// Applies a transform to each line outside fenced code.
template <typename Transform>
std::string map_prose_lines(std::string_view markdown, Transform transform) {
    auto lines = split_lines(markdown);
    std::vector<std::string> kept;
    kept.reserve(lines.size());
    bool fence = false;
    for (auto& line : lines) {
        if (is_fence(line)) { fence = !fence; kept.push_back(std::move(line)); continue; }
        if (fence) { kept.push_back(std::move(line)); continue; }
        if (auto result = transform(line)) kept.push_back(std::move(*result));
    }
    return join_lines(kept);
}

std::string remove_image_artifacts(std::string_view markdown) {
    return map_prose_lines(markdown, [](const std::string& line) -> std::optional<std::string> {
        const auto trimmed = trim(line);
        if (find_tag(trimmed, "img", 0) == 0 && trimmed.find('>') == trimmed.size() - 1) return std::nullopt;
        if (find_tag(trimmed, "figcaption", 0) == 0 && iends_with(trimmed, "</figcaption>")) return std::nullopt;
        std::string current = line;
        bool removed = remove_start_tags(current, "img");
        removed |= remove_elements(current, "figcaption");
        // Markdown images: ![alt](destination)
        std::string without;
        for (size_t index = 0; index < current.size();) {
            const auto image = current.find("![", index);
            if (image == std::string::npos) { without.append(current, index); break; }
            without.append(current, index, image - index);
            size_t label_end = 0, end = 0;
            if (find_closing_bracket(current, image + 1, label_end) && label_end + 1 < current.size() && current[label_end + 1] == '(' &&
                find_destination_end(current, label_end + 2, end)) {
                index = end + 1;
                removed = true;
                continue;
            }
            without += current[image];
            index = image + 1;
        }
        // Only lines that lost an image are re-spaced, so list indentation elsewhere survives.
        if (removed) {
            std::string collapsed;
            for (size_t i = 0; i < without.size(); ++i) {
                if (is_space(without[i]) && i + 1 < without.size() && is_space(without[i + 1])) continue;
                collapsed += is_space(without[i]) ? ' ' : without[i];
            }
            current = std::string(trim_start(collapsed));
        }
        if (trim(current).empty() && !trim(line).empty()) return std::nullopt;
        return current;
    });
}

std::string remove_html_container_artifacts(std::string_view markdown) {
    return map_prose_lines(markdown, [](const std::string& line) -> std::optional<std::string> {
        const auto trimmed = trim(line);
        const bool div = istarts_with(trimmed, "<div") || istarts_with(trimmed, "</div");
        if (div && trimmed.ends_with('>') && trimmed.find('>') == trimmed.size() - 1) {
            const auto after = trimmed.substr(trimmed[1] == '/' ? 5 : 4);
            if (after == ">" || is_space(after.front())) return std::nullopt;
        }
        std::string current = line;
        const auto end = trim_end(current);
        if (iends_with(end, "</div>")) current = std::string(trim_end(end.substr(0, end.size() - 6)));
        if (current.empty() && !line.empty()) return std::nullopt;
        return current;
    });
}

std::string remove_inline_html_artifacts(std::string_view markdown) {
    return map_prose_lines(markdown, [](const std::string& line) -> std::optional<std::string> {
        std::string current = line;
        // Empty spans vanish entirely; other span tags are dropped and their text kept.
        for (size_t at = find_tag(current, "span", 0); at != std::string::npos;) {
            const auto close = current.find('>', at);
            if (close == std::string::npos) break;
            size_t next = close + 1;
            while (next < current.size() && is_space(current[next])) ++next;
            if (istarts_with(std::string_view(current).substr(next), "</span>")) current.erase(at, next + 7 - at);
            else ++at;
            at = find_tag(current, "span", at);
        }
        remove_start_tags(current, "span");
        for (size_t at = ifind(current, "</span"); at != std::string::npos; at = ifind(current, "</span", at)) {
            const auto close = current.find('>', at);
            if (close == std::string::npos) break;
            current.erase(at, close - at + 1);
        }
        return current;
    });
}

std::string convert_simple_html_anchors(std::string_view markdown) {
    std::string out;
    size_t index = 0;
    while (index < markdown.size()) {
        const auto at = find_tag(markdown, "a", index);
        if (at == std::string_view::npos) { out.append(markdown.substr(index)); break; }
        const auto tag_end = markdown.find('>', at);
        const auto close = tag_end == std::string_view::npos ? std::string_view::npos : ifind(markdown, "</a>", tag_end);
        std::optional<std::string> href;
        if (close != std::string_view::npos) {
            const auto tag = markdown.substr(at, tag_end - at);
            for (size_t h = ifind(tag, "href"); h != std::string_view::npos && !href; h = ifind(tag, "href", h + 4)) {
                if (!is_space(tag[h - 1])) continue;
                size_t k = h + 4;
                while (k < tag.size() && is_space(tag[k])) ++k;
                if (k >= tag.size() || tag[k] != '=') continue;
                ++k;
                while (k < tag.size() && is_space(tag[k])) ++k;
                if (k >= tag.size() || (tag[k] != '"' && tag[k] != '\'')) continue;
                const auto end = tag.find_first_of("\"'", k + 1);
                if (end == std::string_view::npos || end == k + 1) continue;
                href = std::string(tag.substr(k + 1, end - k - 1));
            }
        }
        if (!href) {
            out.append(markdown.substr(index, at - index + 1));
            index = at + 1;
            continue;
        }
        out.append(markdown.substr(index, at - index));
        std::string text;
        const auto inner = markdown.substr(tag_end + 1, close - tag_end - 1);
        for (size_t i = 0; i < inner.size(); ++i) {
            if (inner[i] == '<') {
                const auto end = inner.find('>', i);
                if (end == std::string_view::npos) { text += inner.substr(i); break; }
                text += ' ';
                i = end;
            } else text += inner[i];
        }
        text = html_decode(collapse_whitespace(text));
        const auto destination = html_decode(trim(*href));
        if (text.empty() || destination.empty()) out += text;
        else out += "[" + escape_link_text(text) + "](" + escape_link_destination(destination) + ")";
        index = close + 4;
    }
    return out;
}

std::string resolve_wiki_destination(const std::string& destination, const Url& page) {
    const auto trimmed = std::string(trim(destination));
    if (trimmed.empty() || trimmed.starts_with('#') || istarts_with(trimmed, "http://") || istarts_with(trimmed, "https://") ||
        istarts_with(trimmed, "mailto:") || istarts_with(trimmed, "tel:") || istarts_with(trimmed, "data:") || istarts_with(trimmed, "javascript:"))
        return trimmed;
    const auto target = replace_all(replace_all(percent_decode(replace_all(trimmed, " ", "_")), "(", "%28"), ")", "%29");
    if (auto absolute = page.resolve(target)) return escape_link_destination(absolute->str());
    return escape_link_destination(trimmed);
}

std::string resolve_wiki_links(std::string_view markdown, const Url& page) {
    return map_prose_lines(markdown, [&](const std::string& line) -> std::optional<std::string> {
        std::string out;
        for (size_t index = 0; index < line.size();) {
            const auto start = line.find('[', index);
            if (start == std::string::npos) { out.append(line, index); break; }
            if (start > 0 && line[start - 1] == '!') {
                out.append(line, index, start - index + 1);
                index = start + 1;
                continue;
            }
            out.append(line, index, start - index);
            std::string label, destination;
            size_t end = 0;
            if (!read_link(line, start, label, destination, end)) {
                out += line[start];
                index = start + 1;
                continue;
            }
            out += "[" + label + "](" + resolve_wiki_destination(destination, page) + ")";
            index = end + 1;
        }
        return out;
    });
}

bool parse_markdown_heading(std::string_view line, int& level, std::string& text) {
    size_t hashes = 0;
    while (hashes < line.size() && line[hashes] == '#') ++hashes;
    if (hashes == 0 || hashes > 6 || hashes >= line.size() || !is_space(line[hashes])) return false;
    auto body = trim(line.substr(hashes));
    // Optional closing sequence: whitespace then #s.
    auto end = body;
    while (!end.empty() && end.back() == '#') end.remove_suffix(1);
    if (end.size() < body.size() && !end.empty() && is_space(end.back())) body = trim_end(end);
    if (body.empty()) return false;
    level = static_cast<int>(hashes);
    text = collapse_whitespace(html_decode(body));
    return true;
}

std::string remove_dropped_wikipedia_sections(std::string_view markdown) {
    static const std::set<std::string> dropped = {"notes", "references", "bibliography", "external links", "further reading",
                                                  "sources", "citations", "footnotes", "works cited"};
    auto lines = split_lines(markdown);
    std::vector<std::string> kept;
    int dropped_level = 0;
    bool fence = false;
    for (auto& line : lines) {
        if (is_fence(line)) { fence = !fence; kept.push_back(std::move(line)); continue; }
        int level = 0;
        std::string text;
        if (!fence && parse_markdown_heading(trim(line), level, text)) {
            if (dropped_level > 0 && level <= dropped_level) dropped_level = 0;
            if (dropped.contains(lower(text))) { dropped_level = level; continue; }
        }
        if (dropped_level > 0 && !fence) continue;
        kept.push_back(std::move(line));
    }
    return join_lines(kept);
}

bool is_category_link_line(std::string_view line) {
    auto rest = trim(line);
    int links = 0;
    while (!rest.empty()) {
        if (rest.front() != '[') return false;
        const auto close = rest.find(']');
        if (close == std::string_view::npos || close == 1 || close + 1 >= rest.size() || rest[close + 1] != '(') return false;
        const auto end = rest.find(')', close + 2);
        if (end == std::string_view::npos) return false;
        const auto destination = rest.substr(close + 2, end - close - 2);
        if (!destination.starts_with("Category:") && !destination.starts_with("/wiki/Category:")) return false;
        ++links;
        rest = trim_start(rest.substr(end + 1));
    }
    return links > 0;
}

std::string remove_reference_artifacts(std::string_view markdown) {
    return map_prose_lines(markdown, [](const std::string& line) -> std::optional<std::string> {
        const auto trimmed = trim(line);
        if (trimmed.starts_with("[^") && trimmed.ends_with("]:") && trimmed.size() > 4 &&
            trimmed.substr(2, trimmed.size() - 4).find_first_not_of("0123456789") == std::string_view::npos)
            return std::string();
        if (trimmed == "-" || is_category_link_line(line)) return std::string();
        std::string out;
        // Empty parentheses and footnote markers.
        for (size_t i = 0; i < line.size(); ++i) {
            if (line[i] == '(') {
                size_t k = i + 1;
                while (k < line.size() && is_space(line[k])) ++k;
                if (k < line.size() && line[k] == ')') { i = k; continue; }
            }
            if (line[i] == '[' && i + 1 < line.size() && line[i + 1] == '^') {
                size_t k = i + 2;
                while (k < line.size() && line[k] >= '0' && line[k] <= '9') ++k;
                if (k > i + 2 && k < line.size() && line[k] == ']') { i = k; continue; }
            }
            out += line[i];
        }
        // Space before punctuation, and runs of spaces after the indentation.
        std::string tidy;
        const size_t indent = out.find_first_not_of(" \t") == std::string::npos ? out.size() : out.find_first_not_of(" \t");
        tidy.append(out, 0, indent);
        for (size_t i = indent; i < out.size(); ++i) {
            if (out[i] == ' ' || out[i] == '\t') {
                size_t k = i;
                while (k < out.size() && (out[k] == ' ' || out[k] == '\t')) ++k;
                if (k < out.size() && std::string_view(",.;:!?").find(out[k]) != std::string_view::npos) { i = k - 1; continue; }
                tidy += ' ';
                i = k - 1;
                continue;
            }
            tidy += out[i];
        }
        return tidy;
    });
}

std::string trim_link_labels(std::string_view markdown) {
    std::string out;
    for (size_t index = 0; index < markdown.size();) {
        const auto open = markdown.find('[', index);
        if (open == std::string_view::npos) { out.append(markdown.substr(index)); break; }
        out.append(markdown.substr(index, open - index));
        const auto close = markdown.find_first_of("]\r\n", open + 1);
        if (close != std::string_view::npos && markdown[close] == ']' && close + 1 < markdown.size() && markdown[close + 1] == '(') {
            const auto end = markdown.find_first_of("()\r\n", close + 2);
            if (end != std::string_view::npos && markdown[end] == ')') {
                const auto label = trim(markdown.substr(open + 1, close - open - 1));
                if (!label.empty()) {
                    out += "[" + std::string(label) + "](" + std::string(markdown.substr(close + 2, end - close - 2)) + ")";
                    index = end + 1;
                    continue;
                }
            }
        }
        out += '[';
        index = open + 1;
    }
    return out;
}

std::string dedupe_boilerplate(std::string_view markdown) {
    auto lines = split_lines(markdown);
    std::vector<std::pair<std::string, bool>> deduped;  // line, inside a fence
    std::string previous;
    bool has_previous = false, fence = false;
    for (auto& line : lines) {
        if (trim(line).starts_with("```")) {
            fence = !fence;
            deduped.emplace_back(line, true);
            previous = line;
            has_previous = true;
            continue;
        }
        if (!fence && has_previous && line == previous && !trim(line).empty()) continue;
        deduped.emplace_back(line, fence);
        previous = line;
        has_previous = true;
    }
    std::map<std::string, int> frequencies;
    auto eligible = [](std::string_view key) { return key.size() >= 8 && key.size() <= 140 && !key.starts_with('#') && !key.starts_with("```") && !key.starts_with('|'); };
    for (const auto& [line, in_fence] : deduped)
        if (const auto key = trim(line); !in_fence && eligible(key)) ++frequencies[lower(key)];
    std::vector<std::string> kept;
    for (auto& [line, in_fence] : deduped) {
        const auto key = trim(line);
        if (!in_fence && eligible(key) && frequencies[lower(key)] >= 3) continue;
        kept.push_back(std::move(line));
    }
    return join_lines(kept);
}

std::string html_to_plain_text(std::string_view text) {
    std::string out;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '<') {
            const auto end = text.find('>', i);
            if (end != std::string_view::npos) { out += ' '; i = end; continue; }
        }
        out += text[i];
    }
    return collapse_whitespace(html_decode(out));
}

// ---- The crawler ----------------------------------------------------------------------------

struct Page {
    Url url;
    std::string title;
    std::string markdown;
};

struct Fetch {
    bool ok = false;
    std::string text;
    std::string message;
};

class Crawler {
public:
    Crawler(const CrawlOptions& options, const Progress& progress)
        : options_(options), progress_(progress), http_(CrawlerUserAgent, options.request_timeout) {}

    std::vector<Page> run(const Url& start) {
        const Url normalized = start.without_fragment();
        const Url base = crawl_base_url(normalized);
        progress_.report("Base " + base.str());

        const RobotsTxt robots = options_.respect_robots_txt ? fetch_robots(normalized) : RobotsTxt::allow_all();
        const auto delay = robots.crawl_delay && *robots.crawl_delay > options_.default_delay ? *robots.crawl_delay : options_.default_delay;
        if (robots.crawl_delay) progress_.report("Robots crawl-delay " + seconds_text(*robots.crawl_delay) + " seconds");
        progress_.report("Actual delay " + seconds_text(delay) + " seconds");

        if (auto wiki = MediaWikiPage::from(normalized)) {
            if (auto pages = crawl_mediawiki(*wiki, robots, delay)) return *pages;
            progress_.report("WIKI raw wikitext unavailable; falling back to HTML crawl.");
        }

        std::deque<Url> queue{normalized};
        std::set<std::string> queued{key_of(normalized)}, visited;
        std::vector<Page> pages;
        int skipped = 0;
        std::string generator = "_default";
        while (!queue.empty() && static_cast<int>(visited.size()) < options_.max_pages) {
            progress_.check();
            const Url url = queue.front();
            queue.pop_front();
            if (!visited.insert(key_of(url)).second) continue;
            if (!robots.allowed(url)) {
                ++skipped;
                progress_.report("ROBOTS " + url.str());
                continue;
            }
            progress_.report("GET " + url.str());
            const Fetch fetch = fetch_text(url, delay, true, {"text/html", "application/xhtml+xml"});
            if (!fetch.ok) {
                ++skipped;
                progress_.report("SKIP " + fetch.message);
                continue;
            }
            if (looks_like_dynamic_shell(fetch.text)) progress_.report("DYNAMIC likely JavaScript-rendered shell: " + url.str());
            auto document = html::parse(fetch.text);
            if (visited.size() == 1) {
                generator = detect_generator(document);
                progress_.report("GEN " + generator);
            }
            const auto extraction = extract_main_html(document, url, generator);
            progress_.report("CONTENT " + extraction.label);
            for (auto& link : discover_links(document, extraction.content, url, base, generator, options_.same_base_path_only))
                if (!visited.contains(key_of(link)) && queued.insert(key_of(link)).second) queue.push_back(std::move(link));
            const auto title = page_title(document, url);
            auto markdown = clean_crawled_markdown(pandoc_convert("html", extraction.html, progress_));
            if (!trim(markdown).empty()) {
                pages.push_back({url, title, std::move(markdown)});
                progress_.report("OK " + title);
            } else {
                ++skipped;
                progress_.report("EMPTY No Markdown content extracted.");
            }
        }
        progress_.report("Pages " + std::to_string(pages.size()) + "; visited " + std::to_string(visited.size()) + "; skipped " + std::to_string(skipped));
        return pages;
    }

private:
    const CrawlOptions& options_;
    const Progress& progress_;
    HttpClient http_;
    std::optional<std::chrono::steady_clock::time_point> last_request_;

    void respect_delay(std::chrono::milliseconds delay) {
        if (last_request_) {
            const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - *last_request_);
            if (elapsed < delay) progress_.sleep(delay - elapsed);
        }
        last_request_ = std::chrono::steady_clock::now();
    }

    RobotsTxt fetch_robots(const Url& start) {
        const std::string url = start.scheme + "://" + start.authority() + "/robots.txt";
        try {
            respect_delay(options_.default_delay);
            const auto response = http_.get(url, {"text/plain", "*/*"}, options_.max_page_bytes, progress_);
            if (response.status == 404) {
                progress_.report("Robots none found; proceeding cautiously.");
                return RobotsTxt::allow_all();
            }
            if (response.status >= 500) {
                progress_.report("Robots server returned " + std::to_string(response.status) + "; treating as disallow-all.");
                return RobotsTxt::disallow_all();
            }
            if (!response.ok()) {
                progress_.report("Robots HTTP " + std::to_string(response.status) + "; proceeding cautiously.");
                return RobotsTxt::allow_all();
            }
            return RobotsTxt::parse(decode_body(response), CrawlerUserAgent);
        } catch (const HttpError& ex) {
            progress_.report(std::string("Robots could not read robots.txt (") + ex.what() + "); proceeding cautiously.");
            return RobotsTxt::allow_all();
        }
    }

    static bool should_retry(unsigned status) { return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504; }

    Fetch fetch_text(const Url& url, std::chrono::milliseconds delay, bool require_html, const std::vector<std::string>& accept) {
        for (int attempt = 1; attempt <= options_.max_retries; ++attempt) {
            respect_delay(delay);
            try {
                const auto response = http_.get(url.str(), accept, options_.max_page_bytes, progress_);
                if (should_retry(response.status) && attempt < options_.max_retries) {
                    std::chrono::milliseconds wait = std::chrono::seconds(std::min(60, 1 << (attempt + 1)));
                    if (response.retry_after && response.retry_after->count() > 0)
                        wait = std::min<std::chrono::milliseconds>(*response.retry_after, std::chrono::minutes(5));
                    progress_.report("WAIT HTTP " + std::to_string(response.status) + "; retrying after " + seconds_text(wait) + " seconds");
                    progress_.sleep(wait);
                    continue;
                }
                if (!response.ok()) return {false, {}, "HTTP " + std::to_string(response.status) + " " + response.reason};
                if (require_html) {
                    const auto& type = response.content_type;
                    const bool html_like = type.empty() ? has_html_like_extension(url) : type.find("html") != std::string::npos || type == "application/xhtml+xml";
                    if (!html_like) return {false, {}, "non-HTML content-type: " + (type.empty() ? std::string("(none)") : type)};
                }
                return {true, decode_body(response), "OK"};
            } catch (const HttpError& ex) {
                if (attempt < options_.max_retries) {
                    const std::chrono::milliseconds wait = std::chrono::seconds(1 << attempt);
                    progress_.report(std::string("WAIT ") + ex.what() + "; retrying after " + seconds_text(wait) + " seconds");
                    progress_.sleep(wait);
                    continue;
                }
                return {false, {}, ex.what()};
            }
        }
        return {false, {}, "retry limit reached"};
    }

    std::optional<std::vector<Page>> crawl_mediawiki(const MediaWikiPage& page, const RobotsTxt& robots, std::chrono::milliseconds delay) {
        if (robots.allowed(page.api_url)) {
            progress_.report("WIKI parsed article " + page.title);
            progress_.report("GET " + page.api_url.str());
            const auto fetch = fetch_text(page.api_url, delay, false, {"application/json", "text/json", "*/*;q=0.5"});
            std::string message;
            if (fetch.ok) {
                std::string title, html_text;
                if (read_parsed_article(fetch.text, title, html_text, message)) {
                    auto document = html::parse(html_text);
                    const auto extraction = extract_main_html(document, page.page_url, "mediawiki");
                    progress_.report("CONTENT " + extraction.label);
                    auto markdown = clean_crawled_markdown(pandoc_convert("html", extraction.html, progress_), &page.page_url);
                    if (!trim(markdown).empty()) {
                        progress_.report("OK " + title);
                        return std::vector<Page>{{page.page_url, title, std::move(markdown)}};
                    }
                    progress_.report("WIKI parsed HTML conversion produced no Markdown.");
                } else progress_.report("WIKI parsed HTML failed: " + fetch.message + message);
            } else progress_.report("WIKI parsed HTML failed: " + fetch.message);
        } else progress_.report("WIKI parsed article blocked by robots: " + page.api_url.str());

        if (!robots.allowed(page.raw_url)) {
            progress_.report("WIKI raw blocked by robots: " + page.raw_url.str());
            return std::nullopt;
        }
        progress_.report("WIKI raw fallback " + page.title);
        progress_.report("GET " + page.raw_url.str());
        const auto fetch = fetch_text(page.raw_url, delay, false, {"text/plain", "text/x-wiki", "*/*;q=0.5"});
        if (!fetch.ok) {
            progress_.report("WIKI raw failed: " + fetch.message);
            return std::nullopt;
        }
        auto markdown = clean_crawled_markdown(pandoc_convert("mediawiki", fetch.text, progress_), &page.page_url);
        if (trim(markdown).empty()) {
            progress_.report("WIKI raw conversion produced no Markdown.");
            return std::nullopt;
        }
        progress_.report("OK " + page.title);
        return std::vector<Page>{{page.page_url, page.title, std::move(markdown)}};
    }

    static bool read_parsed_article(const std::string& json_text, std::string& title, std::string& html_text, std::string& message) {
        const auto json = nlohmann::json::parse(json_text, nullptr, false);
        if (json.is_discarded()) { message = " invalid JSON."; return false; }
        if (json.contains("error")) {
            const auto& error = json["error"];
            message = error.contains("info") && error["info"].is_string() ? " " + error["info"].get<std::string>() : "";
            return false;
        }
        if (!json.contains("parse") || !json["parse"].contains("text") || !json["parse"]["text"].is_string()) {
            message = " response did not contain parse.text.";
            return false;
        }
        const auto& parse = json["parse"];
        html_text = parse["text"].get<std::string>();
        if (trim(html_text).empty()) { message = " parse.text was empty."; return false; }
        title = "Wikipedia article";
        for (const auto* key : {"displaytitle", "title"})
            if (parse.contains(key) && parse[key].is_string() && !parse[key].get<std::string>().empty()) {
                title = html_to_plain_text(parse[key].get<std::string>());
                break;
            }
        return true;
    }
};

std::string render(const std::vector<Page>& pages) {
    std::string out;
    for (size_t i = 0; i < pages.size(); ++i) {
        if (i > 0) out += "\n---\n\n";
        out += "# " + collapse_whitespace(pages[i].title) + "\n\n" + pages[i].markdown + "\n\n";
    }
    return out;
}

}  // namespace

// ---- Public pieces --------------------------------------------------------------------------

RobotsTxt RobotsTxt::allow_all() { return RobotsTxt(); }

RobotsTxt RobotsTxt::disallow_all() {
    RobotsTxt robots;
    robots.default_allow_ = false;
    robots.rules_.push_back({false, "/"});
    return robots;
}

RobotsTxt RobotsTxt::parse(std::string_view text, std::string_view user_agent) {
    const auto product = user_agent.substr(0, user_agent.find_first_of("/ ("));
    struct Group { std::vector<std::string> agents; std::vector<Rule> rules; std::optional<std::chrono::milliseconds> delay; };
    std::vector<Group> groups;
    Group* current = nullptr;
    bool saw_rules = false;
    for (const auto& raw : split_lines(normalize_newlines(text))) {
        auto line = std::string_view(raw).substr(0, raw.find('#'));
        line = trim(line);
        if (line.empty()) { current = nullptr; saw_rules = false; continue; }
        const auto colon = line.find(':');
        if (colon == std::string_view::npos) continue;
        const auto field = lower(trim(line.substr(0, colon)));
        const auto value = std::string(trim(line.substr(colon + 1)));
        if (field == "user-agent") {
            if (!current || saw_rules) {
                groups.emplace_back();
                current = &groups.back();
                saw_rules = false;
            }
            current->agents.push_back(value);
            continue;
        }
        if (!current) continue;
        if (field == "allow" || field == "disallow") {
            saw_rules = true;
            if (!value.empty()) current->rules.push_back({field == "allow", value});
        } else if (field == "crawl-delay") {
            saw_rules = true;
            double seconds = -1;
            const auto result = std::from_chars(value.data(), value.data() + value.size(), seconds);
            if (result.ec == std::errc() && seconds >= 0) current->delay = std::chrono::milliseconds(static_cast<long long>(seconds * 1000));
        }
    }
    const Group* best = nullptr;
    long best_length = -1;
    for (const auto& group : groups)
        for (const auto& agent : group.agents) {
            const auto name = trim(agent);
            if (name != "*" && !istarts_with(product, name)) continue;
            const long length = name == "*" ? 0 : static_cast<long>(name.size());
            if (length > best_length) { best = &group; best_length = length; }
        }
    RobotsTxt robots;
    if (!best) return robots;
    robots.rules_ = best->rules;
    robots.crawl_delay = best->delay;
    return robots;
}

bool RobotsTxt::allowed(const Url& url) const {
    if (rules_.empty()) return default_allow_;
    const auto path = url.path_and_query();
    const Rule* best = nullptr;
    for (const auto& rule : rules_) {
        if (!robots_match(rule.pattern, path)) continue;
        if (!best || rule.pattern.size() > best->pattern.size() || (rule.pattern.size() == best->pattern.size() && rule.allow && !best->allow)) best = &rule;
    }
    return best ? best->allow : default_allow_;
}

std::optional<MediaWikiPage> MediaWikiPage::from(const Url& url) {
    static constexpr std::string_view hosts[] = {".wikipedia.org", ".wiktionary.org", ".wikibooks.org", ".wikiquote.org",
                                                 ".wikisource.org", ".wikiversity.org", ".wikivoyage.org", ".mediawiki.org"};
    static constexpr std::string_view non_articles[] = {"special", "file", "image", "talk", "user", "user talk", "wikipedia", "wikipedia talk",
                                                        "template", "template talk", "category", "category talk", "help", "help talk", "portal",
                                                        "portal talk", "draft", "draft talk", "module", "module talk", "mediawiki", "timedtext"};
    const bool known = std::any_of(std::begin(hosts), std::end(hosts), [&](std::string_view suffix) { return iends_with(url.host, suffix); });
    const bool wiki_like = icontains(url.host, "wiki") && (icontains(url.path, "/wiki/") || iends_with(url.path, "/index.php"));
    if (!known && !wiki_like) return std::nullopt;

    auto normalize_title = [](std::string_view raw) { return collapse_whitespace(replace_all(percent_decode(replace_all(std::string(raw), "+", " ")), "_", " ")); };
    std::string title;
    bool found = false;
    if (url.query) {
        auto query = std::string_view(*url.query);
        for (size_t start = 0; start <= query.size();) {
            auto end = query.find('&', start);
            if (end == std::string_view::npos) end = query.size();
            const auto part = query.substr(start, end - start);
            start = end + 1;
            const auto equals = part.find('=');
            if (!iequals(percent_decode(part.substr(0, equals)), "title")) continue;
            title = normalize_title(equals == std::string_view::npos ? std::string_view() : part.substr(equals + 1));
            found = true;
            break;
        }
    }
    if (!found) {
        const auto at = ifind(url.path, "/wiki/");
        if (at == std::string::npos) return std::nullopt;
        auto raw = std::string_view(url.path).substr(at + 6);
        while (raw.starts_with('/')) raw.remove_prefix(1);
        while (raw.ends_with('/')) raw.remove_suffix(1);
        title = normalize_title(raw);
    }
    if (title.empty()) return std::nullopt;
    if (const auto colon = title.find(':'); colon != std::string::npos && colon > 0) {
        const auto prefix = lower(trim(std::string_view(title).substr(0, colon)));
        if (std::find(std::begin(non_articles), std::end(non_articles), prefix) != std::end(non_articles)) return std::nullopt;
    }

    MediaWikiPage page;
    page.page_url = url;
    page.title = title;
    page.raw_url = url.without_fragment();
    std::string query;
    if (url.query) {
        auto parts = std::string_view(*url.query);
        for (size_t start = 0; start <= parts.size();) {
            auto end = parts.find('&', start);
            if (end == std::string_view::npos) end = parts.size();
            const auto part = parts.substr(start, end - start);
            start = end + 1;
            if (part.empty() || istarts_with(part, "action=")) continue;
            query += (query.empty() ? "" : "&") + std::string(part);
        }
    }
    page.raw_url.query = query + (query.empty() ? "" : "&") + "action=raw";
    page.api_url = url.without_fragment();
    page.api_url.path = "/w/api.php";
    page.api_url.query = "action=parse&page=" + percent_encode_component(replace_all(title, " ", "_")) +
                         "&prop=text%7Cdisplaytitle&format=json&formatversion=2&redirects=1&disabletoc=1";
    return page;
}

Url crawl_base_url(const Url& start) {
    Url base = start.without_fragment();
    if (!base.path.ends_with('/')) {
        const auto slash = base.path.rfind('/');
        base.path = slash == std::string::npos ? "/" : base.path.substr(0, slash + 1);
    }
    base.query.reset();
    return base;
}

bool is_under_base_path(const Url& candidate, const Url& base) {
    auto base_path = base.path;
    if (!base_path.ends_with('/')) base_path += '/';
    return istarts_with(candidate.path, base_path);
}

bool has_html_like_extension(const Url& url) {
    if (url.path.ends_with('/')) return true;
    const auto name = url.path.substr(url.path.rfind('/') + 1);
    if (name.find('.') == std::string::npos) return true;
    const auto extension = extension_of(name);
    return extension == ".html" || extension == ".htm" || extension == ".php" || extension == ".asp" || extension == ".aspx";
}

bool looks_like_dynamic_shell(std::string_view html) {
    const auto text = lower(html);
    // Visible text: drop scripts, styles, and tags.
    std::string visible;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '<') {
            for (const auto block : {"script", "style"}) {
                if (std::string_view(text).substr(i + 1).starts_with(block)) {
                    const auto close = text.find("</" + std::string(block), i);
                    if (close != std::string::npos) i = close;
                    break;
                }
            }
            const auto end = text.find('>', i);
            if (end == std::string::npos) break;
            visible += ' ';
            i = end;
            continue;
        }
        visible += text[i];
    }
    if (text_length(collapse_whitespace(visible)) >= 1200) return false;
    for (const auto id : {"__next", "__nuxt", "root", "app"})
        for (const char quote : {'"', '\''})
            if (text.find(std::string("id=") + quote + id + quote) != std::string::npos) return true;
    for (const auto signal : {"data-reactroot", "webpack", "chunk-", "chunk_", "chunk."})
        if (text.find(signal) != std::string::npos) return true;
    for (size_t at = text.find("<script"); at != std::string::npos; at = text.find("<script", at + 1)) {
        const auto tag = std::string_view(text).substr(at, text.find('>', at) - at);
        if (tag.find("type=\"module\"") != std::string_view::npos || tag.find("type='module'") != std::string_view::npos) return true;
    }
    return false;
}

std::string clean_crawled_markdown(std::string_view input, const Url* wiki_page) {
    std::string markdown = normalize_newlines(input);
    static constexpr std::pair<std::string_view, std::string_view> artifacts[] = {
        {"\xC3\x82\xC2\xA9", "\xC2\xA9"}, {"\xC3\x82\xC2\xAE", "\xC2\xAE"}, {"\xC3\x82\xC2\xB0", "\xC2\xB0"}, {"\xC3\x82\xC2\xB7", "\xC2\xB7"},
        {"\xC3\x82", ""},
        {"\xC3\xA2\xE2\x82\xAC\xE2\x84\xA2", "\xE2\x80\x99"}, {"\xC3\xA2\xE2\x82\xAC\xCB\x9C", "\xE2\x80\x98"},
        {"\xC3\xA2\xE2\x82\xAC\xC5\x93", "\xE2\x80\x9C"}, {"\xC3\xA2\xE2\x82\xAC\xEF\xBF\xBD", "\xE2\x80\x9D"},
        {"\xC3\xA2\xE2\x82\xAC\xE2\x80\x9C", "\xE2\x80\x93"}, {"\xC3\xA2\xE2\x82\xAC\xE2\x80\x9D", "\xE2\x80\x94"},
        {"\xC3\xA2\xE2\x82\xAC\xC2\xA6", "\xE2\x80\xA6"}, {"\xC3\xA2\xE2\x82\xAC\xC2\xA2", "\xE2\x80\xA2"},
        {"\xC3\xA2\xE2\x80\x9E\xC2\xA2", "\xE2\x84\xA2"}, {"\xC3\x83\xE2\x80\x94", "\xC3\x97"}, {"\xC3\x83\xC2\xB7", "\xC3\xB7"},
    };
    for (const auto& [bad, good] : artifacts) markdown = replace_all(std::move(markdown), bad, good);

    // Pandoc's raw-HTML blocks: ```{=html} ... ```
    for (size_t start = markdown.find("```{=html}"); start != std::string::npos; start = markdown.find("```{=html}", start)) {
        const auto end = markdown.find("```", start + 10);
        if (end == std::string::npos) break;
        markdown.erase(start, end + 3 - start);
    }
    markdown = remove_image_artifacts(markdown);
    markdown = remove_html_container_artifacts(markdown);
    markdown = remove_inline_html_artifacts(markdown);
    markdown = convert_simple_html_anchors(markdown);
    if (wiki_page) markdown = resolve_wiki_links(markdown, *wiki_page);
    markdown = remove_dropped_wikipedia_sections(markdown);
    markdown = remove_reference_artifacts(markdown);
    markdown = trim_link_labels(markdown);
    markdown = replace_all(std::move(markdown), ")[", ")\n[");
    markdown = replace_all(std::move(markdown), "](//", "](https://");
    markdown = collapse_blank_lines(markdown);
    markdown = dedupe_boilerplate(markdown);
    markdown = collapse_blank_lines(markdown);
    return std::string(trim(markdown));
}

std::string crawl(const std::string& start_url, const CrawlOptions& options, const Progress& progress) {
    const auto start = Url::parse(start_url);
    if (!start || !start->is_http()) throw Error(start_url + " is not an http or https address.");
    const auto pandoc = pandoc_path();
    progress.report("Pandoc " + pandoc);
    progress.report("Start " + start->str());
    Crawler crawler(options, progress);
    const auto pages = crawler.run(*start);
    if (pages.empty()) throw Error("No content pages were found.");
    progress.report("Rendering " + std::to_string(pages.size()) + " pages");
    return std::string(trim(render(pages)));
}

}  // namespace mdv
