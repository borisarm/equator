cmake_minimum_required(VERSION 3.24)

project(EquatorCLI LANGUAGES CXX)

add_executable(equator_cli src/main.cpp)

target_link_libraries(equator_cli PRIVATE equator_core)
