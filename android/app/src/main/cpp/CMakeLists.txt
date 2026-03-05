cmake_minimum_required(VERSION 3.22.1)
project("soundlink" LANGUAGES C CXX)

# Download and build libopus
include(FetchContent)
FetchContent_Declare(
    opus
    GIT_REPOSITORY https://gitlab.xiph.org/xiph/opus.git
    GIT_TAG v1.4
)
set(OPUS_BUILD_SHARED_LIBRARY OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_TESTING OFF CACHE BOOL "" FORCE)
set(OPUS_BUILD_PROGRAMS OFF CACHE BOOL "" FORCE)
set(OPUS_INSTALL_PKG_CONFIG_MODULE OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(opus)

# JNI bridge library
add_library(soundlink_opus SHARED opus_jni.cpp)
target_link_libraries(soundlink_opus PRIVATE opus log)
target_include_directories(soundlink_opus PRIVATE ${opus_SOURCE_DIR}/include)
