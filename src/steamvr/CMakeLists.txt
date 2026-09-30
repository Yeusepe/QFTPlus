cmake_minimum_required(VERSION 3.20)
project(qftplus_steamvr CXX)

set(DRIVER_DIR ${CMAKE_BINARY_DIR}/qftplus CACHE PATH "Output driver folder")
add_library(driver_qftplus SHARED driver.cpp)
target_compile_features(driver_qftplus PRIVATE cxx_std_17)
target_compile_definitions(driver_qftplus PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(driver_qftplus PRIVATE ws2_32)
set_target_properties(driver_qftplus PROPERTIES
    MSVC_RUNTIME_LIBRARY MultiThreaded
    RUNTIME_OUTPUT_DIRECTORY $<1:${DRIVER_DIR}/bin/win64>)
add_custom_command(TARGET driver_qftplus POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_CURRENT_SOURCE_DIR}/qftplus ${DRIVER_DIR})
