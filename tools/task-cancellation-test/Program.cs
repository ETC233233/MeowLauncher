using MeowLauncher.WinUI.Services;

var registry = new TaskCancellationRegistry();
var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
registry.Create("queued");
Check(registry.Cancel("queued"), "cancel without a process");
var queued = registry.TokenFor("queued");
Check(queued.IsCancellationRequested, "worker sees cancellation before starting");
registry.Create("queued");
Check(registry.TokenFor("queued").IsCancellationRequested, "duplicate registration cannot revive a cancelled task");
registry.Create("active"); registry.Create("other");
using var linked = CancellationTokenSource.CreateLinkedTokenSource(registry.TokenFor("active"));
var callback = false;
using var registration = linked.Token.Register(() => callback = true);
registry.Cancel("active");
Check(callback && linked.IsCancellationRequested, "active worker receives cancellation");
Check(!registry.TokenFor("other").IsCancellationRequested, "other task remains active");
Check(!registry.Cancel("unknown"), "unknown ID cannot cancel a different task");
Check(!registry.TokenFor(null).CanBeCanceled, "untracked background operations are unaffected");
var scheduled = registry.TokenFor("queued");
await Task.Run(() => Check(scheduled.IsCancellationRequested, "cancellation crosses thread-pool scheduling"));
Console.WriteLine($"{passed}/8 PASS");
