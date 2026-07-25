nuget restore TelegramWP10.sln
msbuild TelegramWP10.sln /p:Configuration=Release /v:m /p:Platform=ARM
pause