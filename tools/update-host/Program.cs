// Integration-test parent only. It lives in an isolated copy, never in a user's launcher directory.
using System.Diagnostics;
using System.Text.Json.Nodes;
var file = args.Length == 1 ? Path.GetFullPath(args[0]) : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "update-test-plan.txt")).Trim();
var plan = JsonNode.Parse(File.ReadAllText(file))!;
using var me = Process.GetCurrentProcess();
plan["ParentPid"] = me.Id;
plan["ParentStarted"] = me.StartTime.ToUniversalTime().Ticks;
File.WriteAllText(file, plan.ToJsonString());
var work = Path.GetDirectoryName(file)!;
var psi = new ProcessStartInfo(Path.Combine(work, "helper", "MeowLauncher.Updater.exe"))
    { UseShellExecute = false, CreateNoWindow = true };
psi.ArgumentList.Add(file);
using var updater = Process.Start(psi)!;
for (var i = 0; i < 1500; i++)
{
    if (File.Exists(Path.Combine(work, "ready"))) { Console.WriteLine("PASS helper ready; old parent exiting normally"); return 0; }
    if (updater.HasExited) { Console.WriteLine(File.ReadAllText(Path.Combine(work, "result.json"))); return 1; }
    await Task.Delay(100);
}
Console.WriteLine("FAIL updater preparation timeout");
return 1;
