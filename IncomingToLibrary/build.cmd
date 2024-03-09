rem Clean before building
dotnet clean -c Release

rem Publish for .NET
dotnet publish -c Release -f net8.0

rem Delete config files so we can deploy
del /q bin\Release\net8.0\publish\appsettings*.json

pause