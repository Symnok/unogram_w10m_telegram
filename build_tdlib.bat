@echo off
setlocal

set PROJECT_DIR=C:\projects\td
set VCPKG_DIR=C:\tools\vcpkg
set OPENSSL_DIR=C:\openssl-arm-uwp
set VCPKG_TRIPLET=arm-uwp
set VS_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\VC\Auxiliary\Build\vcvarsall.bat

echo ============================================
echo  TDLib ARM UWP build script
echo ============================================

echo.
echo === STEP 1: Clone TDLib ===
if not exist %PROJECT_DIR%\.git (
    git clone https://github.com/tdlib/td.git %PROJECT_DIR%
    cd %PROJECT_DIR%
    git submodule update --init --recursive
) else (
    echo TDLib already cloned, skipping
)

echo.
echo === STEP 2: Setup vcpkg ===
if not exist %VCPKG_DIR%\vcpkg.exe (
    git clone https://github.com/microsoft/vcpkg.git %VCPKG_DIR%
    cd %VCPKG_DIR%
    git checkout 281d107
    call bootstrap-vcpkg.bat
) else (
    echo vcpkg already set up, skipping
)

echo.
echo === STEP 3: Install zlib via vcpkg ===
%VCPKG_DIR%\vcpkg.exe install zlib:%VCPKG_TRIPLET%
%VCPKG_DIR%\vcpkg.exe install zlib:x64-windows openssl:x64-windows

echo.
echo === STEP 4: Build OpenSSL 1.1.1w for ARM UWP ===
if exist %OPENSSL_DIR%\lib\libcrypto.lib (
    echo OpenSSL already built, skipping
    goto openssl_done
)

if exist C:\openssl-src rd /s /q C:\openssl-src
if exist C:\openssl.tar.gz del C:\openssl.tar.gz

echo Downloading OpenSSL 1.1.1w...
wget https://www.openssl.org/source/openssl-1.1.1w.tar.gz -O C:\openssl.tar.gz
if errorlevel 1 goto error

cd C:\
7z x openssl.tar.gz -so | 7z x -si -ttar -oC:\openssl-src
if errorlevel 1 goto error

cd C:\openssl-src\openssl-1.1.1w

echo Setting up ARM build environment...
call "%VS_PATH%" x64_arm
if errorlevel 1 goto error

echo Configuring OpenSSL for UWP ARM...
perl Configure VC-WIN32-ARM no-asm no-shared no-tests no-async no-dso no-ui-console no-capieng ^
    --prefix=%OPENSSL_DIR% --openssldir=%OPENSSL_DIR% ^
    -DWINAPI_FAMILY=WINAPI_FAMILY_APP -D_WIN32_WINNT=0x0A00 -DUNICODE -D_UNICODE
if errorlevel 1 goto error

echo Building OpenSSL libraries...
nmake /S build_libs
if errorlevel 1 goto error

echo Installing OpenSSL headers and libs...
nmake install_dev
if errorlevel 1 goto error

echo OpenSSL built successfully:
dir %OPENSSL_DIR%\lib\libcrypto.lib

:openssl_done

echo.
echo === STEP 5: Generate cross-compile files (x64 native) ===
if not exist %PROJECT_DIR%\build_native mkdir %PROJECT_DIR%\build_native
cd %PROJECT_DIR%\build_native

cmake -G "Visual Studio 15 2017" -A x64 ^
    -DCMAKE_TOOLCHAIN_FILE=%VCPKG_DIR%/scripts/buildsystems/vcpkg.cmake ^
    -DVCPKG_TARGET_TRIPLET=x64-windows ^
    -DTD_ENABLE_LTO=OFF ^
    %PROJECT_DIR%
if errorlevel 1 goto error

msbuild prepare_cross_compiling.vcxproj /p:Configuration=Release /p:Platform=x64 /m
if errorlevel 1 goto error

echo.
echo === STEP 6: Configure ARM build ===
if exist %PROJECT_DIR%\build rd /s /q %PROJECT_DIR%\build
mkdir %PROJECT_DIR%\build
cd %PROJECT_DIR%\build

cmake -G "Visual Studio 15 2017" -A ARM ^
    -DCMAKE_SYSTEM_NAME=WindowsStore ^
    -DCMAKE_SYSTEM_VERSION=10.0 ^
    -DCMAKE_TOOLCHAIN_FILE=%VCPKG_DIR%/scripts/buildsystems/vcpkg.cmake ^
    -DVCPKG_TARGET_TRIPLET=%VCPKG_TRIPLET% ^
    -DTD_ENABLE_LTO=OFF ^
    -DOPENSSL_ROOT_DIR=%OPENSSL_DIR% ^
    -DOPENSSL_INCLUDE_DIR=%OPENSSL_DIR%/include ^
    -DOPENSSL_CRYPTO_LIBRARY=%OPENSSL_DIR%/lib/libcrypto.lib ^
    -DOPENSSL_SSL_LIBRARY=%OPENSSL_DIR%/lib/libssl.lib ^
    %PROJECT_DIR%
if errorlevel 1 goto error

echo.
echo CMakeCache OpenSSL entries:
type CMakeCache.txt | findstr /i "OPENSSL"

echo.
echo === STEP 7: Build tdjson.dll ===
dir /s /b tdjson.vcxproj > proj_path.txt
set /p V_PROJ=<proj_path.txt
echo Building: %V_PROJ%

msbuild "%V_PROJ%" /p:Configuration=Release /p:Platform=ARM /m /p:AppxPackage=false
if errorlevel 1 goto error

echo.
echo === BUILD COMPLETE ===
dir /s /b %PROJECT_DIR%\build\Release\tdjson.dll
goto end

:error
echo.
echo === BUILD FAILED ===
exit /b 1

:end
echo Done!
