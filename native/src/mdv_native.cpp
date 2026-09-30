#include "mdv_native.h"

#include "md4c.h"
extern "C" {
#include "entity.h"
}

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <map>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

/* Binary document model (little-endian; str = u32 byte count + UTF-8 bytes):
 *
 *   "MDV1"
 *   u32 lines, u32 words, u32 characters (UTF-16 code units of the source)
 *   u32 heading count, then per heading:
 *       u8 level, u32 source line (1-based, 0 = unknown), u32 top-level block, str title, str slug
 *   u32 top-level block count
 *   events until 0x00:
 *       0x01 enter block: u8 type, then per type
 *            H: u8 level          CODE: str language   UL/OL: u8 tight   OL: u32 start
 *            LI: u8 task (0 none, 1 open, 2 done)      TH/TD: u8 align
 *       0x02 leave block
 *       0x03 enter span: u8 type, then  A: str href, str title   IMG: str src, str title
 *       0x04 leave span
 *       0x05 text: u8 type, str text   (entities and NUL are already decoded into NORMAL)
 *
 * Block and span types are md4c's enums. MDV_BLOCK_FRONT_MATTER is a leading
 * YAML block, delivered as one CODE-type text. */

namespace {

constexpr uint8_t MDV_BLOCK_FRONT_MATTER = 100;
constexpr unsigned kFlags = MD_DIALECT_GITHUB;

char* copy(const std::string& text, size_t* out_size) {
    auto* result = static_cast<char*>(std::malloc(text.size() + 1));
    if (result) std::memcpy(result, text.c_str(), text.size() + 1);
    if (out_size) *out_size = result ? text.size() : 0;
    return result;
}

struct Writer {
    std::vector<uint8_t> bytes;
    void u8(uint8_t value) { bytes.push_back(value); }
    void u32(uint32_t value) {
        for (int i = 0; i < 4; ++i) bytes.push_back(static_cast<uint8_t>(value >> (8 * i)));
    }
    void str(std::string_view text) {
        u32(static_cast<uint32_t>(text.size()));
        bytes.insert(bytes.end(), text.begin(), text.end());
    }
};

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

// Entity text arrives verbatim, e.g. "&amp;", "&#169;" or "&#x1F600;".
void decode_entity(std::string& out, std::string_view entity) {
    if (entity.size() >= 4 && entity[1] == '#') {
        const bool hex = entity[2] == 'x' || entity[2] == 'X';
        unsigned long long cp = 0;
        for (size_t i = hex ? 3 : 2; i + 1 < entity.size(); ++i) {
            const char c = entity[i];
            const int digit = c >= '0' && c <= '9' ? c - '0'
                : hex && c >= 'a' && c <= 'f' ? c - 'a' + 10
                : hex && c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) { out.append(entity); return; }
            cp = std::min<unsigned long long>(cp * (hex ? 16 : 10) + digit, 0x110000);
        }
        append_utf8(out, static_cast<unsigned>(cp));
        return;
    }
    if (const ENTITY* known = entity_lookup(entity.data(), entity.size())) {
        append_utf8(out, known->codepoints[0]);
        if (known->codepoints[1]) append_utf8(out, known->codepoints[1]);
        return;
    }
    out.append(entity);
}

std::string attribute(const MD_ATTRIBUTE& attr) {
    std::string out;
    if (!attr.text || attr.size == 0) return out;
    for (unsigned i = 0; attr.substr_offsets[i] < attr.size; ++i) {
        const std::string_view part(attr.text + attr.substr_offsets[i], attr.substr_offsets[i + 1] - attr.substr_offsets[i]);
        switch (attr.substr_types[i]) {
            case MD_TEXT_ENTITY: decode_entity(out, part); break;
            case MD_TEXT_NULLCHAR: append_utf8(out, 0xFFFD); break;
            default: out.append(part); break;
        }
    }
    return out;
}

bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v'; }

// Returns the offset where Markdown begins after a leading "---" YAML block, or 0 when there is none.
size_t front_matter_end(std::string_view doc) {
    size_t pos = 0;
    if (doc.starts_with("\xEF\xBB\xBF")) pos = 3;
    auto line_end = [&](size_t from) { const auto e = doc.find_first_of("\r\n", from); return e == std::string_view::npos ? doc.size() : e; };
    auto next_line = [&](size_t eol) { return eol >= doc.size() ? doc.size() : eol + (doc[eol] == '\r' && eol + 1 < doc.size() && doc[eol + 1] == '\n' ? 2 : 1); };
    size_t eol = line_end(pos);
    std::string_view first = doc.substr(pos, eol - pos);
    while (!first.empty() && (first.back() == ' ' || first.back() == '\t')) first.remove_suffix(1);
    if (first != "---") return 0;
    size_t start = next_line(eol);
    // A blank line right after the opener is a thematic break, not metadata.
    if (start >= doc.size() || doc[start] == '\r' || doc[start] == '\n') return 0;
    for (size_t line = start; line < doc.size();) {
        eol = line_end(line);
        std::string_view text = doc.substr(line, eol - line);
        while (!text.empty() && (text.back() == ' ' || text.back() == '\t')) text.remove_suffix(1);
        if (text == "---" || text == "...") return next_line(eol);
        line = next_line(eol);
    }
    return 0;
}

struct LineIndex {
    std::vector<size_t> starts{0};
    explicit LineIndex(std::string_view doc) {
        for (size_t i = 0; i < doc.size(); ++i) {
            if (doc[i] == '\r') { if (i + 1 < doc.size() && doc[i + 1] == '\n') ++i; starts.push_back(i + 1); }
            else if (doc[i] == '\n') starts.push_back(i + 1);
        }
    }
    uint32_t line_of(size_t offset) const {
        return static_cast<uint32_t>(std::upper_bound(starts.begin(), starts.end(), offset) - starts.begin());
    }
};

uint32_t utf16_length(std::string_view doc) {
    uint32_t count = 0;
    for (const unsigned char c : doc) {
        if ((c & 0xC0) != 0x80) count += c >= 0xF0 ? 2 : 1;
    }
    return count;
}

std::string collapse_whitespace(std::string_view text) {
    std::string out;
    bool space = false;
    for (const char c : text) {
        if (is_space(c)) { space = !out.empty(); continue; }
        if (space) { out += ' '; space = false; }
        out += c;
    }
    return out;
}

// Pandoc writes heading attributes such as "Title {#intro .unnumbered}"; they are not part of the title.
std::string strip_attribute_block(std::string title) {
    if (title.size() < 3 || title.back() != '}') return title;
    const auto open = title.rfind('{');
    if (open == std::string::npos || open + 1 >= title.size()) return title;
    const char lead = title[open + 1];
    if (lead != '#' && lead != '.' && lead != '-' && title.find('=', open) == std::string::npos) return title;
    title.erase(open);
    while (!title.empty() && is_space(title.back())) title.pop_back();
    return title;
}

std::string slugify(std::string_view title) {
    std::string slug;
    for (const unsigned char c : title) {
        if (c >= 'A' && c <= 'Z') slug += static_cast<char>(c - 'A' + 'a');
        else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c >= 0x80) slug += static_cast<char>(c);
        else if (c == ' ') slug += '-';
    }
    return slug;
}

struct Heading {
    uint8_t level;
    uint32_t line;
    uint32_t block;
    std::string title;
    std::string slug;
};

struct ParseState {
    std::string_view doc;
    const LineIndex* lines = nullptr;
    Writer events;
    std::vector<Heading> headings;
    std::map<std::string, int> slug_uses;
    uint32_t depth = 0, top_blocks = 0, current_top = 0, words = 0;
    bool in_word = false, word_has_alnum = false;
    bool in_heading = false;
    uint8_t heading_level = 0;
    size_t heading_first = std::string_view::npos;
    std::string heading_text;

    bool in_source(const char* p) const { return p >= doc.data() && p < doc.data() + doc.size(); }

    void end_word() {
        if (in_word && word_has_alnum) ++words;
        in_word = word_has_alnum = false;
    }
    void count_words(std::string_view text) {
        for (const unsigned char c : text) {
            if (is_space(static_cast<char>(c))) { end_word(); continue; }
            in_word = true;
            if (c >= 0x80 || (c >= '0' && c <= '9') || ((c | 0x20) >= 'a' && (c | 0x20) <= 'z')) word_has_alnum = true;
        }
    }
    void finish_heading() {
        in_heading = false;
        std::string title = strip_attribute_block(collapse_whitespace(heading_text));
        if (title.empty()) return;
        std::string slug = slugify(title);
        const int used = slug_uses[slug]++;
        if (used > 0) slug += "-" + std::to_string(used);
        const uint32_t line = heading_first == std::string_view::npos ? 0 : lines->line_of(heading_first);
        headings.push_back({heading_level, line, current_top, std::move(title), std::move(slug)});
    }
};

int on_enter_block(MD_BLOCKTYPE type, void* detail, void* user) {
    auto& s = *static_cast<ParseState*>(user);
    if (type == MD_BLOCK_DOC) return 0;
    s.end_word();
    if (s.depth++ == 0) s.current_top = s.top_blocks++;
    auto& w = s.events;
    w.u8(0x01);
    w.u8(static_cast<uint8_t>(type));
    switch (type) {
        case MD_BLOCK_H: {
            const auto level = static_cast<uint8_t>(static_cast<MD_BLOCK_H_DETAIL*>(detail)->level);
            w.u8(level);
            s.in_heading = true;
            s.heading_level = level;
            s.heading_first = std::string_view::npos;
            s.heading_text.clear();
            break;
        }
        case MD_BLOCK_CODE: {
            const auto* code = static_cast<MD_BLOCK_CODE_DETAIL*>(detail);
            w.str(attribute(code->lang));
            break;
        }
        case MD_BLOCK_UL: w.u8(static_cast<MD_BLOCK_UL_DETAIL*>(detail)->is_tight ? 1 : 0); break;
        case MD_BLOCK_OL: {
            const auto* ol = static_cast<MD_BLOCK_OL_DETAIL*>(detail);
            w.u8(ol->is_tight ? 1 : 0);
            w.u32(ol->start);
            break;
        }
        case MD_BLOCK_LI: {
            const auto* li = static_cast<MD_BLOCK_LI_DETAIL*>(detail);
            w.u8(!li->is_task ? 0 : li->task_mark == ' ' ? 1 : 2);
            break;
        }
        case MD_BLOCK_TH:
        case MD_BLOCK_TD: w.u8(static_cast<uint8_t>(static_cast<MD_BLOCK_TD_DETAIL*>(detail)->align)); break;
        default: break;
    }
    return 0;
}

int on_leave_block(MD_BLOCKTYPE type, void*, void* user) {
    auto& s = *static_cast<ParseState*>(user);
    if (type == MD_BLOCK_DOC) return 0;
    s.end_word();
    if (type == MD_BLOCK_H && s.in_heading) s.finish_heading();
    --s.depth;
    s.events.u8(0x02);
    return 0;
}

int on_enter_span(MD_SPANTYPE type, void* detail, void* user) {
    auto& w = static_cast<ParseState*>(user)->events;
    w.u8(0x03);
    w.u8(static_cast<uint8_t>(type));
    if (type == MD_SPAN_A) {
        const auto* a = static_cast<MD_SPAN_A_DETAIL*>(detail);
        w.str(attribute(a->href));
        w.str(attribute(a->title));
    } else if (type == MD_SPAN_IMG) {
        const auto* img = static_cast<MD_SPAN_IMG_DETAIL*>(detail);
        w.str(attribute(img->src));
        w.str(attribute(img->title));
    }
    return 0;
}

int on_leave_span(MD_SPANTYPE, void*, void* user) {
    static_cast<ParseState*>(user)->events.u8(0x04);
    return 0;
}

int on_text(MD_TEXTTYPE type, const MD_CHAR* text, MD_SIZE size, void* user) {
    auto& s = *static_cast<ParseState*>(user);
    const std::string_view raw(text, size);
    std::string decoded;
    MD_TEXTTYPE emitted = type;
    switch (type) {
        case MD_TEXT_ENTITY: decode_entity(decoded, raw); emitted = MD_TEXT_NORMAL; break;
        case MD_TEXT_NULLCHAR: append_utf8(decoded, 0xFFFD); emitted = MD_TEXT_NORMAL; break;
        case MD_TEXT_BR:
        case MD_TEXT_SOFTBR: break;
        default: decoded.assign(raw); break;
    }
    if (type == MD_TEXT_BR || type == MD_TEXT_SOFTBR) s.end_word();
    else if (type != MD_TEXT_HTML) s.count_words(decoded);
    if (s.in_heading) {
        if (s.heading_first == std::string_view::npos && size > 0 && s.in_source(text))
            s.heading_first = static_cast<size_t>(text - s.doc.data());
        if (type == MD_TEXT_BR || type == MD_TEXT_SOFTBR) s.heading_text += ' ';
        else if (type != MD_TEXT_HTML) s.heading_text += decoded;
    }
    s.events.u8(0x05);
    s.events.u8(static_cast<uint8_t>(emitted));
    s.events.str(decoded);
    return 0;
}

MD_PARSER make_parser(int (*enter_block)(MD_BLOCKTYPE, void*, void*), int (*leave_block)(MD_BLOCKTYPE, void*, void*),
                      int (*enter_span)(MD_SPANTYPE, void*, void*), int (*leave_span)(MD_SPANTYPE, void*, void*),
                      int (*text)(MD_TEXTTYPE, const MD_CHAR*, MD_SIZE, void*)) {
    MD_PARSER parser{};
    parser.flags = kFlags;
    parser.enter_block = enter_block;
    parser.leave_block = leave_block;
    parser.enter_span = enter_span;
    parser.leave_span = leave_span;
    parser.text = text;
    return parser;
}

std::vector<uint8_t> parse_document(std::string_view doc) {
    if (doc.size() > 0xFFFFFFF0u) throw std::runtime_error("Documents larger than 4 GB are not supported.");
    const LineIndex lines(doc);
    ParseState state;
    state.doc = doc;
    state.lines = &lines;

    const size_t body = front_matter_end(doc);
    if (body > 0) {
        state.current_top = state.top_blocks++;
        auto& w = state.events;
        w.u8(0x01); w.u8(MDV_BLOCK_FRONT_MATTER);
        w.u8(0x05); w.u8(MD_TEXT_CODE); w.str(doc.substr(0, body));
        w.u8(0x02);
    }

    const MD_PARSER parser = make_parser(on_enter_block, on_leave_block, on_enter_span, on_leave_span, on_text);
    if (md_parse(doc.data() + body, static_cast<MD_SIZE>(doc.size() - body), &parser, &state) != 0)
        throw std::runtime_error("The Markdown parser ran out of memory.");
    state.end_word();
    state.events.u8(0x00);

    Writer out;
    out.bytes = {'M', 'D', 'V', '1'};
    out.u32(static_cast<uint32_t>(lines.starts.size()));
    out.u32(state.words);
    out.u32(utf16_length(doc));
    out.u32(static_cast<uint32_t>(state.headings.size()));
    for (const auto& h : state.headings) {
        out.u8(h.level);
        out.u32(h.line);
        out.u32(h.block);
        out.str(h.title);
        out.str(h.slug);
    }
    out.u32(state.top_blocks);
    out.bytes.insert(out.bytes.end(), state.events.bytes.begin(), state.events.bytes.end());
    return std::move(out.bytes);
}

// ---- Heading reflow --------------------------------------------------------

struct HeadingSpan {
    int level = 0;
    size_t first = std::string_view::npos;
    size_t end = 0;
};

struct ReflowState {
    std::string_view doc;
    std::vector<HeadingSpan> headings;
    bool in_heading = false;
};

int reflow_enter(MD_BLOCKTYPE type, void* detail, void* user) {
    auto& s = *static_cast<ReflowState*>(user);
    if (type == MD_BLOCK_H) {
        s.headings.push_back({static_cast<int>(static_cast<MD_BLOCK_H_DETAIL*>(detail)->level)});
        s.in_heading = true;
    }
    return 0;
}
int reflow_leave(MD_BLOCKTYPE type, void*, void* user) {
    if (type == MD_BLOCK_H) static_cast<ReflowState*>(user)->in_heading = false;
    return 0;
}
int reflow_span(MD_SPANTYPE, void*, void*) { return 0; }
int reflow_text(MD_TEXTTYPE, const MD_CHAR* text, MD_SIZE size, void* user) {
    auto& s = *static_cast<ReflowState*>(user);
    if (!s.in_heading || size == 0 || text < s.doc.data() || text >= s.doc.data() + s.doc.size()) return 0;
    auto& h = s.headings.back();
    const auto offset = static_cast<size_t>(text - s.doc.data());
    h.first = std::min(h.first, offset);
    h.end = std::max(h.end, offset + size);
    return 0;
}

struct Edit {
    size_t start, end;
    std::string text;
};

bool contains_ci(std::string_view haystack, std::string_view needle) {
    return std::search(haystack.begin(), haystack.end(), needle.begin(), needle.end(), [](char a, char b) {
        return (a >= 'A' && a <= 'Z' ? a + 32 : a) == (b >= 'A' && b <= 'Z' ? b + 32 : b);
    }) != haystack.end();
}

bool has_html_heading(std::string_view doc) {
    for (size_t i = 0; i + 2 < doc.size(); ++i) {
        if (doc[i] == '<' && (doc[i + 1] | 0x20) == 'h' && doc[i + 2] >= '1' && doc[i + 2] <= '6' &&
            (i + 3 == doc.size() || doc[i + 3] == '>' || is_space(doc[i + 3]) || doc[i + 3] == '/'))
            return true;
    }
    return false;
}

bool has_anchor_link(std::string_view doc) {
    for (size_t i = 0; i + 1 < doc.size(); ++i) {
        if (doc[i] == ']' && doc[i + 1] == '(') {
            size_t j = i + 2;
            while (j < doc.size() && is_space(doc[j])) ++j;
            if (j < doc.size() && doc[j] == '#') return true;
        }
    }
    for (size_t pos = 0; pos + 4 <= doc.size(); ++pos) {
        if (!contains_ci(doc.substr(pos, 4), "href")) continue;
        size_t j = pos + 4;
        while (j < doc.size() && is_space(doc[j])) ++j;
        if (j >= doc.size() || doc[j] != '=') continue;
        ++j;
        while (j < doc.size() && is_space(doc[j])) ++j;
        if (j + 1 < doc.size() && (doc[j] == '"' || doc[j] == '\'') && doc[j + 1] == '#') return true;
    }
    return false;
}

std::string reflow(std::string_view doc) {
    const size_t body = front_matter_end(doc);
    ReflowState state;
    state.doc = doc;
    const MD_PARSER parser = make_parser(reflow_enter, reflow_leave, reflow_span, reflow_span, reflow_text);
    if (md_parse(doc.data() + body, static_cast<MD_SIZE>(doc.size() - body), &parser, &state) != 0)
        throw std::runtime_error("The Markdown parser ran out of memory.");

    struct Level { int original, normalized; };
    std::vector<Level> hierarchy;
    std::vector<Edit> edits;
    int changed = 0;
    auto is_newline = [&](size_t i) { return i < doc.size() && (doc[i] == '\n' || doc[i] == '\r'); };
    auto line_end = [&](size_t i) { while (i < doc.size() && !is_newline(i)) ++i; return i; };

    for (const auto& h : state.headings) {
        const int requested = std::clamp(h.level, 1, 6);
        while (!hierarchy.empty() && hierarchy.back().original >= requested) hierarchy.pop_back();
        const int normalized = hierarchy.empty() ? 1 : std::min(hierarchy.back().normalized + 1, 6);
        hierarchy.push_back({requested, normalized});
        if (normalized == h.level || h.first == std::string_view::npos) continue;

        size_t line_start = h.first;
        while (line_start > 0 && !is_newline(line_start - 1)) --line_start;
        const std::string_view prefix = doc.substr(line_start, h.first - line_start);
        const auto hash = prefix.find('#');
        const std::string marker(static_cast<size_t>(normalized), '#');
        if (hash != std::string_view::npos) {
            size_t hash_end = line_start + hash;
            while (hash_end < h.first && doc[hash_end] == '#') ++hash_end;
            edits.push_back({line_start + hash, hash_end, marker});
            ++changed;
            continue;
        }

        // Setext heading: the underline follows the heading's last text line.
        const size_t text_eol = line_end(h.end);
        if (text_eol >= doc.size()) continue;
        const size_t underline = text_eol + (doc[text_eol] == '\r' && text_eol + 1 < doc.size() && doc[text_eol + 1] == '\n' ? 2 : 1);
        const size_t underline_eol = line_end(underline);
        const std::string_view rule = doc.substr(underline, underline_eol - underline);
        if (rule.find_first_of("=-") == std::string_view::npos || rule.find_first_not_of(" \t>=-") != std::string_view::npos) continue;

        size_t content = h.first;
        while (content > line_start && !is_space(doc[content - 1]) && doc[content - 1] != '>') --content;
        edits.push_back({content, content, marker + " "});
        for (size_t i = h.first; i < h.end; ++i) {
            if (!is_newline(i)) continue;
            size_t j = i;
            while (is_newline(j)) ++j;
            while (j < h.end && (doc[j] == ' ' || doc[j] == '\t' || doc[j] == '>')) ++j;
            edits.push_back({i, j, " "});
            i = j - 1;
        }
        edits.push_back({text_eol, underline_eol, ""});
        ++changed;
    }

    std::string markdown(doc);
    std::sort(edits.begin(), edits.end(), [](const Edit& a, const Edit& b) { return a.start > b.start; });
    for (const auto& e : edits) markdown.replace(e.start, e.end - e.start, e.text);

    std::vector<std::string> warnings;
    if (state.headings.empty()) warnings.emplace_back("No Markdown headings were found.");
    else if (changed > 0) warnings.emplace_back("Review hand-written tables of contents, outline prose, and links that describe heading levels.");
    if (has_html_heading(doc)) warnings.emplace_back("Raw HTML heading tags were detected; reflow does not rewrite HTML headings.");
    if (has_anchor_link(doc)) warnings.emplace_back("Anchor links were detected; check any manually maintained navigation after reflow.");
    if (state.headings.size() > 1 && state.headings[0].level == 1 && state.headings[1].level == 2)
        warnings.emplace_back("The first H1 was left in place; remove it manually if it is only the document title.");

    std::string result = "OK\n" + std::to_string(changed) + '\t' + std::to_string(state.headings.size()) + '\t' + std::to_string(warnings.size()) + '\n';
    for (const auto& w : warnings) result += w + '\n';
    return result + markdown;
}

}  // namespace

MDV_API int32_t mdv_abi_version(void) { return MDV_ABI_VERSION; }

MDV_API uint8_t* mdv_parse(const char* utf8, size_t length, size_t* out_size) {
    if (out_size) *out_size = 0;
    std::vector<uint8_t> model;
    try {
        model = parse_document(utf8 ? std::string_view(utf8, length) : std::string_view());
    } catch (const std::exception& ex) {
        Writer error;
        error.bytes = {'M', 'D', 'V', 'E'};
        error.str(ex.what());
        model = std::move(error.bytes);
    } catch (...) {
        return nullptr;
    }
    auto* result = static_cast<uint8_t*>(std::malloc(model.size()));
    if (!result) return nullptr;
    std::memcpy(result, model.data(), model.size());
    if (out_size) *out_size = model.size();
    return result;
}

MDV_API char* mdv_reflow_headings(const char* utf8, size_t length, size_t* out_size) {
    try {
        return copy(reflow(utf8 ? std::string_view(utf8, length) : std::string_view()), out_size);
    } catch (const std::exception& ex) {
        return copy(std::string("ERROR\n") + ex.what(), out_size);
    } catch (...) {
        return copy("ERROR\nNative reflow failed.", out_size);
    }
}

MDV_API void mdv_free(void* buffer) { std::free(buffer); }
