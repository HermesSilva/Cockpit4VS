<#
.SYNOPSIS
    Reads keys out of the IDE's private registry with the offline registry API.

.DESCRIPTION
    privateregistry.bin is a hive file, not something Get-ItemProperty can reach, and
    loading it into HKLM needs elevation. offreg.dll — which ships with the VS SDK build
    tools — opens it read-only in process, which is enough to see exactly what the shell
    was told about a package.
#>
param(
    [string]$Hive = "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_f2f12ba4\privateregistry.bin",
    [string[]]$Keys = @('Menus', 'Packages\{92c17b2d-a9a9-460d-a1e2-d48f8f21e29f}')
)

$offreg = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.vssdk.buildtools" -Recurse -Filter 'offreg.dll' -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $offreg) { throw 'offreg.dll was not found in the VS SDK build tools package.' }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class OffReg
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("offreg.dll", CharSet = CharSet.Unicode)]
    public static extern int OROpenHive(string path, out IntPtr hive);

    [DllImport("offreg.dll", CharSet = CharSet.Unicode)]
    public static extern int OROpenKey(IntPtr key, string subKey, out IntPtr result);

    [DllImport("offreg.dll", CharSet = CharSet.Unicode)]
    public static extern int OREnumValue(IntPtr key, uint index, StringBuilder name, ref uint nameLength,
                                         out uint type, byte[] data, ref uint dataLength);

    [DllImport("offreg.dll", CharSet = CharSet.Unicode)]
    public static extern int OREnumKey(IntPtr key, uint index, StringBuilder name, ref uint nameLength,
                                       StringBuilder className, IntPtr classLength, IntPtr lastWrite);

    [DllImport("offreg.dll")]
    public static extern int ORCloseKey(IntPtr key);

    [DllImport("offreg.dll")]
    public static extern int ORCloseHive(IntPtr hive);
}
"@ -Language CSharp

[void][OffReg]::LoadLibrary($offreg)

$hiveHandle = [IntPtr]::Zero
$rc = [OffReg]::OROpenHive($Hive, [ref]$hiveHandle)
if ($rc -ne 0) { throw "OROpenHive failed with $rc (is Visual Studio still running?)" }

try {
    foreach ($keyPath in $Keys) {
        $key = [IntPtr]::Zero
        $rc = [OffReg]::OROpenKey($hiveHandle, $keyPath, [ref]$key)
        if ($rc -ne 0) { "== $keyPath — not present (rc=$rc)"; continue }

        "== $keyPath"
        $i = 0
        while ($true) {
            $name = New-Object System.Text.StringBuilder 1024
            $nameLen = [uint32]1024
            $type = [uint32]0
            $data = New-Object byte[] 4096
            $dataLen = [uint32]4096
            $rc = [OffReg]::OREnumValue($key, $i, $name, [ref]$nameLen, [ref]$type, $data, [ref]$dataLen)
            if ($rc -ne 0) { break }

            $value = if ($type -eq 1 -or $type -eq 2) {
                [System.Text.Encoding]::Unicode.GetString($data, 0, [Math]::Max(0, $dataLen - 2))
            } elseif ($type -eq 4) {
                [BitConverter]::ToUInt32($data, 0)
            } else { "<type $type, $dataLen bytes>" }

            "   '{0}' = {1}" -f $name.ToString(), $value
            $i++
        }

        [void][OffReg]::ORCloseKey($key)
    }
}
finally { [void][OffReg]::ORCloseHive($hiveHandle) }

