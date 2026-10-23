# DeskFuse vNext M2a: Android ARM64 NDK cross-compilation on Windows host.
#
# Verified configuration reported for Windows 10 + NDK r30 (30.0.16248370)
# + cargo-ndk 4.1.2 + boring-sys 5.3.0.
#
# Supplied through the target-specific Cargo build-script environment variable:
#   CMAKE_TOOLCHAIN_FILE_aarch64_linux_android=<absolute path to this file>
# Never set as a global CMake toolchain for native Windows builds.
#
# The NDK's own toolchain chooses its compiler with the correct .exe suffix,
# instead of CMake keeping a Windows compiler/linker configuration.
set(ANDROID_ABI arm64-v8a)
set(ANDROID_PLATFORM android-35)
set(ANDROID_STL c++_shared)

if(NOT DEFINED ENV{ANDROID_NDK_HOME} OR "$ENV{ANDROID_NDK_HOME}" STREQUAL "")
  message(FATAL_ERROR "ANDROID_NDK_HOME must point to a complete Android NDK installation")
endif()

file(TO_CMAKE_PATH "$ENV{ANDROID_NDK_HOME}" _deskfuse_ndk_home)
if(NOT EXISTS "${_deskfuse_ndk_home}/build/cmake/android.toolchain.cmake")
  message(FATAL_ERROR "Cannot locate Android NDK CMake toolchain in ${_deskfuse_ndk_home}")
endif()
include("${_deskfuse_ndk_home}/build/cmake/android.toolchain.cmake")
