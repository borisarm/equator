#include "equator/kernel.hpp"

#include <string>

namespace equator {

void NativeKernel::load_notebook(const std::string& json) {
    (void)json;
}

void NativeKernel::execute_cell(const Cell& cell) {
    (void)cell;
}

EvaluationResult NativeKernel::evaluate_expression(const std::string& expr) {
    EvaluationResult result;
    result.output_text = expr;
    result.output_latex = expr;
    return result;
}

std::string NativeKernel::serialize_notebook() const {
    return "{}";
}

} // namespace equator
