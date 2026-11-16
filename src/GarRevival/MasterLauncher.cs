using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GarRevival;

// Starts the bundled master (loopback only) when settings point at it and nothing answers there.
// It exits with this game.
internal static class MasterLauncher
{
    public const string LocalUrl = "http://127.0.0.1:27090";

    private static bool _checked;

    public static void Start()
    {
        if (_checked || !Settings.MasterAutoStart || !Settings.Masters.Contains(LocalUrl)) return;
        _checked = true;
        MasterHttp.Get(LocalUrl + "/v1/info", r =>
        {
            if (r.Ok)
            {
                RevivalLog.Info("Master: one already runs on this PC");
                return;
            }
            Launch();
        });
    }

    private static void Launch()
    {
        string script = Path.Combine(RevivalLog.Folder, "start-master.cmd");
        if (!File.Exists(script))
        {
            RevivalLog.Info("Master: start-master.cmd is missing, not starting one");
            return;
        }
        try
        {
            // The script compiles the source with Windows' csc, then runs it.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\" --local --parent {Process.GetCurrentProcess().Id}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = RevivalLog.Folder,
            });
            RevivalLog.Info($"Master: started on this PC ({LocalUrl})");
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Master: could not start", ex);
        }
    }
}
