#include "equator/parser.hpp"

#include <cctype>
#include <stdexcept>
#include <string>
#include <utility>

namespace equator {
namespace {

class Parser {
public:
    explicit Parser(std::string_view source) : source_(source) {}

    std::unique_ptr<Expr> parse() {
        auto expr = parse_expression();
        skip_whitespace();
        if (!at_end()) {
            throw std::runtime_error("Unexpected trailing input");
        }
        return expr;
    }

private:
    std::unique_ptr<Expr> parse_expression() {
        auto left = parse_term();
        skip_whitespace();
        while (!at_end() && (peek() == '+' || peek() == '-')) {
            const char op = peek();
            advance();
            auto right = parse_term();
            auto node = std::make_unique<Expr>();
            node->inner = BinaryExpr{op, std::shared_ptr<Expr>(std::move(left)), std::shared_ptr<Expr>(std::move(right))};
            left = std::move(node);
            skip_whitespace();
        }
        return left;
    }

    std::unique_ptr<Expr> parse_term() {
        auto left = parse_factor();
        skip_whitespace();
        while (!at_end() && (peek() == '*' || peek() == '/')) {
            const char op = peek();
            advance();
            auto right = parse_factor();
            auto node = std::make_unique<Expr>();
            node->inner = BinaryExpr{op, std::shared_ptr<Expr>(std::move(left)), std::shared_ptr<Expr>(std::move(right))};
            left = std::move(node);
            skip_whitespace();
        }
        return left;
    }

    std::unique_ptr<Expr> parse_factor() {
        skip_whitespace();
        if (at_end()) {
            throw std::runtime_error("Unexpected end of input");
        }

        if (peek() == '(') {
            advance();
            auto expr = parse_expression();
            skip_whitespace();
            if (at_end() || peek() != ')') {
                throw std::runtime_error("Missing closing parenthesis");
            }
            advance();
            return expr;
        }

        if (peek() == '+' || peek() == '-') {
            const char sign = peek();
            advance();
            auto value = parse_factor();
            auto node = std::make_unique<Expr>();
            node->inner = BinaryExpr{sign, std::make_shared<Expr>(NumberExpr{0.0}), std::shared_ptr<Expr>(std::move(value))};
            return node;
        }

        if (std::isdigit(static_cast<unsigned char>(peek())) || peek() == '.') {
            const std::size_t start = pos_;
            bool saw_decimal = false;
            while (!at_end()) {
                const char ch = peek();
                if (std::isdigit(static_cast<unsigned char>(ch))) {
                    advance();
                    continue;
                }
                if (ch == '.' && !saw_decimal) {
                    saw_decimal = true;
                    advance();
                    continue;
                }
                break;
            }

            const std::string token = std::string(source_.substr(start, pos_ - start));
            auto node = std::make_unique<Expr>();
            node->inner = NumberExpr{std::stod(token)};
            return node;
        }

        const std::size_t start = pos_;
        while (!at_end() && (std::isalnum(static_cast<unsigned char>(peek())) || peek() == '_')) {
            advance();
        }

        if (start == pos_) {
            throw std::runtime_error("Unexpected token");
        }

        auto node = std::make_unique<Expr>();
        node->inner = VariableExpr{std::string(source_.substr(start, pos_ - start))};
        return node;
    }

    void skip_whitespace() {
        while (!at_end() && std::isspace(static_cast<unsigned char>(peek()))) {
            advance();
        }
    }

    bool at_end() const { return pos_ >= source_.size(); }
    char peek() const { return source_[pos_]; }
    void advance() { ++pos_; }

    std::string_view source_;
    std::size_t pos_ = 0;
};

} // namespace

std::unique_ptr<Expr> parse_expression(std::string_view source) {
    Parser parser(source);
    return parser.parse();
}

} // namespace equator
