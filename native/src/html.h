#pragma once

#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace mdv::html {

// A small mutable DOM built from Gumbo's HTML5 parse, enough for the crawler to select,
// prune, rewrite, and serialize page content.
struct Node {
    enum class Kind { Document, Element, Text, Comment };
    Kind kind = Kind::Element;
    std::string tag;  // lower-case local name for elements
    std::vector<std::pair<std::string, std::string>> attributes;
    std::string text;  // text and comment nodes
    Node* parent = nullptr;
    std::vector<std::unique_ptr<Node>> children;
    // Removed subtrees stay alive here (on the topmost ancestor) so selections taken before a
    // removal remain valid, as they do in a browser DOM.
    std::vector<std::unique_ptr<Node>> detached;

    bool is_element() const { return kind == Kind::Element; }
    const std::string* attribute(std::string_view name) const;
    bool has_attribute(std::string_view name) const { return attribute(name) != nullptr; }
    void set_attribute(std::string_view name, std::string value);
    std::string id() const;
    std::string class_name() const;
    std::vector<std::string> classes() const;
    std::string text_content() const;
    std::string inner_html() const;
};

struct Document {
    std::unique_ptr<Node> root;
    Node* body() const;
    Node* document_element() const;
    std::string title() const;
};

Document parse(std::string_view html);

// Selector groups such as "main, article.bd-article, div[role='main'], [style*='display:none']":
// compound selectors of tag, #id, .class, and [attr], [attr=v], [attr*=v], [attr^=v], [attr$=v], [attr~=v].
class Selector {
public:
    explicit Selector(std::string_view text);
    bool matches(const Node& element) const;

private:
    struct Attribute { std::string name; char op = 0; std::string value; };
    struct Compound { std::string tag; std::string id; std::vector<std::string> classes; std::vector<Attribute> attributes; };
    std::vector<Compound> alternatives_;
};

// Descendant elements of root (excluding root) in document order.
std::vector<Node*> select_all(Node& root, const Selector& selector);
Node* select_first(Node& root, const Selector& selector);
void for_each_element(Node& root, const std::function<void(Node&)>& visit);

void remove(Node* node);
void unwrap(Node* node);  // replaces the element with its children
void set_text_content(Node& element, std::string text);

}  // namespace mdv::html
