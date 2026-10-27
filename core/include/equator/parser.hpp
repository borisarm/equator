#pragma once

#include <memory>
#include <string_view>

#include "equator/expression.hpp"

namespace equator {

std::unique_ptr<Expr> parse_expression(std::string_view source);

} // namespace equator
