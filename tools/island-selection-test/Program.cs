using MeowLauncher.WinUI.Services;
var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
var running = new IslandActivity("running", true, false);
var next = new IslandActivity("next", true, false);
var done = new IslandActivity("running", false, true);
int passed = 0;
void Check(string name, string? current, IslandActivity[] entries, bool locked, DateTime hold, string? expected)
{
    var result = IslandSelection.Choose(current, entries, locked, now, hold);
    if (result != expected) throw new Exception($"{name}: expected {expected}, got {result}");
    Console.WriteLine("PASS " + name); passed++;
}
Check("new task cannot replace current running task", "running", [next, running], false, now, "running");
Check("completion feedback holds before handoff", "running", [next, done], false, now.AddSeconds(1), "running");
Check("handoff occurs after feedback deadline", "running", [next, done], false, now.AddSeconds(-1), "next");
Check("pinned result stays under user pointer", "running", [next, done], true, now.AddSeconds(-20), "running");
Check("closing result releases next task", "running", [next, done], false, now.AddSeconds(-20), "next");
Check("failure remains available while interacting", "running", [next, done], true, now, "running");
Check("cancelled task does not block queue", "running", [next, new("running", false, false)], true, now.AddSeconds(10), "next");
Check("removed task falls through safely", "running", [next], true, now.AddSeconds(10), "next");
Check("empty queue returns idle", "running", [], false, now, null);
Check("old finished records are not replayed", null, [done], false, now, null);
Check("last finished task stays visible when pinned", "running", [done], true, now, "running");
Check("last finished task returns to idle after deadline", "running", [done], false, now, null);
Console.WriteLine($"{passed}/{passed} PASS");
