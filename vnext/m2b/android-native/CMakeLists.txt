cmake_minimum_required(VERSION 3.24)
project(DeskFuseAndroidAudio LANGUAGES CXX)

if(NOT ANDROID)
  message(FATAL_ERROR "M2b AAudio renderer is an Android native adapter")
endif()

add_library(deskfuse_android_audio SHARED src/android_audio.cpp)
target_compile_features(deskfuse_android_audio PRIVATE cxx_std_20)
# Keep only the real-time renderer at the measured optimized setting even in
# Debug APKs; JNI glue and transport retain their normal variant diagnostics.
# The reviewed standalone renderer used Clang -O3 -DNDEBUG. This does not
# substitute for verifying the packaged binary and callback timing on-device.
target_compile_options(deskfuse_android_audio PRIVATE -Wall -Wextra -Werror -fvisibility=hidden -O3 -DNDEBUG)
target_link_libraries(deskfuse_android_audio PRIVATE aaudio log)
set_target_properties(deskfuse_android_audio PROPERTIES
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN YES
)
