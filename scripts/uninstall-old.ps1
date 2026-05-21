#Requires -Version 5.1
<#
.SYNOPSIS
  Remove legacy Bimwright RVT plugin installation before/after upgrading to RvtMcp.

.DESCRIPTION
  Stub — filled in Wave 6. Cleans up:
    - %APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright\ (legacy plugin folder, all years 2022-2027)
    - %APPDATA%\Autodesk\Revit\Addins\<year>\Bimwright.R<XX>.addin (legacy manifests)
    - %LOCALAPPDATA%\Bimwright\rvt\server\* (legacy server install root)
  Does NOT remove %LOCALAPPDATA%\Bimwright\ entirely — that contains user data
  (journal, baked tools, logs) that Wave 6 migrates instead.
#>
param([switch]$WhatIf)
Write-Host "uninstall-old.ps1: stub — implementation in Wave 6"
