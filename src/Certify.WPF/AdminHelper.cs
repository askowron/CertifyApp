using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Certify.WPF;

/// <summary>Sprawdzenie elewacji i ponowne uruchomienie przez UAC ("runas").</summary>
public static class AdminHelper
{
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Uruchamia ten sam exe z tymi samymi argumentami z elewacja.
    /// False = uzytkownik odrzucil UAC albo nie udalo sie uruchomic.
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true, // wymagane dla Verb = runas
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: "Nie" w oknie UAC
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
