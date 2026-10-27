#pragma once

#include <optional>
#include <string>
#include <vector>

namespace equator {

struct EvaluationResult {
    std::string output_text;
    std::string output_latex;
    std::optional<std::string> error;
    std::vector<std::string> warnings;
};

} // namespace equator
