
cmake_minimum_required(VERSION 3.15)

cmake_policy(SET CMP0091 NEW)
project(moira CXX)

# --- ASE patch: DIVS/DIVU issue their prefetch LAST -------------------------------
#
# Moira's execDivsMoira/execDivuMoira call prefetch<C, POLL>() before SYNC(cycles), so
# with MOIRA_PRECISE_TIMING the instruction signals its bus access on cycle +2 instead of
# +138. A real 68000 does it the other way round: yacht.txt gives DIVU/DIVS "nn n [seq.]
# n nn np" -- the np last -- and MULU/MULS "np n*" -- the np first, which Moira already
# gets right. Confirmed against the SingleStepTests/680x0 bus traces: the prefetch is the
# LAST transaction in 1239/1239 DIVS and 2494/2494 DIVU cases and the FIRST in 4943/4943
# MULS and 4991/4991 MULU. The instruction's total length matches Moira's exactly, so
# cyclesDiv is right and only the position is wrong -- which is also why Moira's own
# runner cannot see it: it compares totals against Musashi, which is not bus-cycle exact.
#
# It is not cosmetic here. The blitter hands the CPU a quota of bus *accesses*, so where a
# long instruction puts its access decides which one exhausts that quota and how much of
# the instruction overlaps the blitter's burst: with the prefetch at the front, a loop of
# move.l/divs pairs gives the blitter 63 accesses per burst where the machine gives 64.
#
# The core is not versioned in this repository (see .gitignore), so the fix is applied to
# the downloaded copy, in place, and recognised by its marker on every later build.
set(ASE_EXEC_FILE "${CMAKE_CURRENT_SOURCE_DIR}/MoiraExec_cpp.h")
file(READ "${ASE_EXEC_FILE}" ASE_EXEC_SRC)

if(NOT ASE_EXEC_SRC MATCHES "ASE_DIV_PREFETCH_LAST")

    # Normalised to LF first, so the literal below matches whatever the download carries.
    string(REPLACE "\r\n" "\n" ASE_EXEC_SRC "${ASE_EXEC_SRC}")

    set(ASE_DIV_BEFORE [=[    writeD(dst, result);
    prefetch<C, POLL>();

    [[maybe_unused]] auto cycles = cyclesDiv<C, I>(dividend, (u16)divisor) - 4;
    SYNC(cycles);]=])

    set(ASE_DIV_AFTER [=[    writeD(dst, result);

    [[maybe_unused]] auto cycles = cyclesDiv<C, I>(dividend, (u16)divisor) - 4;
    SYNC(cycles);
    prefetch<C, POLL>();   // ASE_DIV_PREFETCH_LAST]=])

    string(REPLACE "${ASE_DIV_BEFORE}" "${ASE_DIV_AFTER}" ASE_EXEC_SRC "${ASE_EXEC_SRC}")

    string(REGEX MATCHALL "ASE_DIV_PREFETCH_LAST" ASE_DIV_HITS "${ASE_EXEC_SRC}")
    list(LENGTH ASE_DIV_HITS ASE_DIV_COUNT)

    if(NOT ASE_DIV_COUNT EQUAL 2)
        message(FATAL_ERROR
            "Moira: could not move the DIVS/DIVU prefetch (patched ${ASE_DIV_COUNT} of 2 "
            "sites). execDivsMoira/execDivuMoira in MoiraExec_cpp.h no longer end with "
            "'prefetch<C, POLL>(); ... SYNC(cycles);' -- check whether upstream has fixed "
            "this itself, and drop this block if so.")
    endif()

    file(WRITE "${ASE_EXEC_FILE}" "${ASE_EXEC_SRC}")
    message(STATUS "Moira: DIVS/DIVU prefetch moved to the end of the instruction (ASE patch)")

else()
    message(STATUS "Moira: DIVS/DIVU prefetch already patched")
endif()

# Moira instantiates its instruction handlers where it builds the jump tables, and
# doing all three cores in one translation unit is over 10,000 functions -- more peak
# memory than some compilers have. Upstream therefore compiles that part once per core
# (MoiraCore_cpp.h with MOIRA_CORE set), and these three files must be built alongside
# Moira.cpp or the jump tables are left undefined at link time.
add_library(moira SHARED
 Moira.cpp
 MoiraCore68000.cpp
 MoiraCore68010.cpp
 MoiraCore68020.cpp
 MoiraDebugger.cpp
 Moira_dotnet.cpp
)

target_compile_features(moira PUBLIC cxx_std_20)

set_target_properties(moira PROPERTIES
  CXX_STANDARD 20
  CXX_STANDARD_REQUIRED YES
  CXX_EXTENSIONS NO
)

if (NOT MSVC)
    target_compile_options(moira PUBLIC -Wno-unused-parameter)
    target_compile_options(moira PUBLIC -Wno-unused-but-set-parameter)
    target_compile_options(moira PUBLIC -Wno-unused-but-set-variable)
    target_compile_options(moira PUBLIC -Wno-missing-field-initializers)
endif()

if(MSVC)
    target_compile_options(moira PUBLIC /W4 /bigobj) # /WX disabled for now
    target_compile_options(moira PUBLIC /wd4100 /wd4201 /wd4324 /wd4458 /wd4127)

    # /MT (/MTd for Debug): no VCRUNTIME140*/MSVCP140 imports
    set_property(TARGET moira PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

if(MINGW)
    target_compile_options(moira PUBLIC -Wa,-mbig-obj)
    target_link_options(moira PRIVATE -static)
endif()

set_target_properties(moira PROPERTIES OUTPUT_NAME "moira")
set_target_properties(moira PROPERTIES PREFIX "")

# --- Resolve the .NET RID (<os>-<arch>) this build targets, so the library
#     lands in native/<rid>/ to match ASE.csproj's per-RID layout.
#     Override with -DMOIRA_RID=<os>-<arch> (e.g. cross-compiling). ---
if(NOT DEFINED MOIRA_RID OR MOIRA_RID STREQUAL "")
    if(WIN32)
        set(_moira_os "win")
    elseif(APPLE)
        set(_moira_os "osx")
    elseif(UNIX)
        set(_moira_os "linux")
    else()
        message(FATAL_ERROR "Moira: unsupported target OS; pass -DMOIRA_RID=<os>-<arch>")
    endif()

    # Prefer explicit cross-compile hints over the host processor.
    if(APPLE AND CMAKE_OSX_ARCHITECTURES)
        set(_moira_arch_raw "${CMAKE_OSX_ARCHITECTURES}")
    elseif(MSVC AND CMAKE_GENERATOR_PLATFORM)
        set(_moira_arch_raw "${CMAKE_GENERATOR_PLATFORM}")
    else()
        set(_moira_arch_raw "${CMAKE_SYSTEM_PROCESSOR}")
    endif()
    string(TOLOWER "${_moira_arch_raw}" _moira_arch_raw)

    if(_moira_arch_raw MATCHES ";")
        message(FATAL_ERROR "Moira: universal/multi-arch build ('${_moira_arch_raw}') has no single RID folder; build one arch at a time or pass -DMOIRA_RID=<os>-<arch>")
    elseif(_moira_arch_raw MATCHES "arm64|aarch64")
        set(_moira_arch "arm64")
    elseif(_moira_arch_raw MATCHES "x64|x86_64|amd64")
        set(_moira_arch "x64")
    else()
        message(FATAL_ERROR "Moira: cannot map architecture '${_moira_arch_raw}' to a RID; pass -DMOIRA_RID=<os>-<arch>")
    endif()

    set(MOIRA_RID "${_moira_os}-${_moira_arch}")
endif()

set(MOIRA_OUT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../ASE/native/${MOIRA_RID}" CACHE PATH "Output dir for native library")
message(STATUS "Moira: target RID = ${MOIRA_RID}  ->  ${MOIRA_OUT_DIR}")

file(MAKE_DIRECTORY "${MOIRA_OUT_DIR}")

set_target_properties(moira PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${MOIRA_OUT_DIR}"

    ARCHIVE_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${MOIRA_OUT_DIR}"
    
    ARCHIVE_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${MOIRA_OUT_DIR}"
)

