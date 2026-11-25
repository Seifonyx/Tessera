param([string]$Dll, [string]$OutDir)
$b = [IO.File]::ReadAllBytes($Dll)
$pe = [BitConverter]::ToInt32($b, 0x3C)
$magic = [BitConverter]::ToUInt16($b, $pe + 24)
$optOff = $pe + 24
$ddOff = if ($magic -eq 0x20b) { $optOff + 112 } else { $optOff + 96 }
$dbgRva = [BitConverter]::ToUInt32($b, $ddOff + 6*8)
$dbgSize = [BitConverter]::ToUInt32($b, $ddOff + 6*8 + 4)
$nSec = [BitConverter]::ToUInt16($b, $pe + 6)
$optSize = [BitConverter]::ToUInt16($b, $pe + 20)
$secOff = $optOff + $optSize
function RvaToOff($rva) {
  for ($i=0; $i -lt $nSec; $i++) {
    $s = $secOff + $i*40
    $va = [BitConverter]::ToUInt32($b, $s+12); $vs = [BitConverter]::ToUInt32($b, $s+8)
    $raw = [BitConverter]::ToUInt32($b, $s+20)
    if ($rva -ge $va -and $rva -lt $va + [Math]::Max($vs,1)) { return $rva - $va + $raw }
  }
  return -1
}
$d = RvaToOff $dbgRva
for ($i=0; $i -lt $dbgSize/28; $i++) {
  $e = $d + $i*28
  $type = [BitConverter]::ToUInt32($b, $e+12)
  if ($type -eq 2) {
    $p = [BitConverter]::ToUInt32($b, $e+24)
    if ([Text.Encoding]::ASCII.GetString($b, $p, 4) -eq 'RSDS') {
      $g = New-Object byte[] 16; [Array]::Copy($b, $p+4, $g, 0, 16)
      $guid = (New-Object Guid (,$g)).ToString('N').ToUpper()
      $age = [BitConverter]::ToUInt32($b, $p+20)
      $end = $p+24; while ($b[$end] -ne 0) { $end++ }
      $name = [IO.Path]::GetFileName([Text.Encoding]::ASCII.GetString($b, $p+24, $end-$p-24))
      $url = "https://msdl.microsoft.com/download/symbols/$name/$guid$('{0:X}' -f $age)/$name"
      $out = Join-Path $OutDir $name
      Write-Output $url
      Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -UserAgent 'Microsoft-Symbol-Server/10.0'
      Write-Output ("saved " + $out + " " + (Get-Item $out).Length)
    }
  }
}
