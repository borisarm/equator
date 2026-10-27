#pragma once

#include <optional>
#include <string>
#include <vector>

namespace equator {

struct NotebookCell {
    std::string id;
    std::string input;
    std::string language = "equator";
};

struct EvaluationResult {
    std::string output_text;
    std::string output_latex;
    std::optional<std::string> error;
    std::vector<std::string> warnings;
};

struct Notebook {
    std::string title;
    std::vector<NotebookCell> cells;
};

} // namespace equator
