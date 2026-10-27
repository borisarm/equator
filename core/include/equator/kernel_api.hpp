#pragma once

#include <string>
#include <vector>

#include "equator/cell.hpp"
#include "equator/result.hpp"

namespace equator {

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
