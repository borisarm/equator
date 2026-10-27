#include "equator/kernel.hpp"

#include <cmath>
#include <memory>
#include <stdexcept>
#include <string>
#include <variant>

#include "equator/expression.hpp"
#include "equator/parser.hpp"

namespace equator {
namespace {

double evaluate(const Expr& expr) {
    return std::visit(
        [](const auto& value) -> double {
            using T = std::decay_t<decltype(value)>;
            if constexpr (std::is_same_v<T, NumberExpr>) {
                return value.value;
            } else if constexpr (std::is_same_v<T, VariableExpr>) {
                if (value.name == "pi") return M_PI;
                if (value.name == "e") return M_E;
                throw std::runtime_error("Unknown variable: " + value.name);
            } else if constexpr (std::is_same_v<T, BinaryExpr>) {
                const double left = evaluate(*value.left);
                const double right = evaluate(*value.right);
                switch (value.op) {
                    case '+': return left + right;
                    case '-': return left - right;
                    case '*': return left * right;
                    case '/':
                        if (right == 0.0) throw std::runtime_error("Division by zero");
                        return left / right;
                    default: throw std::runtime_error("Unsupported operator");
                }
            }
        },
        expr.inner);
}

} // namespace

void NativeKernel::load_notebook(const std::string& json) {
    (void)json;
}

void NativeKernel::execute_cell(const Cell& cell) {
    (void)cell;
}

EvaluationResult NativeKernel::evaluate_expression(const std::string& expr) {
    EvaluationResult result;
    try {
        auto parsed = parse_expression(expr);
        const double value = evaluate(*parsed);
        result.output_text = std::to_string(value);
        result.output_latex = std::to_string(value);
    } catch (const std::exception& ex) {
        result.error = ex.what();
        result.output_text = "error";
    }
    return result;
}

std::string NativeKernel::serialize_notebook() const {
    return "{}";
}

} // namespace equator
