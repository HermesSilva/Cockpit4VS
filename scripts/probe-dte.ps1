# Probes the running Visual Studio through the ROT: does it know the Cockpit commands?
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class Rot
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

    public static object Find(string contains)
    {
        IRunningObjectTable rot;
        IBindCtx ctx;
        GetRunningObjectTable(0, out rot);
        CreateBindCtx(0, out ctx);
        IEnumMoniker e;
        rot.EnumRunning(out e);
        IMoniker[] m = new IMoniker[1];
        while (e.Next(1, m, IntPtr.Zero) == 0)
        {
            string name;
            m[0].GetDisplayName(ctx, null, out name);
            if (name != null && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                object o;
                if (rot.GetObject(m[0], out o) == 0) return o;
            }
        }
        return null;
    }
}
'@ -Language CSharp

$dte = [Rot]::Find('VisualStudio.DTE')
if (-not $dte) { Write-Host 'No running Visual Studio found in the ROT.'; return }

Write-Host ("DTE: " + $dte.Version + "  " + $dte.Name)

foreach ($name in 'Tootega.Open', 'Tootega.OpenHub', 'Tootega.NewSession') {
    try {
        $cmd = $dte.Commands.Item($name)
        Write-Host ("[ ok ] {0}  guid={1} id={2}" -f $name, $cmd.Guid, $cmd.ID)
    }
    catch {
        Write-Host ("[fail] {0} — {1}" -f $name, $_.Exception.Message)
    }
}
