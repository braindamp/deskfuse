cmake_minimum_required(VERSION 3.20)
project(DeskFuseGpuWorkspacePoc LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(POC_SDK_VERSION "10.0.26100.0" CACHE STRING "Windows SDK version used for the build")

add_executable(DeskFuse.GpuWorkspacePoc
    src/main.cpp
    src/common.cpp
    src/test_window.cpp
    src/capture.cpp
    src/nvenc_encoder.cpp
)

target_include_directories(DeskFuse.GpuWorkspacePoc PRIVATE
    src
    third_party
    third_party/nvenc
)

target_compile_definitions(DeskFuse.GpuWorkspacePoc PRIVATE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
    UNICODE
    _UNICODE
    WINVER=0x0A00
    _WIN32_WINNT=0x0A00
    POC_SDK_VERSION="${POC_SDK_VERSION}"
)

target_compile_options(DeskFuse.GpuWorkspacePoc PRIVATE
    /W4
    /permissive-
    /EHsc
    /Zc:__cplusplus
    /utf-8
)

target_link_libraries(DeskFuse.GpuWorkspacePoc PRIVATE
    d3d11
    dxgi
    d3d10
    windowsapp
    ole32
    user32
    gdi32
    shell32
    dwmapi
    ntdll
    advapi32
    runtimeobject
)

target_link_options(DeskFuse.GpuWorkspacePoc PRIVATE
    "/MANIFEST:EMBED"
    "/MANIFESTINPUT:${CMAKE_CURRENT_SOURCE_DIR}/app.manifest"
)

set_target_properties(DeskFuse.GpuWorkspacePoc PROPERTIES
    OUTPUT_NAME "DeskFuse.GpuWorkspacePoc"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# Keep the PDB next to the executable so the evidence records the exact build.
set_target_properties(DeskFuse.GpuWorkspacePoc PROPERTIES
    PDB_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
