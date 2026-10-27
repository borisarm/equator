#pragma once

#include <memory>
#include <string>
#include <variant>

namespace equator {

struct NumberExpr;
struct VariableExpr;
struct BinaryExpr;

struct Expr {
    std::variant<NumberExpr, VariableExpr, BinaryExpr> inner;
};

struct NumberExpr {
    double value;
};

struct VariableExpr {
    std::string name;
};

struct BinaryExpr {
    char op;
    std::shared_ptr<Expr> left;
    std::shared_ptr<Expr> right;
};

} // namespace equator
