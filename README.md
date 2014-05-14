NuGetPackageExplorer
====================

A fork of NuGet Package Explorer on CodePlex: https://npe.codeplex.com/

Fixes some common issues I have found:
* Does not work with NuGet feeds that require Windows Authentication (e.g. ProGet/TeamCity).
* Package history does not work with older NuGet feeds that doesn't support the PackagesById() service.
