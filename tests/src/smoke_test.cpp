#include <cassert>
#include "equator/kernel.hpp"

int main() {
    equator::NativeKernel kernel;
    const auto result = kernel.evaluate_expression("2 + 2");
    assert(!result.error);
    assert(result.output_text == "4.000000");
    return 0;
}
