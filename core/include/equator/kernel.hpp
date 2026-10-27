#pragma once

#include <optional>
#include <string>
#include <vector>

namespace equator {

struct Cell {
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

struct PlotSpec {
    std::string type;
    std::vector<std::string> arguments;
};

class Kernel {
public:
    virtual ~Kernel() = default;

    virtual void load_notebook(const std::string& json) = 0;
    virtual void execute_cell(const Cell& cell) = 0;
    virtual EvaluationResult evaluate_expression(const std::string& expr) = 0;
    virtual std::string serialize_notebook() const = 0;
};

} // namespace equator
