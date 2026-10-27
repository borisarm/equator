#include <iostream>
#include "equator/kernel.hpp"

int main() {
    equator::NativeKernel kernel;
    const auto result = kernel.evaluate_expression("1 + 1");
    std::cout << result.output_text << '\n';
    return 0;
}
