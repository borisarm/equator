add_library(equator_core
    src/kernel.cpp
    src/parser.cpp
)

target_include_directories(equator_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_features(equator_core PUBLIC cxx_std_23)

set_target_properties(equator_core PROPERTIES
    EXPORT_NAME core
)
