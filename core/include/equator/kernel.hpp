#pragma once

#include <optional>
#include <string>
#include <vector>

#include "equator/kernel_api.hpp"

namespace equator {

class NativeKernel final : public Kernel {
public:
    void load_notebook(const std::string& json) override;
    void execute_cell(const Cell& cell) override;
    EvaluationResult evaluate_expression(const std::string& expr) override;
    std::string serialize_notebook() const override;
};

} // namespace equator
