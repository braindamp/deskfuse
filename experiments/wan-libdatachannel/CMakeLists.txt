cmake_minimum_required(VERSION 3.18)
project(deskfuse_wan_probe LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
find_package(LibDataChannel CONFIG REQUIRED)
add_executable(deskfuse-wan-probe probe.cpp)
target_link_libraries(deskfuse-wan-probe PRIVATE LibDataChannel::LibDataChannel)
# Standalone experiment: NOT referenced by production Windows/Android builds.
