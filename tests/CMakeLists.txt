cmake_minimum_required(VERSION 3.24)

project(EquatorTests LANGUAGES CXX)

add_executable(equator_tests src/smoke_test.cpp)
target_link_libraries(equator_tests PRIVATE equator_core)
