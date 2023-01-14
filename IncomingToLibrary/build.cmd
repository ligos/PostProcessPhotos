rem Clean before building
dotnet clean -c Release

rem Publish for .NET
dotnet publish -c Release -f net6.0

rem Delete config files so we can deploy
del /q bin\Release\net6.0\publish\appsettings*.json

pause