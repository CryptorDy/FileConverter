cmake_minimum_required(VERSION 3.15)
project(EssentiaWrapper)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Настройки для Windows и Linux
if(WIN32)
    set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)
    add_definitions(-DESSENTIAWRAPPER_EXPORTS)
endif()

# Поиск Essentia
find_path(ESSENTIA_INCLUDE_DIR essentia/algorithm.h 
    HINTS ${ESSENTIA_DIR}/include /usr/local/include /usr/include)

find_library(ESSENTIA_LIBRARY 
    NAMES essentia libessentia
    HINTS ${ESSENTIA_DIR}/lib /usr/local/lib /usr/lib)

if(NOT ESSENTIA_INCLUDE_DIR OR NOT ESSENTIA_LIBRARY)
    message(FATAL_ERROR "Essentia not found. Please install Essentia or set ESSENTIA_DIR")
endif()

# Создание библиотеки
add_library(EssentiaWrapper SHARED
    EssentiaWrapper.cpp
    EssentiaWrapper.h
    pch.h
)

# Включение заголовочных файлов
target_include_directories(EssentiaWrapper PRIVATE
    ${ESSENTIA_INCLUDE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}
)

# Связывание с Essentia
target_link_libraries(EssentiaWrapper PRIVATE
    ${ESSENTIA_LIBRARY}
)

# Дополнительные системные библиотеки для Linux
if(UNIX AND NOT APPLE)
    find_package(PkgConfig REQUIRED)
    
    # FFmpeg
    pkg_check_modules(LIBAVCODEC REQUIRED libavcodec)
    pkg_check_modules(LIBAVFORMAT REQUIRED libavformat)
    pkg_check_modules(LIBAVUTIL REQUIRED libavutil)
    pkg_check_modules(LIBSWRESAMPLE REQUIRED libswresample)
    
    # FFTW
    find_library(FFTW3_LIBRARY fftw3)
    if(NOT FFTW3_LIBRARY)
        message(FATAL_ERROR "FFTW3 library not found")
    endif()
    
    # TagLib
    find_library(TAGLIB_LIBRARY tag)
    if(NOT TAGLIB_LIBRARY)
        message(FATAL_ERROR "TagLib library not found")
    endif()
    
    target_link_libraries(EssentiaWrapper PRIVATE
        ${LIBAVCODEC_LIBRARIES}
        ${LIBAVFORMAT_LIBRARIES}
        ${LIBAVUTIL_LIBRARIES}
        ${LIBSWRESAMPLE_LIBRARIES}
        ${FFTW3_LIBRARY}
        ${TAGLIB_LIBRARY}
        pthread
        yaml-cpp
    )
    
    target_include_directories(EssentiaWrapper PRIVATE
        ${LIBAVCODEC_INCLUDE_DIRS}
        ${LIBAVFORMAT_INCLUDE_DIRS}
        ${LIBAVUTIL_INCLUDE_DIRS}
        ${LIBSWRESAMPLE_INCLUDE_DIRS}
    )
endif()

# Настройки компилятора
if(MSVC)
    target_compile_options(EssentiaWrapper PRIVATE /W4)
else()
    target_compile_options(EssentiaWrapper PRIVATE -Wall -Wextra -fPIC)
endif()

# Установка
install(TARGETS EssentiaWrapper 
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
) 