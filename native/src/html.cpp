#include "html.h"

#include "common.h"

#include "gumbo.h"

#include <algorithm>

namespace mdv::html {
namespace {

// Real pages nest a few dozen levels; hostile ones can nest thousands. Deeper elements are dropped so
// the recursive walks below stay bounded.
constexpr int MaxDepth = 400;

std::unique_ptr<Node> convert(const GumboNode* source, int depth = 0) {
    auto node = std::make_unique<Node>();
    switch (source->type) {
        case GUMBO_NODE_DOCUMENT: node->kind = Node::Kind::Document; break;
        case GUMBO_NODE_TEXT:
        case GUMBO_NODE_WHITESPACE:
        case GUMBO_NODE_CDATA:
            node->kind = Node::Kind::Text;
            node->text = source->v.text.text;
            return node;
        case GUMBO_NODE_COMMENT:
            node->kind = Node::Kind::Comment;
            node->text = source->v.text.text;
            return node;
        case GUMBO_NODE_ELEMENT:
        case GUMBO_NODE_TEMPLATE: {
            const GumboElement& element = source->v.element;
            if (element.tag != GUMBO_TAG_UNKNOWN) node->tag = gumbo_normalized_tagname(element.tag);
            else {
                GumboStringPiece name = element.original_tag;
                gumbo_tag_from_original_text(&name);
                node->tag = lower(std::string_view(name.data, name.length));
            }
            for (unsigned i = 0; i < element.attributes.length; ++i) {
                const auto* attribute = static_cast<const GumboAttribute*>(element.attributes.data[i]);
                node->attributes.emplace_back(lower(attribute->name), attribute->value);
            }
            break;
        }
        default: return node;
    }
    const GumboVector& children = source->type == GUMBO_NODE_DOCUMENT ? source->v.document.children : source->v.element.children;
    for (unsigned i = 0; i < children.length; ++i) {
        const auto* gumbo_child = static_cast<const GumboNode*>(children.data[i]);
        if (depth >= MaxDepth && (gumbo_child->type == GUMBO_NODE_ELEMENT || gumbo_child->type == GUMBO_NODE_TEMPLATE)) continue;
        auto child = convert(gumbo_child, depth + 1);
        child->parent = node.get();
        node->children.push_back(std::move(child));
    }
    return node;
}

bool is_void(std::string_view tag) {
    static constexpr std::string_view voids[] = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"};
    return std::find(std::begin(voids), std::end(voids), tag) != std::end(voids);
}

bool is_raw_text(std::string_view tag) { return tag == "script" || tag == "style" || tag == "xmp" || tag == "iframe" || tag == "noembed" || tag == "noframes" || tag == "plaintext" || tag == "noscript"; }

void escape(std::string& out, std::string_view text, bool attribute) {
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '&') out += "&amp;";
        else if (static_cast<unsigned char>(c) == 0xC2 && i + 1 < text.size() && static_cast<unsigned char>(text[i + 1]) == 0xA0) { out += "&nbsp;"; ++i; }
        else if (attribute && c == '"') out += "&quot;";
        else if (!attribute && c == '<') out += "&lt;";
        else if (!attribute && c == '>') out += "&gt;";
        else out += c;
    }
}

void serialize(const Node& node, std::string& out) {
    switch (node.kind) {
        case Node::Kind::Text:
            if (node.parent && node.parent->is_element() && is_raw_text(node.parent->tag)) out += node.text;
            else escape(out, node.text, false);
            return;
        case Node::Kind::Comment:
            out += "<!--" + node.text + "-->";
            return;
        case Node::Kind::Document:
            for (const auto& child : node.children) serialize(*child, out);
            return;
        case Node::Kind::Element:
            out += '<' + node.tag;
            for (const auto& [name, value] : node.attributes) {
                out += ' ' + name + "=\"";
                escape(out, value, true);
                out += '"';
            }
            out += '>';
            if (is_void(node.tag)) return;
            for (const auto& child : node.children) serialize(*child, out);
            out += "</" + node.tag + '>';
            return;
    }
}

void collect_text(const Node& node, std::string& out) {
    if (node.kind == Node::Kind::Text) out += node.text;
    for (const auto& child : node.children) collect_text(*child, out);
}

void walk(Node& node, const std::function<void(Node&)>& visit) {
    for (const auto& child : node.children) {
        if (!child->is_element()) continue;
        visit(*child);
        walk(*child, visit);
    }
}

Node* find_element(Node& node, std::string_view tag) {
    for (const auto& child : node.children) {
        if (!child->is_element()) continue;
        if (child->tag == tag) return child.get();
        if (auto* found = find_element(*child, tag)) return found;
    }
    return nullptr;
}

}  // namespace

const std::string* Node::attribute(std::string_view name) const {
    for (const auto& [key, value] : attributes)
        if (key == name) return &value;
    return nullptr;
}

void Node::set_attribute(std::string_view name, std::string value) {
    for (auto& [key, existing] : attributes)
        if (key == name) { existing = std::move(value); return; }
    attributes.emplace_back(std::string(name), std::move(value));
}

std::string Node::id() const { const auto* value = attribute("id"); return value ? *value : std::string(); }
std::string Node::class_name() const { const auto* value = attribute("class"); return value ? *value : std::string(); }

std::vector<std::string> Node::classes() const {
    std::vector<std::string> result;
    const auto names = class_name();
    for (size_t start = 0; start < names.size();) {
        while (start < names.size() && is_space(names[start])) ++start;
        size_t end = start;
        while (end < names.size() && !is_space(names[end])) ++end;
        if (end > start) result.push_back(names.substr(start, end - start));
        start = end;
    }
    return result;
}

std::string Node::text_content() const {
    std::string out;
    collect_text(*this, out);
    return out;
}

std::string Node::inner_html() const {
    std::string out;
    for (const auto& child : children) serialize(*child, out);
    return out;
}

Node* Document::document_element() const { return root ? find_element(*root, "html") : nullptr; }
Node* Document::body() const { return root ? find_element(*root, "body") : nullptr; }

std::string Document::title() const {
    if (!root) return {};
    auto* head = find_element(*root, "head");
    auto* title = head ? find_element(*head, "title") : find_element(*root, "title");
    return title ? collapse_whitespace(title->text_content()) : std::string();
}

Document parse(std::string_view html) {
    GumboOutput* output = gumbo_parse_with_options(&kGumboDefaultOptions, html.data(), html.size());
    Document document;
    if (output) {
        document.root = convert(output->document);
        gumbo_destroy_output(&kGumboDefaultOptions, output);
    } else {
        document.root = std::make_unique<Node>();
        document.root->kind = Node::Kind::Document;
    }
    return document;
}

Selector::Selector(std::string_view text) {
    for (size_t start = 0; start <= text.size();) {
        auto end = text.find(',', start);
        if (end == std::string_view::npos) end = text.size();
        const auto part = trim(text.substr(start, end - start));
        start = end + 1;
        if (part.empty()) continue;
        Compound compound;
        size_t i = 0;
        auto ident = [&] {
            const size_t begin = i;
            while (i < part.size() && (is_ascii_alnum(part[i]) || part[i] == '-' || part[i] == '_' || static_cast<unsigned char>(part[i]) >= 0x80)) ++i;
            return std::string(part.substr(begin, i - begin));
        };
        if (i < part.size() && part[i] != '.' && part[i] != '#' && part[i] != '[') compound.tag = lower(ident());
        while (i < part.size()) {
            const char c = part[i++];
            if (c == '#') compound.id = ident();
            else if (c == '.') compound.classes.push_back(ident());
            else if (c == '[') {
                Attribute attribute;
                attribute.name = lower(ident());
                if (i < part.size() && part[i] != ']') {
                    if (part[i] != '=') attribute.op = part[i++];
                    else attribute.op = '=';
                    if (i < part.size() && part[i] == '=') ++i;
                    const char quote = i < part.size() ? part[i] : 0;
                    if (quote == '\'' || quote == '"') {
                        const auto close = part.find(quote, i + 1);
                        attribute.value = std::string(part.substr(i + 1, close - i - 1));
                        i = close == std::string_view::npos ? part.size() : close + 1;
                    } else {
                        const auto close = part.find(']', i);
                        attribute.value = std::string(trim(part.substr(i, close - i)));
                        i = close == std::string_view::npos ? part.size() : close;
                    }
                }
                while (i < part.size() && part[i] != ']') ++i;
                ++i;
                compound.attributes.push_back(std::move(attribute));
            } else break;  // combinators are not needed by md-viewer
        }
        alternatives_.push_back(std::move(compound));
    }
}

bool Selector::matches(const Node& element) const {
    if (!element.is_element()) return false;
    for (const auto& compound : alternatives_) {
        if (!compound.tag.empty() && compound.tag != "*" && compound.tag != element.tag) continue;
        if (!compound.id.empty() && element.id() != compound.id) continue;
        if (!compound.classes.empty()) {
            const auto classes = element.classes();
            if (!std::all_of(compound.classes.begin(), compound.classes.end(), [&](const std::string& name) { return std::find(classes.begin(), classes.end(), name) != classes.end(); }))
                continue;
        }
        const bool attributes_match = std::all_of(compound.attributes.begin(), compound.attributes.end(), [&](const Attribute& attribute) {
            const auto* value = element.attribute(attribute.name);
            if (!value) return false;
            switch (attribute.op) {
                case 0: return true;
                case '=': return *value == attribute.value;
                case '*': return value->find(attribute.value) != std::string::npos;
                case '^': return value->starts_with(attribute.value);
                case '$': return value->ends_with(attribute.value);
                case '~': {
                    Node probe;
                    probe.attributes = {{"class", *value}};
                    const auto words = probe.classes();
                    return std::find(words.begin(), words.end(), attribute.value) != words.end();
                }
                default: return false;
            }
        });
        if (attributes_match) return true;
    }
    return false;
}

std::vector<Node*> select_all(Node& root, const Selector& selector) {
    std::vector<Node*> result;
    walk(root, [&](Node& element) { if (selector.matches(element)) result.push_back(&element); });
    return result;
}

Node* select_first(Node& root, const Selector& selector) {
    Node* found = nullptr;
    std::function<bool(Node&)> search = [&](Node& node) {
        for (const auto& child : node.children) {
            if (!child->is_element()) continue;
            if (selector.matches(*child)) { found = child.get(); return true; }
            if (search(*child)) return true;
        }
        return false;
    };
    search(root);
    return found;
}

void for_each_element(Node& root, const std::function<void(Node&)>& visit) { walk(root, visit); }

namespace {

std::unique_ptr<Node> detach(Node* node, size_t* index = nullptr) {
    auto& siblings = node->parent->children;
    const auto position = std::find_if(siblings.begin(), siblings.end(), [&](const std::unique_ptr<Node>& child) { return child.get() == node; });
    if (position == siblings.end()) return nullptr;
    auto owned = std::move(*position);
    const auto next = siblings.erase(position);
    if (index) *index = static_cast<size_t>(next - siblings.begin());
    return owned;
}

void bury(Node* anchor, std::unique_ptr<Node> owned) {
    Node* top = anchor;
    while (top->parent) top = top->parent;
    top->detached.push_back(std::move(owned));
}

}  // namespace

void remove(Node* node) {
    if (!node || !node->parent) return;
    Node* parent = node->parent;
    if (auto owned = detach(node)) {
        owned->parent = nullptr;
        bury(parent, std::move(owned));
    }
}

void unwrap(Node* node) {
    if (!node || !node->parent) return;
    Node* parent = node->parent;
    size_t index = 0;
    auto owned = detach(node, &index);
    if (!owned) return;
    for (auto& child : owned->children) child->parent = parent;
    parent->children.insert(parent->children.begin() + static_cast<std::ptrdiff_t>(index),
                            std::make_move_iterator(owned->children.begin()), std::make_move_iterator(owned->children.end()));
    owned->children.clear();
    owned->parent = nullptr;
    bury(parent, std::move(owned));
}

void set_text_content(Node& element, std::string text) {
    for (auto& child : element.children) {
        child->parent = nullptr;
        bury(&element, std::move(child));
    }
    element.children.clear();
    auto child = std::make_unique<Node>();
    child->kind = Node::Kind::Text;
    child->text = std::move(text);
    child->parent = &element;
    element.children.push_back(std::move(child));
}

}  // namespace mdv::html
