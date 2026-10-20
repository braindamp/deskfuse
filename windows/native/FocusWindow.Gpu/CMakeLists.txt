cmake_minimum_required(VERSION 3.20)
project(FocusWindowGpu LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_library(FocusWindow.Gpu SHARED
    src/common.cpp src/capture.cpp src/nvenc_encoder.cpp src/gpu_pipeline.cpp)
target_include_directories(FocusWindow.Gpu PUBLIC include PRIVATE src third_party/nvenc)
target_compile_definitions(FocusWindow.Gpu PRIVATE
    FWGPU_BUILD_DLL NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE WINVER=0x0A00 _WIN32_WINNT=0x0A00)
target_compile_options(FocusWindow.Gpu PRIVATE /W4 /permissive- /EHsc /Zc:__cplusplus /utf-8)
target_link_libraries(FocusWindow.Gpu PRIVATE
    d3d11 dxgi d3d10 windowsapp ole32 user32 gdi32 shell32 dwmapi ntdll advapi32 runtimeobject)
set_target_properties(FocusWindow.Gpu PROPERTIES OUTPUT_NAME "FocusWindow.Gpu"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

include(CTest)
if(BUILD_TESTING)
    add_executable(FocusWindow.Gpu.ContractTests tests/abi_contract.cpp tests/frame_queue.cpp)
    target_include_directories(FocusWindow.Gpu.ContractTests PRIVATE src)
    target_compile_options(FocusWindow.Gpu.ContractTests PRIVATE /W4 /permissive- /EHsc /utf-8)
    target_link_libraries(FocusWindow.Gpu.ContractTests PRIVATE FocusWindow.Gpu)
    set_target_properties(FocusWindow.Gpu.ContractTests PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
    add_test(NAME gpu_contracts_without_hardware COMMAND FocusWindow.Gpu.ContractTests)
endif()
