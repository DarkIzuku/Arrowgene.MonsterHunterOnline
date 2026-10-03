cmake_minimum_required(VERSION 3.20)
project(MHOIFSExtractor LANGUAGES CXX)

if(NOT MSVC)
  message(FATAL_ERROR "MHOIFSExtractor currently requires MSVC Win32/x86.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
  message(FATAL_ERROR "MHOIFSExtractor must be built for Win32/x86.")
endif()

add_executable(MHOIFSExtractor src/main.cpp)
target_compile_features(MHOIFSExtractor PRIVATE cxx_std_20)
target_compile_definitions(MHOIFSExtractor PRIVATE UNICODE _UNICODE)
target_compile_options(MHOIFSExtractor PRIVATE /W4 /EHsc)
