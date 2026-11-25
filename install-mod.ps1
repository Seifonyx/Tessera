# Installs Tessera's Windhawk mod (tessera-start-button.wh.cpp): compiles it with Windhawk's own compiler, using the
# same flags as the Windhawk editor, and registers it as the local mod "local@tessera-start-button".
#   install-mod.cmd              build and enable
#   install-mod.cmd -Logging     same, with the mod's log switched on (view it in Windhawk)
#   install-mod.cmd -Uninstall   remove the mod (Tessera then handles the Start button itself)
param([switch]$Logging, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$wh = 'C:\Apps\Utilities\Customization\Windhawk'
$src = Join-Path $here 'tessera-start-button.wh.cpp'
$text = Get-Content $src -Raw
$id = 'local@' + ([regex]::Match($text, '@id\s+(\S+)')).Groups[1].Value
$version = ([regex]::Match($text, '@version\s+(\S+)')).Groups[1].Value
$include = ([regex]::Matches($text, '@include\s+(\S+)') | ForEach-Object { $_.Groups[1].Value }) -join '|'
$modsDir = "$wh\AppData\Engine\Mods"

function Remove-LocalMod($modId) {
    # deleting the ini unloads the mod; a dll still loaded in explorer can't be deleted yet and is retried next time
    Remove-Item "$modsDir\$modId.ini", "$wh\AppData\Engine\ModsWritable\$modId.ini", "$wh\AppData\ModsSource\$modId.wh.cpp" -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$modsDir\64" -Filter "$modId`_*.dll" | ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }
}

Remove-LocalMod 'local@custom-start-takeover'   # this mod's name before Tessera was named
if ($Uninstall) { Remove-LocalMod $id; Write-Output "removed $id"; return }

$dllName = '{0}_{1}_{2}.dll' -f $id, $version, (Get-Random -Minimum 100000 -Maximum 999999)
$out = "$modsDir\64\$dllName"
$cargs = @('-std=c++23', '-O2', '-shared', '-DUNICODE', '-D_UNICODE',
    '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00', '-D_WIN32_IE=0x0A00', '-DNTDDI_VERSION=0x0A000008',
    '-D__USE_MINGW_ANSI_STDIO=0', '-DWH_MOD', ('-DWH_MOD_ID=L\"' + $id + '\"'), ('-DWH_MOD_VERSION=L\"' + $version + '\"'),   # \" survives Windows PowerShell's argument passing
    "$wh\Engine\1.7.3\64\windhawk.lib", '-x', 'c++', $src, '-include', 'windhawk_api.h',
    '-target', 'x86_64-w64-mingw32', '-Wl,--export-all-symbols', '-o', $out)
Push-Location "$wh\Compiler"
try { & "$wh\Compiler\bin\clang++.exe" @cargs; if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" } }
finally { Pop-Location }

# remove older builds of this mod
Get-ChildItem "$modsDir\64" -Filter "$id`_*.dll" | Where-Object Name -ne $dllName | ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

$ini = "[Mod]`r`nLibraryFileName=$dllName`r`nDisabled=0`r`nInclude=$include`r`nExclude=`r`nArchitecture=x86-64`r`n" +
       "Version=$version`r`nSettingsChangeTime=$([int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))`r`n" +
       "LoggingEnabled=$([int][bool]$Logging)`r`nDebugLoggingEnabled=0`r`n"
[IO.File]::WriteAllText("$modsDir\$id.ini", $ini, [Text.Encoding]::Unicode)
Copy-Item $src "$wh\AppData\ModsSource\$id.wh.cpp" -Force
Write-Output "installed $id $version ($dllName)"

# Windhawk unloads the previous build from explorer a few seconds after the ini changes; delete it then
for ($i = 0; $i -lt 15; $i++) {
    $old = @(Get-ChildItem "$modsDir\64" -Filter "$id`_*.dll" | Where-Object Name -ne $dllName)
    if ($old.Count -eq 0) { break }
    foreach ($f in $old) { try { Remove-Item $f.FullName -Force } catch { } }
    Start-Sleep -Seconds 1
}
