cmake_minimum_required(VERSION 3.20)
project(DeskFuseTimingLab VERSION 0.1.0 LANGUAGES CXX)

option(DF_BUILD_TESTS "Build deterministic and real-thread tests" ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
  add_compile_options(/W4 /permissive- /EHsc)
else()
  add_compile_options(-Wall -Wextra -Wpedantic -Wconversion -Wshadow)
endif()

add_library(df_wp001_core STATIC
    src/strict_sink.cpp
    src/feedback.cpp
    src/timing_lab.cpp)
target_include_directories(df_wp001_core PUBLIC include)
find_package(Threads REQUIRED)
target_link_libraries(df_wp001_core PUBLIC Threads::Threads)

add_executable(df_timing_lab src/main.cpp)
target_link_libraries(df_timing_lab PRIVATE df_wp001_core)

if(DF_BUILD_TESTS)
  enable_testing()
  add_executable(df_wp001_tests tests/test_main.cpp)
  target_link_libraries(df_wp001_tests PRIVATE df_wp001_core)
  add_test(NAME wp001_deterministic_and_threads COMMAND df_wp001_tests)
  set_tests_properties(wp001_deterministic_and_threads PROPERTIES TIMEOUT 90)
endif()
