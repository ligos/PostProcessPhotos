rem Clean before building
dotnet clean -c Release

rem Publish for .NET Core 2.1
dotnet publish -c Release -f netcoreapp2.1 

rem Delete config files so we can deploy
del /q bin\Release\netcoreapp2.1\publish\appsettings*.json

pause