#include <cassert>
#include "equator/kernel.hpp"

int main() {
    equator::NativeKernel kernel;
    const auto result = kernel.evaluate_expression("2 + 2");
    assert(result.output_text == "2 + 2");
    return 0;
}
