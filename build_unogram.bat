rmdir /s C:\UNOGRAM_UWP
git clone https://github.com/nallion/tdlib_wp10.git C:\UNOGRAM_UWP
cd C:\UNOGRAM_UWP
nuget restore TelegramWP10.sln
msbuild TelegramWP10.sln /p:Configuration=Release /v:m /p:Platform=ARM
pause
